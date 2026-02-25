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
    public DateTime EntryTime { get; set; } = DateTime.UtcNow;
    public bool ExitSubmitted { get; set; }


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
    private readonly Dictionary<string, long> _dailyVolume = new();
    private DateTime _lastVolumeResetEt = DateTime.MinValue;


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
    private static readonly TimeZoneInfo Eastern = TimeZoneInfo.FindSystemTimeZoneById("America/New_York");
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
    "TSM","ASML","AVGO","QCOM","TXN","MU","ARM","LRCX","AMAT","SMCI",

    // Cloud / SaaS
    "CRM","NOW","SNOW","MDB","DDOG","NET","OKTA","ZS","CRWD","PANW","PLTR",

    // Consumer / Growth
    "NFLX","DIS","UBER","LYFT","ABNB","RBLX","SHOP","ETSY",

    // Finance / Crypto
    "COIN","MSTR","SQ","PYPL","HOOD","SOFI","JPM","BAC","GS",

    // EV / Energy
    "NIO","RIVN","LCID","ENPH","SEDG","FSLR","PLUG",

    // Healthcare / Biotech
    "JNJ","PFE","MRNA","ABBV","UNH","VRTX",

    // Industrials
    "BA","CAT","DE","GE","HON","RTX","LMT"
};

    private readonly List<(DateTime time, decimal equity)> _equityCurve = new();

    private readonly ConcurrentDictionary<string, Candle> _currentMinuteCandle = new();

    private static readonly TimeZoneInfo Pacific = TimeZoneInfo.FindSystemTimeZoneById("Pacific Standard Time");
    private DateTime _lastMemorySave = DateTime.MinValue;

    // --- MARKET DATA INGESTION ---
    public void UpdateLiveTick(string symbol, decimal price, long size)
    {
        try
        {
            _latestTick[symbol] = price;

            var nowEt = GetEasternTime();
            var minute = new DateTime(
                nowEt.Year, nowEt.Month, nowEt.Day,
                nowEt.Hour, nowEt.Minute, 0);

            var current = _currentMinuteCandle.GetOrAdd(symbol, _ =>
                new Candle
                {
                    Time = minute,
                    Open = price,
                    High = price,
                    Low = price,
                    Close = price,
                    Volume = size
                });

            // If new minute → finalize old candle
            if (current.Time != minute)
            {
                FinalizeCandle(symbol, current);

                current = new Candle
                {
                    Time = minute,
                    Open = price,
                    High = price,
                    Low = price,
                    Close = price,
                    Volume = size
                };

                _currentMinuteCandle[symbol] = current;
            }
            else
            {
                // Update running candle
                current.High = Math.Max(current.High, price);
                current.Low = Math.Min(current.Low, price);
                current.Close = price;
                current.Volume += size;
            }

            if (_haltTrading) return;

            OnTradeTick(symbol, size);

            lock (_lock)
            {
                if (_positions.TryGetValue(symbol, out var pos))
                    pos.CurrentPrice = price;
            }

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
    private void FinalizeCandle(string symbol, Candle candle)
    {
        var list = _marketData.GetOrAdd(symbol, _ => new List<Candle>());

        lock (list)
        {
            // Prevent duplicate minute
            if (!list.Any(c => c.Time == candle.Time))
            {
                list.Add(candle);
            }

            if (list.Count > 500)
                list.RemoveAt(0);
        }

        ExecuteStrategy(symbol);
    }

    public void OnTradeTick(string symbol, long size)
    {
        lock (_lock)
        {
            if (!_dailyVolume.ContainsKey(symbol))
                _dailyVolume[symbol] = 0;

            _dailyVolume[symbol] += size;
        }
    }




    // --- FULL EXECUTE STRATEGY ---
    public void ExecuteStrategy(string symbol)
    {
        CheckDailyReset();
        if (_haltTrading) return;
        if (!_marketData.TryGetValue(symbol, out var candles) || candles.Count == 0) return;
        var nowEt = GetEasternTime();
        if (candles.Count < 20)
        {
            LogMessage($"[SKIP] {symbol} not enough candles: {candles.Count}");
            return;
        }

        long todayVolume = _dailyVolume.GetValueOrDefault(symbol);

        if (candles.TakeLast(300).Sum(c => c.Volume) < 30_000)
            return;



        lock (_lock)
        {
            // Already in a position or max positions reached
            if (_positions.ContainsKey(symbol) || _positions.Count >= MAX_POSITIONS) return;

            // Cooldown check
            if (_lastTradeTime.TryGetValue(symbol, out var lastTime))
                if ((DateTime.UtcNow - lastTime).TotalSeconds < COOLDOWN_SECONDS) return;

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
            decimal highest10 = SafeHighestHigh(candles.Take(candles.Count - 1).ToList(), 10);


            bool isTrendUp = lastCandle.Close > sma50 && sma50 > sma50_5ago;

            // Skip low-volatility ranges
            if (atrPct < 0.002m) return;

            // Resistance filter
            // bool resistanceOk = lastCandle.Close <= highest30 * 1.005m;

            bool resistanceOkDip = lastCandle.Close <= highest30 * 1.06m;

            bool resistanceOkBreakout = true;

            // SPY regime check
            bool regimeOk = true;
            if (_marketData.TryGetValue("SPY", out var spy) && spy.Count > 0)
            {
                decimal spySma50 = SafeSMA(spy, 40);
                regimeOk = spy.Last().Close > spySma50;
            }


            bool volumeExpansion = false;

            if (candles.Count >= 10)
            {
                var last10 = candles.TakeLast(10).ToList();
                long prev5 = last10.Take(5).Sum(c => c.Volume);
                long recent5 = last10.Skip(5).Take(5).Sum(c => c.Volume);

                volumeExpansion = recent5 > prev5 * 1.3m;
            }



            // ===============================
            // CLEAN 1-MIN MOMENTUM LOGIC
            // ===============================

            bool trendAligned =
    sma20 > sma50 ||
    (lastCandle.Close > sma50 && rsi > 55);


            bool momentumStrong =
                lastCandle.Close > sma20 &&
                rsi > 55 &&
                atrPct > 0.0015m;        // 0.2% volatility filter

            // Optional breakout confirmation
            decimal recentHigh = SafeHighestHigh(
                candles.Take(candles.Count - 1).ToList(), 5);

            bool structureBreak = lastCandle.Close > recentHigh;

            bool buySignal =
                regimeOk &&
                trendAligned &&
                momentumStrong &&
                structureBreak;

            if (buySignal)
            {
                int qty = (int)(POSITION_SIZE / lastCandle.Close);

                if (qty > 0)
                {
                    SubmitOrder(symbol, qty, lastCandle.Close,
                        TradeSide.Buy, "MOMENTUM_BREAKOUT");
                }
            }
            Console.WriteLine($"{symbol} | Regime: {regimeOk} | Trend: {trendAligned} | RSI: {rsi:F1} | Break: {structureBreak}");

        }

    }

    private void CheckExits(string symbol, decimal currentPrice)
    {
        lock (_lock)
        {
            if (!_positions.TryGetValue(symbol, out var pos)) return;
            if (!_marketData.TryGetValue(symbol, out var candles)) return;

            // Minimum 2 minutes hold
            double secondsHeld = (DateTime.UtcNow - pos.EntryTime).TotalSeconds;
            if (secondsHeld < MIN_HOLD_SECONDS) return;


            pos.HighWaterMark = Math.Max(pos.HighWaterMark, currentPrice);
            decimal gainPct = pos.AvgPrice > 0 ? (currentPrice - pos.AvgPrice) / pos.AvgPrice : 0m;

            // === HARD STOP (ATR based) ===
            decimal atrValue = SafeATR(candles, 14);
            decimal hardStop = pos.AvgPrice - (atrValue * 1.5m);

            if (currentPrice < hardStop && !pos.ExitSubmitted)
            {
                pos.ExitSubmitted = true;
                SubmitOrder(symbol, pos.Quantity, currentPrice, TradeSide.Sell, "HARD_STOP");
                return;
            }


            // === STRATEGIC EXITS ===
            if (secondsHeld > MIN_HOLD_SECONDS)
            {
                // ATR trailing
                decimal atrTrailStop = pos.HighWaterMark - (atrValue * ATR_TRAIL_MULT);
                if (currentPrice < atrTrailStop && gainPct > 0.005m)
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
                    SubmitOrder(symbol, pos.Quantity, 0, TradeSide.Sell, "VOLATILITY_EXIT", "MKT");
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
            _marketData.TryGetValue(symbol, out var candles);
            decimal atr = SafeATR(candles, 14);

            decimal slippageBuffer = Math.Max(
                atr * 0.1m,
                price * 0.0005m
            );

            adjusted = side == TradeSide.Buy
                ? price + slippageBuffer
                : price - slippageBuffer;
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
        if (!_ordersById.TryGetValue(orderId, out var order)) return;

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
                    EntryTime = DateTime.UtcNow
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
                    decimal holdMinutes = (decimal)(DateTime.UtcNow - pos.EntryTime).TotalMinutes;

                    _marketData.TryGetValue(order.Symbol, out var candlesAtExit);
                    decimal atrAtExit = SafeATR(candlesAtExit, 14);
                    double rsiAtExit = SafeRSI(candlesAtExit, 7);

                    // Detailed analytics log
                    LogTradeAnalytics(
                        order.Symbol,
                        pos.AvgPrice,
                        fillPrice,
                        pnl,
                        holdMinutes,
                        atrAtExit,
                        rsiAtExit
                    );

                    _totalRealizedPnL += pnl;
                    _positions.Remove(order.Symbol);
                    _lastTradeTime[order.Symbol] = DateTime.UtcNow;
                    subject = $"💰 SELL: {order.Symbol} x {fillQty} @ {fillPrice:C2} | PnL: {pnl:C2}";
                    body = $"Sold {fillQty} @ {fillPrice:C2}\nPnL: {pnl:C2}";
                }
            }

            // Log trade
            string logLine = order.Side == TradeSide.Buy ?
                $"[{DateTime.UtcNow:HH:mm:ss}] BUY  {order.Symbol} x {fillQty} @ {fillPrice:C2}" :
                $"[{DateTime.UtcNow:HH:mm:ss}] SELL {order.Symbol} x {fillQty} @ {fillPrice:C2}";

            _tradeHistoryLog.Add(logLine);
            if (_tradeHistoryLog.Count > 50) _tradeHistoryLog.RemoveAt(0); // keep last 50 trades

            // Print log below dashboard
            Console.SetCursorPosition(0, 22 + _tradeHistoryLog.Count - 1);
            Console.ForegroundColor = order.Side == TradeSide.Buy ? ConsoleColor.Green : ConsoleColor.Red;
            Console.WriteLine(logLine);
            Console.ResetColor();

            Task.Run(() => SendEmail(subject, body));
        }
        _equityCurve.Add((DateTime.UtcNow, _totalRealizedPnL));

        SaveEquityCurve();

        CheckDailyLimits();
        SaveState();
    }
    private void SaveEquityCurve()
    {
        try
        {
            File.WriteAllLines("equity_curve.csv",
                _equityCurve.Select(e =>
                    $"{e.time},{e.equity}"));
        }
        catch { }
    }


    private void LogTradeAnalytics(
    string symbol,
    decimal entry,
    decimal exit,
    decimal pnl,
    decimal holdMinutes,
    decimal atr,
    double rsi)
    {
        try
        {
            using var sw = new StreamWriter("trade_analytics.csv", true);

            sw.WriteLine(
                $"{DateTime.UtcNow}," +
                $"{symbol}," +
                $"{entry}," +
                $"{exit}," +
                $"{pnl}," +
                $"{holdMinutes:F2}," +
                $"{atr}," +
                $"{rsi:F2}");
        }
        catch { }
    }

    private void LogToCsv(string symbol, string side, decimal price, decimal pnl)
    {
        try
        {
            using (var sw = new StreamWriter("trades_log.csv", true))
                sw.WriteLine($"{DateTime.UtcNow},{symbol},{side},{price},{pnl}");
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
    private void CheckDailyReset()
    {
        var nowEt = GetEasternTime();

        if (_lastVolumeResetEt == DateTime.MinValue)
            _lastVolumeResetEt = nowEt.Date;

        if (nowEt.Date > _lastVolumeResetEt)
        {
            _dailyVolume.Clear();
            _lastVolumeResetEt = nowEt.Date;
            Console.WriteLine($"[VOLUME RESET] {nowEt:yyyy-MM-dd}");
        }
    }

    public void CheckEndOfDay()
    {
        var now = GetEasternTime();
        if (now.Hour == 15 && now.Minute >= 50 && !_eodSent)
        {
            _haltTrading = true;
            _eodSent = true;
            foreach (var p in _positions.Values.ToList())
                SubmitOrder(p.Symbol, p.Quantity, 0, TradeSide.Sell, "EOD_LIQUIDATE", "MKT");

            if (now.Hour >= 16)
            {
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

public double SafeRSI(List<Candle> candles, int period)
{
    if (candles == null || candles.Count < period + 1)
        return 50;

    for (int i = 1; i < candles.Count; i++)
    {
        if (candles[i] == null || candles[i - 1] == null)
            return 50;
    }

    double gain = 0;
    double loss = 0;

    for (int i = candles.Count - period; i < candles.Count; i++)
    {
        double diff = (double)(candles[i].Close - candles[i - 1].Close);

        if (diff >= 0) gain += diff;
        else loss -= diff;
    }

    if (loss == 0) return 100;

    double rs = gain / loss;
    return 100 - (100 / (1 + rs));
}


    private decimal SafeATR(List<Candle> candles, int period)
    {
        if (candles == null || candles.Count <= period)
            return candles?.LastOrDefault()?.Close * 0.002m ?? 0.01m;

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
        int logLines = 10; // top log area
        int dashboardTop = logLines;
        int width = Console.WindowWidth;
        int height = Console.WindowHeight - dashboardTop;

        var sb = new StringBuilder();
        var now = TimeZoneInfo.ConvertTimeFromUtc(DateTime.UtcNow, Pacific);

        // --- HEADER ---
        sb.AppendLine(new string('─', width));
        sb.AppendLine($" IBKR TRADING BOT │ {now:HH:mm:ss} │ PnL: {_totalRealizedPnL,8:C2} │ Budget: {(TOTAL_BUDGET - _positions.Count * POSITION_SIZE):C0} ".PadRight(width - 1));
        sb.AppendLine(new string('─', width));

        // --- ACTIVE POSITIONS ---
        sb.AppendLine($"ACTIVE POSITIONS ({_positions.Count}/{MAX_POSITIONS}) | Available Cash: {(TOTAL_BUDGET - _positions.Count * POSITION_SIZE):C2}".PadRight(width - 1));
        sb.AppendLine("Symbol  Qty   Entry     Current   PnL       PnL%   Hold  High".PadRight(width - 1));
        sb.AppendLine(new string('-', width - 1));

        if (_positions.Count == 0)
        {
            sb.AppendLine("NO ACTIVE POSITIONS".PadLeft(width / 2));
        }
        else
        {
            lock (_lock)
            {
                foreach (var p in _positions.Values)
                {
                    double mins = (DateTime.UtcNow - p.EntryTime).TotalMinutes;
                    decimal pnl = p.UnrealizedPnL(p.CurrentPrice);
                    decimal pnlPct = p.AvgPrice > 0 ? (p.CurrentPrice - p.AvgPrice) / p.AvgPrice * 100 : 0;
                    sb.AppendLine($"{p.Symbol,-6} {p.Quantity,3} {p.AvgPrice,8:C} {p.CurrentPrice,8:C} {pnl,8:C} {pnlPct,5:F1}% {mins,5:F0} {p.HighWaterMark,8:C}".PadRight(width - 1));
                }
            }
        }
        sb.AppendLine(new string('-', width - 1));

        // --- MARKET SCANNER ---
        sb.AppendLine("MARKET SCANNER (RSI & Trend Signals)".PadRight(width - 1));
        sb.AppendLine("Symbol  Price     SMA20     SMA50   RSI  Trend   Signal  RSI".PadRight(width - 1));
        sb.AppendLine(new string('-', width - 1));

        foreach (var sym in _watchlist)
        {
            _marketData.TryGetValue(sym, out var candles);
            var last = candles?.LastOrDefault();
            decimal price = last?.Close ?? 0m;
            decimal sma20 = SafeSMA(candles, 20);
            decimal sma50 = SafeSMA(candles, 50);
            double rsi = SafeRSI(candles, 7);

            string trend = price > sma50 ? "UP" : "NEUTRAL";
            string signal = "WAIT";

            int barLength = 8;
            int fill = Math.Clamp((int)Math.Round(rsi / 100.0 * barLength), 0, barLength);
            string heatBar = new string('█', fill).PadRight(barLength);

            sb.AppendLine($"{sym,-7} {price,8:C} {sma20,8:C} {sma50,8:C} {rsi,3:F0} {trend,-7} {signal,-7} {heatBar}".PadRight(width - 1));
        }

        sb.AppendLine(new string('-', width - 1));

        // --- RULES & STRATEGY ---
        sb.AppendLine($"Max Positions: {MAX_POSITIONS} | Position Size: {POSITION_SIZE:C} | Cooldown: {COOLDOWN_SECONDS / 60} min".PadRight(width - 1));
        sb.AppendLine($"Hard Stop: -1.5% | Trail: 0.5% after +2% | Min Hold: {MIN_HOLD_SECONDS / 60} min".PadRight(width - 1));

        // --- TRADE LOG ---
        sb.AppendLine("TRADE LOG (recent 10 trades):".PadRight(width - 1));
        var recent = _tradeHistoryLog.TakeLast(10).ToList();
        if (!recent.Any())
            sb.AppendLine("No trades yet.".PadRight(width - 1));
        else
            foreach (var log in recent)
                sb.AppendLine(log.PadRight(width - 1));

        // --- Clear and write ---
        Console.SetCursorPosition(0, dashboardTop);
        Console.Write(sb.ToString());

        // Pad remaining lines to avoid flicker
        int currentHeight = dashboardTop + sb.ToString().Count(c => c == '\n');
        for (int i = currentHeight; i < Console.WindowHeight; i++)
            Console.WriteLine(new string(' ', width - 1));
    }

    private DateTime GetEasternTime() =>
        TimeZoneInfo.ConvertTimeFromUtc(DateTime.UtcNow, Eastern);


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
                $"[{DateTime.Now}] SaveMarketMemory ERROR: {ex}\n");
        }

    }
    // Add this inside the SimulatedBroker class in SimulatedBroker.cs
    public void AddHistoricalCandle(
    string symbol,
    DateTime time,
    decimal open,
    decimal high,
    decimal low,
    decimal close,
    long vol)
    {
        var list = _marketData.GetOrAdd(symbol, _ => new List<Candle>());

        lock (list)
        {
            if (!list.Any(c => c.Time == time))
            {
                list.Add(new Candle
                {
                    Time = time,
                    Open = open,
                    High = high,
                    Low = low,
                    Close = close,
                    Volume = vol
                });

                list.Sort((a, b) => a.Time.CompareTo(b.Time));
            }

            if (list.Count > 500)
                list.RemoveAt(0);
        }
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
                    c.Time < DateTime.UtcNow.AddDays(-3));

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

    public void ClearMarketData()
    {
        foreach (var kv in _marketData)
        {
            lock (kv.Value)
            {
                kv.Value.Clear();
            }
        }

        _marketData.Clear();
        Console.WriteLine("[CANDLE ENGINE] Market data cleared.");
    }
    public async Task RequestAllHistoricalSlow()
    {
        if (RealBroker == null)
            throw new Exception("RealBroker not set.");

        foreach (var symbol in _watchlist)
        {
            Console.WriteLine($"Requesting history for {symbol}");
            RealBroker.RequestHistoricalData(symbol);

            // IB pacing safety delay
            await Task.Delay(1500);
        }
    }



}