// ══════════════════════════════════════════════════════════════════════════════
//  BacktestEngine.cs
//  Standalone performance analytics + forward simulation engine.
//  NO IBKR connections. NO live trading code.
//
//  Two data sources are combined:
//    1. all_trades.json  — real live trades (primary, unbiased)
//    2. market_memory.json — 1-min candles for forward simulation
//    3. lifetime_equity.json — daily equity curve history
//
//  Output: BacktestReport (serialised to JSON for the dashboard /api/backtest)
// ══════════════════════════════════════════════════════════════════════════════

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;

// ── Report data structures ─────────────────────────────────────────────────

public class BtTrade
{
    public string Symbol { get; set; }
    public string Strategy { get; set; }
    public string Side { get; set; }   // "LONG" | "SHORT"
    public string Regime { get; set; }   // regime at entry
    public string Date { get; set; }   // yyyy-MM-dd
    public string Time { get; set; }   // HH:mm ET
    public decimal Entry { get; set; }
    public decimal Exit { get; set; }
    public int Qty { get; set; }
    public decimal NetPnL { get; set; }
    public decimal HoldMinutes { get; set; }
    public string ExitReason { get; set; }
    public bool IsWin { get; set; }
    public bool IsReal { get; set; }   // true = from all_trades.json
    public bool IsHistorical { get; set; }  // true = sim trade from historical replay (not live-session sim)
    // MAE = Maximum Adverse Excursion (how far it went against us, in $)
    public decimal Mae { get; set; }
}

public class RegimeStat
{
    public string Regime { get; set; }
    public int Trades { get; set; }
    public int Wins { get; set; }
    public double WinRate { get; set; }
    public decimal TotalPnL { get; set; }
    public decimal AvgPnL { get; set; }
    public decimal ProfitFactor { get; set; }
    public decimal AvgHoldMin { get; set; }
}

public class StrategyStat
{
    public string Strategy { get; set; }
    public int Trades { get; set; }
    public int Wins { get; set; }
    public double WinRate { get; set; }
    public decimal TotalPnL { get; set; }
    public decimal AvgPnL { get; set; }
    public decimal ProfitFactor { get; set; }
    public decimal AvgHoldMin { get; set; }
    public decimal LargestWin { get; set; }
    public decimal LargestLoss { get; set; }
    // MAE fields — populated from sim trades (real trades have Mae=0)
    public decimal AvgMae { get; set; }  // avg max adverse excursion in $
    public decimal AvgMaePct { get; set; }  // AvgMae as % of entry value
}

public class PeriodStat
{
    public string Label { get; set; }   // "1 Month", "6 Months", etc.
    public int TradingDays { get; set; }
    public int Trades { get; set; }
    public int Wins { get; set; }
    public double WinRate { get; set; }
    public decimal TotalPnL { get; set; }
    public decimal MaxDrawdown { get; set; }
    public decimal ProfitFactor { get; set; }
    public decimal SharpeRatio { get; set; }
    public decimal AvgDailyPnL { get; set; }
    public decimal AvgTradesPerDay { get; set; }
    public bool IsProjected { get; set; }
    public string DataNote { get; set; }
}

public class MonthlyPnL
{
    public string YearMonth { get; set; }  // "2025-03"
    public decimal PnL { get; set; }
    public int Trades { get; set; }
    public double WinRate { get; set; }
    public bool IsProjected { get; set; }
}

public class EquityPt
{
    public string Date { get; set; }  // yyyy-MM-dd
    public decimal Equity { get; set; }
    public decimal DailyPnL { get; set; }
    public bool IsProjected { get; set; }
}

public class RollingWinRate
{
    public string Date { get; set; }
    public double WinRate { get; set; }
    public int Window { get; set; }
}

public class ExitReasonStat
{
    public string ExitReason { get; set; }
    public int Trades { get; set; }
    public int Wins { get; set; }
    public double WinRate { get; set; }
    public decimal TotalPnL { get; set; }
    public decimal AvgPnL { get; set; }
}

public class WeekdayStat
{
    public string Day { get; set; }
    public int Trades { get; set; }
    public int Wins { get; set; }
    public double WinRate { get; set; }
    public decimal TotalPnL { get; set; }
}

public class HourStat
{
    public string Hour { get; set; }
    public int Trades { get; set; }
    public int Wins { get; set; }
    public double WinRate { get; set; }
    public decimal TotalPnL { get; set; }
}

// ── Shared config: passed from live bot so simulation matches live settings ──
public class BacktestConfig
{
    public decimal Capital { get; set; } = 4000m;
    public decimal RiskPct { get; set; } = 0.004m;
    public decimal PositionSize { get; set; } = 900m;
    public decimal HardStopAtrMult { get; set; } = 1.35m;
    public decimal AtrTrailMult { get; set; } = 1.25m;
    public decimal TargetAtrMult { get; set; } = 1.55m;
    public decimal Commission { get; set; } = 2.0m;
    public decimal EntrySlippagePct { get; set; } = 0.0003m;
    public decimal ExitSlippagePct { get; set; } = 0.0003m;
    public int MaxPositions { get; set; } = 3;
    public int MaxTradesPerDay { get; set; } = 4;
    public int MinHoldSeconds { get; set; } = 90;
    public double RsiLongMin { get; set; } = 60.0;
    public double RsiShortMax { get; set; } = 36.0;
    public int OrbMinutes { get; set; } = 12;
    public decimal BreakEvenTriggerR { get; set; } = 1.0m;
    public decimal MinBreakoutBodyRatio { get; set; } = 0.45m;
    public int MinEntryMinutesAfterOpen { get; set; } = 18;
    public int MinEntryQty { get; set; } = 5;
    public decimal MinGrossTargetToCommissionMult { get; set; } = 4.0m;
    public bool EnableCandlePatterns { get; set; } = false;
    public bool EnableOrb { get; set; } = true;
    public bool EnableGapGo { get; set; } = false;
    public bool EnableVwap { get; set; } = true;
    public bool EnableMomentum { get; set; } = true;
    public int PatternMinScore { get; set; } = 65;
    public bool AllowBullishPatternEntries { get; set; } = false;
    public bool AllowMicroPullback { get; set; } = false;
    public bool AllowScalpBreakoutLongs { get; set; } = false;
    public bool AllowScalpBreakoutShorts { get; set; } = false;
    public bool AllowScalpOrbLongs { get; set; } = false;
    public bool AllowShorts { get; set; } = true;
    public bool SwingMode { get; set; } = false;
    public bool EodLiquidate { get; set; } = true;
    public int SwingBaseLookbackDays { get; set; } = 20;
    public int SwingMaxHoldDays { get; set; } = 10;
    public decimal SwingTargetRMult { get; set; } = 3.2m;
    // Historical mode metadata
    public bool IsHistoricalMode { get; set; }
    public string HistoricalPeriodLabel { get; set; } = "";
    // Projections: extrapolated months, equity, and period stats — off by default
    // because with limited data they're misleading noise, not useful signals.
    public bool ShowProjections { get; set; } = false;
}

public class BacktestReport
{
    public DateTime GeneratedAt { get; set; }
    public string DataSummary { get; set; }
    public decimal InitialCapital { get; set; }
    public int RealTradesTotal { get; set; }
    public int SimTradesTotal { get; set; }
    public string DataFrom { get; set; }  // earliest trade date
    public string DataTo { get; set; }  // latest trade date
    public int TradingDaysActual { get; set; }

    // ── Historical mode metadata (set when called with date range) ─────────
    public bool IsHistoricalMode { get; set; }
    public int HistoricalTradesTotal { get; set; }
    public string HistoricalPeriodLabel { get; set; } = "";

    // ── Aggregate (real trades) ───────────────────────────────────────────
    public int TotalTrades { get; set; }
    public int Wins { get; set; }
    public int Losses { get; set; }
    public double WinRate { get; set; }
    public decimal TotalPnL { get; set; }
    public decimal MaxDrawdown { get; set; }
    public decimal ProfitFactor { get; set; }
    public decimal SharpeRatio { get; set; }
    public decimal AvgWin { get; set; }
    public decimal AvgLoss { get; set; }
    public decimal AvgHoldMinutes { get; set; }
    public int MaxConsecLosses { get; set; }
    public int MaxConsecWins { get; set; }
    public decimal LargestWin { get; set; }
    public decimal LargestLoss { get; set; }
    public decimal AvgMae { get; set; }
    public decimal MedianPnL { get; set; }
    public decimal Expectancy { get; set; }
    public decimal PayoffRatio { get; set; }
    public decimal PercentProfitableDays { get; set; }
    public int LongTrades { get; set; }
    public int ShortTrades { get; set; }

    // ── Simulation-only aggregate ─────────────────────────────────────────
    public int SimTrades { get; set; }
    public int SimWins { get; set; }
    public double SimWinRate { get; set; }
    public decimal SimTotalPnL { get; set; }
    public decimal SimProfitFactor { get; set; }

    // ── Breakdowns ────────────────────────────────────────────────────────
    public List<BtTrade> AllTrades { get; set; } = new();
    public List<RegimeStat> ByRegime { get; set; } = new();
    public List<StrategyStat> ByStrategy { get; set; } = new();
    public List<PeriodStat> ByPeriod { get; set; } = new();
    public List<MonthlyPnL> ByMonth { get; set; } = new();
    public List<EquityPt> EquityCurve { get; set; } = new();
    public List<RollingWinRate> Rolling7Day { get; set; } = new();
    public List<ExitReasonStat> ByExitReason { get; set; } = new();
    public List<WeekdayStat> ByWeekday { get; set; } = new();
    public List<HourStat> ByHour { get; set; } = new();
}

// ── Internal simulation position ──────────────────────────────────────────
internal class BtPosition
{
    public string Symbol;
    public bool IsShort;
    public decimal EntryPrice;
    public int Qty;
    public DateTime EntryTime;
    public decimal HighWater;
    public decimal LowWater;
    public string Strategy;
    public string Regime;
    public decimal HardStop;
    public decimal Target;
    public decimal AtrAtEntry;
    public bool MinHoldPassed;
    public decimal EntryCommission = 1m;
    // MAE tracking: most adverse price seen during hold
    // Long:  WorstPrice = lowest Low seen (MAE = Qty * (EntryPrice - WorstPrice))
    // Short: WorstPrice = highest High seen (MAE = Qty * (WorstPrice - EntryPrice))
    public decimal WorstPrice;
}

// ── Static indicator helpers ──────────────────────────────────────────────
internal static class BtCalc
{
    public static double CalcRSI(IReadOnlyList<Candle> c, int period)
    {
        if (c.Count < period * 2) return 50;
        int start = c.Count - period * 2;
        double ag = 0, al = 0;
        for (int i = start + 1; i <= start + period; i++)
        {
            double d = (double)(c[i].Close - c[i - 1].Close);
            if (d > 0) ag += d; else al -= d;
        }
        ag /= period; al /= period;
        for (int i = start + period + 1; i < c.Count; i++)
        {
            double d = (double)(c[i].Close - c[i - 1].Close);
            double g = d > 0 ? d : 0, l = d < 0 ? -d : 0;
            ag = (ag * (period - 1) + g) / period;
            al = (al * (period - 1) + l) / period;
        }
        if (ag == 0 && al == 0) return 50;
        if (al == 0) return 100;
        if (ag == 0) return 0;
        return 100 - 100 / (1 + ag / al);
    }

    public static decimal CalcATR(IReadOnlyList<Candle> c, int period)
    {
        if (c.Count <= period) return c.Count > 0 ? c[^1].Close * 0.002m : 0.01m;
        decimal s = 0;
        for (int i = c.Count - period + 1; i < c.Count; i++)
        {
            var p = c[i - 1]; var curr = c[i];
            decimal tr = Math.Max(curr.High - curr.Low,
                         Math.Max(Math.Abs(curr.High - p.Close),
                                  Math.Abs(curr.Low - p.Close)));
            s += tr;
        }
        return s / period;
    }

    public static decimal CalcSMA(IReadOnlyList<Candle> c, int period)
    {
        if (c.Count < period) return 0;
        decimal s = 0;
        for (int i = c.Count - period; i < c.Count; i++) s += c[i].Close;
        return s / period;
    }

    public static double CalcEMA(IReadOnlyList<Candle> c, int period)
    {
        if (c.Count < period) return (double)c[^1].Close;
        double k = 2.0 / (period + 1);
        double ema = (double)c[c.Count - period].Close;
        for (int i = c.Count - period + 1; i < c.Count; i++)
            ema = (double)c[i].Close * k + ema * (1 - k);
        return ema;
    }

    public static bool IsVolExpansion(IReadOnlyList<Candle> c, double mult = 2.5)
    {
        if (c.Count < 10) return false;
        long prev = 0, recent = 0;
        for (int i = c.Count - 10; i < c.Count - 5; i++) prev += c[i].Volume;
        for (int i = c.Count - 5; i < c.Count; i++) recent += c[i].Volume;
        return prev > 0 && recent > prev * (long)mult;
    }

    // Classify market regime from a trailing window of SPY candles
    public static string ClassifyRegime(IReadOnlyList<Candle> spy, DateTime day)
    {
        var todayC = spy.Where(c => c.Time.Date == day.Date).ToList();
        if (todayC.Count < 5) return "NORMAL";

        decimal sma20 = CalcSMA(spy, Math.Min(20, spy.Count));
        decimal sma50 = CalcSMA(spy, Math.Min(50, spy.Count));
        decimal last = spy[^1].Close;
        decimal todayRange = todayC.Max(c => c.High) - todayC.Min(c => c.Low);
        decimal atr14 = CalcATR(spy, Math.Min(14, spy.Count - 1));

        if (sma20 > 0 && sma50 > 0 && last < sma20 && sma20 < sma50) return "SELL-OFF";
        if (atr14 > 0 && todayRange > atr14 * 1.5m && last > sma20) return "TRENDING";
        if (atr14 > 0 && todayRange < atr14 * 0.6m) return "CHOPPY";
        return "NORMAL";
    }

    public static decimal CalcMaxDrawdown(IEnumerable<decimal> pnlSequence)
    {
        decimal peak = 0, cumulative = 0, maxDD = 0;
        foreach (var p in pnlSequence)
        {
            cumulative += p;
            if (cumulative > peak) peak = cumulative;
            decimal dd = peak - cumulative;
            if (dd > maxDD) maxDD = dd;
        }
        return maxDD;
    }

    public static decimal CalcSharpe(IList<decimal> dailyPnl)
    {
        if (dailyPnl.Count < 2) return 0;
        double avg = (double)dailyPnl.Average();
        double variance = dailyPnl.Select(p => Math.Pow((double)p - avg, 2)).Average();
        double stdDev = Math.Sqrt(variance);
        if (stdDev == 0) return 0;
        return (decimal)(avg / stdDev * Math.Sqrt(252));  // annualised
    }

    public static decimal CalcProfitFactor(IEnumerable<decimal> pnls)
    {
        decimal gross = pnls.Where(p => p > 0).Sum();
        decimal loss = pnls.Where(p => p < 0).Select(Math.Abs).Sum();
        return loss == 0 ? (gross > 0 ? 99m : 0m) : gross / loss;
    }
}

// ══════════════════════════════════════════════════════════════════════════
//  MAIN ENGINE
// ══════════════════════════════════════════════════════════════════════════

public static class BacktestEngine
{
    // ── Fallback defaults (used only when no config is passed) ────────────
    private static readonly BacktestConfig DefaultConfig = new();
    private static readonly HashSet<string> ExcludedSymbols = new(StringComparer.OrdinalIgnoreCase)
    { "VIX" };

    // ── Public entry point ─────────────────────────────────────────────────────
    // Called from /api/backtest. Accepts live in-memory data — no file I/O needed.
    // config is optional — if null, uses built-in defaults for backward compat.
    public static BacktestReport Analyze(
        IReadOnlyList<TradeRecord> liveTrades,
        System.Collections.Concurrent.ConcurrentDictionary<string, List<Candle>> candles1min,
        IReadOnlyList<LifetimeEquityPoint> lifetimeEquity,
        decimal capital,
        BacktestConfig config = null)
    {
        var cfg = config ?? DefaultConfig;

        var report = new BacktestReport
        {
            GeneratedAt = DateTime.UtcNow,
            InitialCapital = capital,
            IsHistoricalMode = cfg.IsHistoricalMode,
            HistoricalPeriodLabel = cfg.HistoricalPeriodLabel ?? ""
        };

        // ── 1. Convert live trade records ───────────────────────────────────
        var real = liveTrades
            .Where(t => t != null && !string.IsNullOrEmpty(t.Symbol))
            .Select(t => new BtTrade
            {
                Symbol = t.Symbol,
                Strategy = t.Strategy ?? "UNKNOWN",
                Side = t.Side ?? "LONG",
                Regime = string.IsNullOrEmpty(t.Regime) ? "UNKNOWN" : t.Regime,
                Date = t.Date ?? "",
                Time = t.Time ?? "",
                Entry = t.Entry,
                Exit = t.Exit,
                Qty = t.Qty,
                NetPnL = t.NetPnL,
                HoldMinutes = t.HoldMinutes,
                ExitReason = t.ExitReason ?? "",
                IsWin = t.NetPnL > 0,
                IsReal = true,
                Mae = 0m
            })
            .ToList();

        report.RealTradesTotal = real.Count;

        // ── 2. Forward simulation on available 1-min candles ───────────────
        var simTrades = RunSimulation(candles1min, cfg);
        report.SimTradesTotal = simTrades.Count;

        // ── 3. Merge — real trades are primary ─────────────────────────────
        report.AllTrades.AddRange(real);
        report.AllTrades.AddRange(simTrades);

        // ── 4. Aggregate stats from real trades ────────────────────────────
        ComputeAggregates(report, real);

        // ── 5. Breakdowns ──────────────────────────────────────────────────
        report.ByStrategy = BuildStrategyBreakdown(real);
        report.ByRegime = BuildRegimeBreakdown(real);
        report.ByMonth = BuildMonthlyBreakdown(real, capital, cfg);
        report.ByPeriod = BuildPeriodStats(real, lifetimeEquity, capital, cfg);
        report.EquityCurve = BuildEquityCurve(real, lifetimeEquity, capital, cfg);
        report.Rolling7Day = BuildRollingWinRate(real, 7);
        report.ByExitReason = BuildExitReasonBreakdown(real);
        report.ByWeekday = BuildWeekdayBreakdown(real);
        report.ByHour = BuildHourBreakdown(real);

        // Sim stats
        if (simTrades.Any())
        {
            report.SimTrades = simTrades.Count;
            report.SimWins = simTrades.Count(t => t.IsWin);
            report.SimWinRate = simTrades.Count > 0 ? (double)report.SimWins / simTrades.Count * 100 : 0;
            report.SimTotalPnL = simTrades.Sum(t => t.NetPnL);
            report.SimProfitFactor = BtCalc.CalcProfitFactor(simTrades.Select(t => t.NetPnL));
        }

        // Data summary for UI
        var dates = real.Where(t => !string.IsNullOrEmpty(t.Date))
                        .Select(t => t.Date).OrderBy(d => d).ToList();
        report.DataFrom = dates.FirstOrDefault() ?? "N/A";
        report.DataTo = dates.LastOrDefault() ?? "N/A";
        report.TradingDaysActual = dates.Distinct().Count();
        report.DataSummary = $"{real.Count} real trades across {report.TradingDaysActual} trading days" +
                             (simTrades.Any() ? $" + {simTrades.Count} simulated trades" : "");

        if (cfg.IsHistoricalMode)
            report.HistoricalTradesTotal = real.Count + simTrades.Count;

        return report;
    }

    // ── Aggregate stats ────────────────────────────────────────────────────────
    private static void ComputeAggregates(BacktestReport r, List<BtTrade> trades)
    {
        if (!trades.Any()) return;

        r.TotalTrades = trades.Count;
        r.Wins = trades.Count(t => t.IsWin);
        r.Losses = trades.Count(t => !t.IsWin);
        r.WinRate = r.TotalTrades > 0 ? (double)r.Wins / r.TotalTrades * 100 : 0;
        r.TotalPnL = trades.Sum(t => t.NetPnL);
        r.AvgWin = r.Wins > 0 ? trades.Where(t => t.IsWin).Average(t => t.NetPnL) : 0;
        r.AvgLoss = r.Losses > 0 ? trades.Where(t => !t.IsWin).Average(t => t.NetPnL) : 0;
        r.LargestWin = trades.Any(t => t.IsWin) ? trades.Where(t => t.IsWin).Max(t => t.NetPnL) : 0;
        r.LargestLoss = trades.Any(t => !t.IsWin) ? trades.Where(t => !t.IsWin).Min(t => t.NetPnL) : 0;
        r.AvgHoldMinutes = trades.Any() ? trades.Average(t => t.HoldMinutes) : 0;
        r.ProfitFactor = BtCalc.CalcProfitFactor(trades.Select(t => t.NetPnL));
        r.MaxDrawdown = BtCalc.CalcMaxDrawdown(trades.OrderBy(t => t.Date + t.Time).Select(t => t.NetPnL));
        var orderedPnls = trades.Select(t => t.NetPnL).OrderBy(v => v).ToList();
        if (orderedPnls.Count > 0)
        {
            int mid = orderedPnls.Count / 2;
            r.MedianPnL = orderedPnls.Count % 2 == 1 ? orderedPnls[mid] : (orderedPnls[mid - 1] + orderedPnls[mid]) / 2m;
        }
        r.Expectancy = trades.Any() ? trades.Average(t => t.NetPnL) : 0m;
        r.PayoffRatio = r.AvgLoss < 0 ? Math.Abs(r.AvgWin / r.AvgLoss) : 0m;
        r.LongTrades = trades.Count(t => string.Equals(t.Side, "LONG", StringComparison.OrdinalIgnoreCase));
        r.ShortTrades = trades.Count(t => string.Equals(t.Side, "SHORT", StringComparison.OrdinalIgnoreCase));

        // Consecutive streaks
        int consec = 0, maxL = 0, maxW = 0, curL = 0, curW = 0;
        foreach (var t in trades.OrderBy(t => t.Date + t.Time))
        {
            if (t.IsWin) { curW++; curL = 0; maxW = Math.Max(maxW, curW); }
            else { curL++; curW = 0; maxL = Math.Max(maxL, curL); }
        }
        r.MaxConsecWins = maxW;
        r.MaxConsecLosses = maxL;

        // Sharpe from daily PnL
        var dailyPnl = trades
            .GroupBy(t => t.Date)
            .Select(g => g.Sum(t => t.NetPnL))
            .ToList();
        r.SharpeRatio = BtCalc.CalcSharpe(dailyPnl);
        r.PercentProfitableDays = dailyPnl.Any() ? (decimal)dailyPnl.Count(v => v > 0) / dailyPnl.Count * 100m : 0m;

        // AvgMae — only meaningful on sim trades (real trades lack intraday path data)
        var maeTrades = trades.Where(t => t.Mae > 0).ToList();
        r.AvgMae = maeTrades.Any() ? maeTrades.Average(t => t.Mae) : 0m;
    }

    // ── Strategy breakdown ─────────────────────────────────────────────────────
    private static List<StrategyStat> BuildStrategyBreakdown(List<BtTrade> trades)
    {
        return trades
            .GroupBy(t => t.Strategy)
            .Select(g =>
            {
                var pnls = g.Select(t => t.NetPnL).ToList();
                var maeTrades = g.Where(t => t.Mae > 0).ToList();
                decimal avgMae = maeTrades.Any() ? maeTrades.Average(t => t.Mae) : 0m;
                decimal avgEntry = g.Average(t => t.Entry);
                decimal avgQty = g.Any() ? (decimal)g.Average(t => t.Qty) : 1m;
                decimal avgMaePct = (avgEntry > 0 && avgQty > 0) ? avgMae / (avgEntry * avgQty) * 100m : 0m;
                return new StrategyStat
                {
                    Strategy = g.Key,
                    Trades = g.Count(),
                    Wins = g.Count(t => t.IsWin),
                    WinRate = g.Count() > 0 ? (double)g.Count(t => t.IsWin) / g.Count() * 100 : 0,
                    TotalPnL = pnls.Sum(),
                    AvgPnL = pnls.Average(),
                    ProfitFactor = BtCalc.CalcProfitFactor(pnls),
                    AvgHoldMin = g.Average(t => t.HoldMinutes),
                    LargestWin = pnls.Where(p => p > 0).DefaultIfEmpty(0).Max(),
                    LargestLoss = pnls.Where(p => p < 0).DefaultIfEmpty(0).Min(),
                    AvgMae = avgMae,
                    AvgMaePct = avgMaePct
                };
            })
            .OrderByDescending(s => s.TotalPnL)
            .ToList();
    }

    // ── Regime breakdown ──────────────────────────────────────────────────────
    private static List<RegimeStat> BuildRegimeBreakdown(List<BtTrade> trades)
    {
        return trades
            .GroupBy(t => string.IsNullOrEmpty(t.Regime) ? "UNKNOWN" : t.Regime)
            .Select(g =>
            {
                var pnls = g.Select(t => t.NetPnL).ToList();
                return new RegimeStat
                {
                    Regime = g.Key,
                    Trades = g.Count(),
                    Wins = g.Count(t => t.IsWin),
                    WinRate = g.Count() > 0 ? (double)g.Count(t => t.IsWin) / g.Count() * 100 : 0,
                    TotalPnL = pnls.Sum(),
                    AvgPnL = pnls.Average(),
                    ProfitFactor = BtCalc.CalcProfitFactor(pnls),
                    AvgHoldMin = g.Average(t => t.HoldMinutes)
                };
            })
            .OrderByDescending(r => r.TotalPnL)
            .ToList();
    }

    // ── Exit reason breakdown ──────────────────────────────────────────────────
    private static List<ExitReasonStat> BuildExitReasonBreakdown(List<BtTrade> trades)
    {
        return trades
            .GroupBy(t => string.IsNullOrWhiteSpace(t.ExitReason) ? "UNKNOWN" : t.ExitReason)
            .Select(g => new ExitReasonStat
            {
                ExitReason = g.Key,
                Trades = g.Count(),
                Wins = g.Count(t => t.IsWin),
                WinRate = g.Any() ? (double)g.Count(t => t.IsWin) / g.Count() * 100 : 0,
                TotalPnL = g.Sum(t => t.NetPnL),
                AvgPnL = g.Any() ? g.Average(t => t.NetPnL) : 0m
            })
            .OrderByDescending(x => x.Trades)
            .ToList();
    }

    // ── Weekday breakdown ──────────────────────────────────────────────────────
    private static List<WeekdayStat> BuildWeekdayBreakdown(List<BtTrade> trades)
    {
        var order = new[] { "Monday", "Tuesday", "Wednesday", "Thursday", "Friday" };
        return trades
            .Where(t => DateTime.TryParse(t.Date, out _))
            .GroupBy(t => DateTime.Parse(t.Date).DayOfWeek)
            .Select(g => new WeekdayStat
            {
                Day = g.Key.ToString(),
                Trades = g.Count(),
                Wins = g.Count(t => t.IsWin),
                WinRate = g.Any() ? (double)g.Count(t => t.IsWin) / g.Count() * 100 : 0,
                TotalPnL = g.Sum(t => t.NetPnL)
            })
            .OrderBy(x => Array.IndexOf(order, x.Day))
            .ToList();
    }

    // ── Hour breakdown ─────────────────────────────────────────────────────────
    private static List<HourStat> BuildHourBreakdown(List<BtTrade> trades)
    {
        return trades
            .Where(t => !string.IsNullOrWhiteSpace(t.Time) && t.Time.Length >= 2)
            .GroupBy(t => t.Time.Substring(0, 2))
            .Select(g => new HourStat
            {
                Hour = g.Key + ":00",
                Trades = g.Count(),
                Wins = g.Count(t => t.IsWin),
                WinRate = g.Any() ? (double)g.Count(t => t.IsWin) / g.Count() * 100 : 0,
                TotalPnL = g.Sum(t => t.NetPnL)
            })
            .OrderBy(x => x.Hour)
            .ToList();
    }

    // ── Monthly breakdown ──────────────────────────────────────────────────────
    private static List<MonthlyPnL> BuildMonthlyBreakdown(List<BtTrade> trades, decimal capital, BacktestConfig cfg)
    {
        if (!trades.Any()) return new();

        var actual = trades
            .Where(t => !string.IsNullOrEmpty(t.Date))
            .GroupBy(t => t.Date.Length >= 7 ? t.Date.Substring(0, 7) : "")
            .Where(g => !string.IsNullOrEmpty(g.Key))
            .Select(g => new MonthlyPnL
            {
                YearMonth = g.Key,
                PnL = g.Sum(t => t.NetPnL),
                Trades = g.Count(),
                WinRate = g.Count() > 0 ? (double)g.Count(t => t.IsWin) / g.Count() * 100 : 0,
                IsProjected = false
            })
            .OrderBy(m => m.YearMonth)
            .ToList();

        // Project future months (up to 12) based on average actual monthly PnL
        if (cfg.ShowProjections && actual.Any())
        {
            decimal avgMonthly = actual.Average(m => m.PnL);
            int avgTrades = (int)actual.Average(m => m.Trades);
            var lastMonth = actual.Last().YearMonth;
            if (DateTime.TryParse(lastMonth + "-01", out var lastDt))
            {
                for (int i = 1; i <= 12; i++)
                {
                    var projMonth = lastDt.AddMonths(i);
                    if (projMonth > DateTime.UtcNow.AddMonths(1)) break;
                    actual.Add(new MonthlyPnL
                    {
                        YearMonth = projMonth.ToString("yyyy-MM"),
                        PnL = avgMonthly,
                        Trades = avgTrades,
                        WinRate = actual.Average(m => m.WinRate),
                        IsProjected = true
                    });
                }
            }
        }

        return actual;
    }

    // ── Period stats (1M, 6M, 1Y, 2Y, 5Y) ────────────────────────────────────
    private static List<PeriodStat> BuildPeriodStats(
        List<BtTrade> trades,
        IReadOnlyList<LifetimeEquityPoint> equity,
        decimal capital,
        BacktestConfig cfg)
    {
        var result = new List<PeriodStat>();
        if (!trades.Any()) return result;

        // Daily stats — base for all projections
        var byDay = trades
            .Where(t => !string.IsNullOrEmpty(t.Date))
            .GroupBy(t => t.Date)
            .OrderBy(g => g.Key)
            .Select(g => (Date: g.Key, PnL: g.Sum(t => t.NetPnL), Trades: g.Count(), Wins: g.Count(t => t.IsWin)))
            .ToList();

        if (!byDay.Any()) return result;

        decimal avgDailyPnL = byDay.Average(d => d.PnL);
        decimal stdDailyPnL = StdDev(byDay.Select(d => d.PnL).ToList());
        double avgDailyTrades = byDay.Average(d => (double)d.Trades);
        double avgDailyWinPct = byDay.Average(d => d.Trades > 0 ? (double)d.Wins / d.Trades : 0.5);
        int actualDays = byDay.Count;

        // Periods: (label, target trading days)
        var periods = new[]
        {
            ("1 Month",  22),
            ("6 Months", 126),
            ("1 Year",   252),
            ("2 Years",  504),
            ("5 Years",  1260)
        };

        foreach (var (label, tDays) in periods)
        {
            bool projected = tDays > actualDays;
            if (projected && !cfg.ShowProjections) continue;
            decimal projPnL;
            decimal projDD;
            decimal projPF;

            if (!projected)
            {
                // Use actual data for this window
                var window = byDay.TakeLast(tDays).ToList();
                projPnL = window.Sum(d => d.PnL);
                projDD = BtCalc.CalcMaxDrawdown(window.Select(d => d.PnL));
                projPF = BtCalc.CalcProfitFactor(
                    trades.Where(t => !string.IsNullOrEmpty(t.Date)
                              && t.Date.CompareTo(window.First().Date) >= 0)
                          .Select(t => t.NetPnL));
            }
            else
            {
                // Project using daily average ± uncertainty
                projPnL = avgDailyPnL * tDays;
                // Max drawdown estimate: scales with sqrt(time) (random walk approximation)
                projDD = stdDailyPnL * (decimal)Math.Sqrt(tDays);
                projPF = BtCalc.CalcProfitFactor(trades.Select(t => t.NetPnL));
            }

            var dailyPnlList = !projected
                ? byDay.TakeLast(tDays).Select(d => d.PnL).ToList()
                : Enumerable.Range(0, tDays).Select(_ => avgDailyPnL).ToList();

            result.Add(new PeriodStat
            {
                Label = label,
                TradingDays = tDays,
                Trades = !projected ? byDay.TakeLast(tDays).Sum(d => d.Trades) : (int)(avgDailyTrades * tDays),
                Wins = !projected ? byDay.TakeLast(tDays).Sum(d => d.Wins) : (int)(avgDailyTrades * tDays * avgDailyWinPct),
                WinRate = avgDailyWinPct * 100,
                TotalPnL = projPnL,
                MaxDrawdown = projDD,
                ProfitFactor = projPF,
                SharpeRatio = BtCalc.CalcSharpe(dailyPnlList),
                AvgDailyPnL = avgDailyPnL,
                AvgTradesPerDay = (decimal)avgDailyTrades,
                IsProjected = projected,
                DataNote = projected
                    ? $"Projected from {actualDays} actual trading day(s). Assumes similar market conditions."
                    : $"Based on {tDays} actual trading days of data."
            });
        }

        return result;
    }

    // ── Equity curve ──────────────────────────────────────────────────────────
    private static List<EquityPt> BuildEquityCurve(
        List<BtTrade> trades,
        IReadOnlyList<LifetimeEquityPoint> lifetime,
        decimal capital,
        BacktestConfig cfg)
    {
        var result = new List<EquityPt>();

        // Prefer lifetime equity (daily snapshots) if available
        if (lifetime != null && lifetime.Any())
        {
            decimal running = capital;
            foreach (var pt in lifetime.OrderBy(p => p.Date))
            {
                result.Add(new EquityPt
                {
                    Date = pt.Date,
                    Equity = pt.AccountValue,
                    DailyPnL = pt.DailyPnL,
                    IsProjected = false
                });
            }
        }
        else if (trades.Any())
        {
            // Build from trade records
            decimal running = capital;
            foreach (var day in trades
                .Where(t => !string.IsNullOrEmpty(t.Date))
                .GroupBy(t => t.Date)
                .OrderBy(g => g.Key))
            {
                decimal dayPnL = day.Sum(t => t.NetPnL);
                running += dayPnL;
                result.Add(new EquityPt { Date = day.Key, Equity = running, DailyPnL = dayPnL, IsProjected = false });
            }
        }

        // Project 30 days forward (only when explicitly enabled)
        if (cfg.ShowProjections && result.Any())
        {
            decimal avgDayPnL = trades.Any()
                ? trades.GroupBy(t => t.Date).Average(g => g.Sum(t => t.NetPnL))
                : 0;
            decimal lastEq = result.Last().Equity;
            var lastDate = DateTime.TryParse(result.Last().Date, out var ld) ? ld : DateTime.UtcNow;
            for (int i = 1; i <= 30; i++)
            {
                var d = lastDate.AddDays(i);
                if (d.DayOfWeek == DayOfWeek.Saturday || d.DayOfWeek == DayOfWeek.Sunday) continue;
                lastEq += avgDayPnL;
                result.Add(new EquityPt { Date = d.ToString("yyyy-MM-dd"), Equity = lastEq, DailyPnL = avgDayPnL, IsProjected = true });
            }
        }

        return result;
    }

    // ── Rolling 7-day win rate ─────────────────────────────────────────────────
    private static List<RollingWinRate> BuildRollingWinRate(List<BtTrade> trades, int windowTrades)
    {
        var ordered = trades.OrderBy(t => t.Date + t.Time).ToList();
        if (ordered.Count < windowTrades) return new();

        var result = new List<RollingWinRate>();
        for (int i = windowTrades - 1; i < ordered.Count; i++)
        {
            var window = ordered.Skip(i - windowTrades + 1).Take(windowTrades).ToList();
            result.Add(new RollingWinRate
            {
                Date = ordered[i].Date,
                WinRate = (double)window.Count(t => t.IsWin) / windowTrades * 100,
                Window = windowTrades
            });
        }
        return result;
    }

    // ── Forward simulation ─────────────────────────────────────────────────────
    // Runs simplified strategy logic on available 1-min candles.
    // Uses the same entry conditions as SimulatedBroker but simplified exits.
    private static List<BtTrade> RunSimulation(
        System.Collections.Concurrent.ConcurrentDictionary<string, List<Candle>> allCandles,
        BacktestConfig cfg)
    {
        if (allCandles == null || !allCandles.Any()) return new();

        // Sim-specific exit parameters (no live bot equivalent — these are simplified exit models)
        const decimal FIXED_PROFIT_TARGET_PCT = 0.0100m;
        const decimal FIXED_STOP_LOSS_PCT = 0.0100m;
        const decimal SCALP_PROFIT_TARGET_PCT = 0.0040m;
        const decimal SCALP_STOP_LOSS_PCT = 0.0028m;
        const int SCALP_MAX_HOLD_SECONDS = 480;

        var trades = new List<BtTrade>();
        var positions = new Dictionary<string, BtPosition>();

        // ── Build chronological tick stream across all symbols ─────────────
        var allEvents = allCandles
            .SelectMany(kv => kv.Value
                .Select(c => (Symbol: kv.Key, Candle: c)))
            .OrderBy(e => e.Candle.Time)
            .ToList();

        if (!allEvents.Any()) return trades;

        // Per-symbol rolling window (last 100 candles) for indicators
        var windows = new Dictionary<string, List<Candle>>();
        var vwapAccum = new Dictionary<string, (decimal sumPV, long sumVol)>();
        var orbRanges = new Dictionary<string, (decimal High, decimal Low, bool IsSet, int MinuteCount)>();
        var prevClose = new Dictionary<string, decimal>();
        var lastDate = new Dictionary<string, DateTime>();
        var tradesPerDay = new Dictionary<DateTime, int>();
        var symbolTradesToday = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

        // ── Replay ──────────────────────────────────────────────────────────
        foreach (var (symbol, candle) in allEvents)
        {
            if (ExcludedSymbols.Contains(symbol)) continue;
            var etTime = candle.Time;  // already ET from the bot's candle data

            // Reset daily structures at each new trading day
            if (!lastDate.TryGetValue(symbol, out var prev) || prev.Date != etTime.Date)
            {
                if (windows.TryGetValue(symbol, out var w) && w.Any())
                    prevClose[symbol] = w.Last().Close;
                vwapAccum[symbol] = (sumPV: 0m, sumVol: 0L);
                orbRanges[symbol] = (candle.High, candle.Low, false, 0);
                lastDate[symbol] = etTime;
                if (!tradesPerDay.ContainsKey(etTime.Date)) tradesPerDay[etTime.Date] = 0;
            }

            // Maintain rolling window
            if (!windows.ContainsKey(symbol)) windows[symbol] = new();
            windows[symbol].Add(candle);
            if (windows[symbol].Count > 120) windows[symbol].RemoveAt(0);
            var win = windows[symbol];

            // Update VWAP
            if (candle.Volume > 0)
            {
                var va = vwapAccum.GetValueOrDefault(symbol, (sumPV: 0m, sumVol: 0L));
                vwapAccum[symbol] = (sumPV: va.sumPV + candle.Close * candle.Volume,
                                     sumVol: va.sumVol + candle.Volume);
            }
            decimal vwap = vwapAccum.TryGetValue(symbol, out var vv) && vv.sumVol > 0
                ? vv.sumPV / vv.sumVol : 0m;

            // Update ORB
            int minsSinceOpen = (etTime.Hour - 9) * 60 + etTime.Minute - 30;
            if (orbRanges.TryGetValue(symbol, out var orb) && minsSinceOpen >= 0 && minsSinceOpen <= cfg.OrbMinutes)
            {
                orb = (Math.Max(orb.High, candle.High), Math.Min(orb.Low, candle.Low),
                       minsSinceOpen >= cfg.OrbMinutes, minsSinceOpen);
                orbRanges[symbol] = orb;
            }

            // ── Check exits for open positions ─────────────────────────────
            if (positions.TryGetValue(symbol, out var pos))
            {
                // Update WorstPrice on every candle — tracks the most adverse
                // price seen during the hold. Used to compute MAE at exit.
                // Long:  adverse = lowest Low  (went against us downward)
                // Short: adverse = highest High (went against us upward)
                if (!pos.IsShort)
                    pos.WorstPrice = pos.WorstPrice == 0 ? candle.Low : Math.Min(pos.WorstPrice, candle.Low);
                else
                    pos.WorstPrice = pos.WorstPrice == 0 ? candle.High : Math.Max(pos.WorstPrice, candle.High);
                positions[symbol] = pos;

                bool exit = false;
                string reason = "";
                decimal exitPrice = candle.Close;

                // EOD exit
                if (etTime.Hour == 15 && etTime.Minute >= 25)
                { exit = true; reason = "EOD"; }

                bool isScalp = (pos.Strategy ?? "").StartsWith("SCALP_", StringComparison.OrdinalIgnoreCase);
                decimal profitPct = isScalp ? SCALP_PROFIT_TARGET_PCT : FIXED_PROFIT_TARGET_PCT;
                decimal stopPct = isScalp ? SCALP_STOP_LOSS_PCT : FIXED_STOP_LOSS_PCT;

                if (!exit && (etTime - pos.EntryTime).TotalSeconds >= cfg.MinHoldSeconds)
                {
                    decimal targetPx = pos.IsShort
                        ? pos.EntryPrice * (1m - profitPct)
                        : pos.EntryPrice * (1m + profitPct);
                    decimal stopPx = pos.IsShort
                        ? pos.EntryPrice * (1m + stopPct)
                        : pos.EntryPrice * (1m - stopPct);

                    if (!pos.IsShort && candle.High >= targetPx)
                    { exit = true; exitPrice = targetPx; reason = isScalp ? "SCALP_TP" : "FIXED_TP"; }
                    if (!exit && pos.IsShort && candle.Low <= targetPx)
                    { exit = true; exitPrice = targetPx; reason = isScalp ? "SCALP_TP" : "FIXED_TP"; }

                    if (!exit && !pos.IsShort && candle.Low <= stopPx)
                    { exit = true; exitPrice = stopPx; reason = isScalp ? "SCALP_SL" : "FIXED_SL"; }
                    if (!exit && pos.IsShort && candle.High >= stopPx)
                    { exit = true; exitPrice = stopPx; reason = isScalp ? "SCALP_SL" : "FIXED_SL"; }

                    if (!exit && isScalp && (etTime - pos.EntryTime).TotalSeconds >= SCALP_MAX_HOLD_SECONDS)
                    { exit = true; exitPrice = candle.Close; reason = "SCALP_TIME_EXIT"; }
                }

                if (exit)
                {
                    decimal exitSlip = Math.Max(0.01m, exitPrice * cfg.ExitSlippagePct);
                    exitPrice = pos.IsShort ? exitPrice + exitSlip : exitPrice - exitSlip;
                    decimal gross = pos.IsShort
                        ? pos.Qty * (pos.EntryPrice - exitPrice)
                        : pos.Qty * (exitPrice - pos.EntryPrice);
                    decimal net = gross - cfg.Commission;

                    // MAE in dollars: how far the trade moved against us at its worst point
                    // Long:  EntryPrice - WorstLow  (positive when it dipped below entry)
                    // Short: WorstHigh - EntryPrice (positive when it spiked above entry)
                    decimal maePrice = pos.IsShort
                        ? Math.Max(0, pos.WorstPrice - pos.EntryPrice)
                        : Math.Max(0, pos.EntryPrice - pos.WorstPrice);
                    decimal mae = maePrice * pos.Qty;

                    trades.Add(new BtTrade
                    {
                        Symbol = symbol,
                        Strategy = pos.Strategy,
                        Side = pos.IsShort ? "SHORT" : "LONG",
                        Regime = pos.Regime,
                        Date = etTime.ToString("yyyy-MM-dd"),
                        Time = etTime.ToString("HH:mm"),
                        Entry = pos.EntryPrice,
                        Exit = exitPrice,
                        Qty = pos.Qty,
                        NetPnL = net,
                        HoldMinutes = (decimal)(etTime - pos.EntryTime).TotalMinutes,
                        ExitReason = reason,
                        IsWin = net > 0,
                        IsReal = false,
                        IsHistorical = cfg.IsHistoricalMode,
                        Mae = mae
                    });
                    positions.Remove(symbol);
                }
            }

            // ── Try entry (only during valid hours, only when no open position) ──
            if (positions.ContainsKey(symbol)) continue;
            if (positions.Count >= cfg.MaxPositions) continue;
            if (tradesPerDay.GetValueOrDefault(etTime.Date) >= cfg.MaxTradesPerDay) continue;
            string tradeKey = symbol + "|" + etTime.Date.ToString("yyyy-MM-dd");
            if (symbolTradesToday.GetValueOrDefault(tradeKey) >= 3) continue;
            if (etTime.Hour < 9 || (etTime.Hour == 9 && etTime.Minute < (30 + cfg.MinEntryMinutesAfterOpen))) continue;
            if (etTime.Hour > 15 || (etTime.Hour == 15 && etTime.Minute >= 30)) continue;
            if (win.Count < 50) continue;

            // Classify regime using available SPY candles
            string regime = "NORMAL";
            if (allCandles.TryGetValue("SPY", out var spyCandles) && spyCandles.Count >= 20)
                regime = BtCalc.ClassifyRegime(spyCandles, etTime);

            // SPY opening-hour bias: if SPY first 30 min closed > 0.3% below open → bearish session
            // All long entries are blocked for the day (mirrors the live bot _spyOpenBearish filter)
            bool spyOpenBearish = false;
            if (allCandles.TryGetValue("SPY", out var spyC2))
            {
                var todaySpy = spyC2.Where(c => c.Time.Date == etTime.Date).OrderBy(c => c.Time).ToList();
                if (todaySpy.Count >= 30 && todaySpy.First().Open > 0)
                {
                    decimal firstBar30Close = todaySpy[Math.Min(29, todaySpy.Count - 1)].Close;
                    spyOpenBearish = (firstBar30Close - todaySpy.First().Open) / todaySpy.First().Open < -0.003m;
                }
            }
            bool blockLongs = regime == "SELL-OFF" || spyOpenBearish;

            decimal close = candle.Close;
            decimal atr = BtCalc.CalcATR(win, 14);
            if (atr <= 0 || close <= 0) continue;
            var todayWin = win.Where(c => c.Time.Date == etTime.Date).ToList();
            decimal todayDollarVolume = todayWin.Sum(c => c.Close * c.Volume);
            decimal requiredDollarVolume = Math.Max(
                500_000m,
                20_000_000m * Math.Clamp(minsSinceOpen, 1, 390) / 390m);
            if (todayDollarVolume < requiredDollarVolume) continue;
            double rsi = BtCalc.CalcRSI(win, 14);
            decimal sma20 = BtCalc.CalcSMA(win, 20);
            decimal sma50 = BtCalc.CalcSMA(win, Math.Min(50, win.Count));
            bool volExp = BtCalc.IsVolExpansion(win);
            long avgVol10 = (long)win.TakeLast(10).Average(c => c.Volume);
            bool volOk = candle.Volume >= avgVol10;
            BtPosition newPos = null;

            // ── Candlestick pattern entry (runs FIRST — early signals) ──
            if (newPos == null && cfg.EnableCandlePatterns && win.Count >= 6)
            {
                var patternResult = DetectBtPattern(win, atr);
                if (patternResult.score >= cfg.PatternMinScore)
                {
                    if (patternResult.bullish && cfg.AllowBullishPatternEntries && !blockLongs && rsi >= 44 && (vwap <= 0 || close >= vwap - atr * 0.5m) && volOk)
                    {
                        int qty = CalcQty(close, atr * cfg.HardStopAtrMult, cfg);
                        if (qty > 0)
                            newPos = new BtPosition { Symbol = symbol, IsShort = false, EntryPrice = close, Qty = qty, EntryTime = etTime, HighWater = close, LowWater = close, Strategy = $"SCALP_PATTERN_{patternResult.tag}_LONG", Regime = regime, HardStop = close - atr * cfg.HardStopAtrMult, Target = close + atr * cfg.TargetAtrMult, AtrAtEntry = atr, WorstPrice = close };
                    }
                    else if (cfg.AllowShorts && !patternResult.bullish && rsi <= 56 && (vwap > 0 && close <= vwap + atr * 0.5m))
                    {
                        int qty = CalcQty(close, atr * cfg.HardStopAtrMult, cfg);
                        if (qty > 0)
                            newPos = new BtPosition { Symbol = symbol, IsShort = true, EntryPrice = close, Qty = qty, EntryTime = etTime, HighWater = close, LowWater = close, Strategy = $"SCALP_PATTERN_{patternResult.tag}_SHORT", Regime = regime, HardStop = close + atr * cfg.HardStopAtrMult, Target = close - atr * cfg.TargetAtrMult, AtrAtEntry = atr, WorstPrice = close };
                    }
                }
            }

            // ── Micro pullback (catches entry after impulse + shallow retrace) ──
            if (newPos == null && cfg.AllowMicroPullback && win.Count >= 8 && !blockLongs)
            {
                bool foundImpulse = false;
                decimal pullbackLow = decimal.MaxValue;
                for (int lookback = win.Count - 4; lookback >= Math.Max(0, win.Count - 7); lookback--)
                {
                    var imp = win[lookback];
                    decimal impBody = imp.Close - imp.Open;
                    decimal impRange = imp.High - imp.Low;
                    if (impBody > atr * 0.6m && impRange > 0 && impBody / impRange >= 0.55m)
                    {
                        bool pbValid = true;
                        for (int j = lookback + 1; j < win.Count - 1; j++)
                        {
                            if (Math.Abs(win[j].Close - win[j].Open) > impBody * 0.70m) { pbValid = false; break; }
                            pullbackLow = Math.Min(pullbackLow, win[j].Low);
                        }
                        decimal retrace = imp.Close - pullbackLow;
                        if (pbValid && impBody > 0 && retrace <= impBody * 0.60m)
                        {
                            decimal lastRange = candle.High - candle.Low;
                            decimal closePos = lastRange > 0 ? (candle.Close - candle.Low) / lastRange : 0.5m;
                            if (closePos >= 0.60m && candle.Close > candle.Open && rsi >= 48)
                            {
                                foundImpulse = true;
                                break;
                            }
                        }
                    }
                }
                if (foundImpulse)
                {
                    decimal stopDist = Math.Max(Math.Max(close - pullbackLow, atr * 1.5m), 0.10m);
                    int qty = CalcQty(close, stopDist, cfg);
                    if (qty > 0)
                        newPos = new BtPosition { Symbol = symbol, IsShort = false, EntryPrice = close, Qty = qty, EntryTime = etTime, HighWater = close, LowWater = close, Strategy = "SCALP_PULLBACK_LONG", Regime = regime, HardStop = close - stopDist, Target = close + atr * cfg.TargetAtrMult, AtrAtEntry = atr, WorstPrice = close };
                }
            }

            // ── Dedicated breakout scalper ───────────────────────────────
            if (newPos == null && win.Count >= 25 && (regime == "TRENDING" || regime == "NORMAL"))
            {
                var priorThree = win.Skip(Math.Max(0, win.Count - 4)).Take(3).ToList();
                if (priorThree.Count == 3)
                {
                    decimal recentHigh3 = priorThree.Max(c => c.High);
                    decimal recentLow3 = priorThree.Min(c => c.Low);
                    decimal range = candle.High - candle.Low;
                    decimal closePos = range > 0 ? (candle.Close - candle.Low) / range : 0.5m;
                    bool fastVol = avgVol10 > 0 && candle.Volume >= avgVol10 * 12 / 10;
                    bool longBias = sma20 > 0 && close > sma20 && close > vwap && rsi >= 54 && rsi <= 78;
                    bool shortBias = sma20 > 0 && close < sma20 && close < vwap && rsi >= 22 && rsi <= 46;

                    if (cfg.AllowScalpBreakoutLongs && !blockLongs && longBias && fastVol && closePos >= 0.65m && close > recentHigh3)
                    {
                        int qty = CalcQty(close, Math.Max(atr * 0.75m, close - candle.Low), cfg);
                        if (qty > 0)
                            newPos = new BtPosition { Symbol = symbol, IsShort = false, EntryPrice = close, Qty = qty, EntryTime = etTime, HighWater = close, LowWater = close, Strategy = "SCALP_BREAKOUT_LONG", Regime = regime, HardStop = close - Math.Max(atr * 0.75m, close - candle.Low), Target = close + atr * 1.10m, AtrAtEntry = atr, WorstPrice = close };
                    }
                    else if (cfg.AllowShorts && cfg.AllowScalpBreakoutShorts && shortBias && fastVol && closePos <= 0.35m && close < recentLow3)
                    {
                        int qty = CalcQty(close, Math.Max(atr * 0.75m, candle.High - close), cfg);
                        if (qty > 0)
                            newPos = new BtPosition { Symbol = symbol, IsShort = true, EntryPrice = close, Qty = qty, EntryTime = etTime, HighWater = close, LowWater = close, Strategy = "SCALP_BREAKOUT_SHORT", Regime = regime, HardStop = close + Math.Max(atr * 0.75m, candle.High - close), Target = close - atr * 1.10m, AtrAtEntry = atr, WorstPrice = close };
                    }
                }
            }

            // ── ORB strategy ─────────────────────────────────────────────
            if (newPos == null && cfg.EnableOrb && orbRanges.TryGetValue(symbol, out orb) && orb.IsSet && regime != "CHOPPY")
            {
                bool orbLongHold = win.Count >= 2 && win[^1].Close > orb.High && win[^2].Close > orb.High;
                bool orbShortHold = win.Count >= 2 && win[^1].Close < orb.Low && win[^2].Close < orb.Low;

                // ORB_LONG: blocked in SELL-OFF or bearish SPY open
                // Also requires stock to be UP or flat on the day (daily direction filter)
                if (cfg.AllowScalpOrbLongs && !blockLongs && orbLongHold && rsi > cfg.RsiLongMin && volOk && HasStrongBodyClose(candle, true, cfg))
                {
                    // Stock daily direction check: must be >= -0.2% vs prev candle day's last close
                    bool stockDayOk = true;
                    if (lastDate.TryGetValue(symbol, out var lastDt) && win.Count > 1)
                    {
                        var prevDayCandles = allCandles.TryGetValue(symbol, out var symAll)
                            ? symAll.Where(c => c.Time.Date < etTime.Date).ToList() : new List<Candle>();
                        if (prevDayCandles.Any())
                        {
                            decimal prevDayClose = prevDayCandles.Last().Close;
                            if (prevDayClose > 0) stockDayOk = (close - prevDayClose) / prevDayClose >= -0.002m;
                        }
                    }
                    if (stockDayOk)
                    {
                        int qty = CalcQty(close, atr * cfg.HardStopAtrMult, cfg);
                        if (qty > 0)
                            newPos = new BtPosition { Symbol = symbol, IsShort = false, EntryPrice = close, Qty = qty, EntryTime = etTime, HighWater = close, LowWater = close, Strategy = "SCALP_ORB_LONG", Regime = regime, HardStop = close - atr * cfg.HardStopAtrMult, Target = close + atr * cfg.TargetAtrMult, AtrAtEntry = atr, WorstPrice = close };
                    }
                }
                // ORB_SHORT: preferred in SELL-OFF / bearish open days
                else if (cfg.AllowShorts && orbShortHold && rsi < cfg.RsiShortMax && volOk && HasStrongBodyClose(candle, false, cfg))
                {
                    if (!(symbol.Equals("MSTR", StringComparison.OrdinalIgnoreCase) || symbol.Equals("COIN", StringComparison.OrdinalIgnoreCase) || symbol.Equals("TSLA", StringComparison.OrdinalIgnoreCase)) || regime == "SELL-OFF")
                    {
                        int qty = CalcQty(close, atr * cfg.HardStopAtrMult, cfg);
                        if (qty > 0)
                            newPos = new BtPosition { Symbol = symbol, IsShort = true, EntryPrice = close, Qty = qty, EntryTime = etTime, HighWater = close, LowWater = close, Strategy = "SCALP_ORB_SHORT", Regime = regime, HardStop = close + atr * cfg.HardStopAtrMult, Target = close - atr * cfg.TargetAtrMult, AtrAtEntry = atr, WorstPrice = close };
                    }
                }
            }

            // ── Gap and Go ─────────────────────────────────────────────────
            if (newPos == null && cfg.EnableGapGo && prevClose.TryGetValue(symbol, out var pdClose) && pdClose > 0)
            {
                decimal gapPct = (close - pdClose) / pdClose;
                decimal relVol = avgVol10 > 0 ? candle.Volume / (decimal)avgVol10 : 0m;
                if (gapPct >= 0.02m && relVol >= 1.8m && !blockLongs && rsi > cfg.RsiLongMin && volOk && close > vwap)
                {
                    int qty = CalcQty(close, atr * cfg.HardStopAtrMult, cfg);
                    if (qty > 0)
                        newPos = new BtPosition { Symbol = symbol, IsShort = false, EntryPrice = close, Qty = qty, EntryTime = etTime, HighWater = close, LowWater = close, Strategy = "GAP_GO_LONG", Regime = regime, HardStop = close - atr * cfg.HardStopAtrMult, Target = close + atr * cfg.TargetAtrMult, AtrAtEntry = atr, WorstPrice = close };
                }
                else if (cfg.AllowShorts && gapPct <= -0.02m && relVol >= 1.8m && rsi < cfg.RsiShortMax && close < vwap)
                {
                    int qty = CalcQty(close, atr * cfg.HardStopAtrMult, cfg);
                    if (qty > 0)
                        newPos = new BtPosition { Symbol = symbol, IsShort = true, EntryPrice = close, Qty = qty, EntryTime = etTime, HighWater = close, LowWater = close, Strategy = "GAP_GO_SHORT", Regime = regime, HardStop = close + atr * cfg.HardStopAtrMult, Target = close - atr * cfg.TargetAtrMult, AtrAtEntry = atr, WorstPrice = close };
                }
            }

            // ── VWAP Reclaim (blocked in SELL-OFF and bearish open) ───────
            if (newPos == null && cfg.EnableVwap && vwap > 0 && volExp && !blockLongs && HasStrongBodyClose(candle, true, cfg))
            {
                bool reclaim = win.Count >= 3 && win[^1].Close > vwap && win[^2].Close > vwap && win[^3].Close <= vwap;
                if (reclaim && rsi > cfg.RsiLongMin)
                {
                    int qty = CalcQty(close, atr * cfg.HardStopAtrMult, cfg);
                    if (qty > 0)
                        newPos = new BtPosition { Symbol = symbol, IsShort = false, EntryPrice = close, Qty = qty, EntryTime = etTime, HighWater = close, LowWater = close, Strategy = "SCALP_VWAP_LONG", Regime = regime, HardStop = close - atr * cfg.HardStopAtrMult, Target = close + atr * cfg.TargetAtrMult, AtrAtEntry = atr, WorstPrice = close };
                }
            }

            // ── VWAP Reject Short ──────────────────────────────────────────
            if (newPos == null && cfg.EnableVwap && cfg.AllowShorts && vwap > 0 && volExp && HasStrongBodyClose(candle, false, cfg))
            {
                bool reject = win.Count >= 3 && win[^1].Close < vwap && win[^2].Close < vwap && win[^3].Close >= vwap;
                if (reject && rsi < cfg.RsiShortMax)
                {
                    int qty = CalcQty(close, atr * cfg.HardStopAtrMult, cfg);
                    if (qty > 0)
                        newPos = new BtPosition { Symbol = symbol, IsShort = true, EntryPrice = close, Qty = qty, EntryTime = etTime, HighWater = close, LowWater = close, Strategy = "SCALP_VWAP_SHORT", Regime = regime, HardStop = close + atr * cfg.HardStopAtrMult, Target = close - atr * cfg.TargetAtrMult, AtrAtEntry = atr, WorstPrice = close };
                }
            }

            // ── Momentum Breakout (TRENDING only, blocked in SELL-OFF) ────
            if (newPos == null && cfg.EnableMomentum && volExp && sma20 > sma50 && sma50 > 0 && regime == "TRENDING" && !blockLongs && HasStrongBodyClose(candle, true, cfg))
            {
                decimal recentHigh = win.Take(win.Count - 1).TakeLast(8).Max(c => c.High);
                // Require volatility compression before the breakout (coil → spring)
                bool compressed = win.Count >= 20 && BtCalc.CalcATR(win.TakeLast(20).ToList(), 10) < BtCalc.CalcATR(win, 14) * 0.8m;
                bool breakout = close > recentHigh && rsi > cfg.RsiLongMin && compressed;
                if (breakout)
                {
                    int qty = CalcQty(close, atr * cfg.HardStopAtrMult, cfg);
                    if (qty > 0)
                        newPos = new BtPosition { Symbol = symbol, IsShort = false, EntryPrice = close, Qty = qty, EntryTime = etTime, HighWater = close, LowWater = close, Strategy = "MOMENTUM_LONG", Regime = regime, HardStop = close - atr * cfg.HardStopAtrMult, Target = close + atr * cfg.TargetAtrMult, AtrAtEntry = atr, WorstPrice = close };
                }
            }

            // ── Momentum short continuation ────────────────────────────────
            if (newPos == null && cfg.EnableMomentum && cfg.AllowShorts && volExp && sma50 > 0 && close < sma20 && close < sma50 && HasStrongBodyClose(candle, false, cfg))
            {
                decimal recentLow = win.Take(win.Count - 1).TakeLast(8).Min(c => c.Low);
                bool breakdown = close < recentLow && rsi < cfg.RsiShortMax && (regime == "SELL-OFF" || regime == "NORMAL");
                if (breakdown)
                {
                    int qty = CalcQty(close, atr * cfg.HardStopAtrMult, cfg);
                    if (qty > 0)
                        newPos = new BtPosition { Symbol = symbol, IsShort = true, EntryPrice = close, Qty = qty, EntryTime = etTime, HighWater = close, LowWater = close, Strategy = "MOMENTUM_SHORT", Regime = regime, HardStop = close + atr * cfg.HardStopAtrMult, Target = close - atr * cfg.TargetAtrMult, AtrAtEntry = atr, WorstPrice = close };
                }
            }

            if (newPos != null)
            {
                if (false)
                    newPos.IsShort = !newPos.IsShort;

                decimal entrySlip = Math.Max(0.01m, close * cfg.EntrySlippagePct);
                newPos.EntryPrice = newPos.IsShort ? close - entrySlip : close + entrySlip;
                newPos.HardStop = newPos.IsShort ? newPos.EntryPrice + atr * cfg.HardStopAtrMult : newPos.EntryPrice - atr * cfg.HardStopAtrMult;
                newPos.Target = newPos.IsShort ? newPos.EntryPrice - atr * cfg.TargetAtrMult : newPos.EntryPrice + atr * cfg.TargetAtrMult;
                positions[symbol] = newPos;
                tradesPerDay[etTime.Date] = tradesPerDay.GetValueOrDefault(etTime.Date) + 1;
                symbolTradesToday[tradeKey] = symbolTradesToday.GetValueOrDefault(tradeKey) + 1;
            }
        }

        // Close any remaining open positions at end of data
        foreach (var kvp in positions)
        {
            var pos = kvp.Value;
            var lastCandle = allCandles.TryGetValue(kvp.Key, out var lc) ? lc.LastOrDefault() : null;
            if (lastCandle == null) continue;
            decimal exitPrice = lastCandle.Close;
            decimal exitSlip = Math.Max(0.01m, exitPrice * cfg.ExitSlippagePct);
            exitPrice = pos.IsShort ? exitPrice + exitSlip : exitPrice - exitSlip;
            decimal gross = pos.IsShort ? pos.Qty * (pos.EntryPrice - exitPrice) : pos.Qty * (exitPrice - pos.EntryPrice);
            decimal net = gross - cfg.Commission;
            decimal maeEod = pos.IsShort
                ? Math.Max(0, pos.WorstPrice - pos.EntryPrice) * pos.Qty
                : Math.Max(0, pos.EntryPrice - pos.WorstPrice) * pos.Qty;
            trades.Add(new BtTrade
            {
                Symbol = pos.Symbol,
                Strategy = pos.Strategy,
                Side = pos.IsShort ? "SHORT" : "LONG",
                Regime = pos.Regime,
                Date = lastCandle.Time.ToString("yyyy-MM-dd"),
                Time = lastCandle.Time.ToString("HH:mm"),
                Entry = pos.EntryPrice,
                Exit = exitPrice,
                Qty = pos.Qty,
                NetPnL = net,
                HoldMinutes = (decimal)(lastCandle.Time - pos.EntryTime).TotalMinutes,
                ExitReason = "END_OF_DATA",
                IsWin = net > 0,
                IsReal = false,
                IsHistorical = cfg.IsHistoricalMode,
                Mae = maeEod
            });
        }

        return trades;
    }

    private static bool HasStrongBodyClose(Candle candle, bool bullish, BacktestConfig cfg)
    {
        decimal range = candle.High - candle.Low;
        if (range <= 0) return false;
        decimal closePos = (candle.Close - candle.Low) / range;
        return bullish ? closePos >= cfg.MinBreakoutBodyRatio : closePos <= (1m - cfg.MinBreakoutBodyRatio);
    }

    private static (int score, string tag, bool bullish) DetectBtPattern(IReadOnlyList<Candle> candles, decimal atr)
    {
        if (candles == null || candles.Count < 3 || atr <= 0) return (0, "", false);

        var last = candles[^1];
        var prev = candles[^2];
        decimal tol = Math.Max(last.Close * 0.002m, atr * 0.18m);

        decimal lastBody = Math.Abs(last.Close - last.Open);
        decimal prevBody = Math.Abs(prev.Close - prev.Open);
        bool prevBear = prev.Close < prev.Open;
        bool prevBull = prev.Close > prev.Open;
        bool lastBull = last.Close > last.Open;
        bool lastBear = last.Close < last.Open;
        decimal lw = Math.Min(last.Open, last.Close) - last.Low;
        decimal uw = last.High - Math.Max(last.Open, last.Close);

        // Context: preceding 5-bar move
        decimal move5 = candles.Count >= 6 ? candles[^1].Close - candles[^6].Close : 0m;
        bool hadDown = move5 < -atr * 0.5m;
        bool hadUp = move5 > atr * 0.5m;

        int bestScore = 0; string bestTag = ""; bool bestBullish = false;
        void Check(int score, string tag, bool bullish)
        { if (score > bestScore) { bestScore = score; bestTag = tag; bestBullish = bullish; } }

        // ── 2-bar: Engulfing ──
        decimal prevBL = Math.Min(prev.Open, prev.Close), prevBH = Math.Max(prev.Open, prev.Close);
        decimal lastBL = Math.Min(last.Open, last.Close), lastBH = Math.Max(last.Open, last.Close);
        if (prevBear && lastBull && lastBL <= prevBL && lastBH >= prevBH)
            Check(62 + (hadDown ? 6 : 0), "BULL_ENGULFING", true);
        if (prevBull && lastBear && lastBH >= prevBH && lastBL <= prevBL)
            Check(62 + (hadUp ? 6 : 0), "BEAR_ENGULFING", false);

        // ── 1-bar: Hammer / Shooting star ──
        if (lastBody > 0 && lw >= lastBody * 2.0m && uw <= lastBody * 0.6m)
            Check(55 + (hadDown ? 8 : 0), "HAMMER", true);
        if (lastBody > 0 && uw >= lastBody * 2.0m && lw <= lastBody * 0.6m)
            Check(55 + (hadUp ? 8 : 0), "SHOOTING_STAR", false);

        // ── 1-bar: Inverted Hammer / Hanging Man ──
        if (lastBody > 0 && uw >= lastBody * 2.0m && lw <= lastBody * 0.5m && hadDown)
            Check(52 + (lastBull ? 3 : 0), "INV_HAMMER", true);
        if (lastBody > 0 && lw >= lastBody * 2.0m && uw <= lastBody * 0.5m && hadUp)
            Check(52 + (lastBear ? 3 : 0), "HANGING_MAN", false);

        // ── 1-bar: Doji ──
        if (lastBody > 0 && lastBody <= (last.High - last.Low) * 0.10m && (last.High - last.Low) >= atr * 0.3m)
        {
            if (hadDown && lw > uw) Check(50, "DRAGONFLY_DOJI", true);
            if (hadUp && uw > lw) Check(50, "GRAVESTONE_DOJI", false);
        }

        // ── 2-bar: Piercing / Dark Cloud ──
        if (prevBear && lastBull && last.Open <= prev.Low && last.Close > (prev.Open + prev.Close) / 2m)
            Check(60 + (hadDown ? 5 : 0), "PIERCING_LINE", true);
        if (prevBull && lastBear && last.Open >= prev.High && last.Close < (prev.Open + prev.Close) / 2m)
            Check(60 + (hadUp ? 5 : 0), "DARK_CLOUD", false);

        // ── 2-bar: Tweezer ──
        if (Math.Abs(last.Low - prev.Low) <= tol && lastBull && prevBear)
            Check(56 + (hadDown ? 5 : 0), "TWEEZER_BOTTOM", true);
        if (Math.Abs(last.High - prev.High) <= tol && lastBear && prevBull)
            Check(56 + (hadUp ? 5 : 0), "TWEEZER_TOP", false);

        // ── 3-bar patterns ──
        if (candles.Count >= 3)
        {
            var a = candles[^3]; var b = candles[^2]; var c = candles[^1];
            decimal aB = Math.Abs(a.Close - a.Open);
            decimal bB = Math.Abs(b.Close - b.Open);
            decimal aMid = (a.Open + a.Close) / 2m;

            // Morning / Evening Star
            if (a.Close < a.Open && bB <= Math.Max(aB, lastBody) * 0.50m && c.Close > c.Open && c.Close >= aMid)
                Check(68 + (hadDown ? 5 : 0), "MORNING_STAR", true);
            if (a.Close > a.Open && bB <= Math.Max(aB, lastBody) * 0.50m && c.Close < c.Open && c.Close <= aMid)
                Check(68 + (hadUp ? 5 : 0), "EVENING_STAR", false);

            // Abandoned Baby
            if (a.Close < a.Open && b.High < Math.Min(a.Close, a.Open)
                && c.Close > c.Open && c.Low > Math.Max(b.Open, b.Close))
                Check(75, "BULL_ABANDONED_BABY", true);
            if (a.Close > a.Open && b.Low > Math.Max(a.Close, a.Open)
                && c.Close < c.Open && c.High < Math.Min(b.Open, b.Close))
                Check(75, "BEAR_ABANDONED_BABY", false);

            // Three Inside Up/Down
            if (a.Close < a.Open && bB < aB
                && Math.Min(b.Open, b.Close) >= Math.Min(a.Open, a.Close)
                && Math.Max(b.Open, b.Close) <= Math.Max(a.Open, a.Close)
                && c.Close > c.Open && c.Close > a.Open)
                Check(65 + (hadDown ? 5 : 0), "THREE_INSIDE_UP", true);
            if (a.Close > a.Open && bB < aB
                && Math.Min(b.Open, b.Close) >= Math.Min(a.Open, a.Close)
                && Math.Max(b.Open, b.Close) <= Math.Max(a.Open, a.Close)
                && c.Close < c.Open && c.Close < a.Open)
                Check(65 + (hadUp ? 5 : 0), "THREE_INSIDE_DOWN", false);
        }

        // ── 3-bar: Soldiers / Crows ──
        if (candles.Count >= 4)
        {
            var x = candles[^3]; var y = candles[^2]; var z = candles[^1];
            if (x.Close > x.Open && y.Close > y.Open && z.Close > z.Open
                && y.Close > x.Close && z.Close > y.Close)
                Check(74, "THREE_WHITE_SOLDIERS", true);
            if (x.Close < x.Open && y.Close < y.Open && z.Close < z.Open
                && y.Close < x.Close && z.Close < y.Close)
                Check(74, "THREE_BLACK_CROWS", false);
        }

        // ── Head & Shoulders (11-bar window) ──
        if (candles.Count >= 11)
        {
            var w = new List<Candle>();
            for (int i = candles.Count - 11; i < candles.Count; i++) w.Add(candles[i]);
            var highs = new List<int>(); var lows = new List<int>();
            for (int i = 1; i < w.Count - 1; i++)
            {
                if (w[i].High > w[i - 1].High && w[i].High > w[i + 1].High) highs.Add(i);
                if (w[i].Low < w[i - 1].Low && w[i].Low < w[i + 1].Low) lows.Add(i);
            }
            if (highs.Count >= 3)
            {
                var h = highs.Skip(highs.Count - 3).ToList();
                decimal ls = w[h[0]].High, head = w[h[1]].High, rs = w[h[2]].High;
                decimal neckline = Math.Min(
                    w.Skip(h[0]).Take(Math.Max(1, h[1] - h[0] + 1)).Min(cc => cc.Low),
                    w.Skip(h[1]).Take(Math.Max(1, h[2] - h[1] + 1)).Min(cc => cc.Low));
                if (head > ls + atr * 0.30m && head > rs + atr * 0.30m
                    && Math.Abs(ls - rs) <= atr * 0.50m && w[^1].Close < neckline)
                    Check(72, "HEAD_SHOULDERS", false);
            }
            if (lows.Count >= 3)
            {
                var l = lows.Skip(lows.Count - 3).ToList();
                decimal ls = w[l[0]].Low, head = w[l[1]].Low, rs = w[l[2]].Low;
                decimal neckline = Math.Max(
                    w.Skip(l[0]).Take(Math.Max(1, l[1] - l[0] + 1)).Max(cc => cc.High),
                    w.Skip(l[1]).Take(Math.Max(1, l[2] - l[1] + 1)).Max(cc => cc.High));
                if (head < ls - atr * 0.30m && head < rs - atr * 0.30m
                    && Math.Abs(ls - rs) <= atr * 0.50m && w[^1].Close > neckline)
                    Check(72, "INV_HEAD_SHOULDERS", true);
            }
        }

        // ── Double Top / Bottom (15-bar window) ──
        if (candles.Count >= 15)
        {
            decimal low1 = decimal.MaxValue, low2 = decimal.MaxValue;
            int lo1 = -1, lo2 = -1;
            int wStart = candles.Count - 15;
            for (int i = 1; i < 8; i++)
                if (candles[wStart + i].Low < low1) { low1 = candles[wStart + i].Low; lo1 = i; }
            for (int i = 8; i < 14; i++)
                if (candles[wStart + i].Low < low2) { low2 = candles[wStart + i].Low; lo2 = i; }
            if (lo1 > 0 && lo2 > 0 && Math.Abs(low1 - low2) <= atr * 0.40m)
            {
                decimal peakBtw = decimal.MinValue;
                for (int i = lo1; i <= lo2; i++) peakBtw = Math.Max(peakBtw, candles[wStart + i].High);
                if (peakBtw - Math.Max(low1, low2) >= atr * 0.5m && candles[^1].Close > peakBtw * 0.995m)
                    Check(70, "DOUBLE_BOTTOM", true);
            }
            decimal hi1 = decimal.MinValue, hi2 = decimal.MinValue;
            int h1 = -1, h2 = -1;
            for (int i = 1; i < 8; i++)
                if (candles[wStart + i].High > hi1) { hi1 = candles[wStart + i].High; h1 = i; }
            for (int i = 8; i < 14; i++)
                if (candles[wStart + i].High > hi2) { hi2 = candles[wStart + i].High; h2 = i; }
            if (h1 > 0 && h2 > 0 && Math.Abs(hi1 - hi2) <= atr * 0.40m)
            {
                decimal troughBtw = decimal.MaxValue;
                for (int i = h1; i <= h2; i++) troughBtw = Math.Min(troughBtw, candles[wStart + i].Low);
                if (Math.Min(hi1, hi2) - troughBtw >= atr * 0.5m && candles[^1].Close < troughBtw * 1.005m)
                    Check(70, "DOUBLE_TOP", false);
            }
        }

        return (bestScore, bestTag, bestBullish);
    }

    private static int CalcQty(decimal price, decimal stopDistance, BacktestConfig cfg)
    {
        decimal minStop = Math.Max(0.10m, price * 0.003m);
        if (stopDistance < minStop) stopDistance = minStop;
        decimal risk = cfg.Capital * cfg.RiskPct;
        int qty = (int)(risk / stopDistance);
        int maxBySlot = price > 0 ? (int)(cfg.PositionSize / price) : 0;
        qty = Math.Min(qty, maxBySlot);
        if (qty <= 0 || qty > 500 || qty < cfg.MinEntryQty) return 0;

        decimal roundTripCommission = cfg.Commission * 2m;
        decimal grossTargetPnL = stopDistance * cfg.TargetAtrMult * qty;
        decimal minGrossTargetPnL = roundTripCommission * cfg.MinGrossTargetToCommissionMult;
        if (grossTargetPnL < minGrossTargetPnL) return 0;

        return qty;
    }

    private static decimal StdDev(IList<decimal> vals)
    {
        if (vals.Count < 2) return 0;
        double avg = (double)vals.Average();
        double variance = vals.Select(v => Math.Pow((double)v - avg, 2)).Average();
        return (decimal)Math.Sqrt(variance);
    }
}
