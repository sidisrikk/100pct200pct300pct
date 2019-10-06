using System;
using System.Linq;
using cAlgo.API;
using cAlgo.API.Indicators;
using cAlgo.API.Internals;
using cAlgo.Indicators;


// TODO flex height each level by volatolity
namespace cAlgo.Robots
{
    [Robot(TimeZone = TimeZones.UTC, AccessRights = AccessRights.None)]
    public class GRIDPASSIVE : Robot
    {
        [Parameter("Position Size (pip)", DefaultValue = 0.01, MaxValue = 1, Group = "Position")]
        public double PositionSize { get; set; }
        [Parameter("Pip Step", DefaultValue = 10, MinValue = 10, Group = "Position")]
        public double PipStep { get; set; }
        [Parameter("Take Profit (ratio pip step)", DefaultValue = 1.0, MinValue = 0.1, Group = "Position")]
        public double TpRatio { get; set; }

        [Parameter("Grid Upper Bound", DefaultValue = 0.0, Group = "Grid Spec")]
        public double UpperBound { get; set; }
        [Parameter("Max Position", DefaultValue = 50, MinValue = 1, Group = "Grid Spec")]
        public int MaxPosition { get; set; }



        [Parameter("period", DefaultValue = 9, Group = "Open Position Signal")]
        public int hvPeriod { get; set; }
        [Parameter("bar history", DefaultValue = 200, Group = "Open Position Signal")]
        public int hvBarHistory { get; set; }

        int[] Inventory;
        Random RAND = new Random();
        double prevPrice = -1;
        double nowPrice = -1;
        double LowerBound;

        HistoricalVolatility hv;

        protected override void OnStart()
        {

            hv = Indicators.HistoricalVolatility(MarketSeries.Close, hvPeriod, hvBarHistory);

            LowerBound = UpperBound - MaxPosition * PipStep * Symbol.PipSize;

            Positions.Closed += PositionsOnClosed;
            DrawGridLine(UpperBound);
            // use index as zone +1
            Inventory = new int[MaxPosition + 1];

        }

        private void PositionsOnClosed(PositionClosedEventArgs args)
        {
            var position = args.Position;
            //Print("Position closed with {0} profit", position.GrossProfit);
            var zoneth = Int32.Parse((position.Label.Split('_')[1]));
            var counter_skip = Int32.Parse((position.Label.Split('_')[2]));

            for (int i = 0; i < counter_skip; i++)
            {
                Inventory[zoneth + i] = 0;
            }
        }

        protected override void OnBar()
        {
            prevPrice = nowPrice;
            nowPrice = Symbol.Ask;
            if (prevPrice == -1)
                return;

            int zoneth = calculateInventoryZone(nowPrice);
            if (IsReachMaxPositionPerZone(zoneth) || zoneth == -1)
                return;


            if (CheckPricePassMidZone(nowPrice, prevPrice, zoneth))
            {
                bool IsNotHighVolatility = (hv.Result.IsFalling()) ? true : false;
                if (!IsNotHighVolatility)
                {
                    // mark counter skip
                    if (Inventory[zoneth] == 0)
                    {
                        Inventory[zoneth] = -1;
                    }
                    return;
                }

                // mark now collection
                Inventory[zoneth] = 1;
                // mark skipped collection
                int count_skip = 1;
                int zoneth_tmp = zoneth + 1;
                while (Inventory[zoneth_tmp] == -1)
                {
                    count_skip++;
                    if (Inventory[zoneth_tmp] == -1)
                        Inventory[zoneth_tmp] = 1;
                    zoneth_tmp++;
                }

                ExecuteMarketOrder(TradeType.Buy, SymbolName, Symbol.QuantityToVolumeInUnits(PositionSize) * count_skip, "posInZone_" + zoneth + "_" + count_skip, 100000, PipStep * TpRatio);
                Print("collect position at zone ", zoneth, " x", count_skip);
                // debug                    
                //var randomName = "tickPassMiddleGrid_" + RAND.NextDouble().ToString() + RAND.NextDouble().ToString();
                //Chart.DrawVerticalLine(randomName, Server.Time, Color.Red);
                //Chart.DrawHorizontalLine(randomName, nowPrice, Color.Goldenrod, 3, LineStyle.DotsVeryRare);
            }
        }

        protected override void OnStop()
        {
        }

        void DrawGridLine(double UpperBound)
        {
            for (int i = 0; i < MaxPosition + 1; i++)
            {
                var yAxis = UpperBound - i * PipStep * Symbol.PipSize;
                Chart.DrawHorizontalLine("grid_" + i, yAxis, Color.Gray);
            }
        }

        bool CheckPricePassMidZone(double currentPrice, double prevPrice, int zoneth)
        {
            if (currentPrice > UpperBound || currentPrice < LowerBound)
                return false;
            var targetPrice = middlePriceThisZone(zoneth);
            if (currentPrice > targetPrice && targetPrice > prevPrice)
                return true;
            if (currentPrice < targetPrice && targetPrice < prevPrice)
                return true;
            return false;
        }

        int calculateInventoryZone(double price)
        {
            int zoneth = 0;

            while ((UpperBound - PipStep * zoneth * Symbol.PipSize) > price)
            {
                zoneth++;
            }
            //out of zone
            if (zoneth > MaxPosition)
                zoneth = -1;
            return zoneth;
        }

        double middlePriceThisZone(int zoneth)
        {
            return UpperBound - (PipStep * (zoneth - 0.5) * Symbol.PipSize);
        }

        bool IsReachMaxPositionPerZone(int zoneth)
        {
            if (zoneth < 1 || zoneth > MaxPosition)
                return true;
            if (Inventory[zoneth] < 1)
                return false;
            else
                return true;
        }
    }
}
