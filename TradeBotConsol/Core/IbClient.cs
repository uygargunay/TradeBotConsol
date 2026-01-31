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
    private int _currentOrderId = -1;
    private int _histReqId = 1000;    // FIX 3
    private int _liveReqId = 20000;   // FIX 3

    private readonly ConcurrentDictionary<int, int> _lastFilled = new(); // FIX 1
    private readonly ConcurrentDictionary<int, string> _reqIdToSymbol = new();
    private readonly SimulatedBroker _broker;

    public IbClient(SimulatedBroker broker)
    {
        _signal = new EReaderMonitorSignal();
        _client = new EClientSocket(this, _signal);
        _broker = broker;
    }


 
    private int _currentReqId = 1000;
    public bool _isReady = false; // Add this field
    private readonly Dictionary<string, long> _currentVolumeBatch = new();

    private readonly ConcurrentDictionary<string, decimal> _currentPriceBatch = new();
    private readonly ConcurrentQueue<string> _ibLogBuffer = new();

    public event Action<string, decimal> OnPrice;
    private readonly ConcurrentQueue<string> _ibLogs = new();

    public bool TryDequeueIbLog(out string log) => _ibLogs.TryDequeue(out log);


    public void Connect(string host = "127.0.0.1", int port = 7497, int clientId = 1)
    {
        while (!_client.IsConnected())
        {
            try
            {
                Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] Connecting to TWS...");
                _client.eConnect(host, port, clientId);
                if (!_client.IsConnected()) Thread.Sleep(2000);
            }
            catch { Thread.Sleep(2000); }
        }

        var reader = new EReader(_client, _signal);
        reader.Start();

        new Thread(() =>
        {
            while (_client.IsConnected())
            {
                _signal.waitForSignal();
                reader.processMsgs();
            }
            Console.WriteLine("[CRITICAL] IBKR Connection Lost!");
        })
        { IsBackground = true }.Start();

        Thread.Sleep(1000);
        _broker.LoadState();
        _broker.ResetPositionSync();
        _client.reqPositions();
    }

    public bool IsConnected() => _client.IsConnected();
    public void Disconnect() => _client.eDisconnect();

    public void InitializeUniverse(IEnumerable<string> symbols)
    {
        _reqIdToSymbol.Clear();
        foreach (var sym in symbols)
        {
            var contract = new Contract
            {
                Symbol = sym,
                SecType = "STK",
                Currency = "USD",
                Exchange = "SMART"
            };

            int histReqId = _histReqId++;
            int liveReqId = _liveReqId++;

            _reqIdToSymbol[histReqId] = sym;
            _reqIdToSymbol[liveReqId] = sym;

            _client.reqHistoricalData(histReqId, contract, "", "7200 S", "1 min", "TRADES", 1, 1, false, null);
            _client.reqMktData(liveReqId, contract, "", false, false, null);
            Thread.Sleep(50);
        }
    }

    // 1. Update SubmitOrder to return the ID so the Broker knows it immediately
    public void SubmitOrder(string symbol, int qty, decimal price, TradeSide side, double rsi = 0, string type = "LMT")
    {
        // FIX: Check if the bot has received nextValidId from IBKR
        if (!_isReady)
        {
            Console.WriteLine($"[REJECTED] Cannot trade {symbol}. Bot not synced with IBKR.");
            return ; // Return -1 instead of void return
        }

        var contract = new Contract { Symbol = symbol, SecType = "STK", Exchange = "SMART", Currency = "USD" };
        var order = new Order
        {
            Action = side == TradeSide.Buy ? "BUY" : "SELL",
            OrderType = type,
            TotalQuantity = qty,
            LmtPrice = (double)Math.Round(price, 2),
            Tif = "GTC"
        };

        int id = GetNextOrderId();
        _broker.RegisterLiveOrder(id, symbol, side, qty);
        _client.placeOrder(id, contract, order);
    }

    // 2. Ensure nextValidId actually initializes the counter correctly
    public void nextValidId(int orderId)
    {
        // If the server's ID is higher than our -1, sync it.
        Interlocked.Exchange(ref _currentOrderId, orderId);
    }
    public void historicalData(int reqId, Bar bar)
    {
        if (_reqIdToSymbol.TryGetValue(reqId, out var symbol))
            _broker.ProcessHistoricalBar(symbol, (decimal)bar.Close, bar.Volume);
    }

    // FIX: tickPrice now only stores the latest price; it does NOT trigger the strategy
    // 1. tickSize now ONLY updates the volume batch
    public void tickSize(int tickerId, int field, int size)
    {
        if (field == 5 && _reqIdToSymbol.TryGetValue(tickerId, out var symbol))
        {
            // Store the size so the next price tick can use it
            _currentVolumeBatch[symbol] = (long)size;
        }
    }

    // 2. tickPrice now carries the weight of triggering the strategy
    public void tickPrice(int tickerId, int field, double price, TickAttrib attribs)
    {
        if (field == 4 && price > 0) // Field 4 is LAST PRICE
        {
            if (_reqIdToSymbol.TryGetValue(tickerId, out var symbol))
            {
                _currentPriceBatch[symbol] = (decimal)price;

                // Get the volume we just stored in tickSize (or 0 if none)
                _currentVolumeBatch.TryGetValue(symbol, out long lastSize);

                // Trigger the broker logic using the latest price AND the latest size
                _broker.UpdateHistory(symbol, (decimal)price, lastSize);
            }
        }
    }
    // FIX: Centralize Order ID generation to prevent ID conflicts
    private int GetNextOrderId()
    {
        return Interlocked.Increment(ref _currentOrderId);
    }



    public int SubmitEmergencyMarketSell(string symbol, int qty)
    {
        var contract = new Contract { Symbol = symbol, SecType = "STK", Exchange = "SMART", Currency = "USD" };
        var order = new Order { Action = "SELL", OrderType = "MKT", TotalQuantity = qty };

        int id = GetNextOrderId(); // Use the central generator
        _broker.RegisterLiveOrder(id, symbol, TradeSide.Sell, qty);
        _client.placeOrder(id, contract, order);
        return id;
    }
    public void position(string account, Contract contract, double pos, double avgCost)
    {
        _broker.SyncFromIB(contract.Symbol, (int)pos, (decimal)avgCost); // FIX 5
    }

    public void positionEnd() => _broker.FinalizePositionSync();

  
    public void orderStatus(int orderId, string status, double filled, double remaining, double avgFillPrice, int permId, int parentId, double lastFillPrice, int clientId, string whyHeld, double mktCapPrice)
    {
        // FIX 1: Monotonic Fill Logic
        int filledInt = (int)filled;
        int last = _lastFilled.GetOrAdd(orderId, 0);
        int delta = filledInt - last;

        if (delta > 0)
        {
            _lastFilled[orderId] = filledInt;
            _broker.OnOrderFilled(orderId, delta, (decimal)avgFillPrice);
        }
    }

    public void error(int id, int errorCode, string errorMsg)
    {
        if (errorCode != 2104 && errorCode != 2106 && errorCode != 2158)
        {
            _ibLogs.Enqueue($"IB Err {errorCode}: {errorMsg}");
            if (id != -1) _broker.NotifyOrderFailed(id, $"IB Error {errorCode}");
        }
    }

    // Unused overrides
    public void error(Exception e) { }
    public void error(string str) { }
    public void connectAck() { }
    public void connectionClosed() { }
    public void tickString(int a, int b, string c) { }
    public void tickGeneric(int a, int b, double c) { }
    public void openOrder(int a, Contract b, Order c, OrderState d) { }
    public void openOrderEnd() { }
    public void currentTime(long t) { }
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
    public void contractDetails(int reqId, ContractDetails contractDetails) { }
    public void contractDetailsEnd(int reqId) { }
    public void execDetails(int reqId, Contract contract, Execution execution) { }
    public void execDetailsEnd(int reqId) { }
    public void commissionReport(CommissionReport commissionReport) { }
    public void fundamentalData(int reqId, string data) { }
    public void historicalDataUpdate(int reqId, Bar bar) { }
    public void historicalDataEnd(int reqId, string start, string end) { }
    public void marketDataType(int reqId, int marketDataType) { }
    public void updateMktDepth(int tickerId, int position, int operation, int side, double price, int size) { }
    public void updateMktDepthL2(int tickerId, int position, string marketMaker, int operation, int side, double price, int size) { }
    public void updateNewsBulletin(int msgId, int msgType, string message, string origExchange) { }
    public void realtimeBar(int reqId, long time, double open, double high, double low, double close, long volume, double WAP, int count) { }
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
}