using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Mail;
using System.Runtime.InteropServices; // Required for the cross-platform timezone fix
using System.Text.Json;

public interface IBroker
{
    void SubmitOrder(string symbol, int qty, decimal price, TradeSide side);
}

public enum TradeSide { Buy, Sell }

public class BotPersistData
{
    public Dictionary<string, SimPosition> Positions { get; set; } = new();
    public int TradesExecutedToday { get; set; }
    public decimal RealizedPnLTotal { get; set; }
    public decimal TotalCommissions { get; set; }
    public decimal StartingDayEquity { get; set; }
    public Dictionary<string, int> TradesPerSymbol { get; set; } = new();
    public Dictionary<string, DateTime> BuyTimes { get; set; } = new();
    public Dictionary<string, List<decimal>> PriceHistory { get; set; } = new();
}

public class Trade
{
    public string Symbol { get; set; }
    public TradeSide Action { get; set; }
    public decimal Price { get; set; }
    public decimal Quantity { get; set; }
    public DateTime Time { get; set; } = DateTime.UtcNow;
}

public class SimPosition
{
    public decimal Quantity { get; set; }
    public decimal AvgPrice { get; set; }
    public decimal TrailingStop { get; set; }
    public decimal CurrentPrice { get; set; }
    public decimal RealizedPnL { get; set; }
    public decimal UnrealizedPnL => Quantity * (CurrentPrice - AvgPrice);
}

public class PositionManager : SimulatedBroker { }

public class SimulatedBroker : IBroker
{
    // --- RISK MANAGEMENT (STRICT 4k POWER) ---
    private const decimal initialAccountValue = 4000m;
    private const decimal riskPerTradeDollar = 50m;
    private const decimal dailyLossLimitPct = 0.02m;
    private const decimal dailyProfitGoalPct = 0.04m;
    private const decimal maxOrderValuePerStock = 1000m;

    private const int maxTradesGlobal = 30;
    private const int maxTradesPerSymbol = 5;
    private const decimal roundTripFee = 2.00m;
    private const decimal slippagePct = 0.0005m;

    // --- STRATEGY SETTINGS ---
    private const int shortSmaPeriod = 9;
    private const int longSmaPeriod = 50;
    private const int atrPeriod = 14;
    private const decimal stopLossAtrMult = 1.5m;
    private const decimal profitTargetMultiplier = 3.0m;
    private const int maxMinutesInTrade = 30;

    // --- TARGET WATCHLIST ---
    private readonly string[] _tradeableStars = { "NVDA", "TSLA", "PLTR", "AMD" };

    protected readonly Dictionary<string, SimPosition> _positions = new();
    protected readonly Dictionary<string, List<decimal>> _priceHistory = new();
    protected readonly Dictionary<string, int> _tradesToday = new();
    protected readonly Dictionary<string, DateTime> _buyTimes = new();

    private DateTime _lastUpdateReceived = DateTime.UtcNow;
    private DateTime _lastHeartbeatLogged = DateTime.UtcNow;
    private DateTime _lastProgressLogged = DateTime.MinValue;
    private const string SaveFilePath = "bot_state.json";
    private readonly object _lock = new object();

    private decimal _startingDayEquity = initialAccountValue;
    private decimal _totalRealizedPnL = 0m;
    private decimal _totalCommissions = 0m;
    private int _tradesExecutedToday = 0;
    private bool _haltNewTrades = false;
    private bool _qqqBullish = false;
    private int _lastSavedMinute = -1;
    private Dictionary<string, int> _lastSavedMinutePerSymbol = new();

    public IBroker RealBroker { get; set; }
    public IReadOnlyDictionary<string, SimPosition> Positions => _positions;
    public decimal TotalRealizedPnL => _totalRealizedPnL;

    private decimal CurrentDailyLossLimit => _startingDayEquity * dailyLossLimitPct * -1;
    private decimal CurrentDailyProfitGoal => _startingDayEquity * dailyProfitGoalPct;
    private decimal CurrentExposure => _positions.Values.Sum(p => p.Quantity * p.CurrentPrice);
    private decimal AvailableBuyingPower => initialAccountValue - CurrentExposure;

    private readonly TimeSpan _latestEntryTime = new TimeSpan(15, 30, 0);

    public void MarkToMarket(Dictionary<string, decimal> prices) => OnPriceUpdate(prices);

    public void SyncExistingPosition(string symbol, decimal qty, decimal avgPrice)
    {
        lock (_lock)
        {
            if (!_positions.ContainsKey(symbol))
            {
                _positions[symbol] = new SimPosition
                {
                    Quantity = qty,
                    AvgPrice = avgPrice,
                    CurrentPrice = avgPrice,
                    TrailingStop = avgPrice - (CalculateTrueATR(symbol) > 0 ? CalculateTrueATR(symbol) * stopLossAtrMult : avgPrice * 0.02m)
                };
                _buyTimes[symbol] = DateTime.UtcNow;
            }
        }
    }

    public void ResetDailyTrades()
    {
        lock (_lock)
        {
            _priceHistory.Clear();
            _startingDayEquity += _totalRealizedPnL;
            _tradesExecutedToday = 0;
            _totalRealizedPnL = 0m;
            _totalCommissions = 0m;
            _tradesToday.Clear();
            _buyTimes.Clear();
            _haltNewTrades = false;
            SaveState();
            Console.WriteLine($"[RESET] New Baseline Equity: {_startingDayEquity:C2}");
        }
    }

    public void ArchiveDailyResults()
    {
        try
        {
            var marketDate = GetEasternTime().ToString("yyyy-MM-dd");
            File.AppendAllText("trade_history_log.txt",
                $"{marketDate} | Starting: {_startingDayEquity:C2} | Net: {_totalRealizedPnL:C2}{Environment.NewLine}");
        }
        catch (Exception ex) { Console.WriteLine($"Archive Error: {ex.Message}"); }
    }

    public void SendEmailSummary(string toEmail)
    {
        Console.WriteLine($"[EMAIL] Preparing summary for {toEmail}... (Realized: {_totalRealizedPnL:C2})");
    }
    public void SendEmailNotification(string subject, string messageBody)
    {
        try
        {
            var fromAddress = new MailAddress("uygargunay@gmail.com", "TradeBot Live");
            var toAddress = new MailAddress("uygargunay@gmail.com");
            // RESTORED: Your specific Google App Password
            const string fromPassword = "vshq kfqv bclm hxsq";

            var smtp = new SmtpClient
            {
                Host = "smtp.gmail.com",
                Port = 587,
                EnableSsl = true,
                DeliveryMethod = SmtpDeliveryMethod.Network,
                UseDefaultCredentials = false,
                Credentials = new NetworkCredential(fromAddress.Address, fromPassword)
            };

            // Adding timestamp to subject to prevent Gmail "threading"
            string timedSubject = $"{subject} [{GetEasternTime():HH:mm:ss}]";

            using (var message = new MailMessage(fromAddress, toAddress) { Subject = timedSubject, Body = messageBody })
            {
                smtp.Send(message);
            }
        }
        catch (Exception ex)
        {
            // We log to console so you know if the email failed without crashing the bot
            Console.WriteLine($"[EMAIL ERROR] {ex.Message}");
        }
    }
    public void PrintDailySummary()
    {
        Console.WriteLine($"\n--- SUMMARY --- \nNet PnL: {_totalRealizedPnL:C2} \nExposure: {CurrentExposure:C2}");
        foreach (var p in _positions) Console.WriteLine($"> {p.Key}: Unrealized {p.Value.UnrealizedPnL:C2}");
    }

    public void OnPriceUpdate(Dictionary<string, decimal> prices)
    {
        lock (_lock)
        {
            // 1. Get current time in Eastern for logic
            DateTime nyNow = GetEasternTime();

            // 2. AUTO-RESET ON NEW DAY: Check if the date has changed
            // We compare the date of our last stored update to today's date
            if (_lastUpdateReceived.Date != DateTime.UtcNow.Date)
            {
                Console.WriteLine($"[NEW DAY] {nyNow:yyyy-MM-dd} detected. Resetting history and limits...");
                ResetDailyTrades();
            }

            // 3. Update the heartbeat and time variables
            _lastUpdateReceived = DateTime.UtcNow;
            TimeSpan currentTimeOfDay = nyNow.TimeOfDay;

            // 4. Time Gate for new entries
            bool isHuntingTime = currentTimeOfDay >= new TimeSpan(9, 30, 0) && currentTimeOfDay < _latestEntryTime;

            foreach (var kv in prices)
            {
                string symbol = kv.Key;
                decimal price = kv.Value;

                UpdateHistory(symbol, price);

                // Safety check: Don't process logic until we have at least 2 data points for SMA diffs
                if (!_priceHistory.ContainsKey(symbol) || _priceHistory[symbol].Count < 2) continue;

                if (symbol == "QQQ") _qqqBullish = IsTrendUp(symbol);

                if (_positions.ContainsKey(symbol))
                    ManagePosition(symbol, price);

                // --- DEBUG LOG: See why it isn't trading ---
                if (_tradeableStars.Contains(symbol) && !_positions.ContainsKey(symbol) && _priceHistory[symbol].Count >= longSmaPeriod)
                {
                    bool trend = IsTrendUp(symbol);
                    bool slope = IsSlopeUp(symbol);

                    // Only log every 30 seconds to avoid console clutter
                    if (DateTime.UtcNow.Second % 30 == 0)
                        Console.WriteLine($"[DEBUG] {symbol}: Price {price:C2} | QQQ Bull: {_qqqBullish} | Trend: {trend} | Slope: {slope}");
                }

                // --- ENTRY LOGIC ---
                if (!_haltNewTrades && isHuntingTime && _tradeableStars.Contains(symbol) && !_positions.ContainsKey(symbol))
                {
                    if (_priceHistory[symbol].Count >= longSmaPeriod)
                    {
                        if (_qqqBullish && IsTrendUp(symbol) && IsSlopeUp(symbol))
                        {
                            TryEnter(symbol, price);
                        }
                    }
                }
            }

            CheckEndOfDayLiquidation();
            CheckHealth();
        }
    }
    protected void TryEnter(string symbol, decimal price)
    {
        // 1. Basic Safety Checks
        if (_tradesExecutedToday >= maxTradesGlobal) return;
        if (_tradesToday.GetValueOrDefault(symbol) >= maxTradesPerSymbol) return;
        if (AvailableBuyingPower < 100) return;

        // 2. ATR Calculation
        decimal atr = CalculateTrueATR(symbol);

        // DEBUG: If ATR is too low, the bot won't trade.
        if (atr <= 0.01m)
        {
            Console.WriteLine($"[SKIPPED] {symbol} ATR too low ({atr:F4}). Market might be too flat.");
            return;
        }

        // 3. Risk-Based Position Sizing
        decimal stopDistance = atr * stopLossAtrMult;

        // Ensure the stop isn't ZERO to avoid division by zero errors
        if (stopDistance <= 0) return;

        int qtyBasedOnRisk = (int)Math.Floor(riskPerTradeDollar / stopDistance);
        int qtyBasedOnCap = (int)Math.Floor(maxOrderValuePerStock / price);
        int qtyBasedOnPower = (int)Math.Floor(AvailableBuyingPower / price);

        // Pick the smallest of the three to be safe
        int finalQty = Math.Min(qtyBasedOnRisk, Math.Min(qtyBasedOnCap, qtyBasedOnPower));

        if (finalQty > 0)
        {
            Console.WriteLine($"[ATTEMPT] Conditions met for {symbol}. Qty: {finalQty} | Price: {price:C2} | ATR: {atr:F2}");
            SubmitOrder(symbol, finalQty, price, TradeSide.Buy);
        }
        else
        {
            Console.WriteLine($"[SKIPPED] {symbol} Qty was 0. Risk: {qtyBasedOnRisk} | Cap: {qtyBasedOnCap}");
        }
    }
    protected void ManagePosition(string symbol, decimal price)
    {
        var pos = _positions[symbol];
        pos.CurrentPrice = price;
        decimal atr = CalculateTrueATR(symbol);
        decimal profitTargetPrice = pos.AvgPrice + (atr * stopLossAtrMult * profitTargetMultiplier);

        decimal newStop = price - (atr * stopLossAtrMult);
        if (newStop > pos.TrailingStop) pos.TrailingStop = newStop;

        bool hitStop = price <= pos.TrailingStop;
        bool hitTarget = price >= profitTargetPrice;
        bool timeExpired = (DateTime.UtcNow - _buyTimes[symbol]).TotalMinutes > maxMinutesInTrade;

        if (hitStop || hitTarget || timeExpired)
        {
            string reason = hitStop ? "Stop Loss" : hitTarget ? "Profit Target" : "Time Expired";
            Console.WriteLine($"[EXIT] {symbol} | Reason: {reason} | PnL: {pos.UnrealizedPnL:C2}");
            SubmitOrder(symbol, (int)pos.Quantity, price, TradeSide.Sell);
        }
    }

    protected void UpdateHistory(string symbol, decimal price)
    {
        lock (_lock)
        {
            if (!_priceHistory.ContainsKey(symbol)) _priceHistory[symbol] = new List<decimal>();

            int currentMinute = DateTime.UtcNow.Minute;
            if (!_lastSavedMinutePerSymbol.TryGetValue(symbol, out int lastMin)) lastMin = -1;

            if (currentMinute != lastMin)
            {
                _priceHistory[symbol].Add(price);
                _lastSavedMinutePerSymbol[symbol] = currentMinute;

                if (_priceHistory[symbol].Count > 200)
                    _priceHistory[symbol].RemoveAt(0);

                Console.WriteLine($"[TICK] {symbol} added new minute candle: {price:C2}");

                // --- ADD THIS LINE ---
                SaveState();
            }
            else if (_priceHistory[symbol].Count > 0)
            {
                _priceHistory[symbol][_priceHistory[symbol].Count - 1] = price;
            }
        }
    }
    protected bool IsTrendUp(string symbol)
    {
        var h = _priceHistory[symbol];
        if (h.Count < longSmaPeriod) return false;
        return h.TakeLast(shortSmaPeriod).Average() > h.TakeLast(longSmaPeriod).Average();
    }

    protected bool IsSlopeUp(string symbol)
    {
        var h = _priceHistory[symbol];
        if (h.Count < shortSmaPeriod + 1) return false;
        decimal currentSma = h.TakeLast(shortSmaPeriod).Average();
        decimal prevSma = h.Skip(Math.Max(0, h.Count - shortSmaPeriod - 1)).Take(shortSmaPeriod).Average();
        return currentSma > prevSma;
    }

    protected decimal CalculateTrueATR(string symbol)
    {
        if (!_priceHistory.ContainsKey(symbol)) return 0;
        var h = _priceHistory[symbol];
        if (h.Count < atrPeriod + 1) return 0;
        decimal sumTR = 0;
        for (int i = h.Count - atrPeriod; i < h.Count; i++)
            sumTR += Math.Abs(h[i] - h[i - 1]);
        return sumTR / atrPeriod;
    }

    public void SubmitOrder(string symbol, int qty, decimal price, TradeSide side)
    {
        lock (_lock)
        {
            // Apply slippage to simulate real-world fills
            decimal executionPrice = side == TradeSide.Buy ? price * (1 + slippagePct) : price * (1 - slippagePct);

            if (side == TradeSide.Buy)
            {
                // Calculate ATR-based stop distance
                decimal atr = CalculateTrueATR(symbol);
                decimal stopDistance = atr * stopLossAtrMult;

                // SAFETY FLOOR: Ensure stop loss is at least 0.2% away from entry price
                // This prevents "instant stops" during low-volatility consolidation.
                decimal minFloor = executionPrice * 0.002m;
                decimal finalStopDistance = Math.Max(stopDistance, minFloor);

                _positions[symbol] = new SimPosition
                {
                    AvgPrice = executionPrice,
                    Quantity = qty,
                    CurrentPrice = executionPrice,
                    TrailingStop = executionPrice - finalStopDistance
                };

                _buyTimes[symbol] = DateTime.UtcNow;
                _tradesExecutedToday++;
                _tradesToday[symbol] = _tradesToday.GetValueOrDefault(symbol) + 1;

                Console.WriteLine($"[BUY] {symbol} | Qty: {qty} | Price: {executionPrice:C2} | Stop: {(_positions[symbol].TrailingStop):C2}");
                string buyMsg = $"🚀 BUY ORDER FILLED\n\nSymbol: {symbol}\nQty: {qty}\nPrice: {executionPrice:C2}\nInitial Stop: {(_positions[symbol].TrailingStop):C2}\nTime: {GetEasternTime()}";
                SendEmailNotification($"🚀 BUY: {symbol}", buyMsg);
            }
            else if (_positions.TryGetValue(symbol, out var pos))
            {
                // Calculate PnL and subtract the round-trip commission fee
                decimal pnl = (executionPrice - pos.AvgPrice) * pos.Quantity;
                _totalRealizedPnL += (pnl - roundTripFee);
                _totalCommissions += roundTripFee;

                _positions.Remove(symbol);
                _buyTimes.Remove(symbol);

                Console.WriteLine($"[SELL] {symbol} | Price: {executionPrice:C2} | Net PnL: {(pnl - roundTripFee):C2}");
                decimal tradePnL = pnl - roundTripFee;
                string sellMsg = $"💰 SELL ORDER FILLED\n\nSymbol: {symbol}\nPrice: {executionPrice:C2}\nNet PnL: {tradePnL:C2}\nTotal Day PnL: {_totalRealizedPnL:C2}";
                SendEmailNotification($"💰 SELL: {symbol} ({tradePnL:C2})", sellMsg);
                // Kill-switch: Halt if daily loss limit or profit goal is reached
                if (_totalRealizedPnL <= CurrentDailyLossLimit || _totalRealizedPnL >= CurrentDailyProfitGoal)
                {
                    _haltNewTrades = true;
                    Console.WriteLine("!!! [HALT] Daily limits reached. New trades disabled. !!!");
                }
            }

            SaveState();
        }
    }
    public void CheckHealth()
    {
        var now = DateTime.UtcNow;

        // Print the status table every 60 seconds
        if ((now - _lastHeartbeatLogged).TotalSeconds >= 60)
        {
            PrintStatusTable();
            _lastHeartbeatLogged = now;
        }

        LogProgressReport();
    }

    public void LogProgressReport()
    {
        var now = DateTime.UtcNow;
        if ((now - _lastProgressLogged).TotalMinutes < 15) return;
        lock (_lock)
        {
            _lastProgressLogged = now;
            decimal goal = CurrentDailyProfitGoal;
            decimal progress = _totalRealizedPnL > 0 ? (_totalRealizedPnL / goal) * 100 : 0;
            Console.WriteLine($"[PROGRESS] {GetEasternTime():HH:mm} NY | PnL: {_totalRealizedPnL:C2} | {progress:F1}% of Goal");
        }
    }

    private readonly object _consoleLock = new object();

    public void PrintStartupConfiguration()
    {
        lock (_consoleLock)
        {
            var nyTime = GetEasternTime();
            var vanTime = GetVancouverTime();

            Console.Clear();
            Console.ForegroundColor = ConsoleColor.Cyan;
            Console.WriteLine("====================================================");
            Console.WriteLine("             TRADING BOT - 4K BUYING POWER          ");
            Console.WriteLine("====================================================");
            Console.ResetColor();

            Console.WriteLine($"[TIME]    NY: {nyTime:HH:mm} | Vancouver: {vanTime:HH:mm}");
            Console.WriteLine($"[CAPITAL]  Power: {initialAccountValue:C2} | Per Stock Cap: {maxOrderValuePerStock:C2}");
            Console.WriteLine($"[EXPOSURE] In Market: {CurrentExposure:C2} | Idle Cash: {AvailableBuyingPower:C2}");
            Console.WriteLine($"[GOALS]    Profit: {CurrentDailyProfitGoal:C2} | Max Loss: {CurrentDailyLossLimit:C2}");

            Console.WriteLine("----------------------------------------------------");
            Console.WriteLine($"[WATCHLIST] NVDA, TSLA, PLTR, AMD");
            Console.WriteLine("----------------------------------------------------");

            Console.ForegroundColor = _haltNewTrades ? ConsoleColor.Yellow : ConsoleColor.Green;
            Console.WriteLine(_haltNewTrades ? "[STATUS] HALTED: Daily limit reached." : "[STATUS] ACTIVE: Scanning 9/50 SMA...");
            Console.ResetColor();

            Console.WriteLine("====================================================");
            Console.WriteLine("\n[CONTROLS] [S] Save | [P] PnL | [K] KILL");
        }
    }

    public List<Trade> CheckEndOfDayLiquidation(bool force = false)
    {
        var trades = new List<Trade>();
        var et = GetEasternTime();

        // This fires at 3:45 PM NY
        if (force || et.TimeOfDay > new TimeSpan(15, 45, 0))
        {
            if (_positions.Count > 0)
            {
                Console.WriteLine($"[EOD] Closing all positions before market close...");
                foreach (var s in _positions.Keys.ToList())
                {
                    var p = _positions[s];
                    SubmitOrder(s, (int)p.Quantity, p.CurrentPrice, TradeSide.Sell);
                    trades.Add(new Trade { Symbol = s, Quantity = p.Quantity });
                }
            }
            if (trades.Count > 0)
            {
                string eodMsg = $"🏁 Market close liquidation complete.\nTotal Closed: {trades.Count}\nFinal Daily Realized: {_totalRealizedPnL:C2}";
                SendEmailNotification("🏁 EOD Summary", eodMsg);
            }
        }

        return trades;
    }
    private DateTime GetEasternTime()
    {
        string tzId = RuntimeInformation.IsOSPlatform(OSPlatform.Windows) ? "Eastern Standard Time" : "America/New_York";
        return TimeZoneInfo.ConvertTimeFromUtc(DateTime.UtcNow, TimeZoneInfo.FindSystemTimeZoneById(tzId));
    }

    private DateTime GetVancouverTime()
    {
        string tzId = RuntimeInformation.IsOSPlatform(OSPlatform.Windows) ? "Pacific Standard Time" : "America/Vancouver";
        return TimeZoneInfo.ConvertTimeFromUtc(DateTime.UtcNow, TimeZoneInfo.FindSystemTimeZoneById(tzId));
    }

    public void SaveState()
    {
        lock (_lock)
        {
            try
            {
                var data = new BotPersistData
                {
                    Positions = _positions,
                    TradesExecutedToday = _tradesExecutedToday,
                    RealizedPnLTotal = _totalRealizedPnL,
                    TotalCommissions = _totalCommissions,
                    StartingDayEquity = _startingDayEquity,
                    TradesPerSymbol = _tradesToday,
                    BuyTimes = _buyTimes,
                    PriceHistory = _priceHistory
                };
                File.WriteAllText(SaveFilePath, JsonSerializer.Serialize(data, new JsonSerializerOptions { WriteIndented = true }));
            }
            catch { }
        }
    }

    public void LoadState()
    {
        if (!File.Exists(SaveFilePath)) return;
        try
        {
            var data = JsonSerializer.Deserialize<BotPersistData>(File.ReadAllText(SaveFilePath));
            if (data == null) return;
            _totalRealizedPnL = data.RealizedPnLTotal; _totalCommissions = data.TotalCommissions;
            _tradesExecutedToday = data.TradesExecutedToday; _startingDayEquity = data.StartingDayEquity;
            foreach (var kv in data.Positions) _positions[kv.Key] = kv.Value;
            foreach (var kv in data.TradesPerSymbol) _tradesToday[kv.Key] = kv.Value;
            foreach (var kv in data.BuyTimes) _buyTimes[kv.Key] = kv.Value;
            if (data.PriceHistory != null) foreach (var kv in data.PriceHistory) _priceHistory[kv.Key] = kv.Value;
        }
        catch { }
    }
    public void PrintStatusTable()
    {
        lock (_lock)
        {
            Console.WriteLine("\n" + new string('=', 70));
            Console.WriteLine(string.Format("{0,-8} | {1,-10} | {2,-8} | {3,-10} | {4,-8} | {5,-8}",
                "SYMBOL", "PRICE", "MINS", "TREND", "SMA 9", "SMA 50"));
            Console.WriteLine(new string('-', 70));

            foreach (var symbol in _tradeableStars.Concat(new[] { "QQQ" }))
            {
                if (!_priceHistory.ContainsKey(symbol) || _priceHistory[symbol].Count == 0) continue;

                var history = _priceHistory[symbol];
                decimal price = history.Last();
                int count = history.Count;

                decimal currentSma9 = history.TakeLast(Math.Min(count, 9)).Average();
                decimal currentSma50 = history.TakeLast(Math.Min(count, 50)).Average();

                string trendStr = count >= 50
                    ? (currentSma9 > currentSma50 ? "BULL 🟢" : "BEAR 🔴")
                    : $"W-{count}";

                if (symbol == "QQQ" && count >= 50)
                    trendStr = _qqqBullish ? "MKT-UP 🚀" : "MKT-DN ⚠️";

                Console.WriteLine(string.Format("{0,-8} | {1,-10:C2} | {2,-8} | {3,-10} | {4,-8:F2} | {5,-8:F2}",
                    symbol, price, $"{count}/50", trendStr, currentSma9, currentSma50));
            }

            // --- DASHBOARD SECTION ---
            Console.WriteLine(new string('=', 70));
            decimal unrealized = _positions.Values.Sum(p => p.UnrealizedPnL);
            decimal totalNet = _totalRealizedPnL + unrealized;

            Console.ForegroundColor = ConsoleColor.Cyan;
            Console.WriteLine($"[ACCOUNT] Start: {_startingDayEquity:C2} | Realized: {_totalRealizedPnL:C2} | Fees: {_totalCommissions:C2}");

            // Color code the PnL
            if (totalNet >= 0) Console.ForegroundColor = ConsoleColor.Green;
            else Console.ForegroundColor = ConsoleColor.Red;

            Console.WriteLine($"[LIVE PnL] {totalNet:C2} (Unrealized: {unrealized:C2})");
            Console.ResetColor();

            if (_positions.Count > 0)
            {
                Console.WriteLine("---------------- Active Positions ----------------");
                foreach (var pos in _positions)
                {
                    Console.WriteLine($"> {pos.Key}: {pos.Value.Quantity} @ {pos.Value.AvgPrice:C2} | Stop: {pos.Value.TrailingStop:C2}");
                }
            }
            Console.WriteLine(new string('=', 70));
        }
    }
}