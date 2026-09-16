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
using System.Linq;
using NodaTime;
using QuantConnect.Data;
using QuantConnect.Data.UniverseSelection;
using QuantConnect.Util;

namespace QuantConnect.DataSource
{
    /// <summary>
    /// Universe selection data for the SEC Form 13F institutional holdings dataset: one
    /// <see cref="SEC13F"/> per security per release date, so the whole cross-section can be ranked
    /// or filtered by institutional ownership without subscribing security by security.
    ///
    /// The file is keyed by release date, not by reported quarter: a single release date carries
    /// filings for several quarters, because late filers and amendments keep arriving. Each
    /// security's quarters travel together in its <see cref="SEC13F.Holdings"/>, so the selection
    /// function sees one record per security rather than one per quarter.
    ///
    /// Each business day carries every security's live quarters forward, not only the ones filed
    /// that day. A quarter stops being carried once the next quarter's 45 day filing deadline has
    /// passed, and a security leaves after its delisting date, so a name nobody reports any more
    /// drops out rather than sitting in every file with its last quarter.
    /// </summary>
    public class SEC13FUniverse : BaseDataCollection
    {
        // The line: the identifier, the ticker, and then a SEC13F point in its own layout.
        private const int IdentifierColumns = 2;

        /// <summary>Location of the universe file: alternative/sec/13f/universe/{yyyyMMdd}.csv</summary>
        public override SubscriptionDataSource GetSource(SubscriptionDataConfig config, DateTime date, bool isLiveMode)
        {
            return new SubscriptionDataSource(
                System.IO.Path.Combine(
                    Globals.DataFolder,
                    "alternative",
                    "sec",
                    SEC13F.ReportFolder,
                    "universe",
                    $"{date.ToStringInvariant(DateFormat.EightCharacter)}.csv"
                ),
                SubscriptionTransportMedium.LocalFile,
                FileFormat.FoldingCollection
            );
        }

        /// <summary>Parses one universe line into the security's point for the release date.</summary>
        public override BaseData Reader(SubscriptionDataConfig config, string line, DateTime date, bool isLiveMode)
        {
            var csv = line.Split(',');
            if (csv.Length < IdentifierColumns)
            {
                return null;
            }

            // The release stamp is the file date rather than a column, so the point is read with a
            // stamp of its own put in front of the rest of the line. The file day stays the point
            // Time and the release time becomes its EndTime, which is when an algorithm can act on
            // it: the engine walks a universe file by the day it is named after.
            var stamp = date.ToStringInvariant(SEC13F.ReleaseFormat);
            var point = SEC13F.Parse($"{stamp},{string.Join(",", csv.Skip(IdentifierColumns))}");
            if (point == null)
            {
                return null;
            }

            point.EndTime = date + SEC13F.ReleaseTimeOfDay;

            point.Symbol = new Symbol(SecurityIdentifier.Parse(csv[0]), csv[1]);
            foreach (var holding in point.Holdings)
            {
                holding.Symbol = point.Symbol;
            }

            return point;
        }

        /// <summary>Sparse: universe files exist only on release dates.</summary>
        public override bool IsSparseData() => true;

        /// <summary>Default resolution.</summary>
        public override Resolution DefaultResolution() => Resolution.Daily;

        /// <summary>Supported resolutions (Daily only).</summary>
        public override List<Resolution> SupportedResolutions() => DailyResolution;

        /// <summary>Data time zone (Eastern, the SEC filing time zone).</summary>
        public override DateTimeZone DataTimeZone() => TimeZones.NewYork;

        /// <summary>Creates a copy of the instance.</summary>
        public override BaseData Clone()
        {
            return new SEC13FUniverse
            {
                Symbol = Symbol,
                Time = Time,
                EndTime = EndTime,
                Underlying = Underlying,
                FilteredContracts = FilteredContracts,
                Data = Data?.Select(point => point.Clone()).ToList()
            };
        }
    }
}
