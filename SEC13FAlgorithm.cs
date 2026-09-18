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
    /// It subscribes the dataset on three large, long-listed US equities and reads the individual
    /// positions managers reported on each, then holds the name the most managers report.
    ///
    /// The dataset publishes what each manager filed and nothing else, so the breadth this trades
    /// on is counted here, in the algorithm: a point is every position reported for the security on
    /// one filing date, and distinct managers are distinct CIKs among them. Counting rather than
    /// reading a published total is the whole shape of the dataset, since no filing states how many
    /// managers hold a name.
    ///
    /// The 13F symbols returned by AddData are signals, not tradeable securities, so every name is
    /// added twice: once as the tradeable equity and once as the custom data subscribed on it.
    /// </summary>
    public class SEC13FAlgorithm : QCAlgorithm
    {
        /// <summary>Tradeable equity symbol, keyed by the 13F data symbol subscribed on it.</summary>
        private readonly Dictionary<Symbol, Symbol> _equityByDataSymbol = new Dictionary<Symbol, Symbol>();

        /// <summary>
        /// Managers seen reporting each equity for the quarter being accumulated, by filer CIK.
        /// A manager files once per quarter, on a day of its own choosing, so breadth is built up
        /// across filing dates rather than read off any single one.
        /// </summary>
        private readonly Dictionary<Symbol, HashSet<int>> _managersByEquity = new Dictionary<Symbol, HashSet<int>>();

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
            SetStartDate(2020, 10, 1);
            SetEndDate(2020, 12, 31);
            SetCash(100000);

            // AAPL is one of the names whose identity chain was validated against published
            // institutional ownership. GOOGL is here on purpose too: its CUSIP is not in the
            // security database, so it is reachable only through the N-PORT ticker crosswalk, and
            // seeing it arrive proves the whole chain rather than just its first step.
            foreach (var ticker in new[] { "AAPL", "GOOGL", "SPY" })
            {
                var equity = AddEquity(ticker, Resolution.Daily).Symbol;
                var dataSymbol = AddData<SEC13FHoldings>(equity).Symbol;
                _equityByDataSymbol[dataSymbol] = equity;
                _managersByEquity[equity] = new HashSet<int>();
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

                // One point per filing date, carrying every position reported for the security that
                // day. Several managers file on the same day and a single manager can report the
                // security on more than one line, which the rules allow when the discretion
                // differs, so records outnumber managers.
                SEC13FHoldings point = slice[kvp.Key];
                var equity = kvp.Value;

                foreach (SEC13FHolding holding in point)
                {
                    // PeriodEnd is the quarter the position describes, typically 45 to 135 days
                    // before the filing date the point is stamped with. Option lines state the
                    // shares underlying the contracts and are left out of a share count.
                    Log($"{Time:yyyy-MM-dd} {equity.Value} - CIK {holding.ManagerCik} for {holding.PeriodEnd:yyyy-MM-dd}: " +
                        $"{holding.Amount} {holding.AmountType}, {holding.MarketValue:C0}{(holding.PutCall.HasValue ? $" ({holding.PutCall})" : "")}");

                    if (!holding.PutCall.HasValue && holding.AmountType == "SH")
                    {
                        _managersByEquity[equity].Add(holding.ManagerCik);
                    }
                }
            }

            // Hold the name the most managers report. Rebalancing only when the leader changes keeps
            // the demo down to a handful of orders.
            var leader = _managersByEquity
                .Where(kvp => kvp.Value.Count > 0)
                .OrderByDescending(kvp => kvp.Value.Count)
                .ThenBy(kvp => kvp.Key.Value)
                .Select(kvp => kvp.Key)
                .FirstOrDefault();

            // A 13F point arrives at midnight the day after its filing date, which is not
            // necessarily a day the equity prints a bar, so the order waits for a price rather than
            // firing against a stale one.
            if (leader == null || leader == _invested || !slice.Bars.ContainsKey(leader))
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
