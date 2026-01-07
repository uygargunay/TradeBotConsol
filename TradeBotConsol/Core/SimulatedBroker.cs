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

    // --- WATCHDOG TRACKING ---
    private readonly Dictionary<string, DateTime> _lastPriceUpdate = new();
    private const int WatchdogTimeoutSeconds = 60;

    private const string SaveFilePath = "bot_state.json";

    // SMA TREND PERIODS
    private const int shortSmaPeriod = 20;
    private const int longSmaPeriod = 100;

    // RISK SETTINGS
    private const int maxTradesGlobal = 10;
    private const int maxTradesPerSymbol = 3;
    private const decimal maxDailyLoss = -300.00m;

    private const decimal stopLossPercent = 0.015m;
    private const decimal targetProfitPercent = 0.03m;
    private const decimal tradeDollarAmount = 2000m;

    private readonly TimeSpan defaultCooldown = TimeSpan.FromMinutes(15);
    private readonly TimeSpan lossCooldown = TimeSpan.FromMinutes(60);

    public IReadOnlyDictionary<string, SimPosition> Positions => _positions;
    private int _tradesExecutedToday = 0;
    private decimal _totalRealizedPnL = 0m;

    private bool _spyBullish = false;
    private bool _qqqBullish = false;

    // --- NEW: Tracks if data is LIVE or DELAYED ---
    public void UpdateDataStatus(string symbol, int dataType)
    {
        _dataStatus[symbol] = (dataType == 1) ? "LIVE" : "DELAYED";
    }

    public List<Trade> OnPriceUpdate(Dictionary<string, decimal> marketPrices)
    {
        var tradesExecuted = new List<Trade>();

        if (_totalRealizedPnL <= maxDailyLoss) return tradesExecuted;

        var momentumScores = new List<(string symbol, decimal score)>();

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

                momentumScores.Add((symbol, score));
            }
        }

        // 3. MARKET REGIME FILTER (Prioritize QQQ because it is LIVE)
        bool canBuyNew = _qqqBullish;

        var sortedMomentum = momentumScores.OrderByDescending(x => x.score).ToList();

        foreach (var kv in sortedMomentum)
        {
            var symbol = kv.symbol;
            if (symbol == "SPY" || symbol == "QQQ") continue;

            var price = marketPrices[symbol];

            // --- SELL LOGIC ---
            if (_positions.ContainsKey(symbol))
            {
                var pos = _positions[symbol];
                pos.CurrentPrice = price;

                decimal newStop = price * (1 - stopLossPercent);
                if (newStop > pos.TrailingStop) pos.TrailingStop = newStop;

                var targetPrice = pos.AvgPrice * (1 + targetProfitPercent);
                bool hasHeldLongEnough = _buyTimes.TryGetValue(symbol, out var buyTime) && (DateTime.Now - buyTime) >= defaultCooldown;
                decimal currentGrossProfit = pos.Quantity * (price - pos.AvgPrice);

                bool triggerStopLoss = price <= pos.TrailingStop;
                bool triggerTakeProfit = price >= targetPrice && hasHeldLongEnough && currentGrossProfit > 2.00m;

                if (triggerStopLoss || triggerTakeProfit)
                {
                    _totalRealizedPnL += currentGrossProfit;
                    tradesExecuted.Add(new Trade { Symbol = symbol, Action = TradeSide.Sell, Price = price, Quantity = pos.Quantity });
                    LogTradeAction(symbol, "SELL", price, pos.Quantity, 0);
                    if (currentGrossProfit > 0)
                    {
                        _tradesExecutedToday = Math.Max(0, _tradesExecutedToday - 1);
                        if (_tradesToday.ContainsKey(symbol)) _tradesToday[symbol] = Math.Max(0, _tradesToday[symbol] - 1);
                        _sellCooldowns[symbol] = DateTime.Now;
                    }
                    else
                    {
                        _sellCooldowns[symbol] = DateTime.Now.Add(lossCooldown - defaultCooldown);
                    }

                    _positions.Remove(symbol);
                    _buyTimes.Remove(symbol);
                    SaveState();
                }
            }

            // --- BUY LOGIC ---
            int tradesForThisStock = _tradesToday.ContainsKey(symbol) ? _tradesToday[symbol] : 0;
            bool isCoolingDown = _sellCooldowns.TryGetValue(symbol, out var lastSell) && (DateTime.Now - lastSell) < defaultCooldown;

            if (kv.score > 0 && canBuyNew && !_positions.ContainsKey(symbol) && IsInTradingWindow() && !isCoolingDown)
            {
                if (_tradesExecutedToday < maxTradesGlobal && tradesForThisStock < maxTradesPerSymbol)
                {
                    var qty = Math.Floor(tradeDollarAmount / price);
                    if (qty > 0)
                    {
                        tradesExecuted.Add(new Trade { Symbol = symbol, Action = TradeSide.Buy, Price = price, Quantity = qty });
                        LogTradeAction(symbol, "BUY", price, qty, kv.score);
                        _positions[symbol] = new SimPosition { AvgPrice = price, Quantity = qty, CurrentPrice = price, TrailingStop = price * (1 - stopLossPercent) };
                        _buyTimes[symbol] = DateTime.Now;
                        _tradesExecutedToday++;
                        _tradesToday[symbol] = tradesForThisStock + 1;
                        SaveState();
                    }
                }
            }
        }
        return tradesExecuted;
    }

    public bool CheckDataHealth()
    {
        if (!IsInTradingWindow()) return true;
        var criticalSymbols = new[] { "SPY", "QQQ" };
        foreach (var sym in criticalSymbols)
        {
            if (!_lastPriceUpdate.TryGetValue(sym, out var lastTime) || (DateTime.Now - lastTime).TotalSeconds > WatchdogTimeoutSeconds)
            {
                Console.WriteLine($"[ALERT] DATA STALE: No updates for {sym}!");
                return false;
            }
        }
        return true;
    }

    public List<Trade> CheckEndOfDayLiquidation()
    {
        var tradesToExecute = new List<Trade>();
        TimeSpan closeTime = new TimeSpan(12, 55, 0);

        if (DateTime.Now.TimeOfDay >= closeTime && _positions.Count > 0)
        {
            Console.WriteLine($"[SYSTEM] EOD Liquidation Triggered.");
            foreach (var symbol in _positions.Keys.ToList())
            {
                var pos = _positions[symbol];
                tradesToExecute.Add(new Trade { Symbol = symbol, Action = TradeSide.Sell, Price = pos.CurrentPrice, Quantity = pos.Quantity });
                _positions.Remove(symbol);
            }
            SaveState();
            SendEmailSummary("uygargunay@gmail.com");
        }
        return tradesToExecute;
    }

    public void SyncExistingPosition(string symbol, decimal qty, decimal avgPrice)
    {
        if (!_positions.ContainsKey(symbol))
        {
            _positions[symbol] = new SimPosition { Quantity = qty, AvgPrice = avgPrice, CurrentPrice = avgPrice, TrailingStop = avgPrice * (1 - stopLossPercent) };
            if (!_buyTimes.ContainsKey(symbol)) _buyTimes[symbol] = DateTime.Now;
            SaveState();
        }
    }

    public bool IsInTradingWindow()
    {
        var now = DateTime.Now.TimeOfDay;
        return now >= new TimeSpan(6, 45, 0) && now <= new TimeSpan(11, 30, 0);
    }

    public void SaveState()
    {
        try
        {
            var data = new BotPersistData
            {
                Positions = _positions.ToDictionary(k => k.Key, v => v.Value),
                TradesExecutedToday = _tradesExecutedToday,
                RealizedPnLTotal = _totalRealizedPnL,
                TradesPerSymbol = _tradesToday.ToDictionary(k => k.Key, v => v.Value),
                BuyTimes = _buyTimes.ToDictionary(k => k.Key, v => v.Value)
            };
            string json = JsonSerializer.Serialize(data, new JsonSerializerOptions { WriteIndented = true });
            string tempPath = SaveFilePath + ".tmp";
            File.WriteAllText(tempPath, json);
            if (File.Exists(SaveFilePath)) File.Delete(SaveFilePath);
            File.Move(tempPath, SaveFilePath);
        }
        catch (Exception ex) { Console.WriteLine("[CRITICAL] Save Error: " + ex.Message); }
    }

    public void LoadState()
    {
        if (!File.Exists(SaveFilePath)) return;
        try
        {
            string json = File.ReadAllText(SaveFilePath);
            if (string.IsNullOrWhiteSpace(json)) return;
            var data = JsonSerializer.Deserialize<BotPersistData>(json);
            if (data == null) return;
            _tradesExecutedToday = data.TradesExecutedToday;
            _totalRealizedPnL = data.RealizedPnLTotal;
            _positions.Clear();
            foreach (var kv in data.Positions) _positions[kv.Key] = kv.Value;
            _tradesToday.Clear();
            if (data.TradesPerSymbol != null) foreach (var kv in data.TradesPerSymbol) _tradesToday[kv.Key] = kv.Value;
            _buyTimes.Clear();
            if (data.BuyTimes != null) foreach (var kv in data.BuyTimes) _buyTimes[kv.Key] = kv.Value;
        }
        catch (Exception) { }
    }

    public void ResetDailyTrades()
    {
        _tradesExecutedToday = 0;
        _tradesToday.Clear();
        _totalRealizedPnL = 0m;
        _sellCooldowns.Clear();
        _buyTimes.Clear();
        SaveState();
    }

    public void PrintDailySummary()
    {
        Console.WriteLine("\n--- DATA FEED STATUS ---");
        foreach (var s in _dataStatus) Console.WriteLine($"{s.Key}: {s.Value}");

        decimal totalCapital = maxTradesGlobal * tradeDollarAmount;
        decimal totalPct = totalCapital > 0 ? (_totalRealizedPnL / totalCapital) * 100 : 0;

        Console.WriteLine($"\n--- Summary: Realized ${_totalRealizedPnL:0.00} ({totalPct:0.00}%) ---");
        Console.WriteLine($"Market: SPY={(_spyBullish ? "UP" : "DOWN")} | QQQ={(_qqqBullish ? "UP" : "DOWN")}");

        foreach (var kv in _positions)
        {
            var p = kv.Value;
            decimal pPct = p.AvgPrice > 0 ? ((p.CurrentPrice - p.AvgPrice) / p.AvgPrice) * 100 : 0;
            Console.WriteLine($"{kv.Key}: {pPct:0.00}% | PnL: ${p.UnrealizedPnL:0.00}");
        }
    }

    public void SendEmailSummary(string toEmail)
    {
        try
        {
            string fromEmail = "uygargunay@gmail.com";
            string password = "zklk qkcu vwya qlky";
            using MailMessage mail = new MailMessage(fromEmail, toEmail);

            // Dynamic subject based on whether it's a startup or EOD report
            mail.Subject = _totalRealizedPnL == 0
                ? "TradeBot: Connection Established & Monitoring"
                : $"Daily Report: ${_totalRealizedPnL:0.00}";

            mail.Body = $"Status: Online\nTime: {DateTime.Now}\nRealized PnL: ${_totalRealizedPnL:0.00}";

            using SmtpClient client = new SmtpClient("smtp.gmail.com", 587)
            {
                Credentials = new NetworkCredential(fromEmail, password),
                EnableSsl = true
            };
            client.Send(mail);
        }
        catch (Exception ex) { Console.WriteLine("Email Fail: " + ex.Message); }
    }

    public void ArchiveDailyResults()
    {
        try
        {
            string archivePath = "trade_history_log.txt";
            decimal totalCapital = maxTradesGlobal * tradeDollarAmount;
            decimal totalPct = totalCapital > 0 ? (_totalRealizedPnL / totalCapital) * 100 : 0;
            string logEntry = $"{DateTime.Now:yyyy-MM-dd} | PnL: ${_totalRealizedPnL:0.00} | Gain: {totalPct:0.00}% | Trades: {_tradesExecutedToday}\n";
            File.AppendAllText(archivePath, logEntry);
            Console.WriteLine("[SYSTEM] Daily results archived to trade_history_log.txt");
        }
        catch (Exception ex) { Console.WriteLine("[ERROR] Failed to archive: " + ex.Message); }
    }
    private void LogTradeAction(string symbol, string action, decimal price, decimal qty, decimal score)
    {
        try
        {
            string logPath = "trade_executions.txt";
            string timestamp = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss");

            // Example: [BUY] PLTR | Price: 25.40 | Qty: 78 | Score: 0.12 | Time: 08:45:02
            string entry = $"[{action.ToUpper()}] {symbol} | Price: {price:0.00} | Qty: {qty} | Score: {score:0.00} | Time: {timestamp}\n";

            File.AppendAllText(logPath, entry);
        }
        catch (Exception ex)
        {
            Console.WriteLine("[LOG ERROR] Could not write to trade log: " + ex.Message);
        }
    }
}