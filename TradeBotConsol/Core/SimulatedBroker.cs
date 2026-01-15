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
    public bool IsBreakEvenProtected { get; set; } // NEW: Tracks if stop is at entry
}

public class BotPersistData
{
    public Dictionary<string, SimPosition> Positions { get; set; } = new();
    public int TradesExecutedToday { get; set; }
    public int DailyLossCount { get; set; } // ADDED THIS
    public decimal RealizedPnLTotal { get; set; }
    public decimal StartingDayEquity { get; set; }
    public Dictionary<string, int> TradesPerSymbol { get; set; } = new();
    public Dictionary<string, DateTime> BuyTimes { get; set; } = new();
    public Dictionary<string, List<decimal>> PriceHistory { get; set; } = new();
    public Dictionary<string, List<long>> VolumeHistory { get; set; } = new();
    public Dictionary<string, DateTime> LastSellTimes { get; set; } = new();
    public Dictionary<string, bool> LastTradeWasLoss { get; set; } = new();
}

public class PositionManager : SimulatedBroker { }

public class SimulatedBroker : IBroker
{
    private const decimal initialAccountValue = 4000m;
    private const decimal dailyProfitGoalPct = 0.03m;
    private const decimal dailyLossLimitPct = 0.02m;
    private const decimal maxTradeLossPct = 0.015m;
    private const int maxTradesGlobal = 30;
    private const decimal roundTripFee = 2.00m; // Updated to $1 buy + $1 sell
    private const decimal slippagePct = 0.0001m;
    private const int MaxActivePositions = 4; // NEW: Slot limit

    // Inside SimulatedBroker Class - Added Variable
    protected int _dailyLossCount = 0;

    public readonly string[] _tradeableStars = { "NVDA", "TSLA", "PLTR", "AMD", "META", "AAPL", "MSFT", "GOOGL", "AMZN", "NFLX", "RKLB", "NBIS", "ZETA" };

    protected readonly Dictionary<string, SimPosition> _positions = new();
    protected readonly Dictionary<string, List<decimal>> _priceHistory = new();
    protected readonly Dictionary<string, List<long>> _volumeHistory = new();
    protected readonly Dictionary<string, int> _tradesToday = new();
    protected readonly Dictionary<string, DateTime> _buyTimes = new();
    protected readonly Dictionary<string, DateTime> _lastSellTimes = new();

    // VWAP tracking
    protected readonly Dictionary<string, decimal> _cumVolume = new();
    protected readonly Dictionary<string, decimal> _cumVwapProd = new();

    private readonly HashSet<string> _pendingOrders = new();
    private readonly TimeSpan _latestEntryTime = new TimeSpan(15, 30, 0);
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

    protected readonly List<ClosedTrade> _tradeHistory = new();
   


    private DateTime _lastHeartbeatLogged = DateTime.UtcNow;


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
            _dailyLossCount = data.DailyLossCount; // ADD THIS LINE

            foreach (var kv in data.Positions) _positions[kv.Key] = kv.Value;
            foreach (var kv in data.PriceHistory) _priceHistory[kv.Key] = kv.Value;
            foreach (var kv in data.VolumeHistory) _volumeHistory[kv.Key] = kv.Value;
            foreach (var kv in data.LastSellTimes) _lastSellTimes[kv.Key] = kv.Value;
            foreach (var kv in data.BuyTimes) _buyTimes[kv.Key] = kv.Value;
        }
        catch { }
    }
    // Ensure these are at the top of your SimulatedBroker class
    private Dictionary<string, double> _peakRsi = new();
    private Dictionary<string, bool> _lastTradeWasLoss = new(); // NEW: Track loss state

    public void ExecuteTradeLogic(string symbol)
    {
        if (symbol == "QQQ") return;
        if (_haltNewTrades || _goalReached) return;

        lock (_lock)
        {
            if (!_priceHistory.ContainsKey(symbol) || _priceHistory[symbol].Count < 20) return;

            var prices = _priceHistory[symbol];
            var volumes = _volumeHistory[symbol];
            decimal currentPrice = prices.Last();
            long currentVolume = volumes.Last();
            double rsi = CalculateRSI(symbol, 14);
            string trend = GetTrend(prices);

            // --- 1. SYNTHETIC SPREAD CHECK ---
            decimal lastPrice = prices[prices.Count - 2];
            decimal priceChangePct = Math.Abs((currentPrice - lastPrice) / lastPrice) * 100;
            bool spreadIsTight = priceChangePct <= 0.12m;

            // --- 2. VWAP & VOLUME ANALYSIS ---
            decimal vwap = _cumVolume.ContainsKey(symbol) && _cumVolume[symbol] > 0
                ? _cumVwapProd[symbol] / _cumVolume[symbol] : currentPrice;
            decimal avgVolume = volumes.Count >= 10 ? (decimal)volumes.Skip(volumes.Count - 10).Average() : 0;
            bool volumeSurge = avgVolume > 0 && currentVolume > (avgVolume * 1.2m);
            bool volumeClimax = avgVolume > 0 && currentVolume > (avgVolume * 5.0m);

            bool hasPosition = _positions.ContainsKey(symbol);

            // --- ENTRY LOGIC ---
            if (!hasPosition && _positions.Count < MaxActivePositions)
            {
                DateTime nyNow = GetEasternTime();
                bool marketOpenTime = nyNow.TimeOfDay >= new TimeSpan(10, 0, 0) && nyNow.TimeOfDay < _latestEntryTime;
                bool underLossLimit = _dailyLossCount < 4;

                // --- COOLDOWN LOGIC ---
                _lastSellTimes.TryGetValue(symbol, out var lastSell);
                double minutesSinceLastTrade = (DateTime.UtcNow - lastSell).TotalMinutes;

                _lastTradeWasLoss.TryGetValue(symbol, out bool wasLoss);
                // Standard cooldown is 15 mins, but if it was a loss, we wait 30 mins (Revenge Trade Shield)
                int requiredCooldown = wasLoss ? 30 : 15;
                bool inCooldown = minutesSinceLastTrade < requiredCooldown;

                decimal ma20 = (decimal)prices.Skip(prices.Count - 20).Average();
                bool isOverextended = currentPrice > (ma20 * 1.012m);

                if (marketOpenTime && underLossLimit && !inCooldown && IsMarketSafe())
                {
                    if (trend == "BULL" && rsi > 45 && rsi < 65 && currentPrice > vwap && volumeSurge &&
                        !isOverextended && !volumeClimax && spreadIsTight)
                    {
                        decimal targetSpend = 1000m;
                        int qty = (int)Math.Floor((targetSpend - 1.00m) / currentPrice);
                        if (qty > 0)
                        {
                            _peakRsi[symbol] = rsi;
                            SubmitOrder(symbol, qty, currentPrice, TradeSide.Buy);
                            Task.Run(() => SendEmailNotification($"🟢 SMART BUY: {symbol}",
                                $"Bought {qty} @ {currentPrice:C2}\nTrend: {trend}\nRSI: {rsi:F1}"));
                        }
                    }
                }
            }
            // --- EXIT LOGIC ---
            else if (hasPosition)
            {
                var pos = _positions[symbol];
                pos.CurrentPrice = currentPrice;
                decimal netPnL = pos.UnrealizedPnL - roundTripFee;
                _buyTimes.TryGetValue(symbol, out var buyTime);
                bool timeShieldActive = (DateTime.UtcNow - buyTime).TotalMinutes < 15;

                if (!_peakRsi.ContainsKey(symbol) || rsi > _peakRsi[symbol]) _peakRsi[symbol] = rsi;

                // VOLATILITY STOP: Tighten stop to 0.4% once up $6.00
                if (netPnL > 6.00m)
                {
                    decimal tightStop = currentPrice * 0.996m;
                    if (tightStop > pos.TrailingStop) pos.TrailingStop = tightStop;
                    pos.IsBreakEvenProtected = true;
                }

                double peak = _peakRsi[symbol];
                bool rsiHooked = peak > 75 && rsi < (peak - 3.0);
                bool climaxExit = volumeClimax && netPnL > 2.00m;
                bool hitStop = currentPrice <= pos.TrailingStop;
                bool rsiWinExit = (rsi >= 80 && netPnL > 3.00m) || rsiHooked || (rsi >= 72 && trend == "FLAT" && netPnL > 1.50m);

                if (hitStop || rsiWinExit || climaxExit)
                {
                    if (hitStop && netPnL < 0 && timeShieldActive) return;

                    // Track if this exit is a loss for the next cooldown
                    _lastTradeWasLoss[symbol] = netPnL < 0;

                    SubmitOrder(symbol, (int)pos.Quantity, currentPrice, TradeSide.Sell);
                    _peakRsi.Remove(symbol);

                    string reason = hitStop ? "STOP" : (climaxExit ? "CLIMAX" : "RSI_HOOK");
                    Task.Run(() => SendEmailNotification($"🔴 SMART SELL ({reason}): {symbol}",
                        $"Sold @ {currentPrice:C2}\nNet PnL: {netPnL:C2}\nCooldown Set: {(netPnL < 0 ? "30m" : "15m")}"));
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
            _pendingOrders.Add(symbol);
        }

        if (RealBroker != null) RealBroker.SubmitOrder(symbol, qty, price, side);

        lock (_lock)
        {
            if (side == TradeSide.Sell && _positions.TryGetValue(symbol, out var pos))
            {
                decimal execPrice = price * (1 - slippagePct);
                decimal pnl = (execPrice - pos.AvgPrice) * pos.Quantity - roundTripFee;

                // Update Revenge Trade Shield
                bool isLoss = pnl < 0;
                _lastTradeWasLoss[symbol] = isLoss;

                if (isLoss)
                {
                    _dailyLossCount++;
                    if (_dailyLossCount >= 4)
                    {
                        _haltNewTrades = true;
                        Task.Run(() => SendEmailNotification("⚠️ LOSS LIMIT REACHED", "4 losses hit. Stopping for the day."));
                    }
                }

                LogTradeToCSV(symbol, "SELL", execPrice, pnl, CalculateRSI(symbol, 14), true);
                _totalRealizedPnL += pnl;
                _tradeHistory.Add(new ClosedTrade { Symbol = symbol, Profit = pnl, ExitTime = DateTime.UtcNow });
                _positions.Remove(symbol);
                _peakRsi.Remove(symbol);
                _lastSellTimes[symbol] = DateTime.UtcNow;
            }
            else if (side == TradeSide.Buy)
            {
                _tradesExecutedToday++;
                _positions[symbol] = new SimPosition
                {
                    Quantity = qty,
                    AvgPrice = price,
                    CurrentPrice = price,
                    TrailingStop = price * (1 - maxTradeLossPct)
                };
                _buyTimes[symbol] = DateTime.UtcNow;

                // Initialize RSI Peak at entry
                _peakRsi[symbol] = CalculateRSI(symbol, 14);

                LogTradeToCSV(symbol, "BUY", price, 0, CalculateRSI(symbol, 14), true);
            }
        }
        SaveState();
        Task.Delay(3000).ContinueWith(_ => { lock (_lock) { _pendingOrders.Remove(symbol); } });
    }
    public void UpdateHistory(string symbol, decimal price, long volume)
    {
        if (price <= 0) return;
        lock (_lock)
        {
            DateTime nyNow = GetEasternTime();

            // Reset Logic for New Day
            if (nyNow.TimeOfDay < new TimeSpan(9, 30, 0) && (_goalReached || _haltNewTrades))
            {
                _goalReached = false; _haltNewTrades = false; _tradesExecutedToday = 0;
                _totalRealizedPnL = 0; _dailyLossCount = 0; // Added loss count reset
                _startingDayEquity = GetTotalEquity();
                _tradesToday.Clear(); _cumVolume.Clear(); _cumVwapProd.Clear();
                SaveState();
            }

            if (!_priceHistory.ContainsKey(symbol))
            {
                _priceHistory[symbol] = new List<decimal>();
                _volumeHistory[symbol] = new List<long>();
                _cumVolume[symbol] = 0; _cumVwapProd[symbol] = 0;
            }

            _priceHistory[symbol].Add(price);
            _volumeHistory[symbol].Add(volume); // ADD ONLY ONCE

            // Update VWAP components
            _cumVolume[symbol] += volume;
            _cumVwapProd[symbol] += (price * volume);

            // Keep history size manageable
            if (_priceHistory[symbol].Count > 300) _priceHistory[symbol].RemoveAt(0);
            if (_volumeHistory[symbol].Count > 300) _volumeHistory[symbol].RemoveAt(0);

            ExecuteTradeLogic(symbol);
            CheckDailyGoal();
        }
    }
    public void PrintStatusTable()
    {
        var sb = new System.Text.StringBuilder();
        List<string> symbolsToPrint;
        string marketStatus;
        decimal pnlPct;

        lock (_lock)
        {
            symbolsToPrint = _tradeableStars.Concat(new[] { "QQQ" }).Distinct().ToList();
            marketStatus = IsMarketSafe() ? "SAFE (BULL/FLAT)" : "DANGER (BEAR)";
            decimal currentEquity = GetTotalEquity();
            pnlPct = _startingDayEquity > 0 ? ((currentEquity - _startingDayEquity) / _startingDayEquity) * 100 : 0;
        }

        string botState = "ACTIVE";
        if (GetEasternTime().TimeOfDay < new TimeSpan(10, 0, 0)) botState = "WAITING (Opens 10:00AM)";
        else if (_dailyLossCount >= 4) botState = "HALTED (4 Losses Hit)";
        else if (_goalReached) botState = "FINISHED (Goal Met)";

        // Header
        sb.AppendLine($"======= BOT: {botState} | Market: {marketStatus} | Day PnL: {pnlPct:F2}% =======".PadRight(Console.WindowWidth - 1));
        sb.AppendLine($"======= Losses: {_dailyLossCount}/4 | Active Slots: {_positions.Count}/4 =======".PadRight(Console.WindowWidth - 1));
        sb.AppendLine(string.Format("{0,-7} | {1,-8} | {2,-5} | {3,-6} | {4,-6} | {5,-8} | {6,-12} | {7,-8}",
            "SYMBOL", "PRICE", "RSI", "TREND", "VOL-X", "$ GAP", "POSITION", "PnL").PadRight(Console.WindowWidth - 1));
        sb.AppendLine(new string('-', 115).PadRight(Console.WindowWidth - 1));

        foreach (var symbol in symbolsToPrint)
        {
            string rsiDisplay = "WARM";
            string trendStr = "WAIT";
            string posStr = "---";
            string pnlStr = "---";
            string volXStr = "0.0x";
            string gapStr = "---";
            decimal currentPrice = 0;

            lock (_lock)
            {
                if (_priceHistory.ContainsKey(symbol) && _priceHistory[symbol].Count > 0)
                {
                    var prices = _priceHistory[symbol];
                    var volumes = _volumeHistory[symbol];
                    currentPrice = prices.Last();

                    // RSI Logic
                    if (prices.Count >= 15)
                        rsiDisplay = CalculateRSI(symbol, 14).ToString("F1");

                    // Trend & Gap Logic
                    if (prices.Count >= 20)
                    {
                        trendStr = GetTrend(prices);
                        decimal ma = prices.Skip(prices.Count - 20).Average();
                        decimal targetPrice = ma * 1.002m;
                        gapStr = currentPrice < targetPrice ? $"+{(targetPrice - currentPrice):F2}" : "AT TARGET";
                    }

                    // Volume Multiplier
                    if (volumes.Count >= 10)
                    {
                        decimal avgVol = (decimal)volumes.Skip(volumes.Count - 10).Average();
                        decimal volX = avgVol > 0 ? (decimal)volumes.Last() / avgVol : 0;
                        volXStr = $"{volX:F1}x";
                    }

                    if (_positions.TryGetValue(symbol, out var pos))
                    {
                        posStr = $"{pos.Quantity} @ ${pos.AvgPrice:F2}";
                        pnlStr = $"{pos.UnrealizedPnL:C2}";
                    }
                }
            }

            string row = string.Format("{0,-7} | {1,-8:C2} | {2,-5} | {3,-6} | {4,-6} | {5,-8} | {6,-12} | {7,-8}",
                symbol, currentPrice, rsiDisplay, trendStr, volXStr, gapStr, posStr, pnlStr);
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
        Console.SetCursorPosition(0, 0); // Keeps the console from flickering
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
                DailyLossCount = _dailyLossCount, // ADD THIS LINE
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
    private bool _eodEmailSent = false;

    public void CheckEndOfDayLiquidation()
    {
        var nyNow = GetEasternTime();

        // 1. Liquidation at 3:45 PM
        if (nyNow.TimeOfDay > new TimeSpan(15, 45, 0))
        {
            if (_positions.Count > 0) LiquidateAll("End of Day Liquidation");
        }

        // 2. Summary Email at 3:55 PM
        if (nyNow.TimeOfDay > new TimeSpan(15, 55, 0) && !_eodEmailSent)
        {
            SendEodSummary();
            _eodEmailSent = true;
        }

        // 3. Reset flag for next day (early morning)
        if (nyNow.TimeOfDay < new TimeSpan(9, 0, 0)) _eodEmailSent = false;
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
    public void LogTradeToCSV(string symbol, string side, decimal price, decimal pnl, double rsi, bool surge)
    {
        try
        {
            string filePath = "trade_log.csv";
            bool fileExists = File.Exists(filePath);

            using (StreamWriter sw = new StreamWriter(filePath, true))
            {
                // Write Header if file is new
                if (!fileExists)
                {
                    sw.WriteLine("Timestamp,Symbol,Action,Price,PnL,RSI,VolSurge");
                }

                string line = $"{DateTime.Now:yyyy-MM-dd HH:mm:ss},{symbol},{side},{price:F2},{pnl:F2},{rsi:F1},{surge}";
                sw.WriteLine(line);
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[LOG ERROR] Could not write to CSV: {ex.Message}");
        }
    }
    public void SendEodSummary()
    {
        lock (_lock)
        {
            int totalTrades = _tradeHistory.Count;
            int wins = _tradeHistory.Count(t => t.Profit > 0);
            int losses = _tradeHistory.Count(t => t.Profit <= 0);
            decimal winRate = totalTrades > 0 ? (decimal)wins / totalTrades * 100 : 0;

            string report = $"--- DAILY PERFORMANCE REPORT ---\n\n" +
                            $"Final PnL: {_totalRealizedPnL:C2}\n" +
                            $"Total Trades: {totalTrades}\n" +
                            $"Wins: {wins} | Losses: {losses}\n" +
                            $"Win Rate: {winRate:F1}%\n" +
                            $"Daily Loss Count: {_dailyLossCount}/4\n\n" +
                            "--- TRADE DETAILS ---\n";

            foreach (var t in _tradeHistory)
            {
                report += $"[{t.ExitTime:HH:mm}] {t.Symbol}: {t.Profit:C2}\n";
            }

            Task.Run(() => SendEmailNotification($"📊 EOD REPORT: {_totalRealizedPnL:C2}", report));
        }
    }
}