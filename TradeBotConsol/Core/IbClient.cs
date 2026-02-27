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
    private readonly SimulatedBroker _broker;
    private readonly ConcurrentDictionary<string, long> _tickVolume = new();

    // NEW: track filled orderIds to prevent double-fire between orderStatus and execDetails
    private readonly ConcurrentDictionary<int, bool> _filledOrders = new();

    public IbClient(SimulatedBroker broker)
    {
        _signal = new EReaderMonitorSignal();
        _client = new EClientSocket(this, _signal);
        _broker = broker;
    }

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
            Tif = "GTC"
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

    // --- SUBSCRIBE TO LIVE DATA ---
    public void Subscribe(string symbol)
    {
        int reqId = Interlocked.Increment(ref _liveReqId);
        _reqIdToSymbol[reqId] = symbol;

        Contract contract = new Contract
        {
            Symbol = symbol,
            SecType = "STK",
            Exchange = "SMART",
            Currency = "USD"
        };

        _client.reqMktData(reqId, contract, "", false, false, null);
        Console.WriteLine($"[IBKR] Subscribed to {symbol} with reqId {reqId}");
    }


    // --- HISTORICAL DATA REQUEST ---
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

    // --- TICK CALLBACKS ---

    // FIX: was processing all tick fields (bid, ask, last).
    // Now filters to field 4 (LAST traded price) only so candles reflect real trades.
    public void tickPrice(int tickerId, int field, double price, TickAttrib attribs)
    {
        if (!_reqIdToSymbol.TryGetValue(tickerId, out string symbol)) return;
        if (price <= 0) return;

        // field 4 = LAST traded price. Ignore bid (1) and ask (2).
        if (field != 4) return;

        _tickVolume.TryGetValue(symbol, out long vol);
        _broker.UpdateLiveTick(symbol, (decimal)price, vol);
        _tickVolume[symbol] = 0;
    }

    // Accumulate trade size per symbol between ticks
    public void tickSize(int tickerId, int field, int size)
    {
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
            if (_filledOrders.TryAdd(orderId, true))
                _broker.OnOrderFilled(orderId, (int)filled, (decimal)avgFillPrice);
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
        if (!_reqIdToSymbol.TryGetValue(reqId, out string symbol)) return;

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

        _broker.AddHistoricalCandle(
            symbol, time,
            (decimal)bar.Open,
            (decimal)bar.High,
            (decimal)bar.Low,
            (decimal)bar.Close,
            bar.Volume
        );
    }

    public void historicalDataEnd(int reqId, string start, string end)
    {
        if (_reqIdToSymbol.TryGetValue(reqId, out string symbol))
            Console.WriteLine($"[HISTORY LOADED] {symbol}");
    }

    // --- EWRAPPER CORE CALLBACKS ---
    void EWrapper.nextValidId(int orderId)
    {
        _currentOrderId = orderId;
        _client.reqMarketDataType(1); // 1 = Live data
        _isReady = true;
        Console.WriteLine("[IBKR] Connected and Ready.");
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
    public void position(string account, Contract contract, double pos, double avgCost) { }
    public void positionEnd() { }
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