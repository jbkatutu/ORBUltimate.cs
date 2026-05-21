# ORBUltimate Trading Rules

## Risk Management (Hard Enforced)
- Daily Loss Limit: $500 (adjustable via property)
- Daily Profit Target: $1000 (adjustable)
- Max Daily Trades: 10
- Max Consecutive Losers: 3
- Max Trades Per Session: 3
- Halt all trading for the day immediately if any limit is hit
- No new entries if any position is active or pending order exists on the account

## Entry Rules (All conditions must be true)
- ORB must be fully formed for the active session
- Current time strictly after session end time
- 15min EMA trend filter (if enabled): Long only if 15min EMA[0] > EMA[1]; Short only if EMA[0] < EMA[1]
- MA Filter (if enabled): Fast EMA > Slow EMA for longs; Fast < Slow for shorts
- Volume Filter (if enabled): Current volume > Volume SMA
- Breakout price exceeds ORB high/low + offset ticks
- Confirmation logic passed (2-candle, retest, or fakeout as configured)
- No more than MaxTradesPerSession entries per session

## Session Rules
- Only trade enabled sessions (Asian/London/NY AM/NY PM)
- Respect exact start/end times for each session
- Use session-specific ATM template for entries
- Global trading window (GlobalStart to GlobalEnd) always enforced

## Position & Exit Rules
- ATM strategy used for all entries (market order + template exits)
- Trailing exit via fast EMA (if UseTrailingExit enabled)
- Strict pre-entry check for live positions and working orders
- Auto-close all ATMs if daily limits breached

## Visual & Monitoring Rules
- Always draw ORB box, mid/quarter lines, offset lines, vertical formation line
- Display real-time volume delta and daily high/low lines
- Show entry/exit signals if enabled
- Log all key stats on chart

## General Rules
- Strategy only runs in Realtime mode
- Allowed accounts and instruments strictly filtered
- No trading on non-primary bars (15min data used only for filter)
- All parameters adjustable in strategy properties for optimization