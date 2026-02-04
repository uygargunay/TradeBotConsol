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
    private int _liveReqId = 10000;

    private readonly ConcurrentDictionary<int, int> _lastFilled = new();
    private readonly ConcurrentDictionary<int, string> _reqIdToSymbol = new();
    private readonly ConcurrentDictionary<string, int> _symbolToConId = new();

    private readonly SimulatedBroker _broker;
    public volatile bool _isReady = false;

    private readonly Dictionary<string, long> _currentVolumeBatch = new();
    private readonly ConcurrentDictionary<string, decimal> _currentPriceBatch = new();
    private readonly ConcurrentQueue<string> _ibLogs = new();

    public bool TryDequeueIbLog(out string log) => _ibLogs.TryDequeue(out log);
    public bool IsConnected() => _client.IsConnected(); 
    public void Disconnect() => _client.eDisconnect();
    public IbClient(SimulatedBroker broker)
    {
        _signal = new EReaderMonitorSignal();
        _client = new EClientSocket(this, _signal);
        _broker = broker;
    }

    public void Connect(string host = "127.0.0.1", int port = 7497, int clientId = 1)
    {
        Console.WriteLine($"[SYSTEM] Connecting to IBKR {host}:{port}...");
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

    public void InitializeUniverse(List<string> symbols)
    {
        _client.reqMarketDataType(3); // LIVE

        int reqId = _liveReqId;
        foreach (var symbol in symbols)
        {
            var contract = BuildStockContract(symbol);
            _reqIdToSymbol[reqId] = symbol;
            _client.reqMktData(reqId, contract, "", false, false, null);
            reqId++;
            Thread.Sleep(25);
        }
    }

    private Contract BuildStockContract(string symbol)
    {
        return new Contract
        {
            Symbol = symbol,
            SecType = "STK",
            Exchange = "SMART",
            PrimaryExch = GetPrimaryExchange(symbol),
            Currency = "USD"
        };
    }

    private string GetPrimaryExchange(string symbol)
    {
        return symbol switch
        {
            "JPM" => "NYSE",
            "GS" => "NYSE",
            "BABA" => "NYSE",
            "PLTR" => "NYSE",
            "UBER" => "NYSE",
            _ => "NASDAQ"
        };
    }

    public void SubmitOrder(string symbol, int qty, decimal price, TradeSide side, double rsi = 0, string type = "LMT")
    {
        if (!_isReady) return;

        var contract = BuildStockContract(symbol);
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

    public int SubmitEmergencyMarketSell(string symbol, int qty)
    {
        var contract = BuildStockContract(symbol);
        var order = new Order { Action = "SELL", OrderType = "MKT", TotalQuantity = qty };

        int id = GetNextOrderId();
        _broker.RegisterLiveOrder(id, symbol, TradeSide.Sell, qty);
        _client.placeOrder(id, contract, order);
        return id;
    }

    public void nextValidId(int orderId)
    {
        Interlocked.Exchange(ref _currentOrderId, orderId);

        if (!_isReady && _currentOrderId > 0)
            _isReady = true;

        _client.reqPositions();
        Console.WriteLine($"[IBKR] Connected! Next Order ID: {orderId}");
    }


    private int GetNextOrderId() => Interlocked.Increment(ref _currentOrderId);

    // -------- DATA --------

    public void tickPrice(int tickerId, int field, double price, TickAttrib attribs)
    {
        if ((field == 4 || field == 6) && price > 0)
        {
            if (_reqIdToSymbol.TryGetValue(tickerId, out var symbol))
            {
                if (_currentPriceBatch.TryGetValue(symbol, out var last))
                {
                    if (Math.Abs((decimal)price - last) / last > 0.1m)
                    {
                        _ibLogs.Enqueue($"[SUSPICIOUS FEED] {symbol} jumped too much: {price}");
                        return;
                    }
                }

                _currentPriceBatch[symbol] = (decimal)price;
                _currentVolumeBatch.TryGetValue(symbol, out long vol);
                _broker.UpdateHistory(symbol, (decimal)price, vol);
            }
        }
    }

    public void tickSize(int tickerId, int field, int size)
    {
        if ((field == 5 || field == 8) && _reqIdToSymbol.TryGetValue(tickerId, out var symbol))
            _currentVolumeBatch[symbol] = size;
    }

    public void position(string account, Contract contract, double pos, double avgCost)
    {
        _broker.SyncFromIB(contract.Symbol, (int)pos, (decimal)avgCost);
    }

    public void positionEnd() => _broker.FinalizePositionSync();

    public void orderStatus(int orderId, string status, double filled, double remaining, double avgFillPrice, int permId, int parentId, double lastFillPrice, int clientId, string whyHeld, double mktCapPrice)
    {
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
        if (errorCode == 10197 || errorCode == 2104 || errorCode == 2106 || errorCode == 2158) return;
        _ibLogs.Enqueue($"[{DateTime.Now:HH:mm:ss}] Error {errorCode}: {errorMsg}");
    }

    // --- REQUIRED NO-OPS ---
    public void error(Exception e) { }
    public void error(string str) { }
    public void connectAck() { }
    public void connectionClosed() { }
    public void tickString(int a, int b, string c) { }
    public void tickGeneric(int a, int b, double c) { }
    public void openOrder(int a, Contract b, Order c, OrderState d) { }
    public void openOrderEnd() { }
    public void currentTime(long t) { }
    public void marketDataType(int reqId, int type)
    {
        if (type != 1)
            Console.WriteLine("⚠ WARNING: NOT LIVE MARKET DATA");
    }

    void EWrapper.tickEFP(int tickerId, int tickType, double basisPoints, string formattedBasisPoints, double impliedFuture, int holdDays, string futureLastTradeDate, double dividendImpact, double dividendsToLastTradeDate)
    {
       
    }

    void EWrapper.deltaNeutralValidation(int reqId, UnderComp underComp)
    {
       
    }

    void EWrapper.tickOptionComputation(int tickerId, int field, double impliedVolatility, double delta, double optPrice, double pvDividend, double gamma, double vega, double theta, double undPrice)
    {
       
    }

    void EWrapper.tickSnapshotEnd(int tickerId)
    {
       
    }

    void EWrapper.managedAccounts(string accountsList)
    {
       
    }

    void EWrapper.accountSummary(int reqId, string account, string tag, string value, string currency)
    {
       
    }

    void EWrapper.accountSummaryEnd(int reqId)
    {
       
    }

    void EWrapper.bondContractDetails(int reqId, ContractDetails contract)
    {
       
    }

    void EWrapper.updateAccountValue(string key, string value, string currency, string accountName)
    {
       
    }

    void EWrapper.updatePortfolio(Contract contract, double position, double marketPrice, double marketValue, double averageCost, double unrealizedPNL, double realizedPNL, string accountName)
    {
       
    }

    void EWrapper.updateAccountTime(string timestamp)
    {
       
    }

    void EWrapper.accountDownloadEnd(string account)
    {
       
    }

    void EWrapper.contractDetails(int reqId, ContractDetails contractDetails)
    {
       
    }

    void EWrapper.contractDetailsEnd(int reqId)
    {
       
    }

    void EWrapper.execDetails(int reqId, Contract contract, Execution execution)
    {
       
    }

    void EWrapper.execDetailsEnd(int reqId)
    {
       
    }

    void EWrapper.commissionReport(CommissionReport commissionReport)
    {
       
    }

    void EWrapper.fundamentalData(int reqId, string data)
    {
       
    }

    void EWrapper.historicalData(int reqId, Bar bar)
    {
       
    }

    void EWrapper.historicalDataUpdate(int reqId, Bar bar)
    {
       
    }

    void EWrapper.historicalDataEnd(int reqId, string start, string end)
    {
       
    }

    void EWrapper.updateMktDepth(int tickerId, int position, int operation, int side, double price, int size)
    {
       
    }

    void EWrapper.updateMktDepthL2(int tickerId, int position, string marketMaker, int operation, int side, double price, int size)
    {
       
    }

    void EWrapper.updateNewsBulletin(int msgId, int msgType, string message, string origExchange)
    {
       
    }

    void EWrapper.realtimeBar(int reqId, long time, double open, double high, double low, double close, long volume, double WAP, int count)
    {
       
    }

    void EWrapper.scannerParameters(string xml)
    {
       
    }

    void EWrapper.scannerData(int reqId, int rank, ContractDetails contractDetails, string distance, string benchmark, string projection, string legsStr)
    {
       
    }

    void EWrapper.scannerDataEnd(int reqId)
    {
       
    }

    void EWrapper.receiveFA(int faDataType, string faXmlData)
    {
       
    }

    void EWrapper.verifyMessageAPI(string apiData)
    {
       
    }

    void EWrapper.verifyCompleted(bool isSuccessful, string errorText)
    {
       
    }

    void EWrapper.verifyAndAuthMessageAPI(string apiData, string xyzChallenge)
    {
       
    }

    void EWrapper.verifyAndAuthCompleted(bool isSuccessful, string errorText)
    {
       
    }

    void EWrapper.displayGroupList(int reqId, string groups)
    {
       
    }

    void EWrapper.displayGroupUpdated(int reqId, string contractInfo)
    {
       
    }

    void EWrapper.positionMulti(int requestId, string account, string modelCode, Contract contract, double pos, double avgCost)
    {
       
    }

    void EWrapper.positionMultiEnd(int requestId)
    {
       
    }

    void EWrapper.accountUpdateMulti(int requestId, string account, string modelCode, string key, string value, string currency)
    {
       
    }

    void EWrapper.accountUpdateMultiEnd(int requestId)
    {
       
    }

    void EWrapper.securityDefinitionOptionParameter(int reqId, string exchange, int underlyingConId, string tradingClass, string multiplier, HashSet<string> expirations, HashSet<double> strikes)
    {
       
    }

    void EWrapper.securityDefinitionOptionParameterEnd(int reqId)
    {
       
    }

    void EWrapper.softDollarTiers(int reqId, SoftDollarTier[] tiers)
    {
       
    }

    void EWrapper.familyCodes(FamilyCode[] familyCodes)
    {
       
    }

    void EWrapper.symbolSamples(int reqId, ContractDescription[] contractDescriptions)
    {
       
    }

    void EWrapper.mktDepthExchanges(DepthMktDataDescription[] depthMktDataDescriptions)
    {
       
    }

    void EWrapper.tickNews(int tickerId, long timeStamp, string providerCode, string articleId, string headline, string extraData)
    {
       
    }

    void EWrapper.smartComponents(int reqId, Dictionary<int, KeyValuePair<string, char>> theMap)
    {
       
    }

    void EWrapper.tickReqParams(int tickerId, double minTick, string bboExchange, int snapshotPermissions)
    {
       
    }

    void EWrapper.newsProviders(NewsProvider[] newsProviders)
    {
       
    }

    void EWrapper.newsArticle(int requestId, int articleType, string articleText)
    {
       
    }

    void EWrapper.historicalNews(int requestId, string time, string providerCode, string articleId, string headline)
    {
       
    }

    void EWrapper.historicalNewsEnd(int requestId, bool hasMore)
    {
       
    }

    void EWrapper.headTimestamp(int reqId, string headTimestamp)
    {
       
    }

    void EWrapper.histogramData(int reqId, HistogramEntry[] data)
    {
       
    }

    void EWrapper.rerouteMktDataReq(int reqId, int conId, string exchange)
    {
       
    }

    void EWrapper.rerouteMktDepthReq(int reqId, int conId, string exchange)
    {
       
    }

    void EWrapper.marketRule(int marketRuleId, PriceIncrement[] priceIncrements)
    {
       
    }

    void EWrapper.pnl(int reqId, double dailyPnL, double unrealizedPnL, double realizedPnL)
    {
       
    }

    void EWrapper.pnlSingle(int reqId, int pos, double dailyPnL, double unrealizedPnL, double realizedPnL, double value)
    {
       
    }

    void EWrapper.historicalTicks(int reqId, HistoricalTick[] ticks, bool done)
    {
       
    }

    void EWrapper.historicalTicksBidAsk(int reqId, HistoricalTickBidAsk[] ticks, bool done)
    {
       
    }

    void EWrapper.historicalTicksLast(int reqId, HistoricalTickLast[] ticks, bool done)
    {
       
    }

    void EWrapper.tickByTickAllLast(int reqId, int tickType, long time, double price, int size, TickAttrib attribs, string exchange, string specialConditions)
    {
       
    }

    void EWrapper.tickByTickBidAsk(int reqId, long time, double bidPrice, double askPrice, int bidSize, int askSize, TickAttrib attribs)
    {
       
    }

    void EWrapper.tickByTickMidPoint(int reqId, long time, double midPoint)
    {
       
    }

    // (rest unchanged)
}
