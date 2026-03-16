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

    private int _liveReqId = 10000;
    private readonly ConcurrentDictionary<int, string> _reqIdToSymbol = new();
    private readonly ConcurrentDictionary<string, int> _symToLiveReqId = new();     // symbol → active reqMktData reqId
    private readonly HashSet<string> _subscribedLive = new(StringComparer.OrdinalIgnoreCase); // dedup guard

    // Daily historical data reqIds use a separate range (30000+) so historicalData()
    // and historicalDataEnd() can distinguish daily bars from 1-min bars and route them
    // to AddDailyCandle() instead of AddHistoricalCandle(), without affecting live subs.
    private int _dailyReqId = 30000;
    private readonly ConcurrentDictionary<int, string> _dailyReqIdToSymbol = new();

    // Hard cap enforced inside Subscribe() — set this before calling Subscribe().
    // Every reqMktData call goes through Subscribe(), so this is the single
    // chokepoint that no other code path can bypass.
    public int MaxMarketDataLines { get; set; } = 95;
    private readonly SimulatedBroker _broker;
    private readonly ConcurrentDictionary<string, long> _tickVolume = new();

    // NEW: track filled orderIds to prevent double-fire between orderStatus and execDetails
    private readonly ConcurrentDictionary<int, bool> _filledOrders = new();

    // ── Bracket child order tracking ───────────────────────────────────────────
    // When SubmitBracketOrder places a stop child and a target child, their IDs are
    // registered here so orderStatus() can route fills correctly and cancel the sibling.
    // _bracketChildToSymbol : stopId/targetId → symbol (so we know which position to close)
    // _bracketSiblings      : stopId → targetId and targetId → stopId (for sibling cancellation)
    // Both are cleared when a child fills or is cancelled.
    private readonly ConcurrentDictionary<int, string> _bracketChildToSymbol = new();
    private readonly ConcurrentDictionary<int, int> _bracketSiblings = new();

    // ── Bid/Ask spread tracking — fields 1 (BID) and 2 (ASK) ──
    // Stored per symbol so SimulatedBroker can check spread before entering a trade.
    // Both must be positive before UpdateBidAsk is called to avoid partial/stale data.
    private readonly ConcurrentDictionary<string, decimal> _latestBid = new();
    private readonly ConcurrentDictionary<string, decimal> _latestAsk = new();

    public IbClient(SimulatedBroker broker)
    {
        _signal = new EReaderMonitorSignal();
        _client = new EClientSocket(this, _signal);
        _broker = broker;
    }

    // IBroker.IsReady — true once nextValidId has fired
    public bool IsReady => _isReady;

    // --- SUBMIT ORDER ---
    public void SubmitOrder(string symbol, int qty, decimal price, TradeSide side, double currentRsi = 0, string orderType = "LMT")
    {
        if (!_isReady) return;

        // Round to valid IBKR tick size before submission
        price = price >= 1.0m
            ? Math.Round(price, 2, MidpointRounding.AwayFromZero)
            : Math.Round(price, 4, MidpointRounding.AwayFromZero);

        int orderId = Interlocked.Increment(ref _currentOrderId);

        Order order = new Order
        {
            Action = side == TradeSide.Buy ? "BUY" : "SELL",
            OrderType = orderType,
            TotalQuantity = qty,
            // MKT orders use DAY so an unfilled EOD liquidation order expires
            // at close rather than carrying over to the next morning open (GTC).
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

        _broker.RegisterLiveOrder(orderId, symbol, side, qty);
        _client.placeOrder(orderId, contract, order);
    }

    // ── IBroker.SupportsBrackets ─────────────────────────────────────────────────
    // Bracket children (stop + target) are now fully registered in _ordersById and
    // _bracketChildToSymbol so orderStatus() routes their fills to OnOrderFilled().
    // SimulatedBroker stores child IDs on SimPosition and skips conflicting local
    // exit logic (scale-out, trailing stop) while a bracket is live.
    public bool SupportsBrackets => true;

    // ── IBroker.SubmitBracketOrder — parent LMT entry + OCA stop + OCA target ─
    // IBKR bracket pattern:
    //   1. Parent order  — LMT entry, transmit=false
    //   2. Stop child    — STP-LMT, parentId = parent orderId, transmit=false
    //   3. Target child  — LMT,     parentId = parent orderId, transmit=true (fires all three)
    // The two child orders share an OCA group so a fill on one cancels the other.
    // entryPrice  — parent limit price
    // stopPrice   — stop trigger; stopLimit — worst acceptable fill price
    // targetPrice — profit target limit price
    public void SubmitBracketOrder(string symbol, int qty, decimal entryPrice, TradeSide side,
                                   decimal stopPrice, decimal stopLimit, decimal targetPrice)
    {
        if (!_isReady) return;

        Contract contract = new Contract
        {
            Symbol = symbol,
            SecType = "STK",
            Exchange = "SMART",
            Currency = "USD"
        };

        string entryAction = side == TradeSide.Buy ? "BUY" : "SELL";
        string exitAction = side == TradeSide.Buy ? "SELL" : "BUY";

        // ── 1. Parent entry order (LMT, not transmitted yet) ──────────────────
        int parentId = Interlocked.Increment(ref _currentOrderId);
        Order parent = new Order
        {
            Action = entryAction,
            OrderType = "LMT",
            TotalQuantity = qty,
            LmtPrice = (double)entryPrice,
            Tif = "GTC",
            Transmit = false   // hold — transmit together with children
        };

        // ── 2. Stop-Limit child ───────────────────────────────────────────────
        int stopId = Interlocked.Increment(ref _currentOrderId);
        string ocaGroup = $"OCA_{symbol}_{parentId}";
        Order stopOrder = new Order
        {
            Action = exitAction,
            OrderType = "STP LMT",
            TotalQuantity = qty,
            AuxPrice = (double)stopPrice,   // STP trigger
            LmtPrice = (double)stopLimit,   // worst-acceptable fill
            ParentId = parentId,
            OcaGroup = ocaGroup,
            OcaType = 1,                   // 1 = cancel remaining orders with block
            Tif = "GTC",
            Transmit = false
        };

        // ── 3. Target LMT child (transmit=true fires all three atomically) ────
        int targetId = Interlocked.Increment(ref _currentOrderId);
        Order targetOrder = new Order
        {
            Action = exitAction,
            OrderType = "LMT",
            TotalQuantity = qty,
            LmtPrice = (double)targetPrice,
            ParentId = parentId,
            OcaGroup = ocaGroup,
            OcaType = 1,
            Tif = "GTC",
            Transmit = true   // transmits all three at once
        };

        // Register all three with SimulatedBroker so fills / rejections are tracked.
        // Only the entry (parentId) is registered as an entry order.
        // Stop and target are exit legs — registered with the exit side so OnOrderFilled
        // routes their fills into the CLOSING path, not as new entries.
        TradeSide exitSide = side == TradeSide.Buy ? TradeSide.Sell : TradeSide.Buy;
        _broker.RegisterLiveOrder(parentId, symbol, side, qty);
        _broker.RegisterLiveOrder(stopId, symbol, exitSide, qty);   // stop child
        _broker.RegisterLiveOrder(targetId, symbol, exitSide, qty);   // target child

        // Track children for fill routing and sibling cancellation in orderStatus()
        _bracketChildToSymbol[stopId] = symbol;
        _bracketChildToSymbol[targetId] = symbol;
        _bracketSiblings[stopId] = targetId;
        _bracketSiblings[targetId] = stopId;

        _client.placeOrder(parentId, contract, parent);
        _client.placeOrder(stopId, contract, stopOrder);
        _client.placeOrder(targetId, contract, targetOrder);

        // Tell SimulatedBroker to stamp child IDs onto the SimPosition once the entry fills
        _broker.RegisterBracketChildren(symbol, stopId, targetId);

        Console.WriteLine($"[BRACKET] {symbol} x{qty} entry={entryPrice:F2} " +
                          $"stop={stopPrice:F2}/{stopLimit:F2} target={targetPrice:F2} " +
                          $"parentId={parentId} stopId={stopId} targetId={targetId}");
    }

    // --- CONNECTION ---
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

    // IBroker.CancelOrder — cancels a live IBKR order by ID.
    // Called by SimulatedBroker when a local exit fires while a bracket is active,
    // so the exchange-side stop/target is withdrawn before the local order goes out.
    public void CancelOrder(int orderId)
    {
        if (!_isReady) return;
        _client.cancelOrder(orderId);
        Console.WriteLine($"[IBKR] cancelOrder({orderId})");
    }

    // IBroker.RequestPositions — fires reqPositions() on the IBKR socket.
    // IBKR will call position() once per holding, then positionEnd() when done.
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

    // --- SUBSCRIBE TO LIVE DATA ---
    // Safe to call multiple times — dedup guard AND hard line-count cap enforced here.
    // This is the ONLY place reqMktData is called, so MaxMarketDataLines cannot be
    // exceeded regardless of how many callers invoke Subscribe().
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
                Console.WriteLine($"[IBKR] HARD CAP: {_subscribedLive.Count}/{MaxMarketDataLines} lines in use — skipping {symbol}. Raise MaxMarketDataLines or buy a Booster Pack.");
                return;
            }
            _subscribedLive.Add(symbol);
        }

        int reqId = Interlocked.Increment(ref _liveReqId);
        _reqIdToSymbol[reqId] = symbol;
        _symToLiveReqId[symbol] = reqId;

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

    // IBroker.CancelMarketData — unsubscribes live tick feed for a symbol
    public void CancelMarketData(string symbol)
    {
        if (_symToLiveReqId.TryRemove(symbol, out int reqId))
        {
            _client.cancelMktData(reqId);
            _reqIdToSymbol.TryRemove(reqId, out _);
            lock (_subscribedLive) { _subscribedLive.Remove(symbol); }
            Console.WriteLine($"[IBKR] Cancelled market data for {symbol} (reqId {reqId})");
        }
        else
        {
            Console.WriteLine($"[IBKR] CancelMarketData: no live subscription found for {symbol}");
        }
    }


    // --- HISTORICAL DATA REQUEST (1-min, 3 days) ---
    // FIX: was _liveReqId++ (not thread-safe). Now uses Interlocked.Increment.
    public void RequestHistoricalData(string symbol)
    {
        int id = Interlocked.Increment(ref _liveReqId);
        _reqIdToSymbol[id] = symbol;

        Contract contract = new Contract
        {
            Symbol = symbol,
            SecType = "STK",
            Exchange = "SMART",
            Currency = "USD"
        };

        _client.reqHistoricalData(id, contract, "", "3 D", "1 min", "TRADES", 0, 1, false, null);
    }

    // --- HISTORICAL DATA REQUEST (daily bars, 1 year) ---
    // Uses a separate reqId range (30000+) so historicalData() routes these bars
    // to AddDailyCandle() for the SMA200 / prev day H/L filters.
    // Does NOT start a live market data subscription on completion.
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

        // "1 Y" of "1 day" bars. useRTH=1 so only regular session closes are used —
        // overnight gaps don't distort the SMA200 calculation.
        _client.reqHistoricalData(id, contract, "", "1 Y", "1 day", "TRADES", 1, 1, false, null);
    }

    // --- TICK CALLBACKS ---

    // FIX: was processing all tick fields (bid, ask, last).
    // Now filters to field 4 (LAST traded price) only so candles reflect real trades.
    // Fields 1 (BID) and 2 (ASK) are also captured for the spread filter.
    public void tickPrice(int tickerId, int field, double price, TickAttrib attribs)
    {
        if (!_reqIdToSymbol.TryGetValue(tickerId, out var symbol)) return;
        if (price <= 0) return;

        // field 1 = BID, field 2 = ASK — store for spread filter
        if (field == 1)
        {
            _latestBid[symbol] = (decimal)price;
            // If we already have a valid ask, push both to the broker
            if (_latestAsk.TryGetValue(symbol, out decimal ask) && ask > 0)
                _broker.UpdateBidAsk(symbol, (decimal)price, ask);
            return;
        }
        if (field == 2)
        {
            _latestAsk[symbol] = (decimal)price;
            // If we already have a valid bid, push both to the broker
            if (_latestBid.TryGetValue(symbol, out decimal bid) && bid > 0)
                _broker.UpdateBidAsk(symbol, bid, (decimal)price);
            return;
        }

        // field 4 = LAST traded price. Ignore all other fields.
        if (field != 4) return;

        _tickVolume.TryGetValue(symbol, out long vol);
        _broker.UpdateLiveTick(symbol, (decimal)price, vol);
        _tickVolume[symbol] = 0;
    }

    // Accumulate trade size per symbol between ticks.
    // Only field 8 (LAST_SIZE) represents actual trades. Ignoring bid (0)
    // and ask (3) size changes which are quote updates, not executions.
    // Without this filter, bid/ask quote churn inflates volume counts and
    // causes CheckVolumeExpansion to fire on non-trade activity.
    public void tickSize(int tickerId, int field, int size)
    {
        if (field != 8) return;  // 8 = LAST_SIZE only
        if (!_reqIdToSymbol.TryGetValue(tickerId, out string symbol)) return;
        _tickVolume.AddOrUpdate(symbol, size, (_, old) => old + size);
    }

    // --- ORDER CALLBACKS ---

    // FIX: use _filledOrders.TryAdd to ensure OnOrderFilled fires exactly once.
    // Previously both orderStatus AND execDetails called OnOrderFilled, doubling PnL.
    public void orderStatus(int orderId, string status, double filled, double remaining,
        double avgFillPrice, int permId, int parentId,
        double lastFillPrice, int clientId, string whyHeld, double mktCapPrice)
    {
        Console.WriteLine($"[ORDER STATUS] Id={orderId} Status={status} Filled={filled} Remaining={remaining}");

        if (status == "Filled")
        {
            // ── Bracket child fill (stop or target hit on the exchange) ──────────
            // Must be checked BEFORE the normal path to prevent double-fire.
            // When a child fills: cancel the sibling so only one exit executes,
            // clean up tracking state, then route to OnOrderFilled normally.
            if (_bracketChildToSymbol.TryRemove(orderId, out _))
            {
                if (_filledOrders.TryAdd(orderId, true))
                {
                    if (_bracketSiblings.TryRemove(orderId, out int siblingId))
                    {
                        // Cancel the other leg (e.g. target fills → cancel stop, and vice versa)
                        _client.cancelOrder(siblingId);
                        _bracketChildToSymbol.TryRemove(siblingId, out _);
                        _bracketSiblings.TryRemove(siblingId, out _);
                        // Remove sibling from _ordersById — its cancel may still call back
                        _broker.RemoveLiveOrder(siblingId);
                    }
                    _broker.OnOrderFilled(orderId, (int)filled, (decimal)avgFillPrice);
                    _filledOrders.TryRemove(orderId, out _);
                }
                return; // handled — do not fall through to normal path
            }

            // ── Normal (non-bracket) fill ─────────────────────────────────────
            if (_filledOrders.TryAdd(orderId, true))
            {
                _broker.OnOrderFilled(orderId, (int)filled, (decimal)avgFillPrice);
                // Clean up immediately after processing — _filledOrders is only
                // needed to deduplicate the orderStatus/execDetails double-fire.
                // Keeping entries forever is a memory leak on a long-running bot.
                _filledOrders.TryRemove(orderId, out _);
            }
        }
    }

    // Intentionally does NOT call OnOrderFilled.
    // orderStatus is the authoritative fill callback. execDetails is for logging only.
    public void execDetails(int reqId, Contract contract, Execution execution)
    {
        Console.WriteLine($"[EXECUTION] OrderId={execution.OrderId} Shares={execution.Shares} Price={execution.Price}");
        // Do not call _broker.OnOrderFilled here — orderStatus handles it
    }

    // --- HISTORICAL DATA CALLBACKS ---
    public void historicalData(int reqId, IBApi.Bar bar)
    {
        // Determine if this is a daily-bar request (reqId >= 30000)
        // or a 1-min request, and route accordingly.
        bool isDaily = _dailyReqIdToSymbol.TryGetValue(reqId, out var dailySymbol);
        string? symbol = isDaily ? dailySymbol : null;
        if (!isDaily && !_reqIdToSymbol.TryGetValue(reqId, out symbol)) return;

        DateTime time;

        if (!DateTime.TryParse(bar.Time, out time))
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
            // Route to daily candle cache — used for SMA200 and prev day H/L
            _broker.AddDailyCandle(
                symbol, time,
                (decimal)bar.Open,
                (decimal)bar.High,
                (decimal)bar.Low,
                (decimal)bar.Close,
                bar.Volume
            );
        }
        else
        {
            _broker.AddHistoricalCandle(
                symbol, time,
                (decimal)bar.Open,
                (decimal)bar.High,
                (decimal)bar.Low,
                (decimal)bar.Close,
                bar.Volume
            );
        }
    }

    public void historicalDataEnd(int reqId, string start, string end)
    {
        // Daily bar request completed — clean up reqId, do NOT start a live subscription.
        // Daily bars are for SMA200 / S/R calculations only, not a streaming data feed.
        if (_dailyReqIdToSymbol.TryRemove(reqId, out string dailySymbol))
        {
            Console.WriteLine($"[IBKR] Daily history loaded for {dailySymbol} — SMA200/S/R ready.");
            return;
        }

        // 1-min request completed — start live tick subscription as before.
        if (!_reqIdToSymbol.TryRemove(reqId, out string symbol)) return;

        Console.WriteLine($"[IBKR] History loaded for {symbol} — starting live tick subscription.");

        // Auto-subscribe live ticks now that candles are ready.
        // Subscribe() is dedup-guarded so calling it here AND from Program.cs is safe.
        Subscribe(symbol);
    }

    // --- EWRAPPER CORE CALLBACKS ---
    void EWrapper.nextValidId(int orderId)
    {
        Interlocked.Exchange(ref _currentOrderId, orderId);
        _client.reqMarketDataType(1); // 1 = Live data
        _isReady = true;
        Console.WriteLine("[IBKR] Connected and Ready.");

        // Case A: LoadState ran BEFORE Connect() and set NeedsReconciliation = true.
        // This is the earliest safe point to call reqPositions() — socket is now live.
        if (_broker.NeedsReconciliation)
        {
            Console.WriteLine("[IBKR] Requesting position snapshot for reconciliation (deferred from LoadState)...");
            _client.reqPositions();
        }
    }

    public void connectAck() => Console.WriteLine("[IBKR] Socket connected.");

    public void connectionClosed() => Console.WriteLine("[IBKR] Connection closed.");

    public void error(Exception e) => Console.WriteLine($"[IB EXCEPTION] {e.Message}");

    public void error(string str) => Console.WriteLine($"[IB MSG] {str}");

    public void error(int id, int errorCode, string errorMsg)
    {
        // Ignore routine farm connection messages
        if (errorCode == 2104 || errorCode == 2106 || errorCode == 2158) return;
        Console.WriteLine($"[IB ERROR] {errorCode}: {errorMsg}");

        // ── Order rejection / cancellation ────────────────────────────────
        // Codes that mean IBKR will never fill this order:
        //   103 = duplicate order id   110 = bad price tick
        //   201 = order rejected       202 = order cancelled
        // Without this block, SimulatedBroker._pendingEntryCount is never
        // decremented → the bot thinks MAX_POSITIONS is permanently full
        // → no new trades ever enter for the rest of the session.
        if (errorCode == 201 || errorCode == 202 || errorCode == 103 || errorCode == 110)
        {
            _broker.OnOrderRejected(id);
        }
    }

    // --- EWRAPPER STUBS (required by interface) ---
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
        // pos == 0 means IBKR closed the position — skip it
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
    void EWrapper.realtimeBar(int reqId, long time, double open, double high, double low, double close, long volume, double WAP, int count) { }
}