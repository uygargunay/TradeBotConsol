using IBApi;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;

public class IbClient : EWrapper, IBroker
{
    private readonly EClientSocket _client;
    private readonly EReaderSignal _signal;

    private int _currentOrderId = 1;
    public volatile bool _isReady = false;

    // ── ReqId ranges (non-overlapping) ────────────────────────────────────────
    // 10000–19999 : live market data subscriptions  (reqMktData)
    // 20000–29999 : 1-min historical data requests  (reqHistoricalData, 1 min)
    // 30000–39999 : daily historical data requests  (reqHistoricalData, 1 day)
    // 40000–49999 : dedicated NW timeframe historical requests
    // Using separate counters and ranges prevents reqId collisions between paths.
    private int _liveReqId = 10000;
    private int _histReqId = 20000;   // FIX #1: was sharing _liveReqId → collision risk
    private int _dailyReqId = 30000;
    private int _hourlyReqId = 40000;  // dedicated timeframe history for Nadaraya-Watson

    private readonly ConcurrentDictionary<int, string> _reqIdToSymbol = new();
    private readonly ConcurrentDictionary<string, int> _symToLiveReqId = new();
    private readonly ConcurrentDictionary<int, string> _histReqIdToSymbol = new();   // FIX #1
    private readonly ConcurrentDictionary<int, string> _dailyReqIdToSymbol = new();
    private readonly ConcurrentDictionary<int, string> _hourlyReqIdToSymbol = new();
    private readonly HashSet<string> _subscribedLive = new(StringComparer.OrdinalIgnoreCase);

    // Hard cap enforced inside Subscribe() — set this before calling Subscribe().
    public int MaxMarketDataLines { get; set; } = 95;

    private readonly SimulatedBroker _broker;
    private readonly ConcurrentDictionary<string, long> _tickVolume = new();

    // Track filled orderIds to prevent double-fire between orderStatus and execDetails
    private readonly ConcurrentDictionary<int, bool> _filledOrders = new();

    // ── Bracket child order tracking ──────────────────────────────────────────
    private readonly ConcurrentDictionary<int, string> _bracketChildToSymbol = new();
    private readonly ConcurrentDictionary<int, int> _bracketSiblings = new();

    // ── Bid/Ask spread tracking ───────────────────────────────────────────────
    private readonly ConcurrentDictionary<string, decimal> _latestBid = new();
    private readonly ConcurrentDictionary<string, decimal> _latestAsk = new();

    // ── Signal reversal ───────────────────────────────────────────────────────
    // When true: BUY signal → SELL order, SELL signal → BUY order.
    // Default false to avoid unexpected reversal; SimulatedBroker manages entry direction.
    public bool ReverseSignals { get; set; } = false;

    public IbClient(SimulatedBroker broker)
    {
        _signal = new EReaderMonitorSignal();
        _client = new EClientSocket(this, _signal);
        _broker = broker;
    }

    // IBroker.IsReady — true once nextValidId has fired
    public bool IsReady => _isReady;

    // ── Signal reversal helper ────────────────────────────────────────────────
    // All order submission paths call this so reversal is applied in exactly one place.
    private TradeSide ApplyReversal(TradeSide side)
        => ReverseSignals
            ? (side == TradeSide.Buy ? TradeSide.Sell : TradeSide.Buy)
            : side;

    // ── IBKR action string ────────────────────────────────────────────────────
    private static string ActionString(TradeSide side)
        => side == TradeSide.Buy ? "BUY" : "SELL";

    // ── SUBMIT ORDER ──────────────────────────────────────────────────────────
    public void SubmitOrder(string symbol, int qty, decimal price, TradeSide side,
                            double currentRsi = 0, string orderType = "LMT")
    {
        if (!_isReady) return;

        // FIX #6: guard against zero/negative quantity
        if (qty <= 0)
        {
            Console.WriteLine($"[IBKR] SubmitOrder rejected: qty={qty} for {symbol} (must be > 0)");
            return;
        }

        // REVERSAL: flip the signal direction before building the order (controlled by ReverseSignals)
        TradeSide effectiveSide = ApplyReversal(side);

        // Round to valid IBKR tick size before submission
        price = price >= 1.0m
            ? Math.Round(price, 2, MidpointRounding.AwayFromZero)
            : Math.Round(price, 4, MidpointRounding.AwayFromZero);

        int orderId = Interlocked.Increment(ref _currentOrderId);

        Order order = new Order
        {
            Action = ActionString(effectiveSide),
            OrderType = orderType,
            TotalQuantity = qty,
            Tif = orderType == "MKT" ? "DAY" : "GTC"
        };

        if (orderType == "LMT")
            order.LmtPrice = (double)price;

        Contract contract = new Contract
        {
            Symbol = symbol,
            SecType = "STK",
            Exchange = "SMART",
            Currency = "USD"
        };

        _broker.RegisterLiveOrder(orderId, symbol, effectiveSide, qty);
        _client.placeOrder(orderId, contract, order);

        Console.WriteLine($"[ORDER] {symbol} x{qty} {ActionString(effectiveSide)} " +
                          $"(signal={ActionString(side)} reversed={ReverseSignals}) " +
                          $"type={orderType} price={price} id={orderId}");
    }

    // ── IBroker.SupportsBrackets ──────────────────────────────────────────────
    public bool SupportsBrackets => true;

    // ── SUBMIT BRACKET ORDER ──────────────────────────────────────────────────
    // FIX #6: qty guard added.
    // REVERSAL: entry and exit sides are both flipped via ApplyReversal.
    public void SubmitBracketOrder(string symbol, int qty, decimal entryPrice, TradeSide side,
                                   decimal stopPrice, decimal stopLimit, decimal targetPrice)
    {
        if (!_isReady) return;

        if (qty <= 0)
        {
            Console.WriteLine($"[IBKR] SubmitBracketOrder rejected: qty={qty} for {symbol} (must be > 0)");
            return;
        }

        TradeSide effectiveSide = ApplyReversal(side);
        TradeSide exitSide = effectiveSide == TradeSide.Buy ? TradeSide.Sell : TradeSide.Buy;

        string entryAction = ActionString(effectiveSide);
        string exitAction = ActionString(exitSide);
        bool hasProfitTarget = targetPrice > 0m;

        Contract contract = new Contract
        {
            Symbol = symbol,
            SecType = "STK",
            Exchange = "SMART",
            Currency = "USD"
        };

        // Parent entry. It is transmitted by the final child order below.
        int parentId = Interlocked.Increment(ref _currentOrderId);
        Order parent = new Order
        {
            Action = entryAction,
            OrderType = "LMT",
            TotalQuantity = qty,
            LmtPrice = (double)entryPrice,
            Tif = "DAY",
            Transmit = false
        };

        // Protective stop. For a normal bracket it waits for the target order
        // to transmit the whole chain. For NW trades targetPrice=0, so the
        // stop itself is the final child and transmits the parent+stop atomically.
        int stopId = Interlocked.Increment(ref _currentOrderId);
        string ocaGroup = $"OCA_{symbol}_{parentId}";
        Order stopOrder = new Order
        {
            Action = exitAction,
            OrderType = "STP LMT",
            TotalQuantity = qty,
            AuxPrice = (double)stopPrice,
            LmtPrice = (double)stopLimit,
            ParentId = parentId,
            OcaGroup = hasProfitTarget ? ocaGroup : "",
            OcaType = hasProfitTarget ? 1 : 0,
            Tif = "DAY",
            Transmit = !hasProfitTarget
        };

        _broker.RegisterLiveOrder(parentId, symbol, effectiveSide, qty);
        _broker.RegisterLiveOrder(stopId, symbol, exitSide, qty);
        _bracketChildToSymbol[stopId] = symbol;

        _client.placeOrder(parentId, contract, parent);
        _client.placeOrder(stopId, contract, stopOrder);

        int targetId = 0;
        if (hasProfitTarget)
        {
            targetId = Interlocked.Increment(ref _currentOrderId);
            Order targetOrder = new Order
            {
                Action = exitAction,
                OrderType = "LMT",
                TotalQuantity = qty,
                LmtPrice = (double)targetPrice,
                ParentId = parentId,
                OcaGroup = ocaGroup,
                OcaType = 1,
                Tif = "DAY",
                Transmit = true
            };

            _broker.RegisterLiveOrder(targetId, symbol, exitSide, qty);
            _bracketChildToSymbol[targetId] = symbol;
            _bracketSiblings[stopId] = targetId;
            _bracketSiblings[targetId] = stopId;
            _client.placeOrder(targetId, contract, targetOrder);
        }

        _broker.RegisterBracketChildren(symbol, stopId, targetId);

        string targetText = hasProfitTarget ? targetPrice.ToString("F2") : "DYNAMIC/NONE";
        Console.WriteLine($"[BRACKET] {symbol} x{qty} entry={entryAction}@{entryPrice:F2} " +
                          $"(signal={ActionString(side)} reversed={ReverseSignals}) " +
                          $"stop={stopPrice:F2}/{stopLimit:F2} target={targetText} " +
                          $"parentId={parentId} stopId={stopId} targetId={targetId}");
    }


    // ── CONNECTION ────────────────────────────────────────────────────────────
    public void Connect(string host = "127.0.0.1", int port = 7497, int clientId = 1)
    {
        _client.eConnect(host, port, clientId);
        var reader = new EReader(_client, _signal);
        reader.Start();
        new Thread(() =>
        {
            while (_client.IsConnected())
            {
                _signal.waitForSignal();
                reader.processMsgs();
            }
        })
        { IsBackground = true }.Start();
    }

    public bool IsConnected() => _client.IsConnected();

    public void Disconnect() => _client.eDisconnect();

    // IBroker.CancelOrder
    public void CancelOrder(int orderId)
    {
        if (!_isReady) return;
        _client.cancelOrder(orderId);
        Console.WriteLine($"[IBKR] cancelOrder({orderId})");
    }

    // IBroker.RequestPositions
    public void RequestPositions()
    {
        if (!_isReady)
        {
            Console.WriteLine("[IBKR] RequestPositions called before ready — ignored.");
            return;
        }
        Console.WriteLine("[IBKR] Sending reqPositions()...");
        _client.reqPositions();
    }

    // ── SUBSCRIBE TO LIVE DATA ────────────────────────────────────────────────
    // FIX #2: lock now covers the full reqId assignment block, not just the dedup guard.
    public void Subscribe(string symbol)
    {
        lock (_subscribedLive)
        {
            if (_subscribedLive.Contains(symbol))
            {
                Console.WriteLine($"[IBKR] Already subscribed to {symbol} — skipping duplicate reqMktData.");
                return;
            }
            if (_subscribedLive.Count >= MaxMarketDataLines)
            {
                Console.WriteLine($"[IBKR] HARD CAP: {_subscribedLive.Count}/{MaxMarketDataLines} lines in use — skipping {symbol}.");
                return;
            }

            // Assign reqId and update dictionaries inside the lock so no concurrent
            // Subscribe() call can race to the same reqId or symbol entry.
            int reqId = Interlocked.Increment(ref _liveReqId);
            _reqIdToSymbol[reqId] = symbol;
            _symToLiveReqId[symbol] = reqId;
            _subscribedLive.Add(symbol);

            Contract contract = new Contract
            {
                Symbol = symbol,
                SecType = "STK",
                Exchange = "SMART",
                Currency = "USD"
            };

            _client.reqMktData(reqId, contract, "", false, false, null);
            Console.WriteLine($"[IBKR] Subscribed live ticks for {symbol} (reqId {reqId})");
        }
    }

    // IBroker.CancelMarketData
    // FIX #8: removal from _symToLiveReqId, cancelMktData, and _reqIdToSymbol are now
    // sequenced inside a lock so a racing tick cannot route through a half-removed entry.
    public void CancelMarketData(string symbol)
    {
        lock (_subscribedLive)
        {
            if (_symToLiveReqId.TryRemove(symbol, out int reqId))
            {
                _client.cancelMktData(reqId);
                _reqIdToSymbol.TryRemove(reqId, out _);
                _subscribedLive.Remove(symbol);
                Console.WriteLine($"[IBKR] Cancelled market data for {symbol} (reqId {reqId})");
            }
            else
            {
                Console.WriteLine($"[IBKR] CancelMarketData: no live subscription found for {symbol}");
            }
        }
    }

    // ── HISTORICAL DATA REQUEST (1-min, 3 days) ───────────────────────────────
    // FIX #1: now uses _histReqId (20000+) instead of _liveReqId to avoid reqId
    // collisions with live subscriptions.  Results are tracked in _histReqIdToSymbol.
    public void RequestHistoricalData(string symbol)
    {
        int id = Interlocked.Increment(ref _histReqId);
        _histReqIdToSymbol[id] = symbol;   // FIX #1: separate tracking dictionary

        Contract contract = new Contract
        {
            Symbol = symbol,
            SecType = "STK",
            Exchange = "SMART",
            Currency = "USD"
        };

        // formatDate=2 returns epoch timestamps for intraday bars. We normalize them
        // to America/New_York in historicalData(), matching the live candle clock.
        // This also lets the bot rebuild today's ORB correctly after a midday/late restart.
        _client.reqHistoricalData(id, contract, "", "3 D", "1 min", "TRADES", 0, 2, false, null);
    }

    // ── HISTORICAL DATA REQUEST (daily bars, 1 year) ──────────────────────────
    public void RequestDailyHistoricalData(string symbol)
    {
        int id = Interlocked.Increment(ref _dailyReqId);
        _dailyReqIdToSymbol[id] = symbol;

        Contract contract = new Contract
        {
            Symbol = symbol,
            SecType = "STK",
            Exchange = "SMART",
            Currency = "USD"
        };

        _client.reqHistoricalData(id, contract, "", "1 Y", "1 day", "TRADES", 1, 1, false, null);
    }

    // ── HISTORICAL DATA REQUEST (dedicated NW timeframe, RTH) ──────────────
    // Dedicated feed for the Nadaraya-Watson envelope. Keeping it
    // separate from the 1-minute buffer prevents the NW calculation from
    // silently collapsing to a 1-minute/15-minute indicator when the intraday
    // buffer is trimmed.
    public void RequestHourlyHistoricalData(string symbol, int timeframeMinutes)
    {
        int id = Interlocked.Increment(ref _hourlyReqId);
        _hourlyReqIdToSymbol[id] = symbol;

        Contract contract = new Contract
        {
            Symbol = symbol,
            SecType = "STK",
            Exchange = "SMART",
            Currency = "USD"
        };

        // Bar size AND duration both depend on the configured NW timeframe.
        // We've reliably pulled "1 Y" of 1-hour bars in practice, but IBKR's
        // historical-data API generally caps sub-hour bar sizes to a much
        // shorter duration per request (commonly documented around "1 M"
        // for 15/30-min bars) before rejecting the request or throwing a
        // pacing violation. If you see a historical-data error in the
        // console right after switching to 15/30-min, shortening this
        // duration further is the first thing to try.
        string barSizeSetting;
        string durationStr;
        switch (timeframeMinutes)
        {
            case 15:
                barSizeSetting = "15 mins";
                durationStr = "1 M";
                break;
            case 30:
                barSizeSetting = "30 mins";
                durationStr = "1 M";
                break;
            default:
                barSizeSetting = "1 hour";
                durationStr = "1 Y";
                break;
        }

        // useRTH=1: the NW levels are based on regular-session bars, not
        // overnight/after-hours prints. formatDate=2 forces Unix timestamps
        // for intraday bars so we can normalize them to US/Eastern explicitly;
        // otherwise the API can return bars in the TWS/login timezone and the
        // 09:30 ET bucket boundaries become wrong on machines outside Eastern time.
        // NOTE: with 15/30-min bars and a 1-month duration, NW_LOOKBACK values
        // much above ~500 (15m) or ~270 (30m) may not have enough bars available
        // — SimulatedBroker's GetNadarayaWatson1HourEnvelope() simply returns
        // blank/zero until enough bars accumulate rather than erroring.
        _client.reqHistoricalData(id, contract, "", durationStr, barSizeSetting, "TRADES", 1, 2, false, null);
    }

    // ── TICK CALLBACKS ────────────────────────────────────────────────────────

    public void tickPrice(int tickerId, int field, double price, TickAttrib attribs)
    {
        if (!_reqIdToSymbol.TryGetValue(tickerId, out var symbol)) return;
        if (price <= 0) return;

        if (field == 1)
        {
            _latestBid[symbol] = (decimal)price;
            if (_latestAsk.TryGetValue(symbol, out decimal ask) && ask > 0)
                _broker.UpdateBidAsk(symbol, (decimal)price, ask);
            return;
        }
        if (field == 2)
        {
            _latestAsk[symbol] = (decimal)price;
            if (_latestBid.TryGetValue(symbol, out decimal bid) && bid > 0)
                _broker.UpdateBidAsk(symbol, bid, (decimal)price);
            return;
        }

        if (field != 4) return;

        _tickVolume.TryGetValue(symbol, out long vol);
        _broker.UpdateLiveTick(symbol, (decimal)price, vol);
        _tickVolume[symbol] = 0;
    }

    // FIX #4: field 5 = LAST_SIZE (individual trade size).
    // Field 8 = VOLUME (cumulative daily volume) — using it inflated _tickVolume with
    // daily totals rather than per-trade sizes, corrupting CheckVolumeExpansion.
    public void tickSize(int tickerId, int field, int size)
    {
        if (field == 8)
        {
            // Field 8 = VOLUME: IBKR's own running cumulative volume for the
            // symbol today, resent on every update. Unlike _tickVolume below
            // (deliberately built from per-trade LAST_SIZE ticks for
            // CheckVolumeExpansion's windowed comparison — see FIX #4 above),
            // this is the right source for "how much has traded today,
            // period" — the dashboard's Volume figure and GAP_GO's relative-
            // volume gate. It self-syncs on every tick regardless of when
            // this bot process connected, so a mid-session restart doesn't
            // reset it to zero the way a locally tick-summed total does.
            // NOTE: verify units against TWS's own displayed daily volume for
            // a symbol after deploying — some IBKR API versions historically
            // reported this field in round lots (100s) rather than raw
            // shares; if the dashboard number reads ~100x low, multiply by
            // 100 here.
            if (_reqIdToSymbol.TryGetValue(tickerId, out string volSymbol))
                _broker.OnAuthoritativeDailyVolume(volSymbol, size);
            return;
        }
        if (field != 5) return;   // FIX #4: was 8 (VOLUME); correct field is 5 (LAST_SIZE)
        if (!_reqIdToSymbol.TryGetValue(tickerId, out string symbol)) return;
        _tickVolume.AddOrUpdate(symbol, size, (_, old) => old + size);
    }

    // ── ORDER CALLBACKS ───────────────────────────────────────────────────────

    public void orderStatus(int orderId, string status, double filled, double remaining,
        double avgFillPrice, int permId, int parentId,
        double lastFillPrice, int clientId, string whyHeld, double mktCapPrice)
    {
        Console.WriteLine($"[ORDER STATUS] Id={orderId} Status={status} Filled={filled} Remaining={remaining}");

        if (status == "Filled")
        {
            if (_bracketChildToSymbol.TryRemove(orderId, out _))
            {
                if (_filledOrders.TryAdd(orderId, true))
                {
                    if (_bracketSiblings.TryRemove(orderId, out int siblingId))
                    {
                        _client.cancelOrder(siblingId);
                        _bracketChildToSymbol.TryRemove(siblingId, out _);
                        _bracketSiblings.TryRemove(siblingId, out _);
                        _broker.RemoveLiveOrder(siblingId);
                    }
                    _broker.OnOrderFilled(orderId, (int)filled, (decimal)avgFillPrice);

                    // FIX #3: consistent cleanup in both bracket and normal paths
                    _filledOrders.TryRemove(orderId, out _);
                }
                return;
            }

            if (_filledOrders.TryAdd(orderId, true))
            {
                _broker.OnOrderFilled(orderId, (int)filled, (decimal)avgFillPrice);
                _filledOrders.TryRemove(orderId, out _);
            }
        }
    }

    public void execDetails(int reqId, Contract contract, Execution execution)
    {
        Console.WriteLine($"[EXECUTION] OrderId={execution.OrderId} Shares={execution.Shares} Price={execution.Price}");
        // Intentionally does NOT call OnOrderFilled — orderStatus is the authoritative callback.
    }

    // ── HISTORICAL DATA CALLBACKS ─────────────────────────────────────────────

    public void historicalData(int reqId, IBApi.Bar bar)
    {
        // Route: daily  (30000+) → AddDailyCandle
        //        hourly (40000+) → AddHourlyCandle
        //        1-min  (20000+) → AddHistoricalCandle
        bool isDaily = _dailyReqIdToSymbol.TryGetValue(reqId, out var dailySymbol);
        string hourlySymbol = null;
        bool isHourly = !isDaily && _hourlyReqIdToSymbol.TryGetValue(reqId, out hourlySymbol);
        string histSymbol = null;                                                               // FIX #1
        bool is1Min = !isDaily && !isHourly && _histReqIdToSymbol.TryGetValue(reqId, out histSymbol);

        if (!isDaily && !isHourly && !is1Min) return;

        // FIX #5: guard against null/empty symbol before proceeding
        string symbol = isDaily ? dailySymbol : isHourly ? hourlySymbol : histSymbol;
        if (string.IsNullOrEmpty(symbol))
        {
            Console.WriteLine($"[WARN] historicalData: null/empty symbol for reqId={reqId} — skipping.");
            return;
        }

        DateTime time;
        if ((isHourly || is1Min) && long.TryParse(bar.Time.Trim(), out long unixSeconds))
        {
            // Intraday requests use formatDate=2. Convert epoch UTC -> Eastern
            // before handing it to SimulatedBroker, whose RTH buckets are
            // explicitly anchored to 09:30 America/New_York.
            DateTime utc = DateTimeOffset.FromUnixTimeSeconds(unixSeconds).UtcDateTime;
            var eastern = TimeZoneInfo.FindSystemTimeZoneById("America/New_York");
            time = TimeZoneInfo.ConvertTimeFromUtc(utc, eastern);
        }
        else if (!DateTime.TryParse(bar.Time, out time))
        {
            if (DateTime.TryParseExact(bar.Time.Trim(), "yyyyMMdd  HH:mm:ss",
                System.Globalization.CultureInfo.InvariantCulture,
                System.Globalization.DateTimeStyles.None, out time))
            { }
            else if (DateTime.TryParseExact(bar.Time.Trim(), "yyyyMMdd",
                System.Globalization.CultureInfo.InvariantCulture,
                System.Globalization.DateTimeStyles.AssumeLocal, out time))
            { }
            else
            {
                Console.WriteLine($"[WARN] Could not parse bar time: '{bar.Time}' for {symbol}");
                return;
            }
        }

        if (isDaily)
        {
            _broker.AddDailyCandle(symbol, time,
                (decimal)bar.Open, (decimal)bar.High,
                (decimal)bar.Low, (decimal)bar.Close, bar.Volume);
        }
        else if (isHourly)
        {
            _broker.AddHourlyCandle(symbol, time,
                (decimal)bar.Open, (decimal)bar.High,
                (decimal)bar.Low, (decimal)bar.Close, bar.Volume);
        }
        else
        {
            _broker.AddHistoricalCandle(symbol, time,
                (decimal)bar.Open, (decimal)bar.High,
                (decimal)bar.Low, (decimal)bar.Close, bar.Volume);
        }
    }

    public void historicalDataEnd(int reqId, string start, string end)
    {
        // NW historical request complete — clean up, do NOT start live subscription.
        if (_hourlyReqIdToSymbol.TryRemove(reqId, out string hourlySymbol))
        {
            int bars = _broker.GetNadarayaWatson1HourBarCount(hourlySymbol);
            int tf = _broker.NadarayaWatsonTimeframeMinutes;
            Console.WriteLine($"[IBKR] {tf}-min NW history loaded for {hourlySymbol}: {bars} completed bars " +
                              $"(need {_broker.NadarayaWatsonLookback} for NW).");
            return;
        }

        // Daily bar request complete — clean up, do NOT start live subscription.
        if (_dailyReqIdToSymbol.TryRemove(reqId, out string dailySymbol))
        {
            Console.WriteLine($"[IBKR] Daily history loaded for {dailySymbol} — SMA200/S/R ready.");
            return;
        }

        // FIX #1: 1-min completion now looks up _histReqIdToSymbol (not _reqIdToSymbol).
        if (!_histReqIdToSymbol.TryRemove(reqId, out string symbol)) return;

        // Historical 1-minute bars are now complete. Seed intraday state that
        // otherwise only exists if the process was running at 09:30 ET (especially ORB).
        _broker.OnMinuteHistoryLoaded(symbol);
        Console.WriteLine($"[IBKR] History loaded for {symbol} — intraday state seeded; starting live tick subscription.");
        Subscribe(symbol);
    }

    // ── EWRAPPER CORE CALLBACKS ───────────────────────────────────────────────

    public void nextValidId(int orderId)
    {
        Interlocked.Exchange(ref _currentOrderId, orderId);
        _client.reqMarketDataType(1);
        _isReady = true;
        Console.WriteLine("[IBKR] Connected and Ready.");

        if (_broker.NeedsReconciliation)
        {
            Console.WriteLine("[IBKR] Requesting position snapshot for reconciliation...");
            _client.reqPositions();
        }
    }

    public void connectAck() => Console.WriteLine("[IBKR] Socket connected.");

    // FIX #9: reset _isReady and clear subscription state on disconnect so a
    // reconnect cycle re-subscribes cleanly instead of silently assuming everything
    // is still live.
    public void connectionClosed()
    {
        Console.WriteLine("[IBKR] Connection closed — resetting ready flag and subscription state.");
        _isReady = false;

        lock (_subscribedLive)
        {
            _subscribedLive.Clear();
        }

        _symToLiveReqId.Clear();
        _reqIdToSymbol.Clear();
        // Note: _histReqIdToSymbol, _dailyReqIdToSymbol and _hourlyReqIdToSymbol are intentionally NOT cleared
        // here — in-flight historical responses that arrive after a brief drop-reconnect
        // can still be routed correctly if the reqId is still in the dictionary.
    }

    public void error(Exception e) => Console.WriteLine($"[IB EXCEPTION] {e.Message}");

    public void error(string str) => Console.WriteLine($"[IB MSG] {str}");

    public void error(int id, int errorCode, string errorMsg)
    {
        if (errorCode == 2104 || errorCode == 2106 || errorCode == 2158) return;
        Console.WriteLine($"[IB ERROR] {errorCode}: {errorMsg}");

        // ── Order rejection / cancellation ─────────────────────────────────
        if (errorCode == 201 || errorCode == 202 || errorCode == 103 || errorCode == 110)
        {
            _broker.OnOrderRejected(id);

            // FIX #7: clean up bracket tracking state on rejection so dictionaries
            // don't grow unboundedly and stale sibling cancellations can't fire.
            if (_bracketChildToSymbol.TryRemove(id, out _))
            {
                if (_bracketSiblings.TryRemove(id, out int siblingId))
                {
                    _bracketChildToSymbol.TryRemove(siblingId, out _);
                    _bracketSiblings.TryRemove(siblingId, out _);
                }
            }
        }
    }

    // ── EWRAPPER STUBS (required by interface) ────────────────────────────────
    public void currentTime(long time) { }
    public void tickString(int tickerId, int field, string value) { }
    public void tickGeneric(int tickerId, int field, double value) { }
    public void tickEFP(int tickerId, int tickType, double basisPoints, string formattedBasisPoints, double impliedFuture, int holdDays, string futureLastTradeDate, double dividendImpact, double dividendsToLastTradeDate) { }
    public void deltaNeutralValidation(int reqId, UnderComp underComp) { }
    public void tickOptionComputation(int tickerId, int field, double impliedVolatility, double delta, double optPrice, double pvDividend, double gamma, double vega, double theta, double undPrice) { }
    public void tickSnapshotEnd(int tickerId) { }
    public void managedAccounts(string accountsList) { }
    public void accountSummary(int reqId, string account, string tag, string value, string currency) { }
    public void accountSummaryEnd(int reqId) { }
    public void bondContractDetails(int reqId, ContractDetails contract) { }
    public void updateAccountValue(string key, string value, string currency, string accountName) { }
    public void updatePortfolio(Contract contract, double position, double marketPrice, double marketValue, double averageCost, double unrealizedPNL, double realizedPNL, string accountName) { }
    public void updateAccountTime(string timestamp) { }
    public void accountDownloadEnd(string account) { }
    public void openOrder(int orderId, Contract contract, Order order, OrderState orderState) { }
    public void openOrderEnd() { }
    public void contractDetails(int reqId, ContractDetails contractDetails) { }
    public void contractDetailsEnd(int reqId) { }
    public void execDetailsEnd(int reqId) { }
    public void commissionReport(CommissionReport commissionReport) { }
    public void fundamentalData(int reqId, string data) { }
    public void historicalDataUpdate(int reqId, Bar bar) { }
    public void marketDataType(int reqId, int marketDataType) { }
    public void updateMktDepth(int tickerId, int position, int operation, int side, double price, int size) { }
    public void updateMktDepthL2(int tickerId, int position, string marketMaker, int operation, int side, double price, int size) { }
    public void updateNewsBulletin(int msgId, int msgType, string message, string origExchange) { }
    public void position(string account, Contract contract, double pos, double avgCost)
    {
        if (pos == 0) return;
        Console.WriteLine($"[IBKR] position(): {contract.Symbol} x{(int)pos} @ {avgCost:F2}");
        _broker.OnPositionReceived(contract.Symbol, (int)pos, (decimal)avgCost);
    }
    public void positionEnd()
    {
        Console.WriteLine("[IBKR] positionEnd() — snapshot complete, triggering reconciliation.");
        _broker.OnReconciliationComplete();
    }
    public void scannerParameters(string xml) { }
    public void scannerData(int reqId, int rank, ContractDetails contractDetails, string distance, string benchmark, string projection, string legsStr) { }
    public void scannerDataEnd(int reqId) { }
    public void receiveFA(int faDataType, string faXmlData) { }
    public void verifyMessageAPI(string apiData) { }
    public void verifyCompleted(bool isSuccessful, string errorText) { }
    public void verifyAndAuthMessageAPI(string apiData, string xyzChallenge) { }
    public void verifyAndAuthCompleted(bool isSuccessful, string errorText) { }
    public void displayGroupList(int reqId, string groups) { }
    public void displayGroupUpdated(int reqId, string contractInfo) { }
    public void positionMulti(int requestId, string account, string modelCode, Contract contract, double pos, double avgCost) { }
    public void positionMultiEnd(int requestId) { }
    public void accountUpdateMulti(int requestId, string account, string modelCode, string key, string value, string currency) { }
    public void accountUpdateMultiEnd(int requestId) { }
    public void securityDefinitionOptionParameter(int reqId, string exchange, int underlyingConId, string tradingClass, string multiplier, HashSet<string> expirations, HashSet<double> strikes) { }
    public void securityDefinitionOptionParameterEnd(int reqId) { }
    public void softDollarTiers(int reqId, SoftDollarTier[] tiers) { }
    public void familyCodes(FamilyCode[] familyCodes) { }
    public void symbolSamples(int reqId, ContractDescription[] contractDescriptions) { }
    public void mktDepthExchanges(DepthMktDataDescription[] depthMktDataDescriptions) { }
    public void tickNews(int tickerId, long timeStamp, string providerCode, string articleId, string headline, string extraData) { }
    public void smartComponents(int reqId, Dictionary<int, KeyValuePair<string, char>> theMap) { }
    public void tickReqParams(int tickerId, double minTick, string bboExchange, int snapshotPermissions) { }
    public void newsProviders(NewsProvider[] newsProviders) { }
    public void newsArticle(int requestId, int articleType, string articleText) { }
    public void historicalNews(int requestId, string time, string providerCode, string articleId, string headline) { }
    public void historicalNewsEnd(int requestId, bool hasMore) { }
    public void headTimestamp(int reqId, string headTimestamp) { }
    public void histogramData(int reqId, HistogramEntry[] data) { }
    public void rerouteMktDataReq(int reqId, int conId, string exchange) { }
    public void rerouteMktDepthReq(int reqId, int conId, string exchange) { }
    public void marketRule(int marketRuleId, PriceIncrement[] priceIncrements) { }
    public void pnl(int reqId, double dailyPnL, double unrealizedPnL, double realizedPnL) { }
    public void pnlSingle(int reqId, int pos, double dailyPnL, double unrealizedPnL, double realizedPnL, double value) { }
    public void historicalTicks(int reqId, HistoricalTick[] ticks, bool done) { }
    public void historicalTicksBidAsk(int reqId, HistoricalTickBidAsk[] ticks, bool done) { }
    public void historicalTicksLast(int reqId, HistoricalTickLast[] ticks, bool done) { }
    public void tickByTickAllLast(int reqId, int tickType, long time, double price, int size, TickAttrib attribs, string exchange, string specialConditions) { }
    public void tickByTickBidAsk(int reqId, long time, double bidPrice, double askPrice, int bidSize, int askSize, TickAttrib attribs) { }
    public void tickByTickMidPoint(int reqId, long time, double midPoint) { }
    public void realtimeBar(int reqId, long time, double open, double high, double low, double close, long volume, double WAP, int count) { }
}
