using System.Collections.Concurrent;

public partial class SimulatedBroker
{
    private sealed record NwHistoryLoad(int RequestId, DateTime RequestedAtEt, List<Candle> Bars);
    private readonly ConcurrentDictionary<string, NwHistoryLoad> _nwHistoryLoads = new();
    private readonly ConcurrentDictionary<string, bool> _nwHistoryReady = new();
    private readonly ConcurrentDictionary<string, DateTime> _nwLiveThrough = new();

    public void BeginNwHistory(string symbol, int timeframeMinutes, int requestId)
    {
        lock (_lock)
        {
            string key = GetNwSeriesKey(symbol, ValidateNwTimeframe(timeframeMinutes));
            _nwHistoryReady.TryRemove(key, out _);
            _nwHistoryLoads[key] = new NwHistoryLoad(requestId, GetEasternTime(), new List<Candle>());
            _nwTouchState.TryRemove(key, out _);
        }
    }

    public void AddNwHistoryBar(int requestId, string symbol, int timeframeMinutes, DateTime time,
        decimal open, decimal high, decimal low, decimal close, long volume)
    {
        string key = GetNwSeriesKey(symbol, timeframeMinutes);
        if (!_nwHistoryLoads.TryGetValue(key, out var load) || load.RequestId != requestId) return;
        if (!GetRegularSessionNwBucket(time, timeframeMinutes).HasValue || close <= 0m) return;
        lock (load.Bars)
            load.Bars.Add(new Candle { Time = time, Open = open, High = high, Low = low, Close = close, Volume = volume });
    }

    public void CompleteNwHistory(string symbol, int timeframeMinutes, int requestId)
    {
        lock (_lock)
            CompleteNwHistoryLocked(symbol, timeframeMinutes, requestId);
    }

    private void CompleteNwHistoryLocked(string symbol, int timeframeMinutes, int requestId)
    {
        string key = GetNwSeriesKey(symbol, timeframeMinutes);
        if (!_nwHistoryLoads.TryGetValue(key, out var load) || load.RequestId != requestId) return;
        List<Candle> source;
        lock (load.Bars) source = load.Bars.OrderBy(c => c.Time).DistinctBy(c => c.Time).ToList();
        if (source.Count == 0) return;
        int sourceMinutes = timeframeMinutes == 15 ? 15 : 30;
        var aggregated = new List<Candle>();
        foreach (var group in source.GroupBy(c => GetRegularSessionNwBucket(c.Time, timeframeMinutes)!.Value))
        {
            var bars = group.OrderBy(c => c.Time).ToList();
            DateTime bucketEnd = group.Key.AddMinutes(timeframeMinutes);
            DateTime close = group.Key.Date.AddHours(16);
            if (bucketEnd > close) bucketEnd = close;
            // Don't turn a leading partial request window or missing interior
            // source bars into a complete NW candle.
            if (bars[0].Time != group.Key) continue;
            if (bars.Where((bar, i) => bar.Time != group.Key.AddMinutes(i * sourceMinutes)).Any()) continue;
            bool complete = bars[^1].Time.AddMinutes(sourceMinutes) >= bucketEnd
                && load.RequestedAtEt >= bucketEnd;
            if (!complete && bucketEnd <= GetEasternTime()) continue;
            aggregated.Add(new Candle
            {
                Time = group.Key, Open = bars[0].Open, High = bars.Max(c => c.High),
                Low = bars.Min(c => c.Low), Close = bars[^1].Close, Volume = bars.Sum(c => c.Volume)
            });
        }
        var target = _hourlyCandles.GetOrAdd(key, _ => new List<Candle>());
        lock (target)
        {
            foreach (var historical in aggregated)
            {
                var live = target.FirstOrDefault(c => c.Time == historical.Time);
                if (live != null && _nwLiveThrough.TryGetValue(key, out var through)
                    && through > load.RequestedAtEt && historical.Time <= through
                    && historical.Time.AddMinutes(timeframeMinutes) >= through)
                {
                    historical.Close = live.Close;
                    historical.High = Math.Max(historical.High, live.High);
                    historical.Low = Math.Min(historical.Low, live.Low);
                }
            }
            // A newer live bucket may not have appeared in the historical snapshot.
            foreach (var live in target.Where(c => c.Time > aggregated.LastOrDefault()?.Time))
                if (live.Time >= load.RequestedAtEt) aggregated.Add(live);
            target.Clear();
            target.AddRange(aggregated.OrderBy(c => c.Time).TakeLast(Math.Max(700, NW_LOOKBACK + 100)));
            _nwEnvelopeCache.TryRemove(key, out _);
            _nwTouchState.TryRemove(key, out _);
            _nwHistoryReady[key] = true;
        }
        _nwHistoryLoads.TryRemove(key, out _);
    }
}
