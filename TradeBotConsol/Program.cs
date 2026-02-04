using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Runtime.InteropServices;

class Program
{
    static void Main(string[] args)
    {
        var broker = new SimulatedBroker();
        broker.LoadMarketMemory();
        broker.LoadState();

        var ibClient = new IbClient(broker);
        broker.RealBroker = ibClient;

        // Watchlist (NO QQQ duplication)
        var watchList = broker._tradeableStars.Distinct().ToList();
        broker.PreInitializeSymbols(watchList);

        AppDomain.CurrentDomain.ProcessExit += (s, e) =>
        {
            Console.WriteLine("\n[SYSTEM] Saving state...");
            broker.SaveState();
        };

        try
        {
            Console.WriteLine("[SYSTEM] Connecting to IBKR...");
            ibClient.Connect();

            // Wait for real IB handshake
            int timeout = 0;
            while (!ibClient._isReady && timeout < 50)
            {
                Console.WriteLine("[SYSTEM] Waiting for IBKR sync...");
                Thread.Sleep(200);
                timeout++;
            }

            if (!ibClient._isReady)
            {
                Console.WriteLine("[CRITICAL] Could not sync with IBKR. Is TWS / Gateway running?");
                return;
            }

            Console.WriteLine("[SYSTEM] IBKR connected.");

            // Request market data
            ibClient.InitializeUniverse(watchList);

            // Allow data to seed before dashboard
            Thread.Sleep(5000);
            Console.Clear();

            CancellationTokenSource cts = new CancellationTokenSource();

            // Dashboard thread
            Task.Run(async () =>
            {
                while (!cts.Token.IsCancellationRequested)
                {
                    try
                    {
                        broker.PrintStatusTable();
                    }
                    catch { }

                    await Task.Delay(1000);
                }
            });

            // Main control loop
            while (true)
            {
                if (!ibClient.IsConnected())
                    break;

                broker.CheckDailyGoal();
                broker.CheckEndOfDayLiquidation();

                if (Console.KeyAvailable && Console.ReadKey(true).Key == ConsoleKey.Enter)
                    break;

                Thread.Sleep(500);
            }

        }
        catch (Exception ex)
        {
            Console.WriteLine($"[CRITICAL] {ex.Message}");
        }
        finally
        {
            Console.WriteLine("[SYSTEM] Disconnecting...");
            ibClient.Disconnect();
        }
    }

    private static DateTime GetEasternTime()
    {
        string tzId = RuntimeInformation.IsOSPlatform(OSPlatform.Windows)
            ? "Eastern Standard Time"
            : "America/New_York";

        return TimeZoneInfo.ConvertTimeFromUtc(
            DateTime.UtcNow,
            TimeZoneInfo.FindSystemTimeZoneById(tzId)
        );
    }
}
