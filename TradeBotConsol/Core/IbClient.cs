using IBApi;
using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

public class IbClient : EWrapper
{
    private readonly EClientSocket _client;
    private readonly EReaderSignal _signal;
    private int _nextReqId = 1;

    private readonly Dictionary<int, string> _reqIdToSymbol = new();
    private readonly Dictionary<string, decimal> _latestPrices = new();

    public event Action<string, decimal> OnPrice;
    public event Action<Dictionary<string, decimal>> OnPriceBatch;
    public SimulatedBroker Broker => _broker;
    public SimulatedBroker GetBroker()
    {
        return _broker;
    }
    public IbClient()
    {
        _signal = new EReaderMonitorSignal();
        _client = new EClientSocket(this, _signal);
    }

    public void Connect(string host = "127.0.0.1", int port = 7497, int clientId = 1)
    {
        // 1. RETRY LOOP
        while (!_client.IsConnected())
        {
            try
            {
                Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] Attempting to connect to TWS...");
                _client.eConnect(host, port, clientId);

                if (!_client.IsConnected())
                {
                    Thread.Sleep(10000);
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Connection Error: {ex.Message}");
                Thread.Sleep(10000);
            }
        }

        // 2. INITIALIZE READER
        var reader = new EReader(_client, _signal);
        reader.Start();

        new Thread(() => {
            while (_client.IsConnected())
            {
                _signal.waitForSignal();
                reader.processMsgs();
            }
            Console.WriteLine("\n[CRITICAL] IBKR Connection Lost!");
        })
        { IsBackground = true }.Start();

        // 3. STARTUP NOTIFICATIONS
        Console.WriteLine("Connected! Sending Startup Email...");
        _broker.SendEmailSummary("uygargunay@gmail.com"); 

        _client.reqAccountSummary(9001, "All", "NetLiquidation,AvailableFunds");
        _client.reqMarketDataType(4); // Ensure delayed data works for SPY

        // 4. LIQUIDATION TIMER
        var liquidationTimer = new System.Timers.Timer(30000);
        liquidationTimer.Elapsed += (s, e) => {
            if (_broker.IsInTradingWindow())
            {
                var eodTrades = _broker.CheckEndOfDayLiquidation();
                foreach (var trade in eodTrades)
                {
                    ExecuteRealTrade(trade);
                    Console.WriteLine($"[EXIT] Liquidated {trade.Symbol} for EOD.");
                }
            }
        };
        liquidationTimer.AutoReset = true;
        liquidationTimer.Start();

        Console.WriteLine("===============================================");
        Console.WriteLine("   SYSTEM READY FOR OVERNIGHT OPERATION");
        Console.WriteLine("===============================================");
    }
    public void nextValidId(int orderId)
    {
        _nextReqId = orderId;
        Console.WriteLine($"Connected. Next Valid ID: {orderId}");

        // Request delayed data (Type 1) 
        _client.reqMarketDataType(4);

        // --- Market Benchmark (Required for the Regime Filter) ---
        Subscribe("SPY");
        Subscribe("QQQ");
        // --- Trading Universe ---
        Subscribe("AAPL");
        Subscribe("GOOG");
        Subscribe("PLTR");
        Subscribe("RKLB");
        Subscribe("NVDA");
        Subscribe("TSLA");
        Subscribe("AMD");
        Subscribe("MSFT");
    }

    // Inside IbClient class
    private readonly Dictionary<string, decimal> _currentBatch = new();
    private readonly SimulatedBroker _broker = new(); //  logic class

    public void tickPrice(int tickerId, int field, double price, TickAttrib attribs)
    {
        // Capture Last or Delayed Last prices
        if ((field == 4 || field == 68) && price > 0)
        {
            if (_reqIdToSymbol.TryGetValue(tickerId, out string symbol))
            {
                _currentBatch[symbol] = (decimal)price;

                // Once we have a few prices, run the logic
                if (_currentBatch.Count >= 1)
                {
                    var trades = _broker.OnPriceUpdate(_currentBatch);
                    foreach (var trade in trades)
                    {
                        // Call the REAL execution method
                        ExecuteRealTrade(trade);
                    }
                }
            }
        }
    }
    public void LoadExistingPositions()
    {
        Console.WriteLine("Requesting current positions from IBKR...");
        _client.reqPositions();
    }


    private void ExecuteRealTrade(Trade trade)
    {
        Contract contract = new Contract
        {
            Symbol = trade.Symbol,
            SecType = "STK",
            Exchange = "SMART",
            Currency = "USD",
            PrimaryExch = "ISLAND"
        };

        Order order = new Order
        {
            Action = trade.Action == TradeSide.Buy ? "BUY" : "SELL",
            OrderType = "MKT", // Market order for immediate fill
            TotalQuantity = (double)trade.Quantity,
            Tif = "GTC"
        };

        int orderId = _nextReqId++;
        _client.placeOrder(orderId, contract, order);

        Console.WriteLine($"[REAL TRADE] {order.Action} {order.TotalQuantity} {trade.Symbol} sent to IBKR.");
    }

    public void Subscribe(string symbol)
    {
        var contract = new Contract
        {
            Symbol = symbol,
            SecType = "STK",
            Currency = "USD",
            Exchange = "SMART",
            PrimaryExch = "ISLAND"
        };

        int reqId = _nextReqId++;
        _reqIdToSymbol[reqId] = symbol;

        // "233" forces RTVolume and helps wake up the stream
        _client.reqMktData(reqId, contract, "233", false, false, null);
        Console.WriteLine($"Subscribed to {symbol} with reqId {reqId}");
    }

    public bool IsConnected() => _client.IsConnected();

    public void Disconnect()
    {
        if (_client.IsConnected())
            _client.eDisconnect();
    }
    public void StartStartupSequence()
    {
        _broker.LoadState(); // 1. Load 
        _client.reqPositions(); // 2. Ask IBKR what  ACTUALLY have
    }

    // EWrapper Method
    public void position(string account, Contract contract, double pos, double avgCost)
    {
        if (pos == 0) return;

        // If IBKR says  have it, but  bot doesn't know about it:
        if (!_broker.Positions.ContainsKey(contract.Symbol))
        {
            Console.WriteLine($"[ALERT] Found untracked position: {contract.Symbol}. Adding to bot.");
            _broker.SyncExistingPosition(contract.Symbol, (decimal)pos, (decimal)avgCost);
        }
    }

    public void positionEnd()
    {
        Console.WriteLine("Portfolio reconciliation complete.");
    }
    #region EWrapper Requirements (Cleaned)

    public void error(int id, int errorCode, string errorMsg) => Console.WriteLine($"IB Error {errorCode}: {errorMsg}");
    public void error(Exception e) => Console.WriteLine($"Exception: {e.Message}");
    public void error(string str) => Console.WriteLine($"Error Message: {str}");
    public void connectionClosed() => Console.WriteLine("Connection closed.");
    public void connectAck() { }
    public void tickSize(int tickerId, int field, int size) { }
    public void tickString(int tickerId, int tickType, string value) { }
    public void tickGeneric(int tickerId, int tickType, double value) { }
    public void tickOptionComputation(int tickerId, int field, double impliedVol, double delta, double optPrice, double pvDividend, double gamma, double vega, double theta, double undPrice) { }
    public void tickEFP(int tickerId, int tickType, double basisPoints, string formattedBasisPoints, double totalDividends, int holdDays, string futureExpiry, double dividendImpact, double dividendsToExpiry) { }
    public void orderStatus(int orderId, string status, double filled, double remaining, double avgFillPrice, int permId, int parentId, double lastFillPrice, int clientId, string whyHeld, double mktCapPrice) { }
    public void openOrder(int orderId, Contract contract, Order order, OrderState orderState) { }
    public void openOrderEnd() { }
    public void currentTime(long time) { }
    public void managedAccounts(string accountsList) { }

public void accountSummary(int reqId, string account, string tag, string value, string currency)
    {
        if (tag == "AvailableFunds")
        {
            decimal cash = decimal.Parse(value);
            Console.WriteLine($"Available Cash: {cash}");
           
        }
    }
    public void accountSummaryEnd(int reqId) { }
    public void updateAccountValue(string key, string value, string currency, string accountName) { }
    public void updatePortfolio(Contract contract, double position, double marketPrice, double marketValue, double averageCost, double unrealizedPNL, double realizedPNL, string accountName) { }
    public void updateAccountTime(string timestamp) { }
    public void accountDownloadEnd(string account) { }
    public void contractDetails(int reqId, ContractDetails contractDetails) { }
    public void contractDetailsEnd(int reqId) { }
    public void execDetails(int reqId, Contract contract, Execution execution) { }
    public void execDetailsEnd(int reqId) { }
    public void fundamentalData(int reqId, string data) { }
    public void historicalData(int reqId, Bar bar) { }
    public void historicalDataEnd(int reqId, string start, string end) { }
    public void marketDataType(int reqId, int marketDataType)
    {
   

        string status = (marketDataType == 1) ? "LIVE" : "DELAYED";
        Console.WriteLine($"[DATA] Stream Status Update: Request {reqId} is now {status}");

   
    }
    public void verifyMessageAPI(string apiData) { }
    public void verifyCompleted(bool isSuccessful, string errorText) { }
    public void verifyAndAuthMessageAPI(string apiData, string xyzChallenge) { }
    public void verifyAndAuthCompleted(bool isSuccessful, string errorText) { }
    public void displayGroupList(int reqId, string groups) { }
    public void displayGroupUpdated(int reqId, string contractInfo) { }
    public void positionMulti(int reqId, string account, string modelCode, Contract contract, double pos, double avgCost) { }
    public void positionMultiEnd(int reqId) { }
    public void accountUpdateMulti(int reqId, string account, string modelCode, string key, string value, string currency) { }
    public void accountUpdateMultiEnd(int reqId) { }
    public void securityDefinitionOptionalParameter(int reqId, string exchange, int underlyingConId, string tradingClass, string multiplier, HashSet<string> expirations, HashSet<double> strikes) { }
    public void securityDefinitionOptionalParameterEnd(int reqId) { }
    public void softDollarTiers(int reqId, SoftDollarTier[] tiers) { }
    public void scannerParameters(string xml) { }
    public void scannerData(int reqId, int rank, ContractDetails contractDetails, string distance, string benchmark, string projection, string legsStr) { }
    public void scannerDataEnd(int reqId) { }
    public void realtimeBar(int reqId, long time, double open, double high, double low, double close, long volume, double wap, int count) { }
    public void bondContractDetails(int reqId, ContractDetails contractDetails) { }

    // Fixed: Replaced throw with empty bodies
    void EWrapper.tickSnapshotEnd(int tickerId) { }
    void EWrapper.historicalDataUpdate(int reqId, Bar bar) { }
    void EWrapper.updateNewsBulletin(int msgId, int msgType, string message, string origExchange) { }
    void EWrapper.receiveFA(int faDataType, string faXmlData) { }
    void EWrapper.securityDefinitionOptionParameter(int reqId, string exchange, int underlyingConId, string tradingClass, string multiplier, HashSet<string> expirations, HashSet<double> strikes) { }
    void EWrapper.securityDefinitionOptionParameterEnd(int reqId) { }
    void EWrapper.familyCodes(FamilyCode[] familyCodes) { }
    void EWrapper.symbolSamples(int reqId, ContractDescription[] contractDescriptions) { }
    void EWrapper.mktDepthExchanges(DepthMktDataDescription[] depthMktDataDescriptions) { }
    void EWrapper.tickNews(int tickerId, long timeStamp, string providerCode, string articleId, string headline, string extraData) { }
    void EWrapper.smartComponents(int reqId, Dictionary<int, KeyValuePair<string, char>> theMap) { }
    void EWrapper.tickReqParams(int tickerId, double minTick, string bboExchange, int snapshotPermissions) { }
    void EWrapper.newsProviders(NewsProvider[] newsProviders) { }
    void EWrapper.newsArticle(int requestId, int articleType, string articleText) { }
    void EWrapper.historicalNews(int requestId, string time, string providerCode, string articleId, string headline) { }
    void EWrapper.historicalNewsEnd(int requestId, bool hasMore) { }
    void EWrapper.headTimestamp(int reqId, string headTimestamp) { }
    void EWrapper.histogramData(int reqId, HistogramEntry[] data) { }
    void EWrapper.rerouteMktDataReq(int reqId, int conId, string exchange) { }
    void EWrapper.rerouteMktDepthReq(int reqId, int conId, string exchange) { }
    void EWrapper.marketRule(int marketRuleId, PriceIncrement[] priceIncrements) { }
    void EWrapper.pnl(int reqId, double dailyPnL, double unrealizedPnL, double realizedPnL) { }
    void EWrapper.historicalTicks(int reqId, HistoricalTick[] ticks, bool done) { }
    void EWrapper.historicalTicksBidAsk(int reqId, HistoricalTickBidAsk[] ticks, bool done) { }
    void EWrapper.historicalTicksLast(int reqId, HistoricalTickLast[] ticks, bool done) { }
    void EWrapper.tickByTickMidPoint(int reqId, long time, double midPoint) { }
    void EWrapper.deltaNeutralValidation(int reqId, UnderComp underComp) { }
    void EWrapper.commissionReport(CommissionReport commissionReport) { }
    void EWrapper.updateMktDepth(int tickerId, int position, int operation, int side, double price, int size) { }
    void EWrapper.updateMktDepthL2(int tickerId, int position, string marketMaker, int operation, int side, double price, int size) { }
    void EWrapper.pnlSingle(int reqId, int pos, double dailyPnL, double unrealizedPnL, double realizedPnL, double value) { }
    void EWrapper.tickByTickAllLast(int reqId, int tickType, long time, double price, int size, TickAttrib attribs, string exchange, string specialConditions) { }
    void EWrapper.tickByTickBidAsk(int reqId, long time, double bidPrice, double askPrice, int bidSize, int askSize, TickAttrib attribs) { }

    #endregion
}