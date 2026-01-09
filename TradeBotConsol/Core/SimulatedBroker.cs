using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Mail;
using System.Text.Json;

public interface IBroker
{
    void SubmitOrder(string symbol, int qty, decimal price, TradeSide side);
}

public enum TradeSide { Buy, Sell }

public class BotPersistData
{
    public Dictionary<string, SimPosition> Positions { get; set; } = new();
    public int TradesExecutedToday { get; set; }
    public decimal RealizedPnLTotal { get; set; }
    public decimal TotalCommissions { get; set; }
    public decimal PeakDailyRealizedPnL { get; set; }
    public Dictionary<string, int> TradesPerSymbol { get; set; } = new();
    public Dictionary<string, DateTime> BuyTimes { get; set; } = new();
}

public class Trade
{
    public string Symbol { get; set; }
    public TradeSide Action { get; set; }
    public decimal Price { get; set; }
    public decimal Quantity { get; set; }
    public DateTime Time { get; set; } = DateTime.Now;
}

public class SimPosition
{
    public decimal Quantity { get; set; }
    public decimal AvgPrice { get; set; }
    public decimal TrailingStop { get; set; }
    public decimal CurrentPrice { get; set; }
    public decimal RealizedPnL { get; set; } = 0m;
    public decimal UnrealizedPnL => Quantity * (CurrentPrice - AvgPrice);
}

public class PositionManager : SimulatedBroker { }

public class SimulatedBroker : IBroker
{
    protected readonly Dictionary<string, SimPosition> _positions = new();
    protected readonly Dictionary<string, List<decimal>> _priceHistory = new();
    protected readonly Dictionary<string, DateTime> _sellCooldowns = new();
    protected readonly Dictionary<string, int> _tradesToday = new();

    private const string SaveFilePath = "bot_state.json";
    private DateTime _lastHeartbeatTime = DateTime.MinValue;

    public IReadOnlyDictionary<string, SimPosition> Positions => _positions;
    public decimal TotalRealizedPnL => _totalRealizedPnL;

    // --- AGGRESSION SETTINGS (9/50 SMA) ---
    private const int shortSmaPeriod = 9;
    private const int longSmaPeriod = 50;
    private const int maxTradesGlobal = 30;
    private const int maxTradesPerSymbol = 5;
    private const decimal tradeDollarAmount = 2000m;
    private const decimal roundTripFee = 2.00m;

    private decimal _totalRealizedPnL = 0m;
    private decimal _totalCommissions = 0m;
    private int _tradesExecutedToday = 0;
    private decimal _peakDailyRealizedPnL = 0m;
    private bool _qqqBullish = false;

    private readonly Dictionary<string, decimal> _customStops = new() {
    { "RKLB", 0.050m }, { "PLTR", 0.035m }, { "TSLA", 0.035m },
    { "NVDA", 0.030m }, { "MSFT", 0.015m }, { "AAPL", 0.015m }
};

    private readonly Dictionary<string, decimal> _customTargets = new() {
    { "RKLB", 0.075m }, { "PLTR", 0.050m }, { "TSLA", 0.050m },
    { "NVDA", 0.045m }, { "MSFT", 0.025m }, { "AAPL", 0.025m }
};

    // This must be linked to your IbClient in your main class
    public IBroker RealBroker { get; set; }

    public void SubmitOrder(string symbol, int qty, decimal price, TradeSide side)
    {
        Console.WriteLine($"\n[BROKER] Executing {side} for {qty} {symbol} at ${price:0.00}");

        // Send the order to the actual IBKR API
        RealBroker?.SubmitOrder(symbol, qty, price, side);

        if (side == TradeSide.Buy)
        {
            decimal stopPct = _customStops.GetValueOrDefault(symbol, 0.025m);
            _positions[symbol] = new SimPosition
            {
                AvgPrice = price,
                Quantity = qty,
                CurrentPrice = price,
                TrailingStop = price * (1 - stopPct)
            };
            _tradesExecutedToday++;
            _tradesToday[symbol] = _tradesToday.GetValueOrDefault(symbol, 0) + 1;
            SendTradeAlert("BUY", symbol, price, qty);
        }
        else if (_positions.ContainsKey(symbol))
        {
            var pos = _positions[symbol];
            decimal grossPnL = pos.Quantity * (price - pos.AvgPrice);
            _totalCommissions += roundTripFee;
            _totalRealizedPnL += (grossPnL - roundTripFee);

            SendTradeAlert(grossPnL > 0 ? "PROFIT" : "STOP", symbol, price, pos.Quantity, grossPnL, (grossPnL - roundTripFee));
            _positions.Remove(symbol);
        }
        SaveState();
    }

    public void MarkToMarket(Dictionary<string, decimal> prices) => OnPriceUpdate(prices);

    public List<Trade> OnPriceUpdate(Dictionary<string, decimal> marketPrices)
    {
        var tradesExecuted = new List<Trade>();

        if ((DateTime.Now - _lastHeartbeatTime).TotalHours >= 2 && IsInTradingWindow())
        {
            SendTradeAlert("HEARTBEAT", "SYSTEM", 0, 0);
            _lastHeartbeatTime = DateTime.Now;
        }

        var eodTrades = CheckEndOfDayLiquidation();
        if (eodTrades.Count > 0) return eodTrades;

        foreach (var kv in marketPrices)
        {
            var symbol = kv.Key; var price = kv.Value;
            if (!_priceHistory.ContainsKey(symbol)) _priceHistory[symbol] = new List<decimal>();
            _priceHistory[symbol].Add(price);
            if (_priceHistory[symbol].Count > 100) _priceHistory[symbol].RemoveAt(0);

            if (_priceHistory[symbol].Count >= longSmaPeriod)
            {
                var history = _priceHistory[symbol];
                var shortSma = (decimal)history.Skip(history.Count - shortSmaPeriod).Average(x => (double)x);
                var longSma = (decimal)history.Skip(history.Count - longSmaPeriod).Average(x => (double)x);

                if (symbol == "QQQ") _qqqBullish = (shortSma > longSma);

                if (_positions.ContainsKey(symbol))
                {
                    var pos = _positions[symbol]; pos.CurrentPrice = price;
                    decimal target = _customTargets.GetValueOrDefault(symbol, 0.02m);
                    decimal stopPct = _customStops.GetValueOrDefault(symbol, 0.025m);

                    decimal newStop = price * (1 - stopPct);
                    if (newStop > pos.TrailingStop) pos.TrailingStop = newStop;

                    if (price <= pos.TrailingStop || price >= pos.AvgPrice * (1 + target))
                    {
                        SubmitOrder(symbol, (int)pos.Quantity, price, TradeSide.Sell);
                    }
                }

                bool isCooling = _sellCooldowns.TryGetValue(symbol, out var cd) && DateTime.Now < cd;
                if (_qqqBullish && (shortSma > longSma) && !_positions.ContainsKey(symbol) && IsInTradingWindow() && !isCooling)
                {
                    if (_tradesExecutedToday < maxTradesGlobal && _tradesToday.GetValueOrDefault(symbol, 0) < maxTradesPerSymbol)
                    {
                        var qty = (int)Math.Floor(tradeDollarAmount / price);
                        if (qty > 0) SubmitOrder(symbol, qty, price, TradeSide.Buy);
                    }
                }
            }
        }
        return tradesExecuted;
    }

    public List<Trade> CheckEndOfDayLiquidation(bool force = false)
    {
        var trades = new List<Trade>();
        if (force || (DateTime.Now.TimeOfDay >= new TimeSpan(12, 45, 0) && _positions.Count > 0))
        {
            Console.WriteLine("[SYSTEM] EOD Time reached. Closing all positions...");
            foreach (var s in _positions.Keys.ToList())
            {
                var pos = _positions[s];
                SubmitOrder(s, (int)pos.Quantity, pos.CurrentPrice, TradeSide.Sell);
                trades.Add(new Trade { Symbol = s, Action = TradeSide.Sell, Price = pos.CurrentPrice, Quantity = pos.Quantity });
            }
            ArchiveDailyResults();
            SendTradeAlert("EOD SUMMARY", "SYSTEM", 0, 0);
        }
        return trades;
    }

    public void SyncExistingPosition(string symbol, decimal qty, decimal avgPrice)
    {
        if (!_positions.ContainsKey(symbol))
        {
            decimal stopPct = _customStops.GetValueOrDefault(symbol, 0.025m);
            _positions[symbol] = new SimPosition { Quantity = qty, AvgPrice = avgPrice, CurrentPrice = avgPrice, TrailingStop = avgPrice * (1 - stopPct) };
            SaveState();
        }
    }

    public void ResetDailyTrades()
    {
        _tradesExecutedToday = 0; _totalRealizedPnL = 0m; _totalCommissions = 0m; _peakDailyRealizedPnL = 0m;
        _tradesToday.Clear(); _sellCooldowns.Clear(); SaveState();
    }

    public void PrintDailySummary()
    {
        decimal grossTotal = _totalRealizedPnL + _totalCommissions;
        Console.WriteLine("\n===============================================");
        Console.WriteLine($"   GROSS PNL: ${grossTotal:0.00}");
        Console.WriteLine($"   NET PNL:   ${_totalRealizedPnL:0.00} (Fees: -${_totalCommissions:0.00})");
        Console.WriteLine("===============================================");
        foreach (var p in _positions) Console.WriteLine($"{p.Key}: ${p.Value.UnrealizedPnL:0.00}");
    }

    public void SendEmailSummary(string toEmail) => SendTradeAlert("REPORT", "SYSTEM", 0, 0);

    public void SendTradeAlert(string action, string symbol, decimal price, decimal qty, decimal gross = 0, decimal net = 0)
    {
        try
        {
            using MailMessage mail = new MailMessage("uygargunay@gmail.com", "uygargunay@gmail.com");
            mail.Subject = $"[{action}] {symbol}";
            string body = $"{action} Alert\nSymbol: {symbol}\nPrice: ${price:0.00}\nQty: {qty}\n";
            if (action != "BUY" && action != "HEARTBEAT" && action != "EOD SUMMARY")
                body += $"Trade Gross: ${gross:0.00}\nTrade Net: ${net:0.00}\n";
            body += $"Daily Net PnL: ${_totalRealizedPnL:0.00}";
            mail.Body = body;
            using SmtpClient sc = new SmtpClient("smtp.gmail.com", 587)
            {
                Credentials = new NetworkCredential("uygargunay@gmail.com", "zklk qkcu vwya qlky"),
                EnableSsl = true
            };
            sc.Send(mail);
        }
        catch { }
    }

    public void ArchiveDailyResults()
    {
        decimal grossTotal = _totalRealizedPnL + _totalCommissions;
        try { File.AppendAllText("trade_history_log.txt", $"{DateTime.Now:yyyy-MM-dd} | Gross: ${grossTotal:0.00} | Net PnL: ${_totalRealizedPnL:0.00}\n"); } catch { }
    }

    public void SaveState()
    {
        try
        {
            var data = new BotPersistData { Positions = _positions, TradesExecutedToday = _tradesExecutedToday, RealizedPnLTotal = _totalRealizedPnL, TotalCommissions = _totalCommissions, PeakDailyRealizedPnL = _peakDailyRealizedPnL, TradesPerSymbol = _tradesToday };
            File.WriteAllText(SaveFilePath, JsonSerializer.Serialize(data));
        }
        catch { }
    }

    public void LoadState()
    {
        if (!File.Exists(SaveFilePath)) return;
        try
        {
            var data = JsonSerializer.Deserialize<BotPersistData>(File.ReadAllText(SaveFilePath));
            if (data == null) return;
            _tradesExecutedToday = data.TradesExecutedToday;
            _totalRealizedPnL = data.RealizedPnLTotal;
            _totalCommissions = data.TotalCommissions;
            foreach (var kv in data.Positions) _positions[kv.Key] = kv.Value;
            foreach (var kv in data.TradesPerSymbol) _tradesToday[kv.Key] = kv.Value;
        }
        catch { }
    }

    public bool IsInTradingWindow()
    {
        var now = DateTime.Now.TimeOfDay;
        return DateTime.Now.DayOfWeek != DayOfWeek.Saturday && DateTime.Now.DayOfWeek != DayOfWeek.Sunday &&
               now >= new TimeSpan(8, 10, 0) && now <= new TimeSpan(12, 40, 0);
    }
}