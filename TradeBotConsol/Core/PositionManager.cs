using System;
using System.Collections.Generic;



public class PositionManager
{
    // All positions keyed by symbol
    public Dictionary<string, SimPosition> Positions { get; } = new();

    // Get existing position or create a new one
    public SimPosition GetOrCreate(string symbol)
    {
        if (!Positions.ContainsKey(symbol))
            Positions[symbol] = new SimPosition();
        return Positions[symbol];
    }

    // Update PnL for all positions based on current market prices
    public void MarkToMarket(Dictionary<string, decimal> marketPrices)
    {
        foreach (var kv in Positions)
        {
            var symbol = kv.Key;
            var pos = kv.Value;

            if (marketPrices.ContainsKey(symbol))
            {

                            pos.CurrentPrice = marketPrices[symbol];

            }
        }
    }

    public decimal TotalRealizedPnL => Positions.Values.Sum(p => p.RealizedPnL);

    public void PrintPositions()
    {
        foreach (var kv in Positions)
        {
            var sym = kv.Key;
            var pos = kv.Value;
            Console.WriteLine($"Symbol: {sym}, Qty: {pos.Quantity}, AvgPrice: {pos.AvgPrice:0.00}, UnrealizedPnL: {pos.UnrealizedPnL:0.00}, RealizedPnL: {pos.RealizedPnL:0.00}");
        }
        Console.WriteLine($"Total Realized PnL: {TotalRealizedPnL:0.00}");
    }
}
