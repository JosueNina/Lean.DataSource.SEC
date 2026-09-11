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
    /// Every value is CUMULATIVE for PeriodEnd: it counts every filing for that quarter that was
    /// public by Time, not only the filings made that day. The managers of a single quarter file
    /// over roughly fifty different days, so a per-day figure would answer a question nobody asks:
    /// on the busiest day of the March 2026 quarter 602 managers reported Apple for the first
    /// time, while 5,920 had reported it by then.
    ///
    /// Raw sums only. Quarter-over-quarter change, percentage of shares outstanding and
    /// concentration ratios are all derivable and are left to the algorithm.
    /// </summary>
    [ProtoContract(SkipConstructor = true)]
    public class SEC13FHoldings : BaseData
    {
        // The frozen layout: the release stamp, the reported quarter, eight measures and the
        // confidential treatment flag.
        private const int ExpectedColumns = 11;

        /// <summary>
        /// The quarter the positions are reported for, which is the SEC PERIODOFREPORT. It is
        /// carried rather than derived from Time because the two are unrelated: late filings and
        /// amendments mean one publication day carries several different reported quarters, and the
        /// gap between them runs from zero to years.
        /// </summary>
        [ProtoMember(10)]
        public DateTime PeriodEnd { get; set; }

        /// <summary>
        /// Number of distinct managers, counted by filer CIK, that have reported the security for
        /// PeriodEnd by Time. A manager counts once however many filings it makes, however many of
        /// the issuer's CUSIPs it names, and whether the filing that first names the security is an
        /// original or an amendment.
        /// </summary>
        [ProtoMember(11)]
        public decimal? Holders { get; set; }

        /// <summary>
        /// Shares held, summed over every reported line with share-type SH and no option flag.
        /// Principal amounts of debt (PRN) and option lines are deliberately excluded.
        /// </summary>
        [ProtoMember(12)]
        public decimal? Shares { get; set; }

        /// <summary>
        /// Market value of the shares above as reported by the managers, in whole dollars. This is
        /// also the data point's Value.
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
        /// treatment. Such a filing is incomplete by design and the withheld positions surface in
        /// a later publication, so the flag is carried rather than silently ignored.
        /// </summary>
        [ProtoMember(19)]
        public bool ConfidentialOmitted { get; set; }

        /// <summary>Name of the dataset's folder under alternative/sec/, which is where its files live.</summary>
        public static string ReportFolder => "13f";

        /// <summary>
        /// Time of day a release date's rows become available. EDGAR lists a day's filings at about
        /// 22:05 ET, the daily job reads them at 01:00 ET the next day and publishes by 03:00, so a
        /// filing is released the day after its filing date at this time, before the market opens.
        /// </summary>
        public static TimeSpan ReleaseTimeOfDay => TimeSpan.FromHours(3);

        /// <summary>Parses one measure column, where an empty field is an absent reading.</summary>
        internal static decimal? ParseMeasure(string value)
        {
            return value.IfNotNullOrEmpty<decimal?>(s => decimal.Parse(s, NumberStyles.Any, CultureInfo.InvariantCulture));
        }

        /// <summary>Creates a new default instance.</summary>
        public SEC13FHoldings()
        {
        }

        /// <summary>Parses one already split CSV line into a data point.</summary>
        public SEC13FHoldings(string[] csv)
        {
            Time = DateTime.ParseExact(csv[0], "yyyyMMdd HH:mm", CultureInfo.InvariantCulture);
            PeriodEnd = DateTime.ParseExact(csv[1], "yyyyMMdd", CultureInfo.InvariantCulture);
            Holders = ParseMeasure(csv[2]);
            Shares = ParseMeasure(csv[3]);
            HoldingValue = ParseMeasure(csv[4]);
            CallShares = ParseMeasure(csv[5]);
            PutShares = ParseMeasure(csv[6]);
            PrincipalValue = ParseMeasure(csv[7]);
            VotingSole = ParseMeasure(csv[8]);
            VotingShared = ParseMeasure(csv[9]);
            ConfidentialOmitted = csv[10] == "1";
            Value = HoldingValue ?? 0m;
        }

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

        /// <summary>Parses the data from the line provided and loads it into LEAN.</summary>
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

            return new SEC13FHoldings(csv) { Symbol = config.Symbol };
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

        /// <summary>Creates a copy of the instance.</summary>
        public override BaseData Clone()
        {
            return new SEC13FHoldings
            {
                Symbol = Symbol,
                Time = Time,
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
