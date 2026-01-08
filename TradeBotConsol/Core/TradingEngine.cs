using System;
using TradeBotConsol;

public class TradingEngine
{
    private readonly IBroker _broker;
    private readonly PositionManager _positions;
    private readonly IbClient _ib;

    public TradingEngine(IBroker broker, PositionManager positions, IbClient ib)
    {
        _broker = broker;
        _positions = positions;
        _ib = ib;

        _ib.OnPrice += OnPrice;
    }

    private void OnPrice(string symbol, decimal price)
    {
  
        _positions.MarkToMarket(new Dictionary<string, decimal> { { symbol, price } });



        // buy if flat and price ends in .00 just as a placeholder rule
        if (!_positions.Positions.ContainsKey(symbol) || _positions.Positions[symbol].Quantity == 0)
        {
            if (price % 1 == 0)
                _broker.SubmitOrder(symbol, 10, price, TradeSide.Buy);
        }
        else
        {
            var p = _positions.Positions[symbol];

            // take profit at +2%, stop at -1%
            if (price > p.AvgPrice * 1.02m)
                _broker.SubmitOrder(symbol, (int)p.Quantity, price, TradeSide.Sell);
            else if (price < p.AvgPrice * 0.99m)
                _broker.SubmitOrder(symbol, (int)p.Quantity, price, TradeSide.Sell);
        }

        Console.WriteLine($"{symbol} {price}  PnL: {_positions.TotalRealizedPnL:0.00}");
    }

    public void Start(string[] symbols)
    {
        _ib.Connect();
        foreach (var s in symbols)
            _ib.Subscribe(s);
    }
}
