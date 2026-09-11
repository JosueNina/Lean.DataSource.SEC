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
using System.IO;
using NUnit.Framework;
using QuantConnect.Data;
using QuantConnect.DataSource;

namespace QuantConnect.DataLibrary.Tests
{
    /// <summary>
    /// Unit tests for <see cref="SEC13FHoldingsUniverse"/>. Every case parses an in-line sample
    /// line, so the fixture needs no data on disk and is safe to run in CI. The one test that
    /// touches the processed output is skipped when that folder is not there yet.
    /// </summary>
    [TestFixture]
    public class SEC13FHoldingsUniverseTests
    {
        private const string AppleSid = "AAPL R735QTJ8XC9X";

        // Frozen column order:
        // Sid,Ticker,PeriodEnd,Holders,Shares,HoldingValue,CallShares,PutShares,PrincipalValue,VotingSole,VotingShared,ConfidentialOmitted
        private const string FullLine =
            AppleSid + ",AAPL,20231231,4812,10589324611,3210456789012,152340000,98760000,4500000,9012345678,1234567890,1";

        private const string EmptyValuesLine = AppleSid + ",AAPL,20231231,,,,,,,,,0";

        // Same release date, an amendment for a much older quarter. PeriodEnd cannot be derived.
        private const string LateAmendmentLine =
            AppleSid + ",AAPL,20180630,7,5000,120000,,,,,,0";

        private static readonly DateTime ReleaseDate = new DateTime(2024, 2, 14);

        private static SubscriptionDataConfig Config()
        {
            return new SubscriptionDataConfig(
                typeof(SEC13FHoldingsUniverse),
                Symbol.Create("AAPL", SecurityType.Base, Market.USA),
                Resolution.Daily,
                TimeZones.NewYork,
                TimeZones.NewYork,
                false, false, false);
        }

        private static SEC13FHoldingsUniverse Read(string line, DateTime? date = null)
        {
            return new SEC13FHoldingsUniverse()
                .Reader(Config(), line, date ?? ReleaseDate, false) as SEC13FHoldingsUniverse;
        }

        [Test]
        public void ReaderParsesEveryColumn()
        {
            var point = Read(FullLine);

            Assert.IsNotNull(point);
            Assert.AreEqual(SecurityIdentifier.Parse(AppleSid), point.Symbol.ID);
            Assert.AreEqual("AAPL", point.Symbol.Value);
            Assert.AreEqual(new DateTime(2023, 12, 31), point.PeriodEnd);
            Assert.AreEqual(4812m, point.Holders);
            Assert.AreEqual(10589324611m, point.Shares);
            Assert.AreEqual(3210456789012m, point.HoldingValue);
            Assert.AreEqual(152340000m, point.CallShares);
            Assert.AreEqual(98760000m, point.PutShares);
            Assert.AreEqual(4500000m, point.PrincipalValue);
            Assert.AreEqual(9012345678m, point.VotingSole);
            Assert.AreEqual(1234567890m, point.VotingShared);
            Assert.IsTrue(point.ConfidentialOmitted);
            Assert.AreEqual(3210456789012m, point.Value);
        }

        [Test]
        public void SymbolRoundTripsThroughSecurityIdentifier()
        {
            var point = Read(FullLine);

            Assert.AreEqual(AppleSid, point.Symbol.ID.ToString());
            Assert.AreEqual(SecurityType.Equity, point.Symbol.SecurityType);
        }

        [Test]
        public void TimeIsTheRequestedReleaseDate()
        {
            // The file name is the release date, so Time comes from the requested date, never from
            // a column. EndTime is the release time on that date, when the daily job has published
            // the file, the same instant the per-security rows carry.
            var date = new DateTime(2021, 8, 16);
            var point = Read(FullLine, date);

            Assert.AreEqual(date, point.Time);
            Assert.AreEqual(date.Add(SEC13FHoldings.ReleaseTimeOfDay), point.EndTime);
            Assert.AreNotEqual(point.Time, point.EndTime);
            Assert.Greater(point.EndTime, point.Time);
        }

        [Test]
        public void PeriodEndComesFromTheFileNotFromTime()
        {
            // One release date carries filings for several reported quarters, so PeriodEnd must be
            // read, not derived. Deriving it from Time would have given 2024-03-31 here.
            var current = Read(FullLine);
            var amendment = Read(LateAmendmentLine);

            Assert.AreEqual(current.Time, amendment.Time);
            Assert.AreEqual(new DateTime(2023, 12, 31), current.PeriodEnd);
            Assert.AreEqual(new DateTime(2018, 6, 30), amendment.PeriodEnd);
            Assert.AreNotEqual(current.PeriodEnd, amendment.PeriodEnd);
        }

        [Test]
        public void CloneCopiesEveryProperty()
        {
            var original = Read(FullLine);
            var clone = original.Clone() as SEC13FHoldingsUniverse;

            Assert.IsNotNull(clone);
            // Reflection rather than a hand-written list: a property added to the class but forgotten
            // in Clone() is exactly the bug this test has to catch, and a hand-written list cannot.
            SEC13FHoldingsTests.AssertPropertiesAndFieldsAreEqual(original, clone);
        }

        [Test]
        public void ValueMirrorsHoldingValue()
        {
            Assert.AreEqual(3210456789012m, Read(FullLine).Value);
            // A withheld HoldingValue leaves the point at zero rather than throwing or carrying null.
            Assert.AreEqual(0m, Read(EmptyValuesLine).Value);
        }

        [Test]
        public void EmptyNumericColumnsBecomeNullNotZero()
        {
            var point = Read(EmptyValuesLine);

            Assert.IsNotNull(point);
            Assert.IsNull(point.Holders);
            Assert.IsNull(point.Shares);
            Assert.IsNull(point.HoldingValue);
            Assert.IsNull(point.CallShares);
            Assert.IsNull(point.PutShares);
            Assert.IsNull(point.PrincipalValue);
            Assert.IsNull(point.VotingSole);
            Assert.IsNull(point.VotingShared);
        }

        [TestCase("1", true)]
        [TestCase("0", false)]
        [TestCase("", false)]
        public void ConfidentialOmittedParsesFlag(string flag, bool expected)
        {
            var line = AppleSid + $",AAPL,20231231,4812,10589324611,3210456789012,,,,,,{flag}";

            Assert.AreEqual(expected, Read(line).ConfidentialOmitted);
        }

        [Test]
        public void ATruncatedLineIsSkippedRatherThanThrown()
        {
            // An exception out of Reader ends the algorithm, so a short line has to come back as
            // null. It is the whole point of the guard: one malformed row must not take a live
            // strategy down.
            Assert.IsNull(Read(AppleSid + ",AAPL,20231231"));
        }

        [Test]
        public void AnExtraColumnStillParses()
        {
            // The guard is "fewer than", not "not equal to". A column appended in a later revision
            // of the file must leave every existing one readable rather than mute the dataset.
            var point = Read(FullLine + ",99");

            Assert.IsNotNull(point);
            Assert.AreEqual(4812m, point.Holders);
        }

        [Test]
        public void NonNumericMeasureThrows()
        {
            Assert.Throws<FormatException>(
                () => Read(AppleSid + ",AAPL,20231231,4812,10589324611,not-a-number,,,,,,0"));
        }

        [Test]
        public void BadPeriodEndThrows()
        {
            Assert.Throws<FormatException>(
                () => Read(AppleSid + ",AAPL,2023-12-31,4812,10589324611,3210456789012,,,,,,0"));
        }

        [Test]
        public void BadSecurityIdentifierThrows()
        {
            Assert.Catch<Exception>(
                () => Read("not-a-sid,AAPL,20231231,4812,10589324611,3210456789012,,,,,,0"));
        }

        [Test]
        public void GetSourceIsTheLocalReleaseDateFile()
        {
            var source = new SEC13FHoldingsUniverse().GetSource(Config(), ReleaseDate, false);

            Assert.AreEqual(SubscriptionTransportMedium.LocalFile, source.TransportMedium);
            Assert.AreEqual(FileFormat.FoldingCollection, source.Format);
            Assert.IsTrue(
                SEC13FHoldingsTests.Normalize(source.Source)
                    .EndsWith("alternative/sec/13f/universe/20240214.csv", StringComparison.Ordinal),
                $"Unexpected source '{source.Source}'");
        }

        [Test]
        public void ClassificationMatchesTheSpec()
        {
            var instance = new SEC13FHoldingsUniverse();

            Assert.IsTrue(instance.IsSparseData());
            Assert.AreEqual(Resolution.Daily, instance.DefaultResolution());
            Assert.AreEqual(1, instance.SupportedResolutions().Count);
            Assert.AreEqual(Resolution.Daily, instance.SupportedResolutions()[0]);
            Assert.AreEqual(TimeZones.NewYork, instance.DataTimeZone());
        }

        [Test]
        public void ToStringIsNotEmpty()
        {
            Assert.IsFalse(string.IsNullOrWhiteSpace(Read(FullLine).ToString()));
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

            var instance = new SEC13FHoldingsUniverse();
            var config = Config();
            var rows = 0;

            foreach (var file in files)
            {
                var name = Path.GetFileNameWithoutExtension(file);
                Assert.IsTrue(
                    DateTime.TryParseExact(name, "yyyyMMdd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var date),
                    $"Universe file '{name}' is not named yyyyMMdd");

                foreach (var line in File.ReadLines(file))
                {
                    if (string.IsNullOrWhiteSpace(line))
                    {
                        continue;
                    }

                    var point = instance.Reader(config, line, date, false) as SEC13FHoldingsUniverse;

                    Assert.IsNotNull(point, $"{name}: '{line}' did not parse");
                    Assert.AreEqual(date, point.Time, $"{name}: Time is not the release date");
                    Assert.Greater(point.EndTime, point.Time, $"{name}: EndTime not after Time");
                    Assert.LessOrEqual(point.PeriodEnd, point.Time, $"{name}: reported quarter ends after the release date");
                    Assert.AreEqual(point.HoldingValue ?? 0m, point.Value, $"{name}: Value does not mirror HoldingValue");
                    rows++;
                }
            }

            TestContext.Progress.WriteLine($"13f universe: {files.Length} files, {rows} rows");
            Assert.Greater(rows, 0);
        }
    }
}
