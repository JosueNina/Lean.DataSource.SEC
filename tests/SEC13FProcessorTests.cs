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
using System.Net;
using System.Net.Http;
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
        private bool _configSeeded;
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
            if (_configSeeded)
            {
                // The data folder is process wide and the resolver caches the one it was built with,
                // so a fixture left behind would be read by whatever runs next. A key that was absent
                // goes back to absent: written back as an empty string it left Globals.DataFolder
                // empty for every later test in the process.
                Config.Reset();
                if (_previousDataFolder != null)
                {
                    Config.Set("data-folder", _previousDataFolder);
                }

                if (_previousLookupDate != null)
                {
                    Config.Set("map-file-provider-lookup-date", _previousLookupDate);
                }

                Globals.Reset();
                SecurityDefinitionSymbolResolver.Reset();
                _configSeeded = false;
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
        public void AFilerThatReportedDollarsBefore2023IsNotScaled()
        {
            // The rule was thousands, but not every filer followed it. In Apple's December 2019
            // quarter 85 of 5,365 lines were already in dollars, and multiplied by a thousand they
            // made 88 percent of the total: an implied $2,577 a share against a $293.65 close. Each
            // line is held against the quarter-end close, which needs no other filer.
            SeedCloses("20220930", ("aapl", 150m));
            var filing = new DateTime(2022, 11, 14);
            var quarter = new DateTime(2022, 9, 30);
            var holdings = ReadInfoTable(
                new[]
                {
                    Line("0000000000-00-000001", "037833100", "15", "100", "SH"),
                    Line("0000000000-00-000002", "037833100", "15000", "100", "SH")
                },
                resolve: true,
                Submission("0000000000-00-000001", filing, quarter, cik: 111),
                Submission("0000000000-00-000002", filing, quarter, cik: 222));

            Assert.AreEqual(15000m + 15000m, holdings.Values.Single().HoldingValue,
                "the one in thousands is scaled, the one already in dollars is not");
        }

        [Test]
        public void AFilerStillReportingThousandsAfter2023IsScaled()
        {
            // The same break from the other side: 548 of Apple's March 2026 lines were still in
            // thousands, which left the published value five percent short. The quarter ends on a
            // Sunday, so the close is the Friday's.
            SeedCloses("20231229", ("aapl", 150m));
            var filing = new DateTime(2024, 2, 14);
            var quarter = new DateTime(2023, 12, 31);
            var holdings = ReadInfoTable(
                new[]
                {
                    Line("0000000000-00-000001", "037833100", "15000", "100", "SH"),
                    Line("0000000000-00-000002", "037833100", "15", "100", "SH")
                },
                resolve: true,
                Submission("0000000000-00-000001", filing, quarter, cik: 111),
                Submission("0000000000-00-000002", filing, quarter, cik: 222));

            Assert.AreEqual(2 * 15000m, holdings.Values.Single().HoldingValue);
        }

        [Test]
        public void TheUnitIsDecidedWithoutLookingAtAnyOtherFiling()
        {
            // The unit used to come from the median of every filing in the three month window, so a
            // filing made on its first day was corrected with filings made weeks later. Read alone,
            // the way the daily job reads a day, the same filing comes out the same.
            SeedCloses("20220930", ("aapl", 150m));
            var filing = new DateTime(2022, 10, 3);
            var quarter = new DateTime(2022, 9, 30);

            var alone = ReadInfoTable(
                new[] { Line("0000000000-00-000001", "037833100", "15000", "100", "SH") },
                resolve: true,
                Submission("0000000000-00-000001", filing, quarter, cik: 111));

            Assert.AreEqual(15000m, alone.Values.Single().HoldingValue,
                "a lone filing in dollars before 2023 is not multiplied by the thousands rule");
        }

        [Test]
        public void ALineInAnotherUnitThanTheRestOfItsFilingIsCorrectedOnItsOwn()
        {
            // Some filers mix units inside one filing, so a decision per filing left Apple's June 2020
            // quarter 14 percent high: the filing is in thousands, its Apple line in dollars. Held
            // against its own close, the line is measured on its own.
            SeedCloses("20200630", ("aapl", 360m), ("msft", 200m), ("nvda", 400m));
            var filing = new DateTime(2020, 8, 14);
            var quarter = new DateTime(2020, 6, 30);

            var holdings = ReadInfoTable(
                new[]
                {
                    Line("0000000000-00-000099", "037833100", "36000", "100", "SH"),
                    Line("0000000000-00-000099", "594918104", "20", "100", "SH"),
                    Line("0000000000-00-000099", "67066G104", "40", "100", "SH")
                },
                resolve: true,
                Submission("0000000000-00-000099", filing, quarter, cik: 99));

            Assert.AreEqual(36000m, holdings.Single(pair => pair.Key.Cusip == "037833100").Value.HoldingValue,
                "the dollar line is not scaled");
            Assert.AreEqual(20000m, holdings.Single(pair => pair.Key.Cusip == "594918104").Value.HoldingValue,
                "and the rest of its filing still is");
        }

        [Test]
        public void ALineAThousandTimesTheCloseIsBroughtDown()
        {
            // Three SPY lines of the March 2023 quarter carried VALUE a thousand times the price, and 16
            // percent of the total with it. It is the VALUE that is off, not the share count: the same
            // filers reported the same number of shares the quarter before, as 4,578 of the 4,740
            // comparable lines of that quarter did.
            SeedCloses("20230331", ("spy", 409.39m));
            var filing = new DateTime(2023, 5, 15);
            var quarter = new DateTime(2023, 3, 31);

            var holdings = ReadInfoTable(
                new[]
                {
                    Line("0000000000-00-000001", "78462F103", "40939", "100", "SH"),
                    Line("0000000000-00-000002", "78462F103", "40939000", "100", "SH")
                },
                resolve: true,
                Submission("0000000000-00-000001", filing, quarter, cik: 1),
                Submission("0000000000-00-000002", filing, quarter, cik: 2));

            Assert.AreEqual(2 * 40939m, holdings.Values.Single().HoldingValue);
        }

        [Test]
        public void ARunWithoutTheSecUserAgentKeysFailsBeforeAnyRequest()
        {
            // The SEC asks automated readers to identify themselves, and the reports dataset reads the
            // name and email from these two keys. Without them the run stops instead of calling out
            // anonymously.
            var previousName = Config.Get("sec-user-agent-company-name", null);
            var previousEmail = Config.Get("sec-user-agent-company-email", null);
            try
            {
                Config.Set("sec-user-agent-company-name", string.Empty);
                Config.Set("sec-user-agent-company-email", string.Empty);

                using var downloader = Downloader();
                Assert.Throws<ArgumentException>(() => downloader.Run());
            }
            finally
            {
                // As in TearDown: written back as an empty string, an absent key would stay set.
                Config.Reset();
                if (previousName != null)
                {
                    Config.Set("sec-user-agent-company-name", previousName);
                }

                if (previousEmail != null)
                {
                    Config.Set("sec-user-agent-company-email", previousEmail);
                }
            }
        }

        [Test]
        public void ALineAMillionTimesOffIsLeftToItsFiling()
        {
            // A price a thousand times below the close in a thousands filing would take a millionfold
            // factor. That is a wrong share count, not a unit, and scaling such lines put Apple's
            // December 2019 quarter ten percent above its close.
            SeedCloses("20191231", ("aapl", 290m));
            var filing = new DateTime(2020, 2, 14);
            var quarter = new DateTime(2019, 12, 31);

            var holdings = ReadInfoTable(
                new[]
                {
                    Line("0000000000-00-000001", "037833100", "29", "100", "SH"),
                    Line("0000000000-00-000002", "037833100", "29", "100", "SH"),
                    Line("0000000000-00-000002", "037833100", "29", "100", "SH"),
                    Line("0000000000-00-000002", "037833100", "29", "100000", "SH")
                },
                resolve: true,
                Submission("0000000000-00-000001", filing, quarter, cik: 1),
                Submission("0000000000-00-000002", filing, quarter, cik: 2));

            Assert.AreEqual(4 * 29000m, holdings.Values.Single().HoldingValue,
                "the odd line keeps the thousands factor its other lines prove");
        }

        [TestCase(true, 1000, 0, new double[0])]                  // nothing to compare against: the rule stands
        [TestCase(false, 1, 0, new double[0])]
        [TestCase(true, 1000, 3, new[] { -3.0, -2.9, -3.1 })]     // thousands, as the rule says
        [TestCase(true, 1, 4, new[] { 0.0, 0.1, -0.1, -3.0 })]    // whole dollars before 2023
        [TestCase(false, 1, 2, new[] { 0.1, -0.1 })]
        [TestCase(false, 1000, 3, new[] { -3.0, -2.8, 0.1 })]     // still thousands after 2023
        [TestCase(false, 1, 10, new[] { -3.0, -2.9 })]            // two of ten priced lines on a step: no evidence
        [TestCase(true, 1, 10, new[] { -3.0, -2.9 })]             // and before 2023 it is not scaled up either
        public void AFilingsUnitComesFromHowItsPricesCompareWithTheClose(bool thousandsRule,
            decimal expected, int priced, double[] offsets)
        {
            Assert.AreEqual(expected, SEC13FDownloader.ValueMultiplier(offsets.ToList(), priced, thousandsRule));
        }

        [Test]
        public void AFilingWithTheShareCountTypedAsValueIsNotScaled()
        {
            // A manager's 9 March 2026 filing carried SSHPRNAMT equal to VALUE on every line, a price of
            // exactly $1. Against a close near $1,000 that reads as thousands, so the filing was taken to
            // report in thousands and every one of its lines was multiplied: Dow's December 2025 quarter
            // went from $12.6 billion to $50.2 billion. Most of its lines sit on no unit step, which is no
            // evidence of a unit, and a line that sits on none keeps the rule of its filing date.
            SeedCloses("20251231", ("aapl", 250m), ("msft", 480m), ("nvda", 180m), ("spy", 680m));
            var filing = new DateTime(2026, 3, 9);
            var quarter = new DateTime(2025, 12, 31);
            var holdings = ReadInfoTable(
                new[]
                {
                    Line("0000000000-00-000001", "037833100", "1000000", "1000000", "SH"),
                    Line("0000000000-00-000001", "594918104", "500000", "500000", "SH"),
                    Line("0000000000-00-000001", "67066G104", "300000", "300000", "SH"),
                    Line("0000000000-00-000001", "78462F103", "200000", "200000", "SH")
                },
                resolve: true,
                Submission("0000000000-00-000001", filing, quarter, cik: 1964189));

            Assert.AreEqual(1000000m, holdings.Single(pair => pair.Key.Cusip == "037833100").Value.HoldingValue);
            Assert.AreEqual(300000m, holdings.Single(pair => pair.Key.Cusip == "67066G104").Value.HoldingValue);

            // MSFT at $480 and SPY at $680 put a $1 price on the thousands step by chance. In a filing
            // whose lines mostly sit on no step those landings are not evidence either.
            Assert.AreEqual(500000m, holdings.Single(pair => pair.Key.Cusip == "594918104").Value.HoldingValue);
            Assert.AreEqual(200000m, holdings.Single(pair => pair.Key.Cusip == "78462F103").Value.HoldingValue);
        }

        [Test]
        public void PrincipalValueIsThePrincipalAmountAndNotTheMarketValue()
        {
            // On a PRN line SSHPRNAMT is the principal amount and VALUE what that debt is worth.
            // The property publishes the first, as its documentation says.
            var holdings = ReadInfoTable(
                new[] { Line("0000000000-00-000001", "037833100", "4800000", "5000000", "PRN") },
                Submission("0000000000-00-000001", new DateTime(2024, 2, 14), new DateTime(2023, 12, 31)));

            var holding = holdings.Values.Single();
            Assert.AreEqual(5000000m, holding.PrincipalValue);
            Assert.AreEqual(0m, holding.HoldingValue, "debt stays out of the share value");
        }

        [Test]
        public void AnAmendmentIsKeptApartFromTheOriginalsUntilItsFilerIsKnown()
        {
            // The reader cannot tell whether an amendment restates a position the manager already
            // reported, possibly in an earlier archive, so it keeps each amending filer's lines apart
            // instead of summing them in. AdmitAmendment decides later, against the filer state.
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

            Assert.AreEqual(new[] { 111 }, holding.Ciks.ToArray(), "only the original's filer is gathered");
            Assert.AreEqual(100m, holding.Shares, "only the original's shares are in the totals");
            Assert.AreEqual(1000m, holding.HoldingValue, "only the original's value is in the totals");
            Assert.AreEqual(300m, holding.Amendments[222].Shares, "the amendment's shares are held apart");
            Assert.AreEqual(2000m, holding.Amendments[222].HoldingValue, "the amendment's value is held apart");
        }

        [Test]
        public void ARestatementOfAPositionAlreadyReportedAddsNothing()
        {
            // The double count this rule removes. Apple's March 2026 quarter carried 61 managers with
            // both an original and a RESTATEMENT, and summing both put 993,574,019 shares in twice,
            // 9.6 percent of the published total. Once the manager is counted for the security and
            // quarter, its later amendment is not added on top.
            using var downloader = Downloader();
            var security = Security("AAPL");
            var quarter = new DateTime(2023, 12, 31);

            downloader.CountNewFilers(security, quarter, new[] { 111 }, new DateTime(2024, 2, 1));

            Assert.IsFalse(downloader.AdmitAmendment(security, quarter, 111, new DateTime(2024, 3, 1), out var isNew),
                "a restatement of a position the manager already reported must not be summed");
            Assert.IsFalse(isNew, "and the manager is not counted again");
        }

        [Test]
        public void AnAmendmentNamingASecurityForTheFirstTimeIsAdded()
        {
            // What a NEW HOLDINGS amendment carries, and what a restatement carries for a security the
            // original left out: a position nobody had counted for this manager yet. It is added, and
            // the manager becomes a holder.
            using var downloader = Downloader();
            var security = Security("AAPL");
            var quarter = new DateTime(2023, 12, 31);

            Assert.IsTrue(downloader.AdmitAmendment(security, quarter, 222, new DateTime(2024, 3, 1), out var isNew));
            Assert.IsTrue(isNew, "the manager is new to this security and quarter");
        }

        [Test]
        public void OneAmendmentNamingTwoCusipsOfTheSameSecurityIsAddedForBoth()
        {
            // An issuer's share classes, or a debt CUSIP, resolve to the same security. The first
            // CUSIP's group marks the manager counted, and the second group of the same filing on
            // the same day must still be admitted, or half of that amendment would vanish.
            using var downloader = Downloader();
            var security = Security("AAPL");
            var quarter = new DateTime(2023, 12, 31);
            var filed = new DateTime(2024, 3, 1);

            Assert.IsTrue(downloader.AdmitAmendment(security, quarter, 333, filed, out var first));
            Assert.IsTrue(downloader.AdmitAmendment(security, quarter, 333, filed, out var second),
                "the same amendment's second CUSIP is part of the same position");
            Assert.IsTrue(first, "counted on the first CUSIP");
            Assert.IsFalse(second, "and only once");
            Assert.IsFalse(downloader.AdmitAmendment(security, quarter, 333, filed.AddDays(10), out _),
                "a later amendment by the same manager is a restatement of what it already added");
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
        public void AMergeThatReadsNoNewFilingReproducesThePublishedRows()
        {
            // The same property as above, seen from where it actually matters: the production job
            // runs daily and most days bring nothing new for a given security. What it publishes then
            // has to be what it read, value for value, or every quiet day would perturb the history.
            var published = new (string, SEC13FDownloader.HoldingsRow)[]
            {
                ("aapl", Row("20240201 17:30", "20231231", 3m, 1234.56789m)),
                ("aapl", Row("20240214 17:30", "20231231", 5m, 1234.56789001m, confidential: true))
            };

            var merged = SEC13FDownloader.MergeSecurity(published, Array.Empty<(string, SEC13FDownloader.HoldingsRow)>());

            Assert.AreEqual(published.Length, merged.Count);
            for (var i = 0; i < published.Length; i++)
            {
                Assert.AreEqual(published[i].Item1, merged[i].Ticker);
                Assert.AreEqual(published[i].Item2.Time, merged[i].Row.Time);
                Assert.AreEqual(published[i].Item2.Values, merged[i].Row.Values);
                Assert.AreEqual(published[i].Item2.ConfidentialOmitted, merged[i].Row.ConfidentialOmitted);
            }
        }

        [Test]
        public void ARenameCarriesTheQuartersRunningTotalIntoTheNewTicker()
        {
            // The defect this exists for. Facebook became Meta on 2022-06-09, while the March 2022
            // quarter was still receiving late filings. With running totals kept per ticker file,
            // meta.csv restarted that quarter at zero and the universe showed Meta held by 3 managers
            // the week after it had shown 3,182. The total belongs to the security, and carries on
            // across the rename in whichever file the row lands.
            var published = new (string, SEC13FDownloader.HoldingsRow)[]
            {
                ("fb", Row("20220401 17:30", "20220331", 2m, 204644m)),
                ("fb", Row("20220608 17:30", "20220331", 3182m, 1827789746m))
            };
            var increments = new (string, SEC13FDownloader.HoldingsRow)[]
            {
                ("meta", Row("20220610 17:30", "20220331", 0m, 30693m)),
                ("meta", Row("20220613 17:30", "20220331", 2m, 21012m))
            };

            var merged = SEC13FDownloader.MergeSecurity(published, increments);

            Assert.AreEqual(new[] { "fb", "fb", "meta", "meta" }, merged.Select(entry => entry.Ticker).ToArray());
            Assert.AreEqual(3182m, merged[2].Row.Values[0], "the quarter's holders carry on into meta.csv");
            Assert.AreEqual(1827789746m + 30693m, merged[2].Row.Values[1], "and so do its shares");
            Assert.AreEqual(3184m, merged[3].Row.Values[0]);
        }

        [Test]
        public void ThisRunWinsOverThePublishedReadingOfTheSameReleaseDate()
        {
            // An incremental run reads its window again, so its reading of a quarter and release date
            // is the current one and replaces the published increment rather than adding to it.
            var published = new (string, SEC13FDownloader.HoldingsRow)[]
            {
                ("aapl", Row("20240201 17:30", "20231231", 3m, 30m)),
                ("aapl", Row("20240214 17:30", "20231231", 5m, 50m))
            };
            var increments = new (string, SEC13FDownloader.HoldingsRow)[]
            {
                ("aapl", Row("20240214 17:30", "20231231", 4m, 40m))
            };

            var merged = SEC13FDownloader.MergeSecurity(published, increments);

            Assert.AreEqual(2, merged.Count);
            Assert.AreEqual(7m, merged[1].Row.Values[0], "three published plus the four re-read, not plus the two published");
            Assert.AreEqual(70m, merged[1].Row.Values[1]);
        }

        // ---- Chronological output order ---------------------------------------------------------

        [Test]
        public void MergedRowsComeOutInReleaseOrderAndNotQuarterOrder()
        {
            // Accumulate() groups by quarter, so its natural order is NOT the order the file ships
            // in, and the two disagree exactly when a late amendment for an old quarter arrives
            // after a newer quarter has started reporting. LEAN's SubscriptionDataReader drops a
            // point whose timestamp moves backwards and does so silently, so a file left in quarter
            // order loses rows with nothing in the log to show for it.
            var published = new (string, SEC13FDownloader.HoldingsRow)[]
            {
                ("aapl", Row("20240215 17:30", "20231231", 1m, 5m))
            };
            var increments = new (string, SEC13FDownloader.HoldingsRow)[]
            {
                // Newer quarter, released first.
                ("aapl", Row("20240501 17:30", "20240331", 5m, 100m)),
                // Older quarter, an amendment released two weeks later.
                ("aapl", Row("20240515 17:30", "20231231", 1m, 7m))
            };

            var merged = SEC13FDownloader.MergeSecurity(published, increments);

            Assert.AreEqual(new[] { "20240215 17:30", "20240501 17:30", "20240515 17:30" },
                merged.Select(entry => entry.Row.Time.ToStringInvariant("yyyyMMdd HH:mm")).ToArray());

            // The amendment accumulates onto the published row of its own quarter rather than
            // starting over: one holder plus one, five shares plus seven.
            Assert.AreEqual(2m, merged[2].Row.Values[0]);
            Assert.AreEqual(12m, merged[2].Row.Values[1]);
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

            Assert.Throws<InvalidOperationException>(() => downloader.Run(),
                "the run carried on with no history behind it");
        }

        [Test]
        public void ARunIntoADestinationAlreadyHoldingFilesFails()
        {
            // The job hands the destination over empty. Files an earlier run left there would be read
            // into the universe and published again, so a run refuses to start on top of them.
            var destination = Path.Combine(_root, "out", SEC13FHoldings.ReportFolder);
            Directory.CreateDirectory(destination);
            File.WriteAllText(Path.Combine(destination, "aapl.csv"), "20240214 17:30,20231231,1,1,1,0,0,0,0,0,0");

            using var downloader = Downloader();

            Assert.Throws<InvalidOperationException>(() => downloader.RequireEmptyDestination());
        }

        // ---- Publication time and the EDGAR days ---------------------------------------------------

        [Test]
        public void AFilingIsPublishedTheMorningAfterItsFilingDate()
        {
            // The defect this dataset shipped with: a row stamped 17:30 on its filing date, when the
            // SEC data sets publish that filing up to three months later. EDGAR lists a day's filings
            // at about 22:05 ET and the daily job reads them at 01:00 the next day, so the row is
            // published at 03:00 the morning after, and a backtest can read it no earlier.
            using var downloader = Downloader();

            Assert.AreEqual(new DateTime(2026, 5, 11, 3, 0, 0), downloader.ReleaseOf(new DateTime(2026, 5, 10)));
        }

        [Test]
        public void ADayReadLateIsPublishedWhenTheRunReadIt()
        {
            // A daily run that picks up a day it missed, a late index or a failed night, could not
            // have published that day before its own deployment date. Stamping the rows with the
            // filing date would put them in the past, where live never delivered them.
            using var downloader = new SEC13FDownloader(
                Path.Combine(_root, "out"), Path.Combine(_root, "processed"), new DateTime(2026, 9, 9));

            Assert.AreEqual(new DateTime(2026, 9, 10, 3, 0, 0), downloader.ReleaseOf(new DateTime(2026, 9, 3)),
                "the missed day is stamped with the run that read it");
            Assert.AreEqual(new DateTime(2026, 9, 10, 3, 0, 0), downloader.ReleaseOf(new DateTime(2026, 9, 9)),
                "and the deployment date itself the usual way");
        }

        [Test]
        public void TheRebuildHandsOverToEdgarTheDayAfterTheLastDataSet()
        {
            // The data sets come out in three month batches, so the rebuild reads them as far as they
            // reach and EDGAR after that, the way the daily job does. EDGAR can also take over
            // earlier, which is how the two sources are compared over a window both carry.
            var archives = new List<SEC13FDownloader.Archive>
            {
                new("01dec2025-28feb2026_form13f.zip", "https://localhost/a.zip", new DateTime(2025, 12, 1), new DateTime(2026, 2, 28)),
                new("01mar2026-31may2026_form13f.zip", "https://localhost/b.zip", new DateTime(2026, 3, 1), new DateTime(2026, 5, 31))
            };

            Assert.AreEqual(new[] { "01dec2025-28feb2026_form13f.zip", "01mar2026-31may2026_form13f.zip" },
                SEC13FDownloader.ArchivesBefore(archives, new DateTime(2026, 6, 1)).Select(archive => archive.Name).ToArray());
            Assert.AreEqual(new[] { "01dec2025-28feb2026_form13f.zip" },
                SEC13FDownloader.ArchivesBefore(archives, new DateTime(2026, 3, 1)).Select(archive => archive.Name).ToArray());
        }

        [Test]
        public void EdgarCannotTakeOverInTheMiddleOfADataSet()
        {
            // The window's filings before the switch would come from the data set and the rest from
            // EDGAR only if the archive were cut by date, which it is not: it would be read twice.
            var archives = new List<SEC13FDownloader.Archive>
            {
                new("01mar2026-31may2026_form13f.zip", "https://localhost/b.zip", new DateTime(2026, 3, 1), new DateTime(2026, 5, 31))
            };

            Assert.Throws<InvalidOperationException>(() => SEC13FDownloader.ArchivesBefore(archives, new DateTime(2026, 4, 15)));
        }

        [Test]
        public void ADailyRunReadsTheWeekdaysNoEarlierRunFoldedIn()
        {
            // A day already published is never read again, since its filings would be added a second
            // time under a later stamp, and a day without an index is tried again by the next run.
            // Nor is a day before EDGAR took over from the data sets: the rebuild read those from the
            // data set, so they are not in the list, and reading them from EDGAR counted them twice.
            var shelf = ShelfWithFilerState();
            File.WriteAllText(Path.Combine(shelf, "edgar-days.txt"), "#from 20260901\n20260903\n20260904\n");

            using var downloader = new SEC13FDownloader(
                Path.Combine(_root, "out"), Path.Combine(_root, "processed"), new DateTime(2026, 9, 9));
            downloader.ReadEdgarState();

            Assert.AreEqual(
                new[] { "20260901", "20260902", "20260907", "20260908", "20260909" },
                downloader.EdgarDaysToRead(new DateTime(2026, 8, 30), new DateTime(2026, 9, 9))
                    .Select(day => day.ToString("yyyyMMdd")).ToArray());
        }

        [Test]
        public void AnIncrementalRunWithoutTheEdgarStateFails()
        {
            // Without the list of days already published a run cannot tell a new day from one it
            // would add a second time, so it stops instead of guessing.
            var shelf = ShelfWithFilerState();
            File.Delete(Path.Combine(shelf, "edgar-days.txt"));
            File.WriteAllLines(Path.Combine(shelf, "aapl.csv"), new[] { "20240215 03:00,20231231,1,5,50,0,0,0,0,0,0" });

            using var downloader = new SEC13FDownloader(
                Path.Combine(_root, "out"), Path.Combine(_root, "processed"), new DateTime(2026, 9, 9));

            Assert.Throws<InvalidOperationException>(() => downloader.RequirePublishedHistoryForIncrementalRun());
        }

        [Test]
        public void AMissingDeploymentDateIsAnErrorUnlessTheRebuildIsAskedFor()
        {
            // An empty date used to mean the full history, which turned a misconfigured nightly job
            // into a five gigabyte refetch that exited zero.
            var previousDate = Environment.GetEnvironmentVariable("QC_DATAFLEET_DEPLOYMENT_DATE");
            try
            {
                Environment.SetEnvironmentVariable("QC_DATAFLEET_DEPLOYMENT_DATE", null);

                Config.Set(Program.RebuildHistoryKey, "false");
                Assert.IsFalse(Program.TryParseDeploymentDate(out _), "no date and no rebuild asked for");

                Config.Set(Program.RebuildHistoryKey, "true");
                Assert.IsTrue(Program.TryParseDeploymentDate(out var rebuild));
                Assert.IsNull(rebuild, "the rebuild runs over the whole history");

                Environment.SetEnvironmentVariable("QC_DATAFLEET_DEPLOYMENT_DATE", "20260908");
                Assert.IsTrue(Program.TryParseDeploymentDate(out var date));
                Assert.AreEqual(new DateTime(2026, 9, 8), date);
            }
            finally
            {
                Environment.SetEnvironmentVariable("QC_DATAFLEET_DEPLOYMENT_DATE", previousDate);
                Config.Set(Program.RebuildHistoryKey, "false");
            }
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

        [Test]
        public void TheFilerStateDoesNotDependOnTheOrderFilersWereFirstSeen()
        {
            // A full run meets securities and quarters in filing order, while an incremental run
            // meets them in the order the published state lists them. Written in the order of the
            // packed keys, whose indexes come from that first sighting, the same filers came out in
            // a different order: the incremental republished all 58 million of them unchanged and
            // filers.zip still changed bytes.
            var path = Path.Combine(_root, "out", SEC13FHoldings.ReportFolder, "filers.zip");
            var apple = Security("AAPL");
            var microsoft = Security("MSFT");
            var december = new DateTime(2023, 12, 31);
            var march = new DateTime(2024, 3, 31);

            using (var first = Downloader())
            {
                first.CountNewFilers(microsoft, march, new[] { 222 }, Filed);
                first.CountNewFilers(apple, december, new[] { 111 }, Filed);
                first.CountNewFilers(apple, march, new[] { 333 }, Filed);
                first.WriteFilerState();
            }

            var before = File.ReadAllBytes(path);

            using (var second = Downloader())
            {
                second.CountNewFilers(apple, december, new[] { 111 }, Filed);
                second.CountNewFilers(apple, march, new[] { 333 }, Filed);
                second.CountNewFilers(microsoft, march, new[] { 222 }, Filed);
                second.WriteFilerState();
            }

            Assert.AreEqual(Convert.ToBase64String(before), Convert.ToBase64String(File.ReadAllBytes(path)),
                "the same filers seen in another order have to write the same archive");
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
            File.WriteAllText(Path.Combine(shelf, "edgar-days.txt"), "#from 20260601\n20260601\n");
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

        // ---- Delisted securities and the universe's shelf life ----------------------------------

        [Test]
        public void AFilingAfterTheDelistingHasNoTickerToLandIn()
        {
            // The map file ends at the delisting, and falling back to the last ticker wrote years of
            // later filings under it: DirecTV, delisted in 2015, sat in the May 2026 universe, and a
            // ticker reused by another company mixed two companies in one file. After the last map
            // file row there is no ticker, and LEAN would not read such a row in any case.
            SeedMapFileRows(("dead", new[] { "19801212,dead", "20150724,dead" }));

            using var downloader = Downloader();
            var security = SecurityIdentifier.GenerateEquity(new DateTime(1980, 12, 12), "DEAD", Market.USA);

            Assert.AreEqual("DEAD", downloader.ResolveTicker(security, new DateTime(2015, 1, 5))?.ToUpperInvariant(),
                "listed then");
            Assert.IsNull(downloader.ResolveTicker(security, new DateTime(2016, 2, 14)), "delisted by then");
        }

        [TestCase("20240201", "20230930")]   // inside the December quarter's filing window
        [TestCase("20240214", "20230930")]   // the deadline itself
        [TestCase("20240215", "20231231")]   // the deadline has passed: September is stale
        [TestCase("20240331", "20231231")]   // a quarter end is not yet the new quarter
        [TestCase("20240401", "20231231")]   // the March quarter starts filing
        [TestCase("20240516", "20240331")]
        public void AQuarterStaysLiveUntilTheNextQuartersDeadlinePasses(string day, string oldest)
        {
            // Between a quarter end and its 45 day deadline both the finished quarter and the one
            // filling in are live, which is the two-quarter rule. Once the deadline passes the older
            // one is stale for everyone who files on time.
            Assert.AreEqual(DateTime.ParseExact(oldest, "yyyyMMdd", null),
                SEC13FDownloader.OldestLivePeriod(DateTime.ParseExact(day, "yyyyMMdd", null)));
        }

        [Test]
        public void ASecurityNobodyReportsAnyMoreLeavesTheUniverse()
        {
            // The forward fill used to carry a security's last row forever: 252 of the 6,886
            // securities in the May 2026 universe had nothing newer than 2024, some back to 2013,
            // and Monsanto was still there with its March 2022 quarter. A quarter now leaves once
            // the next one's deadline has passed without it being refreshed, and a security leaves
            // after its delisting date.
            SeedMapFileRows(
                ("aapl", new[] { "19801212,aapl", "20501231,aapl" }),
                ("stale", new[] { "19801212,stale", "20501231,stale" }),
                ("gone", new[] { "19801212,gone", "20240305,gone" }));

            var holdings = Path.Combine(_root, "out", SEC13FHoldings.ReportFolder);
            Directory.CreateDirectory(holdings);
            File.WriteAllLines(Path.Combine(holdings, "aapl.csv"), new[]
            {
                "20240201 17:30,20231231,5,10,100,0,0,0,0,0,0",
                "20240415 17:30,20240331,6,12,120,0,0,0,0,0,0",
                "20240520 17:30,20240331,7,14,140,0,0,0,0,0,0"
            });
            File.WriteAllLines(Path.Combine(holdings, "stale.csv"), new[] { "20240201 17:30,20231231,1,1,1,0,0,0,0,0,0" });
            File.WriteAllLines(Path.Combine(holdings, "gone.csv"), new[] { "20240202 17:30,20231231,2,2,2,0,0,0,0,0,0" });

            using (var downloader = Downloader())
            {
                downloader.BuildUniverseFiles();
            }

            var universe = Path.Combine(holdings, "universe");
            string[] Tickers(string day) => File.ReadAllLines(Path.Combine(universe, $"{day}.csv"))
                .Where(line => line.Length > 0)
                .Select(line => line.Split(',')[1])
                .OrderBy(ticker => ticker, StringComparer.Ordinal)
                .ToArray();

            Assert.AreEqual(new[] { "AAPL", "GONE", "STALE" }, Tickers("20240205"), "all three are live in February");
            Assert.AreEqual(new[] { "AAPL", "STALE" }, Tickers("20240306"), "GONE leaves after its delisting date");
            Assert.AreEqual(new[] { "AAPL", "STALE" }, Tickers("20240515"),
                "the December quarter is still live up to the March quarter's deadline");
            Assert.AreEqual(new[] { "AAPL" }, Tickers("20240517"),
                "STALE never reported March and leaves once that deadline has passed");
        }

        /// <summary>
        /// Points LEAN's data folder at a map file archive holding exactly the rows given per ticker,
        /// so a test can shape a listing, a rename or a delisting. See SeedMapFiles for why the data
        /// folder and lookup date are pinned.
        /// </summary>
        private void SeedMapFileRows(params (string Ticker, string[] Rows)[] files)
        {
            var dataFolder = Path.Combine(_root, "data");
            var mapFiles = Path.Combine(dataFolder, "equity", "usa", "map_files");
            Directory.CreateDirectory(mapFiles);

            var lookupDate = new DateTime(2026, 1, 2);
            using (var zip = ZipFile.Open(
                       Path.Combine(mapFiles, $"map_files_{lookupDate:yyyyMMdd}.zip"), ZipArchiveMode.Create))
            {
                foreach (var (ticker, rows) in files)
                {
                    using var entry = new StreamWriter(zip.CreateEntry($"{ticker}.csv").Open());
                    foreach (var row in rows)
                    {
                        entry.WriteLine(row);
                    }
                }
            }

            // Null when absent, so the teardown can tell a missing key from an empty one.
            if (!_configSeeded)
            {
                _previousDataFolder = Config.Get("data-folder", null);
                _previousLookupDate = Config.Get("map-file-provider-lookup-date", null);
                _configSeeded = true;
            }

            Config.Set("data-folder", dataFolder);
            Config.Set("map-file-provider-lookup-date", $"{lookupDate:yyyyMMdd}");
            Globals.Reset();
            SecurityDefinitionSymbolResolver.Reset();
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

        [Test]
        public void ACrosswalkTickerNamesTheSecurityItNamedWhenTheFundsReportedIt()
        {
            // Facebook's CUSIP reaches the crosswalk as META, the ticker funds report today. Resolved
            // at each filing date instead, META named the Roundhill Metaverse ETF from 2021-06-30 to
            // 2022-01-28, so seven months of Facebook's holders were published as that ETF's, and
            // before it META named nothing at all.
            SeedMapFileRows(
                ("meta", new[] { "20120518,fb", "20220608,fb", "20501231,meta" }),
                ("metv", new[] { "20210630,meta", "20220128,meta", "20501231,metv" }));

            using var downloader = Downloader();
            downloader.TickerCrosswalk = new Dictionary<string, SEC13FTickerCrosswalk.Entry>
            {
                ["30303M102"] = new("META", new DateTime(2026, 1, 1))
            };

            var security = downloader.ResolveThroughTicker("30303M102");

            Assert.AreEqual("FB", downloader.ResolveTicker(security, new DateTime(2021, 10, 15))?.ToUpperInvariant(),
                "Facebook's filing lands in Facebook's file, not the ETF's");
            Assert.AreEqual("FB", downloader.ResolveTicker(security, new DateTime(2016, 2, 12))?.ToUpperInvariant(),
                "and the years before the ETF existed resolve too");
            Assert.AreEqual("META", downloader.ResolveTicker(security, new DateTime(2023, 2, 14))?.ToUpperInvariant());
        }

        [Test]
        public void ACrosswalkTickerThatNamedNoUsSecurityWhenObservedResolvesToNothing()
        {
            // Barrick's CUSIP reached the crosswalk as ABX, its Toronto ticker, in data reported from
            // July 2025. No US security traded as ABX then, and the resolver still returned the
            // company that took the ticker in December, so its holders would have gone there.
            SeedMapFileRows(
                ("abx", new[] { "20200914,eres", "20230703,eres", "20251229,abl", "20501231,abx" }),
                ("b", new[] { "19980102,abx", "20181231,abx", "20250508,gold", "20501231,b" }));

            using var downloader = Downloader();
            downloader.TickerCrosswalk = new Dictionary<string, SEC13FTickerCrosswalk.Entry>
            {
                ["067901108"] = new("ABX", new DateTime(2025, 7, 1))
            };

            Assert.IsNull(downloader.ResolveThroughTicker("067901108"));
        }

        [TestCase("BRK.B", true)]
        [TestCase("AAPL", true)]
        [TestCase("UA.C ", false)]   // the map files carry a trailing space for Under Armour's class C
        [TestCase("A/B", false)]
        [TestCase("X|Y", false)]
        public void OnlyATickerLeanCanAskForBecomesAFile(string ticker, bool expected)
        {
            // A Symbol cannot hold a space or a '|', so a file named after such a ticker is one LEAN
            // never reads, and every row in it failed when the delivery archive was read back.
            Assert.AreEqual(expected, SEC13FDownloader.IsFileNameSafe(ticker));
        }

        [Test]
        public void ASecurityHasNoTickerBeforeItBeganTrading()
        {
            // Before its first row a map file answers with its first ticker, so a filing dated
            // before the listing would land under a ticker the security did not have yet.
            SeedMapFileRows(("late", new[] { "20200914,late", "20501231,late" }));

            using var downloader = Downloader();
            var security = SecurityIdentifier.GenerateEquity(new DateTime(2020, 9, 14), "LATE", Market.USA);

            Assert.IsNull(downloader.ResolveTicker(security, new DateTime(2019, 6, 3)), "not listed yet");
            Assert.AreEqual("LATE", downloader.ResolveTicker(security, new DateTime(2021, 1, 4))?.ToUpperInvariant());
        }

        [Test]
        public void TheNewestObservationOfACusipWinsWhateverOrderTheQuartersArrive()
        {
            // The quarters used to be folded newest first, so a first build kept the oldest ticker
            // of a renamed security while a refresh kept the newest.
            var older = new[] { KeyValuePair.Create("30303M102", "FB") };
            var newer = new[] { KeyValuePair.Create("30303M102", "META") };

            var olderFirst = new Dictionary<string, SEC13FTickerCrosswalk.Entry>();
            SEC13FTickerCrosswalk.Fold(olderFirst, new DateTime(2022, 4, 1), older);
            SEC13FTickerCrosswalk.Fold(olderFirst, new DateTime(2022, 7, 1), newer);

            var newerFirst = new Dictionary<string, SEC13FTickerCrosswalk.Entry>();
            SEC13FTickerCrosswalk.Fold(newerFirst, new DateTime(2022, 7, 1), newer);
            SEC13FTickerCrosswalk.Fold(newerFirst, new DateTime(2022, 4, 1), older);

            Assert.AreEqual("META", olderFirst["30303M102"].Ticker);
            Assert.AreEqual("META", newerFirst["30303M102"].Ticker);
        }

        // ---- Crosswalk resolutions are held against the close --------------------------------------

        [Test]
        public void ACrosswalkGroupStaysUnlessItsPricesSayItIsAnotherSecurity()
        {
            // The crosswalk is a fund administrator's ticker, so a CUSIP can reach the wrong company.
            // The group's own prices against the quarter-end close decide, on the day it is read.
            SeedCloses("20251231", ("etsy", 120m), ("defi", 129.50m), ("aapl", 250m), ("spy", 24.23m));
            using var downloader = Downloader();
            var period = new DateTime(2025, 12, 31);
            var filed = new DateTime(2026, 2, 14);
            bool Keeps(string cusip, string ticker, params double[] prices) =>
                downloader.KeepsCrosswalkGroup(cusip, ListedSince1980(ticker), period, filed, prices.ToList());

            // Etsy's convertible notes reached Etsy's stock: 171 million of its 298 million shares.
            Assert.IsFalse(Keeps("29786AAJ5", "ETSY"), "a note reported as principal carries no price");
            Assert.IsFalse(Keeps("29786AAJ5", "ETSY", 1.02, 0.98), "a note reported as shares trades near par");
            Assert.IsTrue(Keeps("46435GAA0", "SPY", 24.23, 24.20),
                "a CUSIP with letters that trades at the security's own price, as the iBonds ETFs do, stays");

            // DeFi Technologies at $2 reached the $129.50 Hashdex DEFI ETF through its ticker.
            Assert.IsFalse(Keeps("244916102", "DEFI", 2.11, 2.05, 2.11), "three managers at $2 are another company");
            Assert.IsTrue(Keeps("037833100", "AAPL", 25.0), "one manager's odd price is a slip, not another company");
            Assert.IsTrue(Keeps("037833100", "AAPL", 0.25, 0.25, 250.1), "the same prices in thousands match");
            Assert.IsTrue(Keeps("037833100", "AAPL"), "a day of option lines only cannot be checked and stays");
            Assert.IsTrue(Keeps("594918104", "MSFT", 2.0, 2.0, 2.0), "nor can a security the close file does not carry");

            // Avanos on 5 February 2026: four managers in thousands and four in dollars. Their median
            // sat half way between the two units and dropped the group.
            Assert.IsTrue(Keeps("037833100", "AAPL", 0.25, 0.25, 0.25, 0.25, 250.0, 250.1, 249.9, 740.8),
                "a day that mixes the two units is still the security");
        }

        [Test]
        public void ALineThatSitsOnNoUnitStepIsNotScaled()
        {
            // A price about a hundred times below the close is neither dollars nor thousands: a bond at
            // par against its issuer's stock, or another company. Scaling it by the nearest step put
            // Seagate's December 2025 quarter at $413 billion.
            SeedCloses("20251231", ("aapl", 250m));
            var filing = new DateTime(2026, 2, 14);
            var quarter = new DateTime(2025, 12, 31);
            var holdings = ReadInfoTable(
                new[]
                {
                    Line("0000000000-00-000001", "037833100", "250000", "1000", "SH"),
                    Line("0000000000-00-000002", "037833100", "2500", "1000", "SH")
                },
                resolve: true,
                Submission("0000000000-00-000001", filing, quarter, cik: 1),
                Submission("0000000000-00-000002", filing, quarter, cik: 2));

            Assert.AreEqual(250000m + 2500m, holdings.Values.Single().HoldingValue,
                "the $2.50 line keeps its filing's dollar factor instead of becoming $2.5 million");
        }

        [Test]
        public void ABrokenFilingIsNeverScaledUp()
        {
            // Every line reads $1,000 a share, since SSHPRNAMT carries VALUE in thousands. Two of the
            // four prices land on a unit step by chance, not most, so the filing shows no unit. Before
            // 2023 the rule multiplied it by a thousand, and one such manager put Alphabet's September
            // 2020 quarter 9.5 percent above its close.
            SeedCloses("20200930", ("aapl", 100m), ("msft", 200m), ("nvda", 500m), ("spy", 330m));
            var holdings = ReadInfoTable(
                new[]
                {
                    Line("0000000000-00-000001", "037833100", "1000000", "1000", "SH"),
                    Line("0000000000-00-000001", "594918104", "1000000", "1000", "SH"),
                    Line("0000000000-00-000001", "67066G104", "1000000", "1000", "SH"),
                    Line("0000000000-00-000001", "78462F103", "1000000", "1000", "SH")
                },
                resolve: true,
                Submission("0000000000-00-000001", new DateTime(2020, 11, 6), new DateTime(2020, 9, 30), cik: 1));

            Assert.AreEqual(4, holdings.Count);
            Assert.IsTrue(holdings.Values.All(holding => holding.HoldingValue == 1000000m),
                string.Join(", ", holdings.Values.Select(holding => holding.HoldingValue)));
        }

        [TestCase("20200814", "20200630", "20200630", "250000", "2500", 752500)] // dollars before 2023: the rule made the $2.50 line $2,500
        [TestCase("20230214", "20221231", "20221230", "250", "25", 750025)]      // thousands in 2023: the filing's unit made it 1,000 times larger
        public void ALineOnNoStepIsNeverScaledPastItsFilingOrItsDate(string filed, string quarter, string closeDay,
            string onStep, string offStep, decimal expected)
        {
            // Three lines prove the filing's unit and a fourth sits on no step. It cannot be checked, so
            // it takes the smaller of the filing's unit and the rule of the filing date: either one alone
            // inflated one era by hundreds of billions.
            SeedCloses(closeDay, ("aapl", 250m));
            var holdings = ReadInfoTable(
                new[]
                {
                    Line("0000000000-00-000001", "037833100", onStep, "1000", "SH"),
                    Line("0000000000-00-000001", "037833100", onStep, "1000", "SH"),
                    Line("0000000000-00-000001", "037833100", onStep, "1000", "SH"),
                    Line("0000000000-00-000001", "037833100", offStep, "1000", "SH")
                },
                resolve: true,
                Submission("0000000000-00-000001", Time.ParseDate(filed), Time.ParseDate(quarter), cik: 1));

            Assert.AreEqual(expected, holdings.Values.Single().HoldingValue);
        }

        [TestCase(0.0, 0)]
        [TestCase(-3.0, -1)]
        [TestCase(3.1, 1)]
        [TestCase(-2.9, -1)]
        public void AnOffsetNearAThousandfoldStepIsAUnit(double offset, int step)
        {
            Assert.AreEqual(step, SEC13FDownloader.UnitStep(offset));
        }

        [TestCase(-1.5)]    // half way between dollars and thousands
        [TestCase(-2.0)]    // a hundred times below: a bond at par against a stock
        [TestCase(1.79)]
        [TestCase(-6.0)]    // a millionfold step is a share count slip, not a unit
        public void AnOffsetOnNoStepIsNoUnit(double offset)
        {
            Assert.IsNull(SEC13FDownloader.UnitStep(offset));
        }

        [Test]
        public void TheCloseIsNeverReadAfterTheFilingDate()
        {
            // A period typed ahead of its filing date would otherwise read a price nobody had when
            // the filing was made. The quarter ends on a Tuesday; filed the Sunday before, the
            // Friday's close is the newest one there was.
            SeedCloses("20260327", ("aapl", 250m));
            var coarse = Path.Combine(_root, "data", "equity", "usa", "fundamental", "coarse");
            File.WriteAllText(Path.Combine(coarse, "20260331.csv"), $"{ListedSince1980("AAPL")},AAPL,999,1,1,True,1,1\n");
            var prices = new SEC13FClosePrices(coarse);

            Assert.AreEqual(250m, prices.Close(ListedSince1980("AAPL"), new DateTime(2026, 3, 31), new DateTime(2026, 3, 29)));
            Assert.AreEqual(999m, prices.Close(ListedSince1980("AAPL"), new DateTime(2026, 3, 31), new DateTime(2026, 4, 2)));
        }

        // ---- Reading a day from EDGAR ---------------------------------------------------------------

        [Test]
        public void TheDailyIndexYieldsTheHoldingsReportsAndTheirAmendmentsOnly()
        {
            // Notices carry no information table and the data sets' reader skips them too.
            var index = string.Join("\n",
                "Form Type   Company Name   CIK   Date Filed  File Name",
                "---------------------------------------------------------",
                "10-K             SOMETHING ELSE INC          1111111     20260814    edgar/data/1111111/0001111111-26-000001.txt",
                "13F-HR           &PARTNERS                   107136      20260814    edgar/data/107136/0001214659-26-010148.txt",
                "13F-HR/A         FUND 1 ADVISERS LLC         2222222     20260814    edgar/data/2222222/0002222222-26-000003.txt",
                "13F-NT           NOTICE FILER LP             3333333     20260814    edgar/data/3333333/0003333333-26-000004.txt");

            var entries = SEC13FEdgarDay.ParseIndex(index);

            Assert.AreEqual(new[] { "0001214659-26-010148", "0002222222-26-000003" },
                entries.Select(entry => entry.Accession).ToArray());
            Assert.AreEqual(new[] { "13F-HR", "13F-HR/A" }, entries.Select(entry => entry.FormType).ToArray());
            Assert.AreEqual(new[] { 107136, 2222222 }, entries.Select(entry => entry.Cik).ToArray());
            Assert.AreEqual(new DateTime(2026, 8, 14), entries[0].Filed);
        }

        [Test]
        public void AFilingIsReadFromItsSubmissionFile()
        {
            // The period and the confidential flag come from the primary document, the lines from the
            // information table, whatever namespace the filer's software wrote.
            var filing = SEC13FEdgarDay.ParseFiling(SampleEntry(), SampleSubmission());

            Assert.AreEqual("0001214659-26-010148", filing.Accession);
            Assert.AreEqual(new DateTime(2026, 6, 30), filing.Period);
            Assert.IsTrue(filing.ConfidentialOmitted);
            Assert.AreEqual(2, filing.Lines.Count);
            Assert.AreEqual(new[] { "88025U109", "274974", "7172", "SH", null, "7172", "0" }, filing.Lines[0]);
            Assert.AreEqual("Put", filing.Lines[1][4]);
        }

        [Test]
        public void ADaysArchiveCarriesTheTablesTheProcessorReads()
        {
            // The day is written in the data sets' own layout so the rest of the processor reads it
            // like a window, which is what makes EDGAR and the data sets give the same rows.
            var filing = SEC13FEdgarDay.ParseFiling(SampleEntry(), SampleSubmission());

            using var buffer = new MemoryStream();
            SEC13FEdgarDay.WriteArchive(buffer, new[] { filing });
            buffer.Position = 0;
            using var zip = new ZipArchive(buffer, ZipArchiveMode.Read);

            var submission = SEC13FFiles.ReadTable(zip, "edgar", "SUBMISSION.tsv", false,
                "ACCESSION_NUMBER", "FILING_DATE", "SUBMISSIONTYPE", "CIK", "PERIODOFREPORT").Single();
            Assert.AreEqual("2026-08-14", submission.Fields[submission.Columns["FILING_DATE"]]);
            Assert.AreEqual("2026-06-30", submission.Fields[submission.Columns["PERIODOFREPORT"]]);

            var summary = SEC13FFiles.ReadTable(zip, "edgar", "SUMMARYPAGE.tsv", false,
                "ACCESSION_NUMBER", "ISCONFIDENTIALOMITTED").Single();
            Assert.AreEqual("Y", summary.Fields[summary.Columns["ISCONFIDENTIALOMITTED"]]);

            var lines = SEC13FFiles.ReadTable(zip, "edgar", "INFOTABLE.tsv", false,
                "ACCESSION_NUMBER", "CUSIP", "VALUE", "SSHPRNAMT", "SSHPRNAMTTYPE", "PUTCALL",
                "VOTING_AUTH_SOLE", "VOTING_AUTH_SHARED").ToList();
            Assert.AreEqual(2, lines.Count);
            Assert.AreEqual("037833100", lines[1].Fields[lines[1].Columns["CUSIP"]]);
            Assert.AreEqual("Put", lines[1].Fields[lines[1].Columns["PUTCALL"]]);
        }

        [Test]
        public void AnEdgarDayIsPublishedOnlyWhenItsQuarterListsItsIndex()
        {
            // EDGAR answers 403 both for an index that does not exist and for a reader it has blocked,
            // so whether a day is out comes from its listings. A folder EDGAR has not created yet
            // answers 403 too, so it is looked up in its parent first: requesting any folder missing
            // from this map throws, and fails the test.
            const string root = "https://www.sec.gov/Archives/edgar/daily-index/";
            var listings = new Dictionary<string, ISet<string>>
            {
                [root] = new HashSet<string> { "2025", "2026" },
                [root + "2026/"] = new HashSet<string> { "QTR1", "QTR2", "QTR3" },
                [root + "2026/QTR3/"] = new HashSet<string> { "form.20260904.idx", "form.20260908.idx" }
            };

            Assert.IsTrue(SEC13FEdgarDay.IsIndexPublished(new DateTime(2026, 9, 8), url => listings[url]));
            Assert.IsFalse(SEC13FEdgarDay.IsIndexPublished(new DateTime(2026, 9, 7), url => listings[url]), "Labor Day");
            Assert.IsFalse(SEC13FEdgarDay.IsIndexPublished(new DateTime(2026, 10, 1), url => listings[url]), "a new quarter");
            Assert.IsFalse(SEC13FEdgarDay.IsIndexPublished(new DateTime(2027, 1, 4), url => listings[url]), "a new year");
        }

        [Test]
        public void AnEdgarBlockFailsTheDayInsteadOfSkippingIt()
        {
            // Taken for "no index", a blocked day was never recorded and fell out of reach of the
            // daily run's lookback, so the rebuild lost it for good.
            HttpRequestException Blocked() => new("Forbidden", null, HttpStatusCode.Forbidden);

            Assert.Throws<HttpRequestException>(() => SEC13FEdgarDay.Build(new DateTime(2026, 9, 8), _root,
                _ => throw Blocked(), _ => throw Blocked()));
        }

        [Test]
        public void AnEdgarListingYieldsItsNames()
        {
            const string listing = @"{""directory"":{""item"":[{""last-modified"":""09\/08\/2026 10:02:29 PM""," +
                @"""name"":""form.20260908.idx"",""type"":""file"",""href"":""form.20260908.idx"",""size"":""782 KB""}]," +
                @"""name"":""daily-index\/2026\/QTR3\/"",""parent-dir"":""..\/""}}";

            Assert.AreEqual(new[] { "form.20260908.idx" }, SEC13FEdgarDay.ListingNames(listing).ToArray());
            Assert.Throws<InvalidDataException>(() => SEC13FEdgarDay.ListingNames("{}"));
        }

        private static SEC13FEdgarDay.IndexEntry SampleEntry()
        {
            return new SEC13FEdgarDay.IndexEntry("13F-HR", 107136, new DateTime(2026, 8, 14),
                "edgar/data/107136/0001214659-26-010148.txt");
        }

        /// <summary>A full submission file cut down to the two documents the reader uses.</summary>
        private static string SampleSubmission()
        {
            return string.Join("\n",
                "<SEC-DOCUMENT>0001214659-26-010148.txt : 20260814",
                "<DOCUMENT>",
                "<TYPE>13F-HR",
                "<TEXT>",
                "<XML>",
                "<?xml version=\"1.0\" encoding=\"UTF-8\"?>",
                "<edgarSubmission xmlns=\"http://www.sec.gov/edgar/thirteenffiler\"><headerData>" +
                "<submissionType>13F-HR</submissionType><filerInfo><periodOfReport>06-30-2026</periodOfReport>" +
                "</filerInfo></headerData><formData><summaryPage><isConfidentialOmitted>true</isConfidentialOmitted>" +
                "</summaryPage></formData></edgarSubmission>",
                "</XML>",
                "</TEXT>",
                "</DOCUMENT>",
                "<DOCUMENT>",
                "<TYPE>INFORMATION TABLE",
                "<TEXT>",
                "<XML>",
                "<ns1:informationTable xmlns:ns1=\"http://www.sec.gov/edgar/document/thirteenf/informationtable\">" +
                "<ns1:infoTable><ns1:cusip>88025U109</ns1:cusip><ns1:value>274974</ns1:value><ns1:shrsOrPrnAmt>" +
                "<ns1:sshPrnamt>7172</ns1:sshPrnamt><ns1:sshPrnamtType>SH</ns1:sshPrnamtType></ns1:shrsOrPrnAmt>" +
                "<ns1:votingAuthority><ns1:Sole>7172</ns1:Sole><ns1:Shared>0</ns1:Shared><ns1:None>0</ns1:None>" +
                "</ns1:votingAuthority></ns1:infoTable>" +
                "<ns1:infoTable><ns1:cusip>037833100</ns1:cusip><ns1:value>1000</ns1:value><ns1:shrsOrPrnAmt>" +
                "<ns1:sshPrnamt>50</ns1:sshPrnamt><ns1:sshPrnamtType>SH</ns1:sshPrnamtType></ns1:shrsOrPrnAmt>" +
                "<ns1:putCall>Put</ns1:putCall><ns1:votingAuthority><ns1:Sole>0</ns1:Sole><ns1:Shared>50</ns1:Shared>" +
                "<ns1:None>0</ns1:None></ns1:votingAuthority></ns1:infoTable></ns1:informationTable>",
                "</XML>",
                "</TEXT>",
                "</DOCUMENT>",
                "</SEC-DOCUMENT>");
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
            return ReadInfoTable(lines, false, submissions);
        }

        /// <summary>
        /// The same reader, with the four CUSIPs the unit tests use resolvable through the crosswalk
        /// when <paramref name="resolve"/> is set, so their lines are held against the closes
        /// SeedCloses wrote. Unset, no CUSIP resolves and every line falls back to its filing's rule.
        /// </summary>
        private Dictionary<SEC13FDownloader.HoldingKey, SEC13FDownloader.Holding> ReadInfoTable(
            string[] lines, bool resolve, params KeyValuePair<string, SEC13FDownloader.Submission>[] submissions)
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
            downloader.TickerCrosswalk = resolve
                ? UnitTestCrosswalk()
                : new Dictionary<string, SEC13FTickerCrosswalk.Entry>();

            return downloader.ReadInfoTable(
                archive,
                new SEC13FDownloader.Archive("2024q1_form13f.zip", "https://localhost/2024q1_form13f.zip",
                    new DateTime(2024, 1, 1), new DateTime(2024, 3, 31)),
                submissions.ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.Ordinal));
        }

        /// <summary>The CUSIPs the unit tests name, to the tickers SeedCloses lists.</summary>
        private static Dictionary<string, SEC13FTickerCrosswalk.Entry> UnitTestCrosswalk()
        {
            var observed = new DateTime(2026, 1, 1);
            return new Dictionary<string, SEC13FTickerCrosswalk.Entry>
            {
                ["037833100"] = new("AAPL", observed),
                ["594918104"] = new("MSFT", observed),
                ["67066G104"] = new("NVDA", observed),
                ["78462F103"] = new("SPY", observed)
            };
        }

        /// <summary>
        /// Writes one trading day's coarse file with these closes, and map files listing the tickers
        /// over every date used, so a crosswalk CUSIP resolves to the identifier the file is keyed by.
        /// </summary>
        private void SeedCloses(string day, params (string Ticker, decimal Close)[] closes)
        {
            SeedMapFiles("aapl", "msft", "nvda", "spy", "defi", "etsy");

            var coarse = Path.Combine(_root, "data", "equity", "usa", "fundamental", "coarse");
            Directory.CreateDirectory(coarse);
            File.WriteAllLines(Path.Combine(coarse, $"{day}.csv"), closes.Select(pair =>
                $"{ListedSince1980(pair.Ticker)},{pair.Ticker.ToUpperInvariant()}," +
                $"{pair.Close.ToString(System.Globalization.CultureInfo.InvariantCulture)},100,1000,True,1,1"));
        }

        /// <summary>The identifier SeedMapFiles gives a ticker: listed on its first row, 1980-12-12.</summary>
        private static SecurityIdentifier ListedSince1980(string ticker)
        {
            return SecurityIdentifier.GenerateEquity(new DateTime(1980, 12, 12), ticker.ToUpperInvariant(), Market.USA);
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
            SeedMapFileRows(tickers
                .Select(ticker => (ticker, new[] { $"19801212,{ticker}", $"20501231,{ticker}" }))
                .ToArray());
        }

        /// <summary>A downloader writing into this test's own temporary directories.</summary>
        private SEC13FDownloader Downloader()
        {
            return new SEC13FDownloader(Path.Combine(_root, "out"), Path.Combine(_root, "processed"), null);
        }
    }
}
