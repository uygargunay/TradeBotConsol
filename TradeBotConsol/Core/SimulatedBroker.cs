using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Mail;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

public interface IBroker
{
    void SubmitOrder(string symbol, int qty, decimal price, TradeSide side);
}

public enum TradeSide { Buy, Sell }
public enum MarketRegime { Bullish, Neutral, Bearish }

// --- RESTORED TRADE CLASS ---
public class Trade
{
    public string Symbol { get; set; }
    public TradeSide Action { get; set; }
    public decimal Price { get; set; }
    public decimal Quantity { get; set; }
    public DateTime Time { get; set; } = DateTime.UtcNow;
}

public class BotPersistData
{
    public Dictionary<string, SimPosition> Positions { get; set; } = new();
    public int TradesExecutedToday { get; set; }
    public decimal RealizedPnLTotal { get; set; }
    public decimal TotalCommissions { get; set; }
    public decimal StartingDayEquity { get; set; }
    public Dictionary<string, int> TradesPerSymbol { get; set; } = new();
    public Dictionary<string, DateTime> BuyTimes { get; set; } = new();
    public Dictionary<string, List<decimal>> PriceHistory { get; set; } = new();
    public Dictionary<string, List<long>> VolumeHistory { get; set; } = new();
    public Dictionary<string, DateTime> LastSellTimes { get; set; } = new();
}

public class SimPosition
{
    public decimal Quantity { get; set; }
    public decimal AvgPrice { get; set; }
    public decimal TrailingStop { get; set; }
    public decimal CurrentPrice { get; set; }
    public decimal RealizedPnL { get; set; }
    public decimal UnrealizedPnL => Quantity * (CurrentPrice - AvgPrice);
}

// --- RESTORED POSITION MANAGER ---
public class PositionManager : SimulatedBroker { }

public class SimulatedBroker : IBroker
{
    // --- RISK MANAGEMENT ---
    private const decimal initialAccountValue = 4000m;
    private const decimal dailyLossLimitPct = 0.02m;
    private const decimal dailyProfitGoalPct = 0.04m;
    private const decimal maxTradeLossPct = 0.015m;
    private const int maxTradesGlobal = 30;
    private const int maxTradesPerSymbol = 5;
    private const decimal roundTripFee = 2.00m;
    private const decimal slippagePct = 0.0005m;

    private const decimal fullPositionSize = 1000m;
    private const decimal halfPositionSize = 500m;

    // --- STRATEGY SETTINGS ---
    private const int shortSmaPeriod = 9;
    private const int longSmaPeriod = 25;
    private const int rsiPeriod = 14;
    private const int aggressiveRsiThreshold = 80;
    private const int atrPeriod = 14;
    private const decimal stopLossAtrMult = 1.5m;
    private const decimal profitTargetMultiplier = 3.0m;
    private const int maxMinutesInTrade = 30;
    private const int cooldownMinutes = 15;

    private readonly string[] _tradeableStars = { "NVDA", "TSLA", "PLTR", "AMD" };
    protected readonly Dictionary<string, SimPosition> _positions = new();
    protected readonly Dictionary<string, List<decimal>> _priceHistory = new();
    protected readonly Dictionary<string, List<long>> _volumeHistory = new();
    protected readonly Dictionary<string, int> _tradesToday = new();
    protected readonly Dictionary<string, DateTime> _buyTimes = new();
    protected readonly Dictionary<string, DateTime> _lastSellTimes = new();
    private readonly HashSet<string> _pendingOrders = new();

    private MarketRegime _currentMarketRegime = MarketRegime.Bearish;
    private readonly TimeSpan _latestEntryTime = new TimeSpan(15, 30, 0);
    private DateTime _lastUpdateReceived = DateTime.UtcNow;
    private DateTime _lastHeartbeatLogged = DateTime.UtcNow;
    private const string SaveFilePath = "bot_state.json";
    private readonly object _lock = new object();

    private decimal _startingDayEquity = initialAccountValue;
    private decimal _totalRealizedPnL = 0m;
    private int _tradesExecutedToday = 0;
    private bool _haltNewTrades = false;

    public IBroker RealBroker { get; set; }
    private IbClient _ibClient;

    // --- RESTORED LOADSTATE METHOD ---
    public void LoadState()
    {
        if (!File.Exists(SaveFilePath)) return;
        try
        {
            var data = JsonSerializer.Deserialize<BotPersistData>(File.ReadAllText(SaveFilePath));
            if (data == null) return;

            // Check if state is from today or a previous day
            if (DateTime.UtcNow.Date != _lastUpdateReceived.Date && _lastUpdateReceived != default)
            {
                _totalRealizedPnL = 0;
                _tradesExecutedToday = 0;
                _startingDayEquity = initialAccountValue;
            }
            else
            {
                _totalRealizedPnL = data.RealizedPnLTotal;
                _tradesExecutedToday = data.TradesExecutedToday;
                _startingDayEquity = data.StartingDayEquity;
            }

            foreach (var kv in data.Positions) _positions[kv.Key] = kv.Value;
            foreach (var kv in data.PriceHistory) _priceHistory[kv.Key] = kv.Value;
            foreach (var kv in data.VolumeHistory) _volumeHistory[kv.Key] = kv.Value;
            foreach (var kv in data.LastSellTimes) _lastSellTimes[kv.Key] = kv.Value;
        }
        catch { }
    }

    // --- RESTORED SYNC METHOD ---
    public void SyncExistingPosition(string symbol, decimal qty, decimal avgPrice)
    {
        lock (_lock)
        {
            if (!_positions.ContainsKey(symbol))
            {
                _positions[symbol] = new SimPosition { Quantity = qty, AvgPrice = avgPrice, CurrentPrice = avgPrice, TrailingStop = avgPrice * 0.98m };
                _buyTimes[symbol] = DateTime.UtcNow;
            }
        }
    }

    public void RunWorker()
    {
        // 1. ADDED KEYBOARD LISTENER (Restores S, K, P functionality)
        Task.Run(() => {
            Console.WriteLine("[SYSTEM] Keyboard Controls Active: [S] Sell All, [K] Halt, [P] Status");
            while (true)
            {
                if (Console.KeyAvailable)
                {
                    var key = Console.ReadKey(true).Key;
                    if (key == ConsoleKey.S)
                    {
                        Console.WriteLine("\n[MANUAL] Emergency Liquidation Triggered...");
                        lock (_lock)
                        {
                            foreach (var sym in _positions.Keys.ToList())
                                SubmitOrder(sym, (int)_positions[sym].Quantity, _positions[sym].CurrentPrice, TradeSide.Sell);
                        }
                    }
                    else if (key == ConsoleKey.K)
                    {
                        _haltNewTrades = !_haltNewTrades;
                        Console.WriteLine($"\n[MANUAL] Trading Halt: {_haltNewTrades}");
                    }
                    else if (key == ConsoleKey.P)
                    {
                        PrintStatusTable();
                    }
                }
                Thread.Sleep(100);
            }
        });

        // 2. MAIN CONNECTION LOOP
        while (true)
        {
            try
            {
                if (_ibClient == null)
                {
                    _ibClient = new IbClient();
                    _ibClient.OnPrice += (symbol, price) =>
                        OnPriceUpdate(_ibClient.GetLatestPrices(), _ibClient.GetLatestVolumes());
                }

                if (!_ibClient.IsConnected())
                {
                    Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] Connecting to TWS...");
                    _ibClient.Connect("127.0.0.1", 7497, 1);
                    this.RealBroker = _ibClient;

                    // Call startup config only after connection
                    PrintStartupConfiguration();
                }

                while (_ibClient != null && _ibClient.IsConnected())
                {
                    CheckHealth();
                    Thread.Sleep(1000);
                }
            }
            catch (Exception ex) { Console.WriteLine($"[CRITICAL] {ex.Message}"); }
            Thread.Sleep(5000);
        }
    }
    public void OnPriceUpdate(Dictionary<string, decimal> prices, Dictionary<string, long> volumes)
    {
        lock (_lock)
        {
            DateTime nyNow = GetEasternTime();
            if (_lastUpdateReceived.Date != DateTime.UtcNow.Date) ResetDailyTrades();
            _lastUpdateReceived = DateTime.UtcNow;

            if (prices.ContainsKey("QQQ"))
            {
                UpdateHistory("QQQ", prices["QQQ"], volumes.GetValueOrDefault("QQQ", 0));
                double qqqRsi = CalculateRSI("QQQ", 14);
                bool qqqTrendUp = IsTrendUp("QQQ");

                if (qqqTrendUp) _currentMarketRegime = MarketRegime.Bullish;
                else if (qqqRsi > 40) _currentMarketRegime = MarketRegime.Neutral;
                else _currentMarketRegime = MarketRegime.Bearish;
            }

            foreach (var kv in prices)
            {
                string symbol = kv.Key;
                if (symbol == "QQQ") continue;

                decimal price = kv.Value;
                UpdateHistory(symbol, price, volumes.GetValueOrDefault(symbol, 0));

                if (!_priceHistory.ContainsKey(symbol) || _priceHistory[symbol].Count < longSmaPeriod) continue;

                if (_positions.ContainsKey(symbol)) ManagePosition(symbol, price);

                bool inCooldown = _lastSellTimes.TryGetValue(symbol, out var lastSell) && (DateTime.UtcNow - lastSell).TotalMinutes < cooldownMinutes;
                bool marketOpen = nyNow.TimeOfDay >= new TimeSpan(9, 30, 0) && nyNow.TimeOfDay < _latestEntryTime;

                if (!_haltNewTrades && marketOpen && _tradeableStars.Contains(symbol) && !_positions.ContainsKey(symbol) && !inCooldown)
                {
                    if (_currentMarketRegime != MarketRegime.Bearish && IsTrendUp(symbol) && IsVolumeSpiking(symbol))
                    {
                        if (CalculateRSI(symbol, rsiPeriod) < aggressiveRsiThreshold)
                            TryEnter(symbol, price);
                    }
                }
            }
            CheckEndOfDayLiquidation();
        }
    }

    protected void TryEnter(string symbol, decimal price)
    {
        if (_pendingOrders.Contains(symbol)) return;
        if (_tradesExecutedToday >= maxTradesGlobal) return;

        decimal targetSpend = _currentMarketRegime == MarketRegime.Bullish ? fullPositionSize : halfPositionSize;
        if (AvailableBuyingPower < targetSpend) targetSpend = AvailableBuyingPower;

        // Use Math.Floor to ensure we get a whole number (e.g., 10 instead of 10.8)
        int qty = (int)Math.Floor(targetSpend / price);

        if (qty > 0)
        {
            SubmitOrder(symbol, qty, price, TradeSide.Buy);
        }
    }

    public void SubmitOrder(string symbol, int qty, decimal price, TradeSide side)
    {
        if (_pendingOrders.Contains(symbol)) return;
        decimal execPrice = side == TradeSide.Buy ? price * (1 + slippagePct) : price * (1 - slippagePct);

        if (side == TradeSide.Buy)
        {
            if (_positions.ContainsKey(symbol)) return;
            _pendingOrders.Add(symbol);

            decimal atrDistance = CalculateTrueATR(symbol) * stopLossAtrMult;
            decimal finalStopDistance = Math.Max(atrDistance, execPrice * 0.006m);

            _positions[symbol] = new SimPosition
            {
                AvgPrice = execPrice,
                Quantity = qty,
                CurrentPrice = execPrice,
                TrailingStop = execPrice - finalStopDistance
            };

            _buyTimes[symbol] = DateTime.UtcNow;
            _tradesExecutedToday++;
            _tradesToday[symbol] = _tradesToday.GetValueOrDefault(symbol) + 1;

            // Audio and Console Alert
            Console.Beep(1500, 200); Console.Beep(1800, 300);
            string logMsg = $"BUY {qty} {symbol} @ {price:C2} | Regime: {_currentMarketRegime}";
            Console.WriteLine($"\n*** [TRADE] {logMsg} ***");

            // EMAIL ALERT
            SendEmailNotification($"TRADE ALERT: BUY {symbol}",
                $"{logMsg}\nStop Loss Set: {(_positions[symbol].TrailingStop):C2}\nAccount Power: {AvailableBuyingPower:C2}");

            RealBroker?.SubmitOrder(symbol, qty, price, side);
        }
        else if (_positions.TryGetValue(symbol, out var pos))
        {
            if (price > pos.TrailingStop && (DateTime.UtcNow - _buyTimes[symbol]).TotalSeconds < 60) return;

            _pendingOrders.Add(symbol);
            decimal pnl = (execPrice - pos.AvgPrice) * pos.Quantity - roundTripFee;
            _totalRealizedPnL += pnl;
            _positions.Remove(symbol);
            _lastSellTimes[symbol] = DateTime.UtcNow;

            // Audio and Console Alert
            Console.Beep(800, 400);
            string logMsg = $"SELL {symbol} @ {price:C2} | PnL: {pnl:C2}";
            Console.WriteLine($"\n*** [TRADE] {logMsg} ***");

            // EMAIL ALERT
            SendEmailNotification($"TRADE ALERT: SELL {symbol}",
                $"{logMsg}\nDaily Total PnL: {_totalRealizedPnL:C2}\nTrades Today: {_tradesExecutedToday}");

            RealBroker?.SubmitOrder(symbol, (int)pos.Quantity, price, side);

            if (_totalRealizedPnL <= CurrentDailyLossLimit || _totalRealizedPnL >= CurrentDailyProfitGoal)
                _haltNewTrades = true;
        }

        Task.Delay(5000).ContinueWith(_ => { lock (_lock) { _pendingOrders.Remove(symbol); } });
        SaveState();
    }
    protected void ManagePosition(string symbol, decimal price)
    {
        if (_pendingOrders.Contains(symbol)) return;
        var pos = _positions[symbol];
        pos.CurrentPrice = price;
        decimal atr = CalculateTrueATR(symbol);

        decimal newStop = price - (atr * stopLossAtrMult);
        if (newStop > pos.TrailingStop) pos.TrailingStop = newStop;

        bool hitStop = price <= pos.TrailingStop || price <= pos.AvgPrice * (1 - maxTradeLossPct);
        bool hitTarget = price >= (pos.AvgPrice + (atr * stopLossAtrMult * profitTargetMultiplier));
        bool timedOut = (DateTime.UtcNow - _buyTimes[symbol]).TotalMinutes > maxMinutesInTrade;

        if (hitStop || hitTarget || timedOut) SubmitOrder(symbol, (int)pos.Quantity, price, TradeSide.Sell);
    }

    protected bool IsTrendUp(string symbol)
    {
        if (!_priceHistory.ContainsKey(symbol) || _priceHistory[symbol].Count < longSmaPeriod) return false;
        var h = _priceHistory[symbol];
        decimal s9 = h.TakeLast(shortSmaPeriod).Average();
        decimal sLong = h.TakeLast(longSmaPeriod).Average();
        return s9 > (sLong + (sLong * 0.0003m));
    }

    protected double CalculateRSI(string symbol, int period)
    {
        if (!_priceHistory.ContainsKey(symbol) || _priceHistory[symbol].Count <= period) return 50;
        var prices = _priceHistory[symbol];
        decimal gain = 0, loss = 0;
        for (int i = 1; i <= period; i++)
        {
            decimal diff = prices[prices.Count - i] - prices[prices.Count - i - 1];
            if (diff > 0) gain += diff; else loss -= diff;
        }
        if (loss == 0) return 100;
        return 100 - (100 / (1 + (double)(gain / loss)));
    }

    protected decimal CalculateTrueATR(string symbol)
    {
        var h = _priceHistory.GetValueOrDefault(symbol);
        if (h == null || h.Count < atrPeriod + 1) return 0.50m;
        decimal sum = 0;
        for (int i = h.Count - atrPeriod; i < h.Count; i++) sum += Math.Abs(h[i] - h[i - 1]);
        return sum / atrPeriod;
    }

    protected bool IsVolumeSpiking(string symbol)
    {
        if (!_volumeHistory.ContainsKey(symbol) || _volumeHistory[symbol].Count < 6) return false;
        var history = _volumeHistory[symbol];
        return (double)history.Last() > (history.SkipLast(1).TakeLast(5).Average() * 1.10);
    }

    public void UpdateHistory(string symbol, decimal price, long volume)
    {
        if (!_priceHistory.ContainsKey(symbol)) { _priceHistory[symbol] = new List<decimal>(); _volumeHistory[symbol] = new List<long>(); }
        _priceHistory[symbol].Add(price); _volumeHistory[symbol].Add(volume);
        if (_priceHistory[symbol].Count > 200) { _priceHistory[symbol].RemoveAt(0); _volumeHistory[symbol].RemoveAt(0); }
    }

    private decimal CurrentDailyLossLimit => _startingDayEquity * dailyLossLimitPct * -1;
    private decimal CurrentDailyProfitGoal => _startingDayEquity * dailyProfitGoalPct;
    private decimal AvailableBuyingPower => initialAccountValue - _positions.Values.Sum(p => p.Quantity * p.CurrentPrice);

    // --- RESTORED LOGGING / STARTUP METHODS ---
    public void PrintStartupConfiguration()
    {
        Console.WriteLine("========================================");
        Console.WriteLine($" TRADING BOT (SMA Cross: {shortSmaPeriod}/{longSmaPeriod})");
        Console.WriteLine("========================================");
        Console.WriteLine($"Power: {initialAccountValue:C2} | Status: SMART ACTIVE");
        Console.WriteLine($"Current Market Regime: {_currentMarketRegime}");
        Console.WriteLine("========================================");

        // --- SEND TEST EMAIL ---
        Console.WriteLine("[SYSTEM] Sending startup test email...");
        SendEmailNotification("Bot Online", $"TradeBot started successfully at {DateTime.Now}. Market Regime is currently {_currentMarketRegime}. Ready for Smart Sizing.");

        Console.WriteLine("\n[CONTROLS]");
        Console.WriteLine(" [S] Sell All | [K] Halt Trades | [P] Manual Status");
        Console.WriteLine("====================================================\n");
    }

    public void ArchiveDailyResults()
    {
        try
        {
            string logLine = $"{DateTime.Now:yyyy-MM-dd HH:mm} | PnL: {_totalRealizedPnL:C2} | Trades: {_tradesExecutedToday}\n";
            File.AppendAllText("trade_history_log.txt", logLine);
        }
        catch { }
    }
    public void SendEmailNotification(string subject, string messageBody)
    {
        try
        {
            ServicePointManager.SecurityProtocol = SecurityProtocolType.Tls12;
            var fromAddress = new MailAddress("uygargunay@gmail.com", "TradeBot Live");
            var toAddress = new MailAddress("uygargunay@gmail.com");

            // USE YOUR NEW PASSWORD HERE
            const string fromPassword = "sznd kafk nhec skqh";

            var smtp = new SmtpClient
            {
                Host = "smtp.gmail.com",
                Port = 587,
                EnableSsl = true,
                DeliveryMethod = SmtpDeliveryMethod.Network,
                UseDefaultCredentials = false,
                Credentials = new NetworkCredential(fromAddress.Address, fromPassword),
                Timeout = 10000
            };

            using (var message = new MailMessage(fromAddress, toAddress) { Subject = subject, Body = messageBody })
            {
                smtp.Send(message);
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[EMAIL ERROR] {ex.Message}");
        }
    }
    public void PrintStatusTable()
    {
        Console.WriteLine("\n" + new string('=', 105));
        Console.WriteLine("{0,-8} | {1,-10} | {2,-6} | {3,-8} | {4,-5} | {5,-8} | {6,-12} | {7,-10}",
            "SYMBOL", "PRICE", "MINS", "TREND", "RSI", "PnL", "GAP TO BUY", "REGIME");
        Console.WriteLine(new string('-', 105));

        foreach (var symbol in _tradeableStars.Concat(new[] { "QQQ" }))
        {
            if (!_priceHistory.ContainsKey(symbol) || _priceHistory[symbol].Count == 0) continue;
            var history = _priceHistory[symbol];
            decimal price = history.Last();
            decimal s9 = history.TakeLast(Math.Min(history.Count, 9)).Average();
            decimal sLong = history.TakeLast(Math.Min(history.Count, longSmaPeriod)).Average();
            decimal buffer = sLong * 0.0003m;
            decimal gap = (sLong + buffer) - s9;
            string gapStr = gap > 0 ? $"{gap:F2}" : "READY";
            if (s9 > (sLong + buffer)) gapStr = "BULLISH";
            string pnlStr = _positions.TryGetValue(symbol, out var pos) ? $"{(price - pos.AvgPrice) * pos.Quantity:C2}" : "---";

            Console.WriteLine("{0,-8} | {1,-10:C2} | {2,-6} | {3,-8} | {4,-5:F0} | {5,-8} | {6,-12} | {7,-10}",
                symbol, price, history.Count, (s9 > (sLong + buffer) ? "UP" : "DOWN"),
                CalculateRSI(symbol, rsiPeriod), pnlStr, gapStr, (symbol == "QQQ" ? _currentMarketRegime.ToString() : ""));
        }
    }

    private DateTime GetEasternTime()
    {
        string tzId = RuntimeInformation.IsOSPlatform(OSPlatform.Windows) ? "Eastern Standard Time" : "America/New_York";
        return TimeZoneInfo.ConvertTimeFromUtc(DateTime.UtcNow, TimeZoneInfo.FindSystemTimeZoneById(tzId));
    }

    public void CheckHealth() { if ((DateTime.UtcNow - _lastHeartbeatLogged).TotalSeconds >= 60) { PrintStatusTable(); _lastHeartbeatLogged = DateTime.UtcNow; } }
    public void SaveState() { try { File.WriteAllText(SaveFilePath, JsonSerializer.Serialize(new BotPersistData { Positions = _positions, TradesExecutedToday = _tradesExecutedToday, RealizedPnLTotal = _totalRealizedPnL, StartingDayEquity = _startingDayEquity, TradesPerSymbol = _tradesToday, BuyTimes = _buyTimes, PriceHistory = _priceHistory, VolumeHistory = _volumeHistory, LastSellTimes = _lastSellTimes })); } catch { } }
    public void ResetDailyTrades() { lock (_lock) { _priceHistory.Clear(); _volumeHistory.Clear(); _startingDayEquity += _totalRealizedPnL; _tradesExecutedToday = 0; _totalRealizedPnL = 0m; _tradesToday.Clear(); _buyTimes.Clear(); _lastSellTimes.Clear(); SaveState(); } }
    public void CheckEndOfDayLiquidation() { if (GetEasternTime().TimeOfDay > new TimeSpan(15, 45, 0)) { foreach (var s in _positions.Keys.ToList()) SubmitOrder(s, (int)_positions[s].Quantity, _positions[s].CurrentPrice, TradeSide.Sell); } }
}