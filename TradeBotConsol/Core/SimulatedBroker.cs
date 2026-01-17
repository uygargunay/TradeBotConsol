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
 
}

public enum TradeSide { Buy, Sell }
public enum MarketRegime { Bullish, Neutral, Bearish }

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

public class PositionManager : SimulatedBroker { }

public class SimulatedBroker : IBroker
{
    // MODERATE TUNING CONSTANTS
    private const double VOLATILITY_CAP = 4.5; // Block entries if RVOH > 4.5x
    private const int MIN_HOLD_SECONDS = 120;  // 2-minute mandatory hold
    private const double RSI_HOOK_DROP = 3.0;  // Require 3.0 point drop from peak

    private const decimal dailyProfitGoalPct = 0.01m; // +2.5% Target
    private const decimal dailyLossLimitPct = 0.03m;   // -3.0% Stop
    private const decimal maxTradeLossPct = 0.02m;     // 2% Room for volatility
    private const int maxTradesGlobal = 15;            // Quality over quantity
    private const int MaxActivePositions = 4;          // Focus on 3 slots

    private const decimal initialAccountValue = 4000m;

    private const decimal roundTripFee = 2.00m;        // Matches $1 buy + $1 sell
    private const decimal slippagePct = 0.0002m;       // Increased to 0.02% for more realistic fills

    // Inside SimulatedBroker Class - Added Variable
    protected int _dailyLossCount = 0;

    public readonly string[] _tradeableStars = { 
    // The Core Momentum Kings
    "NVDA", "TSLA", "PLTR", "AMD", "META", "AMZN", "NFLX", "GOOGL",
    
    // High-Volatility Tech & Crypto Proxies
    "COIN", "MSTR", "ARM", "SMCI", "SHOP", "SNOW", 
    
    // Technical Respect (Clean VWAP/RSI performers)
    "MU", "AVGO", "PANW", "UBER", "DKNG", "PYPL", "ON",
    
    // High-Relative Volume Growth
    "RKLB", "MARA", "DKNG"
};

    public ConcurrentDictionary<string, List<decimal>> _priceHistory { get; set; } = new();
    public ConcurrentDictionary<string, List<long>> _volumeHistory { get; set; } = new();
    private ConcurrentDictionary<string, double> _lastRsiMemory = new ConcurrentDictionary<string, double>();
    private ConcurrentDictionary<string, long> _cumVolume = new();
    private ConcurrentDictionary<string, decimal> _cumVwapProd = new();
    private ConcurrentDictionary<string, DateTime> _symbolLastResetDate = new();
    protected readonly Dictionary<string, SimPosition> _positions = new();

    protected readonly Dictionary<string, int> _tradesToday = new();

    protected readonly Dictionary<string, DateTime> _lastSellTimes = new();
    private readonly ConcurrentDictionary<string, DateTime> _buyTimes = new ConcurrentDictionary<string, DateTime>();
    private readonly DateTime _botStartTime = DateTime.UtcNow;
    private readonly ConcurrentDictionary<string, byte> _pendingOrders = new();

    private readonly TimeSpan _latestEntryTime = new TimeSpan(15, 30, 0);
    private DateTime _lastHeartbeatEmail = DateTime.MinValue;
    private const string SaveFilePath = "bot_state.json";
    protected readonly object _lock = new object();
    private const int MinHoldMinutes = 5; // Bot must wait 5 minutes before it can sell
    private decimal _startingDayEquity = initialAccountValue;
    private decimal _totalRealizedPnL = 0m;
    private int _tradesExecutedToday = 0;
    private bool _haltNewTrades = false;
    private bool _goalReached = false;

    private Dictionary<string, decimal> _learnedAvgVolume = new();
    private const string MemoryFilePath = "market_memory.json";
    private const decimal LearningRate = 0.1m; // Adjusts the average by 10% each day

    private ConcurrentDictionary<string, double> _raceScores = new();
    private ConcurrentDictionary<string, DateTime> _raceStartTimes = new();
    private const double MIN_RACE_SCORE = 6.5; // Noise filter
    private const int PERSISTENCE_SECONDS = 30; // Must lead for 30s

    // === QUALITY CONTROL ===
    private const int MaxTradesPerDay = 4;
    private static readonly TimeSpan LastEntryTime = new TimeSpan(14, 0, 0); // 2:00 PM NY
    private const decimal BreakEvenTriggerPnL = 8.0m; // $8 per $1000 slot

    public IBroker RealBroker { get; set; }

    public class ClosedTrade
    {
        public string Symbol { get; set; }
        public decimal Profit { get; set; }
        public DateTime ExitTime { get; set; }
    }

    protected readonly List<ClosedTrade> _tradeHistory = new();

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
        decimal equity = initialAccountValue + _totalRealizedPnL;

        foreach (var pos in _positions.Values)
            equity += pos.UnrealizedPnL;

        return equity;
    }



    public void LoadMarketMemory()
{
    try
    {
        if (File.Exists(MemoryFilePath))
        {
            string json = File.ReadAllText(MemoryFilePath);
            // This loads your "historical average" back into the dictionary
            _learnedAvgVolume = JsonSerializer.Deserialize<Dictionary<string, decimal>>(json)
                                ?? new Dictionary<string, decimal>();
            Console.WriteLine($"[SYSTEM] Memory Loaded: {_learnedAvgVolume.Count} symbols.");
        }
    }
    catch (Exception ex)
    {
        Console.WriteLine($"[ERROR] Could not load memory: {ex.Message}");
    }
}

public void SaveMarketMemory()
{
        // Blend today's volume into our historical average (Learning Rate: 10%)
        foreach (var symbol in _tradeableStars)
        {
            // 1. Extract as 'long' (matches your dictionary type)
            if (_cumVolume.TryGetValue(symbol, out long todayVol))
            {
                // 2. Convert to decimal for the calculation
                decimal todaysVolDecimal = (decimal)todayVol;

                if (!_learnedAvgVolume.ContainsKey(symbol))
                {
                    _learnedAvgVolume[symbol] = todaysVolDecimal;
                }
                else
                {
                    // Blend: 90% old average + 10% today's volume
                    _learnedAvgVolume[symbol] = (_learnedAvgVolume[symbol] * 0.9m) + (todaysVolDecimal * 0.1m);
                }
            }
        }

        // Write it to the disk
        string json = JsonSerializer.Serialize(_learnedAvgVolume, new JsonSerializerOptions { WriteIndented = true });
    File.WriteAllText(MemoryFilePath, json);
    Console.WriteLine("[SYSTEM] Market memory saved to disk.");
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
    private ConcurrentDictionary<string, double> _peakRsi = new ConcurrentDictionary<string, double>();
    private Dictionary<string, bool> _lastTradeWasLoss = new(); // NEW: Track loss state

    public void ExecuteTradeLogic(string symbol)
    {
        // ─────────────────────────────────────────────
        // 1️⃣ GLOBAL SAFETY
        // ─────────────────────────────────────────────
        if (symbol == "QQQ") return;
        if (_haltNewTrades || _goalReached) return;
        if (_dailyLossCount >= 4)
            return;

        var nyNow = GetEasternTime();
        if (nyNow.TimeOfDay > LastEntryTime) return;

        // ─────────────────────────────────────────────
        // 2️⃣ STARTUP SHIELD
        // ─────────────────────────────────────────────
        var minutesSinceStart = (DateTime.UtcNow - _botStartTime).TotalMinutes;
        if (minutesSinceStart < 1.0)
        {
            _lastRsiMemory[symbol] = CalculateRSI(symbol, 14);
            return;
        }

        // ─────────────────────────────────────────────
        // 3️⃣ RACE SCORE CALCULATION
        // ─────────────────────────────────────────────
        double rawScore = CalculateRaceScore(symbol);
        double currentScore = rawScore * TimeOfDayMultiplier();
        _raceScores[symbol] = currentScore;
        if (_raceScores.TryGetValue(symbol, out var prevScore))
        {
            if (prevScore > MIN_RACE_SCORE && currentScore < prevScore * 0.6)
                return;
        }
        lock (_lock)
        {
            bool hasPosition = _positions.ContainsKey(symbol);

            // ─────────────────────────────────────────────
            // 🚀 ENTRY LOGIC
            // ─────────────────────────────────────────────
            if (!hasPosition && _positions.Count < MaxActivePositions)
            {
                if (_tradesToday.TryGetValue(symbol, out int tradeCount) && tradeCount >= 2)
                    return;
                // A. MARKET REGIME FILTER
                if (!IsMarketSafe()) return;

                // B. RACE PERSISTENCE CHECK
                if (currentScore >= MIN_RACE_SCORE)
                {
                    if (!_raceStartTimes.TryGetValue(symbol, out var start))
                    {
                        _raceStartTimes[symbol] = DateTime.UtcNow;
                        return;
                    }

                    if ((DateTime.UtcNow - start).TotalSeconds < PERSISTENCE_SECONDS)
                        return;
                }
                else
                {
                    _raceStartTimes.TryRemove(symbol, out _);
                    return;
                }

                // C. DATA SUFFICIENCY
                if (!_priceHistory.TryGetValue(symbol, out var prices) || prices.Count < 20)
                    return;

                decimal currentPrice = prices.Last();
                double rsi = CalculateRSI(symbol, 14);

                _lastRsiMemory.TryGetValue(symbol, out double prevRsi);
                bool rsiCross = prevRsi <= 45 && rsi > 45;
                _lastRsiMemory[symbol] = rsi;

                // VWAP
                decimal vwap = _cumVolume.TryGetValue(symbol, out var vol) && vol > 0
                    ? _cumVwapProd[symbol] / vol
                    : currentPrice;

                // D. FINAL ENTRY CONFIRMATION
                if (GetTrend(prices) == "BULL" && rsiCross && currentPrice > vwap)
                {
                    if (_pendingOrders.ContainsKey(symbol)) return;

                    decimal throttle = GetEntryThrottle();
                    decimal slotBudget = (_startingDayEquity / MaxActivePositions) * throttle;

                    int qty = (int)Math.Floor((slotBudget - 1.0m) / currentPrice);
                    if (qty <= 0) return;

                    _peakRsi[symbol] = rsi;
                    _buyTimes[symbol] = DateTime.UtcNow;
                    _pendingOrders.TryAdd(symbol, 0);

                    SubmitOrder(symbol, qty, currentPrice, TradeSide.Buy, rsi);
                    _raceStartTimes.TryRemove(symbol, out _);
                    SaveState();
                }
            }
            // ─────────────────────────────────────────────
            // 🔻 EXIT LOGIC
            // ─────────────────────────────────────────────
            else if (hasPosition)
            {
                var pos = _positions[symbol];
                pos.CurrentPrice = _priceHistory[symbol].Last();

                decimal netPnL = pos.UnrealizedPnL - roundTripFee;
                double rsi = CalculateRSI(symbol, 14);

                // A. WINNER PROTECTION
                if (!pos.IsBreakEvenProtected && netPnL >= BreakEvenTriggerPnL)
                {
                    pos.TrailingStop = Math.Max(
                        pos.TrailingStop,
                        pos.AvgPrice * 1.001m
                    );
                    pos.IsBreakEvenProtected = true;

                }
                // 🔒 PROFIT-SCALED TRAILING
                decimal profitPct =
                    pos.AvgPrice > 0
                        ? (pos.CurrentPrice - pos.AvgPrice) / pos.AvgPrice
                        : 0;

                if (profitPct >= 0.006m)
                {
                    pos.TrailingStop = Math.Max(
                        pos.TrailingStop,
                        pos.CurrentPrice * 0.997m
                    );
                }
                else if (profitPct >= 0.004m)
                {
                    pos.TrailingStop = Math.Max(
                        pos.TrailingStop,
                        pos.CurrentPrice * 0.995m
                    );
                }

                // B. RSI HOOK
                _peakRsi[symbol] = Math.Max(
                    _peakRsi.TryGetValue(symbol, out var peak) ? peak : rsi,
                    rsi
                );

                bool rsiHook =
                    _peakRsi[symbol] >= 65 &&
                    (_peakRsi[symbol] - rsi) >= RSI_HOOK_DROP;

                // C. SOFT EXIT CONFIRMATION
                decimal vwap = _cumVwapProd[symbol] / _cumVolume[symbol];
                bool momentumDecay = currentScore < (MIN_RACE_SCORE * 0.4);
                bool vwapLoss = pos.CurrentPrice < vwap;
                bool softExit = momentumDecay && vwapLoss;

                bool hitStop = pos.CurrentPrice <= pos.TrailingStop;

                if (hitStop || softExit || rsiHook)
                {
                    SubmitOrder(symbol, (int)pos.Quantity, pos.CurrentPrice, TradeSide.Sell);
                    _raceStartTimes.TryRemove(symbol, out _);

                    string reason =
                        hitStop ? "STOP" :
                        rsiHook ? "RSI_HOOK" :
                        "MOM_DECAY";

                    Task.Run(() =>
                        SendEmailNotification(
                            $"🔴 SELL ({reason}): {symbol}",
                            $"PnL: {netPnL:C2}"
                        )
                    );
                }
            }
        }
    }

    public void UpdateHistory(string symbol, decimal price, long volume)
    {
        if (price <= 0) return;

        lock (_lock)
        {
            // 1. Data Management
            ManagePriceData(symbol, price, volume);

            // 2. Main Logic Execution
            // This now handles both Entry (if no position) and Exit (if has position)
            ExecuteTradeLogic(symbol);

            // 3. Global Safety Check (PnL and Goal Tracking)
            CheckDailyGoal();

            // 4. Time-based Liquidation
            CheckEndOfDayLiquidation();
        }
    }

    public void SubmitOrder(
      string symbol,
      int qty,
      decimal price,
      TradeSide side,
      double currentRsi = 0,
      string orderType = "LMT")
    {
        // 1️⃣ ATOMIC PENDING CHECK
        if (!_pendingOrders.TryAdd(symbol, 0))
            return;

        try
        {
            // 2️⃣ GLOBAL BUY HALT
            if (side == TradeSide.Buy && (_haltNewTrades || _goalReached))
                return;

            // 3️⃣ VALIDATE SELL
            if (side == TradeSide.Sell)
            {
                if (!_positions.TryGetValue(symbol, out var pos))
                    return;

                var holdMinutes = (DateTime.UtcNow - pos.EntryTime).TotalMinutes;
                decimal currentPnL =
                    pos.AvgPrice > 0 ? (price - pos.AvgPrice) / pos.AvgPrice : 0;

                // Churn shield (allow hard stops)
                if (holdMinutes < 5.0 && currentPnL > -0.015m && !_haltNewTrades)
                    return;

                qty = (int)Math.Abs(pos.Quantity);
                if (qty <= 0) return;
            }

            // 4️⃣ APPLY SLIPPAGE (SIMULATION ONLY)
            decimal fillPrice = side == TradeSide.Buy
                ? price * (1 + slippagePct)
                : price * (1 - slippagePct);

            // 5️⃣ SEND ORDER TO REAL BROKER (NO SLIPPAGE HERE)
            if (RealBroker != null)
            {
                decimal priceToSend =
                    side == TradeSide.Sell ? 0 : Math.Round(price, 2);

                RealBroker.SubmitOrder(symbol, qty, priceToSend, side, currentRsi);
            }

            // 6️⃣ ACCOUNTING & STATE UPDATE
            lock (_lock)
            {
                if (side == TradeSide.Sell && _positions.TryGetValue(symbol, out var pos))
                {
                    decimal pnl =
                        (fillPrice - pos.AvgPrice) * pos.Quantity - roundTripFee;

                    _totalRealizedPnL += pnl;
                    if (pnl <= GetVolatilityAdjustedLoss(symbol))
                        _dailyLossCount++;

                    _tradeHistory.Add(new ClosedTrade
                    {
                        Symbol = symbol,
                        Profit = pnl,
                        ExitTime = DateTime.UtcNow
                    });

                    _positions.Remove(symbol);
                    _lastSellTimes[symbol] = DateTime.UtcNow;
                    _lastTradeWasLoss[symbol] = pnl < 0;
                    _tradesToday[symbol] = _tradesToday.GetValueOrDefault(symbol) + 1;

                    if (_dailyLossCount >= 4)
                        _haltNewTrades = true;
                }
                else if (side == TradeSide.Buy)
                {
                    _positions[symbol] = new SimPosition
                    {
                        Symbol = symbol,
                        Quantity = qty,
                        AvgPrice = fillPrice,
                        CurrentPrice = fillPrice,
                        EntryTime = DateTime.UtcNow,
                        TrailingStop = fillPrice * 0.985m
                    };

                    _buyTimes[symbol] = DateTime.UtcNow;
                    _tradesExecutedToday++;
                }
            }
        }
        finally
        {
            // 7️⃣ RELEASE PENDING FLAG AFTER 3 SECONDS
            Task.Delay(3000).ContinueWith(t =>
            {
                _pendingOrders.TryRemove(symbol, out _);
            });
        }
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

            if (_priceHistory[symbol].Count > 200)
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
                LiquidateAll("Daily 3% Goal Met");
            }
            else if (profitPercent <= (dailyLossLimitPct * -1))
            {
                // FIX: Set halt to true BEFORE liquidating to prevent the loop
                _haltNewTrades = true;
                LiquidateAll("Daily Loss Limit Hit");
            }
        }
    }

    public void LiquidateAll(string reason)
    {
        lock (_lock)
        {
            _haltNewTrades = true; // STOP THE BOT FROM BUYING AGAIN IMMEDIATELY
        }

        Console.WriteLine($"\n[SYSTEM] !!! {reason.ToUpper()} !!! Liquidating all positions.");

        // Create a copy of the keys to avoid collection modified errors
        var symbols = _positions.Keys.ToList();

        foreach (var sym in symbols)
        {
            if (_positions.TryGetValue(sym, out var pos))
            {
                // IMPORTANT: Use Market orders for emergency liquidation to bypass 3% constraints
                // If your SubmitOrder doesn't support Market types, ensure price is set very aggressively
                SubmitOrder(sym, (int)pos.Quantity, pos.CurrentPrice, TradeSide.Sell);

 
            }
        }
        SaveState();
    }


    // Add this dictionary to your class variables at the top
    protected readonly Dictionary<string, decimal> _tenDayAvgVolume = new();
    private DateTime _lastResetDate = DateTime.MinValue;
    // 1. Add this private variable at the top of your class to track the date


    private void ManagePriceData(string symbol, decimal price, long volume)
    {
        DateTime nyNow = GetEasternTime();
        bool isMarketHours = nyNow.TimeOfDay >= new TimeSpan(9, 30, 0) && nyNow.TimeOfDay < new TimeSpan(16, 0, 0);

        // 1. Get or Add handles initialization automatically
        var prices = _priceHistory.GetOrAdd(symbol, _ => new List<decimal>());
        var volumes = _volumeHistory.GetOrAdd(symbol, _ => new List<long>());

        // 2. Safe Reset
        DateTime lastReset = _symbolLastResetDate.GetOrAdd(symbol, DateTime.MinValue);
        if (nyNow.TimeOfDay >= new TimeSpan(9, 30, 0) && lastReset.Date != nyNow.Date)
        {
            _cumVolume[symbol] = 0;
            _cumVwapProd[symbol] = 0;
            _symbolLastResetDate[symbol] = nyNow.Date;
        }

        // 3. Update Lists (List is NOT thread-safe, so we lock just the list update)
        lock (prices)
        {
            prices.Add(price);
            volumes.Add(volume);

            if (prices.Count > 300)
            {
                prices.RemoveAt(0);
                volumes.RemoveAt(0);
            }
        }

        // 4. Update Cumulative Metrics (Thread-safe addition)
        if (isMarketHours)
        {
            _cumVolume.AddOrUpdate(symbol, volume, (key, oldVol) => oldVol + volume);
            _cumVwapProd.AddOrUpdate(symbol, (price * volume), (key, oldProd) => oldProd + (price * volume));
        }
    }

    public void PrintStatusTable()
    {
        var sb = new System.Text.StringBuilder();
        List<string> symbolsToPrint;
        string marketStatus;
        decimal pnlPct;
        int windowWidth = Console.WindowWidth > 0 ? Console.WindowWidth : 125;

        lock (_lock)
        {
            // 1. SAFE SORTING: Use TryGetValue during the OrderBy to prevent crashes
            symbolsToPrint = _tradeableStars
                .OrderByDescending(s => {
                    if (!_volumeHistory.TryGetValue(s, out var vols) || vols.Count < 10) return 0;
                    decimal avg = (decimal)vols.Skip(vols.Count - 10).Average();
                    return avg > 0 ? (decimal)vols.Last() / avg : 0;
                })
                .Concat(new[] { "QQQ" })
                .Distinct()
                .ToList();

            marketStatus = IsMarketSafe() ? "🟢 SAFE (BULL/FLAT)" : "🔴 DANGER (BEAR)";
            decimal currentEquity = GetTotalEquity();
            pnlPct = _startingDayEquity > 0 ? ((currentEquity - _startingDayEquity) / _startingDayEquity) * 100 : 0;
        }

        // Bot State Logic
        string botState = "ACTIVE";
        DateTime nyNow = GetEasternTime();
        if (nyNow.TimeOfDay < new TimeSpan(10, 0, 0)) botState = "⏳ WAITING (10AM)";
        else if (_dailyLossCount >= 4) botState = "🚫 HALTED (LOSS LIMIT)";
        else if (_goalReached) botState = "🏆 GOAL REACHED";

        // --- 1. HEADER SECTION ---
        sb.AppendLine(new string('=', windowWidth - 1));
        sb.AppendLine($"  BOT: {botState} | Market: {marketStatus} | Day PnL: {pnlPct:F2}%".PadRight(windowWidth - 1));
        sb.AppendLine($"  Losses: {_dailyLossCount}/4 | Active Slots: {_positions.Count}/4 | Goal: +3.0%".PadRight(windowWidth - 1));
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

            lock (_lock)
            {
                // DEFENSIVE CHECK: Ensure price history exists for this symbol
                if (_priceHistory.TryGetValue(symbol, out var prices) && prices.Count > 0)
                {
                    _volumeHistory.TryGetValue(symbol, out var volumes);
                    currentPrice = prices.Last();

                    // Safe Indicators
                    if (prices.Count >= 15) rsiDisplay = CalculateRSI(symbol, 14).ToString("F1");
                    if (prices.Count >= 20) trendStr = GetTrend(prices);

                    // Safe Vol-X (Short-term momentum)
                    if (volumes != null && volumes.Count >= 10)
                    {
                        decimal avgVol = (decimal)volumes.Skip(volumes.Count - 10).Average();
                        volXStr = $"{(avgVol > 0 ? (decimal)volumes.Last() / avgVol : 0):F1}x";
                    }

                    // Safe RVOH (Daily Relative Volume) - FIXED TO PREVENT KEYNOTFOUND
                    if (_learnedAvgVolume.TryGetValue(symbol, out decimal learnedAvg) && learnedAvg > 0)
                    {
                        // Use TryGetValue here instead of [_cumVolume[symbol]]
                        if (_cumVolume.TryGetValue(symbol, out long currentCumVol))
                        {
                            decimal rvoh = (decimal)currentCumVol / learnedAvg;
                            rvohStr = $"{rvoh:F1}x";
                        }
                        else
                        {
                            rvohStr = "0.0x";
                        }
                    }

                    // Safe Status/Cooldown
                    _lastSellTimes.TryGetValue(symbol, out var lastSell);
                    _lastTradeWasLoss.TryGetValue(symbol, out bool wasLoss);
                    double minSinceSell = (DateTime.UtcNow - lastSell).TotalMinutes;
                    int cooldown = wasLoss ? 30 : 15;

                    if (lastSell != DateTime.MinValue && minSinceSell < cooldown)
                        statusStr = $"CD {Math.Ceiling(cooldown - minSinceSell)}m";
                    else if (trendStr == "BEAR")
                        statusStr = "BEAR_WAIT";

                    // Position Info & PnL Bar
                    if (_positions.TryGetValue(symbol, out var pos))
                    {
                        posStr = $"{pos.Quantity}@{pos.AvgPrice:F2}";

                        // Calculate PnL % for the visual bar we added
                        decimal pnlPercent = pos.AvgPrice > 0 ? ((currentPrice - pos.AvgPrice) / pos.AvgPrice) * 100 : 0;
                        pnlStr = GetPnLBar(pnlPercent);

                        statusStr = "⭐ OWNED";
                    }
                }
            }

            string row = string.Format(" {0,-7} | {1,-9:C2} | {2,-5} | {3,-6} | {4,-6} | {5,-6} | {6,-10} | {7,-12} | {8,-8}",
                symbol, currentPrice, rsiDisplay, trendStr, volXStr, rvohStr, statusStr, posStr, pnlStr);

            sb.AppendLine(row.PadRight(windowWidth - 1));
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

        // 1️⃣ INITIAL WINDOW — USE MOST RECENT PERIOD
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

        // 2️⃣ WILDER SMOOTHING (CONTINUE TO LATEST BAR)
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

        lock (prices)
        {
            // Look at the last 10 ticks/bars
            var recent = prices.Skip(prices.Count - 10).ToList();
            decimal first = recent.First();
            decimal last = recent.Last();

            // If price moved up by 0.1%, call it BULL (Aggressive)
            if (last > first * 1.001m) return "BULL";
            if (last < first * 0.999m) return "BEAR";

            return "FLAT";
        }
    }
    public void SyncExistingPosition(string symbol, decimal qty, decimal avgPrice)
    {
        lock (_lock)
        {
            // 1. Only sync if it's a long position (qty > 0) and not already tracked
            if (qty > 0 && !_positions.ContainsKey(symbol))
            {
                _positions[symbol] = new SimPosition
                {
                    Symbol = symbol,       // Fixed: Added the missing Symbol property
                    Quantity = qty,
                    AvgPrice = avgPrice,
                    CurrentPrice = avgPrice,

                    // 2. SET PROTECTIVE STOP
                    // We set a 1.5% stop based on the current price or avg price
                    TrailingStop = avgPrice * 0.985m,
                    EntryTime = DateTime.UtcNow.AddMinutes(-5) // 3. Fake a 5-min history
                };

                // 4. PREVENT INSTANT INDICATOR EXITS
                // We initialize the Peak RSI at a neutral level (50) 
                // so the 'RSI Hook' logic doesn't trigger until the bot sees a new peak.
                _peakRsi[symbol] = 50.0;
                _buyTimes[symbol] = DateTime.UtcNow.AddMinutes(-5);

                Console.WriteLine($"[SYNC] Found {symbol} in TWS. Quantity: {qty} | Avg: {avgPrice:C2} | Stop: {avgPrice * 0.985m:C2}");
            }
            else if (qty == 0 && _positions.ContainsKey(symbol))
            {
                // 5. CLEANUP: If TWS says 0 but we have it, remove it.
                _positions.Remove(symbol);
                Console.WriteLine($"[SYNC] Cleanup: {symbol} is no longer held in TWS.");
            }
        }
    }
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
                DailyLossCount = _dailyLossCount, // ADD THIS LINE
                RealizedPnLTotal = _totalRealizedPnL,
                StartingDayEquity = _startingDayEquity,
                TradesPerSymbol = _tradesToday,
                BuyTimes = _buyTimes,
                PriceHistory = _priceHistory,
                VolumeHistory = _volumeHistory,
                LastSellTimes = _lastSellTimes
            };
            string json = JsonSerializer.Serialize(data, new JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(SaveFilePath, json);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[ERROR] Failed to save state: {ex.Message}");
        }
    }
    private bool _hasSavedToday = false;
    private bool _eodEmailSent = false;
    public void CheckEndOfDayLiquidation()
    {
        var nyNow = GetEasternTime();

        // 1. Liquidation at 3:45 PM (Close all active trades)
        if (nyNow.TimeOfDay > new TimeSpan(15, 45, 0) && nyNow.TimeOfDay < new TimeSpan(16, 0, 0))
        {
            if (_positions.Count > 0) LiquidateAll("End of Day Liquidation");
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
        if (nyNow.TimeOfDay < new TimeSpan(9, 0, 0))
        {
            _eodEmailSent = false;
            _hasSavedToday = false;
        }
    }

    public void SendEmailNotification(string subject, string messageBody)
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
                smtp.Send(message);
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

            string report = $"--- DAILY PERFORMANCE REPORT ---\n\n" +
                            $"Final PnL: {_totalRealizedPnL:C2}\n" +
                            $"Total Trades: {totalTrades}\n" +
                            $"Wins: {wins} | Losses: {losses}\n" +
                            $"Win Rate: {winRate:F1}%\n" +
                            $"Daily Loss Count: {_dailyLossCount}/4\n\n" +
                            "--- TRADE DETAILS ---\n";

            foreach (var t in _tradeHistory)
            {
                report += $"[{t.ExitTime:HH:mm}] {t.Symbol}: {t.Profit:C2}\n";
            }

            Task.Run(() => SendEmailNotification($"📊 EOD REPORT: {_totalRealizedPnL:C2}", report));
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