using System;
using System.Threading;
using System.Threading.Tasks;

class Program
{
    static async Task Main(string[] args)
    {
        Console.WriteLine("--- TRADING BOT STARTING ---");

        var broker = new SimulatedBroker();
        var client = new IbClient(broker);
        broker.RealBroker = client;

        // Load state BEFORE connecting — sets _needsReconciliation = true so
        // nextValidId() fires reqPositions() the moment the socket is live.
        broker.LoadMarketMemory();
        broker.LoadLifetimeEquity();
        broker.LoadAllTrades();
        broker.LoadState();
        broker.LoadEquityCurve();   // restores intraday equity chart after restart

        try
        {
            // ── 1. CONNECT ───────────────────────────────────────────────────────
            client.Connect();
            Console.WriteLine("[STARTUP] Waiting for IBKR connection...");
            while (!client._isReady)
                await Task.Delay(200);
            Console.WriteLine("[STARTUP] IBKR ready.");

            // ── 2. AWAIT RECONCILIATION ──────────────────────────────────────────
            // reqPositions() was sent inside nextValidId(). We wait here for
            // positionEnd() to come back before loading history or subscribing.
            // Without this wait, live ticks can arrive and fire ExecuteStrategy
            // before GILD/JPM/etc. are injected — causing duplicate entries.
            Console.WriteLine("[STARTUP] Waiting for position reconciliation...");
            int reconWaitMs = 0;
            while (!broker.IsReconciled)
            {
                await Task.Delay(200);
                reconWaitMs += 200;
                if (reconWaitMs >= 30_000) // 30s — TWS can take 15-25s after a restart before responding to reqPositions()
                {
                    Console.WriteLine("[STARTUP] WARNING: Reconciliation timed out after 10s — forcing reconcile so trading can start.");
                    // Without this call, _reconciled stays false and ExecuteStrategy()
                    // returns early on every tick for the entire session — bot never trades.
                    broker.ForceReconcile();
                    break;
                }
            }
            Console.WriteLine($"[STARTUP] Reconciliation complete ({reconWaitMs}ms).");

            // ── 3. HISTORICAL DATA ───────────────────────────────────────────────
            await broker.RequestAllHistoricalSlow();
            foreach (var sym in broker._watchlist)
            {
                int candleCount = broker._marketData.TryGetValue(sym, out var c) ? c.Count : 0;
                Console.WriteLine($"{sym} loaded {candleCount} candles");
            }

            // ── 4. LIVE TICK SUBSCRIPTIONS ───────────────────────────────────────
            // NOTE: No explicit Subscribe() loop needed here.
            // IbClient.historicalDataEnd() already calls Subscribe(symbol) for every
            // symbol that completes history, staying within the MAX_MARKET_DATA_LINES
            // budget enforced by RequestAllHistoricalSlow(). Calling Subscribe() again
            // here for the full watchlist bypassed that budget — symbols skipped by the
            // slot limiter have no entry in _subscribedLive, so the dedup guard lets
            // them through, pushing total subscriptions over the 100-line account limit.
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[CRITICAL] Startup failed: {ex.Message}");
            Console.WriteLine(ex.StackTrace);
            return;
        }

        // ── 5. START DASHBOARD + BACKGROUND LOOPS ───────────────────────────────
        broker.Start();

        _ = broker.SendEmail("🤖 Bot Started", "Bot is running and reconciled.");

        _ = Task.Run(async () =>
        {
            while (true)
            {
                try { broker.CheckEndOfDay(); }
                catch (Exception ex) { Console.WriteLine($"[EOD LOOP] Exception: {ex.Message}"); }
                await Task.Delay(1000);
            }
        });

        // ── Connection watchdog — reconnects if IBKR drops the socket ───────────
        // TWS restarts nightly around 11:45 PM ET. Without this, the bot sits
        // silently dead for the rest of the session with no ticks and no trades.
        _ = Task.Run(async () =>
        {
            await Task.Delay(30_000); // let startup settle
            while (true)
            {
                try
                {
                    if (!client.IsConnected())
                    {
                        Console.WriteLine("[WATCHDOG] IBKR connection lost — attempting reconnect...");
                        _ = broker.SendEmail("⚠️ Bot Disconnected", "IBKR socket dropped. Attempting reconnect.");
                        client.Connect();
                        int waitMs = 0;
                        while (!client._isReady && waitMs < 15_000)
                        { await Task.Delay(500); waitMs += 500; }
                        if (client._isReady)
                        {
                            Console.WriteLine("[WATCHDOG] Reconnected successfully.");
                            _ = broker.SendEmail("✅ Bot Reconnected", "IBKR socket restored.");
                            // Re-verify positions — IBKR state may have changed while disconnected
                            broker.RequestRereconcile();
                        }
                        else
                            Console.WriteLine("[WATCHDOG] Reconnect failed — will retry in 30s.");
                    }
                }
                catch (Exception ex) { Console.WriteLine($"[WATCHDOG] {ex.Message}"); }
                await Task.Delay(30_000);
            }
        });

        await Task.Delay(Timeout.Infinite);
    }
}