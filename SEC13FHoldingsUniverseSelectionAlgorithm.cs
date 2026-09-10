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
using QuantConnect.Algorithm;
using QuantConnect.DataSource;
using QuantConnect.Data.UniverseSelection;
using QuantConnect.Algorithm.Framework.Portfolio;

namespace QuantConnect.DataLibrary.Tests
{
    /// <summary>
    /// Example algorithm demonstrating universe selection over the SEC Form 13F institutional
    /// holdings dataset: on every release date it ranks the cross-section by the number of
    /// institutional managers reporting each security and holds an equal-weight basket of the most
    /// widely held names.
    ///
    /// The universe file is keyed by the release date rather than by the reported quarter, so a
    /// single selection call mixes filings covering several different quarters. PeriodEnd says
    /// which quarter each record describes, and it typically sits 45 to 135 days behind the day the
    /// record arrives, with late amendments arriving years later. That lag is the product, not a
    /// defect.
    /// </summary>
    public class SEC13FHoldingsUniverseSelectionAlgorithm : QCAlgorithm
    {
        /// <summary>Number of names held at a time.</summary>
        private const int BasketSize = 5;

        private readonly List<Symbol> _selected = new List<Symbol>();
        private bool _rebalance;

        /// <summary>
        /// Initialise the data and resolution required, as well as the cash and start-end dates.
        /// </summary>
        public override void Initialize()
        {
            // Data ADDED via universe selection is added with Daily resolution.
            UniverseSettings.Resolution = Resolution.Daily;

            // The universe runs 2013-05-20 to 2026-05-29, forward filled across business days. One
            // quarter is enough here: it covers a whole handover, which is where the interesting
            // behaviour is, and it is the same window the per-security demo reads.
            SetStartDate(2020, 10, 1);
            SetEndDate(2020, 12, 31);
            SetCash(100000);

            AddUniverse<SEC13FHoldingsUniverse>(data =>
            {
                // Holders counts distinct filer CIKs, so it ranks names by how broadly institutions
                // own them rather than by how much money one large manager put in.
                //
                // A security can appear twice on the same day: the quarter that has finished
                // filling in and the one still filling. The dataset ships both on purpose and
                // leaves the choice here, which is why every row carries its own PeriodEnd and
                // Holders. Keeping the row with more holders keeps the more complete picture,
                // which is what a breadth ranking wants. An algorithm chasing the freshest read
                // instead would keep the later PeriodEnd.
                var selected = (from SEC13FHoldingsUniverse datum in data
                                where datum.Holders.HasValue
                                group datum by datum.Symbol into security
                                select security.OrderByDescending(datum => datum.Holders.Value).First())
                    .OrderByDescending(datum => datum.Holders.Value)
                    .ThenBy(datum => datum.Symbol.Value)
                    .Take(BasketSize)
                    .ToList();

                foreach (var datum in selected)
                {
                    Log($"{datum.Symbol.Value} - Period: {datum.PeriodEnd:yyyy-MM-dd}, Holders: {datum.Holders}, HoldingValue: {datum.HoldingValue}");
                }

                return selected.Select(datum => datum.Symbol);
            });
        }

        /// <summary>
        /// Event fired each time that we add/remove securities from the data feed.
        /// </summary>
        /// <param name="changes">Security additions/removals for this time step</param>
        public override void OnSecuritiesChanged(SecurityChanges changes)
        {
            Log(changes.ToString());

            foreach (var security in changes.RemovedSecurities)
            {
                _selected.Remove(security.Symbol);
            }

            foreach (var security in changes.AddedSecurities)
            {
                _selected.Add(security.Symbol);
            }

            _rebalance = true;
        }

        /// <summary>
        /// OnData event is the primary entry point for your algorithm. Each new data point is here.
        /// </summary>
        /// <param name="slice">Slice object keyed by symbol containing the data</param>
        public override void OnData(Slice slice)
        {
            if (!_rebalance || _selected.Count == 0)
            {
                return;
            }

            // A release date is not necessarily a day every name in the basket printed a bar, so
            // the rebalance takes the ones that did rather than waiting for a day when all of them
            // do. Blocking on the whole basket sounds safer and is not: one name that never prints,
            // for a holiday or a halt or simply because it is missing from the local data, stops
            // the algorithm from ever trading at all.
            var priced = _selected.Where(symbol => slice.Bars.ContainsKey(symbol)).ToList();
            if (priced.Count == 0)
            {
                return;
            }

            _rebalance = false;

            var weight = 1m / priced.Count;
            SetHoldings(
                priced.Select(symbol => new PortfolioTarget(symbol, weight)).ToList(),
                liquidateExistingHoldings: true);
        }
    }
}
