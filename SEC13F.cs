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
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using NodaTime;
using ProtoBuf;
using QuantConnect.Data;
using QuantConnect.Python;
using QuantConnect.Util;

namespace QuantConnect.DataSource
{
    /// <summary>
    /// SEC Form 13F institutional holdings for one security, as they stood on the day the point was
    /// published. Institutional investment managers exercising discretion over at least 100 million
    /// dollars must file a Form 13F within 45 days of quarter end, listing the covered securities
    /// they hold. This data point answers how much of a name institutions hold and how many of them
    /// hold it.
    ///
    /// One release can restate SEVERAL quarters of a security at once, because late filings and
    /// amendments keep arriving, so a point carries them all in <see cref="Holdings"/>, one
    /// <see cref="SEC13FHolding"/> per reported quarter, oldest first. Seven percent of the points
    /// in the history carry more than one, up to 75. LEAN hands an algorithm a single data point
    /// per security per timestamp, so a line per quarter would have reached the algorithm one at a
    /// time and the rest would have been dropped.
    ///
    /// Every value is CUMULATIVE for its quarter: it counts every filing for that quarter that was
    /// public by the point timestamp, not only the filings made that day. The managers of a single
    /// quarter file over roughly fifty different days, so a per-day figure would answer a question
    /// nobody asks: on the busiest day of the March 2026 quarter 602 managers reported Apple for
    /// the first time, while 5,920 had reported it by then.
    ///
    /// Raw sums only. Quarter-over-quarter change, percentage of shares outstanding and
    /// concentration ratios are all derivable and are left to the algorithm.
    /// </summary>
    [ProtoContract(SkipConstructor = true, IgnoreListHandling = true)]
    public class SEC13F : BaseData, IEnumerable<SEC13FHolding>
    {
        // The frozen layout of a line: the release stamp, how many quarters it carries, and then
        // that many groups of columns. The group width is read from the line rather than assumed,
        // so a measure appended in a later revision leaves every existing file readable.
        private const int HeaderColumns = 2;
        internal const int GroupColumns = 10;
        internal const string ReleaseFormat = "yyyyMMdd HH:mm";

        private DateTime? _endTime;

        /// <summary>
        /// When the release became available. The per security files carry the release stamp in
        /// their first column, so it equals Time there; the universe files are named by their date
        /// and set it to the release time of that date.
        /// </summary>
        [ProtoMember(11)]
        public override DateTime EndTime
        {
            get => _endTime ?? Time;
            set => _endTime = value;
        }

        /// <summary>
        /// The quarters this release restated, oldest first. Never empty: a security with nothing
        /// to report on a day has no point at all that day.
        /// </summary>
        [ProtoMember(10)]
        public List<SEC13FHolding> Holdings { get; set; } = new();

        /// <summary>
        /// The quarter the most managers have reported, which is the most complete reading the
        /// point carries. While a new quarter fills in, over roughly six weeks, the quarter before
        /// it still holds more managers and is the one that answers how widely a name is held.
        /// This is the reading whose fields the pandas DataFrame carries as columns.
        /// </summary>
        public SEC13FHolding MostReported => Holdings
            .OrderByDescending(holding => holding.Holders ?? 0m)
            .ThenByDescending(holding => holding.PeriodEnd)
            .FirstOrDefault();

        /// <summary>
        /// The most recent quarter this release restated, which during a quarter handover is the
        /// one still filling in. Kept out of the DataFrame: a second expanded reading would prefix
        /// every column with the name of the property it came from.
        /// </summary>
        [PandasIgnore]
        public SEC13FHolding Latest => Holdings.Count == 0 ? null : Holdings[Holdings.Count - 1];

        /// <summary>Name of the dataset folder under alternative/sec/, which is where its files live.</summary>
        public static string ReportFolder => "13f";

        /// <summary>
        /// Time of day a release date points become available. EDGAR lists a day of filings at
        /// about 22:05 ET, the daily job reads them at 01:00 ET the next day and publishes by
        /// 03:00, so a filing is released the day after its filing date at this time, before the
        /// market opens.
        /// </summary>
        public static TimeSpan ReleaseTimeOfDay => TimeSpan.FromHours(3);

        /// <summary>Location of the source file: alternative/sec/13f/{ticker}.csv</summary>
        public override SubscriptionDataSource GetSource(SubscriptionDataConfig config, DateTime date, bool isLiveMode)
        {
            return new SubscriptionDataSource(
                Path.Combine(
                    Globals.DataFolder,
                    "alternative",
                    "sec",
                    ReportFolder,
                    $"{config.Symbol.Value.ToLowerInvariant()}.csv"
                ),
                SubscriptionTransportMedium.LocalFile,
                FileFormat.Csv
            );
        }

        /// <summary>Parses one line, which carries every quarter the release restated.</summary>
        public override BaseData Reader(SubscriptionDataConfig config, string line, DateTime date, bool isLiveMode)
        {
            var point = Parse(line);
            if (point == null)
            {
                return null;
            }

            point.Symbol = config.Symbol;
            foreach (var holding in point.Holdings)
            {
                holding.Symbol = config.Symbol;
            }

            return point;
        }

        /// <summary>
        /// Parses a line into a point that has no symbol yet, which is what the universe file needs
        /// once it has read the identifier of its own line. A malformed line gives null rather than
        /// throwing: an exception out of Reader ends the algorithm.
        /// </summary>
        internal static SEC13F Parse(string line)
        {
            var csv = line.Split(',');
            if (csv.Length < HeaderColumns + GroupColumns ||
                !int.TryParse(csv[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out var count) ||
                count <= 0)
            {
                return null;
            }

            var width = (csv.Length - HeaderColumns) / count;
            if (width < GroupColumns)
            {
                return null;
            }

            var point = new SEC13F
            {
                Time = DateTime.ParseExact(csv[0], ReleaseFormat, CultureInfo.InvariantCulture)
            };

            for (var i = 0; i < count; i++)
            {
                point.Holdings.Add(SEC13FHolding.Parse(csv, HeaderColumns + i * width));
            }

            point.Value = point.MostReported?.HoldingValue ?? 0m;
            return point;
        }

        /// <summary>Data time zone (Eastern, the SEC filing time zone).</summary>
        public override DateTimeZone DataTimeZone() => TimeZones.NewYork;

        /// <summary>Supported resolutions (Daily only, the quarterly cadence is modeled as Daily).</summary>
        public override List<Resolution> SupportedResolutions() => DailyResolution;

        /// <summary>Default resolution.</summary>
        public override Resolution DefaultResolution() => Resolution.Daily;

        /// <summary>Sparse data: one file per security, so missing-file logs are suppressed.</summary>
        public override bool IsSparseData() => true;

        /// <summary>Linked to Equities, so renames and delistings are applied via map files.</summary>
        public override bool RequiresMapping() => true;

        /// <summary>
        /// Iterates the quarters, which is what lets pandas flatten a point into rows. A point that
        /// arrived live was deserialized from protobuf, which carries the symbol of the point out of
        /// band and leaves its quarters without one, so it is filled in here rather than left null
        /// for the flattening to read.
        /// </summary>
        public IEnumerator<SEC13FHolding> GetEnumerator()
        {
            foreach (var holding in Holdings)
            {
                holding.Symbol ??= Symbol;
                yield return holding;
            }
        }

        IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();

        /// <summary>Creates a copy of the instance.</summary>
        public override BaseData Clone()
        {
            return new SEC13F
            {
                Symbol = Symbol,
                Time = Time,
                EndTime = EndTime,
                Value = Value,
                Holdings = Holdings.Select(holding => holding.Clone()).ToList()
            };
        }

        /// <summary>String representation for debugging.</summary>
        public override string ToString()
        {
            return $"{Symbol}({Time:yyyyMMdd}) :: [{string.Join(", ", Holdings)}]";
        }
    }
}
