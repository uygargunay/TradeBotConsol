using System.Collections.Concurrent;

public partial class SimulatedBroker
{
    private sealed record DeferredExit(int Quantity, int PositionQuantity, decimal Price,
                                      TradeSide Side, string Reason, string Type);
    private readonly Dictionary<string, DeferredExit> _deferredExits = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, (int Id, int Qty, decimal Price)> _requestedNwStops = new();
    private bool _positionSnapshotComplete;
    private bool _openOrderSnapshotComplete;
    private readonly HashSet<int> _openOrderSnapshotIds = new();
    private readonly Dictionary<int, (string Symbol, TradeSide Side, int Qty, string Type, int Parent)> _openOrderSnapshotDetails = new();
    private readonly Dictionary<int, TrackedOrder> _finishedOrders = new();

    private bool HaltRequiresReview()
        => _haltReason is "FILL_MISMATCH" or "SUBMISSION_UNCERTAIN" or "UNMANAGED_ORDERS"
            or "STOP_UPDATE_REJECTED" or "CONFIG_INVALID";

    private List<PendingEntryState> CapturePendingEntries()
        => _pendingEntrySymbols.Keys.Select(symbol => new PendingEntryState
        {
            Symbol = symbol,
            Strategy = _pendingStrategyTag.GetValueOrDefault(symbol) ?? _positions.GetValueOrDefault(symbol)?.StrategyTag ?? "",
            CreatedUtc = _pendingEntryCreatedUtc.GetValueOrDefault(symbol),
            InitialRisk = _pendingInitialRisk.GetValueOrDefault(symbol),
            Audit = _pendingNwEntryAudit.GetValueOrDefault(symbol) ?? _positions.GetValueOrDefault(symbol)?.NwEntryAudit,
            StopId = _pendingBracketChildren.GetValueOrDefault(symbol).stopId,
            TargetId = _pendingBracketChildren.GetValueOrDefault(symbol).targetId
        }).ToList();

    private void RestorePendingEntries(IEnumerable<PendingEntryState> entries)
    {
        foreach (var entry in entries)
        {
            _pendingEntrySymbols[entry.Symbol] = true;
            _pendingEntryCreatedUtc[entry.Symbol] = entry.CreatedUtc;
            _pendingStrategyTag[entry.Symbol] = entry.Strategy;
            _pendingInitialRisk[entry.Symbol] = entry.InitialRisk;
            if (entry.Audit != null) _pendingNwEntryAudit[entry.Symbol] = entry.Audit;
            _pendingBracketChildren[entry.Symbol] = (entry.StopId, entry.TargetId);
        }
        _pendingEntryCount = _pendingEntrySymbols.Keys.Count(s => !_positions.ContainsKey(s));
    }

    public void OnOrderSubmissionUncertain(string symbol, string message)
    {
        lock (_lock)
        {
            _haltTrading = true;
            _haltReason = "SUBMISSION_UNCERTAIN";
            LogMessage($"[SUBMISSION UNCERTAIN] {symbol}: {message}. Tracking retained; account reconciliation required.");
            SaveState();
            RequestRereconcile();
        }
    }

    public void OnOpenOrderReceived(int id, string symbol, TradeSide side, int qty, string type, int parentId)
    {
        lock (_lock)
        {
            _openOrderSnapshotIds.Add(id);
            _openOrderSnapshotDetails[id] = (symbol, side, qty, type, parentId);
            if (!_positions.TryGetValue(symbol, out var pos))
            {
                if (_pendingEntrySymbols.ContainsKey(symbol)) RegisterLiveOrder(id, symbol, side, qty);
                return;
            }
            RegisterLiveOrder(id, symbol, side, qty);
            if (side == (pos.IsShort ? TradeSide.Sell : TradeSide.Buy)) pos.EntryOrderId = id;
            else if (type is "STP" or "STP LMT")
            {
                pos.BracketStopId = id;
                _bracketExitReasonByOrderId[id] = "BRACKET_STOP";
            }
            else if (parentId > 0)
            {
                pos.BracketTargetId = id;
                _bracketExitReasonByOrderId[id] = "BRACKET_TARGET";
            }
        }
    }

    public void OnOpenOrderSnapshotComplete()
    {
        lock (_lock) _openOrderSnapshotComplete = true;
        CompleteReconciliationIfReady();
    }

    public void OnBrokerDisconnected()
    {
        lock (_lock)
        {
            _reconciled = false;
            _needsReconciliation = true;
            _positionSnapshotComplete = false;
            _openOrderSnapshotComplete = false;
            _openOrderSnapshotIds.Clear();
            _openOrderSnapshotDetails.Clear();
            _ibkrPositionSnapshot.Clear();
            _subscribedSymbols.Clear();
            _nwTouchState.Clear();
            _hourlyCandles.Clear();
            _nwHistoryReady.Clear();
            _nwHistoryLoads.Clear();
            _nwLiveThrough.Clear();
            _nwEnvelopeCache.Clear();
            _currentMinuteCandle.Clear();
            _requestedNwStops.Clear();
        }
    }

    private static bool IsNwPosition(SimPosition pos)
        => (pos.StrategyTag ?? "").StartsWith("NW_BAND_", StringComparison.OrdinalIgnoreCase);

    private decimal GetNwStopPrice(SimPosition pos)
        => Math.Round(pos.AvgPrice * (pos.IsShort ? 1m + NW_STOP_LOSS_PCT : 1m - NW_STOP_LOSS_PCT),
                      pos.AvgPrice >= 1m ? 2 : 4, MidpointRounding.AwayFromZero);

    private void SyncNwProtectiveStop(SimPosition pos)
    {
        if (!IsNwPosition(pos) || pos.ExitSubmitted || !_reconciled || RealBroker?.IsReady != true) return;
        if (_haltReason == "STOP_UPDATE_REJECTED") return;
        // A bracket child is held while its parent is still working. Keep local
        // protection for the partial position; resize the child after completion.
        if (pos.EntryOrderId > 0 && _ordersById.ContainsKey(pos.EntryOrderId)) return;
        decimal stop = GetNwStopPrice(pos);
        var requested = (pos.BracketStopId, pos.Quantity, stop);
        if (_requestedNwStops.TryGetValue(pos.Symbol, out var previous) && previous == requested) return;
        // IBKR's TotalQuantity includes shares already filled on this order.
        int filledOnStop = _ordersById.TryGetValue(pos.BracketStopId, out var trackedStop) ? trackedStop.FilledQty : 0;
        int id = RealBroker.EnsureNwStop(pos.Symbol, pos.BracketStopId, pos.Quantity + filledOnStop,
            pos.IsShort ? TradeSide.Buy : TradeSide.Sell, stop);
        if (id <= 0) return;
        pos.BracketStopId = id;
        pos.InitialRiskPerShare = Math.Abs(pos.AvgPrice - stop);
        _bracketExitReasonByOrderId[id] = "NW_STOP_LOSS";
        _requestedNwStops[pos.Symbol] = (id, pos.Quantity, stop);
    }

    public void RegisterProtectiveStop(int id, string symbol, TradeSide side, int totalQty)
    {
        lock (_lock)
        {
            RegisterLiveOrder(id, symbol, side, totalQty);
            if (_positions.TryGetValue(symbol, out var pos)) pos.BracketStopId = id;
            _bracketExitReasonByOrderId[id] = "NW_STOP_LOSS";
            SaveState();
        }
    }

    public void OnProtectiveStopUpdateRejected(int orderId, string message)
    {
        lock (_lock)
        {
            _haltTrading = true;
            _haltReason = "STOP_UPDATE_REJECTED";
            LogMessage($"[STOP UPDATE REJECTED] order={orderId}: {message}. New entries halted; verify the working stop in TWS.");
            RequestRereconcile();
        }
    }

    private bool HasOutstandingProtection(SimPosition pos)
        => pos.BracketStopId > 0 || pos.BracketTargetId > 0
           || pos.EntryOrderId > 0 && _ordersById.ContainsKey(pos.EntryOrderId);

    private bool DeferExitUntilProtectionCancelled(string symbol, int qty, decimal price,
                                                   TradeSide side, string reason, string type)
    {
        lock (_lock)
        {
            if (!_positions.TryGetValue(symbol, out var pos)
                || side != (pos.IsShort ? TradeSide.Buy : TradeSide.Sell)) return false;
            pos.ExitSubmitted = true;
            if (!HasOutstandingProtection(pos)) return false;
            _deferredExits.TryAdd(symbol, new DeferredExit(qty, pos.Quantity, price, side, reason, type));
            CancelBracketChildren(pos);
            return true;
        }
    }

    private void ResumeDeferredExit(string symbol)
    {
        if (!_deferredExits.TryGetValue(symbol, out var exit)) return;
        if (!_positions.TryGetValue(symbol, out var pos))
        {
            _deferredExits.Remove(symbol);
            return;
        }
        if (HasOutstandingProtection(pos)) return;
        _deferredExits.Remove(symbol);
        // A stop can execute while its cancellation is in flight. Sell only the
        // remaining requested quantity, never the stale pre-cancel position size.
        int alreadyClosed = Math.Max(0, exit.PositionQuantity - pos.Quantity);
        int remaining = Math.Min(pos.Quantity, exit.Quantity - alreadyClosed);
        if (remaining > 0)
            SubmitOrder(symbol, remaining, exit.Price, exit.Side, exit.Reason, exit.Type);
        else
            pos.ExitSubmitted = false;
    }

    private void FinishTrackedOrder(TrackedOrder order)
    {
        _finishedOrders[order.OrderId] = order;
        if (_finishedOrders.Count > 1000) _finishedOrders.Remove(_finishedOrders.Keys.First());
        _ordersById.TryRemove(order.OrderId, out _);
        _bracketExitReasonByOrderId.TryRemove(order.OrderId, out _);
        if (_positions.TryGetValue(order.Symbol, out var pos))
        {
            if (pos.EntryOrderId == order.OrderId) pos.EntryOrderId = 0;
            if (pos.BracketStopId == order.OrderId) pos.BracketStopId = 0;
            if (pos.BracketTargetId == order.OrderId) pos.BracketTargetId = 0;
            if (!order.IsEntry && !_deferredExits.ContainsKey(order.Symbol)) pos.ExitSubmitted = false;
        }
        if (order.IsEntry && order.FilledQty > 0)
        {
            _pendingEntrySymbols.TryRemove(order.Symbol, out _);
            _pendingEntryCreatedUtc.TryRemove(order.Symbol, out _);
        }
        ResumeDeferredExit(order.Symbol);
    }

    public void OnOrderCancelled(int orderId)
    {
        lock (_lock)
        {
            if (!_ordersById.TryGetValue(orderId, out var order))
            {
                foreach (var pos in _positions.Values.ToList())
                {
                    if (pos.BracketStopId == orderId) pos.BracketStopId = 0;
                    if (pos.BracketTargetId == orderId) pos.BracketTargetId = 0;
                    ResumeDeferredExit(pos.Symbol);
                }
                return;
            }
            if (order.IsEntry && order.FilledQty == 0)
                ReleasePendingEntrySlot(order.Symbol, $"order {orderId} cancelled");
            FinishTrackedOrder(order);
            SaveState();
        }
    }

    public void OnOrderRejected(int orderId, int errorCode = 0, string errorMessage = "")
    {
        lock (_lock)
        {
            if (!_ordersById.TryGetValue(orderId, out var order)) return;
            if (order.IsEntry && order.FilledQty == 0)
            {
                _pendingStrategyTag.TryGetValue(order.Symbol, out var tag);
                StartEntryOrderRejectionCooldown(order.Symbol,
                    (tag ?? "").StartsWith("NW_BAND_", StringComparison.OrdinalIgnoreCase),
                    errorCode, errorMessage);
            }
            LogMessage($"[REJECTED] {order.Symbol} order={orderId} code={errorCode}: {errorMessage}");
            OnOrderCancelled(orderId);
        }
    }

    // Both orderStatus and execDetails report cumulative fills. Derive the delta
    // from cumulative notional so duplicate callbacks and different fill prices
    // cannot create extra shares or charge the modeled order commission twice.
    public void OnOrderFilled(int orderId, int cumulativeQty, decimal averagePrice, bool terminal = true)
    {
        lock (_lock)
        {
            if (!_ordersById.TryGetValue(orderId, out var order))
            {
                if (_finishedOrders.TryGetValue(orderId, out var finished) && cumulativeQty > finished.FilledQty)
                    RequireReconciliationForFill(finished, "execution arrived after terminal callback");
                return;
            }
            int fillQty = cumulativeQty - order.FilledQty;
            if (fillQty <= 0)
            {
                if (terminal) FinishTrackedOrder(order);
                if (terminal) SaveState();
                return;
            }
            if (averagePrice <= 0m) return;
            decimal totalNotional = cumulativeQty * averagePrice;
            decimal fillPrice = (totalNotional - order.FilledNotional) / fillQty;
            bool firstFill = order.FilledQty == 0;
            decimal fee = firstFill ? COMMISSION_PER_SIDE : 0m;
            order.FilledQty = cumulativeQty;
            order.FilledNotional = totalNotional;

            if (order.IsEntry)
            {
                if (!_positions.TryGetValue(order.Symbol, out var pos))
                {
                    if (!_pendingEntrySymbols.ContainsKey(order.Symbol))
                    {
                        RequireReconciliationForFill(order, "entry has no strategy reservation");
                        return;
                    }
                    _pendingStrategyTag.TryRemove(order.Symbol, out var tag);
                    _pendingInitialRisk.TryRemove(order.Symbol, out var initialRisk);
                    _pendingNwEntryAudit.TryRemove(order.Symbol, out var audit);
                    _pendingBracketChildren.TryRemove(order.Symbol, out var children);
                    _indicatorCache.TryGetValue(order.Symbol, out var ind);
                    _marketData.TryGetValue(order.Symbol, out var candles);
                    pos = new SimPosition
                    {
                        Symbol = order.Symbol, EntryOrderId = orderId,
                        IsShort = order.Side == TradeSide.Sell, StrategyTag = tag ?? "",
                        AvgPrice = fillPrice, CurrentPrice = fillPrice, HighWaterMark = fillPrice,
                        EntryTime = DateTime.UtcNow, EntryRegime = _marketRegime,
                        InitialRiskPerShare = initialRisk, NwEntryAudit = audit,
                        EntryRsi = ind?.Rsi14 ?? 0d, EntryAtr = ind?.Atr14 ?? 0m,
                        EntryVwap = _vwap.GetValueOrDefault(order.Symbol),
                        EntrySetupScore = ScoreSetup(order.Symbol, candles),
                        BracketStopId = children.stopId, BracketTargetId = children.targetId
                    };
                    _positions[order.Symbol] = pos;
                    Interlocked.Decrement(ref _pendingEntryCount);
                    _tradesToday++;
                    IncrementStrategyCount(pos.StrategyTag);
                }
                pos.AvgPrice = (pos.AvgPrice * pos.Quantity + fillPrice * fillQty) / (pos.Quantity + fillQty);
                pos.Quantity += fillQty;
                pos.EntryCommission += fee;
                _totalRealizedPnL -= fee;
            }
            else
            {
                if (!_positions.TryGetValue(order.Symbol, out var pos) || fillQty > pos.Quantity
                    || order.Side != (pos.IsShort ? TradeSide.Buy : TradeSide.Sell))
                {
                    // A fill is an account fact. Ignoring it cannot undo an
                    // accidental short; stop entries and fetch the actual account.
                    RequireReconciliationForFill(order, "exit exceeds or disagrees with the tracked position");
                    return;
                }
                decimal entryFee = pos.EntryCommission * fillQty / pos.Quantity;
                pos.EntryCommission -= entryFee;
                decimal gross = (pos.IsShort ? pos.AvgPrice - fillPrice : fillPrice - pos.AvgPrice) * fillQty;
                decimal net = gross - fee - entryFee;
                pos.RealizedExitPnL += net;
                _totalRealizedPnL += gross - fee;
                pos.Quantity -= fillQty;
                DateTime exitEt = GetEasternTime();
                if (string.IsNullOrEmpty(order.ExitReason))
                    order.ExitReason = _bracketExitReasonByOrderId.GetValueOrDefault(orderId)
                        ?? _pendingExitReasonBySymbol.GetValueOrDefault(order.Symbol) ?? "EXIT";
                lock (_allTrades)
                {
                    var record = _allTrades.LastOrDefault(t => t.FillKey == order.FillKey);
                    if (record == null)
                    {
                        record = new TradeRecord
                        {
                            FillKey = order.FillKey, Symbol = order.Symbol,
                            Side = pos.IsShort ? "SHORT" : "LONG", Strategy = pos.StrategyTag,
                            Entry = pos.AvgPrice, ExitReason = order.ExitReason,
                            EntryTime = TimeZoneInfo.ConvertTimeFromUtc(pos.EntryTime.ToUniversalTime(), Eastern).ToString("HH:mm:ss"),
                            Regime = pos.EntryRegime, EntryRsi = pos.EntryRsi, EntryAtr = pos.EntryAtr,
                            EntryVwap = pos.EntryVwap, EntrySetupScore = pos.EntrySetupScore,
                            NwEntryAudit = pos.NwEntryAudit
                        };
                        _allTrades.Add(record);
                        _completedTrades.Add(record);
                    }
                    record.Qty = cumulativeQty;
                    record.Exit = averagePrice;
                    record.NetPnL += net;
                    record.HoldMinutes = (decimal)(DateTime.UtcNow - pos.EntryTime).TotalMinutes;
                    record.Time = exitEt.ToString("HH:mm");
                    record.ExitTime = exitEt.ToString("HH:mm:ss");
                    record.Date = exitEt.ToString("yyyy-MM-dd");
                    // State and lifetime history are deserialized independently
                    // after restart. Keep the dashboard's session record current.
                    int sessionIndex = _completedTrades.FindIndex(t => t.FillKey == order.FillKey);
                    if (sessionIndex >= 0) _completedTrades[sessionIndex] = record;
                    _cachedAllTradesCount = -1;
                    if (_completedTrades.Count > 200) _completedTrades.RemoveAt(0);
                    if (_allTrades.Count > 2000) _allTrades.RemoveAt(0);
                }
                if (pos.Quantity == 0)
                {
                    bool won = pos.RealizedExitPnL > 0m;
                    if (won) { _winCount++; _consecutiveLosses = 0; }
                    else { _lossCount++; _consecutiveLosses++; }
                    if (!HaltRequiresReview() && MAX_CONSECUTIVE_LOSSES > 0 && _consecutiveLosses >= MAX_CONSECUTIVE_LOSSES)
                    { _haltTrading = true; _haltReason = "CONSECUTIVE_LOSSES"; }
                    if (pos.BracketStopId == orderId) pos.BracketStopId = 0;
                    if (pos.BracketTargetId == orderId) pos.BracketTargetId = 0;
                    CancelBracketChildren(pos);
                    _positions.Remove(order.Symbol);
                    _lastTradeTime[order.Symbol] = DateTime.UtcNow;
                    _lastTradeWasLoss[order.Symbol] = !won;
                    _deferredExits.Remove(order.Symbol);
                    _requestedNwStops.Remove(order.Symbol);
                    _pendingExitReasonBySymbol.TryRemove(order.Symbol, out _);
                }
                SaveAllTrades();
            }
            _tradeHistoryLog.Add($"[{DateTime.UtcNow:HH:mm:ss}] {order.Side} {order.Symbol} x{fillQty} @ {fillPrice:F4} (order {orderId})");
            if (_tradeHistoryLog.Count > 50) _tradeHistoryLog.RemoveAt(0);
            if (terminal) FinishTrackedOrder(order);
            if (terminal && order.IsEntry && _positions.TryGetValue(order.Symbol, out var filledPosition))
                SyncNwProtectiveStop(filledPosition);
            if (terminal)
                _ = SendEmail($"Fill: {order.Symbol} {order.Side} x{cumulativeQty}",
                    $"Order {orderId}: {cumulativeQty} shares at average {averagePrice:F4}. " +
                    $"Reason: {order.ExitReason}. Realized session PnL: {_totalRealizedPnL:F2}.");
            _equityCurve.Add((DateTime.UtcNow, _totalRealizedPnL));
            SaveEquityCurve();
            CheckDailyLimits();
            SaveState();
        }
    }

    private void RequireReconciliationForFill(TrackedOrder order, string reason)
    {
        LogMessage($"[FILL MISMATCH] {order.Symbol} order={order.OrderId}: {reason}; requesting account reconciliation.");
        _haltTrading = true;
        _haltReason = "FILL_MISMATCH";
        RequestRereconcile();
    }
}
