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

    private const decimal dailyProfitGoalPct = 0.025m; // +2.5% Target
    private const decimal dailyLossLimitPct = 0.03m;   // -3.0% Stop
    private const decimal maxTradeLossPct = 0.02m;     // 2% Room for volatility
    private const int maxTradesGlobal = 15;            // Quality over quantity
    private const int MaxActivePositions = 3;          // Focus on 3 slots

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
    private readonly HashSet<string> _pendingOrders = new();
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

    public IBroker RealBroker { get; set; }

    public class ClosedTrade
    {
        public string Symbol { get; set; }
        public decimal Profit { get; set; }
        public DateTime ExitTime { get; set; }
    }

    protected readonly List<ClosedTrade> _tradeHistory = new();
   


    private DateTime _lastHeartbeatLogged = DateTime.UtcNow;


    // --- MARKET SAFETY ---
    public bool IsMarketSafe()
    {
        if (!_priceHistory.ContainsKey("QQQ") || _priceHistory["QQQ"].Count < 20) return false;
        string qqqTrend = GetTrend(_priceHistory["QQQ"]);
        return qqqTrend != "BEAR";
    }

    public decimal GetTotalEquity()
    {
        decimal currentEquity = initialAccountValue + _totalRealizedPnL;
        foreach (var pos in _positions.Values) currentEquity += pos.UnrealizedPnL;
        return currentEquity;
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
        // 1. Global Safety & Exclusions
        if (symbol == "QQQ" ) return;
        if (_haltNewTrades || _goalReached) return;

        // 2. STARTUP COOLING PERIOD (2-Minute Shield)
        // Ensures indicators stabilize and memory is built before trading begins.
        if ((DateTime.UtcNow - _botStartTime).TotalMinutes < 2)
        {
            // We still record the RSI so memory is ready the moment the 2 mins end
            _lastRsiMemory[symbol] = CalculateRSI(symbol, 14);
            return;
        }

        lock (_lock)
        {
            // 3. Price History Check
            if (!_priceHistory.ContainsKey(symbol) || _priceHistory[symbol].Count < 20) return;

            var prices = _priceHistory[symbol];
            var volumes = _volumeHistory[symbol];
            decimal currentPrice = prices.Last();
            long currentVolume = volumes.Last();
            double rsi = CalculateRSI(symbol, 14);

            // 4. RSI CROSSOVER MEMORY (The "Hook" Detector)
            if (!_lastRsiMemory.TryGetValue(symbol, out double prevRsi))
            {
                _lastRsiMemory[symbol] = rsi;
                return; // Baseline established; wait for next tick
            }

            // This is the trigger: Must move from <= 45 to > 45
            bool rsiJustEnteredZone = (prevRsi <= 45.0 && rsi > 45.0);
            _lastRsiMemory[symbol] = rsi; // Always update memory

            string trend = GetTrend(prices);

            // --- 1. SPREAD & MOMENTUM ---
            decimal lastPrice = prices[prices.Count - 2];
            decimal priceChangePct = Math.Abs((currentPrice - lastPrice) / lastPrice) * 100;
            bool spreadIsTight = priceChangePct <= 0.12m;

            // --- 2. VWAP & VOLUME ANALYSIS ---
            decimal vwap = _cumVolume.ContainsKey(symbol) && _cumVolume[symbol] > 0
                ? _cumVwapProd[symbol] / _cumVolume[symbol] : currentPrice;
            decimal avgVolume = volumes.Count >= 10 ? (decimal)volumes.Skip(volumes.Count - 10).Average() : 0;

            bool volumeSurge = avgVolume > 0 && currentVolume > (avgVolume * 1.5m);
            bool isMarketPanic = avgVolume > 0 && currentVolume > (avgVolume * 10.0m);
            bool volumeClimax = avgVolume > 0 && currentVolume > (avgVolume * 8.0m);

            bool hasPosition = _positions.ContainsKey(symbol);

            // --- 3. ENTRY LOGIC ---
            if (!hasPosition && _positions.Count < MaxActivePositions)
            {
                DateTime nyNow = GetEasternTime();
                bool marketOpenTime = nyNow.TimeOfDay >= new TimeSpan(10, 0, 0) && nyNow.TimeOfDay < _latestEntryTime;
                bool underLossLimit = _dailyLossCount < 4;

                _lastSellTimes.TryGetValue(symbol, out var lastSell);
                double minutesSinceLastTrade = (DateTime.UtcNow - lastSell).TotalMinutes;
                _lastTradeWasLoss.TryGetValue(symbol, out bool wasLoss);

                int requiredCooldown = wasLoss ? 30 : 15;
                bool inCooldown = minutesSinceLastTrade < requiredCooldown;

                decimal ma20 = (decimal)prices.Skip(prices.Count - 20).Average();
                bool isOverextended = currentPrice > (ma20 * 1.012m);

                if (marketOpenTime && underLossLimit && !inCooldown && !isMarketPanic && IsMarketSafe())
                {
                    // Trigger Check: trend and volume must align with the fresh RSI crossover
                    if (trend == "BULL" && rsiJustEnteredZone && rsi < 65 && currentPrice > vwap && volumeSurge &&
                        !isOverextended && !volumeClimax && spreadIsTight)
                    {
                        decimal budgetPerSlot = _startingDayEquity / MaxActivePositions;
                        int qty = (int)Math.Floor((budgetPerSlot - 1.00m) / currentPrice);

                        if (qty > 0)
                        {
                            _peakRsi[symbol] = rsi;
                            _buyTimes[symbol] = DateTime.UtcNow;
                            SubmitOrder(symbol, qty, currentPrice, TradeSide.Buy, rsi);
                            SaveState();

                            Task.Run(() => SendEmailNotification($"🟢 BUY: {symbol}", $"Bought {qty} @ {currentPrice:C2} (RSI Hook: {rsi:F2})"));
                        }
                    }
                }
            }
            // --- 4. EXIT LOGIC ---
            else if (hasPosition)
            {
                var pos = _positions[symbol];
                pos.CurrentPrice = currentPrice;
                decimal netPnL = pos.UnrealizedPnL - roundTripFee;

                _buyTimes.TryGetValue(symbol, out var buyTime);
                double minutesHeld = (DateTime.UtcNow - buyTime).TotalMinutes;
                bool timeShieldActive = minutesHeld < 5.0;

                if (!_peakRsi.ContainsKey(symbol) || rsi > _peakRsi[symbol]) _peakRsi[symbol] = rsi;

                if (netPnL > 8.00m)
                {
                    decimal tightStop = currentPrice * 0.995m;
                    if (tightStop > pos.TrailingStop) pos.TrailingStop = tightStop;
                    pos.IsBreakEvenProtected = true;
                }

                double peak = _peakRsi[symbol];
                bool rsiHooked = peak > 75 && rsi < (peak - 5.0);
                bool climaxExit = volumeClimax && netPnL > 5.00m;
                bool hitStop = currentPrice <= pos.TrailingStop;
                bool rsiWinExit = (rsi >= 80 && netPnL > 5.00m) || (rsiHooked && netPnL > 2.00m);

                if (hitStop || rsiWinExit || climaxExit)
                {
                    if (timeShieldActive && !hitStop) return;

                    int sellQty = (int)pos.Quantity;
                    if (sellQty <= 0)
                    {
                        _positions.Remove(symbol);
                        return;
                    }

                    SubmitOrder(symbol, sellQty, currentPrice, TradeSide.Sell);

                    string reason = hitStop ? "STOP" : (climaxExit ? "CLIMAX" : "RSI_HOOK");
                    Task.Run(() => SendEmailNotification($"🔴 SELL ({reason}): {symbol}", $"Net PnL: {netPnL:C2}"));
                }
            }
        }
    }
    public void SubmitOrder(string symbol, int qty, decimal price, TradeSide side, double currentRsi = 0, string orderType = "LMT")
    {
        lock (_lock)
        {
            if (_pendingOrders.Contains(symbol)) return;
            if (_haltNewTrades && side == TradeSide.Buy) return;

            if (side == TradeSide.Sell)
            {
                if (!_positions.TryGetValue(symbol, out var pos)) return;

                // 1. CHURN SHIELD
                var holdMinutes = (DateTime.UtcNow - pos.EntryTime).TotalMinutes;
                decimal currentPnL = (price - pos.AvgPrice) / pos.AvgPrice;
                if (holdMinutes < 5.0 && currentPnL > -0.020m) return;

                // 2. INTEGER ROUNDING (Fixes the IB 2176 Warning)
                qty = (int)Math.Floor(Math.Abs(pos.Quantity));
            }
            _pendingOrders.Add(symbol);
        }

        // 3. EXECUTION (The "5-Argument" Workaround)
        if (RealBroker != null)
        {
            // For Sells, we send price 0. Most IBKR wrappers interpret price 0 as "Market".
            // This bypasses the 3% Percentage Constraint (Error 163).
            decimal priceToSend = (side == TradeSide.Sell) ? 0 : price;

            RealBroker.SubmitOrder(symbol, qty, priceToSend, side, currentRsi);
        }

        // 4. STATE UPDATE (PnL Calculation)
        lock (_lock)
        {
            if (side == TradeSide.Sell && _positions.TryGetValue(symbol, out var pos))
            {
                decimal pnl = (price - pos.AvgPrice) * pos.Quantity - roundTripFee;
                _totalRealizedPnL += pnl;

                if (pnl < -5.00m) _dailyLossCount++;

                _tradeHistory.Add(new ClosedTrade { Symbol = symbol, Profit = pnl, ExitTime = DateTime.UtcNow });
                _positions.Remove(symbol);
                _lastSellTimes[symbol] = DateTime.UtcNow;

                if (_dailyLossCount >= 4) _haltNewTrades = true;
            }
            else if (side == TradeSide.Buy)
            {
                _positions[symbol] = new SimPosition
                {
                    Symbol = symbol, // Now works because we added it in Step 1
                    Quantity = qty,
                    AvgPrice = price,
                    CurrentPrice = price,
                    EntryTime = DateTime.UtcNow,
                    TrailingStop = price * 0.98m
                };
            }
        }

        Task.Delay(3000).ContinueWith(_ => { lock (_lock) { _pendingOrders.Remove(symbol); } });
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

                // Force local cleanup so the loop doesn't re-trigger on this symbol
                _positions.Remove(sym);
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
    public void UpdateHistory(string symbol, decimal price, long volume)
    {
        if (price <= 0) return;

        lock (_lock)
        {
            // 1. Pre-Market Cleanup
            // If it's before 9:30 AM and we have old flags set, reset for the new day
            var nyTime = GetEasternTime().TimeOfDay;
            if (nyTime < new TimeSpan(9, 30, 0) && (_goalReached || _haltNewTrades))
            {
                ResetDailyStats();
            }

            // 2. Data Management
            // Passes the tick to ManagePriceData to update lists and cumulative volume
            ManagePriceData(symbol, price, volume);

            // 3. EXIT MANAGEMENT (High Priority - Every Tick)
            // We check stops/exits immediately so we don't miss a price flush
            if (_positions.ContainsKey(symbol))
            {
                ExecuteExitLogic(symbol);
            }

            // 4. ENTRY SCANNING (Conditional)
            // Only scan if we are in the trading window and have room for new positions
            if (nyTime >= new TimeSpan(9, 30, 0) && nyTime < new TimeSpan(16, 0, 0))
            {
                if (!_haltNewTrades && !_goalReached && _positions.Count < MaxActivePositions)
                {
                    // Optimization: Only scan if the specific ticking symbol is ready.
                    // This is much lighter on the CPU than scanning all 25 stars every tick.
                    if (_priceHistory.ContainsKey(symbol) && _priceHistory[symbol].Count >= 50)
                    {
                        // Check if this specific symbol warrants an entry
                        CheckSpecificEntry(symbol);
                    }
                }
            }

            // 5. Global Safety Check
            CheckDailyGoal();
        }
    }
    private void CheckSpecificEntry(string symbol)
    {
        if (!_priceHistory.TryGetValue(symbol, out var prices) || prices.Count < 20) return;
        if (!_volumeHistory.TryGetValue(symbol, out var volumes) || volumes.Count < 10) return;

        decimal currentPrice = prices.Last();
        double rsi = CalculateRSI(symbol, 14);
        string trend = GetTrend(prices);

        // MODERATE: Volume & Volatility Checks
        decimal avgVol = (decimal)volumes.Skip(volumes.Count - 10).Take(9).Average();
        decimal volX = avgVol > 0 ? (decimal)volumes.Last() / avgVol : 0;

        decimal rvoh = 0;
        if (_learnedAvgVolume.TryGetValue(symbol, out decimal learnedAvg) && learnedAvg > 0)
        {
            if (_cumVolume.TryGetValue(symbol, out long currentCumVol))
                rvoh = (decimal)currentCumVol / learnedAvg;
        }

        // Filter: Bull trend only and no "Flash Spikes" (RVOH > 4.5)
        if (trend == "BULL" && (double)rvoh < VOLATILITY_CAP)
        {
            _lastSellTimes.TryGetValue(symbol, out var lastSell);
            if ((DateTime.UtcNow - lastSell).TotalMinutes > 15)
            {
                ExecuteEntry(symbol);
            }
        }
    }
    private void ExecuteExitLogic(string symbol)
    {
        // 1. DATA SAFETY & INITIALIZATION
        if (!_priceHistory.TryGetValue(symbol, out var prices) || prices.Count < 2) return;
        if (!_positions.TryGetValue(symbol, out var pos)) return;

        // EMERGENCY SHORT-GUARD: If IBKR shows a negative position, our logic is broken.
        // We must close it immediately to prevent inverted math errors.
        if (pos.Quantity < 0)
        {
            SubmitOrder(symbol, (int)Math.Abs(pos.Quantity), prices.Last(), TradeSide.Buy, 0);
            _positions.Remove(symbol);
            return;
        }

        decimal currentPrice = prices.Last();
        decimal lastPrice = prices[prices.Count - 2];
        double rsi = CalculateRSI(symbol, 14);
        decimal netPnL = pos.UnrealizedPnL - roundTripFee;

        // 2. CHURN & SPREAD FILTERS
        _buyTimes.TryGetValue(symbol, out var buyTime);
        double minutesHeld = (DateTime.UtcNow - buyTime).TotalMinutes;

        // SPREAD FILTER: Ignore noise smaller than 0.1% (Prevents flicker-selling in high Vol-X)
        decimal tickChange = Math.Abs((currentPrice - lastPrice) / lastPrice);
        if (tickChange < 0.001m) return;

        // 3. TRAILING STOP & BREAK-EVEN PROTECTION
        double peak = _peakRsi.AddOrUpdate(symbol, rsi, (key, oldPeak) => Math.Max(oldPeak, rsi));

        // Break-Even Protection: Once up $10.00, move stop to Entry Price + Fees
        if (netPnL > 10.00m && pos.TrailingStop < pos.AvgPrice)
        {
            pos.TrailingStop = pos.AvgPrice + (roundTripFee / pos.Quantity);
        }

        // Moderate Trailing: Tighten stop to 0.4% offset once up $15.00
        if (netPnL > 15.00m)
        {
            decimal tightStop = currentPrice * 0.996m;
            if (tightStop > pos.TrailingStop) pos.TrailingStop = tightStop;
        }

        // 4. EXIT CONDITION DEFINITIONS
        // RSI Hook: Requires a 5.0 point drop from a high of at least 70
        bool rsiHooked = peak > 70 && rsi < (peak - 5.0);

        // RSI Overbought: Exit only if profit is meaningful ($15+)
        bool rsiOverbought = rsi >= 80 && netPnL > 15.00m;

        bool hitStop = currentPrice <= pos.TrailingStop;

        // Win Exit: Combine Hook/Overbought with a $8.00 minimum profit floor
        bool rsiWinExit = (rsiOverbought || rsiHooked) && netPnL > 8.00m;

        // 5. MODERATE EXECUTION
        if (hitStop || rsiWinExit)
        {
            // PROTECTION: No "Win Exits" before 5 minutes. (Hard Stop Loss always allowed)
            if (rsiWinExit && minutesHeld < 5.0) return;

            // Cast quantity to int to resolve IBKR fractional share warnings
            int qtyToSell = (int)Math.Abs(pos.Quantity);
            if (qtyToSell <= 0)
            {
                _positions.Remove(symbol);
                return;
            }

            SubmitOrder(symbol, qtyToSell, currentPrice, TradeSide.Sell, rsi);

            // CLEANUP
            _peakRsi.TryRemove(symbol, out _);
            _lastTradeWasLoss[symbol] = netPnL < 0; // Records for the Entry Cooldown
            _lastSellTimes[symbol] = DateTime.UtcNow;

            string reason = hitStop ? "STOP" : "RSI_STUBBORN";
            Task.Run(() => SendEmailNotification($"🔴 SELL ({reason}): {symbol}",
                $"Sold @ {currentPrice:C2} | Net PnL: {netPnL:C2} | RSI: {rsi:F1} | Hold: {minutesHeld:F1}m"));
        }
    }
    private void ResetDailyStats()
    {
        // Realized PnL and Flags
        _goalReached = false;
        _haltNewTrades = false;
        _dailyLossCount = 0;

        // Reset cumulative dictionaries without "Clearing" the keys
        foreach (var key in _cumVolume.Keys)
        {
            _cumVolume[key] = 0;
            _cumVwapProd[key] = 0;
        }

        // Trade history and active lists can still be cleared
        _tradesToday.Clear();
        _tradeHistory.Clear();

        Console.WriteLine("[SYSTEM] Daily stats reset successfully.");
    }

    private void ExecuteEntry(string symbol)
    {
        // 1. GLOBAL HALT & SAFETY FILTERS
        if (_haltNewTrades || _goalReached) return;

        // MODERATE: Consistent with your lossLimitPct, stop at 4 failed trades
        if (_dailyLossCount >= 4)
        {
            _haltNewTrades = true;
            return;
        }

        // Guard against specific symbols or Index ETFs
        if (symbol == "SQ" || symbol == "QQQ") return;

        // 2. POSITION & SHORT-GUARD
        // Ensure we don't buy if we have a "Ghost Short" or are already at Max Slots
        if (_positions.TryGetValue(symbol, out var existing))
        {
            // If IBKR says we are -10 shares, don't buy more until manually fixed
            if (existing.Quantity <= 0) return;
            return; // Already own it
        }

        if (_positions.Count >= MaxActivePositions) return;

        // 3. SAFE DATA RETRIEVAL
        if (!_priceHistory.TryGetValue(symbol, out var prices) || prices.Count < 20) return;

        decimal currentPrice;
        decimal ma20;
        lock (prices)
        {
            currentPrice = prices.Last();
            ma20 = (decimal)prices.Skip(prices.Count - 20).Average();
        }

        // 4. MODERATE STRATEGY FILTERS
        // Overextension: Lowered to 1.0% to prevent buying at the absolute peak
        if (currentPrice > (ma20 * 1.010m)) return;

        // Cooldown Check: Use the wasLoss logic to prevent "Revenge Trading"
        _lastSellTimes.TryGetValue(symbol, out var lastSell);
        _lastTradeWasLoss.TryGetValue(symbol, out bool wasLoss);
        int requiredCooldown = wasLoss ? 30 : 15;

        if ((DateTime.UtcNow - lastSell).TotalMinutes < requiredCooldown) return;

        // 5. QUANTITY CALCULATION (STRICT INTEGERS)
        // Budget is based on initialAccountValue / 3 slots
        decimal budgetPerSlot = initialAccountValue / MaxActivePositions;

        // Subtract $1.00 for the buy fee to ensure we don't over-leverage
        int qty = (int)Math.Floor((budgetPerSlot - 1.00m) / currentPrice);

        if (qty > 0)
        {
            // 6. ATOMIC EXECUTION
            // Note: SubmitOrder now receives the exact (int) qty to satisfy IBKR
            SubmitOrder(symbol, qty, currentPrice, TradeSide.Buy);

            // 7. INITIALIZE TRACKING
            double startingRsi = (double)CalculateRSI(symbol, 14);
            _peakRsi.AddOrUpdate(symbol, startingRsi, (key, val) => startingRsi);

            // Ensure EntryTime is set for the "Time Shield" in Exit Logic
            if (_positions.TryGetValue(symbol, out var pos))
            {
                pos.EntryTime = DateTime.UtcNow;
            }
            _buyTimes[symbol] = DateTime.UtcNow;

            Task.Run(() => SendEmailNotification(
                $"🚀 BUY: {symbol}",
                $"Bought {qty} @ {currentPrice:C2} | MA20: {ma20:C2} | Slot: {_positions.Count}/{MaxActivePositions}"
            ));
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
        if (!_priceHistory.ContainsKey(symbol) || _priceHistory[symbol].Count <= period) return 50;
        var prices = _priceHistory[symbol];
        decimal avgGain = 0, avgLoss = 0;
        for (int i = 1; i <= period; i++)
        {
            decimal diff = prices[i] - prices[i - 1];
            if (diff > 0) avgGain += diff; else avgLoss -= diff;
        }
        avgGain /= period; avgLoss /= period;
        for (int i = period + 1; i < prices.Count; i++)
        {
            decimal diff = prices[i] - prices[i - 1];
            avgGain = (avgGain * (period - 1) + (diff > 0 ? diff : 0)) / period;
            avgLoss = (avgLoss * (period - 1) + (diff < 0 ? -diff : 0)) / period;
        }
        return avgLoss == 0 ? 100 : 100 - (100 / (1 + (double)(avgGain / avgLoss)));
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
            if (qty != 0 && !_positions.ContainsKey(symbol))
            {
                _positions[symbol] = new SimPosition
                {
                    Quantity = qty,
                    AvgPrice = avgPrice,
                    CurrentPrice = avgPrice, // Defaulting to avg until a tick arrives
                    TrailingStop = avgPrice * 0.985m // 1.5% initial stop
                };
                _buyTimes[symbol] = DateTime.UtcNow;

                Console.WriteLine($"[SYNC] Linked existing TWS position: {symbol} | Qty: {qty} | Avg: {avgPrice:C2}");
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

}