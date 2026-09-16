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


class SEC13FUniverseSelectionAlgorithm(QCAlgorithm):
    '''Example algorithm demonstrating universe selection over the SEC Form 13F institutional
    holdings dataset: on every release date it ranks the cross-section by the number of
    institutional managers reporting each security and holds an equal-weight basket of the most
    widely held names.

    The universe file is keyed by the release date rather than by the reported quarter, so a single
    selection call mixes filings covering several different quarters. Each security arrives as one
    record carrying its live quarters together, and period_end says which quarter a reading
    describes: it typically sits 45 to 135 days behind the day the record arrives, with late
    amendments arriving years later. That lag is the product, not a defect.'''

    # Number of names held at a time.
    BASKET_SIZE = 5

    def initialize(self) -> None:
        # Data ADDED via universe selection is added with Daily resolution.
        self.universe_settings.resolution = Resolution.DAILY

        # The universe runs 2013-05-20 to 2026-05-29, forward filled across business days. One
        # quarter is enough here: it covers a whole handover, which is where the interesting
        # behaviour is, and it is the same window the per-security demo reads.
        self.set_start_date(2020, 10, 1)
        self.set_end_date(2020, 12, 31)
        self.set_cash(100000)

        self._selected = []
        self._rebalance = False

        self.add_universe(SEC13FUniverse, self._select)

    def _select(self, data: List[SEC13F]) -> List[Symbol]:
        # holders counts distinct filer CIKs, so it ranks names by how broadly institutions own
        # them rather than by how much money one large manager put in.
        # Every security arrives once, with the quarter that has finished filling in and the one
        # still filling travelling together in holdings. most_reported is the one more managers
        # have reported, which is the more complete picture and what a breadth ranking wants. An
        # algorithm chasing the freshest read would take latest.
        ranked = [datum for datum in data if datum.most_reported.holders is not None]
        selected = sorted(ranked,
                          key=lambda datum: (-datum.most_reported.holders,
                                             datum.symbol.value))[:self.BASKET_SIZE]

        for datum in selected:
            quarter = datum.most_reported
            self.log(f"{datum.symbol.value} - Period: {quarter.quarter} ({quarter.period_end:%Y-%m-%d}), Holders: {quarter.holders}, HoldingValue: {quarter.holding_value}")

        return [datum.symbol for datum in selected]

    def on_securities_changed(self, changes: SecurityChanges) -> None:
        self.log(str(changes))

        for security in changes.removed_securities:
            if security.symbol in self._selected:
                self._selected.remove(security.symbol)

        for security in changes.added_securities:
            self._selected.append(security.symbol)

        self._rebalance = True

    def on_data(self, slice: Slice) -> None:
        if not self._rebalance or not self._selected:
            return

        # A release date is not necessarily a day every name in the basket printed a bar, so the
        # rebalance takes the ones that did rather than waiting for a day when all of them do.
        # Blocking on the whole basket sounds safer and is not: one name that never prints, for a
        # holiday or a halt or simply because it is missing from the local data, stops the
        # algorithm from ever trading at all.
        priced = [symbol for symbol in self._selected if symbol in slice.bars]
        if not priced:
            return

        self._rebalance = False

        weight = 1.0 / len(priced)
        self.set_holdings([PortfolioTarget(symbol, weight) for symbol in priced],
                          liquidate_existing_holdings=True)
