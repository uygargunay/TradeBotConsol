using IBApi;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Mail;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;


public interface IBroker
{
    void SubmitOrder(string symbol, int qty, decimal price, TradeSide side, double currentRsi = 0, string orderType = "LMT");
    bool TryDequeueIbLog(out string log);


}

public enum TradeSide { Buy, Sell }
public enum MarketRegime { Bullish, Neutral, Bearish }
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
    public decimal Quantity { get; set; }
    public decimal AvgPrice { get; set; }
    public decimal TrailingStop { get; set; }
    public decimal CurrentPrice { get; set; }
    public decimal RealizedPnL { get; set; }
    public decimal UnrealizedPnL => Quantity * (CurrentPrice - AvgPrice);
    public bool IsBreakEvenProtected { get; set; }
    public DateTime EntryTime { get; set; } = DateTime.UtcNow; // MODERATE: Track entry for hold-timer
}

public class BotPersistData
{
    public Dictionary<string, SimPosition> Positions { get; set; } = new();
    public int TradesExecutedToday { get; set; }
    public int DailyLossCount { get; set; } // ADDED THIS
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
    public int Qty;            // total requested
    public int FilledQty;      // cumulative filled
    public DateTime Time;
    public OrderLifeState State;
}

public class PositionManager : SimulatedBroker { }

public class SimulatedBroker : IBroker
{
    public IBroker RealBroker { get; set; }
    // 🔴 ORDER FAILURE CALLBACK (from IB client)
    public Action<int> OnOrderFailed;
    private const double RSI_HOOK_DROP = 5.0;  // Require 3.0 point drop from peak
    private const decimal dailyProfitGoalPct = 0.01m; // +1% Target
    private const decimal dailyLossLimitPct = 0.03m;   // -3.0% Stop
    private const int MaxActivePositions = 2;          // Focus on 4 slots
    private const decimal initialAccountValue = 4000m;
    private const decimal roundTripFee = 2.00m;        // Matches $1 buy + $1 sell
    private HashSet<string> _addedToWinners = new();
    public bool IsHalted => _haltNewTrades;
    public bool GoalReached => _goalReached;
    public int FilledQty;
    private bool _softTradeUsed = false;
    private readonly ConcurrentQueue<string> _ibLogs = new();

    // Inside SimulatedBroker Class - Added Variable
    protected int _dailyLossCount = 0;
    private ConcurrentDictionary<string, Queue<(double score, DateTime time)>> _raceScoreHistory = new();

    public readonly string[] _tradeableStars =
    {
    // Institutional momentum
    "NVDA", "AMD", "META", "AMZN", "GOOGL", "MSFT",

    // High beta leaders
    "TSLA", "SMCI", "ARM", "COIN", "MSTR",

    // Clean technicals
    "AVGO", "PANW", "CRWD", "NOW", "ADBE",

    // Intraday runners
    "NFLX", "PLTR", "SNOW", "SHOP", "UBER",
    //semiconductors
    "MU","SOXL",
    //crypto
    "MARA","RIOT",
    //china
    "BABA","PDD",
    //financial
    "JPM","GS",
    // Market regime
    "QQQ"
};

    public ConcurrentDictionary<string, List<decimal>> _priceHistory { get; set; } = new();
    public ConcurrentDictionary<string, List<long>> _volumeHistory { get; set; } = new();
    private ConcurrentDictionary<string, double> _lastRsiMemory = new ConcurrentDictionary<string, double>();
    private ConcurrentDictionary<string, long> _cumVolume = new();
    private ConcurrentDictionary<string, decimal> _cumVwapProd = new();
    private ConcurrentDictionary<string, DateTime> _symbolLastResetDate = new();
    protected readonly Dictionary<string, SimPosition> _positions = new();
    private ConcurrentDictionary<int, TrackedOrder> _ordersById = new();
    private ConcurrentDictionary<string, int> _symbolToOrderId = new();
    protected readonly Dictionary<string, int> _tradesToday = new();
    protected readonly Dictionary<string, DateTime> _lastSellTimes = new();
    private readonly ConcurrentDictionary<string, DateTime> _buyTimes = new ConcurrentDictionary<string, DateTime>();
    private readonly DateTime _botStartTime = DateTime.UtcNow;
    private readonly ConcurrentDictionary<string, bool> _pendingOrders = new();
    private ConcurrentDictionary<string, DateTime> _ignoreSyncUntil = new();
    private ConcurrentDictionary<string, DateTime> _lastPriceAdd = new();

    private decimal _rollingPnL = 0m;
    private int _rollingTrades = 0;


    private const string SaveFilePath = "bot_state.json";
    protected readonly object _lock = new object();
    private decimal _startingDayEquity = initialAccountValue;
    private decimal _totalRealizedPnL = 0m;
    private int _tradesExecutedToday = 0;
    private bool _haltNewTrades = false;
    private bool _goalReached = false;
    private volatile bool _killSwitchEngaged = false;
    private volatile bool _emergencyExit = false;
    private Dictionary<string, decimal> _learnedAvgVolume = new();
    private const string MemoryFilePath = "market_memory.json";
    private ConcurrentDictionary<string, double> _raceScores = new();
    private ConcurrentDictionary<string, DateTime> _raceStartTimes = new();
    private const double RACE_ENTRY_SCORE = 6.5;   // enter only real leaders
    private const double RACE_STRONG_SCORE = 7.8;  // scale / conviction
    private const double RACE_EXIT_SCORE = 4.5;    // momentum death
    private int _chopWarnings = 0;
    protected readonly List<ClosedTrade> _tradeHistory = new();
    private bool _hasSavedToday = false;
    private bool _eodEmailSent = false;
    private ConcurrentDictionary<string, double> _lastRaceScores = new();
    private static readonly object _memoryFileLock = new object();
    private volatile bool _isSavingMemory = false;
    private static readonly TimeSpan ObservationEnd = new TimeSpan(10, 30, 0);
    // Ensure all these have '= new ...'
    private HashSet<string> _syncedSymbols = new HashSet<string>();
    private ConcurrentDictionary<string, double> _peakRsi = new ConcurrentDictionary<string, double>();
    private Dictionary<string, bool> _lastTradeWasLoss = new Dictionary<string, bool>();
    private int GetPersistenceSeconds(double score)
    {
        if (score >= 8.5) return 15;
        if (score >= 7.8) return 25;
        return 35;
    }
    public bool TryDequeueIbLog(out string log)
    {
        return _ibLogs.TryDequeue(out log);
    }

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
            if (o.State == OrderLifeState.Submitted &&
              (DateTime.UtcNow - o.Time).TotalSeconds > 60)
            {
                TriggerKillSwitch($"Order timeout: {o.Symbol}");

                // optional: cancel & retry instead of kill
            }
            if (o.State == OrderLifeState.PartiallyFilled &&
    (DateTime.UtcNow - o.Time).TotalSeconds > 60)
            {
                _pendingOrders.TryRemove(o.Symbol, out _);
                _ordersById.TryRemove(o.OrderId, out _);
            }
            if (_pendingOrders.ContainsKey(o.Symbol) &&
    (DateTime.UtcNow - o.Time).TotalSeconds > 90)
            {
                _pendingOrders.TryRemove(o.Symbol, out _);
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

        decimal priceROC =
            (prices.Last() - prices[^6]) / prices[^6] * 100m;

        decimal avgVol =
            (decimal)volumes.Skip(volumes.Count - 10).Average();

        decimal volExpansion =
            avgVol > 0 ? volumes.Last() / avgVol : 0;

        decimal range =
            prices.TakeLast(10).Max() - prices.TakeLast(10).Min();

        if (range <= 0) return 0;

        double rawScore =
            (double)(priceROC * 10m) +
            (double)(volExpansion * 2m);
        range = Math.Max(range, prices.Last() * 0.002m);

        return Math.Max(0, rawScore / (double)range);
    }

    // --- MARKET SAFETY ---
    public bool IsMarketSafe()
    {
        if (!_priceHistory.ContainsKey("QQQ") || _priceHistory["QQQ"].Count < 20) return false;
        string qqqTrend = GetTrend(_priceHistory["QQQ"]);
        return qqqTrend != "BEAR";
    }

    public decimal GetTotalEquity()
    {
        decimal equity = _startingDayEquity + _totalRealizedPnL;

        foreach (var pos in _positions.Values)
            equity += pos.UnrealizedPnL;

        return equity;
    }

    public void LoadMarketMemory()
    {
        try
        {
            lock (_memoryFileLock)
            {
                if (File.Exists(MemoryFilePath))
                {
                    string json = File.ReadAllText(MemoryFilePath);
                    _learnedAvgVolume = JsonSerializer.Deserialize<Dictionary<string, decimal>>(json)
                                        ?? new Dictionary<string, decimal>();
                    Console.WriteLine($"[SYSTEM] Memory Loaded: {_learnedAvgVolume.Count} symbols.");
                }
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[ERROR] Could not load memory: {ex.Message}");
        }
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

                    if (!_learnedAvgVolume.ContainsKey(symbol))
                        _learnedAvgVolume[symbol] = todaysVolDecimal;
                    else
                        _learnedAvgVolume[symbol] =
                            (_learnedAvgVolume[symbol] * 0.9m) + (todaysVolDecimal * 0.1m);
                }
            }

            string json = JsonSerializer.Serialize(_learnedAvgVolume,
                new JsonSerializerOptions { WriteIndented = true });

            lock (_memoryFileLock)
            {
                var temp = MemoryFilePath + ".tmp";
                File.WriteAllText(temp, json);
                File.Copy(temp, MemoryFilePath, true);
                File.Delete(temp);
            }

            Console.WriteLine("[SYSTEM] Market memory saved to disk.");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[ERROR] SaveMarketMemory failed: {ex.Message}");
        }
        finally
        {
            _isSavingMemory = false;
        }
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
            _dailyLossCount = data.DailyLossCount; // ADD THIS LINE

            foreach (var kv in data.Positions) _positions[kv.Key] = kv.Value;
            foreach (var kv in data.PriceHistory) _priceHistory[kv.Key] = kv.Value;
            foreach (var kv in data.VolumeHistory) _volumeHistory[kv.Key] = kv.Value;
            foreach (var kv in data.LastSellTimes) _lastSellTimes[kv.Key] = kv.Value;
            foreach (var kv in data.BuyTimes) _buyTimes[kv.Key] = kv.Value;
        }
        catch { }
    }
    // Ensure these are at the top of your SimulatedBroker class
    public void ExecuteTradeLogic(string symbol)
    {
        if (!_priceHistory.ContainsKey(symbol)) return;
        var nyNow = GetEasternTime();

        if (nyNow.TimeOfDay < ObservationEnd) return;

        // 1. GLOBAL SAFETY FILTERS
        if (_tradesExecutedToday >= 3) return;
        if (_killSwitchEngaged || symbol == "QQQ" || _haltNewTrades || _goalReached) return;
        if (_dailyLossCount >= 4 || (_softTradeUsed && _positions.Count == 0)) return;
        if (_pendingOrders.ContainsKey(symbol)) return;

        var topLeaders = _raceScores.OrderByDescending(x => x.Value).Take(3).Select(x => x.Key).ToHashSet();
        if (!topLeaders.Contains(symbol)) return;

        // YOUR STARTUP SHIELD
        if ((DateTime.UtcNow - _botStartTime).TotalMinutes < 1.0)
        {
            _lastRsiMemory[symbol] = CalculateRSI(symbol, 14);
            return;
        }

        double currentScore = _raceScores.TryGetValue(symbol, out var sc) ? sc : 0;
        bool hasPosition = _positions.ContainsKey(symbol);

        // ==========================================
        // ENTRY LOGIC (RESTORED YOUR FULL LOGIC)
        // ==========================================
        if (!hasPosition && _positions.Count < MaxActivePositions)
        {
            if (_tradesToday.TryGetValue(symbol, out int tradeCount) && tradeCount >= 2) return;
            if (!IsMarketSafe()) return;

            bool hardPass = currentScore >= 7.0;
            string symTrend = GetTrend(_priceHistory[symbol]);
            string marketTrend = GetTrend(_priceHistory["QQQ"]);

            bool softMode = !_softTradeUsed && IsMarketSafe() &&
                            (symTrend == "BULL" || (symTrend == "FLAT" && marketTrend == "FLAT")) &&
                            currentScore >= 6.0 && GetRaceSlope(symbol) > 0.008;

            if (!hardPass && !softMode) return;

            decimal currentPrice = _priceHistory[symbol].Last();
            double rsi = CalculateRSI(symbol, 14);
            double prevRsi = _lastRsiMemory.ContainsKey(symbol) ? _lastRsiMemory[symbol] : rsi;
            _lastRsiMemory[symbol] = rsi;

            bool rsiCross = rsi > 50 && (rsi - prevRsi) > 2.5;

            decimal vwap = (_cumVolume.TryGetValue(symbol, out var vol) && vol > 0) ? _cumVwapProd[symbol] / vol : currentPrice;

            if (GetTrend(_priceHistory[symbol]) != "BEAR" && rsiCross && currentPrice > vwap)
            {
                double slope = GetRaceSlope(symbol);
                double slopeGate = nyNow.TimeOfDay < new TimeSpan(11, 30, 0) ? 0.02 : 0.035;
                if (!softMode && slope < slopeGate) return;
                decimal throttle = GetEntryThrottle();
                decimal slotBudget = (_startingDayEquity / MaxActivePositions) * throttle;
                decimal atr = GetATR(symbol);
                decimal riskPerShare = Math.Max(atr, currentPrice * 0.006m);
                int qty = (int)Math.Floor(slotBudget / riskPerShare);


                if (qty <= 0) return;

                _buyTimes[symbol] = DateTime.UtcNow;
                if (GetATR(symbol) > currentPrice * 0.01m) return;

                SubmitOrder(symbol, qty, currentPrice, TradeSide.Buy, rsi);
                if (softMode) _softTradeUsed = true;
                SaveState();
            }
        }
        // ==========================================
        // EXIT LOGIC (YOUR LOGIC + SAFETY TIMERS)
        // ==========================================
        else if (hasPosition)
        {
            var pos = _positions[symbol];
            pos.CurrentPrice = _priceHistory[symbol].Last();

            DateTime entryTime = _buyTimes.TryGetValue(symbol, out var bt) ? bt : pos.EntryTime;
            double secondsHeld = (DateTime.UtcNow - entryTime).TotalSeconds;

            // 🛑 STOP: If we haven't held for 15 seconds, don't even look at the indicators.
            if (secondsHeld < 15) return;

            // 1. Logic Variables
            bool minHoldMet = secondsHeld >= 180;
            decimal pnlPct = (pos.CurrentPrice - pos.AvgPrice) / pos.AvgPrice;

            // 2. HARD STOP (Absolute Safety - no timer required)
            decimal atr = GetATR(symbol);
            decimal loss = pos.CurrentPrice - pos.AvgPrice;
            if (atr > 0 && loss <= -atr)
            {
                SubmitOrder(symbol, (int)pos.Quantity, pos.CurrentPrice, TradeSide.Sell);
                SaveState();
                return; // Exit the method immediately
            }

            // 3. ALL OTHER EXITS (Locked behind the 3-minute wall)
            if (minHoldMet)
            {
                decimal atrTrail = atr * 1.4m;
                decimal newStop = pos.CurrentPrice - atrTrail;

                if (newStop > pos.TrailingStop)
                    pos.TrailingStop = Math.Max(pos.TrailingStop, pos.CurrentPrice - atrTrail);
                bool hitTrailingStop = pos.CurrentPrice <= pos.TrailingStop;
                bool rsiHook = (_peakRsi.ContainsKey(symbol) && (_peakRsi[symbol] - CalculateRSI(symbol, 14)) >= 4.0);

                bool shouldExit = false;
                if (hitTrailingStop) shouldExit = true;
                if (atr > 0 && (pos.CurrentPrice - pos.AvgPrice) >= atr * 1.2m && rsiHook)
                    shouldExit = true;


                if (shouldExit)
                {
                    SubmitOrder(symbol, (int)pos.Quantity, pos.CurrentPrice, TradeSide.Sell);
                    SaveState();
                }
            }
        }
    }

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
        _ordersById.TryAdd(orderId, new TrackedOrder
        {
            OrderId = orderId,
            Symbol = symbol,
            Side = side,
            Qty = qty,
            Time = DateTime.UtcNow,
            State = OrderLifeState.Submitted
        });
        _symbolToOrderId[symbol] = orderId;

    }

    public void UpdateHistory(string symbol, decimal price, long volume)
    {
        ManagePriceData(symbol, price, volume);
        double rsi = CalculateRSI(symbol, 14);
        _peakRsi.AddOrUpdate(symbol, rsi, (_, old) => Math.Max(old, rsi));
        // 🔥 score FIRST
        double raw = CalculateRaceScore(symbol) * TimeOfDayMultiplier() * RegimeBias();

        double smooth = raw;
        if (_lastRaceScores.TryGetValue(symbol, out var prev))
            smooth = (raw * 0.7) + (prev * 0.3);

        _raceScores[symbol] = smooth;

        var history = _raceScoreHistory.GetOrAdd(symbol, _ => new Queue<(double, DateTime)>());
        lock (history)
        {
            history.Enqueue((smooth, DateTime.UtcNow));
            if (history.Count > 8) history.Dequeue();
        }

        _lastRaceScores[symbol] = smooth;


        lock (_lock)
        {
            ExecuteTradeLogic(symbol);
        }
        if (_positions.Count > 0)
        {
            CheckDailyGoal();
            CheckEndOfDayLiquidation();
        }

        CheckForStuckOrders();


    }

    public void SubmitOrder(
        string symbol,
        int qty,
        decimal price,
        TradeSide side,
        double currentRsi = 0,
        string orderType = "LMT")
    {
        if (_killSwitchEngaged && side == TradeSide.Buy)
            return;

        if (!_pendingOrders.TryAdd(symbol, true))
            return;

        qty = Math.Max(1, qty);

        // Only submit to broker
        RealBroker?.SubmitOrder(symbol, qty, price, side, currentRsi, orderType);

        // OPTIONAL: debug email only
        /*
        Task.Run(() =>
            SendEmailNotification(
                $"ORDER SENT ({side}): {symbol}",
                $"Qty: {qty}\nPrice: {price:C2}\nRSI: {currentRsi}"
            )
        );
        */
    }


    public void OnOrderFilled(int orderId, int filledQty, decimal avgFillPrice)
    {
        if (!_ordersById.TryGetValue(orderId, out var order)) return;

        order.FilledQty += filledQty;
       

        _ordersById.TryRemove(orderId, out _);
        _symbolToOrderId.TryRemove(order.Symbol, out _);
        _pendingOrders.TryRemove(order.Symbol, out _);
        Task.Run(() =>
      SendEmailNotification(
          $"{(order.Side == TradeSide.Buy ? "🟢 BUY FILLED" : "🔴 SELL FILLED")}: {order.Symbol}",
          $"Qty: {filledQty}\nPrice: {avgFillPrice:C2}"
      )
  );
        lock (_lock)
        {
            if (order.Side == TradeSide.Buy)
            {
                if (_positions.TryGetValue(order.Symbol, out var existing))
                {
                    decimal totalQty = existing.Quantity + order.FilledQty;
                    existing.AvgPrice = ((existing.AvgPrice * existing.Quantity) + (avgFillPrice * order.FilledQty)) / totalQty;
                    existing.Quantity = totalQty;
                }
                // FIXED OnOrderFilled logic
                else
                {
                    // Pull the original buy time if available, otherwise default to now
                    DateTime originalEntry = _buyTimes.TryGetValue(order.Symbol, out var t) ? t : DateTime.UtcNow;
                    _buyTimes.TryRemove(order.Symbol, out _);
                    decimal atr = GetATR(order.Symbol);
                    

                    _positions[order.Symbol] = new SimPosition
                    {
                        Symbol = order.Symbol,
                        Quantity = order.FilledQty,
                        AvgPrice = avgFillPrice,
                        CurrentPrice = avgFillPrice,
                        EntryTime = originalEntry, // ✅ USE ORIGINAL TIME
                        TrailingStop = avgFillPrice - Math.Max(atr * 1.4m, avgFillPrice * 0.006m)

                    };
                }
            }
            else // SELL FILL
            {
                if (_positions.TryGetValue(order.Symbol, out var pos))
                {
                    decimal pnl = (avgFillPrice - pos.AvgPrice) * order.FilledQty - roundTripFee;
                    _rollingPnL += pnl;
                    _rollingTrades++;

                    if (_rollingTrades >= 10)
                    {
                        if ((_rollingPnL / _rollingTrades) < -30)
                            TriggerKillSwitch("Negative expectancy detected");
                        _rollingPnL = 0;
                        _rollingTrades = 0;
                    }

                    _totalRealizedPnL += pnl;
                    _tradesExecutedToday++;
                    _lastTradeWasLoss[order.Symbol] = pnl < 0;

                    // ✅ FIX: Explicitly remove from local memory to prevent "Ghosting"
                    _positions.Remove(order.Symbol);
                    _ignoreSyncUntil[order.Symbol] = DateTime.UtcNow.AddSeconds(5);
                    _syncedSymbols.Remove(order.Symbol);

                    _lastSellTimes[order.Symbol] = DateTime.UtcNow;
                    _tradeHistory.Add(new ClosedTrade { Symbol = order.Symbol, Profit = pnl, ExitTime = DateTime.UtcNow });

                    // ✅ FIX: Log the trade to CSV for your records
                    double rsi = _lastRsiMemory.ContainsKey(order.Symbol) ? _lastRsiMemory[order.Symbol] : 50;
                    LogTradeToCSV(order.Symbol, "SELL", avgFillPrice, pnl, rsi, false);

                    if (pnl < 0)
                        _dailyLossCount += pnl <= GetVolatilityAdjustedLoss(order.Symbol) ? 2 : 1;

                    if (_dailyLossCount >= 4) TriggerKillSwitch("Max daily losses reached");

                    if (!_tradesToday.ContainsKey(order.Symbol))
                        _tradesToday[order.Symbol] = 0;
                    _tradesToday[order.Symbol]++;
                    _buyTimes.TryRemove(order.Symbol, out _);
                    _peakRsi.TryRemove(order.Symbol, out _);
                    _softTradeUsed = false;
                    SaveState();
                }
            }
        }
        SaveState(); 
    }
    public void OnBrokerPosition(string symbol, decimal qty, decimal avgPrice)
    {
        SyncExistingPosition(symbol, qty, avgPrice);
    }

    public void NotifyOrderFailed(int orderId, string reason)
    {
        if (_ordersById.TryRemove(orderId, out var order))
        {
            _symbolToOrderId.TryRemove(order.Symbol, out _);
            _pendingOrders.TryRemove(order.Symbol, out _);
        }

        Console.WriteLine($"[ORDER FAIL] {reason}");
        _haltNewTrades = true;   // pause
        _softTradeUsed = false;

    }


    public void ProcessHistoricalBar(string symbol, decimal close, long volume) // Ensure volume is long
    {
        lock (_lock)
        {
            if (!_priceHistory.ContainsKey(symbol))
            {
                _priceHistory[symbol] = new List<decimal>();

                // FIX: Initialize as List<long> to match your Dictionary<string, List<long>>
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

            // FIX: If we already reached a goal or halted, stop immediately
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
                // FIX: Set halt to true BEFORE liquidating to prevent the loop
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

            int orderId = ((IbClient)RealBroker)
                .SubmitEmergencyMarketSell(symbol, (int)pos.Quantity);

            RegisterLiveOrder(orderId, symbol, TradeSide.Sell, (int)pos.Quantity);
        }
    }



    private void ManagePriceData(string symbol, decimal price, long volume)
    {
        DateTime nyNow = GetEasternTime();
        bool isMarketHours =
            nyNow.TimeOfDay >= new TimeSpan(9, 30, 0) &&
            nyNow.TimeOfDay < new TimeSpan(16, 0, 0);

        var prices = _priceHistory.GetOrAdd(symbol, _ => new List<decimal>());
        var volumes = _volumeHistory.GetOrAdd(symbol, _ => new List<long>());

        lock (prices)
        {
            var lastTime = _lastPriceAdd.GetOrAdd(symbol, DateTime.MinValue);
            if ((DateTime.UtcNow - lastTime).TotalSeconds >= 5)
            {
                prices.Add(price);
                volumes.Add(volume);
                _lastPriceAdd[symbol] = DateTime.UtcNow;

                if (prices.Count > 300) { prices.RemoveAt(0); volumes.RemoveAt(0); }
            }
        }

        // Daily reset
        DateTime lastReset = _symbolLastResetDate.GetOrAdd(symbol, DateTime.MinValue);
        if (nyNow.TimeOfDay >= new TimeSpan(9, 30, 0) && lastReset.Date != nyNow.Date)
        {
            _cumVolume[symbol] = 0;
            _cumVwapProd[symbol] = 0;
            _symbolLastResetDate[symbol] = nyNow.Date;
        }

        if (isMarketHours)
        {
            _cumVolume.AddOrUpdate(symbol, volume, (_, old) => old + volume);
            _cumVwapProd.AddOrUpdate(symbol, price * volume, (_, old) => old + price * volume);
        }
    }


    public void PrintStatusTable()
    {
        var sb = new System.Text.StringBuilder();
        List<string> symbolsToPrint;
        string marketStatus;
        decimal pnlPct;
        int windowWidth = Console.WindowWidth > 0 ? Console.WindowWidth : 125;
        Dictionary<string, double> raceSnapshot;
        Dictionary<string, SimPosition> posSnapshot;
        Dictionary<string, List<decimal>> priceSnap;
        Dictionary<string, List<long>> volSnap;
        Dictionary<string, DateTime> lastSellSnap;
        Dictionary<string, bool> lastLossSnap;
        Dictionary<string, decimal> learnedSnap;
        Dictionary<string, long> cumVolSnap;
        lock (_lock)
        {
            raceSnapshot = new Dictionary<string, double>(_raceScores);
            posSnapshot = new Dictionary<string, SimPosition>(_positions);
            priceSnap = _priceHistory.ToDictionary(k => k.Key, v => v.Value.ToList());
            volSnap = _volumeHistory.ToDictionary(k => k.Key, v => v.Value.ToList());
            lastSellSnap = new Dictionary<string, DateTime>(_lastSellTimes);
            lastLossSnap = new Dictionary<string, bool>(_lastTradeWasLoss);
            learnedSnap = new Dictionary<string, decimal>(_learnedAvgVolume);
            cumVolSnap = new Dictionary<string, long>(_cumVolume);
            decimal currentEquity = GetTotalEquity();
            pnlPct = _startingDayEquity > 0
                ? ((currentEquity - _startingDayEquity) / _startingDayEquity) * 100
                : 0;

            marketStatus = IsMarketSafe() ? "🟢 SAFE (BULL/FLAT)" : "🔴 DANGER (BEAR)";
        }

        symbolsToPrint = _tradeableStars
            .OrderByDescending(s => raceSnapshot.TryGetValue(s, out var sc) ? sc : 0)
            .Concat(new[] { "QQQ" })
            .Distinct()
            .ToList();


        // Bot State Logic
        string botState = "ACTIVE";
        DateTime nyNow = GetEasternTime();
        if (nyNow.TimeOfDay < new TimeSpan(10, 0, 0)) botState = "⏳ WAITING (10AM)";
        else if (_dailyLossCount >= 4) botState = "🚫 HALTED (LOSS LIMIT)";
        else if (_goalReached) botState = "🏆 GOAL REACHED";

        // --- 1. HEADER SECTION ---
        sb.AppendLine(new string('=', windowWidth - 1));
        sb.AppendLine($"  BOT: {botState} | Market: {marketStatus} | Day PnL: {pnlPct:F2}%".PadRight(windowWidth - 1));
        sb.AppendLine($"  Losses: {_dailyLossCount}/4 | Active Slots: {_positions.Count}/2 | Goal: +1.0%".PadRight(windowWidth - 1));
        sb.AppendLine($"  Local Time: {DateTime.Now:HH:mm:ss} | NYC Time: {nyNow:HH:mm:ss}".PadRight(windowWidth - 1));
        sb.AppendLine(new string('=', windowWidth - 1));

        // --- 2. COLUMN HEADERS ---
        sb.AppendLine(string.Format(" {0,-7} | {1,-9} | {2,-5} | {3,-6} | {4,-6} | {5,-6} | {6,-10} | {7,-12} | {8,-8}",
            "SYMBOL", "PRICE", "RSI", "TREND", "VOL-X", "RVOH", "STATUS", "POSITION", "PnL").PadRight(windowWidth - 1));
        sb.AppendLine(new string('-', windowWidth - 1));

        // --- 3. DATA ROWS ---
        foreach (var symbol in symbolsToPrint)
        {
            string rsiDisplay = "---", trendStr = "WAIT", posStr = "---", pnlStr = "---";
            string volXStr = "0.0x", rvohStr = "0.0x", statusStr = "READY";
            decimal currentPrice = 0;

            if (priceSnap.TryGetValue(symbol, out var prices) && prices.Count > 0)
            {
                volSnap.TryGetValue(symbol, out var volumes);
                currentPrice = prices.Last();

                if (prices.Count >= 15) rsiDisplay = CalculateRSI(symbol, 14).ToString("F1");
                if (prices.Count >= 20) trendStr = GetTrend(prices);

                if (volumes != null && volumes.Count >= 10)
                {
                    decimal avgVol = (decimal)volumes.Skip(volumes.Count - 10).Average();
                    volXStr = $"{(avgVol > 0 ? (decimal)volumes.Last() / avgVol : 0):F1}x";
                }

                if (learnedSnap.TryGetValue(symbol, out decimal learnedAvg) && learnedAvg > 0 &&
                    cumVolSnap.TryGetValue(symbol, out long curVol))
                {
                    rvohStr = $"{((decimal)curVol / learnedAvg):F1}x";
                }

                lastSellSnap.TryGetValue(symbol, out var lastSell);
                lastLossSnap.TryGetValue(symbol, out bool wasLoss);
                double minSinceSell = (DateTime.UtcNow - lastSell).TotalMinutes;
                int cooldown = wasLoss ? 30 : 15;

                if (lastSell != DateTime.MinValue && minSinceSell < cooldown)
                    statusStr = $"CD {Math.Ceiling(cooldown - minSinceSell)}m";
                else if (trendStr == "BEAR")
                    statusStr = "BEAR_WAIT";

                if (posSnapshot.TryGetValue(symbol, out var pos))
                {
                    posStr = $"{pos.Quantity}@{pos.AvgPrice:F2}";
                    decimal pnlPercent = pos.AvgPrice > 0 ? ((currentPrice - pos.AvgPrice) / pos.AvgPrice) * 100 : 0;
                    pnlStr = GetPnLBar(pnlPercent);
                    statusStr = "⭐ OWNED";
                }
            }

            sb.AppendLine(string.Format(
                " {0,-7} | {1,-9:C2} | {2,-5} | {3,-6} | {4,-6} | {5,-6} | {6,-10} | {7,-12} | {8,-8}",
                symbol, currentPrice, rsiDisplay, trendStr, volXStr, rvohStr, statusStr, posStr, pnlStr
            ));
        }


        // --- 4. FOOTER ---
        sb.AppendLine(new string('-', windowWidth - 1));
        sb.AppendLine(" [RECENT CLOSED TRADES]".PadRight(windowWidth - 1));
        lock (_lock)
        {
            var recentTrades = _tradeHistory.AsEnumerable().Reverse().Take(5).ToList();
            if (recentTrades.Count == 0) sb.AppendLine("  (No trades closed today)".PadRight(windowWidth - 1));
            foreach (var t in recentTrades)
            {
                string tLine = $"  {(t.Profit >= 0 ? "✅" : "❌")} {t.Symbol,-8} | PnL: {t.Profit,10:C2} | Exit: {t.ExitTime:HH:mm:ss}";
                sb.AppendLine(tLine.PadRight(windowWidth - 1));
            }
        }
        sb.AppendLine(new string('=', windowWidth - 1));
        // --- IB SYSTEM LOG (non-corrupting) ---
        if (RealBroker != null)
        {
            sb.AppendLine();
            sb.AppendLine(" [IB SYSTEM LOG]".PadRight(windowWidth - 1));

            while (RealBroker.TryDequeueIbLog(out var log))
                sb.AppendLine(("  " + log).PadRight(windowWidth - 1));
        }

        // --- 5. RENDER ---
        Console.SetCursorPosition(0, 0);
        Console.Write(sb.ToString());
    }
    private string GetPnLBar(decimal pnlPct)
    {
        // Width of the actual bar (excluding icon and text)
        int barWidth = 10;

        // Cap visual at +/- 3% for the bar scale so it doesn't break the UI
        decimal clamped = Math.Max(-3, Math.Min(3, pnlPct));

        // Calculate how many segments to fill
        // (clamped + 3) shifts the range from [-3,3] to [0,6]
        int greenChars = (int)((clamped + 3) / 6 * barWidth);

        // Create the bar string (█ for filled, ░ for empty)
        string bar = new string('█', greenChars).PadRight(barWidth, '░');

        // Choose icon based on profit or loss
        string colorIcon = pnlPct >= 0 ? "🟢" : "🔴";

        return $"{colorIcon} [{bar}] {pnlPct:F2}%";
    }
    protected double CalculateRSI(string symbol, int period = 14)
    {
        if (!_priceHistory.TryGetValue(symbol, out var prices)) return 50;
        int count = prices.Count;

        // Need at least period + 1 prices
        if (count <= period) return 50;

        decimal avgGain = 0m;
        decimal avgLoss = 0m;

        // 1️- INITIAL WINDOW — USE MOST RECENT PERIOD
        int start = count - period - 1;
        for (int i = start + 1; i <= start + period; i++)
        {
            decimal diff = prices[i] - prices[i - 1];
            if (diff > 0)
                avgGain += diff;
            else
                avgLoss -= diff;
        }

        avgGain /= period;
        avgLoss /= period;

        // 2️- WILDER SMOOTHING (CONTINUE TO LATEST BAR)
        for (int i = start + period + 1; i < count; i++)
        {
            decimal diff = prices[i] - prices[i - 1];
            avgGain = (avgGain * (period - 1) + (diff > 0 ? diff : 0)) / period;
            avgLoss = (avgLoss * (period - 1) + (diff < 0 ? -diff : 0)) / period;
        }

        if (avgLoss == 0) return 100;

        double rs = (double)(avgGain / avgLoss);
        return 100 - (100 / (1 + rs));
    }

    private string GetTrend(List<decimal> prices)
    {
        if (prices == null || prices.Count < 10) return "WAIT";
        // Look at the last 10 ticks/bars
        var recent = prices.Skip(prices.Count - 10).ToList();
        decimal first = recent.First();
        decimal last = recent.Last();

        // If price moved up by 0.1%, call it BULL (Aggressive)
        if (last > first * 1.001m) return "BULL";
        if (last < first * 0.999m) return "BEAR";

        return "FLAT";

    }
    // Inside SimulatedBroker.cs
   
    private DateTime GetEasternTime()
    {
        string tzId = RuntimeInformation.IsOSPlatform(OSPlatform.Windows) ? "Eastern Standard Time" : "America/New_York";
        return TimeZoneInfo.ConvertTimeFromUtc(DateTime.UtcNow, TimeZoneInfo.FindSystemTimeZoneById(tzId));
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
        catch (Exception ex)
        {
            Console.WriteLine($"[ERROR] Failed to save state: {ex.Message}");
        }
    }

    public void ResetPositionSync()
    {
        _syncedSymbols.Clear();
    }

    public void SyncExistingPosition(string symbol, decimal qty, decimal avgPrice)
    {
        if (_ignoreSyncUntil.TryGetValue(symbol, out var until) && DateTime.UtcNow < until)
        {
            return; // Ignore IBKR update for a few seconds after we sell
        }

        _syncedSymbols.Add(symbol);

        // If IB says we have 0, but bot thinks we have some, DELETE IT.
        if (qty == 0)
        {
            _positions.Remove(symbol);
            return;
        }

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
                EntryTime = DateTime.UtcNow // Assumption for synced positions
            };
        }
    }

    public void FinalizePositionSync()
    {
        // Remove any position the bot has that IBKR didn't mention at all
        var toRemove = _positions.Keys.Where(k => !_syncedSymbols.Contains(k)).ToList();
        foreach (var sym in toRemove)
        {
            _positions.Remove(sym);
        }
    }
    public void CheckEndOfDayLiquidation()
    {
        var nyNow = GetEasternTime();

        // 1. Liquidation at 3:45 PM (Close all active trades)
        // 1. Liquidation window
        if (nyNow.TimeOfDay > new TimeSpan(15, 45, 0) &&
            nyNow.TimeOfDay < new TimeSpan(16, 0, 0))
        {
            if (_positions.Count > 0)
                LiquidateAll();
        }




        // 2. Learning Mode Save at 4:00 PM (Market Close)
        // We use a small window (4:00 PM - 4:05 PM) and the _hasSavedToday flag
        if (nyNow.TimeOfDay >= new TimeSpan(16, 0, 0) && !_hasSavedToday)
        {
            SaveMarketMemory(); // This blends today's volume into history
            _hasSavedToday = true;
            Console.WriteLine("[SYSTEM] Market closed. Historical volume memory updated.");
        }

        // 3. Summary Email at 4:05 PM (After everything is finalized)
        if (nyNow.TimeOfDay > new TimeSpan(16, 5, 0) && !_eodEmailSent)
        {
            SendEodSummary();
            _eodEmailSent = true;
        }

        // 4. Reset flags for next day (Runs during pre-market)
        if (nyNow.TimeOfDay < new TimeSpan(9, 0, 0) &&
       (!_symbolLastResetDate.ContainsKey("QQQ") ||
        _symbolLastResetDate["QQQ"].Date != nyNow.Date))

        {
            // 🔥 Reset daily performance baseline
            _startingDayEquity = GetTotalEquity();
            _totalRealizedPnL = 0m;
            _tradeHistory.Clear();

            _eodEmailSent = false;
            _hasSavedToday = false;
            _killSwitchEngaged = false;
            _haltNewTrades = false;
            _dailyLossCount = 0;
            _chopWarnings = 0;
            _softTradeUsed = false;
            _tradesToday.Clear();

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
            string filePath = "trade_log.csv";
            bool fileExists = File.Exists(filePath);

            using (StreamWriter sw = new StreamWriter(filePath, true))
            {
                // Write Header if file is new
                if (!fileExists)
                {
                    sw.WriteLine("Timestamp,Symbol,Action,Price,PnL,RSI,VolSurge");
                }

                string line = $"{DateTime.Now:yyyy-MM-dd HH:mm:ss},{symbol},{side},{price:F2},{pnl:F2},{rsi:F1},{surge}";
                sw.WriteLine(line);
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[LOG ERROR] Could not write to CSV: {ex.Message}");
        }
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

    private decimal GetVolatilityAdjustedLoss(string symbol)
    {
        if (!_priceHistory.TryGetValue(symbol, out var prices) || prices.Count < 20)
            return -6m;

        decimal atr = prices
            .Skip(prices.Count - 14)
            .Zip(prices.Skip(prices.Count - 15), (c, p) => Math.Abs(c - p))
            .Average();

        return -Math.Max(6m, atr * 1.8m);
    }
    private decimal GetEntryThrottle()
    {
        decimal pct = (_totalRealizedPnL / _startingDayEquity);

        if (pct > 0.007m) return 0.5m;   // half size
        if (pct > 0.009m) return 0.25m;  // quarter size
        if (_chopWarnings >= 2) return 0.5m;
        if (_chopWarnings >= 3) return 0.25m;
        return 1.0m;
    }

    private double TimeOfDayMultiplier()
    {
        var ny = GetEasternTime().TimeOfDay;

        if (ny < new TimeSpan(10, 15, 0)) return 1.3;
        if (ny < new TimeSpan(11, 30, 0)) return 1.0;
        if (ny < new TimeSpan(13, 30, 0)) return 0.7;
        return 0.5;
    }
}