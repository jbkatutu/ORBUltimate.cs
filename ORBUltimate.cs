#region Using declarations
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.ComponentModel.DataAnnotations;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Xml.Serialization;
using NinjaTrader.Cbi;
using NinjaTrader.Gui;
using NinjaTrader.Gui.Chart;
using NinjaTrader.Gui.SuperDom;
using NinjaTrader.Gui.Tools;
using NinjaTrader.Data;
using NinjaTrader.NinjaScript;
using NinjaTrader.Core.FloatingPoint;
using NinjaTrader.NinjaScript.Indicators;
using NinjaTrader.NinjaScript.DrawingTools;
#endregion

namespace NinjaTrader.NinjaScript.Strategies
{
    public class AtmTemplateConverterUltimate : TypeConverter
    {
        public override bool GetStandardValuesSupported(ITypeDescriptorContext context) { return true; }
        public override bool GetStandardValuesExclusive(ITypeDescriptorContext context) { return false; } 
        public override StandardValuesCollection GetStandardValues(ITypeDescriptorContext context)
        {
            List<string> templates = new List<string>();
            templates.Add(""); 
            string path = Path.Combine(NinjaTrader.Core.Globals.UserDataDir, "templates", "AtmStrategy");
            if (Directory.Exists(path))
            {
                foreach (string file in Directory.GetFiles(path, "*.xml"))
                    templates.Add(Path.GetFileNameWithoutExtension(file));
            }
            return new StandardValuesCollection(templates);
        }
    }

    public class ORBUltimate : Strategy
    {
        #region Variables & State
        private EMA fastEma;
        private EMA slowEma;
        private SMA volSma;
        private EMA ema15m;
        
        private double dailyRealizedPnL = 0;
        private int dailyTradeCount = 0;
        private int consecutiveLosers = 0;
        private bool isHaltedForDay = false;
        private int currentDay = -1;

        private double dailyHigh = double.MinValue;
        private double dailyLow = double.MaxValue;
        private double yesterdayHigh = 0;
        private double yesterdayLow = 0;

        private long buyVolAcc = 0;
        private long sellVolAcc = 0;

        private double[] orbHigh = new double[4];
        private double[] orbLow = new double[4];
        private bool[] orbFormed = new bool[4];
        private int[] tradesThisSession = new int[4]; 
        private string[] activeAtmId = new string[4];
        
        private MarketPosition[] lastAtmPos = new MarketPosition[4];
        private double[] entryPrice = new double[4];
        
        private bool[] brokeLong = new bool[4];
        private bool[] brokeShort = new bool[4];
        private bool[] retestedLong = new bool[4];
        private bool[] retestedShort = new bool[4];
        private bool[] fakeOutLong = new bool[4];
        private bool[] fakeOutShort = new bool[4];
        private bool[] awaitingLongConf = new bool[4];
        private bool[] awaitingShortConf = new bool[4];
        private double[] confPriceThreshold = new double[4];
        #endregion

        protected override void OnStateChange()
        {
            if (State == State.SetDefaults)
            {
                Description = "ORBUltimate with Global Guard and 3PM default cutoff.";
                Name = "ORBUltimate"; 
                Calculate = Calculate.OnBarClose;
                EntriesPerDirection = 1;
                EntryHandling = EntryHandling.AllEntries;
                IsExitOnSessionCloseStrategy = true;
                ExitOnSessionCloseSeconds = 30;
                BarsRequiredToTrade = 20;
                
                AllowedAccounts = "Sim101, Sim102"; 
                AllowedInstruments = "MGC 06-26"; 
                GlobalStart = 0; 
                GlobalEnd = 150000; 
                DailyLossLimit = 500; DailyProfitTarget = 1000;
                MaxDailyTrades = 10; MaxConsecutiveLosers = 3; MaxTradesPerSession = 3;
                BreakoutOffsetTicks = 0;

                UseMaFilter = true; FastMaPeriod = 9; SlowMaPeriod = 21;
                UseTwoCandleConfirmation = true;
                UseVolumeFilter = false; VolumeSmaPeriod = 20;
                UseRetestEntry = true; RetestToleranceTicks = 2;
                UseFakeOutEntry = true;
                UseTrailingExit = false;
                Use15MinTrendFilter = true;

                EnableAsian = false; AsianStart = 160000; AsianEnd = 170000; AsianAtmTemplate = "";
                EnableLondon = false; LondonStart = 10000; LondonEnd = 20000; LondonAtmTemplate = "";
                EnableNyAm = true; NyAmStart = 73000; NyAmEnd = 80000; NyAmAtmTemplate = "";
                EnableNyPm = false; NyPmStart = 110000; NyPmEnd = 120000; NyPmAtmTemplate = "";

                OrbBoxColor = Brushes.DodgerBlue;
                OrbBoxOpacity = 20;
                OrbBoxOffsetTicks = 0;
                OffsetLineColor = Brushes.Orange;
                ShowOrbLabels = true;
                ShowTradeSignals = true;
            }
            else if (State == State.Configure) 
            { 
                AddDataSeries(BarsPeriodType.Minute, 15);
                ResetTrackingArrays(); 
            }
            else if (State == State.DataLoaded)
            {
                fastEma = EMA(FastMaPeriod); fastEma.Plots[0].Brush = Brushes.Green;
                slowEma = EMA(SlowMaPeriod);
                if (UseMaFilter || UseTrailingExit) AddChartIndicator(fastEma);
                if (UseMaFilter) AddChartIndicator(slowEma);
                if (UseVolumeFilter) volSma = SMA(Volume, VolumeSmaPeriod);
                if (Use15MinTrendFilter) ema15m = EMA(BarsArray[1], 21);
            }
        }

        protected override void OnBarUpdate()
        {
            if (BarsInProgress != 0) return;
            if (CurrentBar < Math.Max(BarsRequiredToTrade, VolumeSmaPeriod)) return;
            if (!IsAccountAllowed() || !IsInstrumentAllowed()) return;

            if (Time[0].Day != currentDay)
            {
                if (currentDay != -1) { yesterdayHigh = dailyHigh; yesterdayLow = dailyLow; }
                currentDay = Time[0].Day;
                dailyRealizedPnL = 0; dailyTradeCount = 0; consecutiveLosers = 0;
                dailyHigh = High[0]; dailyLow = Low[0];
                buyVolAcc = 0; sellVolAcc = 0; 
                isHaltedForDay = false; ResetTrackingArrays();
            }

            if (Close[0] > Open[0]) buyVolAcc += (long)Volume[0];
            else if (Close[0] < Open[0]) sellVolAcc += (long)Volume[0];

            string volStats = string.Format("BUY VOL: {0:N0}\nSELL VOL: {1:N0}\nNET DELTA: {2:N0}", buyVolAcc, sellVolAcc, (buyVolAcc - sellVolAcc));
            Draw.TextFixed(this, "VolDash", volStats, TextPosition.BottomLeft, Brushes.White, new SimpleFont("Arial", 12), Brushes.Transparent, Brushes.Black, 60);

            if (High[0] > dailyHigh) dailyHigh = High[0];
            if (Low[0] < dailyLow) dailyLow = Low[0];

            Draw.HorizontalLine(this, "TodayHigh", dailyHigh, Brushes.DarkGreen, DashStyleHelper.Solid, 2);
            Draw.HorizontalLine(this, "TodayLow", dailyLow, Brushes.Crimson, DashStyleHelper.Solid, 2);
            if (yesterdayHigh > 0) {
                Draw.HorizontalLine(this, "YesterdayHigh", yesterdayHigh, Brushes.DarkGreen, DashStyleHelper.Dash, 1);
                Draw.HorizontalLine(this, "YesterdayLow", yesterdayLow, Brushes.Crimson, DashStyleHelper.Dash, 1);
            }

            for (int i = 0; i < 4; i++)
            {
                if (!string.IsNullOrEmpty(activeAtmId[i]))
                {
                    MarketPosition currentPos = GetAtmStrategyMarketPosition(activeAtmId[i]);
                    
                    if (lastAtmPos[i] != MarketPosition.Flat && currentPos == MarketPosition.Flat)
                    {
                        double exitPrc = Close[0]; 
                        bool isWin = (lastAtmPos[i] == MarketPosition.Long && exitPrc > entryPrice[i]) || 
                                    (lastAtmPos[i] == MarketPosition.Short && exitPrc < entryPrice[i]);

                        Brush exitColor = isWin ? Brushes.Gold : Brushes.DimGray;
                        double textYOffset = (lastAtmPos[i] == MarketPosition.Long) ? (20 * TickSize) : -(20 * TickSize);
                        string uniqueExitTag = activeAtmId[i]; 

                        Draw.Dot(this, "Exit_" + uniqueExitTag, true, 0, exitPrc, exitColor);
                        Draw.Text(this, "ExitTxt_" + uniqueExitTag, "EXIT\n" + exitPrc, 0, exitPrc + textYOffset, exitColor);
                    }
                    lastAtmPos[i] = currentPos;
                }
            }

            if (isHaltedForDay) return;
            CheckDailyLimits(); CheckTrailingExit(); 
            if (isHaltedForDay) return;

            EvaluateSession(0, EnableAsian, AsianStart, AsianEnd, AsianAtmTemplate);
            EvaluateSession(1, EnableLondon, LondonStart, LondonEnd, LondonAtmTemplate);
            EvaluateSession(2, EnableNyAm, NyAmStart, NyAmEnd, NyAmAtmTemplate);
            EvaluateSession(3, EnableNyPm, NyPmStart, NyPmEnd, NyPmAtmTemplate);
        }

        // --- EXTREME LOCKDOWN: Checks Live Positions + Live Working Orders + Internal Strategy State ---
        private bool IsAnyTradeActive()
        {
            if (Account != null && Instrument != null)
            {
                // Strict Failsafe 1: Checks for active filled positions
                lock (Account.Positions)
                {
                    foreach (Position pos in Account.Positions)
                    {
                        if (pos.Instrument.FullName == Instrument.FullName && pos.MarketPosition != MarketPosition.Flat)
                            return true; 
                    }
                }

                // Strict Failsafe 2: Checks for PENDING orders (prevents sending 2 orders before the 1st fills)
                lock (Account.Orders)
                {
                    foreach (Order ord in Account.Orders)
                    {
                        if (ord.Instrument.FullName == Instrument.FullName && 
                         (ord.OrderState == OrderState.Working || ord.OrderState == OrderState.Accepted || ord.OrderState == OrderState.Submitted))
                        {
                            return true;
                        }
                    }
                }
            }

            // Strict Failsafe 3: Strategy memory check
            for (int i = 0; i < 4; i++)
            {
                if (!string.IsNullOrEmpty(activeAtmId[i]))
                {
                    if (GetAtmStrategyMarketPosition(activeAtmId[i]) != MarketPosition.Flat)
                        return true;
                }
            }
            return false;
        }

        private int GetNextActiveSessionStart(int currentIdx)
        {
            if (currentIdx == 0) { if (EnableLondon) return LondonStart; if (EnableNyAm) return NyAmStart; if (EnableNyPm) return NyPmStart; }
            else if (currentIdx == 1) { if (EnableNyAm) return NyAmStart; if (EnableNyPm) return NyPmStart; if (EnableAsian) return AsianStart; }
            else if (currentIdx == 2) { if (EnableNyPm) return NyPmStart; if (EnableAsian) return AsianStart; if (EnableLondon) return LondonStart; }
            else if (currentIdx == 3) { if (EnableAsian) return AsianStart; if (EnableLondon) return LondonStart; if (EnableNyAm) return NyAmStart; }
            return GlobalEnd; 
        }

        private void EvaluateSession(int sIdx, bool isEnabled, int start, int end, string atmTemplate)
        {
            if (!isEnabled || string.IsNullOrEmpty(atmTemplate)) return;
            int currentTime = ToTime(Time[0]);
            int prevTime = ToTime(Time[1]);

            if (currentTime >= start && currentTime <= end)
            {
                if (High[0] > orbHigh[sIdx]) orbHigh[sIdx] = High[0];
                if (Low[0] < orbLow[sIdx]) orbLow[sIdx] = Low[0];
                
                if (currentTime == end || (prevTime < end && currentTime > end))
                {
                    orbFormed[sIdx] = true;
                    int nextStartTime = GetNextActiveSessionStart(sIdx);
                    DateTime extendedTime = new DateTime(Time[0].Year, Time[0].Month, Time[0].Day, nextStartTime / 10000, (nextStartTime / 100) % 100, 0);
                    if (extendedTime <= Time[0]) extendedTime = extendedTime.AddDays(1);

                    Draw.Line(this, "VLine_" + sIdx + "_" + currentDay, false, Time[0], orbLow[sIdx], Time[0], orbHigh[sIdx], OrbBoxColor, DashStyleHelper.Dash, 1);
                    var orbBox = Draw.Rectangle(this, "ORB_Box_" + sIdx + "_" + currentDay, GetSessionStartTime(start), orbHigh[sIdx], extendedTime, orbLow[sIdx], Brushes.Transparent);
                    if (orbBox != null) { orbBox.AreaBrush = OrbBoxColor; orbBox.AreaOpacity = OrbBoxOpacity; }

                    double offsetHigh = orbHigh[sIdx] + (OrbBoxOffsetTicks * TickSize);
                    double offsetLow = orbLow[sIdx] - (OrbBoxOffsetTicks * TickSize);
                    Draw.Line(this, "OffsetHigh_" + sIdx + "_" + currentDay, false, GetSessionStartTime(start), offsetHigh, extendedTime, offsetHigh, OffsetLineColor, DashStyleHelper.Dash, 1);
                    Draw.Line(this, "OffsetLow_" + sIdx + "_" + currentDay, false, GetSessionStartTime(start), offsetLow, extendedTime, offsetLow, OffsetLineColor, DashStyleHelper.Dash, 1);

                    double range = orbHigh[sIdx] - orbLow[sIdx];
                    Draw.Line(this, "Mid_" + sIdx + "_" + currentDay, false, GetSessionStartTime(start), orbLow[sIdx] + (range * 0.5), extendedTime, orbLow[sIdx] + (range * 0.5), OrbBoxColor, DashStyleHelper.Dash, 1);
                    Draw.Line(this, "Q25_" + sIdx + "_" + currentDay, false, GetSessionStartTime(start), orbLow[sIdx] + (range * 0.25), extendedTime, orbLow[sIdx] + (range * 0.25), OrbBoxColor, DashStyleHelper.Dot, 1);
                    Draw.Line(this, "Q75_" + sIdx + "_" + currentDay, false, GetSessionStartTime(start), orbLow[sIdx] + (range * 0.75), extendedTime, orbLow[sIdx] + (range * 0.75), OrbBoxColor, DashStyleHelper.Dot, 1);

                    if (ShowOrbLabels)
                    {
                        Draw.Text(this, "ORB_Txt_" + sIdx + "_" + currentDay, Math.Round((range) / TickSize, 1) + " Ticks", 0, orbHigh[sIdx] + (2 * TickSize), OrbBoxColor);
                        string sessionName = sIdx == 0 ? "Asia" : sIdx == 1 ? "London" : sIdx == 2 ? "NY-morning" : "NY-evening";
                        Draw.Text(this, "ORB_SessionTxt_" + sIdx + "_" + currentDay, sessionName, 0, orbLow[sIdx] - (2 * TickSize), OrbBoxColor);
                    }
                }
            }

            if (currentTime > GlobalEnd) return;

            // --- HARD ENFORCEMENT: Completely abort if ORB is not formed OR if time is not strictly after the session ends ---
            if (!orbFormed[sIdx] || currentTime <= end) return;
            
            // --- HARD ENFORCEMENT: Completely abort if max trades hit ---
            if (tradesThisSession[sIdx] >= MaxTradesPerSession) return;
            
            // --- HARD ENFORCEMENT: Completely abort if ANY trade is active or pending on the account/chart ---
            if (IsAnyTradeActive()) return;

            bool maLongValid = !UseMaFilter || (fastEma[0] > slowEma[0]);
            bool maShortValid = !UseMaFilter || (fastEma[0] < slowEma[0]);
            bool volValid = !UseVolumeFilter || (Volume[0] > volSma[0]);
            bool trendLong = !Use15MinTrendFilter || (ema15m[0] > ema15m[1]);
            bool trendShort = !Use15MinTrendFilter || (ema15m[0] < ema15m[1]);
            double longTrigger = orbHigh[sIdx] + (BreakoutOffsetTicks * TickSize);
            double shortTrigger = orbLow[sIdx] - (BreakoutOffsetTicks * TickSize);

            if (UseTwoCandleConfirmation)
            {
                if (!awaitingLongConf[sIdx] && !awaitingShortConf[sIdx])
                {
                    if (Close[0] > longTrigger) { awaitingLongConf[sIdx] = true; confPriceThreshold[sIdx] = High[0]; }
                    else if (Close[0] < shortTrigger) { awaitingShortConf[sIdx] = true; confPriceThreshold[sIdx] = Low[0]; }
                }
                else if (awaitingLongConf[sIdx] && Close[0] <= longTrigger) awaitingLongConf[sIdx] = false;
                else if (awaitingShortConf[sIdx] && Close[0] >= shortTrigger) awaitingShortConf[sIdx] = false;

                if (awaitingLongConf[sIdx] && High[0] > confPriceThreshold[sIdx] && maLongValid && volValid && trendLong) { ExecuteAtm(OrderAction.Buy, atmTemplate, sIdx); awaitingLongConf[sIdx] = false; return; }
                else if (awaitingShortConf[sIdx] && Low[0] < confPriceThreshold[sIdx] && maShortValid && volValid && trendShort) { ExecuteAtm(OrderAction.SellShort, atmTemplate, sIdx); awaitingShortConf[sIdx] = false; return; }
            }

            if (!UseTwoCandleConfirmation)
            {
                if (UseFakeOutEntry)
                {
                    if (High[0] > longTrigger) fakeOutLong[sIdx] = true;
                    if (Low[0] < shortTrigger) fakeOutShort[sIdx] = true;
                    if (fakeOutShort[sIdx] && Close[0] > shortTrigger && Close[0] > Open[0] && maLongValid && trendLong) { ExecuteAtm(OrderAction.Buy, atmTemplate, sIdx); fakeOutShort[sIdx] = false; return; }
                    if (fakeOutLong[sIdx] && Close[0] < longTrigger && Close[0] < Open[0] && maShortValid && trendShort) { ExecuteAtm(OrderAction.SellShort, atmTemplate, sIdx); fakeOutLong[sIdx] = false; return; }
                }
                if (UseRetestEntry)
                {
                    if (!brokeLong[sIdx] && Close[0] > longTrigger) brokeLong[sIdx] = true;
                    if (!brokeShort[sIdx] && Close[0] < shortTrigger) brokeShort[sIdx] = true;
                    if (brokeLong[sIdx] && !retestedLong[sIdx] && Low[0] <= longTrigger + (RetestToleranceTicks * TickSize)) retestedLong[sIdx] = true;
                    if (brokeShort[sIdx] && !retestedShort[sIdx] && High[0] >= shortTrigger - (RetestToleranceTicks * TickSize)) retestedShort[sIdx] = true;
                    if (retestedLong[sIdx] && Close[0] > Open[0] && maLongValid && trendLong) { ExecuteAtm(OrderAction.Buy, atmTemplate, sIdx); brokeLong[sIdx] = false; retestedLong[sIdx] = false; return; }
                    if (retestedShort[sIdx] && Close[0] < Open[0] && maShortValid && trendShort) { ExecuteAtm(OrderAction.SellShort, atmTemplate, sIdx); brokeShort[sIdx] = false; retestedShort[sIdx] = false; return; }
                }
                if (!UseRetestEntry && !UseFakeOutEntry)
                {
                    if (Close[0] > longTrigger && maLongValid && volValid && trendLong) ExecuteAtm(OrderAction.Buy, atmTemplate, sIdx);
                    else if (Close[0] < shortTrigger && maShortValid && volValid && trendShort) ExecuteAtm(OrderAction.SellShort, atmTemplate, sIdx);
                }
            }
        }

        private void ExecuteAtm(OrderAction action, string template, int sIdx)
        {
            if (State != State.Realtime) return;
            if (!IsAccountAllowed() || !IsInstrumentAllowed()) return;
            
            // Final micro-second check before firing order to the exchange
            if (IsAnyTradeActive()) return; 
            
            activeAtmId[sIdx] = GetAtmStrategyUniqueId();
            string uniqueEntryTag = activeAtmId[sIdx];
            
            tradesThisSession[sIdx]++; dailyTradeCount++;

            entryPrice[sIdx] = Close[0];
            lastAtmPos[sIdx] = (action == OrderAction.Buy) ? MarketPosition.Long : MarketPosition.Short;

            if (ShowTradeSignals) {
                if (action == OrderAction.Buy) {
                    Draw.TriangleUp(this, "Entry_" + uniqueEntryTag, true, 0, Low[0] - (8 * TickSize), Brushes.Lime);
                    Draw.Text(this, "EntryPrice_" + uniqueEntryTag, "BUY\n" + Close[0], 0, Low[0] - (25 * TickSize), Brushes.Lime);
                }
                else {
                    Draw.TriangleDown(this, "Entry_" + uniqueEntryTag, true, 0, High[0] + (8 * TickSize), Brushes.Red);
                    Draw.Text(this, "EntryPrice_" + uniqueEntryTag, "SELL\n" + Close[0], 0, High[0] + (25 * TickSize), Brushes.Red);
                }
            }
            AtmStrategyCreate(action, OrderType.Market, 0, 0, TimeInForce.Day, activeAtmId[sIdx], template, activeAtmId[sIdx], (err, id) => { });
        }

        private void CheckTrailingExit() { if (!UseTrailingExit) return; for (int i = 0; i < 4; i++) { if (!string.IsNullOrEmpty(activeAtmId[i])) { MarketPosition pos = GetAtmStrategyMarketPosition(activeAtmId[i]); if (pos == MarketPosition.Long && Close[0] < fastEma[0]) AtmStrategyClose(activeAtmId[i]); else if (pos == MarketPosition.Short && Close[0] > fastEma[0]) AtmStrategyClose(activeAtmId[i]); } } }
        private void CheckDailyLimits() { double tempPnL = 0; foreach (string id in activeAtmId) if (!string.IsNullOrEmpty(id)) tempPnL += GetAtmStrategyRealizedProfitLoss(id); dailyRealizedPnL = tempPnL; if (dailyRealizedPnL <= -DailyLossLimit || dailyRealizedPnL >= DailyProfitTarget || dailyTradeCount >= MaxDailyTrades) { isHaltedForDay = true; CloseAllAtms(); } }
        private void CloseAllAtms() { foreach (string id in activeAtmId) if (!string.IsNullOrEmpty(id) && GetAtmStrategyMarketPosition(id) != MarketPosition.Flat) AtmStrategyClose(id); }
        
        private void ResetTrackingArrays() { 
            for(int i=0; i<4; i++) { 
                orbHigh[i] = double.MinValue; orbLow[i] = double.MaxValue; orbFormed[i] = false; tradesThisSession[i] = 0; activeAtmId[i] = string.Empty; brokeLong[i] = false; brokeShort[i] = false; retestedLong[i] = false; retestedShort[i] = false; fakeOutLong[i] = false; fakeOutShort[i] = false; awaitingLongConf[i] = false; awaitingShortConf[i] = false; 
                lastAtmPos[i] = MarketPosition.Flat;
                entryPrice[i] = 0;
            } 
        }
        
        private DateTime GetSessionStartTime(int t) { for (int i = 0; i < CurrentBar; i++) if (ToTime(Time[i]) <= t) return Time[i]; return Time[0]; }
        private bool IsAccountAllowed() { if (string.IsNullOrEmpty(AllowedAccounts) || Account == null) return true; return AllowedAccounts.Split(',').Any(a => Account.Name.Trim().Equals(a.Trim(), StringComparison.OrdinalIgnoreCase)); }
        private bool IsInstrumentAllowed() { if (string.IsNullOrEmpty(AllowedInstruments) || Instrument == null) return true; return AllowedInstruments.Split(',').Any(i => Instrument.FullName.Contains(i.Trim()) || Instrument.MasterInstrument.Name.Equals(i.Trim())); }

        #region Properties
        [NinjaScriptProperty, Display(Name="Allowed Accounts", Order=0, GroupName="0. Global Settings")] public string AllowedAccounts { get; set; }
        [NinjaScriptProperty, Display(Name="Allowed Instruments", Order=1, GroupName="0. Global Settings")] public string AllowedInstruments { get; set; }
        [NinjaScriptProperty, Display(Name="Global Start Time", Order=2, GroupName="0. Global Settings")] public int GlobalStart { get; set; }
        [NinjaScriptProperty, Display(Name="Global End Time", Order=3, GroupName="0. Global Settings")] public int GlobalEnd { get; set; }
        [NinjaScriptProperty, Display(Name="Daily Loss Limit ($)", Order=4, GroupName="0. Global Settings")] public double DailyLossLimit { get; set; }
        [NinjaScriptProperty, Display(Name="Daily Profit Target ($)", Order=5, GroupName="0. Global Settings")] public double DailyProfitTarget { get; set; }
        [NinjaScriptProperty, Display(Name="Max Daily Trades", Order=6, GroupName="0. Global Settings")] public int MaxDailyTrades { get; set; }
        [NinjaScriptProperty, Display(Name="Max Consecutive Losers", Order=7, GroupName="0. Global Settings")] public int MaxConsecutiveLosers { get; set; }
        [NinjaScriptProperty, Display(Name="Max Trades Per Session", Order=8, GroupName="0. Global Settings")] public int MaxTradesPerSession { get; set; }
        [NinjaScriptProperty, Display(Name="Breakout Offset (Ticks)", Order=9, GroupName="0. Global Settings")] public int BreakoutOffsetTicks { get; set; }
        
        [NinjaScriptProperty, Display(Name="Use MA Filter", Order=1, GroupName="1. Entry Rules")] public bool UseMaFilter { get; set; }
        [NinjaScriptProperty, Display(Name="Use 2-Candle Confirmation", Order=2, GroupName="1. Entry Rules")] public bool UseTwoCandleConfirmation { get; set; }
        [NinjaScriptProperty, Display(Name="Fast MA Period", Order=3, GroupName="1. Entry Rules")] public int FastMaPeriod { get; set; }
        [NinjaScriptProperty, Display(Name="Slow MA Period", Order=4, GroupName="1. Entry Rules")] public int SlowMaPeriod { get; set; }
        [NinjaScriptProperty, Display(Name="Use Volume Filter", Order=5, GroupName="1. Entry Rules")] public bool UseVolumeFilter { get; set; }
        [NinjaScriptProperty, Display(Name="Volume SMA Period", Order=6, GroupName="1. Entry Rules")] public int VolumeSmaPeriod { get; set; }
        [NinjaScriptProperty, Display(Name="Use Retest Entry", Order=7, GroupName="1. Entry Rules")] public bool UseRetestEntry { get; set; }
        [NinjaScriptProperty, Display(Name="Retest Tolerance (Ticks)", Order=8, GroupName="1. Entry Rules")] public int RetestToleranceTicks { get; set; }
        [NinjaScriptProperty, Display(Name="Use Fake-out Reversal", Order=9, GroupName="1. Entry Rules")] public bool UseFakeOutEntry { get; set; }
        [NinjaScriptProperty, Display(Name="Use Trailing Exit", Order=1, GroupName="2. Exit Rules")] public bool UseTrailingExit { get; set; }
        [NinjaScriptProperty, Display(Name="Use 15min Trend Filter", Order=10, GroupName="1. Entry Rules")] public bool Use15MinTrendFilter { get; set; }
        
        [NinjaScriptProperty, Display(Name="Enable Asian", Order=1, GroupName="3. Asian Session")] public bool EnableAsian { get; set; }
        [NinjaScriptProperty, Display(Name="Start Time", Order=2, GroupName="3. Asian Session")] public int AsianStart { get; set; }
        [NinjaScriptProperty, Display(Name="End Time", Order=3, GroupName="3. Asian Session")] public int AsianEnd { get; set; }
        [TypeConverter(typeof(AtmTemplateConverterUltimate)), NinjaScriptProperty, Display(Name="ATM Template", Order=4, GroupName="3. Asian Session")] public string AsianAtmTemplate { get; set; }
        [NinjaScriptProperty, Display(Name="Enable London", Order=1, GroupName="4. London Session")] public bool EnableLondon { get; set; }
        [NinjaScriptProperty, Display(Name="Start Time", Order=2, GroupName="4. London Session")] public int LondonStart { get; set; }
        [NinjaScriptProperty, Display(Name="End Time", Order=3, GroupName="4. London Session")] public int LondonEnd { get; set; }
        [TypeConverter(typeof(AtmTemplateConverterUltimate)), NinjaScriptProperty, Display(Name="ATM Template", Order=4, GroupName="4. London Session")] public string LondonAtmTemplate { get; set; }
        [NinjaScriptProperty, Display(Name="Enable NY AM", Order=1, GroupName="5. NY AM Session")] public bool EnableNyAm { get; set; }
        [NinjaScriptProperty, Display(Name="Start Time", Order=2, GroupName="5. NY AM Session")] public int NyAmStart { get; set; }
        [NinjaScriptProperty, Display(Name="End Time", Order=3, GroupName="5. NY AM Session")] public int NyAmEnd { get; set; }
        [TypeConverter(typeof(AtmTemplateConverterUltimate)), NinjaScriptProperty, Display(Name="ATM Template", Order=4, GroupName="5. NY AM Session")] public string NyAmAtmTemplate { get; set; }
        [NinjaScriptProperty, Display(Name="Enable NY PM", Order=1, GroupName="6. NY PM Session")] public bool EnableNyPm { get; set; }
        [NinjaScriptProperty, Display(Name="Start Time", Order=2, GroupName="6. NY PM Session")] public int NyPmStart { get; set; }
        [NinjaScriptProperty, Display(Name="End Time", Order=3, GroupName="6. NY PM Session")] public int NyPmEnd { get; set; }
        [TypeConverter(typeof(AtmTemplateConverterUltimate)), NinjaScriptProperty, Display(Name="ATM Template", Order=4, GroupName="6. NY PM Session")] public string NyPmAtmTemplate { get; set; }
        
        [XmlIgnore, Display(Name="ORB Box Color", Order=1, GroupName="7. Visuals")] public Brush OrbBoxColor { get; set; }
        [Browsable(false)] public string OrbBoxColorSerializable { get { return Serialize.BrushToString(OrbBoxColor); } set { OrbBoxColor = Serialize.StringToBrush(value); } }
        [NinjaScriptProperty, Range(1, 100), Display(Name="Box Opacity", Order=2, GroupName="7. Visuals")] public int OrbBoxOpacity { get; set; }
        [NinjaScriptProperty, Display(Name="ORB Box Offset (Ticks)", Order=5, GroupName="7. Visuals")] public int OrbBoxOffsetTicks { get; set; }
        [XmlIgnore, Display(Name="Offset Line Color", Order=6, GroupName="7. Visuals")] public Brush OffsetLineColor { get; set; }
        [Browsable(false)] public string OffsetLineColorSerializable { get { return Serialize.BrushToString(OffsetLineColor); } set { OffsetLineColor = Serialize.StringToBrush(value); } }
        [NinjaScriptProperty, Display(Name="Show Text Labels", Order=3, GroupName="7. Visuals")] public bool ShowOrbLabels { get; set; }
        [NinjaScriptProperty, Display(Name="Show Trade Signals", Order=4, GroupName="7. Visuals")] public bool ShowTradeSignals { get; set; }
        #endregion
    }
}