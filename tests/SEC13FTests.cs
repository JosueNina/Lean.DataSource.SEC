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
using Newtonsoft.Json;
using NUnit.Framework;
using NUnit.Framework.Legacy;
using ProtoBuf;
using QuantConnect.Data;
using QuantConnect.Data.UniverseSelection;
using QuantConnect.DataSource;
using QuantConnect.Python;

namespace QuantConnect.DataLibrary.Tests
{
    /// <summary>
    /// Unit tests for <see cref="SEC13FHolding"/> and <see cref="SEC13FHoldings"/>. Every case
    /// parses an in-line sample line, so the fixture needs no data on disk and is safe to run in CI.
    /// The one test that touches the processed output is skipped when that folder is not there yet.
    /// </summary>
    [TestFixture]
    public class SEC13FTests
    {
        // Frozen line layout, twenty columns:
        // FilingDate,Accession,ManagerCik,PeriodEnd,FormType,AmendmentType,AmendmentNumber,
        // TitleOfClass,Amount,AmountType,ReportedValue,ValueScale,PutCall,Discretion,
        // OtherManager,VotingSole,VotingShared,VotingNone,ConfidentialOmitted,DateReported
        private const string FullLine =
            "20260814,0001067983-26-000012,1067983,20260630,13F-HR,,,COM,80664820,SH," +
            "23341172315,0,,SOLE,,80664820,0,0,0,";

        // The same position with every optional column withheld, which must give nulls and empties
        // rather than zeros and defaults.
        private const string EmptyValuesLine =
            "20260814,0001067983-26-000012,1067983,20260630,13F-HR,,,,,,,0,,,,,,,0,";

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

        /// <summary>Reads one line through the factory, the way the folding reader does.</summary>
        private static SEC13FHolding Read(string line, string ticker = "AAPL")
        {
            return new SEC13FHolding().Reader(Config(ticker), line, DateTime.UtcNow, false) as SEC13FHolding;
        }

        /// <summary>Builds the collection LEAN would fold a file's lines into.</summary>
        private static SEC13FHoldings Fold(params string[] lines)
        {
            var records = lines.Select(line => Read(line)).Where(record => record != null).ToList();
            return new SEC13FHoldings
            {
                Symbol = Config().Symbol,
                Time = records[0].Time,
                EndTime = records[0].EndTime,
                Data = records.Cast<BaseData>().ToList()
            };
        }

        [Test]
        public void ReaderParsesEveryColumn()
        {
            var holding = Read(FullLine);

            Assert.AreEqual(new DateTime(2026, 8, 14), holding.Time);
            Assert.AreEqual("0001067983-26-000012", holding.AccessionNumber);
            Assert.AreEqual(1067983, holding.ManagerCik);
            Assert.AreEqual(new DateTime(2026, 6, 30), holding.PeriodEnd);
            Assert.AreEqual("13F-HR", holding.FormType);
            Assert.AreEqual("", holding.AmendmentType);
            Assert.IsNull(holding.AmendmentNumber);
            Assert.AreEqual("COM", holding.TitleOfClass);
            Assert.AreEqual(80664820m, holding.Amount);
            Assert.AreEqual("SH", holding.AmountType);
            Assert.AreEqual(23341172315m, holding.ReportedValue);
            Assert.AreEqual(0, holding.ValueScale);
            Assert.IsNull(holding.PutCall);
            Assert.AreEqual("SOLE", holding.InvestmentDiscretion);
            Assert.AreEqual("", holding.OtherManager);
            Assert.AreEqual(80664820m, holding.VotingSole);
            Assert.AreEqual(0m, holding.VotingShared);
            Assert.AreEqual(0m, holding.VotingNone);
            Assert.IsFalse(holding.ConfidentialOmitted);
            Assert.IsNull(holding.DateReported);
        }

        [Test]
        public void ThePointCoversItsFilingDateAndIsEmittedWhenThatDayEnds()
        {
            // LEAN emits a point at its end time, not at its time, so this pair is what decides when
            // an algorithm sees a filing: a filing made on the 14th reaches it at 00:00 on the 15th,
            // after EDGAR has finished listing the 14th at about 22:05 ET. Reading the end time as
            // anything but the moment of delivery is the mistake this test exists to prevent.
            var holding = Read(FullLine);

            Assert.AreEqual(new DateTime(2026, 8, 14), holding.Time, "covers its filing date");
            Assert.AreEqual(new DateTime(2026, 8, 15), holding.EndTime, "delivered when that day ends");
            Assert.AreEqual(TimeSpan.FromDays(1), holding.EndTime - holding.Time,
                "a whole day of filings arrives at once, never partway through the day itself");
        }

        [Test]
        public void PeriodEndIsUnrelatedToTheFilingDate()
        {
            // A quarter is reported up to 45 days after it ends, and an amendment can restate one
            // years later, so the reported quarter is carried and never derived from the timestamp.
            var holding = Read(FullLine);

            Assert.AreEqual(new DateTime(2026, 6, 30), holding.PeriodEnd);
            Assert.Greater(holding.Time, holding.PeriodEnd);
        }

        [TestCase("20130101", "20130101")]
        [TestCase("20260630", "20130101")]
        public void AnyFilingLagIsLegitimateData(string filingDate, string periodEnd)
        {
            // A filing made the day its quarter ended and one made thirteen years late are both
            // real: neither is treated as an error.
            var line = FullLine.Replace("20260630", periodEnd).Replace("20260814", filingDate);
            var holding = Read(line);

            Assert.AreEqual(DateTime.ParseExact(filingDate, "yyyyMMdd", null), holding.Time);
            Assert.AreEqual(DateTime.ParseExact(periodEnd, "yyyyMMdd", null), holding.PeriodEnd);
        }

        [Test]
        public void EmptyNumericColumnsBecomeNullNotZero()
        {
            // A withheld reading and a reported zero are different facts and stay distinguishable.
            var holding = Read(EmptyValuesLine);

            Assert.IsNull(holding.Amount);
            Assert.IsNull(holding.ReportedValue);
            Assert.IsNull(holding.VotingSole);
            Assert.IsNull(holding.VotingShared);
            Assert.IsNull(holding.VotingNone);
            Assert.IsNull(holding.MarketValue);
        }

        [Test]
        public void ReportedValueIsLeftAsFiledAndMarketValueAppliesTheScale()
        {
            // The SEC asked for thousands before 2023 and whole dollars after, and filers on both
            // sides ignore the instruction, so the number is published as filed with the scale
            // beside it rather than multiplied into it.
            var inThousands = Read(FullLine.Replace("23341172315,0,", "23341172,3,"));

            Assert.AreEqual(23341172m, inThousands.ReportedValue);
            Assert.AreEqual(3, inThousands.ValueScale);
            Assert.AreEqual(23341172000m, inThousands.MarketValue);
            Assert.AreEqual(23341172000m, inThousands.Value);
        }

        [Test]
        public void AValueOverstatedAThousandfoldIsBroughtBackDown()
        {
            // The scale runs both ways. Three SPY lines of the March 2023 quarter carried a value a
            // thousand times the price, and a flag that only said "in thousands" would have had
            // nowhere to put them, leaving them wrong by six orders of magnitude.
            var overstated = Read(FullLine.Replace("23341172315,0,", "23341172315,-3,"));

            Assert.AreEqual(23341172315m, overstated.ReportedValue);
            Assert.AreEqual(-3, overstated.ValueScale);
            Assert.AreEqual(23341172.315m, overstated.MarketValue);
        }

        [Test]
        public void MarketValueIsDerivedAndNeverStored()
        {
            // Computed from the two columns that are stored, so it cannot drift out of step with
            // them the way a third stored column could.
            Assert.IsNull(typeof(SEC13FHolding).GetProperty(nameof(SEC13FHolding.MarketValue)).SetMethod);
        }

        [TestCase("C", OptionRight.Call)]
        [TestCase("P", OptionRight.Put)]
        public void AnOptionLineCarriesItsSide(string column, OptionRight expected)
        {
            var holding = Read(FullLine.Replace("23341172315,0,,SOLE", $"23341172315,0,{column},SOLE"));

            Assert.AreEqual(expected, holding.PutCall);
        }

        [Test]
        public void AShareLineHasNoOptionSide()
        {
            Assert.IsNull(Read(FullLine).PutCall);
        }

        [Test]
        public void ADateReportedIsReadWhenTheFilingCarriesOne()
        {
            // Filled on about two filings in a thousand, where it marks positions that were
            // withheld under confidential treatment and released later.
            var holding = Read(FullLine + "20260214");

            Assert.AreEqual(new DateTime(2026, 2, 14), holding.DateReported);
        }

        [Test]
        public void ATruncatedLineIsSkippedRatherThanThrown()
        {
            // An exception out of Reader ends the algorithm, so a short line yields nothing.
            Assert.IsNull(Read("20260814,0001067983-26-000012,1067983"));
        }

        [Test]
        public void AnExtraColumnStillParses()
        {
            // The column count is tested as "fewer than", so a column appended in a later revision
            // of the file leaves every existing one readable instead of muting the dataset.
            var holding = Read(FullLine + ",something-new");

            Assert.AreEqual(80664820m, holding.Amount);
        }

        [Test]
        public void GetSourceIsAnEntryOfThePerSecurityZip()
        {
            var source = new SEC13FHoldings()
                .GetSource(Config("aapl"), new DateTime(2026, 8, 14), false);

            Assert.AreEqual(SubscriptionTransportMedium.LocalFile, source.TransportMedium);
            Assert.AreEqual(FileFormat.FoldingCollection, source.Format);
            StringAssert.EndsWith(Path.Combine("alternative", "sec", "13f", "aapl.zip#20260814.csv"), source.Source);
        }

        [Test]
        public void TheCollectionCarriesEveryPositionOfTheDay()
        {
            // Two managers reporting the same security on the same day give two records in one
            // point. Nothing about them is combined.
            var other = FullLine.Replace("1067983", "1350694").Replace("80664820", "1000000");
            var point = Fold(FullLine, other);

            Assert.AreEqual(2, point.Data.Count);
            CollectionAssert.AreEquivalent(
                new[] { 1067983, 1350694 },
                point.Data.Cast<SEC13FHolding>().Select(holding => holding.ManagerCik));
        }

        [Test]
        public void OneManagerReportingTwiceStaysTwoRecords()
        {
            // The rules let a manager report a security on more than one line when the discretion
            // differs, and Berkshire does exactly that with Moody's. Folding the two together would
            // be deriving a number no filing states.
            var second = FullLine.Replace("SOLE", "DFND").Replace("80664820,SH", "500000,SH");
            var point = Fold(FullLine, second);

            Assert.AreEqual(2, point.Data.Count);
            CollectionAssert.AreEqual(
                new[] { "SOLE", "DFND" },
                point.Data.Cast<SEC13FHolding>().Select(holding => holding.InvestmentDiscretion));
        }

        [Test]
        public void EveryRecordCarriesThePointsSymbol()
        {
            var point = Fold(FullLine, FullLine.Replace("1067983", "1350694"));

            Assert.IsTrue(point.Data.Cast<SEC13FHolding>().All(holding => holding.Symbol == point.Symbol));
        }

        [Test]
        public void TheCollectionDeclaresNoReadingsOfItsOwn()
        {
            // LEAN builds the collection itself and sets only its symbol and timestamps, so a
            // measure declared on it would silently stay null. This is the guard against someone
            // adding one back.
            var declared = typeof(SEC13FHoldings)
                .GetProperties(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly)
                .Select(property => property.Name)
                .ToList();

            CollectionAssert.IsEmpty(declared,
                "SEC13FHoldings must delegate to the factory; a property here would never be filled");
        }

        [Test]
        public void CloneCopiesEveryProperty()
        {
            var original = Read(FullLine);
            var clone = (SEC13FHolding)original.Clone();

            foreach (var property in typeof(SEC13FHolding)
                         .GetProperties(BindingFlags.Public | BindingFlags.Instance)
                         .Where(property => property.CanRead && property.GetIndexParameters().Length == 0))
            {
                Assert.AreEqual(property.GetValue(original), property.GetValue(clone), property.Name);
            }
        }

        [Test]
        public void CloneCopiesTheRecordsRatherThanSharingThem()
        {
            var point = Fold(FullLine);
            var clone = (SEC13FHoldings)point.Clone();

            Assert.AreNotSame(point.Data[0], clone.Data[0]);
            Assert.AreEqual(
                ((SEC13FHolding)point.Data[0]).AccessionNumber,
                ((SEC13FHolding)clone.Data[0]).AccessionNumber);
        }

        [Test]
        public void ClassificationMatchesTheSpec()
        {
            var factory = new SEC13FHolding();
            var collection = new SEC13FHoldings();

            foreach (var type in new BaseData[] { factory, collection })
            {
                Assert.IsTrue(type.RequiresMapping(), "linked to equities, so renames apply");
                Assert.IsTrue(type.IsSparseData(), "a security is reported only on the days managers file");
                Assert.AreEqual(Resolution.Daily, type.DefaultResolution());
                CollectionAssert.AreEqual(new[] { Resolution.Daily }, type.SupportedResolutions());
                Assert.AreEqual(TimeZones.NewYork, type.DataTimeZone());
            }
        }

        [Test]
        public void ToStringNamesTheManagerAndThePosition()
        {
            StringAssert.Contains("1067983", Read(FullLine).ToString());
            StringAssert.Contains("80664820", Read(FullLine).ToString());
        }

        [Test]
        public void AProtobufRoundTripKeepsEveryField()
        {
            // The sub type is registered once for the whole assembly, in ProtobufSubTypes.
            var original = Read(FullLine);
            var bytes = ((BaseData)original).ProtobufSerialize(new Guid());

            using var stream = new MemoryStream(bytes);
            var deserialized = (SEC13FHolding)Serializer.Deserialize<IEnumerable<BaseData>>(stream).First();

            Assert.AreEqual(original.AccessionNumber, deserialized.AccessionNumber);
            Assert.AreEqual(original.ManagerCik, deserialized.ManagerCik);
            Assert.AreEqual(original.PeriodEnd, deserialized.PeriodEnd);
            Assert.AreEqual(original.Amount, deserialized.Amount);
            Assert.AreEqual(original.ReportedValue, deserialized.ReportedValue);
            Assert.AreEqual(original.ValueScale, deserialized.ValueScale);
            Assert.AreEqual(original.MarketValue, deserialized.MarketValue);
        }

        [Test]
        public void AJsonRoundTripKeepsEveryProperty()
        {
            // Compared by reflection rather than field by field, so a property added later is
            // covered without anyone remembering to extend this test.
            var original = Read(FullLine);
            var restored = JsonConvert.DeserializeObject<SEC13FHolding>(JsonConvert.SerializeObject(original));

            foreach (var property in typeof(SEC13FHolding)
                         .GetProperties(BindingFlags.Public | BindingFlags.Instance)
                         .Where(property => property.CanRead && property.GetIndexParameters().Length == 0))
            {
                Assert.AreEqual(property.GetValue(original), property.GetValue(restored), property.Name);
            }
        }

        [Test]
        public void TheRecordsProtobufContractIsCompleteAndUnique()
        {
            var members = typeof(SEC13FHolding)
                .GetProperties(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly)
                .Select(property => property.GetCustomAttribute<ProtoMemberAttribute>())
                .Where(attribute => attribute != null)
                .Select(attribute => attribute.Tag)
                .ToList();

            Assert.IsNotNull(typeof(SEC13FHolding).GetCustomAttribute<ProtoContractAttribute>(), "ProtoContract is missing");
            Assert.Greater(members.Count, 0, "no ProtoMember attributes found");
            Assert.AreEqual(members.Count, members.Distinct().Count(), "duplicate ProtoMember numbers");
        }

        [Test]
        public void EveryStoredPropertyCarriesAProtoMember()
        {
            // A property added later without a member number would serialize as nothing, which is
            // indistinguishable from a real absent reading. MarketValue is exempt because it is
            // computed, and Symbol because BaseData carries it.
            var missing = typeof(SEC13FHolding)
                .GetProperties(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly)
                .Where(property => property.SetMethod != null)
                .Where(property => property.GetCustomAttribute<ProtoMemberAttribute>() == null)
                .Select(property => property.Name)
                .ToList();

            CollectionAssert.IsEmpty(missing, "stored properties without a ProtoMember number");
        }

        [Test]
        public void EveryProcessedRowParses()
        {
            // Reads the processor's own output when it is there, which is the only case that proves
            // the writer and the reader agree on the layout.
            var folder = Path.Combine("..", "..", "..", "..", "output", "alternative", "sec", "13f");
            if (!Directory.Exists(folder))
            {
                Assert.Ignore($"No processed output at {folder}");
            }

            var zips = Directory.GetFiles(folder, "*.zip");
            if (zips.Length == 0)
            {
                Assert.Ignore($"No per security zips in {folder}");
            }

            var rows = 0;
            foreach (var path in zips.Take(50))
            {
                using var zip = System.IO.Compression.ZipFile.OpenRead(path);
                foreach (var entry in zip.Entries)
                {
                    using var reader = new StreamReader(entry.Open());
                    string line;
                    while ((line = reader.ReadLine()) != null)
                    {
                        var holding = Read(line, Path.GetFileNameWithoutExtension(path));
                        Assert.IsNotNull(holding, $"{path}#{entry.Name}: {line}");

                        // The entry is named after the filing date every line in it carries.
                        Assert.AreEqual(Path.GetFileNameWithoutExtension(entry.Name),
                            holding.Time.ToString("yyyyMMdd"), $"{path}#{entry.Name}");
                        rows++;
                    }
                }
            }

            Assert.Greater(rows, 0, "the output holds no rows");
        }
    }
}
