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
using System.IO.Compression;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using QuantConnect.Logging;

namespace QuantConnect.DataProcessing
{
    /// <summary>
    /// One day of Form 13F filings read straight from EDGAR and written as an archive with the
    /// tables the SEC data sets carry, so the processor reads it like any other window.
    ///
    /// The data sets come out in three month batches a few days after each window closes. A daily
    /// job waiting for them publishes a filing up to three months after it was public, while a
    /// backtest stamped at the filing date sees it the day it was filed. EDGAR lists each day's
    /// filings in its daily index that night, and their information tables carry the lines the data
    /// sets do: 48 of 48 filings compared line by line, and the same accession numbers on six sample
    /// days, so reading EDGAR is what lets the live job publish on the date the history uses.
    /// </summary>
    internal static class SEC13FEdgarDay
    {
        private const string FormPattern = "13F-HR(?:/A)?";

        private const string PrimaryDocumentTypePrefix = "13F-HR";
        private const string InformationTableDocumentType = "INFORMATION TABLE";

        /// <summary>What the archive's tables carry for one filing.</summary>
        internal sealed class Filing
        {
            public string Accession { get; init; }
            public string SubmissionType { get; init; }
            public int Cik { get; init; }
            public DateTime Filed { get; init; }
            public DateTime Period { get; init; }
            public bool ConfidentialOmitted { get; init; }

            /// <summary>INFOTABLE rows: CUSIP, VALUE, SSHPRNAMT, SSHPRNAMTTYPE, PUTCALL, VOTING_AUTH_SOLE, VOTING_AUTH_SHARED.</summary>
            public List<string[]> Lines { get; } = new();
        }

        /// <summary>The name a day's archive is cached and logged under.</summary>
        public static string ArchiveName(DateTime day) => $"edgar-{day.ToString(DateFormat.EightCharacter, CultureInfo.InvariantCulture)}_form13f.zip";

        /// <summary>
        /// Writes the day's archive into <paramref name="directory"/> and returns its path, or null
        /// when EDGAR has not published an index for the day, which is every weekend and holiday and a
        /// day whose index is late. <paramref name="listDirectory"/> returns the names in one of EDGAR's
        /// directory listings and <paramref name="getText"/> a file; both throw on any failure.
        /// </summary>
        public static string Build(DateTime day, string directory, Func<string, ISet<string>> listDirectory,
            Func<string, string> getText)
        {
            var path = System.IO.Path.Combine(directory, ArchiveName(day));
            if (File.Exists(path))
            {
                return path;
            }

            if (!SECEdgarIndex.IsIndexPublished(day, listDirectory))
            {
                return null;
            }

            var entries = ParseIndex(getText(SECEdgarIndex.IndexUrl(day)));
            var filings = new List<Filing>(entries.Count);
            foreach (var entry in entries)
            {
                filings.Add(ParseFiling(entry, getText(SECEdgarIndex.ArchivesBaseUrl + entry.Path)));
            }

            Directory.CreateDirectory(directory);
            SEC13FFiles.WriteThenMove(path, stream => WriteArchive(stream, filings));

            Log.Trace($"SEC13FEdgarDay.Build(): {day:yyyy-MM-dd}: {filings.Count} holdings filings, " +
                      $"{filings.Sum(filing => filing.Lines.Count)} information table lines");
            return path;
        }

        /// <summary>
        /// The holdings reports and their amendments listed in a daily index. Notices are left out:
        /// they carry no information table, and the processor skips them in the data sets too.
        /// </summary>
        internal static List<SECEdgarIndex.Entry> ParseIndex(string text)
        {
            return SECEdgarIndex.ParseIndex(text, FormPattern);
        }

        /// <summary>
        /// Reads one full submission file: the period and confidential treatment flag from the
        /// primary document, and every line of its information tables. The form type, filer and
        /// filing date come from the index, as they do in the data sets.
        /// </summary>
        internal static Filing ParseFiling(SECEdgarIndex.Entry entry, string text)
        {
            XElement primary = null;
            var tables = new List<XElement>();

            foreach (var document in SECEdgarIndex.Documents(text))
            {
                if (document.Xml == null)
                {
                    continue;
                }

                if (document.Type.StartsWith(PrimaryDocumentTypePrefix, StringComparison.OrdinalIgnoreCase))
                {
                    primary = SECEdgarIndex.ParseXml(document.Xml, entry.Path);
                }
                else if (document.Type.Equals(InformationTableDocumentType, StringComparison.OrdinalIgnoreCase))
                {
                    tables.Add(SECEdgarIndex.ParseXml(document.Xml, entry.Path));
                }
            }

            if (primary == null)
            {
                throw new InvalidDataException($"SEC13FEdgarDay.ParseFiling(): {entry.Path} carries no primary document");
            }

            var period = Value(primary, "periodOfReport") ?? Value(primary, "reportCalendarOrQuarter")
                ?? throw new InvalidDataException($"SEC13FEdgarDay.ParseFiling(): {entry.Path} carries no period of report");

            var filing = new Filing
            {
                Accession = entry.Accession,
                SubmissionType = entry.FormType,
                Cik = entry.Cik,
                Filed = entry.Filed,
                Period = DateTime.ParseExact(period, "MM-dd-yyyy", CultureInfo.InvariantCulture),
                ConfidentialOmitted = IsTrue(Value(primary, "isConfidentialOmitted"))
            };

            foreach (var line in tables.SelectMany(table => SECEdgarIndex.Elements(table, "infoTable")))
            {
                filing.Lines.Add(new[]
                {
                    Value(line, "cusip"),
                    Value(line, "value"),
                    Value(line, "sshPrnamt"),
                    Value(line, "sshPrnamtType"),
                    Value(line, "putCall"),
                    Value(line, "Sole"),
                    Value(line, "Shared")
                });
            }

            return filing;
        }

        /// <summary>Writes the three tables the processor reads, in the data sets' layout.</summary>
        internal static void WriteArchive(Stream stream, IEnumerable<Filing> filings)
        {
            var submissions = new StringBuilder("ACCESSION_NUMBER\tFILING_DATE\tSUBMISSIONTYPE\tCIK\tPERIODOFREPORT\n");
            var summaries = new StringBuilder("ACCESSION_NUMBER\tISCONFIDENTIALOMITTED\n");
            var lines = new StringBuilder("ACCESSION_NUMBER\tCUSIP\tVALUE\tSSHPRNAMT\tSSHPRNAMTTYPE\tPUTCALL\tVOTING_AUTH_SOLE\tVOTING_AUTH_SHARED\n");

            foreach (var filing in filings)
            {
                submissions.Append(string.Join('\t', filing.Accession,
                    filing.Filed.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture), filing.SubmissionType,
                    filing.Cik.ToString(CultureInfo.InvariantCulture), filing.Period.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture))).Append('\n');
                summaries.Append(filing.Accession).Append('\t').Append(filing.ConfidentialOmitted ? "Y" : "N").Append('\n');

                foreach (var line in filing.Lines)
                {
                    lines.Append(filing.Accession);
                    foreach (var field in line)
                    {
                        lines.Append('\t').Append(Clean(field));
                    }

                    lines.Append('\n');
                }
            }

            using var zip = new ZipArchive(stream, ZipArchiveMode.Create, leaveOpen: true);
            foreach (var (name, content) in new[] { ("SUBMISSION.tsv", submissions), ("SUMMARYPAGE.tsv", summaries), ("INFOTABLE.tsv", lines) })
            {
                using var writer = new StreamWriter(zip.CreateEntry(name, CompressionLevel.Optimal).Open());
                writer.Write(content.ToString());
            }
        }

        private static string Value(XElement root, string name) => SECEdgarIndex.Value(root, name);

        private static bool IsTrue(string value)
        {
            return value != null && (value.Equals("true", StringComparison.OrdinalIgnoreCase) || value.Equals("Y", StringComparison.OrdinalIgnoreCase));
        }

        /// <summary>A field as a table cell: tabs and line breaks would split the row.</summary>
        private static string Clean(string value)
        {
            return value == null ? string.Empty : Regex.Replace(value, @"\s+", " ").Trim();
        }
    }
}
