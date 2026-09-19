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
    It follows one manager, Pershing Square, through seven of the names it reports: it holds them
    all when the first quarter arrives, and from then on only those the manager added to.

    The dataset publishes what each manager filed and nothing else, so the change this trades on is
    worked out here: a point is every position reported for the security on one filing date, the
    manager's lines are picked out by CIK, and the quarter they describe is period_end.

    The 13F symbols returned by add_data are signals, not tradeable securities, so every name is
    added twice: once as the tradeable equity and once as the custom data subscribed on it.'''

    # Pershing Square Capital Management, and Pershing Square Inc., which has reported the same
    # positions since the June 2026 quarter while the former files only a notice. A manager is
    # followed by CIK, and a change of reporting entity is a change of CIK.
    MANAGERS = {1336528, 2026053}

    def initialize(self) -> None:
        # Two filings fall in this window: the March 2026 quarter, filed on 15 May, and the June
        # quarter, filed on 14 August. Each reaches the algorithm at midnight after its filing date.
        self.set_start_date(2026, 5, 1)
        self.set_end_date(2026, 8, 31)
        self.set_cash(100000)

        # The shares the manager reported for each equity, by the quarter they describe.
        self._shares_by_equity = {}

        self._rebalance = False

        for ticker in ["META", "UBER", "QSR", "MSFT", "BN", "HTZ", "AMZN"]:
            equity = self.add_equity(ticker, Resolution.DAILY).symbol
            self.add_data(SEC13FHoldings, equity)
            self._shares_by_equity[equity] = {}

    def on_data(self, slice: Slice) -> None:
        for data_symbol, point in slice.get(SEC13FHoldings).items():
            # The data symbol carries the equity it was subscribed on as its underlying.
            equity = data_symbol.underlying

            # One point per filing date, carrying every position every manager reported for the
            # security that day. An amendment would restate lines already counted and an option
            # line states the shares under the contracts, so both are left out of the share count.
            for holding in point:
                if (holding.manager_cik not in self.MANAGERS or holding.form_type != "13F-HR"
                        or holding.amount_type != "SH" or holding.put_call is not None):
                    continue

                shares = self._shares_by_equity[equity]
                shares[holding.period_end] = shares.get(holding.period_end, 0) + (holding.amount or 0)
                self._rebalance = True

                self.log(f"{self.time:%Y-%m-%d} {equity.value} - {holding.manager_name} reports "
                         f"{holding.amount:,.0f} shares, {holding.market_value:,.0f} USD, "
                         f"for {holding.period_end:%Y-%m-%d}")

        # A 13F point arrives at midnight the day after its filing date, which is not necessarily
        # a day the equities print a bar, so the orders wait for prices.
        if not self._rebalance or slice.bars.count == 0:
            return

        self._rebalance = False

        # With one quarter known, hold what the manager holds. With two, hold what it added to.
        selected = []
        for equity, shares in self._shares_by_equity.items():
            periods = sorted(shares)
            quarters = [shares[period] for period in periods]

            # The manager's trades, which no filing states: the change between two reported quarters.
            if len(quarters) > 1:
                self.log(f"{self.time:%Y-%m-%d} {equity.value}: {quarters[-2]:,.0f} -> {quarters[-1]:,.0f} shares "
                         f"({quarters[-1] / quarters[-2] - 1:+.1%}) between {periods[-2]:%Y-%m-%d} and {periods[-1]:%Y-%m-%d}")

            if quarters and (quarters[0] > 0 if len(quarters) == 1 else quarters[-1] > quarters[-2]):
                selected.append(equity)

        if not selected:
            self.liquidate()
            return

        self.log(f"{self.time:%Y-%m-%d} holding {', '.join(equity.value for equity in selected)}")
        self.set_holdings([PortfolioTarget(equity, 1 / len(selected)) for equity in selected],
                          liquidate_existing_holdings=True)

    def on_order_event(self, order_event: OrderEvent) -> None:
        if order_event.status == OrderStatus.FILLED:
            self.debug(f"{self.time} - Filled: {order_event.symbol} {order_event.fill_quantity}")
