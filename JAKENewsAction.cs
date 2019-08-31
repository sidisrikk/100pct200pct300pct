using System;
using System.Linq;
using System.Linq.Expressions;
using System.Collections.Generic;
using System.Windows.Forms;
using System.Diagnostics;
using System.Threading;

using cAlgo.API;
using cAlgo.API.Indicators;
using cAlgo.API.Internals;
using cAlgo.Indicators;

using JAKE.NewsExchange;


namespace cAlgo.Robots
{
    [Robot(TimeZone = TimeZones.NorthAsiaStandardTime, AccessRights = AccessRights.FullAccess)]
    public class JAKENewsAction : Robot
    {
        #region Params
        [Parameter("Enable Trade", DefaultValue = true)]
        public bool ParamEnableTrade { get; set; }

        [Parameter("Risk %", DefaultValue = 10, MinValue = 5)]
        public double ParamRisk { get; set; }
        [Parameter("Lot Size %", DefaultValue = 10, MinValue = 5)]
        public double ParamLotSize { get; set; }
        [Parameter("Open Pips (10x points)", DefaultValue = 4, MinValue = 0.1)]
        public double ParamOpenPips { get; set; }
        [Parameter("Max Spread (x of Average)", DefaultValue = 1.5, MinValue = 1)]
        public double ParamMaxSpread { get; set; }
        [Parameter("Trailing %", DefaultValue = 4, MinValue = 1)]
        public double ParamTrailing { get; set; }

        [Parameter("Trailing Instant (% balance)", DefaultValue = 0, MinValue = 0)]
        public double ParamInstantTrailingInit { get; set; }
        [Parameter("Trailing Instant %", DefaultValue = 4, MinValue = 1)]
        public double ParamInstantTrailing { get; set; }

        [Parameter("For Currency", DefaultValue = "")]
        public string ParamCurrency { get; set; }
        #endregion

        #region Vars and State Vars

        protected NewsManager newsMgr;
        protected JAKESpreadRecorder recorder;
        protected List<VirtualOrder> virtualOrders;
        protected List<News> activeNews;
        protected List<News> announcedNews;
        protected TradingPhase phase;

        protected bool announced;
        protected double spread;
        protected bool firstMinute;
        protected bool recordSpread;
        protected double spreadAccum, spreadAverage, lockedSpreadAverage;
        protected int spreadCount;
        protected double riskCurrent;
        protected TradeState buyState, sellState;
        protected double buyTS, sellTS;
        protected bool instantTS;
        protected bool posCutLoss;
        protected long uiSpreadAccum, uiSpreadCount;
        protected double buyEntry, sellEntry;

        protected DateTime lastNewsLoad;

        #endregion

        protected string __VERSION = "4.71";

        // PHASES

        #region Phases

        protected void ResetPhases()
        {
            virtualOrders = new List<VirtualOrder> 
            {
                            };
            activeNews = null;
            announcedNews = null;
            phase = TradingPhase.Idle;
            announced = false;
            spread = double.MaxValue;
            firstMinute = true;
            recordSpread = false;
            spreadAccum = 0;
            spreadCount = 0;
            spreadAverage = 0;
            lockedSpreadAverage = 0;
            riskCurrent = ParamRisk;
            posCutLoss = false;
            buyEntry = 0;
            sellEntry = 0;
            instantTS = false;

            buyState = TradeState.None;
            sellState = TradeState.None;
            buyTS = double.MinValue;
            sellTS = double.MinValue;

        }

        #endregion

        // WINFORM

        #region WinForm

        protected FormDiag f1;
        protected Thread thread;

        [STAThread()]
        protected void FormInit()
        {
            thread = new Thread(new ThreadStart(FormThread));
            thread.SetApartmentState(ApartmentState.STA);
            thread.Start();

            uiSpreadAccum = 0;
            uiSpreadCount = 0;
        }

        [STAThread()]
        protected void FormThread()
        {
            f1 = new FormDiag();
            f1.OwnerBot = this;

            f1.ResumeLayout(false);
            f1.PerformLayout();

            Application.EnableVisualStyles();
            Application.Run(f1);
        }

        protected void UpdateForm()
        {
            if (f1 == null)
                return;

            uiSpreadCount++;
            int currentSpread = (int)(Symbol.Spread / Symbol.TickSize);
            uiSpreadAccum += currentSpread;
            long avgSpread = uiSpreadAccum / uiSpreadCount;

            f1.SetLabelSymbol(Symbol.Code + " " + " bid: " + Symbol.Bid + " ask: " + Symbol.Ask + " | spread: " + currentSpread + " average: " + avgSpread);
        }

        #endregion

        // EVENT HANDLER

        #region Event Handler

        protected override void OnStart()
        {
            PendingOrders.Created += PendingOrders_Created;
            PendingOrders.Cancelled += PendingOrders_Cancelled;
            Positions.Opened += Positions_Opened;
            Positions.Closed += Positions_Closed;

            Print("JAKENewsAction " + __VERSION);
            ResetPhases();

            virtualOrders = new List<VirtualOrder> 
            {
                            };
            newsMgr = new NewsManager();
            LoadNews();
            recorder = new JAKESpreadRecorder("c:\\fx\\spreadlog\\result_v__VER___.csv".Replace("__VER__", __VERSION));
        }

        protected override void OnStop()
        {

            recorder.SaveAndClear();
        }

        protected override void OnBar()
        {
            try
            {
                if (phase == TradingPhase.PositionOpen)
                {
                    Action_SetTrailingAfterFirstMinute();
                    Action_RiskStop();
                }

                if (phase == TradingPhase.OrderSet)
                {
                    Action_CancelUnopenedOrder();
                }
            } catch (Exception)
            {
            }
        }

        protected override void OnTick()
        {
            try
            {
                UpdateForm();
                RecordSpread();

                if (phase == TradingPhase.Idle)
                {
                    if (DateTime.Now - lastNewsLoad > TimeSpan.FromMinutes(60))
                    {
                        Print("> refreshing news");
                        LoadNews();
                    }
                }

                if (phase == TradingPhase.PositionOpen)
                {
                    Action_SetTrailingIfProfit();
                    Action_DoTrailingIfStated();
                    Action_DoCutlossIfLoss();
                }

                if (phase == TradingPhase.PositionOpen || phase == TradingPhase.OrderSet)
                {
                    Action_OpenPositionFromVirtualOrders();
                }

                if (phase == TradingPhase.Idle || phase == TradingPhase.PositionOpen || phase == TradingPhase.OrderSet)
                {
                    Action_CloseTime();
                }

                if (newsMgr.News == null || newsMgr.News.Count == 0)
                    return;

                if (phase == TradingPhase.Idle || phase == TradingPhase.OrderSet)
                {
                    Action_Announce();
                    Action_MakeOrder();
                }

            } catch (Exception ex)
            {
                Print(ex.Message + "//" + ex.StackTrace);
                Log(ex.Message + "//" + ex.StackTrace);
            }
        }

        protected void PendingOrders_Created(PendingOrderCreatedEventArgs obj)
        {
        }

        protected void PendingOrders_Cancelled(PendingOrderCancelledEventArgs obj)
        {
        }

        protected void Positions_Closed(PositionClosedEventArgs obj)
        {
        }

        protected void Positions_Opened(PositionOpenedEventArgs obj)
        {
            if (obj.Position.TradeType == TradeType.Buy)
            {
                buyState = TradeState.Position;
            }
            else
            {
                sellState = TradeState.Position;
            }

            if (Positions != null && Positions.Count == 2)
            {
                ReduceRisk();
            }

            phase = TradingPhase.PositionOpen;
        }

        #endregion

        // ACTIONS

        #region Actions

        private void Action_OpenPositionFromVirtualOrders()
        {
            if (!IsNewsStarted(activeNews[0].DateTime))
                return;

            double spreadPar = ParamMaxSpread * lockedSpreadAverage;
            double spread = Symbol.Spread / Symbol.TickSize;
            double ask = Symbol.Ask;
            double bid = Symbol.Bid;

            TradeResult result;

            if (spread < spreadPar)
            {
                foreach (var ord in virtualOrders)
                {
                    if (ord.Type == TradeType.Buy && ask >= ord.EntryPrice && !ord.IsOpen)
                    {
                        PrintAll(false, "(open pos) buy spread current:", ToDecimalString(spread), "par:", ToDecimalString(spreadPar));

                        do
                        {
                            if (spread > spreadPar)
                                return;

                            result = ExecuteMarketOrder(TradeType.Buy, Symbol, ord.Volume, ord.Label);
                        } while (!result.IsSuccessful);

                        ord.IsOpen = true;
                    }

                    if (ord.Type == TradeType.Sell && bid <= ord.EntryPrice && !ord.IsOpen)
                    {
                        PrintAll(false, "(open pos) sell spread current:", ToDecimalString(spread), "par:", ToDecimalString(spreadPar));

                        do
                        {
                            if (spread > spreadPar)
                                return;

                            result = ExecuteMarketOrder(TradeType.Sell, Symbol, ord.Volume, ord.Label);
                        } while (!result.IsSuccessful);

                        ord.IsOpen = true;
                    }
                }

                //CancelAllVirtualOrders();
            }
        }

        private void Action_SetTrailingIfProfit()
        {
            if (ParamInstantTrailingInit == 0)
                return;

            foreach (var pos in Positions)
            {
                double profit = pos.NetProfit;
                double par = ParamInstantTrailingInit / 100 * Account.Balance;

                if (profit > par)
                {
                    if (pos.TradeType == TradeType.Buy && buyState != TradeState.Trailing)
                    {
                        Print("> instant profit trailing at " + profit + " par " + par);
                        buyState = TradeState.Trailing;
                        instantTS = true;
                    }
                    else if (pos.TradeType == TradeType.Sell && sellState != TradeState.Trailing)
                    {
                        Print("> instant profit trailing at " + profit + " par " + par);
                        sellState = TradeState.Trailing;
                        instantTS = true;
                    }
                }
            }
        }

        private void Action_DoTrailingIfStated()
        {
            if (buyState == TradeState.Trailing)
            {
                TrailingPositions(TradeType.Buy);
            }

            if (sellState == TradeState.Trailing)
            {
                TrailingPositions(TradeType.Sell);
            }
        }

        private void Action_CancelUnopenedOrder()
        {
            if (IsAfterFirstMinute(activeNews[0].DateTime))
            {
                CancelAllVirtualOrders();
            }
        }

        private void Action_SetTrailingAfterFirstMinute()
        {
            if (IsAfterFirstMinute(activeNews[0].DateTime))
            {
                instantTS = false;
                buyState = TradeState.Trailing;
                sellState = TradeState.Trailing;

                CancelAllVirtualOrders();
            }
        }

        private void Action_RiskStop()
        {
            if (IsAfterFirstMinute(activeNews[0].DateTime))
            {
                if (Positions.Count == 1)
                {
                    double profit0 = Positions[0].NetProfit;

                    if (IsCutloss(Positions[0], ParamRisk / 4))
                    {
                        CloseAllPositions();
                    }
                    else if (profit0 < 0 && posCutLoss)
                    {
                        CloseAllPositions();
                    }
                    else
                    {
                        ReduceRisk();
                    }
                }

                if (Positions.Count == 2)
                {
                    double profit0 = Positions[0].NetProfit;
                    double profit1 = Positions[1].NetProfit;

                    if (profit0 < 0 && profit1 < 0)
                    {
                        CloseAllPositions();
                    }
                }
            }
        }

        private void Action_MakeOrder()
        {
            var effectiveNews = newsMgr.News.Where(q => IsSetTime(q.DateTime)).ToList();
            if (effectiveNews.Count != 0)
            {
                double spreadInPoints = Symbol.Spread / Symbol.TickSize;

                if (Symbol.Spread > spread)
                    return;

                lockedSpreadAverage = spreadAverage;
                Print("(make order) average spread: " + ToDecimalString(spreadAverage));

                CancelAllVirtualOrders();
                CreateVirtualOrders(effectiveNews);

                activeNews = effectiveNews;
                spread = Symbol.Spread;
                phase = TradingPhase.OrderSet;
            }
        }

        private void Action_Announce()
        {
            var n1 = newsMgr.News.Where(q => IsAnnounceTime(q.DateTime)).ToList();

            if (!announced && n1.Count != 0)
            {
                PrintNews(n1);

                recordSpread = true;
                announced = true;
                announcedNews = n1;
            }
        }

        private void Action_CloseTime()
        {
            if (phase == TradingPhase.Idle)
            {
                if (!announced)
                    return;

                if (IsCloseTime(announcedNews[0].DateTime))
                {
                    Print("> reset without news");

                    LoadNews();
                    ResetPhases();

                    recorder.SaveAndClear();
                }

                return;
            }

            try
            {
                if (IsCloseTime(activeNews[0].DateTime))
                {
                    CloseAllPositions();
                    CancelAllVirtualOrders();

                    LoadNews();
                    ResetPhases();

                    recorder.SaveAndClear();
                }

                else if (IsImmediateCloseTime(activeNews[0].DateTime) && Positions.Count == 0)
                {
                    Log("> immediate Cancel Orders");
                    CancelAllVirtualOrders();

                    LoadNews();
                    ResetPhases();
                }

            } catch (Exception ex)
            {

                Print(ex.Message + "//" + ex.StackTrace);
            }
        }

        private void Action_DoCutlossIfLoss()
        {
            if (Positions != null)
            {
                CutLossPositions();
            }
        }

        #endregion

        // NEWS

        #region News

        protected void LoadNews(bool print = false)
        {
            newsMgr.LoadNews();
            lastNewsLoad = DateTime.Now;

            if (ParamCurrency == "")
            {
                newsMgr.FilterNewsByTime(Time, Symbol.Code);
                Print("> " + newsMgr.News.Count + " news for " + Symbol.Code);

            }
            else
            {
                newsMgr.FilterNewsByTime(Time, Symbol.Code, ParamCurrency);
                Print("> " + newsMgr.News.Count + " news for " + Symbol.Code + " based on " + ParamCurrency);

            }


            NextNews();

            if (!print)
                return;

            foreach (var news in newsMgr.News)
            {
                Print("> " + news.ID + " | " + news.DateTime.ToString("dd/MM/yyyy HH:mm") + " | " + news.Impact + " | " + news.Description);
            }
        }

        protected void NextNews()
        {
            if (ParamCurrency == "")
            {
                newsMgr.FilterNewsByTime(Time, Symbol.Code);
            }
            else
            {
                newsMgr.FilterNewsByTime(Time, Symbol.Code, ParamCurrency);
            }

            if (newsMgr.News == null || newsMgr.News.Count == 0)
                return;

            var news = newsMgr.News[0];
            var delta = (news.DateTime - Time);
            var time = string.Format("{0:0}d {1:00}h {2:00}m", delta.Days, delta.Hours, delta.Minutes);

            Print("> next news in " + time + " -- " + news.Currency + " | " + news.DateTime.ToString("dd/MM/yyyy HH:mm") + " | " + news.Impact + " | " + news.Description);
        }

        protected void PrintNews(List<News> news)
        {
            foreach (var n in news)
            {
                Print("> upcoming news at " + n.DateTime + " | " + n.Description);
            }
        }

        #endregion

        // TIMECHECK

        #region Timecheck

        protected bool IsAnnounceTime(DateTime NewsDateTime)
        {
            bool result = false;

            DateTime current = Time;
            DateTime BeforeNews = NewsDateTime.AddMinutes(-5);

            result = current.Date == BeforeNews.Date && current.Hour == BeforeNews.Hour && current.Minute == BeforeNews.Minute;

            return result;
        }

        protected bool IsSetTime(DateTime NewsDateTime)
        {
            bool result = false;

            DateTime current = Time;
            DateTime BeforeNews = NewsDateTime.AddMinutes(-1);

            int startSecond = 55;
            int endSecond = 59;

            result = current.Date == BeforeNews.Date && current.Hour == BeforeNews.Hour && current.Minute == BeforeNews.Minute && current.Second >= startSecond && current.Second <= endSecond;

            return result;
        }

        protected bool IsAfterFirstMinute(DateTime NewsDateTime)
        {
            bool result = false;

            DateTime current = Time;

            if (current > NewsDateTime.AddSeconds(30) && current.Subtract(NewsDateTime).Minutes < 5)
            {
                return true;
            }

            return result;
        }

        protected bool IsCloseTime(DateTime NewsDateTime)
        {
            bool result = false;

            DateTime current = Time;
            DateTime AfterNews = NewsDateTime.AddMinutes(5);

            result = current.Date == AfterNews.Date && current.Hour == AfterNews.Hour && current.Minute == AfterNews.Minute;

            return result;
        }

        protected bool IsImmediateCloseTime(DateTime NewsDateTime)
        {
            bool result = false;

            DateTime current = Time;
            DateTime AfterNews = NewsDateTime.AddSeconds(10);

            result = current.Date == AfterNews.Date && current.Hour == AfterNews.Hour && current.Minute == AfterNews.Minute && current.Second > AfterNews.Second;

            return result;
        }

        protected bool IsNewsStarted(DateTime NewsDateTime)
        {
            bool result = false;

            DateTime current = Time;
            DateTime AfterNews = NewsDateTime;

            result = current.Date == AfterNews.Date && current.Hour == AfterNews.Hour && current.Minute == AfterNews.Minute && current.Second >= AfterNews.Second;

            return result;
        }

        #endregion

        // POSITION+ORDER

        #region Position + Order

        protected void CreateVirtualOrders(List<News> news)
        {
            int volume = CalculateVolumeAdvanced();

            int spreadPoint = (int)(Symbol.Spread / Symbol.TickSize);
            double buyPrice = Symbol.Ask + (ParamOpenPips * Symbol.PipSize);
            double sellPrice = Symbol.Bid - (ParamOpenPips * Symbol.PipSize);

            buyEntry = buyPrice;
            sellEntry = sellPrice;

            string label = "";
            foreach (var n in news)
            {
                label += n.Description + " ";
            }

            VirtualOrder buy = new VirtualOrder 
            {
                Type = TradeType.Buy,
                Volume = volume,
                EntryPrice = buyPrice,
                SpreadInPoint = spreadPoint,
                Label = label
            };

            VirtualOrder sell = new VirtualOrder 
            {
                Type = TradeType.Sell,
                Volume = volume,
                EntryPrice = sellPrice,
                SpreadInPoint = spreadPoint,
                Label = label
            };

            virtualOrders.Clear();
            virtualOrders.Add(buy);
            virtualOrders.Add(sell);

            Print(buy);
            Print(sell);
        }

        protected void TrailingPositions(TradeType type)
        {
            Position pos = Positions.Where(q => q.TradeType == type).FirstOrDefault();

            if (pos == null)
                return;

            double percent = instantTS ? ParamInstantTrailing : ParamTrailing;
            double profit = pos.NetProfit;
            double trailingDist = percent / 100 * Account.Balance;
            double trailingProfit = profit - trailingDist;

            if (type == TradeType.Buy)
            {
                if (buyTS == double.MinValue)
                {
                    if (profit > 0)
                    {
                        buyTS = trailingProfit;
                    }
                }
                else
                {
                    double currentDist = profit - buyTS;

                    if (profit > buyTS && currentDist > trailingDist)
                    {
                        buyTS = trailingProfit;
                    }
                    else if (profit <= buyTS)
                    {
                        ClosePosition(pos);
                        Print("> trailing close profit: " + profit + " trailing: " + buyTS + " side: " + type + " instant: " + instantTS);
                    }
                }
            }
            else if (type == TradeType.Sell)
            {
                if (sellTS == double.MinValue)
                {
                    if (profit > 0)
                    {
                        sellTS = trailingProfit;
                    }
                }
                else
                {
                    double currentDist = profit - sellTS;

                    if (profit > sellTS && currentDist > trailingDist)
                    {
                        sellTS = trailingProfit;
                    }
                    else if (profit <= sellTS)
                    {
                        ClosePosition(pos);
                        Print("> trailing close profit: " + profit + " trailing: " + sellTS + " side: " + type + " instant: " + instantTS);
                    }
                }
            }
        }

        protected void CloseAllPositions(string reason = "none")
        {
            if (Positions.Count == 0)
                return;

            foreach (var pos in Positions)
            {
                ClosePosition(pos);
            }
        }

        protected void ClosePosition(Position pos, string reason = "none")
        {
            TradeResult res = null;
            do
            {
                res = pos.Close();
            } while (res == null || !res.IsSuccessful);
        }

        protected void CutLossPositions()
        {
            if (Positions.Count == 0)
                return;

            foreach (var pos in Positions)
            {
                if (IsCutloss(pos, this.riskCurrent))
                {
                    ClosePosition(pos);
                    posCutLoss = true;
                }
            }
        }

        protected void CancelAllVirtualOrders(string reason = "none")
        {
            virtualOrders.Clear();
        }

        #endregion

        // CALCULATION

        #region Calculation

        protected int CalculateVolumeAdvanced()
        {
            string baseCurr = new string(Symbol.Code.Take(3).ToArray());
            string compCurr = new string(Symbol.Code.Skip(3).ToArray());

            double usdValue = 1, lot, vol;

            if (Symbol.Code.Contains("USD"))
            {
                if (baseCurr != "USD")
                {
                    usdValue = 1 / Symbol.Bid;
                }

                lot = ParamLotSize * Account.Equity / ParamOpenPips * Symbol.PipSize;
                vol = Symbol.QuantityToVolumeInUnits(lot);

                vol = vol * usdValue;
            }
            else
            {
                Func<string, double> value = s =>
                {
                    switch (s)
                    {
                        case "AUD":
                            return 0.71;
                        case "CAD":
                            return 0.75;
                        case "GBP":
                            return 1.33;
                        case "NZD":
                            return 0.68;
                        case "JPY":
                            return 0.009;
                        case "CHF":
                            return 0.999;
                        case "EUR":
                            return 1.13;
                    }
                    return 1;
                };

                double usdRisk = ParamLotSize / 100 * Account.Equity;
                double baseRisk = usdRisk * (1 / value(baseCurr));
                double baseRiskPerPip = baseRisk / (ParamOpenPips * 10);

                vol = baseRiskPerPip * Symbol.LotSize / 10;
            }


            int tradeVol = (int)Symbol.NormalizeVolumeInUnits(vol);
            return tradeVol;
        }

        protected bool IsCutloss(Position pos, double risk)
        {
            double lossPos = pos.NetProfit;

            // Profit
            if (lossPos > 0)
                return false;

            double lossPar = risk / 100 * Account.Balance;
            lossPos = Math.Abs(lossPos);

            if (lossPos > lossPar)
            {
                Print("> cutloss value:" + lossPos + " / par:" + lossPar + " / side:" + pos.TradeType);
                return true;
            }

            return false;
        }

        protected void ReduceRisk()
        {
            riskCurrent = ParamRisk / 2;
        }

        #endregion

        // HELPER

        #region Helper

        protected void Log(string log)
        {

        }

        protected void PrintSummary()
        {
            Print("Trade on " + Symbol + " at " + TimeFrame.ToString());
        }

        protected string ToDecimalString(double d)
        {
            return string.Format("{0:0.000000}", d);
        }

        protected void RecordSpread()
        {
            if (recordSpread)
            {
                double spread = Symbol.Spread / Symbol.TickSize;

                DateTime time = Time;
                if (time >= recorder.LastRecordTime.AddSeconds(1))
                {
                    recorder.AddRecord(Symbol.Code, time, spread, spreadAverage, Symbol.Ask, Symbol.Bid, buyEntry, sellEntry, Symbol.TickSize);
                }

                spreadAccum += spread;
                spreadCount++;

                //TEST
                spreadAverage = spreadAccum / spreadCount;
            }
        }

        protected void PrintAll(bool withSeparator, params string[] strs)
        {
            string s = "";
            for (int i = 0; i < strs.Count(); i++)
            {
                s += strs[i];

                if (i != strs.Count() - 1)
                {
                    s += withSeparator ? " | " : " ";
                }
            }

            Print(s);
        }

        #endregion
    }
}
