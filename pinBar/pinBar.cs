using System;
using cAlgo.API;
using cAlgo.API.Internals;
using cAlgo.API.Indicators;
using cAlgo.Indicators;

namespace cAlgo
{
    [Indicator(IsOverlay = false, TimeZone = TimeZones.UTC, AccessRights = AccessRights.None)]
    public class pinbar : Indicator
    {

        [Parameter("maximum body/fullrange ratio", Group = "Pinbar Spec", DefaultValue = 0.24, MaxValue = 0.3, MinValue = 0.01)]
        public double ratioBF { get; set; }

        [Parameter("maximum nose/tail ratio", Group = "Pinbar Spec", DefaultValue = 0.4, MaxValue = 0.5, MinValue = 0.01)]
        public double ratioNT { get; set; }

        [Parameter("offset (% chart area)", Group = "Display", DefaultValue = 4, MinValue = 1, MaxValue = 20)]
        public double offsetPinbarPosition { get; set; }

        [Parameter("divergence", Group = "Filter", DefaultValue = true)]
        public bool filterDivergence { get; set; }

        [Parameter("volumn falling", Group = "Filter", DefaultValue = true)]
        public bool filterVolumeRising { get; set; }

        [Output("Main")]
        public IndicatorDataSeries Result { get; set; }


        private Random random = new Random();
        private double fullRangeHeight;
        private double bodyHeight;
        private double ratio_body_range;
        private double autoPinbarOffset;


        protected override void Initialize()
        {
        }

        public override void Calculate(int index)
        {

            // part 1 check body/fullrange
            fullRangeHeight = abs(MarketSeries.High.LastValue - MarketSeries.Low.LastValue);
            bodyHeight = abs(MarketSeries.Close.LastValue - MarketSeries.Open.LastValue);
            ratio_body_range = bodyHeight / fullRangeHeight;
            if (ratio_body_range > ratioBF)
            {
                Result[index] = 0;
                return;
            }

            // part 2 check position            
            double[] arr = new double[] 
            {
                MarketSeries.High.LastValue,
                MarketSeries.Close.LastValue,
                MarketSeries.Open.LastValue,
                MarketSeries.Low.LastValue
            };
            Array.Sort(arr);
            Array.Reverse(arr);
            if (abs(arr[0] - arr[1]) / abs(arr[2] - arr[3]) > ratioNT && abs(arr[0] - arr[1]) / abs(arr[2] - arr[3]) < 1 / ratioNT)
            {
                Result[index] = 0;
                return;
            }

            // part 3 check convergence
            if (filterDivergence)
            {
                if (abs(arr[0] - arr[1]) > abs(arr[2] - arr[3]) && MarketSeries.Open.LastValue < MarketSeries.Close.LastValue)
                {
                    Result[index] = 0;
                    return;
                }
                if (abs(arr[0] - arr[1]) < abs(arr[2] - arr[3]) && MarketSeries.Open.LastValue > MarketSeries.Close.LastValue)
                {
                    Result[index] = 0;
                    return;
                }
            }


            // part 4 check volumn
            if (filterVolumeRising)
            {
                // allow maximum 5% falling
                if (MarketSeries.TickVolume.IsFalling() && MarketSeries.TickVolume.LastValue / MarketSeries.TickVolume.Last(1) < 0.95)
                    return;
            }

            autoPinbarOffset = (Chart.TopY - Chart.BottomY) * offsetPinbarPosition / 100;
            if (MarketSeries.Open.LastValue > (MarketSeries.Open.LastValue + MarketSeries.Close.LastValue) / 2)
            {
                Chart.DrawIcon("Bearish pinbar" + random.Next(0, 1000000000), ChartIconType.DownArrow, index, MarketSeries.High.LastValue + autoPinbarOffset, Color.Azure);
            }
            else
            {
                Chart.DrawIcon("Bullish pinbar" + random.Next(0, 1000000000), ChartIconType.UpArrow, index, MarketSeries.Low.LastValue - autoPinbarOffset, Color.Azure);
            }
            Result[index] = 1;

        }

        public double abs(double x)
        {
            return Math.Abs(x);
        }
    }
}
