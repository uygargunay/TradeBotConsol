using IBApi;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Mail;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using static System.Runtime.InteropServices.JavaScript.JSType;

// ══════════════════════════════════════════════════════════
//  DATA STRUCTURES
// ══════════════════════════════════════════════════════════

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
    void SubmitOrder(string symbol, int qty, decimal price, TradeSide side,
                     double currentRsi = 0, string orderType = "LMT");
    void RequestHistoricalData(string symbol);
    void CancelMarketData(string symbol);
    void CancelOrder(int orderId);
    bool IsReady { get; }
    void RequestPositions();
    void RequestDailyHistoricalData(string symbol);
    bool SupportsBrackets { get; }
    void SubmitBracketOrder(string symbol, int qty, decimal entryPrice, TradeSide side,
                            decimal stopPrice, decimal stopLimit, decimal targetPrice);
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
    public bool PartialExitDone { get; set; }
    public bool PartialExitDone2 { get; set; }
    public bool IsShort { get; set; }
    public string StrategyTag { get; set; } = "";
    public decimal EntryCommission { get; set; } = 0m;
    public decimal InitialRiskPerShare { get; set; } = 0m;
    public int BracketStopId { get; set; } = 0;
    public int BracketTargetId { get; set; } = 0;
    public decimal UnrealizedPnL(decimal price) =>
        IsShort ? Quantity * (AvgPrice - price) : Quantity * (price - AvgPrice);
}

public class TrackedOrder
{
    public int OrderId;
    public string Symbol;
    public TradeSide Side;
    public int Qty;
    public bool IsShortEntry;
}

public class BotPersistData
{
    public Dictionary<string, SimPosition> Positions { get; set; } = new();
    public decimal TotalPnL { get; set; }
    public int WinCount { get; set; }
    public int LossCount { get; set; }
    public int TradesToday { get; set; }
    public bool HaltTrading { get; set; }
    public Dictionary<string, DateTime> LastTradeTime { get; set; } = new();
    public Dictionary<string, bool> LastTradeWasLoss { get; set; } = new();
    public Dictionary<string, int> DailyEntryCount { get; set; } = new();
    public int TradesThisHour { get; set; }
    public DateTime TradeHourSlot { get; set; } = DateTime.MinValue;
    public List<TradeRecord> CompletedTrades { get; set; } = new();
    public DateTime LastVolumeResetDate { get; set; } = DateTime.MinValue;
}

public class TradeRecord
{
    public string Symbol { get; set; }
    public string Side { get; set; }
    public string Strategy { get; set; }
    public int Qty { get; set; }
    public decimal Entry { get; set; }
    public decimal Exit { get; set; }
    public decimal NetPnL { get; set; }
    public decimal HoldMinutes { get; set; }
    public string ExitReason { get; set; }
    public string Time { get; set; }
    public string Date { get; set; } = "";
    public string Regime { get; set; } = "";
}

public class LifetimeEquityPoint
{
    public string Date { get; set; }
    public decimal AccountValue { get; set; }
    public decimal DailyPnL { get; set; }
}

public class OpeningRange
{
    public decimal High { get; set; }
    public decimal Low { get; set; }
    public bool IsSet { get; set; }
}

// ══════════════════════════════════════════════════════════
//  SIMULATED BROKER
// ══════════════════════════════════════════════════════════

public class SimulatedBroker
{
    public IBroker RealBroker { get; set; }
    private readonly object _lock = new object();

    // ── TRADING RULES ──────────────────────────────────────
    private decimal TOTAL_BUDGET = 4000m;
    private int MAX_POSITIONS = 3;
    private decimal POSITION_SIZE = 1200m;
    private int MIN_HOLD_SECONDS = 300;
    private decimal DAILY_PROFIT_GOAL = 200m;
    // CHANGE 3: MAX_DAILY_LOSS -80 → -100 — avoids premature halt after 2 normal stops
    private decimal MAX_DAILY_LOSS = -100m;
    private int COOLDOWN_SECONDS = 1800;
    private decimal ATR_TRAIL_MULT = 2.0m;
    private decimal SHORT_ATR_TRAIL = 1.8m;
    private decimal HARD_STOP_ATR_MULT = 2.0m;
    private decimal MAX_LOSS_PER_TRADE = 40m;
    private decimal COMMISSION_PER_SIDE = 1m;
    private decimal MIN_STOP_DISTANCE = 0.10m;
    private int MAX_QTY_SANITY = 500;
    private decimal RISK_PCT = 0.005m;
    private int ORB_MINUTES = 30;
    private decimal VOL_EXPAND_MULT = 1.8m;
    // CHANGE 2: RSI_LONG_MIN 65.0 → 62.0 — recovers valid momentum entries filtered too aggressively 
    //uygar change this to 64 if too much loosing
    private double RSI_LONG_MIN = 62.0;
    private double RSI_SHORT_MAX = 35.0;
    private double RSI_OVERSOLD = 32.0;
    private double RSI_OVERBOUGHT = 68.0;
    private decimal GAP_GO_MIN_PCT = 0.020m;
    private decimal GAP_GO_REL_VOL = 1.8m;
    private int VWAP_CONFIRM_BARS = 2;
    private int MAX_TRADES_PER_DAY = 6;
    private decimal MIN_ATR_PCT = 0.003m;
    private decimal MAX_ATR_PCT = 0.015m;
    private decimal MIN_RR_RATIO = 1.5m;

    private decimal VIX_REDUCE_THRESHOLD = 25m;
    private decimal VIX_NO_LONG_THRESHOLD = 35m;

    // CHANGE 1: MIN_SETUP_SCORE 40 → 45 — filters the weakest 15-20% of setups
    private int MIN_SETUP_SCORE = 45;

    private int MAX_CONSECUTIVE_LOSSES = 3;

    private bool STRATEGY_ORB_ENABLED = true;
    private bool STRATEGY_GAP_GO_ENABLED = true;
    private bool STRATEGY_VWAP_ENABLED = true;
    private bool STRATEGY_MEAN_REV_ENABLED = false;
    private bool STRATEGY_BB_MR_ENABLED = false;
    private bool STRATEGY_MOMENTUM_ENABLED = true;
    private bool STRATEGY_EMA_POCKET_ENABLED = false;
    private bool STRATEGY_OUTSIDE_CANDLE_ENABLED = false;
    private int DATA_LINES_PER_SYMBOL = 1;
    private int MAX_MARKET_DATA_LINES = 95;

    private bool _allowShorts = true;

    // ── STATE ──────────────────────────────────────────────
    public readonly ConcurrentDictionary<string, List<Candle>> _marketData = new();
    private Dictionary<string, SimPosition> _positions = new();
    private readonly ConcurrentDictionary<int, TrackedOrder> _ordersById = new();
    private readonly List<string> _tradeHistoryLog = new();
    private readonly List<TradeRecord> _completedTrades = new();
    private readonly Dictionary<string, DateTime> _lastTradeTime = new();
    private readonly Dictionary<string, long> _dailyVolume = new();
    private readonly ConcurrentDictionary<string, decimal> _latestTick = new();
    private readonly ConcurrentDictionary<string, Candle> _currentMinuteCandle = new();
    private readonly List<(DateTime time, decimal equity)> _equityCurve = new();
    private readonly List<LifetimeEquityPoint> _lifetimeEquity = new();

    private readonly List<TradeRecord> _allTrades = new();
    private static readonly string ALL_TRADES_FILE = StatePath("all_trades.json");

    private static readonly string STATE_DIR = AppDomain.CurrentDomain.BaseDirectory;
    private static string StatePath(string filename) => Path.Combine(AppDomain.CurrentDomain.BaseDirectory, filename);
    private static readonly string LIFETIME_EQUITY_FILE = StatePath("lifetime_equity.json");

    private readonly ConcurrentDictionary<string, OpeningRange> _orbRanges = new();
    private readonly ConcurrentDictionary<string, decimal> _dailyGapPct = new();
    private readonly ConcurrentDictionary<string, List<Candle>> _dailyCandles = new();
    private readonly ConcurrentDictionary<string, decimal> _prevDayHighLevel = new();
    private readonly ConcurrentDictionary<string, decimal> _prevDayLowLevel = new();

    private sealed class SymIndicators
    {
        public double Rsi14;
        public double Rsi14Prev;
        public decimal Atr14;
        public decimal Sma20;
        public decimal Sma50;
        public double Ema9;
        public double Ema21;
        public double Ema9Prev;
        public double Ema21Prev;
        public int MacdDir;
        public decimal RecentHigh8;
        public decimal RecentLow8;
        public bool VolExpansion;
    }
    private readonly ConcurrentDictionary<string, SymIndicators> _indicatorCache = new();
    private readonly ConcurrentDictionary<string, (decimal ema20, DateTime barTime)> _ema20_15min = new();
    private readonly ConcurrentDictionary<string, (double ema50, DateTime barTime)> _ema50_30min = new();

    private int _winCount = 0;
    private int _lossCount = 0;
    private int _consecutiveLosses = 0;

    private string _marketRegime = "UNKNOWN";

    private volatile bool _spyBullish = false;
    private volatile bool _spyBearish = false;
    private volatile bool _spyOpenBearish = false;
    private volatile bool _spyBiasChecked = false;

    private readonly ConcurrentDictionary<string, decimal> _latestBid = new();
    private readonly ConcurrentDictionary<string, decimal> _latestAsk = new();

    private bool MIDDAY_FILTER_ENABLED = true;

    private readonly ConcurrentDictionary<string, (decimal SumPV, long SumVol)> _vwapAccum = new();
    private readonly ConcurrentDictionary<string, decimal> _vwap = new();
    private readonly ConcurrentDictionary<string, decimal> _prevDayClose = new();
    private readonly ConcurrentDictionary<string, bool> _prevBarAboveVwap = new();

    private ConcurrentDictionary<string, string> _pendingStrategyTag = new();
    private ConcurrentDictionary<string, decimal> _pendingInitialRisk = new();

    private readonly HashSet<string> _earningsBlacklist = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, (int stopId, int targetId)> _pendingBracketChildren = new();

    private int _pendingEntryCount = 0;

    private readonly Dictionary<string, bool> _lastTradeWasLoss = new();
    private readonly Dictionary<string, int> _dailyEntryCount = new();

    private decimal _totalRealizedPnL = 0m;
    private int _tradesToday = 0;
    private int _tradesThisHour = 0;
    private int MAX_TRADES_PER_HOUR = 2;
    private DateTime _currentTradeHour = DateTime.MinValue;
    private bool _haltTrading = false;
    private bool _eodSent = false;
    private DateTime _lastVolumeResetEt = DateTime.MinValue;
    private DateTime _lastMemorySave = DateTime.MinValue;
    private DateTime _lastStateSave = DateTime.MinValue;

    private volatile bool _reconciled = false;
    private volatile bool _needsReconciliation = false;
    private readonly Dictionary<string, (int qty, decimal avgCost)> _ibkrPositionSnapshot = new();

    private static readonly TimeZoneInfo Eastern =
        TimeZoneInfo.FindSystemTimeZoneById("America/New_York");
    private static readonly TimeZoneInfo Pacific =
        TimeZoneInfo.FindSystemTimeZoneById("America/Vancouver");

    private const string EmailFrom = "uygargunay@gmail.com";
    private const string EmailTo = "uygargunay@gmail.com";
    private static readonly string EmailPassword =
        Environment.GetEnvironmentVariable("BOT_EMAIL_PASS") ?? "sznd kafk nhec skqh";

    public string[] _watchlist =
    {
        // Tier 1 — Regime anchors (always on)
        "SPY", "QQQ", "IWM",

        // Tier 2 — Mega-cap momentum (large ATR$, pristine fills)
        "NVDA", "TSLA", "META", "AMD", "NFLX", "COIN",

        // Tier 3 — High-beta growth (best Gap&Go + ORB candidates)
        "PLTR", "MSTR", "HOOD", "SOFI", "SNAP", "RBLX",
        "RIVN", "UPST", "AFRM", "APP", "CRWD", "DDOG",
        "NET", "MDB", "IONQ", "SMCI", "RXRX", "SOUN",
        "ACHR", "JOBY"
    };

    private readonly HashSet<string> _subscribedSymbols = new(StringComparer.OrdinalIgnoreCase);
    private int _previousOrbMinutes;

    private static readonly Dictionary<string, string> _symbolSectors
        = new(StringComparer.OrdinalIgnoreCase)
    {
        {"SPY","sp500_etf"},{"QQQ","nasdaq_etf"},{"IWM","small_cap_etf"},
        {"SMH","semi_etf"},{"XLK","tech_etf"},{"XLF","fin_etf"},
        {"XLE","energy_etf"},{"XBI","biotech_etf"},{"DIA","dow_etf"},
        {"AAPL","megacap_tech"},{"MSFT","megacap_tech"},{"NVDA","megacap_tech"},
        {"META","megacap_tech"},{"AMZN","megacap_tech"},{"GOOGL","megacap_tech"},
        {"TSLA","auto_ev"},{"ADBE","design_sw"},
        {"AMD","semiconductor"},{"AVGO","semiconductor"},{"ARM","semiconductor"},
        {"MU","semiconductor"},{"AMAT","semiconductor"},{"LRCX","semiconductor"},
        {"QCOM","semiconductor"},{"TSM","semiconductor"},{"TXN","semiconductor"},
        {"ASML","semiconductor"},{"SMCI","semiconductor"},
        {"CRM","cloud_saas"},{"NOW","cloud_saas"},{"CRWD","cloud_saas"},
        {"PANW","cloud_saas"},{"PLTR","cloud_saas"},{"DDOG","cloud_saas"},
        {"NET","cloud_saas"},{"SNOW","cloud_saas"},{"APP","cloud_saas"},
        {"TTD","cloud_saas"},{"ZS","cloud_saas"},{"MDB","cloud_saas"},{"OKTA","cloud_saas"},
        {"COIN","crypto"},{"MSTR","crypto"},
        {"PYPL","fintech"},{"HOOD","fintech"},{"SOFI","fintech"},
        {"UPST","fintech"},{"AFRM","fintech"},
        {"JPM","bank"},{"GS","bank"},{"MS","bank"},{"BAC","bank"},
        {"V","payments"},{"MA","payments"},{"BX","alt_finance"},
        {"NFLX","streaming"},{"SPOT","streaming"},
        {"UBER","mobility"},{"DASH","delivery"},
        {"SHOP","ecommerce"},{"MELI","ecommerce"},{"RBLX","gaming"},
        {"SNAP","social_media"},
        {"BKNG","travel"},{"ABNB","travel"},
        {"INTU","fin_software"},{"ANET","networking"},
        {"RIVN","auto_ev"},{"LCID","auto_ev"},
        {"ACHR","evtol"},{"JOBY","evtol"},
        {"IONQ","quantum"},{"RXRX","ai_biotech"},{"SOUN","ai_audio"},
        {"XOM","energy"},{"CVX","energy"},{"OXY","energy"},
        {"UNH","healthcare"},{"ABBV","pharma"},
        {"VRTX","biotech"},{"REGN","biotech"},{"GILD","biotech"},
        {"CAT","industrial"},{"DE","industrial"},{"GE","industrial"},{"HON","industrial"},
        {"RTX","defense"},{"LMT","defense"},{"BA","defense"},
        {"ORCL","enterprise_sw"},{"IBM","enterprise_sw"},
    };

    private Timer _dashboardTimer;

    // ══════════════════════════════════════════════════════════
    //  MARKET DATA INGESTION
    // ══════════════════════════════════════════════════════════

    public void UpdateLiveTick(string symbol, decimal price, long size)
    {
        try
        {
            _latestTick[symbol] = price;

            var nowEt = GetEasternTime();
            var minute = new DateTime(nowEt.Year, nowEt.Month, nowEt.Day,
                                      nowEt.Hour, nowEt.Minute, 0);

            UpdateVwap(symbol, price, size);
            UpdateOpeningRange(symbol, price, nowEt);

            var current = _currentMinuteCandle.GetOrAdd(symbol, _ => new Candle
            {
                Time = minute,
                Open = price,
                High = price,
                Low = price,
                Close = price,
                Volume = size
            });

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

            CheckHardStop(symbol, price);
            CheckExits(symbol, price);

            if ((DateTime.UtcNow - _lastMemorySave).TotalMinutes >= 1)
            {
                SaveMarketMemory();
                _lastMemorySave = DateTime.UtcNow;
            }
            if ((DateTime.UtcNow - _lastStateSave).TotalMinutes >= 2)
            {
                SaveState();
                _lastStateSave = DateTime.UtcNow;
            }
        }
        catch (Exception ex)
        {
            LogError("UpdateLiveTick " + symbol, ex.Message);
        }
    }

    public void UpdateBidAsk(string symbol, decimal bid, decimal ask)
    {
        if (bid > 0) _latestBid[symbol] = bid;
        if (ask > 0) _latestAsk[symbol] = ask;
    }

    private void UpdateVwap(string symbol, decimal price, long size)
    {
        if (size <= 0) return;
        var nowEt = GetEasternTime();

        if (nowEt.Hour == 9 && nowEt.Minute == 30 && nowEt.Second < 5)
            _vwapAccum[symbol] = (0m, 0L);

        var cur = _vwapAccum.GetOrAdd(symbol, _ => (0m, 0L));
        var updated = (cur.SumPV + price * size, cur.SumVol + size);
        _vwapAccum[symbol] = updated;

        if (updated.Item2 > 0)
            _vwap[symbol] = updated.Item1 / updated.Item2;
    }

    private void UpdateOpeningRange(string symbol, decimal price, DateTime etNow)
    {
        if (etNow.Hour == 9 && etNow.Minute == 30 && etNow.Second < 5)
        {
            _orbRanges[symbol] = new OpeningRange { High = price, Low = price, IsSet = false };
            return;
        }

        int minutesSinceOpen = (etNow.Hour - 9) * 60 + etNow.Minute - 30;
        if (minutesSinceOpen < 0 || minutesSinceOpen > ORB_MINUTES) return;

        var orb = _orbRanges.GetOrAdd(symbol, _ => new OpeningRange { High = price, Low = price });
        orb.High = Math.Max(orb.High, price);
        orb.Low = Math.Min(orb.Low, price);

        if (minutesSinceOpen >= ORB_MINUTES)
            orb.IsSet = true;
    }

    private void FinalizeCandle(string symbol, Candle candle)
    {
        var list = _marketData.GetOrAdd(symbol, _ => new List<Candle>());

        lock (list)
        {
            if (!list.Any(c => c.Time == candle.Time))
                list.Add(candle);
            if (list.Count > 500)
                list.RemoveAt(0);
        }

        var nowEt = GetEasternTime();
        if (nowEt.Hour == 9 && nowEt.Minute == 29)
            _prevDayClose[symbol] = candle.Close;

        _vwap.TryGetValue(symbol, out decimal vwapNow);
        bool aboveVwap = vwapNow > 0 && candle.Close > vwapNow;
        _prevBarAboveVwap.TryGetValue(symbol, out bool wasAbove);
        _prevBarAboveVwap[symbol] = aboveVwap;

        if (_marketData.TryGetValue(symbol, out var cacheCandles))
        {
            RefreshIndicatorCache(symbol, cacheCandles);
            Refresh15MinEma(symbol, cacheCandles);
        }

        ExecuteStrategy(symbol, wasAbove, aboveVwap);
        UpdateMarketRegime();
    }

    public void OnTradeTick(string symbol, long size)
    {
        lock (_lock)
        {
            if (!_dailyVolume.ContainsKey(symbol)) _dailyVolume[symbol] = 0;
            _dailyVolume[symbol] += size;
        }
    }

    // ══════════════════════════════════════════════════════════
    //  MARKET REGIME CLASSIFIER
    // ══════════════════════════════════════════════════════════

    private void UpdateMarketRegime()
    {
        if (!_marketData.TryGetValue("SPY", out var spy) || spy.Count < 30) return;

        decimal spySma20 = SafeSMA(spy, 20);
        decimal spySma50 = SafeSMA(spy, 50);
        decimal spyLast = spy.Last().Close;

        var todayEt = GetEasternTime().Date;
        var todayCandles = spy.Where(c => c.Time.Date == todayEt).ToList();
        decimal todayRange = todayCandles.Count > 1
            ? todayCandles.Max(c => c.High) - todayCandles.Min(c => c.Low) : 0;
        decimal avgDailyRange = SafeATR(spy, 14);

        if (spyLast < spySma20 && spySma20 < spySma50) _marketRegime = "SELL-OFF";
        else if (todayRange > avgDailyRange * 1.5m && spyLast > spySma20) _marketRegime = "TRENDING";
        else if (todayRange < avgDailyRange * 0.6m) _marketRegime = "CHOPPY";
        else _marketRegime = "NORMAL";

        if (!_spyBiasChecked && todayCandles.Count >= 30)
        {
            decimal spyOpenPrice = todayCandles.First().Open;
            if (spyOpenPrice > 0)
            {
                decimal spyFirstHalfClose = todayCandles[Math.Min(29, todayCandles.Count - 1)].Close;
                decimal openBiasPct = (spyFirstHalfClose - spyOpenPrice) / spyOpenPrice;
                _spyOpenBearish = openBiasPct < -0.003m;
                _spyBiasChecked = true;
                if (_spyOpenBearish)
                    LogMessage($"[REGIME] SPY opening bias: BEARISH ({openBiasPct:P2} from open) — blocking all longs today");
                else
                    LogMessage($"[REGIME] SPY opening bias: BULLISH/NEUTRAL ({openBiasPct:P2})");
            }
        }

        if (spy.Count >= 25)
        {
            var closes = spy.Select(c => (double)c.Close).ToArray();
            double ema20Now = CalcEMA(closes, 20);

            var closesPrev5 = closes.Length > 5 ? closes.Take(closes.Length - 5).ToArray() : closes;
            double ema20Prev = closesPrev5.Length >= 20 ? CalcEMA(closesPrev5, 20) : ema20Now;

            _spyBullish = (double)spyLast > ema20Now && ema20Now > ema20Prev;
            _spyBearish = (double)spyLast < ema20Now && ema20Now < ema20Prev;
        }
    }

    // ══════════════════════════════════════════════════════════
    //  EXECUTE STRATEGY — DISPATCHER
    // ══════════════════════════════════════════════════════════

    public void ExecuteStrategy(string symbol, bool prevBarAboveVwap = false, bool currBarAboveVwap = false)
    {
        CheckDailyReset();
        if (_haltTrading) return;

        if (!_watchlist.Contains(symbol, StringComparer.OrdinalIgnoreCase)) return;

        if (!_reconciled)
        {
            LogMessage("[SKIP] Waiting for IBKR position reconciliation before trading.");
            return;
        }

        var nowEt = GetEasternTime();
        if (nowEt.DayOfWeek == DayOfWeek.Saturday || nowEt.DayOfWeek == DayOfWeek.Sunday) return;
        if (nowEt.Hour < 9 || (nowEt.Hour == 9 && nowEt.Minute < 32)) return;
        if (nowEt.Hour > 14 || (nowEt.Hour == 14 && nowEt.Minute >= 30)) return;

        // ── CHANGE 4: OpEx Friday filter ──────────────────────────────────────
        // 3rd Friday of month = quarterly options expiration. Volume spikes are
        // options-driven noise, not directional. MMs pin to max-pain levels,
        // breakouts reverse violently. Cap at 2 trades on these days.
        bool isOpExFriday = nowEt.DayOfWeek == DayOfWeek.Friday
            && nowEt.Day >= 15 && nowEt.Day <= 21;
        if (isOpExFriday && _tradesToday >= 2)
        {
            LogMessage($"[OPEX SKIP] OpEx Friday — capped at 2 trades, skipping new entries");
            return;
        }

        if (_earningsBlacklist.Contains(symbol))
        {
            LogMessage($"[EARNINGS SKIP] {symbol} is on today's earnings blacklist — no new entries");
            return;
        }

        if (MIDDAY_FILTER_ENABLED &&
            _marketRegime != "TRENDING" &&
            (nowEt.Hour == 11 && nowEt.Minute >= 45 ||
             nowEt.Hour == 12 ||
             nowEt.Hour == 13 && nowEt.Minute < 05))
            return;

        if (!_marketData.TryGetValue(symbol, out var candles) || candles.Count < 50)
        {
            LogMessage($"[SKIP] {symbol} not enough candles: {candles?.Count ?? 0}");
            return;
        }

        if (candles.TakeLast(300).Sum(c => c.Volume) < 500_000) return;

        decimal lastPrice = candles.Last().Close;
        if (lastPrice < 10m) return;

        if (_indicatorCache.TryGetValue(symbol, out var preInd))
        {
            bool orbActive = _orbRanges.TryGetValue(symbol, out var preOrb) && preOrb.IsSet
                                && (lastPrice > preOrb.High || lastPrice < preOrb.Low);
            bool rsiExtreme = preInd.Rsi14 < RSI_OVERSOLD || preInd.Rsi14 > RSI_OVERBOUGHT
                            || preInd.Rsi14 > RSI_LONG_MIN || preInd.Rsi14 < RSI_SHORT_MAX;
            _vwap.TryGetValue(symbol, out decimal preVwap);
            bool nearVwap = preVwap > 0 && Math.Abs(lastPrice - preVwap) <= preInd.Atr14 * 1.5m;
            _prevDayClose.TryGetValue(symbol, out decimal prevCloseCheck);
            bool gapActive = prevCloseCheck > 0
                                && Math.Abs((lastPrice - prevCloseCheck) / prevCloseCheck) >= GAP_GO_MIN_PCT;
            if (!orbActive && !rsiExtreme && !nearVwap && !gapActive && !preInd.VolExpansion)
                return;
        }

        if (_latestBid.TryGetValue(symbol, out decimal bid) &&
            _latestAsk.TryGetValue(symbol, out decimal ask) &&
            bid > 0 && ask > 0)
        {
            decimal spreadPct = (ask - bid) / ask;
            const decimal maxSpreadPct = 0.0015m;
            if (spreadPct > maxSpreadPct)
            {
                LogMessage($"[REJECT] {symbol} spread {spreadPct:P3} > hard max {maxSpreadPct:P3} (bid={bid:F2} ask={ask:F2})");
                return;
            }
        }

        if (candles.Count >= 11 && IsLiquiditySweep(candles))
        {
            LogMessage($"[SWEEP SKIP] {symbol} last candle is a liquidity sweep — skipping");
            return;
        }

        {
            var todayCandlesRange = candles.Where(c => c.Time.Date == nowEt.Date).ToList();
            if (todayCandlesRange.Count >= 20)
            {
                decimal dayHigh = todayCandlesRange.Max(c => c.High);
                decimal dayLow = todayCandlesRange.Min(c => c.Low);
                decimal dayRngPct = lastPrice > 0 ? (dayHigh - dayLow) / lastPrice : 0m;
                if (dayRngPct < 0.006m)
                {
                    LogMessage($"[RANGE SKIP] {symbol} daily range {dayRngPct:P2} < 0.6% — stock too dormant");
                    return;
                }
            }
        }

        lock (_lock)
        {
            if (_tradesToday >= MAX_TRADES_PER_DAY) return;

            var hourSlot = new DateTime(nowEt.Year, nowEt.Month, nowEt.Day, nowEt.Hour, 0, 0);
            if (hourSlot != _currentTradeHour) { _currentTradeHour = hourSlot; _tradesThisHour = 0; }
            if (_tradesThisHour >= MAX_TRADES_PER_HOUR) return;

            if (_positions.ContainsKey(symbol)) return;
            if (_positions.Count + _pendingEntryCount >= MAX_POSITIONS) return;

            decimal deployedCapital = _positions.Values.Sum(p => p.AvgPrice * p.Quantity)
                                    + _pendingEntryCount * POSITION_SIZE;
            if (TOTAL_BUDGET - deployedCapital < POSITION_SIZE) return;
            if (_lastTradeTime.TryGetValue(symbol, out var lastTime))
            {
                int cooldown = _lastTradeWasLoss.GetValueOrDefault(symbol)
                    ? COOLDOWN_SECONDS * 2
                    : COOLDOWN_SECONDS;
                if ((DateTime.UtcNow - lastTime).TotalSeconds < cooldown) return;
            }
            if (_dailyEntryCount.GetValueOrDefault(symbol) >= 2) return;

            int minutesSinceOpen = (nowEt.Hour - 9) * 60 + nowEt.Minute - 30;

            int setupScore = ScoreSetup(symbol, candles);
            if (setupScore < MIN_SETUP_SCORE)
            {
                LogMessage($"[QUALITY SKIP] {symbol} setup score {setupScore} < {MIN_SETUP_SCORE} — skipping");
                return;
            }

            // 1. Opening Range Breakout (only after ORB window closes)
            if (STRATEGY_ORB_ENABLED && minutesSinceOpen > ORB_MINUTES)
                if (TryOrbStrategy(symbol, candles, nowEt)) return;

            // CHANGE 5: Gap&Go end time — regime-dependent to avoid post-11am stale gaps
            // TRENDING days: allow full 150-min window. Non-trending: cut to 120 min (11:00 ET).
            int gapGoEnd = _marketRegime == "TRENDING" ? 150 : 120;
            if (STRATEGY_GAP_GO_ENABLED && minutesSinceOpen >= 45 && minutesSinceOpen <= gapGoEnd)
                if (TryGapAndGoStrategy(symbol, candles)) return;

            // 3. VWAP Bounce / Reclaim
            if (STRATEGY_VWAP_ENABLED)
                if (TryVwapBounceStrategy(symbol, candles, prevBarAboveVwap, currBarAboveVwap)) return;

            // 4. RSI Mean Reversion
            if (STRATEGY_MEAN_REV_ENABLED)
                if (TryMeanReversionStrategy(symbol, candles)) return;

            // 4b. Bollinger Band Mean Reversion
            if (STRATEGY_BB_MR_ENABLED)
                if (TryBollingerMeanReversionStrategy(symbol, candles)) return;

            // 5. Momentum Breakout + Continuation
            if (STRATEGY_MOMENTUM_ENABLED)
                if (TryMomentumStrategy(symbol, candles)) return;

            // 6. EMA Pocket
            if (STRATEGY_EMA_POCKET_ENABLED)
                if (TryEmaPocketStrategy(symbol, candles)) return;

            // 7. Outside Candle
            if (STRATEGY_OUTSIDE_CANDLE_ENABLED)
                TryOutsideCandleStrategy(symbol, candles);
        }
    }

    // ══════════════════════════════════════════════════════════
    //  STRATEGY 8 — BOLLINGER BAND MEAN REVERSION
    // ══════════════════════════════════════════════════════════

    private bool TryBollingerMeanReversionStrategy(string symbol, List<Candle> candles)
    {
        if (candles.Count < 20) return false;

        if (_marketRegime != "CHOPPY" && _marketRegime != "SELL-OFF") return false;

        var lastCandle = candles.Last();
        decimal close = lastCandle.Close;

        if (!_indicatorCache.TryGetValue(symbol, out var ind)) return false;
        decimal sma20 = ind.Sma20;
        decimal sma50 = ind.Sma50;
        double rsi = ind.Rsi14;
        double prevRsi = ind.Rsi14Prev;
        decimal atr = ind.Atr14;

        if (close > 0 && (atr / close < MIN_ATR_PCT || (MAX_ATR_PCT > 0 && atr / close > MAX_ATR_PCT))) return false;
        if (sma20 <= 0) return false;

        var last20 = candles.TakeLast(20).Select(c => (double)c.Close).ToArray();
        double mean = last20.Average();
        double stdDev = Math.Sqrt(last20.Average(x => (x - mean) * (x - mean)));
        if (stdDev <= 0) return false;

        decimal upperBand = (decimal)(mean + 2.0 * stdDev);
        decimal lowerBand = (decimal)(mean - 2.0 * stdDev);

        decimal bandWidth = upperBand - lowerBand;
        if (sma20 > 0 && bandWidth / sma20 > 0.04m) return false;

        bool touchLower = close <= lowerBand && rsi < 40.0 && sma50 > 0 && close > sma50
                        && rsi > prevRsi;
        if (touchLower && (_marketRegime != "SELL-OFF" || CheckStrongRelativeStrength(symbol, candles)))
        {
            decimal stopDistance = Math.Max(atr * HARD_STOP_ATR_MULT, MIN_STOP_DISTANCE);
            int qty = CalcQty(close, stopDistance);
            if (qty <= 0) return false;
            OpenPosition(symbol, qty, close, TradeSide.Buy, false, "BB_MEAN_REV_LONG");
            return true;
        }

        bool touchUpper = close >= upperBand && rsi > 60.0 && sma50 > 0 && close < sma50
                        && rsi < prevRsi;
        if (touchUpper && _allowShorts)
        {
            decimal stopDistance = Math.Max(atr * HARD_STOP_ATR_MULT, MIN_STOP_DISTANCE);
            int qty = CalcQty(close, stopDistance);
            if (qty <= 0) return false;
            OpenPosition(symbol, qty, close, TradeSide.Sell, true, "BB_MEAN_REV_SHORT");
            return true;
        }

        return false;
    }

    // ══════════════════════════════════════════════════════════
    //  STRATEGY 1 — OPENING RANGE BREAKOUT (ORB)
    // ══════════════════════════════════════════════════════════

    private bool TryOrbStrategy(string symbol, List<Candle> candles, DateTime nowEt)
    {
        // CHANGE 6: Allow NORMAL regime for ORB — previously blocked clean setups on normal days.
        // NORMAL requires a stricter 3-bar hold confirmation to compensate for reduced regime strength.
        if (_marketRegime != "TRENDING" && _marketRegime != "SELL-OFF" && _marketRegime != "NORMAL") return false;

        if (!_orbRanges.TryGetValue(symbol, out var orb) || !orb.IsSet) return false;
        if (orb.High <= orb.Low) return false;

        var lastCandle = candles.Last();
        decimal close = lastCandle.Close;

        if (!_indicatorCache.TryGetValue(symbol, out var ind)) return false;
        decimal atr = ind.Atr14;
        double rsi = ind.Rsi14;

        if (close > 0 && atr / close < MIN_ATR_PCT) return false;
        if (MAX_ATR_PCT > 0 && close > 0 && atr / close > MAX_ATR_PCT) return false;

        decimal orbSma20 = ind.Sma20; decimal orbSma50 = ind.Sma50;
        if (orbSma20 > 0 && orbSma50 > 0 && close > 0 && Math.Abs(orbSma20 - orbSma50) / close < 0.002m) return false;

        _vwap.TryGetValue(symbol, out decimal vwapVal);

        var last10Vols = candles.TakeLast(10).ToList();
        long avgVol10 = last10Vols.Count > 0 ? (long)last10Vols.Average(c => c.Volume) : 0;
        bool lastBarVolOk = candles.Last().Volume >= avgVol10;

        // CHANGE 6 (continued): NORMAL regime requires 3-bar hold; TRENDING/SELL-OFF keep 2-bar hold.
        // 3-bar hold significantly reduces false breakouts on lower-conviction normal days.
        int requiredHoldBars = _marketRegime == "NORMAL" ? 3 : 2;

        bool orbLongHold = candles.Count >= requiredHoldBars
                        && candles.TakeLast(requiredHoldBars).All(c => c.Close > orb.High);
        if (orbLongHold && lastBarVolOk && rsi > RSI_LONG_MIN)
        {
            if (!_spyBullish)
            {
                LogMessage($"[ORB SKIP] {symbol} ORB_LONG blocked — SPY bias not bullish enough");
                goto TryShortOrb;
            }

            if (_marketRegime == "SELL-OFF")
            {
                LogMessage($"[ORB SKIP] {symbol} ORB_LONG blocked — SELL-OFF regime");
                goto TryShortOrb;
            }
            if (_spyOpenBearish)
            {
                LogMessage($"[ORB SKIP] {symbol} ORB_LONG blocked — bearish SPY open bias");
                goto TryShortOrb;
            }

            if (_prevDayClose.TryGetValue(symbol, out decimal prevCloseOrb) && prevCloseOrb > 0)
            {
                decimal stockDayPct = (close - prevCloseOrb) / prevCloseOrb;
                if (stockDayPct < -0.002m)
                {
                    LogMessage($"[ORB SKIP] {symbol} ORB_LONG blocked — stock down {stockDayPct:P2} on day");
                    goto TryShortOrb;
                }
            }

            if (_ema20_15min.TryGetValue(symbol, out var ema15L) && ema15L.ema20 > 0
                && close < ema15L.ema20) goto TryShortOrb;

            var (pdHigh, _) = GetPrevDayHL(symbol);
            if (pdHigh > 0 && close < pdHigh && close >= pdHigh * 0.9975m) goto TryShortOrb;

            decimal stopDistance = Math.Max(atr * HARD_STOP_ATR_MULT, MIN_STOP_DISTANCE);
            int qty = CalcQty(close, stopDistance);
            if (qty <= 0) return false;

            OpenPosition(symbol, qty, close, TradeSide.Buy, false, "ORB_LONG");
            return true;
        }

        TryShortOrb:
        bool orbShortHold = candles.Count >= requiredHoldBars
                         && candles.TakeLast(requiredHoldBars).All(c => c.Close < orb.Low);
        if (orbShortHold && lastBarVolOk && rsi < RSI_SHORT_MAX && _allowShorts)
        {
            if (!_spyBearish && _marketRegime != "SELL-OFF") return false;

            if (_ema20_15min.TryGetValue(symbol, out var ema15S) && ema15S.ema20 > 0
                && close > ema15S.ema20) return false;

            var (_, pdLow) = GetPrevDayHL(symbol);
            if (pdLow > 0 && close > pdLow && close <= pdLow * 1.0025m) return false;

            decimal stopDistance = Math.Max(atr * HARD_STOP_ATR_MULT, MIN_STOP_DISTANCE);
            int qty = CalcQty(close, stopDistance);
            if (qty <= 0) return false;

            OpenPosition(symbol, qty, close, TradeSide.Sell, true, "ORB_SHORT");
            return true;
        }

        return false;
    }

    // ══════════════════════════════════════════════════════════
    //  STRATEGY 2 — GAP AND GO
    // ══════════════════════════════════════════════════════════

    private bool TryGapAndGoStrategy(string symbol, List<Candle> candles)
    {
        if (!_prevDayClose.TryGetValue(symbol, out decimal prevClose) || prevClose <= 0) return false;
        if (!_latestTick.TryGetValue(symbol, out decimal currentPrice) || currentPrice <= 0) return false;
        decimal gapPct = (currentPrice - prevClose) / prevClose;

        if (Math.Abs(gapPct) < GAP_GO_MIN_PCT) return false;

        var todayEt = GetEasternTime().Date;
        long todayVol = _dailyVolume.GetValueOrDefault(symbol);
        int minutesToday = Math.Max(1, (GetEasternTime().Hour - 9) * 60 + GetEasternTime().Minute - 30);
        long avg20Vol = (long)(candles
            .GroupBy(c => c.Time.Date)
            .OrderByDescending(g => g.Key)
            .Take(20)
            .Select(g => (double)g.Sum(c => c.Volume) / Math.Max(1, g.Count()))
            .DefaultIfEmpty(0)
            .Average());
        double todayRate = todayVol / (double)minutesToday;
        bool highRelVol = avg20Vol > 0 && todayRate > avg20Vol * (double)GAP_GO_REL_VOL;
        if (!highRelVol) return false;

        var lastCandle = candles.Last();
        decimal close = lastCandle.Close;

        if (!_indicatorCache.TryGetValue(symbol, out var ind)) return false;
        double rsi = ind.Rsi14;
        decimal atr = ind.Atr14;
        bool volExp = ind.VolExpansion;

        if (close > 0 && atr / close < MIN_ATR_PCT) return false;
        if (gapPct > 0 && rsi > RSI_LONG_MIN && _marketRegime != "SELL-OFF" && !_spyOpenBearish)
        {
            decimal sma200 = GetDailySma200(symbol);
            if (sma200 > 0 && close < sma200 * 0.97m) return false;

            decimal stopDistance = Math.Max(atr * HARD_STOP_ATR_MULT, MIN_STOP_DISTANCE);
            int qty = CalcQty(close, stopDistance);
            if (qty <= 0) return false;

            OpenPosition(symbol, qty, close, TradeSide.Buy, false, "GAP_GO_LONG");
            return true;
        }

        if (gapPct < 0 && rsi < RSI_SHORT_MAX && _allowShorts)
        {
            decimal sma200 = GetDailySma200(symbol);
            if (sma200 > 0 && close > sma200 && _marketRegime != "SELL-OFF") return false;

            decimal stopDistance = Math.Max(atr * HARD_STOP_ATR_MULT, MIN_STOP_DISTANCE);
            int qty = CalcQty(close, stopDistance);
            if (qty <= 0) return false;

            OpenPosition(symbol, qty, close, TradeSide.Sell, true, "GAP_GO_SHORT");
            return true;
        }

        return false;
    }

    // ══════════════════════════════════════════════════════════
    //  STRATEGY 3 — VWAP BOUNCE / RECLAIM
    // ══════════════════════════════════════════════════════════

    private bool TryVwapBounceStrategy(string symbol, List<Candle> candles,
                                       bool prevAbove, bool currAbove)
    {
        _vwap.TryGetValue(symbol, out decimal vwapVal);
        if (vwapVal <= 0) return false;

        if (candles.Count < VWAP_CONFIRM_BARS) return false;
        var recentBars = candles.TakeLast(VWAP_CONFIRM_BARS).ToList();
        bool allRecentAbove = recentBars.All(c => c.Close > vwapVal);
        bool allRecentBelow = recentBars.All(c => c.Close < vwapVal);

        var lastCandle = candles.Last();
        decimal close = lastCandle.Close;

        if (!_indicatorCache.TryGetValue(symbol, out var ind)) return false;
        double rsi = ind.Rsi14;
        decimal atr = ind.Atr14;
        bool volExp = ind.VolExpansion;

        if (close > 0 && atr / close < MIN_ATR_PCT) return false;
        bool vwapReclaim = !prevAbove && allRecentAbove;

        // CHANGE 7: Block VWAP reclaim longs on CHOPPY — coin-flip regime kills VWAP win rate.
        // VWAP reclaims only work when there's a real trend carrying price above VWAP.
        // In CHOPPY, price oscillates through VWAP repeatedly with no follow-through.
        if (vwapReclaim && volExp && rsi > RSI_LONG_MIN
            && _marketRegime != "SELL-OFF"
            && _marketRegime != "CHOPPY"   // NEW: skip reclaims in choppy — no trend to sustain them
            && !_spyOpenBearish)
        {
            var (pdHigh, _) = GetPrevDayHL(symbol);
            if (pdHigh > 0 && close < pdHigh && close >= pdHigh * 0.997m) return false;

            decimal stopDistance = Math.Max(atr * HARD_STOP_ATR_MULT, MIN_STOP_DISTANCE);
            int qty = CalcQty(close, stopDistance);
            if (qty <= 0) return false;
            OpenPosition(symbol, qty, close, TradeSide.Buy, false, "VWAP_RECLAIM");
            return true;
        }

        bool vwapRejection = prevAbove && allRecentBelow;
        if (vwapRejection && volExp && rsi < RSI_SHORT_MAX && _allowShorts)
        {
            var (_, pdLow) = GetPrevDayHL(symbol);
            if (pdLow > 0 && close > pdLow && close <= pdLow * 1.003m) return false;

            decimal stopDistance = Math.Max(atr * HARD_STOP_ATR_MULT, MIN_STOP_DISTANCE);
            int qty = CalcQty(close, stopDistance);
            if (qty <= 0) return false;
            OpenPosition(symbol, qty, close, TradeSide.Sell, true, "VWAP_REJECT_SHORT");
            return true;
        }

        return false;
    }

    // ══════════════════════════════════════════════════════════
    //  STRATEGY 4 — RSI MEAN REVERSION
    // ══════════════════════════════════════════════════════════

    private bool TryMeanReversionStrategy(string symbol, List<Candle> candles)
    {
        if (candles.Count < 50) return false;

        if (_marketRegime != "CHOPPY" && _marketRegime != "SELL-OFF") return false;

        var lastCandle = candles.Last();
        decimal close = lastCandle.Close;

        if (!_indicatorCache.TryGetValue(symbol, out var ind)) return false;
        decimal sma50 = ind.Sma50;
        double rsi = ind.Rsi14;
        double prevRsi = ind.Rsi14Prev;
        decimal atr = ind.Atr14;

        if (close > 0 && (atr / close < MIN_ATR_PCT || (MAX_ATR_PCT > 0 && atr / close > MAX_ATR_PCT))) return false;

        bool oversoldInUptrend = close > sma50 && rsi < RSI_OVERSOLD
                               && rsi > prevRsi
                               && _marketRegime != "SELL-OFF"
                               && !_spyOpenBearish;
        if (oversoldInUptrend)
        {
            decimal stopDistance = Math.Max(atr * HARD_STOP_ATR_MULT, MIN_STOP_DISTANCE);
            int qty = CalcQty(close, stopDistance);
            if (qty <= 0) return false;

            OpenPosition(symbol, qty, close, TradeSide.Buy, false, "MEAN_REV_LONG");
            return true;
        }

        bool overboughtInDowntrend = close < sma50 && rsi > RSI_OVERBOUGHT
                                   && rsi < prevRsi;
        if (overboughtInDowntrend && _allowShorts)
        {
            decimal stopDistance = Math.Max(atr * HARD_STOP_ATR_MULT, MIN_STOP_DISTANCE);
            int qty = CalcQty(close, stopDistance);
            if (qty <= 0) return false;

            OpenPosition(symbol, qty, close, TradeSide.Sell, true, "MEAN_REV_SHORT");
            return true;
        }

        return false;
    }

    // ══════════════════════════════════════════════════════════
    //  STRATEGY 5 — MOMENTUM BREAKOUT & CONTINUATION (relaxed)
    // ══════════════════════════════════════════════════════════

    private bool TryMomentumStrategy(string symbol, List<Candle> candles)
    {
        var lastCandle = candles.Last();
        decimal close = lastCandle.Close;

        if (!_indicatorCache.TryGetValue(symbol, out var ind)) return false;
        decimal sma20 = ind.Sma20;
        decimal sma50 = ind.Sma50;
        double rsi = ind.Rsi14;
        decimal atr = ind.Atr14;
        bool volExp = ind.VolExpansion;
        int macdDir = ind.MacdDir;

        decimal atrPct = close > 0 ? atr / close : 0m;

        if (atrPct < MIN_ATR_PCT) return false;
        if (MAX_ATR_PCT > 0 && atrPct > MAX_ATR_PCT) return false;

        if (sma20 > 0 && sma50 > 0 && close > 0)
        {
            decimal smaGap = Math.Abs(sma20 - sma50) / close;
            if (smaGap < 0.002m) return false;
        }

        _vwap.TryGetValue(symbol, out decimal vwapVal);

        bool regimeStrong = _marketRegime != "SELL-OFF" && !_spyOpenBearish && _spyBullish;
        bool relativeStrength = CheckRelativeStrength(symbol, candles);
        bool rsiConfirm = rsi > RSI_LONG_MIN;
        bool aboveVwap = vwapVal <= 0 || close > vwapVal;
        bool macdBullish = macdDir > 0;

        decimal recentHigh = ind.RecentHigh8;
        decimal range = lastCandle.High - lastCandle.Low;
        decimal avgRange = candles.TakeLast(10).Average(c => c.High - c.Low);
        bool expansion = range > avgRange * 1.3m;
        bool choppyMode = _marketRegime == "CHOPPY";

        bool pullbackEntry = lastCandle.Low <= sma20 && lastCandle.Close > sma20
                              && rsi > 55 && close > sma50 && relativeStrength;
        bool volCompressed = IsVolatilityCompressed(candles);
        bool breakoutSignal = !choppyMode
                           && (_marketRegime == "TRENDING" || _marketRegime == "SELL-OFF")
                           && _spyBullish
                           && expansion && volExp && close > recentHigh && volCompressed;

        bool trendContinuation = (_marketRegime == "NORMAL" || _marketRegime == "TRENDING")
                               && _spyBullish
                               && close > sma20 && close > sma50
                               && rsi > 58 && relativeStrength;

        bool hasSignal = breakoutSignal || pullbackEntry || trendContinuation;

        int required = _marketRegime == "TRENDING" ? 3
                     : _marketRegime == "CHOPPY" ? 5
                     : 4;

        int score = (regimeStrong ? 1 : 0)
                  + (relativeStrength ? 1 : 0)
                  + (rsiConfirm ? 1 : 0)
                  + (aboveVwap ? 1 : 0)
                  + (macdBullish ? 1 : 0);

        if (score >= required && hasSignal && (volExp || pullbackEntry || trendContinuation))
        {
            decimal sma200 = GetDailySma200(symbol);
            if (sma200 > 0 && close < sma200 * 0.97m) return false;

            if (_ema20_15min.TryGetValue(symbol, out var ema15ML) && ema15ML.ema20 > 0
                && close < ema15ML.ema20) return false;

            decimal stopDistance = Math.Max(atr * HARD_STOP_ATR_MULT, MIN_STOP_DISTANCE);
            int qty = CalcQty(close, stopDistance);
            if (qty <= 0) return false;

            OpenPosition(symbol, qty, close, TradeSide.Buy, false, "MOMENTUM_LONG");
            return true;
        }

        bool inShortRegime = _marketRegime == "SELL-OFF" || _marketRegime == "NORMAL" || _marketRegime == "CHOPPY";
        if (inShortRegime && _allowShorts)
        {
            bool rsiShortConfirm = rsi < RSI_SHORT_MAX;
            bool belowVwap = vwapVal > 0 && close < vwapVal;
            bool bearishTape = _spyBearish || _marketRegime == "SELL-OFF";
            bool breakdownSignal = !choppyMode && bearishTape && expansion && volExp
                                   && close < ind.RecentLow8 && volCompressed;
            bool macdBearish = macdDir < 0;

            int shortScore = (rsiShortConfirm ? 1 : 0)
                           + (!relativeStrength ? 1 : 0)
                           + (belowVwap ? 1 : 0)
                           + (volExp ? 1 : 0)
                           + (macdBearish ? 1 : 0);

            int shortRequired = _marketRegime == "SELL-OFF" ? 3 : 4;
            if (shortRequired <= shortScore && breakdownSignal)
            {
                decimal sma200 = GetDailySma200(symbol);
                if (sma200 > 0 && close > sma200 && _marketRegime != "SELL-OFF") return false;

                if (_ema20_15min.TryGetValue(symbol, out var ema15MS) && ema15MS.ema20 > 0
                    && close > ema15MS.ema20) return false;

                decimal stopDistance = Math.Max(atr * HARD_STOP_ATR_MULT, MIN_STOP_DISTANCE);
                int qty = CalcQty(close, stopDistance);
                if (qty <= 0) return false;

                OpenPosition(symbol, qty, close, TradeSide.Sell, true, "MOMENTUM_SHORT");
                return true;
            }
        }

        return false;
    }

    // ══════════════════════════════════════════════════════════
    //  STRATEGY 6 — EMA POCKET (Triple Line Setup)
    // ══════════════════════════════════════════════════════════

    private bool TryEmaPocketStrategy(string symbol, List<Candle> candles)
    {
        if (candles.Count < 30) return false;

        if (!_indicatorCache.TryGetValue(symbol, out var ind)) return false;

        double ema9 = ind.Ema9;
        double ema21 = ind.Ema21;
        double ema9Prev = ind.Ema9Prev;
        double ema21Prev = ind.Ema21Prev;
        if (ema21Prev == 0) return false;

        decimal close = candles.Last().Close;
        decimal atr = ind.Atr14;
        double rsi = ind.Rsi14;
        _vwap.TryGetValue(symbol, out decimal vwapVal);
        var (pdHigh, pdLow) = GetPrevDayHL(symbol);

        double slopeThreshold = ema21Prev * 0.0005;
        bool ema9Rising = ema9 > ema9Prev + slopeThreshold;
        bool ema9Falling = ema9 < ema9Prev - slopeThreshold;
        bool ema21Rising = ema21 > ema21Prev + slopeThreshold;
        bool ema21Falling = ema21 < ema21Prev - slopeThreshold;

        long avgVol = (long)candles.TakeLast(10).Average(c => c.Volume);
        bool volOk = candles.Last().Volume >= avgVol;

        bool nearLevel = false;
        if (pdHigh > 0 && Math.Abs(close - pdHigh) <= atr) nearLevel = true;
        if (pdLow > 0 && Math.Abs(close - pdLow) <= atr) nearLevel = true;
        if (vwapVal > 0 && Math.Abs(close - vwapVal) <= atr * 0.5m) nearLevel = true;
        if (!nearLevel) return false;

        if (!volOk) return false;

        bool bullishOrder = ema9 > ema21;
        bool inLongPocket = (double)close < ema9 && (double)close > ema21;

        if (bullishOrder && inLongPocket && ema9Rising && ema21Rising && rsi > 50)
        {
            if (_marketRegime == "SELL-OFF" && !CheckStrongRelativeStrength(symbol, candles))
                return false;

            decimal stopDistance = Math.Max(atr * HARD_STOP_ATR_MULT, MIN_STOP_DISTANCE);
            int qty = CalcQty(close, stopDistance);
            if (qty <= 0) return false;

            OpenPosition(symbol, qty, close, TradeSide.Buy, false, "EMA_POCKET_LONG");
            return true;
        }

        bool bearishOrder = ema9 < ema21;
        bool inShortPocket = (double)close > ema9 && (double)close < ema21;

        if (bearishOrder && inShortPocket && ema9Falling && ema21Falling && rsi < 50 && _allowShorts)
        {
            decimal sma200 = GetDailySma200(symbol);
            if (sma200 > 0 && close > sma200 && _marketRegime != "SELL-OFF") return false;

            decimal stopDistance = Math.Max(atr * HARD_STOP_ATR_MULT, MIN_STOP_DISTANCE);
            int qty = CalcQty(close, stopDistance);
            if (qty <= 0) return false;

            OpenPosition(symbol, qty, close, TradeSide.Sell, true, "EMA_POCKET_SHORT");
            return true;
        }

        return false;
    }

    // ══════════════════════════════════════════════════════════
    //  STRATEGY 7 — OUTSIDE CANDLE (Engulfing 3-Bar)
    // ══════════════════════════════════════════════════════════

    private bool TryOutsideCandleStrategy(string symbol, List<Candle> candles)
    {
        if (candles.Count < 10) return false;

        var last4 = candles.TakeLast(4).ToList();
        var outside = last4[3];
        var prior3 = last4.Take(3).ToList();

        decimal outerHigh = prior3.Max(c => c.High);
        decimal outerLow = prior3.Min(c => c.Low);
        bool isOutside = outside.High > outerHigh && outside.Low < outerLow;
        if (!isOutside) return false;

        decimal range = outside.High - outside.Low;
        if (range < outside.Close * 0.003m) return false;

        decimal closePosition = range > 0 ? (outside.Close - outside.Low) / range : 0.5m;
        bool bullishClose = closePosition >= 0.75m;
        bool bearishClose = closePosition <= 0.25m;
        if (!bullishClose && !bearishClose) return false;

        decimal close = outside.Close;

        if (!_indicatorCache.TryGetValue(symbol, out var ind)) return false;
        decimal atr = ind.Atr14;
        double rsi = ind.Rsi14;

        _vwap.TryGetValue(symbol, out decimal vwapVal);
        var (pdHigh, pdLow) = GetPrevDayHL(symbol);

        double ema50_30min = Calc30MinEma(symbol, candles, 50);

        bool nearSR = false;
        if (pdHigh > 0 && Math.Abs(close - pdHigh) <= atr * 1.5m) nearSR = true;
        if (pdLow > 0 && Math.Abs(close - pdLow) <= atr * 1.5m) nearSR = true;
        if (vwapVal > 0 && Math.Abs(close - vwapVal) <= atr) nearSR = true;
        if (_orbRanges.TryGetValue(symbol, out var orb) && orb.IsSet)
        {
            if (Math.Abs(close - orb.High) <= atr || Math.Abs(close - orb.Low) <= atr)
                nearSR = true;
        }
        if (!nearSR) return false;

        if (bullishClose && ema50_30min > 0 && (double)close > ema50_30min && rsi > 50)
        {
            if (_marketRegime == "SELL-OFF" || _spyOpenBearish)
                return false;

            decimal stopDistance = Math.Max(close - outside.Low, atr * HARD_STOP_ATR_MULT);
            stopDistance = Math.Max(stopDistance, MIN_STOP_DISTANCE);
            decimal target = vwapVal > close ? vwapVal : (pdHigh > close ? pdHigh : close + atr * 2);
            if (target - close < stopDistance * 2m) return false;

            int qty = CalcQty(close, stopDistance);
            if (qty <= 0) return false;

            OpenPosition(symbol, qty, close, TradeSide.Buy, false, "OUTSIDE_CANDLE_LONG");
            return true;
        }

        if (bearishClose && ema50_30min > 0 && (double)close < ema50_30min && rsi < 50 && _allowShorts)
        {
            decimal sma200 = GetDailySma200(symbol);
            if (sma200 > 0 && close > sma200 && _marketRegime != "SELL-OFF") return false;

            decimal stopDistance = Math.Max(outside.High - close, atr * HARD_STOP_ATR_MULT);
            stopDistance = Math.Max(stopDistance, MIN_STOP_DISTANCE);
            decimal target = vwapVal > 0 && vwapVal < close ? vwapVal : (pdLow > 0 && pdLow < close ? pdLow : close - atr * 2);
            if (close - target < stopDistance * 2m) return false;

            int qty = CalcQty(close, stopDistance);
            if (qty <= 0) return false;

            OpenPosition(symbol, qty, close, TradeSide.Sell, true, "OUTSIDE_CANDLE_SHORT");
            return true;
        }

        return false;
    }

    private double Calc30MinEma(string symbol, List<Candle> candles, int period)
    {
        if (candles == null || candles.Count < 2) return 0;
        var lastCandle = candles.Last();
        var barTime = new DateTime(lastCandle.Time.Year, lastCandle.Time.Month, lastCandle.Time.Day,
                                   lastCandle.Time.Hour, (lastCandle.Time.Minute / 30) * 30, 0);

        if (_ema50_30min.TryGetValue(symbol, out var cached) && cached.barTime == barTime)
            return cached.ema50;

        var bars30 = candles
            .GroupBy(c => new DateTime(c.Time.Year, c.Time.Month, c.Time.Day,
                                       c.Time.Hour, (c.Time.Minute / 30) * 30, 0))
            .OrderBy(g => g.Key)
            .Select(g => (double)g.Last().Close)
            .ToArray();

        if (bars30.Length < period) return 0;
        double ema = CalcEMA(bars30, period);
        _ema50_30min[symbol] = (ema, barTime);
        return ema;
    }

    // ══════════════════════════════════════════════════════════
    //  SETUP QUALITY SCORER
    // ══════════════════════════════════════════════════════════

    private int ScoreSetup(string symbol, List<Candle> candles)
    {
        if (candles == null || candles.Count < 50) return 0;
        if (!_indicatorCache.TryGetValue(symbol, out var ind)) return 0;

        int score = 0;
        decimal lastClose = candles.Last().Close;

        bool aboveSma50 = lastClose > ind.Sma50 && ind.Sma50 > 0;
        if (aboveSma50) score += 15;
        else if (!aboveSma50 && ind.Sma50 > 0) score += 8;

        if (_ema20_15min.TryGetValue(symbol, out var ema15) && ema15.ema20 > 0)
        {
            if (aboveSma50 && lastClose > ema15.ema20) score += 10;
            else if (!aboveSma50 && lastClose < ema15.ema20) score += 10;
        }

        if (ind.VolExpansion) score += 25;
        else
        {
            var last10 = candles.TakeLast(10).ToList();
            if (last10.Count >= 10)
            {
                long prev5vol = last10.Take(5).Sum(c => c.Volume);
                long recent5vol = last10.Skip(5).Take(5).Sum(c => c.Volume);
                if (prev5vol > 0 && recent5vol > prev5vol * 1.3m) score += 10;
            }
        }

        if (lastClose > 0 && ind.Atr14 > 0)
        {
            decimal atrPct = ind.Atr14 / lastClose;
            if (atrPct >= MIN_ATR_PCT && (MAX_ATR_PCT <= 0 || atrPct <= MAX_ATR_PCT))
                score += 20;
            else if (atrPct >= MIN_ATR_PCT * 0.7m)
                score += 8;
        }

        score += _marketRegime switch
        {
            "TRENDING" => 20,
            "NORMAL" => 12,
            "CHOPPY" => 4,
            "SELL-OFF" => 0,
            _ => 8
        };

        if (_vwap.TryGetValue(symbol, out decimal vwapNow) && vwapNow > 0 && ind.Atr14 > 0)
        {
            if (Math.Abs(lastClose - vwapNow) <= ind.Atr14 * 1.5m)
                score += 10;
        }

        return Math.Clamp(score, 0, 100);
    }

    // ══════════════════════════════════════════════════════════
    //  POSITION OPENING HELPER
    // ══════════════════════════════════════════════════════════

    private void OpenPosition(string symbol, int qty, decimal price,
                           TradeSide side, bool isShort, string strategyTag)
    {
        if (string.IsNullOrEmpty(symbol) || string.IsNullOrEmpty(strategyTag)) return;
        if (isShort && !_allowShorts) return;
        if (qty <= 0 || qty > MAX_QTY_SANITY) return;

        if (symbol != "SPY" && symbol != "QQQ" && symbol != "IWM")
        {
            if (!isShort && _spyBearish)
            {
                _marketData.TryGetValue(symbol, out var gateCandles);
                if (gateCandles == null || !CheckStrongRelativeStrength(symbol, gateCandles))
                {
                    LogMessage($"[SPY GATE] {strategyTag} {symbol} LONG blocked — SPY EMA20 bearish");
                    return;
                }
                LogMessage($"[SPY GATE] {strategyTag} {symbol} LONG ALLOWED despite bearish SPY — strong relative strength");
            }
            if (isShort && _spyBullish)
            {
                LogMessage($"[SPY GATE] {strategyTag} {symbol} SHORT blocked — SPY EMA20 bullish");
                return;
            }
        }

        if (_symbolSectors.TryGetValue(symbol, out string newSector))
        {
            foreach (var openPos in _positions.Values)
            {
                if (_symbolSectors.TryGetValue(openPos.Symbol, out string existingSector)
                    && existingSector == newSector)
                {
                    LogMessage($"[SECTOR GATE] {strategyTag} {symbol} blocked — already have {openPos.Symbol} in sector [{newSector}]");
                    return;
                }
            }
        }

        decimal stopDist = MIN_STOP_DISTANCE;
        if (_marketData.TryGetValue(symbol, out var rrCandles))
        {
            decimal atrForRR = SafeATR(rrCandles, 14);
            stopDist = Math.Max(atrForRR * HARD_STOP_ATR_MULT, MIN_STOP_DISTANCE);

            _vwap.TryGetValue(symbol, out decimal vwapRR);
            var (pdHighRR, pdLowRR) = GetPrevDayHL(symbol);

            decimal targetDist;
            if (!isShort)
            {
                decimal toVwap = vwapRR > price ? vwapRR - price : decimal.MaxValue;
                decimal toPdHigh = pdHighRR > price ? pdHighRR - price : decimal.MaxValue;
                decimal toAtrTgt = atrForRR * 3.5m;
                targetDist = Math.Min(toVwap, Math.Min(toPdHigh, toAtrTgt));
            }
            else
            {
                decimal toVwap = vwapRR > 0 && vwapRR < price ? price - vwapRR : decimal.MaxValue;
                decimal toPdLow = pdLowRR > 0 && pdLowRR < price ? price - pdLowRR : decimal.MaxValue;
                decimal toAtrTgt = atrForRR * 3.5m;
                targetDist = Math.Min(toVwap, Math.Min(toPdLow, toAtrTgt));
            }

            if (targetDist < stopDist * MIN_RR_RATIO)
            {
                LogMessage($"[RR SKIP] {strategyTag} {symbol} R:R={targetDist / stopDist:F2} < {MIN_RR_RATIO} — target too close to stop, skipping");
                return;
            }
        }

        if (_pendingStrategyTag == null)
            _pendingStrategyTag = new ConcurrentDictionary<string, string>();
        _pendingStrategyTag[symbol] = strategyTag;
        _pendingInitialRisk[symbol] = stopDist;

        if (!isShort && _latestTick.TryGetValue("VIX", out decimal vixLevel) && vixLevel > 0)
        {
            if (vixLevel >= VIX_NO_LONG_THRESHOLD)
            {
                LogMessage($"[VIX BLOCK] {strategyTag} {symbol} LONG blocked — VIX={vixLevel:F1} ≥ {VIX_NO_LONG_THRESHOLD}");
                return;
            }
            if (vixLevel >= VIX_REDUCE_THRESHOLD)
            {
                qty = Math.Max(1, qty / 2);
                LogMessage($"[VIX REDUCE] {strategyTag} {symbol} qty halved to {qty} — VIX={vixLevel:F1} ≥ {VIX_REDUCE_THRESHOLD}");
            }
        }

        Interlocked.Increment(ref _pendingEntryCount);
        lock (_lock)
        {
            _dailyEntryCount[symbol] = _dailyEntryCount.GetValueOrDefault(symbol) + 1;
            _tradesThisHour++;
        }

        bool usedBracket = false;
        if (RealBroker != null && RealBroker.SupportsBrackets)
        {
            _marketData.TryGetValue(symbol, out var bracketCandles);
            decimal atrBracket = SafeATR(bracketCandles, 14);

            decimal stopTrigger = isShort
                ? price + stopDist
                : price - stopDist;
            decimal stopLimitPrice = isShort
                ? stopTrigger + atrBracket * 0.5m
                : stopTrigger - atrBracket * 0.5m;

            decimal targetPrice = isShort
                ? price - stopDist * 2.4m
                : price + stopDist * 2.4m;

            Func<decimal, decimal> tickRound = p => p >= 1.0m
                ? Math.Round(p, 2, MidpointRounding.AwayFromZero)
                : Math.Round(p, 4, MidpointRounding.AwayFromZero);

            decimal entryAdj = tickRound(price + (side == TradeSide.Buy
                ? Math.Max(atrBracket * 0.1m, price * 0.0005m)
                : -Math.Max(atrBracket * 0.1m, price * 0.0005m)));

            RealBroker.SubmitBracketOrder(
                symbol, qty,
                entryAdj, side,
                tickRound(stopTrigger), tickRound(stopLimitPrice),
                tickRound(targetPrice));

            LogMessage($"[BRACKET] {strategyTag} {symbol} x{qty} entry={entryAdj:F2} " +
                       $"stop={tickRound(stopTrigger):F2}/{tickRound(stopLimitPrice):F2} " +
                       $"target={tickRound(targetPrice):F2}");
            usedBracket = true;
        }

        if (!usedBracket)
        {
            SubmitOrder(symbol, qty, price, side, strategyTag);
        }

        LogMessage($"[{strategyTag}] {symbol} x{qty} @ {price:F2} | regime={_marketRegime} | bracket={usedBracket}");
    }

    // ══════════════════════════════════════════════════════════
    //  EXIT LOGIC
    // ══════════════════════════════════════════════════════════

    private void CancelBracketChildren(SimPosition pos)
    {
        if (RealBroker == null) return;
        if (pos.BracketStopId > 0)
        {
            RealBroker.CancelOrder(pos.BracketStopId);
            _ordersById.TryRemove(pos.BracketStopId, out _);
            pos.BracketStopId = 0;
        }
        if (pos.BracketTargetId > 0)
        {
            RealBroker.CancelOrder(pos.BracketTargetId);
            _ordersById.TryRemove(pos.BracketTargetId, out _);
            pos.BracketTargetId = 0;
        }
    }

    private void CheckHardStop(string symbol, decimal currentPrice)
    {
        lock (_lock)
        {
            if (!_positions.TryGetValue(symbol, out var pos)) return;
            if (pos.ExitSubmitted) return;
            if (!_marketData.TryGetValue(symbol, out var candles)) return;

            double secondsHeld = (DateTime.UtcNow - pos.EntryTime).TotalSeconds;

            decimal atrValue = SafeATR(candles, 14);
            decimal unrealizedLoss = pos.UnrealizedPnL(currentPrice);

            decimal dynamicDollarStop = Math.Max(MAX_LOSS_PER_TRADE, pos.Quantity * Math.Max(atrValue * HARD_STOP_ATR_MULT, MIN_STOP_DISTANCE));
            bool dollarStopHit = unrealizedLoss <= -dynamicDollarStop;

            bool atrStopHit = false;
            if (pos.IsShort)
            {
                decimal shortHardStop = pos.AvgPrice + atrValue * HARD_STOP_ATR_MULT;
                atrStopHit = currentPrice > shortHardStop;
            }
            else
            {
                decimal longHardStop = pos.AvgPrice - atrValue * HARD_STOP_ATR_MULT;
                atrStopHit = currentPrice < longHardStop;
            }

            if (atrStopHit || dollarStopHit)
            {
                pos.ExitSubmitted = true;
                string reason = dollarStopHit ? "MAX_LOSS_STOP" : "HARD_STOP";
                TradeSide exitSide = pos.IsShort ? TradeSide.Buy : TradeSide.Sell;

                CancelBracketChildren(pos);

                string exitOrderType = "MKT";
                decimal exitPrice = 0m;
                if (atrValue > 0)
                {
                    decimal band = atrValue * 0.5m;
                    exitPrice = pos.IsShort
                        ? Math.Round(currentPrice + band, 2, MidpointRounding.AwayFromZero)
                        : Math.Round(currentPrice - band, 2, MidpointRounding.AwayFromZero);
                    exitOrderType = "LMT";
                }

                SubmitOrder(symbol, pos.Quantity, exitPrice, exitSide, reason, exitOrderType);
            }
        }
    }

    private void CheckExits(string symbol, decimal currentPrice)
    {
        lock (_lock)
        {
            if (!_positions.TryGetValue(symbol, out var pos)) return;
            if (!_marketData.TryGetValue(symbol, out var candles)) return;
            if (pos.ExitSubmitted) return;

            double secondsHeld = (DateTime.UtcNow - pos.EntryTime).TotalSeconds;
            if (secondsHeld < MIN_HOLD_SECONDS) return;

            decimal atrValue = SafeATR(candles, 14);
            TradeSide exitSide = pos.IsShort ? TradeSide.Buy : TradeSide.Sell;

            decimal gainPerShare = pos.IsShort
                ? pos.AvgPrice - currentPrice
                : currentPrice - pos.AvgPrice;
            decimal oneR = pos.InitialRiskPerShare > 0
                ? pos.InitialRiskPerShare
                : Math.Max(atrValue * HARD_STOP_ATR_MULT, MIN_STOP_DISTANCE);
            if (oneR <= 0) oneR = MIN_STOP_DISTANCE;
            decimal rMultiple = gainPerShare / oneR;

            if (pos.IsShort)
                pos.HighWaterMark = Math.Min(
                    pos.HighWaterMark == 0 ? currentPrice : pos.HighWaterMark, currentPrice);
            else
                pos.HighWaterMark = Math.Max(pos.HighWaterMark, currentPrice);

            bool bracketActive = pos.BracketStopId > 0 || pos.BracketTargetId > 0;

            if (secondsHeld > 3600 && rMultiple < 0.30m)
            {
                CancelBracketChildren(pos);
                pos.ExitSubmitted = true;
                SubmitOrder(symbol, pos.Quantity, currentPrice, exitSide, "TIME_STOP");
                return;
            }

            if (bracketActive) return;

            if (rMultiple >= 1.25m && !pos.PartialExitDone && pos.Quantity >= 2)
            {
                int halfQty = pos.Quantity / 2;
                pos.PartialExitDone = true;
                SubmitOrder(symbol, halfQty, currentPrice, exitSide, "PARTIAL_TP_1");
                return;
            }

            if (pos.PartialExitDone && !pos.ExitSubmitted)
            {
                if (rMultiple >= 1.75m)
                    pos.PartialExitDone2 = true;

                bool takeAtFinal = rMultiple >= 2.4m;
                bool givebackTo1R = pos.PartialExitDone2 && rMultiple <= 1.0m;

                if (takeAtFinal || givebackTo1R)
                {
                    pos.ExitSubmitted = true;
                    string reason = takeAtFinal ? "PARTIAL_TP_2" : "TRAIL_BACK_TO_1R";
                    SubmitOrder(symbol, pos.Quantity, currentPrice, exitSide, reason);
                    return;
                }
            }

            decimal trailMult = pos.IsShort ? SHORT_ATR_TRAIL : ATR_TRAIL_MULT;

            bool trailHit;
            if (pos.IsShort)
            {
                decimal trailStop = pos.HighWaterMark + atrValue * trailMult;
                trailHit = currentPrice > trailStop && rMultiple > 1.2m;
            }
            else
            {
                decimal trailStop = pos.HighWaterMark - atrValue * trailMult;
                trailHit = currentPrice < trailStop && rMultiple > 1.2m;
            }

            if (trailHit)
            {
                pos.ExitSubmitted = true;
                SubmitOrder(symbol, pos.Quantity, currentPrice, exitSide, "ATR_TRAIL_EXIT");
            }
        }
    }

    // ══════════════════════════════════════════════════════════
    //  ORDER MANAGEMENT
    // ══════════════════════════════════════════════════════════

    public void SubmitOrder(string symbol, int qty, decimal price,
                         TradeSide side, string note, string type = "LMT")
    {
        if (RealBroker == null) return;

        decimal adjusted = price;
        if (type == "LMT")
        {
            _marketData.TryGetValue(symbol, out var candles);
            decimal atr = SafeATR(candles, 14);
            decimal slippageBuffer = Math.Max(atr * 0.1m, price * 0.0005m);
            adjusted = side == TradeSide.Buy
                ? price + slippageBuffer
                : price - slippageBuffer;

            adjusted = adjusted >= 1.0m
                ? Math.Round(adjusted, 2, MidpointRounding.AwayFromZero)
                : Math.Round(adjusted, 4, MidpointRounding.AwayFromZero);
        }

        RealBroker.SubmitOrder(symbol, qty, adjusted, side, 0, type);
        LogMessage($"[ORDER] {note} → {side} {symbol} x{qty} @ {adjusted:F2}");
    }

    public void RegisterLiveOrder(int orderId, string symbol, TradeSide side, int qty)
    {
        _ordersById[orderId] = new TrackedOrder
        { OrderId = orderId, Symbol = symbol, Side = side, Qty = qty };
    }

    public void RemoveLiveOrder(int orderId) => _ordersById.TryRemove(orderId, out _);

    public void RegisterBracketChildren(string symbol, int stopId, int targetId)
        => _pendingBracketChildren[symbol] = (stopId, targetId);

    public void OnOrderRejected(int orderId)
    {
        if (!_ordersById.TryRemove(orderId, out var order)) return;
        lock (_lock)
        {
            bool isEntry = !_positions.ContainsKey(order.Symbol);
            if (isEntry) Interlocked.Decrement(ref _pendingEntryCount);
        }
        LogMessage($"[REJECTED] orderId={orderId} {order.Side} {order.Symbol} x{order.Qty} — slot freed.");
        _ = SendEmail($"⚠️ Order Rejected: {order.Symbol}",
            $"IBKR rejected orderId={orderId} {order.Side} {order.Symbol} x{order.Qty}.Check errors.log.");
    }

    public void OnOrderFilled(int orderId, int fillQty, decimal fillPrice)
    {
        if (!_ordersById.TryGetValue(orderId, out var order)) return;
        if (!_ordersById.TryRemove(orderId, out _)) return;

        string subject = "", body = "";

        lock (_lock)
        {
            bool isShortEntry = order.Side == TradeSide.Sell && !_positions.ContainsKey(order.Symbol);
            bool isLongEntry = order.Side == TradeSide.Buy && !_positions.ContainsKey(order.Symbol);

            if (isLongEntry || isShortEntry)
            {
                Interlocked.Decrement(ref _pendingEntryCount);

                string tag = "";
                decimal initialRisk = 0m;
                _pendingStrategyTag?.TryRemove(order.Symbol, out tag);
                _pendingInitialRisk?.TryRemove(order.Symbol, out initialRisk);
                string resolvedTag = tag ?? "";

                _positions[order.Symbol] = new SimPosition
                {
                    Symbol = order.Symbol,
                    Quantity = fillQty,
                    AvgPrice = fillPrice,
                    HighWaterMark = fillPrice,
                    CurrentPrice = fillPrice,
                    EntryTime = DateTime.UtcNow,
                    IsShort = isShortEntry,
                    StrategyTag = resolvedTag,
                    EntryCommission = COMMISSION_PER_SIDE,
                    InitialRiskPerShare = initialRisk
                };

                if (_pendingBracketChildren.TryRemove(order.Symbol, out var bracketIds))
                {
                    _positions[order.Symbol].BracketStopId = bracketIds.stopId;
                    _positions[order.Symbol].BracketTargetId = bracketIds.targetId;
                    LogMessage($"[BRACKET] {order.Symbol} entry filled — bracket live " +
                               $"(stop={bracketIds.stopId} target={bracketIds.targetId})");
                }

                _tradesToday++;
                _totalRealizedPnL -= COMMISSION_PER_SIDE;

                string dir = isShortEntry ? "SHORT" : "BUY";
                subject = $"🚀 {dir}: {order.Symbol} x{fillQty} @ {fillPrice:C2}";
                body = $"{dir} {fillQty} shares @ {fillPrice:C2}  (commission: -$1)";
            }
            else if (_positions.TryGetValue(order.Symbol, out var pos))
            {
                decimal grossPnl = pos.IsShort
                    ? (pos.AvgPrice - fillPrice) * fillQty
                    : (fillPrice - pos.AvgPrice) * fillQty;

                decimal netPnl = grossPnl - COMMISSION_PER_SIDE;
                decimal holdMinutes = (decimal)(DateTime.UtcNow - pos.EntryTime).TotalMinutes;

                _totalRealizedPnL += netPnl;

                pos.Quantity -= fillQty;
                bool isFullClose = pos.Quantity <= 0;

                string exitReason = isFullClose ? "EXIT" : "PARTIAL";
                foreach (var tag in new[]{ "ATR_TRAIL_EXIT","TIME_STOP","HARD_STOP",
                                           "MAX_LOSS_STOP","PARTIAL_TP_1","PARTIAL_TP_2","TRAIL_BACK_TO_1R","EOD_LIQUIDATE" })
                    if (_tradeHistoryLog.LastOrDefault()?.Contains(tag) == true)
                    { exitReason = tag; break; }

                decimal recordedNetPnl = isFullClose
                    ? netPnl - pos.EntryCommission
                    : netPnl;

                _completedTrades.Add(new TradeRecord
                {
                    Symbol = order.Symbol,
                    Side = pos.IsShort ? "SHORT" : "LONG",
                    Strategy = pos.StrategyTag,
                    Qty = fillQty,
                    Entry = pos.AvgPrice,
                    Exit = fillPrice,
                    NetPnL = recordedNetPnl,
                    HoldMinutes = holdMinutes,
                    ExitReason = exitReason,
                    Time = GetEasternTime().ToString("HH:mm"),
                    Date = GetEasternTime().Date.ToString("yyyy-MM-dd"),
                    Regime = _marketRegime
                });
                if (_completedTrades.Count > 200) _completedTrades.RemoveAt(0);

                var allTradeRecord = new TradeRecord
                {
                    Symbol = order.Symbol,
                    Side = pos.IsShort ? "SHORT" : "LONG",
                    Strategy = pos.StrategyTag,
                    Qty = fillQty,
                    Entry = pos.AvgPrice,
                    Exit = fillPrice,
                    NetPnL = recordedNetPnl,
                    HoldMinutes = holdMinutes,
                    ExitReason = exitReason,
                    Time = GetEasternTime().ToString("HH:mm"),
                    Date = GetEasternTime().Date.ToString("yyyy-MM-dd"),
                    Regime = _marketRegime
                };
                lock (_allTrades)
                {
                    _allTrades.Add(allTradeRecord);
                    if (_allTrades.Count > 2000) _allTrades.RemoveAt(0);
                }
                Task.Run(() => SaveAllTrades());

                if (isFullClose)
                {
                    if (recordedNetPnl > 0)
                    {
                        _winCount++;
                        _consecutiveLosses = 0;
                    }
                    else
                    {
                        _lossCount++;
                        _consecutiveLosses++;
                        if (MAX_CONSECUTIVE_LOSSES > 0 && _consecutiveLosses >= MAX_CONSECUTIVE_LOSSES)
                        {
                            _haltTrading = true;
                            LogMessage($"[HALT] {_consecutiveLosses} consecutive losses — trading paused for the session.");
                            _ = SendEmail($"🛑 {_consecutiveLosses} Consecutive Losses",
                                          $"Bot halted after {_consecutiveLosses} losses in a row. Daily PnL: {_totalRealizedPnL:C2}");
                        }
                    }

                    _marketData.TryGetValue(order.Symbol, out var exitCandles);
                    LogTradeAnalytics(order.Symbol, pos.AvgPrice, fillPrice,
                                      netPnl, holdMinutes,
                                      SafeATR(exitCandles, 14), SafeRSI(exitCandles, 14),
                                      pos.StrategyTag, pos.IsShort);

                    _positions.Remove(order.Symbol);
                    _lastTradeTime[order.Symbol] = DateTime.UtcNow;
                    _lastTradeWasLoss[order.Symbol] = netPnl <= 0;
                }

                subject = $"💰 {(isFullClose ? "CLOSE" : "PARTIAL")}: {order.Symbol} x{fillQty} @ {fillPrice:C2} | Net: {recordedNetPnl:C2}";
                body = $"{(isFullClose ? "Closed" : "Partial")} {fillQty} @ {fillPrice:C2}\nGross: {grossPnl:C2}  Commission: -{(isFullClose ? COMMISSION_PER_SIDE * 2 : COMMISSION_PER_SIDE):C0}  Net: {recordedNetPnl:C2}\nStrategy: {pos.StrategyTag}";
            }

            string arrow = order.Side == TradeSide.Buy ? "▲" : "▼";
            string logLine = $"[{DateTime.UtcNow:HH:mm:ss}] {arrow} {order.Side,-4} {order.Symbol,-5} x{fillQty,-4} @ {fillPrice:C2}";
            _tradeHistoryLog.Add(logLine);
            if (_tradeHistoryLog.Count > 50) _tradeHistoryLog.RemoveAt(0);

            Task.Run(() => SendEmail(subject, body));
        }

        _equityCurve.Add((DateTime.UtcNow, _totalRealizedPnL));
        SaveEquityCurve();
        CheckDailyLimits();
        SaveState();
    }

    // ══════════════════════════════════════════════════════════
    //  DAILY CONTROLS
    // ══════════════════════════════════════════════════════════

    private void CheckDailyLimits()
    {
        if (_totalRealizedPnL >= DAILY_PROFIT_GOAL || _totalRealizedPnL <= MAX_DAILY_LOSS)
        {
            _haltTrading = true;
            string status = _totalRealizedPnL > 0 ? "GOAL REACHED ✅" : "MAX LOSS HIT 🛑";
            _ = SendEmail($"🛑 TRADING HALTED: {status}",
                          $"Final PnL: {_totalRealizedPnL:C2}");
        }
    }

    private void CheckDailyReset()
    {
        var nowEt = GetEasternTime();
        if (_lastVolumeResetEt == DateTime.MinValue)
            _lastVolumeResetEt = nowEt.Date;
        if (nowEt.Date <= _lastVolumeResetEt) return;

        _dailyVolume.Clear();
        _vwapAccum.Clear();
        _vwap.Clear();
        _orbRanges.Clear();
        _dailyGapPct.Clear();
        _prevBarAboveVwap.Clear();
        _dailyEntryCount.Clear();
        _lastTradeWasLoss.Clear();
        _earningsBlacklist.Clear();
        _pendingEntryCount = 0;
        _completedTrades.Clear();
        _lastVolumeResetEt = nowEt.Date;
        _eodSent = false;
        _haltTrading = false;
        _tradesToday = 0;
        _tradesThisHour = 0;
        _currentTradeHour = DateTime.MinValue;
        _winCount = 0;
        _lossCount = 0;
        _consecutiveLosses = 0;
        _totalRealizedPnL = 0m;
        _spyOpenBearish = false;
        _spyBiasChecked = false;
        LogMessage($"[DAY RESET] {nowEt:yyyy-MM-dd} — new session started");
    }

    public void CheckEndOfDay()
    {
        var now = GetEasternTime();
        if (!_eodSent && now.Hour == 15 && now.Minute >= 30)
        {
            _haltTrading = true;
            _eodSent = true;

            SnapshotLifetimeEquity();

            foreach (var p in _positions.Values.ToList())
            {
                TradeSide exitSide = p.IsShort ? TradeSide.Buy : TradeSide.Sell;
                SubmitOrder(p.Symbol, p.Quantity, 0, exitSide, "EOD_LIQUIDATE", "MKT");
            }

            int total = _winCount + _lossCount;
            double winRate = total > 0 ? (double)_winCount / total * 100 : 0;
            string report =
                $"EOD PnL   : {_totalRealizedPnL:C2}\n" +
                $"Trades    : {_tradesToday}\n" +
                $"Win Rate  : {winRate:F1}% ({_winCount}W / {_lossCount}L)\n\n" +
                $"Trade Log:\n{string.Join("\n", _tradeHistoryLog)}";
            _ = SendEmail("📊 EOD PERFORMANCE REPORT", report);
        }
    }

    // ══════════════════════════════════════════════════════════
    //  PERSISTENCE
    // ══════════════════════════════════════════════════════════

    private static string SafeReadJson(string path)
    {
        foreach (var candidate in new[] { path, path + ".bak" })
        {
            if (!File.Exists(candidate)) continue;
            try
            {
                var raw = File.ReadAllText(candidate);
                if (!string.IsNullOrWhiteSpace(raw) && (raw[0] == '{' || raw[0] == '['))
                    return raw;
            }
            catch { }
        }
        return null;
    }

    private static void AtomicWrite(string path, string content)
    {
        string tmp = path + ".tmp";
        File.WriteAllText(tmp, content);
        string bak = path + ".bak";
        if (File.Exists(path))
            File.Replace(tmp, path, bak);
        else
            File.Move(tmp, path);
    }

    public void SaveState()
    {
        try
        {
            var json = JsonSerializer.Serialize(new BotPersistData
            {
                Positions = _positions,
                TotalPnL = _totalRealizedPnL,
                WinCount = _winCount,
                LossCount = _lossCount,
                TradesToday = _tradesToday,
                HaltTrading = _haltTrading,
                LastTradeTime = _lastTradeTime,
                LastTradeWasLoss = _lastTradeWasLoss,
                DailyEntryCount = _dailyEntryCount,
                TradesThisHour = _tradesThisHour,
                TradeHourSlot = _currentTradeHour,
                CompletedTrades = _completedTrades.ToList(),
                LastVolumeResetDate = _lastVolumeResetEt
            });
            AtomicWrite(StatePath("bot_state.json"), json);
        }
        catch (Exception ex) { LogError("SaveState", ex.Message); }
    }

    public void LoadState()
    {
        string primaryPath = StatePath("bot_state.json");
        string bakPath = primaryPath + ".bak";

        string raw = null;
        if (File.Exists(primaryPath))
        {
            try { raw = File.ReadAllText(primaryPath); } catch { }
            if (string.IsNullOrWhiteSpace(raw) || raw[0] != '{')
            {
                LogMessage($"[LoadState] Primary state file corrupt (first char='{(raw?.Length > 0 ? raw[0] : '?')}') — trying backup.");
                raw = null;
            }
        }
        if (raw == null && File.Exists(bakPath))
        {
            try { raw = File.ReadAllText(bakPath); } catch { }
            if (string.IsNullOrWhiteSpace(raw) || raw[0] != '{')
            {
                LogMessage("[LoadState] Backup state file also corrupt — starting fresh.");
                raw = null;
            }
            else
                LogMessage("[LoadState] Loaded state from backup file.");
        }

        if (raw == null)
        {
            _needsReconciliation = true;
            if (RealBroker?.IsReady == true)
                RealBroker.RequestPositions();
            return;
        }

        try
        {
            var data = JsonSerializer.Deserialize<BotPersistData>(raw);
            if (data == null) { _reconciled = true; return; }

            _positions = data.Positions;
            _totalRealizedPnL = data.TotalPnL;
            _winCount = data.WinCount;
            _lossCount = data.LossCount;
            _tradesToday = data.TradesToday;
            _haltTrading = data.HaltTrading;

            foreach (var kv in data.LastTradeTime) _lastTradeTime[kv.Key] = kv.Value;
            foreach (var kv in data.LastTradeWasLoss) _lastTradeWasLoss[kv.Key] = kv.Value;
            foreach (var kv in data.DailyEntryCount) _dailyEntryCount[kv.Key] = kv.Value;
            _tradesThisHour = data.TradesThisHour;
            if (data.TradeHourSlot != DateTime.MinValue) _currentTradeHour = data.TradeHourSlot;
            if (data.CompletedTrades?.Count > 0)
            {
                _completedTrades.Clear();
                foreach (var t in data.CompletedTrades) _completedTrades.Add(t);

                _tradeHistoryLog.Clear();
                foreach (var t in _completedTrades.TakeLast(20))
                {
                    string arrow = t.Side == "LONG" ? "▲" : "▼";
                    string sign = t.NetPnL >= 0 ? "+" : "";
                    _tradeHistoryLog.Add(
                        $"[{t.Time}] {arrow} {t.Side,-5} {t.Symbol,-5} x{t.Qty,-4}" +
                        $" @ {t.Exit:C2}  {sign}{t.NetPnL:C2}  [{t.Strategy}]");
                }
            }
            if (data.LastVolumeResetDate != DateTime.MinValue)
                _lastVolumeResetEt = data.LastVolumeResetDate;

            foreach (var pos in _positions.Values)
            {
                pos.ExitSubmitted = false;
                if (pos.HighWaterMark <= 0) pos.HighWaterMark = pos.AvgPrice;
            }

            LogMessage($"[RESUME] State file: {primaryPath}");
            LogMessage($"[RESUME] Loaded {_positions.Count} positions | PnL={_totalRealizedPnL:C2} | Trades today={_tradesToday} | Wins={_winCount} Losses={_lossCount} | ThisHour={_tradesThisHour}");
            LoadConfig();
            CheckDailyReset();
            _needsReconciliation = true;
            if (RealBroker?.IsReady == true)
            {
                LogMessage("[RESUME] Broker already ready — requesting positions now.");
                RealBroker.RequestPositions();
            }
            else
            {
                LogMessage("[RESUME] Waiting for broker ready — reconciliation deferred to nextValidId.");
            }
        }
        catch (Exception ex)
        {
            LogError("LoadState", ex.Message);
            _reconciled = true;
        }
    }

    // ══════════════════════════════════════════════════════════
    //  IBKR POSITION RECONCILIATION
    // ══════════════════════════════════════════════════════════

    public bool NeedsReconciliation => _needsReconciliation;

    public void RequestRereconcile()
    {
        lock (_lock)
        {
            _ibkrPositionSnapshot.Clear();
            _needsReconciliation = true;
            _reconciled = false;
        }
        LogMessage("[WATCHDOG] Re-reconciliation armed — requesting fresh position snapshot.");
        if (RealBroker?.IsReady == true)
            RealBroker.RequestPositions();
    }

    public bool IsReconciled => _reconciled;

    public void ForceReconcile()
    {
        lock (_lock)
        {
            if (_reconciled) return;
            _needsReconciliation = false;

            if (_ibkrPositionSnapshot.Count > 0)
            {
                LogMessage($"[RECONCILE] Forced after timeout — processing {_ibkrPositionSnapshot.Count} partial position(s) received.");
                foreach (var (sym, (ibkrQty, ibkrCost)) in _ibkrPositionSnapshot)
                {
                    if (!_positions.ContainsKey(sym))
                    {
                        LogMessage($"[RECONCILE] Force-injected: {sym} x{ibkrQty} @ {ibkrCost:F2}");
                        _positions[sym] = new SimPosition
                        {
                            Symbol = sym,
                            Quantity = ibkrQty,
                            AvgPrice = ibkrCost,
                            HighWaterMark = ibkrCost,
                            CurrentPrice = ibkrCost,
                            EntryTime = DateTime.UtcNow,
                            IsShort = ibkrQty < 0,
                            ExitSubmitted = false,
                            StrategyTag = "UNKNOWN_RESUME"
                        };
                        _dailyEntryCount[sym] = 1;
                        _lastTradeTime[sym] = DateTime.UtcNow;
                    }
                }
            }
            else
            {
                LogMessage("[RECONCILE] Forced — no IBKR position data received. Using saved state as-is.");
            }

            _reconciled = true;
            LogMessage("[RECONCILE] Forced complete — trading unblocked. Verify positions manually.");
        }
        _ = SendEmail("⚠️ Reconciliation Forced",
            $"positionEnd() not received in time. Partial snapshot had {_ibkrPositionSnapshot.Count} position(s). " +
            "Verify open positions in TWS.");
    }

    public void OnPositionReceived(string symbol, int qty, decimal avgCost)
    {
        if (string.IsNullOrEmpty(symbol)) return;
        if (qty == 0) return;
        lock (_lock)
            _ibkrPositionSnapshot[symbol] = (qty, avgCost);
        Console.WriteLine($"[RECONCILE] ← {symbol} x{qty} @ {avgCost:F2}");
    }

    public void OnReconciliationComplete()
    {
        List<string> ghosts;
        lock (_lock)
        {
            if (!_needsReconciliation) return;

            Console.WriteLine($"[RECONCILE] Snapshot received: {_ibkrPositionSnapshot.Count} position(s).");

            ghosts = _positions.Keys
                .Where(sym => !_ibkrPositionSnapshot.ContainsKey(sym))
                .ToList();
            foreach (var sym in ghosts)
            {
                LogMessage($"[RECONCILE] Ghost removed: {sym}");
                _positions.Remove(sym);
            }

            foreach (var (sym, (ibkrQty, ibkrCost)) in _ibkrPositionSnapshot)
            {
                if (_positions.TryGetValue(sym, out var existing))
                {
                    if (existing.Quantity != ibkrQty || existing.AvgPrice != ibkrCost)
                    {
                        LogMessage($"[RECONCILE] Corrected {sym}: qty {existing.Quantity}→{ibkrQty}  cost {existing.AvgPrice:F2}→{ibkrCost:F2}");
                        existing.Quantity = ibkrQty;
                        existing.AvgPrice = ibkrCost;
                        existing.HighWaterMark = ibkrCost;
                    }
                }
                else
                {
                    LogMessage($"[RECONCILE] Injected: {sym} x{ibkrQty} @ {ibkrCost:F2}");
                    _positions[sym] = new SimPosition
                    {
                        Symbol = sym,
                        Quantity = ibkrQty,
                        AvgPrice = ibkrCost,
                        HighWaterMark = ibkrCost,
                        CurrentPrice = ibkrCost,
                        EntryTime = DateTime.UtcNow,
                        IsShort = ibkrQty < 0,
                        ExitSubmitted = false,
                        StrategyTag = "UNKNOWN_RESUME"
                    };
                    _dailyEntryCount[sym] = 1;
                    _lastTradeTime[sym] = DateTime.UtcNow;
                }
            }

            _needsReconciliation = false;
            _reconciled = true;
            SaveState();
        }

        LogMessage($"[RECONCILE] Done — {_positions.Count} active: " +
                   string.Join(", ", _positions.Keys.DefaultIfEmpty("none")));

        _ = SendEmail("🔄 Bot Reconciled After Restart",
            "Active positions:\n" +
            string.Join("\n", _positions.Values.Select(p =>
                $"  {p.Symbol} x{p.Quantity} @ {p.AvgPrice:C2}  [{p.StrategyTag}]")) +
            $"\nGhosts removed: {string.Join(", ", ghosts.DefaultIfEmpty("none"))}");
    }

    public void SaveMarketMemory()
    {
        try
        {
            var cutoff = DateTime.UtcNow.AddDays(-3);
            var dict = _marketData.ToDictionary(k => k.Key, v => v.Value);
            foreach (var kv in dict)
                lock (kv.Value) { kv.Value.RemoveAll(c => c.Time < cutoff); }
            AtomicWrite(StatePath("market_memory.json"), JsonSerializer.Serialize(dict));
        }
        catch (Exception ex) { LogError("SaveMarketMemory", ex.Message); }
    }

    public void LoadMarketMemory()
    {
        string raw = SafeReadJson(StatePath("market_memory.json"));
        if (raw == null) return;
        try
        {
            var data = JsonSerializer.Deserialize<ConcurrentDictionary<string, List<Candle>>>(raw);
            if (data != null)
                foreach (var kv in data)
                    _marketData[kv.Key] = kv.Value;
        }
        catch (Exception ex) { LogError("LoadMarketMemory", ex.Message); }
    }

    public void ClearMarketData()
    {
        foreach (var kv in _marketData)
            lock (kv.Value) { kv.Value.Clear(); }
        _marketData.Clear();
        LogMessage("[CANDLE ENGINE] Market data cleared.");
    }

    private int GetSubscriptionSlots()
    {
        int linesPerSym = Math.Max(1, DATA_LINES_PER_SYMBOL);
        return Math.Min(MAX_MARKET_DATA_LINES / linesPerSym, MAX_MARKET_DATA_LINES);
    }

    private IEnumerable<string> GetPrioritizedWatchlist()
    {
        var withPosition = new HashSet<string>(_positions.Keys, StringComparer.OrdinalIgnoreCase);
        return _watchlist
            .OrderByDescending(s => withPosition.Contains(s) ? 2 :
                (_marketData.TryGetValue(s, out var c) && c.Count > 0 &&
                 SafeATR(c, 14) / (c.LastOrDefault()?.Close ?? 1m) >= MIN_ATR_PCT ? 1 : 0));
    }

    public async Task RequestAllHistoricalSlow()
    {
        if (RealBroker == null)
            throw new Exception("RealBroker not set.");

        int slots = GetSubscriptionSlots();
        var toSubscribe = GetPrioritizedWatchlist().Take(slots).ToList();
        int skipped = _watchlist.Length - toSubscribe.Count;

        if (skipped > 0)
            LogMessage($"[DATA] Line budget: {MAX_MARKET_DATA_LINES} lines ÷ {DATA_LINES_PER_SYMBOL}/sym = {slots} slots. " +
                       $"Subscribing {toSubscribe.Count}/{_watchlist.Length} symbols ({skipped} skipped — raise MAX_MARKET_DATA_LINES or buy a Booster Pack).");

        foreach (var symbol in toSubscribe)
        {
            LogMessage($"[HIST] Requesting 1-min history: {symbol}...");
            RealBroker.RequestHistoricalData(symbol);
            _subscribedSymbols.Add(symbol);
            await Task.Delay(1500);
        }

        LogMessage($"[HIST] Requesting daily bars for {_watchlist.Length} symbols (SMA200 + S/R levels)...");
        foreach (var symbol in _watchlist)
        {
            LogMessage($"[HIST] Requesting daily history: {symbol}...");
            RealBroker.RequestDailyHistoricalData(symbol);
            await Task.Delay(1500);
        }

        _previousOrbMinutes = ORB_MINUTES;
    }

    public async Task ApplyWatchlistDiff(string[] oldList, string[] newList)
    {
        if (RealBroker == null || !RealBroker.IsReady) return;

        var added = newList.Except(oldList, StringComparer.OrdinalIgnoreCase).ToArray();
        var removed = oldList.Except(newList, StringComparer.OrdinalIgnoreCase).ToArray();

        foreach (var sym in removed)
        {
            bool hasPosition;
            lock (_lock) { hasPosition = _positions.ContainsKey(sym); }
            if (hasPosition)
            {
                LogMessage($"[WATCHLIST] {sym} removed from watchlist but has open position — keeping feed until flat.");
                continue;
            }
            LogMessage($"[WATCHLIST] Unsubscribing removed symbol: {sym}");
            try { RealBroker.CancelMarketData(sym); } catch { }
            _subscribedSymbols.Remove(sym);
        }

        int slots = GetSubscriptionSlots();
        int available = slots - _subscribedSymbols.Count;

        if (available <= 0)
        {
            LogMessage($"[DATA] No free data slots for {added.Length} new symbol(s). " +
                       $"Budget: {MAX_MARKET_DATA_LINES} lines ÷ {DATA_LINES_PER_SYMBOL}/sym = {slots} slots, all used. " +
                       $"Raise MAX_MARKET_DATA_LINES in settings or remove other symbols first.");
            return;
        }

        var toAdd = added.Take(available).ToArray();
        int overflow = added.Length - toAdd.Length;
        if (overflow > 0)
            LogMessage($"[DATA] Line budget only allows {toAdd.Length}/{added.Length} new symbols. {overflow} skipped.");

        foreach (var sym in toAdd)
        {
            if (_subscribedSymbols.Contains(sym)) continue;
            LogMessage($"[WATCHLIST] Subscribing new symbol: {sym}");
            RealBroker.RequestHistoricalData(sym);
            _subscribedSymbols.Add(sym);
            await Task.Delay(1500);
        }
    }

    public void ReevaluateHalt()
    {
        lock (_lock)
        {
            bool shouldHalt = _totalRealizedPnL >= DAILY_PROFIT_GOAL
                           || _totalRealizedPnL <= MAX_DAILY_LOSS;
            if (_haltTrading && !shouldHalt)
            {
                _haltTrading = false;
                LogMessage($"[CONFIG] Halt lifted — PnL {_totalRealizedPnL:C2} is within updated limits " +
                           $"(goal={DAILY_PROFIT_GOAL:C2}, maxLoss={MAX_DAILY_LOSS:C2}).");
            }
            else if (!_haltTrading && shouldHalt)
            {
                _haltTrading = true;
                LogMessage($"[CONFIG] New limits triggered halt — PnL {_totalRealizedPnL:C2} breaches " +
                           $"(goal={DAILY_PROFIT_GOAL:C2}, maxLoss={MAX_DAILY_LOSS:C2}).");
            }
        }
    }

    public void AddHistoricalCandle(string symbol, DateTime time,
        decimal open, decimal high, decimal low, decimal close, long vol)
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
            if (list.Count > 500) list.RemoveAt(0);
        }
    }

    public void AddDailyCandle(string symbol, DateTime date,
        decimal open, decimal high, decimal low, decimal close, long vol)
    {
        var list = _dailyCandles.GetOrAdd(symbol, _ => new List<Candle>());
        lock (list)
        {
            if (!list.Any(c => c.Time.Date == date.Date))
            {
                list.Add(new Candle { Time = date, Open = open, High = high, Low = low, Close = close, Volume = vol });
                list.Sort((a, b) => a.Time.CompareTo(b.Time));
            }
            if (list.Count > 250) list.RemoveAt(0);

            if (list.Count >= 2)
            {
                var yesterday = list[list.Count - 2];
                _prevDayHighLevel[symbol] = yesterday.High;
                _prevDayLowLevel[symbol] = yesterday.Low;
                _prevDayClose[symbol] = yesterday.Close;
            }
        }
    }

    private void SaveEquityCurve()
    {
        try
        {
            var lines = string.Join("\n", _equityCurve.Select(e => $"{e.time:O},{e.equity}"));
            AtomicWrite(StatePath("equity_curve.csv"), lines);
        }
        catch { }
    }

    public void LoadEquityCurve()
    {
        try
        {
            string path = StatePath("equity_curve.csv");
            if (!File.Exists(path)) return;
            var todayEt = GetEasternTime().Date;
            var fileLines = File.ReadAllLines(path);
            int loaded = 0;
            foreach (var line in fileLines)
            {
                if (string.IsNullOrWhiteSpace(line)) continue;
                int commaIdx = line.IndexOf(',');
                if (commaIdx < 0) continue;
                var timePart = line[..commaIdx];
                var valPart = line[(commaIdx + 1)..];
                if (!DateTime.TryParse(timePart,
                        null,
                        System.Globalization.DateTimeStyles.RoundtripKind,
                        out DateTime t)) continue;
                if (!decimal.TryParse(valPart,
                        System.Globalization.NumberStyles.Any,
                        System.Globalization.CultureInfo.InvariantCulture,
                        out decimal v)) continue;
                var etTime = TimeZoneInfo.ConvertTimeFromUtc(
                    t.Kind == DateTimeKind.Utc ? t : DateTime.SpecifyKind(t, DateTimeKind.Utc),
                    Eastern);
                if (etTime.Date != todayEt) continue;
                _equityCurve.Add((t, v));
                loaded++;
            }
            LogMessage($"[EQUITY CURVE] Loaded {loaded} intraday point(s) from disk.");
        }
        catch (Exception ex) { LogError("LoadEquityCurve", ex.Message); }
    }

    private void SnapshotLifetimeEquity()
    {
        try
        {
            var etDate = GetEasternTime().Date.ToString("yyyy-MM-dd");
            decimal accountValue = TOTAL_BUDGET + _totalRealizedPnL;

            lock (_lifetimeEquity)
            {
                var existing = _lifetimeEquity.FirstOrDefault(p => p.Date == etDate);
                if (existing != null)
                {
                    existing.AccountValue = accountValue;
                    existing.DailyPnL = _totalRealizedPnL;
                }
                else
                {
                    _lifetimeEquity.Add(new LifetimeEquityPoint
                    {
                        Date = etDate,
                        AccountValue = accountValue,
                        DailyPnL = _totalRealizedPnL
                    });
                }
            }
            SaveLifetimeEquity();
        }
        catch (Exception ex) { LogError("SnapshotLifetimeEquity", ex.Message); }
    }

    private void SaveLifetimeEquity()
    {
        try
        {
            string json;
            lock (_lifetimeEquity)
                json = JsonSerializer.Serialize(_lifetimeEquity);
            AtomicWrite(LIFETIME_EQUITY_FILE, json);
        }
        catch { }
    }

    public void LoadAllTrades()
    {
        string raw = SafeReadJson(ALL_TRADES_FILE);
        if (raw == null) return;
        try
        {
            var data = JsonSerializer.Deserialize<List<TradeRecord>>(raw);
            if (data == null) return;
            lock (_allTrades)
            {
                _allTrades.Clear();
                _allTrades.AddRange(data);
            }
            LogMessage($"[ALL TRADES] Loaded {_allTrades.Count} historical trade records.");
        }
        catch (Exception ex) { LogError("LoadAllTrades", ex.Message); }
    }

    private void SaveAllTrades()
    {
        try
        {
            string json;
            lock (_allTrades)
                json = JsonSerializer.Serialize(_allTrades);
            AtomicWrite(ALL_TRADES_FILE, json);
        }
        catch { }
    }

    public void LoadLifetimeEquity()
    {
        string raw = SafeReadJson(LIFETIME_EQUITY_FILE);
        if (raw == null) return;
        try
        {
            var data = JsonSerializer.Deserialize<List<LifetimeEquityPoint>>(raw);
            if (data == null) return;
            lock (_lifetimeEquity)
            {
                _lifetimeEquity.Clear();
                _lifetimeEquity.AddRange(data.OrderBy(p => p.Date));
            }
            LogMessage($"[LIFETIME] Loaded {_lifetimeEquity.Count} daily equity points.");
        }
        catch (Exception ex) { LogError("LoadLifetimeEquity", ex.Message); }
    }

    private void LogTradeAnalytics(string symbol, decimal entry, decimal exit,
        decimal pnl, decimal holdMinutes, decimal atr, double rsi,
        string strategy = "", bool isShort = false)
    {
        try
        {
            using var sw = new StreamWriter("trade_analytics.csv", true);
            sw.WriteLine($"{DateTime.UtcNow:O},{symbol},{entry},{exit}," +
                         $"{pnl},{holdMinutes:F2},{atr},{rsi:F2},{strategy},{(isShort ? "SHORT" : "LONG")}");
        }
        catch { }
    }

    // ══════════════════════════════════════════════════════════
    //  HTTP DASHBOARD SERVER
    // ══════════════════════════════════════════════════════════

    private static readonly string CONFIG_FILE = StatePath("bot-config.json");
    private const string CONFIG_PASSWORD = "Efmukl123!";

    private string EffectiveConfigPassword()
    {
        var env = Environment.GetEnvironmentVariable("BOT_CONFIG_PASSWORD");
        return string.IsNullOrWhiteSpace(env) ? CONFIG_PASSWORD : env.Trim();
    }

    private bool PasswordMatches(string? supplied) => supplied == EffectiveConfigPassword();

    public void LoadConfig()
    {
        try
        {
            if (!File.Exists(CONFIG_FILE)) return;
            var text = File.ReadAllText(CONFIG_FILE);
            using var doc = System.Text.Json.JsonDocument.Parse(text);
            var root = doc.RootElement;

            T Get<T>(string key, T fallback)
            {
                if (!root.TryGetProperty(key, out var el)) return fallback;
                try { return (T)Convert.ChangeType(el.GetRawText().Trim('"'), typeof(T)); } catch { return fallback; }
            }
            decimal GetD(string k, decimal fb) => Get<decimal>(k, fb);
            int GetI(string k, int fb) => Get<int>(k, fb);
            double GetF(string k, double fb) => Get<double>(k, fb);
            bool GetB(string k, bool fb) { if (!root.TryGetProperty(k, out var el)) return fb; return el.GetBoolean(); }

            TOTAL_BUDGET = GetD("TOTAL_BUDGET", TOTAL_BUDGET);
            MAX_POSITIONS = GetI("MAX_POSITIONS", MAX_POSITIONS);
            POSITION_SIZE = GetD("POSITION_SIZE", POSITION_SIZE);
            MIN_HOLD_SECONDS = GetI("MIN_HOLD_SECONDS", MIN_HOLD_SECONDS);
            DAILY_PROFIT_GOAL = GetD("DAILY_PROFIT_GOAL", DAILY_PROFIT_GOAL);
            MAX_DAILY_LOSS = GetD("MAX_DAILY_LOSS", MAX_DAILY_LOSS);
            COOLDOWN_SECONDS = GetI("COOLDOWN_SECONDS", COOLDOWN_SECONDS);
            ATR_TRAIL_MULT = GetD("ATR_TRAIL_MULT", ATR_TRAIL_MULT);
            SHORT_ATR_TRAIL = GetD("SHORT_ATR_TRAIL", SHORT_ATR_TRAIL);
            HARD_STOP_ATR_MULT = GetD("HARD_STOP_ATR_MULT", HARD_STOP_ATR_MULT);
            MAX_LOSS_PER_TRADE = GetD("MAX_LOSS_PER_TRADE", MAX_LOSS_PER_TRADE);
            COMMISSION_PER_SIDE = GetD("COMMISSION_PER_SIDE", COMMISSION_PER_SIDE);
            MIN_STOP_DISTANCE = GetD("MIN_STOP_DISTANCE", MIN_STOP_DISTANCE);
            MAX_QTY_SANITY = GetI("MAX_QTY_SANITY", MAX_QTY_SANITY);
            RISK_PCT = GetD("RISK_PCT", RISK_PCT);
            ORB_MINUTES = GetI("ORB_MINUTES", ORB_MINUTES);
            VOL_EXPAND_MULT = GetD("VOL_EXPAND_MULT", VOL_EXPAND_MULT);
            RSI_LONG_MIN = GetF("RSI_LONG_MIN", RSI_LONG_MIN);
            RSI_SHORT_MAX = GetF("RSI_SHORT_MAX", RSI_SHORT_MAX);
            RSI_OVERSOLD = GetF("RSI_OVERSOLD", RSI_OVERSOLD);
            RSI_OVERBOUGHT = GetF("RSI_OVERBOUGHT", RSI_OVERBOUGHT);
            GAP_GO_MIN_PCT = GetD("GAP_GO_MIN_PCT", GAP_GO_MIN_PCT);
            GAP_GO_REL_VOL = GetD("GAP_GO_REL_VOL", GAP_GO_REL_VOL);
            VWAP_CONFIRM_BARS = GetI("VWAP_CONFIRM_BARS", VWAP_CONFIRM_BARS);
            MAX_TRADES_PER_DAY = GetI("MAX_TRADES_PER_DAY", MAX_TRADES_PER_DAY);
            MIN_ATR_PCT = GetD("MIN_ATR_PCT", MIN_ATR_PCT);
            _allowShorts = GetB("ALLOW_SHORTS", _allowShorts);
            MIDDAY_FILTER_ENABLED = GetB("MIDDAY_FILTER_ENABLED", MIDDAY_FILTER_ENABLED);
            MAX_CONSECUTIVE_LOSSES = GetI("MAX_CONSECUTIVE_LOSSES", MAX_CONSECUTIVE_LOSSES);
            STRATEGY_ORB_ENABLED = GetB("STRATEGY_ORB", STRATEGY_ORB_ENABLED);
            STRATEGY_GAP_GO_ENABLED = GetB("STRATEGY_GAP_GO", STRATEGY_GAP_GO_ENABLED);
            STRATEGY_VWAP_ENABLED = GetB("STRATEGY_VWAP", STRATEGY_VWAP_ENABLED);
            STRATEGY_MEAN_REV_ENABLED = GetB("STRATEGY_MEAN_REV", STRATEGY_MEAN_REV_ENABLED);
            STRATEGY_BB_MR_ENABLED = GetB("STRATEGY_BB_MR", STRATEGY_BB_MR_ENABLED);
            STRATEGY_MOMENTUM_ENABLED = GetB("STRATEGY_MOMENTUM", STRATEGY_MOMENTUM_ENABLED);
            STRATEGY_EMA_POCKET_ENABLED = GetB("STRATEGY_EMA_POCKET", STRATEGY_EMA_POCKET_ENABLED);
            STRATEGY_OUTSIDE_CANDLE_ENABLED = GetB("STRATEGY_OUTSIDE_CANDLE", STRATEGY_OUTSIDE_CANDLE_ENABLED);
            DATA_LINES_PER_SYMBOL = GetI("DATA_LINES_PER_SYMBOL", DATA_LINES_PER_SYMBOL);
            MAX_MARKET_DATA_LINES = GetI("MAX_MARKET_DATA_LINES", MAX_MARKET_DATA_LINES);
            VIX_REDUCE_THRESHOLD = GetD("VIX_REDUCE_THRESHOLD", VIX_REDUCE_THRESHOLD);
            VIX_NO_LONG_THRESHOLD = GetD("VIX_NO_LONG_THRESHOLD", VIX_NO_LONG_THRESHOLD);
            MIN_SETUP_SCORE = GetI("MIN_SETUP_SCORE", MIN_SETUP_SCORE);

            if (root.TryGetProperty("earnings_blacklist", out var ebEl) && ebEl.ValueKind == System.Text.Json.JsonValueKind.Array)
            {
                _earningsBlacklist.Clear();
                foreach (var item in ebEl.EnumerateArray())
                {
                    var s = item.GetString();
                    if (!string.IsNullOrWhiteSpace(s)) _earningsBlacklist.Add(s.Trim().ToUpper());
                }
                if (_earningsBlacklist.Count > 0)
                    Console.WriteLine($"[CONFIG] Earnings blacklist loaded: {string.Join(", ", _earningsBlacklist)}");
            }

            if (root.TryGetProperty("watchlist", out var wlEl) && wlEl.ValueKind == System.Text.Json.JsonValueKind.Array)
            {
                var list = new List<string>();
                foreach (var item in wlEl.EnumerateArray())
                {
                    var s = item.GetString();
                    if (!string.IsNullOrWhiteSpace(s)) list.Add(s.Trim().ToUpper());
                }
                int slotCap = DATA_LINES_PER_SYMBOL > 0
                    ? MAX_MARKET_DATA_LINES / DATA_LINES_PER_SYMBOL
                    : MAX_MARKET_DATA_LINES;
                if (list.Count > slotCap)
                {
                    Console.WriteLine($"[CONFIG] Watchlist in config has {list.Count} symbols — trimming to {slotCap} to stay within data line budget.");
                    list = list.Take(slotCap).ToList();
                }
                if (list.Count > 0) _watchlist = list.ToArray();
            }

            Console.WriteLine($"[CONFIG] Loaded from {CONFIG_FILE}");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[CONFIG] Load failed ({ex.Message}), using defaults.");
        }
    }

    private void SaveConfig()
    {
        try
        {
            AtomicWrite(CONFIG_FILE, BuildConfigJson(pretty: true));
            Console.WriteLine($"[CONFIG] Saved to {CONFIG_FILE}");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[CONFIG] Save failed: {ex.Message}");
        }
    }

    private string BuildConfigJson(bool pretty = false)
    {
        var wlJson = "[" + string.Join(",", _watchlist.Select(s => $"\"{s}\"")) + "]";
        var indent = pretty ? "\n  " : "";
        var nl = pretty ? "\n" : "";
        var sep = pretty ? ",\n  " : ",";
        return $"{{{nl}{indent}" + string.Join(sep, new[]
        {
            $"\"TOTAL_BUDGET\":{TOTAL_BUDGET:F2}",
            $"\"MAX_POSITIONS\":{MAX_POSITIONS}",
            $"\"POSITION_SIZE\":{POSITION_SIZE:F2}",
            $"\"MIN_HOLD_SECONDS\":{MIN_HOLD_SECONDS}",
            $"\"DAILY_PROFIT_GOAL\":{DAILY_PROFIT_GOAL:F2}",
            $"\"MAX_DAILY_LOSS\":{MAX_DAILY_LOSS:F2}",
            $"\"COOLDOWN_SECONDS\":{COOLDOWN_SECONDS}",
            $"\"ATR_TRAIL_MULT\":{ATR_TRAIL_MULT:F2}",
            $"\"SHORT_ATR_TRAIL\":{SHORT_ATR_TRAIL:F2}",
            $"\"HARD_STOP_ATR_MULT\":{HARD_STOP_ATR_MULT:F2}",
            $"\"MAX_LOSS_PER_TRADE\":{MAX_LOSS_PER_TRADE:F2}",
            $"\"COMMISSION_PER_SIDE\":{COMMISSION_PER_SIDE:F2}",
            $"\"MIN_STOP_DISTANCE\":{MIN_STOP_DISTANCE:F2}",
            $"\"MAX_QTY_SANITY\":{MAX_QTY_SANITY}",
            $"\"RISK_PCT\":{RISK_PCT:F4}",
            $"\"ORB_MINUTES\":{ORB_MINUTES}",
            $"\"VOL_EXPAND_MULT\":{VOL_EXPAND_MULT:F2}",
            $"\"RSI_LONG_MIN\":{RSI_LONG_MIN:F1}",
            $"\"RSI_SHORT_MAX\":{RSI_SHORT_MAX:F1}",
            $"\"RSI_OVERSOLD\":{RSI_OVERSOLD:F1}",
            $"\"RSI_OVERBOUGHT\":{RSI_OVERBOUGHT:F1}",
            $"\"GAP_GO_MIN_PCT\":{GAP_GO_MIN_PCT:F4}",
            $"\"GAP_GO_REL_VOL\":{GAP_GO_REL_VOL:F2}",
            $"\"VWAP_CONFIRM_BARS\":{VWAP_CONFIRM_BARS}",
            $"\"MAX_TRADES_PER_DAY\":{MAX_TRADES_PER_DAY}",
            $"\"MIN_ATR_PCT\":{MIN_ATR_PCT:F4}",
            $"\"ALLOW_SHORTS\":{(_allowShorts ? "true" : "false")}",
            $"\"MIDDAY_FILTER_ENABLED\":{(MIDDAY_FILTER_ENABLED ? "true" : "false")}",
            $"\"MAX_CONSECUTIVE_LOSSES\":{MAX_CONSECUTIVE_LOSSES}",
            $"\"STRATEGY_ORB\":{(STRATEGY_ORB_ENABLED ? "true" : "false")}",
            $"\"STRATEGY_GAP_GO\":{(STRATEGY_GAP_GO_ENABLED ? "true" : "false")}",
            $"\"STRATEGY_VWAP\":{(STRATEGY_VWAP_ENABLED ? "true" : "false")}",
            $"\"STRATEGY_MEAN_REV\":{(STRATEGY_MEAN_REV_ENABLED ? "true" : "false")}",
            $"\"STRATEGY_BB_MR\":{(STRATEGY_BB_MR_ENABLED ? "true" : "false")}",
            $"\"STRATEGY_MOMENTUM\":{(STRATEGY_MOMENTUM_ENABLED ? "true" : "false")}",
            $"\"STRATEGY_EMA_POCKET\":{(STRATEGY_EMA_POCKET_ENABLED ? "true" : "false")}",
            $"\"STRATEGY_OUTSIDE_CANDLE\":{(STRATEGY_OUTSIDE_CANDLE_ENABLED ? "true" : "false")}",
            $"\"DATA_LINES_PER_SYMBOL\":{DATA_LINES_PER_SYMBOL}",
            $"\"MAX_MARKET_DATA_LINES\":{MAX_MARKET_DATA_LINES}",
            $"\"VIX_REDUCE_THRESHOLD\":{VIX_REDUCE_THRESHOLD:F1}",
            $"\"VIX_NO_LONG_THRESHOLD\":{VIX_NO_LONG_THRESHOLD:F1}",
            $"\"MIN_SETUP_SCORE\":{MIN_SETUP_SCORE}",
            $"\"earnings_blacklist\":[{string.Join(",", _earningsBlacklist.Select(s => $"\"{s}\""))}]",
            $"\"watchlist\":{wlJson}"
        }) + $"{nl}}}";
    }

    private HttpListener _httpListener;

    private void StartDashboardServer()
    {
        try
        {
            _httpListener = new HttpListener();
            _httpListener.Prefixes.Add("http://*:5003/");
            _httpListener.Start();
            Task.Run(() => HandleDashboardRequests());
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[DASHBOARD] Failed to start HTTP server: {ex.Message}");
        }
    }

    private async Task HandleDashboardRequests()
    {
        while (_httpListener?.IsListening == true)
        {
            try
            {
                var ctx = await _httpListener.GetContextAsync();
                _ = Task.Run(() => ServeRequest(ctx));
            }
            catch { }
        }
    }

    private void ServeRequest(HttpListenerContext ctx)
    {
        try
        {
            ctx.Response.Headers.Add("Access-Control-Allow-Origin", "*");
            ctx.Response.Headers.Add("Access-Control-Allow-Methods", "GET, POST, OPTIONS");
            ctx.Response.Headers.Add("Access-Control-Allow-Headers", "Content-Type");
            ctx.Response.ContentType = "application/json";

            var path = ctx.Request.Url.AbsolutePath.ToLower();
            var method = ctx.Request.HttpMethod.ToUpper();
            string json;

            if (method == "OPTIONS")
            {
                ctx.Response.StatusCode = 204;
                ctx.Response.OutputStream.Close();
                return;
            }
            else if (path == "/api/backtest")
            {
                try
                {
                    List<TradeRecord> snapshot;
                    lock (_allTrades) { snapshot = _allTrades.ToList(); }
                    var report = BacktestEngine.Analyze(
                        snapshot,
                        _marketData,
                        _lifetimeEquity,
                        TOTAL_BUDGET);
                    json = JsonSerializer.Serialize(report);
                }
                catch (Exception ex)
                {
                    json = $"{{\"error\":\"{ex.Message.Replace("\"", "'")}\"}}";
                }
            }
            else if (path == "/api/status")
            {
                json = BuildStatusJson();
            }
            else if (path == "/api/candles")
            {
                var qs = ctx.Request.Url.Query;
                string sym = "";
                if (qs.StartsWith("?"))
                    foreach (var part in qs.Substring(1).Split('&'))
                    {
                        var kv = part.Split('=');
                        if (kv.Length == 2 && kv[0].ToLower() == "sym")
                            sym = Uri.UnescapeDataString(kv[1]).ToUpper();
                    }
                json = BuildCandlesJson(sym);
            }
            else if (path == "/api/config" && method == "GET")
            {
                json = BuildConfigJson(pretty: true);
            }
            else if (path == "/api/config" && method == "POST")
            {
                try
                {
                    using var reader = new System.IO.StreamReader(ctx.Request.InputStream, System.Text.Encoding.UTF8);
                    var body = reader.ReadToEnd();
                    using var doc = System.Text.Json.JsonDocument.Parse(body);
                    var root = doc.RootElement;

                    if (!root.TryGetProperty("password", out var pwEl) ||
                        !PasswordMatches(pwEl.GetString()))
                    {
                        ctx.Response.StatusCode = 401;
                        json = "{\"ok\":false,\"message\":\"Incorrect password.\"}";
                        byte[] deny = System.Text.Encoding.UTF8.GetBytes(json);
                        ctx.Response.ContentLength64 = deny.Length;
                        ctx.Response.OutputStream.Write(deny, 0, deny.Length);
                        return;
                    }

                    T Get<T>(string key, T fallback)
                    {
                        if (!root.TryGetProperty(key, out var el)) return fallback;
                        try { return (T)Convert.ChangeType(el.GetRawText().Trim('"'), typeof(T)); } catch { return fallback; }
                    }
                    decimal GetD(string k, decimal fb) => Get<decimal>(k, fb);
                    int GetI(string k, int fb) => Get<int>(k, fb);
                    double GetF(string k, double fb) => Get<double>(k, fb);
                    bool GetB(string k, bool fb) { if (!root.TryGetProperty(k, out var el)) return fb; return el.GetBoolean(); }

                    string[] oldWatchlist;
                    string[] newWatchlist = null;
                    bool orbChanged;

                    lock (_lock)
                    {
                        oldWatchlist = _watchlist.ToArray();
                        int oldOrbMinutes = ORB_MINUTES;

                        TOTAL_BUDGET = GetD("TOTAL_BUDGET", TOTAL_BUDGET);
                        MAX_POSITIONS = GetI("MAX_POSITIONS", MAX_POSITIONS);
                        POSITION_SIZE = GetD("POSITION_SIZE", POSITION_SIZE);
                        MIN_HOLD_SECONDS = GetI("MIN_HOLD_SECONDS", MIN_HOLD_SECONDS);
                        DAILY_PROFIT_GOAL = GetD("DAILY_PROFIT_GOAL", DAILY_PROFIT_GOAL);
                        MAX_DAILY_LOSS = GetD("MAX_DAILY_LOSS", MAX_DAILY_LOSS);
                        COOLDOWN_SECONDS = GetI("COOLDOWN_SECONDS", COOLDOWN_SECONDS);
                        ATR_TRAIL_MULT = GetD("ATR_TRAIL_MULT", ATR_TRAIL_MULT);
                        SHORT_ATR_TRAIL = GetD("SHORT_ATR_TRAIL", SHORT_ATR_TRAIL);
                        HARD_STOP_ATR_MULT = GetD("HARD_STOP_ATR_MULT", HARD_STOP_ATR_MULT);
                        MAX_LOSS_PER_TRADE = GetD("MAX_LOSS_PER_TRADE", MAX_LOSS_PER_TRADE);
                        COMMISSION_PER_SIDE = GetD("COMMISSION_PER_SIDE", COMMISSION_PER_SIDE);
                        MIN_STOP_DISTANCE = GetD("MIN_STOP_DISTANCE", MIN_STOP_DISTANCE);
                        MAX_QTY_SANITY = GetI("MAX_QTY_SANITY", MAX_QTY_SANITY);
                        RISK_PCT = GetD("RISK_PCT", RISK_PCT);
                        ORB_MINUTES = GetI("ORB_MINUTES", ORB_MINUTES);
                        VOL_EXPAND_MULT = GetD("VOL_EXPAND_MULT", VOL_EXPAND_MULT);
                        RSI_LONG_MIN = GetF("RSI_LONG_MIN", RSI_LONG_MIN);
                        RSI_SHORT_MAX = GetF("RSI_SHORT_MAX", RSI_SHORT_MAX);
                        RSI_OVERSOLD = GetF("RSI_OVERSOLD", RSI_OVERSOLD);
                        RSI_OVERBOUGHT = GetF("RSI_OVERBOUGHT", RSI_OVERBOUGHT);
                        GAP_GO_MIN_PCT = GetD("GAP_GO_MIN_PCT", GAP_GO_MIN_PCT);
                        GAP_GO_REL_VOL = GetD("GAP_GO_REL_VOL", GAP_GO_REL_VOL);
                        VWAP_CONFIRM_BARS = GetI("VWAP_CONFIRM_BARS", VWAP_CONFIRM_BARS);
                        MAX_TRADES_PER_DAY = GetI("MAX_TRADES_PER_DAY", MAX_TRADES_PER_DAY);
                        MIN_ATR_PCT = GetD("MIN_ATR_PCT", MIN_ATR_PCT);
                        _allowShorts = GetB("ALLOW_SHORTS", _allowShorts);
                        MIDDAY_FILTER_ENABLED = GetB("MIDDAY_FILTER_ENABLED", MIDDAY_FILTER_ENABLED);
                        MAX_CONSECUTIVE_LOSSES = GetI("MAX_CONSECUTIVE_LOSSES", MAX_CONSECUTIVE_LOSSES);
                        STRATEGY_ORB_ENABLED = GetB("STRATEGY_ORB", STRATEGY_ORB_ENABLED);
                        STRATEGY_GAP_GO_ENABLED = GetB("STRATEGY_GAP_GO", STRATEGY_GAP_GO_ENABLED);
                        STRATEGY_VWAP_ENABLED = GetB("STRATEGY_VWAP", STRATEGY_VWAP_ENABLED);
                        STRATEGY_MEAN_REV_ENABLED = GetB("STRATEGY_MEAN_REV", STRATEGY_MEAN_REV_ENABLED);
                        STRATEGY_BB_MR_ENABLED = GetB("STRATEGY_BB_MR", STRATEGY_BB_MR_ENABLED);
                        STRATEGY_MOMENTUM_ENABLED = GetB("STRATEGY_MOMENTUM", STRATEGY_MOMENTUM_ENABLED);
                        STRATEGY_EMA_POCKET_ENABLED = GetB("STRATEGY_EMA_POCKET", STRATEGY_EMA_POCKET_ENABLED);
                        STRATEGY_OUTSIDE_CANDLE_ENABLED = GetB("STRATEGY_OUTSIDE_CANDLE", STRATEGY_OUTSIDE_CANDLE_ENABLED);
                        DATA_LINES_PER_SYMBOL = GetI("DATA_LINES_PER_SYMBOL", DATA_LINES_PER_SYMBOL);
                        MAX_MARKET_DATA_LINES = GetI("MAX_MARKET_DATA_LINES", MAX_MARKET_DATA_LINES);
                        VIX_REDUCE_THRESHOLD = GetD("VIX_REDUCE_THRESHOLD", VIX_REDUCE_THRESHOLD);
                        VIX_NO_LONG_THRESHOLD = GetD("VIX_NO_LONG_THRESHOLD", VIX_NO_LONG_THRESHOLD);
                        MIN_SETUP_SCORE = GetI("MIN_SETUP_SCORE", MIN_SETUP_SCORE);

                        if (root.TryGetProperty("earnings_blacklist", out var ebLiveEl) && ebLiveEl.ValueKind == System.Text.Json.JsonValueKind.Array)
                        {
                            _earningsBlacklist.Clear();
                            foreach (var item in ebLiveEl.EnumerateArray())
                            {
                                var s = item.GetString();
                                if (!string.IsNullOrWhiteSpace(s)) _earningsBlacklist.Add(s.Trim().ToUpper());
                            }
                            LogMessage($"[CONFIG] Earnings blacklist updated: {(_earningsBlacklist.Count > 0 ? string.Join(", ", _earningsBlacklist) : "(empty)")}");
                        }

                        orbChanged = ORB_MINUTES != oldOrbMinutes;

                        if (root.TryGetProperty("watchlist", out var wlEl) && wlEl.ValueKind == System.Text.Json.JsonValueKind.Array)
                        {
                            var list = new List<string>();
                            foreach (var item in wlEl.EnumerateArray())
                            {
                                var s = item.GetString();
                                if (!string.IsNullOrWhiteSpace(s)) list.Add(s.Trim().ToUpper());
                            }
                            int liveCap = Math.Max(1, DATA_LINES_PER_SYMBOL) > 0
                                ? MAX_MARKET_DATA_LINES / Math.Max(1, DATA_LINES_PER_SYMBOL)
                                : MAX_MARKET_DATA_LINES;
                            if (list.Count > liveCap)
                            {
                                LogMessage($"[CONFIG] Watchlist trimmed {list.Count}→{liveCap} (data line budget).");
                                list = list.Take(liveCap).ToList();
                            }
                            if (list.Count > 0)
                            {
                                newWatchlist = list.ToArray();
                                _watchlist = newWatchlist;
                            }
                        }
                    }

                    ReevaluateHalt();

                    if (orbChanged)
                    {
                        _orbRanges.Clear();
                        LogMessage($"[CONFIG] ORB_MINUTES changed — cleared all opening ranges, will recompute.");
                    }

                    if (newWatchlist != null)
                        _ = Task.Run(() => ApplyWatchlistDiff(oldWatchlist, newWatchlist));

                    SaveConfig();
                    json = "{\"ok\":true,\"message\":\"Configuration saved and applied live.\"}";
                }
                catch (Exception ex)
                {
                    ctx.Response.StatusCode = 400;
                    json = $"{{\"ok\":false,\"message\":\"{ex.Message.Replace("\"", "\\\"").Replace("\n", " ")}\"}}";
                }
            }
            else if (path == "/api/sell" && method == "POST")
            {
                try
                {
                    using var reader = new System.IO.StreamReader(ctx.Request.InputStream, System.Text.Encoding.UTF8);
                    var body = reader.ReadToEnd();
                    using var doc = System.Text.Json.JsonDocument.Parse(body);
                    var root = doc.RootElement;

                    if (!root.TryGetProperty("password", out var pwEl) || !PasswordMatches(pwEl.GetString()))
                    {
                        ctx.Response.StatusCode = 401;
                        json = "{\"ok\":false,\"message\":\"Incorrect password.\"}";
                        byte[] deny = System.Text.Encoding.UTF8.GetBytes(json);
                        ctx.Response.ContentLength64 = deny.Length;
                        ctx.Response.OutputStream.Write(deny, 0, deny.Length);
                        return;
                    }

                    if (!root.TryGetProperty("symbol", out var symEl))
                    {
                        ctx.Response.StatusCode = 400;
                        json = "{\"ok\":false,\"message\":\"Missing symbol.\"}";
                        byte[] deny = System.Text.Encoding.UTF8.GetBytes(json);
                        ctx.Response.ContentLength64 = deny.Length;
                        ctx.Response.OutputStream.Write(deny, 0, deny.Length);
                        return;
                    }

                    var sym = symEl.GetString()?.ToUpper()?.Trim();
                    lock (_lock)
                    {
                        if (!_positions.TryGetValue(sym, out var pos))
                        {
                            json = $"{{\"ok\":false,\"message\":\"No open position found for {sym}.\"}}";
                        }
                        else
                        {
                            bool alreadyPending = pos.ExitSubmitted;
                            pos.ExitSubmitted = true;
                            TradeSide exitSide = pos.IsShort ? TradeSide.Buy : TradeSide.Sell;
                            string tag = alreadyPending ? "MANUAL_FORCE_EXIT" : "MANUAL_SELL";
                            SubmitOrder(sym, pos.Quantity, 0, exitSide, tag, "MKT");
                            string warn = alreadyPending ? " (prior exit order was pending — forced new MKT)" : "";
                            LogMessage($"[{tag}] Dashboard triggered market exit for {sym} x{pos.Quantity}.{warn}");
                            json = $"{{\"ok\":true,\"message\":\"Market exit submitted for {sym} x{pos.Quantity}.{warn.Replace("\"", "'")}\"}}";
                        }
                    }
                }
                catch (Exception ex)
                {
                    ctx.Response.StatusCode = 400;
                    json = $"{{\"ok\":false,\"message\":\"{ex.Message.Replace("\"", "\\\"").Replace("\n", " ")}\"}}";
                }
            }
            else if (path == "/api/liquidate" && method == "POST")
            {
                try
                {
                    using var reader = new System.IO.StreamReader(ctx.Request.InputStream, System.Text.Encoding.UTF8);
                    var body = reader.ReadToEnd();
                    using var doc = System.Text.Json.JsonDocument.Parse(body);
                    var root = doc.RootElement;

                    if (!root.TryGetProperty("password", out var pwEl) || !PasswordMatches(pwEl.GetString()))
                    {
                        ctx.Response.StatusCode = 401;
                        json = "{\"ok\":false,\"message\":\"Incorrect password.\"}";
                        byte[] deny = System.Text.Encoding.UTF8.GetBytes(json);
                        ctx.Response.ContentLength64 = deny.Length;
                        ctx.Response.OutputStream.Write(deny, 0, deny.Length);
                        return;
                    }

                    int count = 0;
                    lock (_lock)
                    {
                        foreach (var pos in _positions.Values.ToList())
                        {
                            bool alreadyPending = pos.ExitSubmitted;
                            pos.ExitSubmitted = true;
                            TradeSide exitSide = pos.IsShort ? TradeSide.Buy : TradeSide.Sell;
                            string tag = alreadyPending ? "MANUAL_FORCE_LIQUIDATE" : "MANUAL_LIQUIDATE";
                            SubmitOrder(pos.Symbol, pos.Quantity, 0, exitSide, tag, "MKT");
                            count++;
                        }
                    }
                    LogMessage($"[MANUAL LIQUIDATE] Dashboard triggered liquidation of {count} position(s).");
                    json = $"{{\"ok\":true,\"message\":\"Liquidated {count} position(s) at market.\"}}";
                }
                catch (Exception ex)
                {
                    ctx.Response.StatusCode = 400;
                    json = $"{{\"ok\":false,\"message\":\"{ex.Message.Replace("\"", "\\\"").Replace("\n", " ")}\"}}";
                }
            }
            else
            {
                json = "{}";
            }

            byte[] buf = System.Text.Encoding.UTF8.GetBytes(json);
            ctx.Response.ContentLength64 = buf.Length;
            ctx.Response.OutputStream.Write(buf, 0, buf.Length);
        }
        catch { }
        finally { try { ctx.Response.OutputStream.Close(); } catch { } }
    }

    private string BuildCandlesJson(string sym)
    {
        if (string.IsNullOrEmpty(sym) || !_marketData.TryGetValue(sym, out var candles))
            return @"{""sym"":"""",""candles"":[]}";

        List<Candle> snapshot;
        lock (candles) { snapshot = candles.ToList(); }

        if (_currentMinuteCandle.TryGetValue(sym, out var live))
        {
            if (!snapshot.Any(c => c.Time == live.Time))
                snapshot.Add(live);
        }

        _vwap.TryGetValue(sym, out decimal vwapNow);
        var (pdHiLine, pdLoLine) = GetPrevDayHL(sym);
        decimal sma200Line = GetDailySma200(sym);

        var sb = new StringBuilder();
        sb.Append($@"{{""sym"":""{sym}"",""vwap"":{vwapNow:F2},""pdHi"":{pdHiLine:F2},""pdLo"":{pdLoLine:F2},""sma200"":{sma200Line:F2},""candles"":[");
        for (int i = 0; i < snapshot.Count; i++)
        {
            var c = snapshot[i];
            if (i > 0) sb.Append(",");
            sb.Append($@"{{""t"":""{c.Time:HH:mm}"",""o"":{c.Open:F2},""h"":{c.High:F2},""l"":{c.Low:F2},""c"":{c.Close:F2},""v"":{c.Volume}}}");
        }
        sb.Append("]}");
        return sb.ToString();
    }

    private string BuildStatusJson()
    {
        lock (_lock)
        {
            var now = GetPacificTime();
            var et = GetEasternTime();
            int total = _winCount + _lossCount;
            double wr = total > 0 ? (double)_winCount / total * 100 : 0;

            var posArr = new StringBuilder("[");
            bool first = true;
            foreach (var kv in _positions)
            {
                var p = kv.Value;
                decimal px = _latestTick.TryGetValue(p.Symbol, out var tp) ? tp : p.CurrentPrice;
                decimal unrl = p.UnrealizedPnL(px);
                decimal pnlPt = p.AvgPrice > 0
                    ? (px - p.AvgPrice) / p.AvgPrice * (p.IsShort ? -1 : 1) * 100 : 0;
                double heldMin = (DateTime.UtcNow - p.EntryTime).TotalMinutes;
                if (!first) posArr.Append(",");
                posArr.Append($@"{{""sym"":""{p.Symbol}"",""qty"":{p.Quantity},""side"":""{(p.IsShort ? "SHORT" : "LONG")}"",""avg"":{p.AvgPrice:F2},""cur"":{px:F2},""unrl"":{unrl:F2},""pct"":{pnlPt:F2},""min"":{heldMin:F1},""strat"":""{p.StrategyTag}"",""exitPending"":{(p.ExitSubmitted ? "true" : "false")}}}");
                first = false;
            }
            posArr.Append("]");

            var curve = _equityCurve.TakeLast(390).ToList();
            var curveArr = new StringBuilder("[");
            for (int i = 0; i < curve.Count; i++)
            {
                if (i > 0) curveArr.Append(",");
                curveArr.Append($@"{{""t"":""{curve[i].time:HH:mm}"",""v"":{curve[i].equity:F2}}}");
            }
            curveArr.Append("]");

            var wlArr = new StringBuilder("[");
            bool wfirst = true;
            foreach (var sym in _watchlist)
            {
                _marketData.TryGetValue(sym, out var candles);
                decimal price = candles?.LastOrDefault()?.Close ?? 0m;
                bool dataReady = price > 0m;

                var candleList = candles ?? new List<Candle>();
                decimal sma20 = dataReady ? SafeSMA(candleList, 20) : 0m;
                decimal sma50 = dataReady ? SafeSMA(candleList, 50) : 0m;
                double rsi = dataReady ? SafeRSI(candleList, 14) : 0.0;
                decimal atr = dataReady ? SafeATR(candleList, 14) : 0m;
                decimal atrPct = price > 0 ? atr / price * 100 : 0;
                _vwap.TryGetValue(sym, out decimal vwap);
                _prevDayClose.TryGetValue(sym, out decimal prevClose);
                decimal gapPct = prevClose > 0 ? (price - prevClose) / prevClose * 100 : 0;
                _latestTick.TryGetValue(sym, out decimal livePx);
                decimal chgPct = prevClose > 0 && livePx > 0
                    ? (livePx - prevClose) / prevClose * 100 : gapPct;
                if (price == 0m && livePx > 0) price = livePx;
                long volK = _dailyVolume.GetValueOrDefault(sym) / 1000;
                bool abvVwap = vwap > 0 && price > vwap;
                string trend = price > sma50 ? "UP" : "NEUT";

                decimal sma200 = GetDailySma200(sym);
                var (pdHi, pdLo) = GetPrevDayHL(sym);
                int macdDir = dataReady ? SafeMACDDirection(candleList) : 0;

                _orbRanges.TryGetValue(sym, out var orb);
                decimal orbHi = orb?.High ?? 0m;
                decimal orbLo = orb?.Low ?? 0m;

                string sig = "";
                if (dataReady)
                {
                    if (orb != null && orb.IsSet)
                    {
                        if (price > orbHi) sig = "ORB↑";
                        else if (price < orbLo) sig = "ORB↓";
                    }
                    if (sig == "" && rsi < RSI_OVERSOLD && price > sma50) sig = "MR↑";
                    if (sig == "" && rsi > RSI_OVERBOUGHT && price < sma50) sig = "MR↓";
                    if (sig == "" && vwap > 0)
                    {
                        bool above = price > vwap;
                        _prevBarAboveVwap.TryGetValue(sym, out bool wasAbove);
                        if (!wasAbove && above) sig = "VWAP↑";
                        else if (wasAbove && !above) sig = "VWAP↓";
                    }
                }
                bool hot = dataReady && vwap > 0 && price > vwap && rsi > 55;

                if (!wfirst) wlArr.Append(",");
                wfirst = false;
                wlArr.Append($@"{{""s"":""{sym}"",""price"":{price:F2},""vwap"":{vwap:F2},""sma20"":{sma20:F2},""sma50"":{sma50:F2},""sma200"":{sma200:F2},""rsi"":{rsi:F1},""gap"":{gapPct:F2},""chg"":{chgPct:F2},""vol"":{volK},""atr"":{atrPct:F2},""orbHi"":{orbHi:F2},""orbLo"":{orbLo:F2},""pdHi"":{pdHi:F2},""pdLo"":{pdLo:F2},""macd"":{macdDir},""trend"":""{trend}"",""sig"":""{sig}"",""hot"":{(hot ? "true" : "false")},""abvVwap"":{(abvVwap ? "true" : "false")}}}");
            }
            wlArr.Append("]");

            var recentTrades = _tradeHistoryLog.TakeLast(20).Reverse().ToList();
            var tradeArr = new StringBuilder("[");
            for (int i = 0; i < recentTrades.Count; i++)
            {
                if (i > 0) tradeArr.Append(",");
                tradeArr.Append($@"""{recentTrades[i].Replace("\"", "\\\"").Replace("\r", "").Replace("\n", "")}""");
            }
            tradeArr.Append("]");

            var histArr = new StringBuilder("[");
            var histList = _completedTrades.ToList();
            for (int i = histList.Count - 1; i >= 0; i--)
            {
                var t = histList[i];
                if (i < histList.Count - 1) histArr.Append(",");
                histArr.Append($@"{{""sym"":""{t.Symbol}"",""side"":""{t.Side}"",""strat"":""{t.Strategy}"",""qty"":{t.Qty},""entry"":{t.Entry:F2},""exit"":{t.Exit:F2},""pnl"":{t.NetPnL:F2},""min"":{t.HoldMinutes:F0},""reason"":""{t.ExitReason}"",""time"":""{t.Time}""}}");
            }
            histArr.Append("]");

            decimal cash = TOTAL_BUDGET - _positions.Values.Sum(p => p.AvgPrice * p.Quantity);

            var ltArr = new StringBuilder("[");
            lock (_lifetimeEquity)
            {
                var pts = _lifetimeEquity.ToList();
                var todayStr = et.Date.ToString("yyyy-MM-dd");
                bool todayExists = pts.Any(p => p.Date == todayStr);
                if (!todayExists)
                    pts.Add(new LifetimeEquityPoint
                    {
                        Date = todayStr,
                        AccountValue = TOTAL_BUDGET + _totalRealizedPnL,
                        DailyPnL = _totalRealizedPnL
                    });
                else
                {
                    var todayPt = pts.First(p => p.Date == todayStr);
                    todayPt.AccountValue = TOTAL_BUDGET + _totalRealizedPnL;
                    todayPt.DailyPnL = _totalRealizedPnL;
                }
                for (int i = 0; i < pts.Count; i++)
                {
                    if (i > 0) ltArr.Append(",");
                    var pt = pts[i];
                    ltArr.Append($@"{{""d"":""{pt.Date}"",""v"":{pt.AccountValue:F2},""p"":{pt.DailyPnL:F2}}}");
                }
            }
            ltArr.Append("]");

            var allArr = new StringBuilder("[");
            lock (_allTrades)
            {
                var all = _allTrades.ToList();
                int start = Math.Max(0, all.Count - 500);
                bool firstAt = true;
                for (int i = all.Count - 1; i >= start; i--)
                {
                    var t = all[i];
                    if (!firstAt) allArr.Append(",");
                    allArr.Append($@"{{""sym"":""{t.Symbol}"",""side"":""{t.Side}"",""strat"":""{t.Strategy}"",""qty"":{t.Qty},""entry"":{t.Entry:F2},""exit"":{t.Exit:F2},""pnl"":{t.NetPnL:F2},""min"":{t.HoldMinutes:F0},""reason"":""{t.ExitReason}"",""time"":""{t.Time}"",""date"":""{t.Date}""}}");
                    firstAt = false;
                }
            }
            allArr.Append("]");

            return $@"{{""time"":""{now:yyyy-MM-dd HH:mm:ss} PT"",""et"":""{et:HH:mm:ss} ET"",""regime"":""{_marketRegime}"",""spyBullish"":{(_spyBullish ? "true" : "false")},""spyBearish"":{(_spyBearish ? "true" : "false")},""halted"":{(_haltTrading ? "true" : "false")},""reconciled"":{(_reconciled ? "true" : "false")},""pnl"":{_totalRealizedPnL:F2},""goal"":{DAILY_PROFIT_GOAL:F2},""maxLoss"":{MAX_DAILY_LOSS:F2},""trades"":{_tradesToday},""maxTrades"":{MAX_TRADES_PER_DAY},""wins"":{_winCount},""losses"":{_lossCount},""wr"":{wr:F1},""cash"":{cash:F2},""budget"":{TOTAL_BUDGET:F2},""initialBudget"":{TOTAL_BUDGET:F2},""positions"":{posArr},""curve"":{curveArr},""watchlist"":{wlArr},""feed"":{tradeArr},""hist"":{histArr},""lifetimeCurve"":{ltArr},""allTrades"":{allArr}}}";
        }
    }

    private const int LOG_LINES = 8;
    private readonly Queue<string> _logQueue = new Queue<string>();
    private int _dashTick = 0;

    public void Start()
    {
        Console.CursorVisible = false;
        Console.OutputEncoding = System.Text.Encoding.UTF8;
        _dashboardTimer = new Timer(_ => PrintDetailedDashboard(), null, 0, 1000);
        StartDashboardServer();
    }

    public void LogMessage(string msg)
    {
        lock (_logQueue)
        {
            if (_logQueue.Count >= LOG_LINES) _logQueue.Dequeue();
            _logQueue.Enqueue($"{GetPacificTime():HH:mm:ss}  {msg}");
        }
    }

    public void PrintDetailedDashboard()
    {
        try
        {
            _dashTick++;
            int W = Math.Max(Console.WindowWidth, 110);
            var sb = new StringBuilder();
            var now = GetPacificTime();
            var et = GetEasternTime();

            int total = _winCount + _lossCount;
            double winRate = total > 0 ? (double)_winCount / total * 100 : 0;
            decimal cash = TOTAL_BUDGET - _positions.Count * POSITION_SIZE;
            decimal pnlGoalPct = DAILY_PROFIT_GOAL > 0
                ? _totalRealizedPnL / DAILY_PROFIT_GOAL * 100 : 0;
            decimal lossUsedPct = MAX_DAILY_LOSS < 0
                ? _totalRealizedPnL / MAX_DAILY_LOSS * 100 : 0;

            string regimeIcon = _marketRegime switch
            {
                "TRENDING" => "▲ TRENDING",
                "CHOPPY" => "~ CHOPPY  ",
                "SELL-OFF" => "▼ SELL-OFF",
                _ => "● NORMAL  "
            };

            string statusStr = _haltTrading
                ? "  ██ HALTED ██  "
                : (_dashTick % 2 == 0 ? "  ● LIVE       " : "  ○ LIVE       ");

            sb.AppendLine(
                $"  IBKR BOT  │  {now:ddd HH:mm:ss} PT  │  ET: {et:HH:mm}  │" +
                $"  {regimeIcon}  │  {statusStr}".PadRight(W));
            sb.AppendLine(new string('═', W));

            string pnlColor = _totalRealizedPnL >= 0 ? "+" : "";
            string pnlBar = MakeProgressBar(_totalRealizedPnL, DAILY_PROFIT_GOAL, 20, '█', '░');
            string lossBar = MakeProgressBar(-_totalRealizedPnL, -MAX_DAILY_LOSS, 20, '▓', '░');

            sb.AppendLine(
                $"  PnL Today : {pnlColor}{_totalRealizedPnL,8:C2}  " +
                $"Goal [{pnlBar}] {pnlGoalPct,5:F0}%   " +
                $"MaxLoss [{lossBar}] {lossUsedPct,5:F0}%   " +
                $"Cash: {cash:C0}   " +
                $"Trades: {_tradesToday}   " +
                $"W/L: {_winCount}/{_lossCount}  ({winRate:F0}%)");
            sb.AppendLine(new string('─', W));

            sb.AppendLine(
                $"  POSITIONS ({_positions.Count}/{MAX_POSITIONS})   " +
                $"{"DIR",-5}  {"SYMBOL",-6}  {"QTY",4}  {"ENTRY",8}  {"NOW",8}  " +
                $"{"P&L",9}  {"P&L%",6}  {"HOLD",6}  {"HWM",8}  VWAP   STOP  STRATEGY");
            sb.AppendLine(new string('─', W));

            lock (_lock)
            {
                if (_positions.Count == 0)
                {
                    sb.AppendLine("  — no open positions —".PadRight(W));
                }
                else
                {
                    foreach (var p in _positions.Values)
                    {
                        double mins = (DateTime.UtcNow - p.EntryTime).TotalMinutes;
                        decimal pnl = p.UnrealizedPnL(p.CurrentPrice);
                        decimal pnlPct = p.AvgPrice > 0
                            ? (p.CurrentPrice - p.AvgPrice) / p.AvgPrice * 100 : 0;
                        _vwap.TryGetValue(p.Symbol, out decimal v);
                        _marketData.TryGetValue(p.Symbol, out var pc);
                        decimal atr = SafeATR(pc, 14);
                        decimal hardSt = p.IsShort
                            ? p.AvgPrice + atr * 1.5m
                            : p.AvgPrice - atr * 1.5m;
                        string pnlSign = pnl >= 0 ? "+" : "";
                        string partial = p.PartialExitDone2 ? "**" : (p.PartialExitDone ? "* " : "  ");
                        string dir = p.IsShort ? "SHORT" : "LONG ";

                        sb.AppendLine(
                            $"  {partial}{dir}  {p.Symbol,-6}  {p.Quantity,4}  {p.AvgPrice,8:C}  " +
                            $"{p.CurrentPrice,8:C}  {pnlSign}{pnl,8:C}  {pnlPct,5:F1}%  " +
                            $"{mins,5:F0}m  {p.HighWaterMark,8:C}  {v,6:C}  {hardSt,7:C}  {p.StrategyTag}");
                    }
                }
            }
            sb.AppendLine(new string('─', W));

            sb.Append("  Equity  ");
            sb.AppendLine(MakeSparkline(_equityCurve, W - 12));
            sb.AppendLine(new string('─', W));

            sb.AppendLine(
                $"  {"SYM",-5}  {"PRICE",8}  {"VWAP",8}  {"SMA20",8}  {"SMA50",8}" +
                $"  {"RSI",5}  {"GAP%",6}  {"VOL K",7}  {"ORB HI",8}  {"ORB LO",8}  TREND  SIGNAL");
            sb.AppendLine(new string('─', W));

            foreach (var sym in _watchlist)
            {
                _marketData.TryGetValue(sym, out var candles);
                var last = candles?.LastOrDefault();
                decimal price = last?.Close ?? 0m;
                if (price == 0m) continue;

                decimal sma20 = SafeSMA(candles, 20);
                decimal sma50 = SafeSMA(candles, 50);
                double rsi = SafeRSI(candles, 14);
                _vwap.TryGetValue(sym, out decimal vwapVal);
                _prevDayClose.TryGetValue(sym, out decimal prevClose);
                decimal gapPct = prevClose > 0 ? (price - prevClose) / prevClose * 100 : 0;
                long volK = _dailyVolume.GetValueOrDefault(sym) / 1000;
                string trend = price > sma50 ? "▲ UP  " : "- NEUT";

                _orbRanges.TryGetValue(sym, out var orb);
                decimal orbHi = orb?.High ?? 0m;
                decimal orbLo = orb?.Low ?? 0m;

                string sig = "";
                if (orb != null && orb.IsSet)
                {
                    if (price > orbHi) sig = "ORB↑";
                    else if (price < orbLo) sig = "ORB↓";
                }
                if (sig == "" && rsi < RSI_OVERSOLD && price > sma50) sig = "MR↑";
                if (sig == "" && rsi > RSI_OVERBOUGHT && price < sma50) sig = "MR↓";
                if (sig == "" && vwapVal > 0)
                {
                    bool above = price > vwapVal;
                    _prevBarAboveVwap.TryGetValue(sym, out bool wasAbove);
                    if (!wasAbove && above) sig = "VWAP↑";
                    else if (wasAbove && !above) sig = "VWAP↓";
                }
                bool hot = vwapVal > 0 && price > vwapVal && rsi > 55;
                string prefix = hot ? "►" : " ";
                string gapStr = gapPct >= 0 ? $"+{gapPct:F1}%" : $"{gapPct:F1}%";

                sb.AppendLine(
                    $"  {prefix}{sym,-5}  {price,8:C}  {vwapVal,8:C}  {sma20,8:C}  {sma50,8:C}" +
                    $"  {rsi,5:F1}  {gapStr,6}  {volK,6}K  {orbHi,8:C}  {orbLo,8:C}  {trend}  {sig}");
            }

            sb.AppendLine(new string('─', W));

            sb.AppendLine(
                $"  Budget:{TOTAL_BUDGET:C0}  PosSz:{POSITION_SIZE:C0}  " +
                $"MaxPos:{MAX_POSITIONS}  Cooldown:{COOLDOWN_SECONDS / 60}min  " +
                $"MinHold:{MIN_HOLD_SECONDS / 60}min  Risk:{RISK_PCT * 100:F0}%/trade  " +
                $"MaxLoss:{MAX_LOSS_PER_TRADE:C0}  ATRTrail:{ATR_TRAIL_MULT}x  " +
                $"ORBWindow:{ORB_MINUTES}min  VolExp:{VOL_EXPAND_MULT}x  " +
                $"Strategies: ORB | GAP-GO | VWAP | Momentum | MeanRev(off)");
            sb.AppendLine(new string('─', W));

            sb.AppendLine("  RECENT TRADES");
            var recent = _tradeHistoryLog.TakeLast(8).ToList();
            if (recent.Count == 0)
                sb.AppendLine("  — no trades yet —".PadRight(W));
            else
                foreach (var log in recent)
                    sb.AppendLine("  " + log.PadRight(W - 2));

            sb.AppendLine(new string('─', W));

            sb.AppendLine("  EVENT LOG");
            lock (_logQueue)
            {
                foreach (var line in _logQueue)
                    sb.AppendLine("  " + (line.Length > W - 3
                        ? line.Substring(0, W - 3) : line).PadRight(W - 2));
                for (int i = _logQueue.Count; i < LOG_LINES; i++)
                    sb.AppendLine(new string(' ', W));
            }

            Console.SetCursorPosition(0, 0);
            Console.Write(sb.ToString());

            int used = sb.ToString().Count(c => c == '\n');
            for (int i = used; i < Console.WindowHeight - 1; i++)
                Console.WriteLine(new string(' ', W));
        }
        catch { }
    }

    private static string MakeProgressBar(decimal value, decimal max,
                                          int width, char fill, char empty)
    {
        if (max <= 0) return new string(empty, width);
        int filled = Math.Clamp((int)(value / max * width), 0, width);
        return new string(fill, filled) + new string(empty, width - filled);
    }

    private static string MakeSparkline(
        List<(DateTime time, decimal equity)> curve, int width)
    {
        if (curve.Count < 2) return new string('─', Math.Max(width, 0));

        char[] blocks = { '▁', '▂', '▃', '▄', '▅', '▆', '▇', '█' };
        int points = Math.Min(curve.Count, width);
        var slice = curve.TakeLast(points).ToList();
        decimal min = slice.Min(e => e.equity);
        decimal max = slice.Max(e => e.equity);
        decimal range = max - min;

        var sb = new StringBuilder();
        foreach (var e in slice)
        {
            int idx = range > 0
                ? (int)((e.equity - min) / range * (blocks.Length - 1))
                : 4;
            sb.Append(blocks[Math.Clamp(idx, 0, blocks.Length - 1)]);
        }
        return sb.ToString().PadRight(width);
    }

    // ══════════════════════════════════════════════════════════
    //  HELPERS
    // ══════════════════════════════════════════════════════════

    private DateTime GetEasternTime() =>
        TimeZoneInfo.ConvertTimeFromUtc(DateTime.UtcNow, Eastern);

    private DateTime GetPacificTime() =>
        TimeZoneInfo.ConvertTimeFromUtc(DateTime.UtcNow, Pacific);

    private void LogError(string context, string message) =>
        File.AppendAllText("errors.log",
            $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] [{context}] {message}\n");

    private int SafeMACDDirection(List<Candle> candles)
    {
        if (candles == null || candles.Count < 30) return 0;
        var closes = candles.Select(c => (double)c.Close).ToArray();

        double ema12Now = CalcEMA(closes, 12);
        double ema26Now = CalcEMA(closes, 26);
        double macdNow = ema12Now - ema26Now;

        var closesPrev = closes.Take(closes.Length - 5).ToArray();
        if (closesPrev.Length < 26) return 0;
        double macdPrev = CalcEMA(closesPrev, 12) - CalcEMA(closesPrev, 26);

        if (macdNow > 0 && macdNow > macdPrev) return 1;
        if (macdNow < 0 && macdNow < macdPrev) return -1;
        return 0;
    }

    private double CalcEMA(double[] data, int period)
    {
        if (data == null || data.Length < period) return data?.LastOrDefault() ?? 0;
        double k = 2.0 / (period + 1);
        double ema = data.Take(period).Average();
        for (int i = period; i < data.Length; i++)
            ema = data[i] * k + ema * (1 - k);
        return ema;
    }

    private void RefreshIndicatorCache(string symbol, List<Candle> candles)
    {
        if (candles == null || candles.Count < 30) return;
        var closes = candles.Select(c => (double)c.Close).ToArray();
        var closesPrev = closes.Length > 5 ? closes.Take(closes.Length - 5).ToArray() : closes;

        var candlesM1 = candles.Count > 1 ? candles.Take(candles.Count - 1).ToList() : candles;

        var ind = new SymIndicators
        {
            Rsi14 = SafeRSI(candles, 14),
            Rsi14Prev = SafeRSI(candlesM1, 14),
            Atr14 = SafeATR(candles, 14),
            Sma20 = SafeSMA(candles, 20),
            Sma50 = SafeSMA(candles, 50),
            Ema9 = CalcEMA(closes, 9),
            Ema21 = CalcEMA(closes, 21),
            Ema9Prev = closesPrev.Length >= 9 ? CalcEMA(closesPrev, 9) : 0,
            Ema21Prev = closesPrev.Length >= 21 ? CalcEMA(closesPrev, 21) : 0,
            MacdDir = SafeMACDDirection(candles),
            RecentHigh8 = SafeHighestHigh(candles, 8),
            RecentLow8 = SafeLowestLow(candles, 8),
            VolExpansion = CheckVolumeExpansion(candles),
        };
        _indicatorCache[symbol] = ind;
    }

    private void Refresh15MinEma(string symbol, List<Candle> candles)
    {
        if (candles == null || candles.Count < 2) return;
        var lastBar = candles.Last();
        var barTime = new DateTime(lastBar.Time.Year, lastBar.Time.Month, lastBar.Time.Day,
                                   lastBar.Time.Hour, (lastBar.Time.Minute / 15) * 15, 0);

        if (_ema20_15min.TryGetValue(symbol, out var cached) && cached.barTime == barTime)
            return;

        var bars15 = candles
            .GroupBy(c => new DateTime(c.Time.Year, c.Time.Month, c.Time.Day,
                                       c.Time.Hour, (c.Time.Minute / 15) * 15, 0))
            .OrderBy(g => g.Key)
            .Select(g => (double)g.Last().Close)
            .ToArray();

        if (bars15.Length >= 20)
            _ema20_15min[symbol] = ((decimal)CalcEMA(bars15, 20), barTime);
    }

    private decimal GetDailySma200(string symbol)
    {
        if (!_dailyCandles.TryGetValue(symbol, out var daily)) return 0m;
        lock (daily)
        {
            if (daily.Count < 200) return 0m;
            return daily.TakeLast(200).Average(c => c.Close);
        }
    }

    private (decimal High, decimal Low) GetPrevDayHL(string symbol)
    {
        _prevDayHighLevel.TryGetValue(symbol, out decimal h);
        _prevDayLowLevel.TryGetValue(symbol, out decimal l);
        return (h, l);
    }

    private decimal SafeSMA(List<Candle> candles, int period)
    {
        if (candles == null || candles.Count < period) return 0m;
        return candles.TakeLast(period).Average(c => c.Close);
    }

    public double SafeRSI(List<Candle> candles, int period)
    {
        if (candles == null || candles.Count < period * 2) return 50;

        int seedStart = candles.Count - period * 2;
        double avgGain = 0, avgLoss = 0;

        for (int i = seedStart + 1; i <= seedStart + period; i++)
        {
            double diff = (double)(candles[i].Close - candles[i - 1].Close);
            if (diff > 0) avgGain += diff;
            else avgLoss -= diff;
        }
        avgGain /= period;
        avgLoss /= period;

        for (int i = seedStart + period + 1; i < candles.Count; i++)
        {
            double diff = (double)(candles[i].Close - candles[i - 1].Close);
            double g = diff > 0 ? diff : 0;
            double l = diff < 0 ? -diff : 0;
            avgGain = (avgGain * (period - 1) + g) / period;
            avgLoss = (avgLoss * (period - 1) + l) / period;
        }

        if (avgGain == 0 && avgLoss == 0) return 50;
        if (avgLoss == 0) return 100;
        if (avgGain == 0) return 0;

        return 100 - 100 / (1 + avgGain / avgLoss);
    }

    private decimal SafeATR(List<Candle> candles, int period)
    {
        if (candles == null || candles.Count <= period)
            return candles?.LastOrDefault()?.Close * 0.002m ?? 0.01m;

        int start = candles.Count - period;
        decimal atr = 0m;
        for (int i = start; i < candles.Count; i++)
        {
            var c = candles[i];
            var prev = candles[i - 1];
            decimal tr = Math.Max(c.High - c.Low,
                         Math.Max(Math.Abs(c.High - prev.Close),
                                  Math.Abs(c.Low - prev.Close)));
            if (i == start) atr = tr;
            else atr = ((atr * (period - 1)) + tr) / period;
        }
        return atr;
    }

    private decimal SafeHighestHigh(List<Candle> candles, int lookback)
    {
        if (candles == null || candles.Count < lookback) return 0m;
        return candles.TakeLast(lookback).Max(c => c.High);
    }

    private decimal SafeLowestLow(List<Candle> candles, int lookback)
    {
        if (candles == null || candles.Count < lookback) return decimal.MaxValue;
        return candles.TakeLast(lookback).Min(c => c.Low);
    }

    private bool IsLiquiditySweep(List<Candle> candles)
    {
        if (candles.Count < 22) return false;

        var last = candles.Last();
        var prior = candles.TakeLast(21).Take(20).ToList();
        decimal prevHigh = prior.Max(c => c.High);
        decimal prevLow = prior.Min(c => c.Low);

        decimal upperWick = last.High - Math.Max(last.Close, last.Open);
        decimal lowerWick = Math.Min(last.Close, last.Open) - last.Low;
        decimal body = Math.Abs(last.Close - last.Open);
        if (body < 0.001m) body = 0.001m;

        bool sweepUp = last.High > prevHigh && last.Close < prevHigh && upperWick > body * 1.5m;
        bool sweepDown = last.Low < prevLow && last.Close > prevLow && lowerWick > body * 1.5m;

        return sweepUp || sweepDown;
    }

    private bool IsVolatilityCompressed(List<Candle> candles)
    {
        if (candles.Count < 60) return false;

        var recent = candles.TakeLast(20).ToList();
        var prior = candles.Skip(candles.Count - 60).Take(40).ToList();

        if (prior.Count < 20) return false;

        decimal avgRecentRange = recent.Average(c => c.High - c.Low);
        decimal avgPriorRange = prior.Average(c => c.High - c.Low);

        if (avgPriorRange <= 0) return false;
        return avgRecentRange < avgPriorRange * 0.60m;
    }

    private bool CheckVolumeExpansion(List<Candle> candles)
    {
        if (candles.Count < 10) return false;
        var last10 = candles.TakeLast(10).ToList();
        long prev5 = last10.Take(5).Sum(c => c.Volume);
        long recent5 = last10.Skip(5).Take(5).Sum(c => c.Volume);
        return recent5 > prev5 * VOL_EXPAND_MULT;
    }

    private bool CheckRelativeStrength(string symbol, List<Candle> candles)
    {
        if (!_marketData.TryGetValue("SPY", out var spy) || spy.Count < 20) return true;
        decimal symReturn = candles.Last().Close / candles[candles.Count - 20].Close;
        decimal spyReturn = spy.Last().Close / spy[spy.Count - 20].Close;
        return symReturn > spyReturn;
    }

    private bool CheckStrongRelativeStrength(string symbol, List<Candle> candles)
    {
        if (!_marketData.TryGetValue("SPY", out var spy) || spy.Count < 5) return false;
        var todayEt = GetEasternTime().Date;
        var todaySym = candles.Where(c => c.Time.Date == todayEt).ToList();
        var todaySpy = spy.Where(c => c.Time.Date == todayEt).ToList();
        if (todaySym.Count < 2 || todaySpy.Count < 2) return false;
        decimal symDayReturn = candles.Last().Close / todaySym.First().Open - 1m;
        decimal spyDayReturn = spy.Last().Close / todaySpy.First().Open - 1m;
        return symDayReturn >= 0.005m && spyDayReturn < 0m;
    }

    private int CalcQty(decimal price, decimal stopDistance)
    {
        decimal minStop = Math.Max(MIN_STOP_DISTANCE, price * 0.003m);
        if (stopDistance < minStop) stopDistance = minStop;

        decimal riskAmount = TOTAL_BUDGET * RISK_PCT;
        int qty = (int)(riskAmount / stopDistance);

        decimal deployedCapital = _positions.Values.Sum(p => p.AvgPrice * p.Quantity)
                                + _pendingEntryCount * POSITION_SIZE;
        decimal remainingCash = Math.Max(0, TOTAL_BUDGET - deployedCapital);
        decimal effectiveSlot = Math.Min(POSITION_SIZE, remainingCash);
        int maxByBudget = price > 0 ? (int)(effectiveSlot / price) : 0;

        qty = Math.Min(qty, maxByBudget);
        if (qty <= 0 || qty > MAX_QTY_SANITY) return 0;
        return qty;
    }

    public async Task SendEmail(string subject, string body)
    {
        try
        {
            var smtp = new SmtpClient("smtp.gmail.com")
            {
                Port = 587,
                EnableSsl = true,
                Credentials = new NetworkCredential(EmailFrom, EmailPassword)
            };
            using var msg = new MailMessage(EmailFrom, EmailTo)
            { Subject = subject, Body = body };
            await smtp.SendMailAsync(msg);
        }
        catch (Exception ex) { LogError("SendEmail", ex.Message); }
    }
}