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

    // True once the IBKR socket handshake is complete (nextValidId has fired).
    bool IsReady { get; }

    // Sends reqPositions() to IBKR. The adapter must call
    // SimulatedBroker.OnPositionReceived() per position, then
    // SimulatedBroker.OnReconciliationComplete() when IBKR fires positionEnd().
    void RequestPositions();
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

    // NEW — needed to safely resume
    public int WinCount { get; set; }
    public int LossCount { get; set; }
    public int TradesToday { get; set; }
    public bool HaltTrading { get; set; }
    public Dictionary<string, DateTime> LastTradeTime { get; set; } = new();
    public Dictionary<string, bool> LastTradeWasLoss { get; set; } = new();
    public Dictionary<string, int> DailyEntryCount { get; set; } = new();
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

    // ── TRADING RULES — SWEET SPOT ─────────────────────────
    private const decimal TOTAL_BUDGET = 4000m;
    private const int MAX_POSITIONS = 2;
    private const decimal POSITION_SIZE = 2000m;
    private const int MIN_HOLD_SECONDS = 300;
    private const decimal DAILY_PROFIT_GOAL = 300m;
    private const decimal MAX_DAILY_LOSS = -150m;
    private const int COOLDOWN_SECONDS = 480;    // 8 min
    private const decimal ATR_TRAIL_MULT = 2.5m;
    private const decimal SHORT_ATR_TRAIL = 2.0m;
    private const decimal HARD_STOP_ATR_MULT = 2.0m;   // KEY FIX — was 1.5
    private const decimal MAX_LOSS_PER_TRADE = 75m;
    private const decimal MIN_STOP_DISTANCE = 0.10m;
    private const int MAX_QTY_SANITY = 500;
    private const decimal RISK_PCT = 0.015m;  // 1.5%
    private const int ORB_MINUTES = 30;
    private const decimal VOL_EXPAND_MULT = 1.5m;    // tightened from 1.3 — need real volume surge
    private const double RSI_LONG_MIN = 56.0;    // tightened from 54 — confirm upward momentum
    private const double RSI_SHORT_MAX = 44.0;   // tightened from 46 — confirm downward momentum
    private const double RSI_OVERSOLD = 30.0;    // genuinely stretched
    private const double RSI_OVERBOUGHT = 70.0;
    private const decimal GAP_GO_MIN_PCT = 0.008m;  // tightened from 0.5% — only meaningful gaps
    private const decimal GAP_GO_REL_VOL = 1.5m;
    private const int VWAP_CONFIRM_BARS = 2;


    private bool _allowShorts = false; // set to true only if you have a margin account

    // ── STATE ──────────────────────────────────────────────
    public readonly ConcurrentDictionary<string, List<Candle>> _marketData = new();
    private Dictionary<string, SimPosition> _positions = new();
    private readonly ConcurrentDictionary<int, TrackedOrder> _ordersById = new();
    private readonly List<string> _tradeHistoryLog = new();
    private readonly Dictionary<string, DateTime> _lastTradeTime = new();
    private readonly Dictionary<string, long> _dailyVolume = new();
    private readonly ConcurrentDictionary<string, decimal> _latestTick = new();
    private readonly ConcurrentDictionary<string, Candle> _currentMinuteCandle = new();
    private readonly List<(DateTime time, decimal equity)> _equityCurve = new();

    // Opening Range per symbol (resets daily)
    private readonly ConcurrentDictionary<string, OpeningRange> _orbRanges = new();

    // Gap-and-Go: track daily gap % per symbol
    private readonly ConcurrentDictionary<string, decimal> _dailyGapPct = new();

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

    // ── WATCHLIST ──────────────────────────────────────────
    public readonly string[] _watchlist =
  {
    // ── CORE ETFs ─────────────────────────────────────────
    "SPY","QQQ","IWM","DIA","SMH","XLK","XLF","XLE","XBI",

    // ── MEGA CAP TECH ─────────────────────────────────────
    "AAPL","MSFT","AMZN","GOOGL","META","NVDA","TSLA","ORCL","IBM","ADBE",

    // ── SEMIS ─────────────────────────────────────────────
    "AMD","ARM","AVGO","QCOM","MU","LRCX","AMAT","ASML","TSM","TXN",

    // ── CLOUD / SAAS ──────────────────────────────────────
    "CRM","NOW","SNOW","MDB","DDOG","NET","ZS","CRWD","PANW","PLTR",
    "OKTA","TTD","APP",

    // ── FINTECH / CRYPTO ──────────────────────────────────
    "COIN","MSTR","PYPL","HOOD","SOFI","XYZ",   // XYZ = Block (was SQ)

    // ── FINANCIALS ────────────────────────────────────────
    "JPM","BAC","GS","MS","V","MA","BX",

    // ── HIGH-MOMENTUM GROWTH ──────────────────────────────
    "NFLX","UBER","ABNB","SHOP","MELI","BKNG","DASH","SPOT","INTU","ANET",

    // ── ENERGY ────────────────────────────────────────────
    "XOM","CVX","OXY",

    // ── BIOTECH / HEALTH ──────────────────────────────────
    "ABBV","UNH","VRTX","REGN","GILD",

    // ── INDUSTRIALS / DEFENSE ─────────────────────────────
    "CAT","DE","GE","HON","RTX","LMT","BA"
};

    // Dashboard timer
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

        // Mark as fully set once the ORB window closes
        if (minutesSinceOpen == ORB_MINUTES)
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
        if (nowEt.Hour < 10) return;
        // No trading in last 30 min (15:30–16:00 ET) — liquidity dries up, slippage spikes
        if (nowEt.Hour > 15 || (nowEt.Hour == 15 && nowEt.Minute >= 30)) return;

        if (!_marketData.TryGetValue(symbol, out var candles) || candles.Count < 30)
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
            if (_positions.ContainsKey(symbol)) return;
            // Include pending (submitted but unfilled) orders in the cap
            if (_positions.Count + _pendingEntryCount >= MAX_POSITIONS) return;
            if (_lastTradeTime.TryGetValue(symbol, out var lastTime))
            {
                // Double cooldown if the last trade on this symbol was a loss
                int cooldown = _lastTradeWasLoss.GetValueOrDefault(symbol)
                    ? COOLDOWN_SECONDS * 2
                    : COOLDOWN_SECONDS;
                if ((DateTime.UtcNow - lastTime).TotalSeconds < cooldown) return;
            }
            // Max 1 entry per symbol per day — if already entered once today, skip
            if (_dailyEntryCount.GetValueOrDefault(symbol) >= 1) return;

            int minutesSinceOpen = (nowEt.Hour - 9) * 60 + nowEt.Minute - 30;

            // ── STRATEGY DISPATCH ─────────────────────────────
            // Try each strategy in priority order. First one to fire wins.

            // 1. Opening Range Breakout (only after ORB window closes)
            if (minutesSinceOpen > ORB_MINUTES)
                if (TryOrbStrategy(symbol, candles, nowEt)) return;

            // 2. Gap-and-Go (only first 30 min of allowed trading window, 10:00–10:30 ET)
            if (minutesSinceOpen <= 60)
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
        double rsi = SafeRSI(candles, 7);
        _vwap.TryGetValue(symbol, out decimal vwapVal);

        bool volumeExpansion = CheckVolumeExpansion(candles);

        // ── LONG: price breaks above ORB high with volume ─────
        if (close > orb.High && volumeExpansion && rsi > RSI_LONG_MIN)
        {
            // Confirm not in sell-off for longs
            if (_marketRegime == "SELL-OFF") goto TryShortOrb;

            decimal stopDistance = Math.Max(atr * HARD_STOP_ATR_MULT, MIN_STOP_DISTANCE);
            int qty = CalcQty(close, stopDistance);
            if (qty <= 0) return false;

            OpenPosition(symbol, qty, close, TradeSide.Buy, false, "ORB_LONG");
            return true;
        }

        TryShortOrb:
        // ── SHORT: price breaks below ORB low with volume ──────
        if (close < orb.Low && volumeExpansion && rsi < RSI_SHORT_MAX)
        {
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

        // Relative volume: compare today's volume so far vs historical avg
        var todayEt = GetEasternTime().Date;
        long todayVol = _dailyVolume.GetValueOrDefault(symbol);
        long avg20Vol = (long)(candles
            .GroupBy(c => c.Time.Date)
            .OrderByDescending(g => g.Key)
            .Take(20)
            .Select(g => g.Sum(c => c.Volume))
            .DefaultIfEmpty(0)
            .Average());

        bool highRelVol = avg20Vol > 0 && todayVol > avg20Vol * GAP_GO_REL_VOL;
        if (!highRelVol) return false;

        var lastCandle = candles.Last();
        decimal close = lastCandle.Close;
        double rsi = SafeRSI(candles, 7);
        decimal atr = SafeATR(candles, 14);
        bool volExp = CheckVolumeExpansion(candles);

        // Gap UP → long (not in sell-off)
        if (gapPct > 0 && rsi > RSI_LONG_MIN && volExp && _marketRegime != "SELL-OFF")
        {
            decimal stopDistance = Math.Max(atr * HARD_STOP_ATR_MULT, MIN_STOP_DISTANCE);
            int qty = CalcQty(close, stopDistance);
            if (qty <= 0) return false;

            OpenPosition(symbol, qty, close, TradeSide.Buy, false, "GAP_GO_LONG");
            return true;
        }

        // Gap DOWN → short
        if (gapPct < 0 && rsi < RSI_SHORT_MAX && volExp)
        {
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
        double rsi = SafeRSI(candles, 7);
        decimal atr = SafeATR(candles, 14);
        bool volExp = CheckVolumeExpansion(candles);

        // Reclaim: previous bar below, now 2 bars confirmed above
        bool vwapReclaim = !prevAbove && allRecentAbove;
        if (vwapReclaim && volExp && rsi > RSI_LONG_MIN && _marketRegime != "SELL-OFF")
        {
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
        double rsi = SafeRSI(candles, 7);
        decimal atr = SafeATR(candles, 14);

        // ── OVERSOLD BOUNCE → long ───────────────────────────
        // Stock is in uptrend (above SMA50) but RSI has dipped — buy the dip
        bool oversoldInUptrend = close > sma50 && rsi < RSI_OVERSOLD && _marketRegime != "SELL-OFF";
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
        double rsi = SafeRSI(candles, 7);
        decimal atr = SafeATR(candles, 14);
        decimal atrPct = close > 0 ? atr / close : 0m;
        bool volExp = CheckVolumeExpansion(candles);

        if (atrPct < 0.002m) return false;

        // ── SCORE-BASED LONG ENTRY (need 3 of 4 conditions) ───
        _vwap.TryGetValue(symbol, out decimal vwapVal);

        bool regimeStrong = _marketRegime != "SELL-OFF";
        bool relativeStrength = CheckRelativeStrength(symbol, candles);
        bool rsiConfirm = rsi > RSI_LONG_MIN;
        bool aboveVwap = vwapVal <= 0 || close > vwapVal;

        // Breakout or pullback-continuation signals
        decimal recentHigh = SafeHighestHigh(candles.Take(candles.Count - 1).ToList(), 8);
        decimal range = lastCandle.High - lastCandle.Low;
        decimal avgRange = candles.TakeLast(10).Average(c => c.High - c.Low);
        bool expansion = range > avgRange * 1.8m;
        bool choppyMode = _marketRegime == "CHOPPY";

        bool pullbackEntry = lastCandle.Low <= sma20 && lastCandle.Close > sma20
                              && rsi > 55 && volExp;
        bool breakoutSignal = !choppyMode && expansion && volExp && close > recentHigh;
        bool hasSignal = breakoutSignal || pullbackEntry;

        // In TryMomentumStrategy — replace the score check:
        int score = (regimeStrong ? 1 : 0)
                  + (relativeStrength ? 1 : 0)
                  + (rsiConfirm ? 1 : 0)
                  + (aboveVwap ? 1 : 0);

        // Require all 4 in normal/trending, allow 3 only in choppy
        int required = (_marketRegime == "CHOPPY") ? 3 : 4;
        if (score >= required && hasSignal && volExp)
        {
            decimal stopDistance = Math.Max(atr * HARD_STOP_ATR_MULT, MIN_STOP_DISTANCE);
            int qty = CalcQty(close, stopDistance);
            if (qty <= 0) return false;

            OpenPosition(symbol, qty, close, TradeSide.Buy, false, "MOMENTUM_LONG");
            return true;
        }

        // ── SELL-OFF REGIME → SHORT MOMENTUM ─────────────────
        if (_marketRegime == "SELL-OFF")
        {
            bool rsiShortConfirm = rsi < RSI_SHORT_MAX;
            bool belowVwap = vwapVal > 0 && close < vwapVal;
            bool breakdownSignal = !choppyMode && expansion && volExp
                                   && close < SafeLowestLow(candles, 8);

            int shortScore = (rsiShortConfirm ? 1 : 0)
                           + (!relativeStrength ? 1 : 0)  // weak vs SPY
                           + (belowVwap ? 1 : 0)
                           + (volExp ? 1 : 0);

            if (shortScore >= 3 && breakdownSignal)
            {
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

            // Compute gain % correctly for both directions
            decimal gainPct = pos.AvgPrice > 0
                ? pos.IsShort
                    ? (pos.AvgPrice - currentPrice) / pos.AvgPrice
                    : (currentPrice - pos.AvgPrice) / pos.AvgPrice
                : 0m;

            // Track best price (HWM = highest price for longs, lowest for shorts)
            if (pos.IsShort)
                pos.HighWaterMark = Math.Min(
                    pos.HighWaterMark == 0 ? currentPrice : pos.HighWaterMark, currentPrice);
            else
                pos.HighWaterMark = Math.Max(pos.HighWaterMark, currentPrice);

            // ── PARTIAL 1: +1.5% → sell half ─────────────────
            if (gainPct >= 0.015m && !pos.PartialExitDone && pos.Quantity >= 2)
            {
                int halfQty = pos.Quantity / 2;
                pos.Quantity -= halfQty;
                pos.PartialExitDone = true;
                SubmitOrder(symbol, halfQty, currentPrice, exitSide, "PARTIAL_TP_1");
                return;
            }

            // ── PARTIAL 2: +2.5% → sell another quarter ───────
            if (gainPct >= 0.025m && !pos.PartialExitDone2 && pos.Quantity >= 2)
            {
                int quarterQty = Math.Max(1, pos.Quantity / 2);
                pos.Quantity -= quarterQty;
                pos.PartialExitDone2 = true;
                SubmitOrder(symbol, quarterQty, currentPrice, exitSide, "PARTIAL_TP_2");
                return;
            }

            // ── TIME STOP: 30 min with no progress (was 20, PLTR/ORCL/PANW exited too soon)
            if (secondsHeld > 1800 && gainPct < 0.003m)
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
                    StrategyTag = resolvedTag
                };
                _tradesToday++;

                string dir = isShortEntry ? "SHORT" : "BUY";
                subject = $"🚀 {dir}: {order.Symbol} x{fillQty} @ {fillPrice:C2}";
                body = $"{dir} {fillQty} shares @ {fillPrice:C2}";
            }
            // ── CLOSING A POSITION ────────────────────────────
            else if (_positions.TryGetValue(order.Symbol, out var pos))
            {
                decimal pnl = pos.IsShort
                    ? (pos.AvgPrice - fillPrice) * fillQty
                    : (fillPrice - pos.AvgPrice) * fillQty;
                decimal holdMinutes = (decimal)(DateTime.UtcNow - pos.EntryTime).TotalMinutes;

                _totalRealizedPnL += pnl;

                // Only count win/loss and remove position on FULL close
                // Partial exits reduce qty but keep the position alive
                bool isFullClose = fillQty >= pos.Quantity;
                if (isFullClose)
                {
                    if (pnl > 0) _winCount++;
                    else _lossCount++;

                    _marketData.TryGetValue(order.Symbol, out var exitCandles);
                    LogTradeAnalytics(order.Symbol, pos.AvgPrice, fillPrice,
                                      pnl, holdMinutes,
                                      SafeATR(exitCandles, 14), SafeRSI(exitCandles, 7),
                                      pos.StrategyTag, pos.IsShort);

                    _positions.Remove(order.Symbol);
                    _lastTradeTime[order.Symbol] = DateTime.UtcNow;
                    // Remember outcome so we can double the cooldown after a loss
                    _lastTradeWasLoss[order.Symbol] = pnl <= 0;
                }
                // Partial close — qty was already reduced in CheckExits before submit,
                // so pos.Quantity is already the remaining amount. Nothing more to do.

                subject = $"💰 {(isFullClose ? "CLOSE" : "PARTIAL")}: {order.Symbol} x{fillQty} @ {fillPrice:C2} | PnL: {pnl:C2}";
                body = $"{(isFullClose ? "Closed" : "Partial")} {fillQty} @ {fillPrice:C2}\nPnL: {pnl:C2}\nStrategy: {pos.StrategyTag}";
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
                DailyEntryCount = _dailyEntryCount
            }));
        }
        catch (Exception ex) { LogError("SaveState", ex.Message); }
    }
    public void LoadState()
    {
        if (!File.Exists("bot_state.json"))
        {
            // First-ever run — nothing to reconcile, allow trading immediately.
            _reconciled = true;
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

            foreach (var pos in _positions.Values)
            {
                pos.ExitSubmitted = false;
                if (pos.HighWaterMark <= 0) pos.HighWaterMark = pos.AvgPrice;
            }

            LogMessage($"[RESUME] Loaded {_positions.Count} positions, PnL={_totalRealizedPnL:C2}");

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

    // Polled by Program.cs to know when it's safe to subscribe to live ticks.
    public bool IsReconciled => _reconciled;

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
            var dict = _marketData.ToDictionary(k => k.Key, v => v.Value);
            foreach (var kv in dict)
                kv.Value.RemoveAll(c => c.Time < DateTime.UtcNow.AddDays(-3));
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

    public async Task RequestAllHistoricalSlow()
    {
        if (RealBroker == null)
            throw new Exception("RealBroker not set.");
        foreach (var symbol in _watchlist)
        {
            LogMessage($"[HIST] Requesting {symbol}...");
            RealBroker.RequestHistoricalData(symbol);
            await Task.Delay(1500); // IB pacing limit
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

    private void SaveEquityCurve()
    {
        try
        {
            File.WriteAllLines("equity_curve.csv",
                _equityCurve.Select(e => $"{e.time:O},{e.equity}"));
        }
        catch { }
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

    private const int LOG_LINES = 8;
    private readonly Queue<string> _logQueue = new Queue<string>();
    private int _dashTick = 0;

    public void Start()
    {
        Console.CursorVisible = false;
        Console.OutputEncoding = System.Text.Encoding.UTF8;
        _dashboardTimer = new Timer(_ => PrintDetailedDashboard(), null, 0, 1000);
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
                double rsi = SafeRSI(candles, 7);
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

    // Unified position sizing: risk RISK_PCT of budget per trade
    private int CalcQty(decimal price, decimal stopDistance)
    {
        // Enforce a price-relative floor: at least 0.3% of price
        // Prevents over-large qty on low-ATR stocks like ABBV/JNJ where
        // 1-min ATR can be $0.10–0.20, making stop distance trivially small
        decimal minStop = Math.Max(MIN_STOP_DISTANCE, price * 0.003m);
        if (stopDistance < minStop) stopDistance = minStop;

        decimal riskAmount = TOTAL_BUDGET * RISK_PCT;
        int qty = (int)(riskAmount / stopDistance);
        int maxByBudget = (int)(POSITION_SIZE / price);
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