using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
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
    public Dictionary<string, int> TradesPerSymbol { get; set; } = new();
    public Dictionary<string, DateTime> BuyTimes { get; set; } = new();
    // New: Store the price history list for each symbol
    public Dictionary<string, List<decimal>> PriceHistory { get; set; } = new();
}
public class Trade
{
    public string Symbol { get; set; }
    public TradeSide Action { get; set; }
    public decimal Price { get; set; }
    public decimal Quantity { get; set; }
    public DateTime Time { get; set; } = DateTime.UtcNow;
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

// Ensure PositionManager inherits everything properly
public class PositionManager : SimulatedBroker { }

public class SimulatedBroker : IBroker
{

    // --- RISK MANAGEMENT ---
    private const decimal riskPerTrade = 50m;            // Dollar amount to risk based on ATR
    private const decimal dailyLossLimit = -400m;        // Stop trading for the day if PnL hits this
    private const decimal dailyProfitGoal = 600m;        // Bank wins and stop if PnL hits this
    private const int maxTradesGlobal = 30;              // Total trades allowed per day
    private const int maxTradesPerSymbol = 5;            // Max entries per ticker per day
    private const decimal roundTripFee = 2.00m;          // Total commission cost (Buy + Sell)
    private const decimal slippagePct = 0.0005m;         // 0.05% expected slippage for market orders

    // --- TREND LOGIC (SMAs) ---
    private const int shortSmaPeriod = 9;                // Fast moving average (9 mins)
    private const int longSmaPeriod = 50;                // Slow moving average (50 mins)
    private const int atrPeriod = 14;                    // Lookback for volatility calculation

    // --- EXIT LOGIC ---
    private const decimal stopLossAtrMult = 1.5m;        // Trailing stop distance (ATR * 1.5)
    private const decimal profitTargetMult = 3.0m;       // Take profit at 3x the risk
    private const int maxMinutesInTrade = 30;



    protected readonly Dictionary<string, SimPosition> _positions = new();
    protected readonly Dictionary<string, List<decimal>> _priceHistory = new();
    protected readonly Dictionary<string, int> _tradesToday = new();
    protected readonly Dictionary<string, DateTime> _buyTimes = new();

    private DateTime _lastUpdateReceived = DateTime.UtcNow;
    private DateTime _lastHeartbeatLogged = DateTime.UtcNow;

    private const string SaveFilePath = "bot_state.json";
    private readonly object _lock = new object();

    // Strategy Parameters

    private decimal _totalRealizedPnL = 0m;
    private decimal _totalCommissions = 0m;
    private int _tradesExecutedToday = 0;
    private bool _haltNewTrades = false;
    private bool _qqqBullish = false;

    // --- Public Members requested by your Errors ---
    public IBroker RealBroker { get; set; }
    public IReadOnlyDictionary<string, SimPosition> Positions => _positions;
    public decimal TotalRealizedPnL => _totalRealizedPnL;

    public void MarkToMarket(Dictionary<string, decimal> prices) => OnPriceUpdate(prices);

    public void SyncExistingPosition(string symbol, decimal qty, decimal avgPrice)
    {
        lock (_lock)
        {
            if (!_positions.ContainsKey(symbol))
            {
                _positions[symbol] = new SimPosition
                {
                    Quantity = qty,
                    AvgPrice = avgPrice,
                    CurrentPrice = avgPrice,
                    TrailingStop = avgPrice - (CalculateTrueATR(symbol) > 0 ? CalculateTrueATR(symbol) * 1.5m : avgPrice * 0.02m)
                };
                _buyTimes[symbol] = DateTime.UtcNow;
            }
        }
    }

    public void ResetDailyTrades()
    {
        _tradesExecutedToday = 0;
        _totalRealizedPnL = 0m;
        _totalCommissions = 0m;
        _tradesToday.Clear();
        _buyTimes.Clear();
        _haltNewTrades = false;
        SaveState();
    }

    public void PrintDailySummary()
    {
        Console.WriteLine($"Net PnL: {_totalRealizedPnL:0.00}");
        foreach (var p in _positions)
            Console.WriteLine($"{p.Key}: Unrealized {p.Value.UnrealizedPnL:0.00}");
    }

    public void ArchiveDailyResults()
    {
        File.AppendAllText("trade_history_log.txt",
            $"{DateTime.Now:yyyy-MM-dd} Net: {_totalRealizedPnL:0.00}{Environment.NewLine}");
    }

    public void SendEmailSummary(string toEmail) { /* Implementation if needed */ }

    // Updated to handle the 'force' parameter and return the List<Trade> expected
    public List<Trade> CheckEndOfDayLiquidation(bool force = false)
    {
        var trades = new List<Trade>();
        var et = GetEasternTime();

        if (force || et.TimeOfDay > new TimeSpan(15, 45, 0))
        {
            foreach (var symbol in _positions.Keys.ToList())
            {
                var pos = _positions[symbol];
                trades.Add(new Trade { Symbol = symbol, Action = TradeSide.Sell, Price = pos.CurrentPrice, Quantity = pos.Quantity });
                SubmitOrder(symbol, (int)pos.Quantity, pos.CurrentPrice, TradeSide.Sell);
            }
            if (_positions.Count == 0 && !force) ArchiveDailyResults();
        }
        return trades;
    }

    // --- Core Logic ---

    private DateTime GetEasternTime() => TimeZoneInfo.ConvertTimeFromUtc(DateTime.UtcNow,
        TimeZoneInfo.FindSystemTimeZoneById("Eastern Standard Time"));

    public void OnPriceUpdate(Dictionary<string, decimal> prices)
    {
        lock (_lock)
        {
            _lastUpdateReceived = DateTime.UtcNow;

            foreach (var kv in prices)
            {
                string symbol = kv.Key;
                decimal price = kv.Value;

                // 1. Update the "Current Candle" or start a new one
                UpdateHistory(symbol, price);

                // 2. Need enough history to calculate SMAs
                if (!_priceHistory.ContainsKey(symbol) || _priceHistory[symbol].Count < longSmaPeriod)
                    continue;

                // 3. Update Market Context (QQQ is updated every second)
                if (symbol == "QQQ") _qqqBullish = IsTrendUp(symbol);

                // 4. Manage Position (Check stops EVERY SECOND)
                // Even if the minute hasn't ended, we check if the current price hit our stop.
                if (_positions.ContainsKey(symbol))
                    ManagePosition(symbol, price);

                // 5. Try Entry (Only on stable trends)
                if (!_haltNewTrades && !_positions.ContainsKey(symbol))
                {
                    if (_qqqBullish && IsTrendUp(symbol) && IsSlopeUp(symbol))
                        TryEnter(symbol, price);
                }
            }
            CheckEndOfDayLiquidation();
            CheckHealth();
        }
    }

    /// <summary>
    /// Logs a status update every 5 minutes and warns if the data feed is stale.
    /// </summary>
    public void CheckHealth()
    {
        var now = DateTime.UtcNow;

        // 1. Log Heartbeat every 5 minutes so you know it's alive
        if ((now - _lastHeartbeatLogged).TotalMinutes >= 5)
        {
            Console.WriteLine($"[HEARTBEAT] {GetEasternTime():HH:mm:ss} | Net PnL: {_totalRealizedPnL:C2} | Active Trades: {_positions.Count}");
            _lastHeartbeatLogged = now;
        }

        // 2. Alert if no price data has been received for more than 60 seconds
        if ((now - _lastUpdateReceived).TotalSeconds > 60 && IsMarketOpen())
        {
            Console.ForegroundColor = ConsoleColor.Red;
            Console.WriteLine($"[WARNING] DATA FEED STALE. No updates for {(now - _lastUpdateReceived).TotalSeconds:0} seconds!");
            Console.ResetColor();
        }
    }
    private bool IsMarketOpen()
    {
        var et = GetEasternTime().TimeOfDay;
        return et >= new TimeSpan(9, 30, 0) && et <= new TimeSpan(16, 0, 0);
    }
    private int _lastSavedMinute = -1;
    private void UpdateHistory(string symbol, decimal price)
    {
        if (!_priceHistory.ContainsKey(symbol)) _priceHistory[symbol] = new List<decimal>();

        int currentMinute = DateTime.UtcNow.Minute;

        if (currentMinute != _lastSavedMinute)
        {
            // New minute started: Add a new data point
            _priceHistory[symbol].Add(price);
            _lastSavedMinute = currentMinute;
            if (_priceHistory[symbol].Count > 200) _priceHistory[symbol].RemoveAt(0);
        }
        else
        {
            // Still in the same minute: Update the last point with the latest 1-second price
            // This keeps our SMA "live"
            if (_priceHistory[symbol].Count > 0)
                _priceHistory[symbol][_priceHistory[symbol].Count - 1] = price;
        }
    }
    private bool IsTrendUp(string symbol)
    {
        var h = _priceHistory[symbol];
        return h.TakeLast(shortSmaPeriod).Average() > h.TakeLast(longSmaPeriod).Average();
    }

    private bool IsSlopeUp(string symbol)
    {
        var h = _priceHistory[symbol];
        decimal currentSma = h.TakeLast(shortSmaPeriod).Average();
        decimal prevSma = h.Skip(h.Count - shortSmaPeriod - 1).Take(shortSmaPeriod).Average();
        return currentSma > prevSma;
    }

    private void TryEnter(string symbol, decimal price)
    {
        if (_tradesExecutedToday >= maxTradesGlobal) return;
        if (_tradesToday.GetValueOrDefault(symbol) >= maxTradesPerSymbol) return;

        decimal atr = CalculateTrueATR(symbol);
        if (atr <= 0) return;

        int qty = (int)Math.Floor(riskPerTrade / atr);
        if (qty <= 0) return;

        SubmitOrder(symbol, qty, price, TradeSide.Buy);
    }

    // 1. Add this constant to your Strategy Parameters section
    private const decimal profitTargetMultiplier = 3.0m; // Aim for 3x the risk (e.g., Risk $50, Target $150)

    // 2. Updated ManagePosition Method
    private void ManagePosition(string symbol, decimal price)
    {
        var pos = _positions[symbol];
        pos.CurrentPrice = price;

        decimal atr = CalculateTrueATR(symbol);

        // Calculate the dollar risk taken at entry
        // (If ATR was $1.00 and we risked $50, our "R" unit is $50)
        decimal initialRiskPerShare = pos.AvgPrice - (pos.AvgPrice - (atr * 1.5m));
        decimal profitTargetPrice = pos.AvgPrice + (atr * 1.5m * profitTargetMultiplier);

        // --- EXIT CONDITION 1: Trailing Stop ---
        decimal newStop = price - (atr * 1.5m);
        if (newStop > pos.TrailingStop)
            pos.TrailingStop = newStop;

        bool hitStop = price <= pos.TrailingStop;

        // --- EXIT CONDITION 2: Profit Target (Take Profit) ---
        bool hitTarget = price >= profitTargetPrice;

        // --- EXIT CONDITION 3: Time Expiry ---
        bool timeExpired = (DateTime.UtcNow - _buyTimes[symbol]).TotalMinutes > maxMinutesInTrade;

        if (hitStop)
        {
            Console.WriteLine($"[EXIT] {symbol} stopped out for protection.");
            SubmitOrder(symbol, (int)pos.Quantity, price, TradeSide.Sell);
        }
        else if (hitTarget)
        {
            Console.WriteLine($"[EXIT] {symbol} hit Profit Target! Nice win.");
            SubmitOrder(symbol, (int)pos.Quantity, price, TradeSide.Sell);
        }
        else if (timeExpired)
        {
            Console.WriteLine($"[EXIT] {symbol} time limit reached. Closing position.");
            SubmitOrder(symbol, (int)pos.Quantity, price, TradeSide.Sell);
        }
    }

    private decimal CalculateTrueATR(string symbol)
    {
        if (!_priceHistory.ContainsKey(symbol)) return 0;
        var h = _priceHistory[symbol];
        if (h.Count < atrPeriod + 1) return 0;

        decimal sumTR = 0;
        for (int i = h.Count - atrPeriod; i < h.Count; i++)
            sumTR += Math.Abs(h[i] - h[i - 1]);

        return sumTR / atrPeriod;
    }

    public void SubmitOrder(string symbol, int qty, decimal price, TradeSide side)
    {
        lock (_lock)
        {
            decimal executionPrice = side == TradeSide.Buy ? price * (1 + slippagePct) : price * (1 - slippagePct);

            if (side == TradeSide.Buy)
            {
                _positions[symbol] = new SimPosition
                {
                    AvgPrice = executionPrice,
                    Quantity = qty,
                    CurrentPrice = executionPrice,
                    TrailingStop = executionPrice - (CalculateTrueATR(symbol) > 0 ? CalculateTrueATR(symbol) * 1.5m : executionPrice * 0.02m)
                };
                _buyTimes[symbol] = DateTime.UtcNow;
                _tradesExecutedToday++;
                _tradesToday[symbol] = _tradesToday.GetValueOrDefault(symbol) + 1;
            }
            else if (_positions.TryGetValue(symbol, out var pos))
            {
                decimal pnl = (executionPrice - pos.AvgPrice) * pos.Quantity;
                _totalCommissions += roundTripFee;
                _totalRealizedPnL += (pnl - roundTripFee);

                _positions.Remove(symbol);
                _buyTimes.Remove(symbol);

                // --- ADD THE LIMIT CHECK HERE ---
                if (_totalRealizedPnL <= dailyLossLimit)
                {
                    _haltNewTrades = true;
                    Console.WriteLine($"[STOP LOSS] Daily loss limit hit: {_totalRealizedPnL:C2}. New trades halted.");
                }
                else if (_totalRealizedPnL >= dailyProfitGoal) // You'll need to define this constant
                {
                    _haltNewTrades = true;
                    Console.WriteLine($"[PROFIT GOAL] Daily goal reached: {_totalRealizedPnL:C2}. Banking wins for the day!");
                }
            }
            SaveState();
        }
    }
    public void PrintStartupConfiguration()
    {
        Console.Clear();
        Console.ForegroundColor = ConsoleColor.Cyan;
        Console.WriteLine("====================================================");
        Console.WriteLine("                 TRADING BOT - INITIALIZED          ");
        Console.WriteLine("====================================================");
        Console.ResetColor();

        Console.WriteLine($"[TIME] Local: {DateTime.Now:HH:mm:ss} | ET: {GetEasternTime():HH:mm:ss}");
        Console.WriteLine($"[LIMITS] Goal: {dailyProfitGoal:C2} | Max Loss: {dailyLossLimit:C2}");
        Console.WriteLine($"[RISK] Per Trade: {riskPerTrade:C2} | Max Trades/Day: {maxTradesGlobal}");
        Console.WriteLine($"[STRATEGY] SMA: {shortSmaPeriod}/{longSmaPeriod} | ATR Stop: 1.5x");

        if (_haltNewTrades)
        {
            Console.ForegroundColor = ConsoleColor.Yellow;
            Console.WriteLine("[STATUS] HALTED: Daily limit previously reached.");
        }
        else
        {
            Console.ForegroundColor = ConsoleColor.Green;
            Console.WriteLine("[STATUS] ACTIVE: Scanning for entries...");
        }
        Console.ResetColor();

        Console.WriteLine("----------------------------------------------------");
        Console.WriteLine($"[CURRENT PNL] Total: {_totalRealizedPnL:C2} | Fees: {_totalCommissions:C2}");
        Console.WriteLine($"[ACTIVE POSITIONS] Count: {_positions.Count}");
        foreach (var pos in _positions)
        {
            Console.WriteLine($" -> {pos.Key}: {pos.Value.Quantity} @ {pos.Value.AvgPrice:C2}");
        }
        Console.WriteLine("====================================================");
    }
    public void SaveState()
    {
        lock (_lock)
        {
            var data = new BotPersistData
            {
                Positions = _positions,
                TradesExecutedToday = _tradesExecutedToday,
                RealizedPnLTotal = _totalRealizedPnL,
                TotalCommissions = _totalCommissions,
                TradesPerSymbol = _tradesToday,
                BuyTimes = _buyTimes,
                PriceHistory = _priceHistory // Save the memory
            };

            string json = JsonSerializer.Serialize(data, new JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(SaveFilePath, json);
        }
    }

    // 3. Update the LoadState Method
    public void LoadState()
    {
        if (!File.Exists(SaveFilePath)) return;

        try
        {
            var data = JsonSerializer.Deserialize<BotPersistData>(File.ReadAllText(SaveFilePath));
            if (data == null) return;

            _totalRealizedPnL = data.RealizedPnLTotal;
            _totalCommissions = data.TotalCommissions;
            _tradesExecutedToday = data.TradesExecutedToday;

            // Restore Dictionaries
            _positions.Clear();
            foreach (var kv in data.Positions) _positions[kv.Key] = kv.Value;

            _tradesToday.Clear();
            foreach (var kv in data.TradesPerSymbol) _tradesToday[kv.Key] = kv.Value;

            _buyTimes.Clear();
            foreach (var kv in data.BuyTimes) _buyTimes[kv.Key] = kv.Value;

            // Restore Price History so indicators work immediately
            _priceHistory.Clear();
            if (data.PriceHistory != null)
            {
                foreach (var kv in data.PriceHistory) _priceHistory[kv.Key] = kv.Value;
            }

            if (_totalRealizedPnL <= dailyLossLimit) _haltNewTrades = true;

            Console.WriteLine("State loaded successfully. Memory restored.");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Error loading state: {ex.Message}");
        }
    }
}