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

        broker.LoadMarketMemory();
        broker.ClearMarketData();

        try
        {
            client.Connect();

            // Wait until IB is ready
            while (!client._isReady)
                await Task.Delay(200);

            // Request historical AFTER IB connection
            await broker.RequestAllHistoricalSlow();

            foreach (var sym in broker._watchlist)
            {
                int candleCount = broker._marketData.TryGetValue(sym, out var c) ? c.Count : 0;
                Console.WriteLine($"{sym} loaded {candleCount} candles");
            }

            // Subscribe to live
            foreach (var sym in broker._watchlist)
                client.Subscribe(sym);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Critical Error: {ex.Message}");
            return;
        }

        _ = broker.SendEmail("Bot started", "Bot is running.");

        // Dashboard loop
        _ = Task.Run(async () =>
        {
            while (true)
            {
                broker.PrintDetailedDashboard();
                broker.CheckEndOfDay();
                await Task.Delay(1000);
            }
        });

        // Keep alive
        await Task.Delay(Timeout.Infinite);
    }
}
