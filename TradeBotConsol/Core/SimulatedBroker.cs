using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Mail;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

public interface IBroker
{
    void SubmitOrder(string symbol, int qty, decimal price, TradeSide side);
}

public enum TradeSide { Buy, Sell }
public enum MarketRegime { Bullish, Neutral, Bearish }

public class SimPosition
{
    public decimal Quantity { get; set; }
    public decimal AvgPrice { get; set; }
    public decimal TrailingStop { get; set; }
    public decimal CurrentPrice { get; set; }
    public decimal RealizedPnL { get; set; }
    public decimal UnrealizedPnL => Quantity * (CurrentPrice - AvgPrice);
}

public class BotPersistData
{
    public Dictionary<string, SimPosition> Positions { get; set; } = new();
    public int TradesExecutedToday { get; set; }
    public decimal RealizedPnLTotal { get; set; }
    public decimal StartingDayEquity { get; set; }
    public Dictionary<string, int> TradesPerSymbol { get; set; } = new();
    public Dictionary<string, DateTime> BuyTimes { get; set; } = new();
    public Dictionary<string, List<decimal>> PriceHistory { get; set; } = new();
    public Dictionary<string, List<long>> VolumeHistory { get; set; } = new();
    public Dictionary<string, DateTime> LastSellTimes { get; set; } = new();
}

public class PositionManager : SimulatedBroker { }

public class SimulatedBroker : IBroker
{
    // --- RISK & PROFIT TARGETS ---
    private const decimal initialAccountValue = 4000m;
    private const decimal dailyProfitGoalPct = 0.03m; // YOUR 3% TARGET
    private const decimal dailyLossLimitPct = 0.02m;
    private const decimal maxTradeLossPct = 0.015m;
    private const int maxTradesGlobal = 30;
    private const decimal roundTripFee = 0.50m;
    private const decimal slippagePct = 0.0001m;

    public readonly string[] _tradeableStars = { "NVDA", "TSLA", "PLTR", "AMD", "META", "AAPL", "MSFT", "GOOGL", "AMZN", "NFLX", "RKLB", "NBIS", "ZETA" };

    protected readonly Dictionary<string, SimPosition> _positions = new();
    protected readonly Dictionary<string, List<decimal>> _priceHistory = new();
    protected readonly Dictionary<string, List<long>> _volumeHistory = new();
    protected readonly Dictionary<string, int> _tradesToday = new();
    protected readonly Dictionary<string, DateTime> _buyTimes = new();
    protected readonly Dictionary<string, DateTime> _lastSellTimes = new();
    private readonly HashSet<string> _pendingOrders = new();

    private readonly TimeSpan _latestEntryTime = new TimeSpan(15, 30, 0);
    private DateTime _lastHeartbeatLogged = DateTime.UtcNow;
    private DateTime _lastHeartbeatEmail = DateTime.MinValue;
    private const string SaveFilePath = "bot_state.json";
    protected readonly object _lock = new object();

    private decimal _startingDayEquity = initialAccountValue;
    private decimal _totalRealizedPnL = 0m;
    private int _tradesExecutedToday = 0;
    private bool _haltNewTrades = false;
    private bool _goalReached = false;

    public IBroker RealBroker { get; set; }

    public class ClosedTrade
    {
        public string Symbol { get; set; }
        public decimal Profit { get; set; }
        public DateTime ExitTime { get; set; }
    }

    // Inside SimulatedBroker class:
    protected readonly List<ClosedTrade> _tradeHistory = new();

    // --- MARKET SAFETY ---
    public bool IsMarketSafe()
    {
        if (!_priceHistory.ContainsKey("QQQ") || _priceHistory["QQQ"].Count < 20) return false;
        string qqqTrend = GetTrend(_priceHistory["QQQ"]);
        return qqqTrend != "BEAR";
    }

    public decimal GetTotalEquity()
    {
        decimal currentEquity = initialAccountValue + _totalRealizedPnL;
        foreach (var pos in _positions.Values) currentEquity += pos.UnrealizedPnL;
        return currentEquity;
    }

    public void LoadState()
    {
        if (!File.Exists(SaveFilePath)) return;
        try
        {
            var data = JsonSerializer.Deserialize<BotPersistData>(File.ReadAllText(SaveFilePath));
            if (data == null) return;
            _totalRealizedPnL = data.RealizedPnLTotal;
            _tradesExecutedToday = data.TradesExecutedToday;
            _startingDayEquity = data.StartingDayEquity;
            foreach (var kv in data.Positions) _positions[kv.Key] = kv.Value;
            foreach (var kv in data.PriceHistory) _priceHistory[kv.Key] = kv.Value;
            foreach (var kv in data.VolumeHistory) _volumeHistory[kv.Key] = kv.Value;
            foreach (var kv in data.LastSellTimes) _lastSellTimes[kv.Key] = kv.Value;
            foreach (var kv in data.BuyTimes) _buyTimes[kv.Key] = kv.Value;
        }
        catch { }
    }

    public void ExecuteTradeLogic(string symbol)
    {
        if (symbol == "QQQ") return;
        if (_haltNewTrades || _goalReached) return;

        lock (_lock)
        {
            if (!_priceHistory.ContainsKey(symbol) || _priceHistory[symbol].Count < 20) return;

            var history = _priceHistory[symbol];
            decimal currentPrice = history.Last();
            double rsi = CalculateRSI(symbol, 14);
            string trend = GetTrend(history);
            bool hasPosition = _positions.ContainsKey(symbol);

            // --- ENTRY LOGIC ---
            if (!hasPosition)
            {
                DateTime nyNow = GetEasternTime();
                bool marketOpen = nyNow.TimeOfDay >= new TimeSpan(9, 30, 0) && nyNow.TimeOfDay < _latestEntryTime;
                bool inCooldown = _lastSellTimes.TryGetValue(symbol, out var lastSell) && (DateTime.UtcNow - lastSell).TotalMinutes < 20;

                if (marketOpen && !inCooldown && IsMarketSafe() && trend == "BULL" && rsi > 50 && rsi < 65)
                {
                    decimal targetSpend = 1000m;
                    int qty = (int)Math.Floor(targetSpend / currentPrice);
                    if (qty > 0)
                    {
                        SubmitOrder(symbol, qty, currentPrice, TradeSide.Buy);

                        // EMAIL NOTIFICATION: BUY
                        Task.Run(() => SendEmailNotification(
                            $"🟢 BUY: {symbol}",
                            $"Bot bought {qty} shares of {symbol} at {currentPrice:C2}.\nTrend: {trend} | RSI: {rsi:F2}"));
                    }
                }
            }
            // --- EXIT & TRAILING STOP LOGIC ---
            else
            {
                var pos = _positions[symbol];
                pos.CurrentPrice = currentPrice;

                decimal newStop = currentPrice * (1 - maxTradeLossPct);
                if (newStop > pos.TrailingStop)
                {
                    pos.TrailingStop = newStop;
                }

                bool hitStop = currentPrice <= pos.TrailingStop;
                bool hitTarget = rsi >= 70;

                if (hitStop || hitTarget)
                {
                    SubmitOrder(symbol, (int)pos.Quantity, currentPrice, TradeSide.Sell);

                    // EMAIL NOTIFICATION: SELL
                    string reason = hitStop ? "Trailing Stop Hit" : "RSI Target (70+) Hit";
                    Task.Run(() => SendEmailNotification(
                        $"🔴 SELL: {symbol}",
                        $"Sold {pos.Quantity} shares of {symbol} at {currentPrice:C2}.\nReason: {reason}\nEst. PnL: {pos.UnrealizedPnL:C2}"));
                }
            }
        }
    }
    public void CheckDailyGoal()
    {
        lock (_lock)
        {
            if (_goalReached) return;

            decimal currentEquity = GetTotalEquity();
            decimal profitPercent = (currentEquity - _startingDayEquity) / _startingDayEquity;

            if (profitPercent >= dailyProfitGoalPct)
            {
                _goalReached = true;
                _haltNewTrades = true;
                LiquidateAll("Daily 3% Goal Met");
            }
            else if (profitPercent <= (dailyLossLimitPct * -1))
            {
                _haltNewTrades = true;
                LiquidateAll("Daily Loss Limit Hit");
            }
        }
    }

    public void LiquidateAll(string reason)
    {
        Console.WriteLine($"\n[SYSTEM] !!! {reason.ToUpper()} !!! Liquidating all positions.");
        foreach (var sym in _positions.Keys.ToList())
        {
            SubmitOrder(sym, (int)_positions[sym].Quantity, _positions[sym].CurrentPrice, TradeSide.Sell);
        }
    }

    public void SubmitOrder(string symbol, int qty, decimal price, TradeSide side)
    {
        lock (_lock)
        {
            if (_pendingOrders.Contains(symbol)) return;

            // 1. Safety Gate: Don't sell something we just bought 60s ago (prevent flickering)
            if (side == TradeSide.Sell && _buyTimes.TryGetValue(symbol, out var buyTime))
            {
                if ((DateTime.UtcNow - buyTime).TotalSeconds < 60) return;
            }

            _pendingOrders.Add(symbol);
        }

        // 2. Real Execution
        // We send the order to IBKR FIRST. 
        // We let the 'position' callback (Sync) handle adding it to the _positions list.
        if (RealBroker != null)
        {
            Console.WriteLine($"[ORDER] Sending {side} for {symbol} @ approx {price:C2}");
            RealBroker.SubmitOrder(symbol, qty, price, side);
        }

        // 3. Simulation / Tracking Logic
        lock (_lock)
        {
            if (side == TradeSide.Sell && _positions.TryGetValue(symbol, out var pos))
            {
                // Calculate Realized PnL for the Summary
                decimal execPrice = price * (1 - slippagePct);
                decimal pnl = (execPrice - pos.AvgPrice) * pos.Quantity - roundTripFee;

                _totalRealizedPnL += pnl;

                // Add to the new History list for the UI
                _tradeHistory.Add(new ClosedTrade
                {
                    Symbol = symbol,
                    Profit = pnl,
                    ExitTime = DateTime.UtcNow
                });

                _positions.Remove(symbol);
                _lastSellTimes[symbol] = DateTime.UtcNow;
            }
            else if (side == TradeSide.Buy)
            {
                _tradesExecutedToday++;
                _tradesToday[symbol] = _tradesToday.GetValueOrDefault(symbol) + 1;
            }
        }

        // 4. Persistence & Cleanup
        SaveState();

        // Remove from pending after 3 seconds so we can trade this symbol again if needed
        Task.Delay(3000).ContinueWith(_ => {
            lock (_lock) { _pendingOrders.Remove(symbol); }
        });
    }
    public void UpdateHistory(string symbol, decimal price, long volume)
    {
        if (price <= 0) return;
        lock (_lock)
        {
            DateTime nyNow = GetEasternTime();

            // --- NEW AUTO-RESET LOGIC & DAY START EMAIL ---
            if (nyNow.TimeOfDay < new TimeSpan(9, 30, 0) && (_goalReached || _haltNewTrades))
            {
                _goalReached = false;
                _haltNewTrades = false;
                _tradesExecutedToday = 0;
                _totalRealizedPnL = 0;
                _startingDayEquity = GetTotalEquity();
                _tradesToday.Clear();

                Console.WriteLine($"[SYSTEM] {nyNow.ToShortDateString()} - New day detected. Resetting goals.");

                // EMAIL NOTIFICATION: DAY START
                Task.Run(() => SendEmailNotification(
                    "🚀 TradeBot: Daily Session Reset",
                    $"Date: {nyNow.ToShortDateString()}\nStarting Equity: {_startingDayEquity:C2}\nMarket opens at 9:30 AM EST."));

                SaveState();
            }

            // --- HEARTBEAT NOTIFICATION (Every 4 Hours) ---
            if ((DateTime.UtcNow - _lastHeartbeatEmail).TotalHours >= 4)
            {
                _lastHeartbeatEmail = DateTime.UtcNow;
                decimal currentEquity = GetTotalEquity();
                Task.Run(() => SendEmailNotification(
                    "💓 TradeBot Heartbeat",
                    $"The bot is active and processing ticks.\nCurrent Equity: {currentEquity:C2}\nRealized PnL: {_totalRealizedPnL:C2}\nActive Positions: {_positions.Count}"));
            }

            if (!_priceHistory.ContainsKey(symbol))
            {
                _priceHistory[symbol] = new List<decimal>();
                _volumeHistory[symbol] = new List<long>();
            }

            if (_priceHistory[symbol].Count > 0)
            {
                decimal lastPrice = _priceHistory[symbol].Last();
                if (price > lastPrice * 3m || price < lastPrice * 0.3m) return;
            }

            _priceHistory[symbol].Add(price);
            _volumeHistory[symbol].Add(volume);
            if (_priceHistory[symbol].Count > 300) _priceHistory[symbol].RemoveAt(0);

            ExecuteTradeLogic(symbol);
            CheckDailyGoal();
        }
    }
    public void PrintStatusTable()
    {
        // 1. Hide the cursor to stop the 'blinking' flicker
        //Console.CursorVisible = false;

        // 2. Jump to the very top-left
       // Console.SetCursorPosition(0, 0);

        // Build the entire output in a StringBuilder to print at once
        var sb = new System.Text.StringBuilder();

        List<string> symbolsToPrint;
        string marketStatus;
        decimal pnlPct;

        lock (_lock)
        {
            symbolsToPrint = _tradeableStars.Concat(new[] { "QQQ" }).Distinct().ToList();
            marketStatus = IsMarketSafe() ? "SAFE (BULL/FLAT)" : "DANGER (BEAR)";
            decimal currentEquity = GetTotalEquity();
            pnlPct = ((currentEquity - _startingDayEquity) / _startingDayEquity) * 100;
        }

        // --- Header Line ---
        sb.AppendLine($"======= BOT ACTIVE | Market: {marketStatus} | Day PnL: {pnlPct:F2}% / 3.00% =======".PadRight(Console.WindowWidth - 1));
        sb.AppendLine(new string('-', 115).PadRight(Console.WindowWidth - 1));
        sb.AppendLine(string.Format("{0,-9} | {1,-10} | {2,-10} | {3,-8} | {4,-20} | {5,-15} | {6,-6}",
            "SYMBOL", "PRICE", "RSI (14)", "TREND", "POSITION", "UNREAL PnL", "HIST").PadRight(Console.WindowWidth - 1));
        sb.AppendLine(new string('-', 115).PadRight(Console.WindowWidth - 1));

        foreach (var symbol in symbolsToPrint)
        {
            decimal currentPrice = 0;
            int barCount = 0;
            string rsiDisplay = "WARMUP";
            string trendStr = "WAIT";
            string posStr = "---";
            string pnlStr = "---";

            lock (_lock)
            {
                if (_priceHistory.ContainsKey(symbol) && _priceHistory[symbol].Count > 0)
                {
                    var history = _priceHistory[symbol];
                    currentPrice = history.Last();
                    barCount = history.Count;

                    if (barCount >= 15)
                    {
                        double rsiValue = CalculateRSI(symbol, 14);
                        rsiDisplay = rsiValue.ToString("F1");
                    }
                    if (barCount >= 20) trendStr = GetTrend(history);

                    if (_positions.TryGetValue(symbol, out var pos))
                    {
                        posStr = $"{pos.Quantity} @ ${pos.AvgPrice:F2}";
                        pnlStr = $"{pos.UnrealizedPnL:C2}";
                    }
                }
            }

            string row = string.Format("{0,-9} | {1,-10:C2} | {2,-10} | {3,-8} | {4,-20} | {5,-15} | {6,-6}",
                symbol, currentPrice, rsiDisplay, trendStr, posStr, pnlStr, barCount);
            sb.AppendLine(row.PadRight(Console.WindowWidth - 1));
        }

        sb.AppendLine(new string('-', 115).PadRight(Console.WindowWidth - 1));
        sb.AppendLine("\n[RECENT CLOSED TRADES]".PadRight(Console.WindowWidth - 1));

        lock (_lock)
        {
            var recentTrades = _tradeHistory.AsEnumerable().Reverse().Take(5).ToList();
            for (int i = 0; i < 5; i++)
            {
                if (i < recentTrades.Count)
                {
                    var t = recentTrades[i];
                    string tLine = $"{(t.Profit >= 0 ? "✅" : "❌")} {t.Symbol,-8} | {t.Profit,10:C2} | {t.ExitTime:HH:mm:ss}";
                    sb.AppendLine(tLine.PadRight(Console.WindowWidth - 1));
                }
                else sb.AppendLine(" ".PadRight(Console.WindowWidth - 1));
            }
        }

        sb.AppendLine("\nPress [ENTER] to stop and save...".PadRight(Console.WindowWidth - 1));

        // 3. THE MAGIC: Write the entire string in one go
        Console.Write(sb.ToString());
    }
    protected double CalculateRSI(string symbol, int period = 14)
    {
        if (!_priceHistory.ContainsKey(symbol) || _priceHistory[symbol].Count <= period) return 50;
        var prices = _priceHistory[symbol];
        decimal avgGain = 0, avgLoss = 0;
        for (int i = 1; i <= period; i++)
        {
            decimal diff = prices[i] - prices[i - 1];
            if (diff > 0) avgGain += diff; else avgLoss -= diff;
        }
        avgGain /= period; avgLoss /= period;
        for (int i = period + 1; i < prices.Count; i++)
        {
            decimal diff = prices[i] - prices[i - 1];
            avgGain = (avgGain * (period - 1) + (diff > 0 ? diff : 0)) / period;
            avgLoss = (avgLoss * (period - 1) + (diff < 0 ? -diff : 0)) / period;
        }
        return avgLoss == 0 ? 100 : 100 - (100 / (1 + (double)(avgGain / avgLoss)));
    }

    public string GetTrend(List<decimal> prices, int maPeriod = 20)
    {
        if (prices.Count < maPeriod) return "WAIT";
        decimal ma = prices.Skip(prices.Count - maPeriod).Average();
        decimal current = prices.Last();

        // Adjust these thresholds (1.002m) to make the bot more or less sensitive
        if (current > ma * 1.002m) return "BULL";
        if (current < ma * 0.998m) return "BEAR";
        return "FLAT";
    }
    public void SyncExistingPosition(string symbol, decimal qty, decimal avgPrice)
    {
        lock (_lock)
        {
            if (qty != 0 && !_positions.ContainsKey(symbol))
            {
                _positions[symbol] = new SimPosition
                {
                    Quantity = qty,
                    AvgPrice = avgPrice,
                    CurrentPrice = avgPrice, // Defaulting to avg until a tick arrives
                    TrailingStop = avgPrice * 0.985m // 1.5% initial stop
                };
                _buyTimes[symbol] = DateTime.UtcNow;

                Console.WriteLine($"[SYNC] Linked existing TWS position: {symbol} | Qty: {qty} | Avg: {avgPrice:C2}");
            }
        }
    }
    private DateTime GetEasternTime()
    {
        string tzId = RuntimeInformation.IsOSPlatform(OSPlatform.Windows) ? "Eastern Standard Time" : "America/New_York";
        return TimeZoneInfo.ConvertTimeFromUtc(DateTime.UtcNow, TimeZoneInfo.FindSystemTimeZoneById(tzId));
    }

    public void SaveState()
    {
        try
        {
            var data = new BotPersistData
            {
                Positions = _positions,
                TradesExecutedToday = _tradesExecutedToday,
                RealizedPnLTotal = _totalRealizedPnL,
                StartingDayEquity = _startingDayEquity,
                TradesPerSymbol = _tradesToday,
                BuyTimes = _buyTimes,
                PriceHistory = _priceHistory,
                VolumeHistory = _volumeHistory,
                LastSellTimes = _lastSellTimes
            };
            string json = JsonSerializer.Serialize(data, new JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(SaveFilePath, json);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[ERROR] Failed to save state: {ex.Message}");
        }
    }

    public void CheckEndOfDayLiquidation()
    {
        // Close all positions at 3:45 PM EST to avoid overnight gap risk
        if (GetEasternTime().TimeOfDay > new TimeSpan(15, 45, 0))
        {
            if (_positions.Count > 0)
            {
                LiquidateAll("End of Day Liquidation");
            }
        }
    }

    public void SendEmailNotification(string subject, string messageBody)
    {
        try
        {
            ServicePointManager.SecurityProtocol = SecurityProtocolType.Tls12;
            var fromAddress = new MailAddress("uygargunay@gmail.com", "TradeBot Live");
            var toAddress = new MailAddress("uygargunay@gmail.com");
            const string fromPassword = "sznd kafk nhec skqh";

            var smtp = new SmtpClient
            {
                Host = "smtp.gmail.com",
                Port = 587,
                EnableSsl = true,
                DeliveryMethod = SmtpDeliveryMethod.Network,
                UseDefaultCredentials = false,
                Credentials = new NetworkCredential(fromAddress.Address, fromPassword),
                Timeout = 10000
            };

            using (var message = new MailMessage(fromAddress, toAddress) { Subject = subject, Body = messageBody })
            {
                smtp.Send(message);
            }
            Console.WriteLine($"[EMAIL] Sent: {subject}");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[EMAIL ERROR] {ex.Message}");
        }
    }
}