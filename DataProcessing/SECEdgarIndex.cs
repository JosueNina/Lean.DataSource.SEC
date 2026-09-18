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
using System.Text.RegularExpressions;
using System.Xml.Linq;
using Newtonsoft.Json;
using QuantConnect.DataSource;

namespace QuantConnect.DataProcessing
{
    /// <summary>
    /// EDGAR's daily form index and full submission files, which every SEC dataset read from EDGAR
    /// goes through: where a day's index lives, whether it is published, the filings it lists, and
    /// the documents and XML inside one submission.
    /// </summary>
    public static class SECEdgarIndex
    {
        /// <summary>Root of the daily indexes, one folder per year and quarter.</summary>
        public const string DailyIndexRootUrl = "https://www.sec.gov/Archives/edgar/daily-index/";

        /// <summary>Root the index's file paths are relative to.</summary>
        public const string ArchivesBaseUrl = "https://www.sec.gov/Archives/";

        private static readonly Regex DocumentBlock = new(@"<DOCUMENT>(?<body>.*?)</DOCUMENT>",
            RegexOptions.Compiled | RegexOptions.Singleline);

        private static readonly Regex DocumentType = new(@"<TYPE>(?<type>[^\r\n<]+)", RegexOptions.Compiled);

        private static readonly Regex XmlBlock = new(@"<XML>(?<xml>.*?)</XML>",
            RegexOptions.Compiled | RegexOptions.Singleline);

        /// <summary>One filing listed in a daily index, under one of the CIKs the filing names.</summary>
        public readonly record struct Entry(string FormType, int Cik, DateTime Filed, string Path)
        {
            /// <summary>The accession number, which is the name of the submission file.</summary>
            public string Accession => System.IO.Path.GetFileNameWithoutExtension(Path);
        }

        /// <summary>One document of a submission: its type and, when it carries one, its XML.</summary>
        public readonly record struct Document(string Type, string Xml);

        /// <summary>The daily form index, which lists a day's filings by form type.</summary>
        public static string IndexUrl(DateTime day)
        {
            return $"{QuarterUrl(day)}{IndexFileName(day)}";
        }

        /// <summary>
        /// Whether EDGAR lists the day's form index. The answer comes from its directory listings,
        /// never from a failed request: EDGAR answers 403 both for an index that does not exist and
        /// for a reader it has blocked, and a block taken for "no index" dropped the day for good. Each
        /// folder is looked up in its parent's listing first, from the root, which always exists: a
        /// year or quarter EDGAR has not created yet answers 403 as well.
        /// </summary>
        public static bool IsIndexPublished(DateTime day, Func<string, ISet<string>> listDirectory)
        {
            var year = day.Year.ToString(CultureInfo.InvariantCulture);
            return listDirectory(DailyIndexRootUrl).Contains(year)
                   && listDirectory($"{DailyIndexRootUrl}{year}/").Contains(QuarterName(day))
                   && listDirectory(QuarterUrl(day)).Contains(IndexFileName(day));
        }

        /// <summary>The names in one of EDGAR's index.json directory listings.</summary>
        public static HashSet<string> ListingNames(string json)
        {
            var listing = JsonConvert.DeserializeObject<SECReportIndexFile>(json)?.Directory
                ?? throw new InvalidDataException("SECEdgarIndex.ListingNames(): not an EDGAR directory listing");

            return (listing.Items ?? new List<SECReportIndexItem>()).Select(item => item.Name).ToHashSet(StringComparer.Ordinal);
        }

        /// <summary>
        /// The filings of the form types <paramref name="formPattern"/> matches, a regular expression
        /// for the whole form type such as <c>13F-HR(?:/A)?</c>. EDGAR lists a filing once for every
        /// CIK it names, so a filing can come back more than once; <see cref="DistinctFilings"/> keeps one.
        /// </summary>
        public static List<Entry> ParseIndex(string text, string formPattern)
        {
            var line = new Regex(
                $@"^(?<form>{formPattern})\s+.+?\s+(?<cik>\d+)\s+(?<date>\d{{8}})\s+(?<file>edgar/data/\S+\.txt)\s*$");

            var entries = new List<Entry>();
            foreach (var raw in text.Split('\n'))
            {
                var match = line.Match(raw.TrimEnd('\r'));
                if (!match.Success)
                {
                    continue;
                }

                entries.Add(new Entry(
                    match.Groups["form"].Value,
                    int.Parse(match.Groups["cik"].Value, CultureInfo.InvariantCulture),
                    DateTime.ParseExact(match.Groups["date"].Value, DateFormat.EightCharacter, CultureInfo.InvariantCulture),
                    match.Groups["file"].Value));
            }

            return entries;
        }

        /// <summary>One entry per accession, the first the index lists.</summary>
        public static List<Entry> DistinctFilings(IEnumerable<Entry> entries)
        {
            var seen = new HashSet<string>(StringComparer.Ordinal);
            return entries.Where(entry => seen.Add(entry.Accession)).ToList();
        }

        /// <summary>The documents of a full submission file, in order, with the XML of those that carry it.</summary>
        public static IEnumerable<Document> Documents(string submission)
        {
            foreach (Match document in DocumentBlock.Matches(submission))
            {
                var body = document.Groups["body"].Value;
                var xml = XmlBlock.Match(body);
                yield return new Document(
                    DocumentType.Match(body).Groups["type"].Value.Trim(),
                    xml.Success ? xml.Groups["xml"].Value : null);
            }
        }

        /// <summary>Parses a document's XML, naming the submission when it is malformed.</summary>
        public static XElement ParseXml(string xml, string source)
        {
            try
            {
                return XDocument.Parse(xml.Trim()).Root;
            }
            catch (Exception err)
            {
                throw new InvalidDataException($"SECEdgarIndex.ParseXml(): {source} carries malformed XML: {err.Message}", err);
            }
        }

        /// <summary>Elements by local name, whatever namespace and case the filer's software used.</summary>
        public static IEnumerable<XElement> Elements(XElement root, string name)
        {
            return root.DescendantsAndSelf().Where(element => element.Name.LocalName.Equals(name, StringComparison.OrdinalIgnoreCase));
        }

        /// <summary>The trimmed text of the first element with a local name, or null.</summary>
        public static string Value(XElement root, string name)
        {
            return Elements(root, name).FirstOrDefault()?.Value.Trim();
        }

        private static string QuarterName(DateTime day) => $"QTR{(day.Month - 1) / 3 + 1}";

        private static string QuarterUrl(DateTime day) =>
            $"{DailyIndexRootUrl}{day.Year.ToString(CultureInfo.InvariantCulture)}/{QuarterName(day)}/";

        private static string IndexFileName(DateTime day) =>
            $"form.{day.ToString(DateFormat.EightCharacter, CultureInfo.InvariantCulture)}.idx";
    }
}
