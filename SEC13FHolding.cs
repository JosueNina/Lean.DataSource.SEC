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
using System.Globalization;
using ProtoBuf;
using QuantConnect.Data;
using QuantConnect.Python;
using QuantConnect.Util;

namespace QuantConnect.DataSource
{
    /// <summary>
    /// One reported quarter of a <see cref="SEC13F"/> point: every 13F filing for that quarter that
    /// was public by the point's timestamp, summed. Deliberately not a <see cref="QuantConnect.Data.BaseData"/>,
    /// so that it can ride inside the point: the data distributor registers every BaseData type
    /// exposing a DataSourceId as a protobuf sub type, and a nested sub type cannot be serialized.
    /// </summary>
    [ProtoContract(SkipConstructor = true)]
    public class SEC13FHolding : ISymbolProvider
    {
        /// <summary>
        /// The security the quarter is reported for, which is the point's own symbol. Carried so
        /// that pandas can flatten a point into one row per quarter, and kept out of the frame
        /// itself, where it would collide with the index.
        /// </summary>
        [PandasIgnore]
        public Symbol Symbol { get; set; }

        /// <summary>
        /// The quarter the positions are reported for, which is the SEC PERIODOFREPORT. It is
        /// carried rather than derived from the point's timestamp because the two are unrelated:
        /// late filings and amendments mean one publication day carries several different reported
        /// quarters, and the gap between them runs from zero to years.
        /// </summary>
        [ProtoMember(1)]
        public DateTime PeriodEnd { get; set; }

        /// <summary>
        /// The reported quarter as a label, such as 2020Q2. Derived from <see cref="PeriodEnd"/> and
        /// never stored, so it cannot disagree with it. The year comes first so that sorting the
        /// label sorts the quarters.
        /// </summary>
        public string Quarter => $"{PeriodEnd.Year}Q{(PeriodEnd.Month - 1) / 3 + 1}";

        /// <summary>
        /// Number of distinct managers, counted by filer CIK, that have reported the security for
        /// <see cref="PeriodEnd"/> by the point's timestamp. A manager counts once however many
        /// filings it makes, however many of the issuer's CUSIPs it names, and whether the filing
        /// that first names the security is an original or an amendment.
        /// </summary>
        [ProtoMember(2)]
        public decimal? Holders { get; set; }

        /// <summary>
        /// Shares held, summed over every reported line with share-type SH and no option flag.
        /// Principal amounts of debt (PRN) and option lines are deliberately excluded.
        /// </summary>
        [ProtoMember(3)]
        public decimal? Shares { get; set; }

        /// <summary>
        /// Market value of the shares above as reported by the managers, in whole dollars. The
        /// most reported quarter's value is also the point's Value.
        /// </summary>
        [ProtoMember(4)]
        public decimal? HoldingValue { get; set; }

        /// <summary>Shares underlying reported call positions, kept separate from Shares.</summary>
        [ProtoMember(5)]
        public decimal? CallShares { get; set; }

        /// <summary>Shares underlying reported put positions, kept separate from Shares.</summary>
        [ProtoMember(6)]
        public decimal? PutShares { get; set; }

        /// <summary>Principal amount of debt instruments reported for the security (share-type PRN).</summary>
        [ProtoMember(7)]
        public decimal? PrincipalValue { get; set; }

        /// <summary>Shares over which the reporting managers hold sole voting authority.</summary>
        [ProtoMember(8)]
        public decimal? VotingSole { get; set; }

        /// <summary>Shares over which the reporting managers share voting authority.</summary>
        [ProtoMember(9)]
        public decimal? VotingShared { get; set; }

        /// <summary>
        /// True when at least one contributing filing withheld positions under confidential
        /// treatment. Such a filing is incomplete by design and the withheld positions surface in
        /// a later publication, so the flag is carried rather than silently ignored.
        /// </summary>
        [ProtoMember(10)]
        public bool ConfidentialOmitted { get; set; }

        /// <summary>Reads one group of columns, starting at the reported quarter.</summary>
        internal static SEC13FHolding Parse(string[] csv, int offset)
        {
            return new SEC13FHolding
            {
                PeriodEnd = DateTime.ParseExact(csv[offset], "yyyyMMdd", CultureInfo.InvariantCulture),
                Holders = ParseMeasure(csv[offset + 1]),
                Shares = ParseMeasure(csv[offset + 2]),
                HoldingValue = ParseMeasure(csv[offset + 3]),
                CallShares = ParseMeasure(csv[offset + 4]),
                PutShares = ParseMeasure(csv[offset + 5]),
                PrincipalValue = ParseMeasure(csv[offset + 6]),
                VotingSole = ParseMeasure(csv[offset + 7]),
                VotingShared = ParseMeasure(csv[offset + 8]),
                ConfidentialOmitted = csv[offset + 9] == "1"
            };
        }

        /// <summary>Parses one measure column, where an empty field is an absent reading.</summary>
        internal static decimal? ParseMeasure(string value)
        {
            return value.IfNotNullOrEmpty<decimal?>(s => decimal.Parse(s, NumberStyles.Any, CultureInfo.InvariantCulture));
        }

        /// <summary>Creates a copy of the instance.</summary>
        public SEC13FHolding Clone() => (SEC13FHolding)MemberwiseClone();

        /// <summary>String representation for debugging.</summary>
        public override string ToString() => $"{Quarter}: {Holders} holders, {Shares} shares";
    }
}
