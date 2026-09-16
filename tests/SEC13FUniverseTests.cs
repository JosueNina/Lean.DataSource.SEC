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
using NUnit.Framework;
using QuantConnect.Data;
using QuantConnect.DataSource;

namespace QuantConnect.DataLibrary.Tests
{
    /// <summary>
    /// Unit tests for <see cref="SEC13FUniverse"/>, the release-date view of the dataset. Every case
    /// parses an in-line sample line, so the fixture needs no data on disk and is safe to run in CI.
    /// The one test that touches the processed output is skipped when that folder is not there yet.
    /// </summary>
    [TestFixture]
    public class SEC13FUniverseTests
    {
        private const string AppleSid = "AAPL R735QTJ8XC9X";

        // Frozen line layout: the identifier, the ticker, how many quarters the security carries,
        // and then that many groups of ten columns, oldest quarter first:
        // PeriodEnd,Holders,Shares,HoldingValue,CallShares,PutShares,PrincipalValue,VotingSole,VotingShared,ConfidentialOmitted
        private const string FullLine =
            AppleSid + ",AAPL,1,20231231,4812,10589324611,3210456789012,152340000,98760000,4500000,9012345678,1234567890,1";

        private const string EmptyValuesLine = AppleSid + ",AAPL,1,20231231,,,,,,,,,0";

        // The usual shape of a universe line during a handover: the finished quarter, which holds
        // the managers, and the one filling in behind it. A security never carries more than two.
        private const string TwoQuarterLine =
            AppleSid + ",AAPL,2," +
            "20230930,4800,48000,480000,0,0,0,0,0,1," +
            "20231231,12,120,1200,0,0,0,0,0,0";

        // Same release date, an amendment for a much older quarter. PeriodEnd cannot be derived.
        private const string LateAmendmentLine = AppleSid + ",AAPL,1,20180630,7,5000,120000,,,,,,0";

        private static readonly DateTime ReleaseDate = new DateTime(2024, 2, 14);

        private static SubscriptionDataConfig Config()
        {
            return new SubscriptionDataConfig(
                typeof(SEC13FUniverse),
                Symbol.Create("AAPL", SecurityType.Base, Market.USA),
                Resolution.Daily,
                TimeZones.NewYork,
                TimeZones.NewYork,
                false, false, false);
        }

        /// <summary>
        /// One universe line, read the way the folding collection reads it: the line becomes the
        /// security's own data point rather than a record of its own.
        /// </summary>
        private static SEC13F Read(string line, DateTime? date = null)
        {
            return new SEC13FUniverse().Reader(Config(), line, date ?? ReleaseDate, false) as SEC13F;
        }

        [Test]
        public void ReaderParsesEveryColumn()
        {
            var point = Read(FullLine);

            Assert.IsNotNull(point);
            Assert.AreEqual(SecurityIdentifier.Parse(AppleSid), point.Symbol.ID);
            Assert.AreEqual("AAPL", point.Symbol.Value);
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
        public void ALineIsOneRecordPerSecurityWithItsQuartersInside()
        {
            // The selection function is handed one record per security, not one per quarter: a
            // security with two live quarters would otherwise appear twice in the same file and the
            // second reading would silently win.
            var point = Read(TwoQuarterLine);

            Assert.IsNotNull(point);
            Assert.AreEqual(2, point.Holdings.Count);
            Assert.AreEqual(new[] { "2023Q3", "2023Q4" }, point.Holdings.Select(holding => holding.Quarter).ToArray());
            Assert.AreEqual(new[] { 4800m, 12m }, point.Holdings.Select(holding => holding.Holders).ToArray());

            // The finished quarter holds the managers while the new one fills in, so it is the one
            // a cross-section is ranked on. The newest is still reachable through Latest.
            Assert.AreEqual(new DateTime(2023, 9, 30), point.MostReported.PeriodEnd);
            Assert.AreEqual(new DateTime(2023, 12, 31), point.Latest.PeriodEnd);
            Assert.AreEqual(480000m, point.Value);
        }

        [Test]
        public void SymbolRoundTripsThroughSecurityIdentifier()
        {
            var point = Read(TwoQuarterLine);

            Assert.AreEqual(AppleSid, point.Symbol.ID.ToString());
            Assert.AreEqual(SecurityType.Equity, point.Symbol.SecurityType);
            Assert.IsTrue(point.Holdings.All(holding => holding.Symbol == point.Symbol),
                "every quarter names the security its line identified");
        }

        [Test]
        public void ThePointSpansTheFileDateAndEndsAtTheReleaseTime()
        {
            // The file name is the release date, so the stamp comes from the requested date and
            // never from a column: the point spans that day and becomes available at the release
            // time, the same instant the per-security lines carry. Ending the point at the release
            // rather than starting it there is what the engine expects of a universe file, and a
            // point that started there would be walked a day at a time from a three hour offset.
            var date = new DateTime(2021, 8, 16);
            var point = Read(FullLine, date);

            Assert.AreEqual(date, point.Time);
            Assert.AreEqual(date.Add(SEC13F.ReleaseTimeOfDay), point.EndTime);
        }

        [Test]
        public void TheCollectionIsAvailableAtTheReleaseTimeOfItsPoints()
        {
            // The engine builds the collection around the points it folded, taking their Time and
            // EndTime, so the release time reaches the selection through them rather than through
            // an override of its own. A clone has to carry it too, since the engine clones the
            // collection on its way to the algorithm.
            var point = Read(FullLine, ReleaseDate);
            var collection = new SEC13FUniverse
            {
                Symbol = Symbol.Create("SEC13F-USA", SecurityType.Base, Market.USA),
                Time = point.Time,
                EndTime = point.EndTime,
                Data = new List<BaseData> { point }
            };

            Assert.AreEqual(ReleaseDate.Add(SEC13F.ReleaseTimeOfDay), collection.EndTime);
            Assert.Greater(collection.EndTime, collection.Time);
            Assert.AreEqual(collection.EndTime, ((SEC13FUniverse)collection.Clone()).EndTime);
        }

        [Test]
        public void PeriodEndComesFromTheFileNotFromTime()
        {
            // One release date carries filings for several reported quarters, so PeriodEnd must be
            // read, not derived. Deriving it from Time would have given 2024-03-31 here.
            var current = Read(FullLine);
            var amendment = Read(LateAmendmentLine);

            Assert.AreEqual(current.Time, amendment.Time);
            Assert.AreEqual(new DateTime(2023, 12, 31), current.Latest.PeriodEnd);
            Assert.AreEqual(new DateTime(2018, 6, 30), amendment.Latest.PeriodEnd);
            Assert.AreNotEqual(current.Latest.PeriodEnd, amendment.Latest.PeriodEnd);
        }

        [Test]
        public void CloneCopiesEveryProperty()
        {
            var original = new SEC13FUniverse
            {
                Symbol = Symbol.Create("AAPL", SecurityType.Base, Market.USA),
                Time = ReleaseDate,
                Data = new List<BaseData> { Read(TwoQuarterLine), Read(LateAmendmentLine) }
            };

            var clone = original.Clone() as SEC13FUniverse;

            Assert.IsNotNull(clone);
            // Reflection rather than a hand-written list: a property added to the class but forgotten
            // in Clone() is exactly the bug this test has to catch, and a hand-written list cannot.
            SEC13FTests.AssertPropertiesAndFieldsAreEqual(original, clone);
        }

        [Test]
        public void ValueMirrorsTheMostReportedHoldingValue()
        {
            Assert.AreEqual(3210456789012m, Read(FullLine).Value);
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
            var line = AppleSid + $",AAPL,1,20231231,4812,10589324611,3210456789012,,,,,,{flag}";

            Assert.AreEqual(expected, Read(line).Holdings.Single().ConfidentialOmitted);
        }

        [Test]
        public void ATruncatedLineIsSkippedRatherThanThrown()
        {
            // An exception out of Reader ends the algorithm, so a short line has to come back as
            // null. It is the whole point of the guard: one malformed row must not take a live
            // strategy down.
            Assert.IsNull(Read(AppleSid + ",AAPL,1,20231231"));
            Assert.IsNull(Read(AppleSid), "a line with nothing but an identifier");
        }

        [Test]
        public void AQuarterCountThatDoesNotMatchTheLineIsSkipped()
        {
            // The count is what the group width is derived from, so a count the columns cannot
            // support would read a quarter out of the middle of another one.
            Assert.IsNull(Read(AppleSid + ",AAPL,2,20231231,4812,10589324611,3210456789012,0,0,0,0,0,0"));
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
            // The group width is derived from the line and its count, so a measure appended to
            // every group leaves each existing quarter where the reader expects it.
            var point = Read(
                AppleSid + ",AAPL,2," +
                "20230930,4800,48000,480000,0,0,0,0,0,0,99," +
                "20231231,12,120,1200,0,0,0,0,0,1,98");

            Assert.AreEqual(2, point.Holdings.Count);
            Assert.AreEqual(new DateTime(2023, 9, 30), point.Holdings[0].PeriodEnd);
            Assert.AreEqual(4800m, point.Holdings[0].Holders);
            Assert.AreEqual(new DateTime(2023, 12, 31), point.Holdings[1].PeriodEnd);
            Assert.IsTrue(point.Holdings[1].ConfidentialOmitted, "the flag is still the last column of its own group");
        }

        [Test]
        public void NonNumericMeasureThrows()
        {
            Assert.Throws<FormatException>(
                () => Read(AppleSid + ",AAPL,1,20231231,4812,10589324611,not-a-number,,,,,,0"));
        }

        [Test]
        public void BadPeriodEndThrows()
        {
            Assert.Throws<FormatException>(
                () => Read(AppleSid + ",AAPL,1,2023-12-31,4812,10589324611,3210456789012,,,,,,0"));
        }

        [Test]
        public void BadSecurityIdentifierThrows()
        {
            Assert.Catch<Exception>(
                () => Read("not-a-sid,AAPL,1,20231231,4812,10589324611,3210456789012,,,,,,0"));
        }

        [Test]
        public void GetSourceIsTheLocalReleaseDateFile()
        {
            var source = new SEC13FUniverse().GetSource(Config(), ReleaseDate, false);

            Assert.AreEqual(SubscriptionTransportMedium.LocalFile, source.TransportMedium);
            Assert.AreEqual(FileFormat.FoldingCollection, source.Format);
            Assert.IsTrue(
                SEC13FTests.Normalize(source.Source)
                    .EndsWith("alternative/sec/13f/universe/20240214.csv", StringComparison.Ordinal),
                $"Unexpected source '{source.Source}'");
        }

        [Test]
        public void ClassificationMatchesTheSpec()
        {
            var instance = new SEC13FUniverse();

            Assert.IsTrue(instance.IsSparseData());
            Assert.AreEqual(Resolution.Daily, instance.DefaultResolution());
            Assert.AreEqual(1, instance.SupportedResolutions().Count);
            Assert.AreEqual(Resolution.Daily, instance.SupportedResolutions()[0]);
            Assert.AreEqual(TimeZones.NewYork, instance.DataTimeZone());
        }

        [Test]
        public void ToStringIsNotEmpty()
        {
            Assert.IsFalse(string.IsNullOrWhiteSpace(Read(TwoQuarterLine).ToString()));
        }

        [Test]
        public void EveryProcessedUniverseRowParses()
        {
            var directory = SEC13FTestPaths.UniverseDirectory;
            if (directory == null || !Directory.Exists(directory))
            {
                Assert.Ignore($"No processed universe output at {SEC13FTestPaths.Describe(directory)}");
            }

            var files = Directory.GetFiles(directory, "*.csv", SearchOption.TopDirectoryOnly);
            if (files.Length == 0)
            {
                Assert.Ignore($"No universe files in {directory}");
            }

            // The quarter count is the third column of a universe line, after identifier and ticker.
            SEC13FTestPaths.RequirePackedLayout(files, 2);

            var instance = new SEC13FUniverse();
            var config = Config();
            var rows = 0;
            var quarters = 0;

            foreach (var file in files)
            {
                var name = Path.GetFileNameWithoutExtension(file);
                Assert.IsTrue(
                    DateTime.TryParseExact(name, "yyyyMMdd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var date),
                    $"Universe file '{name}' is not named yyyyMMdd");

                var securities = new HashSet<Symbol>();

                foreach (var line in File.ReadLines(file))
                {
                    if (string.IsNullOrWhiteSpace(line))
                    {
                        continue;
                    }

                    var point = instance.Reader(config, line, date, false) as SEC13F;

                    Assert.IsNotNull(point, $"{name}: '{line}' did not parse");
                    Assert.AreEqual(date, point.Time, $"{name}: Time is not the file date");
                    Assert.AreEqual(date.Add(SEC13F.ReleaseTimeOfDay), point.EndTime,
                        $"{name}: EndTime is not the release time");
                    Assert.IsNotEmpty(point.Holdings, $"{name}: a security carrying no quarter");
                    Assert.LessOrEqual(point.Holdings.Count, 2,
                        $"{name}: {point.Symbol.Value} carries more than the two live quarters");
                    Assert.AreEqual(point.MostReported.HoldingValue ?? 0m, point.Value,
                        $"{name}: Value does not mirror the most reported quarter");

                    // One record per security is the whole point of packing the quarters together:
                    // a second line for the same name would quietly replace the first.
                    Assert.IsTrue(securities.Add(point.Symbol), $"{name}: {point.Symbol.Value} appears twice");

                    var period = DateTime.MinValue;
                    foreach (var holding in point.Holdings)
                    {
                        Assert.LessOrEqual(holding.PeriodEnd, date, $"{name}: a quarter ends after the release date");
                        Assert.Greater(holding.PeriodEnd, period, $"{name}: {point.Symbol.Value} quarters out of order");
                        period = holding.PeriodEnd;
                        quarters++;
                    }

                    rows++;
                }
            }

            TestContext.Progress.WriteLine($"13f universe: {files.Length} files, {rows} securities, {quarters} quarters");
            Assert.Greater(rows, 0);
        }
    }
}
