using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace TradeBotConsol.Core
{
    public class Enums
    {
        public enum TradeSide { Buy, Sell }
        public enum MarketRegime { Bullish, Neutral, Bearish }
        public enum OrderLifeState
        {
            Submitted,
            PartiallyFilled,
            Filled,
            Cancelled,
            Rejected
        }
    }
}
