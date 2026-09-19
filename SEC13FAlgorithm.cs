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

using System;
using System.Linq;
using System.Collections.Generic;
using QuantConnect.Data;
using QuantConnect.Orders;
using QuantConnect.Algorithm;
using QuantConnect.Algorithm.Framework.Portfolio;
using QuantConnect.DataSource;

namespace QuantConnect.DataLibrary.Tests
{
    /// <summary>
    /// Example algorithm using the SEC Form 13F institutional holdings dataset as a source of alpha.
    /// It follows one manager, Pershing Square, through seven of the names it reports: it holds
    /// them all when the first quarter arrives, and from then on only those the manager added to.
    ///
    /// The dataset publishes what each manager filed and nothing else, so the change this trades on
    /// is worked out here: a point is every position reported for the security on one filing date,
    /// the manager's lines are picked out by CIK, and the quarter they describe is PeriodEnd.
    ///
    /// The 13F symbols returned by AddData are signals, not tradeable securities, so every name is
    /// added twice: once as the tradeable equity and once as the custom data subscribed on it.
    /// </summary>
    public class SEC13FAlgorithm : QCAlgorithm
    {
        /// <summary>
        /// Pershing Square Capital Management, and Pershing Square Inc., which has reported the same
        /// positions since the June 2026 quarter while the former files only a notice. A manager is
        /// followed by CIK, and a change of reporting entity is a change of CIK.
        /// </summary>
        private static readonly HashSet<int> Managers = [1336528, 2026053];

        /// <summary>The shares the manager reported for each equity, by the quarter they describe.</summary>
        private readonly Dictionary<Symbol, SortedDictionary<DateTime, decimal>> _sharesByEquity = [];

        private bool _rebalance;

        /// <summary>
        /// Initialise the data and resolution required, as well as the cash and start-end dates.
        /// </summary>
        public override void Initialize()
        {
            // Two filings fall in this window: the March 2026 quarter, filed on 15 May, and the June
            // quarter, filed on 14 August. Each reaches the algorithm at midnight after its filing date.
            SetStartDate(2026, 5, 1);
            SetEndDate(2026, 8, 31);
            SetCash(100000);

            foreach (var ticker in new[] { "META", "UBER", "QSR", "MSFT", "BN", "HTZ", "AMZN" })
            {
                var equity = AddEquity(ticker, Resolution.Daily).Symbol;
                AddData<SEC13FHoldings>(equity);
                _sharesByEquity[equity] = [];
            }
        }

        /// <summary>
        /// OnData event is the primary entry point for your algorithm. Each new data point is here.
        /// </summary>
        /// <param name="slice">Slice object keyed by symbol containing the data</param>
        public override void OnData(Slice slice)
        {
            foreach (var (dataSymbol, point) in slice.Get<SEC13FHoldings>())
            {
                // The data symbol carries the equity it was subscribed on as its underlying.
                var equity = dataSymbol.Underlying;

                // One point per filing date, carrying every position every manager reported for the
                // security that day. An amendment would restate lines already counted and an option
                // line states the shares under the contracts, so both are left out of the share count.
                foreach (var holding in point.OfType<SEC13FHolding>().Where(holding =>
                             Managers.Contains(holding.ManagerCik) && holding.FormType == "13F-HR" &&
                             holding.AmountType == "SH" && !holding.PutCall.HasValue))
                {
                    var shares = _sharesByEquity[equity];
                    shares[holding.PeriodEnd] = shares.GetValueOrDefault(holding.PeriodEnd) + (holding.Amount ?? 0);
                    _rebalance = true;

                    Log($"{Time:yyyy-MM-dd} {equity.Value} - {holding.ManagerName} reports {holding.Amount:N0} shares, " +
                        $"{holding.MarketValue:C0}, for {holding.PeriodEnd:yyyy-MM-dd}");
                }
            }

            // A 13F point arrives at midnight the day after its filing date, which is not
            // necessarily a day the equities print a bar, so the orders wait for prices.
            if (!_rebalance || slice.Bars.Count == 0)
            {
                return;
            }

            _rebalance = false;

            // The manager's trades, which no filing states: the change between two reported quarters.
            foreach (var (equity, shares) in _sharesByEquity.Where(kvp => kvp.Value.Count > 1))
            {
                var (previous, latest) = (shares.Values.ElementAt(shares.Count - 2), shares.Values.Last());
                Log($"{Time:yyyy-MM-dd} {equity.Value}: {previous:N0} -> {latest:N0} shares ({latest / previous - 1:+0.0%;-0.0%}) " +
                    $"between {shares.Keys.ElementAt(shares.Count - 2):yyyy-MM-dd} and {shares.Keys.Last():yyyy-MM-dd}");
            }

            // With one quarter known, hold what the manager holds. With two, hold what it added to.
            var selected = _sharesByEquity
                .Where(kvp => kvp.Value.Count > 0)
                .Where(kvp =>
                {
                    var quarters = kvp.Value.Values.ToList();
                    return quarters.Count == 1 ? quarters[0] > 0 : quarters[^1] > quarters[^2];
                })
                .Select(kvp => kvp.Key)
                .ToList();

            if (selected.Count == 0)
            {
                Liquidate();
                return;
            }

            Log($"{Time:yyyy-MM-dd} holding {string.Join(", ", selected.Select(symbol => symbol.Value))}");
            SetHoldings(selected.Select(symbol => new PortfolioTarget(symbol, 1m / selected.Count)).ToList(),
                liquidateExistingHoldings: true);
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
