using IBApi;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
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
    void RequestHourlyHistoricalData(string symbol, int timeframeMinutes);
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
    public string EntryRegime { get; set; } = "";
    public decimal EntryCommission { get; set; } = 0m;
    public decimal InitialRiskPerShare { get; set; } = 0m;
    public double EntryRsi { get; set; } = 0d;
    public decimal EntryAtr { get; set; } = 0m;
    public decimal EntryVwap { get; set; } = 0m;
    public int EntrySetupScore { get; set; } = 0;
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
    public string HaltReason { get; set; } = "";
    public int ConsecutiveLosses { get; set; }
    public Dictionary<string, DateTime> LastTradeTime { get; set; } = new();
    public Dictionary<string, bool> LastTradeWasLoss { get; set; } = new();
    public Dictionary<string, int> DailyEntryCount { get; set; } = new();
    public int TradesThisHour { get; set; }
    public DateTime TradeHourSlot { get; set; } = DateTime.MinValue;
    public List<DateTime> RecentEntryTimesUtc { get; set; } = new();
    public Dictionary<string, int> StrategyTradeCount { get; set; } = new();
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
    public string EntryTime { get; set; } = "";
    public string ExitTime { get; set; } = "";
    public string Date { get; set; } = "";
    public string Regime { get; set; } = "";
    public double EntryRsi { get; set; }
    public decimal EntryAtr { get; set; }
    public decimal EntryVwap { get; set; }
    public int EntrySetupScore { get; set; }
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

public sealed class PatternSignal
{
    public string Tag { get; set; } = "";
    public bool Bullish { get; set; }
    public int Score { get; set; }
    public string Reason { get; set; } = "";
}

// ══════════════════════════════════════════════════════════
//  SIMULATED BROKER
// ══════════════════════════════════════════════════════════

public partial class SimulatedBroker
{

    public IBroker RealBroker { get; set; }
    private readonly object _lock = new object();

    // ── TRADING RULES ──────────────────────────────────────
    private decimal TOTAL_BUDGET = 4000m;
    private int MAX_POSITIONS = 4;
    private decimal POSITION_SIZE = 750m;
    private int MIN_HOLD_SECONDS = 90;
    private decimal DAILY_PROFIT_GOAL = 120m;



    private decimal MAX_DAILY_LOSS = -40m;
    private int COOLDOWN_SECONDS = 600;          // looser recycle; still prevents repeated same-symbol revenge trades
    private decimal ATR_TRAIL_MULT = 1.25m;
    private decimal SHORT_ATR_TRAIL = 1.20m;
    private decimal HARD_STOP_ATR_MULT = 1.35m;
    private decimal MAX_LOSS_PER_TRADE = 35m;
    private decimal COMMISSION_PER_SIDE = 1m;
    private decimal MIN_STOP_DISTANCE = 0.10m;
    private int MAX_QTY_SANITY = 500;
    private decimal RISK_PCT = 0.004m;
    private int ORB_MINUTES = 10;              // looser ORB window; still avoids first-minute noise
    private decimal VOL_EXPAND_MULT = 1.30m;
    private double RSI_LONG_MIN = 58.0;
    private double RSI_SHORT_MAX = 42.0;
    private double RSI_OVERSOLD = 35.0;
    private double RSI_OVERBOUGHT = 65.0;
    private decimal GAP_GO_MIN_PCT = 0.012m;    // looser gap threshold; still avoids tiny overnight noise
    private decimal GAP_GO_REL_VOL = 1.30m;
    private int VWAP_CONFIRM_BARS = 2;
    private int MAX_TRADES_PER_DAY = 4;
    private decimal MIN_ATR_PCT = 0.0018m;
    private decimal MAX_ATR_PCT = 0.020m;
    private decimal MIN_RR_RATIO = 1.10m;       // looser RR floor; scalp economics filter still applies

    // Liquidity is measured in dollars and paced through the session. The old
    // hard 300,000-share requirement treated a $10 stock and a $500 stock the
    // same and effectively blocked almost every early-session entry.
    private const decimal MIN_SESSION_DOLLAR_VOLUME_TARGET = 20_000_000m;
    private const decimal MIN_SESSION_DOLLAR_VOLUME_FLOOR = 500_000m;

    private decimal VIX_REDUCE_THRESHOLD = 25m;
    private decimal VIX_NO_LONG_THRESHOLD = 35m;

    // ── RISK MGMT: Unrealized Drawdown Circuit Breaker ──
    // Halts NEW entries (not exits) when realized + unrealized PnL breaches threshold.
    // Prevents the gap scenario where 3 positions go against you simultaneously
    // and you keep opening more before stops fire.
    private decimal UNREALIZED_DD_HALT_THRESHOLD = -50m;

    // ── RISK MGMT: Dynamic Position Sizing ──
    // Scales risk down when losing, keeps normal when winning.
    // At MAX_DAILY_LOSS/2 drawdown, risk is halved. No increase above baseline.
    private bool DYNAMIC_SIZING_ENABLED = true;

    // ── RISK MGMT: Strategy Allocation Limits ──
    // Max trades per strategy family per day. Prevents one misfiring strategy
    // from consuming the entire daily trade budget.
    private int MAX_TRADES_PER_STRATEGY = 2;
    private int MAX_TRADES_PER_SYMBOL_PER_DAY = 1;

    // ── RISK MGMT: Trend Reversal Gate ──
    // Rejects entries when short-term trend structure is breaking down (for longs)
    // or recovering (for shorts). Protects early-entry logic from catching falling knives.
    private bool TREND_REVERSAL_GATE_ENABLED = true;

    // ── Backtest projections (off by default — misleading with limited data) ──
    private bool SHOW_PROJECTIONS = false;

    // Used by swing mode only. Intraday strategies own their confirmations so
    // the router remains OR across complete, independently valid signals.
    private int MIN_SETUP_SCORE = 50;



    //change back uygar
    //// CHANGE 3: MAX_DAILY_LOSS -80 → -100 — avoids premature halt after 2 normal stops
    //private decimal MAX_DAILY_LOSS = -100m;
    //private int COOLDOWN_SECONDS = 1800;
    //private decimal ATR_TRAIL_MULT = 2.0m;
    //private decimal SHORT_ATR_TRAIL = 1.8m;
    //private decimal HARD_STOP_ATR_MULT = 2.0m;
    //private decimal MAX_LOSS_PER_TRADE = 40m;
    //private decimal COMMISSION_PER_SIDE = 1m;
    //private decimal MIN_STOP_DISTANCE = 0.10m;
    //private int MAX_QTY_SANITY = 500;
    //private decimal RISK_PCT = 0.005m;
    //private int ORB_MINUTES = 30;
    //private decimal VOL_EXPAND_MULT = 1.8m;
    //// CHANGE 2: RSI_LONG_MIN 65.0 → 62.0 — recovers valid momentum entries filtered too aggressively 
    ////uygar change this to 64 if too much loosing
    //private double RSI_LONG_MIN = 62.0;
    //private double RSI_SHORT_MAX = 35.0;
    //private double RSI_OVERSOLD = 35.0;
    //private double RSI_OVERBOUGHT = 65.0;
    //private decimal GAP_GO_MIN_PCT = 0.020m;
    //private decimal GAP_GO_REL_VOL = 1.30m;
    //private int VWAP_CONFIRM_BARS = 2;
    //private int MAX_TRADES_PER_DAY = 6;
    //private decimal MIN_ATR_PCT = 0.003m;
    //private decimal MAX_ATR_PCT = 0.020m;
    //private decimal MIN_RR_RATIO = 1.5m;

    //private decimal VIX_REDUCE_THRESHOLD = 25m;
    //private decimal VIX_NO_LONG_THRESHOLD = 35m;

    //// CHANGE 1: MIN_SETUP_SCORE 40 → 45 — filters the weakest 15-20% of setups
    //private int MIN_SETUP_SCORE = 45;

    private int MAX_CONSECUTIVE_LOSSES = 2;

    // ── Entry quality hardening ─────────────────────────────
    private int MIN_ENTRY_MINUTES_AFTER_OPEN = 15;
    private const int MIN_ENTRY_QTY = 5;
    // V2 FIX: Raised from 4.0 to 5.0 — with $2 round-trip commission, gross target must be
    // at least $10 to make the trade worthwhile after slippage and market impact.
    private static readonly decimal MIN_GROSS_TARGET_TO_COMMISSION_MULT = 3.0m;
    private bool ALLOW_BULLISH_CANDLE_PATTERNS = true;
    private bool ALLOW_SCALP_BREAKOUT_LONGS = true;
    private bool ALLOW_SCALP_BREAKOUT_SHORTS = true;
    private bool ALLOW_SCALP_ORB_LONGS = true;

    // ── Swing-mode conversion (Turkish notes) ───────────────
    // When enabled, the bot behaves like a higher-timeframe breakout engine
    // instead of a same-day scalp engine.
    private bool SWING_MODE_ENABLED = false;
    private bool EOD_LIQUIDATE_ENABLED = true;
    private int SWING_BASE_LOOKBACK_DAYS = 20;
    private int SWING_MAX_HOLD_DAYS = 10;
    private decimal SWING_BREAKOUT_BUFFER_PCT = 0.0025m;
    private decimal SWING_BASE_TIGHTNESS_MAX = 0.18m;
    private decimal SWING_TARGET_R_MULT = 3.20m;
    private bool SWING_REQUIRE_CONTRACTION = true;

    private bool STRATEGY_ORB_ENABLED = true;
    private bool STRATEGY_GAP_GO_ENABLED = true;
    private bool STRATEGY_VWAP_ENABLED = true;
    private bool STRATEGY_MEAN_REV_ENABLED = true;
    private bool STRATEGY_BB_MR_ENABLED = true;
    private bool STRATEGY_MOMENTUM_ENABLED = true;
    private bool STRATEGY_EMA_POCKET_ENABLED = true;
    private bool STRATEGY_OUTSIDE_CANDLE_ENABLED = true;
    private bool STRATEGY_CANDLE_PATTERNS_ENABLED = true;
    private bool STRATEGY_MICRO_PULLBACK_ENABLED = true;
    private bool STRATEGY_NADARAYA_WATSON_ENABLED = true;
    // Keep these fallbacks aligned with bot-config.json. They matter whenever the
    // runtime config file is missing from bin/Release or bin/Debug.
    private int NW_TIMEFRAME_MINUTES = 30;  // NW bar size: 15, 30, or 60 minutes
    private int NW_LOOKBACK = 250;          // completed regular-session bars at NW_TIMEFRAME_MINUTES
    private decimal NW_BANDWIDTH = 6m;      // Gaussian kernel bandwidth — larger = smoother centerline
    private decimal NW_MULT = 2.5m;         // band width = kernel MAE * this multiplier
    private decimal NW_STOP_LOSS_PCT = 0.03m;  // flat % stop-loss for NW_BAND_ trades (not ATR-based)
    private bool EARLY_PATTERN_ENTRY_ENABLED = true;
    private int PATTERN_MIN_SCORE = 68;
    private int INTRABAR_SIGNAL_COOLDOWN_SECONDS = 30;
    private decimal FAST_VOL_MULT = 1.30m;
    private int DATA_LINES_PER_SYMBOL = 1;
    private int MAX_MARKET_DATA_LINES = 120;

    private bool _allowShorts = true;

    // Entry inversion switch:
    // When true, the signal/gate logic remains unchanged, but the actual entry
    // sent to IBKR is reversed: LONG signal -> SHORT entry; SHORT signal -> LONG entry.
    // Exit orders are NOT inverted; they must follow the actual open position.
    // NOTE: IbClient.ReverseSignals already handles inversion at the broker client level.
    // Disable this SimulatedBroker-level inversion to avoid double-inversion bugs.
    private bool INVERT_ENTRY_DIRECTION = false;

    // ── STATE ──────────────────────────────────────────────
    public readonly ConcurrentDictionary<string, List<Candle>> _marketData = new();
    private Dictionary<string, SimPosition> _positions = new();
    private readonly ConcurrentDictionary<int, TrackedOrder> _ordersById = new();
    private readonly List<string> _tradeHistoryLog = new();
    private readonly List<TradeRecord> _completedTrades = new();
    private readonly Dictionary<string, DateTime> _lastTradeTime = new();
    private readonly Dictionary<string, long> _dailyVolume = new();
    // Populated from IBKR's own cumulative daily volume ticks (see IbClient.tickSize,
    // field 8). Self-healing across restarts/reconnects, unlike _dailyVolume above,
    // which is summed locally from individual trade ticks and resets to zero on every
    // process restart — see GetTodayVolume() below, which prefers this when available.
    private readonly ConcurrentDictionary<string, long> _dailyVolumeAuthoritative = new();
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
    // Dedicated regular-session timeframe bars for the Nadaraya-Watson envelope.
    // Loaded directly from IBKR at startup and updated from finalized live 1-min bars.
    private readonly ConcurrentDictionary<string, List<Candle>> _hourlyCandles = new();

    // NW bands are based only on completed timeframe bars, so they change at most
    // once per configured interval. Cache the expensive kernel/MAE calculation
    // instead of recomputing it on every LAST tick. The parameter
    // signature is part of the cache key so dashboard config changes invalidate
    // the cached value automatically.
    private readonly ConcurrentDictionary<string,
        (decimal mid, decimal upper, decimal lower, int bars, DateTime lastBarTime,
         int lookback, decimal bandwidth, decimal mult)> _nwEnvelopeCache = new();
    private readonly ConcurrentDictionary<string, string> _lastNwDecisionBySymbol =
        new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, string> _lastNwTouchDecisionBySymbol =
        new(StringComparer.OrdinalIgnoreCase);

    private readonly ConcurrentDictionary<string, decimal> _prevDayHighLevel = new();
    private readonly ConcurrentDictionary<string, decimal> _prevDayLowLevel = new();

    private sealed class SymIndicators
    {
        public double Rsi14;
        public double Rsi14Prev;
        public decimal Atr14;
        public decimal Sma20;
        public decimal Sma50;
        public decimal Sma100;
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

    // Rebuild status (exposed to dashboard/status API)
    private volatile bool _rebuildInProgress = false;
    private DateTime _lastRebuildUtc = DateTime.MinValue;
    private string _rebuildMessage = "";

    private readonly ConcurrentDictionary<string, decimal> _latestBid = new();
    private readonly ConcurrentDictionary<string, decimal> _latestAsk = new();
    private readonly ConcurrentDictionary<string, DateTime> _latestBidAskUpdateUtc = new();
    private readonly ConcurrentDictionary<string, DateTime> _latestTradeUpdateUtc = new();

    private bool MIDDAY_FILTER_ENABLED = true;

    private readonly ConcurrentDictionary<string, (decimal SumPV, long SumVol)> _vwapAccum = new();
    private readonly ConcurrentDictionary<string, decimal> _vwap = new();
    private readonly ConcurrentDictionary<string, decimal> _prevDayClose = new();
    private readonly ConcurrentDictionary<string, bool> _prevBarAboveVwap = new();
    // Daily SMA cache computed from Yahoo or live-derived daily bars (sma20,sma50,sma100,sma200)
    private readonly ConcurrentDictionary<string, (decimal sma20, decimal sma50, decimal sma100, decimal sma200)> _dailySmaCache = new();

    private ConcurrentDictionary<string, string> _pendingStrategyTag = new();
    private ConcurrentDictionary<string, decimal> _pendingInitialRisk = new();
    private readonly ConcurrentDictionary<string, string> _pendingExitReasonBySymbol = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<int, string> _bracketExitReasonByOrderId = new();

    private readonly HashSet<string> _earningsBlacklist = new(StringComparer.OrdinalIgnoreCase);

    // ── RISK MGMT: Per-strategy daily trade counters ──
    // Key = strategy family (e.g. "ORB", "PATTERN", "VWAP"), Value = count today
    private readonly ConcurrentDictionary<string, int> _strategyTradeCount = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, (int stopId, int targetId)> _pendingBracketChildren = new();
    private readonly ConcurrentDictionary<string, bool> _pendingEntrySymbols = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, DateTime> _pendingEntryCreatedUtc = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, DateTime> _lastIntrabarSignalUtc = new(StringComparer.OrdinalIgnoreCase);

    private const int PENDING_ENTRY_TIMEOUT_SECONDS = 120;
    private int _pendingEntryCount = 0;

    private readonly Dictionary<string, bool> _lastTradeWasLoss = new();
    private readonly Dictionary<string, int> _dailyEntryCount = new();

    // Telemetry: why entries are blocked
    private readonly ConcurrentDictionary<string, int> _blockedReasonCounts = new();
    private readonly ConcurrentDictionary<string, string> _lastBlockedReasonBySymbol = new(StringComparer.OrdinalIgnoreCase);
    private void RecordBlock(string symbol, string reason, string? detail = null)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(reason)) return;
            _blockedReasonCounts.AddOrUpdate(reason, 1, (k, v) => v + 1);
            if (!string.IsNullOrWhiteSpace(symbol))
                _lastBlockedReasonBySymbol[symbol] = string.IsNullOrWhiteSpace(detail) ? reason : detail;
        }
        catch { }
    }

    // New config flags
    private bool REQUIRE_SMA_ALIGNMENT = false; // require 50>100>200 for trend-following entries
    private int SHORT_TAPE_STRICTNESS = 1; // 0=loose,1=normal,2=strict
    private bool ALLOW_PARTIAL_ON_WEAK_SIGNAL = true;

    private decimal _totalRealizedPnL = 0m;
    private int _tradesToday = 0;
    private int _tradesThisHour = 0;
    private int MAX_TRADES_PER_HOUR = 2;
    private DateTime _currentTradeHour = DateTime.MinValue;
    private readonly Queue<DateTime> _recentEntryTimesUtc = new();
    private bool _haltTrading = false;
    private string _haltReason = "";
    private DateTime _lastHaltGateLogUtc = DateTime.MinValue;
    private DateTime _lastReconGateLogUtc = DateTime.MinValue;
    private bool _manualResumeOverride = false;
    private bool _eodSent = false;
    private DateTime _lastVolumeResetEt = DateTime.MinValue;
    private DateTime _lastMemorySave = DateTime.MinValue;
    private DateTime _lastStateSave = DateTime.MinValue;

    // ── Status API caching — avoids re-serializing 500 trades on every poll ──
    private string _cachedAllTradesJson = "[]";
    private int _cachedAllTradesCount = -1;
    private string _cachedLifetimeJson = "[]";
    private int _cachedLifetimeCount = -1;

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
        Environment.GetEnvironmentVariable("BOT_EMAIL_PASS")?.Trim() ?? "sznd kafk nhec skqh";

    private static readonly Dictionary<string, string[]> DEFAULT_WATCHLIST_GROUPS =
        new(StringComparer.OrdinalIgnoreCase)
        {
            ["Market ETFs"] = new[]
            {
            "SPY", "QQQ", "IWM", "DIA",
            "SMH", "XLF", "XLE", "XLV",
            "XLI", "XBI"
            },

            ["Mega Cap Tech"] = new[]
            {
            "NVDA", "MSFT", "AAPL", "AMZN",
            "GOOGL", "META", "TSLA",
            "AMD", "AVGO", "NFLX"
            },

            ["AI & Semiconductors"] = new[]
            {
            "ARM", "MU", "QCOM", "LRCX",
            "AMAT", "KLAC", "ASML",
            "MRVL", "ON", "INTC"
            },

            ["Software & Cloud"] = new[]
            {
            "PLTR", "CRM", "NOW", "SNOW",
            "DDOG", "MDB", "CRWD",
            "PANW", "ZS", "NET"
            },

            ["Growth & Momentum"] = new[]
            {
            "RKLB", "NBIS", "HOOD",
            "HIMS", "SOFI", "RDDT",
            "APP", "CRWV"
            },

            ["Healthcare & Biotech"] = new[]
            {
            "LLY", "NVO", "ISRG",
            "VRTX", "XBI"
            },

            ["Industrials & Energy"] = new[]
            {
            "GE", "CAT", "DE",
            "VRT", "ETN", "CEG"
            },

            ["Financials"] = new[]
            {
            "JPM", "GS", "BAC",
            "MS", "BLK", "V"
            }
        };

    private static readonly string[] DEFAULT_WATCHLIST = DEFAULT_WATCHLIST_GROUPS
        .SelectMany(kvp => kvp.Value)
        .Distinct(StringComparer.OrdinalIgnoreCase)
        .ToArray();

    public string[] _watchlist = DEFAULT_WATCHLIST.ToArray();

    private readonly HashSet<string> _subscribedSymbols = new(StringComparer.OrdinalIgnoreCase);
    private int _previousOrbMinutes;

    private static readonly Dictionary<string, string> _symbolSectors
        = new(StringComparer.OrdinalIgnoreCase)
    {
        {"SPY","sp500_etf"},{"QQQ","nasdaq_etf"},{"IWM","small_cap_etf"},
        {"SMH","semi_etf"},{"XLK","tech_etf"},{"XLF","fin_etf"},
        {"XLE","energy_etf"},{"XBI","biotech_etf"},{"DIA","dow_etf"},
        {"XLI","industrial_etf"},{"XLV","healthcare_etf"},{"XLY","consumer_disc_etf"},{"XLC","comm_services_etf"},
        {"AAPL","megacap_tech"},{"MSFT","megacap_tech"},{"NVDA","megacap_tech"},
        {"META","megacap_tech"},{"AMZN","megacap_tech"},{"GOOGL","megacap_tech"},
        {"TSLA","auto_ev"},{"ADBE","design_sw"},
        {"AMD","semiconductor"},{"AVGO","semiconductor"},{"ARM","semiconductor"},
        {"MU","semiconductor"},{"AMAT","semiconductor"},{"LRCX","semiconductor"},
        {"QCOM","semiconductor"},{"TSM","semiconductor"},{"TXN","semiconductor"},
        {"ASML","semiconductor"},{"SMCI","semiconductor"},{"KLAC","semiconductor"},
        {"MRVL","semiconductor"},{"ON","semiconductor"},{"INTC","semiconductor"},
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
        {"RKLB","space"},{"NBIS","ai_cloud"},{"CRWV","ai_cloud"},
        {"HIMS","digital_health"},{"RDDT","social_media"},
        {"XOM","energy"},{"CVX","energy"},{"OXY","energy"},
        {"UNH","healthcare"},{"ABBV","pharma"},{"LLY","pharma"},{"NVO","pharma"},
        {"ISRG","medical_devices"},
        {"VRTX","biotech"},{"REGN","biotech"},{"GILD","biotech"},
        {"CAT","industrial"},{"DE","industrial"},{"GE","industrial"},{"HON","industrial"},
        {"RTX","defense"},{"LMT","defense"},{"BA","defense"},
        {"ORCL","enterprise_sw"},{"IBM","enterprise_sw"},{"BLK","asset_management"},
        {"VRT","data_center_infrastructure"},{"ETN","electrical_infrastructure"},
        {"CEG","energy"},
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
            _latestTradeUpdateUtc[symbol] = DateTime.UtcNow;

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

            if (_haltTrading)
            {
                // Halt blocks NEW entries — but we must still manage open positions.
                // Without this, positions go unmonitored: hard stops don't fire,
                // trailing stops don't update, regime-aware exits never trigger.
                // Only bracket orders at IBKR survive, and those may already be
                // cancelled (post-partial, post-manual-sell).
                OnTradeTick(symbol, size);
                lock (_lock)
                {
                    if (_positions.TryGetValue(symbol, out var haltPos))
                        haltPos.CurrentPrice = price;
                }
                CheckHardStop(symbol, price);
                CheckBreakevenStop(symbol, price);
                CheckExits(symbol, price);
                return;
            }

            OnTradeTick(symbol, size);

            lock (_lock)
            {
                if (_positions.TryGetValue(symbol, out var pos))
                    pos.CurrentPrice = price;
            }

            CheckHardStop(symbol, price);
            // BUGFIX: CheckBreakevenStop was previously only called inside the _haltTrading
            // branch above, so the breakeven-stop-arming logic (move stop to entry at 1R)
            // never ran during normal trading — only after the bot had already halted for
            // the day. That meant winning trades could give back their entire gain because
            // the stop was never moved up while the bot was actively trading.
            CheckBreakevenStop(symbol, price);
            CheckExits(symbol, price);

            // NW is a price-touch strategy, so evaluate it on the live LAST tick,
            // not only when a 1-minute candle closes. OpenPosition() still enforces
            // every portfolio/risk/cooldown gate before an order can be submitted.
            if (STRATEGY_NADARAYA_WATSON_ENABLED)
                TryNadarayaWatsonStrategy(symbol, _marketData.GetValueOrDefault(symbol), price);

            TryEarlyPatternEntry(symbol, current);

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
        if (bid > 0 || ask > 0) _latestBidAskUpdateUtc[symbol] = DateTime.UtcNow;
    }

    private (decimal price, string source, double ageSec, decimal bid, decimal ask, decimal last) GetDisplayQuote(string symbol, decimal fallbackPrice = 0m)
    {
        _latestTick.TryGetValue(symbol, out decimal last);
        _latestBid.TryGetValue(symbol, out decimal bid);
        _latestAsk.TryGetValue(symbol, out decimal ask);

        _latestTradeUpdateUtc.TryGetValue(symbol, out var lastTradeUtc);
        _latestBidAskUpdateUtc.TryGetValue(symbol, out var lastQuoteUtc);

        bool hasLast = last > 0;
        bool hasBidAsk = bid > 0 && ask > 0 && ask >= bid;
        double tradeAgeSec = hasLast && lastTradeUtc != default ? Math.Max(0, (DateTime.UtcNow - lastTradeUtc).TotalSeconds) : double.PositiveInfinity;
        double quoteAgeSec = hasBidAsk && lastQuoteUtc != default ? Math.Max(0, (DateTime.UtcNow - lastQuoteUtc).TotalSeconds) : double.PositiveInfinity;

        if (hasLast && tradeAgeSec <= 3)
            return (last, "last", tradeAgeSec, bid, ask, last);

        if (hasBidAsk)
            return ((bid + ask) / 2m, "mid", quoteAgeSec, bid, ask, last);

        if (hasLast)
            return (last, "last-stale", tradeAgeSec, bid, ask, last);

        return (fallbackPrice, fallbackPrice > 0 ? "bar" : "none", double.PositiveInfinity, bid, ask, last);
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

    // Rebuild today's opening range from completed historical 1-minute bars.
    // This is essential after a restart later than the ORB window: the old code
    // only populated _orbRanges from live ticks received between 09:30 and
    // 09:30+ORB_MINUTES, so a midday/after-close restart showed blank ORB forever.
    private void SeedOpeningRangeFromCandles(string symbol)
    {
        if (!_marketData.TryGetValue(symbol, out var candles) || candles == null) return;

        DateTime nowEt = GetEasternTime();
        DateTime open = new DateTime(nowEt.Year, nowEt.Month, nowEt.Day, 9, 30, 0);
        DateTime end = open.AddMinutes(ORB_MINUTES);

        List<Candle> openingBars;
        lock (candles)
        {
            openingBars = candles
                .Where(c => c.Time >= open && c.Time < end)
                .OrderBy(c => c.Time)
                .ToList();
        }

        if (openingBars.Count == 0) return;

        decimal high = openingBars.Max(c => c.High);
        decimal low = openingBars.Min(c => c.Low);
        if (high <= 0 || low <= 0 || high < low) return;

        _orbRanges[symbol] = new OpeningRange
        {
            High = high,
            Low = low,
            // If the requested opening interval is over, historical bars are
            // authoritative even if one sparse symbol missed a minute print.
            IsSet = nowEt >= end
        };

        LogMessage($"[ORB SEED] {symbol} {openingBars.Count} bars -> {low:F2}-{high:F2} ({ORB_MINUTES}m)");
    }

    // Called by IbClient exactly when a symbol's 1-minute historical request ends.
    // Seeding here avoids a race where RequestAllHistoricalSlow() tried to rebuild
    // ORB/VWAP before the asynchronous historical response had actually finished.
    public void OnMinuteHistoryLoaded(string symbol)
    {
        try
        {
            if (_marketData.TryGetValue(symbol, out var candles) && candles != null)
            {
                if (candles.Count >= 20)
                {
                    RefreshIndicatorCache(symbol, candles);
                    Refresh15MinEma(symbol, candles);
                }
            }

            SeedVwapFromCandles(symbol);
            SeedOpeningRangeFromCandles(symbol);
        }
        catch (Exception ex)
        {
            LogError("OnMinuteHistoryLoaded " + symbol, ex.Message);
        }
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

        // NOTE: previously set _prevDayClose[symbol] = candle.Close here when the
        // 9:29 AM bar finalized. That used the 9:29 AM pre-market tick as "previous
        // close", which is NOT yesterday's close — it's just the last price ticking
        // in right before open. It also silently raced with AddDailyCandle()'s daily
        // rollover (the correct source, now fixed below), so depending on timing
        // either value could win, producing wrong Gap %/price-change readings.
        // AddDailyCandle() is now the single source of truth for _prevDayClose.

        // Keep the dedicated NW timeframe series current from finalized 1-minute bars.
        // Only regular-session minutes are accepted by UpdateHourlyFromMinute().
        UpdateHourlyFromMinute(symbol, candle);

        _vwap.TryGetValue(symbol, out decimal vwapNow);
        bool aboveVwap = vwapNow > 0 && candle.Close > vwapNow;
        _prevBarAboveVwap.TryGetValue(symbol, out bool wasAbove);
        _prevBarAboveVwap[symbol] = aboveVwap;

        if (_marketData.TryGetValue(symbol, out var cacheCandles))
        {
            RefreshIndicatorCache(symbol, cacheCandles);
            Refresh15MinEma(symbol, cacheCandles);
        }

        // Refresh market regime before evaluating this closed bar. The old order
        // made every entry decision use the previous bar's SPY regime.
        UpdateMarketRegime();
        ExecuteStrategy(symbol, wasAbove, aboveVwap);
    }

    public void OnTradeTick(string symbol, long size)
    {
        lock (_lock)
        {
            if (!_dailyVolume.ContainsKey(symbol)) _dailyVolume[symbol] = 0;
            _dailyVolume[symbol] += size;
        }
    }

    // Called from IbClient on every field-8 (cumulative VOLUME) tick. This is
    // IBKR's own running daily total, resent on every update — so it self-
    // corrects on reconnect/restart instead of resetting to zero the way
    // _dailyVolume (summed locally from individual trade prints) does.
    public void OnAuthoritativeDailyVolume(string symbol, long cumulativeVolume)
    {
        if (cumulativeVolume > 0)
            _dailyVolumeAuthoritative[symbol] = cumulativeVolume;
    }

    // Single source of truth for "shares traded today" — prefers IBKR's own
    // cumulative total when available, falls back to the locally-summed
    // counter only if IBKR hasn't sent a field-8 tick yet (e.g. right after
    // subscribing, before the first volume update arrives).
    private long GetTodayVolume(string symbol)
    {
        if (_dailyVolumeAuthoritative.TryGetValue(symbol, out long authVol) && authVol > 0)
            return authVol;
        return _dailyVolume.GetValueOrDefault(symbol);
    }

    private bool HasSufficientSessionLiquidity(string symbol, List<Candle> candles,
                                                DateTime nowEt,
                                                out decimal actualDollarVolume,
                                                out decimal requiredDollarVolume)
    {
        actualDollarVolume = 0m;
        requiredDollarVolume = MIN_SESSION_DOLLAR_VOLUME_FLOOR;
        if (candles == null || candles.Count == 0) return false;

        decimal price = candles.Last().Close;
        if (price <= 0m) return false;

        // Prefer IBKR's cumulative session volume, but keep the completed-bar
        // total as a fallback after reconnects or before the first volume tick.
        long completedBarVolume = candles
            .Where(c => c.Time.Date == nowEt.Date)
            .Sum(c => Math.Max(0L, c.Volume));
        long shares = Math.Max(GetTodayVolume(symbol), completedBarVolume);
        actualDollarVolume = price * shares;

        int minutesSinceOpen = Math.Clamp(
            (nowEt.Hour - 9) * 60 + nowEt.Minute - 30,
            1,
            390);
        requiredDollarVolume = Math.Max(
            MIN_SESSION_DOLLAR_VOLUME_FLOOR,
            MIN_SESSION_DOLLAR_VOLUME_TARGET * minutesSinceOpen / 390m);

        return actualDollarVolume >= requiredDollarVolume;
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
        if (todayCandles.Count == 0) return;

        // Update fast SPY direction before classifying the regime so this cycle's
        // classification and entry gates use the same tape state.
        if (spy.Count >= 25)
        {
            var closes = spy.Select(c => (double)c.Close).ToArray();
            double ema20Now = CalcEMA(closes, 20);

            var closesPrev5 = closes.Length > 5 ? closes.Take(closes.Length - 5).ToArray() : closes;
            double ema20Prev = closesPrev5.Length >= 20 ? CalcEMA(closesPrev5, 20) : ema20Now;

            _spyBullish = (double)spyLast > ema20Now && ema20Now > ema20Prev;
            _spyBearish = (double)spyLast < ema20Now && ema20Now < ema20Prev;
        }

        decimal open = todayCandles.First().Open;
        decimal dayMove = open > 0 ? (spyLast - open) / open : 0m;
        decimal smaSpread = spyLast > 0 ? Math.Abs(spySma20 - spySma50) / spyLast : 0m;

        // The old code compared today's full session range with a 1-minute ATR,
        // mixing incompatible units and labelling ordinary movement TRENDING.
        // Use SPY's move from today's open plus aligned intraday structure instead.
        bool downsideTrend = spyLast < spySma20 && spySma20 <= spySma50 && _spyBearish;
        bool upsideTrend = spyLast > spySma20 && spySma20 >= spySma50 && _spyBullish;

        if ((dayMove <= -0.003m && downsideTrend) || dayMove <= -0.006m)
            _marketRegime = "SELL-OFF";
        else if (dayMove >= 0.003m && upsideTrend)
            _marketRegime = "TRENDING";
        else if (Math.Abs(dayMove) < 0.0015m && smaSpread < 0.0008m)
            _marketRegime = "CHOPPY";
        else
            _marketRegime = "NORMAL";

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

    }

    private int GetMinEntryQtyForPrice(decimal price)
    {
        if (price >= 250m) return 1;
        if (price >= 100m) return 2;
        if (price >= 30m) return 3;
        return MIN_ENTRY_QTY;
    }

    // Call only while holding _lock. A calendar-hour counter allowed a burst at
    // 09:59 followed by another burst at 10:00; the queue enforces a true rolling
    // 60-minute limit and survives restarts through BotPersistData.
    private int GetRollingHourEntryCountLocked(DateTime nowUtc)
    {
        DateTime cutoff = nowUtc.AddHours(-1);
        while (_recentEntryTimesUtc.Count > 0 && _recentEntryTimesUtc.Peek() <= cutoff)
            _recentEntryTimesUtc.Dequeue();

        _tradesThisHour = _recentEntryTimesUtc.Count;
        return _tradesThisHour;
    }

    private string GetWatchlistReadiness(string symbol, bool nwTouchMode = false)
    {
        try
        {
            if (!_watchlist.Contains(symbol, StringComparer.OrdinalIgnoreCase)) return "Off WL";
            if (_haltTrading) return string.IsNullOrWhiteSpace(_haltReason) ? "Halted" : $"Halted: {_haltReason}";
            if (!_reconciled) return "Recon";
            if (!_manualResumeOverride && IsUnrealizedDrawdownBreached()) return "DD block";

            var nowEt = GetEasternTime();
            if (nowEt.DayOfWeek == DayOfWeek.Saturday || nowEt.DayOfWeek == DayOfWeek.Sunday) return "Closed";
            if (nowEt.Hour < 9 || (nowEt.Hour == 9 && nowEt.Minute < (30 + MIN_ENTRY_MINUTES_AFTER_OPEN))) return "Too early";
            if (!SWING_MODE_ENABLED && (nowEt.Hour > 15 || (nowEt.Hour == 15 && nowEt.Minute >= 30))) return "Late";
            if (SWING_MODE_ENABLED && (nowEt.Hour > 15 || (nowEt.Hour == 15 && nowEt.Minute >= 50))) return "Late";
            bool isOpExFriday = nowEt.DayOfWeek == DayOfWeek.Friday
                && nowEt.Day >= 15 && nowEt.Day <= 21;
            if (isOpExFriday && _tradesToday >= 3) return "OpEx cap";
            if (_earningsBlacklist.Contains(symbol)) return "Earnings";
            if (!SWING_MODE_ENABLED && !nwTouchMode && MIDDAY_FILTER_ENABLED &&
                _marketRegime != "TRENDING" &&
                (nowEt.Hour == 11 && nowEt.Minute >= 45 ||
                 nowEt.Hour == 12 ||
                 nowEt.Hour == 13 && nowEt.Minute < 5))
                return "Midday";

            if (!_marketData.TryGetValue(symbol, out var candles) || candles.Count < 50) return "No data";
            decimal price = candles.Last().Close;
            if (price <= 0m) return "No px";

            if (!HasSufficientSessionLiquidity(symbol, candles, nowEt,
                                                out decimal dollarVolume,
                                                out decimal requiredDollarVolume))
                return $"Liq ${dollarVolume / 1_000_000m:F1}M/${requiredDollarVolume / 1_000_000m:F1}M";

            if (_latestBid.TryGetValue(symbol, out decimal bid) &&
                _latestAsk.TryGetValue(symbol, out decimal ask) &&
                bid > 0m && ask > 0m)
            {
                decimal mid = (ask + bid) / 2m;
                decimal spreadPct = mid > 0m ? (ask - bid) / mid : 0m;
                if (spreadPct > 0.0020m) return "Wide spr";
            }

            if (!nwTouchMode && candles.Count >= 11 && IsLiquiditySweep(candles)) return "Sweep";

            if (!nwTouchMode)
            {
                var todayCandles = candles.Where(c => c.Time.Date == nowEt.Date).ToList();
                if (todayCandles.Count >= 20)
                {
                    decimal dayHigh = todayCandles.Max(c => c.High);
                    decimal dayLow = todayCandles.Min(c => c.Low);
                    decimal dayRngPct = price > 0m ? (dayHigh - dayLow) / price : 0m;
                    if (dayRngPct < 0.004m) return "Dormant";
                }
            }

            lock (_lock)
            {
                if (_tradesToday >= MAX_TRADES_PER_DAY) return "Day cap";
                if (GetRollingHourEntryCountLocked(DateTime.UtcNow) >= MAX_TRADES_PER_HOUR) return "Hour cap";
                if (_positions.ContainsKey(symbol)) return "In pos";
                if (_pendingEntrySymbols.ContainsKey(symbol)) return "Pending";
                if (_positions.Count + _pendingEntryCount >= MAX_POSITIONS) return "Max pos";

                decimal deployedCapital = _positions.Values.Sum(p => p.AvgPrice * p.Quantity) + _pendingEntryCount * POSITION_SIZE;
                if (TOTAL_BUDGET - deployedCapital < POSITION_SIZE) return "No cash";

                if (_lastTradeTime.TryGetValue(symbol, out var lastTime))
                {
                    int cooldown = _lastTradeWasLoss.GetValueOrDefault(symbol) ? COOLDOWN_SECONDS * 2 : COOLDOWN_SECONDS;
                    if ((DateTime.UtcNow - lastTime).TotalSeconds < cooldown) return "Cooldown";
                }

                int symCount = _dailyEntryCount.GetValueOrDefault(symbol);
                if (symCount >= MAX_TRADES_PER_SYMBOL_PER_DAY) return $"Sym cap {symCount}";
            }

            if (SWING_MODE_ENABLED)
            {
                var dailyBars = GetDailyBarsPreferLive(symbol);
                if (dailyBars == null || dailyBars.Count < 60) return "No daily";
                return "Swing scan";
            }

            // In OR mode there is no universal setup score or generic quantity
            // estimate. Each strategy owns its signal and sizing requirements.
            return nwTouchMode ? "Ready (NW)" : "Ready (OR)";
        }
        catch
        {
            return "Check";
        }
    }

    private void ReleasePendingEntrySlot(string symbol, string reason)
    {
        if (string.IsNullOrWhiteSpace(symbol)) return;

        bool hadPending = _pendingEntrySymbols.TryRemove(symbol, out _);
        _pendingEntryCreatedUtc.TryRemove(symbol, out DateTime submittedUtc);
        _pendingStrategyTag?.TryRemove(symbol, out _);
        _pendingInitialRisk.TryRemove(symbol, out _);
        _pendingBracketChildren.TryRemove(symbol, out _);

        if (hadPending)
        {
            int after = Interlocked.Decrement(ref _pendingEntryCount);
            if (after < 0) Interlocked.Exchange(ref _pendingEntryCount, 0);

            lock (_lock)
            {
                int symbolAttempts = _dailyEntryCount.GetValueOrDefault(symbol);
                if (symbolAttempts <= 1) _dailyEntryCount.Remove(symbol);
                else _dailyEntryCount[symbol] = symbolAttempts - 1;

                if (submittedUtc != DateTime.MinValue && _recentEntryTimesUtc.Contains(submittedUtc))
                {
                    var retained = _recentEntryTimesUtc.Where(t => t != submittedUtc).ToList();
                    _recentEntryTimesUtc.Clear();
                    foreach (DateTime t in retained) _recentEntryTimesUtc.Enqueue(t);
                    GetRollingHourEntryCountLocked(DateTime.UtcNow);
                }
            }
            LogMessage($"[PENDING RELEASE] {symbol} — {reason}; slot freed.");
        }
    }

    private void ExpireStalePendingEntries()
    {
        if (_pendingEntryCreatedUtc.IsEmpty) return;

        var now = DateTime.UtcNow;
        foreach (var kv in _pendingEntryCreatedUtc.ToArray())
        {
            string symbol = kv.Key;
            if ((now - kv.Value).TotalSeconds < PENDING_ENTRY_TIMEOUT_SECONDS) continue;
            if (_positions.ContainsKey(symbol))
            {
                _pendingEntryCreatedUtc.TryRemove(symbol, out _);
                _pendingEntrySymbols.TryRemove(symbol, out _);
                continue;
            }

            foreach (var order in _ordersById.Where(o => string.Equals(o.Value.Symbol, symbol, StringComparison.OrdinalIgnoreCase)).ToList())
            {
                try { RealBroker?.CancelOrder(order.Key); } catch { }
                _ordersById.TryRemove(order.Key, out _);
                _bracketExitReasonByOrderId.TryRemove(order.Key, out _);
            }

            ReleasePendingEntrySlot(symbol, $"entry order stale > {PENDING_ENTRY_TIMEOUT_SECONDS}s");
        }
    }

    // ══════════════════════════════════════════════════════════
    //  UNIVERSAL ENTRY GATES
    //  Shared by ExecuteStrategy (closed-bar) and TryEarlyPatternEntry (intrabar).
    //  Every check that limits entry eligibility lives here — no path may bypass them.
    // ══════════════════════════════════════════════════════════

    private bool PassesEntryGates(string symbol, out int minutesSinceOpen, bool nwTouchMode = false)
    {
        minutesSinceOpen = -1;
        ExpireStalePendingEntries();
        if (_haltTrading)
        {
            // Previously silent — a stuck _haltTrading blocked every entry
            // with zero indication why. Throttled to once per 5 min per
            // process (not per-symbol/per-tick) so it doesn't flood the
            // console across a 50+ symbol watchlist.
            if ((DateTime.UtcNow - _lastHaltGateLogUtc).TotalMinutes >= 5)
            {
                LogMessage($"[GATE] _haltTrading=true ({(string.IsNullOrWhiteSpace(_haltReason) ? "UNSPECIFIED" : _haltReason)}) — ALL entries blocked. " +
                           "Clears automatically at next daily reset, or check DAILY_PROFIT_GOAL/MAX_DAILY_LOSS/MAX_CONSECUTIVE_LOSSES.");
                _lastHaltGateLogUtc = DateTime.UtcNow;
            }
            return false;
        }
        if (!_watchlist.Contains(symbol, StringComparer.OrdinalIgnoreCase)) return false;
        if (!_reconciled)
        {
            // Previously silent — a stuck _reconciled=false (e.g. IBKR's
            // positionEnd() never firing after a watchdog reconnect) blocked
            // every entry with zero indication why. Same 5-min throttle.
            if ((DateTime.UtcNow - _lastReconGateLogUtc).TotalMinutes >= 5)
            {
                LogMessage("[GATE] _reconciled=false — ALL entries blocked pending IBKR position reconciliation. " +
                           "If this repeats for more than ~1 min after startup/reconnect, reconciliation is stuck.");
                _lastReconGateLogUtc = DateTime.UtcNow;
            }
            return false;
        }

        // ── RISK MGMT: Unrealized drawdown circuit breaker ──
        // Block new entries when realized+unrealized equity is deeply negative.
        // Existing positions keep their stops — this only prevents digging deeper.
        if (!_manualResumeOverride && IsUnrealizedDrawdownBreached())
        {
            LogMessage($"[DD BREAKER] {symbol} entry blocked — total equity PnL ${GetTotalEquityPnL():F2} ≤ threshold ${UNREALIZED_DD_HALT_THRESHOLD}");
            return false;
        }

        var nowEt = GetEasternTime();
        if (nowEt.DayOfWeek == DayOfWeek.Saturday || nowEt.DayOfWeek == DayOfWeek.Sunday) return false;
        if (nowEt.Hour < 9 || (nowEt.Hour == 9 && nowEt.Minute < (30 + MIN_ENTRY_MINUTES_AFTER_OPEN))) return false;
        if (!SWING_MODE_ENABLED && (nowEt.Hour > 15 || (nowEt.Hour == 15 && nowEt.Minute >= 30))) return false;
        if (SWING_MODE_ENABLED && (nowEt.Hour > 15 || (nowEt.Hour == 15 && nowEt.Minute >= 50))) return false;

        bool isOpExFriday = nowEt.DayOfWeek == DayOfWeek.Friday
            && nowEt.Day >= 15 && nowEt.Day <= 21;
        if (isOpExFriday && _tradesToday >= 3) return false;

        if (_earningsBlacklist.Contains(symbol)) return false;

        if (!SWING_MODE_ENABLED && !nwTouchMode && IsSymbolCold(symbol)) return false;

        if (!SWING_MODE_ENABLED && !nwTouchMode && MIDDAY_FILTER_ENABLED &&
            _marketRegime != "TRENDING" &&
            (nowEt.Hour == 11 && nowEt.Minute >= 45 ||
             nowEt.Hour == 12 ||
             nowEt.Hour == 13 && nowEt.Minute < 05))
            return false;

        if (!_marketData.TryGetValue(symbol, out var candles) || candles.Count < 50) return false;
        if (!HasSufficientSessionLiquidity(symbol, candles, nowEt, out _, out _)) return false;

        decimal lastPrice = candles.Last().Close;

        // Spread check
        if (_latestBid.TryGetValue(symbol, out decimal bid) &&
            _latestAsk.TryGetValue(symbol, out decimal ask) &&
            bid > 0 && ask > 0)
        {
            decimal mid = (ask + bid) / 2m;
            decimal spreadPct = mid > 0 ? (ask - bid) / mid : 0m;
            const decimal maxSpreadPct = 0.0020m;
            if (spreadPct > maxSpreadPct) return false;
        }

        // Liquidity sweep
        if (!nwTouchMode && candles.Count >= 11 && IsLiquiditySweep(candles)) return false;

        // Daily range dormancy is a setup-quality filter. NW touch mode skips it:
        // the 1H lower-envelope touch itself is the strategy signal.
        if (!nwTouchMode)
        {
            var todayCandlesRange = candles.Where(c => c.Time.Date == nowEt.Date).ToList();
            if (todayCandlesRange.Count >= 20)
            {
                decimal dayHigh = todayCandlesRange.Max(c => c.High);
                decimal dayLow = todayCandlesRange.Min(c => c.Low);
                decimal dayRngPct = lastPrice > 0 ? (dayHigh - dayLow) / lastPrice : 0m;
                if (dayRngPct < 0.004m) return false;
            }
        }

        // Position / budget / rate limits (needs lock)
        lock (_lock)
        {
            if (_tradesToday >= MAX_TRADES_PER_DAY) return false;
            if (GetRollingHourEntryCountLocked(DateTime.UtcNow) >= MAX_TRADES_PER_HOUR) return false;

            if (_positions.ContainsKey(symbol)) return false;
            if (_pendingEntrySymbols.ContainsKey(symbol)) return false;
            if (_positions.Count + _pendingEntryCount >= MAX_POSITIONS) return false;

            decimal deployedCapital = _positions.Values.Sum(p => p.AvgPrice * p.Quantity)
                                    + _pendingEntryCount * POSITION_SIZE;
            if (TOTAL_BUDGET - deployedCapital < POSITION_SIZE) return false;

            if (_lastTradeTime.TryGetValue(symbol, out var lastTime))
            {
                int cooldown = _lastTradeWasLoss.GetValueOrDefault(symbol)
                    ? COOLDOWN_SECONDS * 2
                    : COOLDOWN_SECONDS;
                if ((DateTime.UtcNow - lastTime).TotalSeconds < cooldown) return false;
            }
            if (_dailyEntryCount.GetValueOrDefault(symbol) >= MAX_TRADES_PER_SYMBOL_PER_DAY) return false;
        }

        minutesSinceOpen = (nowEt.Hour - 9) * 60 + nowEt.Minute - 30;

        if (SWING_MODE_ENABLED)
            return true;

        // OR semantics: a strategy's own signal is sufficient. A universal
        // ScoreSetup requirement here used to turn every strategy into
        // "strategy signal AND generic setup score". Safety, liquidity,
        // exposure, cooldown and risk gates above remain mandatory.
        return true;
    }


    private bool HasVcpStyleContraction(List<Candle> recentDaily)
    {
        if (recentDaily == null || recentDaily.Count < 16) return false;

        int chunk = Math.Max(4, recentDaily.Count / 4);
        var c1 = recentDaily.Take(chunk).ToList();
        var c2 = recentDaily.Skip(chunk).Take(chunk).ToList();
        var c3 = recentDaily.Skip(chunk * 2).Take(chunk).ToList();
        var c4 = recentDaily.Skip(chunk * 3).ToList();
        if (c1.Count < 3 || c2.Count < 3 || c3.Count < 3 || c4.Count < 3) return false;

        decimal r1 = c1.Max(x => x.High) - c1.Min(x => x.Low);
        decimal r2 = c2.Max(x => x.High) - c2.Min(x => x.Low);
        decimal r3 = c3.Max(x => x.High) - c3.Min(x => x.Low);
        decimal r4 = c4.Max(x => x.High) - c4.Min(x => x.Low);

        double v1 = c1.Average(x => (double)x.Volume);
        double v2 = c2.Average(x => (double)x.Volume);
        double v3 = c3.Average(x => (double)x.Volume);
        double v4 = c4.Average(x => (double)x.Volume);

        bool tighter = r4 <= r3 * 1.08m && r3 <= r2 * 1.10m && r2 <= r1 * 1.12m && r4 < r1;
        bool volumeDry = v4 < ((v1 + v2 + v3) / 3.0) * 0.92;
        return tighter && volumeDry;
    }

    private int ScoreSwingSetup(string symbol, decimal close, List<Candle> intradayCandles, List<Candle> dailyBars,
                                decimal pivot, decimal baseLow, bool contraction, bool volumeDry)
    {
        if (dailyBars == null || dailyBars.Count < 50) return 0;

        decimal sma20 = SafeSMA(dailyBars, 20);
        decimal sma50 = SafeSMA(dailyBars, 50);
        decimal sma100 = SafeSMA(dailyBars, 100);
        decimal sma200 = dailyBars.Count >= 200 ? SafeSMA(dailyBars, 200) : 0m;
        double rsi = SafeRSI(dailyBars, 14);
        decimal high52 = dailyBars.Max(c => c.High);
        decimal baseDepth = pivot > 0 ? (pivot - baseLow) / pivot : 1m;

        int score = 0;
        if (close > sma20 && sma20 > 0) score += 15;
        if (sma20 > sma50 && sma50 > 0) score += 15;
        if (sma50 > sma100 && sma100 > 0) score += 10;
        if (sma200 <= 0 || sma50 > sma200 || (sma50 > sma100 && sma100 > sma200)) score += 15;
        if (rsi >= 48 && rsi <= 72) score += 12;
        else if (rsi >= 44 && rsi <= 76) score += 6;
        if (baseDepth <= SWING_BASE_TIGHTNESS_MAX) score += 15;
        if (high52 > 0 && close >= high52 * 0.88m) score += 8;
        if (contraction) score += 12;
        if (volumeDry) score += 8;
        if (HasFastVolumeSurge(intradayCandles, FAST_VOL_MULT)) score += 8;
        if (_vwap.TryGetValue(symbol, out decimal vwapVal) && vwapVal > 0 && close > vwapVal) score += 5;
        return Math.Clamp(score, 0, 100);
    }

    private bool TrySwingBreakoutStrategy(string symbol, List<Candle> candles, DateTime nowEt)
    {
        var dailyBars = GetDailyBarsPreferLive(symbol);
        if (dailyBars == null || dailyBars.Count == 0) return false;
        if (candles == null || candles.Count < 30) return false;

        decimal close = candles.Last().Close;
        if (close <= 0) return false;

        decimal dailySma20 = SafeSMA(dailyBars, 20);
        decimal dailySma50 = SafeSMA(dailyBars, 50);
        decimal dailySma100 = SafeSMA(dailyBars, 100);
        decimal dailySma200 = dailyBars.Count >= 200 ? SafeSMA(dailyBars, 200) : 0m;
        if (dailySma20 <= 0 || dailySma50 <= 0) return false;

        if (!(close > dailySma20 && dailySma20 > dailySma50 && (dailySma200 <= 0 || dailySma50 > dailySma200 || (dailySma50 > dailySma100 && dailySma100 > dailySma200))))
            return false;

        double dailyRsi = SafeRSI(dailyBars, 14);
        if (dailyRsi < 48 || dailyRsi > 74) return false;

        var baseWindow = dailyBars.TakeLast(SWING_BASE_LOOKBACK_DAYS).ToList();

        decimal pivot = baseWindow.Max(c => c.High);
        decimal baseLow = baseWindow.Min(c => c.Low);
        if (pivot <= 0 || baseLow <= 0 || pivot <= baseLow) return false;

        decimal baseDepth = (pivot - baseLow) / pivot;
        if (baseDepth > SWING_BASE_TIGHTNESS_MAX) return false;

        bool contraction = HasVcpStyleContraction(baseWindow);
        if (SWING_REQUIRE_CONTRACTION && !contraction) return false;

        double recentVol = baseWindow.TakeLast(Math.Min(5, baseWindow.Count)).Average(c => (double)c.Volume);
        var olderWindow = baseWindow.Take(baseWindow.Count - Math.Min(5, baseWindow.Count)).ToList();
        double earlierVol = olderWindow.Count > 0 ? olderWindow.Average(c => (double)c.Volume) : 0;
        bool volumeDry = earlierVol > 0 && recentVol < earlierVol * 0.92;

        decimal breakoutBuffer = pivot * SWING_BREAKOUT_BUFFER_PCT;
        if (close < pivot + breakoutBuffer) return false;

        long todayVol = candles.Where(c => c.Time.Date == nowEt.Date).Sum(c => c.Volume);
        double avgDailyVol20 = dailyBars.TakeLast(Math.Min(20, dailyBars.Count)).Average(c => (double)c.Volume);
        bool relVolOk = avgDailyVol20 <= 0 || todayVol >= avgDailyVol20 * 0.08;
        bool tapeOk = relVolOk || HasFastVolumeSurge(candles, FAST_VOL_MULT) || CheckVolumeExpansion(candles);
        if (!tapeOk) return false;

        int score = ScoreSwingSetup(symbol, close, candles, dailyBars, pivot, baseLow, contraction, volumeDry);
        if (score < Math.Max(50, MIN_SETUP_SCORE)) return false;

        decimal stopAnchor = Math.Max(baseLow, dailySma20 * 0.995m);
        decimal stopDistance = Math.Max(close - stopAnchor, Math.Max(SafeATR(dailyBars, 14) * 1.10m, MIN_STOP_DISTANCE));
        if (stopDistance <= 0 || stopDistance / close > 0.12m) return false;

        int qty = CalcQtyV2(close, stopDistance);
        if (qty <= 0) return false;

        string tag = contraction ? "SWING_VCP_BREAKOUT_LONG" : "SWING_BASE_BREAKOUT_LONG";
        return OpenPosition(symbol, qty, close, TradeSide.Buy, false, tag);
    }

    // ══════════════════════════════════════════════════════════
    //  EXECUTE STRATEGY — DISPATCHER
    // ══════════════════════════════════════════════════════════

    public void ExecuteStrategy(string symbol, bool prevBarAboveVwap = false, bool currBarAboveVwap = false)
    {
        CheckDailyReset();

        if (!PassesEntryGates(symbol, out int minutesSinceOpen))
            return;

        if (!_marketData.TryGetValue(symbol, out var candles) || candles.Count < 50)
            return;

        lock (_lock)
        {
            var nowEt = GetEasternTime();

            if (SWING_MODE_ENABLED)
            {
                if (TrySwingBreakoutStrategy(symbol, candles, nowEt)) return;
                return;
            }

            // Gap&Go should be available earlier than the old 45-minute gate.
            // Strong gaps often do their real move in the first 20-40 minutes.
            int gapGoEnd = _marketRegime == "TRENDING" ? 150 : 120;

            // NON-NW OR ROUTER: every enabled strategy is evaluated independently until
            // one actually queues an order. Run() returns true only when
            // OpenPosition accepts the entry; a rejected candidate must not stop
            // later strategies from being tried.
            //
            // Nadaraya-Watson is deliberately absent here. UpdateLiveTick() owns
            // its independent live lower-band touch path, so NW never needs another
            // strategy to confirm it and never participates in this router's order.
            //
            // Why: a per-strategy cap can still let the earliest strategy families
            // consume most or all of the daily budget. With a fixed order,
            // CANDLE_PATTERNS/MICRO_PULLBACK/OUTSIDE_CANDLE/SCALP_BREAKOUT always got
            // first crack at every signal and silently absorbed the whole daily
            // budget — ORB/GAP_GO/VWAP/MEAN_REV/BB_MR/MOMENTUM/EMA_POCKET could go
            // days without ever getting a chance to fire, regardless of their flags.
            var nonNwStrategySlots = new (string Name, Func<bool> Run)[]
            {
                ("CANDLE_PATTERNS", () => STRATEGY_CANDLE_PATTERNS_ENABLED && minutesSinceOpen >= 10
                    && TryCandlestickPatternStrategy(symbol, candles, intrabar: false)),
                ("MICRO_PULLBACK", () => STRATEGY_MICRO_PULLBACK_ENABLED && minutesSinceOpen >= 6
                    && TryMicroPullbackStrategy(symbol, candles)),
                ("OUTSIDE_CANDLE", () => STRATEGY_OUTSIDE_CANDLE_ENABLED && minutesSinceOpen >= 10
                    && TryOutsideCandleStrategy(symbol, candles)),
                ("SCALP_BREAKOUT", () => minutesSinceOpen >= 6
                    && TryScalpBreakoutStrategy(symbol, candles)),
                ("ORB", () => STRATEGY_ORB_ENABLED && minutesSinceOpen >= ORB_MINUTES
                    && TryOrbStrategy(symbol, candles, nowEt)),
                ("GAP_GO", () => STRATEGY_GAP_GO_ENABLED && minutesSinceOpen >= 20 && minutesSinceOpen <= gapGoEnd
                    && TryGapAndGoStrategy(symbol, candles)),
                ("VWAP", () => STRATEGY_VWAP_ENABLED && minutesSinceOpen >= 15
                    && TryVwapBounceStrategy(symbol, candles, prevBarAboveVwap, currBarAboveVwap)),
                ("MEAN_REV", () => STRATEGY_MEAN_REV_ENABLED
                    && TryMeanReversionStrategy(symbol, candles)),
                ("BB_MR", () => STRATEGY_BB_MR_ENABLED
                    && TryBollingerMeanReversionStrategy(symbol, candles)),
                ("MOMENTUM", () => STRATEGY_MOMENTUM_ENABLED
                    && TryMomentumStrategy(symbol, candles)),
                ("EMA_POCKET", () => STRATEGY_EMA_POCKET_ENABLED
                    && TryEmaPocketStrategy(symbol, candles)),
            };

            // Rotate the starting point daily (day-of-year mod slot count) so each
            // strategy family leads the queue on a roughly equal share of days over
            // time, rather than the same families always going first.
            int rotation = nowEt.DayOfYear % nonNwStrategySlots.Length;
            for (int i = 0; i < nonNwStrategySlots.Length; i++)
            {
                var slot = nonNwStrategySlots[(rotation + i) % nonNwStrategySlots.Length];
                if (slot.Run())
                {
                    LogMessage($"[OR ROUTER] {symbol} accepted by {slot.Name}");
                    return;
                }
            }
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
            int qty = CalcQtyV2(close, stopDistance);
            if (qty <= 0) return false;
            return OpenPosition(symbol, qty, close, TradeSide.Buy, false, "BB_MR_LONG");
        }

        bool touchUpper = close >= upperBand && rsi > 60.0 && sma50 > 0 && close < sma50
                        && rsi < prevRsi;
        if (touchUpper && _allowShorts)
        {
            decimal stopDistance = Math.Max(atr * HARD_STOP_ATR_MULT, MIN_STOP_DISTANCE);
            int qty = CalcQtyV2(close, stopDistance);
            if (qty <= 0) return false;
            return OpenPosition(symbol, qty, close, TradeSide.Sell, true, "BB_MR_SHORT");
        }

        return false;
    }

    // ══════════════════════════════════════════════════════════
    //  STRATEGY: NADARAYA-WATSON TIMEFRAME ENVELOPE
    //
    //  IMPORTANT:
    //  - NW is calculated ONLY from completed regular-session timeframe bars.
    //  - The live stock price is compared against those fixed bands on
    //    every LAST tick: price <= lower => BUY.
    //  - An open NW long is sold when live price >= the current NW upper band.
    //  - No NW short is opened at the upper band. The upper band is the exit.
    //
    //  Using completed bars avoids the old circular/repainting problem where
    //  the current 1-minute price was also moving the band it was trying to touch.
    // ══════════════════════════════════════════════════════════

    // NOTE: name says "Hour" but this now buckets by NW_TIMEFRAME_MINUTES
    // (15, 30, or 60) — kept the original name to avoid touching every
    // caller across two files; the 60-minute default keeps existing
    // behavior unchanged unless NW_TIMEFRAME_MINUTES is set otherwise.
    private DateTime? GetRegularSessionHourBucket(DateTime time)
    {
        DateTime open = new DateTime(time.Year, time.Month, time.Day, 9, 30, 0);
        DateTime close = new DateTime(time.Year, time.Month, time.Day, 16, 0, 0);
        if (time < open || time >= close) return null;

        int tf = (NW_TIMEFRAME_MINUTES == 15 || NW_TIMEFRAME_MINUTES == 30 || NW_TIMEFRAME_MINUTES == 60)
            ? NW_TIMEFRAME_MINUTES : 60;
        int minutesFromOpen = (int)(time - open).TotalMinutes;
        return open.AddMinutes((minutesFromOpen / tf) * tf);
    }

    // Snaps any config value to the nearest of the three supported NW bar
    // sizes. IBKR's historical-data duration limits are much shorter for
    // sub-hour bars than for 1-hour bars (see IbClient.RequestHourlyHistoricalData),
    // so only these three are supported rather than an arbitrary integer.
    private int ValidateNwTimeframe(int minutes)
    {
        if (minutes <= 20) return 15;
        if (minutes <= 45) return 30;
        return 60;
    }

    private void UpdateHourlyFromMinute(string symbol, Candle minuteBar)
    {
        if (minuteBar == null) return;
        DateTime? bucketMaybe = GetRegularSessionHourBucket(minuteBar.Time);
        if (!bucketMaybe.HasValue) return;

        DateTime bucket = bucketMaybe.Value;
        var list = _hourlyCandles.GetOrAdd(symbol, _ => new List<Candle>());

        lock (list)
        {
            var bar = list.FirstOrDefault(c => c.Time == bucket);
            if (bar == null)
            {
                list.Add(new Candle
                {
                    Time = bucket,
                    Open = minuteBar.Open,
                    High = minuteBar.High,
                    Low = minuteBar.Low,
                    Close = minuteBar.Close,
                    Volume = minuteBar.Volume
                });
                list.Sort((a, b) => a.Time.CompareTo(b.Time));
            }
            else
            {
                bar.High = Math.Max(bar.High, minuteBar.High);
                bar.Low = Math.Min(bar.Low, minuteBar.Low);
                bar.Close = minuteBar.Close;
                // Volume is not used by NW; keep it monotonic for diagnostics.
                bar.Volume += Math.Max(0L, minuteBar.Volume);
            }

            int maxHourlyBars = Math.Max(NW_LOOKBACK + 100, 700);
            if (list.Count > maxHourlyBars)
                list.RemoveRange(0, list.Count - maxHourlyBars);
        }
    }

    private List<Candle> GetCompletedNwHourlyCandles(string symbol)
    {
        if (!_hourlyCandles.TryGetValue(symbol, out var source) || source == null)
            return new List<Candle>();

        List<Candle> snapshot;
        lock (source)
            snapshot = source.OrderBy(c => c.Time).ToList();

        DateTime nowEt = GetEasternTime();
        DateTime? activeBucket = GetRegularSessionHourBucket(nowEt);

        // During RTH, exclude the current still-forming timeframe bar.
        if (activeBucket.HasValue)
            snapshot = snapshot.Where(c => c.Time < activeBucket.Value).ToList();
        else
        {
            DateTime open = new DateTime(nowEt.Year, nowEt.Month, nowEt.Day, 9, 30, 0);
            DateTime close = new DateTime(nowEt.Year, nowEt.Month, nowEt.Day, 16, 0, 0);

            // Before today's open, do not accidentally use a partial current-day bar
            // that an IBKR historical response may have returned.
            if (nowEt < open)
                snapshot = snapshot.Where(c => c.Time.Date < nowEt.Date).ToList();
            else if (nowEt >= close)
                snapshot = snapshot.Where(c => c.Time < close).ToList();
        }

        return snapshot;
    }

    private (decimal mid, decimal upper, decimal lower) ComputeNadarayaWatsonEnvelope(List<Candle> candles)
    {
        if (candles == null || candles.Count < NW_LOOKBACK) return (0m, 0m, 0m);

        var series = candles.OrderBy(c => c.Time).ToList();
        int count = series.Count;
        double h = (double)NW_BANDWIDTH;
        double h2 = 2.0 * h * h;
        if (h2 <= 0) return (0m, 0m, 0m);

        // Endpoint estimate for the newest completed timeframe bar using the most
        // recent NW_LOOKBACK closes.
        int last = count - 1;
        int first = Math.Max(0, count - NW_LOOKBACK);
        double weightedSum = 0.0;
        double weightTotal = 0.0;
        for (int j = first; j <= last; j++)
        {
            int dist = last - j;
            double w = Math.Exp(-(dist * dist) / h2);
            weightedSum += w * (double)series[j].Close;
            weightTotal += w;
        }
        if (weightTotal <= 0) return (0m, 0m, 0m);
        decimal mid = (decimal)(weightedSum / weightTotal);

        // True non-repainting MAE. Average the latest NW_LOOKBACK-1 causal
        // endpoint errors, so the calculation follows the configured profile
        // (currently 30-minute / 250 / 6 / 2.5) without using future bars.
        double maeSum = 0.0;
        int maeCount = 0;
        int errorWindow = Math.Max(1, NW_LOOKBACK - 1);
        int errorStart = Math.Max(0, count - errorWindow);

        for (int i = errorStart; i < count; i++)
        {
            int jStart = Math.Max(0, i - NW_LOOKBACK + 1);
            double wSum = 0.0;
            double wTot = 0.0;

            for (int j = jStart; j <= i; j++)
            {
                int dist = i - j;
                double w = Math.Exp(-(dist * dist) / h2);
                wSum += w * (double)series[j].Close;
                wTot += w;
            }

            if (wTot <= 0) continue;
            double est = wSum / wTot;
            maeSum += Math.Abs((double)series[i].Close - est);
            maeCount++;
        }

        if (maeCount == 0) return (0m, 0m, 0m);
        decimal mae = (decimal)(maeSum / maeCount);

        decimal upper = mid + mae * NW_MULT;
        decimal lower = mid - mae * NW_MULT;
        return (mid, upper, lower);
    }

    private (decimal mid, decimal upper, decimal lower, int bars) GetNadarayaWatson1HourEnvelope(string symbol)
    {
        var bars = GetCompletedNwHourlyCandles(symbol);
        if (bars.Count < NW_LOOKBACK) return (0m, 0m, 0m, bars.Count);

        DateTime lastBarTime = bars[^1].Time;
        if (_nwEnvelopeCache.TryGetValue(symbol, out var cached)
            && cached.bars == bars.Count
            && cached.lastBarTime == lastBarTime
            && cached.lookback == NW_LOOKBACK
            && cached.bandwidth == NW_BANDWIDTH
            && cached.mult == NW_MULT)
        {
            return (cached.mid, cached.upper, cached.lower, cached.bars);
        }

        var (mid, upper, lower) = ComputeNadarayaWatsonEnvelope(bars);
        _nwEnvelopeCache[symbol] = (mid, upper, lower, bars.Count, lastBarTime,
                                    NW_LOOKBACK, NW_BANDWIDTH, NW_MULT);
        return (mid, upper, lower, bars.Count);
    }

    private bool TryNadarayaWatsonStrategy(string symbol, List<Candle> candles, decimal triggerPrice = 0m)
    {
        if (!STRATEGY_NADARAYA_WATSON_ENABLED) return false;

        var (mid, upper, lower, nwBars) = GetNadarayaWatson1HourEnvelope(symbol);
        if (nwBars < NW_LOOKBACK)
        {
            _lastNwDecisionBySymbol[symbol] = $"History {nwBars}/{NW_LOOKBACK}";
            return false;
        }
        if (mid <= 0 || upper <= lower)
        {
            _lastNwDecisionBySymbol[symbol] = "Bands invalid";
            return false;
        }

        decimal price = triggerPrice;
        if (price <= 0 && _latestTick.TryGetValue(symbol, out decimal livePrice))
            price = livePrice;
        if (price <= 0 && candles != null && candles.Count > 0)
            price = candles.Last().Close;
        if (price <= 0)
        {
            _lastNwDecisionBySymbol[symbol] = "No live price";
            return false;
        }

        // Entry is a live touch/cross of the completed lower envelope.
        if (price > lower)
        {
            decimal distancePct = lower > 0 ? (price - lower) / lower * 100m : 0m;
            _lastNwDecisionBySymbol[symbol] = $"Waiting: {distancePct:F2}% above NW Low";
            return false;
        }
        if (!PassesEntryGates(symbol, out _, nwTouchMode: true))
        {
            string decision = $"TOUCH blocked: {GetWatchlistReadiness(symbol, nwTouchMode: true)}";
            _lastNwDecisionBySymbol[symbol] = decision;
            _lastNwTouchDecisionBySymbol[symbol] = $"Last {GetEasternTime():HH:mm:ss} ET — {decision}";
            return false;
        }

        decimal atrForSizing = candles != null ? SafeATR(candles, 14) : MIN_STOP_DISTANCE;
        decimal stopDistance = GetStopDistanceForStrategy("NW_BAND_LONG", atrForSizing, price);
        int qty = CalcQtyV2(price, stopDistance);
        if (qty <= 0)
        {
            const string decision = "TOUCH blocked: size is zero";
            _lastNwDecisionBySymbol[symbol] = decision;
            _lastNwTouchDecisionBySymbol[symbol] = $"Last {GetEasternTime():HH:mm:ss} ET — {decision}";
            return false;
        }

        LogMessage($"[NW {NW_TIMEFRAME_MINUTES}M TOUCH] {symbol} price={price:F2} <= lower={lower:F2} | mid={mid:F2} upper={upper:F2} bars={nwBars}");
        _lastBlockedReasonBySymbol.TryRemove(symbol, out _);
        bool opened = OpenPosition(symbol, qty, price, TradeSide.Buy, false, "NW_BAND_LONG");
        if (opened)
        {
            string decision = $"ENTRY submitted at {price:F2}";
            _lastNwDecisionBySymbol[symbol] = decision;
            _lastNwTouchDecisionBySymbol[symbol] = $"Last {GetEasternTime():HH:mm:ss} ET — {decision}";
        }
        else
        {
            string reason = _lastBlockedReasonBySymbol.TryGetValue(symbol, out var blocked)
                ? blocked
                : "order rejected";
            string decision = $"TOUCH blocked: {reason}";
            _lastNwDecisionBySymbol[symbol] = decision;
            _lastNwTouchDecisionBySymbol[symbol] = $"Last {GetEasternTime():HH:mm:ss} ET — {decision}";
        }
        return opened;
    }


    // ══════════════════════════════════════════════════════════
    //  STRATEGY 0c — DEDICATED BREAKOUT SCALPER
    //  Faster, smaller-profit continuation entries that reuse the existing
    //  tape filters but avoid the oversized 2.4R swing-style expectation.
    // ══════════════════════════════════════════════════════════

    private bool TryScalpBreakoutStrategy(string symbol, List<Candle> candles)
    {
        if (candles.Count < 25) return false;
        if (!_indicatorCache.TryGetValue(symbol, out var ind)) return false;

        decimal close = candles.Last().Close;
        decimal atr = ind.Atr14;
        if (close <= 0 || atr <= 0) return false;

        decimal atrPct = atr / close;
        if (atrPct < MIN_ATR_PCT || (MAX_ATR_PCT > 0 && atrPct > MAX_ATR_PCT * 1.25m)) return false;

        _vwap.TryGetValue(symbol, out decimal vwapVal);

        var last = candles[^1];
        var priorFive = candles.Skip(Math.Max(0, candles.Count - 6)).Take(5).ToList();
        if (priorFive.Count < 5) return false;

        decimal recentHigh5 = priorFive.Max(c => c.High);
        decimal recentLow5 = priorFive.Min(c => c.Low);
        decimal range = last.High - last.Low;
        decimal closePos = range > 0 ? (last.Close - last.Low) / range : 0.5m;

        var priorVolumes = candles.Skip(Math.Max(0, candles.Count - 9)).Take(8).ToList();
        long avgPriorVol = priorVolumes.Count > 0 ? (long)priorVolumes.Average(c => c.Volume) : 0;
        bool fastVol = avgPriorVol > 0
                    && last.Volume >= avgPriorVol * Math.Max(1.30m, VOL_EXPAND_MULT);

        bool longBody = last.Close > last.Open && Body(last) >= atr * 0.35m && closePos >= 0.80m;
        bool shortBody = last.Close < last.Open && Body(last) >= atr * 0.35m && closePos <= 0.20m;
        decimal breakoutBuffer = Math.Max(atr * 0.15m, close * 0.0008m);
        bool notExtended = (ind.Ema21 <= 0 || Math.Abs(close - (decimal)ind.Ema21) <= atr * 1.75m)
                        && (vwapVal <= 0 || Math.Abs(close - vwapVal) <= atr * 1.75m);

        bool longBias = ind.Ema9 > ind.Ema21
                     && close > ind.Sma20
                     && (vwapVal <= 0 || close > vwapVal)
                     && ind.Rsi14 >= RSI_LONG_MIN && ind.Rsi14 <= 78;

        bool shortBias = ind.Ema9 < ind.Ema21
                      && close < ind.Sma20
                      && vwapVal > 0 && close < vwapVal
                      && ind.Rsi14 >= 22 && ind.Rsi14 <= RSI_SHORT_MAX;

        if (ALLOW_SCALP_BREAKOUT_LONGS
            && (_marketRegime == "TRENDING" || _marketRegime == "NORMAL")
            && !_spyOpenBearish
            && longBias
            && fastVol
            && longBody
            && notExtended
            && close > recentHigh5 + breakoutBuffer)
        {
            decimal stopDistance = Math.Max(GetStopDistanceForStrategy("SCALP_BREAKOUT_LONG", atr, close), close - last.Low);
            int qty = CalcQtyV2(close, stopDistance);
            if (qty <= 0) return false;

            return OpenPosition(symbol, qty, close, TradeSide.Buy, false, "SCALP_BREAKOUT_LONG");
        }

        if (ALLOW_SCALP_BREAKOUT_SHORTS
            && _allowShorts
            && (_marketRegime == "SELL-OFF" || _marketRegime == "NORMAL")
            && (_spyBearish || _spyOpenBearish)
            && shortBias
            && fastVol
            && shortBody
            && notExtended
            && close < recentLow5 - breakoutBuffer)
        {
            decimal stopDistance = Math.Max(GetStopDistanceForStrategy("SCALP_BREAKOUT_SHORT", atr, close), last.High - close);
            int qty = CalcQtyV2(close, stopDistance);
            if (qty <= 0) return false;

            return OpenPosition(symbol, qty, close, TradeSide.Sell, true, "SCALP_BREAKOUT_SHORT");
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

        var prior10Vols = candles.Skip(Math.Max(0, candles.Count - 11)).Take(10).ToList();
        long avgPriorVol10 = prior10Vols.Count > 0 ? (long)prior10Vols.Average(c => c.Volume) : 0;
        bool lastBarVolOk = avgPriorVol10 > 0 && lastCandle.Volume >= avgPriorVol10 * 1.20m;

        // Require a real hold beyond the range. A one-bar touch was producing
        // immediate failed breakouts on the bad session.
        int requiredHoldBars = _marketRegime == "NORMAL" ? 3 : 2;
        decimal orbBuffer = Math.Max(atr * 0.15m, close * 0.0008m);
        decimal lastRange = lastCandle.High - lastCandle.Low;
        decimal lastClosePos = lastRange > 0 ? (lastCandle.Close - lastCandle.Low) / lastRange : 0.5m;
        bool strongBullBar = lastCandle.Close > lastCandle.Open
                          && Body(lastCandle) >= atr * 0.30m
                          && lastClosePos >= 0.75m;
        bool strongBearBar = lastCandle.Close < lastCandle.Open
                          && Body(lastCandle) >= atr * 0.30m
                          && lastClosePos <= 0.25m;

        bool orbLongHold = candles.Count >= requiredHoldBars
                        && candles.TakeLast(requiredHoldBars).All(c => c.Close > orb.High + orbBuffer)
                        && (vwapVal <= 0 || close >= vwapVal);
        if (ALLOW_SCALP_ORB_LONGS && orbLongHold && lastBarVolOk && strongBullBar && rsi > RSI_LONG_MIN)
        {
            // V2 FIX: Changed from requiring _spyBullish to only blocking when _spyBearish.
            // Neutral SPY days still produce valid ORB breakouts — requiring bullish SPY
            // blocked too many winning trades. Only block when SPY is actively bearish.
            if (_spyBearish)
            {
                LogMessage($"[ORB SKIP] {symbol} ORB_LONG blocked — SPY actively bearish");
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

            decimal prevCloseOrb = GetPrevDayClose(symbol);
            if (prevCloseOrb > 0)
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

            decimal stopDistance = GetStopDistanceForStrategy("SCALP_ORB_LONG", atr, close);
            int qty = CalcQtyV2(close, stopDistance);
            if (qty <= 0) return false;

            return OpenPosition(symbol, qty, close, TradeSide.Buy, false, "SCALP_ORB_LONG");
        }

    TryShortOrb:
        bool orbShortHold = candles.Count >= requiredHoldBars
                         && candles.TakeLast(requiredHoldBars).All(c => c.Close < orb.Low - orbBuffer)
                         && (vwapVal > 0 && close <= vwapVal);
        if (orbShortHold && lastBarVolOk && strongBearBar && rsi < RSI_SHORT_MAX && _allowShorts)
        {
            if (!_spyBearish && !_spyOpenBearish) return false;

            if (_ema20_15min.TryGetValue(symbol, out var ema15S) && ema15S.ema20 > 0
                && close > ema15S.ema20) return false;

            var (_, pdLow) = GetPrevDayHL(symbol);
            if (pdLow > 0 && close > pdLow && close <= pdLow * 1.0025m) return false;

            decimal stopDistance = GetStopDistanceForStrategy("SCALP_ORB_SHORT", atr, close);
            int qty = CalcQtyV2(close, stopDistance);
            if (qty <= 0) return false;

            return OpenPosition(symbol, qty, close, TradeSide.Sell, true, "SCALP_ORB_SHORT");
        }

        return false;
    }

    // ══════════════════════════════════════════════════════════
    //  STRATEGY 2 — GAP AND GO
    // ══════════════════════════════════════════════════════════

    private bool TryGapAndGoStrategy(string symbol, List<Candle> candles)
    {
        decimal prevClose = GetPrevDayClose(symbol);
        if (prevClose <= 0) return false;
        if (!_latestTick.TryGetValue(symbol, out decimal currentPrice) || currentPrice <= 0) return false;
        decimal gapPct = (currentPrice - prevClose) / prevClose;

        if (Math.Abs(gapPct) < GAP_GO_MIN_PCT) return false;

        var todayEt = GetEasternTime().Date;
        long todayVol = GetTodayVolume(symbol);
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
            int qty = CalcQtyV2(close, stopDistance);
            if (qty <= 0) return false;

            return OpenPosition(symbol, qty, close, TradeSide.Buy, false, "GAP_GO_LONG");
        }

        if (gapPct < 0 && rsi < RSI_SHORT_MAX && _allowShorts)
        {
            decimal sma200 = GetDailySma200(symbol);
            if (sma200 > 0 && close > sma200 && _marketRegime != "SELL-OFF") return false;

            decimal stopDistance = Math.Max(atr * HARD_STOP_ATR_MULT, MIN_STOP_DISTANCE);
            int qty = CalcQtyV2(close, stopDistance);
            if (qty <= 0) return false;

            return OpenPosition(symbol, qty, close, TradeSide.Sell, true, "GAP_GO_SHORT");
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

            decimal stopDistance = GetStopDistanceForStrategy("SCALP_VWAP_LONG", atr, close);
            int qty = CalcQtyV2(close, stopDistance);
            if (qty <= 0) return false;
            return OpenPosition(symbol, qty, close, TradeSide.Buy, false, "SCALP_VWAP_LONG");
        }

        bool vwapRejection = prevAbove && allRecentBelow;
        if (vwapRejection && volExp && rsi < RSI_SHORT_MAX && _allowShorts)
        {
            var (_, pdLow) = GetPrevDayHL(symbol);
            if (pdLow > 0 && close > pdLow && close <= pdLow * 1.003m) return false;

            decimal stopDistance = GetStopDistanceForStrategy("SCALP_VWAP_SHORT", atr, close);
            int qty = CalcQtyV2(close, stopDistance);
            if (qty <= 0) return false;
            return OpenPosition(symbol, qty, close, TradeSide.Sell, true, "SCALP_VWAP_SHORT");
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
            int qty = CalcQtyV2(close, stopDistance);
            if (qty <= 0) return false;

            return OpenPosition(symbol, qty, close, TradeSide.Buy, false, "MEAN_REV_LONG");
        }

        bool overboughtInDowntrend = close < sma50 && rsi > RSI_OVERBOUGHT
                                   && rsi < prevRsi;
        if (overboughtInDowntrend && _allowShorts)
        {
            decimal stopDistance = Math.Max(atr * HARD_STOP_ATR_MULT, MIN_STOP_DISTANCE);
            int qty = CalcQtyV2(close, stopDistance);
            if (qty <= 0) return false;

            return OpenPosition(symbol, qty, close, TradeSide.Sell, true, "MEAN_REV_SHORT");
        }

        return false;
    }

    // ══════════════════════════════════════════════════════════
    //  STRATEGY 5 — MOMENTUM BREAKOUT & CONFIRMED PULLBACK
    // ══════════════════════════════════════════════════════════

    private bool TryMomentumStrategy(string symbol, List<Candle> candles)
    {
        if (candles == null || candles.Count < 30) return false;

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

        bool longRegime = _marketRegime == "TRENDING" || _marketRegime == "NORMAL";
        bool regimeStrong = longRegime && !_spyOpenBearish && !_spyBearish;
        bool relativeStrength = CheckRelativeStrength(symbol, candles);
        bool rsiConfirm = rsi > RSI_LONG_MIN;
        bool aboveVwap = vwapVal <= 0 || close > vwapVal;
        bool macdBullish = macdDir > 0;

        decimal recentHigh = ind.RecentHigh8;
        decimal range = lastCandle.High - lastCandle.Low;
        var prior10 = candles.Skip(Math.Max(0, candles.Count - 11)).Take(10).ToList();
        decimal avgRange = prior10.Count > 0 ? prior10.Average(c => c.High - c.Low) : 0m;
        bool expansion = range > avgRange * 1.3m;
        decimal closePos = range > 0 ? (lastCandle.Close - lastCandle.Low) / range : 0.5m;
        decimal breakoutBuffer = Math.Max(atr * 0.15m, close * 0.0008m);

        long priorAvgVolume = prior10.Count > 0 ? (long)prior10.Average(c => c.Volume) : 0;
        bool pullbackVolume = priorAvgVolume > 0 && lastCandle.Volume >= priorAvgVolume * 0.90m;
        bool pullbackEntry = longRegime
                          && lastCandle.Close > lastCandle.Open
                          && lastCandle.Low <= sma20 + atr * 0.10m
                          && lastCandle.Close > sma20
                          && close > sma50
                          && rsi >= RSI_LONG_MIN && rsi <= 72
                          && relativeStrength
                          && aboveVwap
                          && ind.Ema9 > ind.Ema21
                          && pullbackVolume;
        bool volCompressed = IsVolatilityCompressed(candles);
        bool breakoutSignal = longRegime
                           && !_spyBearish
                           && expansion
                           && volExp
                           && volCompressed
                           && lastCandle.Close > lastCandle.Open
                           && closePos >= 0.75m
                           && Body(lastCandle) >= atr * 0.30m
                           && close > recentHigh + breakoutBuffer;

        bool hasSignal = breakoutSignal || pullbackEntry;

        int score = (regimeStrong ? 1 : 0)
                  + (relativeStrength ? 1 : 0)
                  + (rsiConfirm ? 1 : 0)
                  + (aboveVwap ? 1 : 0)
                  + (macdBullish ? 1 : 0);

        if (score >= 4 && hasSignal)
        {
            decimal sma200 = GetDailySma200(symbol);
            if (sma200 > 0 && close < sma200 * 0.97m) return false;

            if (_ema20_15min.TryGetValue(symbol, out var ema15ML) && ema15ML.ema20 > 0
                && close < ema15ML.ema20) return false;

            decimal stopDistance = Math.Max(atr * HARD_STOP_ATR_MULT, MIN_STOP_DISTANCE);
            int qty = CalcQtyV2(close, stopDistance);
            if (qty <= 0) return false;

            return OpenPosition(symbol, qty, close, TradeSide.Buy, false, "MOMENTUM_LONG");
        }

        bool inShortRegime = _marketRegime == "SELL-OFF" || _marketRegime == "NORMAL";
        if (inShortRegime && _allowShorts)
        {
            bool rsiShortConfirm = rsi < RSI_SHORT_MAX;
            bool belowVwap = vwapVal > 0 && close < vwapVal;
            bool bearishTape = _spyBearish || _marketRegime == "SELL-OFF";
            bool breakdownSignal = bearishTape
                                   && expansion
                                   && volExp
                                   && volCompressed
                                   && lastCandle.Close < lastCandle.Open
                                   && closePos <= 0.25m
                                   && Body(lastCandle) >= atr * 0.30m
                                   && close < ind.RecentLow8 - breakoutBuffer;
            bool macdBearish = macdDir < 0;

            int shortScore = (rsiShortConfirm ? 1 : 0)
                           + (!relativeStrength ? 1 : 0)
                           + (belowVwap ? 1 : 0)
                           + (volExp ? 1 : 0)
                           + (macdBearish ? 1 : 0);

            int shortRequired = _marketRegime == "SELL-OFF" ? 4 : 5;
            if (shortRequired <= shortScore && breakdownSignal)
            {
                decimal sma200 = GetDailySma200(symbol);
                if (sma200 > 0 && close > sma200 && _marketRegime != "SELL-OFF") return false;

                if (_ema20_15min.TryGetValue(symbol, out var ema15MS) && ema15MS.ema20 > 0
                    && close > ema15MS.ema20) return false;

                decimal stopDistance = Math.Max(atr * HARD_STOP_ATR_MULT, MIN_STOP_DISTANCE);
                int qty = CalcQtyV2(close, stopDistance);
                if (qty <= 0) return false;

                return OpenPosition(symbol, qty, close, TradeSide.Sell, true, "MOMENTUM_SHORT");
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

            decimal stopDistance = GetStopDistanceForStrategy("SCALP_EMA_LONG", atr, close);
            int qty = CalcQtyV2(close, stopDistance);
            if (qty <= 0) return false;

            return OpenPosition(symbol, qty, close, TradeSide.Buy, false, "SCALP_EMA_LONG");
        }

        bool bearishOrder = ema9 < ema21;
        bool inShortPocket = (double)close > ema9 && (double)close < ema21;

        if (bearishOrder && inShortPocket && ema9Falling && ema21Falling && rsi < 50 && _allowShorts)
        {
            decimal sma200 = GetDailySma200(symbol);
            if (sma200 > 0 && close > sma200 && _marketRegime != "SELL-OFF") return false;

            decimal stopDistance = GetStopDistanceForStrategy("SCALP_EMA_SHORT", atr, close);
            int qty = CalcQtyV2(close, stopDistance);
            if (qty <= 0) return false;

            return OpenPosition(symbol, qty, close, TradeSide.Sell, true, "SCALP_EMA_SHORT");
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

            int qty = CalcQtyV2(close, stopDistance);
            if (qty <= 0) return false;

            return OpenPosition(symbol, qty, close, TradeSide.Buy, false, "OUTSIDE_CANDLE_LONG");
        }

        if (bearishClose && ema50_30min > 0 && (double)close < ema50_30min && rsi < 50 && _allowShorts)
        {
            decimal sma200 = GetDailySma200(symbol);
            if (sma200 > 0 && close > sma200 && _marketRegime != "SELL-OFF") return false;

            decimal stopDistance = Math.Max(outside.High - close, atr * HARD_STOP_ATR_MULT);
            stopDistance = Math.Max(stopDistance, MIN_STOP_DISTANCE);
            decimal target = vwapVal > 0 && vwapVal < close ? vwapVal : (pdLow > 0 && pdLow < close ? pdLow : close - atr * 2);
            if (close - target < stopDistance * 2m) return false;

            int qty = CalcQtyV2(close, stopDistance);
            if (qty <= 0) return false;

            return OpenPosition(symbol, qty, close, TradeSide.Sell, true, "OUTSIDE_CANDLE_SHORT");
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

    private void TryEarlyPatternEntry(string symbol, Candle current)
    {
        if (!STRATEGY_CANDLE_PATTERNS_ENABLED || !EARLY_PATTERN_ENTRY_ENABLED) return;
        if (current == null) return;

        var nowEt = GetEasternTime();
        if (nowEt.Hour < 9 || (nowEt.Hour == 9 && nowEt.Minute < 40)) return;

        // ── Run the FULL universal gate stack — same gates as ExecuteStrategy ──
        // This was the #1 architectural bug: the intrabar path previously bypassed
        // earnings blacklist, spread check, daily range, trade limits, budget,
        // cooldown-after-loss, setup score, and more.
        if (!PassesEntryGates(symbol, out _)) return;

        // Intrabar-specific cooldown (separate from per-symbol cooldown in gates)
        if (_lastIntrabarSignalUtc.TryGetValue(symbol, out var lastSig)
            && (DateTime.UtcNow - lastSig).TotalSeconds < INTRABAR_SIGNAL_COOLDOWN_SECONDS)
            return;

        if (!_marketData.TryGetValue(symbol, out var completed) || completed.Count < 50) return;

        List<Candle> snapshot;
        lock (completed)
        {
            snapshot = completed.TakeLast(120)
                .Select(c => new Candle { Time = c.Time, Open = c.Open, High = c.High, Low = c.Low, Close = c.Close, Volume = c.Volume })
                .ToList();
        }
        snapshot.Add(new Candle
        {
            Time = current.Time,
            Open = current.Open,
            High = current.High,
            Low = current.Low,
            Close = current.Close,
            Volume = current.Volume
        });

        if (TryCandlestickPatternStrategy(symbol, snapshot, intrabar: true))
            _lastIntrabarSignalUtc[symbol] = DateTime.UtcNow;
    }

    private bool TryCandlestickPatternStrategy(string symbol, List<Candle> candles, bool intrabar)
    {
        if (candles == null || candles.Count < 3) return false;

        var pattern = DetectBestPattern(candles);
        if (pattern.Score < PATTERN_MIN_SCORE) return false;

        decimal close = candles.Last().Close;
        decimal atr = SafeATR(candles, 14);
        if (close <= 0 || atr <= 0) return false;

        decimal atrPct = close > 0 ? atr / close : 0m;
        if (atrPct < MIN_ATR_PCT * 0.75m) return false;
        if (MAX_ATR_PCT > 0 && atrPct > MAX_ATR_PCT * 1.30m) return false;

        double rsi = SafeRSI(candles, 14);
        double prevRsi = SafeRSI(candles.Take(candles.Count - 1).ToList(), 14);
        _vwap.TryGetValue(symbol, out decimal vwapVal);

        // Volume check: HIGH-SCORE patterns (>= 65) can fire without volume confirmation.
        // This is the KEY change — the old code blocked virtually all pattern entries
        // because volume expansion rarely aligns with the exact pattern completion bar.
        bool fastVol = HasFastVolumeSurge(candles, intrabar ? FAST_VOL_MULT : Math.Max(1.15m, FAST_VOL_MULT - 0.10m));
        bool volOk = fastVol || CheckVolumeExpansion(candles);
        bool highScorePattern = pattern.Score >= 65;
        if (!volOk && !highScorePattern) return false;

        decimal stopDistance = GetStopDistanceForStrategy(pattern.Bullish ? $"SCALP_PATTERN_{pattern.Tag}_LONG" : $"SCALP_PATTERN_{pattern.Tag}_SHORT", atr, close);
        int qty = CalcQtyV2(close, stopDistance);
        if (qty <= 0) return false;

        if (pattern.Bullish)
        {
            if (!ALLOW_BULLISH_CANDLE_PATTERNS) return false;
            if (_marketRegime == "SELL-OFF" && !CheckStrongRelativeStrength(symbol, candles)) return false;
            if (_spyOpenBearish) return false;

            // Relaxed VWAP: allow slightly below VWAP (within 0.3% or 0.5 ATR)
            bool nearOrAboveVwap = vwapVal <= 0 || close >= vwapVal - atr * 0.5m;
            bool rsiOk = rsi >= 44 || rsi > prevRsi;  // relaxed from 48
            if (!nearOrAboveVwap && !CheckStrongRelativeStrength(symbol, candles)) return false;
            if (!rsiOk) return false;

            string tag = $"SCALP_PATTERN_{pattern.Tag}_LONG";
            if (OpenPosition(symbol, qty, close, TradeSide.Buy, false, tag))
            {
                LogMessage($"[PATTERN] {(intrabar ? "EARLY" : "CLOSE")} {symbol} bullish {pattern.Tag} score={pattern.Score} rsi={rsi:F0} vol={volOk}");
                return true;
            }
            return false;
        }

        if (!_allowShorts) return false;
        bool nearOrBelowVwap = vwapVal > 0 && close <= vwapVal + atr * 0.5m;
        bool bearishTape = _spyBearish || _marketRegime == "SELL-OFF" || _marketRegime == "NORMAL";
        bool shortRsiOk = rsi <= 56 || rsi < prevRsi;  // relaxed from 52
        if (!nearOrBelowVwap && _marketRegime != "SELL-OFF") return false;
        if (!bearishTape || !shortRsiOk) return false;

        string shortTag = $"SCALP_PATTERN_{pattern.Tag}_SHORT";
        if (OpenPosition(symbol, qty, close, TradeSide.Sell, true, shortTag))
        {
            LogMessage($"[PATTERN] {(intrabar ? "EARLY" : "CLOSE")} {symbol} bearish {pattern.Tag} score={pattern.Score} rsi={rsi:F0} vol={volOk}");
            return true;
        }
        return false;
    }

    // ══════════════════════════════════════════════════════════
    //  STRATEGY 9 — MICRO PULLBACK (1-2 bar dip after impulse)
    //  Catches entries BEFORE the breakout is confirmed — this is
    //  the "enter early" strategy that addresses the core timing problem.
    // ══════════════════════════════════════════════════════════

    private bool TryMicroPullbackStrategy(string symbol, List<Candle> candles)
    {
        if (candles.Count < 15) return false;
        if (!_indicatorCache.TryGetValue(symbol, out var ind)) return false;

        decimal close = candles.Last().Close;
        decimal atr = ind.Atr14;
        double rsi = ind.Rsi14;
        if (close <= 0 || atr <= 0) return false;
        decimal atrPct = atr / close;
        if (atrPct < MIN_ATR_PCT || (MAX_ATR_PCT > 0 && atrPct > MAX_ATR_PCT)) return false;

        _vwap.TryGetValue(symbol, out decimal vwapVal);
        var last = candles[^1];
        var prev = candles[^2];

        // ── LONG micro-pullback ──
        // Look for: strong impulse bar(s) up, then 1-2 small pullback bars, 
        // then current bar reclaims and closes in upper 60% of its range.
        // This catches the move 1-2 bars BEFORE momentum/ORB would trigger.
        if (!_spyOpenBearish && _marketRegime != "SELL-OFF")
        {
            // Find impulse: look back 3-6 bars for a strong bullish candle
            int impulseIdx = -1;
            for (int i = candles.Count - 4; i >= Math.Max(0, candles.Count - 7); i--)
            {
                var c = candles[i];
                decimal body = c.Close - c.Open;
                decimal range = c.High - c.Low;
                if (body > atr * 0.6m && range > 0 && body / range >= 0.55m && c.Volume > 0)
                {
                    // Confirm it was a real impulse — volume above avg
                    double avgVol = candles.Skip(Math.Max(0, i - 10)).Take(10).Average(x => (double)x.Volume);
                    if (c.Volume >= avgVol * 1.2)
                    { impulseIdx = i; break; }
                }
            }

            if (impulseIdx >= 0)
            {
                var impulse = candles[impulseIdx];
                // Bars between impulse and current should be small pullbacks
                bool validPullback = true;
                decimal pullbackLow = decimal.MaxValue;
                for (int i = impulseIdx + 1; i < candles.Count - 1; i++)
                {
                    var pb = candles[i];
                    decimal pbBody = Math.Abs(pb.Close - pb.Open);
                    if (pbBody > Body(impulse) * 0.70m) { validPullback = false; break; }
                    pullbackLow = Math.Min(pullbackLow, pb.Low);
                }

                // Pullback should not retrace more than 60% of impulse body
                decimal impulseBody = impulse.Close - impulse.Open;
                decimal retracement = impulse.Close - pullbackLow;
                bool shallowPullback = impulseBody > 0 && retracement <= impulseBody * 0.60m;

                // Current bar must show reclaim: close in upper 60% of its range
                decimal lastRange = last.High - last.Low;
                decimal closePos = lastRange > 0 ? (last.Close - last.Low) / lastRange : 0.5m;
                bool reclaimBar = closePos >= 0.60m && last.Close > last.Open;

                if (validPullback && shallowPullback && reclaimBar && rsi >= 48)
                {
                    bool aboveVwap = vwapVal <= 0 || close >= vwapVal * 0.998m;
                    if (aboveVwap || CheckStrongRelativeStrength(symbol, candles))
                    {
                        decimal stopDistance = Math.Max(Math.Max(close - pullbackLow, GetStopDistanceForStrategy("SCALP_PULLBACK_LONG", atr, close)), MIN_STOP_DISTANCE);
                        int qty = CalcQtyV2(close, stopDistance);
                        if (qty > 0)
                        {
                            if (OpenPosition(symbol, qty, close, TradeSide.Buy, false, "SCALP_PULLBACK_LONG"))
                            {
                                LogMessage($"[SCALP PULLBACK] {symbol} LONG impulse@{impulse.Close:F2} pullback-low={pullbackLow:F2} reclaim@{close:F2}");
                                return true;
                            }
                        }
                    }
                }
            }
        }

        // ── SHORT micro-pullback ──
        if (_allowShorts && (_marketRegime == "SELL-OFF" || _marketRegime == "NORMAL" || _spyBearish))
        {
            int impulseIdx = -1;
            for (int i = candles.Count - 4; i >= Math.Max(0, candles.Count - 7); i--)
            {
                var c = candles[i];
                decimal body = c.Open - c.Close;
                decimal range = c.High - c.Low;
                if (body > atr * 0.6m && range > 0 && body / range >= 0.55m)
                {
                    double avgVol = candles.Skip(Math.Max(0, i - 10)).Take(10).Average(x => (double)x.Volume);
                    if (c.Volume >= avgVol * 1.2)
                    { impulseIdx = i; break; }
                }
            }

            if (impulseIdx >= 0)
            {
                var impulse = candles[impulseIdx];
                bool validPullback = true;
                decimal pullbackHigh = decimal.MinValue;
                for (int i = impulseIdx + 1; i < candles.Count - 1; i++)
                {
                    var pb = candles[i];
                    decimal pbBody = Math.Abs(pb.Close - pb.Open);
                    if (pbBody > Body(impulse) * 0.70m) { validPullback = false; break; }
                    pullbackHigh = Math.Max(pullbackHigh, pb.High);
                }

                decimal impulseBody = impulse.Open - impulse.Close;
                decimal retracement = pullbackHigh - impulse.Close;
                bool shallowPullback = impulseBody > 0 && retracement <= impulseBody * 0.60m;

                decimal lastRange = last.High - last.Low;
                decimal closePos = lastRange > 0 ? (last.Close - last.Low) / lastRange : 0.5m;
                bool rejectBar = closePos <= 0.40m && last.Close < last.Open;

                if (validPullback && shallowPullback && rejectBar && rsi <= 52)
                {
                    bool belowVwap = vwapVal > 0 && close <= vwapVal * 1.002m;
                    if (belowVwap || _marketRegime == "SELL-OFF")
                    {
                        decimal stopDistance = Math.Max(Math.Max(pullbackHigh - close, GetStopDistanceForStrategy("SCALP_PULLBACK_SHORT", atr, close)), MIN_STOP_DISTANCE);
                        int qty = CalcQtyV2(close, stopDistance);
                        if (qty > 0)
                        {
                            if (OpenPosition(symbol, qty, close, TradeSide.Sell, true, "SCALP_PULLBACK_SHORT"))
                            {
                                LogMessage($"[SCALP PULLBACK] {symbol} SHORT impulse@{impulse.Close:F2} pullback-high={pullbackHigh:F2} reject@{close:F2}");
                                return true;
                            }
                        }
                    }
                }
            }
        }

        return false;
    }

    // ══════════════════════════════════════════════════════════
    //  COMPREHENSIVE PATTERN DETECTION
    //  Detects 14 candlestick patterns with context-aware scoring.
    //  Scores are boosted when patterns appear at key levels (VWAP, S/R)
    //  and when volume confirms. This is the main "enter early" engine.
    // ══════════════════════════════════════════════════════════

    private PatternSignal DetectBestPattern(List<Candle> candles)
    {
        var none = new PatternSignal();
        if (candles == null || candles.Count < 3) return none;

        var patterns = new List<PatternSignal>();
        var last = candles[^1];
        var prev = candles[^2];
        decimal atr = SafeATR(candles, 14);
        decimal tol = Math.Max(last.Close * 0.0020m, atr * 0.18m);

        bool prevBear = prev.Close < prev.Open;
        bool prevBull = prev.Close > prev.Open;
        bool lastBull = last.Close > last.Open;
        bool lastBear = last.Close < last.Open;
        decimal prevBodyLow = Math.Min(prev.Open, prev.Close);
        decimal prevBodyHigh = Math.Max(prev.Open, prev.Close);
        decimal lastBodyLow = Math.Min(last.Open, last.Close);
        decimal lastBodyHigh = Math.Max(last.Open, last.Close);
        decimal lastBody = Body(last);
        decimal prevBody = Body(prev);

        // Context bonus: patterns at key levels are much more reliable
        int contextBonus = 0;
        _vwap.TryGetValue(candles[^1].Close > 0 ? "" : "", out _); // dummy to get symbol - we compute below
        bool volumeConfirm = candles.Count >= 6 && HasFastVolumeSurge(candles, 1.20m);
        if (volumeConfirm) contextBonus += 8;

        // Check if recent bars show a preceding trend (patterns are only valid after a move)
        decimal move5 = candles.Count >= 6 ? candles[^1].Close - candles[^6].Close : 0m;
        bool hadDownMove = move5 < -atr * 0.5m;
        bool hadUpMove = move5 > atr * 0.5m;

        // ── 1. BULLISH ENGULFING ──
        // Previous bar bearish, current bar's body fully engulfs previous body, high volume
        if (prevBear && lastBull && lastBodyLow <= prevBodyLow && lastBodyHigh >= prevBodyHigh
            && lastBody >= prevBody * 0.90m)  // body should be meaningful
        {
            int score = 62 + contextBonus;
            if (hadDownMove) score += 6;  // more reliable after a down move
            if (lastBody > prevBody * 1.5m) score += 4;  // strong engulfing
            patterns.Add(new PatternSignal { Tag = "BULL_ENGULFING", Bullish = true, Score = score, Reason = "bullish engulfing" });
        }

        // ── 2. BEARISH ENGULFING ──
        if (prevBull && lastBear && lastBodyHigh >= prevBodyHigh && lastBodyLow <= prevBodyLow
            && lastBody >= prevBody * 0.90m)
        {
            int score = 62 + contextBonus;
            if (hadUpMove) score += 6;
            if (lastBody > prevBody * 1.5m) score += 4;
            patterns.Add(new PatternSignal { Tag = "BEAR_ENGULFING", Bullish = false, Score = score, Reason = "bearish engulfing" });
        }

        // ── 3. HAMMER (bullish reversal single bar) ──
        // Small body at top, long lower wick >= 2x body, tiny upper wick
        if (lastBody > 0 && LowerWick(last) >= lastBody * 2.0m && UpperWick(last) <= lastBody * 0.60m)
        {
            int score = 55 + contextBonus;
            if (hadDownMove) score += 8;  // hammer after a drop is a strong signal
            if (lastBull) score += 3;     // green hammer slightly better
            patterns.Add(new PatternSignal { Tag = "HAMMER", Bullish = true, Score = score, Reason = "hammer" });
        }

        // ── 4. SHOOTING STAR (bearish reversal single bar) ──
        if (lastBody > 0 && UpperWick(last) >= lastBody * 2.0m && LowerWick(last) <= lastBody * 0.60m)
        {
            int score = 55 + contextBonus;
            if (hadUpMove) score += 8;
            if (lastBear) score += 3;
            patterns.Add(new PatternSignal { Tag = "SHOOTING_STAR", Bullish = false, Score = score, Reason = "shooting star" });
        }

        // ── 5. INVERTED HAMMER (bullish, after down move) ──
        // Long upper wick, small body at bottom — buyers tried to push up
        if (lastBody > 0 && UpperWick(last) >= lastBody * 2.0m && LowerWick(last) <= lastBody * 0.50m
            && hadDownMove)
        {
            int score = 52 + contextBonus;
            if (lastBull) score += 3;
            patterns.Add(new PatternSignal { Tag = "INV_HAMMER", Bullish = true, Score = score, Reason = "inverted hammer" });
        }

        // ── 6. HANGING MAN (bearish, after up move) ──
        // Long lower wick, small body at top — same shape as hammer but context differs
        if (lastBody > 0 && LowerWick(last) >= lastBody * 2.0m && UpperWick(last) <= lastBody * 0.50m
            && hadUpMove)
        {
            int score = 52 + contextBonus;
            if (lastBear) score += 3;
            patterns.Add(new PatternSignal { Tag = "HANGING_MAN", Bullish = false, Score = score, Reason = "hanging man" });
        }

        // ── 7. DOJI (indecision — use as confirmation with context) ──
        if (lastBody > 0 && lastBody <= (last.High - last.Low) * 0.10m && (last.High - last.Low) >= atr * 0.3m)
        {
            // Doji after a move signals reversal
            if (hadDownMove && LowerWick(last) > UpperWick(last))
                patterns.Add(new PatternSignal { Tag = "DRAGONFLY_DOJI", Bullish = true, Score = 50 + contextBonus, Reason = "dragonfly doji" });
            if (hadUpMove && UpperWick(last) > LowerWick(last))
                patterns.Add(new PatternSignal { Tag = "GRAVESTONE_DOJI", Bullish = false, Score = 50 + contextBonus, Reason = "gravestone doji" });
        }

        // ── 8. PIERCING LINE (bullish 2-bar) ──
        // Previous bearish, current opens below prev low, closes above prev midpoint
        if (prevBear && lastBull && last.Open <= prev.Low && last.Close > (prev.Open + prev.Close) / 2m
            && last.Close < prev.Open)
        {
            int score = 60 + contextBonus;
            if (hadDownMove) score += 5;
            patterns.Add(new PatternSignal { Tag = "PIERCING_LINE", Bullish = true, Score = score, Reason = "piercing line" });
        }

        // ── 9. DARK CLOUD COVER (bearish 2-bar) ──
        // Previous bullish, current opens above prev high, closes below prev midpoint
        if (prevBull && lastBear && last.Open >= prev.High && last.Close < (prev.Open + prev.Close) / 2m
            && last.Close > prev.Open)
        {
            int score = 60 + contextBonus;
            if (hadUpMove) score += 5;
            patterns.Add(new PatternSignal { Tag = "DARK_CLOUD", Bullish = false, Score = score, Reason = "dark cloud cover" });
        }

        // ── 10. TWEEZER BOTTOM / TOP ──
        if (Math.Abs(last.Low - prev.Low) <= tol && lastBull && prevBear)
        {
            int score = 56 + contextBonus;
            if (hadDownMove) score += 5;
            patterns.Add(new PatternSignal { Tag = "TWEEZER_BOTTOM", Bullish = true, Score = score, Reason = "tweezer bottom" });
        }
        if (Math.Abs(last.High - prev.High) <= tol && lastBear && prevBull)
        {
            int score = 56 + contextBonus;
            if (hadUpMove) score += 5;
            patterns.Add(new PatternSignal { Tag = "TWEEZER_TOP", Bullish = false, Score = score, Reason = "tweezer top" });
        }

        // ── 3-bar patterns ──
        if (candles.Count >= 3)
        {
            var a = candles[^3];
            var b = candles[^2];
            var c = candles[^1];
            decimal aBody = Body(a);
            decimal bBody = Body(b);
            decimal cBody = Body(c);
            decimal aMid = (a.Open + a.Close) / 2m;

            // ── 11. MORNING STAR ──
            bool morningStar = a.Close < a.Open                        // first bar bearish
                             && bBody <= Math.Max(aBody, cBody) * 0.50m // middle bar small (indecision)
                             && c.Close > c.Open                        // third bar bullish
                             && c.Close >= aMid;                        // closes above first bar midpoint
            if (morningStar)
            {
                int score = 68 + contextBonus;
                if (hadDownMove) score += 5;
                patterns.Add(new PatternSignal { Tag = "MORNING_STAR", Bullish = true, Score = score, Reason = "morning star" });
            }

            // ── 12. EVENING STAR ──
            bool eveningStar = a.Close > a.Open
                             && bBody <= Math.Max(aBody, cBody) * 0.50m
                             && c.Close < c.Open
                             && c.Close <= aMid;
            if (eveningStar)
            {
                int score = 68 + contextBonus;
                if (hadUpMove) score += 5;
                patterns.Add(new PatternSignal { Tag = "EVENING_STAR", Bullish = false, Score = score, Reason = "evening star" });
            }

            // ── 13. BULLISH ABANDONED BABY (gapped morning star) ──
            if (a.Close < a.Open && b.High < Math.Min(a.Close, a.Open)
                && c.Close > c.Open && c.Low > Math.Max(b.Open, b.Close))
            {
                patterns.Add(new PatternSignal { Tag = "BULL_ABANDONED_BABY", Bullish = true, Score = 75 + contextBonus, Reason = "bullish abandoned baby" });
            }

            // ── 14. BEARISH ABANDONED BABY ──
            if (a.Close > a.Open && b.Low > Math.Max(a.Close, a.Open)
                && c.Close < c.Open && c.High < Math.Min(b.Open, b.Close))
            {
                patterns.Add(new PatternSignal { Tag = "BEAR_ABANDONED_BABY", Bullish = false, Score = 75 + contextBonus, Reason = "bearish abandoned baby" });
            }

            // ── 15. THREE INSIDE UP (harami confirmation) ──
            // Bar A bearish, Bar B inside A (harami), Bar C closes above A's open
            bool threeInsideUp = a.Close < a.Open
                               && bBody < aBody
                               && Math.Min(b.Open, b.Close) >= Math.Min(a.Open, a.Close)
                               && Math.Max(b.Open, b.Close) <= Math.Max(a.Open, a.Close)
                               && c.Close > c.Open && c.Close > a.Open;
            if (threeInsideUp)
            {
                int score = 65 + contextBonus;
                if (hadDownMove) score += 5;
                patterns.Add(new PatternSignal { Tag = "THREE_INSIDE_UP", Bullish = true, Score = score, Reason = "three inside up" });
            }

            // ── 16. THREE INSIDE DOWN ──
            bool threeInsideDown = a.Close > a.Open
                                 && bBody < aBody
                                 && Math.Min(b.Open, b.Close) >= Math.Min(a.Open, a.Close)
                                 && Math.Max(b.Open, b.Close) <= Math.Max(a.Open, a.Close)
                                 && c.Close < c.Open && c.Close < a.Open;
            if (threeInsideDown)
            {
                int score = 65 + contextBonus;
                if (hadUpMove) score += 5;
                patterns.Add(new PatternSignal { Tag = "THREE_INSIDE_DOWN", Bullish = false, Score = score, Reason = "three inside down" });
            }
        }

        // ── 4-bar patterns (soldiers/crows) ──
        if (candles.Count >= 4)
        {
            var x = candles[^3];
            var y = candles[^2];
            var z = candles[^1];
            bool soldiers = x.Close > x.Open && y.Close > y.Open && z.Close > z.Open
                            && y.Close > x.Close && z.Close > y.Close
                            && y.Open >= Math.Min(x.Open, x.Close) // each opens within prior body
                            && z.Open >= Math.Min(y.Open, y.Close)
                            && LowerWick(y) <= Body(y) && LowerWick(z) <= Body(z);
            bool crows = x.Close < x.Open && y.Close < y.Open && z.Close < z.Open
                         && y.Close < x.Close && z.Close < y.Close
                         && y.Open <= Math.Max(x.Open, x.Close)
                         && z.Open <= Math.Max(y.Open, y.Close)
                         && UpperWick(y) <= Body(y) && UpperWick(z) <= Body(z);
            if (soldiers)
                patterns.Add(new PatternSignal { Tag = "THREE_WHITE_SOLDIERS", Bullish = true, Score = 74 + contextBonus, Reason = "three white soldiers" });
            if (crows)
                patterns.Add(new PatternSignal { Tag = "THREE_BLACK_CROWS", Bullish = false, Score = 74 + contextBonus, Reason = "three black crows" });
        }

        // ── HEAD AND SHOULDERS ──
        var hs = DetectHeadAndShoulders(candles, atr);
        if (hs.Score > 0) patterns.Add(hs);

        // ── DOUBLE TOP / DOUBLE BOTTOM (15-bar window) ──
        var dbl = DetectDoubleTopBottom(candles, atr);
        if (dbl.Score > 0) patterns.Add(dbl);

        return patterns.OrderByDescending(p => p.Score).FirstOrDefault() ?? none;
    }

    private PatternSignal DetectHeadAndShoulders(List<Candle> candles, decimal atr)
    {
        var none = new PatternSignal();
        if (candles == null || candles.Count < 11 || atr <= 0) return none;

        // Use a wider window (11 bars) for more reliable detection
        var w = candles.TakeLast(11).ToList();
        var highs = new List<int>();
        var lows = new List<int>();
        for (int i = 1; i < w.Count - 1; i++)
        {
            if (w[i].High > w[i - 1].High && w[i].High > w[i + 1].High) highs.Add(i);
            if (w[i].Low < w[i - 1].Low && w[i].Low < w[i + 1].Low) lows.Add(i);
        }

        if (highs.Count >= 3)
        {
            var h = highs.TakeLast(3).ToList();
            decimal ls = w[h[0]].High;
            decimal head = w[h[1]].High;
            decimal rs = w[h[2]].High;
            decimal shoulderDiff = Math.Abs(ls - rs);
            decimal neckline = Math.Min(
                w.Skip(h[0]).Take(Math.Max(1, h[1] - h[0] + 1)).Min(c => c.Low),
                w.Skip(h[1]).Take(Math.Max(1, h[2] - h[1] + 1)).Min(c => c.Low));
            if (head > ls + atr * 0.30m && head > rs + atr * 0.30m
                && shoulderDiff <= atr * 0.50m && w.Last().Close < neckline)
                return new PatternSignal { Tag = "HEAD_SHOULDERS", Bullish = false, Score = 72, Reason = "head and shoulders neckline break" };
        }

        if (lows.Count >= 3)
        {
            var l = lows.TakeLast(3).ToList();
            decimal ls = w[l[0]].Low;
            decimal head = w[l[1]].Low;
            decimal rs = w[l[2]].Low;
            decimal shoulderDiff = Math.Abs(ls - rs);
            decimal neckline = Math.Max(
                w.Skip(l[0]).Take(Math.Max(1, l[1] - l[0] + 1)).Max(c => c.High),
                w.Skip(l[1]).Take(Math.Max(1, l[2] - l[1] + 1)).Max(c => c.High));
            if (head < ls - atr * 0.30m && head < rs - atr * 0.30m
                && shoulderDiff <= atr * 0.50m && w.Last().Close > neckline)
                return new PatternSignal { Tag = "INV_HEAD_SHOULDERS", Bullish = true, Score = 72, Reason = "inverse head and shoulders neckline break" };
        }

        return none;
    }

    private PatternSignal DetectDoubleTopBottom(List<Candle> candles, decimal atr)
    {
        var none = new PatternSignal();
        if (candles == null || candles.Count < 15 || atr <= 0) return none;

        var w = candles.TakeLast(15).ToList();

        // Double Bottom: two lows within tolerance, with a higher bar between them
        decimal low1 = decimal.MaxValue, low2 = decimal.MaxValue;
        int low1Idx = -1, low2Idx = -1;

        // Find first low in first half
        for (int i = 1; i < 8; i++)
            if (w[i].Low < low1) { low1 = w[i].Low; low1Idx = i; }
        // Find second low in second half
        for (int i = 8; i < w.Count - 1; i++)
            if (w[i].Low < low2) { low2 = w[i].Low; low2Idx = i; }

        if (low1Idx > 0 && low2Idx > 0 && Math.Abs(low1 - low2) <= atr * 0.40m)
        {
            // Must have a meaningful bounce between the two lows
            decimal peakBetween = w.Skip(low1Idx).Take(low2Idx - low1Idx + 1).Max(c => c.High);
            decimal bounceSize = peakBetween - Math.Max(low1, low2);
            if (bounceSize >= atr * 0.5m && w.Last().Close > peakBetween * 0.995m)
                return new PatternSignal { Tag = "DOUBLE_BOTTOM", Bullish = true, Score = 70, Reason = "double bottom neckline break" };
        }

        // Double Top: two highs within tolerance
        decimal high1 = decimal.MinValue, high2 = decimal.MinValue;
        int high1Idx = -1, high2Idx = -1;

        for (int i = 1; i < 8; i++)
            if (w[i].High > high1) { high1 = w[i].High; high1Idx = i; }
        for (int i = 8; i < w.Count - 1; i++)
            if (w[i].High > high2) { high2 = w[i].High; high2Idx = i; }

        if (high1Idx > 0 && high2Idx > 0 && Math.Abs(high1 - high2) <= atr * 0.40m)
        {
            decimal troughBetween = w.Skip(high1Idx).Take(high2Idx - high1Idx + 1).Min(c => c.Low);
            decimal dip = Math.Min(high1, high2) - troughBetween;
            if (dip >= atr * 0.5m && w.Last().Close < troughBetween * 1.005m)
                return new PatternSignal { Tag = "DOUBLE_TOP", Bullish = false, Score = 70, Reason = "double top neckline break" };
        }

        return none;
    }

    private bool HasFastVolumeSurge(List<Candle> candles, decimal mult = 1.30m)
    {
        if (candles == null || candles.Count < 6) return false;
        var recent = candles.TakeLast(6).ToList();
        double baseline = recent.Take(5).Average(c => (double)c.Volume);
        return baseline > 0 && recent.Last().Volume >= baseline * (double)mult;
    }

    private decimal Body(Candle c) => Math.Abs(c.Close - c.Open);
    private decimal UpperWick(Candle c) => c.High - Math.Max(c.Open, c.Close);
    private decimal LowerWick(Candle c) => Math.Min(c.Open, c.Close) - c.Low;

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
            "SELL-OFF" => 6,  // was 0 — shorts and relative-strength longs can work
            _ => 8
        };

        if (_vwap.TryGetValue(symbol, out decimal vwapNow) && vwapNow > 0 && ind.Atr14 > 0)
        {
            if (Math.Abs(lastClose - vwapNow) <= ind.Atr14 * 1.5m)
                score += 10;
        }

        if (HasFastVolumeSurge(candles)) score += 10;

        var pattern = DetectBestPattern(candles);
        if (pattern.Score > 0)
            score += Math.Min(30, Math.Max(10, pattern.Score / 3 + 5));

        return Math.Clamp(score, 0, 100);
    }

    private bool PassesDirectionalQualityGate(string symbol, List<Candle> candles, bool isShort, string strategyTag, decimal price)
    {
        if (IsSwingStrategy(strategyTag)) return true;
        if (candles == null || candles.Count < 30) return false;
        if (!_indicatorCache.TryGetValue(symbol, out var ind)) return false;

        decimal atr = ind.Atr14;
        if (atr <= 0 || price <= 0) return false;

        _vwap.TryGetValue(symbol, out decimal vwapVal);

        bool isReversalFamily = strategyTag.StartsWith("MEAN_REV_", StringComparison.OrdinalIgnoreCase)
                             || strategyTag.StartsWith("BB_MR_", StringComparison.OrdinalIgnoreCase)
                             || strategyTag.StartsWith("NW_BAND_", StringComparison.OrdinalIgnoreCase)
                             || strategyTag.StartsWith("SCALP_PATTERN_", StringComparison.OrdinalIgnoreCase)
                             || strategyTag.StartsWith("SCALP_PULLBACK_", StringComparison.OrdinalIgnoreCase)
                             || strategyTag.StartsWith("OUTSIDE_CANDLE_", StringComparison.OrdinalIgnoreCase);

        if (!isShort)
        {
            // Longs were the largest source of losses. Only take them when the tape
            // is not actively bearish, unless the symbol has clear relative strength.
            if (_spyOpenBearish && !CheckStrongRelativeStrength(symbol, candles)) return false;
            if (_marketRegime == "SELL-OFF" && !CheckStrongRelativeStrength(symbol, candles)) return false;

            if (!isReversalFamily)
            {
                if (REQUIRE_SMA_ALIGNMENT)
                {
                    // Require intraday SMA50 > SMA100 as a loose trend confirmation
                    if (ind.Sma50 <= ind.Sma100) return false;
                }
                if (vwapVal > 0 && price < vwapVal - atr * 0.15m) return false;
                if (ind.Ema9 <= ind.Ema21 && !strategyTag.StartsWith("SCALP_ORB_", StringComparison.OrdinalIgnoreCase)) return false;
                if (ind.Rsi14 < RSI_LONG_MIN - 4) return false;
            }
        }
        else
        {
            if (!_allowShorts) return false;

            // Shorts are enabled, but only when the index/tape agrees. The historical
            // log showed noisy short attempts in NORMAL/TRENDING tape were low quality.
            bool bearishTape = _marketRegime == "SELL-OFF" || _spyBearish || _spyOpenBearish;
            if (SHORT_TAPE_STRICTNESS >= 2)
            {
                // strict: require clear sell-off
                if (!(_marketRegime == "SELL-OFF" && (_spyBearish || _spyOpenBearish))) return false;
            }
            else if (!bearishTape) return false;
            if (vwapVal <= 0 || price > vwapVal + atr * 0.15m) return false;
            if (ind.Ema9 >= ind.Ema21 && _marketRegime != "SELL-OFF") return false;
            if (ind.Rsi14 > RSI_SHORT_MAX + 5) return false;
        }

        return true;
    }

    // ══════════════════════════════════════════════════════════
    //  POSITION OPENING HELPER
    // ══════════════════════════════════════════════════════════

    private bool OpenPosition(string symbol, int qty, decimal price,
                              TradeSide side, bool isShort, string strategyTag)
    {
        if (string.IsNullOrEmpty(symbol) || string.IsNullOrEmpty(strategyTag)) return false;
        bool isNwBand = strategyTag.StartsWith("NW_BAND_", StringComparison.OrdinalIgnoreCase);

        // ── BUG FIX: validate side/isShort/tag consistency ──
        // The trade log showed LONG trades tagged as GAP_GO_SHORT etc.
        // Force consistency: if tag says SHORT, side must be Sell & isShort=true
        if (strategyTag.EndsWith("_SHORT", StringComparison.OrdinalIgnoreCase))
        {
            if (side != TradeSide.Sell || !isShort)
            {
                LogMessage($"[BUG GUARD] {strategyTag} {symbol} tag says SHORT but side={side} isShort={isShort} — fixing");
                side = TradeSide.Sell;
                isShort = true;
            }
        }
        else if (strategyTag.EndsWith("_LONG", StringComparison.OrdinalIgnoreCase))
        {
            if (side != TradeSide.Buy || isShort)
            {
                LogMessage($"[BUG GUARD] {strategyTag} {symbol} tag says LONG but side={side} isShort={isShort} — fixing");
                side = TradeSide.Buy;
                isShort = false;
            }
        }

        if (isShort && !_allowShorts)
        {
            RecordBlock(symbol, "SHORTS_DISABLED");
            LogMessage($"[SHORTS_DISABLED] {strategyTag} {symbol} blocked — shorts disabled");
            return false;
        }
        if (qty <= 0 || qty > MAX_QTY_SANITY)
        {
            RecordBlock(symbol, "QTY_SANITY");
            LogMessage($"[QTY_SANITY] {strategyTag} {symbol} blocked — qty {qty} invalid");
            return false;
        }
        if (_pendingEntrySymbols.ContainsKey(symbol))
        {
            RecordBlock(symbol, "PENDING_ENTRY");
            LogMessage($"[PENDING] {strategyTag} {symbol} blocked — entry already pending");
            return false;
        }

        if (!IsSwingStrategy(strategyTag) && !isNwBand)
        {
            if (!_marketData.TryGetValue(symbol, out var directionCandles) ||
                !PassesDirectionalQualityGate(symbol, directionCandles, isShort, strategyTag, price))
            {
                RecordBlock(symbol, "DIRECTION_GATE");
                LogMessage($"[DIRECTION GATE] {strategyTag} {symbol} blocked — tape/EMA/VWAP not aligned");
                return false;
            }

            DateTime nowEt = GetEasternTime();
            int minutesSinceOpen = (nowEt.Hour - 9) * 60 + nowEt.Minute - 30;
            if (!PassesEnhancedDirectionalGates(symbol, directionCandles, isShort,
                                                strategyTag, price, minutesSinceOpen))
            {
                RecordBlock(symbol, "ENHANCED_DIRECTION_GATE");
                return false;
            }
        }

        // ── RISK MGMT: Strategy allocation limit ──
        // Prevents one misfiring strategy from consuming the full daily budget.
        if (!IsSwingStrategy(strategyTag) && IsStrategyAtDailyLimit(strategyTag))
        {
            RecordBlock(symbol, "STRAT_LIMIT",
                $"{GetStrategyFamily(strategyTag)} cap {MAX_TRADES_PER_STRATEGY}");
            LogMessage($"[STRAT LIMIT] {strategyTag} {symbol} blocked — {GetStrategyFamily(strategyTag)} hit {MAX_TRADES_PER_STRATEGY} trades today");
            return false;
        }

        if (!IsSwingStrategy(strategyTag) && !isNwBand && IsStrategyCold(strategyTag))
        {
            RecordBlock(symbol, "STRAT_COOLING");
            LogMessage($"[STRAT COOLING] {strategyTag} {symbol} blocked — recent real-trade performance is negative");
            return false;
        }

        // ── RISK MGMT: Trend reversal gate ──
        // Only applied to trend-following strategies. Reversal strategies (PATTERN_*,
        // MICRO_PB_*, MEAN_REV_*, BB_MR_*, NW_BAND_*) are EXEMPT because they
        // intentionally enter against exhausted moves — blocking them would destroy
        // early-entry edge.
        bool isTrendFollowing = !IsSwingStrategy(strategyTag)
                             && !strategyTag.StartsWith("PATTERN_", StringComparison.OrdinalIgnoreCase)
                             && !strategyTag.StartsWith("SCALP_PATTERN_", StringComparison.OrdinalIgnoreCase)
                             && !strategyTag.StartsWith("MICRO_PB_", StringComparison.OrdinalIgnoreCase)
                             && !strategyTag.StartsWith("SCALP_PULLBACK_", StringComparison.OrdinalIgnoreCase)
                             && !strategyTag.StartsWith("MEAN_REV_", StringComparison.OrdinalIgnoreCase)
                             && !strategyTag.StartsWith("BB_MR_", StringComparison.OrdinalIgnoreCase)
                             && !strategyTag.StartsWith("NW_BAND_", StringComparison.OrdinalIgnoreCase)
                             && !strategyTag.StartsWith("OUTSIDE_CANDLE_", StringComparison.OrdinalIgnoreCase);
        if (isTrendFollowing && _marketData.TryGetValue(symbol, out var trendCandles))
        {
            if (IsTrendReversing(symbol, isShort, trendCandles))
            {
                LogMessage($"[TREND GATE] {strategyTag} {symbol} blocked — trend structure reversing against {(isShort ? "SHORT" : "LONG")} entry");
                return false;
            }
        }

        if (!isNwBand && symbol != "SPY" && symbol != "QQQ" && symbol != "IWM")
        {
            if (!isShort && _spyBearish)
            {
                _marketData.TryGetValue(symbol, out var gateCandles);
                if (gateCandles == null || !CheckStrongRelativeStrength(symbol, gateCandles))
                {
                    RecordBlock(symbol, "SPY_GATE_LONG");
                    LogMessage($"[SPY GATE] {strategyTag} {symbol} LONG blocked — SPY EMA20 bearish");
                    return false;
                }
                LogMessage($"[SPY GATE] {strategyTag} {symbol} LONG ALLOWED despite bearish SPY — strong relative strength");
            }
            if (isShort && _spyBullish)
            {
                RecordBlock(symbol, "SPY_GATE_SHORT");
                LogMessage($"[SPY GATE] {strategyTag} {symbol} SHORT blocked — SPY EMA20 bullish");
                return false;
            }
        }

        if (_symbolSectors.TryGetValue(symbol, out string newSector))
        {
            foreach (var openPos in _positions.Values)
            {
                if (_symbolSectors.TryGetValue(openPos.Symbol, out string existingSector)
                    && existingSector == newSector)
                {
                    RecordBlock(symbol, "SECTOR_GATE", $"Sector {newSector}: already holding {openPos.Symbol}");
                    LogMessage($"[SECTOR GATE] {strategyTag} {symbol} blocked — already have {openPos.Symbol} in sector [{newSector}]");
                    return false;
                }
            }

            foreach (string pendingSymbol in _pendingEntrySymbols.Keys)
            {
                if (string.Equals(pendingSymbol, symbol, StringComparison.OrdinalIgnoreCase)) continue;
                if (_symbolSectors.TryGetValue(pendingSymbol, out string pendingSector)
                    && pendingSector == newSector)
                {
                    RecordBlock(symbol, "SECTOR_GATE", $"Sector {newSector}: pending {pendingSymbol}");
                    LogMessage($"[SECTOR GATE] {strategyTag} {symbol} blocked — {pendingSymbol} entry already pending in sector [{newSector}]");
                    return false;
                }
            }
        }

        decimal nwUpperAtEntry = 0m;
        if (isNwBand && !isShort)
        {
            var (_, nwUpper, _, nwBars) = GetNadarayaWatson1HourEnvelope(symbol);
            if (nwBars < NW_LOOKBACK || nwUpper <= price)
            {
                RecordBlock(symbol, "NW_TARGET_NOT_READY", "NW upper target unavailable/not above entry");
                LogMessage($"[NW {NW_TIMEFRAME_MINUTES}M] {symbol} entry blocked — upper band unavailable or not above price.");
                return false;
            }
            nwUpperAtEntry = nwUpper;
        }

        decimal stopDist = MIN_STOP_DISTANCE;
        if (_marketData.TryGetValue(symbol, out var rrCandles))
        {
            decimal atrForRR = SafeATR(rrCandles, 14);
            stopDist = GetStopDistanceForStrategy(strategyTag, atrForRR, price);

            // Apply the key-level stop adjustment before every remaining guard
            // and before committing a pending-entry slot. Previously this ran
            // after stopTrigger/target/risk were calculated, so the order still
            // used the old stop and a failed resized quantity left trade counters
            // incremented even though no order was submitted.
            if (RealBroker != null && RealBroker.SupportsBrackets
                && IsStopPlacedAtKeyLevel(symbol, price, stopDist, isShort))
            {
                stopDist *= 1.15m;
                qty = CalcQtyV2(price, stopDist);
                if (qty <= 0)
                {
                    RecordBlock(symbol, "STOP_LEVEL_QTY");
                    return false;
                }
            }

            if (!IsSwingStrategy(strategyTag))
            {
                _vwap.TryGetValue(symbol, out decimal vwapRR);
                var (pdHighRR, pdLowRR) = GetPrevDayHL(symbol);

                decimal targetDist;
                if (isNwBand && !isShort && nwUpperAtEntry > price)
                {
                    // NW trades are explicitly lower-band -> upper-band trades.
                    // Use the actual 1H upper envelope for R:R, not VWAP/ATR proxies.
                    targetDist = nwUpperAtEntry - price;
                }
                else if (!isShort)
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

                decimal requiredRr = IsScalpStrategy(strategyTag) ? 1.25m : MIN_RR_RATIO;
                if (targetDist < stopDist * requiredRr)
                {
                    decimal actualRr = targetDist / stopDist;
                    RecordBlock(symbol, "RR_SKIP", $"R:R {actualRr:F2} < {requiredRr:F2}");
                    LogMessage($"[RR SKIP] {strategyTag} {symbol} R:R={actualRr:F2} < {requiredRr:F2} — target too close to stop, skipping");
                    return false;
                }
            }
        }

        if (!isShort && _latestTick.TryGetValue("VIX", out decimal vixLevel) && vixLevel > 0)
        {
            if (vixLevel >= VIX_NO_LONG_THRESHOLD)
            {
                RecordBlock(symbol, "VIX_BLOCK", $"VIX {vixLevel:F1} >= {VIX_NO_LONG_THRESHOLD:F1}");
                LogMessage($"[VIX BLOCK] {strategyTag} {symbol} LONG blocked — VIX={vixLevel:F1} ≥ {VIX_NO_LONG_THRESHOLD}");
                return false;
            }
            if (vixLevel >= VIX_REDUCE_THRESHOLD)
            {
                qty = Math.Max(1, qty / 2);
                LogMessage($"[VIX REDUCE] {strategyTag} {symbol} qty halved to {qty} — VIX={vixLevel:F1} ≥ {VIX_REDUCE_THRESHOLD}");
            }
        }

        int minEntryQty = GetMinEntryQtyForPrice(price);
        if (qty < minEntryQty)
        {
            RecordBlock(symbol, "ECON_MIN_QTY", $"Qty {qty} < minimum {minEntryQty}");
            LogMessage($"[ECON FILTER] {strategyTag} {symbol} blocked — qty {qty} < minimum {minEntryQty} for ${price:F2}");
            return false;
        }

        decimal targetMultiple = GetTargetMultipleForStrategy(strategyTag);
        decimal grossTargetPnL = isNwBand && !isShort && nwUpperAtEntry > price
            ? (nwUpperAtEntry - price) * qty
            : stopDist * targetMultiple * qty;
        decimal minGrossTargetPnL = COMMISSION_PER_SIDE * 2m * MIN_GROSS_TARGET_TO_COMMISSION_MULT;
        if (grossTargetPnL < minGrossTargetPnL)
        {
            RecordBlock(symbol, "ECON_GROSS_TARGET",
                $"Gross target ${grossTargetPnL:F2} < ${minGrossTargetPnL:F2}");
            LogMessage($"[ECON FILTER] {strategyTag} {symbol} blocked — gross target ${grossTargetPnL:F2} < required ${minGrossTargetPnL:F2}");
            return false;
        }

        // ── ALL GUARDS PASSED — now decide the ACTUAL transaction side ──
        // All signal generation, quality gates, sector gates, RR gates, strategy limits,
        // and cooldown logic above still run exactly as before. Only the submitted
        // entry side changes when INVERT_ENTRY_DIRECTION is enabled.

        TradeSide executionSide = side;
        bool executionIsShort = isShort;

        // ── COMMIT TO THE ENTRY ──
        // Tag + risk are set AFTER all guards so they can't be polluted
        // by a blocked attempt that returns early.
        if (_pendingStrategyTag == null)
            _pendingStrategyTag = new ConcurrentDictionary<string, string>();
        _pendingStrategyTag[symbol] = strategyTag;
        _pendingInitialRisk[symbol] = stopDist;

        DateTime entryAttemptUtc = DateTime.UtcNow;
        _pendingEntrySymbols[symbol] = true;
        _pendingEntryCreatedUtc[symbol] = entryAttemptUtc;
        Interlocked.Increment(ref _pendingEntryCount);
        lock (_lock)
        {
            _dailyEntryCount[symbol] = _dailyEntryCount.GetValueOrDefault(symbol) + 1;
            _recentEntryTimesUtc.Enqueue(entryAttemptUtc);
            GetRollingHourEntryCountLocked(entryAttemptUtc);
            DateTime entryEt = GetEasternTime();
            _currentTradeHour = new DateTime(
                entryEt.Year, entryEt.Month, entryEt.Day, entryEt.Hour, 0, 0);
        }

        bool usedBracket = false;
        if (RealBroker != null && RealBroker.SupportsBrackets)
        {
            _marketData.TryGetValue(symbol, out var bracketCandles);
            decimal atrBracket = SafeATR(bracketCandles, 14);

            decimal stopTrigger = executionIsShort
                ? price + stopDist
                : price - stopDist;
            decimal stopLimitPrice = executionIsShort
                ? stopTrigger + atrBracket * 0.5m
                : stopTrigger - atrBracket * 0.5m;

            // NW exits are dynamic: the upper band is recalculated from the
            // newest completed timeframe bar. Pass targetPrice=0 so IbClient creates
            // a parent + protective stop only; CheckExits() owns the upper-band sell.
            decimal targetPrice = isNwBand
                ? 0m
                : executionIsShort
                    ? price - stopDist * targetMultiple
                    : price + stopDist * targetMultiple;

            Func<decimal, decimal> tickRound = p => p >= 1.0m
                ? Math.Round(p, 2, MidpointRounding.AwayFromZero)
                : Math.Round(p, 4, MidpointRounding.AwayFromZero);

            decimal entryAdj = tickRound(price + (executionSide == TradeSide.Buy
                ? Math.Max(atrBracket * 0.1m, price * 0.0005m)
                : -Math.Max(atrBracket * 0.1m, price * 0.0005m)));

            RealBroker.SubmitBracketOrder(
                symbol, qty,
                entryAdj, executionSide,
                tickRound(stopTrigger), tickRound(stopLimitPrice),
                tickRound(targetPrice));

            string bracketTargetText = isNwBand ? $"NW_{NW_TIMEFRAME_MINUTES}M_DYNAMIC" : $"{tickRound(targetPrice):F2}";
            LogMessage($"[BRACKET] {strategyTag} {symbol} x{qty} execution={(executionIsShort ? "SHORT" : "LONG")} entry={entryAdj:F2} " +
                       $"stop={tickRound(stopTrigger):F2}/{tickRound(stopLimitPrice):F2} " +
                       $"target={bracketTargetText}");
            usedBracket = true;
        }

        if (!usedBracket)
        {
            SubmitOrder(symbol, qty, price, executionSide, strategyTag);
        }

        LogMessage($"[{strategyTag}] {symbol} x{qty} @ {price:F2} | execution={(executionIsShort ? "SHORT" : "LONG")} | regime={_marketRegime} | bracket={usedBracket}");
        return true;
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
            decimal plannedStopPerShare = pos.InitialRiskPerShare > 0
                ? pos.InitialRiskPerShare
                : GetStopDistanceForStrategy(pos.StrategyTag ?? "", atrValue, pos.AvgPrice);

            decimal plannedDollarRisk = pos.Quantity * plannedStopPerShare;
            decimal dynamicDollarStop = MAX_LOSS_PER_TRADE > 0
                ? Math.Min(MAX_LOSS_PER_TRADE, plannedDollarRisk)
                : plannedDollarRisk;
            bool dollarStopHit = unrealizedLoss <= -dynamicDollarStop;

            bool atrStopHit = false;
            if (pos.IsShort)
            {
                decimal shortHardStop = pos.AvgPrice + plannedStopPerShare;
                atrStopHit = currentPrice > shortHardStop;
            }
            else
            {
                decimal longHardStop = pos.AvgPrice - plannedStopPerShare;
                atrStopHit = currentPrice < longHardStop;
            }
            bool isNwBand = (pos.StrategyTag ?? "").StartsWith("NW_BAND_", StringComparison.OrdinalIgnoreCase);
            bool beStopHit = !isNwBand && IsBreakevenStopHit(pos, currentPrice);

            if (atrStopHit || dollarStopHit || beStopHit)
            {
                pos.ExitSubmitted = true;
                string reason = dollarStopHit ? "MAX_LOSS_STOP"
                              : beStopHit ? "BREAKEVEN_STOP"
                              : "HARD_STOP";
                TradeSide exitSide = pos.IsShort ? TradeSide.Buy : TradeSide.Sell;

                CancelBracketChildren(pos);

                // Hard stops are protection, not price improvement. Use MKT so a local
                // emergency exit cannot sit unfilled while ExitSubmitted=true blocks retries.
                SubmitOrder(symbol, pos.Quantity, currentPrice, exitSide, reason, "MKT");
            }
        }
    }

    private void CheckExits(string symbol, decimal currentPrice)
    {
        // Compute NW bands outside _lock. GetNadarayaWatson1HourEnvelope() locks
        // the hourly-bar list, while FinalizeCandle() updates that list before
        // it enters strategy code that may take _lock; reversing that order here
        // would create a lock-order deadlock.
        bool needsNwBands = false;
        lock (_lock)
        {
            if (_positions.TryGetValue(symbol, out var probePos))
                needsNwBands = (probePos.StrategyTag ?? "").StartsWith("NW_BAND_", StringComparison.OrdinalIgnoreCase);
        }

        var nwExitEnvelope = needsNwBands
            ? GetNadarayaWatson1HourEnvelope(symbol)
            : (mid: 0m, upper: 0m, lower: 0m, bars: 0);

        lock (_lock)
        {
            if (!_positions.TryGetValue(symbol, out var pos)) return;
            if (!_marketData.TryGetValue(symbol, out var candles)) return;
            if (pos.ExitSubmitted) return;

            TradeSide exitSide = pos.IsShort ? TradeSide.Buy : TradeSide.Sell;

            // NW upper-band exit has priority over the generic minimum-hold/partial/trailing
            // logic. This is the requested rule: BUY at the configured lower envelope and
            // SELL the long as soon as live price touches/crosses the upper envelope.
            bool isNwPosition = (pos.StrategyTag ?? "").StartsWith("NW_BAND_", StringComparison.OrdinalIgnoreCase);
            if (isNwPosition)
            {
                // NW positions have exactly two strategy exits:
                //   1) the protective NW_STOP_LOSS_PCT stop (handled in CheckHardStop / IBKR stop child), and
                //   2) a live touch/cross of the completed opposite envelope here.
                // Generic time stops, partial exits, regime exits and trailing/breakeven exits are
                // deliberately skipped so they cannot sell before the requested NW upper band.
                if (nwExitEnvelope.bars < NW_LOOKBACK || nwExitEnvelope.upper <= 0 || nwExitEnvelope.lower <= 0)
                    return;

                bool nwExitHit = !pos.IsShort
                    ? currentPrice >= nwExitEnvelope.upper
                    : currentPrice <= nwExitEnvelope.lower;

                if (nwExitHit)
                {
                    CancelBracketChildren(pos);
                    pos.ExitSubmitted = true;
                    string reason = pos.IsShort
                        ? $"NW_{NW_TIMEFRAME_MINUTES}M_LOWER_EXIT"
                        : $"NW_{NW_TIMEFRAME_MINUTES}M_UPPER_EXIT";
                    LogMessage($"[{reason}] {symbol} price={currentPrice:F2} | lower={nwExitEnvelope.lower:F2} upper={nwExitEnvelope.upper:F2}");
                    SubmitOrder(symbol, pos.Quantity, currentPrice, exitSide, reason, "MKT");
                }
                return;
            }

            double secondsHeld = (DateTime.UtcNow - pos.EntryTime).TotalSeconds;
            if (secondsHeld < MIN_HOLD_SECONDS) return;

            decimal atrValue = SafeATR(candles, 14);

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
            bool isScalp = IsScalpStrategy(pos.StrategyTag ?? "");
            bool isSwing = IsSwingStrategy(pos.StrategyTag ?? "");
            int maxHoldSeconds = GetMaxHoldSecondsForStrategy(pos.StrategyTag ?? "");

            if (isScalp)
            {
                if (secondsHeld >= GetScratchSecondsForStrategy(pos.StrategyTag ?? "") && rMultiple <= GetScratchRForStrategy(pos.StrategyTag ?? ""))
                {
                    CancelBracketChildren(pos);
                    pos.ExitSubmitted = true;
                    SubmitOrder(symbol, pos.Quantity, currentPrice, exitSide, "SCALP_SCRATCH_EXIT", "MKT");
                    return;
                }

                // V2 FIX: 150s (2.5min) was too short — many trades just need time to develop.
                // Now 480s (8min) with lower R threshold (0.15R via GetStaleR).
                if (secondsHeld >= 480 && rMultiple < GetStaleRForStrategy(pos.StrategyTag ?? ""))
                {
                    CancelBracketChildren(pos);
                    pos.ExitSubmitted = true;
                    SubmitOrder(symbol, pos.Quantity, currentPrice, exitSide, "SCALP_STALE_EXIT", "MKT");
                    return;
                }

                if (secondsHeld >= maxHoldSeconds)
                {
                    CancelBracketChildren(pos);
                    pos.ExitSubmitted = true;
                    SubmitOrder(symbol, pos.Quantity, currentPrice, exitSide, "SCALP_TIME_STOP", "MKT");
                    return;
                }
            }

            // V2 FIX: Extended from 3600s (1hr) to 5400s (1.5hr) to let momentum trades develop
            if (!isSwing && secondsHeld > 5400 && rMultiple < 0.30m)
            {
                CancelBracketChildren(pos);
                pos.ExitSubmitted = true;
                SubmitOrder(symbol, pos.Quantity, currentPrice, exitSide, "TIME_STOP");
                return;
            }

            if (isSwing)
            {
                if (secondsHeld >= maxHoldSeconds)
                {
                    CancelBracketChildren(pos);
                    pos.ExitSubmitted = true;
                    SubmitOrder(symbol, pos.Quantity, currentPrice, exitSide, "SWING_TIME_STOP", "MKT");
                    return;
                }

                var swingDaily = GetDailyBarsPreferLive(symbol);
                if (swingDaily != null && swingDaily.Count >= 20)
                {
                    decimal dSma20 = SafeSMA(swingDaily, 20);
                    decimal dSma50 = swingDaily.Count >= 50 ? SafeSMA(swingDaily, 50) : 0m;

                    if (!pos.IsShort && secondsHeld >= 86400 && dSma20 > 0 && currentPrice < dSma20)
                    {
                        CancelBracketChildren(pos);
                        pos.ExitSubmitted = true;
                        SubmitOrder(symbol, pos.Quantity, currentPrice, exitSide, "SWING_LOST_20DMA", "MKT");
                        return;
                    }

                    if (!pos.IsShort && rMultiple < -0.50m && dSma50 > 0 && currentPrice < dSma50)
                    {
                        CancelBracketChildren(pos);
                        pos.ExitSubmitted = true;
                        SubmitOrder(symbol, pos.Quantity, currentPrice, exitSide, "SWING_LOST_50DMA", "MKT");
                        return;
                    }
                }
            }

            // ── FIX: Regime-aware exit acceleration ──
            // V2 FIX: Original fired at rMultiple < 0 — any tiny drawdown during a brief
            // regime shift caused premature exits (-$19 from 4 trades). Now requires:
            // 1. Deeper drawdown (-0.5R, not 0R) — confirms the trade is actually failing
            // 2. Longer hold (600s, not MIN_HOLD) — gives regime time to stabilize
            if (rMultiple < -0.50m && secondsHeld >= 600)
            {
                bool regimeAgainstLong = !pos.IsShort && (_marketRegime == "SELL-OFF");
                bool regimeAgainstShort = pos.IsShort && (_marketRegime == "TRENDING") && _spyBullish;
                if (regimeAgainstLong || regimeAgainstShort)
                {
                    CancelBracketChildren(pos);
                    pos.ExitSubmitted = true;
                    string reason = regimeAgainstLong ? "REGIME_EXIT_SELLOFF" : "REGIME_EXIT_TRENDING";
                    SubmitOrder(symbol, pos.Quantity, currentPrice, exitSide, reason);
                    LogMessage($"[{reason}] {symbol} exit — underwater ({rMultiple:F2}R) and regime turned against position");
                    return;
                }
            }

            if (bracketActive) return;

            if (isSwing)
            {
                if (rMultiple >= 1.80m && !pos.PartialExitDone && pos.Quantity >= 2)
                {
                    int halfQty = Math.Max(1, pos.Quantity / 2);
                    pos.PartialExitDone = true;
                    SubmitOrder(symbol, halfQty, currentPrice, exitSide, "SWING_PARTIAL_TP_1");
                    return;
                }

                decimal swingTrailDist = Math.Max(oneR * 0.90m, atrValue * 1.50m);
                bool swingTrailHit = pos.IsShort
                    ? currentPrice > pos.HighWaterMark + swingTrailDist && rMultiple > 1.50m
                    : currentPrice < pos.HighWaterMark - swingTrailDist && rMultiple > 1.50m;

                if (swingTrailHit)
                {
                    pos.ExitSubmitted = true;
                    SubmitOrder(symbol, pos.Quantity, currentPrice, exitSide, "SWING_TRAIL_STOP");
                    return;
                }
            }

            // V2 FIX: Take first partial later (1.5R vs 1.25R) so winners have room to build.
            // Original partials at 1.25R locked in tiny gains that didn't cover commissions.
            if (rMultiple >= 1.50m && !pos.PartialExitDone && pos.Quantity >= 2)
            {
                int halfQty = pos.Quantity / 2;
                pos.PartialExitDone = true;
                SubmitOrder(symbol, halfQty, currentPrice, exitSide, "PARTIAL_TP_1");
                return;
            }

            if (pos.PartialExitDone && !pos.ExitSubmitted)
            {
                if (rMultiple >= 2.0m)
                    pos.PartialExitDone2 = true;

                // V2 FIX: Final target at 2.8R (was 2.4R) — let the runner run.
                // Give-back trailing at 1.2R (was 1.0R) — protect more profit.
                bool takeAtFinal = rMultiple >= 2.8m;
                bool givebackTo1R = pos.PartialExitDone2 && rMultiple <= 1.2m;

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
                // V2 FIX: Only trail at 1.5R (was 1.2R) — give the trade room before trailing
                trailHit = currentPrice > trailStop && rMultiple > 1.5m;
            }
            else
            {
                decimal trailStop = pos.HighWaterMark - atrValue * trailMult;
                trailHit = currentPrice < trailStop && rMultiple > 1.5m;
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

        bool isExitOrder = false;
        lock (_lock)
        {
            if (_positions.TryGetValue(symbol, out var pos))
            {
                isExitOrder = (pos.IsShort && side == TradeSide.Buy) || (!pos.IsShort && side == TradeSide.Sell);
            }
        }
        if (isExitOrder)
            _pendingExitReasonBySymbol[symbol] = note;

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
    {
        _pendingBracketChildren[symbol] = (stopId, targetId);
        if (stopId > 0)
            _bracketExitReasonByOrderId[stopId] = "BRACKET_STOP";
        if (targetId > 0)
            _bracketExitReasonByOrderId[targetId] = "BRACKET_TARGET";
    }

    public void OnOrderRejected(int orderId)
    {
        if (!_ordersById.TryRemove(orderId, out var order)) return;

        bool wasExit = false;
        lock (_lock)
        {
            if (_positions.TryGetValue(order.Symbol, out var pos))
            {
                wasExit = (pos.IsShort && order.Side == TradeSide.Buy) || (!pos.IsShort && order.Side == TradeSide.Sell);
                if (wasExit)
                {
                    // Let the next tick retry the protective exit. Otherwise one rejected
                    // stop/market order can leave the position unmanaged forever.
                    pos.ExitSubmitted = false;
                    _pendingExitReasonBySymbol.TryRemove(order.Symbol, out _);
                }
            }
        }

        if (!wasExit)
            ReleasePendingEntrySlot(order.Symbol, $"IBKR rejected orderId={orderId}");

        LogMessage($"[REJECTED] orderId={orderId} {order.Side} {order.Symbol} x{order.Qty} — {(wasExit ? "exit retry enabled" : "entry slot freed")}.");
        _ = SendEmail($"⚠️ Order Rejected: {order.Symbol}",
            $"IBKR rejected orderId={orderId} {order.Side} {order.Symbol} x{order.Qty}. Check errors.log.");
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

            // ── FIX: Phantom entry guard ──
            // If this looks like an entry but there's no pending strategy tag, it's an
            // orphaned bracket fill (stop/target that survived after manual sell or EOD).
            // Reject it rather than creating a ghost position with empty StrategyTag.
            if ((isLongEntry || isShortEntry) && !_pendingEntrySymbols.ContainsKey(order.Symbol))
            {
                // No OpenPosition() ever queued this symbol — it's a phantom.
                LogMessage($"[PHANTOM GUARD] Rejected orphaned fill: {order.Side} {order.Symbol} x{fillQty} @ {fillPrice:F2} orderId={orderId} — no pending entry exists.");
                _ = SendEmail($"⚠️ Phantom Fill Blocked: {order.Symbol}",
                    $"Orphaned {order.Side} fill for {order.Symbol} x{fillQty} @ {fillPrice:C2} was blocked from creating a ghost position.");
                return;
            }

            if (isLongEntry || isShortEntry)
            {
                Interlocked.Decrement(ref _pendingEntryCount);
                _pendingEntrySymbols.TryRemove(order.Symbol, out _);
                _pendingEntryCreatedUtc.TryRemove(order.Symbol, out _);

                string tag = "";
                decimal initialRisk = 0m;
                _pendingStrategyTag?.TryRemove(order.Symbol, out tag);
                _pendingInitialRisk?.TryRemove(order.Symbol, out initialRisk);
                string resolvedTag = tag ?? "";
                _indicatorCache.TryGetValue(order.Symbol, out var entryIndicators);
                _vwap.TryGetValue(order.Symbol, out decimal entryVwap);
                _marketData.TryGetValue(order.Symbol, out var entryCandles);

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
                    EntryRegime = _marketRegime,
                    EntryCommission = COMMISSION_PER_SIDE,
                    InitialRiskPerShare = initialRisk,
                    EntryRsi = entryIndicators?.Rsi14 ?? 0d,
                    EntryAtr = entryIndicators?.Atr14 ?? 0m,
                    EntryVwap = entryVwap,
                    EntrySetupScore = ScoreSetup(order.Symbol, entryCandles)
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
                IncrementStrategyCount(resolvedTag);

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
                if (_bracketExitReasonByOrderId.TryRemove(orderId, out var bracketReason) && !string.IsNullOrWhiteSpace(bracketReason))
                {
                    exitReason = bracketReason;
                }
                else if (_pendingExitReasonBySymbol.TryRemove(order.Symbol, out var pendingReason) && !string.IsNullOrWhiteSpace(pendingReason))
                {
                    exitReason = pendingReason;
                }
                else
                {
                    foreach (var tag in new[]{ "ATR_TRAIL_EXIT","TIME_STOP","HARD_STOP",
                                               "MAX_LOSS_STOP","PARTIAL_TP_1","PARTIAL_TP_2","TRAIL_BACK_TO_1R",
                                               "SCALP_SCRATCH_EXIT","SCALP_STALE_EXIT","SCALP_TIME_STOP","EOD_LIQUIDATE" })
                        if (_tradeHistoryLog.LastOrDefault()?.Contains(tag) == true)
                        { exitReason = tag; break; }
                }

                decimal recordedNetPnl = isFullClose
                    ? netPnl - pos.EntryCommission
                    : netPnl;
                DateTime exitUtc = DateTime.UtcNow;
                DateTime entryEt = TimeZoneInfo.ConvertTimeFromUtc(
                    pos.EntryTime.Kind == DateTimeKind.Utc ? pos.EntryTime : pos.EntryTime.ToUniversalTime(),
                    Eastern);
                DateTime exitEt = TimeZoneInfo.ConvertTimeFromUtc(exitUtc, Eastern);

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
                    Time = exitEt.ToString("HH:mm"),
                    EntryTime = entryEt.ToString("HH:mm:ss"),
                    ExitTime = exitEt.ToString("HH:mm:ss"),
                    Date = exitEt.Date.ToString("yyyy-MM-dd"),
                    Regime = string.IsNullOrWhiteSpace(pos.EntryRegime) ? _marketRegime : pos.EntryRegime,
                    EntryRsi = pos.EntryRsi,
                    EntryAtr = pos.EntryAtr,
                    EntryVwap = pos.EntryVwap,
                    EntrySetupScore = pos.EntrySetupScore
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
                    Time = exitEt.ToString("HH:mm"),
                    EntryTime = entryEt.ToString("HH:mm:ss"),
                    ExitTime = exitEt.ToString("HH:mm:ss"),
                    Date = exitEt.Date.ToString("yyyy-MM-dd"),
                    Regime = string.IsNullOrWhiteSpace(pos.EntryRegime) ? _marketRegime : pos.EntryRegime,
                    EntryRsi = pos.EntryRsi,
                    EntryAtr = pos.EntryAtr,
                    EntryVwap = pos.EntryVwap,
                    EntrySetupScore = pos.EntrySetupScore
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
                            _haltReason = "CONSECUTIVE_LOSSES";
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
                    _lastTradeWasLoss[order.Symbol] = recordedNetPnl <= 0;
                }

                subject = $"💰 {(isFullClose ? "CLOSE" : "PARTIAL")}: {order.Symbol} x{fillQty} @ {fillPrice:C2} | Net: {recordedNetPnl:C2}";
                body = $"{(isFullClose ? "Closed" : "Partial")} {fillQty} @ {fillPrice:C2}\nGross: {grossPnl:C2}  Commission: -{(isFullClose ? COMMISSION_PER_SIDE * 2 : COMMISSION_PER_SIDE):C0}  Net: {recordedNetPnl:C2}\nStrategy: {pos.StrategyTag}";
            }

            string arrow = order.Side == TradeSide.Buy ? "▲" : "▼";
            string detail = string.IsNullOrWhiteSpace(subject) ? "" : $" | {subject}";
            string logLine = $"[{DateTime.UtcNow:HH:mm:ss}] {arrow} {order.Side,-4} {order.Symbol,-5} x{fillQty,-4} @ {fillPrice:C2}{detail}";
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
        if (_manualResumeOverride) return;

        if (_totalRealizedPnL >= DAILY_PROFIT_GOAL || _totalRealizedPnL <= MAX_DAILY_LOSS)
        {
            bool newlyHalted = !_haltTrading;
            _haltTrading = true;
            if (_haltReason != "CONSECUTIVE_LOSSES" && _haltReason != "EOD")
                _haltReason = "DAILY_LIMIT";
            string status = _totalRealizedPnL > 0 ? "GOAL REACHED ✅" : "MAX LOSS HIT 🛑";
            if (newlyHalted)
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
        _dailyVolumeAuthoritative.Clear();
        _vwapAccum.Clear();
        _vwap.Clear();
        _orbRanges.Clear();
        _dailyGapPct.Clear();
        _prevBarAboveVwap.Clear();
        _dailyEntryCount.Clear();
        _lastTradeWasLoss.Clear();
        _earningsBlacklist.Clear();
        _strategyTradeCount.Clear();
        _lastNwDecisionBySymbol.Clear();
        _lastNwTouchDecisionBySymbol.Clear();
        _pendingEntrySymbols.Clear();
        _pendingEntryCreatedUtc.Clear();
        _pendingStrategyTag?.Clear();
        _pendingInitialRisk.Clear();
        _pendingBracketChildren.Clear();
        _pendingEntryCount = 0;
        _completedTrades.Clear();
        _lastVolumeResetEt = nowEt.Date;
        _eodSent = false;
        _haltTrading = false;
        _haltReason = "";
        _manualResumeOverride = false;
        _tradesToday = 0;
        _tradesThisHour = 0;
        _currentTradeHour = DateTime.MinValue;
        _recentEntryTimesUtc.Clear();
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
            _haltReason = "EOD";
            _eodSent = true;

            SnapshotLifetimeEquity();

            if (EOD_LIQUIDATE_ENABLED)
            {
                foreach (var p in _positions.Values.ToList())
                {
                    CancelBracketChildren(p);  // FIX: prevent orphaned bracket fills creating ghost entries overnight
                    TradeSide exitSide = p.IsShort ? TradeSide.Buy : TradeSide.Sell;
                    SubmitOrder(p.Symbol, p.Quantity, 0, exitSide, "EOD_LIQUIDATE", "MKT");
                }
            }

            int total = _winCount + _lossCount;
            double winRate = total > 0 ? (double)_winCount / total * 100 : 0;
            string report =
                $"EOD PnL   : {_totalRealizedPnL:C2}\n" +
                $"Trades    : {_tradesToday}\n" +
                $"Win Rate  : {winRate:F1}% ({_winCount}W / {_lossCount}L)\n" +
                $"Overnight : {(EOD_LIQUIDATE_ENABLED ? "disabled" : $"{_positions.Count} position(s) may remain open")}\n\n" +
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
            List<DateTime> recentEntryTimes;
            lock (_lock)
            {
                GetRollingHourEntryCountLocked(DateTime.UtcNow);
                recentEntryTimes = _recentEntryTimesUtc.ToList();
            }

            var json = JsonSerializer.Serialize(new BotPersistData
            {
                Positions = _positions,
                TotalPnL = _totalRealizedPnL,
                WinCount = _winCount,
                LossCount = _lossCount,
                TradesToday = _tradesToday,
                HaltTrading = _haltTrading,
                HaltReason = _haltReason,
                ConsecutiveLosses = _consecutiveLosses,
                LastTradeTime = _lastTradeTime,
                LastTradeWasLoss = _lastTradeWasLoss,
                DailyEntryCount = _dailyEntryCount,
                TradesThisHour = _tradesThisHour,
                TradeHourSlot = _currentTradeHour,
                RecentEntryTimesUtc = recentEntryTimes,
                StrategyTradeCount = _strategyTradeCount.ToDictionary(kv => kv.Key, kv => kv.Value),
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
            _haltReason = data.HaltReason ?? "";

            foreach (var kv in data.LastTradeTime) _lastTradeTime[kv.Key] = kv.Value;
            foreach (var kv in data.LastTradeWasLoss) _lastTradeWasLoss[kv.Key] = kv.Value;
            foreach (var kv in data.DailyEntryCount) _dailyEntryCount[kv.Key] = kv.Value;
            lock (_lock)
            {
                _recentEntryTimesUtc.Clear();
                foreach (DateTime entryTime in data.RecentEntryTimesUtc ?? new List<DateTime>())
                {
                    DateTime utc = entryTime.Kind == DateTimeKind.Utc
                        ? entryTime
                        : entryTime.ToUniversalTime();
                    if (utc > DateTime.UtcNow.AddHours(-1) && utc <= DateTime.UtcNow.AddMinutes(1))
                        _recentEntryTimesUtc.Enqueue(utc);
                }

                // One-time compatibility with pre-queue state files: entries
                // recorded in the current ET calendar hour are certainly still
                // inside the rolling window, so retain that conservative count.
                if (_recentEntryTimesUtc.Count == 0
                    && data.TradesThisHour > 0
                    && data.TradeHourSlot != DateTime.MinValue)
                {
                    DateTime nowEt = GetEasternTime();
                    DateTime currentEtHour = new DateTime(
                        nowEt.Year, nowEt.Month, nowEt.Day, nowEt.Hour, 0, 0);
                    if (data.TradeHourSlot == currentEtHour)
                    {
                        for (int i = 0; i < data.TradesThisHour; i++)
                            _recentEntryTimesUtc.Enqueue(DateTime.UtcNow);
                    }
                }
                GetRollingHourEntryCountLocked(DateTime.UtcNow);
            }
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

            int reconstructedLosses = 0;
            foreach (var t in _completedTrades.AsEnumerable().Reverse())
            {
                if ((t.ExitReason ?? "").StartsWith("PARTIAL", StringComparison.OrdinalIgnoreCase))
                    continue;
                if (t.NetPnL > 0) break;
                reconstructedLosses++;
            }
            _consecutiveLosses = Math.Max(data.ConsecutiveLosses, reconstructedLosses);
            if (_haltTrading && string.IsNullOrWhiteSpace(_haltReason))
            {
                _haltReason = MAX_CONSECUTIVE_LOSSES > 0 && _consecutiveLosses >= MAX_CONSECUTIVE_LOSSES
                    ? "CONSECUTIVE_LOSSES"
                    : "RESTORED_HALT";
            }
            _strategyTradeCount.Clear();
            if (data.StrategyTradeCount?.Count > 0)
            {
                foreach (var kv in data.StrategyTradeCount)
                    _strategyTradeCount[kv.Key] = kv.Value;
            }
            else if (data.LastVolumeResetDate.Date == GetEasternTime().Date)
            {
                // Backward compatibility for state files written before the
                // per-strategy counters were persisted. Rebuild from today's
                // completed trades so restarting cannot reset a strategy's cap.
                foreach (var t in _completedTrades.Where(t => !string.IsNullOrWhiteSpace(t.Strategy)))
                    IncrementStrategyCount(t.Strategy);
                foreach (var p in _positions.Values.Where(p => !string.IsNullOrWhiteSpace(p.StrategyTag)))
                    IncrementStrategyCount(p.StrategyTag);
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

        // Safety net: Program.cs only guarded the STARTUP reconciliation with
        // a 30s ForceReconcile timeout. This path — triggered by the
        // connection watchdog after every reconnect — had no equivalent.
        // If IBKR's positionEnd() callback never fires after this
        // RequestPositions() call (missed/stale reqId, TWS still catching
        // up post-reconnect, etc.), _reconciled stays false for the rest of
        // the session and PassesEntryGates() silently blocks every entry —
        // no error, no log, the bot just never trades again until the
        // process is fully restarted. Mirror the startup timeout here so a
        // stuck reconnect-reconciliation can't strand trading indefinitely.
        _ = Task.Run(async () =>
        {
            await Task.Delay(30_000);
            if (!_reconciled)
            {
                LogMessage("[WATCHDOG] Re-reconciliation timed out after 30s — forcing reconcile so trading can resume.");
                ForceReconcile();
            }
        });
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
            // persist daily candles too
            SaveDailyMemory();
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

        // Load persisted daily candles and if missing, seed from Yahoo
        try
        {
            LoadDailyMemory();
        }
        catch (Exception ex) { LogError("LoadDailyMemory", ex.Message); }

        // If some symbols lack daily history, fetch from Yahoo to ensure SMA accuracy
        try
        {
            foreach (var sym in _watchlist)
            {
                if (_dailyCandles.ContainsKey(sym) && _dailyCandles[sym].Count > 10) continue;
                var bars = FetchDailyFromYahoo(sym).GetAwaiter().GetResult();
                if (bars != null && bars.Count > 0)
                {
                    _dailyCandles[sym] = bars;
                    LogMessage($"[YAHOO SEED] {sym} daily bars seeded: {bars.Count}");
                }
            }
        }
        catch (Exception ex) { LogError("SeedDailyFromYahoo", ex.Message); }
    }

    private void SaveDailyMemory()
    {
        try
        {
            var dict = _dailyCandles.ToDictionary(k => k.Key, v => v.Value);
            AtomicWrite(StatePath("daily_memory.json"), JsonSerializer.Serialize(dict));
        }
        catch (Exception ex) { LogError("SaveDailyMemory", ex.Message); }
    }

    private void LoadDailyMemory()
    {
        string raw = SafeReadJson(StatePath("daily_memory.json"));
        if (raw == null) return;
        try
        {
            var data = JsonSerializer.Deserialize<ConcurrentDictionary<string, List<Candle>>>(raw);
            if (data != null)
                foreach (var kv in data)
                    _dailyCandles[kv.Key] = kv.Value;
        }
        catch (Exception ex) { LogError("LoadDailyMemory", ex.Message); }
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

    private static string[] NormalizeWatchlist(IEnumerable<string> symbols)
    {
        return (symbols ?? Array.Empty<string>())
            .Where(s => !string.IsNullOrWhiteSpace(s))
            .Select(s => s.Trim().ToUpperInvariant())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private void SyncBrokerDataLineBudget()
    {
        if (RealBroker is IbClient ib)
        {
            int slots = GetSubscriptionSlots();
            ib.MaxMarketDataLines = slots;
            LogMessage($"[DATA] IBKR live-subscription cap synced to {slots} symbol slots (budget: {MAX_MARKET_DATA_LINES} lines ÷ {Math.Max(1, DATA_LINES_PER_SYMBOL)}/sym).");
        }
    }

    public int ConfiguredMarketDataLines => MAX_MARKET_DATA_LINES;
    public int ConfiguredDataLinesPerSymbol => Math.Max(1, DATA_LINES_PER_SYMBOL);
    public int ConfiguredSubscriptionSlots => GetSubscriptionSlots();
    public int NadarayaWatsonLookback => NW_LOOKBACK;
    public int NadarayaWatsonTimeframeMinutes => NW_TIMEFRAME_MINUTES;

    public int GetNadarayaWatson1HourBarCount(string symbol)
        => GetCompletedNwHourlyCandles(symbol).Count;

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

        if (STRATEGY_NADARAYA_WATSON_ENABLED)
        {
            LogMessage($"[HIST] Requesting dedicated {NW_TIMEFRAME_MINUTES}-min RTH bars for {toSubscribe.Count} symbols (NW)...");
            foreach (var symbol in toSubscribe)
            {
                LogMessage($"[HIST] Requesting {NW_TIMEFRAME_MINUTES}-min NW history: {symbol}...");
                RealBroker.RequestHourlyHistoricalData(symbol, NW_TIMEFRAME_MINUTES);
                await Task.Delay(750);
            }
        }

        LogMessage($"[HIST] Requesting daily bars for {_watchlist.Length} symbols (SMA200 + S/R levels)...");
        foreach (var symbol in _watchlist)
        {
            LogMessage($"[HIST] Requesting daily history: {symbol}...");
            RealBroker.RequestDailyHistoricalData(symbol);
            await Task.Delay(1500);
        }

        // ── FIX: Seed indicator cache from historical candles so the web dashboard
        // shows SMA20/50/RSI immediately — not just after the first live tick.
        // Without this, _indicatorCache is empty until FinalizeCandle() fires,
        // and BuildStatusJson() emits sma20=0, sma50=0 → HTML shows "—".
        // BUGFIX: SeedVwapFromCandles(symbol) used to be called BEFORE
        // RealBroker.RequestHistoricalData(symbol) even ran, so _marketData had no
        // candles for that symbol yet — the seed always no-op'd (empty _marketData,
        // or "today" filtered down to 0 bars on a fresh session). Moved here, after
        // the historical data loop above has had time to populate _marketData, so
        // VWAP actually gets backfilled instead of sitting at $0 ("—" on the
        // dashboard) until enough live ticks trickle in on their own.
        foreach (var symbol in toSubscribe)
        {
            if (_marketData.TryGetValue(symbol, out var candles) && candles.Count >= 30)
            {
                RefreshIndicatorCache(symbol, candles);
                Refresh15MinEma(symbol, candles);
            }
            SeedVwapFromCandles(symbol);
            SeedOpeningRangeFromCandles(symbol);
        }
        LogMessage($"[HIST] Indicator/ORB cache seeded for {toSubscribe.Count} symbols.");

        _previousOrbMinutes = ORB_MINUTES;
    }

    public async Task ReconcileWatchlistSubscriptions(bool requestNwForNewSymbols = true)
    {
        if (RealBroker == null || !RealBroker.IsReady) return;

        SyncBrokerDataLineBudget();

        int slots = GetSubscriptionSlots();
        var target = GetPrioritizedWatchlist().Take(slots).ToArray();
        var targetSet = new HashSet<string>(target, StringComparer.OrdinalIgnoreCase);
        var currentlySubscribed = _subscribedSymbols.ToArray();

        foreach (var sym in currentlySubscribed)
        {
            if (targetSet.Contains(sym)) continue;

            bool hasPosition;
            lock (_lock) { hasPosition = _positions.ContainsKey(sym); }
            if (hasPosition)
            {
                LogMessage($"[WATCHLIST] {sym} is outside the current line budget but has an open position — keeping feed until flat.");
                continue;
            }

            LogMessage($"[WATCHLIST] Unsubscribing non-target symbol: {sym}");
            try { RealBroker.CancelMarketData(sym); } catch { }
            _subscribedSymbols.Remove(sym);
        }

        var currentlySubscribedSet = new HashSet<string>(_subscribedSymbols, StringComparer.OrdinalIgnoreCase);
        int available = Math.Max(0, slots - currentlySubscribedSet.Count);
        var missing = target.Where(sym => !currentlySubscribedSet.Contains(sym)).Take(available).ToArray();
        int overflow = Math.Max(0, _watchlist.Length - target.Length);

        if (overflow > 0)
            LogMessage($"[DATA] Active budget is {slots} symbol slots. {_watchlist.Length - target.Length} watchlist symbol(s) remain inactive until slots are freed or MAX_MARKET_DATA_LINES is raised.");

        if (missing.Length < target.Count(sym => !currentlySubscribedSet.Contains(sym)))
            LogMessage($"[DATA] Some target symbols could not be activated because existing open-position feeds are using the remaining slots.");

        foreach (var sym in missing)
        {
            LogMessage($"[WATCHLIST] Subscribing target symbol: {sym}");
            RealBroker.RequestHistoricalData(sym);
            if (STRATEGY_NADARAYA_WATSON_ENABLED && requestNwForNewSymbols)
                RealBroker.RequestHourlyHistoricalData(sym, NW_TIMEFRAME_MINUTES);
            _subscribedSymbols.Add(sym);
            await Task.Delay(1500);
        }
    }

    public Task ApplyWatchlistDiff(string[] oldList, string[] newList)
    {
        return ReconcileWatchlistSubscriptions();
    }

    private async Task ReloadNwHistoryForActiveSymbols()
    {
        if (RealBroker == null || !RealBroker.IsReady || !STRATEGY_NADARAYA_WATSON_ENABLED)
            return;

        var active = GetPrioritizedWatchlist().Take(GetSubscriptionSlots()).ToArray();
        LogMessage($"[NW CONFIG] Reloading {NW_TIMEFRAME_MINUTES}-min history for {active.Length} active symbols...");
        foreach (var sym in active)
        {
            RealBroker.RequestHourlyHistoricalData(sym, NW_TIMEFRAME_MINUTES);
            await Task.Delay(750);
        }
    }

    public void ReevaluateHalt()
    {
        lock (_lock)
        {
            if (_manualResumeOverride)
            {
                _haltTrading = false;
                _haltReason = "";
                return;
            }

            bool shouldHalt = _totalRealizedPnL >= DAILY_PROFIT_GOAL
                           || _totalRealizedPnL <= MAX_DAILY_LOSS;
            // Saving dashboard config may legitimately lift a halt caused by the
            // old daily PnL limits. It must never erase an EOD, consecutive-loss,
            // or restored safety halt.
            if (_haltTrading && _haltReason == "DAILY_LIMIT" && !shouldHalt)
            {
                _haltTrading = false;
                _haltReason = "";
                LogMessage($"[CONFIG] Halt lifted — PnL {_totalRealizedPnL:C2} is within updated limits " +
                           $"(goal={DAILY_PROFIT_GOAL:C2}, maxLoss={MAX_DAILY_LOSS:C2}).");
            }
            else if (!_haltTrading && shouldHalt)
            {
                _haltTrading = true;
                _haltReason = "DAILY_LIMIT";
                LogMessage($"[CONFIG] New limits triggered halt — PnL {_totalRealizedPnL:C2} breaches " +
                           $"(goal={DAILY_PROFIT_GOAL:C2}, maxLoss={MAX_DAILY_LOSS:C2}).");
            }
        }
    }

    public string ManualUnhalt()
    {
        lock (_lock)
        {
            _manualResumeOverride = true;
            _haltTrading = false;
            _haltReason = "";
            _consecutiveLosses = 0;
            LogMessage($"[MANUAL RESUME] Trading manually resumed from dashboard. Daily halt and DD block are overridden until the next day reset. PnL={_totalRealizedPnL:C2}");
            return $"Trading resumed manually. Daily halt and DD block are overridden until the next day reset. Current PnL: {_totalRealizedPnL:C2}.";
        }
    }

    public void AddHourlyCandle(string symbol, DateTime time,
        decimal open, decimal high, decimal low, decimal close, long vol)
    {
        DateTime? bucketMaybe = GetRegularSessionHourBucket(time);
        if (!bucketMaybe.HasValue) return;

        DateTime bucket = bucketMaybe.Value;
        var list = _hourlyCandles.GetOrAdd(symbol, _ => new List<Candle>());

        lock (list)
        {
            var existing = list.FirstOrDefault(c => c.Time == bucket);
            if (existing == null)
            {
                list.Add(new Candle
                {
                    Time = bucket,
                    Open = open,
                    High = high,
                    Low = low,
                    Close = close,
                    Volume = vol
                });
                list.Sort((a, b) => a.Time.CompareTo(b.Time));
            }
            else
            {
                // Historical data is authoritative for the bar snapshot received
                // at request time. Finalized live 1-min bars will keep this bucket
                // current afterwards via UpdateHourlyFromMinute().
                existing.Open = open;
                existing.High = high;
                existing.Low = low;
                existing.Close = close;
                existing.Volume = vol;
            }

            int maxHourlyBars = Math.Max(NW_LOOKBACK + 100, 700);
            if (list.Count > maxHourlyBars)
                list.RemoveRange(0, list.Count - maxHourlyBars);
        }
    }

    public void AddHistoricalCandle(string symbol, DateTime time,
        decimal open, decimal high, decimal low, decimal close, long vol)
    {
        // Capture today's ORB while historical bars stream in, BEFORE the rolling
        // 500-bar intraday buffer discards the morning. A 3-day 1-minute request
        // contains far more than 500 bars, so waiting until historicalDataEnd()
        // meant the 09:30 opening bars were already gone on a midday/late restart.
        DateTime nowEt = GetEasternTime();
        DateTime sessionOpen = new DateTime(nowEt.Year, nowEt.Month, nowEt.Day, 9, 30, 0);
        DateTime orbEnd = sessionOpen.AddMinutes(ORB_MINUTES);
        if (time.Date == nowEt.Date && time >= sessionOpen && time < orbEnd)
        {
            if (time == sessionOpen)
                _orbRanges[symbol] = new OpeningRange { High = high, Low = low, IsSet = nowEt >= orbEnd };
            else
            {
                var histOrb = _orbRanges.GetOrAdd(symbol, _ => new OpeningRange { High = high, Low = low });
                histOrb.High = Math.Max(histOrb.High, high);
                histOrb.Low = histOrb.Low <= 0 ? low : Math.Min(histOrb.Low, low);
                histOrb.IsSet = nowEt >= orbEnd;
            }
        }

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

            // "Yesterday" = the most recent completed session strictly before
            // today's ET trading date — resolved by calendar date, not list
            // position. IBKR's historical daily-bar feed only ever returns
            // *completed* sessions, so for most of the trading day the list's
            // last entry already IS yesterday's bar (today's own bar doesn't
            // exist yet). The old `list[list.Count - 2]` logic assumed today's
            // bar was always present, so it was actually grabbing the day
            // BEFORE yesterday — Prev Day Hi/Lo/Close (and Gap %, which is
            // derived from _prevDayClose) lagged a full session for the entire
            // live session, every day, which — combined with the nightly
            // TWS restart requiring a fresh historical reload each morning —
            // meant this was essentially always wrong during market hours.
            var todayEt = GetEasternTime().Date;
            var mostRecentPrior = list
                .Where(c => c.Time.Date < todayEt)
                .OrderByDescending(c => c.Time)
                .FirstOrDefault();
            if (mostRecentPrior != null)
            {
                _prevDayHighLevel[symbol] = mostRecentPrior.High;
                _prevDayLowLevel[symbol] = mostRecentPrior.Low;
                _prevDayClose[symbol] = mostRecentPrior.Close;
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
    private const string CONFIG_PASSWORD = "a";

    private string EffectiveConfigPassword()
    {
        var env = Environment.GetEnvironmentVariable("BOT_CONFIG_PASSWORD");
        return string.IsNullOrWhiteSpace(env) ? CONFIG_PASSWORD : env.Trim();
    }

    private bool PasswordMatches(string? supplied)
    {
        string expected = EffectiveConfigPassword();
        return !string.IsNullOrWhiteSpace(expected)
            && string.Equals(supplied, expected, StringComparison.Ordinal);
    }

    public void LoadConfig()
    {
        try
        {
            if (!File.Exists(CONFIG_FILE))
            {
                Console.WriteLine($"[CONFIG] Missing {CONFIG_FILE}; using built-in settings. " +
                                  $"NW={NW_TIMEFRAME_MINUTES}m/{NW_LOOKBACK}/{NW_BANDWIDTH:F1}/{NW_MULT:F1}");
                return;
            }
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
            MIN_ENTRY_MINUTES_AFTER_OPEN = GetI("MIN_ENTRY_MINUTES_AFTER_OPEN", MIN_ENTRY_MINUTES_AFTER_OPEN);
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
            MAX_TRADES_PER_HOUR = GetI("MAX_TRADES_PER_HOUR", MAX_TRADES_PER_HOUR);
            MIN_ATR_PCT = GetD("MIN_ATR_PCT", MIN_ATR_PCT);
            MIN_RR_RATIO = GetD("MIN_RR_RATIO", MIN_RR_RATIO);
            _allowShorts = GetB("ALLOW_SHORTS", _allowShorts);
            INVERT_ENTRY_DIRECTION = GetB("INVERT_ENTRY_DIRECTION", INVERT_ENTRY_DIRECTION);
            MIDDAY_FILTER_ENABLED = GetB("MIDDAY_FILTER_ENABLED", MIDDAY_FILTER_ENABLED);
            REQUIRE_SMA_ALIGNMENT = GetB("REQUIRE_SMA_ALIGNMENT", REQUIRE_SMA_ALIGNMENT);
            SHORT_TAPE_STRICTNESS = GetI("SHORT_TAPE_STRICTNESS", SHORT_TAPE_STRICTNESS);
            ALLOW_PARTIAL_ON_WEAK_SIGNAL = GetB("ALLOW_PARTIAL_ON_WEAK_SIGNAL", ALLOW_PARTIAL_ON_WEAK_SIGNAL);
            MAX_CONSECUTIVE_LOSSES = GetI("MAX_CONSECUTIVE_LOSSES", MAX_CONSECUTIVE_LOSSES);
            STRATEGY_ORB_ENABLED = GetB("STRATEGY_ORB", STRATEGY_ORB_ENABLED);
            STRATEGY_GAP_GO_ENABLED = GetB("STRATEGY_GAP_GO", STRATEGY_GAP_GO_ENABLED);
            STRATEGY_VWAP_ENABLED = GetB("STRATEGY_VWAP", STRATEGY_VWAP_ENABLED);
            STRATEGY_MEAN_REV_ENABLED = GetB("STRATEGY_MEAN_REV", STRATEGY_MEAN_REV_ENABLED);
            STRATEGY_BB_MR_ENABLED = GetB("STRATEGY_BB_MR", STRATEGY_BB_MR_ENABLED);
            STRATEGY_MOMENTUM_ENABLED = GetB("STRATEGY_MOMENTUM", STRATEGY_MOMENTUM_ENABLED);
            STRATEGY_EMA_POCKET_ENABLED = GetB("STRATEGY_EMA_POCKET", STRATEGY_EMA_POCKET_ENABLED);
            STRATEGY_OUTSIDE_CANDLE_ENABLED = GetB("STRATEGY_OUTSIDE_CANDLE", STRATEGY_OUTSIDE_CANDLE_ENABLED);
            STRATEGY_CANDLE_PATTERNS_ENABLED = GetB("STRATEGY_CANDLE_PATTERNS", STRATEGY_CANDLE_PATTERNS_ENABLED);
            STRATEGY_MICRO_PULLBACK_ENABLED = GetB("STRATEGY_MICRO_PULLBACK", STRATEGY_MICRO_PULLBACK_ENABLED);
            STRATEGY_NADARAYA_WATSON_ENABLED = GetB("STRATEGY_NADARAYA_WATSON", STRATEGY_NADARAYA_WATSON_ENABLED);
            NW_TIMEFRAME_MINUTES = ValidateNwTimeframe(GetI("NW_TIMEFRAME_MINUTES", NW_TIMEFRAME_MINUTES));
            NW_LOOKBACK = GetI("NW_LOOKBACK", NW_LOOKBACK);
            NW_BANDWIDTH = GetD("NW_BANDWIDTH", NW_BANDWIDTH);
            NW_MULT = GetD("NW_MULT", NW_MULT);
            NW_STOP_LOSS_PCT = GetD("NW_STOP_LOSS_PCT", NW_STOP_LOSS_PCT);
            EARLY_PATTERN_ENTRY_ENABLED = GetB("EARLY_PATTERN_ENTRY", EARLY_PATTERN_ENTRY_ENABLED);
            PATTERN_MIN_SCORE = GetI("PATTERN_MIN_SCORE", PATTERN_MIN_SCORE);
            INTRABAR_SIGNAL_COOLDOWN_SECONDS = GetI("INTRABAR_SIGNAL_COOLDOWN_SECONDS", INTRABAR_SIGNAL_COOLDOWN_SECONDS);
            FAST_VOL_MULT = GetD("FAST_VOL_MULT", FAST_VOL_MULT);
            DATA_LINES_PER_SYMBOL = GetI("DATA_LINES_PER_SYMBOL", DATA_LINES_PER_SYMBOL);
            MAX_MARKET_DATA_LINES = GetI("MAX_MARKET_DATA_LINES", MAX_MARKET_DATA_LINES);
            VIX_REDUCE_THRESHOLD = GetD("VIX_REDUCE_THRESHOLD", VIX_REDUCE_THRESHOLD);
            VIX_NO_LONG_THRESHOLD = GetD("VIX_NO_LONG_THRESHOLD", VIX_NO_LONG_THRESHOLD);
            MIN_SETUP_SCORE = GetI("MIN_SETUP_SCORE", MIN_SETUP_SCORE);
            UNREALIZED_DD_HALT_THRESHOLD = GetD("UNREALIZED_DD_HALT", UNREALIZED_DD_HALT_THRESHOLD);
            DYNAMIC_SIZING_ENABLED = GetB("DYNAMIC_SIZING", DYNAMIC_SIZING_ENABLED);
            MAX_TRADES_PER_STRATEGY = GetI("MAX_TRADES_PER_STRATEGY", MAX_TRADES_PER_STRATEGY);
            MAX_TRADES_PER_SYMBOL_PER_DAY = GetI("MAX_TRADES_PER_SYMBOL_PER_DAY", MAX_TRADES_PER_SYMBOL_PER_DAY);
            TREND_REVERSAL_GATE_ENABLED = GetB("TREND_REVERSAL_GATE", TREND_REVERSAL_GATE_ENABLED);
            SHOW_PROJECTIONS = GetB("SHOW_PROJECTIONS", SHOW_PROJECTIONS);
            SWING_MODE_ENABLED = GetB("SWING_MODE_ENABLED", SWING_MODE_ENABLED);
            EOD_LIQUIDATE_ENABLED = GetB("EOD_LIQUIDATE_ENABLED", EOD_LIQUIDATE_ENABLED);
            SWING_BASE_LOOKBACK_DAYS = GetI("SWING_BASE_LOOKBACK_DAYS", SWING_BASE_LOOKBACK_DAYS);
            SWING_MAX_HOLD_DAYS = GetI("SWING_MAX_HOLD_DAYS", SWING_MAX_HOLD_DAYS);
            SWING_BREAKOUT_BUFFER_PCT = GetD("SWING_BREAKOUT_BUFFER_PCT", SWING_BREAKOUT_BUFFER_PCT);
            SWING_BASE_TIGHTNESS_MAX = GetD("SWING_BASE_TIGHTNESS_MAX", SWING_BASE_TIGHTNESS_MAX);
            SWING_TARGET_R_MULT = GetD("SWING_TARGET_R_MULT", SWING_TARGET_R_MULT);
            SWING_REQUIRE_CONTRACTION = GetB("SWING_REQUIRE_CONTRACTION", SWING_REQUIRE_CONTRACTION);
            ALLOW_BULLISH_CANDLE_PATTERNS = GetB("ALLOW_BULLISH_CANDLE_PATTERNS", ALLOW_BULLISH_CANDLE_PATTERNS);
            ALLOW_SCALP_BREAKOUT_LONGS = GetB("ALLOW_SCALP_BREAKOUT_LONGS", ALLOW_SCALP_BREAKOUT_LONGS);
            ALLOW_SCALP_BREAKOUT_SHORTS = GetB("ALLOW_SCALP_BREAKOUT_SHORTS", ALLOW_SCALP_BREAKOUT_SHORTS);
            ALLOW_SCALP_ORB_LONGS = GetB("ALLOW_SCALP_ORB_LONGS", ALLOW_SCALP_ORB_LONGS);

            // Backwards-compatible: if USE_SMA100 present, set REQUIRE_SMA_ALIGNMENT loosely
            if (root.TryGetProperty("USE_SMA100", out var useSma100El) && useSma100El.ValueKind == System.Text.Json.JsonValueKind.True)
            {
                // do not enforce strict alignment by default; it's a hint to use sma100 in scoring
                REQUIRE_SMA_ALIGNMENT = GetB("REQUIRE_SMA_ALIGNMENT", REQUIRE_SMA_ALIGNMENT);
            }

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
                    if (!string.IsNullOrWhiteSpace(s)) list.Add(s);
                }
                var normalized = NormalizeWatchlist(list);
                if (normalized.Length > 0) _watchlist = normalized;
            }

            SyncBrokerDataLineBudget();
            Console.WriteLine($"[CONFIG] Loaded from {CONFIG_FILE}");
            Console.WriteLine($"[CONFIG] Effective: budget={TOTAL_BUDGET:F0}, position={POSITION_SIZE:F0}, " +
                              $"NW={NW_TIMEFRAME_MINUTES}m/{NW_LOOKBACK}/{NW_BANDWIDTH:F1}/{NW_MULT:F1}, " +
                              $"day/hour/strategyCap={MAX_TRADES_PER_DAY}/{MAX_TRADES_PER_HOUR}/{MAX_TRADES_PER_STRATEGY}, " +
                              $"consecutiveLossCap={MAX_CONSECUTIVE_LOSSES}, firstEntry={MIN_ENTRY_MINUTES_AFTER_OPEN}m");
            if (string.IsNullOrWhiteSpace(EffectiveConfigPassword()))
                Console.WriteLine("[SECURITY] BOT_CONFIG_PASSWORD is not set; dashboard mutation endpoints are disabled.");
            if (string.IsNullOrWhiteSpace(EmailPassword))
                Console.WriteLine("[EMAIL] BOT_EMAIL_PASS is not set; email notifications are disabled.");
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

    // ── Build a BacktestConfig that mirrors current live bot settings ──
    // This ensures the simulation layer uses the same parameters you've tuned
    // from the dashboard, rather than hardcoded defaults that drift over time.
    private BacktestConfig BuildBacktestConfig(bool isHistorical = false, string periodLabel = "")
    {
        return new BacktestConfig
        {
            Capital = TOTAL_BUDGET,
            RiskPct = RISK_PCT,
            PositionSize = POSITION_SIZE,
            HardStopAtrMult = HARD_STOP_ATR_MULT,
            AtrTrailMult = ATR_TRAIL_MULT,
            TargetAtrMult = 1.55m,    // scalper target multiplier after commission filter
            Commission = COMMISSION_PER_SIDE * 2,
            EntrySlippagePct = 0.0003m,
            ExitSlippagePct = 0.0003m,
            MaxPositions = MAX_POSITIONS,
            MaxTradesPerDay = MAX_TRADES_PER_DAY,
            MaxTradesPerHour = MAX_TRADES_PER_HOUR,
            MaxConsecutiveLosses = MAX_CONSECUTIVE_LOSSES,
            MaxDailyLoss = MAX_DAILY_LOSS,
            MinHoldSeconds = MIN_HOLD_SECONDS,
            MinEntryMinutesAfterOpen = MIN_ENTRY_MINUTES_AFTER_OPEN,
            SwingMode = SWING_MODE_ENABLED,
            EodLiquidate = EOD_LIQUIDATE_ENABLED,
            SwingBaseLookbackDays = SWING_BASE_LOOKBACK_DAYS,
            SwingMaxHoldDays = SWING_MAX_HOLD_DAYS,
            SwingTargetRMult = SWING_TARGET_R_MULT,
            RsiLongMin = RSI_LONG_MIN,
            RsiShortMax = RSI_SHORT_MAX,
            OrbMinutes = ORB_MINUTES,
            BreakEvenTriggerR = 1.0m,
            MinBreakoutBodyRatio = 0.55m,
            MinGrossTargetToCommissionMult = MIN_GROSS_TARGET_TO_COMMISSION_MULT,
            EnableCandlePatterns = STRATEGY_CANDLE_PATTERNS_ENABLED,
            EnableOrb = STRATEGY_ORB_ENABLED,
            EnableGapGo = STRATEGY_GAP_GO_ENABLED,
            EnableVwap = STRATEGY_VWAP_ENABLED,
            EnableMomentum = STRATEGY_MOMENTUM_ENABLED,
            PatternMinScore = PATTERN_MIN_SCORE,
            AllowBullishPatternEntries = STRATEGY_CANDLE_PATTERNS_ENABLED && ALLOW_BULLISH_CANDLE_PATTERNS,
            AllowMicroPullback = STRATEGY_MICRO_PULLBACK_ENABLED,
            AllowScalpBreakoutLongs = ALLOW_SCALP_BREAKOUT_LONGS,
            AllowScalpBreakoutShorts = ALLOW_SCALP_BREAKOUT_SHORTS,
            AllowScalpOrbLongs = ALLOW_SCALP_ORB_LONGS,
            AllowShorts = _allowShorts,
            IsHistoricalMode = isHistorical,
            HistoricalPeriodLabel = periodLabel,
            ShowProjections = SHOW_PROJECTIONS
        };
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
            $"\"MIN_ENTRY_MINUTES_AFTER_OPEN\":{MIN_ENTRY_MINUTES_AFTER_OPEN}",
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
            $"\"MAX_TRADES_PER_HOUR\":{MAX_TRADES_PER_HOUR}",
            $"\"MIN_ATR_PCT\":{MIN_ATR_PCT:F4}",
            $"\"MIN_RR_RATIO\":{MIN_RR_RATIO:F2}",
            $"\"ALLOW_SHORTS\":{(_allowShorts ? "true" : "false")}",
            $"\"INVERT_ENTRY_DIRECTION\":{(INVERT_ENTRY_DIRECTION ? "true" : "false")}",
            $"\"MIDDAY_FILTER_ENABLED\":{(MIDDAY_FILTER_ENABLED ? "true" : "false")}",
            $"\"REQUIRE_SMA_ALIGNMENT\":{(REQUIRE_SMA_ALIGNMENT ? "true" : "false")}",
            $"\"SHORT_TAPE_STRICTNESS\":{SHORT_TAPE_STRICTNESS}",
            $"\"ALLOW_PARTIAL_ON_WEAK_SIGNAL\":{(ALLOW_PARTIAL_ON_WEAK_SIGNAL ? "true" : "false")}",
            $"\"MAX_CONSECUTIVE_LOSSES\":{MAX_CONSECUTIVE_LOSSES}",
            $"\"STRATEGY_ORB\":{(STRATEGY_ORB_ENABLED ? "true" : "false")}",
            $"\"STRATEGY_GAP_GO\":{(STRATEGY_GAP_GO_ENABLED ? "true" : "false")}",
            $"\"STRATEGY_VWAP\":{(STRATEGY_VWAP_ENABLED ? "true" : "false")}",
            $"\"STRATEGY_MEAN_REV\":{(STRATEGY_MEAN_REV_ENABLED ? "true" : "false")}",
            $"\"STRATEGY_BB_MR\":{(STRATEGY_BB_MR_ENABLED ? "true" : "false")}",
            $"\"STRATEGY_MOMENTUM\":{(STRATEGY_MOMENTUM_ENABLED ? "true" : "false")}",
            $"\"STRATEGY_EMA_POCKET\":{(STRATEGY_EMA_POCKET_ENABLED ? "true" : "false")}",
            $"\"STRATEGY_OUTSIDE_CANDLE\":{(STRATEGY_OUTSIDE_CANDLE_ENABLED ? "true" : "false")}",
            $"\"STRATEGY_CANDLE_PATTERNS\":{(STRATEGY_CANDLE_PATTERNS_ENABLED ? "true" : "false")}",
            $"\"USE_SMA100\":{(/* USE_SMA100 present in config file; default true for backwards compat */ true ? "true" : "false")}",
            $"\"STRATEGY_MICRO_PULLBACK\":{(STRATEGY_MICRO_PULLBACK_ENABLED ? "true" : "false")}",
            $"\"STRATEGY_NADARAYA_WATSON\":{(STRATEGY_NADARAYA_WATSON_ENABLED ? "true" : "false")}",
            $"\"NW_TIMEFRAME_MINUTES\":{NW_TIMEFRAME_MINUTES}",
            $"\"NW_LOOKBACK\":{NW_LOOKBACK}",
            $"\"NW_BANDWIDTH\":{NW_BANDWIDTH:F2}",
            $"\"NW_MULT\":{NW_MULT:F2}",
            $"\"NW_STOP_LOSS_PCT\":{NW_STOP_LOSS_PCT:F4}",
            $"\"EARLY_PATTERN_ENTRY\":{(EARLY_PATTERN_ENTRY_ENABLED ? "true" : "false")}",
            $"\"PATTERN_MIN_SCORE\":{PATTERN_MIN_SCORE}",
            $"\"INTRABAR_SIGNAL_COOLDOWN_SECONDS\":{INTRABAR_SIGNAL_COOLDOWN_SECONDS}",
            $"\"FAST_VOL_MULT\":{FAST_VOL_MULT:F2}",
            $"\"DATA_LINES_PER_SYMBOL\":{DATA_LINES_PER_SYMBOL}",
            $"\"MAX_MARKET_DATA_LINES\":{MAX_MARKET_DATA_LINES}",
            $"\"VIX_REDUCE_THRESHOLD\":{VIX_REDUCE_THRESHOLD:F1}",
            $"\"VIX_NO_LONG_THRESHOLD\":{VIX_NO_LONG_THRESHOLD:F1}",
            $"\"MIN_SETUP_SCORE\":{MIN_SETUP_SCORE}",
            $"\"UNREALIZED_DD_HALT\":{UNREALIZED_DD_HALT_THRESHOLD:F2}",
            $"\"DYNAMIC_SIZING\":{(DYNAMIC_SIZING_ENABLED ? "true" : "false")}",
            $"\"MAX_TRADES_PER_STRATEGY\":{MAX_TRADES_PER_STRATEGY}",
            $"\"MAX_TRADES_PER_SYMBOL_PER_DAY\":{MAX_TRADES_PER_SYMBOL_PER_DAY}",
            $"\"TREND_REVERSAL_GATE\":{(TREND_REVERSAL_GATE_ENABLED ? "true" : "false")}",
            $"\"SHOW_PROJECTIONS\":{(SHOW_PROJECTIONS ? "true" : "false")}",
            $"\"SWING_MODE_ENABLED\":{(SWING_MODE_ENABLED ? "true" : "false")}",
            $"\"EOD_LIQUIDATE_ENABLED\":{(EOD_LIQUIDATE_ENABLED ? "true" : "false")}",
            $"\"SWING_BASE_LOOKBACK_DAYS\":{SWING_BASE_LOOKBACK_DAYS}",
            $"\"SWING_MAX_HOLD_DAYS\":{SWING_MAX_HOLD_DAYS}",
            $"\"SWING_BREAKOUT_BUFFER_PCT\":{SWING_BREAKOUT_BUFFER_PCT}",
            $"\"SWING_BASE_TIGHTNESS_MAX\":{SWING_BASE_TIGHTNESS_MAX}",
            $"\"SWING_TARGET_R_MULT\":{SWING_TARGET_R_MULT}",
            $"\"SWING_REQUIRE_CONTRACTION\":{(SWING_REQUIRE_CONTRACTION ? "true" : "false")}",
            $"\"ALLOW_BULLISH_CANDLE_PATTERNS\":{(ALLOW_BULLISH_CANDLE_PATTERNS ? "true" : "false")}",
            $"\"ALLOW_SCALP_BREAKOUT_LONGS\":{(ALLOW_SCALP_BREAKOUT_LONGS ? "true" : "false")}",
            $"\"ALLOW_SCALP_BREAKOUT_SHORTS\":{(ALLOW_SCALP_BREAKOUT_SHORTS ? "true" : "false")}",
            $"\"ALLOW_SCALP_ORB_LONGS\":{(ALLOW_SCALP_ORB_LONGS ? "true" : "false")}",
            $"\"earnings_blacklist\":[{string.Join(",", _earningsBlacklist.Select(s => $"\"{s}\""))}]",
            $"\"watchlist\":{wlJson}"
        }) + $"{nl}}}";
    }

    private string BuildDefaultConfigJson(bool pretty = false)
    {
        var payload = new
        {
            watchlist = DEFAULT_WATCHLIST,
            watchlist_groups = DEFAULT_WATCHLIST_GROUPS
        };
        return JsonSerializer.Serialize(payload, new JsonSerializerOptions { WriteIndented = pretty });
    }

    private HttpListener _httpListener;

    private void StartDashboardServer()
    {
        try
        {
            _httpListener = new HttpListener();
            _httpListener.Prefixes.Add("http://*:8883/");
            _httpListener.Prefixes.Add("http://*:8884/");
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
            // Log incoming requests for diagnostics
            try { LogMessage($"[HTTP] {ctx.Request.HttpMethod} {ctx.Request.Url.AbsolutePath}"); } catch { }

            var path = ctx.Request.Url.AbsolutePath.ToLower();
            var method = ctx.Request.HttpMethod.ToUpper();
            string json;

            if (method == "OPTIONS")
            {
                ctx.Response.StatusCode = 204;
                ctx.Response.OutputStream.Close();
                return;
            }
            // Quick path: handle rebuild request early to ensure response is explicit
            else if (path == "/api/rebuild_sma" && method == "POST")
            {
                try
                {
                    using var reader = new System.IO.StreamReader(ctx.Request.InputStream, System.Text.Encoding.UTF8);
                    var body = reader.ReadToEnd();
                    using var doc = System.Text.Json.JsonDocument.Parse(string.IsNullOrWhiteSpace(body) ? "{}" : body);
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

                    if (_rebuildInProgress)
                    {
                        json = "{\"ok\":false,\"message\":\"Rebuild already in progress.\"}";
                    }
                    else
                    {
                        _rebuildInProgress = true;
                        _rebuildMessage = "Started by dashboard";
                        _lastRebuildUtc = DateTime.UtcNow;
                        Task.Run(() => {
                            try
                            {
                                RebuildAllSmaFromYahoo();
                                _rebuildMessage = "Completed successfully";
                            }
                            catch (Exception ex)
                            {
                                _rebuildMessage = "Failed: " + ex.Message;
                                LogError("RebuildAllSmaFromYahoo", ex.Message);
                            }
                            finally
                            {
                                _rebuildInProgress = false;
                                _lastRebuildUtc = DateTime.UtcNow;
                                try { SaveDailyMemory(); } catch { }
                            }
                        });
                        json = "{\"ok\":true,\"message\":\"Rebuild started\"}";
                    }
                }
                catch (Exception ex)
                {
                    ctx.Response.StatusCode = 400;
                    json = $"{{\"ok\":false,\"message\":\"{ex.Message.Replace("\"", "\\\"").Replace("\n", " ")}\"}}";
                }
            }
            else if (path == "/api/backtest/historical")
            {
                try
                {
                    var query = ctx.Request.QueryString;
                    string fromStr = query["from"] ?? "";
                    string toStr = query["to"] ?? "";

                    List<TradeRecord> snapshot;
                    lock (_allTrades) { snapshot = _allTrades.ToList(); }

                    // Filter by date range if provided
                    if (!string.IsNullOrEmpty(fromStr))
                        snapshot = snapshot.Where(t => string.Compare(t.Date, fromStr, StringComparison.Ordinal) >= 0).ToList();
                    if (!string.IsNullOrEmpty(toStr))
                        snapshot = snapshot.Where(t => string.Compare(t.Date, toStr, StringComparison.Ordinal) <= 0).ToList();

                    // Filter candle data to the same range for simulation
                    var filteredCandles = new ConcurrentDictionary<string, List<Candle>>();
                    DateTime fromDt = DateTime.TryParse(fromStr, out var fd) ? fd : DateTime.MinValue;
                    DateTime toDt = DateTime.TryParse(toStr, out var td) ? td.AddDays(1) : DateTime.MaxValue;
                    foreach (var kvp in _marketData)
                    {
                        List<Candle> filtered;
                        lock (kvp.Value)
                        {
                            filtered = kvp.Value
                                .Where(c => c.Time >= fromDt && c.Time < toDt)
                                .ToList();
                        }
                        if (filtered.Count > 0)
                            filteredCandles[kvp.Key] = filtered;
                    }

                    var filteredEquity = _lifetimeEquity
                        .Where(e => string.Compare(e.Date, fromStr, StringComparison.Ordinal) >= 0
                                 && string.Compare(e.Date, toStr, StringComparison.Ordinal) <= 0)
                        .ToList();

                    var report = BacktestEngine.Analyze(
                        snapshot,
                        filteredCandles,
                        filteredEquity,
                        TOTAL_BUDGET,
                        BuildBacktestConfig(
                            isHistorical: true,
                            periodLabel: $"{fromStr} → {toStr}"));
                    json = JsonSerializer.Serialize(report);
                }
                catch (Exception ex)
                {
                    json = $"{{\"error\":\"{ex.Message.Replace("\"", "'")}\"}}";
                }
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
                        TOTAL_BUDGET,
                        BuildBacktestConfig());
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
            else if (path == "/api/config/defaults" && method == "GET")
            {
                json = BuildDefaultConfigJson(pretty: true);
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

                    bool orbChanged;
                    bool nwHistoryReloadNeeded;
                    bool nwEnvelopeChanged;

                    lock (_lock)
                    {
                        int oldOrbMinutes = ORB_MINUTES;
                        bool oldNwEnabled = STRATEGY_NADARAYA_WATSON_ENABLED;
                        int oldNwTimeframe = NW_TIMEFRAME_MINUTES;
                        int oldNwLookback = NW_LOOKBACK;
                        decimal oldNwBandwidth = NW_BANDWIDTH;
                        decimal oldNwMult = NW_MULT;

                        TOTAL_BUDGET = GetD("TOTAL_BUDGET", TOTAL_BUDGET);
                        MAX_POSITIONS = GetI("MAX_POSITIONS", MAX_POSITIONS);
                        POSITION_SIZE = GetD("POSITION_SIZE", POSITION_SIZE);
                        MIN_HOLD_SECONDS = GetI("MIN_HOLD_SECONDS", MIN_HOLD_SECONDS);
                        MIN_ENTRY_MINUTES_AFTER_OPEN = GetI("MIN_ENTRY_MINUTES_AFTER_OPEN", MIN_ENTRY_MINUTES_AFTER_OPEN);
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
                        MAX_TRADES_PER_HOUR = GetI("MAX_TRADES_PER_HOUR", MAX_TRADES_PER_HOUR);
                        MIN_ATR_PCT = GetD("MIN_ATR_PCT", MIN_ATR_PCT);
                        MIN_RR_RATIO = GetD("MIN_RR_RATIO", MIN_RR_RATIO);
                        _allowShorts = GetB("ALLOW_SHORTS", _allowShorts);
                        INVERT_ENTRY_DIRECTION = GetB("INVERT_ENTRY_DIRECTION", INVERT_ENTRY_DIRECTION);
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
                        STRATEGY_CANDLE_PATTERNS_ENABLED = GetB("STRATEGY_CANDLE_PATTERNS", STRATEGY_CANDLE_PATTERNS_ENABLED);
                        STRATEGY_MICRO_PULLBACK_ENABLED = GetB("STRATEGY_MICRO_PULLBACK", STRATEGY_MICRO_PULLBACK_ENABLED);
                        STRATEGY_NADARAYA_WATSON_ENABLED = GetB("STRATEGY_NADARAYA_WATSON", STRATEGY_NADARAYA_WATSON_ENABLED);
                        NW_TIMEFRAME_MINUTES = ValidateNwTimeframe(GetI("NW_TIMEFRAME_MINUTES", NW_TIMEFRAME_MINUTES));
                        NW_LOOKBACK = GetI("NW_LOOKBACK", NW_LOOKBACK);
                        NW_BANDWIDTH = GetD("NW_BANDWIDTH", NW_BANDWIDTH);
                        NW_MULT = GetD("NW_MULT", NW_MULT);
                        NW_STOP_LOSS_PCT = GetD("NW_STOP_LOSS_PCT", NW_STOP_LOSS_PCT);
                        EARLY_PATTERN_ENTRY_ENABLED = GetB("EARLY_PATTERN_ENTRY", EARLY_PATTERN_ENTRY_ENABLED);
                        PATTERN_MIN_SCORE = GetI("PATTERN_MIN_SCORE", PATTERN_MIN_SCORE);
                        INTRABAR_SIGNAL_COOLDOWN_SECONDS = GetI("INTRABAR_SIGNAL_COOLDOWN_SECONDS", INTRABAR_SIGNAL_COOLDOWN_SECONDS);
                        FAST_VOL_MULT = GetD("FAST_VOL_MULT", FAST_VOL_MULT);
                        DATA_LINES_PER_SYMBOL = GetI("DATA_LINES_PER_SYMBOL", DATA_LINES_PER_SYMBOL);
                        MAX_MARKET_DATA_LINES = GetI("MAX_MARKET_DATA_LINES", MAX_MARKET_DATA_LINES);
                        VIX_REDUCE_THRESHOLD = GetD("VIX_REDUCE_THRESHOLD", VIX_REDUCE_THRESHOLD);
                        VIX_NO_LONG_THRESHOLD = GetD("VIX_NO_LONG_THRESHOLD", VIX_NO_LONG_THRESHOLD);
                        MIN_SETUP_SCORE = GetI("MIN_SETUP_SCORE", MIN_SETUP_SCORE);
                        UNREALIZED_DD_HALT_THRESHOLD = GetD("UNREALIZED_DD_HALT", UNREALIZED_DD_HALT_THRESHOLD);
                        DYNAMIC_SIZING_ENABLED = GetB("DYNAMIC_SIZING", DYNAMIC_SIZING_ENABLED);
                        MAX_TRADES_PER_STRATEGY = GetI("MAX_TRADES_PER_STRATEGY", MAX_TRADES_PER_STRATEGY);
                        MAX_TRADES_PER_SYMBOL_PER_DAY = GetI("MAX_TRADES_PER_SYMBOL_PER_DAY", MAX_TRADES_PER_SYMBOL_PER_DAY);
                        TREND_REVERSAL_GATE_ENABLED = GetB("TREND_REVERSAL_GATE", TREND_REVERSAL_GATE_ENABLED);
                        SHOW_PROJECTIONS = GetB("SHOW_PROJECTIONS", SHOW_PROJECTIONS);
                        SWING_MODE_ENABLED = GetB("SWING_MODE_ENABLED", SWING_MODE_ENABLED);
                        EOD_LIQUIDATE_ENABLED = GetB("EOD_LIQUIDATE_ENABLED", EOD_LIQUIDATE_ENABLED);
                        SWING_BASE_LOOKBACK_DAYS = GetI("SWING_BASE_LOOKBACK_DAYS", SWING_BASE_LOOKBACK_DAYS);
                        SWING_MAX_HOLD_DAYS = GetI("SWING_MAX_HOLD_DAYS", SWING_MAX_HOLD_DAYS);
                        SWING_BREAKOUT_BUFFER_PCT = GetD("SWING_BREAKOUT_BUFFER_PCT", SWING_BREAKOUT_BUFFER_PCT);
                        SWING_BASE_TIGHTNESS_MAX = GetD("SWING_BASE_TIGHTNESS_MAX", SWING_BASE_TIGHTNESS_MAX);
                        SWING_TARGET_R_MULT = GetD("SWING_TARGET_R_MULT", SWING_TARGET_R_MULT);
                        SWING_REQUIRE_CONTRACTION = GetB("SWING_REQUIRE_CONTRACTION", SWING_REQUIRE_CONTRACTION);
                        ALLOW_BULLISH_CANDLE_PATTERNS = GetB("ALLOW_BULLISH_CANDLE_PATTERNS", ALLOW_BULLISH_CANDLE_PATTERNS);
                        ALLOW_SCALP_BREAKOUT_LONGS = GetB("ALLOW_SCALP_BREAKOUT_LONGS", ALLOW_SCALP_BREAKOUT_LONGS);
                        ALLOW_SCALP_BREAKOUT_SHORTS = GetB("ALLOW_SCALP_BREAKOUT_SHORTS", ALLOW_SCALP_BREAKOUT_SHORTS);
                        ALLOW_SCALP_ORB_LONGS = GetB("ALLOW_SCALP_ORB_LONGS", ALLOW_SCALP_ORB_LONGS);

                        nwHistoryReloadNeeded = NW_TIMEFRAME_MINUTES != oldNwTimeframe
                            || (!oldNwEnabled && STRATEGY_NADARAYA_WATSON_ENABLED);
                        nwEnvelopeChanged = nwHistoryReloadNeeded
                            || NW_LOOKBACK != oldNwLookback
                            || NW_BANDWIDTH != oldNwBandwidth
                            || NW_MULT != oldNwMult;

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
                                if (!string.IsNullOrWhiteSpace(s)) list.Add(s);
                            }
                            var normalized = NormalizeWatchlist(list);
                            if (normalized.Length > 0)
                                _watchlist = normalized;
                        }
                    }

                    SyncBrokerDataLineBudget();
                    ReevaluateHalt();

                    if (orbChanged)
                    {
                        _orbRanges.Clear();
                        LogMessage($"[CONFIG] ORB_MINUTES changed — cleared all opening ranges, will recompute.");
                    }

                    if (nwEnvelopeChanged)
                    {
                        _nwEnvelopeCache.Clear();
                        _lastNwDecisionBySymbol.Clear();
                        _lastNwTouchDecisionBySymbol.Clear();
                    }
                    if (nwHistoryReloadNeeded)
                    {
                        _hourlyCandles.Clear();
                    }

                    _ = Task.Run(async () =>
                    {
                        await ReconcileWatchlistSubscriptions(requestNwForNewSymbols: !nwHistoryReloadNeeded);
                        if (nwHistoryReloadNeeded)
                            await ReloadNwHistoryForActiveSymbols();
                    });

                    SaveConfig();
                    json = "{\"ok\":true,\"message\":\"Configuration saved and applied live.\"}";
                }
                catch (Exception ex)
                {
                    ctx.Response.StatusCode = 400;
                    json = $"{{\"ok\":false,\"message\":\"{ex.Message.Replace("\"", "\\\"").Replace("\n", " ")}\"}}";
                }
            }
            // PLACEHOLDER: insertion point for external patches
            else if (path == "/api/unhalt" && method == "POST")
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

                    var msg = ManualUnhalt();
                    SaveState();
                    json = $"{{\"ok\":true,\"message\":\"{msg.Replace("\"", "'")}\"}}";
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

                            // ── FIX: Cancel bracket children BEFORE submitting manual MKT exit.
                            // Without this, the manual MKT fills first and removes the position,
                            // then the orphaned bracket stop/target fills later and OnOrderFilled
                            // sees no position → interprets the fill as a NEW entry → ghost position
                            // with empty StrategyTag that bleeds money in the wrong direction.
                            CancelBracketChildren(pos);

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
                            CancelBracketChildren(pos);
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
                var posQuote = GetDisplayQuote(p.Symbol, p.CurrentPrice > 0 ? p.CurrentPrice : p.AvgPrice);
                decimal px = posQuote.price > 0 ? posQuote.price : p.CurrentPrice;
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

                // Use cached indicators for RSI/ATR/MACD (intraday, appropriate for near-term signal
                // timing); fall back to direct computation if the cache hasn't been seeded yet.
                // SMA20/50/100 are shown as DAILY values here (matching SMA200) since that's what
                // the dashboard displays them as — the strategies themselves still use the fast,
                // intraday SMA20/50/100 (ind.Sma20/Sma50/Sma100) for entry timing; this is display-only.
                decimal sma20 = 0m, sma50 = 0m, sma100 = 0m, atr = 0m;
                double rsi = 0.0;
                int macdDir = 0;
                if (dataReady && _indicatorCache.TryGetValue(sym, out var indC))
                {
                    rsi = indC.Rsi14;
                    atr = indC.Atr14;
                    macdDir = indC.MacdDir;
                }
                else if (dataReady && candles != null && candles.Count >= 20)
                {
                    rsi = SafeRSI(candles, 14);
                    atr = SafeATR(candles, 14);
                    macdDir = SafeMACDDirection(candles);
                }
                if (dataReady)
                {
                    sma20 = GetDailySma20(sym);
                    sma50 = GetDailySma50(sym);
                    sma100 = GetDailySma100(sym);
                }
                decimal atrPct = price > 0 ? atr / price * 100 : 0;
                _vwap.TryGetValue(sym, out decimal vwap);
                decimal prevClose = GetPrevDayClose(sym);
                var quote = GetDisplayQuote(sym, price);
                decimal displayPrice = quote.price > 0 ? quote.price : price;
                // Both Gap % and Chg % now read the SAME fresh price (displayPrice) that
                // the Price column itself displays. Previously gapPct was computed from
                // `price` here BEFORE it got overwritten with displayPrice two lines
                // down — i.e. from the last completed 1-min candle close (up to ~59s
                // stale), while chgPct and the displayed Price both used the fresher
                // live quote. That made Gap % silently drift from what the Price column
                // showed, and from Chg % itself, by however much the price moved in that
                // stale window.
                decimal gapPct = prevClose > 0 && displayPrice > 0 ? (displayPrice - prevClose) / prevClose * 100 : 0;
                decimal chgPct = gapPct;
                if (price == 0m && displayPrice > 0) price = displayPrice;
                else if (displayPrice > 0) price = displayPrice;
                long volK = GetTodayVolume(sym) / 1000;
                bool abvVwap = vwap > 0 && price > vwap;
                string trend = price > sma50 ? "UP" : "NEUT";

                decimal sma200 = GetDailySma200(sym);
                var (pdHi, pdLo) = GetPrevDayHL(sym);
                // macdDir already set from _indicatorCache above

                _orbRanges.TryGetValue(sym, out var orb);
                decimal orbHi = orb?.High ?? 0m;
                decimal orbLo = orb?.Low ?? 0m;

                // Dashboard must show the exact same completed-1H NW envelope
                // used by the trading engine. The old code calculated these
                // columns from raw 1-minute candles, while the strategy used
                // 15-minute candles, so the displayed levels were unrelated.
                decimal nwHi = 0m, nwLo = 0m;
                var (_, nwUpRow, nwLoRow, nwBarsRow) = GetNadarayaWatson1HourEnvelope(sym);
                if (nwBarsRow >= NW_LOOKBACK)
                {
                    nwHi = nwUpRow;
                    nwLo = nwLoRow;
                }
                string nwWhy;
                if (!STRATEGY_NADARAYA_WATSON_ENABLED)
                    nwWhy = "Disabled";
                else if (nwBarsRow < NW_LOOKBACK)
                    nwWhy = $"History {nwBarsRow}/{NW_LOOKBACK}";
                else if (_lastNwTouchDecisionBySymbol.TryGetValue(sym, out var lastNwTouch))
                    nwWhy = lastNwTouch;
                else if (_lastNwDecisionBySymbol.TryGetValue(sym, out var nwDecision))
                    nwWhy = nwDecision;
                else if (nwLo > 0 && quote.last > 0)
                    nwWhy = quote.last <= nwLo
                        ? "Live LAST touching NW Low"
                        : $"Waiting: {(quote.last - nwLo) / nwLo * 100m:F2}% above NW Low";
                else
                    nwWhy = "Waiting for live LAST";

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
                    // The trading engine triggers NW from an actual LAST trade tick.
                    // Do not show a touch merely because the fallback bid/ask midpoint
                    // crossed the band while LAST remained on the other side.
                    bool nwLiveLast = quote.source == "last" && quote.last > 0;
                    if (sig == "" && nwLiveLast && nwLo > 0 && quote.last <= nwLo) sig = "NW↑";
                    else if (sig == "" && nwLiveLast && nwHi > 0 && quote.last >= nwHi) sig = "NW↓";
                }
                bool hot = dataReady && vwap > 0 && price > vwap && rsi > 55;

                if (!wfirst) wlArr.Append(",");
                wfirst = false; string pxSrc = quote.source;
                string ageJson = double.IsFinite(quote.ageSec) ? quote.ageSec.ToString("F1", System.Globalization.CultureInfo.InvariantCulture) : "9999";
                string why = GetWatchlistReadiness(sym);
                string whyEsc = why.Replace("\\", "\\\\").Replace("\"", "\\\"");
                string nwWhyEsc = nwWhy.Replace("\\", "\\\\").Replace("\"", "\\\"");
                wlArr.Append($@"{{""s"":""{sym}"",""price"":{price:F2},""last"":{quote.last:F2},""bid"":{quote.bid:F2},""ask"":{quote.ask:F2},""pxSrc"":""{pxSrc}"",""ageSec"":{ageJson},""vwap"":{vwap:F2},""sma20"":{sma20:F2},""sma50"":{sma50:F2},""sma100"":{sma100:F2},""sma200"":{sma200:F2},""rsi"":{rsi:F1},""gap"":{gapPct:F2},""chg"":{chgPct:F2},""vol"":{volK},""atr"":{atrPct:F2},""orbHi"":{orbHi:F2},""orbLo"":{orbLo:F2},""nwHi"":{nwHi:F2},""nwLo"":{nwLo:F2},""nwBars"":{nwBarsRow},""nwWhy"":""{nwWhyEsc}"",""pdHi"":{pdHi:F2},""pdLo"":{pdLo:F2},""macd"":{macdDir},""trend"":""{trend}"",""sig"":""{sig}"",""why"":""{whyEsc}"",""hot"":{(hot ? "true" : "false")},""abvVwap"":{(abvVwap ? "true" : "false")}}}");
            }
            wlArr.Append("]");
            // blocked reasons summary
            var blockedArr = new StringBuilder("{");
            bool bfirst = true;
            foreach (var kv in _blockedReasonCounts.OrderByDescending(kv => kv.Value).Take(20))
            {
                if (!bfirst) blockedArr.Append(","); bfirst = false;
                blockedArr.Append($"\"{kv.Key}\":{kv.Value}");
            }
            blockedArr.Append("}");

            var lastBlockedArr = new StringBuilder("{");
            bool lfirst = true;
            foreach (var kv in _lastBlockedReasonBySymbol)
            {
                if (!lfirst) lastBlockedArr.Append(","); lfirst = false;
                lastBlockedArr.Append($"\"{kv.Key}\":\"{kv.Value}\"");
            }
            lastBlockedArr.Append("}");

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

            // allTrades — only re-serialize when count changes (500 trade loop is expensive)
            lock (_allTrades)
            {
                int currentCount = _allTrades.Count;
                if (currentCount != _cachedAllTradesCount)
                {
                    _cachedAllTradesCount = currentCount;
                    var allArr = new StringBuilder("[");
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
                    allArr.Append("]");
                    _cachedAllTradesJson = allArr.ToString();
                }
            }

            decimal unrealizedPnl = 0m;
            int rollingHourTrades;
            lock (_lock)
            {
                foreach (var p in _positions.Values) unrealizedPnl += p.UnrealizedPnL(p.CurrentPrice);
                rollingHourTrades = GetRollingHourEntryCountLocked(DateTime.UtcNow);
            }
            decimal totalEquityPnl = _totalRealizedPnL + unrealizedPnl;
            decimal sizeMultiplier = GetDynamicSizeMultiplier();

            var __rebuildInProgressJson = (_rebuildInProgress ? "true" : "false");
            var __lastRebuildIso = (_lastRebuildUtc == DateTime.MinValue) ? "" : _lastRebuildUtc.ToString("o");
            var __rebuildMessageEsc = (_rebuildMessage ?? "").Replace("\"", "\\\"");

            var __sb = new StringBuilder();
            __sb.Append('{');
            __sb.AppendFormat("\"time\":\"{0:yyyy-MM-dd HH:mm:ss} PT\",", now);
            __sb.AppendFormat("\"et\":\"{0:HH:mm:ss} ET\",", et);
            __sb.AppendFormat("\"regime\":\"{0}\",", _marketRegime);
            __sb.Append("\"spyBullish\":").Append(_spyBullish ? "true" : "false").Append(',');
            __sb.Append("\"spyBearish\":").Append(_spyBearish ? "true" : "false").Append(',');
            __sb.Append("\"halted\":").Append(_haltTrading ? "true" : "false").Append(',');
            __sb.Append("\"haltReason\":\"").Append((_haltReason ?? "").Replace("\"", "\\\"")).Append("\",");
            __sb.Append("\"manualResume\":").Append(_manualResumeOverride ? "true" : "false").Append(',');
            __sb.Append("\"reconciled\":").Append(_reconciled ? "true" : "false").Append(',');
            __sb.AppendFormat("\"pnl\":{0:F2},", _totalRealizedPnL);
            __sb.AppendFormat("\"unrealizedPnl\":{0:F2},", unrealizedPnl);
            __sb.AppendFormat("\"totalEquityPnl\":{0:F2},", totalEquityPnl);
            __sb.AppendFormat("\"sizeMult\":{0:F2},", sizeMultiplier);
            __sb.Append("\"ddBreached\":").Append(IsUnrealizedDrawdownBreached() ? "true" : "false").Append(',');
            __sb.AppendFormat("\"goal\":{0:F2},", DAILY_PROFIT_GOAL);
            __sb.AppendFormat("\"maxLoss\":{0:F2},", MAX_DAILY_LOSS);
            __sb.AppendFormat("\"trades\":{0},", _tradesToday);
            __sb.AppendFormat("\"maxTrades\":{0},", MAX_TRADES_PER_DAY);
            __sb.AppendFormat("\"rollingHourTrades\":{0},", rollingHourTrades);
            __sb.AppendFormat("\"wins\":{0},", _winCount);
            __sb.AppendFormat("\"losses\":{0},", _lossCount);
            __sb.AppendFormat("\"wr\":{0:F1},", wr);
            __sb.AppendFormat("\"cash\":{0:F2},", cash);
            __sb.AppendFormat("\"budget\":{0:F2},", TOTAL_BUDGET);
            __sb.AppendFormat("\"initialBudget\":{0:F2},", TOTAL_BUDGET);
            __sb.Append("\"positions\":").Append(posArr).Append(',');
            __sb.Append("\"curve\":").Append(curveArr).Append(',');
            __sb.Append("\"watchlist\":").Append(wlArr).Append(',');
            __sb.Append("\"feed\":").Append(tradeArr).Append(',');
            __sb.Append("\"hist\":").Append(histArr).Append(',');
            __sb.Append("\"lifetimeCurve\":").Append(ltArr).Append(',');
            __sb.Append("\"allTrades\":").Append(_cachedAllTradesJson).Append(',');
            __sb.Append("\"rebuildInProgress\":").Append(__rebuildInProgressJson).Append(',');
            __sb.Append("\"lastRebuildUtc\":\"").Append(__lastRebuildIso).Append("\",");
            __sb.Append("\"rebuildMessage\":\"").Append(__rebuildMessageEsc).Append("\",");
            __sb.Append("\"blockedReasons\":").Append(blockedArr).Append(',');
            __sb.Append("\"lastBlocked\":").Append(lastBlockedArr).Append('}');
            return __sb.ToString();
        }
    }

    private const int LOG_LINES = 8;
    private readonly Queue<string> _logQueue = new Queue<string>();
    private int _dashTick = 0;

    public void Start()
    {
        Console.CursorVisible = false;
        Console.OutputEncoding = System.Text.Encoding.UTF8;
        //_dashboardTimer = new Timer(_ => PrintDetailedDashboard(), null, 0, 1000);
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

            // ── Risk management status line ──
            decimal unrealPnlConsole = 0m;
            lock (_lock) { foreach (var p in _positions.Values) unrealPnlConsole += p.UnrealizedPnL(p.CurrentPrice); }
            decimal totalEqConsole = _totalRealizedPnL + unrealPnlConsole;
            decimal sizeMult = GetDynamicSizeMultiplier();
            string ddFlag = IsUnrealizedDrawdownBreached() ? "⚠ BREACHED" : "OK";
            sb.AppendLine(
                $"  Unrealized: {(unrealPnlConsole >= 0 ? "+" : "")}{unrealPnlConsole:C2}   " +
                $"Total Equity: {(totalEqConsole >= 0 ? "+" : "")}{totalEqConsole:C2}   " +
                $"SizeMult: {sizeMult:P0}   " +
                $"DD-Breaker: {ddFlag}   " +
                $"ConsecLoss: {_consecutiveLosses}");
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
                $"  {"SYM",-5}  {"PRICE",8}  {"VWAP",8}  {"SMA20",8}  {"SMA50",8}  {"SMA100",8}" +
                $"  {"RSI",5}  {"GAP%",6}  {"VOL K",7}  {"ORB HI",8}  {"ORB LO",8}  TREND  SIGNAL");
            sb.AppendLine(new string('─', W));

            foreach (var sym in _watchlist)
            {
                _marketData.TryGetValue(sym, out var candles);
                var last = candles?.LastOrDefault();
                decimal price = last?.Close ?? 0m;
                if (price == 0m) continue;

                // Show DAILY SMA20/50/100 here (matching how SMA200 is already shown elsewhere) —
                // the strategies themselves still use the fast intraday SMA20/50/100 (ind.Sma*) for
                // entry timing; this console table is a human-readable trend view, not a signal feed.
                decimal sma20 = GetDailySma20(sym);
                decimal sma50 = GetDailySma50(sym);
                decimal sma100 = GetDailySma100(sym);
                double rsi = SafeRSI(candles, 14);
                _vwap.TryGetValue(sym, out decimal vwapVal);
                decimal prevClose = GetPrevDayClose(sym);
                decimal gapPct = prevClose > 0 ? (price - prevClose) / prevClose * 100 : 0;
                long volK = GetTodayVolume(sym) / 1000;
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
                    $"  {prefix}{sym,-5}  {price,8:C}  {vwapVal,8:C}  {sma20,8:C}  {sma50,8:C}  {sma100,8:C}" +
                    $"  {rsi,5:F1}  {gapStr,6}  {volK,6}K  {orbHi,8:C}  {orbLo,8:C}  {trend}  {sig}");
            }

            sb.AppendLine(new string('─', W));

            sb.AppendLine(
                $"  Budget:{TOTAL_BUDGET:C0}  PosSz:{POSITION_SIZE:C0}  " +
                $"MaxPos:{MAX_POSITIONS}  Cooldown:{COOLDOWN_SECONDS / 60}min  " +
                $"MinHold:{MIN_HOLD_SECONDS / 60}min  Risk:{RISK_PCT * 100:F0}%/trade  " +
                $"MaxLoss:{MAX_LOSS_PER_TRADE:C0}  ATRTrail:{ATR_TRAIL_MULT}x  " +
                $"ORBWindow:{ORB_MINUTES}min  VolExp:{VOL_EXPAND_MULT}x  " +
                $"Strategies: non-NW OR router | NW independent {NW_TIMEFRAME_MINUTES}m");
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
        // Standard MACD: signal line is the 9-period EMA of the MACD line itself
        // (EMA12 - EMA26), not a comparison against the raw MACD value from N bars
        // ago. That old approach doesn't smooth the comparison point at all, so it
        // was noisier and didn't match what "MACD direction" conventionally means.
        // Need 26 bars for the slow EMA to seed, plus ~9 more so the signal EMA
        // itself has warmed up.
        if (candles == null || candles.Count < 35) return 0;
        var closes = candles.Select(c => (double)c.Close).ToArray();

        double k12 = 2.0 / (12 + 1);
        double k26 = 2.0 / (26 + 1);

        double ema12 = closes.Take(12).Average();
        double ema26 = closes.Take(26).Average();

        var macdSeries = new List<double>();
        for (int i = 0; i < closes.Length; i++)
        {
            if (i >= 12) ema12 = closes[i] * k12 + ema12 * (1 - k12);
            if (i >= 26)
            {
                ema26 = closes[i] * k26 + ema26 * (1 - k26);
                macdSeries.Add(ema12 - ema26);
            }
        }

        if (macdSeries.Count < 9) return 0;

        double signal = CalcEMA(macdSeries.ToArray(), 9);
        double macdNow = macdSeries[macdSeries.Count - 1];
        double histogram = macdNow - signal;

        if (histogram > 0) return 1;
        if (histogram < 0) return -1;
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
            // Use live intraday 1-min data for intraday SMAs
            Sma20 = GetIntradaySma(symbol, 20),
            Sma50 = GetIntradaySma(symbol, 50),
            Sma100 = GetIntradaySma(symbol, 100),
            Ema9 = CalcEMA(closes, 9),
            Ema21 = CalcEMA(closes, 21),
            Ema9Prev = closesPrev.Length >= 9 ? CalcEMA(closesPrev, 9) : 0,
            Ema21Prev = closesPrev.Length >= 21 ? CalcEMA(closesPrev, 21) : 0,
            MacdDir = SafeMACDDirection(candles),
            // Breakout levels must exclude the candle being tested. Including
            // the current candle made close > RecentHigh8 and close < RecentLow8
            // impossible because a candle's close cannot exceed its own high or
            // fall below its own low.
            RecentHigh8 = SafeHighestHigh(candlesM1, 8),
            RecentLow8 = SafeLowestLow(candlesM1, 8),
            VolExpansion = CheckVolumeExpansion(candles),
        };
        _indicatorCache[symbol] = ind;

        // Diagnostic logging for suspicious symbols (NVDA, AAPL)
        try
        {
            if (string.Equals(symbol, "NVDA", StringComparison.OrdinalIgnoreCase)
                || string.Equals(symbol, "AAPL", StringComparison.OrdinalIgnoreCase))
            {
                var (pdHigh, pdLow) = GetPrevDayHL(symbol);
                decimal pdClose = GetPrevDayClose(symbol);
                var vwapNow = _vwap.GetValueOrDefault(symbol);
                LogMessage($"[DIAG] {symbol} bars={candles.Count} sma20={ind.Sma20:F3} sma50={ind.Sma50:F3} sma100={ind.Sma100:F3} atr={ind.Atr14:F3} prevHi={pdHigh:F2} prevLo={pdLow:F2} prevClose={pdClose:F2} vwap={vwapNow:F2}");
            }
        }
        catch { }
    }

    // Compute intraday SMA from live 1-min market data (preferred over any cached series)
    private decimal GetIntradaySma(string symbol, int period)
    {
        if (period <= 0) return 0m;
        if (!_marketData.TryGetValue(symbol, out var list) || list == null) return 0m;
        lock (list)
        {
            if (list.Count < period) return 0m;
            return list.TakeLast(period).Average(c => c.Close);
        }
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
        // Use the merged (historical + live "today") daily bar series so the
        // average is actually taken over up to 200 distinct trading days,
        // not just whatever days happen to be sitting in the 1-min live buffer.
        var bars = GetDailyBarsPreferLive(symbol);
        if (bars.Count >= 200) return SafeSMA(bars, 200);
        if (bars.Count > 0) return bars.Average(c => c.Close);

        // Last-resort fallback: previously cached value (e.g. from a Yahoo rebuild).
        if (_dailySmaCache.TryGetValue(symbol, out var cached) && cached.sma200 > 0) return cached.sma200;

        return 0m;
    }

    private decimal GetDailySma100(string symbol)
    {
        // Use the merged (historical + live "today") daily bar series so the
        // average is actually taken over up to 100 distinct trading days,
        // not just whatever days happen to be sitting in the 1-min live buffer.
        var bars = GetDailyBarsPreferLive(symbol);
        if (bars.Count >= 100) return SafeSMA(bars, 100);
        if (bars.Count > 0) return bars.Average(c => c.Close);

        // Last-resort fallback: previously cached value (e.g. from a Yahoo rebuild).
        if (_dailySmaCache.TryGetValue(symbol, out var cached) && cached.sma100 > 0) return cached.sma100;

        return 0m;
    }

    private decimal GetDailySma50(string symbol)
    {
        var bars = GetDailyBarsPreferLive(symbol);
        if (bars.Count >= 50) return SafeSMA(bars, 50);
        if (bars.Count > 0) return bars.Average(c => c.Close);

        if (_dailySmaCache.TryGetValue(symbol, out var cached) && cached.sma50 > 0) return cached.sma50;

        return 0m;
    }

    private decimal GetDailySma20(string symbol)
    {
        var bars = GetDailyBarsPreferLive(symbol);
        if (bars.Count >= 20) return SafeSMA(bars, 20);
        if (bars.Count > 0) return bars.Average(c => c.Close);

        if (_dailySmaCache.TryGetValue(symbol, out var cached) && cached.sma20 > 0) return cached.sma20;

        return 0m;
    }

    // Rebuild daily candles for the watchlist from Yahoo and persist them.
    private void RebuildAllSmaFromYahoo()
    {
        try
        {
            LogMessage("[REBUILD] Starting Yahoo daily rebuild for watchlist...");
            int succeeded = 0, failed = 0;
            foreach (var sym in _watchlist)
            {
                try
                {
                    var bars = FetchDailyFromYahoo(sym).GetAwaiter().GetResult();
                    if (bars != null && bars.Count > 0)
                    {
                        // Keep last ~250 bars
                        var take = bars.Count > 250 ? bars.TakeLast(250).ToList() : bars;
                        _dailyCandles[sym] = take;
                        // Compute daily SMAs and cache them
                        try
                        {
                            var closes = take.Select(c => c.Close).ToList();
                            decimal sma20 = closes.Count >= 20 ? closes.TakeLast(20).Average() : (closes.Count > 0 ? closes.Average() : 0m);
                            decimal sma50 = closes.Count >= 50 ? closes.TakeLast(50).Average() : (closes.Count > 0 ? closes.Average() : 0m);
                            decimal sma100 = closes.Count >= 100 ? closes.TakeLast(100).Average() : (closes.Count > 0 ? closes.Average() : 0m);
                            decimal sma200 = closes.Count >= 200 ? closes.TakeLast(200).Average() : (closes.Count > 0 ? closes.Average() : 0m);
                            _dailySmaCache[sym] = (sma20, sma50, sma100, sma200);
                        }
                        catch { }
                        LogMessage($"[REBUILD] {sym} seeded {take.Count} daily bars from Yahoo");
                        succeeded++;
                    }
                    else
                    {
                        LogMessage($"[REBUILD] {sym} yahoo returned no bars — skipping");
                        failed++;
                    }
                }
                catch (Exception ex)
                {
                    LogError("RebuildAllSmaFromYahoo", $"{sym}: {ex.Message}");
                    failed++;
                }
            }
            try { SaveDailyMemory(); } catch { }
            LogMessage($"[REBUILD] Done — success={succeeded} failed={failed}");
        }
        catch (Exception ex)
        {
            LogError("RebuildAllSmaFromYahoo", ex.Message);
        }
    }

    // Build the daily bar series used for SMA20/50/100/200 etc.
    //
    // BUGFIX: this previously returned ONLY the days derivable from the 1-min
    // live buffer (_marketData, capped at ~500 bars ≈ 1 trading day) whenever
    // that buffer was non-empty, and never fell back to the persisted
    // multi-day history (_dailyCandles, up to 250 days) even though that
    // history was available. Since _marketData has data during/after every
    // session, GetDailyBarsPreferLive was effectively always returning a
    // single "day" (today, built out of 1-min bars) — so every SMA100/SMA200
    // call downstream either returned 0 (SafeSMA needs `period` bars) or, in
    // GetDailySma100/200's own fallback, the average of a single day's price
    // action mislabeled as a 100/200-day moving average.
    //
    // Fix: merge the persisted daily history with the live-derived bar(s),
    // letting the live bar override only the same calendar date (so "today"
    // stays current) instead of live data replacing the whole history.
    private List<Candle> GetDailyBarsPreferLive(string symbol)
    {
        try
        {
            List<Candle> historical = new List<Candle>();
            if (_dailyCandles.TryGetValue(symbol, out var dlist) && dlist != null)
            {
                lock (dlist) { historical = dlist.ToList(); }
            }

            List<Candle> liveDaily = new List<Candle>();
            if (_marketData.TryGetValue(symbol, out var mlist))
            {
                lock (mlist)
                {
                    if (mlist.Count > 0)
                    {
                        var todayEtForLive = GetEasternTime().Date;
                        liveDaily = mlist
                            .GroupBy(c => c.Time.Date)
                            .Where(g => g.Key == todayEtForLive)   // see comment above: only TODAY is trustworthy here
                            .OrderBy(g => g.Key)
                            .Select(g => new Candle
                            {
                                Time = g.Key,
                                Open = g.First().Open,
                                High = g.Max(x => x.High),
                                Low = g.Min(x => x.Low),
                                Close = g.Last().Close,
                                Volume = g.Sum(x => x.Volume)
                            })
                            .ToList();
                    }
                }
            }

            if (historical.Count > 0)
            {
                // Keep historical bars for any date not already covered by the live buffer,
                // then layer the live (more current) bars on top, so "today" is up to date
                // while the long history used for SMA100/200 stays intact.
                var merged = historical.Where(h => !liveDaily.Any(l => l.Time.Date == h.Time.Date)).ToList();
                merged.AddRange(liveDaily);
                merged.Sort((a, b) => a.Time.CompareTo(b.Time));
                return merged;
            }

            if (liveDaily.Count > 0) return liveDaily;

            // No historical or live data at all — fetch daily series from Yahoo Finance
            var yahoo = FetchDailyFromYahoo(symbol).GetAwaiter().GetResult();
            if (yahoo != null && yahoo.Count > 0) return yahoo;
        }
        catch { }
        return new List<Candle>();
    }

    // Simple in-memory TTL cache for Yahoo responses
    private readonly ConcurrentDictionary<string, (DateTime fetchedAt, List<Candle> bars)> _yahooCache = new();

    private async Task<List<Candle>> FetchDailyFromYahoo(string symbol)
    {
        try
        {
            // TTL 30 minutes
            if (_yahooCache.TryGetValue(symbol, out var cached) && (DateTime.UtcNow - cached.fetchedAt).TotalMinutes < 30)
                return cached.bars;

            using var http = new HttpClient();
            http.DefaultRequestHeaders.UserAgent.ParseAdd("Mozilla/5.0 (compatible; TradeBot/1.0)");
            string url = $"https://query1.finance.yahoo.com/v8/finance/chart/{symbol}?range=1y&interval=1d";
            var res = await http.GetStringAsync(url);
            using var doc = JsonDocument.Parse(res);
            var root = doc.RootElement;
            if (!root.TryGetProperty("chart", out var chart)) return null;
            var result = chart.GetProperty("result")[0];
            var timestamps = result.GetProperty("timestamp").EnumerateArray().Select(t => t.GetInt64()).ToList();
            var indicators = result.GetProperty("indicators").GetProperty("quote")[0];
            var closes = indicators.GetProperty("close").EnumerateArray().Select(c => c.ValueKind == JsonValueKind.Null ? (decimal?)null : (decimal?)c.GetDecimal()).ToList();
            var opens = indicators.TryGetProperty("open", out var op) ? op.EnumerateArray().Select(c => c.ValueKind == JsonValueKind.Null ? (decimal?)null : (decimal?)c.GetDecimal()).ToList() : null;
            var highs = indicators.TryGetProperty("high", out var hi) ? hi.EnumerateArray().Select(c => c.ValueKind == JsonValueKind.Null ? (decimal?)null : (decimal?)c.GetDecimal()).ToList() : null;
            var lows = indicators.TryGetProperty("low", out var lo) ? lo.EnumerateArray().Select(c => c.ValueKind == JsonValueKind.Null ? (decimal?)null : (decimal?)c.GetDecimal()).ToList() : null;
            var volumes = indicators.TryGetProperty("volume", out var vol) ? vol.EnumerateArray().Select(c => c.ValueKind == JsonValueKind.Null ? (long?)null : (long?)c.GetInt64()).ToList() : null;

            var bars = new List<Candle>();
            for (int i = 0; i < timestamps.Count && i < closes.Count; i++)
            {
                if (!closes[i].HasValue) continue;
                var utc = DateTimeOffset.FromUnixTimeSeconds(timestamps[i]).UtcDateTime;
                // Convert to Eastern date
                var et = TimeZoneInfo.ConvertTimeBySystemTimeZoneId(utc, "Eastern Standard Time");
                var c = new Candle
                {
                    Time = new DateTime(et.Year, et.Month, et.Day),
                    Close = closes[i].Value,
                    Open = opens != null && opens.Count > i && opens[i].HasValue ? opens[i].Value : closes[i].Value,
                    High = highs != null && highs.Count > i && highs[i].HasValue ? highs[i].Value : closes[i].Value,
                    Low = lows != null && lows.Count > i && lows[i].HasValue ? lows[i].Value : closes[i].Value,
                    Volume = volumes != null && volumes.Count > i && volumes[i].HasValue ? volumes[i].Value : 0L
                };
                bars.Add(c);
            }

            _yahooCache[symbol] = (DateTime.UtcNow, bars);
            return bars;
        }
        catch { return null; }
    }


    // Resolves the most recent COMPLETED trading day's daily bar from the same
    // merged series used for SMA100/200 (IBKR persisted history + live-built
    // "today" bar, falling back to Yahoo Finance when IBKR has delivered
    // nothing at all for the symbol — see GetDailyBarsPreferLive/FetchDailyFromYahoo
    // above). Previously, Prev Day Hi/Lo/Close each had their own separate,
    // narrower data path (a tiny live 1-min buffer capped at ~1 session, and
    // a dictionary populated only from IBKR's own historical daily bars) with
    // no Yahoo fallback, so a symbol IBKR hadn't delivered data for yet would
    // just show stale/zero values instead of self-healing like SMA200 now does.
    private Candle GetMostRecentCompletedDailyBar(string symbol)
    {
        var bars = GetDailyBarsPreferLive(symbol);
        if (bars == null || bars.Count == 0) return null;

        var todayEt = GetEasternTime().Date;
        var prior = bars.Where(b => b.Time.Date < todayEt)
                         .OrderByDescending(b => b.Time)
                         .FirstOrDefault();
        if (prior != null) return prior;

        // No bar dated strictly before today (e.g. a brand-new/thinly-covered
        // symbol) — fall back to whatever the most recent bar is rather than
        // returning nothing.
        return bars.OrderByDescending(b => b.Time).FirstOrDefault();
    }

    private (decimal High, decimal Low) GetPrevDayHL(string symbol)
    {
        var prior = GetMostRecentCompletedDailyBar(symbol);
        if (prior != null && (prior.High > 0 || prior.Low > 0))
            return (prior.High, prior.Low);

        // Last-resort fallback: dictionaries populated by AddDailyCandle()
        _prevDayHighLevel.TryGetValue(symbol, out decimal h);
        _prevDayLowLevel.TryGetValue(symbol, out decimal l);
        return (h, l);
    }

    private decimal GetPrevDayClose(string symbol)
    {
        var prior = GetMostRecentCompletedDailyBar(symbol);
        if (prior != null && prior.Close > 0) return prior.Close;

        // Last-resort fallback: dictionary populated by AddDailyCandle()
        _prevDayClose.TryGetValue(symbol, out decimal c);
        return c;
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
        // Only fall back to the arbitrary 0.2%-of-price placeholder when there's
        // truly not enough data to compute even a single true-range value (need
        // at least 2 candles: current + previous). Otherwise compute a genuine
        // ATR over however many bars are actually available, instead of ignoring
        // real history whenever it's shorter than `period`.
        if (candles == null || candles.Count < 2)
            return candles?.LastOrDefault()?.Close * 0.002m ?? 0.01m;

        int effectivePeriod = Math.Min(period, candles.Count - 1);
        int start = candles.Count - effectivePeriod;
        decimal atr = 0m;
        for (int i = start; i < candles.Count; i++)
        {
            var c = candles[i];
            var prev = candles[i - 1];
            decimal tr = Math.Max(c.High - c.Low,
                         Math.Max(Math.Abs(c.High - prev.Close),
                                  Math.Abs(c.Low - prev.Close)));
            if (i == start) atr = tr;
            else atr = ((atr * (effectivePeriod - 1)) + tr) / effectivePeriod;
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

    // ══════════════════════════════════════════════════════════
    //  RISK MGMT: Unrealized Drawdown Circuit Breaker
    //  Computes aggregate PnL (realized + all open unrealized).
    //  Returns true if total drawdown exceeds threshold — blocks new entries.
    //  Does NOT close existing positions (stops handle that).
    // ══════════════════════════════════════════════════════════

    private decimal GetTotalEquityPnL()
    {
        decimal unrealized = 0m;
        lock (_lock)
        {
            foreach (var pos in _positions.Values)
                unrealized += pos.UnrealizedPnL(pos.CurrentPrice);
        }
        return _totalRealizedPnL + unrealized;
    }

    private bool IsUnrealizedDrawdownBreached()
    {
        if (_manualResumeOverride) return false;
        if (UNREALIZED_DD_HALT_THRESHOLD >= 0) return false; // disabled
        return GetTotalEquityPnL() <= UNREALIZED_DD_HALT_THRESHOLD;
    }

    // ══════════════════════════════════════════════════════════
    //  RISK MGMT: Strategy Allocation Limit
    //  Maps a strategy tag to its family for counting purposes.
    //  "PATTERN_BULL_ENGULFING_LONG" → "PATTERN"
    //  "ORB_LONG" → "ORB"
    //  "SCALP_PULLBACK_LONG" → "MICRO_PB"
    // ══════════════════════════════════════════════════════════

    private string GetStrategyFamily(string tag)
    {
        if (string.IsNullOrEmpty(tag)) return "UNKNOWN";
        // Known prefixes — order matters (longest match first)
        foreach (var prefix in new[] { "SWING_VCP_BREAKOUT_", "SWING_BASE_BREAKOUT_",
                                        "SCALP_PATTERN_", "SCALP_PULLBACK_", "SCALP_BREAKOUT_",
                                        "SCALP_VWAP_", "SCALP_ORB_", "SCALP_EMA_",
                                        "PATTERN_", "MICRO_PB_", "EMA_POCKET_",
                                        "OUTSIDE_CANDLE_", "MOMENTUM_", "VWAP_",
                                        "GAP_GO_", "ORB_", "BB_MR_", "MEAN_REV_", "NW_BAND_" })
        {
            if (tag.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                return prefix.TrimEnd('_');
        }
        // Fallback: take everything before the last _LONG / _SHORT
        int lastUnderscore = tag.LastIndexOf('_');
        if (lastUnderscore > 0)
        {
            string suffix = tag.Substring(lastUnderscore);
            if (suffix.Equals("_LONG", StringComparison.OrdinalIgnoreCase) ||
                suffix.Equals("_SHORT", StringComparison.OrdinalIgnoreCase))
                return tag.Substring(0, lastUnderscore);
        }
        return tag;
    }

    private bool IsScalpStrategy(string strategyTag)
    {
        return !string.IsNullOrWhiteSpace(strategyTag)
            && strategyTag.StartsWith("SCALP_", StringComparison.OrdinalIgnoreCase);
    }

    private bool IsSwingStrategy(string strategyTag)
    {
        return !string.IsNullOrWhiteSpace(strategyTag)
            && strategyTag.StartsWith("SWING_", StringComparison.OrdinalIgnoreCase);
    }

    private decimal GetStopDistanceForStrategy(string strategyTag, decimal atr, decimal price)
    {
        // V2 FIX: Original stops (0.70-0.85x ATR) were far too tight for 1-min candle noise.
        // Result: 30% of trades hit HARD_STOP instantly. Wider stops give the trade thesis
        // time to play out. Every stop-out costs $2 commission + the loss, so fewer stops
        // at slightly wider distance is strictly better than many tight stops.
        decimal minStop = Math.Max(MIN_STOP_DISTANCE, price * 0.0030m);

        // Nadaraya-Watson band strategy uses a flat percentage stop (user-configured),
        // not an ATR-based one — checked before the scalp/swing branches below.
        if (strategyTag.StartsWith("NW_BAND_", StringComparison.OrdinalIgnoreCase))
            return Math.Max(minStop, price * NW_STOP_LOSS_PCT);

        // All scalp strategies now get 1.4-1.8x ATR (was 0.70-0.85x)
        decimal scalpAtrMult = strategyTag.StartsWith("SCALP_ORB_", StringComparison.OrdinalIgnoreCase)
            ? 1.60m   // ORB needs room — opening volatility is wide
            : strategyTag.StartsWith("SCALP_BREAKOUT_", StringComparison.OrdinalIgnoreCase)
                ? 1.50m
                : 1.40m;  // default scalp

        if (IsScalpStrategy(strategyTag))
            return Math.Max(minStop, atr * scalpAtrMult);

        if (IsSwingStrategy(strategyTag))
            return Math.Max(Math.Max(MIN_STOP_DISTANCE, price * 0.0050m), atr * 1.50m);

        // Non-scalp, non-swing (ORB_LONG, MOMENTUM_LONG, etc.)
        return Math.Max(minStop, atr * HARD_STOP_ATR_MULT);
    }

    private decimal GetTargetMultipleForStrategy(string strategyTag)
    {
        // V2 FIX: With $2 round-trip commissions on a $4k account, targets of 1.45-1.65R
        // produced avg wins of $4.94 — barely above commission. Wider targets let
        // winners actually offset losers. You need fewer, bigger wins.
        if (IsSwingStrategy(strategyTag)) return SWING_TARGET_R_MULT;
        if (!IsScalpStrategy(strategyTag)) return 2.80m;  // was 2.40
        if (strategyTag.StartsWith("SCALP_ORB_", StringComparison.OrdinalIgnoreCase)) return 2.20m;  // was 1.65
        return 2.00m;  // was 1.55
    }

    private int GetMaxHoldSecondsForStrategy(string strategyTag)
    {
        // V2 FIX: Original 480-720s (8-12 min) forced exits before winners could run.
        // Winners averaged 17 min hold vs losers at 8 min — the bot was killing its own winners.
        if (IsSwingStrategy(strategyTag)) return Math.Max(1, SWING_MAX_HOLD_DAYS) * 24 * 60 * 60;
        if (!IsScalpStrategy(strategyTag)) return 5400;  // was 3600 (1hr) → now 1.5hr
        if (strategyTag.StartsWith("SCALP_ORB_", StringComparison.OrdinalIgnoreCase)) return 2400;  // was 720 (12min) → now 40min
        return 1800;  // was 480 (8min) → now 30min
    }

    private int GetScratchSecondsForStrategy(string strategyTag)
    {
        // V2 FIX: Scratch exits at 120s caused -$55.65 from 14 trades. The bot was
        // scratching positions that needed more time. Raise to 300s (5 min).
        return IsScalpStrategy(strategyTag) ? 300 : int.MaxValue;
    }

    private decimal GetScratchRForStrategy(string strategyTag)
    {
        // V2 FIX: Scratching at -0.10R was too aggressive — nearly every slight drawdown
        // triggered a scratch, locking in commission losses. Now only scratch at -0.40R
        // (which means it's clearly failing, not just normal noise).
        return IsScalpStrategy(strategyTag) ? -0.40m : decimal.MinValue;
    }

    private decimal GetStaleRForStrategy(string strategyTag)
    {
        // V2 FIX: Stale exit at 0.35R after 150s was killing trades still building.
        // Now only exit stale trades that are basically flat after 5 minutes.
        return IsScalpStrategy(strategyTag) ? 0.15m : decimal.MinValue;
    }

    private bool IsStrategyAtDailyLimit(string strategyTag)
    {
        if (MAX_TRADES_PER_STRATEGY <= 0) return false; // disabled
        string family = GetStrategyFamily(strategyTag);
        return _strategyTradeCount.GetValueOrDefault(family) >= MAX_TRADES_PER_STRATEGY;
    }

    private void IncrementStrategyCount(string strategyTag)
    {
        string family = GetStrategyFamily(strategyTag);
        _strategyTradeCount.AddOrUpdate(family, 1, (_, old) => old + 1);
    }

    private DateTime ParseTradeRecordTime(TradeRecord t)
    {
        if (t == null || string.IsNullOrWhiteSpace(t.Date)) return DateTime.MinValue;
        if (!DateTime.TryParse(t.Date, out var d)) return DateTime.MinValue;
        if (!string.IsNullOrWhiteSpace(t.Time) && TimeSpan.TryParse(t.Time, out var tod))
            return d.Date.Add(tod);
        return d.Date;
    }

    private bool IsSymbolCold(string symbol)
    {
        if (string.IsNullOrWhiteSpace(symbol)) return false;
        if (symbol.Equals("SPY", StringComparison.OrdinalIgnoreCase) ||
            symbol.Equals("QQQ", StringComparison.OrdinalIgnoreCase) ||
            symbol.Equals("IWM", StringComparison.OrdinalIgnoreCase))
            return false;

        // Do not let old March/April losses permanently blacklist a ticker.
        // A cold symbol must be recently and repeatedly failing.
        DateTime cutoff = GetEasternTime().Date.AddDays(-7);
        List<TradeRecord> recent;
        lock (_allTrades)
        {
            recent = _allTrades
                .Where(t => string.Equals(t.Symbol, symbol, StringComparison.OrdinalIgnoreCase))
                .Select(t => new { Trade = t, When = ParseTradeRecordTime(t) })
                .Where(x => x.When >= cutoff)
                .OrderByDescending(x => x.When)
                .Take(8)
                .Select(x => x.Trade)
                .ToList();
        }

        if (recent.Count < 5) return false;
        decimal pnl = recent.Sum(t => t.NetPnL);
        double winRate = recent.Count(t => t.NetPnL > 0) / (double)recent.Count;
        return pnl <= -20m && winRate < 0.25;
    }

    private bool IsStrategyCold(string strategyTag)
    {
        string family = GetStrategyFamily(strategyTag);
        if (string.IsNullOrWhiteSpace(family) || family.Equals("UNKNOWN", StringComparison.OrdinalIgnoreCase))
            return false;

        // Use a short rolling window only. This prevents the bot from missing
        // current valid trades just because an older version of the strategy was bad.
        DateTime cutoff = GetEasternTime().Date.AddDays(-7);
        List<TradeRecord> recent;
        lock (_allTrades)
        {
            recent = _allTrades
                .Where(t => GetStrategyFamily(t.Strategy ?? "") == family)
                .Select(t => new { Trade = t, When = ParseTradeRecordTime(t) })
                .Where(x => x.When >= cutoff)
                .OrderByDescending(x => x.When)
                .Take(12)
                .Select(x => x.Trade)
                .ToList();
        }

        if (recent.Count < 8) return false;
        decimal pnl = recent.Sum(t => t.NetPnL);
        double winRate = recent.Count(t => t.NetPnL > 0) / (double)recent.Count;
        return pnl <= -30m && winRate < 0.25;
    }

    // ══════════════════════════════════════════════════════════
    //  RISK MGMT: Trend Reversal Gate
    //  Checks if the stock's short-term trend structure is breaking
    //  AGAINST the proposed trade direction. Uses EMA9/EMA21 crossover
    //  and higher-low / lower-high structure on recent bars.
    //
    //  For LONGS: rejects when EMA9 crosses below EMA21 AND price is
    //             making lower highs (trend is turning down).
    //  For SHORTS: rejects when EMA9 crosses above EMA21 AND price is
    //              making higher lows (trend is turning up).
    //
    //  This does NOT interfere with early-entry or reversal-pattern
    //  strategies (patterns, micro-pullback) — those rely on actual
    //  reversal candle confirmation and get their own exemption.
    //  Only the trend-following strategies (ORB, momentum, gap-go,
    //  EMA pocket, VWAP) are filtered.
    // ══════════════════════════════════════════════════════════

    private bool IsTrendReversing(string symbol, bool isShort, List<Candle> candles)
    {
        if (!TREND_REVERSAL_GATE_ENABLED) return false;
        if (!_indicatorCache.TryGetValue(symbol, out var ind)) return false;
        if (candles == null || candles.Count < 12) return false;

        // EMA crossover direction
        bool ema9BelowEma21 = ind.Ema9 < ind.Ema21;
        bool ema9AboveEma21 = ind.Ema9 > ind.Ema21;
        bool emaCrossDown = ind.Ema9 < ind.Ema21 && ind.Ema9Prev >= ind.Ema21Prev;
        bool emaCrossUp = ind.Ema9 > ind.Ema21 && ind.Ema9Prev <= ind.Ema21Prev;

        // Structure: check last 8 bars for lower-highs or higher-lows
        var recent8 = candles.TakeLast(8).ToList();
        bool lowerHighs = false;
        bool higherLows = false;
        if (recent8.Count >= 8)
        {
            var first4 = recent8.Take(4).ToList();
            var last4 = recent8.Skip(4).ToList();
            decimal firstHalfHigh = first4.Max(c => c.High);
            decimal secondHalfHigh = last4.Max(c => c.High);
            decimal firstHalfLow = first4.Min(c => c.Low);
            decimal secondHalfLow = last4.Min(c => c.Low);
            decimal atr = ind.Atr14;
            // Only flag if the structural shift is meaningful (> 0.3 ATR)
            lowerHighs = secondHalfHigh < firstHalfHigh - atr * 0.3m;
            higherLows = secondHalfLow > firstHalfLow + atr * 0.3m;
        }

        if (!isShort)
        {
            // LONG entry: trend reversing down?
            // Need BOTH: EMA structure turning bearish AND price making lower highs
            if ((emaCrossDown || ema9BelowEma21) && lowerHighs) return true;
        }
        else
        {
            // SHORT entry: trend reversing up?
            if ((emaCrossUp || ema9AboveEma21) && higherLows) return true;
        }

        return false;
    }

    // Helper: extract trade direction from strategy tag
    private bool IsShortTag(string tag)
    {
        return tag != null && tag.EndsWith("_SHORT", StringComparison.OrdinalIgnoreCase);
    }

    // ══════════════════════════════════════════════════════════
    //  RISK MGMT: Dynamic Position Sizing Multiplier
    //  Returns a scale factor (0.0 – 1.0) that reduces risk
    //  proportionally as daily PnL approaches MAX_DAILY_LOSS.
    //  At PnL=0: returns 1.0 (full size)
    //  At PnL=MAX_DAILY_LOSS/2: returns 0.5 (half size)
    //  Never increases above 1.0 even when profitable.
    // ══════════════════════════════════════════════════════════

    private decimal GetDynamicSizeMultiplier()
    {
        if (!DYNAMIC_SIZING_ENABLED) return 1.0m;
        if (MAX_DAILY_LOSS >= 0) return 1.0m; // safety: MAX_DAILY_LOSS should be negative

        decimal totalPnl = GetTotalEquityPnL();
        if (totalPnl >= 0) return 1.0m; // winning — no reduction

        // Linear scale from 1.0 at PnL=0 to 0.25 at PnL=MAX_DAILY_LOSS
        // Clamp so it never goes below 0.25 (still trades, just tiny)
        decimal drawdownRatio = Math.Abs(totalPnl) / Math.Abs(MAX_DAILY_LOSS);
        decimal multiplier = 1.0m - drawdownRatio * 0.75m;
        return Math.Clamp(multiplier, 0.25m, 1.0m);
    }

    private int CalcQty(decimal price, decimal stopDistance, bool logIfScaled = true)
    {
        // V2 FIX: Raised floor from 0.0018 to 0.0030 — aligns with wider stops
        decimal minStop = Math.Max(MIN_STOP_DISTANCE, price * 0.0030m);
        if (stopDistance < minStop) stopDistance = minStop;

        // ── RISK MGMT: Dynamic sizing — scale down risk when in drawdown ──
        decimal sizeMultiplier = GetDynamicSizeMultiplier();
        decimal riskAmount = TOTAL_BUDGET * RISK_PCT * sizeMultiplier;
        int qty = (int)(riskAmount / stopDistance);

        decimal deployedCapital = _positions.Values.Sum(p => p.AvgPrice * p.Quantity)
                                + _pendingEntryCount * POSITION_SIZE;
        decimal remainingCash = Math.Max(0, TOTAL_BUDGET - deployedCapital);
        decimal effectiveSlot = Math.Min(POSITION_SIZE, remainingCash);
        int maxByBudget = price > 0 ? (int)(effectiveSlot / price) : 0;

        qty = Math.Min(qty, maxByBudget);
        if (qty <= 0 || qty > MAX_QTY_SANITY) return 0;

        if (logIfScaled && sizeMultiplier < 1.0m)
            LogMessage($"[DYNAMIC SIZE] Risk scaled to {sizeMultiplier:P0} — qty={qty} (drawdown protection)");

        return qty;
    }

    public async Task SendEmail(string subject, string body)
    {
        if (string.IsNullOrWhiteSpace(EmailPassword)) return;

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
    // EOF placeholder: no-op change to ensure file end context matches patching expectations
}
