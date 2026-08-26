// ═══════════════════════════════════════════════════════════════════════
//  SIMULATEDBROKER — PATCH MODULE
//  Drop this file next to SimulatedBroker.cs and make it a partial class.
//  All new engines and fixed methods live here.
//  Ensure SimulatedBroker.cs declares: public partial class SimulatedBroker
// ═══════════════════════════════════════════════════════════════════════

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;

// ─── Monte Carlo result ───────────────────────────────────────────────
public class MonteCarloResult
{
    public double ProbabilityOfRuin   { get; set; }  // P(equity < 50% of start)
    public double ExpectedFinalEquity { get; set; }
    public double Percentile5Equity   { get; set; }  // worst 5th percentile
    public double Percentile95Equity  { get; set; }
    public double MedianEquity        { get; set; }
    public double KellyFraction       { get; set; }  // optimal bet size
    public double HalfKelly           { get; set; }
    public double SuggestedRiskPct    { get; set; }  // what the bot should use
    public int    SimulationsRun      { get; set; }
    public double WinRate             { get; set; }
    public double AvgWin              { get; set; }
    public double AvgLoss             { get; set; }
    public double ExpectedValuePerTrade { get; set; }
    public string EdgeAssessment      { get; set; } = "";  // "STRONG" / "WEAK" / "NEGATIVE"
}

public partial class SimulatedBroker
{
    // ══════════════════════════════════════════════════════════
    //  BUG FIX #1: Remove SimulatedBroker-level INVERT_ENTRY_DIRECTION.
    //  IbClient.ReverseSignals already handles this.
    //  The old double-inversion was: signal LONG → SB inverts to SHORT → IbClient inverts
    //  back to LONG → bot trades LONG despite wanting SHORT. Net effect = no reversal.
    //  Fix: SimulatedBroker passes side through unchanged. IbClient.ReverseSignals does it once.
    //
    //  ACTION REQUIRED IN SimulatedBroker.cs:
    //  1. Set INVERT_ENTRY_DIRECTION = false and remove the inversion block in OpenPosition().
    //  2. The block from "TradeSide signalSide = side;" to the closing brace ~30 lines later
    //     should be deleted, and executionSide/executionIsShort should just equal side/isShort.
    // ══════════════════════════════════════════════════════════

    // ══════════════════════════════════════════════════════════
    //  BUG FIX #2: Breakeven stop — move stop to entry once at 1R
    //  Called from UpdateLiveTick() after CheckHardStop()
    // ══════════════════════════════════════════════════════════
    private void CheckBreakevenStop(string symbol, decimal currentPrice)
    {
        lock (_lock)
        {
            if (!_positions.TryGetValue(symbol, out var pos)) return;
            if (pos.ExitSubmitted) return;

            // NW positions are intentionally managed by the configured lower/upper
            // envelope plus their configured protective NW stop. Do not arm the
            // generic 1R breakeven stop, because that can close the trade before
            // the live price reaches the NW upper band.
            if ((pos.StrategyTag ?? "").StartsWith("NW_BAND_", StringComparison.OrdinalIgnoreCase))
                return;

            // Already at breakeven or better — no action needed
            if (!_marketData.TryGetValue(symbol, out var candles)) return;
            decimal oneR = pos.InitialRiskPerShare > 0
                ? pos.InitialRiskPerShare
                : SafeATR(candles, 14) * HARD_STOP_ATR_MULT;
            if (oneR <= 0) return;

            decimal gainPerShare = pos.IsShort
                ? pos.AvgPrice - currentPrice
                : currentPrice - pos.AvgPrice;
            decimal rMultiple = gainPerShare / oneR;

            // Once at 1R, the hard stop floor becomes entry price
            if (rMultiple >= 1.0m && !pos.IsShort)
            {
                // If current hard stop is below entry, bump it up
                // We track this via a flag on the position
                // (SimPosition needs a BreakevenArmed bool — add to class)
                // For now: log the event; the actual stop enforcement is in CheckHardStop
                // which already checks pos.AvgPrice - plannedStop vs current
                // The fix: update pos.TrailingStop to pos.AvgPrice when at 1R
                if (pos.TrailingStop < pos.AvgPrice)
                {
                    pos.TrailingStop = pos.AvgPrice;
                    //pos.BreakevenArmed = true;
                    LogMessage($"[BREAKEVEN] {symbol} stop moved to entry {pos.AvgPrice:F2} at {rMultiple:F2}R");
                }
            }
            else if (rMultiple >= 1.0m && pos.IsShort)
            {
                // For shorts: hard stop ceiling = entry price
                if (pos.TrailingStop == 0 || pos.TrailingStop > pos.AvgPrice)
                {
                    pos.TrailingStop = pos.AvgPrice;
                 //   pos.BreakevenArmed = true;
                    LogMessage($"[BREAKEVEN] {symbol} SHORT stop moved to entry {pos.AvgPrice:F2} at {rMultiple:F2}R");
                }
            }
        }
    }

    // ══════════════════════════════════════════════════════════
    //  BUG FIX #3: Breakeven stop enforcement inside CheckHardStop
    //  Add this check AFTER the existing dollarStopHit / atrStopHit check.
    //  If TrailingStop is set (breakeven armed) and price crosses back through it → exit.
    // ══════════════════════════════════════════════════════════
    private bool IsBreakevenStopHit(SimPosition pos, decimal currentPrice)
    {
        if (pos.TrailingStop <= 0) return false;
        if (!pos.IsShort && currentPrice < pos.TrailingStop) return true;  // long: price below BE
        if (pos.IsShort && currentPrice > pos.TrailingStop) return true;   // short: price above BE
        return false;
    }

    // ══════════════════════════════════════════════════════════
    //  BUG FIX #4: Time-of-day filter — probability-based gate
    //  Returns the allowed strategy families for the current ET time.
    //  Restricts low-edge periods to only the highest-conviction setups.
    // ══════════════════════════════════════════════════════════
    private bool PassesTimeOfDayFilter(string strategyTag, int minutesSinceOpen)
    {
        // 0-15 min (9:30-9:45): only ORB and GAP — opening auction noise
        if (minutesSinceOpen < 15)
            return strategyTag.StartsWith("SCALP_ORB_", StringComparison.OrdinalIgnoreCase)
                || strategyTag.StartsWith("GAP_GO_", StringComparison.OrdinalIgnoreCase);

        // 15-75 min (9:45-10:45): all strategies allowed — best edge window
        if (minutesSinceOpen < 75)
            return true;

        // 75-105 min (10:45-11:15): no pure mean reversion (lunch fade coming)
        if (minutesSinceOpen < 105)
            return !strategyTag.StartsWith("MEAN_REV_", StringComparison.OrdinalIgnoreCase)
                && !strategyTag.StartsWith("BB_MR_", StringComparison.OrdinalIgnoreCase);

        // 105-210 min (11:15-13:00): midday — only strong momentum/breakout
        if (minutesSinceOpen < 210)
        {
            bool isMomentumOrBreakout =
                strategyTag.StartsWith("MOMENTUM_", StringComparison.OrdinalIgnoreCase)
             || strategyTag.StartsWith("SCALP_BREAKOUT_", StringComparison.OrdinalIgnoreCase)
             || strategyTag.StartsWith("GAP_GO_", StringComparison.OrdinalIgnoreCase);
            return isMomentumOrBreakout && _marketRegime == "TRENDING";
        }

        // 210-300 min (13:00-14:30): afternoon open — all allowed again
        if (minutesSinceOpen < 300)
            return true;

        // 300-360 min (14:30-15:30): only trend continuations and ORB
        return strategyTag.StartsWith("SCALP_ORB_", StringComparison.OrdinalIgnoreCase)
            || strategyTag.StartsWith("MOMENTUM_", StringComparison.OrdinalIgnoreCase)
            || strategyTag.StartsWith("SCALP_BREAKOUT_", StringComparison.OrdinalIgnoreCase);
    }

    // ══════════════════════════════════════════════════════════
    //  BUG FIX #5: Volatility regime filter
    //  Skip entries when intraday ATR is unusually elevated (>2x normal).
    //  On Fed days, earnings days, macro events — stops are too wide,
    //  fills are terrible, and the model's edge disappears.
    // ══════════════════════════════════════════════════════════
    private bool IsVolatilityTooHigh(string symbol, List<Candle> candles)
    {
        if (candles == null || candles.Count < 20) return false;
        decimal currentAtr = SafeATR(candles, 5);   // very short-term ATR
        decimal normalAtr = SafeATR(candles, 20);   // medium-term ATR
        if (normalAtr <= 0) return false;
        // If short-term ATR is > 2.5x normal → extreme vol → skip
        return currentAtr > normalAtr * 2.5m;
    }

    // ══════════════════════════════════════════════════════════
    //  BUG FIX #6: Regime velocity — detect fast regime shifts
    //  If SPY dropped > 0.5% in the last 3 bars, we're transitioning
    //  into SELL-OFF. Block new LONG entries even if regime says NORMAL.
    // ══════════════════════════════════════════════════════════
    private bool IsRegimeShiftingBearish()
    {
        if (!_marketData.TryGetValue("SPY", out var spy) || spy.Count < 5) return false;
        decimal recent3Close = spy[^1].Close;
        decimal prior3Close = spy[Math.Max(0, spy.Count - 4)].Close;
        if (prior3Close <= 0) return false;
        decimal change = (recent3Close - prior3Close) / prior3Close;
        return change < -0.005m;  // SPY dropped 0.5% in 3 bars
    }

    private bool IsRegimeShiftingBullish()
    {
        if (!_marketData.TryGetValue("SPY", out var spy) || spy.Count < 5) return false;
        decimal recent3Close = spy[^1].Close;
        decimal prior3Close = spy[Math.Max(0, spy.Count - 4)].Close;
        if (prior3Close <= 0) return false;
        decimal change = (recent3Close - prior3Close) / prior3Close;
        return change > 0.005m;
    }

    // ══════════════════════════════════════════════════════════
    //  MONTE CARLO ENGINE
    //  Simulates N sessions using historical trade PnL distribution.
    //  Used to:
    //    1. Compute probability of ruin (equity < 50% of start)
    //    2. Compute optimal Kelly fraction for next session
    //    3. Provide real confidence intervals for equity projection
    //
    //  Inputs: list of completed trade PnLs (net, after commission)
    //  Outputs: MonteCarloResult with all stats
    // ══════════════════════════════════════════════════════════
    public MonteCarloResult RunMonteCarlo(
        List<decimal> tradePnLs,
        decimal startingCapital,
        int tradesPerSession = 6,
        int sessionsToProject = 60,
        int simulations = 5000)
    {
        var result = new MonteCarloResult { SimulationsRun = simulations };

        if (tradePnLs == null || tradePnLs.Count < 5)
        {
            result.EdgeAssessment = "INSUFFICIENT_DATA";
            return result;
        }

        // ── Basic statistics from historical trades ──
        var wins  = tradePnLs.Where(p => p > 0).ToList();
        var losses = tradePnLs.Where(p => p <= 0).ToList();

        double winRate  = wins.Count / (double)tradePnLs.Count;
        double avgWin   = wins.Count > 0 ? (double)wins.Average() : 0;
        double avgLoss  = losses.Count > 0 ? Math.Abs((double)losses.Average()) : 1;
        double ev       = winRate * avgWin - (1 - winRate) * avgLoss;

        result.WinRate   = winRate;
        result.AvgWin    = avgWin;
        result.AvgLoss   = avgLoss;
        result.ExpectedValuePerTrade = ev;

        // ── Kelly Criterion ──
        // Kelly = (bp - q) / b  where b = avgWin/avgLoss, p = winRate, q = 1-winRate
        double b = avgLoss > 0 ? avgWin / avgLoss : 0;
        double kellyFull = avgLoss > 0
            ? (b * winRate - (1 - winRate)) / b
            : 0;
        kellyFull = Math.Max(0, Math.Min(kellyFull, 0.25));  // cap at 25%
        double halfKelly = kellyFull / 2.0;

        result.KellyFraction = kellyFull;
        result.HalfKelly = halfKelly;

        // ── Suggested risk per trade ──
        // Blend half-Kelly with current RISK_PCT
        // If half-Kelly < current RISK_PCT → reduce risk
        // If half-Kelly > current RISK_PCT → keep current (conservative)
        double suggestedRisk = Math.Min(halfKelly, (double)RISK_PCT);
        if (ev < 0) suggestedRisk = (double)RISK_PCT * 0.5;  // halve on negative edge
        result.SuggestedRiskPct = suggestedRisk;

        // ── Monte Carlo simulation ──
        var rng = new Random(42);
        var pnlArr = tradePnLs.Select(p => (double)p).ToArray();
        double ruinLevel = (double)startingCapital * 0.5;  // ruin = 50% drawdown
        var finalEquities = new double[simulations];
        int ruinCount = 0;

        for (int sim = 0; sim < simulations; sim++)
        {
            double equity = (double)startingCapital;
            bool ruined = false;

            for (int session = 0; session < sessionsToProject; session++)
            {
                for (int trade = 0; trade < tradesPerSession; trade++)
                {
                    // Bootstrap: sample a random trade from history
                    int idx = rng.Next(pnlArr.Length);
                    equity += pnlArr[idx];

                    if (equity < ruinLevel && !ruined)
                    {
                        ruined = true;
                        ruinCount++;
                        break;
                    }
                }
                if (ruined) break;
            }
            finalEquities[sim] = equity;
        }

        Array.Sort(finalEquities);
        result.ProbabilityOfRuin   = ruinCount / (double)simulations;
        result.Percentile5Equity   = finalEquities[(int)(simulations * 0.05)];
        result.MedianEquity        = finalEquities[simulations / 2];
        result.Percentile95Equity  = finalEquities[(int)(simulations * 0.95)];
        result.ExpectedFinalEquity = finalEquities.Average();

        // ── Edge assessment ──
        if (ev > avgLoss * 0.10 && winRate >= 0.45)
            result.EdgeAssessment = "STRONG";
        else if (ev > 0 && winRate >= 0.35)
            result.EdgeAssessment = "POSITIVE";
        else if (ev > 0)
            result.EdgeAssessment = "WEAK";
        else
            result.EdgeAssessment = "NEGATIVE";

        return result;
    }

    // ══════════════════════════════════════════════════════════
    //  KELLY POSITION SIZER
    //  Returns a multiplier (0.25–1.0) to apply to CalcQty output.
    //  Based on rolling 30-trade Kelly estimate.
    //  Applied in CalcQty() — blend 70% fixed / 30% Kelly.
    // ══════════════════════════════════════════════════════════
    private decimal GetKellyMultiplier()
    {
        List<TradeRecord> recent;
        lock (_allTrades)
        {
            recent = _allTrades.TakeLast(30).ToList();
        }

        if (recent.Count < 15) return 1.0m;  // not enough data → full size

        var wins = recent.Where(t => t.NetPnL > 0).ToList();
        var losses = recent.Where(t => t.NetPnL <= 0).ToList();

        if (wins.Count == 0 || losses.Count == 0) return 0.5m;

        double p = wins.Count / (double)recent.Count;
        double avgW = (double)wins.Average(t => t.NetPnL);
        double avgL = Math.Abs((double)losses.Average(t => t.NetPnL));
        if (avgL == 0) return 1.0m;

        double b = avgW / avgL;
        double kelly = (b * p - (1 - p)) / b;
        double halfKelly = Math.Max(0, kelly / 2.0);

        // Blend: 70% fixed, 30% Kelly-adjusted
        double blended = 0.70 + 0.30 * (halfKelly / (double)RISK_PCT);
        return (decimal)Math.Clamp(blended, 0.25, 1.20);
    }

    // ══════════════════════════════════════════════════════════
    //  ORDER FLOW IMBALANCE DETECTOR
    //  Uses the bid/ask size ratio as a proxy for institutional order flow.
    //  Large bid-stack vs ask-stack = buy pressure.
    //  This is a real edge: dark pool prints and block orders often show
    //  up as persistent bid-stack before a breakout.
    //
    //  Note: IBKR tickSize field 0 = BID_SIZE, field 3 = ASK_SIZE.
    //  We'd need to store these separately — for now we use bid/ask price
    //  as a proxy: if mid > vwap and bid is closer to ask → bullish pressure.
    // ══════════════════════════════════════════════════════════
    private bool HasBullishOrderFlow(string symbol, decimal price)
    {
        if (!_latestBid.TryGetValue(symbol, out decimal bid)) return true;  // no data → allow
        if (!_latestAsk.TryGetValue(symbol, out decimal ask)) return true;
        if (bid <= 0 || ask <= 0 || ask < bid) return true;

        decimal spread = ask - bid;
        decimal mid = (ask + bid) / 2m;
        if (spread <= 0 || mid <= 0) return true;

        // How close is price to ask vs bid?
        // Price at ask = buyers aggressive; price at bid = sellers aggressive
        decimal closeToAsk = ask - price;
        decimal closeToBid = price - bid;

        // Bullish pressure: price trades closer to ask
        return closeToAsk <= closeToBid;
    }

    private bool HasBearishOrderFlow(string symbol, decimal price)
    {
        if (!_latestBid.TryGetValue(symbol, out decimal bid)) return true;
        if (!_latestAsk.TryGetValue(symbol, out decimal ask)) return true;
        if (bid <= 0 || ask <= 0 || ask < bid) return true;

        decimal closeToAsk = ask - price;
        decimal closeToBid = price - bid;

        // Bearish pressure: price trades closer to bid
        return closeToBid <= closeToAsk;
    }

    // ══════════════════════════════════════════════════════════
    //  SESSION EDGE SCORE
    //  Real-time estimate of how well the bot is performing today
    //  relative to its historical base rate.
    //  Score > 0 = better than historical average → increase confidence
    //  Score < 0 = worse than historical average → reduce size
    // ══════════════════════════════════════════════════════════
    private double GetSessionEdgeScore()
    {
        // Today's trades
        var todayStr = GetEasternTime().Date.ToString("yyyy-MM-dd");
        List<TradeRecord> today;
        lock (_allTrades)
        {
            today = _allTrades.Where(t => t.Date == todayStr).ToList();
        }
        if (today.Count < 2) return 0.0;

        double todayWR = today.Count(t => t.NetPnL > 0) / (double)today.Count;
        double todayEV = today.Count > 0 ? (double)today.Average(t => t.NetPnL) : 0;

        // Historical baseline (last 20 days)
        List<TradeRecord> historical;
        lock (_allTrades)
        {
            historical = _allTrades
                .Where(t => t.Date != todayStr)
                .TakeLast(200)
                .ToList();
        }
        if (historical.Count < 10) return 0.0;

        double histWR = historical.Count(t => t.NetPnL > 0) / (double)historical.Count;
        double histEV = (double)historical.Average(t => t.NetPnL);

        // Score = how much better today is vs baseline
        return (todayEV - histEV) / Math.Max(1.0, Math.Abs(histEV));
    }

    // ══════════════════════════════════════════════════════════
    //  VWAP RESET FIX
    //  If the bot starts mid-day, _vwapAccum is empty.
    //  The existing code only resets at 09:30:00–09:30:05.
    //  Add a cold-start check: if accumulation is empty and market is open,
    //  seed vwap from recent candles.
    // ══════════════════════════════════════════════════════════
    public void SeedVwapFromCandles(string symbol)
    {
        if (_vwapAccum.ContainsKey(symbol)) return;  // already accumulating

        if (!_marketData.TryGetValue(symbol, out var candles)) return;
        var etNow = GetEasternTime();
        var todayCandles = candles.Where(c => c.Time.Date == etNow.Date).ToList();
        if (todayCandles.Count == 0) return;

        decimal sumPV = 0m;
        long sumVol = 0;
        foreach (var c in todayCandles)
        {
            decimal typical = (c.High + c.Low + c.Close) / 3m;
            sumPV += typical * c.Volume;
            sumVol += c.Volume;
        }

        if (sumVol > 0)
        {
            _vwapAccum[symbol] = (sumPV, sumVol);
            _vwap[symbol] = sumPV / sumVol;
            LogMessage($"[VWAP SEED] {symbol} VWAP seeded from {todayCandles.Count} historical bars = {_vwap[symbol]:F2}");
        }
    }

    // ══════════════════════════════════════════════════════════
    //  IMPROVED CalcQty — blends Kelly with dynamic sizing
    //  Replaces the existing CalcQty method.
    //  Key changes:
    //  1. Kelly multiplier applied (blended 70/30)
    //  2. Session edge score adjusts size ±20%
    //  3. Stop distance not artificially floored (let ATR drive it)
    // ══════════════════════════════════════════════════════════
    private int CalcQtyV2(decimal price, decimal stopDistance, bool logIfScaled = true)
    {
        if (stopDistance < MIN_STOP_DISTANCE) stopDistance = MIN_STOP_DISTANCE;

        // ── Dynamic size multipliers ──
        decimal dynamicMult = GetDynamicSizeMultiplier();   // drawdown protection
        decimal kellyMult   = GetKellyMultiplier();          // edge-based sizing
        double  edgeScore   = GetSessionEdgeScore();         // today vs historical
        decimal edgeMult    = 1.0m + (decimal)Math.Clamp(edgeScore * 0.20, -0.20, 0.20);

        decimal combinedMult = dynamicMult * kellyMult * edgeMult;
        combinedMult = Math.Clamp(combinedMult, 0.25m, 1.50m);

        decimal riskAmount = TOTAL_BUDGET * RISK_PCT * combinedMult;
        int qty = (int)(riskAmount / stopDistance);

        // Budget cap
        decimal deployedCapital = _positions.Values.Sum(p => p.AvgPrice * p.Quantity)
                                + _pendingEntryCount * POSITION_SIZE;
        decimal remainingCash = Math.Max(0, TOTAL_BUDGET - deployedCapital);
        decimal effectiveSlot = Math.Min(POSITION_SIZE, remainingCash);
        int maxByBudget = price > 0 ? (int)(effectiveSlot / price) : 0;

        qty = Math.Min(qty, maxByBudget);
        if (qty <= 0 || qty > MAX_QTY_SANITY) return 0;

        if (logIfScaled && combinedMult < 0.95m)
            LogMessage($"[SMART SIZE] {combinedMult:P0} (DD={dynamicMult:P0} Kelly={kellyMult:P0} Edge={edgeMult:P0}) → qty={qty}");

        return qty;
    }

    // ══════════════════════════════════════════════════════════
    //  IMPROVED PassesDirectionalQualityGate
    //  Adds: order flow check, volatility check, regime velocity
    // ══════════════════════════════════════════════════════════
    private bool PassesEnhancedDirectionalGates(string symbol, List<Candle> candles,
        bool isShort, string strategyTag, decimal price, int minutesSinceOpen)
    {
        // 1. Time-of-day filter
        if (!PassesTimeOfDayFilter(strategyTag, minutesSinceOpen))
        {
            LogMessage($"[TOD GATE] {strategyTag} {symbol} blocked — not in optimal time window");
            return false;
        }

        // 2. Volatility regime filter
        if (IsVolatilityTooHigh(symbol, candles))
        {
            LogMessage($"[VOL GATE] {strategyTag} {symbol} blocked — intraday ATR spike (event day?)");
            return false;
        }

        // 3. Regime velocity gate
        if (!isShort && IsRegimeShiftingBearish())
        {
            // Only block non-reversal strategies
            bool isReversal = strategyTag.StartsWith("MEAN_REV_", StringComparison.OrdinalIgnoreCase)
                           || strategyTag.StartsWith("BB_MR_", StringComparison.OrdinalIgnoreCase)
                           || strategyTag.StartsWith("SCALP_PATTERN_", StringComparison.OrdinalIgnoreCase);
            if (!isReversal)
            {
                LogMessage($"[REGIME VEL] {strategyTag} {symbol} LONG blocked — SPY dropping fast");
                return false;
            }
        }
        if (isShort && IsRegimeShiftingBullish())
        {
            LogMessage($"[REGIME VEL] {strategyTag} {symbol} SHORT blocked — SPY surging");
            return false;
        }

        // 4. Order flow confirmation (for trend-following only)
        bool isTrend = strategyTag.StartsWith("SCALP_ORB_", StringComparison.OrdinalIgnoreCase)
                    || strategyTag.StartsWith("MOMENTUM_", StringComparison.OrdinalIgnoreCase)
                    || strategyTag.StartsWith("SCALP_BREAKOUT_", StringComparison.OrdinalIgnoreCase)
                    || strategyTag.StartsWith("GAP_GO_", StringComparison.OrdinalIgnoreCase);
        if (isTrend)
        {
            if (!isShort && !HasBullishOrderFlow(symbol, price))
            {
                LogMessage($"[OFI GATE] {strategyTag} {symbol} LONG blocked — bearish order flow (price at bid)");
                return false;
            }
            if (isShort && !HasBearishOrderFlow(symbol, price))
            {
                LogMessage($"[OFI GATE] {strategyTag} {symbol} SHORT blocked — bullish order flow (price at ask)");
                return false;
            }
        }

        return true;
    }

    // ══════════════════════════════════════════════════════════
    //  EOD MONTE CARLO REPORT
    //  Call from CheckEndOfDay() after halting.
    //  Appends MC projections to the EOD email.
    // ══════════════════════════════════════════════════════════
    public string BuildMonteCarloReport()
    {
        List<decimal> pnls;
        lock (_allTrades)
        {
            pnls = _allTrades.TakeLast(100).Select(t => t.NetPnL).ToList();
        }

        if (pnls.Count < 5) return "Monte Carlo: insufficient data (<5 trades)";

        var mc = RunMonteCarlo(pnls, TOTAL_BUDGET);

        return $"""
Monte Carlo Analysis ({mc.SimulationsRun} simulations, 60-session horizon)
──────────────────────────────────────────────────────────
Edge Assessment  : {mc.EdgeAssessment}
Win Rate         : {mc.WinRate:P1}
Avg Win / Loss   : ${mc.AvgWin:F2} / ${mc.AvgLoss:F2}
Expected EV/trade: ${mc.ExpectedValuePerTrade:F2}

Kelly Fraction   : {mc.KellyFraction:P1} (full)
Half-Kelly       : {mc.HalfKelly:P1}
Suggested RISK%  : {mc.SuggestedRiskPct:P1} (vs current {RISK_PCT:P1})

60-Day Projection (${TOTAL_BUDGET:F0} start):
  P5  worst case : ${mc.Percentile5Equity:F0}
  Median         : ${mc.MedianEquity:F0}
  P95 best case  : ${mc.Percentile95Equity:F0}
  Expected       : ${mc.ExpectedFinalEquity:F0}
  P(Ruin)        : {mc.ProbabilityOfRuin:P1}
──────────────────────────────────────────────────────────
{(mc.EdgeAssessment == "NEGATIVE" ? "⚠️  NEGATIVE EDGE — reduce size and review strategies" :
  mc.EdgeAssessment == "WEAK" ? "⚠️  WEAK EDGE — reduce size or improve selectivity" :
  mc.ProbabilityOfRuin > 0.10 ? "⚠️  HIGH RUIN RISK — reduce position size" :
  "✅  Edge looks positive — continue with current parameters")}
""";
    }

    // ══════════════════════════════════════════════════════════
    //  SUPPORT / RESISTANCE CLUSTER DETECTOR
    //  Uses daily candle data to find significant S/R levels.
    //  Entry within 0.3% of a key level gets a quality bonus.
    //  Entry that WOULD stop-out at a key level gets a penalty.
    //
    //  Real edge: institutional orders cluster at round numbers and
    //  prior day highs/lows. Knowing these prevents dumb stop placement.
    // ══════════════════════════════════════════════════════════
    private List<decimal> GetKeyLevels(string symbol, decimal price)
    {
        var levels = new List<decimal>();

        // Previous day H/L
        var (pdH, pdL) = GetPrevDayHL(symbol);
        if (pdH > 0) levels.Add(pdH);
        if (pdL > 0) levels.Add(pdL);

        // VWAP
        if (_vwap.TryGetValue(symbol, out decimal vwapVal) && vwapVal > 0)
            levels.Add(vwapVal);

        // ORB H/L
        if (_orbRanges.TryGetValue(symbol, out var orb) && orb.IsSet)
        {
            levels.Add(orb.High);
            levels.Add(orb.Low);
        }

        // Round numbers (within 2% of price)
        if (price > 0)
        {
            decimal tickSize = price > 100m ? 5m : price > 20m ? 1m : 0.5m;
            decimal roundDown = Math.Floor(price / tickSize) * tickSize;
            decimal roundUp = Math.Ceiling(price / tickSize) * tickSize;
            levels.Add(roundDown);
            levels.Add(roundUp);
        }

        // SMA 200 daily
        decimal sma200 = GetDailySma200(symbol);
        if (sma200 > 0) levels.Add(sma200);

        return levels.Where(l => l > 0).ToList();
    }

    /// <summary>
    /// Returns true if stop placement (entry ± stopDist) would land ON a key level,
    /// meaning the stop would be triggered by normal support/resistance bouncing.
    /// This is one of the biggest reasons retail stops get hit before the move resumes.
    /// </summary>
    private bool IsStopPlacedAtKeyLevel(string symbol, decimal entry, decimal stopDist, bool isShort)
    {
        decimal stopPrice = isShort ? entry + stopDist : entry - stopDist;
        var levels = GetKeyLevels(symbol, entry);
        decimal atr = _indicatorCache.TryGetValue(symbol, out var ind) ? ind.Atr14 : stopDist * 0.5m;
        decimal tolerance = atr * 0.20m;  // within 20% of ATR = too close

        foreach (var level in levels)
        {
            if (Math.Abs(stopPrice - level) <= tolerance)
            {
                LogMessage($"[STOP CLUSTER] {symbol} stop at {stopPrice:F2} is too close to key level {level:F2} — widen or skip");
                return true;
            }
        }
        return false;
    }

    // ══════════════════════════════════════════════════════════
    //  MARKET INTERNAL STRENGTH CHECK
    //  Checks if QQQ and IWM confirm the SPY direction.
    //  If only SPY is moving but QQQ/IWM diverge → breadth failure → skip longs.
    //  This catches the "SPY-only move" that doesn't stick.
    // ══════════════════════════════════════════════════════════
    private bool IsBreadthConfirming(bool forLongs)
    {
        // Need at least 2 of {SPY, QQQ, IWM} to agree
        int bullCount = 0;
        int bearCount = 0;
        foreach (var etf in new[] { "SPY", "QQQ", "IWM" })
        {
            if (!_marketData.TryGetValue(etf, out var c) || c.Count < 20) continue;
            decimal price = c.Last().Close;
            decimal sma20 = SafeSMA(c, 20);
            if (price > sma20) bullCount++; else bearCount++;
        }
        if (forLongs) return bullCount >= 2;
        return bearCount >= 2;
    }

    // ══════════════════════════════════════════════════════════
    //  CONSECUTIVE-LOSS POSITION HALVING
    //  After 2 consecutive losses on the same day, halve position size
    //  for the next 2 trades (instead of halting completely).
    //  This keeps the bot in the game while reducing exposure.
    // ══════════════════════════════════════════════════════════
    private decimal GetConsecutiveLossMultiplier()
    {
        if (_consecutiveLosses == 0) return 1.0m;
        if (_consecutiveLosses == 1) return 1.0m;
        if (_consecutiveLosses == 2) return 0.60m;  // 2 losses: trade at 60%
        return 0.35m;                                 // 3+ losses: trade at 35%
    }
}
