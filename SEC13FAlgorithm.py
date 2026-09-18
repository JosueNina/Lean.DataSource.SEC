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


class SEC13FAlgorithm(QCAlgorithm):
    '''Example algorithm using the SEC Form 13F institutional holdings dataset as a source of alpha.
    It subscribes the dataset on three large, long-listed US equities and reads the individual
    positions managers reported on each, then holds the name the most managers report.

    The dataset publishes what each manager filed and nothing else, so the breadth this trades on is
    counted here, in the algorithm: a point is every position reported for the security on one
    filing date, and distinct managers are distinct CIKs among them. Counting rather than reading a
    published total is the whole shape of the dataset, since no filing states how many managers hold
    a name.

    The 13F symbols returned by add_data are signals, not tradeable securities, so every name is
    added twice: once as the tradeable equity and once as the custom data subscribed on it.'''

    def initialize(self) -> None:
        # The dataset runs 2013-05-20 to 2026-05-29, which is where the SEC's structured 13F
        # tables begin. This window sits inside it and holds one whole quarterly cycle: the
        # September 2020 quarter fills in from a handful of managers in early October to
        # thousands by the middle of November, while the June quarter it replaces finishes.
        self.set_start_date(2020, 10, 1)
        self.set_end_date(2020, 12, 31)
        self.set_cash(100000)

        # Tradeable equity symbol, keyed by the 13F data symbol subscribed on it.
        self._equity_by_data_symbol = {}

        # Managers seen reporting each equity, by filer CIK. A manager files once per quarter, on a
        # day of its own choosing, so breadth is built up across filing dates rather than read off
        # any single one.
        self._managers_by_equity = {}

        # Shares reported per equity, and how many records the run read, so the end of the
        # algorithm can state what the dataset actually delivered.
        self._shares_by_equity = {}
        self._positions = 0
        self._option_lines = 0

        self._invested = None

        # AAPL is one of the names whose identity chain was validated against published
        # institutional ownership. GOOGL is here on purpose too: its CUSIP is not in the security
        # database, so it is reachable only through the N-PORT ticker crosswalk, and seeing it
        # arrive proves the whole chain rather than just its first step.
        for ticker in ["AAPL", "GOOGL", "SPY"]:
            equity = self.add_equity(ticker, Resolution.DAILY).symbol
            data_symbol = self.add_data(SEC13FHoldings, equity).symbol
            self._equity_by_data_symbol[data_symbol] = equity
            self._managers_by_equity[equity] = set()
            self._shares_by_equity[equity] = 0

    def on_data(self, slice: Slice) -> None:
        # One point per security per filing date, carrying every position reported for it that day.
        # Several managers file on the same day and a single manager can report the security on more
        # than one line, which the rules allow when the discretion differs, so records outnumber
        # managers.
        for data_symbol, holdings in slice.get(SEC13FHoldings).items():
            equity = self._equity_by_data_symbol[data_symbol]

            for holding in holdings:
                # period_end is the quarter the position describes, typically 45 to 135 days before
                # the filing date the point is stamped with. reported_value is the number the
                # manager filed and value_scale the power of ten that turns it into dollars, which
                # market_value applies; a filing that withheld the value leaves all three None.
                self._positions += 1
                if self._positions <= 5:
                    value = "withheld" if holding.market_value is None else f"{holding.market_value:,.0f} USD"
                    self.log(f"{self.time:%Y-%m-%d} {equity.value} - CIK {holding.manager_cik} "
                             f"for {holding.period_end:%Y-%m-%d} on {holding.form_type}: "
                             f"{holding.amount} {holding.amount_type}, {value}, "
                             f"discretion {holding.investment_discretion}, put_call {holding.put_call}")

                # Option lines state the shares underlying the contracts, not a holding of the
                # security, so they are counted apart rather than added to the share count.
                if holding.put_call is not None:
                    self._option_lines += 1
                elif holding.amount_type == "SH":
                    self._managers_by_equity[equity].add(holding.manager_cik)
                    self._shares_by_equity[equity] += holding.amount or 0

        # Hold the name the most managers report. Rebalancing only when the leader changes keeps the
        # demo down to a handful of orders.
        reported = {equity: ciks for equity, ciks in self._managers_by_equity.items() if ciks}
        if not reported:
            return

        leader = sorted(reported.items(), key=lambda kvp: (-len(kvp[1]), kvp[0].value))[0][0]

        # A 13F point arrives at midnight the day after its filing date, which is not necessarily
        # a day the equity prints a bar, so the order waits for a price rather than a stale one.
        if leader == self._invested or leader not in slice.bars:
            return

        self._invested = leader
        self.log(f"{self.time:%Y-%m-%d} most widely held name is now {leader.value}, rotating into it")
        self.set_holdings(leader, 1, liquidate_existing_holdings=True)

    def on_order_event(self, order_event: OrderEvent) -> None:
        if order_event.status == OrderStatus.FILLED:
            self.debug(f"{self.time} - Filled: {order_event.symbol} {order_event.fill_quantity}")

    def on_end_of_algorithm(self) -> None:
        # Breadth and size are counted here, from the records, because no 13F filing states either.
        self.log(f"{self._positions} reported positions read, {self._option_lines} of them option lines")
        for equity, managers in self._managers_by_equity.items():
            self.log(f"{equity.value}: {len(managers)} distinct managers, "
                     f"{self._shares_by_equity[equity]:,.0f} shares reported")
