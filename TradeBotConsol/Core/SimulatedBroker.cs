using System;
using System.Collections.Concurrent;
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

public class SimPosition
{
    public decimal Quantity { get; set; }
    public decimal AvgPrice { get; set; }
    public decimal TrailingStop { get; set; }
    public decimal CurrentPrice { get; set; }
    public decimal RealizedPnL { get; set; }
    public decimal UnrealizedPnL => Quantity * (CurrentPrice - AvgPrice);
    public bool IsBreakEvenProtected { get; set; } // NEW: Tracks if stop is at entry
}

public class BotPersistData
{
    public Dictionary<string, SimPosition> Positions { get; set; } = new();
    public int TradesExecutedToday { get; set; }
    public int DailyLossCount { get; set; } // ADDED THIS
    public decimal RealizedPnLTotal { get; set; }
    public decimal StartingDayEquity { get; set; }
    public Dictionary<string, int> TradesPerSymbol { get; set; } = new();
    public Dictionary<string, DateTime> BuyTimes { get; set; } = new();

public ConcurrentDictionary<string, List<decimal>> PriceHistory { get; set; } = new();
public ConcurrentDictionary<string, List<long>> VolumeHistory { get; set; } = new();
public Dictionary<string, DateTime> LastSellTimes { get; set; } = new();
    public Dictionary<string, bool> LastTradeWasLoss { get; set; } = new();
}

public class PositionManager : SimulatedBroker { }

public class SimulatedBroker : IBroker
{
    private const decimal initialAccountValue = 4000m;
    private const decimal dailyProfitGoalPct = 0.03m;
    private const decimal dailyLossLimitPct = 0.02m;
    private const decimal maxTradeLossPct = 0.015m;
    private const int maxTradesGlobal = 30;
    private const decimal roundTripFee = 2.00m; // Updated to $1 buy + $1 sell
    private const decimal slippagePct = 0.0001m;
    private const int MaxActivePositions = 4; // NEW: Slot limit

    // Inside SimulatedBroker Class - Added Variable
    protected int _dailyLossCount = 0;

    public readonly string[] _tradeableStars = { 
    // The Core Momentum Kings
    "NVDA", "TSLA", "PLTR", "AMD", "META", "AMZN", "NFLX", "GOOGL",
    
    // High-Volatility Tech & Crypto Proxies
    "COIN", "MSTR", "ARM", "SMCI", "SHOP", "SNOW", 
    
    // Technical Respect (Clean VWAP/RSI performers)
    "MU", "AVGO", "PANW", "UBER", "DKNG", "SQ", "PYPL", "ON",
    
    // High-Relative Volume Growth
    "RKLB", "MARA", "DKNG"
};

    public ConcurrentDictionary<string, List<decimal>> _priceHistory { get; set; } = new();
    public ConcurrentDictionary<string, List<long>> _volumeHistory { get; set; } = new();

    private ConcurrentDictionary<string, long> _cumVolume = new();
    private ConcurrentDictionary<string, decimal> _cumVwapProd = new();
    private ConcurrentDictionary<string, DateTime> _symbolLastResetDate = new();
    protected readonly Dictionary<string, SimPosition> _positions = new();

    protected readonly Dictionary<string, int> _tradesToday = new();
    protected readonly Dictionary<string, DateTime> _buyTimes = new();
    protected readonly Dictionary<string, DateTime> _lastSellTimes = new();


    private readonly HashSet<string> _pendingOrders = new();
    private readonly TimeSpan _latestEntryTime = new TimeSpan(15, 30, 0);
    private DateTime _lastHeartbeatEmail = DateTime.MinValue;
    private const string SaveFilePath = "bot_state.json";
    protected readonly object _lock = new object();

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
        if (symbol == "QQQ") return;
        if (_haltNewTrades || _goalReached) return;

        lock (_lock)
        {
            if (!_priceHistory.ContainsKey(symbol) || _priceHistory[symbol].Count < 20) return;

            var prices = _priceHistory[symbol];
            var volumes = _volumeHistory[symbol];
            decimal currentPrice = prices.Last();
            long currentVolume = volumes.Last();
            double rsi = CalculateRSI(symbol, 14);
            string trend = GetTrend(prices);

            // --- 1. SYNTHETIC SPREAD CHECK ---
            decimal lastPrice = prices[prices.Count - 2];
            decimal priceChangePct = Math.Abs((currentPrice - lastPrice) / lastPrice) * 100;
            bool spreadIsTight = priceChangePct <= 0.12m;

            // --- 2. VWAP & VOLUME ANALYSIS ---
            decimal vwap = _cumVolume.ContainsKey(symbol) && _cumVolume[symbol] > 0
                ? _cumVwapProd[symbol] / _cumVolume[symbol] : currentPrice;
            decimal avgVolume = volumes.Count >= 10 ? (decimal)volumes.Skip(volumes.Count - 10).Average() : 0;
            bool volumeSurge = avgVolume > 0 && currentVolume > (avgVolume * 1.2m);
            bool volumeClimax = avgVolume > 0 && currentVolume > (avgVolume * 5.0m);

            bool hasPosition = _positions.ContainsKey(symbol);

            // --- ENTRY LOGIC ---
            if (!hasPosition && _positions.Count < MaxActivePositions)
            {
                DateTime nyNow = GetEasternTime();
                bool marketOpenTime = nyNow.TimeOfDay >= new TimeSpan(10, 0, 0) && nyNow.TimeOfDay < _latestEntryTime;
                bool underLossLimit = _dailyLossCount < 4;

                // --- COOLDOWN LOGIC ---
                _lastSellTimes.TryGetValue(symbol, out var lastSell);
                double minutesSinceLastTrade = (DateTime.UtcNow - lastSell).TotalMinutes;

                _lastTradeWasLoss.TryGetValue(symbol, out bool wasLoss);
                // Standard cooldown is 15 mins, but if it was a loss, we wait 30 mins (Revenge Trade Shield)
                int requiredCooldown = wasLoss ? 30 : 15;
                bool inCooldown = minutesSinceLastTrade < requiredCooldown;

                decimal ma20 = (decimal)prices.Skip(prices.Count - 20).Average();
                bool isOverextended = currentPrice > (ma20 * 1.012m);

                if (marketOpenTime && underLossLimit && !inCooldown && IsMarketSafe())
                {
                    if (trend == "BULL" && rsi > 45 && rsi < 65 && currentPrice > vwap && volumeSurge &&
                        !isOverextended && !volumeClimax && spreadIsTight)
                    {
                        decimal targetSpend = 1000m;
                        int qty = (int)Math.Floor((targetSpend - 1.00m) / currentPrice);
                        if (qty > 0)
                        {
                            _peakRsi[symbol] = rsi;
                            SubmitOrder(symbol, qty, currentPrice, TradeSide.Buy);
                            Task.Run(() => SendEmailNotification($"🟢 SMART BUY: {symbol}",
                                $"Bought {qty} @ {currentPrice:C2}\nTrend: {trend}\nRSI: {rsi:F1}"));
                        }
                    }
                }
            }
            // --- EXIT LOGIC ---
            else if (hasPosition)
            {
                var pos = _positions[symbol];
                pos.CurrentPrice = currentPrice;
                decimal netPnL = pos.UnrealizedPnL - roundTripFee;
                _buyTimes.TryGetValue(symbol, out var buyTime);
                bool timeShieldActive = (DateTime.UtcNow - buyTime).TotalMinutes < 15;

                if (!_peakRsi.ContainsKey(symbol) || rsi > _peakRsi[symbol]) _peakRsi[symbol] = rsi;

                // VOLATILITY STOP: Tighten stop to 0.4% once up $6.00
                if (netPnL > 6.00m)
                {
                    decimal tightStop = currentPrice * 0.996m;
                    if (tightStop > pos.TrailingStop) pos.TrailingStop = tightStop;
                    pos.IsBreakEvenProtected = true;
                }

                double peak = _peakRsi[symbol];
                bool rsiHooked = peak > 75 && rsi < (peak - 3.0);
                bool climaxExit = volumeClimax && netPnL > 2.00m;
                bool hitStop = currentPrice <= pos.TrailingStop;
                bool rsiWinExit = (rsi >= 80 && netPnL > 3.00m) || rsiHooked || (rsi >= 72 && trend == "FLAT" && netPnL > 1.50m);

                if (hitStop || rsiWinExit || climaxExit)
                {
                    if (hitStop && netPnL < 0 && timeShieldActive) return;

                    // Track if this exit is a loss for the next cooldown
                    _lastTradeWasLoss[symbol] = netPnL < 0;

                    SubmitOrder(symbol, (int)pos.Quantity, currentPrice, TradeSide.Sell);
                    _peakRsi.TryRemove(symbol, out _);
                    string reason = hitStop ? "STOP" : (climaxExit ? "CLIMAX" : "RSI_HOOK");
                    Task.Run(() => SendEmailNotification($"🔴 SMART SELL ({reason}): {symbol}",
                        $"Sold @ {currentPrice:C2}\nNet PnL: {netPnL:C2}\nCooldown Set: {(netPnL < 0 ? "30m" : "15m")}"));
                }
            }
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
            if (_goalReached) return;

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
                _haltNewTrades = true;
                LiquidateAll("Daily Loss Limit Hit");
            }
        }
    }

    public void LiquidateAll(string reason)
    {
        Console.WriteLine($"\n[SYSTEM] !!! {reason.ToUpper()} !!! Liquidating all positions.");
        foreach (var sym in _positions.Keys.ToList())
        {
            SubmitOrder(sym, (int)_positions[sym].Quantity, _positions[sym].CurrentPrice, TradeSide.Sell);
        }
    }

    public void SubmitOrder(string symbol, int qty, decimal price, TradeSide side)
    {
        lock (_lock)
        {
            if (_pendingOrders.Contains(symbol)) return;
            _pendingOrders.Add(symbol);
        }

        if (RealBroker != null) RealBroker.SubmitOrder(symbol, qty, price, side);

        lock (_lock)
        {
            if (side == TradeSide.Sell && _positions.TryGetValue(symbol, out var pos))
            {
                // Calculate actual fill with slippage
                decimal execPrice = price * (1 - slippagePct);
                decimal pnl = (execPrice - pos.AvgPrice) * pos.Quantity - roundTripFee;

                // 1. Set the Revenge Shield State
                bool isLoss = pnl < 0;
                _lastTradeWasLoss[symbol] = isLoss;
                if (isLoss) _dailyLossCount++;

                // 2. Clear Tracking
                _totalRealizedPnL += pnl;
                _tradeHistory.Add(new ClosedTrade { Symbol = symbol, Profit = pnl, ExitTime = DateTime.UtcNow });
                _positions.Remove(symbol);
                _peakRsi.TryRemove(symbol, out _);
                _lastSellTimes[symbol] = DateTime.UtcNow;

                // 3. Global Halt Check
                if (_dailyLossCount >= 4) _haltNewTrades = true;
            }
            else if (side == TradeSide.Buy)
            {
                _tradesExecutedToday++;
                _positions[symbol] = new SimPosition
                {
                    Quantity = qty,
                    AvgPrice = price,
                    CurrentPrice = price,
                    TrailingStop = price * (1 - maxTradeLossPct)
                };
                _buyTimes[symbol] = DateTime.UtcNow;
                _peakRsi[symbol] = CalculateRSI(symbol, 14);
            }
        }
        SaveState();

        // Release the lock on this symbol after a delay to prevent "double-tap" orders
        Task.Delay(3000).ContinueWith(_ => { lock (_lock) { _pendingOrders.Remove(symbol); } });
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
        if (!_priceHistory.TryGetValue(symbol, out var prices) || prices.Count < 50) return;
        if (!_volumeHistory.TryGetValue(symbol, out var volumes) || volumes.Count < 10) return;

        decimal currentPrice = prices.Last();

        // 1. Calculate Indicators
        decimal rsi = (decimal)CalculateRSI(symbol, 14); // Cast double to decimal
        string trend = GetTrend(prices);

        // 2. Fix the Double-to-Decimal conversion error here
        // We cast the Average() result (double) to (decimal)
        decimal avgVol = (decimal)volumes.Skip(volumes.Count - 10).Take(9).Average();
        decimal volX = avgVol > 0 ? (decimal)volumes.Last() / avgVol : 0;

        decimal rvoh = 0;
        if (_learnedAvgVolume.TryGetValue(symbol, out decimal learnedAvg) && learnedAvg > 0)
        {
            _cumVolume.TryGetValue(symbol, out long currentCumVol);
            rvoh = (decimal)currentCumVol / learnedAvg;
        }

        // 3. Entry Logic
        if (trend == "BULL" && rsi < 40 && volX > 1.5m)
        {
            _lastSellTimes.TryGetValue(symbol, out var lastSell);
            if ((DateTime.UtcNow - lastSell).TotalMinutes > 15)
            {
                // FIX: Removed the 'currentPrice' argument to match your method signature
                ExecuteEntry(symbol);
            }
        }
    }
    private void ExecuteExitLogic(string symbol)
    {
        // 1. SAFE DATA RETRIEVAL
        // Use the underscore version to match the new declaration
        if (!_priceHistory.TryGetValue(symbol, out var prices) || prices.Count == 0) return;
        if (!_volumeHistory.TryGetValue(symbol, out var volumes) || volumes.Count == 0) return;
        if (!_positions.TryGetValue(symbol, out var pos)) return; // Position might have been closed already

        decimal currentPrice = prices.Last();
        long currentVolume = volumes.Last();
        double rsi = (double)CalculateRSI(symbol, 14); // Cast if your RSI returns decimal
        string trend = GetTrend(prices);
        double peak = _peakRsi.AddOrUpdate(symbol, rsi, (key, oldPeak) => Math.Max(oldPeak, rsi));
        pos.CurrentPrice = currentPrice;
        decimal netPnL = pos.UnrealizedPnL - roundTripFee;

        _buyTimes.TryGetValue(symbol, out var buyTime);
        bool timeShieldActive = (DateTime.UtcNow - buyTime).TotalMinutes < 15;


        // 3. VOLATILITY STOP: Tighten to 0.4% once in profit
        if (netPnL > 6.00m)
        {
            decimal tightStop = currentPrice * 0.996m;
            if (tightStop > pos.TrailingStop) pos.TrailingStop = tightStop;
            pos.IsBreakEvenProtected = true;
        }

        // 4. DEFINE EXIT TRIGGERS
        bool rsiHooked = peak > 75 && rsi < (peak - 3.0);

        decimal avgVolume = 0;
        lock (volumes) // Lock the list while calculating average
        {
            if (volumes.Count >= 10)
                avgVolume = (decimal)volumes.Skip(volumes.Count - 10).Average();
        }

        bool volumeClimax = avgVolume > 0 && currentVolume > (avgVolume * 5.0m);
        bool hitStop = currentPrice <= pos.TrailingStop;
        bool rsiWinExit = (rsi >= 80 && netPnL > 3.00m) || rsiHooked || (rsi >= 72 && trend == "FLAT" && netPnL > 1.50m);

        // 5. EXECUTE EXIT
        if (hitStop || rsiWinExit || volumeClimax)
        {
            // Time shield: Don't let a stop-loss fire in the first 15 mins if it's a minor dip
            if (hitStop && netPnL < 0 && timeShieldActive) return;

            string reason = hitStop ? "STOP" : (volumeClimax ? "CLIMAX" : "RSI_WIN");

            // FIX: Cast quantity to (int) to stop the "Fractional Share" API warning
            int qtyToSell = (int)pos.Quantity;

            SubmitOrder(symbol, qtyToSell, currentPrice, TradeSide.Sell);

            // CLEANUP: Remove the peak RSI tracking so it's fresh for the next trade
            _peakRsi.TryRemove(symbol, out _);

            Task.Run(() => SendEmailNotification($"🔴 SELL ({reason}): {symbol}",
                $"Sold @ {currentPrice:C2} | Net PnL: {netPnL:C2}"));
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
        var prices = _priceHistory[symbol];
        decimal currentPrice = prices.Last();

        // Final Overextension Check
        decimal ma20 = (decimal)prices.Skip(prices.Count - 20).Average();
        if (currentPrice > (ma20 * 1.012m)) return;

        decimal targetSpend = 1000m;
        int qty = (int)Math.Floor((targetSpend - 1.00m) / currentPrice);

        if (qty > 0)
        {
            SubmitOrder(symbol, qty, currentPrice, TradeSide.Buy);
            _peakRsi[symbol] = CalculateRSI(symbol, 14);
            Task.Run(() => SendEmailNotification($"🚀 PRIORITY BUY: {symbol}", $"Ranked as top volume runner. Bought {qty} @ {currentPrice:C2}"));
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

    private string GetTrend(IList<decimal> prices)
    {
        int fastPeriod = 20;
        int slowPeriod = 50;
        if (prices.Count < slowPeriod) return "WAIT";

        decimal fastMA = prices.Skip(prices.Count - fastPeriod).Average();
        decimal slowMA = prices.Skip(prices.Count - slowPeriod).Average();
        decimal current = prices.Last();

        // Bulls: Price > Fast > Slow
        if (current > fastMA * 1.001m && fastMA > slowMA) return "BULL";
        // Bears: Price < Fast < Slow
        if (current < fastMA * 0.999m && fastMA < slowMA) return "BEAR";

        return "FLAT";
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