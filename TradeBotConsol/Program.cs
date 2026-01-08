using IBApi;
using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Threading;
using TradeBotConsol;

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

                        // Save yesterday's performance to trade_history_log.txt
                        broker.ArchiveDailyResults();

                        // Reset PnL and Trade Counters for today
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

            Console.WriteLine("\n===============================================");
            Console.WriteLine("   TRADING BOT ACTIVE (PST TIME)");
            Console.WriteLine("   Filters: SPY + QQQ (Double-Green Logic)");
            Console.WriteLine("   Circuit Breaker: -$300.00 Daily");
            Console.WriteLine("===============================================");
            Console.WriteLine("   Press 'S' to Save & Exit Safely.");
            Console.WriteLine("   Press 'P' for Live Daily Summary.");
            Console.WriteLine("===============================================\n");

            // 3. MAIN MONITORING LOOP
            while (true)
            {
                // --- A. DATA HEALTH CHECK ---
                // Alerts  if SPY or QQQ data stops updating for > 60 seconds
                if (!broker.CheckDataHealth())
                {
                   
                }

                // --- B. USER INPUT HANDLING ---
                if (Console.KeyAvailable)
                {
                    var key = Console.ReadKey(intercept: true).Key;

                    // Manual Save and Exit
                    if (key == ConsoleKey.S)
                    {
                        Console.WriteLine("\n[EXIT] Saving final state...");
                        broker.SaveState();
                        bot.Disconnect();
                        break;
                    }

                    // Status Report
                    if (key == ConsoleKey.P)
                    {
                        broker.PrintDailySummary();
                    }
                }

                // --- C. AUTOMATIC EOD CHECK ---
                // Checks if it is 12:55 PM (5 mins before close) to liquidate positions
                broker.CheckEndOfDayLiquidation();

                // Keep CPU usage low
                Thread.Sleep(100);
            }
        }
        catch (Exception ex)
        {
            // 4. EMERGENCY CRASH HANDLING
            string errorDetails = $"CRITICAL BOT CRASH\nTime: {DateTime.Now}\nError: {ex.Message}\nStack: {ex.StackTrace}";
            Console.WriteLine("\n" + errorDetails);

            // Attempt to send crash report via email
            try
            {
                broker.SendEmailSummary("uygargunay@gmail.com");
            }
            catch { /* Avoid nested crash */ }

            Console.WriteLine("\nPress any key to terminate...");
            Console.ReadKey();
        }
    }
}