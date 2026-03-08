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
    private const decimal INITIAL_CAPITAL = 4000m;
    private const decimal RISK_PCT = 0.012m;
    private const decimal POSITION_SIZE = 1500m;
    private const decimal HARD_STOP_ATR_MULT = 2.0m;
    private const decimal ATR_TRAIL_MULT = 2.0m;
    private const decimal TARGET_ATR_MULT = 3.0m;
    private const decimal COMMISSION = 2.0m;  // $1 each side
    private const int MAX_POSITIONS = 2;
    private const int MIN_HOLD_SECONDS = 300;
    private const double RSI_LONG_MIN = 65.0;
    private const double RSI_SHORT_MAX = 35.0;
    private const int ORB_MINUTES = 30;

    // ── Public entry point ─────────────────────────────────────────────────────
    // Called from /api/backtest. Accepts live in-memory data — no file I/O needed.
    public static BacktestReport Analyze(
        IReadOnlyList<TradeRecord> liveTrades,
        System.Collections.Concurrent.ConcurrentDictionary<string, List<Candle>> candles1min,
        IReadOnlyList<LifetimeEquityPoint> lifetimeEquity,
        decimal capital)
    {
        var report = new BacktestReport
        {
            GeneratedAt = DateTime.UtcNow,
            InitialCapital = capital
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
        var simTrades = RunSimulation(candles1min);
        report.SimTradesTotal = simTrades.Count;

        // ── 3. Merge — real trades are primary ─────────────────────────────
        report.AllTrades.AddRange(real);
        report.AllTrades.AddRange(simTrades);

        // ── 4. Aggregate stats from real trades ────────────────────────────
        ComputeAggregates(report, real);

        // ── 5. Breakdowns ──────────────────────────────────────────────────
        report.ByStrategy = BuildStrategyBreakdown(real);
        report.ByRegime = BuildRegimeBreakdown(real);
        report.ByMonth = BuildMonthlyBreakdown(real, capital);
        report.ByPeriod = BuildPeriodStats(real, lifetimeEquity, capital);
        report.EquityCurve = BuildEquityCurve(real, lifetimeEquity, capital);
        report.Rolling7Day = BuildRollingWinRate(real, 7);

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

    // ── Monthly breakdown ──────────────────────────────────────────────────────
    private static List<MonthlyPnL> BuildMonthlyBreakdown(List<BtTrade> trades, decimal capital)
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
        if (actual.Any())
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
        decimal capital)
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
        decimal capital)
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

        // Project 30 days forward
        if (result.Any())
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
        System.Collections.Concurrent.ConcurrentDictionary<string, List<Candle>> allCandles)
    {
        if (allCandles == null || !allCandles.Any()) return new();

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

        // ── Replay ──────────────────────────────────────────────────────────
        foreach (var (symbol, candle) in allEvents)
        {
            var etTime = candle.Time;  // already ET from the bot's candle data

            // Reset daily structures at each new trading day
            if (!lastDate.TryGetValue(symbol, out var prev) || prev.Date != etTime.Date)
            {
                if (windows.TryGetValue(symbol, out var w) && w.Any())
                    prevClose[symbol] = w.Last().Close;
                vwapAccum[symbol] = (sumPV: 0m, sumVol: 0L);
                orbRanges[symbol] = (candle.High, candle.Low, false, 0);
                lastDate[symbol] = etTime;
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
            if (orbRanges.TryGetValue(symbol, out var orb) && minsSinceOpen >= 0 && minsSinceOpen <= ORB_MINUTES)
            {
                orb = (Math.Max(orb.High, candle.High), Math.Min(orb.Low, candle.Low),
                       minsSinceOpen >= ORB_MINUTES, minsSinceOpen);
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

                // Hard stop (check candle high/low)
                if (!exit && !pos.IsShort && candle.Low <= pos.HardStop)
                { exit = true; exitPrice = pos.HardStop; reason = "STOP"; }
                if (!exit && pos.IsShort && candle.High >= pos.HardStop)
                { exit = true; exitPrice = pos.HardStop; reason = "STOP"; }

                // Target
                if (!exit && !pos.IsShort && candle.High >= pos.Target)
                { exit = true; exitPrice = pos.Target; reason = "TARGET"; }
                if (!exit && pos.IsShort && candle.Low <= pos.Target)
                { exit = true; exitPrice = pos.Target; reason = "TARGET"; }

                // Trail (once min-hold passed)
                if (!exit && (etTime - pos.EntryTime).TotalSeconds >= MIN_HOLD_SECONDS)
                {
                    if (!pos.IsShort)
                    {
                        pos.HighWater = Math.Max(pos.HighWater, candle.High);
                        decimal trail = pos.HighWater - pos.AtrAtEntry * ATR_TRAIL_MULT;
                        if (candle.Low <= trail && trail > pos.EntryPrice * 0.99m)
                        { exit = true; exitPrice = trail; reason = "TRAIL"; }
                    }
                    else
                    {
                        pos.LowWater = Math.Min(pos.LowWater, candle.Low);
                        decimal trail = pos.LowWater + pos.AtrAtEntry * ATR_TRAIL_MULT;
                        if (candle.High >= trail && trail < pos.EntryPrice * 1.01m)
                        { exit = true; exitPrice = trail; reason = "TRAIL"; }
                    }
                    positions[symbol] = pos;
                }

                if (exit)
                {
                    decimal gross = pos.IsShort
                        ? pos.Qty * (pos.EntryPrice - exitPrice)
                        : pos.Qty * (exitPrice - pos.EntryPrice);
                    decimal net = gross - COMMISSION;

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
                        Mae = mae
                    });
                    positions.Remove(symbol);
                }
            }

            // ── Try entry (only during valid hours, only when no open position) ──
            if (positions.ContainsKey(symbol)) continue;
            if (positions.Count >= MAX_POSITIONS) continue;
            if (etTime.Hour < 10 || (etTime.Hour == 10 && etTime.Minute < 15)) continue;
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
            double rsi = BtCalc.CalcRSI(win, 14);
            decimal sma20 = BtCalc.CalcSMA(win, 20);
            decimal sma50 = BtCalc.CalcSMA(win, Math.Min(50, win.Count));
            bool volExp = BtCalc.IsVolExpansion(win);
            long avgVol10 = (long)win.TakeLast(10).Average(c => c.Volume);
            bool volOk = candle.Volume >= avgVol10;

            BtPosition newPos = null;

            // ── ORB strategy ─────────────────────────────────────────────
            if (orbRanges.TryGetValue(symbol, out orb) && orb.IsSet && regime != "CHOPPY")
            {
                bool orbLongHold = win.Count >= 2 && win[^1].Close > orb.High && win[^2].Close > orb.High;
                bool orbShortHold = win.Count >= 2 && win[^1].Close < orb.Low && win[^2].Close < orb.Low;

                // ORB_LONG: blocked in SELL-OFF or bearish SPY open
                // Also requires stock to be UP or flat on the day (daily direction filter)
                if (!blockLongs && orbLongHold && rsi > RSI_LONG_MIN && volOk)
                {
                    // Stock daily direction check: must be >= -0.2% vs prev candle day's last close
                    bool stockDayOk = true;
                    if (lastDate.TryGetValue(symbol, out var lastDt) && win.Count > 1)
                    {
                        var prevDayCandles = allCandles.TryGetValue(symbol, out var symAll)
                            ? symAll.Where(c => c.Time.Date < etTime.Date).ToList() : new List<Candle>();
                        if (prevDayCandles.Any())
                        {
                            decimal pdClose = prevDayCandles.Last().Close;
                            if (pdClose > 0) stockDayOk = (close - pdClose) / pdClose >= -0.002m;
                        }
                    }
                    if (stockDayOk)
                    {
                        int qty = CalcQty(close, atr * HARD_STOP_ATR_MULT);
                        if (qty > 0)
                            newPos = new BtPosition { Symbol = symbol, IsShort = false, EntryPrice = close, Qty = qty, EntryTime = etTime, HighWater = close, LowWater = close, Strategy = "ORB_LONG", Regime = regime, HardStop = close - atr * HARD_STOP_ATR_MULT, Target = close + atr * TARGET_ATR_MULT, AtrAtEntry = atr };
                    }
                }
                // ORB_SHORT: preferred in SELL-OFF / bearish open days
                else if (orbShortHold && rsi < RSI_SHORT_MAX && volOk)
                {
                    int qty = CalcQty(close, atr * HARD_STOP_ATR_MULT);
                    if (qty > 0)
                        newPos = new BtPosition { Symbol = symbol, IsShort = true, EntryPrice = close, Qty = qty, EntryTime = etTime, HighWater = close, LowWater = close, Strategy = "ORB_SHORT", Regime = regime, HardStop = close + atr * HARD_STOP_ATR_MULT, Target = close - atr * TARGET_ATR_MULT, AtrAtEntry = atr };
                }
            }

            // ── VWAP Reclaim (blocked in SELL-OFF and bearish open) ───────
            if (newPos == null && vwap > 0 && volExp && !blockLongs)
            {
                bool reclaim = win.Count >= 3 && win[^1].Close > vwap && win[^2].Close > vwap && win[^3].Close <= vwap;
                if (reclaim && rsi > RSI_LONG_MIN)
                {
                    int qty = CalcQty(close, atr * HARD_STOP_ATR_MULT);
                    if (qty > 0)
                        newPos = new BtPosition { Symbol = symbol, IsShort = false, EntryPrice = close, Qty = qty, EntryTime = etTime, HighWater = close, LowWater = close, Strategy = "VWAP_RECLAIM", Regime = regime, HardStop = close - atr * HARD_STOP_ATR_MULT, Target = close + atr * TARGET_ATR_MULT, AtrAtEntry = atr };
                }
            }

            // ── Momentum Breakout (TRENDING only, blocked in SELL-OFF) ────
            if (newPos == null && volExp && sma20 > sma50 && sma50 > 0 && regime == "TRENDING" && !blockLongs)
            {
                decimal recentHigh = win.TakeLast(8).Max(c => c.High);
                // Require volatility compression before the breakout (coil → spring)
                bool compressed = win.Count >= 20 && BtCalc.CalcATR(win.TakeLast(20).ToList(), 10) < BtCalc.CalcATR(win, 14) * 0.8m;
                bool breakout = close > recentHigh && rsi > RSI_LONG_MIN && compressed;
                if (breakout)
                {
                    int qty = CalcQty(close, atr * HARD_STOP_ATR_MULT);
                    if (qty > 0)
                        newPos = new BtPosition { Symbol = symbol, IsShort = false, EntryPrice = close, Qty = qty, EntryTime = etTime, HighWater = close, LowWater = close, Strategy = "MOMENTUM_LONG", Regime = regime, HardStop = close - atr * HARD_STOP_ATR_MULT, Target = close + atr * TARGET_ATR_MULT, AtrAtEntry = atr };
                }
            }

            // ── Mean Reversion (CHOPPY only, never in SELL-OFF) ───────────
            if (newPos == null && regime == "CHOPPY" && sma50 > 0 && close > sma50)
            {
                double prevRsi = win.Count > 1 ? BtCalc.CalcRSI(win.Take(win.Count - 1).ToList(), 14) : 50;
                bool oversold = rsi < 35 && rsi > prevRsi;  // RSI curling up = not a falling knife
                if (oversold)
                {
                    int qty = CalcQty(close, atr * HARD_STOP_ATR_MULT);
                    if (qty > 0)
                        newPos = new BtPosition { Symbol = symbol, IsShort = false, EntryPrice = close, Qty = qty, EntryTime = etTime, HighWater = close, LowWater = close, Strategy = "MEAN_REV_LONG", Regime = regime, HardStop = close - atr * HARD_STOP_ATR_MULT, Target = close + atr * 2.0m, AtrAtEntry = atr };
                }
            }

            if (newPos != null) positions[symbol] = newPos;
        }

        // Close any remaining open positions at end of data
        foreach (var kvp in positions)
        {
            var pos = kvp.Value;
            var lastCandle = allCandles.TryGetValue(kvp.Key, out var lc) ? lc.LastOrDefault() : null;
            if (lastCandle == null) continue;
            decimal exitPrice = lastCandle.Close;
            decimal gross = pos.IsShort ? pos.Qty * (pos.EntryPrice - exitPrice) : pos.Qty * (exitPrice - pos.EntryPrice);
            decimal net = gross - COMMISSION;
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
                Mae = maeEod
            });
        }

        return trades;
    }

    private static int CalcQty(decimal price, decimal stopDistance)
    {
        decimal minStop = Math.Max(0.10m, price * 0.003m);
        if (stopDistance < minStop) stopDistance = minStop;
        decimal risk = INITIAL_CAPITAL * RISK_PCT;
        int qty = (int)(risk / stopDistance);
        int maxBySlot = price > 0 ? (int)(POSITION_SIZE / price) : 0;
        qty = Math.Min(qty, maxBySlot);
        return qty > 0 && qty <= 500 ? qty : 0;
    }

    private static decimal StdDev(IList<decimal> vals)
    {
        if (vals.Count < 2) return 0;
        double avg = (double)vals.Average();
        double variance = vals.Select(v => Math.Pow((double)v - avg, 2)).Average();
        return (decimal)Math.Sqrt(variance);
    }
}