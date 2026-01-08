using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Mail;
using System.Text.Json;

public enum TradeSide { Buy, Sell }

// Data that stays saved even if the bot restarts
public class BotPersistData
{
    public Dictionary<string, SimPosition> Positions { get; set; } = new();
    public int TradesExecutedToday { get; set; }
    public decimal RealizedPnLTotal { get; set; }
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

    // RESTORED: This fixed your "RealizedPnL" error
    public decimal RealizedPnL { get; set; } = 0m;

    public decimal UnrealizedPnL => Quantity * (CurrentPrice - AvgPrice);
}

public class SimulatedBroker
{
    private readonly Dictionary<string, SimPosition> _positions = new();
    private readonly Dictionary<string, List<decimal>> _priceHistory = new();
    private readonly Dictionary<string, DateTime> _sellCooldowns = new();
    private readonly Dictionary<string, int> _tradesToday = new();
    private readonly Dictionary<string, DateTime> _buyTimes = new();

    private const string SaveFilePath = "bot_state.json";
    private DateTime _lastHeartbeatTime = DateTime.MinValue;

    // RESTORED: This fixed your "Positions" error
    public IReadOnlyDictionary<string, SimPosition> Positions => _positions;

    // SMA CONFIG
    private const int shortSmaPeriod = 20;
    private const int longSmaPeriod = 100;

    // RISK PARAMETERS
    private const int maxTradesGlobal = 20;
    private const int maxTradesPerSymbol = 3;
    private const decimal maxDailyLoss = -400.00m;
    private const decimal tradeDollarAmount = 2000m;

    // TRAILING PROFIT
    private decimal _peakDailyRealizedPnL = 0m;
    private const decimal profitActivationThreshold = 300.00m;
    private const decimal profitTrailingBuffer = 150.00m;

    // PERSONALIZED TARGETS & STOPS
    private readonly Dictionary<string, decimal> _customStops = new() {
        { "RKLB", 0.045m }, { "PLTR", 0.035m }, { "TSLA", 0.030m },
        { "NVDA", 0.030m }, { "MSFT", 0.020m }, { "AAPL", 0.020m }
    };

    private readonly Dictionary<string, decimal> _customTargets = new() {
        { "RKLB", 0.050m }, { "PLTR", 0.035m }, { "TSLA", 0.030m },
        { "NVDA", 0.025m }, { "MSFT", 0.012m }, { "AAPL", 0.012m }
    };

    private const decimal defaultStopLoss = 0.025m;
    private const decimal defaultTarget = 0.020m;

    private int _tradesExecutedToday = 0;
    private decimal _totalRealizedPnL = 0m;
    private bool _qqqBullish = false;

    public void UpdateDataStatus(string symbol, int dataType) { }

    public List<Trade> OnPriceUpdate(Dictionary<string, decimal> marketPrices)
    {
        var tradesExecuted = new List<Trade>();

        // Heartbeat Email every 2 hours
        if ((DateTime.Now - _lastHeartbeatTime).TotalHours >= 2 && IsInTradingWindow())
        {
            SendTradeAlert("HEARTBEAT", "SYSTEM", 0, 0);
            _lastHeartbeatTime = DateTime.Now;
        }

        // Global Daily Stop
        if (_totalRealizedPnL <= maxDailyLoss) return tradesExecuted;

        // Trailing Profit Floor
        if (_totalRealizedPnL > _peakDailyRealizedPnL) _peakDailyRealizedPnL = _totalRealizedPnL;
        if (_peakDailyRealizedPnL >= profitActivationThreshold && _totalRealizedPnL < (_peakDailyRealizedPnL - profitTrailingBuffer))
            return CheckEndOfDayLiquidation(force: true);

        // Check for 12:45 PM Close
        var eodTrades = CheckEndOfDayLiquidation();
        if (eodTrades.Count > 0) return eodTrades;

        foreach (var kv in marketPrices)
        {
            var symbol = kv.Key; var price = kv.Value;

            if (!_priceHistory.ContainsKey(symbol)) _priceHistory[symbol] = new List<decimal>();
            _priceHistory[symbol].Add(price);
            if (_priceHistory[symbol].Count > 150) _priceHistory[symbol].RemoveAt(0);

            if (_priceHistory[symbol].Count >= longSmaPeriod)
            {
                var history = _priceHistory[symbol];
                var shortSma = history.Skip(history.Count - shortSmaPeriod).Average();
                var longSma = history.Skip(history.Count - longSmaPeriod).Average();
                if (symbol == "QQQ") _qqqBullish = (shortSma > longSma);

                // --- SELL LOGIC ---
                if (_positions.ContainsKey(symbol))
                {
                    var pos = _positions[symbol]; pos.CurrentPrice = price;
                    decimal stopPct = _customStops.GetValueOrDefault(symbol, defaultStopLoss);
                    decimal targetPct = _customTargets.GetValueOrDefault(symbol, defaultTarget);

                    // Update Trailing Stop Price
                    decimal newStop = price * (1 - stopPct);
                    if (newStop > pos.TrailingStop) pos.TrailingStop = newStop;

                    if (price <= pos.TrailingStop || price >= pos.AvgPrice * (1 + targetPct))
                    {
                        decimal pnl = pos.Quantity * (price - pos.AvgPrice);
                        _totalRealizedPnL += pnl;
                        tradesExecuted.Add(new Trade { Symbol = symbol, Action = TradeSide.Sell, Price = price, Quantity = pos.Quantity });
                        SendTradeAlert(pnl > 0 ? "PROFIT" : "STOP", symbol, price, pos.Quantity);

                        _sellCooldowns[symbol] = DateTime.Now.Add(pnl < 0 ? TimeSpan.FromMinutes(120) : TimeSpan.FromMinutes(15));
                        _positions.Remove(symbol);
                        SaveState();
                    }
                }

                // --- BUY LOGIC ---
                bool isCooling = _sellCooldowns.TryGetValue(symbol, out var cd) && DateTime.Now < cd;
                if (_qqqBullish && (shortSma > longSma) && !_positions.ContainsKey(symbol) && IsInTradingWindow() && !isCooling)
                {
                    if (_tradesExecutedToday < maxTradesGlobal && _tradesToday.GetValueOrDefault(symbol, 0) < maxTradesPerSymbol && symbol != "SPY" && symbol != "QQQ")
                    {
                        var qty = Math.Floor(tradeDollarAmount / price);
                        if (qty > 0)
                        {
                            decimal stopPct = _customStops.GetValueOrDefault(symbol, defaultStopLoss);
                            _positions[symbol] = new SimPosition { AvgPrice = price, Quantity = qty, CurrentPrice = price, TrailingStop = price * (1 - stopPct) };
                            _tradesExecutedToday++;
                            _tradesToday[symbol] = _tradesToday.GetValueOrDefault(symbol, 0) + 1;
                            tradesExecuted.Add(new Trade { Symbol = symbol, Action = TradeSide.Buy, Price = price, Quantity = qty });
                            SendTradeAlert("BUY", symbol, price, qty);
                            SaveState();
                        }
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
            foreach (var s in _positions.Keys.ToList())
            {
                trades.Add(new Trade { Symbol = s, Action = TradeSide.Sell, Price = _positions[s].CurrentPrice, Quantity = _positions[s].Quantity });
                _positions.Remove(s);
            }
            ArchiveDailyResults();
            SendTradeAlert("EOD SUMMARY", "SYSTEM", 0, 0);
            SaveState();
        }
        return trades;
    }

    public void SyncExistingPosition(string symbol, decimal qty, decimal avgPrice)
    {
        if (!_positions.ContainsKey(symbol))
        {
            decimal stopPct = _customStops.GetValueOrDefault(symbol, defaultStopLoss);
            _positions[symbol] = new SimPosition { Quantity = qty, AvgPrice = avgPrice, CurrentPrice = avgPrice, TrailingStop = avgPrice * (1 - stopPct) };
            SaveState();
        }
    }

    public void ResetDailyTrades()
    {
        _tradesExecutedToday = 0; _totalRealizedPnL = 0m; _peakDailyRealizedPnL = 0m;
        _tradesToday.Clear(); _sellCooldowns.Clear(); SaveState();
    }

    public void PrintDailySummary()
    {
        Console.WriteLine($"\n--- DAILY PNL: ${_totalRealizedPnL:0.00} ---");
        foreach (var p in _positions) Console.WriteLine($"{p.Key}: ${p.Value.UnrealizedPnL:0.00}");
    }

    public void SendTradeAlert(string action, string symbol, decimal price, decimal qty)
    {
        try
        {
            using MailMessage mail = new MailMessage("uygargunay@gmail.com", "uygargunay@gmail.com");
            mail.Subject = $"[{action}] {symbol}";
            mail.Body = $"{action} Alert\nSymbol: {symbol}\nPrice: ${price:0.00}\nDaily PnL: ${_totalRealizedPnL:0.00}";
            using SmtpClient sc = new SmtpClient("smtp.gmail.com", 587) { Credentials = new NetworkCredential("uygargunay@gmail.com", "zklk qkcu vwya qlky"), EnableSsl = true };
            sc.Send(mail);
        }
        catch { }
    }

    public void SendEmailSummary(string toEmail) => SendTradeAlert("REPORT", "SYSTEM", 0, 0);

    public void ArchiveDailyResults()
    {
        try { File.AppendAllText("trade_history_log.txt", $"{DateTime.Now:yyyy-MM-dd} | PnL: ${_totalRealizedPnL:0.00}\n"); } catch { }
    }

    public void SaveState()
    {
        try
        {
            var data = new BotPersistData { Positions = _positions, TradesExecutedToday = _tradesExecutedToday, RealizedPnLTotal = _totalRealizedPnL, PeakDailyRealizedPnL = _peakDailyRealizedPnL, TradesPerSymbol = _tradesToday };
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
            _tradesExecutedToday = data.TradesExecutedToday; _totalRealizedPnL = data.RealizedPnLTotal; _peakDailyRealizedPnL = data.PeakDailyRealizedPnL;
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