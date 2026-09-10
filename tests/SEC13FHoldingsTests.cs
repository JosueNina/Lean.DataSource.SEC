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
using System.IO;
using System.Linq;
using System.Reflection;
using NUnit.Framework;
using ProtoBuf;
using QuantConnect.Data;
using QuantConnect.DataSource;

namespace QuantConnect.DataLibrary.Tests
{
    /// <summary>
    /// Unit tests for <see cref="SEC13FHoldings"/>. Every case parses an in-line sample line, so
    /// the fixture needs no data on disk and is safe to run in CI. The one test that touches the
    /// processed output is skipped when that folder is not there yet.
    /// </summary>
    [TestFixture]
    public class SEC13FHoldingsTests
    {
        // Frozen column order:
        // Time,PeriodEnd,Holders,Shares,HoldingValue,CallShares,PutShares,PrincipalValue,VotingSole,VotingShared,ConfidentialOmitted
        private const string FullLine =
            "20240214 17:30,20231231,4812,10589324611,3210456789012,152340000,98760000,4500000,9012345678,1234567890,1";

        // Same quarter, every optional measure withheld. Must produce nulls, never zeros.
        private const string EmptyValuesLine = "20240214 17:30,20231231,,,,,,,,,0";

        private static SubscriptionDataConfig Config(string ticker = "AAPL")
        {
            return new SubscriptionDataConfig(
                typeof(SEC13FHoldings),
                Symbol.Create(ticker, SecurityType.Base, Market.USA),
                Resolution.Daily,
                TimeZones.NewYork,
                TimeZones.NewYork,
                false, false, false);
        }

        private static SEC13FHoldings Read(string line, string ticker = "AAPL")
        {
            return new SEC13FHoldings().Reader(Config(ticker), line, DateTime.UtcNow, false) as SEC13FHoldings;
        }

        [Test]
        public void ReaderParsesEveryColumn()
        {
            var point = Read(FullLine);

            Assert.IsNotNull(point);
            Assert.AreEqual(Symbol.Create("AAPL", SecurityType.Base, Market.USA), point.Symbol);
            Assert.AreEqual(new DateTime(2024, 2, 14, 17, 30, 0), point.Time);
            Assert.AreEqual(point.Time, point.EndTime);
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
        public void CloneCopiesEveryProperty()
        {
            var original = Read(FullLine);
            var clone = original.Clone() as SEC13FHoldings;

            Assert.IsNotNull(clone);
            // Reflection rather than a hand-written list: a property added to the class but forgotten
            // in Clone() is exactly the bug this test has to catch, and a hand-written list cannot.
            AssertPropertiesAndFieldsAreEqual(original, clone);
        }

        [Test]
        public void ValueMirrorsHoldingValue()
        {
            Assert.AreEqual(Read(FullLine).HoldingValue, Read(FullLine).Value);
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
            var line = $"20240214 17:30,20231231,4812,10589324611,3210456789012,,,,,,{flag}";

            Assert.AreEqual(expected, Read(line).ConfidentialOmitted);
        }

        [Test]
        public void ReleaseIsStampedAfterTheQuarterItReports()
        {
            // The lag from the reported quarter to publication is the product here: measured across
            // 11,761 filings it runs from 0 to 6,596 days with a median of 42. Whatever its size,
            // the release can never precede the quarter it reports.
            var point = Read(FullLine);

            Assert.GreaterOrEqual(point.Time.Date, point.PeriodEnd);
        }

        [Test]
        public void AVeryLongFilingLagIsLegitimateData()
        {
            // Amendments for old quarters keep arriving; the measured worst case is 6,596 days.
            // A row like this is late, not corrupt, and must parse.
            var point = Read("20260122 17:30,20071231,3,1000,50000,,,,,,0");

            Assert.AreEqual(new DateTime(2026, 1, 22, 17, 30, 0), point.Time);
            Assert.AreEqual(new DateTime(2007, 12, 31), point.PeriodEnd);
            Assert.Greater((point.Time.Date - point.PeriodEnd).TotalDays, 6000);
        }

        [Test]
        public void AZeroDayFilingLagIsLegitimateData()
        {
            // The measured minimum is zero: filed on the reported quarter end itself.
            var point = Read("20240630 17:30,20240630,3,1000,50000,,,,,,0");

            Assert.AreEqual(point.Time.Date, point.PeriodEnd);
        }

        [TestCase("20240331", 2024, 3, 31)]
        [TestCase("20240630", 2024, 6, 30)]
        [TestCase("20240930", 2024, 9, 30)]
        [TestCase("20241231", 2024, 12, 31)]
        public void PeriodEndComesFromTheFileNotFromTime(string periodEnd, int year, int month, int day)
        {
            // Deriving the quarter from the release date would be wrong: one publication day
            // carries filings for several different reported quarters, so it has to be a column.
            var point = Read($"20260515 17:30,{periodEnd},3,1000,50000,,,,,,0");

            Assert.AreEqual(new DateTime(year, month, day), point.PeriodEnd);
            Assert.AreEqual(new DateTime(2026, 5, 15, 17, 30, 0), point.Time);
        }

        [Test]
        public void ATruncatedLineIsSkippedRatherThanThrown()
        {
            // An exception out of Reader ends the algorithm, so a short line has to come back as
            // null. It is the whole point of the guard: one malformed row must not take a live
            // strategy down.
            Assert.IsNull(Read("20240214 17:30,20231231,4812"));
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
            // Column 0 is a well formed release stamp, so the line reaches the decimal parse rather
            // than failing on the timestamp before it.
            Assert.Throws<FormatException>(
                () => Read("20240214 17:30,20231231,4812,not-a-number,3210456789012,,,,,,0"));
        }

        [Test]
        public void BadTimeThrows()
        {
            Assert.Throws<FormatException>(
                () => Read("2023-10-01,20240214 00:00,4812,10589324611,3210456789012,,,,,,0"));
        }

        [Test]
        public void GetSourceIsTheLocalPerSecurityFile()
        {
            var source = new SEC13FHoldings().GetSource(Config(), new DateTime(2024, 2, 14), false);

            Assert.AreEqual(SubscriptionTransportMedium.LocalFile, source.TransportMedium);
            Assert.AreEqual(FileFormat.Csv, source.Format);
            Assert.IsTrue(
                Normalize(source.Source).EndsWith("alternative/sec/13f/aapl.csv", StringComparison.Ordinal),
                $"Unexpected source '{source.Source}'");
        }

        [Test]
        public void ClassificationMatchesTheSpec()
        {
            var instance = new SEC13FHoldings();

            Assert.IsTrue(instance.IsSparseData());
            Assert.IsTrue(instance.RequiresMapping());
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

            var instance = new SEC13FHoldings();
            var rows = 0;

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

                    var point = instance.Reader(config, line, DateTime.UtcNow, false) as SEC13FHoldings;

                    Assert.IsNotNull(point, $"{ticker}: '{line}' did not parse");
                    Assert.AreEqual(point.Time, point.EndTime, $"{ticker}: EndTime is not Time on {point.Time:yyyy-MM-dd}");
                    Assert.GreaterOrEqual(point.Time.Date, point.PeriodEnd, $"{ticker}: released before the quarter it reports");
                    Assert.AreEqual(point.HoldingValue ?? 0m, point.Value, $"{ticker}: Value does not mirror HoldingValue");

                    // LEAN's SubscriptionDataReader silently drops a point whose timestamp moves
                    // backwards, so a file out of chronological order loses rows with nothing in
                    // the log to show for it. Accumulating groups by quarter, which is NOT the
                    // order the file has to ship in, so this is worth asserting on real output.
                    Assert.GreaterOrEqual(point.Time, previous, $"{ticker}: {point.Time:yyyy-MM-dd HH:mm} follows {previous:yyyy-MM-dd HH:mm}");
                    previous = point.Time;
                    rows++;
                }
            }

            TestContext.Progress.WriteLine($"13f: {files.Length} per-security files, {rows} rows");
            Assert.Greater(rows, 0);
        }

        [TestCase(typeof(SEC13FHoldings))]
        [TestCase(typeof(SEC13FHoldingsUniverse))]
        public void ProtobufContractIsCompleteAndUnique(Type type)
        {
            // LEAN routes a custom type through the BaseData branch of ProtobufSerialize, and
            // BaseData declares ProtoInclude only for its built-in types, so a round trip cannot
            // pass today whatever this class carries. Registration is LEAN's half of the contract.
            // Ours is that the contract is declared and the member numbers are unique.
            Assert.IsNotNull(type.GetCustomAttribute<ProtoContractAttribute>(), $"{type.Name}: ProtoContract is missing");

            // DeclaredOnly: BaseData numbers its own members from 1, and those are LEAN's to own.
            var members = type.GetProperties(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly)
                .Select(property => property.GetCustomAttribute<ProtoMemberAttribute>())
                .Where(attribute => attribute != null)
                .Select(attribute => attribute.Tag)
                .ToList();

            Assert.Greater(members.Count, 0, $"{type.Name}: no ProtoMember attributes found");
            Assert.AreEqual(members.Count, members.Distinct().Count(), $"{type.Name}: duplicate ProtoMember numbers");
            Assert.GreaterOrEqual(members.Min(), 10, $"{type.Name}: member numbers start at 10, BaseData reserves the low ones");
        }

        [TestCase(typeof(SEC13FHoldings))]
        [TestCase(typeof(SEC13FHoldingsUniverse))]
        public void EveryStoredPropertyCarriesAProtoMember(Type type)
        {
            // A property added later without a member number would serialize as nothing, which is
            // indistinguishable from a real absent reading.
            var unnumbered = type
                .GetProperties(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly)
                .Where(property => property.CanRead && property.CanWrite && property.GetIndexParameters().Length == 0)
                .Where(property => property.GetCustomAttribute<ProtoMemberAttribute>() == null)
                .Select(property => property.Name)
                .ToList();

            Assert.IsEmpty(unnumbered, $"{type.Name}: stored properties with no ProtoMember: {string.Join(", ", unnumbered)}");
        }

        internal static void AssertPropertiesAndFieldsAreEqual(object expected, object actual)
        {
            foreach (var property in expected.GetType().GetProperties())
            {
                Assert.AreEqual(property.GetValue(expected), property.GetValue(actual), $"Property '{property.Name}' was not copied");
            }

            foreach (var field in expected.GetType().GetFields())
            {
                Assert.AreEqual(field.GetValue(expected), field.GetValue(actual), $"Field '{field.Name}' was not copied");
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
