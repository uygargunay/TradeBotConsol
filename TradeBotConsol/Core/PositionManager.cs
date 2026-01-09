using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Mail;
using System.Text.Json;

namespace TradeBotConsol
{
    public interface IBroker
    {
        void SubmitOrder(string symbol, int qty, decimal price, TradeSide side);
    }

    public enum TradeSide { Buy, Sell }

    public class Trade
    {
        public string Symbol { get; set; }
        public TradeSide Action { get; set; }
        public decimal Price { get; set; }
        public decimal Quantity { get; set; }
        public DateTime Time { get; set; } = DateTime.Now;
    }

    public class SimPosition
    {
        public decimal Quantity { get; set; }
        public decimal AvgPrice { get; set; }
        public decimal TrailingStop { get; set; }
        public decimal CurrentPrice { get; set; }
        public decimal RealizedPnL { get; set; } = 0m;
        public decimal UnrealizedPnL => Quantity * (CurrentPrice - AvgPrice);
    }

    public class BotPersistData
    {
        public Dictionary<string, SimPosition> Positions { get; set; } = new();
        public int TradesExecutedToday { get; set; }
        public decimal RealizedPnLTotal { get; set; }
        public decimal TotalCommissions { get; set; }
        public decimal PeakDailyRealizedPnL { get; set; }
        public Dictionary<string, int> TradesPerSymbol { get; set; } = new();
    }

    // CLEANED: PositionManager now simply inherits everything without duplicating code
    public class PositionManager : SimulatedBroker { }

    public class SimulatedBroker : IBroker
    {
        protected readonly Dictionary<string, SimPosition> _positions = new();
        protected readonly Dictionary<string, List<decimal>> _priceHistory = new();
        protected readonly Dictionary<string, DateTime> _sellCooldowns = new();
        protected readonly Dictionary<string, int> _tradesToday = new();

        private const string SaveFilePath = "bot_state.json";
        private DateTime _lastHeartbeatTime = DateTime.MinValue;

        public IReadOnlyDictionary<string, SimPosition> Positions => _positions;
        public decimal TotalRealizedPnL => _totalRealizedPnL;

        // --- AGGRESSION SETTINGS ---
        private const int shortSmaPeriod = 9;
        private const int longSmaPeriod = 50;
        private const int maxTradesGlobal = 30;
        private const int maxTradesPerSymbol = 5;
        private const decimal tradeDollarAmount = 2000m;
        private const decimal roundTripFee = 2.00m;

        private decimal _totalRealizedPnL = 0m;
        private decimal _totalCommissions = 0m;
        private int _tradesExecutedToday = 0;
        private decimal _peakDailyRealizedPnL = 0m;
        private bool _qqqBullish = false;

        // --- OPTIMIZED RISK/REWARD SETTINGS ---
        private readonly Dictionary<string, decimal> _customStops = new() {
            { "RKLB", 0.050m }, { "PLTR", 0.035m }, { "TSLA", 0.035m },
            { "NVDA", 0.030m }, { "MSFT", 0.015m }, { "AAPL", 0.015m }
        };
        private readonly Dictionary<string, decimal> _customTargets = new() {
            { "RKLB", 0.075m }, { "PLTR", 0.050m }, { "TSLA", 0.050m },
            { "NVDA", 0.045m }, { "MSFT", 0.025m }, { "AAPL", 0.025m }
        };

        public IBroker RealBroker { get; set; }

        public void SubmitOrder(string symbol, int qty, decimal price, TradeSide side)
        {
            Console.WriteLine($"\n[BROKER] Executing {side} for {qty} {symbol} at ${price:0.00}");
            RealBroker?.SubmitOrder(symbol, qty, price, side);

            if (side == TradeSide.Buy)
            {
                decimal stopPct = _customStops.GetValueOrDefault(symbol, 0.025m);
                _positions[symbol] = new SimPosition
                {
                    AvgPrice = price,
                    Quantity = qty,
                    CurrentPrice = price,
                    TrailingStop = price * (1 - stopPct)
                };
                _tradesExecutedToday++;
                _tradesToday[symbol] = _tradesToday.GetValueOrDefault(symbol, 0) + 1;
                SendTradeAlert("BUY", symbol, price, (decimal)qty);
            }
            else if (_positions.ContainsKey(symbol))
            {
                var pos = _positions[symbol];
                decimal grossPnL = pos.Quantity * (price - pos.AvgPrice);
                _totalCommissions += roundTripFee;
                _totalRealizedPnL += (grossPnL - roundTripFee);
                SendTradeAlert(grossPnL > 0 ? "PROFIT" : "STOP", symbol, price, pos.Quantity, grossPnL, (grossPnL - roundTripFee));
                _positions.Remove(symbol);
                _sellCooldowns[symbol] = DateTime.Now.AddMinutes(15); // Prevent instant re-entry
            }
            SaveState();
        }

        public void OnPriceUpdate(Dictionary<string, decimal> marketPrices)
        {
            // 1. Heartbeat check
            if ((DateTime.Now - _lastHeartbeatTime).TotalHours >= 2 && IsInTradingWindow())
            {
                SendTradeAlert("HEARTBEAT", "SYSTEM", 0, 0);
                _lastHeartbeatTime = DateTime.Now;
            }

            // 2. Circuit Breaker (Loss Limit)
            if (_totalRealizedPnL <= -400.00m)
            {
                if (_positions.Count > 0) CheckEndOfDayLiquidation(true);
                Console.WriteLine("[CRITICAL] Daily Loss Limit Hit. Trading Suspended.");
                return;
            }

            // 3. Time-based exit check (EOD)
            CheckEndOfDayLiquidation();

            foreach (var kv in marketPrices)
            {
                var symbol = kv.Key;
                var price = kv.Value;

                if (!_priceHistory.ContainsKey(symbol)) _priceHistory[symbol] = new List<decimal>();
                _priceHistory[symbol].Add(price);
                if (_priceHistory[symbol].Count > 110) _priceHistory[symbol].RemoveAt(0);

                // Ensure we have enough history for SMA + Slope check
                if (_priceHistory[symbol].Count >= longSmaPeriod + 5)
                {
                    var history = _priceHistory[symbol];
                    var shortSma = history.Skip(history.Count - shortSmaPeriod).Average();
                    var longSma = history.Skip(history.Count - longSmaPeriod).Average();

                    // --- SLOPE CALCULATION ---
                    // Look at where the Long SMA was 5 ticks ago
                    var prevLongSma = history.Skip(history.Count - (longSmaPeriod + 5)).Take(longSmaPeriod).Average();
                    bool isSlopingUp = longSma > prevLongSma;

                    // Update Regime Filter based on QQQ
                    if (symbol == "QQQ") _qqqBullish = (shortSma > longSma);

                    // 4. POSITION MANAGEMENT (Exit Logic)
                    if (_positions.ContainsKey(symbol))
                    {
                        var pos = _positions[symbol];
                        pos.CurrentPrice = price;

                        decimal target = _customTargets.GetValueOrDefault(symbol, 0.02m);
                        decimal stopPct = _customStops.GetValueOrDefault(symbol, 0.025m);

                        // Update Trailing Stop
                        decimal newStop = price * (1 - stopPct);
                        if (newStop > pos.TrailingStop) pos.TrailingStop = newStop;

                        // Profit Target or Trailing Stop hit
                        if (price <= pos.TrailingStop || price >= pos.AvgPrice * (1 + target))
                        {
                            SubmitOrder(symbol, (int)pos.Quantity, price, TradeSide.Sell);
                        }
                    }

                    // 5. ENTRY LOGIC (Buy Logic)
                    bool isCooling = _sellCooldowns.TryGetValue(symbol, out var cd) && DateTime.Now < cd;

                    // Added isSlopingUp to ensure we aren't buying a downward trend
                    if (_qqqBullish && (shortSma > longSma) && isSlopingUp && !_positions.ContainsKey(symbol) && IsInTradingWindow() && !isCooling)
                    {
                        if (_tradesExecutedToday < maxTradesGlobal && _tradesToday.GetValueOrDefault(symbol, 0) < maxTradesPerSymbol)
                        {
                            var qty = (int)Math.Floor(tradeDollarAmount / price);
                            if (qty > 0)
                            {
                                SubmitOrder(symbol, qty, price, TradeSide.Buy);
                            }
                        }
                    }
                }
            }
        }
        public List<Trade> CheckEndOfDayLiquidation(bool force = false)
        {
            var trades = new List<Trade>();
            if (force || (DateTime.Now.TimeOfDay >= new TimeSpan(12, 45, 0) && _positions.Count > 0))
            {
                Console.WriteLine("[SYSTEM] EOD/Manual Liquidation. Closing all positions...");
                foreach (var s in _positions.Keys.ToList())
                {
                    var pos = _positions[s];
                    SubmitOrder(s, (int)pos.Quantity, pos.CurrentPrice, TradeSide.Sell);
                    trades.Add(new Trade { Symbol = s, Action = TradeSide.Sell, Price = pos.CurrentPrice, Quantity = pos.Quantity });
                }
                ArchiveDailyResults();
                SendTradeAlert("EOD SUMMARY", "SYSTEM", 0, 0);
            }
            return trades;
        }

        public void SyncExistingPosition(string symbol, decimal qty, decimal avgPrice)
        {
            if (!_positions.ContainsKey(symbol))
            {
                decimal stopPct = _customStops.GetValueOrDefault(symbol, 0.025m);
                _positions[symbol] = new SimPosition { Quantity = qty, AvgPrice = avgPrice, CurrentPrice = avgPrice, TrailingStop = avgPrice * (1 - stopPct) };
                SaveState();
            }
        }

        public void PrintDailySummary()
        {
            decimal grossTotal = _totalRealizedPnL + _totalCommissions;
            Console.WriteLine("\n===============================================");
            Console.WriteLine($"   NET PNL:   ${_totalRealizedPnL:0.00} (Fees: -${_totalCommissions:0.00})");
            Console.WriteLine("===============================================");
            foreach (var p in _positions) Console.WriteLine($"{p.Key}: Unrealized ${p.Value.UnrealizedPnL:0.00}");
        }

        public void SendTradeAlert(string action, string symbol, decimal price, decimal qty, decimal gross = 0, decimal net = 0)
        {
            try
            {
                using MailMessage mail = new MailMessage("uygargunay@gmail.com", "uygargunay@gmail.com");
                mail.Subject = $"[{action}] {symbol}";
                string body = $"{action} Alert\nSymbol: {symbol}\nPrice: ${price:0.00}\nQty: {qty}\n";
                if (action != "BUY" && action != "HEARTBEAT" && action != "EOD SUMMARY")
                    body += $"Trade Gross: ${gross:0.00}\nTrade Net: ${net:0.00}\n";
                body += $"Daily Net PnL: ${_totalRealizedPnL:0.00}";
                mail.Body = body;
                using SmtpClient sc = new SmtpClient("smtp.gmail.com", 587)
                {
                    Credentials = new NetworkCredential("uygargunay@gmail.com", "zklk qkcu vwya qlky"),
                    EnableSsl = true
                };
                sc.Send(mail);
            }
            catch { }
        }

        public void ArchiveDailyResults()
        {
            decimal grossTotal = _totalRealizedPnL + _totalCommissions;
            try { File.AppendAllText("trade_history_log.txt", $"{DateTime.Now:yyyy-MM-dd} | Gross: ${grossTotal:0.00} | Net PnL: ${_totalRealizedPnL:0.00}\n"); } catch { }
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
                    TotalCommissions = _totalCommissions,
                    PeakDailyRealizedPnL = _peakDailyRealizedPnL,
                    TradesPerSymbol = _tradesToday
                };
                File.WriteAllText(SaveFilePath, JsonSerializer.Serialize(data, new JsonSerializerOptions { WriteIndented = true }));
            }
            catch { }
        }

        public void LoadState()
        {
            if (!File.Exists(SaveFilePath)) return;
            try
            {
                var data = JsonSerializer.Deserialize<BotPersistData>(File.ReadAllText(SaveFilePath));
                if (data == null) return;
                _tradesExecutedToday = data.TradesExecutedToday;
                _totalRealizedPnL = data.RealizedPnLTotal;
                _totalCommissions = data.TotalCommissions;
                _positions.Clear();
                foreach (var kv in data.Positions) _positions[kv.Key] = kv.Value;
                _tradesToday.Clear();
                foreach (var kv in data.TradesPerSymbol) _tradesToday[kv.Key] = kv.Value;
            }
            catch { }
        }

        public bool IsInTradingWindow()
        {
            var now = DateTime.Now.TimeOfDay;
            return DateTime.Now.DayOfWeek != DayOfWeek.Saturday && DateTime.Now.DayOfWeek != DayOfWeek.Sunday &&
                   now >= new TimeSpan(8, 10, 0) && now <= new TimeSpan(12, 40, 0);
        }
    }
}