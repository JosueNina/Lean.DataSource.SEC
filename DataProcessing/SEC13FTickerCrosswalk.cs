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
using System.Text.RegularExpressions;
using QuantConnect.Logging;

namespace QuantConnect.DataProcessing
{
    /// <summary>
    /// CUSIP to ticker crosswalk built from the SEC Form N-PORT data sets, used as the last step of
    /// the 13F identity chain.
    ///
    /// A 13F reports a CUSIP and nothing else, and LEAN's security database does not carry every
    /// issuer: it misses names as large as Alphabet, and for foreign issuers filing under a CINS the
    /// arithmetic US ISIN is wrong by construction. N-PORT publishes, for the same securities, both
    /// the CUSIP and the ticker the fund reported, so joining its tables yields a CUSIP to ticker
    /// map made entirely out of filings. On the March to May 2026 13F window it covers 98.1 percent
    /// of reported value, against 88.9 percent for the security database steps on their own.
    ///
    /// The ticker is free text written by fund administrators ("GOOGL", "GOOGL US", "goog"), so it
    /// is normalised and voted on across every fund that reported the security. Each entry keeps the
    /// day its data set begins, because a ticker only names a security on a date: META named a
    /// Roundhill ETF from June 2021 to January 2022, before Facebook took it.
    ///
    /// N-PORT begins in late 2019, so a security that stopped trading before then never appears
    /// here and still depends on the security database.
    /// </summary>
    public static class SEC13FTickerCrosswalk
    {
        private const string NPortUrlFormat =
            "https://www.sec.gov/files/dera/data/form-n-port-data-sets/{0}q{1}_nport.zip";

        private const string HoldingTable = "FUND_REPORTED_HOLDING.tsv";
        private const string IdentifierTable = "IDENTIFIERS.tsv";

        /// <summary>
        /// The file the built map is cached in, so the next run does not re-download gigabytes. Not
        /// a .csv, so nothing that walks the security files mistakes it for one.
        /// </summary>
        public const string CacheFileName = "nport-crosswalk.txt";

        /// <summary>The ticker the funds reported for a CUSIP, and the first day of the data set it came from.</summary>
        public readonly record struct Entry(string Ticker, DateTime Observed);

        /// <summary>
        /// Tickers arrive with a venue suffix in the Bloomberg style ("GOOGL US"), in lower case,
        /// and as the literal "N/A". Strip the suffix, upper case the rest and accept only what
        /// looks like a US equity ticker.
        /// </summary>
        private static readonly Regex VenueSuffix = new(@"\s+[A-Z]{2}$", RegexOptions.Compiled);
        private static readonly Regex TickerShape = new(@"^[A-Z.\-]{1,8}$", RegexOptions.Compiled);

        /// <summary>
        /// Loads the crosswalk cached in <paramref name="readDirectory"/>, building it from the
        /// newest <paramref name="quarters"/> N-PORT data sets when the cache does not cover them and
        /// writing the result to <paramref name="writeDirectory"/>. <paramref name="exists"/> says
        /// whether a data set is published and <paramref name="download"/> puts one on disk, both
        /// through the caller's rate limit and retries.
        /// </summary>
        public static Dictionary<string, Entry> Load(string readDirectory, string writeDirectory, int quarters,
            Func<string, bool> exists, Func<string, string, string> download)
        {
            var cached = ReadCache(Path.Combine(readDirectory, CacheFileName), out var cachedQuarters);

            var missing = FindAvailableQuarters(quarters, exists)
                .Where(quarter => !cachedQuarters.Contains(QuarterKey(quarter)))
                .ToList();

            if (missing.Count == 0)
            {
                Log.Trace($"SEC13FTickerCrosswalk.Load(): cache holds {cached.Count} CUSIPs, nothing to fetch");
                return cached;
            }

            var downloads = new List<string>();
            foreach (var (year, quarter) in missing)
            {
                var path = download(Url(year, quarter), $"{QuarterKey((year, quarter))}_nport.zip");
                downloads.Add(path);

                Fold(cached, QuarterStart(year, quarter), BuildFromQuarter(path, year, quarter));
                cachedQuarters.Add(QuarterKey((year, quarter)));
            }

            WriteCache(Path.Combine(writeDirectory, CacheFileName), cached, cachedQuarters);

            // The derived map is what is kept; each archive is several hundred megabytes.
            foreach (var path in downloads)
            {
                File.Delete(path);
            }

            Log.Trace($"SEC13FTickerCrosswalk.Load(): {cached.Count} CUSIPs after folding in " +
                      $"{string.Join(", ", missing.Select(QuarterKey))}");
            return cached;
        }

        /// <summary>
        /// Folds one data set's tickers into the map. The newest observation of a CUSIP wins whatever
        /// order the data sets arrive in: a first build walks back from today, while a refresh adds
        /// one newer quarter on top of the cache.
        /// </summary>
        internal static void Fold(Dictionary<string, Entry> map, DateTime observed,
            IEnumerable<KeyValuePair<string, string>> tickers)
        {
            foreach (var (cusip, ticker) in tickers)
            {
                if (!map.TryGetValue(cusip, out var held) || held.Observed <= observed)
                {
                    map[cusip] = new Entry(ticker, observed);
                }
            }
        }

        /// <summary>Walks back from the current quarter until it has found the requested number of data sets.</summary>
        private static List<(int Year, int Quarter)> FindAvailableQuarters(int quarters, Func<string, bool> exists)
        {
            var found = new List<(int, int)>();
            var probe = DateTime.UtcNow;

            // Twelve quarters of lookback is far more than the publication lag and stops the probe
            // from walking to 2019 if the SEC ever moves the files.
            for (var attempt = 0; attempt < 12 && found.Count < quarters; attempt++, probe = probe.AddMonths(-3))
            {
                var year = probe.Year;
                var quarter = (probe.Month - 1) / 3 + 1;
                if (exists(Url(year, quarter)))
                {
                    found.Add((year, quarter));
                }
            }

            if (found.Count == 0)
            {
                throw new InvalidOperationException(
                    "SEC13FTickerCrosswalk.FindAvailableQuarters(): no N-PORT data set is published");
            }

            return found;
        }

        /// <summary>
        /// Builds the map for one quarter. Two passes over the archive, because the tables join on
        /// HOLDING_ID and neither is sorted: the first collects the identifier rows that carry a
        /// ticker, only 6.9 percent of them, and the second votes those tickers onto the CUSIP each
        /// holding belongs to.
        /// </summary>
        private static IEnumerable<KeyValuePair<string, string>> BuildFromQuarter(string path, int year, int quarter)
        {
            using var archive = ZipFile.OpenRead(path);

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
                pair.Value.OrderByDescending(vote => vote.Value).ThenBy(vote => vote.Key, StringComparer.Ordinal).First().Key))
                .ToList();
        }

        /// <summary>
        /// The requested columns of one N-PORT table. Short rows are skipped rather than thrown on,
        /// so one malformed holding cannot stop the whole build.
        /// </summary>
        private static IEnumerable<string[]> ReadTable(ZipArchive archive, string table, params string[] wanted)
        {
            return SEC13FFiles.ReadTable(archive, "N-PORT", table, skipShortRows: true, wanted)
                .Select(row => wanted.Select(column => row.Fields[row.Columns[column]]).ToArray());
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

        /// <summary>
        /// The day a data set's tickers are taken to be observed on: its quarter's first day. A ticker
        /// renamed within that quarter fails the traded-under check in the downloader, so its CUSIP
        /// stays unresolved until a newer quarter is folded in. Rare, and it never resolves wrongly.
        /// </summary>
        private static DateTime QuarterStart(int year, int quarter)
            => new(year, quarter * 3 - 2, 1);

        private static Dictionary<string, Entry> ReadCache(string path, out HashSet<string> quarters)
        {
            var map = new Dictionary<string, Entry>(StringComparer.Ordinal);
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
                if (fields.Length == 3 && fields[0].Length == 9)
                {
                    map[fields[0]] = new Entry(fields[1],
                        DateTime.ParseExact(fields[2], DateFormat.EightCharacter, CultureInfo.InvariantCulture));
                }
            }

            return map;
        }

        private static void WriteCache(string path, Dictionary<string, Entry> map, HashSet<string> quarters)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path));

            var lines = new List<string> { "#" + string.Join(",", quarters.OrderBy(value => value, StringComparer.Ordinal)) };
            lines.AddRange(map.OrderBy(pair => pair.Key, StringComparer.Ordinal)
                .Select(pair => $"{pair.Key},{pair.Value.Ticker},{pair.Value.Observed.ToString(DateFormat.EightCharacter, CultureInfo.InvariantCulture)}"));

            SEC13FFiles.WriteThenMove(path, stream =>
            {
                using var writer = new StreamWriter(stream, leaveOpen: true);
                foreach (var line in lines)
                {
                    writer.WriteLine(line);
                }
            });
        }
    }
}
