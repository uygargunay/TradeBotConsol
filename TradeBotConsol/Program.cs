using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;

class Program
{
    static void Main(string[] args)
    {
        // 1. Initialize the "Brain"
        var broker = new PositionManager();

        // Load all persistent data from JSON files
        broker.LoadMarketMemory();
        broker.LoadState();

        // 2. Initialize the "Pipe" and link them
        var ibClient = new IbClient(broker);
        broker.RealBroker = ibClient;

        // 3. Emergency Shutdown Hook
        // Ensures data is saved if the console is closed or crashed
        AppDomain.CurrentDomain.ProcessExit += (s, e) => {
            Console.WriteLine("\n[SYSTEM] Emergency shutdown! Saving state and memory...");
            broker.SaveMarketMemory();
            broker.SaveState();
        };

        try
        {
            // 4. Connect to TWS/Gateway
            ibClient.Connect();

            // 5. Start data requests
            var watchList = broker._tradeableStars.Concat(new[] { "QQQ" }).Distinct().ToList();
            ibClient.InitializeUniverse(watchList);

            Console.WriteLine("\n" + new string('=', 60));
            Console.WriteLine("STRATEGY ACTIVE | Press [ENTER] to save and exit safely.");
            Console.WriteLine(new string('=', 60) + "\n");

            int uiCounter = 0;

            // 6. The Main Execution Loop
            while (true)
            {
                // A. Execute Trade Logic (Stops, RSI, Entries)
                foreach (var symbol in watchList)
                {
                    broker.ExecuteTradeLogic(symbol);
                }

                // B. Manage End-of-Day (Liquidation, Learning Save, Email)
                // This method now internally handles the 4:00 PM SaveMarketMemory()
                broker.CheckEndOfDayLiquidation();

                // C. Check Profit Goals / Loss Halts
                broker.CheckDailyGoal();

                // D. Update Dashboard (Every 2 seconds)
                if (uiCounter >= 2)
                {
                    broker.PrintStatusTable();
                    uiCounter = 0;
                }

                uiCounter++;
                Thread.Sleep(1000); // Heartbeat

                // E. Manual Safe Exit
                if (Console.KeyAvailable && Console.ReadKey(true).Key == ConsoleKey.Enter)
                    break;
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[CRITICAL ERROR] {ex.Message}");
        }
        finally
        {
            // 7. Graceful Exit
            Console.WriteLine("[SYSTEM] Shutting down gracefully...");
            broker.SaveMarketMemory();
            broker.SaveState();
            ibClient.Disconnect();
            Console.WriteLine("[EXIT] Goodbye!");
        }
    }
}