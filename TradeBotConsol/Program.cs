using System;
using System.Threading;
using System.Threading.Tasks;

class Program
{
    static void Main(string[] args)
    {
        Console.WriteLine("--- TRADING BOT STARTING ---");
        var broker = new SimulatedBroker();
        var client = new IbClient(broker);
        broker.RealBroker = client;
        broker.SendEmail("Bot started", "Bot is running.");
        try
        {
            client.Connect();
            foreach (var sym in broker._watchlist)
            {
                client.Subscribe(sym);
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Critical Error: {ex.Message}");
            return;
        }

        // Main Loop
        while (true)
        {
            if (client.IsConnected())
            {
                // UI Update
                {
                    broker.PrintDetailedDashboard(); // Full detail print
                    broker.CheckEndOfDay();
                }
                Thread.Sleep(1000); // 1 second refresh is plenty
            }

            Thread.Sleep(5000); // Check every 5 seconds
        }
    }

}