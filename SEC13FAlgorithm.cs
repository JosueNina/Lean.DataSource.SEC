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
    public class SEC13FAlgorithm : QCAlgorithm
    {
        /// <summary>Tradeable equity symbol, keyed by the 13F data symbol subscribed on it.</summary>
        private readonly Dictionary<Symbol, Symbol> _equityByDataSymbol = new Dictionary<Symbol, Symbol>();

        /// <summary>Latest institutional breadth per equity, read off the most complete quarter.</summary>
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
                var dataSymbol = AddData<SEC13F>(equity).Symbol;
                _equityByDataSymbol[dataSymbol] = equity;
            }
        }

        /// <summary>
        /// OnData event is the primary entry point for your algorithm. Each new data point is here.
        /// </summary>
        /// <param name="slice">Slice object keyed by symbol containing the data</param>
        public override void OnData(Slice slice)
        {
            foreach (var kvp in _equityByDataSymbol)
            {
                if (!slice.ContainsKey(kvp.Key))
                {
                    continue;
                }

                // A release can restate several quarters of the same security at once and the point
                // carries every one of them, so indexing the Slice by symbol loses nothing.
                SEC13F point = slice[kvp.Key];
                var equity = kvp.Value;

                // Time is when the point was published, 03:00 ET the day after the filing date,
                // while PeriodEnd is the quarter a reading describes, typically 45 to 135 days
                // earlier. Every value is cumulative for its own PeriodEnd: it counts every manager
                // that had reported the security for that quarter by the publication time.
                foreach (var holding in point.Holdings)
                {
                    Log($"{Time:yyyy-MM-dd} {equity.Value} - Period: {holding.Quarter} ({holding.PeriodEnd:yyyy-MM-dd}), Holders: {holding.Holders}, Shares: {holding.Shares}, HoldingValue: {holding.HoldingValue}");
                }

                // MostReported is the quarter the most managers have reported, so a new quarter that
                // is still filling in cannot hide the finished one it replaces: the same rule the
                // universe file applies. An algorithm chasing the freshest read would take Latest.
                var mostReported = point.MostReported;
                if (mostReported.Holders.HasValue)
                {
                    _holdersByEquity[equity] = mostReported.Holders.Value;
                }
            }

            if (_holdersByEquity.Count == 0)
            {
                return;
            }

            // Hold the name the most institutions report. Rebalancing only when the leader changes
            // keeps the demo down to a handful of orders.
            var leader = _holdersByEquity
                .OrderByDescending(kvp => kvp.Value)
                .ThenBy(kvp => kvp.Key.Value)
                .First()
                .Key;

            // A 13F point lands before the open on the day after its filing, which is not necessarily
            // a day the equity prints a bar, so the order waits for a price rather than firing
            // against a stale one.
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
