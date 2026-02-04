using IBApi;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Mail;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Threading.Tasks;

public interface IBroker
{
    void SubmitOrder(string symbol, int qty, decimal price, TradeSide side, double currentRsi = 0, string orderType = "LMT");
    bool TryDequeueIbLog(out string log);
}

public enum TradeSide { Buy, Sell }
public enum OrderLifeState
{
    Submitted,
    PartiallyFilled,
    Filled,
    Cancelled,
    Rejected
}

public class SimPosition
{
    public string Symbol { get; set; }
    public int Quantity { get; set; } // CHANGED TO INT
    public decimal AvgPrice { get; set; }
    public decimal TrailingStop { get; set; }
    public decimal CurrentPrice { get; set; }
    public decimal RealizedPnL { get; set; }
    public decimal UnrealizedPnL => Quantity * (CurrentPrice - AvgPrice);
    public bool IsBreakEvenProtected { get; set; }
    public DateTime EntryTime { get; set; } = DateTime.UtcNow;
}

public class BotPersistData
{
    public Dictionary<string, SimPosition> Positions { get; set; } = new();
    public int TradesExecutedToday { get; set; }
    public int DailyLossCount { get; set; }
    public decimal RealizedPnLTotal { get; set; }
    public decimal StartingDayEquity { get; set; }
    public Dictionary<string, int> TradesPerSymbol { get; set; } = new();
    public ConcurrentDictionary<string, DateTime> BuyTimes = new ConcurrentDictionary<string, DateTime>();

    public ConcurrentDictionary<string, List<decimal>> PriceHistory { get; set; } = new();
    public ConcurrentDictionary<string, List<long>> VolumeHistory { get; set; } = new();
    public Dictionary<string, DateTime> LastSellTimes { get; set; } = new();
    public Dictionary<string, bool> LastTradeWasLoss { get; set; } = new();
}

public class TrackedOrder
{
    public int OrderId;
    public string Symbol;
    public TradeSide Side;
    public int Qty;
    public int FilledQty;
    public DateTime Time;
    public OrderLifeState State;
}

public class SimulatedBroker : IBroker
{
    public IBroker RealBroker { get; set; }
    public Action<int> OnOrderFailed;

    private const double RSI_HOOK_DROP = 5.0;
    private const decimal dailyProfitGoalPct = 0.025m;
    private const decimal dailyLossLimitPct = 0.05m;
    private const int MaxActivePositions = 3;
   
    private const decimal roundTripFee = 2.00m;

    public bool IsHalted => _haltNewTrades;
    public bool GoalReached => _goalReached;

    private readonly ConcurrentDictionary<string, List<decimal>> _priceHistory = new();
    private readonly ConcurrentDictionary<string, List<long>> _volumeHistory = new();
    private readonly Dictionary<string, SimPosition> _positions = new();
    private readonly ConcurrentDictionary<string, bool> _pendingOrders = new();
    private readonly ConcurrentDictionary<int, TrackedOrder> _ordersById = new();

    private bool _softTradeUsed = false;
    private readonly ConcurrentQueue<string> _ibLogs = new();
    protected int _dailyLossCount = 0;
    private ConcurrentDictionary<string, Queue<(double score, DateTime time)>> _raceScoreHistory = new();
    private decimal _startingDayEquity = 4000m;
    private decimal _totalRealizedPnL = 0m;
    public readonly string[] _tradeableStars =
    {
        "NVDA", "AMD", "META", "AMZN", "GOOGL", "MSFT",
        "TSLA", "SMCI", "ARM", "COIN", "MSTR",
        "AVGO", "PANW", "CRWD", "NOW", "ADBE",
        "NFLX", "PLTR", "SNOW", "SHOP", "UBER",
        "MU","SOXL",
        "MARA","RIOT",
        "BABA","PDD",
        "JPM","GS",
        "QQQ"
    };

 
    private ConcurrentDictionary<string, double> _lastRsiMemory = new ConcurrentDictionary<string, double>();
    private ConcurrentDictionary<string, long> _cumVolume = new();
    private ConcurrentDictionary<string, decimal> _cumVwapProd = new();
    private ConcurrentDictionary<string, DateTime> _symbolLastResetDate = new();

    private ConcurrentDictionary<string, int> _symbolToOrderId = new();
    protected readonly Dictionary<string, int> _tradesToday = new();
    protected readonly Dictionary<string, DateTime> _lastSellTimes = new();
    private readonly ConcurrentDictionary<string, DateTime> _buyTimes = new ConcurrentDictionary<string, DateTime>();
    private readonly DateTime _botStartTime = DateTime.UtcNow;

    private ConcurrentDictionary<string, DateTime> _ignoreSyncUntil = new();
    private ConcurrentDictionary<string, DateTime> _lastPriceAdd = new();

    private decimal _rollingPnL = 0m;
    private int _rollingTrades = 0;
    private const string SaveFilePath = "bot_state.json";
    protected readonly object _lock = new object();

    private int _tradesExecutedToday = 0;
    private bool _haltNewTrades = false;
    private bool _goalReached = false;
    private volatile bool _killSwitchEngaged = false;
    private volatile bool _emergencyExit = false;
    private Dictionary<string, decimal> _learnedAvgVolume = new();
    private const string MemoryFilePath = "market_memory.json";
    private ConcurrentDictionary<string, double> _raceScores = new();
    private ConcurrentDictionary<string, double> _lastRaceScores = new();
    private ConcurrentDictionary<string, double> _peakRsi = new ConcurrentDictionary<string, double>();
    private readonly ConcurrentDictionary<string, DateTime> _lastPriceUpdate = new();
    protected readonly List<ClosedTrade> _tradeHistory = new();
    private bool _hasSavedToday = false;
    private bool _eodEmailSent = false;
    private static readonly object _memoryFileLock = new object();
    private volatile bool _isSavingMemory = false;
    private static readonly TimeSpan ObservationEnd = new TimeSpan(10, 00, 0);
    private HashSet<string> _syncedSymbols = new HashSet<string>();
    private Dictionary<string, bool> _lastTradeWasLoss = new Dictionary<string, bool>();
    private int _chopWarnings = 0;

    public bool TryDequeueIbLog(out string log) => _ibLogs.TryDequeue(out log);

    public class ClosedTrade
    {
        public string Symbol { get; set; }
        public decimal Profit { get; set; }
        public DateTime ExitTime { get; set; }
    }

    public SimulatedBroker()
    {
        OnOrderFailed = HandleOrderFailure;
    }

    private void HandleOrderFailure(int orderId)
    {
        if (_ordersById.TryRemove(orderId, out var order))
        {
            _symbolToOrderId.TryRemove(order.Symbol, out _);
            _pendingOrders.TryRemove(order.Symbol, out _);
            _softTradeUsed = false;
        }
    }

    public bool TryGetTrackedOrder(int orderId, out TrackedOrder order)
    {
        return _ordersById.TryGetValue(orderId, out order);
    }

    private void CheckForStuckOrders()
    {
        foreach (var kv in _ordersById)
        {
            var o = kv.Value;
            if (o.State == OrderLifeState.Submitted && (DateTime.UtcNow - o.Time).TotalSeconds > 60)
            {
                TriggerKillSwitch($"Order timeout: {o.Symbol}");
            }
        }
    }

    private void TriggerKillSwitch(string reason)
    {
        if (_killSwitchEngaged) return;
        _killSwitchEngaged = true;
        _haltNewTrades = true;
        Console.WriteLine($"🚨 KILL SWITCH ENGAGED: {reason}");
        Task.Run(() => LiquidateAll());
    }

    // ==========================================
    // LOGIC RESTORED HERE
    // ==========================================
    private double RegimeBias()
    {
        if (!_priceHistory.TryGetValue("QQQ", out var qqq) || qqq.Count < 20) return 1;
        string trend = GetTrend(qqq);
        return trend == "BULL" ? 1.15 : trend == "BEAR" ? 0.75 : 1.0;
    }

    private double GetRaceSlope(string symbol)
    {
        if (!_raceScoreHistory.TryGetValue(symbol, out var q) || q.Count < 6)
            return 0;

        lock (q)
        {
            var arr = q.ToArray();
            double sum = 0;
            for (int i = 1; i < arr.Length; i++)
                sum += (arr[i].score - arr[i - 1].score);

            return sum / (arr.Length - 1);
        }
    }

    private double CalculateRaceScore(string symbol)
    {
        if (!_priceHistory.TryGetValue(symbol, out var prices) || prices.Count < 15) return 0;
        if (!_volumeHistory.TryGetValue(symbol, out var volumes) || volumes.Count < 15) return 0;

        decimal priceROC = (prices.Last() - prices[^6]) / prices[^6] * 100m;
        if (volumes.Count < 10 || prices.Count < 10) return 0;
        decimal avgVol = (decimal)volumes.Skip(volumes.Count - 10).Average();

        decimal volExpansion = avgVol > 0 ? volumes.Last() / avgVol : 0;

        double rawScore = (double)(priceROC * 25m) + (double)(volExpansion * 15m);
        return Math.Max(0, rawScore);
    }

    public bool IsMarketSafe()
    {
        if (!_priceHistory.TryGetValue("QQQ", out var qqq) || qqq.Count < 100)
            return true;

        var shortAvg = qqq.Skip(qqq.Count - 20).Average();
        var longAvg = qqq.Skip(qqq.Count - 100).Average();

        return shortAvg >= longAvg;
    }


    public decimal GetTotalEquity()
    {
        decimal equity = _startingDayEquity + _totalRealizedPnL;
        foreach (var pos in _positions.Values)
            equity += pos.UnrealizedPnL;
        return equity;
    }
    private void UpdateRaceHistory(string symbol, double score)
    {
        var q = _raceScoreHistory.GetOrAdd(symbol, _ => new Queue<(double, DateTime)>());
        lock (q)
        {
            q.Enqueue((score, DateTime.UtcNow));
            if (q.Count > 10) q.Dequeue();
        }
    }
    // ==========================================
    // TRADE EXECUTION (FIXED)
    // ==========================================
    private decimal GetATR(string symbol, int period = 14)
    {
        if (!_priceHistory.TryGetValue(symbol, out var prices) || prices.Count < period + 1)
            return 0;

        decimal atr = 0;
        for (int i = prices.Count - period; i < prices.Count; i++)
            atr += Math.Abs(prices[i] - prices[i - 1]);

        return atr / period;
    }

    public void RegisterLiveOrder(int orderId, string symbol, TradeSide side, int qty)
    {
        _ordersById[orderId] = new TrackedOrder { OrderId = orderId, Symbol = symbol, Side = side, Qty = qty };
    }

    public void UpdateHistory(string symbol, decimal price, long volume)
    {
        // Step 1: Manage data (throttled logic)
        ManagePriceData(symbol, price, volume);

        // Step 2: Immediate checks (Stop Loss)
        CheckImmediateStops(symbol, price);
    }

    private void ManagePriceData(string symbol, decimal price, long volume)
    {
        var lastTime = _lastPriceUpdate.GetOrAdd(symbol, DateTime.MinValue);

        // FIX 1: Safety check. Ensure list exists and has elements before checking .Last()
        bool hasHistory = _priceHistory.TryGetValue(symbol, out var historyList);
        bool isFirstTick = hasHistory && historyList.Count > 0 && historyList.Last() == 0;

        // FIX 2: Use 1 second for faster updates (as you requested), or immediate if it's the first tick
        if (isFirstTick || (DateTime.UtcNow - lastTime).TotalSeconds >= 1)
        {
            var prices = _priceHistory.GetOrAdd(symbol, _ => new List<decimal>());
            var vols = _volumeHistory.GetOrAdd(symbol, _ => new List<long>());

            lock (prices)
            {
                // FIX 3: Remove the "Dummy 0" placeholder if it exists
                if (prices.Count > 0 && prices[0] == 0)
                {
                    prices.RemoveAt(0);
                    // Also remove corresponding dummy volume if it exists
                    if (vols.Count > 0) vols.RemoveAt(0);
                }

                if (price > 0) prices.Add(price);
                vols.Add(volume);

                // Keep memory manageable (rolling window of 300 data points)
                if (prices.Count > 300)
                {
                    prices.RemoveAt(0);
                    vols.RemoveAt(0);
                }
            }

            // FIX 4: CRITICAL - Update Cumulative Volume for "Learning"
            // This ensures market_memory.json actually updates at the end of the day
            _cumVolume.AddOrUpdate(symbol, volume, (key, existingVal) => existingVal + volume);

            _lastPriceUpdate[symbol] = DateTime.UtcNow;
            ExecuteTradeLogic(symbol);
        }
    }

    public void SubmitOrder(string symbol, int qty, decimal price, TradeSide side, double rsi = 0, string type = "LMT")
    {
        // FIX 8 & Reviewer logic: Block shorts
        if (side == TradeSide.Sell && !_positions.ContainsKey(symbol)) return;

        if (_pendingOrders.TryAdd(symbol, true))
        {
            RealBroker?.SubmitOrder(symbol, qty, price, side, rsi, type);
        }
    }
    public void OnOrderFilled(int orderId, int filledQty, decimal avgFillPrice)
    {
        lock (_lock)
        {
            if (!_ordersById.TryGetValue(orderId, out var order)) return;

            if (order.Side == TradeSide.Buy)
            {
                if (!_positions.TryGetValue(order.Symbol, out var pos))
                {
                    // 🔥 FIX: Initialize Trailing Stop and Position Metadata
                    _positions[order.Symbol] = new SimPosition
                    {
                        Symbol = order.Symbol,
                        Quantity = filledQty,
                        AvgPrice = avgFillPrice,
                        EntryTime = DateTime.UtcNow,
                        TrailingStop = avgFillPrice - (GetATR(order.Symbol) * 1.5m)
                    };

                    // 🔥 FIX: Increment daily counter to enforce the 3-trade limit
                    _tradesExecutedToday++;
                }
                else
                {
                    // Handle adding to an existing position
                    pos.AvgPrice = ((pos.AvgPrice * pos.Quantity) + (avgFillPrice * filledQty)) / (pos.Quantity + filledQty);
                    pos.Quantity += filledQty;
                }

                LogTradeToCSV(order.Symbol, "BUY", avgFillPrice, 0, CalculateRSI(order.Symbol), false);
                _ = SendEmailNotification($"🚀 BUY: {order.Symbol}", $"Bought {filledQty} at {avgFillPrice:C2}");
            }
            else // SELL
            {
                if (_positions.TryGetValue(order.Symbol, out var pos))
                {
                    decimal profit = (avgFillPrice - pos.AvgPrice) * filledQty;
                    _totalRealizedPnL += profit;

                    _tradeHistory.Add(new ClosedTrade { Symbol = order.Symbol, Profit = profit, ExitTime = DateTime.Now });

                    LogTradeToCSV(order.Symbol, "SELL", avgFillPrice, profit, CalculateRSI(order.Symbol), false);
                    _ = SendEmailNotification($"💰 SELL: {order.Symbol}", $"Sold at {avgFillPrice:C2}. Profit: {profit:C2}");

                    pos.Quantity -= filledQty;
                    if (pos.Quantity <= 0) _positions.Remove(order.Symbol);
                }
            }

            // Clear pending flag so the symbol is eligible for trading again later
            _pendingOrders.TryRemove(order.Symbol, out _);
        }
    }

    public void NotifyOrderFailed(int orderId, string reason)
    {
        if (_ordersById.TryRemove(orderId, out var order))
        {
            _symbolToOrderId.TryRemove(order.Symbol, out _);
            _pendingOrders.TryRemove(order.Symbol, out _);
        }
        Console.WriteLine($"[ORDER FAIL] {reason}");
        //_haltNewTrades = true;
        _softTradeUsed = false;
    }

    // ==========================================
    // DATA & CALC HELPERS (RESTORED)
    // ==========================================
    protected double CalculateRSI(string symbol, int period = 14)
    {
        if (!_priceHistory.TryGetValue(symbol, out var prices) || prices.Count <= period + 5)
            return 50;

        decimal avgGain = 0, avgLoss = 0;
        for (int i = prices.Count - period; i < prices.Count; i++)
        {
            decimal diff = prices[i] - prices[i - 1];
            if (diff > 0) avgGain += diff; else avgLoss -= diff;
        }
        if (avgLoss == 0) return 100;
        return 100 - (100 / (1 + (double)(avgGain / avgLoss)));
    }


    private string GetTrend(List<decimal> prices)
    {
       

        if (prices == null || prices.Count < 20) return "WAIT";

        int n = 15;
        var recent = prices.Skip(prices.Count - n).ToList();
        decimal slope = 0;
        for (int i = 1; i < recent.Count; i++)
            slope += recent[i] - recent[i - 1];

        slope /= n;

        if (slope > recent.Average() * 0.001m) return "BULL";
        if (slope < -recent.Average() * 0.002m) return "BEAR";
        return "FLAT";
    }

    private double TimeOfDayMultiplier()
    {
        var ny = GetEasternTime().TimeOfDay;
        if (ny < new TimeSpan(10, 15, 0)) return 1.3;
        if (ny < new TimeSpan(11, 30, 0)) return 1.0;
        if (ny < new TimeSpan(13, 30, 0)) return 0.7;
        return 0.5;
    }

    private decimal GetEntryThrottle()
    {
        decimal pct = (_startingDayEquity > 0) ? (_totalRealizedPnL / _startingDayEquity) : 0;
        if (pct > 0.007m) return 0.5m;
        if (pct > 0.009m) return 0.25m;
        if (_chopWarnings >= 2) return 0.5m;
        if (_chopWarnings >= 3) return 0.25m;
        return 1.0m;
    }

    private decimal GetVolatilityAdjustedLoss(string symbol)
    {
        if (!_priceHistory.TryGetValue(symbol, out var prices) || prices.Count < 20) return -6m;
        decimal atr = prices.Skip(prices.Count - 14).Zip(prices.Skip(prices.Count - 15), (c, p) => Math.Abs(c - p)).Average();
        return -Math.Max(6m, atr * 1.8m);
    }

    public void SyncFromIB(string symbol, int realQty, decimal avgPrice)
    {
        lock (_positions)
        {
            if (realQty == 0) _positions.Remove(symbol);
            else _positions[symbol] = new SimPosition { Symbol = symbol, Quantity = realQty, AvgPrice = avgPrice };
        }
    }
    // ==========================================
    // BOILERPLATE (Load/Save, Printing, Sync)
    // ==========================================
    public void LoadMarketMemory()
    {
        try
        {
            lock (_memoryFileLock)
            {
                if (File.Exists(MemoryFilePath))
                {
                    string json = File.ReadAllText(MemoryFilePath);
                    _learnedAvgVolume = JsonSerializer.Deserialize<Dictionary<string, decimal>>(json) ?? new Dictionary<string, decimal>();
                    Console.WriteLine($"[SYSTEM] Memory Loaded: {_learnedAvgVolume.Count} symbols.");
                }
            }
        }
        catch (Exception ex) { Console.WriteLine($"[ERROR] Load memory: {ex.Message}"); }
    }

    public void SaveMarketMemory()
    {
        if (_isSavingMemory) return;
        _isSavingMemory = true;
        try
        {
            foreach (var symbol in _tradeableStars)
            {
                if (_cumVolume.TryGetValue(symbol, out long todayVol))
                {
                    decimal todaysVolDecimal = (decimal)todayVol;
                    if (!_learnedAvgVolume.ContainsKey(symbol)) _learnedAvgVolume[symbol] = todaysVolDecimal;
                    else _learnedAvgVolume[symbol] = (_learnedAvgVolume[symbol] * 0.9m) + (todaysVolDecimal * 0.1m);
                }
            }
            string json = JsonSerializer.Serialize(_learnedAvgVolume, new JsonSerializerOptions { WriteIndented = true });
            lock (_memoryFileLock) { File.WriteAllText(MemoryFilePath, json); }
            Console.WriteLine("[SYSTEM] Market memory saved.");
        }
        catch (Exception ex) { Console.WriteLine($"[ERROR] SaveMemory: {ex.Message}"); }
        finally { _isSavingMemory = false; }
    }

    public void LoadState()
    {
        if (!File.Exists(SaveFilePath)) return;
        try
        {
            var data = JsonSerializer.Deserialize<BotPersistData>(File.ReadAllText(SaveFilePath));
            if (data == null) return;
            _totalRealizedPnL = data.RealizedPnLTotal;
            _tradesExecutedToday = data.TradesExecutedToday;
            _startingDayEquity = data.StartingDayEquity;
            _dailyLossCount = data.DailyLossCount;
            foreach (var kv in data.Positions) _positions[kv.Key] = kv.Value;
            foreach (var kv in data.PriceHistory) _priceHistory[kv.Key] = kv.Value;
            foreach (var kv in data.VolumeHistory) _volumeHistory[kv.Key] = kv.Value;
            foreach (var kv in data.LastSellTimes) _lastSellTimes[kv.Key] = kv.Value;
            foreach (var kv in data.BuyTimes) _buyTimes[kv.Key] = kv.Value;
        }
        catch { }
    }
    public void ExecuteTradeLogic(string symbol)
    {
        // 1. Safety Filters
        double regime = RegimeBias();

        if (_haltNewTrades || symbol == "QQQ") return;
        if (!_priceHistory.ContainsKey(symbol) || _priceHistory[symbol].Count < 8) return;

        var nyNow = GetEasternTime();
        if (nyNow.TimeOfDay < new TimeSpan(9, 35, 0)) return;

        if (nyNow.TimeOfDay > new TimeSpan(15, 30, 0)) return;

        // 2. Momentum Calculation
        double currentScore = CalculateRaceScore(symbol) * regime;

        _raceScores[symbol] = currentScore;

        // 🔥 FIX: Populate history so GetRaceSlope has data to calculate
        UpdateRaceHistory(symbol, currentScore);
        double slope = GetRaceSlope(symbol);

        bool hasPosition = _positions.ContainsKey(symbol);

        // 3. ENTRY LOGIC
        // Check if we already have a position or an order in-flight for this symbol
        if (!hasPosition && !_pendingOrders.ContainsKey(symbol))
        {
            if (_positions.Count < 3 && _tradesExecutedToday < 10 && !_goalReached)
            {
                if (GetTrend(_priceHistory[symbol]) != "BEAR"
     && slope > 0.015        // was 0.05
     && currentScore > 2.0) // was 4

                {
                    decimal currentPrice = _priceHistory[symbol].Last();
                    int qty = (int)(3000m / currentPrice);

                    if (_lastSellTimes.TryGetValue(symbol, out var lastSell))
                        if ((DateTime.UtcNow - lastSell).TotalSeconds < 60) return;

                    if (qty > 0)
                        SubmitOrder(symbol, qty, currentPrice, TradeSide.Buy, CalculateRSI(symbol));
                }

            }
        }
        // 4. EXIT LOGIC
        else if (hasPosition)
        {
            var pos = _positions[symbol];
            pos.CurrentPrice = _priceHistory[symbol].Last();

            // 1. Hard Stop Loss (MUST be checked before any timers)
            decimal atr = GetATR(symbol);

            // 2. Minimum Holding Time (Now only applies to "Smart" exits)
            double secondsHeld = (DateTime.UtcNow - pos.EntryTime).TotalSeconds;
            if (secondsHeld < 15) return;

            // 3. Dynamic Exit (RSI Hook or Trailing Stop)
            double rsi = CalculateRSI(symbol);
            decimal gainPct = (pos.CurrentPrice - pos.AvgPrice) / pos.AvgPrice;


            // --- EXIT LOGIC FIX ---
            if (gainPct <= -0.03m || gainPct >= 0.012m)
            {
                SubmitOrder(symbol, pos.Quantity, pos.CurrentPrice, TradeSide.Sell, rsi, "MKT");
                _lastSellTimes[symbol] = DateTime.UtcNow;
                return;
            }


        }
    }
    public void SaveState()
    {
        try
        {
            var data = new BotPersistData
            {
                Positions = _positions,
                TradesExecutedToday = _tradesExecutedToday,
                DailyLossCount = _dailyLossCount,
                RealizedPnLTotal = _totalRealizedPnL,
                StartingDayEquity = _startingDayEquity,
                TradesPerSymbol = _tradesToday,
                BuyTimes = _buyTimes,
                PriceHistory = _priceHistory,
                VolumeHistory = _volumeHistory,
                LastSellTimes = _lastSellTimes,
                LastTradeWasLoss = _lastTradeWasLoss
            };
            string json = JsonSerializer.Serialize(data, new JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(SaveFilePath, json);
        }
        catch (Exception ex) { Console.WriteLine($"[ERROR] Save state: {ex.Message}"); }
    }

    public void ProcessHistoricalBar(string symbol, decimal close, long volume)
    {
        lock (_lock)
        {
            if (!_priceHistory.ContainsKey(symbol))
            {
                _priceHistory[symbol] = new List<decimal>();
                _volumeHistory[symbol] = new List<long>();
            }
            _priceHistory[symbol].Add(close);
            _volumeHistory[symbol].Add(volume);
            if (_priceHistory[symbol].Count > 300)
            {
                _priceHistory[symbol].RemoveAt(0);
                _volumeHistory[symbol].RemoveAt(0);
            }
        }
    }

    public void CheckDailyGoal()
    {
        lock (_lock)
        {
            if (_goalReached || _haltNewTrades) return;
            decimal currentEquity = GetTotalEquity();
            decimal profitPercent = (currentEquity - _startingDayEquity) / _startingDayEquity;

            if (profitPercent >= dailyProfitGoalPct)
            {
                _goalReached = true;
                _haltNewTrades = true;
                LiquidateAll();
            }
            else if (profitPercent <= (dailyLossLimitPct * -1))
            {
                _haltNewTrades = true;
                LiquidateAll();
            }
        }
    }

    private void LiquidateAll()
    {
        if (_positions.Count == 0 || _emergencyExit) return;
        _emergencyExit = true;

        foreach (var kv in _positions.ToList())
        {
            var symbol = kv.Key;
            var pos = kv.Value;
            _pendingOrders.TryRemove(symbol, out _);

            // Int Cast
            int orderId = ((IbClient)RealBroker).SubmitEmergencyMarketSell(symbol, pos.Quantity);
            RegisterLiveOrder(orderId, symbol, TradeSide.Sell, pos.Quantity);
        }
    }


    public void CheckImmediateStops(string symbol, decimal currentPrice)
    {
        lock (_lock)
        {
            if (_positions.TryGetValue(symbol, out var pos))
            {
                pos.CurrentPrice = currentPrice;
                decimal atr = GetATR(symbol);

                // Instant Hard Stop - No 5-second waiting
                if (currentPrice < pos.AvgPrice - (atr * 1.5m))
                {
                    Console.WriteLine($"[EMERGENCY] ATR Stop Hit for {symbol} at {currentPrice}");
                    SubmitOrder(symbol, pos.Quantity, currentPrice, TradeSide.Sell, 0, "MKT");
                }
            }
        }
    }
    public void PrintStatusTable()
    {
        var sb = new System.Text.StringBuilder();
        int w = 110;

        lock (_lock)
        {
            sb.AppendLine(new string('=', w));
            sb.AppendLine($" BOT: {(_haltNewTrades ? "HALTED" : "ACTIVE")} | PnL: {_totalRealizedPnL:C2} | TRADES: {_tradesExecutedToday}/10 | POS: {_positions.Count}".PadRight(w));
            sb.AppendLine(new string('-', w));
            sb.AppendLine($"{"SYMBOL",-8} | {"PRICE",-10} | {"RSI",-6} | {"TREND",-6} | {"POSITION",-15} | {"PnL $",-10}");
            sb.AppendLine(new string('-', w));

            foreach (var symbol in _tradeableStars)

            {
                // FIX: Don't skip row if data is missing, just show "Loading"
                if (!_priceHistory.TryGetValue(symbol, out var prices) || prices.Count == 0)
                {
                    sb.AppendLine($"{symbol,-8} | {"WAITING...",-10} | {"-",-6} | {"-",-6} | {"-",-15} | {"-",-10}");
                    continue;
                }

                decimal last = prices.Last();
                if (last == 0) // Placeholder data
                {
                    sb.AppendLine($"{symbol,-8} | {"LOADING...",-10} | {"-",-6} | {"-",-6} | {"-",-15} | {"-",-10}");
                    continue;
                }

                double rsi = CalculateRSI(symbol);
                string trend = GetTrend(prices);

                string posStr = "---";
                string pnlStr = "---";
                if (_positions.TryGetValue(symbol, out var p))
                {
                    posStr = $"{p.Quantity}@{p.AvgPrice:F0}";
                    pnlStr = ((last - p.AvgPrice) * p.Quantity).ToString("C2");
                }

                sb.AppendLine($"{symbol,-8} | {last,-10:C2} | {rsi,-6:F0} | {trend,-6} | {posStr,-15} | {pnlStr,-10}");
            }
        }
        Console.SetCursorPosition(0, 0);
        Console.Write(sb.ToString());
    }
    public void PreInitializeSymbols(List<string> symbols)
    {
        foreach (var s in symbols)
        {
            if (!_priceHistory.ContainsKey(s))
                _priceHistory.TryAdd(s, new List<decimal>()); // NO ZERO
        }
    }

    private DateTime GetEasternTime()
    {
        string tzId = RuntimeInformation.IsOSPlatform(OSPlatform.Windows) ? "Eastern Standard Time" : "America/New_York";
        return TimeZoneInfo.ConvertTimeFromUtc(DateTime.UtcNow, TimeZoneInfo.FindSystemTimeZoneById(tzId));
    }

    public void ResetPositionSync() { _syncedSymbols.Clear(); }

    public void SyncExistingPosition(string symbol, int qty, decimal avgPrice)
    {
        if (_ignoreSyncUntil.TryGetValue(symbol, out var until) && DateTime.UtcNow < until) return;
        _syncedSymbols.Add(symbol);

        if (qty == 0) { _positions.Remove(symbol); return; }

        if (_positions.TryGetValue(symbol, out var pos))
        {
            pos.Quantity = qty;
            pos.AvgPrice = avgPrice;
        }
        else
        {
            _positions[symbol] = new SimPosition
            {
                Symbol = symbol,
                Quantity = qty,
                AvgPrice = avgPrice,
                EntryTime = DateTime.UtcNow
            };
        }
    }

    public void FinalizePositionSync()
    {
        var toRemove = _positions.Keys.Where(k => !_syncedSymbols.Contains(k)).ToList();
        foreach (var sym in toRemove) _positions.Remove(sym);
    }

    public void CheckEndOfDayLiquidation()
    {
        var nyNow = GetEasternTime();
        if (nyNow.TimeOfDay > new TimeSpan(15, 45, 0) && nyNow.TimeOfDay < new TimeSpan(16, 0, 0))
        {
            if (_positions.Count > 0) LiquidateAll();
        }
        if (nyNow.TimeOfDay >= new TimeSpan(16, 0, 0) && !_hasSavedToday)
        {
            SaveMarketMemory();
            _hasSavedToday = true;
        }
    }

    public async Task SendEmailNotification(string subject, string messageBody)
    {
        try
        {
            ServicePointManager.SecurityProtocol = SecurityProtocolType.Tls12;
            var fromAddress = new MailAddress("uygargunay@gmail.com", "TradeBot Live");
            var toAddress = new MailAddress("uygargunay@gmail.com");
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
                await smtp.SendMailAsync(message); // Use Async method
            }
            Console.WriteLine($"[EMAIL] Sent: {subject}");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[EMAIL ERROR] {ex.Message}");
        }
    }


    public void LogTradeToCSV(string symbol, string side, decimal price, decimal pnl, double rsi, bool surge)
    {
        try
        {
            using (StreamWriter sw = new StreamWriter("trade_log.csv", true))
            {
                sw.WriteLine($"{DateTime.Now},{symbol},{side},{price},{pnl},{rsi},{surge}");
            }
        }
        catch { }
    }

    public void SendEodSummary()
    {
        lock (_lock)
        {
            int totalTrades = _tradeHistory.Count;
            int wins = _tradeHistory.Count(t => t.Profit > 0);
            int losses = _tradeHistory.Count(t => t.Profit <= 0);
            decimal winRate = totalTrades > 0 ? (decimal)wins / totalTrades * 100 : 0;

            // REPLACE your current PnL calculation with this:
            decimal reportPnL = _tradeHistory.Sum(t => t.Profit);

            string report = $"--- DAILY PERFORMANCE REPORT ---\n\n" +
                            $"Final PnL: {reportPnL:C2}\n" + // Use the calculated value
                            $"Total Trades: {totalTrades}\n" +
                            $"Wins: {wins} | Losses: {losses}\n" +
                            $"Win Rate: {winRate:F1}%\n" +
                            $"Daily Loss Count: {_dailyLossCount}/4\n\n" +
                            "--- TRADE DETAILS ---\n";

            foreach (var t in _tradeHistory)
            {
                report += $"[{t.ExitTime:HH:mm}] {t.Symbol}: {t.Profit:C2}\n";
            }
            var emailTask = SendEmailNotification($"📊 EOD REPORT: {reportPnL:C2}", report);
            emailTask.Wait(); // Force wait so program doesn't exit before sending

        }

    }
}