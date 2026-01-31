using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

class Program
{
    static void Main(string[] args)
    {
        // 1. Initialize the "Brain" (PositionManager / SimulatedBroker)
        var broker = new SimulatedBroker();

        // Load all persistent data from JSON files to prevent starting from zero
        broker.LoadMarketMemory();
        broker.LoadState();

        // 2. Initialize the "Pipe" (IbClient) and link them
        var ibClient = new IbClient(broker);
        broker.RealBroker = ibClient;

        // 3. Emergency Shutdown Hook
        // Ensures your trade history and memory are saved even if the window is closed
        AppDomain.CurrentDomain.ProcessExit += (s, e) => {
            Console.WriteLine("\n[SYSTEM] Emergency shutdown! Saving state and memory...");
            broker.SaveMarketMemory();
            broker.SaveState();
        };

        try
        {
            // 4. Connect to TWS/Gateway
            Console.WriteLine("[SYSTEM] Connecting to IBKR TWS...");
            ibClient.Connect();

            // 5. Synchronization Wait
            // We wait here to ensure we have the next valid Order ID before starting
            int timeout = 0;
            while (!ibClient._isReady && timeout < 50)
            {
                Console.WriteLine("[SYSTEM] Waiting for IBKR synchronization...");
                Thread.Sleep(200);
                timeout++;
            }

            if (!ibClient._isReady)
            {
                Console.WriteLine("[CRITICAL] Could not sync with IBKR. Check if TWS is open.");
                return;
            }

            // 6. Start data requests
            var watchList = broker._tradeableStars.Concat(new[] { "QQQ" }).Distinct().ToList();
            ibClient.InitializeUniverse(watchList);

            Console.WriteLine("\n" + new string('=', 60));
            Console.WriteLine("STRATEGY ACTIVE | Dashboard Running | Press [ENTER] to Exit.");
            Console.WriteLine(new string('=', 60) + "\n");

            // 7. Decoupled Dashboard Task
            // This runs on a separate thread so UI updates don't slow down trade execution
            CancellationTokenSource cts = new CancellationTokenSource();
            Task.Run(async () => {
                while (!cts.Token.IsCancellationRequested)
                {
                    try
                    {
                        broker.PrintStatusTable();
                    }
                    catch { /* Suppress UI-only errors */ }
                    await Task.Delay(1000); // UI Refresh Rate
                }
            });

            // 8. The Main Execution Loop (Lifecycle Management)
            while (true)
            {
                // A. Manage End-of-Day (Liquidation, Learning Save, Email)
                broker.CheckEndOfDayLiquidation();

                // B. Check Profit Goals / Loss Halts
                broker.CheckDailyGoal();

                // NOTE: Trading logic (Entries/Stops) is NOT called here.
                // It is now event-driven inside IbClient.tickPrice to ensure zero-latency.

                // C. Manual Safe Exit
                if (Console.KeyAvailable && Console.ReadKey(true).Key == ConsoleKey.Enter)
                {
                    cts.Cancel();
                    break;
                }

                Thread.Sleep(100); // High-resolution heartbeat
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[CRITICAL ERROR] {ex.Message}\n{ex.StackTrace}");
        }
        finally
        {
            // 9. Graceful Exit
            Console.WriteLine("[SYSTEM] Shutting down gracefully...");
            broker.SaveMarketMemory();
            broker.SaveState();
            ibClient.Disconnect();
            Console.WriteLine("[EXIT] Connection closed. Data saved. Goodbye!");
        }
    }
}