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

    // Unsubscribes live tick + market data feed for a symbol.
    // Implement as a no-op if your adapter doesn't support it yet.
    void CancelMarketData(string symbol);

    // True once the IBKR socket handshake is complete (nextValidId has fired).
    bool IsReady { get; }

    // Sends reqPositions() to IBKR. The adapter must call
    // SimulatedBroker.OnPositionReceived() per position, then
    // SimulatedBroker.OnReconciliationComplete() when IBKR fires positionEnd().
    void RequestPositions();

    // Requests 1 year of daily (1-day bar) historical data for a symbol.
    // IbClient routes these bars to SimulatedBroker.AddDailyCandle().
    // Used to build the daily SMA200 trend filter without touching the
    // 1-min candle pipeline or consuming a live market data subscription slot.
    void RequestDailyHistoricalData(string symbol);
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
    public bool PartialExitDone { get; set; }    // first partial at +1.5%
    public bool PartialExitDone2 { get; set; }   // second partial at +2.5%
    public bool IsShort { get; set; }            // true = short position
    public string StrategyTag { get; set; } = ""; // which strategy opened this
    public decimal EntryCommission { get; set; } = 0m; // $1 paid at open — deducted from final close record

    // For shorts: PnL is inverted — profit when price falls
    public decimal UnrealizedPnL(decimal price) =>
        IsShort ? Quantity * (AvgPrice - price) : Quantity * (price - AvgPrice);
}

public class TrackedOrder
{
    public int OrderId;
    public string Symbol;
    public TradeSide Side;
    public int Qty;
    public bool IsShortEntry;  // true if this BUY is covering a short
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
    // Persisted so the UI history panel survives same-day restarts and matches _totalRealizedPnL
    public List<TradeRecord> CompletedTrades { get; set; } = new();
    // Persisted so CheckDailyReset() detects a new trading day on restart
    // and correctly clears yesterday's halt/PnL/counters.
    public DateTime LastVolumeResetDate { get; set; } = DateTime.MinValue;
}

// A completed (fully closed) trade — stored in-memory for the dashboard history panel
public class TradeRecord
{
    public string Symbol { get; set; }
    public string Side { get; set; }        // "LONG" or "SHORT"
    public string Strategy { get; set; }
    public int Qty { get; set; }
    public decimal Entry { get; set; }
    public decimal Exit { get; set; }
    public decimal NetPnL { get; set; }
    public decimal HoldMinutes { get; set; }
    public string ExitReason { get; set; }
    public string Time { get; set; }        // ET close time HH:mm
}

// One data point in the lifetime equity history — one entry per trading day
public class LifetimeEquityPoint
{
    public string Date { get; set; }         // "yyyy-MM-dd"
    public decimal AccountValue { get; set; } // TOTAL_BUDGET + cumulative PnL at close
    public decimal DailyPnL { get; set; }    // that day's realized PnL
}

// Tracks the Opening Range (first 30 min high/low) per symbol
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
    // ── Runtime-configurable fields (loaded from bot-config.json on startup) ──
    private decimal TOTAL_BUDGET = 4000m;
    private int MAX_POSITIONS = 2;
    private decimal POSITION_SIZE = 2000m;
    private int MIN_HOLD_SECONDS = 600;          // 10 min
    private decimal DAILY_PROFIT_GOAL = 300m;
    private decimal MAX_DAILY_LOSS = -150m;
    private int COOLDOWN_SECONDS = 1800;         // 30 min
    private decimal ATR_TRAIL_MULT = 2.5m;
    private decimal SHORT_ATR_TRAIL = 2.0m;
    private decimal HARD_STOP_ATR_MULT = 2.5m;  // raised from 2.0 — gives more room on high-beta stocks at open
    private decimal MAX_LOSS_PER_TRADE = 50m;
    private decimal COMMISSION_PER_SIDE = 1m;
    private decimal MIN_STOP_DISTANCE = 0.10m;
    private int MAX_QTY_SANITY = 500;
    private decimal RISK_PCT = 0.015m;
    private int ORB_MINUTES = 30;
    private decimal VOL_EXPAND_MULT = 1.2m;
    private double RSI_LONG_MIN = 55.0;
    private double RSI_SHORT_MAX = 43.0;
    private double RSI_OVERSOLD = 35.0;
    private double RSI_OVERBOUGHT = 65.0;
    private decimal GAP_GO_MIN_PCT = 0.010m;
    private decimal GAP_GO_REL_VOL = 1.8m;
    private int VWAP_CONFIRM_BARS = 2;
    private int MAX_TRADES_PER_DAY = 10;
    private decimal MIN_ATR_PCT = 0.0003m; // was 0.004 (daily threshold on 1-min bars — blocked ALL trades)
    // IBKR market data line budget.
    // DATA_LINES_PER_SYMBOL: how many lines ONE symbol uses (1 = reqMktData only,
    //   6 = reqMktData + reqRealTimeBars, adjust to match your IbClient adapter).
    // MAX_MARKET_DATA_LINES: hard ceiling — stay below your account limit (100 by default).
    private int DATA_LINES_PER_SYMBOL = 1;
    private int MAX_MARKET_DATA_LINES = 95;


    private bool _allowShorts = true; // set to true only if you have a margin account

    // ── STATE ──────────────────────────────────────────────
    public readonly ConcurrentDictionary<string, List<Candle>> _marketData = new();
    private Dictionary<string, SimPosition> _positions = new();
    private readonly ConcurrentDictionary<int, TrackedOrder> _ordersById = new();
    private readonly List<string> _tradeHistoryLog = new();
    private readonly List<TradeRecord> _completedTrades = new();  // structured history for dashboard
    private readonly Dictionary<string, DateTime> _lastTradeTime = new();
    private readonly Dictionary<string, long> _dailyVolume = new();
    private readonly ConcurrentDictionary<string, decimal> _latestTick = new();
    private readonly ConcurrentDictionary<string, Candle> _currentMinuteCandle = new();
    private readonly List<(DateTime time, decimal equity)> _equityCurve = new();
    private readonly List<LifetimeEquityPoint> _lifetimeEquity = new();
    private const string LIFETIME_EQUITY_FILE = "lifetime_equity.json";

    // Opening Range per symbol (resets daily)
    private readonly ConcurrentDictionary<string, OpeningRange> _orbRanges = new();

    // Gap-and-Go: track daily gap % per symbol
    private readonly ConcurrentDictionary<string, decimal> _dailyGapPct = new();

    // ── DAILY CANDLE DATA (1 bar per day, up to 250 days) ────────────────────
    // Populated by AddDailyCandle() which IbClient calls after RequestDailyHistoricalData().
    // Used exclusively for the daily SMA200 trend filter — completely separate from
    // the 1-min _marketData pipeline. Does NOT consume a live market data slot.
    private readonly ConcurrentDictionary<string, List<Candle>> _dailyCandles = new();

    // Previous day High/Low — derived from _dailyCandles on load and daily reset.
    // Used to detect key S/R levels (prev day high = resistance, prev day low = support).
    private readonly ConcurrentDictionary<string, decimal> _prevDayHighLevel = new();
    private readonly ConcurrentDictionary<string, decimal> _prevDayLowLevel = new();

    // Win / loss
    private int _winCount = 0;
    private int _lossCount = 0;

    // Market regime
    private string _marketRegime = "UNKNOWN";

    // VWAP (reset daily)
    private readonly ConcurrentDictionary<string, (decimal SumPV, long SumVol)> _vwapAccum = new();
    private readonly ConcurrentDictionary<string, decimal> _vwap = new();
    private readonly ConcurrentDictionary<string, decimal> _prevDayClose = new();

    // Previous bar VWAP tracking for crossover detection
    private readonly ConcurrentDictionary<string, bool> _prevBarAboveVwap = new();

    // Strategy tag staging — set before order, stamped onto position at fill
    private ConcurrentDictionary<string, string> _pendingStrategyTag = new();

    // Race-condition guard: counts orders submitted but not yet filled
    // Prevents 3 entries firing before any fill comes back (12:09 bug)
    private int _pendingEntryCount = 0;

    // Per-symbol loss tracking — doubles cooldown after a losing trade
    private readonly Dictionary<string, bool> _lastTradeWasLoss = new();
    // Per-symbol daily entry count — max 1 entry per symbol per day
    private readonly Dictionary<string, int> _dailyEntryCount = new();

    private decimal _totalRealizedPnL = 0m;
    private int _tradesToday = 0;
    private bool _haltTrading = false;
    private bool _eodSent = false;
    private DateTime _lastVolumeResetEt = DateTime.MinValue;
    private DateTime _lastMemorySave = DateTime.MinValue;
    private DateTime _lastStateSave = DateTime.MinValue;

    // ── RECONCILIATION ─────────────────────────────────────────────────────────
    // _reconciled blocks ExecuteStrategy until IBKR confirms its live positions.
    // _needsReconciliation is the one-shot trigger: set by LoadState(), consumed by
    // either nextValidId (if Connect comes after LoadState) or LoadState itself
    // (if Connect already happened first). Cleared in OnReconciliationComplete().
    private volatile bool _reconciled = false;
    private volatile bool _needsReconciliation = false;
    private readonly Dictionary<string, (int qty, decimal avgCost)> _ibkrPositionSnapshot = new();

    private static readonly TimeZoneInfo Eastern =
        TimeZoneInfo.FindSystemTimeZoneById("America/New_York");
    private static readonly TimeZoneInfo Pacific =
        TimeZoneInfo.FindSystemTimeZoneById("America/Vancouver");

    // ── EMAIL ──────────────────────────────────────────────
    private const string EmailFrom = "uygargunay@gmail.com";
    private const string EmailTo = "uygargunay@gmail.com";
    private static readonly string EmailPassword =
        Environment.GetEnvironmentVariable("BOT_EMAIL_PASS") ?? "sznd kafk nhec skqh";

    // ── WATCHLIST (mutable — updated at runtime via /api/config) ──────────────
    // 85 best-in-class symbols — fits within the 95-slot IBKR data budget.
    // Ranked by: daily volume, ATR, trend-following behaviour, fill quality.
    // Add more only after purchasing an IBKR Market Data Booster Pack.
    public string[] _watchlist =
    {
        // ── CORE ETFs (9) ─────────────────────────────────────────────────
        "SPY","QQQ","IWM","SMH","XLK","XLF","XLE","XBI","DIA",

        // ── MEGA CAP TECH (8) ─────────────────────────────────────────────
        "AAPL","MSFT","NVDA","META","AMZN","GOOGL","TSLA","ADBE",

        // ── SEMIS (8) ─────────────────────────────────────────────────────
        "AMD","AVGO","ARM","MU","AMAT","LRCX","QCOM","TSM",

        // ── CLOUD / SAAS (10) ─────────────────────────────────────────────
        "CRM","NOW","CRWD","PANW","PLTR","DDOG","NET","SNOW","APP","TTD",

        // ── FINTECH / CRYPTO (5) ──────────────────────────────────────────
        "COIN","MSTR","PYPL","HOOD","SOFI",

        // ── FINANCIALS (7) ────────────────────────────────────────────────
        "JPM","GS","MS","BAC","V","MA","BX",

        // ── HIGH-MOMENTUM GROWTH (10) ─────────────────────────────────────
        "NFLX","UBER","SHOP","MELI","BKNG","ABNB","INTU","ANET","DASH","SPOT",

        // ── ENERGY (3) ────────────────────────────────────────────────────
        "XOM","CVX","OXY",

        // ── BIOTECH / HEALTH (5) ──────────────────────────────────────────
        "UNH","ABBV","VRTX","REGN","GILD",

        // ── INDUSTRIALS / DEFENSE (7) ─────────────────────────────────────
        "CAT","DE","GE","HON","RTX","LMT","BA",

        // ── ADDITIONAL ALPHA (8) ──────────────────────────────────────────
        "ORCL","IBM","TXN","ASML","ZS","MDB","OKTA","XYZ"
    };

    // Dashboard timer
    // Tracks which symbols we've already called RequestHistoricalData for so
    // ApplyWatchlistDiff doesn't re-subscribe symbols already streaming.
    private readonly HashSet<string> _subscribedSymbols = new(StringComparer.OrdinalIgnoreCase);
    // Used to detect ORB_MINUTES changes so we can clear stale opening ranges.
    private int _previousOrbMinutes;

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

            // Hard stop runs on EVERY tick, outside the min-hold gate
            CheckHardStop(symbol, price);
            CheckExits(symbol, price);

            if ((DateTime.UtcNow - _lastMemorySave).TotalMinutes >= 1)
            {
                SaveMarketMemory();
                _lastMemorySave = DateTime.UtcNow;
            }
            // Save full state every 2 min so positions survive a crash between fills
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

    // ── VWAP accumulator — resets at 9:30 ET open each day ──
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

    // ── Opening Range tracker — builds first 30 min high/low ──
    private void UpdateOpeningRange(string symbol, decimal price, DateTime etNow)
    {
        // Reset at market open
        if (etNow.Hour == 9 && etNow.Minute == 30 && etNow.Second < 5)
        {
            _orbRanges[symbol] = new OpeningRange { High = price, Low = price, IsSet = false };
            return;
        }

        // Only accumulate during the first ORB_MINUTES of the session
        int minutesSinceOpen = (etNow.Hour - 9) * 60 + etNow.Minute - 30;
        if (minutesSinceOpen < 0 || minutesSinceOpen > ORB_MINUTES) return;

        var orb = _orbRanges.GetOrAdd(symbol, _ => new OpeningRange { High = price, Low = price });
        orb.High = Math.Max(orb.High, price);
        orb.Low = Math.Min(orb.Low, price);

        // Mark as fully set once the ORB window closes.
        // >= not == — if no tick arrives at exactly minute ORB_MINUTES the
        // == check would never fire and ORB strategy would be dead all day.
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

        // Snapshot prev-day close just before open (for gap calc)
        var nowEt = GetEasternTime();
        if (nowEt.Hour == 9 && nowEt.Minute == 29)
            _prevDayClose[symbol] = candle.Close;
        // NOTE: _dailyGapPct is now computed live in TryGapAndGoStrategy
        //       using _latestTick vs _prevDayClose, so it's always fresh

        // Track whether prev bar was above/below VWAP for crossover detection
        _vwap.TryGetValue(symbol, out decimal vwapNow);
        bool aboveVwap = vwapNow > 0 && candle.Close > vwapNow;
        _prevBarAboveVwap.TryGetValue(symbol, out bool wasAbove);
        _prevBarAboveVwap[symbol] = aboveVwap;

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
    }

    // ══════════════════════════════════════════════════════════
    //  EXECUTE STRATEGY — DISPATCHER
    // ══════════════════════════════════════════════════════════

    public void ExecuteStrategy(string symbol, bool prevBarAboveVwap = false, bool currBarAboveVwap = false)
    {
        CheckDailyReset();
        if (_haltTrading) return;

        // Skip entry logic for symbols no longer on the watchlist.
        // CheckHardStop / CheckExits still run via UpdateLiveTick to protect open positions.
        if (!_watchlist.Contains(symbol, StringComparer.OrdinalIgnoreCase)) return;

        // Block new entries until IBKR position snapshot is reconciled.
        // Prevents buying into already-open positions after a restart.
        if (!_reconciled)
        {
            LogMessage("[SKIP] Waiting for IBKR position reconciliation before trading.");
            return;
        }

        var nowEt = GetEasternTime();
        if (nowEt.DayOfWeek == DayOfWeek.Saturday || nowEt.DayOfWeek == DayOfWeek.Sunday) return;
        // No trading in first 30 min (9:30–10:00 ET) — ORB is still forming, spreads wide
        // Extra 15-min buffer (until 10:15 ET): spreads are still wide at 10:00 sharp,
        // institutional algos pile in at the open causing false breakouts and wide stops.
        // Both MU and OXY losses (7:00 PT = 10:00 ET) were caused by firing on the first tick.
        if (nowEt.Hour < 10 || (nowEt.Hour == 10 && nowEt.Minute < 15)) return;
        // No trading in last 30 min (15:30–16:00 ET) — liquidity dries up, slippage spikes
        if (nowEt.Hour > 15 || (nowEt.Hour == 15 && nowEt.Minute >= 30)) return;

        if (!_marketData.TryGetValue(symbol, out var candles) || candles.Count < 50)
        {
            LogMessage($"[SKIP] {symbol} not enough candles: {candles?.Count ?? 0}");
            return;
        }

        if (candles.TakeLast(300).Sum(c => c.Volume) < 50_000) return;

        // Skip stocks trading below $10 — too noisy, wide spreads, unreliable ATR signals
        decimal lastPrice = candles.Last().Close;
        if (lastPrice < 10m) return;

        lock (_lock)
        {
            if (_tradesToday >= MAX_TRADES_PER_DAY) return;
            if (_positions.ContainsKey(symbol)) return;
            // Include pending (submitted but unfilled) orders in the cap
            if (_positions.Count + _pendingEntryCount >= MAX_POSITIONS) return;

            // Hard budget guard: don't open a new position if remaining cash < POSITION_SIZE.
            // MAX_POSITIONS × POSITION_SIZE can exceed TOTAL_BUDGET (e.g. 3 × $2000 = $6000
            // on a $4000 budget) — this check ensures we never actually overspend.
            decimal deployedCapital = _positions.Values.Sum(p => p.AvgPrice * p.Quantity)
                                    + _pendingEntryCount * POSITION_SIZE; // conservative: reserve a full slot per pending order
            if (TOTAL_BUDGET - deployedCapital < POSITION_SIZE) return;
            if (_lastTradeTime.TryGetValue(symbol, out var lastTime))
            {
                // Double cooldown if the last trade on this symbol was a loss
                int cooldown = _lastTradeWasLoss.GetValueOrDefault(symbol)
                    ? COOLDOWN_SECONDS * 2
                    : COOLDOWN_SECONDS;
                if ((DateTime.UtcNow - lastTime).TotalSeconds < cooldown) return;
            }
            // Max 2 entries per symbol per day
            if (_dailyEntryCount.GetValueOrDefault(symbol) >= 2) return;

            int minutesSinceOpen = (nowEt.Hour - 9) * 60 + nowEt.Minute - 30;

            // ── STRATEGY DISPATCH ─────────────────────────────
            // Try each strategy in priority order. First one to fire wins.

            // 1. Opening Range Breakout (only after ORB window closes)
            if (minutesSinceOpen > ORB_MINUTES)
                if (TryOrbStrategy(symbol, candles, nowEt)) return;

            // 2. Gap-and-Go (10:15–11:00 ET only — starts after new 15-min buffer, ends before mid-session)
            // Was minutesSinceOpen <= 60 which fired right at 10:00 ET (worst minute of the day).
            if (minutesSinceOpen >= 45 && minutesSinceOpen <= 90)
                if (TryGapAndGoStrategy(symbol, candles)) return;

            // 3. VWAP Bounce / Reclaim
            if (TryVwapBounceStrategy(symbol, candles, prevBarAboveVwap, currBarAboveVwap)) return;

            // 4. RSI Mean Reversion
            if (TryMeanReversionStrategy(symbol, candles)) return;

            // 5. Momentum Breakout + Continuation (original, now relaxed)
            TryMomentumStrategy(symbol, candles);
        }
    }

    // ══════════════════════════════════════════════════════════
    //  STRATEGY 1 — OPENING RANGE BREAKOUT (ORB)
    // ══════════════════════════════════════════════════════════

    private bool TryOrbStrategy(string symbol, List<Candle> candles, DateTime nowEt)
    {
        if (!_orbRanges.TryGetValue(symbol, out var orb) || !orb.IsSet) return false;
        if (orb.High <= orb.Low) return false;

        var lastCandle = candles.Last();
        decimal close = lastCandle.Close;
        decimal atr = SafeATR(candles, 14);
        if (close > 0 && atr / close < MIN_ATR_PCT) return false;
        double rsi = SafeRSI(candles, 14);
        _vwap.TryGetValue(symbol, out decimal vwapVal);

        // Lighter volume check: last bar volume >= 10-bar average catches fake pokes
        // without requiring the full 5-bar expansion which is too strict on slow mornings.
        var last10Vols = candles.TakeLast(10).ToList();
        long avgVol10 = last10Vols.Count > 0 ? (long)last10Vols.Average(c => c.Volume) : 0;
        bool lastBarVolOk = candles.Last().Volume >= avgVol10;

        // ── LONG: price breaks above ORB high ─────────────────
        if (close > orb.High && lastBarVolOk && rsi > RSI_LONG_MIN)
        {
            // In SELL-OFF, only allow longs if the stock is showing genuine sector
            // strength (up on the day while SPY is down — e.g. XOM on an oil spike).
            if (_marketRegime == "SELL-OFF" && !CheckStrongRelativeStrength(symbol, candles))
                goto TryShortOrb;

            // Skip if price is within 0.25% BELOW prev day high — too close to
            // resistance ceiling. A real breakout clears prev day high convincingly.
            var (pdHigh, _) = GetPrevDayHL(symbol);
            if (pdHigh > 0 && close < pdHigh && close >= pdHigh * 0.9975m) goto TryShortOrb;

            decimal stopDistance = Math.Max(atr * HARD_STOP_ATR_MULT, MIN_STOP_DISTANCE);
            int qty = CalcQty(close, stopDistance);
            if (qty <= 0) return false;

            OpenPosition(symbol, qty, close, TradeSide.Buy, false, "ORB_LONG");
            return true;
        }

        TryShortOrb:
        // ── SHORT: price breaks below ORB low ──────────────────
        if (close < orb.Low && lastBarVolOk && rsi < RSI_SHORT_MAX)
        {
            // Skip if price is within 0.25% ABOVE prev day low — too close to
            // support floor. A real breakdown holds convincingly below prev day low.
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
        // Compute gap live: current price vs previous day close
        if (!_prevDayClose.TryGetValue(symbol, out decimal prevClose) || prevClose <= 0) return false;
        if (!_latestTick.TryGetValue(symbol, out decimal currentPrice) || currentPrice <= 0) return false;
        decimal gapPct = (currentPrice - prevClose) / prevClose;

        if (Math.Abs(gapPct) < GAP_GO_MIN_PCT) return false;

        // Per-minute rate comparison — avoids partial-day bias (45-min volume vs full-day avg
        // made the threshold impossible to pass every morning).
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
        double rsi = SafeRSI(candles, 14);
        decimal atr = SafeATR(candles, 14);
        bool volExp = CheckVolumeExpansion(candles);
        if (close > 0 && atr / close < MIN_ATR_PCT) return false;
        if (gapPct > 0 && rsi > RSI_LONG_MIN && volExp && (_marketRegime != "SELL-OFF" || CheckStrongRelativeStrength(symbol, candles)))
        {
            // SMA200: allow within 3% below — only block deep structural downtrends
            decimal sma200 = GetDailySma200(symbol);
            if (sma200 > 0 && close < sma200 * 0.97m) return false;

            decimal stopDistance = Math.Max(atr * HARD_STOP_ATR_MULT, MIN_STOP_DISTANCE);
            int qty = CalcQty(close, stopDistance);
            if (qty <= 0) return false;

            OpenPosition(symbol, qty, close, TradeSide.Buy, false, "GAP_GO_LONG");
            return true;
        }

        // Gap DOWN → short
        if (gapPct < 0 && rsi < RSI_SHORT_MAX && volExp)
        {
            // Daily SMA200: prefer stocks already in downtrends for shorts.
            // Exception: in SELL-OFF regime stocks above SMA200 can still crash hard
            // intraday (e.g. AAPL/MSFT on a broad selloff day) — don't block those.
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

        // Need the last VWAP_CONFIRM_BARS candles ALL above VWAP — not just a 1-bar flicker
        if (candles.Count < VWAP_CONFIRM_BARS) return false;
        var recentBars = candles.TakeLast(VWAP_CONFIRM_BARS).ToList();
        bool allRecentAbove = recentBars.All(c => c.Close > vwapVal);
        bool allRecentBelow = recentBars.All(c => c.Close < vwapVal);

        var lastCandle = candles.Last();
        decimal close = lastCandle.Close;
        double rsi = SafeRSI(candles, 14);
        decimal atr = SafeATR(candles, 14);
        bool volExp = CheckVolumeExpansion(candles);
        if (close > 0 && atr / close < MIN_ATR_PCT) return false;
        bool vwapReclaim = !prevAbove && allRecentAbove;
        // VWAP reclaim long: the crossover is itself the signal — volExp not required
        if (vwapReclaim && rsi > RSI_LONG_MIN && (_marketRegime != "SELL-OFF" || CheckStrongRelativeStrength(symbol, candles)))
        {
            // Skip if price is pinned just under prev day high — VWAP reclaim into
            // overhead resistance is a low-probability long setup.
            var (pdHigh, _) = GetPrevDayHL(symbol);
            if (pdHigh > 0 && close < pdHigh && close >= pdHigh * 0.997m) return false;

            decimal stopDistance = Math.Max(atr * HARD_STOP_ATR_MULT, MIN_STOP_DISTANCE);
            int qty = CalcQty(close, stopDistance);
            if (qty <= 0) return false;
            OpenPosition(symbol, qty, close, TradeSide.Buy, false, "VWAP_RECLAIM");
            return true;
        }

        // Rejection: previous bar above, now 2 bars confirmed below
        bool vwapRejection = prevAbove && allRecentBelow;
        if (vwapRejection && volExp && rsi < RSI_SHORT_MAX && _allowShorts)
        {
            // Skip if price is just above prev day low — shorting into support floor
            // is a low-probability short setup.
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

        var lastCandle = candles.Last();
        decimal close = lastCandle.Close;
        decimal sma50 = SafeSMA(candles, 50);
        double rsi = SafeRSI(candles, 14);
        decimal atr = SafeATR(candles, 14);
        if (close > 0 && atr / close < MIN_ATR_PCT) return false;
        // Stock is in uptrend (above SMA50) but RSI has dipped — buy the dip
        bool oversoldInUptrend = close > sma50 && rsi < RSI_OVERSOLD
                               && (_marketRegime != "SELL-OFF" || CheckStrongRelativeStrength(symbol, candles));
        if (oversoldInUptrend)
        {
            decimal stopDistance = Math.Max(atr * HARD_STOP_ATR_MULT, MIN_STOP_DISTANCE);
            int qty = CalcQty(close, stopDistance);
            if (qty <= 0) return false;

            OpenPosition(symbol, qty, close, TradeSide.Buy, false, "MEAN_REV_LONG");
            return true;
        }

        // ── OVERBOUGHT FADE → short ───────────────────────────
        // Stock is in downtrend (below SMA50) and RSI has spiked — fade the rally
        bool overboughtInDowntrend = close < sma50 && rsi > RSI_OVERBOUGHT;
        if (overboughtInDowntrend)
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
        decimal sma20 = SafeSMA(candles, 20);
        decimal sma50 = SafeSMA(candles, 50);
        double rsi = SafeRSI(candles, 14);
        decimal atr = SafeATR(candles, 14);
        decimal atrPct = close > 0 ? atr / close : 0m;
        bool volExp = CheckVolumeExpansion(candles);

        if (atrPct < MIN_ATR_PCT) return false;

        // ── SCORE-BASED LONG ENTRY (need 3 of 5 conditions) ───
        _vwap.TryGetValue(symbol, out decimal vwapVal);

        bool regimeStrong = _marketRegime != "SELL-OFF"
                         || CheckStrongRelativeStrength(symbol, candles); // sector strength overrides SELL-OFF for longs
        bool relativeStrength = CheckRelativeStrength(symbol, candles);
        bool rsiConfirm = rsi > RSI_LONG_MIN;
        bool aboveVwap = vwapVal <= 0 || close > vwapVal;

        // MACD direction: +1 when momentum is building in the long direction.
        // Used as a score factor rather than a hard block so we don't miss trades
        // when MACD is temporarily flat/neutral (common mid-session).
        int macdDir = SafeMACDDirection(candles);
        bool macdBullish = macdDir >= 0; // neutral counts as OK for longs

        // Breakout or pullback-continuation signals
        decimal recentHigh = SafeHighestHigh(candles.Take(candles.Count - 1).ToList(), 8);
        decimal range = lastCandle.High - lastCandle.Low;
        decimal avgRange = candles.TakeLast(10).Average(c => c.High - c.Low);
        bool expansion = range > avgRange * 1.3m; // lowered from 1.8 — 1.3x still confirms above-avg momentum
        bool choppyMode = _marketRegime == "CHOPPY";

        bool pullbackEntry = lastCandle.Low <= sma20 && lastCandle.Close > sma20
                              && rsi > 55 && volExp;
        bool breakoutSignal = !choppyMode && expansion && volExp && close > recentHigh;

        // trendContinuation: no volExp required — grinding days don't spike volume.
        // UNKNOWN included so it works during startup before SPY builds 30 bars.
        bool trendContinuation = (_marketRegime == "NORMAL" || _marketRegime == "TRENDING" || _marketRegime == "UNKNOWN")
                               && close > sma20 && close > sma50
                               && rsi > 58 && relativeStrength;

        bool hasSignal = breakoutSignal || pullbackEntry || trendContinuation;

        int required = (_marketRegime == "CHOPPY" || _marketRegime == "TRENDING") ? 3 : 4;

        // Score: max 5 points
        int score = (regimeStrong ? 1 : 0)
                  + (relativeStrength ? 1 : 0)
                  + (rsiConfirm ? 1 : 0)
                  + (aboveVwap ? 1 : 0)
                  + (macdBullish ? 1 : 0);

        // trendContinuation bypasses volExp — designed for low-volume grind days
        if (score >= required && hasSignal && (volExp || trendContinuation))
        {
            // SMA200: allow within 3% below — only block deep structural downtrends
            decimal sma200 = GetDailySma200(symbol);
            if (sma200 > 0 && close < sma200 * 0.97m) return false;

            decimal stopDistance = Math.Max(atr * HARD_STOP_ATR_MULT, MIN_STOP_DISTANCE);
            int qty = CalcQty(close, stopDistance);
            if (qty <= 0) return false;

            OpenPosition(symbol, qty, close, TradeSide.Buy, false, "MOMENTUM_LONG");
            return true;
        }

        // Shorts allowed in SELL-OFF, NORMAL, CHOPPY — individual stocks break down on any regime.
        // Require score≥4 outside SELL-OFF for extra selectivity.
        bool inShortRegime = _marketRegime == "SELL-OFF" || _marketRegime == "NORMAL" || _marketRegime == "CHOPPY";
        if (inShortRegime && _allowShorts)
        {
            bool rsiShortConfirm = rsi < RSI_SHORT_MAX;
            bool belowVwap = vwapVal > 0 && close < vwapVal;
            bool breakdownSignal = !choppyMode && expansion && volExp
                                   && close < SafeLowestLow(candles, 8);
            bool macdBearish = macdDir <= 0; // neutral counts as OK for shorts

            int shortScore = (rsiShortConfirm ? 1 : 0)
                           + (!relativeStrength ? 1 : 0)  // weak vs SPY
                           + (belowVwap ? 1 : 0)
                           + (volExp ? 1 : 0)
                           + (macdBearish ? 1 : 0);

            int shortRequired = _marketRegime == "SELL-OFF" ? 3 : 4;
            if (shortRequired <= shortScore && breakdownSignal)
            {
                // Daily SMA200: prefer stocks already in downtrends.
                // In SELL-OFF regime, skip — stocks can be above SMA200 and still crash.
                decimal sma200 = GetDailySma200(symbol);
                if (sma200 > 0 && close > sma200 && _marketRegime != "SELL-OFF") return false;

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
    //  POSITION OPENING HELPER
    // ══════════════════════════════════════════════════════════

    private void OpenPosition(string symbol, int qty, decimal price,
                           TradeSide side, bool isShort, string strategyTag)
    {
        // Guard against nulls before touching any dictionary
        if (string.IsNullOrEmpty(symbol) || string.IsNullOrEmpty(strategyTag)) return;

        // Block short selling if not enabled or no margin account
        if (isShort && !_allowShorts) return;

        if (qty <= 0 || qty > MAX_QTY_SANITY) return;

        // Store the strategy tag so OnOrderFilled can stamp it onto the SimPosition
        // Lazy-init guard: if field is null for any reason, recreate it before use
        if (_pendingStrategyTag == null)
            _pendingStrategyTag = new ConcurrentDictionary<string, string>();
        _pendingStrategyTag[symbol] = strategyTag;

        // Reserve a slot immediately so concurrent candle events can't over-enter
        Interlocked.Increment(ref _pendingEntryCount);
        // Track how many times this symbol has been entered today
        lock (_lock)
        {
            _dailyEntryCount[symbol] = _dailyEntryCount.GetValueOrDefault(symbol) + 1;
        }

        SubmitOrder(symbol, qty, price, side, strategyTag);
        LogMessage($"[{strategyTag}] {symbol} x{qty} @ {price:F2} | regime={_marketRegime}");
    }
    // ══════════════════════════════════════════════════════════
    //  EXIT LOGIC
    // ══════════════════════════════════════════════════════════

    // Hard stop — runs every tick, no min-hold gate
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

            // Dollar stop fires immediately (protects against flash crashes)
            bool dollarStopHit = unrealizedLoss <= -MAX_LOSS_PER_TRADE;

            // ATR stop has a 5-minute immunity window — matches MIN_HOLD_SECONDS
            // 2-minute window was stopping ABNB/GS/COIN on normal open volatility
            bool atrStopHit = false;
            if (secondsHeld >= MIN_HOLD_SECONDS)
            {
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
            }

            if (atrStopHit || dollarStopHit)
            {
                pos.ExitSubmitted = true;
                string reason = dollarStopHit ? "MAX_LOSS_STOP" : "HARD_STOP";
                TradeSide exitSide = pos.IsShort ? TradeSide.Buy : TradeSide.Sell;
                SubmitOrder(symbol, pos.Quantity, currentPrice, exitSide, reason);
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

            // Net P&L after full $2 round-trip commission ($1 entry already paid + $1 exit to come).
            // All exit thresholds below use this so they reflect real account impact.
            decimal grossPnl = pos.IsShort
                ? (pos.AvgPrice - currentPrice) * pos.Quantity
                : (currentPrice - pos.AvgPrice) * pos.Quantity;
            decimal netPnl = grossPnl - (COMMISSION_PER_SIDE * 2);
            decimal positionCost = pos.AvgPrice * pos.Quantity;
            decimal gainPct = positionCost > 0 ? netPnl / positionCost : 0m;

            // Track best price (HWM = highest price for longs, lowest for shorts)
            if (pos.IsShort)
                pos.HighWaterMark = Math.Min(
                    pos.HighWaterMark == 0 ? currentPrice : pos.HighWaterMark, currentPrice);
            else
                pos.HighWaterMark = Math.Max(pos.HighWaterMark, currentPrice);

            // ── PARTIAL 1: net +1.5% → sell half ──────────────
            if (gainPct >= 0.015m && !pos.PartialExitDone && pos.Quantity >= 2)
            {
                int halfQty = pos.Quantity / 2;
                // Do NOT decrement pos.Quantity here — OnOrderFilled does it when
                // the fill arrives. Pre-decrementing caused isFullClose to be true
                // for any even-size position, removing it before the other half sold.
                pos.PartialExitDone = true;
                SubmitOrder(symbol, halfQty, currentPrice, exitSide, "PARTIAL_TP_1");
                return;
            }

            // ── PARTIAL 2: net +2.5% → sell another quarter ───
            if (gainPct >= 0.025m && !pos.PartialExitDone2 && pos.Quantity >= 2)
            {
                int quarterQty = Math.Max(1, pos.Quantity / 2);
                // Same: let OnOrderFilled handle the quantity decrement on fill.
                pos.PartialExitDone2 = true;
                SubmitOrder(symbol, quarterQty, currentPrice, exitSide, "PARTIAL_TP_2");
                return;
            }

            // ── TIME STOP: 60 min, net gain < 0.5% ────────────
            if (secondsHeld > 3600 && gainPct < 0.005m)
            {
                pos.ExitSubmitted = true;
                SubmitOrder(symbol, pos.Quantity, currentPrice, exitSide, "TIME_STOP");
                return;
            }

            // ── ATR TRAILING STOP ─────────────────────────────
            decimal trailMult = pos.IsShort ? SHORT_ATR_TRAIL : ATR_TRAIL_MULT;

            bool trailHit;
            if (pos.IsShort)
            {
                decimal trailStop = pos.HighWaterMark + atrValue * trailMult;
                trailHit = currentPrice > trailStop && gainPct > 0.005m;
            }
            else
            {
                decimal trailStop = pos.HighWaterMark - atrValue * trailMult;
                trailHit = currentPrice < trailStop && gainPct > 0.005m;
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

            // Round to valid tick size — 2dp for stocks >= $1, 4dp for penny stocks
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

    // Called by IbClient.error() on IBKR order rejection/cancellation.
    // Frees the pending slot so the bot can enter new positions again.
    public void OnOrderRejected(int orderId)
    {
        if (!_ordersById.TryRemove(orderId, out var order)) return;
        // Decrement only for entry orders — exits don't consume a pending slot.
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
            // ── OPENING A POSITION ────────────────────────────
            // Long entry = BUY; Short entry = SELL (and flagged IsShortEntry)
            bool isShortEntry = order.Side == TradeSide.Sell && !_positions.ContainsKey(order.Symbol);
            bool isLongEntry = order.Side == TradeSide.Buy && !_positions.ContainsKey(order.Symbol);

            if (isLongEntry || isShortEntry)
            {
                // Fill received — pending slot is now a real position
                Interlocked.Decrement(ref _pendingEntryCount);

                string tag = "";
                _pendingStrategyTag?.TryRemove(order.Symbol, out tag);
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
                    EntryCommission = COMMISSION_PER_SIDE  // track $1 entry commission for final record
                };
                _tradesToday++;

                // $1 entry commission — charged the moment the buy/short order fills
                _totalRealizedPnL -= COMMISSION_PER_SIDE;

                string dir = isShortEntry ? "SHORT" : "BUY";
                subject = $"🚀 {dir}: {order.Symbol} x{fillQty} @ {fillPrice:C2}";
                body = $"{dir} {fillQty} shares @ {fillPrice:C2}  (commission: -$1)";
            }
            // ── CLOSING A POSITION ────────────────────────────
            else if (_positions.TryGetValue(order.Symbol, out var pos))
            {
                decimal grossPnl = pos.IsShort
                    ? (pos.AvgPrice - fillPrice) * fillQty
                    : (fillPrice - pos.AvgPrice) * fillQty;

                // $1 exit commission — entry $1 was already charged at open
                decimal netPnl = grossPnl - COMMISSION_PER_SIDE;

                decimal holdMinutes = (decimal)(DateTime.UtcNow - pos.EntryTime).TotalMinutes;

                _totalRealizedPnL += netPnl;

                // Only count win/loss and remove position on FULL close
                // Decrement on actual fill — not before (see CheckExits fix).
                pos.Quantity -= fillQty;
                bool isFullClose = pos.Quantity <= 0;

                // Determine exit reason (sniff from last log line)
                string exitReason = isFullClose ? "EXIT" : "PARTIAL";
                foreach (var tag in new[]{ "ATR_TRAIL_EXIT","TIME_STOP","HARD_STOP",
                                           "MAX_LOSS_STOP","PARTIAL_TP_1","PARTIAL_TP_2","EOD_LIQUIDATE" })
                    if (_tradeHistoryLog.LastOrDefault()?.Contains(tag) == true)
                    { exitReason = tag; break; }

                // On the final close, also absorb the entry commission so that the sum
                // of all TradeRecord.NetPnL values for this position equals the true
                // round-trip P&L (entryComm + all exit comms + gross P&L).
                // Partial fills only carry their own exit commission.
                decimal recordedNetPnl = isFullClose
                    ? netPnl - pos.EntryCommission
                    : netPnl;

                // Record EVERY fill (partial and full) so the history panel is complete.
                // Previously only full closes were recorded — this hid partial-exit fills
                // like COIN's first sell (5@185.78, +$13.35) from the dashboard entirely.
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
                    Time = GetEasternTime().ToString("HH:mm")
                });
                if (_completedTrades.Count > 50) _completedTrades.RemoveAt(0);

                if (isFullClose)
                {
                    // Win/loss on full round-trip net (entry + exit commission both included)
                    if (recordedNetPnl > 0) _winCount++;
                    else _lossCount++;

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
        _pendingEntryCount = 0;
        _completedTrades.Clear();
        _lastVolumeResetEt = nowEt.Date;
        _eodSent = false;
        _haltTrading = false;
        _tradesToday = 0;
        _winCount = 0;
        _lossCount = 0;
        _totalRealizedPnL = 0m;
        LogMessage($"[DAY RESET] {nowEt:yyyy-MM-dd} — new session started");
    }

    public void CheckEndOfDay()
    {
        var now = GetEasternTime();
        if (!_eodSent && now.Hour == 15 && now.Minute >= 30)
        {
            _haltTrading = true;
            _eodSent = true;

            // Snapshot today's closing account value into the lifetime equity file
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

    public void SaveState()
    {
        try
        {
            File.WriteAllText("bot_state.json", JsonSerializer.Serialize(new BotPersistData
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
                CompletedTrades = _completedTrades.ToList(),
                LastVolumeResetDate = _lastVolumeResetEt
            }));
        }
        catch (Exception ex) { LogError("SaveState", ex.Message); }
    }
    public void LoadState()
    {
        if (!File.Exists("bot_state.json"))
        {
            // No state file — still query IBKR positions on startup.
            // The file may have been deleted, or IBKR may hold positions from a previous session.
            // Keep _reconciled = false so ExecuteStrategy() is blocked until positionEnd() confirms.
            _needsReconciliation = true;
            if (RealBroker?.IsReady == true)
                RealBroker.RequestPositions();
            return;
        }
        try
        {
            var data = JsonSerializer.Deserialize<BotPersistData>(
                           File.ReadAllText("bot_state.json"));
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
            // Restore trade history so the dashboard stays consistent with _totalRealizedPnL
            // after a same-day restart (without this the header total and history panel diverge).
            if (data.CompletedTrades?.Count > 0)
            {
                _completedTrades.Clear();
                foreach (var t in data.CompletedTrades) _completedTrades.Add(t);
            }

            // Restore last reset date so CheckDailyReset() correctly detects a new
            // day on restart and clears yesterday's halt. Without this it initialises
            // to DateTime.MinValue → sets to today → returns early → halt never clears.
            if (data.LastVolumeResetDate != DateTime.MinValue)
                _lastVolumeResetEt = data.LastVolumeResetDate;

            foreach (var pos in _positions.Values)
            {
                pos.ExitSubmitted = false;
                if (pos.HighWaterMark <= 0) pos.HighWaterMark = pos.AvgPrice;
            }

            LogMessage($"[RESUME] Loaded {_positions.Count} positions, PnL={_totalRealizedPnL:C2}");
            // Restore runtime config (DAILY_PROFIT_GOAL, POSITION_SIZE, etc.) from
            // bot-config.json before CheckDailyReset so limits are correct.
            LoadConfig();
            CheckDailyReset();
            // --- Reconciliation trigger (handles both startup orderings) ---
            // Case A: LoadState runs BEFORE Connect() / nextValidId fires.
            //         Set the flag; IbClient.nextValidId() will call reqPositions().
            // Case B: LoadState runs AFTER Connect() (nextValidId already fired).
            //         IsReady is already true — call RequestPositions() right now.
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
            _reconciled = true; // don't leave the bot permanently blocked on a bad file
        }

    }

    // ══════════════════════════════════════════════════════════
    //  IBKR POSITION RECONCILIATION
    // ══════════════════════════════════════════════════════════

    // Read by IbClient.nextValidId() to know whether to fire reqPositions().
    public bool NeedsReconciliation => _needsReconciliation;

    // Called by the watchdog after a successful reconnect.
    // Clears the old snapshot and re-arms reconciliation so the next
    // positionEnd() callback re-verifies IBKR's live state.
    public void RequestRereconcile()
    {
        lock (_lock)
        {
            _ibkrPositionSnapshot.Clear();
            _needsReconciliation = true;
            _reconciled = false; // block new entries until IBKR confirms positions
        }
        LogMessage("[WATCHDOG] Re-reconciliation armed — requesting fresh position snapshot.");
        if (RealBroker?.IsReady == true)
            RealBroker.RequestPositions();
    }

    // Polled by Program.cs to know when it's safe to subscribe to live ticks.
    public bool IsReconciled => _reconciled;

    // Called by Program.cs when the reconciliation wait times out (10s).
    // Without this, _reconciled stays false and ExecuteStrategy() silently
    // skips every tick for the entire session — bot connects but never trades.
    public void ForceReconcile()
    {
        lock (_lock)
        {
            if (_reconciled) return;
            _needsReconciliation = false;

            // Process whatever partial position data arrived before the timeout.
            // Better to work with incomplete IBKR data than throw it all away.
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

    // Called by IbClient.position() for every position IBKR reports.
    public void OnPositionReceived(string symbol, int qty, decimal avgCost)
    {
        if (string.IsNullOrEmpty(symbol)) return;
        // qty < 0 = short position; qty == 0 = closed (IBKR sometimes sends these, skip)
        if (qty == 0) return;
        lock (_lock)
            _ibkrPositionSnapshot[symbol] = (qty, avgCost);
        Console.WriteLine($"[RECONCILE] ← {symbol} x{qty} @ {avgCost:F2}");
    }

    // Called by IbClient.positionEnd() — performs the merge and unblocks trading.
    // IBKR is always the source of truth.
    //   In IBKR, not in saved state  →  inject  (the GILD scenario)
    //   In saved state, not in IBKR  →  ghost; remove
    //   In both                      →  correct qty / avgCost from IBKR
    public void OnReconciliationComplete()
    {
        List<string> ghosts;
        lock (_lock)
        {
            if (!_needsReconciliation)
            {
                // positionEnd() can fire spuriously (e.g. after cancelPositions).
                // Only act if we actually requested reconciliation.
                return;
            }

            Console.WriteLine($"[RECONCILE] Snapshot received: {_ibkrPositionSnapshot.Count} position(s).");

            // 1. Remove ghosts — in saved state but IBKR says closed
            ghosts = _positions.Keys
                .Where(sym => !_ibkrPositionSnapshot.ContainsKey(sym))
                .ToList();
            foreach (var sym in ghosts)
            {
                LogMessage($"[RECONCILE] Ghost removed: {sym}");
                _positions.Remove(sym);
            }

            // 2. Inject or correct from IBKR
            foreach (var (sym, (ibkrQty, ibkrCost)) in _ibkrPositionSnapshot)
            {
                if (_positions.TryGetValue(sym, out var existing))
                {
                    // Correct stale qty / cost
                    if (existing.Quantity != ibkrQty || existing.AvgPrice != ibkrCost)
                    {
                        LogMessage($"[RECONCILE] Corrected {sym}: qty {existing.Quantity}→{ibkrQty}  cost {existing.AvgPrice:F2}→{ibkrCost:F2}");
                        existing.Quantity = ibkrQty;
                        existing.AvgPrice = ibkrCost;
                        existing.HighWaterMark = ibkrCost; // restart trail conservatively
                    }
                }
                else
                {
                    // Held in IBKR but state file didn't know — inject it
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
            File.WriteAllText("market_memory.json", JsonSerializer.Serialize(dict));
        }
        catch (Exception ex) { LogError("SaveMarketMemory", ex.Message); }
    }

    public void LoadMarketMemory()
    {
        if (!File.Exists("market_memory.json")) return;
        try
        {
            var data = JsonSerializer.Deserialize<ConcurrentDictionary<string, List<Candle>>>(
                           File.ReadAllText("market_memory.json"));
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

    // ── How many symbols we can subscribe given the current line budget ────────
    // SAFETY: DATA_LINES_PER_SYMBOL <= 0 treated as 1 so it never falls back to
    // _watchlist.Length, which would bypass the 100-line IBKR account limit.
    private int GetSubscriptionSlots()
    {
        int linesPerSym = Math.Max(1, DATA_LINES_PER_SYMBOL);
        return Math.Min(MAX_MARKET_DATA_LINES / linesPerSym, MAX_MARKET_DATA_LINES);
    }

    // ── Priority order for which symbols get a live-data slot ─────────────────
    //  1. Symbols with an open position     (must never lose their feed)
    //  2. Symbols with recent candle activity (ATR ≥ threshold = volatile/moving)
    //  3. Everything else in watchlist order
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

        // ── Pass 1: 1-min historical data (3 days) — builds candle engine ──────
        foreach (var symbol in toSubscribe)
        {
            LogMessage($"[HIST] Requesting 1-min history: {symbol}...");
            RealBroker.RequestHistoricalData(symbol);
            _subscribedSymbols.Add(symbol);
            await Task.Delay(1500); // IB pacing limit
        }

        // ── Pass 2: daily historical data (1 year) — builds SMA200 + S/R ───────
        // Requested for ALL watchlist symbols (not just the live-data slot budget)
        // because daily bars don't consume a market data subscription line.
        // IBKR pacing: 60 historical requests per 10 min → 1.5s gap is safe.
        LogMessage($"[HIST] Requesting daily bars for {_watchlist.Length} symbols (SMA200 + S/R levels)...");
        foreach (var symbol in _watchlist)
        {
            LogMessage($"[HIST] Requesting daily history: {symbol}...");
            RealBroker.RequestDailyHistoricalData(symbol);
            await Task.Delay(1500);
        }

        _previousOrbMinutes = ORB_MINUTES;
    }

    // ── Apply watchlist changes live without restart ───────────────────────────
    public async Task ApplyWatchlistDiff(string[] oldList, string[] newList)
    {
        if (RealBroker == null || !RealBroker.IsReady) return;

        var added = newList.Except(oldList, StringComparer.OrdinalIgnoreCase).ToArray();
        var removed = oldList.Except(newList, StringComparer.OrdinalIgnoreCase).ToArray();

        // ── Unsubscribe removed symbols first (frees slots before we add) ──────
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
            try { RealBroker.CancelMarketData(sym); } catch { /* adapter may not support yet */ }
            _subscribedSymbols.Remove(sym);
        }

        // ── Subscribe new symbols respecting the line budget ──────────────────
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
            await Task.Delay(1500); // IBKR pacing
        }
    }

    // ── Re-evaluate halt state after P&L limits change ────────────────────────
    // If the user loosened the limits, un-halt so trading can resume.
    // If they tightened them and current PnL already breaches the new values, halt.
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

    // ── Daily candle ingestion ───────────────────────────────────────────────
    // Called by IbClient.historicalData() for daily-bar requests.
    // Keeps up to 250 days (> 1 year) per symbol.
    // Side-effects:
    //   • Updates _prevDayHighLevel / _prevDayLowLevel from the second-to-last bar
    //     (yesterday's session) so S/R filters are always current.
    //   • Overwrites _prevDayClose from the authoritative daily bar so the gap
    //     calculation in TryGapAndGoStrategy uses a clean end-of-day close rather
    //     than the 9:29 ET 1-min snapshot (which can be pre-market noise).
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

            // Second-to-last bar = yesterday (last bar = today's partial session)
            if (list.Count >= 2)
            {
                var yesterday = list[list.Count - 2];
                _prevDayHighLevel[symbol] = yesterday.High;
                _prevDayLowLevel[symbol] = yesterday.Low;
                _prevDayClose[symbol] = yesterday.Close; // overwrite 9:29 snapshot
            }
        }
    }

    private void SaveEquityCurve()
    {
        try
        {
            File.WriteAllLines("equity_curve.csv",
                _equityCurve.Select(e => $"{e.time:O},{e.equity}"));
        }
        catch { }
    }

    // Records today's closing account value. Called once at EOD.
    // Upserts by date so restarting on the same day doesn't duplicate.
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
            lock (_lifetimeEquity)
                File.WriteAllText(LIFETIME_EQUITY_FILE,
                    JsonSerializer.Serialize(_lifetimeEquity));
        }
        catch { }
    }

    public void LoadLifetimeEquity()
    {
        try
        {
            if (!File.Exists(LIFETIME_EQUITY_FILE)) return;
            var data = JsonSerializer.Deserialize<List<LifetimeEquityPoint>>(
                File.ReadAllText(LIFETIME_EQUITY_FILE));
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
    //  DASHBOARD
    // ══════════════════════════════════════════════════════════

    // ══════════════════════════════════════════════════════════
    //  HTTP DASHBOARD SERVER  →  http://localhost:5000/api/status
    //  Open dashboard.html in any browser — auto-refreshes every second.
    // ══════════════════════════════════════════════════════════

    private const string CONFIG_FILE = "bot-config.json";
    private const string CONFIG_PASSWORD = "Efmukl123!";

    // ── Load configuration from bot-config.json (called at bot startup) ───────
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
            DATA_LINES_PER_SYMBOL = GetI("DATA_LINES_PER_SYMBOL", DATA_LINES_PER_SYMBOL);
            MAX_MARKET_DATA_LINES = GetI("MAX_MARKET_DATA_LINES", MAX_MARKET_DATA_LINES);

            if (root.TryGetProperty("watchlist", out var wlEl) && wlEl.ValueKind == System.Text.Json.JsonValueKind.Array)
            {
                var list = new List<string>();
                foreach (var item in wlEl.EnumerateArray())
                {
                    var s = item.GetString();
                    if (!string.IsNullOrWhiteSpace(s)) list.Add(s.Trim().ToUpper());
                }
                // Cap at the subscription slot budget so a stale/inflated bot-config.json
                // never loads more symbols than IBKR will allow without a Booster Pack.
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

    // ── Persist current configuration to bot-config.json ──────────────────────
    private void SaveConfig()
    {
        try
        {
            File.WriteAllText(CONFIG_FILE, BuildConfigJson(pretty: true));
            Console.WriteLine($"[CONFIG] Saved to {CONFIG_FILE}");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[CONFIG] Save failed: {ex.Message}");
        }
    }

    // ── Serialise all runtime config to JSON ──────────────────────────────────
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
            $"\"DATA_LINES_PER_SYMBOL\":{DATA_LINES_PER_SYMBOL}",
            $"\"MAX_MARKET_DATA_LINES\":{MAX_MARKET_DATA_LINES}",
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

            // CORS pre-flight
            if (method == "OPTIONS")
            {
                ctx.Response.StatusCode = 204;
                ctx.Response.OutputStream.Close();
                return;
            }

            string json;

            if (path == "/api/status")
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

                    // ── Password check ────────────────────────────────────────
                    if (!root.TryGetProperty("password", out var pwEl) ||
                        pwEl.GetString() != CONFIG_PASSWORD)
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
                        DATA_LINES_PER_SYMBOL = GetI("DATA_LINES_PER_SYMBOL", DATA_LINES_PER_SYMBOL);
                        MAX_MARKET_DATA_LINES = GetI("MAX_MARKET_DATA_LINES", MAX_MARKET_DATA_LINES);

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

                    // ── Re-evaluate halt conditions with new limits ────────────
                    ReevaluateHalt();

                    // ── Clear stale ORB ranges if the window size changed ──────
                    if (orbChanged)
                    {
                        _orbRanges.Clear();
                        LogMessage($"[CONFIG] ORB_MINUTES changed — cleared all opening ranges, will recompute.");
                    }

                    // ── Subscribe/unsubscribe symbols diff (async, outside lock) ──
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

        // Also include current in-progress candle so latest price is visible
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

            // ── Positions ────────────────────────────────────────
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
                posArr.Append($@"{{""sym"":""{p.Symbol}"",""qty"":{p.Quantity},""side"":""{(p.IsShort ? "SHORT" : "LONG")}"",""avg"":{p.AvgPrice:F2},""cur"":{px:F2},""unrl"":{unrl:F2},""pct"":{pnlPt:F2},""min"":{heldMin:F1},""strat"":""{p.StrategyTag}""}}");
                first = false;
            }
            posArr.Append("]");

            // ── Equity curve (last 390 pts = full day at 1/min) ───
            var curve = _equityCurve.TakeLast(390).ToList();
            var curveArr = new StringBuilder("[");
            for (int i = 0; i < curve.Count; i++)
            {
                if (i > 0) curveArr.Append(",");
                curveArr.Append($@"{{""t"":""{curve[i].time:HH:mm}"",""v"":{curve[i].equity:F2}}}");
            }
            curveArr.Append("]");

            // ── Watchlist — every console column ─────────────────
            // SYM | PRICE | VWAP | SMA20 | SMA50 | RSI | GAP% | VOL K | ATR% | ORB HI | ORB LO | TREND | SIGNAL | HOT
            var wlArr = new StringBuilder("[");
            bool wfirst = true;
            foreach (var sym in _watchlist)
            {
                if (!_marketData.TryGetValue(sym, out var candles)) continue;
                decimal price = candles.LastOrDefault()?.Close ?? 0m;
                if (price == 0m) continue;

                decimal sma20 = SafeSMA(candles, 20);
                decimal sma50 = SafeSMA(candles, 50);
                double rsi = SafeRSI(candles, 14);
                decimal atr = SafeATR(candles, 14);
                decimal atrPct = price > 0 ? atr / price * 100 : 0;
                _vwap.TryGetValue(sym, out decimal vwap);
                _prevDayClose.TryGetValue(sym, out decimal prevClose);
                decimal gapPct = prevClose > 0 ? (price - prevClose) / prevClose * 100 : 0;
                // chg = real-time day change using latest tick (updates every tick,
                // more accurate than gapPct which uses last closed candle)
                _latestTick.TryGetValue(sym, out decimal livePx);
                decimal chgPct = prevClose > 0 && livePx > 0
                    ? (livePx - prevClose) / prevClose * 100 : gapPct;
                long volK = _dailyVolume.GetValueOrDefault(sym) / 1000;
                bool abvVwap = vwap > 0 && price > vwap;
                string trend = price > sma50 ? "UP" : "NEUT";

                // ── New indicators ────────────────────────────────
                decimal sma200 = GetDailySma200(sym);          // 0 = daily data not loaded yet
                var (pdHi, pdLo) = GetPrevDayHL(sym);          // 0 = not loaded yet
                int macdDir = SafeMACDDirection(candles);       // +1 / 0 / -1

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
                if (sig == "" && vwap > 0)
                {
                    bool above = price > vwap;
                    _prevBarAboveVwap.TryGetValue(sym, out bool wasAbove);
                    if (!wasAbove && above) sig = "VWAP↑";
                    else if (wasAbove && !above) sig = "VWAP↓";
                }
                bool hot = vwap > 0 && price > vwap && rsi > 55;

                if (!wfirst) wlArr.Append(",");
                wlArr.Append($@"{{""s"":""{sym}"",""price"":{price:F2},""vwap"":{vwap:F2},""sma20"":{sma20:F2},""sma50"":{sma50:F2},""sma200"":{sma200:F2},""rsi"":{rsi:F1},""gap"":{gapPct:F2},""chg"":{chgPct:F2},""vol"":{volK},""atr"":{atrPct:F2},""orbHi"":{orbHi:F2},""orbLo"":{orbLo:F2},""pdHi"":{pdHi:F2},""pdLo"":{pdLo:F2},""macd"":{macdDir},""trend"":""{trend}"",""sig"":""{sig}"",""hot"":{(hot ? "true" : "false")},""abvVwap"":{(abvVwap ? "true" : "false")}}}");
                wfirst = false;
            }
            wlArr.Append("]");

            // ── Recent trades (last 20, newest first) ─────────────
            var recentTrades = _tradeHistoryLog.TakeLast(20).Reverse().ToList();
            var tradeArr = new StringBuilder("[");
            for (int i = 0; i < recentTrades.Count; i++)
            {
                if (i > 0) tradeArr.Append(",");
                tradeArr.Append($@"""{recentTrades[i].Replace("\"", "\\\"").Replace("\r", "").Replace("\n", "")}""");
            }
            tradeArr.Append("]");

            // ── Structured completed trades (newest first) for history panel ──
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

            // ── Lifetime equity (all daily snapshots) ────────────
            var ltArr = new StringBuilder("[");
            lock (_lifetimeEquity)
            {
                for (int i = 0; i < _lifetimeEquity.Count; i++)
                {
                    if (i > 0) ltArr.Append(",");
                    var pt = _lifetimeEquity[i];
                    ltArr.Append($@"{{""d"":""{pt.Date}"",""v"":{pt.AccountValue:F2},""p"":{pt.DailyPnL:F2}}}");
                }
            }
            ltArr.Append("]");

            return $@"{{""time"":""{now:yyyy-MM-dd HH:mm:ss} PT"",""et"":""{et:HH:mm:ss} ET"",""regime"":""{_marketRegime}"",""halted"":{(_haltTrading ? "true" : "false")},""reconciled"":{(_reconciled ? "true" : "false")},""pnl"":{_totalRealizedPnL:F2},""goal"":{DAILY_PROFIT_GOAL:F2},""maxLoss"":{MAX_DAILY_LOSS:F2},""trades"":{_tradesToday},""maxTrades"":{MAX_TRADES_PER_DAY},""wins"":{_winCount},""losses"":{_lossCount},""wr"":{wr:F1},""cash"":{cash:F2},""budget"":{TOTAL_BUDGET:F2},""initialBudget"":{TOTAL_BUDGET:F2},""positions"":{posArr},""curve"":{curveArr},""watchlist"":{wlArr},""feed"":{tradeArr},""hist"":{histArr},""lifetimeCurve"":{ltArr}}}";
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

            // ── TOP BAR ─────────────────────────────────────────
            sb.AppendLine(
                $"  IBKR BOT  │  {now:ddd HH:mm:ss} PT  │  ET: {et:HH:mm}  │" +
                $"  {regimeIcon}  │  {statusStr}".PadRight(W));
            sb.AppendLine(new string('═', W));

            // ── PNL / STATS ROW ──────────────────────────────────
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

            // ── ACTIVE POSITIONS ─────────────────────────────────
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

            // ── EQUITY SPARKLINE ─────────────────────────────────
            sb.Append("  Equity  ");
            sb.AppendLine(MakeSparkline(_equityCurve, W - 12));
            sb.AppendLine(new string('─', W));

            // ── MARKET SCANNER ───────────────────────────────────
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

                // Detect which signal is active
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

            // ── RULES FOOTER ─────────────────────────────────────
            sb.AppendLine(
                $"  Budget:{TOTAL_BUDGET:C0}  PosSz:{POSITION_SIZE:C0}  " +
                $"MaxPos:{MAX_POSITIONS}  Cooldown:{COOLDOWN_SECONDS / 60}min  " +
                $"MinHold:{MIN_HOLD_SECONDS / 60}min  Risk:{RISK_PCT * 100:F0}%/trade  " +
                $"MaxLoss:{MAX_LOSS_PER_TRADE:C0}  ATRTrail:{ATR_TRAIL_MULT}x  " +
                $"ORBWindow:{ORB_MINUTES}min  VolExp:{VOL_EXPAND_MULT}x  " +
                $"Strategies: ORB | GAP-GO | VWAP | MeanRev | Momentum");
            sb.AppendLine(new string('─', W));

            // ── TRADE LOG ────────────────────────────────────────
            sb.AppendLine("  RECENT TRADES");
            var recent = _tradeHistoryLog.TakeLast(8).ToList();
            if (recent.Count == 0)
                sb.AppendLine("  — no trades yet —".PadRight(W));
            else
                foreach (var log in recent)
                    sb.AppendLine("  " + log.PadRight(W - 2));

            sb.AppendLine(new string('─', W));

            // ── EVENT LOG ────────────────────────────────────────
            sb.AppendLine("  EVENT LOG");
            lock (_logQueue)
            {
                foreach (var line in _logQueue)
                    sb.AppendLine("  " + (line.Length > W - 3
                        ? line.Substring(0, W - 3) : line).PadRight(W - 2));
                for (int i = _logQueue.Count; i < LOG_LINES; i++)
                    sb.AppendLine(new string(' ', W));
            }

            // ── RENDER ───────────────────────────────────────────
            Console.SetCursorPosition(0, 0);
            Console.Write(sb.ToString());

            int used = sb.ToString().Count(c => c == '\n');
            for (int i = used; i < Console.WindowHeight - 1; i++)
                Console.WriteLine(new string(' ', W));
        }
        catch { /* never crash the dashboard thread */ }
    }

    // ── Dashboard helpers ──────────────────────────────────

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

    // ── MACD direction on 1-min candles ────────────────────────────────────
    // Returns  +1 if MACD line is positive AND rising  (bullish momentum building)
    // Returns  -1 if MACD line is negative AND falling (bearish momentum building)
    // Returns   0 if neutral / not enough data
    //
    // Intentionally simplified — we only need direction, not exact value.
    // Two EMA calculations per call (O(n) each). Called once per candle close,
    // so CPU cost is negligible.
    private int SafeMACDDirection(List<Candle> candles)
    {
        if (candles == null || candles.Count < 30) return 0;
        var closes = candles.Select(c => (double)c.Close).ToArray();

        double ema12Now = CalcEMA(closes, 12);
        double ema26Now = CalcEMA(closes, 26);
        double macdNow = ema12Now - ema26Now;

        // Compare vs 5 bars ago to determine direction of MACD line
        var closesPrev = closes.Take(closes.Length - 5).ToArray();
        if (closesPrev.Length < 26) return 0;
        double macdPrev = CalcEMA(closesPrev, 12) - CalcEMA(closesPrev, 26);

        if (macdNow > 0 && macdNow > macdPrev) return 1;  // rising above zero
        if (macdNow < 0 && macdNow < macdPrev) return -1;  // falling below zero
        return 0;
    }

    // Exponential Moving Average — standard recursive formula
    private double CalcEMA(double[] data, int period)
    {
        if (data == null || data.Length < period) return data?.LastOrDefault() ?? 0;
        double k = 2.0 / (period + 1);
        double ema = data.Take(period).Average();
        for (int i = period; i < data.Length; i++)
            ema = data[i] * k + ema * (1 - k);
        return ema;
    }

    // ── Daily SMA200 lookup ────────────────────────────────────────────────
    // Returns the 200-day SMA from the daily candle cache, or 0 if not enough
    // history has been loaded yet (< 200 daily bars). Callers treat 0 as
    // "filter disabled" so the bot never blocks trades due to missing data.
    private decimal GetDailySma200(string symbol)
    {
        if (!_dailyCandles.TryGetValue(symbol, out var daily)) return 0m;
        lock (daily)
        {
            if (daily.Count < 200) return 0m;
            return daily.TakeLast(200).Average(c => c.Close);
        }
    }

    // ── Previous day High / Low (from daily candle cache) ─────────────────
    // Falls back to (0, 0) if daily data not loaded — callers skip the check.
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
        if (candles == null || candles.Count < period + 1) return 50;

        double gain = 0, loss = 0;
        for (int i = candles.Count - period; i < candles.Count; i++)
        {
            double diff = (double)(candles[i].Close - candles[i - 1].Close);
            if (diff > 0) gain += diff;
            else if (diff < 0) loss -= diff;
        }

        // If there is literally no movement, return neutral 50
        if (gain == 0 && loss == 0) return 50;
        if (loss == 0) return 100;
        if (gain == 0) return 0;

        return 100 - 100 / (1 + gain / loss);
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

    private decimal SafeLowestLow(List<Candle> candles, int lookback)
    {
        if (candles == null || candles.Count < lookback) return decimal.MaxValue;
        return candles.TakeLast(lookback).Min(c => c.Low);
    }

    // Returns true if recent 5 bars have 1.2x more volume than prior 5 (relaxed from 1.5x)
    private bool CheckVolumeExpansion(List<Candle> candles)
    {
        if (candles.Count < 10) return false;
        var last10 = candles.TakeLast(10).ToList();
        long prev5 = last10.Take(5).Sum(c => c.Volume);
        long recent5 = last10.Skip(5).Take(5).Sum(c => c.Volume);
        return recent5 > prev5 * VOL_EXPAND_MULT;
    }

    // Returns true if this symbol outperformed SPY over the last 20 bars
    private bool CheckRelativeStrength(string symbol, List<Candle> candles)
    {
        if (!_marketData.TryGetValue("SPY", out var spy) || spy.Count < 20) return true;
        decimal symReturn = candles.Last().Close / candles[candles.Count - 20].Close;
        decimal spyReturn = spy.Last().Close / spy[spy.Count - 20].Close;
        return symReturn > spyReturn;
    }

    // Returns true ONLY when the stock is UP ≥0.5% today while SPY is DOWN on the day.
    // Used as a SELL-OFF override for longs — catches sector-specific strength
    // (e.g. energy stocks during an oil spike) on broad market selloff days.
    private bool CheckStrongRelativeStrength(string symbol, List<Candle> candles)
    {
        if (!_marketData.TryGetValue("SPY", out var spy) || spy.Count < 5) return false;
        var todayEt = GetEasternTime().Date;
        var todaySym = candles.Where(c => c.Time.Date == todayEt).ToList();
        var todaySpy = spy.Where(c => c.Time.Date == todayEt).ToList();
        if (todaySym.Count < 2 || todaySpy.Count < 2) return false;
        decimal symDayReturn = candles.Last().Close / todaySym.First().Open - 1m;
        decimal spyDayReturn = spy.Last().Close / todaySpy.First().Open - 1m;
        // Stock must be up ≥0.5% while SPY is negative on the day
        return symDayReturn >= 0.005m && spyDayReturn < 0m;
    }

    // Unified position sizing: risk RISK_PCT of budget per trade
    private int CalcQty(decimal price, decimal stopDistance)
    {
        // Enforce a price-relative floor: at least 0.3% of price
        decimal minStop = Math.Max(MIN_STOP_DISTANCE, price * 0.003m);
        if (stopDistance < minStop) stopDistance = minStop;

        decimal riskAmount = TOTAL_BUDGET * RISK_PCT;
        int qty = (int)(riskAmount / stopDistance);

        // Cap by remaining available cash (not just POSITION_SIZE) so that when
        // multiple positions are open, the new one can't overspend the budget.
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