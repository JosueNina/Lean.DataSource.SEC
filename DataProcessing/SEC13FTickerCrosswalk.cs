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
using System.IO.Compression;
using System.Linq;
using System.Net.Http;
using System.Text.RegularExpressions;
using QuantConnect.Logging;

namespace QuantConnect.DataProcessing
{
    /// <summary>
    /// CUSIP to ticker crosswalk built from the SEC Form N-PORT data sets, used as the last step of
    /// the 13F identity chain.
    ///
    /// Why it exists. A 13F reports a CUSIP and nothing else, and the first two steps of the chain
    /// resolve a CUSIP through LEAN's security database, which does not carry every issuer: it is
    /// missing names as large as Alphabet, and for foreign issuers filing under a CINS the
    /// arithmetic US ISIN is wrong by construction. N-PORT is the SEC's own fund holdings data set
    /// and it publishes, for the same securities, both the CUSIP and the ticker the fund reported.
    /// Joining the two tables therefore yields a CUSIP to ticker map made entirely out of filings,
    /// and a ticker resolves to a LEAN Symbol through the map files, which ship with the engine.
    ///
    /// Measured on the 2026 Q1 data set against the March to May 2026 13F window:
    ///
    ///     24,790 CUSIPs in the window, 74.68 trillion dollars of reported value
    ///      7,775 carry a ticker here, covering 98.1 percent of that value
    ///        911 of those are CINS foreign issuers, 4.2 percent of value, which the
    ///            constructed ISIN cannot reach at all
    ///      7,416 of the 7,775, or 95.4 percent, have every fund reporting the same ticker
    ///
    /// against 88.9 percent of value for the CUSIP and constructed-ISIN steps on their own.
    ///
    /// Two properties of the raw field matter. It is free text written by fund administrators, so
    /// it arrives as "GOOGL", "GOOGL US" and "goog" for the same security and has to be normalised
    /// and voted on rather than trusted row by row. And it is populated on only 6.9 percent of the
    /// identifier rows, so a naive one-row-per-CUSIP sample badly understates the coverage: the
    /// vote has to run across every fund that reported the security.
    ///
    /// The limit worth knowing. N-PORT begins in late 2019 while the 13F history begins in 2013 Q2,
    /// so a security that stopped trading before N-PORT existed will never appear here and still
    /// depends on the security database.
    /// </summary>
    public static class SEC13FTickerCrosswalk
    {
        private const string NPortUrlFormat =
            "https://www.sec.gov/files/dera/data/form-n-port-data-sets/{0}q{1}_nport.zip";

        private const string HoldingTable = "FUND_REPORTED_HOLDING.tsv";
        private const string IdentifierTable = "IDENTIFIERS.tsv";

        /// <summary>The file the built map is cached in, so a rerun does not re-download gigabytes.</summary>
        public const string CacheFileName = "nport-cusip-tickers.csv";

        /// <summary>
        /// Tickers arrive with a venue suffix in the Bloomberg style ("GOOGL US"), in lower case,
        /// and as the literal "N/A". Strip the suffix, upper case the rest and accept only what
        /// looks like a US equity ticker.
        /// </summary>
        private static readonly Regex VenueSuffix = new(@"\s+[A-Z]{2}$", RegexOptions.Compiled);
        private static readonly Regex TickerShape = new(@"^[A-Z.\-]{1,8}$", RegexOptions.Compiled);

        /// <summary>
        /// Loads the crosswalk, building it from the newest <paramref name="quarters"/> N-PORT data
        /// sets when the cache does not already cover them. Returns CUSIP (9 characters, as the SEC
        /// writes it) to ticker.
        /// </summary>
        public static Dictionary<string, string> Load(HttpClient client, string cacheDirectory, int quarters)
        {
            var cachePath = Path.Combine(cacheDirectory, CacheFileName);
            var cached = ReadCache(cachePath, out var cachedQuarters);

            var available = FindAvailableQuarters(client, quarters);
            var missing = available.Where(quarter => !cachedQuarters.Contains(QuarterKey(quarter))).ToList();

            if (missing.Count == 0)
            {
                Log.Trace($"SEC13FTickerCrosswalk.Load(): cache holds {cached.Count} CUSIPs, nothing to fetch");
                return cached;
            }

            foreach (var (year, quarter) in missing)
            {
                foreach (var (cusip, ticker) in BuildFromQuarter(client, year, quarter))
                {
                    // Newer quarters win, so a security that changed ticker keeps the recent one.
                    // The map files then take that ticker back to the right Symbol for an old date.
                    cached[cusip] = ticker;
                }

                cachedQuarters.Add(QuarterKey((year, quarter)));
            }

            WriteCache(cachePath, cached, cachedQuarters);
            Log.Trace($"SEC13FTickerCrosswalk.Load(): {cached.Count} CUSIPs after folding in " +
                      $"{string.Join(", ", missing.Select(QuarterKey))}");
            return cached;
        }

        /// <summary>Walks back from the current quarter until it has found the requested number of data sets.</summary>
        private static List<(int Year, int Quarter)> FindAvailableQuarters(HttpClient client, int quarters)
        {
            var found = new List<(int, int)>();
            var probe = DateTime.UtcNow;

            // Twelve quarters of lookback is far more than the publication lag and stops the probe
            // from walking to 2019 if the SEC ever moves the files.
            for (var attempt = 0; attempt < 12 && found.Count < quarters; attempt++, probe = probe.AddMonths(-3))
            {
                var year = probe.Year;
                var quarter = (probe.Month - 1) / 3 + 1;

                using var request = new HttpRequestMessage(HttpMethod.Head, Url(year, quarter));
                using var response = client.Send(request);
                if (response.IsSuccessStatusCode)
                {
                    found.Add((year, quarter));
                }
            }

            if (found.Count == 0)
            {
                throw new InvalidOperationException(
                    "SEC13FTickerCrosswalk.FindAvailableQuarters(): no N-PORT data set responded");
            }

            return found;
        }

        /// <summary>
        /// Builds the map for one quarter. Two passes over the archive, because the tables join on
        /// HOLDING_ID and neither is sorted: the first collects the identifier rows that actually
        /// carry a ticker, which is only 6.9 percent of them and therefore small enough to hold,
        /// and the second votes those tickers onto the CUSIP each holding belongs to.
        /// </summary>
        private static IEnumerable<KeyValuePair<string, string>> BuildFromQuarter(HttpClient client, int year, int quarter)
        {
            var url = Url(year, quarter);
            Log.Trace($"SEC13FTickerCrosswalk.BuildFromQuarter(): downloading {url}");

            using var stream = client.GetStreamAsync(url).GetAwaiter().GetResult();
            using var buffer = new MemoryStream();
            stream.CopyTo(buffer);
            buffer.Position = 0;

            using var archive = new ZipArchive(buffer, ZipArchiveMode.Read);

            var tickersByHolding = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var fields in ReadTable(archive, IdentifierTable, "HOLDING_ID", "IDENTIFIER_TICKER"))
            {
                var ticker = Normalize(fields[1]);
                if (ticker != null)
                {
                    tickersByHolding[fields[0]] = ticker;
                }
            }

            var votes = new Dictionary<string, Dictionary<string, int>>(StringComparer.Ordinal);
            foreach (var fields in ReadTable(archive, HoldingTable, "HOLDING_ID", "ISSUER_CUSIP"))
            {
                if (!tickersByHolding.TryGetValue(fields[0], out var ticker))
                {
                    continue;
                }

                var cusip = fields[1].Trim().ToUpperInvariant();

                // Foreign issuers without a CUSIP are masked as all zeros in this data set.
                if (cusip.Length != 9 || cusip == "000000000")
                {
                    continue;
                }

                if (!votes.TryGetValue(cusip, out var tally))
                {
                    votes[cusip] = tally = new Dictionary<string, int>(StringComparer.Ordinal);
                }

                tally.TryGetValue(ticker, out var count);
                tally[ticker] = count + 1;
            }

            Log.Trace($"SEC13FTickerCrosswalk.BuildFromQuarter(): {year}Q{quarter}: " +
                      $"{tickersByHolding.Count} holdings carried a ticker, {votes.Count} CUSIPs resolved");

            return votes.Select(pair => new KeyValuePair<string, string>(
                pair.Key,
                pair.Value.OrderByDescending(vote => vote.Value).ThenBy(vote => vote.Key, StringComparer.Ordinal).First().Key));
        }

        /// <summary>Streams the requested columns of one tab separated table out of the archive.</summary>
        private static IEnumerable<string[]> ReadTable(ZipArchive archive, string table, params string[] wanted)
        {
            var entry = archive.Entries.FirstOrDefault(
                            candidate => candidate.FullName.EndsWith(table, StringComparison.OrdinalIgnoreCase))
                        ?? throw new FileNotFoundException($"{table} is missing from the N-PORT archive");

            using var reader = new StreamReader(entry.Open());

            var header = reader.ReadLine()?.Split('\t');
            if (header == null)
            {
                throw new FormatException($"{table} is empty");
            }

            var indexes = wanted.Select(column =>
            {
                var index = Array.IndexOf(header, column);
                if (index < 0)
                {
                    throw new FormatException($"{table} has no column {column}");
                }

                return index;
            }).ToArray();

            var widest = indexes.Max();
            string line;
            while ((line = reader.ReadLine()) != null)
            {
                var fields = line.Split('\t');
                if (fields.Length <= widest)
                {
                    continue;
                }

                yield return indexes.Select(index => fields[index]).ToArray();
            }
        }

        /// <summary>Cleans one raw ticker, returning null when it is not usable.</summary>
        internal static string Normalize(string raw)
        {
            var ticker = VenueSuffix.Replace(raw.Trim().ToUpperInvariant(), string.Empty);
            return ticker.Length > 0 && ticker != "N/A" && TickerShape.IsMatch(ticker) ? ticker : null;
        }

        private static string Url(int year, int quarter)
            => string.Format(CultureInfo.InvariantCulture, NPortUrlFormat, year, quarter);

        private static string QuarterKey((int Year, int Quarter) quarter)
            => $"{quarter.Year}q{quarter.Quarter}";

        private static Dictionary<string, string> ReadCache(string path, out HashSet<string> quarters)
        {
            var map = new Dictionary<string, string>(StringComparer.Ordinal);
            quarters = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            if (!File.Exists(path))
            {
                return map;
            }

            foreach (var line in File.ReadLines(path))
            {
                if (line.StartsWith("#", StringComparison.Ordinal))
                {
                    quarters.UnionWith(line.TrimStart('#').Split(',', StringSplitOptions.RemoveEmptyEntries)
                        .Select(value => value.Trim()));
                    continue;
                }

                var fields = line.Split(',');
                if (fields.Length == 2 && fields[0].Length == 9)
                {
                    map[fields[0]] = fields[1];
                }
            }

            return map;
        }

        private static void WriteCache(string path, Dictionary<string, string> map, HashSet<string> quarters)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path));

            var lines = new List<string> { "#" + string.Join(",", quarters.OrderBy(value => value, StringComparer.Ordinal)) };
            lines.AddRange(map.OrderBy(pair => pair.Key, StringComparer.Ordinal)
                .Select(pair => $"{pair.Key},{pair.Value}"));

            var temporaryPath = path + ".tmp";
            File.WriteAllLines(temporaryPath, lines);
            File.Move(temporaryPath, path, overwrite: true);
        }
    }
}
