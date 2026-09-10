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
using NodaTime;
using ProtoBuf;
using QuantConnect.Data;
using QuantConnect.Data.UniverseSelection;
using QuantConnect.Util;

namespace QuantConnect.DataSource
{
    /// <summary>
    /// Universe selection data for the SEC Form 13F institutional holdings dataset. One record per
    /// security per release date, so the whole cross-section can be ranked or filtered by
    /// institutional ownership without subscribing security by security.
    ///
    /// The file is keyed by release date, not by reported quarter: a single release date carries
    /// filings for several quarters, because late filers and amendments keep arriving. Read
    /// PeriodEnd to know which quarter a record describes.
    ///
    /// Each business day carries every security's live quarters forward, not only the ones filed
    /// that day. A quarter stops being carried once the next quarter's 45 day filing deadline has
    /// passed, and a security leaves after its delisting date, so a name nobody reports any more
    /// drops out rather than sitting in every file with its last quarter.
    /// </summary>
    [ProtoContract(SkipConstructor = true)]
    public class SEC13FHoldingsUniverse : BaseDataCollection
    {
        private static readonly TimeSpan _period = TimeSpan.FromDays(1);

        // The frozen layout: the identifier, the ticker, the reported quarter, eight measures and
        // the confidential treatment flag.
        private const int ExpectedColumns = 12;

        /// <summary>
        /// Last day of the quarter the positions are reported for. Unlike the per-security class,
        /// this is carried in the file: one release date mixes several reported quarters, so it
        /// cannot be derived from Time.
        /// </summary>
        [ProtoMember(10)]
        public DateTime PeriodEnd { get; set; }

        /// <summary>
        /// Number of distinct managers, counted by filer CIK, that have reported the security for
        /// PeriodEnd. A manager counts once however many filings or CUSIPs it reports it under.
        /// </summary>
        [ProtoMember(11)]
        public decimal? Holders { get; set; }

        /// <summary>
        /// Shares held, summed over every reported line with share-type SH and no option flag.
        /// </summary>
        [ProtoMember(12)]
        public decimal? Shares { get; set; }

        /// <summary>
        /// Market value of the shares above as reported by the managers. This is also the data
        /// point's Value.
        /// </summary>
        [ProtoMember(13)]
        public decimal? HoldingValue { get; set; }

        /// <summary>Shares underlying reported call positions, kept separate from Shares.</summary>
        [ProtoMember(14)]
        public decimal? CallShares { get; set; }

        /// <summary>Shares underlying reported put positions, kept separate from Shares.</summary>
        [ProtoMember(15)]
        public decimal? PutShares { get; set; }

        /// <summary>Principal amount of debt instruments reported for the security (share-type PRN).</summary>
        [ProtoMember(16)]
        public decimal? PrincipalValue { get; set; }

        /// <summary>Shares over which the reporting managers hold sole voting authority.</summary>
        [ProtoMember(17)]
        public decimal? VotingSole { get; set; }

        /// <summary>Shares over which the reporting managers share voting authority.</summary>
        [ProtoMember(18)]
        public decimal? VotingShared { get; set; }

        /// <summary>
        /// True when at least one contributing filing withheld positions under confidential
        /// treatment.
        /// </summary>
        [ProtoMember(19)]
        public bool ConfidentialOmitted { get; set; }

        /// <summary>The time the data point ends and becomes available to the algorithm.</summary>
        public override DateTime EndTime => Time + _period;

        /// <summary>Location of the universe file: alternative/sec/13f/universe/{yyyyMMdd}.csv</summary>
        public override SubscriptionDataSource GetSource(SubscriptionDataConfig config, DateTime date, bool isLiveMode)
        {
            return new SubscriptionDataSource(
                Path.Combine(
                    Globals.DataFolder,
                    "alternative",
                    "sec",
                    SEC13FHoldings.ReportFolder,
                    "universe",
                    $"{date.ToStringInvariant(DateFormat.EightCharacter)}.csv"
                ),
                SubscriptionTransportMedium.LocalFile,
                FileFormat.FoldingCollection
            );
        }

        /// <summary>Parses one universe CSV line into a data point.</summary>
        public override BaseData Reader(SubscriptionDataConfig config, string line, DateTime date, bool isLiveMode)
        {
            var csv = line.Split(',');

            // A truncated line is skipped rather than thrown on: an exception out of Reader ends the
            // algorithm. The test is "fewer than" and not "not equal to", so a column appended in a
            // later revision of the file leaves every existing one readable instead of muting the
            // whole dataset.
            if (csv.Length < ExpectedColumns)
            {
                return null;
            }

            var holdingValue = csv[5].IfNotNullOrEmpty<decimal?>(s => decimal.Parse(s, NumberStyles.Any, CultureInfo.InvariantCulture));
            return new SEC13FHoldingsUniverse
            {
                Symbol = new Symbol(SecurityIdentifier.Parse(csv[0]), csv[1]),
                Time = date,
                PeriodEnd = DateTime.ParseExact(csv[2], "yyyyMMdd", CultureInfo.InvariantCulture),
                Holders = csv[3].IfNotNullOrEmpty<decimal?>(s => decimal.Parse(s, NumberStyles.Any, CultureInfo.InvariantCulture)),
                Shares = csv[4].IfNotNullOrEmpty<decimal?>(s => decimal.Parse(s, NumberStyles.Any, CultureInfo.InvariantCulture)),
                HoldingValue = holdingValue,
                CallShares = csv[6].IfNotNullOrEmpty<decimal?>(s => decimal.Parse(s, NumberStyles.Any, CultureInfo.InvariantCulture)),
                PutShares = csv[7].IfNotNullOrEmpty<decimal?>(s => decimal.Parse(s, NumberStyles.Any, CultureInfo.InvariantCulture)),
                PrincipalValue = csv[8].IfNotNullOrEmpty<decimal?>(s => decimal.Parse(s, NumberStyles.Any, CultureInfo.InvariantCulture)),
                VotingSole = csv[9].IfNotNullOrEmpty<decimal?>(s => decimal.Parse(s, NumberStyles.Any, CultureInfo.InvariantCulture)),
                VotingShared = csv[10].IfNotNullOrEmpty<decimal?>(s => decimal.Parse(s, NumberStyles.Any, CultureInfo.InvariantCulture)),
                ConfidentialOmitted = csv[11] == "1",
                Value = holdingValue ?? 0m
            };
        }

        /// <summary>Sparse: universe files exist only on filing dates.</summary>
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
            return new SEC13FHoldingsUniverse
            {
                Symbol = Symbol,
                Time = Time,
                Data = Data,
                Value = Value,
                PeriodEnd = PeriodEnd,
                Holders = Holders,
                Shares = Shares,
                HoldingValue = HoldingValue,
                CallShares = CallShares,
                PutShares = PutShares,
                PrincipalValue = PrincipalValue,
                VotingSole = VotingSole,
                VotingShared = VotingShared,
                ConfidentialOmitted = ConfidentialOmitted
            };
        }

        /// <summary>String representation for debugging.</summary>
        public override string ToString()
        {
            return $"{Symbol} - Period: {PeriodEnd:yyyy-MM-dd}, Holders: {Holders}, Shares: {Shares}";
        }
    }
}
