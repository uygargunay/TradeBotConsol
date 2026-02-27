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
        broker.LoadState();
        try
        {
            client.Connect();
            while (!client._isReady)
                await Task.Delay(200);
            await broker.RequestAllHistoricalSlow();
            foreach (var sym in broker._watchlist)
            {
                int candleCount = broker._marketData.TryGetValue(sym, out var c) ? c.Count : 0;
                Console.WriteLine($"{sym} loaded {candleCount} candles");
            }
   
            foreach (var sym in broker._watchlist)
            {
                client.Subscribe(sym);
                await Task.Delay(50); // 50ms between each — 90 symbols = 4.5 seconds total
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Critical Error: {ex.Message}");
            return;
        }

        broker.Start(); // ← ADD THIS — starts the dashboard timer

        _ = broker.SendEmail("Bot started", "Bot is running.");
        _ = Task.Run(async () =>
        {
            while (true)
            {
                broker.CheckEndOfDay();
                await Task.Delay(1000);
            }
        });
        await Task.Delay(Timeout.Infinite);
    }
}
