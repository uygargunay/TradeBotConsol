using System;
using System.Threading;
using System.Threading.Tasks;

class Program
{
    static void Main(string[] args)
    {
        Console.WriteLine("--- TRADING BOT STARTING ---");

        var broker = new SimulatedBroker();
        broker.LoadMarketMemory();

        // DEBUG CHECK
        foreach (var sym in broker._watchlist)
        {
            int candleCount = broker._marketData.TryGetValue(sym, out var c) ? c.Count : 0;
            Console.WriteLine($"{sym} loaded {candleCount} candles");
        }

        var client = new IbClient(broker);
        broker.RealBroker = client;

        // Fire-and-forget email
        _ = broker.SendEmail("Bot started", "Bot is running.");

        try
        {
            client.Connect();

            // Subscribe to all symbols
            foreach (var sym in broker._watchlist)
                client.Subscribe(sym);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Critical Error: {ex.Message}");
            return;
        }

        // --- MAIN LOOP ---
        Task.Run(() =>
        {
            while (true)
            {
                broker.PrintDetailedDashboard(); // Full table print
                broker.CheckEndOfDay();
                Thread.Sleep(1000); // refresh 1 sec
            }
        });

        // Keep main thread alive for IB ticks
        while (true)
        {
            Thread.Sleep(5000);
        }
    }
}
