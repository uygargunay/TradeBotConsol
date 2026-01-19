using IBApi;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using static SimulatedBroker;

public class IbClient : EWrapper, IBroker
{
    private readonly EClientSocket _client;
    private readonly EReaderSignal _signal;

    // IDs for TWS tracking
    private int _currentOrderId = -1;
    private int _currentReqId = 1000;

    // Local data caches
    private readonly Dictionary<string, long> _currentVolumeBatch = new();
    private readonly PositionManager _broker; // Shared brain
    private readonly ConcurrentDictionary<int, string> _reqIdToSymbol = new();
    private readonly ConcurrentDictionary<string, decimal> _currentPriceBatch = new();

    public event Action<string, decimal> OnPrice;
    public PositionManager Broker => _broker;

    public IbClient(PositionManager brokerInstance)
    {
        _signal = new EReaderMonitorSignal();
        _client = new EClientSocket(this, _signal);
        _broker = brokerInstance;
        _broker.RealBroker = this;
    }

    // ───────────────── CONNECTION ─────────────────
    public void Connect(string host = "127.0.0.1", int port = 7497, int clientId = 1)
    {
        while (!_client.IsConnected())
        {
            try
            {
                Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] Connecting to TWS...");
                _client.eConnect(host, port, clientId);
                if (!_client.IsConnected()) Thread.Sleep(10000);
            }
            catch
            {
                Thread.Sleep(10000);
            }
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

        _broker.LoadState();
        _client.reqPositions();

        var liquidationTimer = new System.Timers.Timer(30000);
        liquidationTimer.Elapsed += (_, _) => _broker.CheckEndOfDayLiquidation();
        liquidationTimer.Start();
    }

    public bool IsConnected() => _client.IsConnected();
    public void Disconnect() => _client.eDisconnect();

    // ─────────────── UNIVERSE INIT ───────────────
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
                Exchange = "SMART",
                PrimaryExch = "ISLAND"
            };

            int histReqId = _currentReqId++;
            int liveReqId = histReqId + 10000;

            _reqIdToSymbol[histReqId] = sym;
            _reqIdToSymbol[liveReqId] = sym;

            _client.reqHistoricalData(histReqId, contract, "", "7200 S", "1 min", "TRADES", 1, 1, false, null);
            _client.reqMktData(liveReqId, contract, "", false, false, null);

            Thread.Sleep(75); // pacing safety
        }
    }

    // ─────────────── IB CALLBACKS ───────────────
    public void nextValidId(int orderId)
    {
        _currentOrderId = Math.Max(_currentOrderId, orderId);
        _client.reqMarketDataType(1);
        Console.WriteLine($"[SYSTEM] IB Ready. OrderID: {_currentOrderId}");
    }

    public void historicalData(int reqId, Bar bar)
    {
        if (_reqIdToSymbol.TryGetValue(reqId, out var symbol))
            _broker.ProcessHistoricalBar(symbol, (decimal)bar.Close, bar.Volume);
    }

    public void tickPrice(int tickerId, int field, double price, TickAttrib attribs)
    {
        if ((field == 4 || field == 68 || field == 9) && price > 0 &&
            _reqIdToSymbol.TryGetValue(tickerId, out var symbol))
        {
            _currentPriceBatch[symbol] = (decimal)price;
            OnPrice?.Invoke(symbol, (decimal)price);
        }
    }

    // ✅ FIXED: UpdateHistory is called ONLY here
    public void tickSize(int tickerId, int field, int size)
    {
        if (field != 8 || !_reqIdToSymbol.TryGetValue(tickerId, out var symbol))
            return;

        long newTotal = size;
        _currentVolumeBatch.TryGetValue(symbol, out long lastTotal);
        long tickVol = newTotal - lastTotal;

        if (tickVol > 0 && _currentPriceBatch.TryGetValue(symbol, out var price))
            _broker.UpdateHistory(symbol, price, tickVol);

        _currentVolumeBatch[symbol] = newTotal;
    }

    public void position(string account, Contract contract, double pos, double avgCost)
    {
        if (pos != 0)
            _broker.SyncExistingPosition(contract.Symbol, (decimal)pos, (decimal)avgCost);
    }

    public void positionEnd() => Console.WriteLine("[SYSTEM] Portfolio synced.");

    // ─────────────── ORDER EXECUTION ───────────────
    public void SubmitOrder(
        string symbol,
        int qty,
        decimal price,
        TradeSide side,
        double currentRsi = 0,
        string orderType = "LMT")
    {
        if (_currentOrderId < 0) return;

        qty = Math.Max(1, qty);

        var contract = new Contract
        {
            Symbol = symbol,
            SecType = "STK",
            Exchange = "SMART",
            Currency = "USD"
        };

        var order = new Order
        {
            Action = side == TradeSide.Buy ? "BUY" : "SELL",
            OrderType = orderType,
            TotalQuantity = qty,
            Tif = "DAY",
            OutsideRth = false
        };

        // 🔒 force market sell on halt / liquidation
        if (side == TradeSide.Sell && (_broker.IsHalted || _broker.GoalReached))
            order.OrderType = "MKT";

        if (order.OrderType == "LMT")
            order.LmtPrice = (double)Math.Round(price, 2);

        int orderId = Interlocked.Increment(ref _currentOrderId);

        Console.WriteLine($"[ORDER] {order.Action} {symbol} x{qty} ({order.OrderType}) ID={orderId}");

        // 🔑 register FIRST
        _broker.RegisterLiveOrder(orderId, symbol, side, qty);

        // 🚀 send to IB
        _client.placeOrder(orderId, contract, order);
    }

    // ─────────────── ORDER SAFETY ───────────────
    public void orderStatus(
        int orderId,
        string status,
        double filled,
        double remaining,
        double avgFillPrice,
        int permId,
        int parentId,
        double lastFillPrice,
        int clientId,
        string whyHeld,
        double mktCapPrice)
    {
        if (_broker.TryGetTrackedOrder(orderId, out var order))
        {
            if (status == "Filled")
                order.State = OrderLifeState.Filled;
            else if (filled > 0)
                order.State = OrderLifeState.PartiallyFilled;
            else if (status == "Cancelled")
                order.State = OrderLifeState.Cancelled;
            else if (status == "Rejected")
                order.State = OrderLifeState.Rejected;
        }

        if (filled > 0 && remaining == 0)
        {
            _broker.OnOrderFilled(orderId, (int)filled, (decimal)avgFillPrice);
        }
        else if (status == "Rejected" || status == "Cancelled")
        {
            _broker.NotifyOrderFailed(orderId, status);
        }

    }


    #region Errors
    public void error(int id, int errorCode, string errorMsg)
    {
        if (errorCode != 2104 && errorCode != 2106 && errorCode != 2158)
            Console.WriteLine($"IB {errorCode}: {errorMsg}");
    }
    public void error(Exception e) => Console.WriteLine(e.Message);
    public void error(string str) => Console.WriteLine(str);
    public void connectionClosed() => Console.WriteLine("[IB] Connection closed.");
    #endregion

    #region Unused
    public void connectAck() { }
    public void tickString(int a, int b, string c) { }
    public void tickGeneric(int a, int b, double c) { }
    public void openOrder(int a, Contract b, Order c, OrderState d) { }
    public void openOrderEnd() { }
    public void currentTime(long t) { }

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

    void EWrapper.historicalDataUpdate(int reqId, Bar bar)
    {
        
    }

    void EWrapper.historicalDataEnd(int reqId, string start, string end)
    {
        
    }

    void EWrapper.marketDataType(int reqId, int marketDataType)
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
    #endregion
}
