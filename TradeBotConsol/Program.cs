using IBApi;
using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Threading;

class Program
{
    static void Main(string[] args)
    {
        // Initialize the IB Client and get the Broker/PositionManager instance
        IbClient bot = new IbClient();
        var broker = bot.GetBroker();

        try
        {
            // 1. STARTUP DATA MANAGEMENT
            string stateFile = "bot_state.json";
            if (File.Exists(stateFile))
            {
                FileInfo fileInfo = new FileInfo(stateFile);
                if (fileInfo.Length > 0)
                {
                    // Load the previous memory
                    broker.LoadState();

                    // Check if the file is from a previous calendar day
                    // We use .Date to compare only the Day/Month/Year
                    if (fileInfo.LastWriteTime.Date < DateTime.Now.Date)
                    {
                        Console.WriteLine("[SYSTEM] New day detected. Archiving old results...");
                        broker.ArchiveDailyResults();

                        Console.WriteLine("[SYSTEM] Resetting daily counters for a fresh session...");
                        broker.ResetDailyTrades();
                    }
                    else
                    {
                        Console.WriteLine("[SYSTEM] Today's session found. Resuming...");
                    }
                }
                else
                {
                    Console.WriteLine("[WARN] bot_state.json was empty. Starting fresh.");
                    File.Delete(stateFile);
                }
            }

            // 2. CONNECT TO INTERACTIVE BROKERS
            // Note: Ensure your TWS or IB Gateway is open and API is enabled
            bot.Connect();

            // 3. DISPLAY THE DASHBOARD
            // This shows your Goals, Limits, and Active Positions clearly
            broker.PrintStartupConfiguration();

            Console.WriteLine("\n[CONTROLS]");
            Console.WriteLine(" [S] Save & Exit | [P] PnL Summary | [K] EMERGENCY KILL (Sell All)");
            Console.WriteLine("====================================================\n");

            // 4. MAIN MONITORING LOOP
            while (true)
            {
                // --- A. USER INPUT HANDLING ---
                if (Console.KeyAvailable)
                {
                    var key = Console.ReadKey(intercept: true).Key;

                    // [S] SAFE EXIT
                    if (key == ConsoleKey.S)
                    {
                        Console.WriteLine("\n[EXIT] Saving final state and disconnecting...");
                        broker.SaveState();
                        bot.Disconnect();
                        break;
                    }

                    // [P] PRINT SUMMARY
                    if (key == ConsoleKey.P)
                    {
                        broker.PrintDailySummary();
                    }

                    // [K] EMERGENCY LIQUIDATION
                    if (key == ConsoleKey.K)
                    {
                        Console.ForegroundColor = ConsoleColor.Red;
                        Console.WriteLine("\n[!!!] EMERGENCY MANUAL LIQUIDATION INITIATED...");
                        Console.ResetColor();

                        var closedTrades = broker.CheckEndOfDayLiquidation(force: true);
                        Console.WriteLine($"[SYSTEM] Closed {closedTrades.Count} positions. Trading Halted.");
                    }
                }

                // --- B. AUTOMATIC END-OF-DAY CHECK ---
                // This method internally checks if the time is > 3:45 PM ET
                broker.CheckEndOfDayLiquidation();

                // --- C. PREVENT CPU OVERLOAD ---
                // Small sleep prevents the while(true) loop from using 100% CPU
                Thread.Sleep(100);
            }
        }
        catch (Exception ex)
        {
            // 5. EMERGENCY CRASH HANDLING
            string errorDetails = $"CRITICAL BOT CRASH\nTime: {DateTime.Now}\nError: {ex.Message}";
            Console.ForegroundColor = ConsoleColor.Red;
            Console.WriteLine("\n" + errorDetails);
            Console.ResetColor();

            // Try to alert you via email
            try
            {
                broker.SaveState(); // Save whatever we can
                broker.SendEmailSummary("uygargunay@gmail.com");
            }
            catch { /* Fallback if email or save fails */ }

            Console.WriteLine("\nPress any key to terminate...");
            Console.ReadKey();
        }
    }
}