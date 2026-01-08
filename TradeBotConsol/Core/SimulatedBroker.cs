using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Mail;
using System.Text.Json;

public enum TradeSide { Buy, Sell }

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
    public decimal RealizedPnL { get; set; } = 0m;
    public decimal CurrentPrice { get; set; }
    public decimal UnrealizedPnL => Quantity * (CurrentPrice - AvgPrice);
}

public class SimulatedBroker
{
    private readonly Dictionary<string, SimPosition> _positions = new();
    private readonly Dictionary<string, List<decimal>> _priceHistory = new();
    private readonly Dictionary<string, DateTime> _sellCooldowns = new();
    private readonly Dictionary<string, int> _tradesToday = new();
    private readonly Dictionary<string, DateTime> _buyTimes = new();
    private readonly Dictionary<string, string> _dataStatus = new();
    private readonly Dictionary<string, DateTime> _lastPriceUpdate = new();

    private const string SaveFilePath = "bot_state.json";
    private const int WatchdogTimeoutSeconds = 60;
    private DateTime _lastHeartbeatTime = DateTime.MinValue;

    // SMA TREND PERIODS
    private const int shortSmaPeriod = 20;
    private const int longSmaPeriod = 100;

    // RISK SETTINGS
    private const int maxTradesGlobal = 20;
    private const int maxTradesPerSymbol = 3;
    private const decimal maxDailyLoss = -300.00m;
    private const decimal tradeDollarAmount = 2000m;

    // TRAILING DAILY PROFIT LOGIC
    private decimal _peakDailyRealizedPnL = 0m;
    private const decimal profitActivationThreshold = 300.00m;
    private const decimal profitTrailingBuffer = 100.00m;

    // SYMBOL-SPECIFIC STOP LOSSES
    private readonly Dictionary<string, decimal> _customStops = new() {
        { "RKLB", 0.035m }, { "PLTR", 0.025m }, { "TSLA", 0.025m },
        { "NVDA", 0.020m }, { "MSFT", 0.015m }, { "AAPL", 0.015m }
    };
    private const decimal defaultStopLoss = 0.015m;
    private const decimal targetProfitPercent = 0.03m;

    public IReadOnlyDictionary<string, SimPosition> Positions => _positions;
    private int _tradesExecutedToday = 0;
    private decimal _totalRealizedPnL = 0m;
    private bool _spyBullish = false;
    private bool _qqqBullish = false;

    public void UpdateDataStatus(string symbol, int dataType)
    {
        _dataStatus[symbol] = (dataType == 1) ? "LIVE" : "DELAYED";
    }

    public List<Trade> OnPriceUpdate(Dictionary<string, decimal> marketPrices)
    {
        var tradesExecuted = new List<Trade>();

        // Heartbeat Logic
        if ((DateTime.Now - _lastHeartbeatTime).TotalHours >= 2 && IsInTradingWindow())
        {
            SendTradeAlert("HEARTBEAT", "SYSTEM", 0, 0);
            _lastHeartbeatTime = DateTime.Now;
        }

        if (_totalRealizedPnL <= maxDailyLoss) return tradesExecuted;

        // Trailing Profit Logic
        if (_totalRealizedPnL > _peakDailyRealizedPnL) _peakDailyRealizedPnL = _totalRealizedPnL;
        if (_peakDailyRealizedPnL >= profitActivationThreshold)
        {
            if (_totalRealizedPnL < (_peakDailyRealizedPnL - profitTrailingBuffer))
            {
                Console.WriteLine($"[SHUTDOWN] Trailing Profit Stop hit.");
                return CheckEndOfDayLiquidation(force: true);
            }
        }

        var eodTrades = CheckEndOfDayLiquidation();
        if (eodTrades.Count > 0) return eodTrades;

        foreach (var kv in marketPrices)
        {
            var symbol = kv.Key;
            var price = kv.Value;
            _lastPriceUpdate[symbol] = DateTime.Now;

            if (!_priceHistory.ContainsKey(symbol)) _priceHistory[symbol] = new List<decimal>();
            var history = _priceHistory[symbol];
            history.Add(price);
            if (history.Count > 150) history.RemoveAt(0);

            if (history.Count >= longSmaPeriod)
            {
                var shortSma = (decimal)history.Skip(history.Count - shortSmaPeriod).Take(shortSmaPeriod).Average();
                var longSma = (decimal)history.Skip(history.Count - longSmaPeriod).Take(longSmaPeriod).Average();
                decimal score = shortSma - longSma;

                if (symbol == "SPY") _spyBullish = score > 0;
                if (symbol == "QQQ") _qqqBullish = score > 0;

                // --- SELL LOGIC ---
                if (_positions.ContainsKey(symbol))
                {
                    var pos = _positions[symbol];
                    pos.CurrentPrice = price;

                    decimal stopPct = _customStops.GetValueOrDefault(symbol, defaultStopLoss);
                    decimal newStop = price * (1 - stopPct);
                    if (newStop > pos.TrailingStop) pos.TrailingStop = newStop;

                    decimal currentGrossProfit = pos.Quantity * (price - pos.AvgPrice);
                    bool triggerStopLoss = price <= pos.TrailingStop;
                    bool triggerTakeProfit = price >= pos.AvgPrice * (1 + targetProfitPercent);

                    if (triggerStopLoss || triggerTakeProfit)
                    {
                        bool isLoss = currentGrossProfit < 0;
                        _totalRealizedPnL += currentGrossProfit;
                        tradesExecuted.Add(new Trade { Symbol = symbol, Action = TradeSide.Sell, Price = price, Quantity = pos.Quantity });
                        LogTradeAction(symbol, isLoss ? "STOP" : "PROFIT", price, pos.Quantity, score);
                        SendTradeAlert(isLoss ? "STOP LOSS" : "TAKE PROFIT", symbol, price, pos.Quantity);

                        _sellCooldowns[symbol] = DateTime.Now.Add(isLoss ? TimeSpan.FromMinutes(120) : TimeSpan.FromMinutes(15));
                        _positions.Remove(symbol);
                        _buyTimes.Remove(symbol);
                        SaveState();
                    }
                }

                // --- BUY LOGIC ---
                bool isCoolingDown = _sellCooldowns.TryGetValue(symbol, out var cooldown) && DateTime.Now < cooldown;
                int tradesForThisStock = _tradesToday.GetValueOrDefault(symbol, 0);

                if (score > 0 && _qqqBullish && !_positions.ContainsKey(symbol) && IsInTradingWindow() && !isCoolingDown)
                {
                    if (_tradesExecutedToday < maxTradesGlobal && tradesForThisStock < maxTradesPerSymbol && symbol != "SPY" && symbol != "QQQ")
                    {
                        var qty = Math.Floor(tradeDollarAmount / price);
                        if (qty > 0)
                        {
                            decimal stopPct = _customStops.GetValueOrDefault(symbol, defaultStopLoss);
                            _positions[symbol] = new SimPosition { AvgPrice = price, Quantity = qty, CurrentPrice = price, TrailingStop = price * (1 - stopPct) };
                            _buyTimes[symbol] = DateTime.Now;
                            _tradesExecutedToday++;
                            _tradesToday[symbol] = tradesForThisStock + 1;
                            tradesExecuted.Add(new Trade { Symbol = symbol, Action = TradeSide.Buy, Price = price, Quantity = qty });
                            LogTradeAction(symbol, "BUY", price, qty, score);
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
        var tradesToExecute = new List<Trade>();
        TimeSpan closeTime = new TimeSpan(12, 45, 0);

        if (force || (DateTime.Now.TimeOfDay >= closeTime && _positions.Count > 0))
        {
            foreach (var symbol in _positions.Keys.ToList())
            {
                var pos = _positions[symbol];
                tradesToExecute.Add(new Trade { Symbol = symbol, Action = TradeSide.Sell, Price = pos.CurrentPrice, Quantity = pos.Quantity });
                _positions.Remove(symbol);
            }
            ArchiveDailyResults();
            SendEmailSummary("uygargunay@gmail.com");
            SaveState();
        }
        return tradesToExecute;
    }

    public void SyncExistingPosition(string symbol, decimal qty, decimal avgPrice)
    {
        if (!_positions.ContainsKey(symbol))
        {
            decimal stopPct = _customStops.GetValueOrDefault(symbol, defaultStopLoss);
            _positions[symbol] = new SimPosition { Quantity = qty, AvgPrice = avgPrice, CurrentPrice = avgPrice, TrailingStop = avgPrice * (1 - stopPct) };
            if (!_buyTimes.ContainsKey(symbol)) _buyTimes[symbol] = DateTime.Now;
            SaveState();
        }
    }

    public bool IsInTradingWindow()
    {
        var now = DateTime.Now;
        var startTime = now.Date.AddHours(8).AddMinutes(10);
        var endTime = now.Date.AddHours(12).AddMinutes(40);
        return (now.DayOfWeek != DayOfWeek.Saturday && now.DayOfWeek != DayOfWeek.Sunday) && (now >= startTime && now <= endTime);
    }

    public void ResetDailyTrades()
    {
        _tradesExecutedToday = 0;
        _totalRealizedPnL = 0m;
        _peakDailyRealizedPnL = 0m;
        _tradesToday.Clear();
        _sellCooldowns.Clear();
        _buyTimes.Clear();
        SaveState();
    }

    public void PrintDailySummary()
    {
        Console.WriteLine($"\n--- PnL: Realized ${_totalRealizedPnL:0.00} | Peak: ${_peakDailyRealizedPnL:0.00} ---");
        Console.WriteLine("--- ACTIVE POSITIONS ---");
        foreach (var kv in _positions) Console.WriteLine($"{kv.Key}: {kv.Value.UnrealizedPnL:0.00}");
        Console.WriteLine("--- PENALTY BOX ---");
        foreach (var kv in _sellCooldowns) if (DateTime.Now < kv.Value) Console.WriteLine($"{kv.Key} locked for {(kv.Value - DateTime.Now).Minutes}m");
    }

    public void SendTradeAlert(string action, string symbol, decimal price, decimal qty)
    {
        try
        {
            using MailMessage mail = new MailMessage("uygargunay@gmail.com", "uygargunay@gmail.com");
            mail.Subject = $"[{action}] {symbol}";
            mail.Body = $"{action} Alert\nSymbol: {symbol}\nPrice: ${price:0.00}\nQty: {qty}\nDaily PnL: ${_totalRealizedPnL:0.00}";
            using SmtpClient sc = new SmtpClient("smtp.gmail.com", 587) { Credentials = new NetworkCredential("uygargunay@gmail.com", "zklk qkcu vwya qlky"), EnableSsl = true };
            sc.Send(mail);
        }
        catch { }
    }

    public void SendEmailSummary(string toEmail) => SendTradeAlert("DAILY REPORT", "SYSTEM", 0, 0);

    public void ArchiveDailyResults()
    {
        try
        {
            string log = $"{DateTime.Now:yyyy-MM-dd} | PnL: ${_totalRealizedPnL:0.00} | Peak: ${_peakDailyRealizedPnL:0.00}\n";
            File.AppendAllText("trade_history_log.txt", log);
        }
        catch { }
    }

    private void LogTradeAction(string symbol, string action, decimal price, decimal qty, decimal score)
    {
        try
        {
            string entry = $"[{action}] {symbol} | Price: {price:0.00} | Qty: {qty} | Time: {DateTime.Now}\n";
            File.AppendAllText("trade_executions.txt", entry);
        }
        catch { }
    }

    public void SaveState()
    {
        try
        {
            var data = new BotPersistData
            {
                Positions = _positions,
                TradesExecutedToday = _tradesExecutedToday,
                RealizedPnLTotal = _totalRealizedPnL,
                PeakDailyRealizedPnL = _peakDailyRealizedPnL,
                TradesPerSymbol = _tradesToday,
                BuyTimes = _buyTimes
            };
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
            _peakDailyRealizedPnL = data.PeakDailyRealizedPnL;
            foreach (var kv in data.Positions) _positions[kv.Key] = kv.Value;
            foreach (var kv in data.TradesPerSymbol) _tradesToday[kv.Key] = kv.Value;
            foreach (var kv in data.BuyTimes) _buyTimes[kv.Key] = kv.Value;
        }
        catch { }
    }

    public bool CheckDataHealth() => true; // Original method placeholder
}