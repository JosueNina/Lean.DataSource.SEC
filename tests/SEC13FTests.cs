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
using System.Reflection;
using NUnit.Framework;
using ProtoBuf;
using QuantConnect.Data;
using QuantConnect.DataSource;
using QuantConnect.Python;

namespace QuantConnect.DataLibrary.Tests
{
    /// <summary>
    /// Unit tests for <see cref="SEC13F"/> and <see cref="SEC13FHolding"/>. Every case parses an
    /// in-line sample line, so the fixture needs no data on disk and is safe to run in CI. The one
    /// test that touches the processed output is skipped when that folder is not there yet.
    /// </summary>
    [TestFixture]
    public class SEC13FTests
    {
        // Frozen line layout: the release stamp, how many quarters the release restated, and then
        // that many groups of ten columns, oldest quarter first:
        // PeriodEnd,Holders,Shares,HoldingValue,CallShares,PutShares,PrincipalValue,VotingSole,VotingShared,ConfidentialOmitted
        private const string FullLine =
            "20240214 03:00,1,20231231,4812,10589324611,3210456789012,152340000,98760000,4500000,9012345678,1234567890,1";

        // Same quarter, every optional measure withheld. Must produce nulls, never zeros.
        private const string EmptyValuesLine = "20240214 03:00,1,20231231,,,,,,,,,0";

        // One release restating three quarters at once, which is what the layout exists for. The
        // middle quarter is the one most managers have reported.
        private const string ThreeQuarterLine =
            "20240515 03:00,3," +
            "20230630,11,110,1100,0,0,0,0,0,0," +
            "20230930,4800,48000,480000,0,0,0,0,0,1," +
            "20231231,12,120,1200,0,0,0,0,0,0";

        private static SubscriptionDataConfig Config(string ticker = "AAPL")
        {
            return new SubscriptionDataConfig(
                typeof(SEC13F),
                Symbol.Create(ticker, SecurityType.Base, Market.USA),
                Resolution.Daily,
                TimeZones.NewYork,
                TimeZones.NewYork,
                false, false, false);
        }

        private static SEC13F Read(string line, string ticker = "AAPL")
        {
            return new SEC13F().Reader(Config(ticker), line, DateTime.UtcNow, false) as SEC13F;
        }

        [Test]
        public void ReaderParsesEveryColumn()
        {
            var point = Read(FullLine);

            Assert.IsNotNull(point);
            Assert.AreEqual(Symbol.Create("AAPL", SecurityType.Base, Market.USA), point.Symbol);
            Assert.AreEqual(new DateTime(2024, 2, 14, 3, 0, 0), point.Time);
            Assert.AreEqual(point.Time, point.EndTime);
            Assert.AreEqual(1, point.Holdings.Count);

            var quarter = point.Holdings[0];
            Assert.AreEqual(new DateTime(2023, 12, 31), quarter.PeriodEnd);
            Assert.AreEqual("2023Q4", quarter.Quarter);
            Assert.AreEqual(4812m, quarter.Holders);
            Assert.AreEqual(10589324611m, quarter.Shares);
            Assert.AreEqual(3210456789012m, quarter.HoldingValue);
            Assert.AreEqual(152340000m, quarter.CallShares);
            Assert.AreEqual(98760000m, quarter.PutShares);
            Assert.AreEqual(4500000m, quarter.PrincipalValue);
            Assert.AreEqual(9012345678m, quarter.VotingSole);
            Assert.AreEqual(1234567890m, quarter.VotingShared);
            Assert.IsTrue(quarter.ConfidentialOmitted);
            Assert.AreEqual(3210456789012m, point.Value);
        }

        [Test]
        public void EveryQuarterCarriesThePointsSymbol()
        {
            // A quarter reaches pandas as a row of its own, so it has to name the security it
            // belongs to rather than leave the frame to guess from the row before it.
            var point = Read(ThreeQuarterLine, "MSFT");

            Assert.IsTrue(point.Holdings.All(holding => holding.Symbol == point.Symbol));
        }

        [Test]
        public void OneReleaseCarriesEveryQuarterItRestated()
        {
            // The reason the layout changed. LEAN hands an algorithm a single data point per
            // security per timestamp, so the quarters of one release shipped as separate lines
            // reached the algorithm one at a time and the rest were dropped in silence.
            var point = Read(ThreeQuarterLine);

            Assert.IsNotNull(point);
            Assert.AreEqual(3, point.Holdings.Count);
            Assert.AreEqual(
                new[] { "2023Q2", "2023Q3", "2023Q4" },
                point.Holdings.Select(holding => holding.Quarter).ToArray(),
                "the quarters have to come out oldest first, the order the file writes them in");
            Assert.AreEqual(new[] { 11m, 4800m, 12m }, point.Holdings.Select(holding => holding.Holders).ToArray());
            Assert.IsTrue(point.Holdings.All(holding => holding.PeriodEnd < point.Time),
                "every quarter of a release ends before the release");
        }

        [Test]
        public void EnumeratingThePointWalksItsQuarters()
        {
            // The IEnumerable is what lets pandas flatten one point into one row per quarter.
            var point = Read(ThreeQuarterLine);

            Assert.AreEqual(point.Holdings, point.ToList());
        }

        [Test]
        public void MostReportedIsTheQuarterMostManagersHaveReported()
        {
            // While a new quarter fills in, over roughly six weeks, the quarter before it still
            // holds more managers and is the one that answers how widely a name is held. It is also
            // the reading whose columns the DataFrame carries, and the one Value mirrors.
            var point = Read(ThreeQuarterLine);

            Assert.AreEqual(new DateTime(2023, 9, 30), point.MostReported.PeriodEnd);
            Assert.AreEqual(4800m, point.MostReported.Holders);
            Assert.AreEqual(480000m, point.Value);
        }

        [Test]
        public void MostReportedPrefersTheNewerQuarterWhenTwoAreTied()
        {
            // Two quarters with the same number of managers is the handover finishing: the newer
            // one is the current reading, so the tie goes to it rather than to file order.
            var point = Read(
                "20240515 03:00,2," +
                "20230930,4800,48000,480000,0,0,0,0,0,0," +
                "20231231,4800,49000,490000,0,0,0,0,0,0");

            Assert.AreEqual(new DateTime(2023, 12, 31), point.MostReported.PeriodEnd);
            Assert.AreEqual(490000m, point.Value);
        }

        [Test]
        public void LatestIsTheNewestQuarterOfTheRelease()
        {
            var point = Read(ThreeQuarterLine);

            Assert.AreEqual(new DateTime(2023, 12, 31), point.Latest.PeriodEnd);
            Assert.AreSame(point.Holdings[point.Holdings.Count - 1], point.Latest);
        }

        [Test]
        public void LatestIsKeptOutOfTheDataFrame()
        {
            // Two expanded readings on one point would prefix every column with the name of the
            // property it came from, so only MostReported is expanded.
            Assert.IsNotNull(
                typeof(SEC13F).GetProperty(nameof(SEC13F.Latest)).GetCustomAttribute<PandasIgnoreAttribute>(),
                "Latest has to carry PandasIgnore");
            Assert.IsNull(
                typeof(SEC13F).GetProperty(nameof(SEC13F.MostReported)).GetCustomAttribute<PandasIgnoreAttribute>(),
                "MostReported is the reading the frame expands");
        }

        [Test]
        public void CloneCopiesEveryProperty()
        {
            var original = Read(ThreeQuarterLine);
            var clone = original.Clone() as SEC13F;

            Assert.IsNotNull(clone);
            // Reflection rather than a hand-written list: a property added to the class but forgotten
            // in Clone() is exactly the bug this test has to catch, and a hand-written list cannot.
            AssertPropertiesAndFieldsAreEqual(original, clone);
        }

        [Test]
        public void CloneCopiesTheQuartersRatherThanSharingThem()
        {
            // The clone is what reaches the algorithm, which may keep it. Sharing the quarters
            // would let the next release mutate a point that has already been read.
            var original = Read(ThreeQuarterLine);
            var clone = original.Clone() as SEC13F;

            Assert.AreNotSame(original.Holdings, clone.Holdings);
            for (var i = 0; i < original.Holdings.Count; i++)
            {
                Assert.AreNotSame(original.Holdings[i], clone.Holdings[i]);
            }
        }

        [Test]
        public void ValueMirrorsTheMostReportedHoldingValue()
        {
            Assert.AreEqual(Read(FullLine).MostReported.HoldingValue, Read(FullLine).Value);
            // A withheld HoldingValue leaves the point at zero rather than throwing or carrying null.
            Assert.AreEqual(0m, Read(EmptyValuesLine).Value);
        }

        [Test]
        public void EmptyNumericColumnsBecomeNullNotZero()
        {
            var quarter = Read(EmptyValuesLine)?.Holdings.SingleOrDefault();

            Assert.IsNotNull(quarter);
            Assert.IsNull(quarter.Holders);
            Assert.IsNull(quarter.Shares);
            Assert.IsNull(quarter.HoldingValue);
            Assert.IsNull(quarter.CallShares);
            Assert.IsNull(quarter.PutShares);
            Assert.IsNull(quarter.PrincipalValue);
            Assert.IsNull(quarter.VotingSole);
            Assert.IsNull(quarter.VotingShared);
        }

        [TestCase("1", true)]
        [TestCase("0", false)]
        [TestCase("", false)]
        public void ConfidentialOmittedParsesFlag(string flag, bool expected)
        {
            var line = $"20240214 03:00,1,20231231,4812,10589324611,3210456789012,,,,,,{flag}";

            Assert.AreEqual(expected, Read(line).Holdings.Single().ConfidentialOmitted);
        }

        [Test]
        public void TheConfidentialFlagBelongsToTheQuarterNotTheRelease()
        {
            // One release restates several quarters and only some of them may be missing positions
            // withheld under confidential treatment, so the flag rides on the quarter.
            var point = Read(ThreeQuarterLine);

            Assert.AreEqual(new[] { false, true, false },
                point.Holdings.Select(holding => holding.ConfidentialOmitted).ToArray());
        }

        [Test]
        public void ReleaseIsStampedAfterTheQuarterItReports()
        {
            // The lag from the reported quarter to publication is the product here: measured across
            // 11,761 filings it runs from 0 to 6,596 days with a median of 42. Whatever its size,
            // the release can never precede the quarter it reports.
            var point = Read(FullLine);

            Assert.GreaterOrEqual(point.Time.Date, point.Holdings.Single().PeriodEnd);
        }

        [Test]
        public void AVeryLongFilingLagIsLegitimateData()
        {
            // Amendments for old quarters keep arriving; the measured worst case is 6,596 days.
            // A row like this is late, not corrupt, and must parse.
            var point = Read("20260122 03:00,1,20071231,3,1000,50000,,,,,,0");
            var quarter = point.Holdings.Single();

            Assert.AreEqual(new DateTime(2026, 1, 22, 3, 0, 0), point.Time);
            Assert.AreEqual(new DateTime(2007, 12, 31), quarter.PeriodEnd);
            Assert.Greater((point.Time.Date - quarter.PeriodEnd).TotalDays, 6000);
        }

        [Test]
        public void AZeroDayFilingLagIsLegitimateData()
        {
            // The measured minimum is zero: filed on the reported quarter end itself.
            var point = Read("20240630 03:00,1,20240630,3,1000,50000,,,,,,0");

            Assert.AreEqual(point.Time.Date, point.Holdings.Single().PeriodEnd);
        }

        [TestCase("20240331", 2024, 3, 31)]
        [TestCase("20240630", 2024, 6, 30)]
        [TestCase("20240930", 2024, 9, 30)]
        [TestCase("20241231", 2024, 12, 31)]
        public void PeriodEndComesFromTheFileNotFromTime(string periodEnd, int year, int month, int day)
        {
            // Deriving the quarter from the release date would be wrong: one publication day
            // carries filings for several different reported quarters, so it has to be a column.
            var point = Read($"20260515 03:00,1,{periodEnd},3,1000,50000,,,,,,0");

            Assert.AreEqual(new DateTime(year, month, day), point.Holdings.Single().PeriodEnd);
            Assert.AreEqual(new DateTime(2026, 5, 15, 3, 0, 0), point.Time);
        }

        [TestCase("20200331", "2020Q1")]
        [TestCase("20200630", "2020Q2")]
        [TestCase("20200930", "2020Q3")]
        [TestCase("20201231", "2020Q4")]
        public void QuarterLabelsTheReportedPeriod(string periodEnd, string expected)
        {
            // The label is derived from PeriodEnd and never stored, so the two cannot disagree. The
            // year comes first so that sorting the label sorts the quarters.
            Assert.AreEqual(expected, Read($"20260515 03:00,1,{periodEnd},3,1000,50000,,,,,,0").Latest.Quarter);
        }

        [Test]
        public void ATruncatedLineIsSkippedRatherThanThrown()
        {
            // An exception out of Reader ends the algorithm, so a short line has to come back as
            // null. It is the whole point of the guard: one malformed row must not take a live
            // strategy down.
            Assert.IsNull(Read("20240214 03:00,1,20231231,4812"));
        }

        [TestCase("20240214 03:00,3,20231231,4812,1,1,0,0,0,0,0,0", TestName = "three quarters promised, one written")]
        [TestCase("20240214 03:00,0,20231231,4812,1,1,0,0,0,0,0,0", TestName = "no quarters at all")]
        [TestCase("20240214 03:00,-2,20231231,4812,1,1,0,0,0,0,0,0", TestName = "a negative count")]
        [TestCase("20240214 03:00,many,20231231,4812,1,1,0,0,0,0,0,0", TestName = "a count that is not a number")]
        public void AQuarterCountThatDoesNotMatchTheLineIsSkipped(string line)
        {
            // The count is what the group width is derived from, so a count the columns cannot
            // support would read a quarter out of the middle of another one. Same rule as a
            // truncated line: null rather than an exception, and the file carries on.
            Assert.IsNull(Read(line));
        }

        [Test]
        public void AnExtraColumnStillParses()
        {
            // The guard is "fewer than", not "not equal to". A column appended in a later revision
            // of the file must leave every existing one readable rather than mute the dataset.
            var point = Read(FullLine + ",99");

            Assert.IsNotNull(point);
            Assert.AreEqual(4812m, point.Holdings.Single().Holders);
        }

        [Test]
        public void AnExtraColumnPerQuarterStillParses()
        {
            // The group width is derived from the line and its count rather than assumed, so a
            // measure appended to every group leaves each existing quarter where the reader expects
            // it instead of shifting the ones behind it.
            var point = Read(
                "20240515 03:00,2," +
                "20230930,4800,48000,480000,0,0,0,0,0,0,99," +
                "20231231,12,120,1200,0,0,0,0,0,1,98");

            Assert.IsNotNull(point);
            Assert.AreEqual(2, point.Holdings.Count);
            Assert.AreEqual(new DateTime(2023, 9, 30), point.Holdings[0].PeriodEnd);
            Assert.AreEqual(4800m, point.Holdings[0].Holders);
            Assert.AreEqual(new DateTime(2023, 12, 31), point.Holdings[1].PeriodEnd);
            Assert.AreEqual(12m, point.Holdings[1].Holders);
            Assert.IsTrue(point.Holdings[1].ConfidentialOmitted, "the flag is still the last column of its own group");
        }

        [Test]
        public void NonNumericMeasureThrows()
        {
            // Column 0 is a well formed release stamp, so the line reaches the decimal parse rather
            // than failing on the timestamp before it.
            Assert.Throws<FormatException>(
                () => Read("20240214 03:00,1,20231231,4812,not-a-number,3210456789012,,,,,,0"));
        }

        [Test]
        public void BadTimeThrows()
        {
            Assert.Throws<FormatException>(
                () => Read("2023-10-01,1,20231231,4812,10589324611,3210456789012,,,,,,0"));
        }

        [Test]
        public void GetSourceIsTheLocalPerSecurityFile()
        {
            var source = new SEC13F().GetSource(Config(), new DateTime(2024, 2, 14), false);

            Assert.AreEqual(SubscriptionTransportMedium.LocalFile, source.TransportMedium);
            Assert.AreEqual(FileFormat.Csv, source.Format);
            Assert.IsTrue(
                Normalize(source.Source).EndsWith("alternative/sec/13f/aapl.csv", StringComparison.Ordinal),
                $"Unexpected source '{source.Source}'");
        }

        [Test]
        public void ClassificationMatchesTheSpec()
        {
            var instance = new SEC13F();

            Assert.IsTrue(instance.IsSparseData());
            Assert.IsTrue(instance.RequiresMapping());
            Assert.AreEqual(Resolution.Daily, instance.DefaultResolution());
            Assert.AreEqual(1, instance.SupportedResolutions().Count);
            Assert.AreEqual(Resolution.Daily, instance.SupportedResolutions()[0]);
            Assert.AreEqual(TimeZones.NewYork, instance.DataTimeZone());
        }

        [Test]
        public void ToStringNamesEveryQuarterItCarries()
        {
            var text = Read(ThreeQuarterLine).ToString();

            Assert.IsFalse(string.IsNullOrWhiteSpace(text));
            foreach (var quarter in new[] { "2023Q2", "2023Q3", "2023Q4" })
            {
                Assert.IsTrue(text.Contains(quarter, StringComparison.Ordinal), $"'{text}' does not name {quarter}");
            }
        }

        [Test]
        public void AProtobufRoundTripKeepsEveryQuarter()
        {
            // How the live data distributor moves a point: it registers the custom type as a
            // BaseData sub type, in ProtobufSubTypes here, and serializes through the BaseData
            // branch. A quarter lost on the way would reach a live algorithm as an absent reading
            // while the same algorithm saw it in a backtest, the worst way to lose data.
            var original = Read(ThreeQuarterLine);
            var bytes = ((BaseData)original).ProtobufSerialize(new Guid());

            using var stream = new MemoryStream(bytes);
            var deserialized = Serializer
                .DeserializeItems<BaseData>(stream, PrefixStyle.Base128, 1)
                .Single() as SEC13F;

            Assert.IsNotNull(deserialized, "the point did not come back as a SEC13F");
            Assert.AreEqual(original.Time, deserialized.Time);
            Assert.AreEqual(original.Holdings.Count, deserialized.Holdings.Count);

            for (var i = 0; i < original.Holdings.Count; i++)
            {
                var expected = original.Holdings[i];
                var actual = deserialized.Holdings[i];

                Assert.AreEqual(expected.PeriodEnd, actual.PeriodEnd, $"quarter {i}: PeriodEnd");
                Assert.AreEqual(expected.Quarter, actual.Quarter, $"quarter {i}: Quarter");
                Assert.AreEqual(expected.Holders, actual.Holders, $"quarter {i}: Holders");
                Assert.AreEqual(expected.Shares, actual.Shares, $"quarter {i}: Shares");
                Assert.AreEqual(expected.HoldingValue, actual.HoldingValue, $"quarter {i}: HoldingValue");
                Assert.AreEqual(expected.CallShares, actual.CallShares, $"quarter {i}: CallShares");
                Assert.AreEqual(expected.PutShares, actual.PutShares, $"quarter {i}: PutShares");
                Assert.AreEqual(expected.PrincipalValue, actual.PrincipalValue, $"quarter {i}: PrincipalValue");
                Assert.AreEqual(expected.VotingSole, actual.VotingSole, $"quarter {i}: VotingSole");
                Assert.AreEqual(expected.VotingShared, actual.VotingShared, $"quarter {i}: VotingShared");
                Assert.AreEqual(expected.ConfidentialOmitted, actual.ConfidentialOmitted, $"quarter {i}: ConfidentialOmitted");
            }

            // The readings derived from the quarters have to come back with them.
            Assert.AreEqual(original.MostReported.PeriodEnd, deserialized.MostReported.PeriodEnd);
            Assert.AreEqual(original.Latest.PeriodEnd, deserialized.Latest.PeriodEnd);
        }

        [Test]
        public void EveryProcessedRowParses()
        {
            var directory = SEC13FTestPaths.HoldingsDirectory;
            if (directory == null || !Directory.Exists(directory))
            {
                Assert.Ignore($"No processed output at {SEC13FTestPaths.Describe(directory)}");
            }

            var files = Directory.GetFiles(directory, "*.csv", SearchOption.TopDirectoryOnly);
            if (files.Length == 0)
            {
                Assert.Ignore($"No per-security files in {directory}");
            }

            // The quarter count is the second column of a per-security line.
            SEC13FTestPaths.RequirePackedLayout(files, 1);

            var instance = new SEC13F();
            var points = 0;
            var quarters = 0;

            foreach (var file in files)
            {
                var ticker = Path.GetFileNameWithoutExtension(file).ToUpperInvariant();
                var config = Config(ticker);
                var previous = DateTime.MinValue;

                foreach (var line in File.ReadLines(file))
                {
                    if (string.IsNullOrWhiteSpace(line))
                    {
                        continue;
                    }

                    var point = instance.Reader(config, line, DateTime.UtcNow, false) as SEC13F;

                    Assert.IsNotNull(point, $"{ticker}: '{line}' did not parse");
                    Assert.AreEqual(point.Time, point.EndTime, $"{ticker}: EndTime is not Time on {point.Time:yyyy-MM-dd}");
                    Assert.IsNotEmpty(point.Holdings, $"{ticker}: a point carrying no quarter on {point.Time:yyyy-MM-dd}");
                    Assert.AreEqual(point.MostReported.HoldingValue ?? 0m, point.Value,
                        $"{ticker}: Value does not mirror the most reported quarter");

                    var period = DateTime.MinValue;
                    foreach (var holding in point.Holdings)
                    {
                        Assert.AreEqual(point.Symbol, holding.Symbol, $"{ticker}: a quarter names another security");
                        Assert.GreaterOrEqual(point.Time.Date, holding.PeriodEnd,
                            $"{ticker}: released before the quarter it reports");

                        // Written oldest first and never twice, which is what makes Latest the
                        // newest quarter rather than whichever one the file happened to end on.
                        Assert.Greater(holding.PeriodEnd, period,
                            $"{ticker}: quarters out of order on {point.Time:yyyy-MM-dd}");
                        period = holding.PeriodEnd;
                        quarters++;
                    }

                    // LEAN's SubscriptionDataReader silently drops a point whose timestamp moves
                    // backwards, so a file out of chronological order loses rows with nothing in
                    // the log to show for it. Accumulating groups by quarter, which is NOT the
                    // order the file has to ship in, so this is worth asserting on real output.
                    // One line per release, so a repeated timestamp is a dropped point too.
                    Assert.Greater(point.Time, previous,
                        $"{ticker}: {point.Time:yyyy-MM-dd HH:mm} follows {previous:yyyy-MM-dd HH:mm}");
                    previous = point.Time;
                    points++;
                }
            }

            TestContext.Progress.WriteLine(
                $"13f: {files.Length} per-security files, {points} points, {quarters} quarters");
            Assert.Greater(points, 0);
        }

        [Test]
        public void ThePointsProtobufContractIsCompleteAndUnique()
        {
            // DeclaredOnly: BaseData numbers its own members from 1, and those are LEAN's to own.
            var members = ProtoMembers(typeof(SEC13F));

            Assert.IsNotNull(typeof(SEC13F).GetCustomAttribute<ProtoContractAttribute>(), "ProtoContract is missing");
            Assert.Greater(members.Count, 0, "no ProtoMember attributes found");
            Assert.AreEqual(members.Count, members.Distinct().Count(), "duplicate ProtoMember numbers");
            Assert.GreaterOrEqual(members.Min(), 10, "member numbers start at 10, BaseData reserves the low ones");
        }

        [Test]
        public void TheQuartersProtobufContractIsCompleteAndUnique()
        {
            // A quarter is deliberately not a BaseData, so that it can ride inside the point. It
            // owns its whole number space and starts at one.
            var members = ProtoMembers(typeof(SEC13FHolding));

            Assert.IsNotNull(typeof(SEC13FHolding).GetCustomAttribute<ProtoContractAttribute>(), "ProtoContract is missing");
            Assert.Greater(members.Count, 0, "no ProtoMember attributes found");
            Assert.AreEqual(members.Count, members.Distinct().Count(), "duplicate ProtoMember numbers");
        }

        [TestCase(typeof(SEC13F))]
        [TestCase(typeof(SEC13FHolding))]
        public void EveryStoredPropertyCarriesAProtoMember(Type type)
        {
            // A property added later without a member number would serialize as nothing, which is
            // indistinguishable from a real absent reading. Symbol is the one exception, the same
            // one BaseData itself makes: the distributor sends the symbol alongside the point
            // rather than inside it.
            var unnumbered = type
                .GetProperties(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly)
                .Where(property => property.CanRead && property.CanWrite && property.GetIndexParameters().Length == 0)
                .Where(property => property.Name != nameof(SEC13FHolding.Symbol))
                .Where(property => property.GetCustomAttribute<ProtoMemberAttribute>() == null)
                .Select(property => property.Name)
                .ToList();

            Assert.IsEmpty(unnumbered, $"{type.Name}: stored properties with no ProtoMember: {string.Join(", ", unnumbered)}");
        }

        private static List<int> ProtoMembers(Type type)
        {
            return type.GetProperties(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly)
                .Select(property => property.GetCustomAttribute<ProtoMemberAttribute>())
                .Where(attribute => attribute != null)
                .Select(attribute => attribute.Tag)
                .ToList();
        }

        /// <summary>
        /// Compares two instances member by member, so that a property added to a class and
        /// forgotten in its Clone() is caught without a hand-written list to keep up to date.
        /// </summary>
        internal static void AssertPropertiesAndFieldsAreEqual(object expected, object actual)
        {
            foreach (var property in expected.GetType().GetProperties())
            {
                AssertValueWasCopied(property.GetValue(expected), property.GetValue(actual), $"Property '{property.Name}'");
            }

            foreach (var field in expected.GetType().GetFields())
            {
                AssertValueWasCopied(field.GetValue(expected), field.GetValue(actual), $"Field '{field.Name}'");
            }
        }

        /// <summary>
        /// Compares one member. A point and a quarter are compared member by member rather than by
        /// reference: a clone that shared them would let the next release mutate a point the
        /// algorithm has already read, so equality here can never mean the same instance.
        /// </summary>
        private static void AssertValueWasCopied(object expected, object actual, string member)
        {
            switch (expected)
            {
                case SEC13F:
                case SEC13FHolding:
                    Assert.IsNotNull(actual, $"{member} was not copied");
                    Assert.AreEqual(expected.GetType(), actual.GetType(), $"{member} changed type");
                    AssertPropertiesAndFieldsAreEqual(expected, actual);
                    break;

                case IEnumerable<object> items:
                    var copies = (actual as IEnumerable<object>)?.ToList();
                    Assert.IsNotNull(copies, $"{member} was not copied");

                    var originals = items.ToList();
                    Assert.AreEqual(originals.Count, copies.Count, $"{member} changed length");
                    for (var i = 0; i < originals.Count; i++)
                    {
                        AssertValueWasCopied(originals[i], copies[i], $"{member}[{i}]");
                    }

                    break;

                default:
                    Assert.AreEqual(expected, actual, $"{member} was not copied");
                    break;
            }
        }

        internal static string Normalize(string path) => path.Replace('\\', '/');
    }

    /// <summary>
    /// Locates the processed 13F output produced by Phase 3. The tests that use it skip cleanly
    /// while the folder does not exist yet.
    /// </summary>
    internal static class SEC13FTestPaths
    {
        /// <summary>Root of the processed output, or null when the repository root cannot be found.</summary>
        public static string HoldingsDirectory
        {
            get
            {
                var root = Environment.GetEnvironmentVariable("SEC_13F_OUTPUT");
                if (!string.IsNullOrWhiteSpace(root))
                {
                    return root;
                }

                var repository = RepositoryRoot();
                return repository == null
                    ? null
                    : Path.Combine(repository, "output", "alternative", "sec", "13f");
            }
        }

        public static string UniverseDirectory
        {
            get
            {
                var holdings = HoldingsDirectory;
                return holdings == null ? null : Path.Combine(holdings, "universe");
            }
        }

        public static string Describe(string directory) => directory ?? "<repository root not found>";

        /// <summary>
        /// Skips the caller when the sample output on disk still carries one line per reported
        /// quarter, the layout that shipped before a release became a single point. Such a sample
        /// is an artefact of an older processor run rather than a parsing failure, and the only fix
        /// is to run the processor again. Everything else is left to fail in the caller.
        /// </summary>
        /// <param name="files">The sample files about to be read.</param>
        /// <param name="countColumn">Index of the quarter count, which the old layout does not have.</param>
        public static void RequirePackedLayout(IEnumerable<string> files, int countColumn)
        {
            var stale = files.Where(file => IsPrePackingLayout(FirstLine(file), countColumn)).ToList();
            if (stale.Count == 0)
            {
                return;
            }

            Assert.Ignore(
                $"{stale.Count} sample file(s) under output/ are still in the pre-packing layout, one line per " +
                $"reported quarter rather than one per release. Run the processor again to refresh them. " +
                $"First: {stale[0]}");
        }

        private static string FirstLine(string file)
        {
            return File.ReadLines(file).FirstOrDefault(line => !string.IsNullOrWhiteSpace(line));
        }

        // The column that now holds the quarter count used to hold the reported quarter itself, and
        // a count never looks like a date, so the two layouts cannot be confused for each other.
        private static bool IsPrePackingLayout(string line, int countColumn)
        {
            var csv = line?.Split(',');
            return csv != null
                   && csv.Length > countColumn
                   && DateTime.TryParseExact(csv[countColumn], "yyyyMMdd", CultureInfo.InvariantCulture,
                       DateTimeStyles.None, out _);
        }

        // The test binaries live in tests/bin/<config>/<framework>, so walk up to the folder that
        // holds the data source project file.
        private static string RepositoryRoot()
        {
            var directory = new DirectoryInfo(TestContext.CurrentContext.TestDirectory);

            while (directory != null)
            {
                if (File.Exists(Path.Combine(directory.FullName, "QuantConnect.DataSource.csproj")))
                {
                    return directory.FullName;
                }

                directory = directory.Parent;
            }

            return null;
        }
    }
}
