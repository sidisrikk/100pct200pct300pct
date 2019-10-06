using System;
using cAlgo.API;
using cAlgo.API.Internals;
using cAlgo.API.Indicators;

namespace cAlgo.Robots
{
    [Robot(TimeZone = TimeZones.UTC, AccessRights = AccessRights.None)]
    public class RecoveryZone : Robot
    {
        [Parameter("Initial Volumn (pip)", DefaultValue = 0.01, MaxValue = 0.1, Step = 0.01)]
        public double InitialPip { get; set; }

        [Parameter("Take Profit (USD)", DefaultValue = 0.1, MinValue = 0, Step = 0.1)]
        public double TakeProfit { get; set; }

        [Parameter("Zone Height (x atr)", DefaultValue = 10, Step = 1)]
        public double ZoneHeight { get; set; }

        [Parameter("atr period (x atr)", DefaultValue = 120, Step = 10)]
        public int atrPeriod { get; set; }

        [Parameter("run once ", DefaultValue = true)]
        public bool isRunOnce { get; set; }

        private TradeType EntryTradeType = TradeType.Sell;

        // Recovery Zone
        private CalPosition calPos;
        private double EntryPrice;

        // etc.
        private int InitialVolumeInUnits;

        // flag start zone recoverey
        private bool IsOnProcessZoneRecovery;

        private AverageTrueRange atr;

        protected override void OnStart()
        {
            Positions.Closed += OnPositionsClosed;
            Positions.Opened += OnPositionsOpened;

            atr = Indicators.AverageTrueRange(atrPeriod, MovingAverageType.Simple);
            InitialZoneRecovery();


        }

        private void InitialZoneRecovery()
        {
            // init first order
            InitialVolumeInUnits = (int)Symbol.QuantityToVolumeInUnits(InitialPip);
            var tmp = ExecuteMarketOrder(EntryTradeType, SymbolName, InitialVolumeInUnits, "ExeTheFirstOrder");
            // mark entry point
            EntryPrice = tmp.Position.EntryPrice;


            // recovery zone
            double adjustedHeight = atr.Result.LastValue * ZoneHeight;
            var upperBoundZone = EntryPrice + adjustedHeight;
            var lowerBoundZone = EntryPrice;
            var upperBreakout = upperBoundZone + adjustedHeight;
            var lowerBreakout = lowerBoundZone - adjustedHeight;
            Print(upperBoundZone, "-", lowerBoundZone, "-", upperBreakout, "-", lowerBreakout);
            Chart.DrawHorizontalLine("upperBreakout", upperBreakout, Color.White);
            Chart.DrawHorizontalLine("upperBoundZone", upperBoundZone, Color.White);
            Chart.DrawHorizontalLine("lowerBoundZone", lowerBoundZone, Color.White);
            Chart.DrawHorizontalLine("lowerBreakout", lowerBreakout, Color.White);

            calPos = new CalPosition(EntryTradeType, upperBoundZone, lowerBoundZone);

            // first pending order
            var result = PlaceStopOrder(calPos.nextType(), SymbolName, InitialVolumeInUnits * calPos.nextSize(), calPos.nextTargetPriceToLive(), "consequence");
            IsOnProcessZoneRecovery = true;
        }


        protected override void OnBar()
        {
            if (IsOnProcessZoneRecovery)
            {
                // finish recovery zone strategy
                if (Symbol.UnrealizedNetProfit > TakeProfit)
                {
                    Print("All positions wil be closed");
                    close_all_positions();
                    IsOnProcessZoneRecovery = false;

                    if (isRunOnce)
                        Stop();

                    InitialZoneRecovery();
                }
            }
        }

        private void OnPositionsOpened(PositionOpenedEventArgs args)
        {
            var position = args.Position;
            Print("open {0} stop with {1} lotsize", position.TradeType, position.Quantity);

            // ignore first order
            if (position.Label == "ExeTheFirstOrder")
                return;
            // consequence pending order
            PlaceStopOrder(calPos.nextType(), SymbolName, InitialVolumeInUnits * calPos.nextSize(), calPos.nextTargetPriceToLive(), "consequence");
        }

        private void OnPositionsClosed(PositionClosedEventArgs args)
        {
            var position = args.Position;
            Print("...PositionsID : {0}  closed", position.Id);
        }

        private void close_all_positions()
        {
            foreach (var i in PendingOrders)
            {
                i.Cancel();
            }
            foreach (var i in Positions)
            {
                i.Close();
            }
        }
    }

    internal class TakeProfitLevel
    {
        public string Name { get; private set; }

        public bool IsEnabled { get; private set; }

        public double Pips { get; private set; }

        public int Volume { get; private set; }

        public bool IsTriggered { get; private set; }

        public TakeProfitLevel(string name, bool isEnabled, double pips, int volume)
        {
            Name = name;
            IsEnabled = isEnabled;
            Pips = pips;
            Volume = volume;
        }

        public void MarkAsTriggered()
        {
            IsTriggered = true;
        }
    }

    public class CalPosition
    {
        //OPTIMIZE notsure condition pip constructure set nowType
        public TradeType _nowType;
        public double _upperBoundZone;
        public double _lowerBoundZone;

        private int index = 1;

        public int[] series = new int[] 
        {
            1,
            2,
            3,
            6,
            12,
            24,
            48,
            96,
            192,
            384,
            768,
            1536
        };

        public CalPosition(TradeType nowType, double upperBoundZone, double lowerBoundZone)
        {
            _nowType = nowType;
            _upperBoundZone = upperBoundZone;
            _lowerBoundZone = lowerBoundZone;
        }

        public int nextSize()
        {
            if (index + 1 == series.Length)
                return -1;
            return series[index++];
        }

        public TradeType nextType()
        {
            _nowType = TradeType.Buy == _nowType ? TradeType.Sell : TradeType.Buy;
            return _nowType;
        }

        public double nextTargetPriceToLive()
        {
            return TradeType.Buy == _nowType ? _upperBoundZone : _lowerBoundZone;
        }
    }
}
