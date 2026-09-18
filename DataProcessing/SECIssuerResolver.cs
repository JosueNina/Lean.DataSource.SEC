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
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using QuantConnect.Data.Auxiliary;
using QuantConnect.Interfaces;
using QuantConnect.Lean.Engine.DataFeeds;
using QuantConnect.Securities;

namespace QuantConnect.DataProcessing
{
    /// <summary>
    /// Resolves a company named by its SEC CIK and a CUSIP to the equity that traded on a date, and
    /// the ticker it traded under then, which names its file. The CIK and CUSIP columns come from the
    /// security database, which QuantConnect keeps wherever the job runs.
    /// </summary>
    internal sealed class SECIssuerResolver
    {
        private readonly IMapFileProvider _mapFileProvider;
        private readonly Dictionary<int, List<SecurityDefinition>> _securitiesByCik;
        private readonly Dictionary<string, List<SecurityDefinition>> _securitiesByCusip;
        private readonly Dictionary<SecurityIdentifier, MapFile> _mapFiles = new();
        private readonly Dictionary<(string Ticker, DateTime Date), SecurityIdentifier> _tickerOwners = new();

        /// <summary>Filings resolved by CIK, by CUSIP, and not at all, for the run summary.</summary>
        public long ResolvedByCik { get; private set; }
        public long ResolvedByCusip { get; private set; }
        public long Unresolved { get; private set; }

        /// <summary>
        /// Loads the security database's CIK column from the data folder. A data folder without the
        /// database, as in the unit tests, leaves only the ticker path.
        /// </summary>
        public SECIssuerResolver(IMapFileProvider mapFileProvider = null)
        {
            _mapFileProvider = mapFileProvider;
            if (_mapFileProvider == null)
            {
                // Zip, not disk: the security master ships map_files_<yyyyMMdd>.zip, and the disk
                // provider only sees loose csv files, so it would resolve almost nothing without failing.
                _mapFileProvider = new LocalZipMapFileProvider();
                _mapFileProvider.Initialize(new DefaultDataProvider());
            }

            var securityDatabasePath = Path.Combine(Globals.GetDataFolderPath("symbol-properties"), "security-database.csv");
            SecurityDefinition.TryRead(new DefaultDataProvider(), securityDatabasePath, out var definitions);

            definitions ??= new List<SecurityDefinition>();
            _securitiesByCik = definitions
                .Where(definition => definition.CIK.HasValue)
                .GroupBy(definition => definition.CIK.Value)
                .ToDictionary(group => group.Key, group => group.ToList());
            _securitiesByCusip = definitions
                .Where(definition => CusipBody(definition.CUSIP) != null)
                .GroupBy(definition => CusipBody(definition.CUSIP), StringComparer.OrdinalIgnoreCase)
                .ToDictionary(group => group.Key, group => group.ToList(), StringComparer.OrdinalIgnoreCase);
        }

        /// <summary>
        /// Resolves the subject of a filing that names its security by CUSIP rather than by ticker, as
        /// Schedules 13D and 13G do. The CIK decides the company and the CUSIP only picks the class
        /// among its listings: a CUSIP pointing at another company is an error in the filing, such as
        /// the filings on Spotify and Recursion that carry Ginkgo Bioworks' CUSIP. The CUSIP alone is
        /// tried only for a CIK the security database does not carry.
        /// </summary>
        public (SecurityIdentifier Security, string Ticker)? ResolveByCusip(int cik, string cusip, DateTime date)
        {
            var body = CusipBody(cusip);

            if (_securitiesByCik.TryGetValue(cik, out var definitions))
            {
                var trading = Trading(definitions, date);
                if (trading.Count > 1 && body != null)
                {
                    trading = trading.Where(candidate => string.Equals(CusipBody(candidate.Definition.CUSIP), body, StringComparison.OrdinalIgnoreCase)).ToList();
                }

                if (trading.Count == 1)
                {
                    ResolvedByCik++;
                    return (trading[0].Definition.SecurityIdentifier, trading[0].Ticker.ToLowerInvariant());
                }

                Unresolved++;
                return null;
            }

            if (body != null && _securitiesByCusip.TryGetValue(body, out definitions))
            {
                var trading = Trading(definitions, date);
                if (trading.Count == 1)
                {
                    ResolvedByCusip++;
                    return (trading[0].Definition.SecurityIdentifier, trading[0].Ticker.ToLowerInvariant());
                }
            }

            Unresolved++;
            return null;
        }

        /// <summary>The database rows trading under their own ticker on a date, one per security.</summary>
        private List<(SecurityDefinition Definition, string Ticker)> Trading(IEnumerable<SecurityDefinition> definitions, DateTime date)
        {
            return definitions
                .Select(definition => (Definition: definition, Ticker: TickerOf(definition.SecurityIdentifier, date)))
                .Where(candidate => candidate.Ticker != null && Equals(TickerOwner(candidate.Ticker, date), candidate.Definition.SecurityIdentifier))
                .GroupBy(candidate => candidate.Definition.SecurityIdentifier)
                .Select(group => group.First())
                .ToList();
        }

        /// <summary>The eight character body of a CUSIP, the form the security database stores, or null.</summary>
        internal static string CusipBody(string cusip)
        {
            var value = cusip?.Trim().Replace("-", "").Replace(" ", "");
            return value != null && (value.Length == 8 || value.Length == 9) && value.All(char.IsLetterOrDigit)
                ? value.Substring(0, 8).ToUpperInvariant()
                : null;
        }

        /// <summary>
        /// The ticker a security traded under on a date, or null when its map file does not cover it.
        /// No fallback to the last ticker: by then it can belong to another company.
        /// </summary>
        private string TickerOf(SecurityIdentifier security, DateTime date)
        {
            if (!_mapFiles.TryGetValue(security, out var mapFile))
            {
                _mapFiles[security] = mapFile = _mapFileProvider
                    .Get(AuxiliaryDataKey.Create(security))
                    .ResolveMapFile(security.Symbol, security.Date);
            }

            if (mapFile == null || !mapFile.Any() || date < mapFile.FirstDate || date > mapFile.DelistingDate)
            {
                return null;
            }

            var ticker = mapFile.GetMappedSymbol(date, null);
            return string.IsNullOrEmpty(ticker) ? null : ticker;
        }

        /// <summary>The security trading under a ticker on a date per the map files, or null.</summary>
        private SecurityIdentifier TickerOwner(string ticker, DateTime date)
        {
            var key = (ticker.ToUpperInvariant(), date.Date);
            if (!_tickerOwners.TryGetValue(key, out var owner))
            {
                var mapFile = _mapFileProvider
                    .Get(new AuxiliaryDataKey(Market.USA, SecurityType.Equity))
                    .ResolveMapFile(key.Item1, key.Item2);

                _tickerOwners[key] = owner = mapFile.Any() && date >= mapFile.FirstDate && date <= mapFile.DelistingDate
                    && string.Equals(mapFile.GetMappedSymbol(date, null), key.Item1, StringComparison.OrdinalIgnoreCase)
                    ? SecurityIdentifier.GenerateEquity(mapFile.FirstDate, mapFile.FirstTicker, Market.USA)
                    : null;
            }

            return owner;
        }

        /// <summary>A summary line for the run log.</summary>
        public string Summary()
        {
            var total = ResolvedByCik + ResolvedByCusip + Unresolved;
            return total == 0
                ? "no filings resolved"
                : string.Format(CultureInfo.InvariantCulture, "{0} filings: {1} by CIK, {2} by CUSIP, {3} unresolved ({4:P1})",
                    total, ResolvedByCik, ResolvedByCusip, Unresolved, (double)Unresolved / total);
        }
    }
}
