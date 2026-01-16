using IBApi;
using System;
using System.Collections.Generic;
using System.Threading;
using System.Collections.Concurrent;
using System.Linq;

public class IbClient : EWrapper, IBroker
{
    private readonly EClientSocket _client;
    private readonly EReaderSignal _signal;

    // IDs for TWS tracking
    private int _currentOrderId = -1;
    private int _currentReqId = 1000;

    // Local data caches
    private readonly Dictionary<string, long> _currentVolumeBatch = new();
    private readonly PositionManager _broker; // The shared "Brain"
    private readonly ConcurrentDictionary<int, string> _reqIdToSymbol = new();
    private readonly ConcurrentDictionary<string, decimal> _currentPriceBatch = new();

    public event Action<string, decimal> OnPrice;
    public PositionManager Broker => _broker;

    /// <summary>
    /// Unified Constructor: Forces the use of a shared PositionManager instance.
    /// This prevents the "Empty Table" issue caused by multiple broker instances.
    /// </summary>
    public IbClient(PositionManager brokerInstance)
    {
        _signal = new EReaderMonitorSignal();
        _client = new EClientSocket(this, _signal);
        _broker = brokerInstance;
        _broker.RealBroker = this; // Link back for order execution
    }

    // --- CONNECTION ---
    public void Connect(string host = "127.0.0.1", int port = 7497, int clientId = 1)
    {
        while (!_client.IsConnected())
        {
            try
            {
                Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] Attempting to connect to TWS...");
                _client.eConnect(host, port, clientId);
                if (!_client.IsConnected()) Thread.Sleep(10000);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Connection Error: {ex.Message}");
                Thread.Sleep(10000);
            }
        }

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

        // Initial Sync
        _broker.LoadState();
        _client.reqPositions();

        // Start EOD Protection Timer
        var liquidationTimer = new System.Timers.Timer(30000);
        liquidationTimer.Elapsed += (s, e) => _broker.CheckEndOfDayLiquidation();
        liquidationTimer.AutoReset = true;
        liquidationTimer.Start();
    }

    public bool IsConnected() => _client != null && _client.IsConnected();

    public void Disconnect()
    {
        if (_client != null && _client.IsConnected())
            _client.eDisconnect();
    }

    // --- MARKET DATA & UNIVERSE ---
    public void InitializeUniverse(IEnumerable<string> symbols)
    {
        Console.WriteLine("====================================================");
        _reqIdToSymbol.Clear();

        foreach (var sym in symbols)
        {
            var contract = new Contract
            {
                Symbol = sym,
                SecType = "STK",
                Currency = "USD",
                Exchange = "SMART",
                PrimaryExch = "ISLAND" // Using ISLAND (NASDAQ) helps avoid some data routing errors
            };

            // ID for History Request
            int histReqId = _currentReqId++;
            _reqIdToSymbol[histReqId] = sym;

            // ID for Live Stream (Offset by 10,000 for easy identification)
            int liveReqId = histReqId + 10000;
            _reqIdToSymbol[liveReqId] = sym;

            Console.WriteLine($"[DATA] Warmup + Stream request for {sym}...");

            // 1. Request 2 hours of history (7200 seconds)
            _client.reqHistoricalData(histReqId, contract, "", "7200 S", "1 min", "TRADES", 1, 1, false, null);

            // 2. Start Live Streaming (Ticks)
            // This ensures that as soon as the bot is done loading history, it has live prices
            _client.reqMktData(liveReqId, contract, "", false, false, null);

            Thread.Sleep(50); // Prevent Pacing Violation
        }
        Console.WriteLine("[SYSTEM] Warmup and Live Subscriptions Initialized.");
    }
    // --- IBKR CALLBACKS ---
    public void nextValidId(int orderId)
    {
        _currentOrderId = orderId;
        _client.reqMarketDataType(1); // 1 = Live, 3 = Delayed (if you don't have paid data)
        Console.WriteLine($"[SYSTEM] Handshake Complete. Next Valid Order ID: {_currentOrderId}");
    }

    public void historicalData(int reqId, Bar bar)
    {
        if (_reqIdToSymbol.TryGetValue(reqId, out string symbol))
        {
            // Add to history without triggering trade logic (it's in the past!)
            _broker.ProcessHistoricalBar(symbol, (decimal)bar.Close, bar.Volume);
        }
    }

    public void historicalDataEnd(int reqId, string start, string end)
    {
        if (_reqIdToSymbol.TryGetValue(reqId, out string symbol))
        {
            Console.WriteLine($"[DATA] Warmup complete for {symbol}. Ready for strategy.");
        }
    }

    public void tickPrice(int tickerId, int field, double price, TickAttrib attribs)
    {
        // 4 = Last, 68 = Delayed Last, 9 = Close (for index-based QQQ)
        if ((field == 4 || field == 68 || field == 9) && price > 0)
        {
            if (_reqIdToSymbol.TryGetValue(tickerId, out string symbol))
            {
                decimal decPrice = (decimal)price;
                _currentPriceBatch[symbol] = decPrice;

                // Update the shared broker's history with the newest live tick
                _broker.UpdateHistory(symbol, decPrice, 0);
                OnPrice?.Invoke(symbol, decPrice);
            }
        }
    }

    public void tickSize(int tickerId, int field, int size)
    {
        // Field 8 = Cumulative Daily Volume
        if (field == 8 && _reqIdToSymbol.TryGetValue(tickerId, out string symbol))
        {
            long newTotalVolume = (long)size;

            // Get the previous total to find the volume of THIS specific update
            _currentVolumeBatch.TryGetValue(symbol, out long lastTotalVolume);
            long tickVolume = (lastTotalVolume == 0) ? 0 : (newTotalVolume - lastTotalVolume);

            if (tickVolume > 0)
            {
                // Send the incremental volume to the Brain for Surge calculation
                _broker.UpdateHistory(symbol, _currentPriceBatch.GetValueOrDefault(symbol), tickVolume);
            }

            _currentVolumeBatch[symbol] = newTotalVolume; // Store the new total
        }
    }
    public void position(string account, Contract contract, double pos, double avgCost)
    {
        if (pos != 0) _broker.SyncExistingPosition(contract.Symbol, (decimal)pos, (decimal)avgCost);
    }

    public void positionEnd() => Console.WriteLine("Portfolio reconciliation complete.");

    // --- ORDER EXECUTION ---
    public void SubmitOrder(string symbol, int qty, decimal price, TradeSide side, double currentRsi = 0, string orderType = "LMT")
    {
        if (_currentOrderId < 0) return;

        Contract contract = new Contract { Symbol = symbol, SecType = "STK", Exchange = "SMART", Currency = "USD" };

        // Switch to LMT (Limit) to avoid "Flash Crash" fills
        // For a BUY, we set the limit slightly above current price (e.g., +0.05)
        // For a SELL, we set the limit slightly below.
        decimal limitPrice = side == TradeSide.Buy ? price * 1.002m : price * 0.998m;

        Order order = new Order
        {
            Action = side == TradeSide.Buy ? "BUY" : "SELL",
            OrderType = "LMT", // Changed from MKT to LMT
            LmtPrice = (double)Math.Round(limitPrice, 2),
            TotalQuantity = (double)qty,
            Tif = "DAY",
            OutsideRth = false // Ensure we don't trade in pre-market by accident
        };

        Console.WriteLine($"[API] Placing {order.Action} Limit Order for {symbol} at {limitPrice:F2}. OrderID: {_currentOrderId}");
        _client.placeOrder(_currentOrderId++, contract, order);
    }

    #region Error Handling
    public void error(int id, int errorCode, string errorMsg)
    {
        if (errorCode != 2104 && errorCode != 2106 && errorCode != 2158)
            Console.WriteLine($"IB {errorCode}: {errorMsg}");
    }
    public void error(Exception e) => Console.WriteLine($"Exception: {e.Message}");
    public void error(string str) => Console.WriteLine($"Error: {str}");
    public void connectionClosed() => Console.WriteLine("Connection closed.");
    #endregion

    #region Unused EWrapper
    public void connectAck() { }
    public void tickString(int tickerId, int tickType, string value) { }
    public void tickGeneric(int tickerId, int tickType, double value) { }
    public void tickOptionComputation(int tickerId, int field, double impliedVol, double delta, double optPrice, double pvDividend, double gamma, double vega, double theta, double undPrice) { }
    public void tickEFP(int tickerId, int tickType, double basisPoints, string formattedBasisPoints, double totalDividends, int holdDays, string futureExpiry, double dividendImpact, double dividendsToExpiry) { }
    public void orderStatus(int orderId, string status, double filled, double remaining, double avgFillPrice, int permId, int parentId, double lastFillPrice, int clientId, string whyHeld, double mktCapPrice) { }
    public void openOrder(int orderId, Contract contract, Order order, OrderState orderState) { }
    public void openOrderEnd() { }
    public void currentTime(long time) { }
    public void managedAccounts(string accountsList) { }
    public void accountSummary(int reqId, string account, string tag, string value, string currency) { }
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
    public void marketDataType(int reqId, int marketDataType) { }
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
    public void tickSnapshotEnd(int tickerId) { }
    public void historicalDataUpdate(int reqId, Bar bar) { }
    public void updateNewsBulletin(int msgId, int msgType, string message, string origExchange) { }
    public void receiveFA(int faDataType, string faXmlData) { }
    public void securityDefinitionOptionParameter(int reqId, string exchange, int underlyingConId, string tradingClass, string multiplier, HashSet<string> expirations, HashSet<double> strikes) { }
    public void securityDefinitionOptionParameterEnd(int reqId) { }
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
    public void historicalTicks(int reqId, HistoricalTick[] ticks, bool done) { }
    public void historicalTicksBidAsk(int reqId, HistoricalTickBidAsk[] ticks, bool done) { }
    public void historicalTicksLast(int reqId, HistoricalTickLast[] ticks, bool done) { }
    public void tickByTickMidPoint(int reqId, long time, double midPoint) { }
    public void deltaNeutralValidation(int reqId, UnderComp underComp) { }
    public void commissionReport(CommissionReport commissionReport) { }
    public void updateMktDepth(int tickerId, int position, int operation, int side, double price, int size) { }
    public void updateMktDepthL2(int tickerId, int position, string marketMaker, int operation, int side, double price, int size) { }
    public void pnlSingle(int reqId, int pos, double dailyPnL, double unrealizedPnL, double realizedPnL, double value) { }
    public void tickByTickAllLast(int reqId, int tickType, long time, double price, int size, TickAttrib attribs, string exchange, string specialConditions) { }
    public void tickByTickBidAsk(int reqId, long time, double bidPrice, double askPrice, int bidSize, int askSize, TickAttrib attribs) { }
    #endregion
}