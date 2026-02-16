using IBApi;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Mail;
using System.Reflection.Metadata;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;

// --- DATA STRUCTURES ---
public class Candle
{
    public DateTime Time { get; set; }
    public decimal Open { get; set; }
    public decimal High { get; set; }
    public decimal Low { get; set; }
    public decimal Close { get; set; }
    public long Volume { get; set; }
}

public interface IBroker
{
    void SubmitOrder(string symbol, int qty, decimal price, TradeSide side, double currentRsi = 0, string orderType = "LMT");
    void RequestHistoricalData(string symbol);
}

public enum TradeSide { Buy, Sell }

public class SimPosition
{
    public string Symbol { get; set; }
    public int Quantity { get; set; }
    public decimal AvgPrice { get; set; }
    public decimal TrailingStop { get; set; }
    public decimal HighWaterMark { get; set; }
    public decimal CurrentPrice { get; set; }
    public DateTime EntryTime { get; set; } = TimeZoneInfo.ConvertTimeFromUtc(DateTime.UtcNow, TimeZoneInfo.FindSystemTimeZoneById("Pacific Standard Time"));
    public decimal UnrealizedPnL(decimal price) => Quantity * (price - AvgPrice);
}

public class TrackedOrder
{
    public int OrderId;
    public string Symbol;
    public TradeSide Side;
    public int Qty;
}

public class BotPersistData
{
    public Dictionary<string, SimPosition> Positions { get; set; } = new();
    public decimal TotalPnL { get; set; }
}

public class SimulatedBroker
{
    public IBroker RealBroker { get; set; }
    private readonly object _lock = new object();

    // --- YOUR PREDEFINED RULES ---
    private const decimal TOTAL_BUDGET = 4000m;    // Total Capital
    private const int MAX_POSITIONS = 2;           // Max 2 stocks at once
    private const decimal POSITION_SIZE = 2000m;   // $2,000 per trade
    private const int MIN_HOLD_SECONDS = 300;      // 5-minute minimum hold (No flip-flopping)
    private const decimal DAILY_PROFIT_GOAL = 300m;
    private const decimal MAX_DAILY_LOSS = -150m;
    private const int COOLDOWN_SECONDS = 600;      // 10 mins before re-entering same symbol

    // --- STATE ---
    public readonly ConcurrentDictionary<string, List<Candle>> _marketData = new();
    private Dictionary<string, SimPosition> _positions = new();
    private readonly ConcurrentDictionary<int, TrackedOrder> _ordersById = new();
    private readonly List<string> _tradeHistoryLog = new();
    private readonly Dictionary<string, DateTime> _lastTradeTime = new();
    private const decimal ATR_TRAIL_MULT = 2.0m;   // 2x ATR is standard for scalping

    private decimal _totalRealizedPnL = 0m;
    private int _tradesToday = 0;
    private bool _haltTrading = false;
    private bool _eodSent = false;
    private readonly ConcurrentDictionary<string, decimal> _latestTick = new();
    private const decimal SLIPPAGE_PCT = 0.0015m; // 0.15%

    // --- EMAIL SETTINGS (FILL THESE IN) ---
    private const string EmailFrom = "uygargunay@gmail.com";
    private const string EmailTo = "uygargunay@gmail.com";
    private const string EmailPassword = "sznd kafk nhec skqh";

    public readonly string[] _watchlist =
 {
    // Index / ETFs
    "SPY","QQQ","IWM","DIA","SMH","XLK","XLF","XLE","ARKK",

    // Mega Tech
    "AAPL","MSFT","AMZN","GOOGL","META","NVDA","TSLA","AMD","INTC","ORCL","IBM",

    // AI / Semis
    "TSM","ASML","AVGO","QCOM","TXN","MU","ARM","LRCX","AMAT","KLAC","SMCI",

    // Cloud / SaaS
    "CRM","NOW","SNOW","MDB","DDOG","NET","OKTA","ZS","CRWD","PANW","PLTR",

    // Consumer / Growth
    "NFLX","DIS","UBER","LYFT","ABNB","BKNG","RBLX","SHOP","ETSY","PINS",

    // Finance / Crypto
    "COIN","MSTR","SQ","PYPL","HOOD","SOFI","JPM","BAC","GS","MS",

    // EV / Energy
    "NIO","RIVN","LCID","ENPH","SEDG","FSLR","PLUG","CHPT",

    // Healthcare / Biotech
    "JNJ","PFE","MRNA","LLY","ABBV","UNH","VRTX","REGN",

    // Industrials
    "BA","CAT","DE","GE","HON","RTX","LMT","NOC"
};



    private static readonly TimeZoneInfo Pacific = TimeZoneInfo.FindSystemTimeZoneById("Pacific Standard Time");
    private DateTime _lastMemorySave = DateTime.MinValue;

    // --- MARKET DATA INGESTION ---
    public void UpdateLiveTick(string symbol, decimal price, long size)
    {
        try
        {
            if (_haltTrading) return;

            lock (_lock)
            {
                if (_positions.TryGetValue(symbol, out var pos))
                    pos.CurrentPrice = price;
            }

            ManageCandles(symbol, price, size);
            CheckExits(symbol, price);
            if ((DateTime.UtcNow - _lastMemorySave).TotalMinutes >= 1)
            {
                SaveMarketMemory();
                _lastMemorySave = DateTime.UtcNow;
            }

        }
        catch (Exception ex)
        {
            Console.WriteLine($"[UpdateLiveTick ERROR] {symbol} -> {ex.Message}");
        }
    }


    public void AddCandle(string symbol, Candle candle)
    {
        if (!_marketData.ContainsKey(symbol))
            _marketData[symbol] = new List<Candle>();

        var candles = _marketData[symbol];

        // Avoid duplicates (same timestamp)
        if (candles.Count == 0 || candles.Last().Time != candle.Time)
            candles.Add(candle);

        // Optional: keep last N candles to limit memory
        if (candles.Count > 500)
            candles.RemoveAt(0);
    }

    private void ManageCandles(string symbol, decimal price, long size)
    {
        var candles = _marketData.GetOrAdd(symbol, _ => new List<Candle>());
        var now = TimeZoneInfo.ConvertTimeFromUtc(DateTime.UtcNow, Pacific);

        // UPDATE: Define a 5-minute rolling window (300 seconds)
        var windowStart = now.AddSeconds(-300);

        lock (candles)
        {
            // 1. Add the new tick as a micro-candle or update the tail
            if (candles.Count == 0 || (now - candles.Last().Time).TotalSeconds >= 1)
            {
                candles.Add(new Candle
                {
                    Time = now,
                    Open = price,
                    High = price,
                    Low = price,
                    Close = price,
                    Volume = size
                });
            }
            else
            {
                var current = candles.Last();
                current.High = Math.Max(current.High, price);
                current.Low = Math.Min(current.Low, price);
                current.Close = price;
                current.Volume += size;
            }

            // 2. Remove data older than 5 minutes to keep indicators "Rolling"
            candles.RemoveAll(c => c.Time < windowStart);

            // 3. Only execute strategy if we have a full 5 minutes of data
            if (candles.Count > 0 && (candles.Last().Time - candles.First().Time).TotalSeconds >= 290)
            {
                ExecuteStrategy(symbol);
            }
        }
    }


    // --- FULL EXECUTE STRATEGY ---
    public void ExecuteStrategy(string symbol)
    {
        if (_haltTrading) return;
        if (!_marketData.TryGetValue(symbol, out var candles) || candles.Count == 0) return;
        long todayVolume = candles
       .Where(c => c.Time.Date == TimeZoneInfo.ConvertTimeFromUtc(DateTime.UtcNow, Pacific).Date)
       .Sum(c => c.Volume);
        if (todayVolume < 1_000_000) return;

        lock (_lock)
        {
            // Already in a position or max positions reached
            if (_positions.ContainsKey(symbol) || _positions.Count >= MAX_POSITIONS) return;

            // Cooldown check
            if (_lastTradeTime.TryGetValue(symbol, out var lastTime))
                if ((TimeZoneInfo.ConvertTimeFromUtc(DateTime.UtcNow, Pacific) - lastTime).TotalSeconds < COOLDOWN_SECONDS) return;

            // Last candle
            var lastCandle = candles.Last();
            if (lastCandle == null) return;

            // --- INDICATORS ---
            decimal sma20 = SafeSMA(candles, 20);
            decimal sma50 = SafeSMA(candles, 50);

            // sma50 5 bars ago (use all available if <55)
            var last55 = candles.Count >= 55 ? candles.Skip(candles.Count - 55).ToList() : new List<Candle>(candles);
            decimal sma50_5ago = SafeSMA(last55, 50);

            double rsi = SafeRSI(candles, 7);
            decimal atr = SafeATR(candles, 14);
            decimal atrPct = lastCandle.Close > 0 ? atr / lastCandle.Close : 0m;

            decimal highest30 = SafeHighestHigh(candles, 30);
            decimal highest10 = SafeHighestHigh(candles, 10);

            bool isTrendUp = lastCandle.Close > sma50 && sma50 > sma50_5ago;

            // Skip low-volatility ranges
            if (atrPct < 0.004m) return;

            // Resistance filter
            // bool resistanceOk = lastCandle.Close <= highest30 * 1.005m;

            bool resistanceOkDip = lastCandle.Close <= highest30 * 1.06m;

            bool resistanceOkBreakout = true;

            // SPY regime check
            bool regimeOk = true;
            if (_marketData.TryGetValue("SPY", out var spy) && spy.Count > 0)
            {
                decimal spySma50 = SafeSMA(spy, 50);
                regimeOk = spy.Last().Close > spySma50;
            }

            // --- BUY CONDITIONS ---
            bool buyDip =
     regimeOk &&
     isTrendUp &&
     atrPct >= 0.0025m &&
     resistanceOkDip &&
     lastCandle.Close > sma50 &&
     rsi > 35 && rsi < 60;

            bool buyBreakout =
                regimeOk &&
                isTrendUp &&
                atrPct >= 0.0025m &&
                resistanceOkBreakout &&
                lastCandle.Close > highest10 &&
                rsi > 55 && rsi < 75;


            if (buyDip || buyBreakout)
            {
                int qty = (int)(POSITION_SIZE / lastCandle.Close);
                if (qty > 0)
                {
                    string note = buyDip ? "DIP_CONTINUATION" : "STRUCTURE_BREAKOUT";
                    SubmitOrder(symbol, qty, lastCandle.Close, TradeSide.Buy, note);
                }
            }
        }
    }

    private void CheckExits(string symbol, decimal currentPrice)
    {
        lock (_lock)
        {
            if (!_positions.TryGetValue(symbol, out var pos)) return;
            if (!_marketData.TryGetValue(symbol, out var candles)) return;

            // Minimum 2 minutes hold
            double secondsHeld = (TimeZoneInfo.ConvertTimeFromUtc(DateTime.UtcNow, Pacific) - pos.EntryTime).TotalSeconds;
            if (secondsHeld < 120) return;

            pos.HighWaterMark = Math.Max(pos.HighWaterMark, currentPrice);
            decimal gainPct = pos.AvgPrice > 0 ? (currentPrice - pos.AvgPrice) / pos.AvgPrice : 0m;

            // === HARD STOP (ATR based) ===
            decimal atrValue = SafeATR(candles, 14);
            decimal hardStop = pos.AvgPrice - (atrValue * 1.5m);

            if (currentPrice < hardStop)
            {
                SubmitOrder(symbol, pos.Quantity, currentPrice, TradeSide.Sell, "HARD_STOP");
                return;
            }

            // === STRATEGIC EXITS ===
            if (secondsHeld > MIN_HOLD_SECONDS)
            {
                // ATR trailing
                decimal atrTrailStop = pos.HighWaterMark - (atrValue * ATR_TRAIL_MULT);
                if (currentPrice < atrTrailStop && gainPct > 0.01m)
                {
                    SubmitOrder(symbol, pos.Quantity, currentPrice, TradeSide.Sell, "ATR_TRAIL_EXIT");
                    return;
                }

                // Trend break
                decimal sma20 = SafeSMA(candles, 20);
                if (currentPrice < sma20 && gainPct > 0.005m)
                {
                    SubmitOrder(symbol, pos.Quantity, currentPrice, TradeSide.Sell, "TREND_EXIT_PROFIT");
                    return;
                }

                // Volatility collapse
                decimal atrPct = currentPrice > 0 ? atrValue / currentPrice : 0m;
                if (atrPct < 0.004m)
                {
                    SubmitOrder(symbol, pos.Quantity, currentPrice, TradeSide.Sell, "VOLATILITY_EXIT");
                    return;
                }
            }
        }
    }


    // --- PERSISTENCE ---
    public void SaveState()
    {
        try
        {
            var data = new BotPersistData { Positions = _positions, TotalPnL = _totalRealizedPnL };
            File.WriteAllText("bot_state.json", JsonSerializer.Serialize(data));
        }
        catch (Exception ex)
        {
            File.AppendAllText("errors.log",
                $"[{DateTime.Now}] SaveMarketMemory ERROR: {ex.Message}\n");
        }

    }

    public void LoadState()
    {
        if (File.Exists("bot_state.json"))
        {
            try
            {
                var data = JsonSerializer.Deserialize<BotPersistData>(File.ReadAllText("bot_state.json"));
                if (data != null) { _positions = data.Positions; _totalRealizedPnL = data.TotalPnL; }
            }
            catch (Exception ex)
            {
                File.AppendAllText("errors.log",
                    $"[{DateTime.Now}] SaveMarketMemory ERROR: {ex.Message}\n");
            }

        }
    }

    // --- ORDER MANAGEMENT ---
    public void SubmitOrder(string symbol, int qty, decimal price, TradeSide side, string note, string type = "LMT")
    {
        if (RealBroker == null) return;
        decimal adjusted = price;

        if (type == "LMT")
        {
            // UPDATE: Get current volatility (ATR) for this specific stock
            _marketData.TryGetValue(symbol, out var candles);
            decimal atr = SafeATR(candles, 14);

            // Use 10% of the ATR as a "buffer" to ensure the limit order fills
            decimal slippageBuffer = atr * 0.1m;

            if (side == TradeSide.Buy)
                adjusted = price + slippageBuffer; // Bid slightly higher to secure the buy
            else
                adjusted = price - slippageBuffer; // Ask slightly lower to secure the sell
        }

        RealBroker.SubmitOrder(symbol, qty, adjusted, side, 0, type);
        Console.WriteLine($"[ORDER] {note} -> {side} {symbol} x {qty} @ {adjusted:F2} (Orig: {price:F2})");
    }

    public void RegisterLiveOrder(int orderId, string symbol, TradeSide side, int qty)
    {
        _ordersById[orderId] = new TrackedOrder { OrderId = orderId, Symbol = symbol, Side = side, Qty = qty };
    }

    public void OnOrderFilled(int orderId, int fillQty, decimal fillPrice)
    {
        if (!_ordersById.TryRemove(orderId, out var order)) return;

        string subject = ""; string body = "";

        lock (_lock)
        {
            if (order.Side == TradeSide.Buy)
            {
                var pos = new SimPosition
                {
                    Symbol = order.Symbol,
                    Quantity = fillQty,
                    AvgPrice = fillPrice,
                    HighWaterMark = fillPrice,
                    CurrentPrice = fillPrice,
                    EntryTime = TimeZoneInfo.ConvertTimeFromUtc(DateTime.UtcNow, Pacific)
                };
                _positions[order.Symbol] = pos;
                _tradesToday++;
                subject = $"🚀 BUY: {order.Symbol} x {fillQty} @ {fillPrice:C2}";
                body = $"Bought {fillQty} @ {fillPrice:C2}";
            }
            else
            {
                if (_positions.TryGetValue(order.Symbol, out var pos))
                {
                    decimal pnl = (fillPrice - pos.AvgPrice) * fillQty;
                    _totalRealizedPnL += pnl;
                    _positions.Remove(order.Symbol);
                    _lastTradeTime[order.Symbol] = TimeZoneInfo.ConvertTimeFromUtc(DateTime.UtcNow, Pacific);
                    subject = $"💰 SELL: {order.Symbol} x {fillQty} @ {fillPrice:C2} | PnL: {pnl:C2}";
                    body = $"Sold {fillQty} @ {fillPrice:C2}\nPnL: {pnl:C2}";
                }
            }

            // Log trade
            string logLine = order.Side == TradeSide.Buy ?
                $"[{TimeZoneInfo.ConvertTimeFromUtc(DateTime.UtcNow, Pacific):HH:mm:ss}] BUY  {order.Symbol} x {fillQty} @ {fillPrice:C2}" :
                $"[{TimeZoneInfo.ConvertTimeFromUtc(DateTime.UtcNow, Pacific):HH:mm:ss}] SELL {order.Symbol} x {fillQty} @ {fillPrice:C2}";

            _tradeHistoryLog.Add(logLine);
            if (_tradeHistoryLog.Count > 50) _tradeHistoryLog.RemoveAt(0); // keep last 50 trades

            // Print log below dashboard
            Console.SetCursorPosition(0, 22 + _tradeHistoryLog.Count - 1);
            Console.ForegroundColor = order.Side == TradeSide.Buy ? ConsoleColor.Green : ConsoleColor.Red;
            Console.WriteLine(logLine);
            Console.ResetColor();

            Task.Run(() => SendEmail(subject, body));
        }

        CheckDailyLimits();
        SaveState();
    }

    private void LogToCsv(string symbol, string side, decimal price, decimal pnl)
    {
        try
        {
            using (var sw = new StreamWriter("trades_log.csv", true))
                sw.WriteLine($"{TimeZoneInfo.ConvertTimeFromUtc(DateTime.UtcNow, Pacific)},{symbol},{side},{price},{pnl}");
        }
        catch (Exception ex)
        {
            File.AppendAllText("errors.log",
                $"[{DateTime.Now}] SaveMarketMemory ERROR: {ex.Message}\n");
        }

    }

    private void CheckDailyLimits()
    {
        if (_totalRealizedPnL >= DAILY_PROFIT_GOAL || _totalRealizedPnL <= MAX_DAILY_LOSS)
        {
            _haltTrading = true;
            string status = _totalRealizedPnL > 0 ? "GOAL REACHED" : "MAX LOSS HIT";
            SendEmail($"🛑 TRADING HALTED: {status}", $"Final PnL: {_totalRealizedPnL:C2}");
        }
    }

    public void CheckEndOfDay()
    {
        var now = GetEasternTime();
        if (now.Hour == 15 && now.Minute >= 50 && !_eodSent)
        {
            _haltTrading = true;
            foreach (var p in _positions.Values.ToList())
                SubmitOrder(p.Symbol, p.Quantity, 0, TradeSide.Sell, "EOD_LIQUIDATE", "MKT");

            if (now.Hour >= 16)
            {
                _eodSent = true;
                string report = $"EOD PnL: {_totalRealizedPnL:C2}\nTrades: {_tradesToday}\n\nLog:\n" + string.Join("\n", _tradeHistoryLog);
                SendEmail("📊 EOD PERFORMANCE REPORT", report);
            }
        }
    }

    // --- SAFE HELPERS ---
    private decimal SafeSMA(List<Candle> candles, int period)
    {
        if (candles == null || candles.Count < period) return 0m;
        return candles.TakeLast(period).Average(c => c.Close);
    }

    private double SafeRSI(List<Candle> candles, int period)
    {
        if (candles == null || candles.Count <= period) return 50.0;
        double gain = 0, loss = 0;
        for (int i = candles.Count - period + 1; i < candles.Count; i++)
        {
            double diff = (double)(candles[i].Close - candles[i - 1].Close);
            if (diff > 0) gain += diff; else loss -= diff;
        }
        return loss == 0 ? 100 : 100 - (100 / (1 + (gain / loss)));
    }

    private decimal SafeATR(List<Candle> candles, int period)
    {
        if (candles == null || candles.Count <= period) return 0m;
        decimal sum = 0;
        for (int i = candles.Count - period + 1; i < candles.Count; i++)
        {
            var c = candles[i];
            var prev = candles[i - 1];
            decimal tr = Math.Max(c.High - c.Low,
                          Math.Max(Math.Abs(c.High - prev.Close),
                                   Math.Abs(c.Low - prev.Close)));
            sum += tr;
        }
        return sum / period;
    }

    private decimal SafeHighestHigh(List<Candle> candles, int lookback)
    {
        if (candles == null || candles.Count < lookback) return 0m;
        return candles.TakeLast(lookback).Max(c => c.High);
    }

    // --- REFACTORED DASHBOARD ---
    private const int LOG_LINES = 20; // lines reserved for scrolling log
    private Queue<string> _scrollingLog = new Queue<string>();
    
    Queue<string> _logQueue = new Queue<string>();

    private Timer _dashboardTimer;

    public void Start()
    {
        // Start a timer to refresh the dashboard every second
        _dashboardTimer = new Timer(_ =>
        {
            PrintDetailedDashboard();
        }, null, 0, 1000);
    }

    public void LogMessage(string msg)
    {
        if (_logQueue.Count >= LOG_LINES)
            _logQueue.Dequeue();

        _logQueue.Enqueue(msg);

        int originalCursor = Console.CursorTop;
        Console.SetCursorPosition(0, 0);

        foreach (var line in _logQueue)
        {
            string truncated = line.Length > Console.WindowWidth - 1
                ? line.Substring(0, Console.WindowWidth - 1)
                : line;
            Console.WriteLine(truncated.PadRight(Console.WindowWidth - 1));
        }

        // fill remaining lines if less than LOG_LINES
        for (int i = _logQueue.Count; i < LOG_LINES; i++)
            Console.WriteLine(new string(' ', Console.WindowWidth - 1));

        Console.SetCursorPosition(0, originalCursor); // restore cursor for dashboard
    }

    public void PrintDetailedDashboard()
    {
        int dashboardTop = LOG_LINES;
        Console.SetCursorPosition(0, dashboardTop);


        var now = TimeZoneInfo.ConvertTimeFromUtc(DateTime.UtcNow, TimeZoneInfo.FindSystemTimeZoneById("Pacific Standard Time"));

        // --- HEADER ---
        Console.WriteLine("┌────────────────────────────────────────────────────────────────────────────┐");
        Console.WriteLine($"│ IBKR TRADING BOT │ {now:HH:mm:ss} │ PnL: {_totalRealizedPnL,8:C2} │ Budget: {_positions.Count * POSITION_SIZE + (_totalRealizedPnL >= 0 ? TOTAL_BUDGET : TOTAL_BUDGET + _totalRealizedPnL),7:C0} │");
        Console.WriteLine("└────────────────────────────────────────────────────────────────────────────┘\n");

        // --- ACTIVE POSITIONS ---
        Console.WriteLine($"ACTIVE POSITIONS ({_positions.Count}/{MAX_POSITIONS}) | Available Cash: {(TOTAL_BUDGET - _positions.Count * POSITION_SIZE):C2}");
        Console.WriteLine("┌────────┬─────┬─────────┬─────────┬─────────┬───────┬───────┬─────────┐");
        Console.WriteLine("│ Symbol │ Qty │ Entry   │ Current │ PnL     │ PnL%  │ Hold  │ High    │");
        Console.WriteLine("├────────┼─────┼─────────┼─────────┼─────────┼───────┼───────┼─────────┤");

        if (_positions.Count == 0)
            Console.WriteLine("│                       NO ACTIVE POSITIONS                                   │");
        else
        {
            foreach (var p in _positions.Values)
            {
                double mins = (TimeZoneInfo.ConvertTimeFromUtc(DateTime.UtcNow, TimeZoneInfo.FindSystemTimeZoneById("Pacific Standard Time")) - p.EntryTime).TotalMinutes;
                decimal pnl = p.UnrealizedPnL(p.CurrentPrice);
                decimal pnlPct = p.AvgPrice > 0 ? (p.CurrentPrice - p.AvgPrice) / p.AvgPrice * 100 : 0;
                Console.WriteLine($"│ {p.Symbol,-6} │ {p.Quantity,-3} │ {p.AvgPrice,7:C} │ {p.CurrentPrice,7:C} │ {pnl,7:C} │ {pnlPct,5:F1}% │ {mins,5:F0} │ {p.HighWaterMark,7:C} │");
            }
        }

        Console.WriteLine("└────────┴─────┴─────────┴─────────┴─────────┴───────┴───────┴─────────┘\n");

        // --- MARKET SCANNER ---
        Console.WriteLine("MARKET SCANNER (RSI & Trend Signals)");
        Console.WriteLine("┌────────┬─────────┬─────────┬─────────┬─────┬─────────┬─────────┬─────────────┐");
        Console.WriteLine("│ Symbol │ Price   │ SMA20   │ SMA50   │ RSI │ Trend   │ Signal  │ RSI Heat    │");
        Console.WriteLine("├────────┼─────────┼─────────┼─────────┼─────┼─────────┼─────────┼─────────────┤");

        foreach (var sym in _watchlist)
        {
            _marketData.TryGetValue(sym, out var candles);
            var last = candles != null && candles.Count > 0 ? candles.Last() : null;

            decimal price = last?.Close ?? 0m;
            decimal sma20 = SafeSMA(candles, 20);
            decimal sma50 = SafeSMA(candles, 50);
            double rsi = SafeRSI(candles, 7);

            string trend = price > sma50 ? "UP" : "NEUTRAL";
            string signal = "WAIT";

            int barLength = 10;
            int fill = (int)Math.Round(rsi / 100.0 * barLength);
            string heatBar = new string('█', fill) + new string(' ', barLength - fill);

            Console.WriteLine($"│ {sym,-6} │ {price,7:C} │ {sma20,7:C} │ {sma50,7:C} │ {rsi,3:F0} │ {trend,-7} │ {signal,-7} │ {heatBar,-11} │");
        }

        Console.WriteLine("└────────┴─────────┴─────────┴─────────┴─────┴─────────┴─────────┴─────────────┘\n");

        // --- RULES & STRATEGY ---
        Console.WriteLine("RULES & STRATEGY");
        Console.WriteLine($"Max Positions: {MAX_POSITIONS} | Position Size: {POSITION_SIZE:C} | Cooldown: {COOLDOWN_SECONDS / 60} min");
        Console.WriteLine($"Hard Stop: -1.5% | Trail: 0.5% after +2% | Min Hold: {MIN_HOLD_SECONDS / 60} min\n");

        // --- TRADE LOG ---
        Console.WriteLine("TRADE LOG (recent 10 trades):");
        var recent = _tradeHistoryLog.TakeLast(10).ToList();
        if (!recent.Any())
            Console.WriteLine("No trades yet.");
        else
            foreach (var log in recent)
                Console.WriteLine(log);

        // Clear remaining dashboard space
        int dashboardHeight = Console.WindowHeight - LOG_LINES;
        int currentHeight = Console.CursorTop;
        for (int i = currentHeight; i < dashboardHeight + LOG_LINES; i++)
            Console.WriteLine(new string(' ', Console.WindowWidth - 1));
    }
    private double CalculateRSI(List<Candle> candles, int period)
    {
        if (candles.Count <= period) return 50;
        double gain = 0, loss = 0;
        for (int i = candles.Count - period + 1; i < candles.Count; i++)
        {
            double diff = (double)(candles[i].Close - candles[i - 1].Close);
            if (diff > 0) gain += diff; else loss -= diff;
        }
        return loss == 0 ? 100 : 100 - (100 / (1 + (gain / loss)));
    }

    private DateTime GetEasternTime() => TimeZoneInfo.ConvertTimeFromUtc(TimeZoneInfo.ConvertTimeFromUtc(DateTime.UtcNow, Pacific), TimeZoneInfo.FindSystemTimeZoneById("Eastern Standard Time"));

    public async Task SendEmail(string subject, string body)
    {
        try
        {
            var smtp = new SmtpClient("smtp.gmail.com") { Port = 587, EnableSsl = true, Credentials = new NetworkCredential(EmailFrom, EmailPassword) };
            using var msg = new MailMessage(EmailFrom, EmailTo) { Subject = subject, Body = body };
            await smtp.SendMailAsync(msg);
        }
        catch (Exception ex)
        {
            File.AppendAllText("errors.log",
                $"[{DateTime.Now}] SaveMarketMemory ERROR: {ex.Message}\n");
        }

    }
    // Add this inside the SimulatedBroker class in SimulatedBroker.cs
    public void AddHistoricalCandle(string symbol, DateTime time, decimal open, decimal high, decimal low, decimal close, long vol)
    {
        var candles = _marketData.GetOrAdd(symbol, _ => new List<Candle>());
        lock (candles)
        {
            if (!candles.Any(c => c.Time == time))
            {
                candles.Add(new Candle
                {
                    Time = time,
                    Open = open,
                    High = high,
                    Low = low,
                    Close = close,
                    Volume = vol
                });
                // Keep them sorted by time
                _marketData[symbol] = candles.OrderBy(c => c.Time).ToList();
            }
        }
    }
    public void UpdateHistory(string symbol, decimal price, long size)
    {
        // This bridges the IB Client call to your existing logic
        UpdateLiveTick(symbol, price, size);
    }

    public void SaveMarketMemory()
    {
        try
        {
            var normalDict = _marketData.ToDictionary(k => k.Key, v => v.Value);

            // STEP 5 — TRIM OLD DATA (keep 3 days)
            foreach (var kv in normalDict)
            {
                kv.Value.RemoveAll(c =>
                    c.Time < TimeZoneInfo.ConvertTimeFromUtc(
                        DateTime.UtcNow.AddDays(-3), Pacific));
            }

            File.WriteAllText("market_memory.json",
                JsonSerializer.Serialize(normalDict));
        }
        catch (Exception ex)
        {
            File.AppendAllText("errors.log",
                $"[{DateTime.Now}] SaveMarketMemory ERROR: {ex.Message}\n");
        }

    }


    public void LoadMarketMemory()
    {
        if (!File.Exists("market_memory.json")) return;

        try
        {
            var data = JsonSerializer.Deserialize<ConcurrentDictionary<string, List<Candle>>>(
                File.ReadAllText("market_memory.json"));

            if (data != null)
            {
                foreach (var kv in data)
                    _marketData[kv.Key] = kv.Value;
            }
        }
        catch (Exception ex)
        {
            File.AppendAllText("errors.log",
                $"[{DateTime.Now}] SaveMarketMemory ERROR: {ex.Message}\n");
        }

    }
    public async Task RequestAllHistoricalSlow()
    {
        foreach (var sym in _watchlist)
        {
            RealBroker.RequestHistoricalData(sym);
            Console.WriteLine($"[HIST REQ] {sym}");
            await Task.Delay(1200);   // IBKR-safe throttle
        }
    }



}