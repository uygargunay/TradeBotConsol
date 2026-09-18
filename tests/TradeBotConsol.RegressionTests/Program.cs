using System.Collections.Concurrent;
using System.Reflection;
using System.Text.Json;
using IBApi;

static class RegressionTests
{
    const BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic;
    static T Get<T>(object obj, string name) => (T)obj.GetType().GetField(name, Private)!.GetValue(obj)!;
    static void Set(object obj, string name, object value) => obj.GetType().GetField(name, Private)!.SetValue(obj, value);
    static object? Call(object obj, string name, params object?[] args)
        => obj.GetType().GetMethod(name, Private)!.Invoke(obj, args);
    static void Check(bool result, string description)
    { if (!result) throw new Exception(description); }
    static Dictionary<string, SimPosition> Positions(SimulatedBroker broker) => Get<Dictionary<string, SimPosition>>(broker, "_positions");
    static (SimulatedBroker Bot, FakeBroker Wire) NewBot()
    {
        var bot = new SimulatedBroker();
        var wire = new FakeBroker(bot);
        bot.RealBroker = wire;
        Set(bot, "_reconciled", true);
        return (bot, wire);
    }
    static void Reserve(SimulatedBroker bot, int qty, int id = 1)
    {
        Get<ConcurrentDictionary<string, bool>>(bot, "_pendingEntrySymbols")["TEST"] = true;
        Get<ConcurrentDictionary<string, DateTime>>(bot, "_pendingEntryCreatedUtc")["TEST"] = DateTime.UtcNow;
        Get<ConcurrentDictionary<string, string>>(bot, "_pendingStrategyTag")["TEST"] = "NW_BAND_1H_LONG";
        Get<ConcurrentDictionary<string, decimal>>(bot, "_pendingInitialRisk")["TEST"] = 3m;
        Get<ConcurrentDictionary<string, NwEntryAudit>>(bot, "_pendingNwEntryAudit")["TEST"] = new() { TimeframeMinutes = 60 };
        Set(bot, "_pendingEntryCount", 1);
        bot.RegisterLiveOrder(id, "TEST", TradeSide.Buy, qty);
    }
    static SimPosition Hold(SimulatedBroker bot, int qty = 2, int stopId = 0)
    {
        var pos = new SimPosition { Symbol = "TEST", Quantity = qty, AvgPrice = 100m,
            CurrentPrice = 100m, InitialRiskPerShare = 3m, EntryCommission = 1m,
            StrategyTag = "NW_BAND_1H_LONG", BracketStopId = stopId };
        Positions(bot)["TEST"] = pos;
        if (stopId > 0) bot.RegisterLiveOrder(stopId, "TEST", TradeSide.Sell, qty);
        return pos;
    }
    public static void Main()
    {
        int passed = 0;
        void Test(string name, Action action)
        {
            action();
            passed++;
            Console.WriteLine($"PASS {name}");
        }
        Test("NW-only final boundary rejects non-NW", () =>
        {
            var (bot, wire) = NewBot();
            Set(bot, "STRATEGY_MOMENTUM_ENABLED", true);
            Check(!(bool)Call(bot, "OpenPosition", "TEST", 2, 100m, TradeSide.Buy, false, "MOMENTUM_LONG", null)!, "Non-NW entry accepted");
            Check(wire.Sent.Count == 0, "Non-NW order reached broker");
        });
        Test("partial entries and duplicate cumulative callbacks", () =>
        {
            var (bot, _) = NewBot(); Reserve(bot, 4);
            bot.OnOrderFilled(1, 2, 100m, false);
            Check(Positions(bot)["TEST"].Quantity == 2, "Partial shares absent");
            bot.OnOrderFilled(1, 2, 100m, false);
            bot.OnOrderFilled(1, 4, 101m);
            var pos = Positions(bot)["TEST"];
            Check(pos.Quantity == 4 && pos.AvgPrice == 101m, "Cumulative notional incorrectly accumulated");
            Check(pos.EntryCommission == 1m && Get<int>(bot, "_tradesToday") == 1, "Entry commission/count duplicated");
            Check(pos.NwEntryAudit?.TimeframeMinutes == 60, "Timeframe attribution lost");
            Check(Get<int>(bot, "_pendingEntryCount") == 0, "Pending reservation leaked");
        });
        Test("execDetails alone records shares", () =>
        {
            var (bot, _) = NewBot(); Reserve(bot, 2);
            var client = new IbClient(bot);
            var execution = new Execution { OrderId = 1, CumQty = 2, AvgPrice = 100, Shares = 2, Price = 100 };
            client.execDetails(-1, new Contract { Symbol = "TEST" }, execution);
            client.execDetails(-1, new Contract { Symbol = "TEST" }, execution);
            Check(Positions(bot)["TEST"].Quantity == 2, "Execution missing or counted twice");
            client.orderStatus(1, "Filled", 2, 0, 100, 1, 0, 100, 1, "", 0);
            Check(Positions(bot)["TEST"].Quantity == 2, "Status duplicated execution");
        });
        Test("partial exits aggregate PnL and one commission", () =>
        {
            var (bot, _) = NewBot(); Reserve(bot, 3); bot.OnOrderFilled(1, 3, 100m);
            bot.RegisterLiveOrder(2, "TEST", TradeSide.Sell, 3);
            bot.OnOrderFilled(2, 1, 105m, false);
            bot.OnOrderFilled(2, 1, 105m, false);
            bot.OnOrderFilled(2, 3, 106m);
            var trades = Get<List<TradeRecord>>(bot, "_allTrades");
            Check(Positions(bot).Count == 0 && trades.Count == 1, "Exit split into false trades");
            Check(trades[0].Qty == 3 && trades[0].NetPnL == 16m, "Incorrect exit economics");
            Check(Get<decimal>(bot, "_totalRealizedPnL") == 16m, "Commission double counted");
        });
        Test("wait for cancellation and resize after a stop partially fills", () =>
        {
            var (bot, wire) = NewBot(); Hold(bot, 2, 20);
            bot.SubmitOrder("TEST", 2, 105m, TradeSide.Sell, "NW_TAKE_PROFIT", "MKT");
            Check(wire.Sent.Count == 0 && wire.Cancelled.Contains(20), "Exit sent before cancel confirmation");
            bot.OnOrderFilled(20, 1, 97m, false);
            bot.OnOrderCancelled(20);
            Check(wire.Sent.Count == 1 && wire.Sent[0].Qty == 1, "Exit oversells the remaining position");
        });
        Test("fully filled stop prevents deferred duplicate sell", () =>
        {
            var (bot, wire) = NewBot(); Hold(bot, 2, 20);
            bot.SubmitOrder("TEST", 2, 105m, TradeSide.Sell, "NW_TAKE_PROFIT", "MKT");
            bot.OnOrderFilled(20, 2, 97m);
            bot.OnOrderCancelled(20);
            Check(wire.Sent.Count == 0 && Positions(bot).Count == 0, "Second exit sent after stop closed position");
        });
        Test("NW target works without intraday history", () =>
        {
            var (bot, wire) = NewBot(); Hold(bot);
            Call(bot, "CheckExits", "TEST", 104.99m);
            Check(wire.Sent.Count == 0, "NW exited below its 5% target");
            Call(bot, "CheckExits", "TEST", 105m);
            Check(wire.Sent.Single().Qty == 2, "NW target depends on unrelated minute data");
        });
        Test("NW stop setting affects existing position and native stop", () =>
        {
            var (bot, wire) = NewBot(); Hold(bot, 2, 20);
            Set(bot, "NW_STOP_LOSS_PCT", 0.01m);
            Call(bot, "CheckHardStop", "TEST", 100m);
            Check(wire.Stops.Single().Price == 99m, "Broker stop did not synchronize to 1%");
            Call(bot, "CheckHardStop", "TEST", 98.99m);
            bot.OnOrderCancelled(20);
            Check(wire.Sent.Single().Qty == 2, "Local stop retained old initial risk");
        });
        Test("reconciliation needs both snapshots and handles short quantities", () =>
        {
            var (bot, _) = NewBot(); bot.OnBrokerDisconnected();
            bot.ForceReconcile(); Check(!bot.IsReconciled, "Timeout enabled trading");
            bot.OnPositionReceived("TEST", -2, 100m);
            bot.OnReconciliationComplete(); Check(!bot.IsReconciled, "Open-order snapshot was skipped");
            bot.OnOpenOrderSnapshotComplete();
            Check(bot.IsReconciled && Positions(bot)["TEST"].Quantity == 2 && Positions(bot)["TEST"].IsShort,
                "Signed IBKR quantity corrupted position accounting");
        });
        Test("hourly history aggregates 30m bars without opening-bar overwrite", () =>
        {
            var (bot, _) = NewBot(); DateTime day = new(2026, 9, 15, 9, 30, 0);
            bot.BeginNwHistory("TEST", 60, 77);
            for (int i = 0; i < 4; i++)
                bot.AddNwHistoryBar(77, "TEST", 60, day.AddMinutes(i * 30), 100 + i, 102 + i, 99 + i, 101 + i, 1000);
            Check(bot.GetNadarayaWatsonBarCount("TEST", 60) == 0, "In-flight history exposed before publication");
            bot.CompleteNwHistory("TEST", 60, 77);
            var bars = Get<ConcurrentDictionary<string, List<Candle>>>(bot, "_hourlyCandles")["TEST|60"];
            Check(bars.Count == 2 && bars[0].Open == 100 && bars[0].Close == 102 && bars[1].Close == 104,
                "Hourly OHLC uses native-clock rebucketing or overwrites a sub-bar");
        });
        Test("4h and final short session bucket aggregate consistently", () =>
        {
            var (bot, _) = NewBot(); DateTime day = new(2026, 9, 15, 9, 30, 0);
            bot.BeginNwHistory("TEST", 240, 78);
            for (int i = 0; i < 13; i++)
                bot.AddNwHistoryBar(78, "TEST", 240, day.AddMinutes(i * 30), 100 + i, 102 + i, 99 + i, 101 + i, 1000);
            bot.CompleteNwHistory("TEST", 240, 78);
            var bars = Get<ConcurrentDictionary<string, List<Candle>>>(bot, "_hourlyCandles")["TEST|240"];
            Check(bars.Count == 2 && bars[0].Close == 108 && bars[1].Time == day.AddHours(4) && bars[1].Close == 113,
                "4h history differs from session-anchored live aggregation");
        });
        Test("stale history response cannot contaminate a newer request", () =>
        {
            var (bot, _) = NewBot(); var day = new DateTime(2026, 9, 15, 9, 30, 0);
            bot.BeginNwHistory("TEST", 30, 1); bot.BeginNwHistory("TEST", 30, 2);
            bot.AddNwHistoryBar(1, "TEST", 30, day, 999m, 999m, 999m, 999m, 1);
            bot.CompleteNwHistory("TEST", 30, 1);
            bot.AddNwHistoryBar(2, "TEST", 30, day, 100m, 100m, 100m, 100m, 1);
            bot.CompleteNwHistory("TEST", 30, 2);
            Check(Get<ConcurrentDictionary<string, List<Candle>>>(bot, "_hourlyCandles")["TEST|30"].Single().Close == 100m,
                "Old request overwrote new history");
        });
        Test("legacy timeframe choice is preserved; invalid or empty choices rejected", () =>
        {
            var method = typeof(SimulatedBroker).GetMethod("ReadNwTimeframes", BindingFlags.Static | BindingFlags.NonPublic)!;
            using var legacy = JsonDocument.Parse("{\"NW_TIMEFRAME_MINUTES\":30}");
            var actual = (int[])method.Invoke(null, new object[] { legacy.RootElement, new[] { 30, 60, 240 } })!;
            Check(actual.SequenceEqual(new[] { 30 }), "Legacy 30m config silently gained untested timeframes");
            foreach (string json in new[] { "{\"NW_TIMEFRAMES_MINUTES\":[]}", "{\"NW_TIMEFRAMES_MINUTES\":[99]}" })
            {
                using var doc = JsonDocument.Parse(json); bool rejected = false;
                try { method.Invoke(null, new object[] { doc.RootElement, new[] { 30 } }); }
                catch (TargetInvocationException) { rejected = true; }
                Check(rejected, "Invalid timeframe silently mapped to a valid trading timeframe");
            }
        });
        Test("stale partial entry stays tracked until cancellation", () =>
        {
            var (bot, wire) = NewBot(); Reserve(bot, 4);
            bot.OnOrderFilled(1, 1, 100m, false);
            Get<ConcurrentDictionary<string, DateTime>>(bot, "_pendingEntryCreatedUtc")["TEST"] = DateTime.UtcNow.AddHours(-1);
            Call(bot, "ExpireStalePendingEntries");
            Check(wire.Cancelled.Contains(1), "Partial entry remainder was not cancelled");
            Check(Get<ConcurrentDictionary<int, TrackedOrder>>(bot, "_ordersById").ContainsKey(1), "Tracking removed before acknowledgment");
            bot.OnOrderFilled(1, 2, 100m, false);
            bot.OnOrderCancelled(1);
            Check(Positions(bot)["TEST"].Quantity == 2 && Get<int>(bot, "_pendingEntryCount") == 0,
                "In-flight partial fill or reservation was lost");
        });
        Test("pending entry metadata survives state serialization", () =>
        {
            var (bot, _) = NewBot(); Reserve(bot, 4);
            bot.RegisterBracketChildren("TEST", 20, 0);
            var pending = (List<PendingEntryState>)Call(bot, "CapturePendingEntries")!;
            var copy = JsonSerializer.Deserialize<List<PendingEntryState>>(JsonSerializer.Serialize(pending))!;
            var (restored, _) = NewBot(); Call(restored, "RestorePendingEntries", copy);
            restored.RegisterLiveOrder(1, "TEST", TradeSide.Buy, 4);
            restored.OnOrderFilled(1, 1, 100m, false);
            var pos = Positions(restored)["TEST"];
            Check(pos.StrategyTag == "NW_BAND_1H_LONG" && pos.NwEntryAudit?.TimeframeMinutes == 60 && pos.BracketStopId == 20,
                "Restart lost strategy, timeframe, or protection identity");
        });
        Test("day reset retains working reservations and operational halt", () =>
        {
            var (bot, _) = NewBot(); Reserve(bot, 4);
            Set(bot, "_lastVolumeResetEt", DateTime.Today.AddDays(-2));
            Set(bot, "_haltTrading", true); Set(bot, "_haltReason", "FILL_MISMATCH");
            Call(bot, "CheckDailyReset");
            Check(Get<int>(bot, "_pendingEntryCount") == 1 && Get<bool>(bot, "_haltTrading"), "Daily reset discarded account safety state");
            Set(bot, "_manualResumeOverride", true);
            bot.ReevaluateHalt();
            Check(Get<bool>(bot, "_haltTrading"), "Old manual override erased a newer operational halt");
        });
        Test("late execution after cancellation forces reconciliation", () =>
        {
            var (bot, _) = NewBot(); Reserve(bot, 4); bot.OnOrderCancelled(1);
            bot.OnOrderFilled(1, 1, 100m, false);
            Check(!bot.IsReconciled && Get<bool>(bot, "_haltTrading"), "Late account fill silently ignored");
        });
        Test("cancel notice without cumulative fills cannot release an exit", () =>
        {
            var (bot, wire) = NewBot(); Hold(bot, 2, 20);
            bot.SubmitOrder("TEST", 2, 105m, TradeSide.Sell, "NW_TAKE_PROFIT", "MKT");
            var client = new IbClient(bot); client.error(20, 202, "Cancelled");
            Check(wire.Sent.Count == 0, "Error 202 released exit before filled quantity was known");
            client.orderStatus(20, "Cancelled", 1, 1, 97, 1, 0, 97, 1, "", 0);
            Check(wire.Sent.Single().Qty == 1, "Cancellation status did not account for its partial execution");
        });
        Test("invalid NW envelope parameters are rejected", () =>
        {
            var method = typeof(SimulatedBroker).GetMethod("ValidateNwEnvelopeSettings", BindingFlags.Static | BindingFlags.NonPublic)!;
            foreach (object[] args in new[] { new object[] { 0, 6m, 2.5m }, new object[] { 250, 0m, 2.5m }, new object[] { 250, 6m, -1m } })
            {
                bool rejected = false;
                try { method.Invoke(null, args); } catch (TargetInvocationException) { rejected = true; }
                Check(rejected, "Invalid NW parameter accepted");
            }
        });
        Test("stop amendment includes already filled shares in total quantity", () =>
        {
            var (bot, wire) = NewBot(); Hold(bot, 3, 20);
            bot.OnOrderFilled(20, 1, 97m, false);
            Call(bot, "CheckHardStop", "TEST", 100m);
            Check(Positions(bot)["TEST"].Quantity == 2 && wire.Stops.Single().Qty == 3,
                "Stop total quantity omitted its earlier fill and under-protected the remainder");
        });
        Console.WriteLine($"{passed} regression scenarios passed.");
    }
}

sealed class FakeBroker(SimulatedBroker bot) : IBroker
{
    public bool IsReady { get; set; } = true;
    public bool SupportsBrackets => true;
    public readonly List<(string Symbol, int Qty, decimal Price, TradeSide Side)> Sent = new();
    public readonly List<int> Cancelled = new();
    public readonly List<(int Id, int Qty, decimal Price)> Stops = new();
    public int EnsureNwStop(string symbol, int id, int qty, TradeSide side, decimal price)
    { if (id == 0) id = 2000 + Stops.Count; Stops.Add((id, qty, price)); return id; }
    public void SubmitOrder(string symbol, int qty, decimal price, TradeSide side, double currentRsi = 0, string orderType = "LMT")
    { Sent.Add((symbol, qty, price, side)); bot.RegisterLiveOrder(1000 + Sent.Count, symbol, side, qty); }
    public bool SubmitBracketOrder(string s, int q, decimal e, TradeSide side, decimal stop, decimal limit, decimal target,
        bool useStopMarket = false, bool overridePercentageConstraints = false, bool goodTillCanceledStop = false) => true;
    public void CancelOrder(int id) => Cancelled.Add(id);
    public void RequestPositions() { }
    public void RequestHistoricalData(string symbol) { }
    public void RequestDailyHistoricalData(string symbol) { }
    public void RequestHourlyHistoricalData(string symbol, int timeframeMinutes) { }
    public void CancelMarketData(string symbol) { }
}
