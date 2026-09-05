#region Using declarations

using System;
using System.Collections.Generic;
using System.Threading;
using NinjaTrader.Cbi;

#endregion

namespace NinjaTrader.NinjaScript.AddOns
{
    /// <summary>
    /// State of the daily risk monitor. This first version only detects and reports
    /// a limit; it deliberately does not submit, cancel, or flatten orders.
    /// </summary>
    public enum AccountRiskState
    {
        Armed,
        Triggered,
        Locked
    }

    public sealed class AccountRiskSettings
    {
        public double ProfitLimit { get; set; }
        public double LossLimit { get; set; }
        public int TradingDayStartHour { get; set; } = 17;
        public int TradingDayStartMinute { get; set; }

        public void Validate()
        {
            if (ProfitLimit <= 0)
                throw new ArgumentOutOfRangeException(nameof(ProfitLimit), "ProfitLimit must be greater than zero.");
            if (LossLimit >= 0)
                throw new ArgumentOutOfRangeException(nameof(LossLimit), "LossLimit must be less than zero.");
            if (TradingDayStartHour < 0 || TradingDayStartHour > 23)
                throw new ArgumentOutOfRangeException(nameof(TradingDayStartHour));
            if (TradingDayStartMinute < 0 || TradingDayStartMinute > 59)
                throw new ArgumentOutOfRangeException(nameof(TradingDayStartMinute));
        }
    }

    /// <summary>Maps a timestamp to a session-style trading day (17:00 to 16:59:59 by default).</summary>
    public sealed class TradingDayManager
    {
        public TradingDayManager(int startHour = 17, int startMinute = 0)
        {
            if (startHour < 0 || startHour > 23 || startMinute < 0 || startMinute > 59)
                throw new ArgumentOutOfRangeException("Trading-day start is invalid.");

            StartTime = new TimeSpan(startHour, startMinute, 0);
        }

        public TimeSpan StartTime { get; private set; }

        public DateTime GetTradingDay(DateTime time)
        {
            return time.TimeOfDay >= StartTime ? time.Date : time.Date.AddDays(-1);
        }

        public DateTime GetNextTradingDayStart(DateTime time)
        {
            DateTime todayStart = time.Date.Add(StartTime);
            return time < todayStart ? todayStart : todayStart.AddDays(1);
        }
    }

    public sealed class RiskMarketPrice
    {
        public double Bid { get; set; }
        public double Ask { get; set; }
        public double Last { get; set; }
        public DateTime Timestamp { get; set; }
    }

    /// <summary>
    /// Keeps the latest quote for every subscribed instrument. Quotes are updated
    /// one field at a time because NinjaTrader publishes Bid, Ask, and Last separately.
    /// </summary>
    public sealed class RiskMarketDataManager
    {
        private readonly Dictionary<string, RiskMarketPrice> prices = new Dictionary<string, RiskMarketPrice>();

        public void Update(Instrument instrument, MarketDataType marketDataType, double value, DateTime timestamp)
        {
            if (instrument == null || value <= 0)
                return;

            RiskMarketPrice price;
            if (!prices.TryGetValue(instrument.FullName, out price))
            {
                price = new RiskMarketPrice();
                prices.Add(instrument.FullName, price);
            }

            if (marketDataType == MarketDataType.Bid)
                price.Bid = value;
            else if (marketDataType == MarketDataType.Ask)
                price.Ask = value;
            else if (marketDataType == MarketDataType.Last)
                price.Last = value;
            else
                return;

            price.Timestamp = timestamp;
        }

        public bool TryGetPrice(Instrument instrument, out RiskMarketPrice price)
        {
            price = null;
            return instrument != null && prices.TryGetValue(instrument.FullName, out price);
        }
    }

    internal sealed class RiskPosition
    {
        public Instrument Instrument;
        public MarketPosition MarketPosition;
        public int Quantity;
        public double AveragePrice;
        // The open P&L at the trading-day boundary. It makes a position held through
        // 17:00 contribute only its movement after 17:00 to the new daily P&L.
        public double TradingDayUnrealizedBaseline;
    }

    /// <summary>Calculates account-wide P&L using executable prices: Bid for longs and Ask for shorts.</summary>
    public sealed class AccountPnLEngine
    {
        private readonly Dictionary<string, RiskPosition> positions = new Dictionary<string, RiskPosition>();
        private readonly RiskMarketDataManager marketData;
        private double realizedAccountBaseline;
        private double currentAccountRealized;

        public AccountPnLEngine(RiskMarketDataManager marketData)
        {
            this.marketData = marketData;
        }

        public double RealizedPnL { get { return currentAccountRealized - realizedAccountBaseline; } }
        public double UnrealizedPnL { get; private set; }
        public double DailyPnL { get { return RealizedPnL + UnrealizedPnL; } }

        public void SetAccountRealizedPnL(double accountRealizedPnL)
        {
            currentAccountRealized = accountRealizedPnL;
        }

        public void UpdatePosition(Position position)
        {
            if (position == null || position.Instrument == null)
                return;

            string key = position.Instrument.FullName;
            if (position.MarketPosition == MarketPosition.Flat || position.Quantity == 0)
            {
                positions.Remove(key);
                Recalculate();
                return;
            }

            RiskPosition riskPosition;
            if (!positions.TryGetValue(key, out riskPosition))
            {
                riskPosition = new RiskPosition { Instrument = position.Instrument };
                positions.Add(key, riskPosition);
            }

            riskPosition.MarketPosition = position.MarketPosition;
            riskPosition.Quantity = position.Quantity;
            riskPosition.AveragePrice = position.AveragePrice;
            Recalculate();
        }

        public void Recalculate()
        {
            double total = 0;
            foreach (RiskPosition position in positions.Values)
            {
                double absoluteUnrealized;
                if (!TryGetUnrealized(position, out absoluteUnrealized))
                    continue;

                total += absoluteUnrealized - position.TradingDayUnrealizedBaseline;
            }
            UnrealizedPnL = total;
        }

        /// <summary>Captures both realized and open-P&L baselines exactly at the trading-day boundary.</summary>
        public void BeginTradingDay()
        {
            realizedAccountBaseline = currentAccountRealized;
            foreach (RiskPosition position in positions.Values)
            {
                double absoluteUnrealized;
                position.TradingDayUnrealizedBaseline = TryGetUnrealized(position, out absoluteUnrealized)
                    ? absoluteUnrealized : 0;
            }
            Recalculate();
        }

        private bool TryGetUnrealized(RiskPosition position, out double value)
        {
            value = 0;
            RiskMarketPrice price;
            if (!marketData.TryGetPrice(position.Instrument, out price) || position.AveragePrice <= 0)
                return false;

            double exitPrice = position.MarketPosition == MarketPosition.Long ? price.Bid : price.Ask;
            if (exitPrice <= 0)
                return false;

            double difference = position.MarketPosition == MarketPosition.Long
                ? exitPrice - position.AveragePrice
                : position.AveragePrice - exitPrice;
            value = difference * position.Instrument.MasterInstrument.PointValue * position.Quantity;
            return true;
        }
    }

    /// <summary>
    /// Owns the account-specific monitoring lifecycle. Callers may subscribe to
    /// LimitReached to add a separately reviewed lock/cancel/flatten workflow.
    /// </summary>
    public sealed class AccountRiskController
    {
        private readonly AccountRiskSettings settings;
        private readonly object sync = new object();
        public AccountRiskController(AccountRiskSettings settings, DateTime now, double accountRealizedPnL)
        {
            settings.Validate();
            this.settings = settings;
            TradingDay = new TradingDayManager(settings.TradingDayStartHour, settings.TradingDayStartMinute);
            MarketData = new RiskMarketDataManager();
            PnL = new AccountPnLEngine(MarketData);
            CurrentTradingDay = TradingDay.GetTradingDay(now);
            PnL.SetAccountRealizedPnL(accountRealizedPnL);
            PnL.BeginTradingDay();
            State = AccountRiskState.Armed;
        }

        public event EventHandler<string> LimitReached;
        public TradingDayManager TradingDay { get; private set; }
        public RiskMarketDataManager MarketData { get; private set; }
        public AccountPnLEngine PnL { get; private set; }
        public DateTime CurrentTradingDay { get; private set; }
        public AccountRiskState State { get; private set; }

        public void OnPositionUpdate(Position position, DateTime timestamp)
        {
            lock (sync)
            {
                CheckTradingDay(timestamp);
                PnL.UpdatePosition(position);
                Evaluate();
            }
        }

        public void OnMarketData(Instrument instrument, MarketDataType type, double price, DateTime timestamp)
        {
            lock (sync)
            {
                CheckTradingDay(timestamp);
                MarketData.Update(instrument, type, price, timestamp);
                PnL.Recalculate();
                Evaluate();
            }
        }

        public void OnAccountRealizedPnL(double accountRealizedPnL, DateTime timestamp)
        {
            lock (sync)
            {
                CheckTradingDay(timestamp);
                PnL.SetAccountRealizedPnL(accountRealizedPnL);
                Evaluate();
            }
        }

        /// <summary>
        /// Advances the trading-day clock even when there are no fills or ticks.
        /// The host calls this once per second so a 17:00 reset is not dependent
        /// on an instrument receiving its next market-data update.
        /// </summary>
        public void OnClock(DateTime timestamp)
        {
            lock (sync)
                CheckTradingDay(timestamp);
        }

        private void CheckTradingDay(DateTime timestamp)
        {
            DateTime newTradingDay = TradingDay.GetTradingDay(timestamp);
            if (newTradingDay == CurrentTradingDay)
                return;

            CurrentTradingDay = newTradingDay;
            PnL.BeginTradingDay();
            State = AccountRiskState.Armed;
        }

        private void Evaluate()
        {
            if (State != AccountRiskState.Armed)
                return;

            if (PnL.DailyPnL >= settings.ProfitLimit)
                Trigger("Profit limit");
            else if (PnL.DailyPnL <= settings.LossLimit)
                Trigger("Loss limit");
        }

        private void Trigger(string reason)
        {
            State = AccountRiskState.Triggered;
            EventHandler<string> handler = LimitReached;
            if (handler != null)
                handler(this, reason);
        }
    }

    /// <summary>
    /// NinjaTrader event adapter. It dynamically subscribes only to instruments
    /// that have an account position, then forwards individual Bid/Ask/Last ticks.
    /// </summary>
    public sealed class NinjaTraderAccountRiskMonitor : IDisposable
    {
        private readonly Account account;
        private readonly HashSet<Instrument> subscribedInstruments = new HashSet<Instrument>();
        private Timer tradingDayTimer;

        public NinjaTraderAccountRiskMonitor(Account account, AccountRiskSettings settings)
        {
            if (account == null)
                throw new ArgumentNullException(nameof(account));

            this.account = account;
            Controller = new AccountRiskController(
                settings,
                DateTime.Now,
                account.Get(AccountItem.RealizedProfitLoss, Currency.UsDollar));
        }

        public AccountRiskController Controller { get; private set; }

        public void Start()
        {
            account.PositionUpdate += OnPositionUpdate;
            account.AccountItemUpdate += OnAccountItemUpdate;
            foreach (Position position in account.Positions)
                TrackPosition(position, DateTime.Now);

            // Do not make the daily reset conditional on a market-data event.
            tradingDayTimer = new Timer(OnTradingDayTimer, null, TimeSpan.Zero, TimeSpan.FromSeconds(1));
        }

        public void Dispose()
        {
            if (tradingDayTimer != null)
            {
                tradingDayTimer.Dispose();
                tradingDayTimer = null;
            }
            account.PositionUpdate -= OnPositionUpdate;
            account.AccountItemUpdate -= OnAccountItemUpdate;
            foreach (Instrument instrument in subscribedInstruments)
                instrument.MarketData.Update -= OnMarketDataUpdate;
            subscribedInstruments.Clear();
        }

        private void OnPositionUpdate(object sender, PositionEventArgs e)
        {
            TrackPosition(e.Position, DateTime.Now);
        }

        private void TrackPosition(Position position, DateTime timestamp)
        {
            Controller.OnPositionUpdate(position, timestamp);
            if (position == null || position.Instrument == null)
                return;

            if (position.MarketPosition != MarketPosition.Flat && position.Quantity != 0 && subscribedInstruments.Add(position.Instrument))
                position.Instrument.MarketData.Update += OnMarketDataUpdate;
        }

        private void OnAccountItemUpdate(object sender, AccountItemEventArgs e)
        {
            if (e.AccountItem == AccountItem.RealizedProfitLoss)
                Controller.OnAccountRealizedPnL(e.Value, DateTime.Now);
        }

        private void OnMarketDataUpdate(object sender, MarketDataUpdateEventArgs e)
        {
            if (e.MarketDataType == MarketDataType.Bid || e.MarketDataType == MarketDataType.Ask || e.MarketDataType == MarketDataType.Last)
                Controller.OnMarketData(e.Instrument, e.MarketDataType, e.Price, e.Time);
        }

        private void OnTradingDayTimer(object state)
        {
            Controller.OnClock(DateTime.Now);
        }
    }
}
