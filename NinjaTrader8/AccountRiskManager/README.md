# Account Risk Manager for NinjaTrader 8 — monitoring core

This folder contains the initial **AddOn-compatible monitoring core** for a whole NinjaTrader account. It is intentionally scoped to the safe first milestone:

- calculate whole-account daily P&L (realized + unrealized);
- reset its accounting trading day at a configurable time (default **17:00**, checked by an independent one-second clock rather than waiting for a tick);
- subscribe dynamically to `Bid`, `Ask`, and `Last` market data for instruments with an open account position;
- value long positions at Bid and short positions at Ask; and
- raise a one-shot `LimitReached` event when the configured profit or loss limit is crossed.

It **does not place orders, cancel orders, flatten positions, or disable strategies**. Those actions must be implemented and Sim101-tested as an explicit later safety workflow rather than accidentally enabled by a P&L-calculation change.

## Install / host it

1. In NinjaTrader 8, open **New → NinjaScript Editor → AddOn** and create the AddOn shell that will host the monitor.
2. Add `AccountRiskManager.cs` to the NinjaScript AddOns folder, compile it in the NinjaScript Editor, and create the monitor from the AddOn after the user has selected an `Account`.
3. Keep the returned object alive for the AddOn lifetime, call `Start()`, and call `Dispose()` when the AddOn terminates.

The source imports both `System.Threading` (for the reset clock) and `NinjaTrader.Data`. In NT8, `Instrument.MarketData.Update` is an `EventHandler<MarketDataEventArgs>` event; consequently the callback is declared as `OnMarketDataUpdate(object sender, MarketDataEventArgs e)`. Do not replace it with `MarketDataUpdateEventArgs`, which is not the event argument type expected by this subscription.

```csharp
private NinjaTraderAccountRiskMonitor monitor;

private void StartMonitoring(Account account)
{
    monitor = new NinjaTraderAccountRiskMonitor(account, new AccountRiskSettings
    {
        ProfitLimit = 1500,
        LossLimit = -750,
        TradingDayStartHour = 17,
        TradingDayStartMinute = 0
    });
    monitor.Controller.LimitReached += OnDailyRiskLimitReached;
    monitor.Start();
}

private void OnDailyRiskLimitReached(object sender, string reason)
{
    // Monitoring phase: show a warning and audit P&L. Do NOT flatten here yet.
    Print("Risk limit reached: " + reason);
}
```

## Trading-day semantics

`TradingDayManager.GetTradingDay()` returns the calendar date on which a trading day began. With the default start of 17:00:

| Timestamp | Trading day key |
|---|---|
| Friday 16:59:59 | Thursday |
| Friday 17:00:00 | Friday |
| Saturday 00:01 | Friday |
| Saturday 16:59:59 | Friday |
| Saturday 17:00:00 | Saturday |

At a boundary, the engine records two baselines: the account realized P&L and each currently-open position's executable unrealized P&L. Therefore a position held over 17:00 contributes only its post-17:00 price movement to the new daily P&L. The monitor's clock checks once per second, so the logical boundary is 17:00 without depending on the next tick; actual scheduling can occur within that one-second interval.

## Verification before protection actions

Run this only on **Sim101** first. Verify the 17:00 boundary, long Bid valuation, short Ask valuation, and a position held across the boundary. A production protection phase should make the state transition idempotent and ordered: lock → cancel working orders → disable strategies → flatten → confirm flat → locked until the next trading day.
