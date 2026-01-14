using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

class Program
{
    static void Main(string[] args)
    {
        // 1. Initialize the shared "Brain" (PositionManager)
        var broker = new PositionManager();
        broker.LoadState();

        // 2. Initialize the "Pipe" (IbClient) and link them
        var ibClient = new IbClient(broker);
        broker.RealBroker = ibClient;

        // 3. Connect to TWS
        ibClient.Connect();

        // 4. Start data requests (Watchlist + QQQ for Market Regime)
        var watchList = broker._tradeableStars.Concat(new[] { "QQQ" }).Distinct().ToList();
        ibClient.InitializeUniverse(watchList);

        Console.WriteLine("\n" + new string('=', 60));
        Console.WriteLine("STRATEGY STARTING... Press [ENTER] to save and exit safely.");
        Console.WriteLine(new string('=', 60) + "\n");

        int heartBeatCounter = 0;

        // 5. The Execution Loop
        // 5. The Execution Loop
        int uiCounter = 0;
        while (true)
        {
            // 1. Run Strategy Logic (FAST - Every 1 second)
            // This ensures stops and RSI entries are never missed
            foreach (var symbol in watchList)
            {
                broker.ExecuteTradeLogic(symbol);
            }
            broker.CheckDailyGoal();

            // 2. Update the Dashboard (SLOW - Every 2 or 3 seconds)
            // Frequent updates cause the "flicker" effect in Windows Console
            if (uiCounter >= 2)
            {
                broker.PrintStatusTable();
                uiCounter = 0;
            }

            uiCounter++;
            Thread.Sleep(1000);

            if (Console.KeyAvailable && Console.ReadKey(true).Key == ConsoleKey.Enter)
                break;
        }

        broker.SaveState(); // Final save before closing
        ibClient.Disconnect();
        Console.WriteLine("[EXIT] Disconnected. Goodbye!");
    }
}