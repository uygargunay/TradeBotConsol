using IBApi;
using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Threading;
using TradeBotConsol;

public class AutoTrader
{
    private readonly IbClient _ibClient;
    private readonly SimulatedBroker _broker;
    private readonly List<string> _watchList = new();
    private const string WatchlistFile = "watchlist.json";
    private const string LogFile = "trades.log";

    public AutoTrader()
    {
        _ibClient = new IbClient();
        _broker = new SimulatedBroker();

        LoadWatchlist();

        _ibClient.OnPrice += (symbol, price) =>
        {
            Console.WriteLine($"BOT RECEIVED {symbol} {price}");
            var trades = _broker.OnPriceUpdate(new Dictionary<string, decimal>
        {
            { symbol, price }
        });

            foreach (var trade in trades)
            {
                LogTrade(trade);
            }
        };
    }


    private void LoadWatchlist()
    {
        if (!File.Exists(WatchlistFile))
        {
            Console.WriteLine($"Watchlist file '{WatchlistFile}' not found. Creating default.");
            var defaultList = new List<string>
            {
                "AAPL", "MSFT", "NVDA", "AMZN", "META",
                "GOOGL", "TSLA", "AMD", "NFLX", "INTC"
            };
            File.WriteAllText(WatchlistFile, JsonSerializer.Serialize(defaultList, new JsonSerializerOptions { WriteIndented = true }));
            _watchList.AddRange(defaultList);
        }
        else
        {
            try
            {
                string json = File.ReadAllText(WatchlistFile);
                var symbols = JsonSerializer.Deserialize<List<string>>(json);
                if (symbols != null)
                    _watchList.AddRange(symbols);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Failed to read watchlist.json: {ex.Message}");
            }
        }

        Console.WriteLine("Loaded watchlist: " + string.Join(", ", _watchList));
    }

    private void OnPriceBatch(Dictionary<string, decimal> marketPrices)
    {
        var trades = _broker.OnPriceUpdate(marketPrices);

        foreach (var trade in trades)
        {
            LogTrade(trade);
        }
    }

    private void LogTrade(Trade trade)
    {
        try
        {
            string log = $"{DateTime.Now:yyyy-MM-dd HH:mm:ss} | {trade.Symbol} | {trade.Action} | {trade.Price:0.00} | {trade.Quantity}";
            File.AppendAllText(LogFile, log + Environment.NewLine);
            Console.WriteLine("Trade executed: " + log);
        }
        catch (Exception ex)
        {
            Console.WriteLine("Failed to log trade: " + ex.Message);
        }
    }

    public void Start()
    {
        Console.WriteLine("Connecting to IB...");
        _ibClient.Connect();

        // Subscribe to all symbols in the watchlist
        foreach (var symbol in _watchList)
        {
            _ibClient.Subscribe(symbol);
        }

        Console.WriteLine("AutoTrader running. Press Ctrl+C to exit.");

        // Keep the program alive and print summary every hour
        while (true)
        {
            Thread.Sleep(1000);

            if (DateTime.Now.Minute == 0 && DateTime.Now.Second < 2)
            {
                _broker.PrintDailySummary();
            }

            // Reset daily trades at midnight
            if (DateTime.Now.Hour == 0 && DateTime.Now.Minute == 0 && DateTime.Now.Second < 2)
            {
                _broker.ResetDailyTrades();
            }
        }
    }

}
