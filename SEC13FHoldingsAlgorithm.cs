/*
 * QUANTCONNECT.COM - Democratizing Finance, Empowering Individuals.
 * Lean Algorithmic Trading Engine v2.0. Copyright 2014 QuantConnect Corporation.
 *
 * Licensed under the Apache License, Version 2.0 (the "License");
 * you may not use this file except in compliance with the License.
 * You may obtain a copy of the License at http://www.apache.org/licenses/LICENSE-2.0
 *
 * Unless required by applicable law or agreed to in writing, software
 * distributed under the License is distributed on an "AS IS" BASIS,
 * WITHOUT WARRANTIES OR CONDITIONS OF ANY KIND, either express or implied.
 * See the License for the specific language governing permissions and
 * limitations under the License.
*/

using System.Linq;
using System.Collections.Generic;
using QuantConnect.Data;
using QuantConnect.Orders;
using QuantConnect.Algorithm;
using QuantConnect.DataSource;

namespace QuantConnect.DataLibrary.Tests
{
    /// <summary>
    /// Example algorithm using the SEC Form 13F institutional holdings dataset as a source of alpha.
    /// It subscribes the dataset on three large, long-listed US equities, reads the aggregated
    /// institutional position on each (number of reporting managers, shares held and their market
    /// value), and holds the name the most institutions report.
    ///
    /// The 13F symbols returned by AddData are signals, not tradeable securities, so every name is
    /// added twice: once as the tradeable equity and once as the custom data subscribed on it.
    /// </summary>
    public class SEC13FHoldingsAlgorithm : QCAlgorithm
    {
        /// <summary>Tradeable equity symbol, keyed by the 13F data symbol subscribed on it.</summary>
        private readonly Dictionary<Symbol, Symbol> _equityByDataSymbol = new Dictionary<Symbol, Symbol>();

        /// <summary>Latest reported number of institutional holders, keyed by equity symbol.</summary>
        private readonly Dictionary<Symbol, decimal> _holdersByEquity = new Dictionary<Symbol, decimal>();

        private Symbol _invested;

        /// <summary>
        /// Initialise the data and resolution required, as well as the cash and start-end dates.
        /// </summary>
        public override void Initialize()
        {
            // The dataset runs 2013-05-20 to 2026-05-29, which is where the SEC's structured 13F
            // tables begin. This window sits inside it and holds one whole quarterly cycle: the
            // September 2020 quarter fills in from a handful of managers in early October to
            // thousands by the middle of November, while the June quarter it replaces finishes.
            // That handover is the behaviour the cumulative shape exists for.
            SetStartDate(2020, 10, 1);
            SetEndDate(2020, 12, 31);
            SetCash(100000);

            // AAPL is one of the names whose aggregation was validated against published
            // institutional ownership. GOOGL is here on purpose too: its CUSIP is not in the
            // security database, so it is reachable only through the N-PORT ticker crosswalk, and
            // seeing it arrive proves the whole identity chain rather than just its first step.
            foreach (var ticker in new[] { "AAPL", "GOOGL", "SPY" })
            {
                var equity = AddEquity(ticker, Resolution.Daily).Symbol;
                var dataSymbol = AddData<SEC13FHoldings>(equity).Symbol;
                _equityByDataSymbol[dataSymbol] = equity;
            }
        }

        /// <summary>
        /// OnData event is the primary entry point for your algorithm. Each new data point is here.
        /// </summary>
        /// <param name="slice">Slice object keyed by symbol containing the data</param>
        public override void OnData(Slice slice)
        {
            var holdings = slice.Get<SEC13FHoldings>();

            foreach (var kvp in _equityByDataSymbol)
            {
                if (!holdings.ContainsKey(kvp.Key))
                {
                    continue;
                }

                var equity = kvp.Value;
                var holding = holdings[kvp.Key];

                // Point-in-time shape: Time and EndTime are both the filing date, and the quarter
                // the numbers describe is PeriodEnd. A point therefore arrives typically 45 to 135
                // days after the quarter it reports, with late amendments arriving years later, and
                // the algorithm sees it on the day it became public, exactly like a live manager
                // would. That lag is the product, not a defect.
                // Every value is cumulative for PeriodEnd: it counts every manager that had reported
                // the security by this date, not only the ones that filed today.
                Log($"{Time:yyyy-MM-dd} {equity.Value} - Period: {holding.PeriodEnd:yyyy-MM-dd}, Filed: {holding.EndTime:yyyy-MM-dd}, Holders: {holding.Holders}, Shares: {holding.Shares}, HoldingValue: {holding.HoldingValue}");

                if (holding.Holders.HasValue)
                {
                    _holdersByEquity[equity] = holding.Holders.Value;
                }
            }

            if (_holdersByEquity.Count == 0)
            {
                return;
            }

            // Hold the name the largest number of institutions report. Rebalancing only when the
            // leader changes keeps the demo down to a handful of orders.
            var leader = _holdersByEquity
                .OrderByDescending(kvp => kvp.Value)
                .ThenBy(kvp => kvp.Key.Value)
                .First()
                .Key;

            // A 13F lands on its filing date, which is not necessarily a day the equity printed a
            // bar, so the order waits for a price rather than firing against a stale one.
            if (leader == _invested || !slice.Bars.ContainsKey(leader))
            {
                return;
            }

            _invested = leader;
            Log($"{Time:yyyy-MM-dd} most widely held name is now {leader.Value}, rotating into it");
            SetHoldings(leader, 1, liquidateExistingHoldings: true);
        }

        /// <summary>
        /// Order fill event handler.
        /// </summary>
        /// <param name="orderEvent">Order event details</param>
        public override void OnOrderEvent(OrderEvent orderEvent)
        {
            if (orderEvent.Status == OrderStatus.Filled)
            {
                Debug($"{Time} - Filled: {orderEvent.Symbol} {orderEvent.FillQuantity}");
            }
        }
    }
}
