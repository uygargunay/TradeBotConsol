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
                    broker.LoadState();

                    // Check if the file is from a previous calendar day
                    if (fileInfo.LastWriteTime.Date < DateTime.Now.Date)
                    {
                        Console.WriteLine("[SYSTEM] New day detected.");
                        broker.ArchiveDailyResults();

                        Console.WriteLine("[SYSTEM] Resetting daily counters for a fresh session...");
                        broker.ResetDailyTrades();
                    }
                    else
                    {
                        Console.WriteLine("[SYSTEM] Resuming existing session from today.");
                    }
                }
                else
                {
                    Console.WriteLine("[WARN] bot_state.json was empty. Starting fresh.");
                    File.Delete(stateFile);
                }
            }

            // 2. CONNECT TO INTERACTIVE BROKERS
            bot.Connect();

            // UPDATED STARTUP DISPLAY
            Console.WriteLine("\n===============================================");
            Console.WriteLine("   TRADING BOT ACTIVE (PST TIME)");
            Console.WriteLine("   Filters: SPY + QQQ (Double-Green Logic)");
            Console.WriteLine("   Circuit Breaker: -$400.00 Daily");
            Console.WriteLine("===============================================");
            Console.WriteLine("   Press 'S' to Save & Exit Safely.");
            Console.WriteLine("   Press 'P' for Live Daily Summary.");
            Console.WriteLine("   Press 'K' for EMERGENCY LIQUIDATION (Sell All)."); // Added hint
            Console.WriteLine("===============================================\n");

            // 3. MAIN MONITORING LOOP
            while (true)
            {
                // --- B. USER INPUT HANDLING ---
                if (Console.KeyAvailable)
                {
                    var key = Console.ReadKey(intercept: true).Key;

                    if (key == ConsoleKey.S)
                    {
                        Console.WriteLine("\n[EXIT] Saving final state...");
                        broker.SaveState();
                        bot.Disconnect();
                        break;
                    }

                    if (key == ConsoleKey.P)
                    {
                        broker.PrintDailySummary();
                    }

                    // --- NEW EMERGENCY MANUAL LIQUIDATION ---
                    if (key == ConsoleKey.K)
                    {
                        Console.WriteLine("\n[!!!] MANUAL LIQUIDATION REQUESTED...");
                        var closedTrades = broker.CheckEndOfDayLiquidation(force: true);
                        Console.WriteLine($"[SYSTEM] Successfully closed {closedTrades.Count} positions.");
                    }
                }

                // --- C. AUTOMATIC EOD CHECK ---
                // This call ensures that at 12:45 PM the broker clears all positions
                broker.CheckEndOfDayLiquidation();

                Thread.Sleep(100);
            }
        }
        catch (Exception ex)
        {
            // 4. EMERGENCY CRASH HANDLING
            string errorDetails = $"CRITICAL BOT CRASH\nTime: {DateTime.Now}\nError: {ex.Message}";
            Console.WriteLine("\n" + errorDetails);

            try { broker.SendEmailSummary("uygargunay@gmail.com"); } catch { }

            Console.WriteLine("\nPress any key to terminate...");
            Console.ReadKey();
        }
    }
}