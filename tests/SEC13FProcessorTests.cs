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
using System.IO.Compression;
using System.Linq;
using System.Text;
using NUnit.Framework;
using QuantConnect.Configuration;
using QuantConnect.DataProcessing;
using QuantConnect.DataSource;
using QuantConnect.Securities;

namespace QuantConnect.DataLibrary.Tests
{
    /// <summary>
    /// Unit tests for the 13F processor.
    ///
    /// The data classes had full coverage while the processor, which is where the subtle logic
    /// lives, had none. Every case below is a defect that actually reached a run and was caught by
    /// hand: a CUSIP left-padded into a different security, a thousand-fold step in the middle of
    /// the value history, a file written in quarter order that LEAN then silently truncated, and a
    /// universe that either threw away the finished quarter or resurrected a retired one.
    /// </summary>
    [TestFixture]
    public class SEC13FProcessorTests
    {
        /// <summary>The filing date the counting tests file on, when the date itself is not the point.</summary>
        private static readonly DateTime Filed = new(2024, 2, 14);

        private string _root;
        private string _previousDataFolder;
        private string _previousLookupDate;

        [SetUp]
        public void SetUp()
        {
            _root = Path.Combine(Path.GetTempPath(), $"sec-13f-tests-{Guid.NewGuid():N}");
            Directory.CreateDirectory(_root);
        }

        [TearDown]
        public void TearDown()
        {
            if (_previousDataFolder != null)
            {
                // The data folder is process wide and the resolver caches the one it was built with,
                // so a fixture left behind would be read by whatever runs next.
                Config.Set("data-folder", _previousDataFolder);
                Config.Set("map-file-provider-lookup-date", _previousLookupDate);
                Globals.Reset();
                SecurityDefinitionSymbolResolver.Reset();
                _previousDataFolder = null;
            }

            if (Directory.Exists(_root))
            {
                Directory.Delete(_root, true);
            }
        }

        // ---- CUSIP normalisation --------------------------------------------------------------

        [TestCase("037833100")]     // Apple
        [TestCase("464287226")]     // iShares Core US Aggregate Bond
        [TestCase("72201R205")]
        public void ANineCharacterCusipIsLeftAlone(string cusip)
        {
            // Most reported CUSIPs already carry their check digit and must not be touched at all.
            Assert.AreEqual(cusip, SEC13FDownloader.NormalizeCusip(cusip));
        }

        [TestCase("37833100", "037833100")]     // Apple, one leading zero eaten
        [TestCase("2824100", "002824100")]      // Abbott, two eaten
        [TestCase("84670702", "084670702")]
        public void AShortCusipMissingItsLeadingZerosIsPadded(string reported, string expected)
        {
            // The full history carries 12,372 short CUSIPs, most of them an identifier a spreadsheet
            // somewhere read as a number and stripped the leading zeros off.
            Assert.AreEqual(expected, SEC13FDownloader.NormalizeCusip(reported));
        }

        [TestCase("46428722", "464287226")]     // iShares Core US Aggregate Bond
        [TestCase("72201R20", "72201R205")]
        public void AShortCusipMissingItsCheckDigitIsCompletedInstead(string reported, string expected)
        {
            // The other reason a CUSIP arrives short, and it needs the opposite repair. Left-padding
            // "46428722" would have produced 046428722, a perfectly well-formed identifier belonging
            // to a different security, so the two cases are told apart by the check digit itself:
            // the padded reading is only accepted when it checks out.
            Assert.AreEqual(expected, SEC13FDownloader.NormalizeCusip(reported));
        }

        [TestCase("037833100")]     // Apple
        [TestCase("594918104")]     // Microsoft
        [TestCase("67066G104")]     // NVIDIA, the letter case
        [TestCase("464287226")]     // iShares Core US Aggregate Bond
        [TestCase("002824100")]     // Abbott, the leading-zero case
        public void TheCheckDigitAgreesWithRealCusips(string cusip)
        {
            // Everything above rests on the check digit being right, so it is held against real
            // published identifiers rather than against itself.
            Assert.AreEqual(cusip[8], SEC13FDownloader.ComputeCusipCheckDigit(cusip.Substring(0, 8)),
                $"the computed check digit disagrees with the published {cusip}");
        }

        [TestCase("")]
        [TestCase("0")]
        [TestCase("ABC")]
        [TestCase("12345")]
        public void JunkTooShortToBeACusipIsRejected(string cusip)
        {
            // Below six characters there is not enough of an identifier left to repair, and guessing
            // one would attribute somebody's positions to whichever security the guess landed on.
            Assert.IsNull(SEC13FDownloader.NormalizeCusip(cusip));
        }

        // ---- The constructed US ISIN ----------------------------------------------------------

        [TestCase("037833100", "US0378331005")]     // Apple
        [TestCase("02079K305", "US02079K3059")]     // Alphabet Class C
        public void TheConstructedIsinMatchesTheRealOne(string cusip, string isin)
        {
            // This is the second step of the identity chain and it is pure arithmetic, so it is
            // either exactly right or it silently resolves to nothing. It lifted measured coverage
            // from 76.1% to 88.9% of reported value, which is only true while it is exact: both of
            // these are checked against the security's real published ISIN.
            Assert.AreEqual(isin, SEC13FDownloader.BuildUnitedStatesIsin(cusip));
        }

        // ---- The VALUE unit break at 2023 ------------------------------------------------------

        [TestCase("2022-11-14", 1000000)]   // reported in thousands, scaled up
        [TestCase("2022-12-31", 1000000)]   // the last day of the old unit
        [TestCase("2023-01-01", 1000)]      // the first day of the new one, left alone
        [TestCase("2023-02-14", 1000)]
        public void ValueIsScaledOnlyForFilingsMadeBefore2023(string filingDate, decimal expected)
        {
            // VALUE changed unit with the 2023 filings: through 2022 it is thousands of dollars,
            // from 2023 whole dollars. Measured on the real archives as the median VALUE/SSHPRNAMT
            // of share lines, which is a price per share: 0.0460 in NOV-2022 against 39.86 in
            // FEB-2023. Left unscaled this puts a 1000x step in the middle of the published history
            // and breaks every comparison that crosses it. The cut is on FILING_DATE rather than on
            // the reported period, because the rule changed for filings made from January 2023.
            var filing = DateTime.Parse(filingDate);
            var holdings = ReadInfoTable(
                new[] { Line("0000000000-00-000001", "037833100", "1000", "100", "SH") },
                Submission("0000000000-00-000001", filing, new DateTime(2022, 9, 30)));

            Assert.AreEqual(1, holdings.Count);
            Assert.AreEqual(expected, holdings.Values.Single().HoldingValue);
        }

        [Test]
        public void AnAmendmentGathersItsFilerLikeAnyOtherFilingAndStillSumsItsColumns()
        {
            // The reader gathers every filer that names the security, amendments included. Whether
            // one of them moves Holders is not decided here: it is decided in CountNewFilers, the
            // only place that knows whether that manager was already counted for the quarter.
            var filing = new DateTime(2024, 2, 14);
            var holdings = ReadInfoTable(
                new[]
                {
                    Line("0000000000-00-000001", "037833100", "1000", "100", "SH"),
                    Line("0000000000-00-000002", "037833100", "2000", "300", "SH")
                },
                Submission("0000000000-00-000001", filing, new DateTime(2023, 12, 31), cik: 111),
                Submission("0000000000-00-000002", filing, new DateTime(2023, 12, 31), cik: 222, isAmendment: true));

            var holding = holdings.Values.Single();

            Assert.AreEqual(2, holding.Ciks.Count, "both filers are gathered");
            Assert.AreEqual(400m, holding.Shares, "the amendment's shares still sum");
            Assert.AreEqual(3000m, holding.HoldingValue, "the amendment's value still sums");
        }

        // ---- Holders is a distinct count of managers --------------------------------------------

        [Test]
        public void AFilerReachesHoldersOnceHoweverManyTimesItReportsTheQuarter()
        {
            // Measured against the source for Bristol-Myers Squibb's December 2019 quarter: summing
            // the per filing counts gives 2,765 managers where 2,235 distinct ones reported, because
            // 530 of them named both the common stock and the contingent value right, 525 of those
            // on one filing. Summing counts a manager once per CUSIP and once per day it appears.
            using var downloader = Downloader();
            var security = Security("AAPL");
            var quarter = new DateTime(2023, 12, 31);

            Assert.AreEqual(3, downloader.CountNewFilers(security, quarter, new[] { 111, 222, 333 }, Filed),
                "three managers, first sight");
            Assert.AreEqual(0, downloader.CountNewFilers(security, quarter, new[] { 111, 222 }, Filed),
                "the same managers on a second CUSIP or a later day add nothing");
            Assert.AreEqual(1, downloader.CountNewFilers(security, quarter, new[] { 222, 444 }, Filed),
                "only the manager that had not reported yet is added");
        }

        [Test]
        public void AManagerThatNamesTheSecurityOnlyInAnAmendmentIsCounted()
        {
            // The undercount the earlier rule had. A manager that leaves a security out of its
            // original report and adds it in the amendment holds that security, and its shares and
            // value are carried, so leaving it out published value with no holder at all. Apple has
            // eight such managers in the March 2026 quarter.
            using var downloader = Downloader();
            var security = Security("AAPL");
            var quarter = new DateTime(2023, 12, 31);

            downloader.CountNewFilers(security, quarter, new[] { 111 }, Filed);

            Assert.AreEqual(1, downloader.CountNewFilers(security, quarter, new[] { 222 }, Filed),
                "the amendment's filer is new to this security and quarter");
        }

        [Test]
        public void FilersAreCountedPerSecurityAndPerQuarter()
        {
            // The key has to carry all three parts. A manager counted for one quarter must still be
            // new to the next one, which is what makes each quarter its own census rather than a
            // running total of every quarter before it.
            using var downloader = Downloader();
            var apple = Security("AAPL");
            var microsoft = Security("MSFT");

            downloader.CountNewFilers(apple, new DateTime(2023, 12, 31), new[] { 111 }, Filed);

            Assert.AreEqual(1, downloader.CountNewFilers(apple, new DateTime(2024, 3, 31), new[] { 111 }, Filed),
                "a new quarter counts the manager again");
            Assert.AreEqual(1, downloader.CountNewFilers(microsoft, new DateTime(2023, 12, 31), new[] { 111 }, Filed),
                "a different security counts the manager again");
        }

        [Test]
        public void APeriodThatIsNotAQuarterEndGetsItsOwnSlot()
        {
            // PERIODOFREPORT is free text from the filer. Deriving the key from the quarter a date
            // falls in would fold a malformed period into a real one and credit its filers there.
            using var downloader = Downloader();
            var security = Security("AAPL");

            downloader.CountNewFilers(security, new DateTime(2023, 12, 31), new[] { 111 }, Filed);

            Assert.AreEqual(1, downloader.CountNewFilers(security, new DateTime(2023, 12, 15), new[] { 111 }, Filed),
                "a period inside the same quarter is still a different period");
        }

        // ---- Accumulation ----------------------------------------------------------------------

        [Test]
        public void IncrementsRunningSumWithinAQuarterAndNeverAcrossTwo()
        {
            // The managers of one quarter file across roughly fifty days, so the published row states
            // what had been reported for its own quarter as of its own release date. Carrying the
            // running total into the next quarter would restate a fresh quarter as the sum of every
            // quarter before it.
            var cumulative = SEC13FDownloader.Accumulate(new[]
            {
                Row("20240201 17:30", "20231231", 3m, 1000.25m),
                Row("20240214 17:30", "20231231", 2m, 500.75m),
                Row("20240415 17:30", "20240331", 1m, 7.5m)
            });

            Assert.AreEqual(new[] { 3m, 5m, 1m }, cumulative.Select(row => row.Values[0]).ToArray());
            Assert.AreEqual(new[] { 1000.25m, 1501m, 7.5m }, cumulative.Select(row => row.Values[1]).ToArray());
        }

        [Test]
        public void TheConfidentialFlagOnlyEverGoesUpWithinAQuarter()
        {
            // Once a contributing filing withheld positions the quarter's total is incomplete and
            // stays incomplete, however many complete filings arrive after it. The next quarter is
            // a fresh total, so it starts clean again.
            var cumulative = SEC13FDownloader.Accumulate(new[]
            {
                Row("20240201 17:30", "20231231", 1m, 10m),
                Row("20240214 17:30", "20231231", 1m, 10m, confidential: true),
                Row("20240301 17:30", "20231231", 1m, 10m),
                Row("20240415 17:30", "20240331", 1m, 10m)
            });

            Assert.AreEqual(new[] { false, true, true, false },
                cumulative.Select(row => row.ConfidentialOmitted).ToArray());
        }

        // ---- Difference and re-accumulate -------------------------------------------------------

        [Test]
        public void DifferencingACumulativeFileRecoversTheIncrementsExactly()
        {
            // This is the property that makes an incremental run idempotent, and the one that would
            // break in silence. A published file holds running totals, and two running totals over
            // different sets of filings cannot be combined at all, so the merge differences the
            // published copy back to increments first. Everything is decimal on purpose: the
            // difference is the exact inverse of the sum only while no rounding happens, so the
            // values here are deliberately awkward rather than round.
            var increments = new[]
            {
                Row("20240201 17:30", "20231231", 3m, 1234.56789m),
                Row("20240214 17:30", "20231231", 2m, 0.00000001m),
                Row("20240301 17:30", "20231231", 7m, 98765.4321m, confidential: true),
                Row("20240415 17:30", "20240331", 1m, 0.1m),
                Row("20240515 17:30", "20240331", 4m, 0.2m)
            };

            var roundTripped = SEC13FDownloader
                .Difference(SEC13FDownloader.Accumulate(increments))
                .ToDictionary(row => (row.PeriodEnd, row.Time));

            Assert.AreEqual(increments.Length, roundTripped.Count);

            foreach (var original in increments)
            {
                var recovered = roundTripped[(original.PeriodEnd, original.Time)];

                Assert.AreEqual(original.Values, recovered.Values,
                    $"{original.PeriodEnd:yyyyMMdd} at {original.Time:yyyyMMdd HH:mm} did not round-trip");
                Assert.AreEqual(original.ConfidentialOmitted || recovered.ConfidentialOmitted,
                    recovered.ConfidentialOmitted, "the flag is OR-ed forward, so it never comes back down");
            }
        }

        [Test]
        public void AMergeThatReadsNoNewFilingReproducesTheFileItRead()
        {
            // The same property as above, seen from where it actually matters: the production job
            // runs daily and most days bring nothing new for a given security. The file it publishes
            // then has to be the file it read, byte for byte, or every quiet day would perturb the
            // history.
            var output = Path.Combine(_root, "aapl.csv");
            var processed = Path.Combine(_root, "processed-aapl.csv");

            File.WriteAllLines(output, new[]
            {
                "20240201 17:30,20231231,3,1234.56789,50,0,0,0,10,20,0",
                "20240214 17:30,20231231,2,0.00000001,25,0,0,0,5,10,1"
            });
            SEC13FDownloader.MergeAndWrite(output, processed);

            var published = File.ReadAllLines(output);
            File.Copy(output, processed);
            File.WriteAllText(output, string.Empty);

            SEC13FDownloader.MergeAndWrite(output, processed);

            Assert.AreEqual(published, File.ReadAllLines(output));
        }

        // ---- Chronological output order ---------------------------------------------------------

        [Test]
        public void ThePublishedFileIsSortedByTimeAndNotByQuarter()
        {
            // Accumulate() groups by quarter, so its natural order is NOT the order the file ships
            // in, and the two disagree exactly when a late amendment for an old quarter arrives
            // after a newer quarter has started reporting. LEAN's SubscriptionDataReader drops a
            // point whose timestamp moves backwards and does so silently, so a file left in quarter
            // order loses rows with nothing in the log to show for it.
            var output = Path.Combine(_root, "aapl.csv");
            var processed = Path.Combine(_root, "processed-aapl.csv");

            // The published history, which the merge has to fold in before it can sort anything.
            File.WriteAllLines(processed, new[]
            {
                "20240215 17:30,20231231,1,5,50,0,0,0,0,0,0"
            });

            File.WriteAllLines(output, new[]
            {
                // Newer quarter, released first.
                "20240501 17:30,20240331,5,100,1000,0,0,0,0,0,0",
                // Older quarter, an amendment released two weeks later.
                "20240515 17:30,20231231,1,7,70,0,0,0,0,0,0"
            });

            Assert.AreEqual(1, SEC13FDownloader.MergeAndWrite(output, processed),
                "the published row was not read off the shelf");

            var written = File.ReadAllLines(output).Where(line => line.Length > 0).ToList();

            Assert.AreEqual(3, written.Count);
            Assert.IsTrue(written[0].StartsWith("20240215 17:30,20231231", StringComparison.Ordinal),
                $"first row is '{written[0]}'");
            Assert.IsTrue(written[1].StartsWith("20240501 17:30,20240331", StringComparison.Ordinal),
                $"second row is '{written[1]}'");
            Assert.IsTrue(written[2].StartsWith("20240515 17:30,20231231", StringComparison.Ordinal),
                $"third row is '{written[2]}'");

            // The amendment accumulates onto the published row of its own quarter rather than
            // starting over: one holder plus one, five shares plus seven.
            Assert.AreEqual("20240515 17:30,20231231,2,12,120,0,0,0,0,0,0", written[2]);
        }

        // ---- The incremental run has to see the published history ---------------------------------

        [Test]
        public void AnIncrementalRunWithNoPublishedHistoryFails()
        {
            // The destination arrives empty on every deployment, so a processed-data-directory that
            // is wrong or unmounted leaves an incremental run with nothing to merge into. Publishing
            // the window on its own would republish thirteen years as three months, rebuild every
            // universe file from it, and return success: the files come out the right shape, so no
            // row count tells the two apart. The guard runs before anything is fetched.
            using var downloader = new SEC13FDownloader(
                Path.Combine(_root, "out"), Path.Combine(_root, "processed"), new DateTime(2026, 9, 8));

            Assert.IsFalse(downloader.Run(), "the run reported success with no history behind it");
        }

        [Test]
        public void AnIncrementalRunAcceptsAShelfThatHoldsSecurities()
        {
            // The same guard from the other side: a published history present is not a failure. Only
            // the guard is exercised here, by handing it a deployment date and a shelf and checking
            // it does not stop the run before the first archive is fetched.
            var processed = ShelfWithFilerState();
            File.WriteAllLines(Path.Combine(processed, "aapl.csv"), new[]
            {
                "20240215 17:30,20231231,1,5,50,0,0,0,0,0,0"
            });

            using var downloader = new SEC13FDownloader(
                Path.Combine(_root, "out"), Path.Combine(_root, "processed"), new DateTime(2026, 9, 8));

            Assert.DoesNotThrow(() => downloader.RequirePublishedHistoryForIncrementalRun());
        }

        [Test]
        public void AnIncrementalRunWithAHistoryButNoFilerStateFails()
        {
            // Holders is a distinct count and cannot be rebuilt from the published totals the way
            // the summed columns can, so a run that cannot read the state would count every filer in
            // tonight's archive again on top of the existing history. The file would keep its shape
            // and only that one column would be wrong, which is what this refuses to publish.
            var processed = Path.Combine(_root, "processed", SEC13FHoldings.ReportFolder);
            Directory.CreateDirectory(processed);
            File.WriteAllLines(Path.Combine(processed, "aapl.csv"), new[]
            {
                "20240215 17:30,20231231,1,5,50,0,0,0,0,0,0"
            });

            using var downloader = new SEC13FDownloader(
                Path.Combine(_root, "out"), Path.Combine(_root, "processed"), new DateTime(2026, 9, 8));

            Assert.Throws<InvalidOperationException>(
                () => downloader.RequirePublishedHistoryForIncrementalRun(),
                "a shelf with securities but no filer state has to stop the run");
        }

        [Test]
        public void AFullHistoryRunNeedsNoFilerState()
        {
            // A full run rebuilds every count from the archives, so it neither needs the state nor
            // reads it. Requiring it there would make the one run that can restore the file
            // impossible whenever the file is what went missing.
            using var downloader = Downloader();

            Assert.DoesNotThrow(() => downloader.RequirePublishedHistoryForIncrementalRun());
            Assert.DoesNotThrow(() => downloader.ReadFilerState());
        }

        [Test]
        public void TheFilerStateSurvivesAWriteAndReadRoundTrip()
        {
            var security = Security("AAPL");
            var other = Security("MSFT");
            var quarter = new DateTime(2023, 12, 31);

            using (var first = Downloader())
            {
                first.CountNewFilers(security, quarter, new[] { 111, 222, 333 }, Filed);
                first.CountNewFilers(other, quarter, new[] { 111 }, Filed);
                first.WriteFilerState();
            }

            // The shelf is what the next run reads and the publish step is what puts this run's
            // output there, so the round trip goes through that move rather than reading the output
            // in place.
            var shelf = Path.Combine(_root, "processed", SEC13FHoldings.ReportFolder);
            Directory.CreateDirectory(shelf);
            File.Copy(
                Path.Combine(_root, "out", SEC13FHoldings.ReportFolder, "filers.zip"),
                Path.Combine(shelf, "filers.zip"), overwrite: true);

            using var second = new SEC13FDownloader(
                Path.Combine(_root, "out"), Path.Combine(_root, "processed"), new DateTime(2024, 2, 14));
            second.ReadFilerState();

            Assert.AreEqual(0, second.CountNewFilers(security, quarter, new[] { 111, 222, 333 }, Filed),
                "every filer the earlier run counted is already known");
            Assert.AreEqual(0, second.CountNewFilers(other, quarter, new[] { 111 }, Filed),
                "the same manager on another security is known there too");
            Assert.AreEqual(1, second.CountNewFilers(security, quarter, new[] { 444 }, Filed),
                "a manager nobody has seen is still new");
        }

        [Test]
        public void ReadingAnArchiveAgainCountsItsFilersAgainRatherThanPublishingZero()
        {
            // The defect this exists for, caught by running the incremental path end to end: the
            // rolling window covers three months and the job reads it every night, so on the second
            // night every filer of that window is already in the state, each of its days publishes
            // an increment of zero, and MergeAndWrite lets the fresh reading win. Apple's March 2026
            // quarter went from 6,041 holders to 0 in one run, with the file the right shape and
            // only that column moved.
            var security = Security("AAPL");
            var quarter = new DateTime(2023, 12, 31);
            var filed = new DateTime(2024, 2, 14);

            using (var first = Downloader())
            {
                first.CountNewFilers(security, quarter, new[] { 111, 222, 333 }, filed);
                first.WriteFilerState();
            }

            var shelf = Path.Combine(_root, "processed", SEC13FHoldings.ReportFolder);
            Directory.CreateDirectory(shelf);
            File.Copy(
                Path.Combine(_root, "out", SEC13FHoldings.ReportFolder, "filers.zip"),
                Path.Combine(shelf, "filers.zip"), overwrite: true);

            using var second = new SEC13FDownloader(
                Path.Combine(_root, "out"), Path.Combine(_root, "processed"), filed);
            second.ReadFilerState();
            second.PruneFilerState(new List<SEC13FDownloader.Archive>
            {
                new("01jan2024-31mar2024_form13f.zip", "https://localhost/x.zip",
                    new DateTime(2024, 1, 1), new DateTime(2024, 3, 31))
            });

            Assert.AreEqual(3, second.CountNewFilers(security, quarter, new[] { 111, 222, 333 }, filed),
                "reading the same window again has to reproduce the same increment");
        }

        [Test]
        public void AFilerCountedBeforeTheWindowSurvivesThePrune()
        {
            // The other half of the rule. Only the filers first counted inside the window being read
            // again are forgotten; one counted in an earlier window stays known, or reading tonight's
            // archive would count it a second time and push the quarter above its real number.
            var security = Security("AAPL");
            var quarter = new DateTime(2023, 12, 31);

            using (var first = Downloader())
            {
                first.CountNewFilers(security, quarter, new[] { 111 }, new DateTime(2023, 11, 15));
                first.CountNewFilers(security, quarter, new[] { 222 }, new DateTime(2024, 2, 14));
                first.WriteFilerState();
            }

            var shelf = Path.Combine(_root, "processed", SEC13FHoldings.ReportFolder);
            Directory.CreateDirectory(shelf);
            File.Copy(
                Path.Combine(_root, "out", SEC13FHoldings.ReportFolder, "filers.zip"),
                Path.Combine(shelf, "filers.zip"), overwrite: true);

            using var second = new SEC13FDownloader(
                Path.Combine(_root, "out"), Path.Combine(_root, "processed"), new DateTime(2024, 2, 14));
            second.ReadFilerState();
            second.PruneFilerState(new List<SEC13FDownloader.Archive>
            {
                new("01jan2024-31mar2024_form13f.zip", "https://localhost/x.zip",
                    new DateTime(2024, 1, 1), new DateTime(2024, 3, 31))
            });

            Assert.AreEqual(0, second.CountNewFilers(security, quarter, new[] { 111 }, new DateTime(2024, 2, 20)),
                "the filer counted in November is outside the window and stays counted");
            Assert.AreEqual(1, second.CountNewFilers(security, quarter, new[] { 222 }, new DateTime(2024, 2, 14)),
                "the filer counted inside the window is recounted");
        }

        [Test]
        public void TheFilerStateIsByteReproducible()
        {
            // The state ships with the data, so a run that read no new filing has to produce the
            // same bytes. A zip stamps every entry with the moment it was written, which would make
            // the delivery archive look changed on every run when nothing in it had moved.
            var security = Security("AAPL");
            var quarter = new DateTime(2023, 12, 31);
            var path = Path.Combine(_root, "out", SEC13FHoldings.ReportFolder, "filers.zip");

            using (var first = Downloader())
            {
                first.CountNewFilers(security, quarter, new[] { 111, 222 }, Filed);
                first.WriteFilerState();
            }

            var before = File.ReadAllBytes(path);

            using (var second = Downloader())
            {
                second.CountNewFilers(security, quarter, new[] { 111, 222 }, Filed);
                second.WriteFilerState();
            }

            Assert.AreEqual(Convert.ToBase64String(before), Convert.ToBase64String(File.ReadAllBytes(path)),
                "the same filers have to write the same archive");
        }

        /// <summary>
        /// An equity identifier built without touching the map files. These tests are about how
        /// filers are counted, not about identity resolution, and the mapping overload reaches for a
        /// map file provider that most of them have no fixture for.
        /// </summary>
        private static SecurityIdentifier Security(string ticker)
        {
            return SecurityIdentifier.GenerateEquity(ticker, Market.USA, mapSymbol: false);
        }

        /// <summary>A shelf directory carrying a filer state, which an incremental run demands.</summary>
        private string ShelfWithFilerState()
        {
            using (var seed = Downloader())
            {
                seed.CountNewFilers(Security("AAPL"),
                    new DateTime(2023, 12, 31), new[] { 1 }, Filed);
                seed.WriteFilerState();
            }

            var shelf = Path.Combine(_root, "processed", SEC13FHoldings.ReportFolder);
            Directory.CreateDirectory(shelf);
            File.Copy(
                Path.Combine(_root, "out", SEC13FHoldings.ReportFolder, "filers.zip"),
                Path.Combine(shelf, "filers.zip"), overwrite: true);
            return shelf;
        }

        // ---- The universe two-quarter rule -------------------------------------------------------

        [Test]
        public void TheUniverseCarriesTheNewestQuarterAndTheOneBeforeIt()
        {
            // A quarter fills in over roughly fifty days, so on any given day a security has a brand
            // new quarter with a handful of managers in it and a finished one with thousands.
            // Publishing only the newest throws the finished cross-section away for six weeks of
            // every year; publishing only the finished one hides the fresh data. Both are carried,
            // and the older is retired the moment the newer overtakes it in Holders, which is the
            // point where it stops adding anything. ExtractAlphaTrueBeats ships every live fiscal
            // period the same way rather than choosing between them.
            var universe = BuildUniverse(
                // Holders is the fourth column of the published row, right after the quarter.
                "20231101 17:30,20230930,1500,100,1000,0,0,0,0,0,0",
                "20240201 17:30,20231231,5,10,100,0,0,0,0,0,0",
                "20240415 17:30,20240331,3,7,70,0,0,0,0,0,0",
                "20240516 17:30,20240331,9,20,200,0,0,0,0,0,0",
                // An amendment for a quarter that has already been retired.
                "20240603 17:30,20230930,1600,110,1100,0,0,0,0,0,0");

            // Only the finished quarter exists yet.
            Assert.AreEqual(new[] { "20230930" }, universe["20231101"]);

            // The new quarter has five managers against fifteen hundred, so both are published.
            Assert.AreEqual(new[] { "20230930", "20231231" }, universe["20240201"]);

            // A third quarter arrives and the oldest goes, whatever its Holders: two is the cap.
            Assert.AreEqual(new[] { "20231231", "20240331" }, universe["20240415"]);

            // The newest overtakes the one before it, which is where the older stops being useful.
            Assert.AreEqual(new[] { "20240331" }, universe["20240516"]);

            // The late amendment for 2023-09-30 must not put a stale cross-section back into the
            // current file. Its own release-date row still exists in the per-security file, so
            // nothing is lost by ignoring it here.
            Assert.AreEqual(new[] { "20240331" }, universe["20240603"]);
        }

        // ---- N-PORT ticker normalisation ----------------------------------------------------------

        [TestCase("GOOGL US", "GOOGL")]     // the Bloomberg style venue suffix
        [TestCase("googl", "GOOGL")]
        [TestCase("GOOGL", "GOOGL")]
        [TestCase(" brk.b ", "BRK.B")]      // share classes survive, they are the LEAN spelling
        public void ANPortTickerIsNormalisedBeforeItIsVotedOn(string raw, string expected)
        {
            // IDENTIFIER_TICKER is free text written by fund administrators, so the same security
            // arrives as "GOOGL", "GOOGL US" and "goog" across funds. It is normalised and then
            // voted on across every fund that reported the security, rather than trusted row by row.
            Assert.AreEqual(expected, SEC13FTickerCrosswalk.Normalize(raw));
        }

        [TestCase("N/A")]
        [TestCase("")]
        [TestCase("   ")]
        [TestCase("912828YV6")]             // a CUSIP typed into the ticker column
        [TestCase("NOTATICKERATALL")]       // longer than any US equity ticker
        public void ANPortTickerThatIsNotOneIsRejected(string raw)
        {
            // The crosswalk feeds a map file lookup, and a ticker the map files do not know still
            // produces a plausible looking Symbol with no data behind it, so anything that is not
            // ticker-shaped is dropped here rather than downstream.
            Assert.IsNull(SEC13FTickerCrosswalk.Normalize(raw));
        }

        // ---- Fixtures -----------------------------------------------------------------------------

        /// <summary>One increment or cumulative row. Only the first two measures are exercised.</summary>
        private static SEC13FDownloader.HoldingsRow Row(string time, string periodEnd, decimal holders,
            decimal shares, bool confidential = false)
        {
            return new SEC13FDownloader.HoldingsRow
            {
                Time = DateTime.ParseExact(time, "yyyyMMdd HH:mm", null),
                PeriodEnd = DateTime.ParseExact(periodEnd, "yyyyMMdd", null),
                Values = new[] { holders, shares, 0m, 0m, 0m, 0m, 0m, 0m },
                ConfidentialOmitted = confidential
            };
        }

        /// <summary>One INFOTABLE.tsv line, in the column order the header below declares.</summary>
        private static string Line(string accession, string cusip, string value, string amount, string shareType,
            string putCall = "", string votingSole = "0", string votingShared = "0")
        {
            return string.Join('\t', accession, cusip, value, amount, shareType, putCall, votingSole, votingShared);
        }

        /// <summary>One SUBMISSION row, keyed by its accession number the way the processor keys it.</summary>
        private static KeyValuePair<string, SEC13FDownloader.Submission> Submission(string accession,
            DateTime filingDate, DateTime period, int cik = 1, bool isAmendment = false)
        {
            return new KeyValuePair<string, SEC13FDownloader.Submission>(accession,
                new SEC13FDownloader.Submission
                {
                    Cik = cik,
                    FilingDate = filingDate,
                    Period = period,
                    IsAmendment = isAmendment
                });
        }

        /// <summary>
        /// Runs the real INFOTABLE reader over an in-memory archive. The unit break and the amendment
        /// rule live inside that loop, so a test that rebuilt them here would only be asserting
        /// itself; this drives the shipped code over the table shape the SEC publishes.
        /// </summary>
        private Dictionary<SEC13FDownloader.HoldingKey, SEC13FDownloader.Holding> ReadInfoTable(
            string[] lines, params KeyValuePair<string, SEC13FDownloader.Submission>[] submissions)
        {
            var text = new StringBuilder()
                .AppendLine(string.Join('\t', "ACCESSION_NUMBER", "CUSIP", "VALUE", "SSHPRNAMT",
                    "SSHPRNAMTTYPE", "PUTCALL", "VOTING_AUTH_SOLE", "VOTING_AUTH_SHARED"));

            foreach (var line in lines)
            {
                text.AppendLine(line);
            }

            using var buffer = new MemoryStream();
            using (var writing = new ZipArchive(buffer, ZipArchiveMode.Create, true))
            using (var entry = new StreamWriter(writing.CreateEntry("INFOTABLE.tsv").Open()))
            {
                entry.Write(text.ToString());
            }

            buffer.Position = 0;
            using var archive = new ZipArchive(buffer, ZipArchiveMode.Read);
            using var downloader = Downloader();

            return downloader.ReadInfoTable(
                archive,
                new SEC13FDownloader.Archive("2024q1_form13f.zip", "https://localhost/2024q1_form13f.zip",
                    new DateTime(2024, 1, 1), new DateTime(2024, 3, 31)),
                submissions.ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.Ordinal));
        }

        /// <summary>
        /// Runs the real universe build over a single per-security file and reads back which quarters
        /// each business day published, keyed by the day. The retirement rule is what is being
        /// asserted, so the whole forward-fill is driven rather than the rule on its own.
        /// </summary>
        private Dictionary<string, string[]> BuildUniverse(params string[] rows)
        {
            SeedMapFiles("aapl");

            var destination = Path.Combine(_root, "out");
            var holdings = Path.Combine(destination, SEC13FHoldings.ReportFolder);
            Directory.CreateDirectory(holdings);
            File.WriteAllLines(Path.Combine(holdings, "aapl.csv"), rows);

            using (var downloader = Downloader())
            {
                downloader.BuildUniverseFiles();
            }

            var universe = Path.Combine(holdings, "universe");
            Assert.IsTrue(Directory.Exists(universe), $"no universe written at {universe}");

            return Directory.GetFiles(universe, "*.csv").ToDictionary(
                Path.GetFileNameWithoutExtension,
                // Column two is PeriodEnd: sid, ticker, then the quarter.
                file => File.ReadAllLines(file)
                    .Where(line => line.Length > 0)
                    .Select(line => line.Split(',')[2])
                    .OrderBy(period => period, StringComparer.Ordinal)
                    .ToArray());
        }

        /// <summary>
        /// Points LEAN's data folder at a map file archive this test wrote, holding one row per
        /// ticker that spans every date used here.
        ///
        /// The universe build turns each ticker into a SecurityIdentifier point in time, and
        /// LocalZipMapFileProvider throws outright when it finds no archive at all, so without this
        /// the test could only be skipped on a machine with no LEAN data checkout, which is most of
        /// them. The lookup date is pinned rather than left to walk back from yesterday, so the
        /// fixture does not expire.
        /// </summary>
        private void SeedMapFiles(params string[] tickers)
        {
            var dataFolder = Path.Combine(_root, "data");
            var mapFiles = Path.Combine(dataFolder, "equity", "usa", "map_files");
            Directory.CreateDirectory(mapFiles);

            var lookupDate = new DateTime(2026, 1, 2);
            using (var zip = ZipFile.Open(
                       Path.Combine(mapFiles, $"map_files_{lookupDate:yyyyMMdd}.zip"), ZipArchiveMode.Create))
            {
                foreach (var ticker in tickers)
                {
                    using var entry = new StreamWriter(zip.CreateEntry($"{ticker}.csv").Open());
                    entry.WriteLine($"19801212,{ticker}");
                    entry.WriteLine($"20501231,{ticker}");
                }
            }

            _previousDataFolder = Config.Get("data-folder", string.Empty);
            _previousLookupDate = Config.Get("map-file-provider-lookup-date", string.Empty);

            Config.Set("data-folder", dataFolder);
            Config.Set("map-file-provider-lookup-date", $"{lookupDate:yyyyMMdd}");
            Globals.Reset();
            SecurityDefinitionSymbolResolver.Reset();
        }

        /// <summary>A downloader writing into this test's own temporary directories.</summary>
        private SEC13FDownloader Downloader()
        {
            return new SEC13FDownloader(Path.Combine(_root, "out"), Path.Combine(_root, "processed"), null);
        }
    }
}
