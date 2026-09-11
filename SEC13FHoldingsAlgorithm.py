# QUANTCONNECT.COM - Democratizing Finance, Empowering Individuals.
# Lean Algorithmic Trading Engine v2.0. Copyright 2014 QuantConnect Corporation.
#
# Licensed under the Apache License, Version 2.0 (the "License");
# you may not use this file except in compliance with the License.
# You may obtain a copy of the License at http://www.apache.org/licenses/LICENSE-2.0
#
# Unless required by applicable law or agreed to in writing, software
# distributed under the License is distributed on an "AS IS" BASIS,
# WITHOUT WARRANTIES OR CONDITIONS OF ANY KIND, either express or implied.
# See the License for the specific language governing permissions and
# limitations under the License.

from AlgorithmImports import *
from QuantConnect.DataSource import *


class SEC13FHoldingsAlgorithm(QCAlgorithm):
    '''Example algorithm using the SEC Form 13F institutional holdings dataset as a source of alpha.
    It subscribes the dataset on three large, long-listed US equities, reads the aggregated
    institutional position on each (number of reporting managers, shares held and their market
    value), and holds the name the most institutions report.

    The 13F symbols returned by add_data are signals, not tradeable securities, so every name is
    added twice: once as the tradeable equity and once as the custom data subscribed on it.'''

    def initialize(self) -> None:
        # The dataset runs 2013-05-20 to 2026-05-29, which is where the SEC's structured 13F
        # tables begin. This window sits inside it and holds one whole quarterly cycle: the
        # September 2020 quarter fills in from a handful of managers in early October to
        # thousands by the middle of November, while the June quarter it replaces finishes.
        # That handover is the behaviour the cumulative shape exists for.
        self.set_start_date(2020, 10, 1)
        self.set_end_date(2020, 12, 31)
        self.set_cash(100000)

        # Tradeable equity symbol, keyed by the 13F data symbol subscribed on it.
        self._equity_by_data_symbol = {}

        # Latest number of institutional holders per reported quarter, keyed by equity symbol.
        self._holders_by_equity = {}

        self._invested = None

        # AAPL is one of the names whose aggregation was validated against published institutional
        # ownership. GOOGL is here on purpose too: its CUSIP is not in the security database, so it
        # is reachable only through the N-PORT ticker crosswalk, and seeing it arrive proves the
        # whole identity chain rather than just its first step.
        for ticker in ["AAPL", "GOOGL", "SPY"]:
            equity = self.add_equity(ticker, Resolution.DAILY).symbol
            data_symbol = self.add_data(SEC13FHoldings, equity).symbol
            self._equity_by_data_symbol[data_symbol] = equity

    def on_data(self, slice: Slice) -> None:
        # One filing date can carry several reported quarters of the same security, and the Slice
        # keeps one point per symbol, so slice.get would hand over only the newest quarter.
        # all_data carries every one of them.
        for holding in slice.all_data:
            if not isinstance(holding, SEC13FHoldings) or holding.holders is None:
                continue
            equity = self._equity_by_data_symbol.get(holding.symbol)
            if equity is None:
                continue

            # time is when the point was published, 03:00 ET the day after the filing date, and
            # period_end the quarter the numbers describe, typically 45 to 135 days earlier. Every
            # value is cumulative for period_end: it counts every manager that had reported the
            # security by then.
            self.log(f"{self.time:%Y-%m-%d} {equity.value} - Period: {holding.period_end:%Y-%m-%d}, Holders: {holding.holders}, Shares: {holding.shares}, HoldingValue: {holding.holding_value}")

            self._holders_by_equity.setdefault(equity, {})[holding.period_end] = holding.holders

        if not self._holders_by_equity:
            return

        # Hold the name the most institutions report. Breadth is the larger of the two newest
        # quarters, so a quarter that is still filling in does not hide the finished one: the same
        # rule the universe file applies. Rebalancing only when the leader changes keeps the demo
        # down to a handful of orders.
        def breadth(quarters):
            return max(quarters[period] for period in sorted(quarters)[-2:])

        leader = sorted(self._holders_by_equity.items(), key=lambda kvp: (-breadth(kvp[1]), kvp[0].value))[0][0]

        # A 13F point lands before the open on the day after its filing, which is not necessarily a
        # day the equity prints a bar, so the order waits for a price rather than firing against a
        # stale one.
        if leader == self._invested or leader not in slice.bars:
            return

        self._invested = leader
        self.log(f"{self.time:%Y-%m-%d} most widely held name is now {leader.value}, rotating into it")
        self.set_holdings(leader, 1, liquidate_existing_holdings=True)

    def on_order_event(self, order_event: OrderEvent) -> None:
        if order_event.status == OrderStatus.FILLED:
            self.debug(f"{self.time} - Filled: {order_event.symbol} {order_event.fill_quantity}")
