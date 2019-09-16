using System;
using System.Linq;
using cAlgo.API;
using cAlgo.API.Indicators;
using cAlgo.API.Internals;
using cAlgo.Indicators;

namespace cAlgo.Robots
{
    [Robot(TimeZone = TimeZones.UTC, AccessRights = AccessRights.None)]
    public class GRIDPASSIVE : Robot
    {
        [Parameter("Initial Volumn (pip)", DefaultValue = 0.01, MaxValue = 1)]
        public double InitialPip { get; set; }

        [Parameter("Pip Step", DefaultValue = 20, MinValue = 20)]
        public double PipStep { get; set; }

        [Parameter("Max Position", DefaultValue = 50, MinValue = 1)]
        public int MaxPosition { get; set; }

        [Parameter("Grid Upper Bound", DefaultValue = 0.0)]
        public double UpperBound { get; set; }

        int[] Inventory;
        Random RAND = new Random();
        double prevPrice = -1;
        double nowPrice = -1;
        double LowerBound;
        protected override void OnStart()
        {
            Positions.Closed += PositionsOnClosed;
            DrawGridLine(UpperBound);
            Inventory = new int[MaxPosition + 1];
            LowerBound = UpperBound - MaxPosition * PipStep * Symbol.PipSize;
        }

        private void PositionsOnClosed(PositionClosedEventArgs args)
        {
            var position = args.Position;
            Print("Position closed with {0} profit", position.GrossProfit);
            var zoneth = Int32.Parse((position.Label.Split('_')[1]));
            Inventory[zoneth]--;
        }

        protected override void OnTick()
        {

            //Print(prevPrice, " - ", nowPrice);
            prevPrice = nowPrice;
            nowPrice = Symbol.Bid;
            if (prevPrice == -1)
                return;

            // Inventoryeachzone

            int zoneth = calculateInventoryZone(nowPrice);
            if (!IsReachMaxPositionPerZone(zoneth))
            {

                if (CheckPricePassMidZone(nowPrice, prevPrice, zoneth))
                {
                    // debug
                    //var randomName = "tickPassMiddleGrid_" + RAND.NextDouble().ToString() + RAND.NextDouble().ToString();
                    //Chart.DrawVerticalLine(randomName, Server.Time, Color.Red);
                    //Chart.DrawHorizontalLine(randomName, nowPrice, Color.Goldenrod, 3, LineStyle.DotsVeryRare);

                    // collect position
                    ExecuteMarketOrder(TradeType.Buy, Symbol, Symbol.QuantityToVolumeInUnits(InitialPip), "posInZone_" + zoneth, 100000, PipStep);
                    Print("collect position at zone ", zoneth);
                    Inventory[zoneth]++;
                }
            }
        }

        protected override void OnStop()
        {
            // Put your deinitialization logic here
            foreach (int i in Inventory)
            {
                Print(Inventory.GetValue(i), " - ");
            }
        }

        void DrawGridLine(double UpperBound)
        {
            for (int i = 0; i < MaxPosition; i++)
            {
                Chart.DrawHorizontalLine("grid_" + i, UpperBound - i * PipStep * Symbol.PipSize, Color.Gray);
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
            return zoneth;
        }

        double middlePriceThisZone(int zoneth)
        {
            return UpperBound - (PipStep * (zoneth - 0.5) * Symbol.PipSize);
        }

        bool IsReachMaxPositionPerZone(int zoneth)
        {
            if (Inventory[zoneth] == 0)
                return false;
            else
                return true;
        }
    }
}
