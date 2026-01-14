class Program
{
    static void Main(string[] args)
    {
        // 1. Create the single source of truth: The Broker
        var broker = new PositionManager(); // Using the PositionManager wrapper

        try
        {
            Console.Title = "TradeBot Live - Smart Sizing Engine";

            // 2. STARTUP DATA MANAGEMENT
            string stateFile = "bot_state.json";
            if (File.Exists(stateFile) && new FileInfo(stateFile).Length > 0)
            {
                broker.LoadState();

                // Check if we are resuming from a previous calendar day
                if (File.GetLastWriteTime(stateFile).Date < DateTime.Now.Date)
                {
                    Console.WriteLine("[SYSTEM] New day detected. Archiving old results and resetting...");
                    broker.ArchiveDailyResults();
                    broker.ResetDailyTrades();
                }
                else
                {
                    Console.WriteLine("[SYSTEM] Today's session found. Resuming existing state...");
                }
            }

            // 3. DISPLAY STARTUP HEADER & SEND TEST EMAIL
            broker.PrintStartupConfiguration();

            // 4. START THE ENGINE (Non-Blocking)
            // Wrapping this in Task.Run allows the bot to trade in the background 
            // while this Main thread stays open to listen for your [ENTER] key.
            Task.Run(() => broker.RunWorker());

            Console.WriteLine("\n[STATUS] Bot Engine is active and monitoring QQQ/NVDA/TSLA/PLTR/AMD.");
            Console.WriteLine("[STATUS] Press [ENTER] at any time to shut down safely.");

            // --- KEEP ALIVE ---
            Console.ReadLine();

            // 5. GRACEFUL SHUTDOWN
            Console.WriteLine("[SYSTEM] Shutting down... Saving final state.");
            broker.SaveState();
            Console.WriteLine("[SYSTEM] Shutdown complete. Press any key to close window.");
            Console.ReadKey();
        }
        catch (Exception ex)
        {
            Console.ForegroundColor = ConsoleColor.Red;
            Console.WriteLine($"\n[CRITICAL CRASH] {ex.Message}");
            Console.WriteLine(ex.StackTrace);
            Console.ResetColor();

            try { broker.SaveState(); } catch { }

            Console.WriteLine("\nPress any key to exit...");
            Console.ReadKey();
        }
    }
}