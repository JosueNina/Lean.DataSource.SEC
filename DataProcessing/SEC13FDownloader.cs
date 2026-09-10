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
using System.Net;
using System.Net.Http;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using QuantConnect.Configuration;
using QuantConnect.Data.Auxiliary;
using QuantConnect.DataSource;
using QuantConnect.Interfaces;
using QuantConnect.Lean.Engine.DataFeeds;
using QuantConnect.Logging;
using QuantConnect.Securities;
using QuantConnect.Util;

[assembly: InternalsVisibleTo("Tests")]

namespace QuantConnect.DataProcessing
{
    /// <summary>
    /// Converts the SEC Form 13F structured data sets, one zip per filing window, into LEAN's
    /// per-security files and one universe file per release date. Without
    /// QC_DATAFLEET_DEPLOYMENT_DATE it rebuilds the whole history; with it, it reads the archive
    /// covering that date and folds it into the published history.
    /// </summary>
    public class SEC13FDownloader : IDisposable
    {
        /// <summary>
        /// The "dataset-name" config value that selects this downloader. Spelled the same as the
        /// data class's ReportFolder, which stays the single source of truth for the output path.
        /// </summary>
        public const string DatasetName = "13f";

        /// <summary>Vendor name (matches the alternative/&lt;vendor&gt; data path).</summary>
        public static string VendorName => "sec";

        private const string DataSetsPageUrl = "https://www.sec.gov/data-research/sec-markets-data/form-13f-data-sets";
        private const string SecBaseUrl = "https://www.sec.gov";


        private const int MaxRetries = 5;

        // A 13F-NT is a notice that the manager's holdings are reported on someone else's filing. It
        // carries no INFOTABLE at all, so it is not a zero position and must not become one.
        private const string NoticeSubmissionTypePrefix = "13F-NT";

        // A 13F-HR/A amends the manager's report for the quarter, and most restate all of it, so an
        // amendment's lines count only for a security the manager had not reported yet. A
        // restatement's corrections to a position already counted are not applied; see AdmitAmendment.
        private const string AmendmentSubmissionTypeSuffix = "/A";

        // EDGAR stops accepting same-day filings at 17:30 ET, so a filing is public by then on its
        // FILING_DATE and the point can never be read before it existed.
        private static readonly TimeSpan ReleaseTimeOfDay = new(17, 30, 0);

        private const string ReleaseFormat = "yyyyMMdd HH:mm";
        private const string PeriodFormat = "yyyyMMdd";

        // The published row: release, period, the eight measures and the confidential flag.
        private const int FirstValueColumn = 2;
        private const int ValueColumnCount = 8;
        private const int PublishedColumnCount = FirstValueColumn + ValueColumnCount + 1;

        // N-PORT quarters folded into the crosswalk: a year reaches every security still trading.
        private const int NPortQuartersToFold = 4;

        /// <summary>Days after a quarter end that managers have to file their 13F for it.</summary>
        private const int FilingDeadlineDays = 45;

        /// <summary>Reports of one security and quarter needed before their median price is a reference.</summary>
        private const int MinimumReportsForMedianPrice = 3;

        /// <summary>Reports needed before the median can correct a single line on its own.</summary>
        private const int MinimumReportsForLineCorrection = 10;

        // The SEC rule: VALUE in thousands for filings before this date, whole dollars from it.
        // DetectValueUnits checks each filing against it.
        private static readonly DateTime ValueInWholeDollarsFrom = new(2023, 1, 1);

        private static readonly string[] SecDateFormats = { "dd-MMM-yyyy", "d-MMM-yyyy", "yyyy-MM-dd", "MM/dd/yyyy" };

        private static readonly Regex ArchiveLinkRegex = new(
            @"href=""(?<href>[^""]*?/(?<name>[^""/]+_form13f\.zip))""",
            RegexOptions.Compiled | RegexOptions.IgnoreCase);

        private static readonly Regex QuarterArchiveRegex = new(
            @"^(?<year>\d{4})q(?<quarter>[1-4])_form13f\.zip$",
            RegexOptions.Compiled | RegexOptions.IgnoreCase);

        private static readonly Regex WindowArchiveRegex = new(
            @"^(?<start>\d{2}[a-z]{3}\d{4})-(?<end>\d{2}[a-z]{3}\d{4})_form13f\.zip$",
            RegexOptions.Compiled | RegexOptions.IgnoreCase);

        private readonly string _destinationDirectory;
        private readonly string _processedDataDirectory;
        private readonly string _archiveCacheDirectory;
        private readonly DateTime? _deploymentDate;

        private readonly HttpClient _client;
        private bool _userAgentSet;

        // The SEC rate limits automated readers to ten requests a second.
        private readonly RateGate _rateGate = new(10, TimeSpan.FromSeconds(1));

        private readonly IMapFileProvider _mapFileProvider;
        private readonly SecurityDefinitionSymbolResolver _symbolResolver;

        /// <summary>
        /// CUSIP and filing date to security, misses included, since each miss scans the whole
        /// security database. The date is part of the key because resolution is point in time; the
        /// cache is cleared per archive so it stays the size of one window.
        /// </summary>
        private readonly Dictionary<(string Cusip, DateTime Date), SecurityIdentifier> _resolvedCusips = new();

        /// <summary>CUSIPs already tallied in the resolution summary, so its totals stay per CUSIP.</summary>
        private readonly HashSet<string> _countedCusips = new(StringComparer.Ordinal);

        /// <summary>Map file per security, so the point-in-time ticker costs one lookup per date.</summary>
        private readonly Dictionary<SecurityIdentifier, MapFile> _mapFiles = new();

        /// <summary>Accessions already folded in, so a submission can never be counted twice.</summary>
        private readonly HashSet<string> _processedAccessions = new(StringComparer.Ordinal);

        /// <summary>
        /// Increments waiting to be staged, flushed per archive. Keyed by security and then by
        /// (period, release), because several CUSIPs can reach one security and their contributions
        /// are summed here. Each keeps the ticker of its day, which is the file it lands in.
        /// </summary>
        private readonly Dictionary<string, Dictionary<(DateTime PeriodEnd, DateTime Time), (string Ticker, HoldingsRow Row)>>
            _pendingSecurityRows = new(StringComparer.Ordinal);

        /// <summary>
        /// Where increments wait, one file per security, until the finalize pass. Per security
        /// because the running total belongs to the security and not to a ticker, and outside the
        /// output folder because everything there is published.
        /// </summary>
        private readonly string _stagingDirectory =
            Path.Combine(Path.GetTempPath(), "sec-13f-staging", Guid.NewGuid().ToString("N"));

        /// <summary>Every security with increments staged, which is every security this run touched.</summary>
        private readonly HashSet<string> _stagedSecurities = new(StringComparer.Ordinal);

        /// <summary>
        /// Amendments admitted in this archive, as (filer key, filing date), so one amendment naming
        /// several CUSIPs of a security is admitted for all of them.
        /// </summary>
        private readonly HashSet<(long FilerKey, int Stamp)> _admittedAmendments = new();

        /// <summary>Ticker and date to the security the map files say owns that ticker then, per archive.</summary>
        private readonly Dictionary<(string Ticker, DateTime Date), string> _tickerOwners = new();

        private long _conflictingTickers;
        private decimal _conflictingValue;
        private readonly List<string> _conflictSample = new();
        private long _unchangedGroups;

        /// <summary>
        /// CUSIP to the ticker the SEC's N-PORT filings report for it. Built on first use rather than
        /// at construction: the archives may all resolve through the security database, and building
        /// it downloads a few hundred megabytes per quarter folded in. Settable for tests.
        /// </summary>
        internal Dictionary<string, SEC13FTickerCrosswalk.Entry> TickerCrosswalk
        {
            // Read from the published copy and written with the output, since the raw folder is not
            // restored between runs and the next run would otherwise rebuild it from 1.8 GB of N-PORT.
            get => _tickerCrosswalk ??= SEC13FTickerCrosswalk.Load(_processedDataDirectory, _destinationDirectory,
                NPortQuartersToFold, UrlExists, (url, name) => DownloadFile(url, name, _archiveCacheDirectory));
            set => _tickerCrosswalk = value;
        }

        private Dictionary<string, SEC13FTickerCrosswalk.Entry> _tickerCrosswalk;

        /// <summary>CUSIP to the security its crosswalk ticker named when the funds reported it.</summary>
        private readonly Dictionary<string, SecurityIdentifier> _crosswalkSecurities = new(StringComparer.Ordinal);

        private long _resolvedByCusip;
        private long _resolvedByIsin;
        private long _resolvedByTicker;
        private long _unresolvedGroups;
        private long _malformedCusips;
        private decimal _resolvedValue;
        private decimal _unresolvedValue;
        private readonly HashSet<string> _unresolvedCusips = new(StringComparer.Ordinal);

        /// <summary>
        /// Creates a new instance writing to <paramref name="destinationDirectory"/>, merging with any
        /// previously processed data found in <paramref name="processedDataDirectory"/>. A null
        /// <paramref name="deploymentDate"/> walks every published archive; a date reads only the
        /// archive whose window covers it. Downloads are kept under
        /// <paramref name="rawDataDirectory"/> so the next run finds them.
        /// </summary>
        public SEC13FDownloader(string destinationDirectory, string processedDataDirectory, DateTime? deploymentDate,
            string rawDataDirectory = null)
        {
            // The folder comes from the data type rather than a literal, so the writer and the
            // reader cannot drift apart.
            _destinationDirectory = Path.Combine(destinationDirectory, SEC13FHoldings.ReportFolder);
            _processedDataDirectory = Path.Combine(processedDataDirectory, SEC13FHoldings.ReportFolder);
            _deploymentDate = deploymentDate;

            // Downloads land in the raw folder, which the job archives after every run but does not
            // restore before the next, so it only saves work within a run.
            _archiveCacheDirectory = rawDataDirectory == null
                ? Path.Combine(Path.GetTempPath(), "sec-13f-archives")
                : Path.Combine(rawDataDirectory, SEC13FHoldings.ReportFolder, "archives");
            Directory.CreateDirectory(_archiveCacheDirectory);

            _client = new HttpClient { Timeout = TimeSpan.FromMinutes(30) };

            // Zip, not disk: the security master ships map_files_<yyyyMMdd>.zip, and the disk provider
            // only sees loose csv files, so it would resolve almost nothing without failing.
            _mapFileProvider = new LocalZipMapFileProvider();
            _mapFileProvider.Initialize(new DefaultDataProvider());

            // The resolver takes its map file provider from the Composer, so both are registered first.
            var dataProvider = new DefaultDataProvider();
            Composer.Instance.AddPart<IDataProvider>(dataProvider);
            Composer.Instance.AddPart(_mapFileProvider);
            _symbolResolver = SecurityDefinitionSymbolResolver.GetInstance(dataProvider);

            // Without security-database.csv, which is not distributable, the first two resolution
            // steps answer nothing, so say so at startup rather than let the coverage drop silently.
            var securityDatabasePath = Path.Combine(
                Globals.GetDataFolderPath("symbol-properties"), "security-database.csv");
            if (!File.Exists(securityDatabasePath))
            {
                Log.Error($"SEC13FDownloader(): {securityDatabasePath} is missing. It is not distributable, " +
                          "and without it no CUSIP or ISIN resolves and the run produces no data.");
            }
        }

        /// <summary>
        /// Runs the download/convert. Failures throw rather than return false, so the caller logs
        /// the actual reason: a guard stopping the run and a network outage are not the same thing.
        /// </summary>
        public void Run()
        {
            RequireEmptyDestination();
            RequirePublishedHistoryForIncrementalRun();
            ReadFilerState();

            var archives = GetArchives();
            Log.Trace($"SEC13FDownloader.Run(): {archives.Count} archives published, " +
                      $"{archives[0].Start:yyyy-MM-dd}..{archives[archives.Count - 1].End:yyyy-MM-dd}");

            var selected = SelectArchives(archives);
            Log.Trace($"SEC13FDownloader.Run(): processing {selected.Count} archive(s): " +
                      $"{string.Join(", ", selected.Select(x => x.Name))}");

            PruneFilerState(selected);

            foreach (var archive in selected)
            {
                ProcessArchive(archive);
                FlushPendingRows();
            }

            FinalizeSecurityFiles();
            BuildUniverseFiles();
            WriteFilerState();
            LogResolutionSummary();
        }

        /// <summary>
        /// Stops a run into a destination that already holds files. The job hands it over empty, and
        /// whatever an earlier run left there would be read into the universe and published again.
        /// </summary>
        internal void RequireEmptyDestination()
        {
            if (Directory.Exists(_destinationDirectory) && Directory.EnumerateFileSystemEntries(_destinationDirectory).Any())
            {
                throw new InvalidOperationException(
                    $"SEC13FDownloader.Run(): {_destinationDirectory} is not empty. The destination has to start empty, " +
                    "or the files an earlier run left there would be published again.");
            }
        }

        /// <summary>
        /// Stops an incremental run that cannot see the published history: merging into nothing would
        /// republish thirteen years as one three month window and still report success.
        /// </summary>
        internal void RequirePublishedHistoryForIncrementalRun()
        {
            if (_deploymentDate == null)
            {
                return;
            }

            var published = Directory.Exists(_processedDataDirectory)
                ? Directory.EnumerateFiles(_processedDataDirectory, "*.csv").Count()
                : 0;

            if (published == 0)
            {
                throw new InvalidOperationException(
                    $"SEC13FDownloader.Run(): the incremental run for {_deploymentDate:yyyy-MM-dd} found no published " +
                    $"history under {_processedDataDirectory}. Publishing this window on its own would truncate the " +
                    "dataset to it, so the run stops instead of reporting success.");
            }

            Log.Trace($"SEC13FDownloader.Run(): {published} published securities to merge into, " +
                      $"read from {_processedDataDirectory}");

            RequireFilerStateForIncrementalRun();
        }

        /// <summary>
        /// Stops an incremental run without the filer state: every manager in tonight's archive would
        /// look new and Holders would be inflated, with nothing else in the files to show it.
        /// </summary>
        internal void RequireFilerStateForIncrementalRun()
        {
            var path = Path.Combine(_processedDataDirectory, FilerStateFileName);
            if (File.Exists(path))
            {
                return;
            }

            throw new InvalidOperationException(
                $"SEC13FDownloader.Run(): the incremental run for {_deploymentDate:yyyy-MM-dd} found no filer state " +
                $"at {path}. Holders is a distinct count of managers and cannot be rebuilt from the published totals, " +
                "so every filer read tonight would be counted again on top of the existing history. The run stops " +
                "instead of publishing an inflated count. Republish the dataset with a full history run to restore it.");
        }

        /// <summary>
        /// One published archive: the zip name, its absolute URL, and the filing-receipt window it
        /// covers.
        /// </summary>
        internal sealed record Archive(string Name, string Url, DateTime Start, DateTime End);

        /// <summary>
        /// Scrapes the data sets page for every published archive, oldest first, so a new window is
        /// picked up without a code change.
        /// </summary>
        private List<Archive> GetArchives()
        {
            var html = GetWithRetry(DataSetsPageUrl);
            var archives = new Dictionary<string, Archive>(StringComparer.OrdinalIgnoreCase);

            foreach (Match match in ArchiveLinkRegex.Matches(html))
            {
                var name = match.Groups["name"].Value;
                if (archives.ContainsKey(name))
                {
                    continue;
                }

                var href = WebUtility.HtmlDecode(match.Groups["href"].Value);
                var url = href.StartsWith("http", StringComparison.OrdinalIgnoreCase)
                    ? href
                    : SecBaseUrl + (href.StartsWith("/", StringComparison.Ordinal) ? href : "/" + href);

                var (start, end) = ParseArchiveWindow(name);
                archives[name] = new Archive(name, url, start, end);
            }

            if (archives.Count == 0)
            {
                throw new InvalidOperationException(
                    $"SEC13FDownloader.GetArchives(): no _form13f.zip links found on {DataSetsPageUrl}. " +
                    "The page layout changed, or the request was blocked.");
            }

            return archives.Values.OrderBy(x => x.End).ThenBy(x => x.Name, StringComparer.Ordinal).ToList();
        }

        /// <summary>
        /// Reads the filing-receipt window out of an archive name. Two shapes are published:
        /// "2013q2_form13f.zip" for the quarterly archives and "01mar2026-31may2026_form13f.zip" for
        /// the rolling windows that replaced them.
        /// </summary>
        private static (DateTime start, DateTime end) ParseArchiveWindow(string name)
        {
            var quarter = QuarterArchiveRegex.Match(name);
            if (quarter.Success)
            {
                var year = int.Parse(quarter.Groups["year"].Value, CultureInfo.InvariantCulture);
                var number = int.Parse(quarter.Groups["quarter"].Value, CultureInfo.InvariantCulture);
                var start = new DateTime(year, number * 3 - 2, 1);
                return (start, start.AddMonths(3).AddDays(-1));
            }

            var window = WindowArchiveRegex.Match(name);
            if (window.Success)
            {
                return (ParseWindowDate(window.Groups["start"].Value), ParseWindowDate(window.Groups["end"].Value));
            }

            throw new FormatException(
                $"SEC13FDownloader.ParseArchiveWindow(): '{name}' matches neither the quarterly nor the " +
                "rolling-window naming. A third naming scheme appeared and the selection would be wrong.");
        }

        /// <summary>Parses a "01mar2026" style date out of a rolling-window archive name.</summary>
        private static DateTime ParseWindowDate(string value)
        {
            return DateTime.ParseExact(value, "ddMMMyyyy", CultureInfo.InvariantCulture);
        }

        /// <summary>
        /// Picks the archives a run reads: all of them for a full pass. An incremental pass reads
        /// every archive whose window ends on or after the last published release, plus any covering
        /// the deployment date, so archives the SEC published while the job was down are not skipped.
        /// The SEC publishes a window after it closes, so the covering one is usually not out yet.
        /// </summary>
        internal List<Archive> SelectArchives(List<Archive> archives)
        {
            if (_deploymentDate == null)
            {
                Log.Trace("SEC13FDownloader.SelectArchives(): full history");
                return archives;
            }

            var date = _deploymentDate.Value.Date;
            var lastPublished = LastPublishedRelease();
            var selected = archives
                .Where(x => x.End >= lastPublished || (x.Start <= date && date <= x.End))
                .ToList();

            if (selected.Count == 0)
            {
                selected.Add(archives[archives.Count - 1]);
            }

            Log.Trace($"SEC13FDownloader.SelectArchives(): incremental for {date:yyyy-MM-dd}, last published release " +
                      $"{lastPublished:yyyy-MM-dd}");
            return selected;
        }

        /// <summary>The newest release the published universe carries, or DateTime.MaxValue when there is none.</summary>
        private DateTime LastPublishedRelease()
        {
            var universe = Path.Combine(_processedDataDirectory, "universe");
            if (!Directory.Exists(universe))
            {
                return DateTime.MaxValue;
            }

            var days = Directory.EnumerateFiles(universe, "*.csv")
                .Select(file => DateTime.TryParseExact(Path.GetFileNameWithoutExtension(file), DateFormat.EightCharacter,
                    CultureInfo.InvariantCulture, DateTimeStyles.None, out var day) ? day : (DateTime?)null)
                .Where(day => day.HasValue)
                .ToList();

            return days.Count == 0 ? DateTime.MaxValue : days.Max().Value;
        }

        /// <summary>
        /// Reads one archive end to end: the submissions that carry holdings, the confidential
        /// treatment flags, and then the positions themselves, streamed rather than loaded.
        /// </summary>
        private void ProcessArchive(Archive archive)
        {
            // One window's worth of resolutions is all that is worth holding: the key carries the
            // filing date, so entries from a window already read can never be hit again.
            _resolvedCusips.Clear();
            _admittedAmendments.Clear();
            _tickerOwners.Clear();

            var path = DownloadArchive(archive);

            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read);
            using var zip = new ZipArchive(stream, ZipArchiveMode.Read);

            var submissions = ReadSubmissions(zip, archive);
            if (submissions.Count == 0)
            {
                // The windows do not overlap, so an archive with no holdings means the layout moved,
                // and carrying on would publish a history with this window missing.
                throw new InvalidDataException(
                    $"SEC13FDownloader.ProcessArchive(): {archive.Name} yielded no holdings submissions. " +
                    "An archive that contributes nothing means the upstream layout moved.");
            }

            ApplyConfidentialFlags(zip, archive, submissions);

            var holdings = ReadInfoTable(zip, archive, submissions);
            Log.Trace($"SEC13FDownloader.ProcessArchive(): {archive.Name}: {submissions.Count} submissions, " +
                      $"{holdings.Count} (security, period, release) groups");

            EmitHoldings(holdings);
        }

        private string DownloadArchive(Archive archive)
        {
            return DownloadFile(archive.Url, archive.Name, _archiveCacheDirectory);
        }

        /// <summary>
        /// Downloads a file into a directory, reusing a copy already there. It lands as ".part" first,
        /// so an interrupted run cannot leave a truncated file for the next one.
        /// </summary>
        private string DownloadFile(string url, string name, string directory)
        {
            Directory.CreateDirectory(directory);
            var path = Path.Combine(directory, name);
            if (File.Exists(path))
            {
                Log.Trace($"SEC13FDownloader.DownloadFile(): {name} already on disk");
                return path;
            }

            var temporaryPath = path + ".part";
            WithRetry(url, () =>
            {
                using var response = _client
                    .GetAsync(url, HttpCompletionOption.ResponseHeadersRead)
                    .GetAwaiter().GetResult();
                response.EnsureSuccessStatusCode();

                using var source = response.Content.ReadAsStreamAsync().GetAwaiter().GetResult();
                using var destination = new FileStream(temporaryPath, FileMode.Create, FileAccess.Write);
                source.CopyTo(destination);
                return true;
            });

            File.Move(temporaryPath, path, overwrite: true);
            Log.Trace($"SEC13FDownloader.DownloadFile(): {name}, {new FileInfo(path).Length} bytes");
            return path;
        }

        /// <summary>
        /// Whether the SEC has published a file: false only on 404. Anything else fails once the
        /// retries run out, since a 403 taken for "not published" would change the crosswalk quietly.
        /// </summary>
        private bool UrlExists(string url)
        {
            return WithRetry(url, () =>
            {
                using var request = new HttpRequestMessage(HttpMethod.Head, url);
                using var response = _client.Send(request);
                if (response.StatusCode == HttpStatusCode.NotFound)
                {
                    return false;
                }

                response.EnsureSuccessStatusCode();
                return true;
            });
        }

        /// <summary>One filing that carries holdings, keyed in the caller by accession number.</summary>
        internal sealed class Submission
        {
            public int Cik { get; init; }
            public DateTime FilingDate { get; init; }
            public DateTime Period { get; init; }
            public bool ConfidentialOmitted { get; set; }

            /// <summary>True for a 13F-HR/A, which restates a report this filer already made.</summary>
            public bool IsAmendment { get; init; }
        }

        /// <summary>
        /// Reads SUBMISSION.tsv, keeping only the filings that carry positions and that no earlier
        /// archive already contributed.
        /// </summary>
        private Dictionary<string, Submission> ReadSubmissions(ZipArchive zip, Archive archive)
        {
            var submissions = new Dictionary<string, Submission>(StringComparer.Ordinal);
            var notices = 0;

            foreach (var (columns, fields) in ReadTable(zip, archive, "SUBMISSION.tsv",
                         "ACCESSION_NUMBER", "FILING_DATE", "SUBMISSIONTYPE", "CIK", "PERIODOFREPORT"))
            {
                var submissionType = fields[columns["SUBMISSIONTYPE"]];
                if (submissionType.StartsWith(NoticeSubmissionTypePrefix, StringComparison.OrdinalIgnoreCase))
                {
                    notices++;
                    continue;
                }

                var accession = fields[columns["ACCESSION_NUMBER"]];
                if (!_processedAccessions.Add(accession))
                {
                    continue;
                }

                submissions[accession] = new Submission
                {
                    Cik = int.Parse(fields[columns["CIK"]], NumberStyles.Integer, CultureInfo.InvariantCulture),
                    FilingDate = ParseSecDate(fields[columns["FILING_DATE"]], archive, "FILING_DATE"),
                    Period = ParseSecDate(fields[columns["PERIODOFREPORT"]], archive, "PERIODOFREPORT"),
                    IsAmendment = submissionType.EndsWith(AmendmentSubmissionTypeSuffix, StringComparison.OrdinalIgnoreCase)
                };
            }

            Log.Trace($"SEC13FDownloader.ReadSubmissions(): {archive.Name}: {submissions.Count} holdings filings, " +
                      $"{notices} notices skipped");
            return submissions;
        }

        /// <summary>
        /// Marks the filings that withheld positions under confidential treatment (SUMMARYPAGE
        /// ISCONFIDENTIALOMITTED). COVERPAGE CONFDENIEDEXPIRED is not read: it means the opposite,
        /// positions withheld before and disclosed in this filing.
        /// </summary>
        private void ApplyConfidentialFlags(ZipArchive zip, Archive archive, Dictionary<string, Submission> submissions)
        {
            var flagged = 0;

            foreach (var (columns, fields) in ReadTable(
                         zip, archive, "SUMMARYPAGE.tsv", "ACCESSION_NUMBER", "ISCONFIDENTIALOMITTED"))
            {
                if (!IsYes(fields[columns["ISCONFIDENTIALOMITTED"]]) ||
                    !submissions.TryGetValue(fields[columns["ACCESSION_NUMBER"]], out var submission) ||
                    submission.ConfidentialOmitted)
                {
                    continue;
                }

                submission.ConfidentialOmitted = true;
                flagged++;
            }

            Log.Trace($"SEC13FDownloader.ApplyConfidentialFlags(): {archive.Name}: {flagged} filings flagged");
        }

        /// <summary>The summed measures of a set of reported lines.</summary>
        internal class Measures
        {
            public decimal Shares { get; set; }
            public decimal HoldingValue { get; set; }
            public decimal CallShares { get; set; }
            public decimal PutShares { get; set; }
            public decimal PrincipalValue { get; set; }
            public decimal VotingSole { get; set; }
            public decimal VotingShared { get; set; }
            public bool ConfidentialOmitted { get; set; }

            /// <summary>Adds another set of lines into this one.</summary>
            public void Add(Measures other)
            {
                Shares += other.Shares;
                HoldingValue += other.HoldingValue;
                CallShares += other.CallShares;
                PutShares += other.PutShares;
                PrincipalValue += other.PrincipalValue;
                VotingSole += other.VotingSole;
                VotingShared += other.VotingShared;
                ConfidentialOmitted |= other.ConfidentialOmitted;
            }

            /// <summary>The published value columns, Holders first.</summary>
            public decimal[] ToValues(int holders)
            {
                return new[] { holders, Shares, HoldingValue, CallShares, PutShares, PrincipalValue, VotingSole, VotingShared };
            }
        }

        /// <summary>
        /// The reported lines sharing one (CUSIP, period, release). The measures hold the original
        /// filings; amending filers' lines wait in Amendments for AdmitAmendment.
        /// </summary>
        internal sealed class Holding : Measures
        {
            /// <summary>Distinct filer CIKs of the original filings, which is what Holders counts.</summary>
            public HashSet<int> Ciks { get; } = new();

            /// <summary>Each amending filer's lines, by CIK.</summary>
            public Dictionary<int, Measures> Amendments { get; } = new();

            /// <summary>Every reported value, originals and amendments, for the coverage summary.</summary>
            public decimal ReportedValue => HoldingValue + Amendments.Values.Sum(amendment => amendment.HoldingValue);
        }

        /// <summary>
        /// Every (security, quarter, filer) already counted, so a filer reaches Holders once. Packed
        /// into a long (security index in bits 42..61, period index 32..41, CIK 0..31) because the
        /// history holds 58 million of them. The value is the filing date first counted on, as
        /// yyyyMMdd, which PruneFilerState uses to make re-reading an archive idempotent.
        /// </summary>
        private readonly Dictionary<long, int> _countedFilers = new();

        private readonly Dictionary<SecurityIdentifier, int> _securityIndexes = new();
        private readonly List<SecurityIdentifier> _securitiesByIndex = new();
        private readonly Dictionary<DateTime, int> _periodIndexes = new();
        private readonly List<DateTime> _periodsByIndex = new();

        private const int SecurityIndexBits = 20;
        private const int PeriodIndexBits = 10;

        /// <summary>Packs a counted filer into its key, assigning either index on first sight.</summary>
        private long FilerKey(SecurityIdentifier security, DateTime period, int cik)
        {
            if (!_securityIndexes.TryGetValue(security, out var securityIndex))
            {
                securityIndex = _securitiesByIndex.Count;
                if (securityIndex >= 1 << SecurityIndexBits)
                {
                    // Throwing rather than wrapping: a wrapped index would silently credit one
                    // security's filers to another, and no row count would show it.
                    throw new InvalidOperationException(
                        $"More than {1 << SecurityIndexBits} securities; the filer key needs more bits");
                }

                _securityIndexes[security] = securityIndex;
                _securitiesByIndex.Add(security);
            }

            if (!_periodIndexes.TryGetValue(period, out var periodIndex))
            {
                periodIndex = _periodsByIndex.Count;
                if (periodIndex >= 1 << PeriodIndexBits)
                {
                    throw new InvalidOperationException(
                        $"More than {1 << PeriodIndexBits} reported periods; the filer key needs more bits");
                }

                _periodIndexes[period] = periodIndex;
                _periodsByIndex.Add(period);
            }

            return ((long)securityIndex << 42) | ((long)periodIndex << 32) | (uint)cik;
        }

        /// <summary>
        /// How many of these filers had not been counted for this security and quarter yet, marking
        /// them counted. A manager reaches Holders once, on the first day it reports the security.
        /// </summary>
        internal int CountNewFilers(SecurityIdentifier security, DateTime period, IEnumerable<int> ciks,
            DateTime filingDate)
        {
            var stamp = int.Parse(filingDate.ToStringInvariant(DateFormat.EightCharacter), CultureInfo.InvariantCulture);
            var newFilers = 0;

            foreach (var cik in ciks)
            {
                if (_countedFilers.TryAdd(FilerKey(security, period, cik), stamp))
                {
                    newFilers++;
                }
            }

            return newFilers;
        }

        /// <summary>
        /// Forgets every filer first counted inside these archives' windows, so reading them again
        /// recounts the same increments. Otherwise the second night would find the whole window
        /// counted and publish Holders increments of zero over the real ones.
        /// </summary>
        internal void PruneFilerState(List<Archive> archives)
        {
            if (_deploymentDate == null || _countedFilers.Count == 0)
            {
                return;
            }

            var windows = archives
                .Select(archive => (
                    Start: int.Parse(archive.Start.ToStringInvariant(DateFormat.EightCharacter), CultureInfo.InvariantCulture),
                    End: int.Parse(archive.End.ToStringInvariant(DateFormat.EightCharacter), CultureInfo.InvariantCulture)))
                .ToList();

            var stale = new List<long>();
            foreach (var (key, stamp) in _countedFilers)
            {
                if (windows.Any(window => stamp >= window.Start && stamp <= window.End))
                {
                    stale.Add(key);
                }
            }

            foreach (var key in stale)
            {
                _countedFilers.Remove(key);
            }

            Log.Trace($"SEC13FDownloader.PruneFilerState(): dropped {stale.Count} filers first counted inside " +
                      $"{string.Join(", ", archives.Select(archive => archive.Name))}, {_countedFilers.Count} kept");
        }

        /// <summary>Swaps a filer key's security and period indexes through the given maps, keeping the CIK.</summary>
        private static long Repack(long key, int[] securities, int[] periods)
        {
            var securityIndex = (int)(key >> 42);
            var periodIndex = (int)((key >> 32) & ((1 << PeriodIndexBits) - 1));
            return ((long)securities[securityIndex] << 42) | ((long)periods[periodIndex] << 32) | (key & 0xFFFFFFFFL);
        }

        /// <summary>The inverse of a permutation: for each value, the position it holds.</summary>
        private static int[] Invert(int[] order)
        {
            var positions = new int[order.Length];
            for (var position = 0; position < order.Length; position++)
            {
                positions[order[position]] = position;
            }

            return positions;
        }

        /// <summary>Unpacks a filer key back into the three values the state file carries.</summary>
        private (SecurityIdentifier Security, DateTime Period, int Cik) FilerParts(long key)
        {
            var securityIndex = (int)(key >> 42);
            var periodIndex = (int)((key >> 32) & ((1 << PeriodIndexBits) - 1));
            return (_securitiesByIndex[securityIndex], _periodsByIndex[periodIndex], (int)(uint)key);
        }

        /// <summary>
        /// The counted filers, published next to the security files so an incremental run can tell a
        /// filer already counted from a new one: a distinct count cannot be rebuilt from the totals.
        /// </summary>
        private const string FilerStateFileName = "filers.zip";

        private const string FilerStateEntryName = "filers.csv";

        /// <summary>The stamp written on the state entry, fixed so the archive is byte reproducible.</summary>
        private static readonly DateTimeOffset FilerStateTimestamp =
            new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

        /// <summary>
        /// Loads the counted filers an earlier run published. A full run rebuilds every count and
        /// starts empty.
        /// </summary>
        internal void ReadFilerState()
        {
            if (_deploymentDate == null)
            {
                Log.Trace("SEC13FDownloader.ReadFilerState(): full history run, rebuilding the filer state from scratch");
                return;
            }

            var path = Path.Combine(_processedDataDirectory, FilerStateFileName);
            using var file = File.OpenRead(path);
            using var zip = new ZipArchive(file, ZipArchiveMode.Read);

            var entry = zip.GetEntry(FilerStateEntryName)
                ?? throw new InvalidOperationException($"{path} carries no {FilerStateEntryName}");

            using var reader = new StreamReader(entry.Open());
            string line;
            var rows = 0;

            while ((line = reader.ReadLine()) != null)
            {
                if (line.Length == 0)
                {
                    continue;
                }

                var parts = line.Split(',');
                if (parts.Length != 4)
                {
                    throw new InvalidOperationException($"{path}: '{line}' is not a filer row");
                }

                _countedFilers[FilerKey(
                    SecurityIdentifier.Parse(parts[0]),
                    DateTime.ParseExact(parts[1], DateFormat.EightCharacter, CultureInfo.InvariantCulture),
                    int.Parse(parts[2], CultureInfo.InvariantCulture))] = int.Parse(parts[3], CultureInfo.InvariantCulture);
                rows++;
            }

            Log.Trace($"SEC13FDownloader.ReadFilerState(): read {rows} counted filers from {path}, " +
                      $"{_securitiesByIndex.Count} securities and {_periodsByIndex.Count} periods");
        }

        /// <summary>
        /// Publishes the counted filers sorted by security, quarter and CIK. The order comes from the
        /// values and not from the packed keys, whose indexes depend on the order things were first
        /// seen, so a full run and an incremental one write the same bytes.
        /// </summary>
        internal void WriteFilerState()
        {
            Directory.CreateDirectory(_destinationDirectory);

            var path = Path.Combine(_destinationDirectory, FilerStateFileName);

            var securityOrder = Enumerable.Range(0, _securitiesByIndex.Count)
                .OrderBy(index => _securitiesByIndex[index].ToString(), StringComparer.Ordinal)
                .ToArray();
            var periodOrder = Enumerable.Range(0, _periodsByIndex.Count)
                .OrderBy(index => _periodsByIndex[index])
                .ToArray();
            var securityRanks = Invert(securityOrder);
            var periodRanks = Invert(periodOrder);

            var keys = new long[_countedFilers.Count];
            var next = 0;
            foreach (var key in _countedFilers.Keys)
            {
                keys[next++] = Repack(key, securityRanks, periodRanks);
            }

            Array.Sort(keys);

            SEC13FFiles.WriteThenMove(path, file =>
            {
                using var zip = new ZipArchive(file, ZipArchiveMode.Create, leaveOpen: true);
                var entry = zip.CreateEntry(FilerStateEntryName, CompressionLevel.Optimal);

                // A fixed stamp, or the same filers would hash differently on every run.
                entry.LastWriteTime = FilerStateTimestamp;

                using var writer = new StreamWriter(entry.Open());
                foreach (var ranked in keys)
                {
                    var key = Repack(ranked, securityOrder, periodOrder);
                    var (security, period, cik) = FilerParts(key);
                    writer.Write(security.ToString());
                    writer.Write(',');
                    writer.Write(period.ToStringInvariant(DateFormat.EightCharacter));
                    writer.Write(',');
                    writer.Write(cik.ToStringInvariant());
                    writer.Write(',');
                    writer.Write(_countedFilers[key].ToStringInvariant());
                    writer.Write('\n');
                }
            });
            Log.Trace($"SEC13FDownloader.WriteFilerState(): wrote {keys.Length} counted filers to {path}, " +
                      $"{new FileInfo(path).Length / (1024 * 1024)} MB");
        }

        /// <summary>The group a reported line belongs to.</summary>
        internal readonly record struct HoldingKey(string Cusip, DateTime Period, DateTime FilingDate);

        /// <summary>
        /// Streams INFOTABLE.tsv and sums every reported line into its group, with no dedup by
        /// discretion or other manager: measured against shares outstanding the raw sum matches
        /// published ownership, and both dedup rules understate it about three times.
        /// </summary>
        internal Dictionary<HoldingKey, Holding> ReadInfoTable(
            ZipArchive zip, Archive archive, Dictionary<string, Submission> submissions)
        {
            var holdings = new Dictionary<HoldingKey, Holding>();
            var units = DetectValueUnits(zip, archive, submissions);

            foreach (var (columns, fields) in ReadTable(zip, archive, "INFOTABLE.tsv",
                         "ACCESSION_NUMBER", "CUSIP", "VALUE", "SSHPRNAMT", "SSHPRNAMTTYPE", "PUTCALL",
                         "VOTING_AUTH_SOLE", "VOTING_AUTH_SHARED"))
            {
                var accession = fields[columns["ACCESSION_NUMBER"]];
                if (!submissions.TryGetValue(accession, out var submission))
                {
                    // A notice, or a filing an earlier archive already contributed.
                    continue;
                }

                var cusip = fields[columns["CUSIP"]].Trim().ToUpperInvariant();
                if (cusip.Length == 0)
                {
                    continue;
                }

                var key = new HoldingKey(cusip, submission.Period, submission.FilingDate);
                if (!holdings.TryGetValue(key, out var group))
                {
                    holdings[key] = group = new Holding();
                }

                // Holders counts every manager that reported the security, options included. An
                // amendment's lines are held apart per filer until AdmitAmendment knows whether the
                // manager had already reported it.
                Measures holding;
                if (submission.IsAmendment)
                {
                    if (!group.Amendments.TryGetValue(submission.Cik, out holding))
                    {
                        group.Amendments[submission.Cik] = holding = new Measures();
                    }
                }
                else
                {
                    group.Ciks.Add(submission.Cik);
                    holding = group;
                }

                holding.ConfidentialOmitted |= submission.ConfidentialOmitted;

                var shareType = fields[columns["SSHPRNAMTTYPE"]].Trim();
                var putCall = fields[columns["PUTCALL"]].Trim();
                var amount = ParseDecimal(fields[columns["SSHPRNAMT"]]);

                if (putCall.Equals("Call", StringComparison.OrdinalIgnoreCase))
                {
                    holding.CallShares += amount;
                    continue;
                }

                if (putCall.Equals("Put", StringComparison.OrdinalIgnoreCase))
                {
                    holding.PutShares += amount;
                    continue;
                }

                // On a PRN line SSHPRNAMT is the principal amount, which is what PrincipalValue
                // publishes; VALUE is the market value of that debt.
                if (shareType.Equals("PRN", StringComparison.OrdinalIgnoreCase))
                {
                    holding.PrincipalValue += amount;
                    continue;
                }

                if (!shareType.Equals("SH", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                // VALUE only counts under the share filter: unfiltered it totals $70.1 trillion for a
                // single quarter. In whole dollars, whichever unit the line used.
                var value = ParseDecimal(fields[columns["VALUE"]]);
                holding.Shares += amount;
                holding.HoldingValue += value * units.Factor(accession, cusip, submission, value, amount);
                holding.VotingSole += ParseDecimal(fields[columns["VOTING_AUTH_SOLE"]]);
                holding.VotingShared += ParseDecimal(fields[columns["VOTING_AUTH_SHARED"]]);
            }

            Log.Trace($"SEC13FDownloader.ReadInfoTable(): {archive.Name}: {units.CorrectedLines} share lines in " +
                      "a different unit than the rest of their filing");
            return holdings;
        }

        /// <summary>
        /// The factors that turn VALUE into whole dollars. Filers do not all follow the 2023 unit
        /// change (in Apple's December 2019 quarter 85 lines in dollars made 88 percent of the scaled
        /// total), and some mix units within one filing, so implied prices are compared with every
        /// filer's median for the same security: per line where the security has enough reports to
        /// trust its median, per filing otherwise. A thousand times off means the other unit.
        /// </summary>
        internal static ValueUnits DetectValueUnits(ZipArchive zip, Archive archive,
            Dictionary<string, Submission> submissions)
        {
            var keys = new Dictionary<(string Cusip, DateTime Period), int>();
            var pricesByKey = new List<List<double>>();
            var linesByFiling = new Dictionary<string, List<(int Key, double Price)>>(StringComparer.Ordinal);

            foreach (var (columns, fields) in ReadTable(zip, archive, "INFOTABLE.tsv",
                         "ACCESSION_NUMBER", "CUSIP", "VALUE", "SSHPRNAMT", "SSHPRNAMTTYPE", "PUTCALL"))
            {
                var accession = fields[columns["ACCESSION_NUMBER"]];
                if (!submissions.TryGetValue(accession, out var submission)
                    || !fields[columns["SSHPRNAMTTYPE"]].Trim().Equals("SH", StringComparison.OrdinalIgnoreCase)
                    || fields[columns["PUTCALL"]].Trim().Length > 0)
                {
                    continue;
                }

                var value = ParseDecimal(fields[columns["VALUE"]]);
                var amount = ParseDecimal(fields[columns["SSHPRNAMT"]]);
                if (value <= 0m || amount <= 0m)
                {
                    continue;
                }

                var cusip = (fields[columns["CUSIP"]].Trim().ToUpperInvariant(), submission.Period);
                if (!keys.TryGetValue(cusip, out var key))
                {
                    keys[cusip] = key = pricesByKey.Count;
                    pricesByKey.Add(new List<double>());
                }

                var price = (double)(value / amount);
                pricesByKey[key].Add(price);

                if (!linesByFiling.TryGetValue(accession, out var lines))
                {
                    linesByFiling[accession] = lines = new List<(int, double)>();
                }

                lines.Add((key, price));
            }

            // A median needs a few reports behind it before it says anything about the unit.
            var medians = pricesByKey
                .Select(prices => prices.Count >= MinimumReportsForMedianPrice ? Median(prices) : double.NaN)
                .ToArray();

            var units = new Dictionary<string, decimal>(StringComparer.Ordinal);
            var againstTheRule = 0;
            foreach (var (accession, submission) in submissions)
            {
                var offsets = linesByFiling.TryGetValue(accession, out var lines)
                    ? lines.Where(line => !double.IsNaN(medians[line.Key]))
                        .Select(line => Math.Log10(line.Price / medians[line.Key]))
                        .ToList()
                    : new List<double>();

                var thousandsRule = submission.FilingDate < ValueInWholeDollarsFrom;
                units[accession] = ValueMultiplier(offsets, thousandsRule);
                if (units[accession] != (thousandsRule ? 1000m : 1m))
                {
                    againstTheRule++;
                }
            }

            Log.Trace($"SEC13FDownloader.DetectValueUnits(): {archive.Name}: {againstTheRule} of " +
                      $"{submissions.Count} filings report VALUE in the other unit");

            var references = keys
                .Where(key => pricesByKey[key.Value].Count >= MinimumReportsForLineCorrection)
                .ToDictionary(key => key.Key, key => medians[key.Value]);
            return new ValueUnits(units, references);
        }

        /// <summary>
        /// The whole-dollar factor of each share line: its own, measured against the median price of
        /// its security when that is well reported, or else the one decided for its whole filing.
        /// </summary>
        internal sealed class ValueUnits
        {
            private readonly Dictionary<string, decimal> _filings;
            private readonly Dictionary<(string Cusip, DateTime Period), double> _references;

            /// <summary>Share lines whose factor differed from their filing's.</summary>
            public long CorrectedLines { get; private set; }

            public ValueUnits(Dictionary<string, decimal> filings, Dictionary<(string Cusip, DateTime Period), double> references)
            {
                _filings = filings;
                _references = references;
            }

            /// <summary>The factor for one share line of VALUE <paramref name="value"/> over <paramref name="amount"/> shares.</summary>
            public decimal Factor(string accession, string cusip, Submission submission, decimal value, decimal amount)
            {
                var filing = _filings[accession];
                if (value <= 0m || amount <= 0m || !_references.TryGetValue((cusip, submission.Period), out var median))
                {
                    return filing;
                }

                // How many thousandfold steps the line's price sits from the majority's, which reports
                // in the unit of its filing date.
                var steps = (int)Math.Clamp(Math.Round(Math.Log10((double)(value / amount) / median) / 3), -2, 2);
                var factor = submission.FilingDate < ValueInWholeDollarsFrom ? 1000m : 1m;
                for (var i = 0; i < Math.Abs(steps); i++)
                {
                    factor = steps > 0 ? factor / 1000m : factor * 1000m;
                }

                // One thousandfold step either way is a unit slip. A line a thousand times above the
                // median in the dollar era is almost always a VALUE typed a thousand times too large:
                // of the 4,740 such lines of the March 2023 quarter whose filer reported the same
                // security the quarter before, 4,578 held the same shares then and 162 a thousand times
                // more. A millionfold step is a wrong share count rather than a unit, and scaling it
                // inflated Apple's 2019 quarters by ten percent, so such a line keeps its filing's factor.
                if (factor > 1000m || factor < 0.001m)
                {
                    return filing;
                }

                if (factor != filing)
                {
                    CorrectedLines++;
                }

                return factor;
            }
        }

        /// <summary>
        /// Whether a filing's VALUE is multiplied by a thousand. The offsets are log10 of each of its
        /// implied prices over the median for that security: near zero it uses the majority's unit,
        /// near +3 it reports dollars where the rule is thousands, near -3 the reverse. With nothing
        /// to compare against, the rule of the filing date stands.
        /// </summary>
        internal static decimal ValueMultiplier(List<double> offsets, bool thousandsRule)
        {
            var offset = offsets.Count == 0 ? 0d : Median(offsets);
            if (thousandsRule)
            {
                return offset > 1.5 ? 1m : 1000m;
            }

            return offset < -1.5 ? 1000m : 1m;
        }

        private static double Median(List<double> values)
        {
            var sorted = values.OrderBy(value => value).ToList();
            var middle = sorted.Count / 2;
            return sorted.Count % 2 == 1 ? sorted[middle] : (sorted[middle - 1] + sorted[middle]) / 2;
        }

        /// <summary>
        /// Resolves each group to a security and queues its increment, the filings of that day only;
        /// the finalize pass turns increments into running totals. Groups that resolve to nothing,
        /// mostly foreign issuers, are dropped.
        /// </summary>
        private void EmitHoldings(Dictionary<HoldingKey, Holding> holdings)
        {
            // Filing date order, since a filer counts on the first day it reports the security.
            foreach (var day in holdings.GroupBy(entry => entry.Key.FilingDate).OrderBy(group => group.Key))
            {
                var resolved = new List<(HoldingKey Key, Holding Holding, SecurityIdentifier Security, string Ticker, int Counted)>();

                // Originals first, for every group of the day, so an amendment filed the same day as
                // the manager's original is recognised as a restatement whichever CUSIP comes first.
                foreach (var (key, holding) in day)
                {
                    var security = ResolveSecurity(key.Cusip, key.FilingDate);
                    var ticker = security == null ? null : ResolveTicker(security, key.FilingDate);
                    if (string.IsNullOrWhiteSpace(ticker) || !IsFileNameSafe(ticker))
                    {
                        _unresolvedGroups++;
                        _unresolvedValue += holding.ReportedValue;
                        continue;
                    }

                    // Whoever reads the ticker's file takes its rows to belong to the security that
                    // owns the ticker that day; a group of another security is dropped, not mixed in.
                    var owner = TickerOwner(ticker, key.FilingDate);
                    if (owner != security.ToString())
                    {
                        _conflictingTickers++;
                        _conflictingValue += holding.ReportedValue;
                        if (_conflictSample.Count < 10)
                        {
                            _conflictSample.Add($"{key.Cusip} {key.FilingDate:yyyy-MM-dd} {ticker} is {security}, owner {owner ?? "none"}");
                        }

                        _unresolvedGroups++;
                        _unresolvedValue += holding.ReportedValue;
                        continue;
                    }

                    // Counted after the guards above: a group that resolves to nothing must not
                    // consume the filer, or the day it does resolve would find it already taken.
                    resolved.Add((key, holding, security, ticker.ToLowerInvariant(),
                        CountNewFilers(security, key.Period, holding.Ciks, key.FilingDate)));
                }

                foreach (var (key, holding, security, ticker, counted) in resolved)
                {
                    var admitted = new Measures();
                    admitted.Add(holding);
                    var newFilers = counted;

                    foreach (var (cik, amendment) in holding.Amendments)
                    {
                        if (AdmitAmendment(security, key.Period, cik, key.FilingDate, out var isNew))
                        {
                            admitted.Add(amendment);
                            newFilers += isNew ? 1 : 0;
                        }
                    }

                    _resolvedValue += holding.ReportedValue;

                    // A day of nothing but restatements of counted positions changes no total.
                    var values = admitted.ToValues(newFilers);
                    if (!admitted.ConfidentialOmitted && values.All(value => value == 0m))
                    {
                        _unchangedGroups++;
                        continue;
                    }

                    Queue(security.ToString(), ticker, new HoldingsRow
                    {
                        Time = key.FilingDate + ReleaseTimeOfDay,
                        PeriodEnd = key.Period,
                        Values = values,
                        ConfidentialOmitted = admitted.ConfidentialOmitted
                    });
                }
            }
        }

        /// <summary>
        /// Whether an amendment's lines for this security and quarter are added, and whether its
        /// filer is new: only when the manager had not reported the security for the quarter yet.
        /// </summary>
        internal bool AdmitAmendment(SecurityIdentifier security, DateTime period, int cik, DateTime filingDate,
            out bool newFiler)
        {
            var key = FilerKey(security, period, cik);
            var stamp = int.Parse(filingDate.ToStringInvariant(DateFormat.EightCharacter), CultureInfo.InvariantCulture);

            if (_countedFilers.TryAdd(key, stamp))
            {
                _admittedAmendments.Add((key, stamp));
                newFiler = true;
                return true;
            }

            // Already counted: either by this same amendment through another CUSIP of the same
            // security, which is still part of that one new position, or by an earlier report,
            // which this amendment restates.
            newFiler = false;
            return _admittedAmendments.Contains((key, stamp));
        }

        /// <summary>
        /// The security trading under a ticker on a date per the map files, or null. It is how the
        /// universe and LEAN read a ticker file, so writing applies the same test.
        /// </summary>
        private string TickerOwner(string ticker, DateTime date)
        {
            var key = (ticker.ToUpperInvariant(), date.Date);
            if (!_tickerOwners.TryGetValue(key, out var owner))
            {
                var mapFile = _mapFileProvider
                    .Get(new AuxiliaryDataKey(Market.USA, SecurityType.Equity))
                    .ResolveMapFile(key.Item1, key.Item2);

                _tickerOwners[key] = owner = mapFile.Any()
                    ? SecurityIdentifier.GenerateEquity(mapFile.FirstDate, mapFile.FirstTicker, Market.USA).ToString()
                    : null;
            }

            return owner;
        }

        /// <summary>
        /// Resolves a CUSIP to a security, point in time: by CUSIP, then by the US ISIN built from
        /// it, then through the N-PORT ticker crosswalk.
        /// </summary>
        private SecurityIdentifier ResolveSecurity(string rawCusip, DateTime tradingDate)
        {
            var key = (rawCusip, tradingDate);
            if (_resolvedCusips.TryGetValue(key, out var cached))
            {
                return cached;
            }

            // The summary counts CUSIPs, not lookups, and one CUSIP is now resolved once per filing
            // date rather than once per run.
            var firstSighting = _countedCusips.Add(rawCusip);

            var cusip = NormalizeCusip(rawCusip);
            if (cusip == null)
            {
                if (firstSighting)
                {
                    _malformedCusips++;
                }

                _resolvedCusips[key] = null;
                return null;
            }

            // Step 1: LEAN stores the CUSIP without its check digit, so the ninth character comes off.
            var symbol = _symbolResolver.CUSIP(cusip.Substring(0, 8), tradingDate);
            if (symbol != null)
            {
                if (firstSighting)
                {
                    _resolvedByCusip++;
                }
            }
            else
            {
                // Step 2: the US ISIN reaches issuers whose CUSIP is blank in the security database.
                symbol = _symbolResolver.ISIN(BuildUnitedStatesIsin(cusip), tradingDate);
                if (symbol != null && firstSighting)
                {
                    _resolvedByIsin++;
                }
            }

            // Step 3: the N-PORT crosswalk needs no security database, and reaches Alphabet and the
            // CINS foreign issuers the constructed ISIN cannot represent.
            if (symbol == null)
            {
                var identifier = ResolveThroughTicker(cusip);
                if (identifier != null)
                {
                    if (firstSighting)
                    {
                        _resolvedByTicker++;
                    }

                    _resolvedCusips[key] = identifier;
                    return identifier;
                }

                _unresolvedCusips.Add(cusip);
                _resolvedCusips[key] = null;
                return null;
            }

            _resolvedCusips[key] = symbol.ID;
            return symbol.ID;
        }

        /// <summary>
        /// Brings a reported CUSIP to nine characters, or null when it cannot be one. A short value
        /// has lost either its leading zero ("37833100", Apple) or its check digit ("46428722", an
        /// iShares fund). Padding the second with a zero would invent another security, so the
        /// check digit decides which repair applies.
        /// </summary>
        internal static string NormalizeCusip(string cusip)
        {
            if (cusip.Length == 9)
            {
                return cusip;
            }

            if (cusip.Length is < 6 or > 9)
            {
                return null;
            }

            var padded = cusip.PadLeft(9, '0');
            if (ComputeCusipCheckDigit(padded.Substring(0, 8)) == padded[8])
            {
                return padded;
            }

            if (cusip.Length != 8)
            {
                return null;
            }

            var checkDigit = ComputeCusipCheckDigit(cusip);
            return checkDigit.HasValue ? cusip + checkDigit.Value : null;
        }

        /// <summary>
        /// The CUSIP check digit: a modulus ten sum over the eight character body where every
        /// second character counts double, letters standing for their position in the alphabet
        /// plus nine.
        /// </summary>
        internal static char? ComputeCusipCheckDigit(string body)
        {
            var sum = 0;
            for (var i = 0; i < body.Length; i++)
            {
                var character = body[i];
                int value;
                if (char.IsDigit(character))
                {
                    value = character - '0';
                }
                else if (char.IsLetter(character))
                {
                    value = char.ToUpperInvariant(character) - 'A' + 10;
                }
                else
                {
                    value = character switch { '*' => 36, '@' => 37, '#' => 38, _ => -1 };
                    if (value < 0)
                    {
                        // Not a character a CUSIP can hold, so there is no check digit.
                        return null;
                    }
                }

                if (i % 2 == 1)
                {
                    value *= 2;
                }

                sum += value / 10 + value % 10;
            }

            return (char)('0' + (10 - sum % 10) % 10);
        }

        /// <summary>
        /// Resolves a CUSIP through the N-PORT ticker crosswalk. The ticker is resolved on the day
        /// the funds reported it, since a ticker names a security only on a date: at a 2021 filing
        /// META named a Roundhill ETF, not Facebook. The map file is checked first because
        /// GenerateEquity never returns null for an unknown ticker.
        /// </summary>
        internal SecurityIdentifier ResolveThroughTicker(string cusip)
        {
            if (!TickerCrosswalk.TryGetValue(cusip, out var entry))
            {
                return null;
            }

            if (!_crosswalkSecurities.TryGetValue(cusip, out var security))
            {
                var mapFile = _mapFileProvider
                    .Get(new AuxiliaryDataKey(Market.USA, SecurityType.Equity))
                    .ResolveMapFile(entry.Ticker, entry.Observed);

                // The map file has to carry the ticker on that very date. Barrick reached the
                // crosswalk as ABX, its Toronto ticker, when ABX named no US security, and the
                // resolver still returned the company that took ABX months later.
                var tradedUnderIt = mapFile.Any() && string.Equals(
                    mapFile.GetMappedSymbol(entry.Observed, null), entry.Ticker, StringComparison.OrdinalIgnoreCase);

                _crosswalkSecurities[cusip] = security = tradedUnderIt
                    ? SecurityIdentifier.GenerateEquity(mapFile.FirstDate, mapFile.FirstTicker, Market.USA)
                    : null;
            }

            return security;
        }

        /// <summary>
        /// Builds the US ISIN for a nine character CUSIP: the country code, the CUSIP, and the
        /// check digit, which is arithmetic rather than a lookup. "037833100" gives "US0378331005".
        /// </summary>
        internal static string BuildUnitedStatesIsin(string cusip)
        {
            var body = "US" + cusip;
            return body + ComputeIsinCheckDigit(body);
        }

        /// <summary>
        /// Computes an ISIN check digit: expand each letter to its ordinal (A is 10 through Z is 35),
        /// then run Luhn over the resulting digits from the right.
        /// </summary>
        private static char ComputeIsinCheckDigit(string body)
        {
            var digits = new StringBuilder(body.Length * 2);
            foreach (var character in body)
            {
                if (character >= '0' && character <= '9')
                {
                    digits.Append(character);
                }
                else if (character >= 'A' && character <= 'Z')
                {
                    digits.Append((character - 'A' + 10).ToStringInvariant());
                }
                else
                {
                    throw new FormatException(
                        $"SEC13FDownloader.ComputeIsinCheckDigit(): '{body}' carries '{character}', which is neither " +
                        "a digit nor an upper case letter");
                }
            }

            var sum = 0;
            var doubled = true;
            for (var i = digits.Length - 1; i >= 0; i--)
            {
                var value = digits[i] - '0';
                if (doubled)
                {
                    value *= 2;
                    if (value > 9)
                    {
                        value -= 9;
                    }
                }

                sum += value;
                doubled = !doubled;
            }

            return (char)('0' + (10 - sum % 10) % 10);
        }

        /// <summary>
        /// The ticker a security traded under on a date, or null when its map file does not cover
        /// it. No fallback to the last ticker: managers report dead CUSIPs for years, and by then the
        /// ticker can belong to another company.
        /// </summary>
        internal string ResolveTicker(SecurityIdentifier security, DateTime tradingDate)
        {
            // Before its first row a map file answers with its first ticker, for a security that
            // did not trade yet.
            var mapFile = MapFileOf(security);
            if (mapFile == null || tradingDate < mapFile.FirstDate)
            {
                return null;
            }

            var ticker = mapFile.GetMappedSymbol(tradingDate, null);
            return string.IsNullOrEmpty(ticker) ? null : ticker;
        }

        /// <summary>The map file of a security, cached, or null when it has none.</summary>
        private MapFile MapFileOf(SecurityIdentifier security)
        {
            if (!_mapFiles.TryGetValue(security, out var mapFile))
            {
                _mapFiles[security] = mapFile = _mapFileProvider
                    .Get(AuxiliaryDataKey.Create(security))
                    .ResolveMapFile(security.Symbol, security.Date);
            }

            return mapFile;
        }

        /// <summary>
        /// Queues an increment for a security, to be staged when the archive is done, summing it into
        /// whatever this archive already holds for the same quarter and release date.
        /// </summary>
        private void Queue(string security, string ticker, HoldingsRow row)
        {
            if (!_pendingSecurityRows.TryGetValue(security, out var rows))
            {
                _pendingSecurityRows[security] = rows = new Dictionary<(DateTime, DateTime), (string, HoldingsRow)>();
            }

            var key = (row.PeriodEnd, row.Time);
            rows[key] = rows.TryGetValue(key, out var queued) ? (queued.Ticker, Add(queued.Row, row)) : (ticker, row);
        }

        /// <summary>Sums two increments reported for the same security, quarter and release date.</summary>
        private static HoldingsRow Add(HoldingsRow left, HoldingsRow right)
        {
            var values = new decimal[ValueColumnCount];
            for (var i = 0; i < ValueColumnCount; i++)
            {
                values[i] = left.Values[i] + right.Values[i];
            }

            return new HoldingsRow
            {
                Time = left.Time,
                PeriodEnd = left.PeriodEnd,
                Values = values,
                ConfidentialOmitted = left.ConfidentialOmitted || right.ConfidentialOmitted
            };
        }

        /// <summary>
        /// Appends the archive's increments to the staging file of their security; the merge happens
        /// once, in the finalize pass.
        /// </summary>
        private void FlushPendingRows()
        {
            Directory.CreateDirectory(_stagingDirectory);
            foreach (var (security, rows) in _pendingSecurityRows)
            {
                File.AppendAllLines(
                    StagingPath(security),
                    rows.Values
                        .OrderBy(entry => entry.Row.PeriodEnd)
                        .ThenBy(entry => entry.Row.Time)
                        .Select(entry => $"{entry.Ticker},{FormatRow(entry.Row)}"));
                _stagedSecurities.Add(security);
            }

            _pendingSecurityRows.Clear();
        }

        /// <summary>The staging file of a security. The identifier carries a space, so it is escaped.</summary>
        private string StagingPath(string security)
        {
            return Path.Combine(_stagingDirectory, Uri.EscapeDataString(security) + ".csv");
        }

        /// <summary>
        /// Turns the staged increments into the running total per quarter and writes the ticker
        /// files. The total is kept per security, so a quarter still filing at a rename carries on in
        /// the new ticker's file. An incremental run folds in the security's published rows from
        /// every ticker it traded under, and keeps the rows of other securities in a shared file.
        /// </summary>
        private void FinalizeSecurityFiles()
        {
            if (_stagedSecurities.Count == 0)
            {
                Log.Trace("SEC13FDownloader.FinalizeSecurityFiles(): nothing was written");
                return;
            }

            var incremental = _deploymentDate != null;
            var tickerStaging = Path.Combine(_stagingDirectory, "tickers");
            Directory.CreateDirectory(tickerStaging);

            var shelfRows = 0L;
            foreach (var security in _stagedSecurities)
            {
                var published = incremental
                    ? PublishedRowsOf(security)
                    : new List<(string Ticker, HoldingsRow Row)>();
                shelfRows += published.Count;

                var merged = MergeSecurity(published, ReadStagedRows(StagingPath(security)));
                foreach (var ticker in merged.GroupBy(entry => entry.Ticker))
                {
                    File.AppendAllLines(Path.Combine(tickerStaging, $"{ticker.Key}.csv"),
                        ticker.Select(entry => FormatRow(entry.Row)));
                }
            }

            // A ticker file can hold the rows of more than one security, one after another as the
            // ticker changed hands, so each is written once every security has been merged into it.
            Directory.CreateDirectory(_destinationDirectory);
            var files = Directory.GetFiles(tickerStaging, "*.csv");
            var keptRows = 0L;

            foreach (var file in files)
            {
                var ticker = Path.GetFileNameWithoutExtension(file);
                var rows = ReadRows(file).ToList();

                if (incremental)
                {
                    var others = OtherSecuritiesRows(ticker);
                    keptRows += others.Count;
                    rows.AddRange(others);
                }

                // Time order: LEAN's SubscriptionDataReader silently drops a point whose timestamp
                // moves backwards. Ties keep the older quarter first.
                File.WriteAllLines(
                    Path.Combine(_destinationDirectory, $"{ticker}.csv"),
                    rows.OrderBy(row => row.Time).ThenBy(row => row.PeriodEnd).Select(FormatRow));
            }

            // The shelf row count is what tells a merge from a silent rebuild, so it is logged.
            Log.Trace($"SEC13FDownloader.FinalizeSecurityFiles(): {_stagedSecurities.Count} securities merged into " +
                      $"{files.Length} ticker files, {shelfRows} of their rows read from {_processedDataDirectory}, " +
                      $"{keptRows} rows of other securities kept unchanged");

            Directory.Delete(_stagingDirectory, recursive: true);
        }

        /// <summary>
        /// The published rows of a security, from every ticker it ever traded under. A ticker file
        /// keeps only the rows written while the security owned the ticker, which is the same test
        /// EmitHoldings applies before writing one.
        /// </summary>
        private List<(string Ticker, HoldingsRow Row)> PublishedRowsOf(string security)
        {
            var rows = new List<(string Ticker, HoldingsRow Row)>();
            var mapFile = MapFileOf(SecurityIdentifier.Parse(security));
            if (mapFile == null)
            {
                return rows;
            }

            foreach (var ticker in mapFile.Select(row => row.MappedSymbol.ToLowerInvariant()).Distinct())
            {
                var path = Path.Combine(_processedDataDirectory, $"{ticker}.csv");
                if (!IsFileNameSafe(ticker) || !File.Exists(path))
                {
                    continue;
                }

                rows.AddRange(ReadRows(path)
                    .Where(row => TickerOwner(ticker, row.Time.Date) == security)
                    .Select(row => (ticker, row)));
            }

            // Only this security's dates are held, and the next one asks about others.
            _tickerOwners.Clear();
            return rows;
        }

        /// <summary>
        /// The published rows of a ticker that belong to a security this run did not touch, which
        /// stay as they are. The rows of a touched security come back through its own merge.
        /// </summary>
        private List<HoldingsRow> OtherSecuritiesRows(string ticker)
        {
            var path = Path.Combine(_processedDataDirectory, $"{ticker}.csv");
            if (!File.Exists(path))
            {
                return new List<HoldingsRow>();
            }

            var rows = ReadRows(path)
                .Where(row =>
                {
                    var owner = TickerOwner(ticker, row.Time.Date);
                    return owner == null || !_stagedSecurities.Contains(owner);
                })
                .ToList();

            _tickerOwners.Clear();
            return rows;
        }

        /// <summary>Reads a staging file back: the ticker, then the increment in the published layout.</summary>
        private static IEnumerable<(string Ticker, HoldingsRow Row)> ReadStagedRows(string path)
        {
            foreach (var line in File.ReadLines(path))
            {
                if (string.IsNullOrWhiteSpace(line))
                {
                    continue;
                }

                var comma = line.IndexOf(',');
                yield return (line.Substring(0, comma), ParseRow(line.Substring(comma + 1)));
            }
        }

        /// <summary>
        /// Rebuilds the universe from the per-security files and forward-fills it across business
        /// days, so each file is the whole cross-section on its date rather than who filed that day.
        /// A row only replaces one of an equal or older quarter, since amendments arrive years late
        /// and must not overwrite newer figures. A quarter leaves once it is older than
        /// OldestLivePeriod, and a security after its delisting date.
        /// </summary>
        internal void BuildUniverseFiles()
        {
            // Release date to the rows public that day. An incremental run also reads the published
            // files, this run's copy of a ticker winning; a full run replaces them and reads only its own.
            var events = new SortedDictionary<DateTime, List<UniverseRow>>();
            var securities = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var unmapped = 0;
            var malformed = 0;

            var directories = _deploymentDate == null
                ? new[] { _destinationDirectory }
                : new[] { _destinationDirectory, _processedDataDirectory };

            foreach (var directory in directories)
            {
                if (!Directory.Exists(directory))
                {
                    continue;
                }

                foreach (var file in Directory.EnumerateFiles(directory, "*.csv"))
                {
                    var ticker = Path.GetFileNameWithoutExtension(file).ToUpperInvariant();
                    if (!securities.Add(ticker))
                    {
                        continue;
                    }

                    foreach (var line in File.ReadAllLines(file))
                    {
                        if (string.IsNullOrWhiteSpace(line))
                        {
                            continue;
                        }

                        var csv = line.Split(',');
                        if (csv.Length < PublishedColumnCount ||
                            !DateTime.TryParseExact(csv[1], PeriodFormat, CultureInfo.InvariantCulture,
                                DateTimeStyles.None, out var periodEnd))
                        {
                            malformed++;
                            continue;
                        }

                        var releaseDate = DateTime
                            .ParseExact(csv[0], ReleaseFormat, CultureInfo.InvariantCulture).Date;

                        // The owner of the ticker on the release date. The map file is checked first
                        // because GenerateEquity never returns null for an unknown ticker.
                        var mapFile = _mapFileProvider
                            .Get(new AuxiliaryDataKey(Market.USA, SecurityType.Equity))
                            .ResolveMapFile(ticker, releaseDate);
                        if (!mapFile.Any())
                        {
                            unmapped++;
                            continue;
                        }

                        var security = SecurityIdentifier
                            .GenerateEquity(mapFile.FirstDate, mapFile.FirstTicker, Market.USA);

                        if (!events.TryGetValue(releaseDate, out var rows))
                        {
                            events[releaseDate] = rows = new List<UniverseRow>();
                        }

                        // The per-security row is carried unchanged, so the two views cannot disagree.
                        rows.Add(new UniverseRow(
                            security.ToString(),
                            periodEnd,
                            ParseDecimal(csv[FirstValueColumn]),
                            mapFile.DelistingDate,
                            $"{security},{ticker},{string.Join(",", csv.Skip(1))}"));
                    }
                }
            }

            if (unmapped > 0 || malformed > 0)
            {
                Log.Trace($"SEC13FDownloader.BuildUniverseFiles(): {unmapped} rows skipped, the map files know no " +
                          $"security under their ticker on the release date; {malformed} rows unreadable");
            }

            if (events.Count == 0)
            {
                Log.Trace("SEC13FDownloader.BuildUniverseFiles(): nothing to forward-fill");
                return;
            }

            var universeDirectory = Path.Combine(_destinationDirectory, "universe");
            Directory.CreateDirectory(universeDirectory);

            // Keyed by SecurityIdentifier, the identity that survives a rename. Two quarters are
            // carried per security: a new quarter fills in over about fifty days, so publishing only
            // it would rank names by who filed first, and publishing only the finished one would
            // hide the fresh data. The older leaves once the newer overtakes it in Holders.
            var current = new Dictionary<string, SortedList<DateTime, UniverseRow>>(StringComparer.Ordinal);
            var written = 0;
            var superseded = 0;
            var ignoredAsStale = 0;
            var expiredQuarters = 0;
            var delistedSecurities = 0;
            var rowsWritten = 0L;

            for (var day = events.Keys.First(); day <= events.Keys.Last(); day = day.AddDays(1))
            {
                var oldestLive = OldestLivePeriod(day);

                if (events.TryGetValue(day, out var todays))
                {
                    foreach (var row in todays)
                    {
                        current.TryGetValue(row.SecurityIdentifier, out var periods);

                        // A late row for a retired or expired quarter is not resurrected.
                        if (row.PeriodEnd < oldestLive || periods?.Count > 0 && row.PeriodEnd < periods.Keys[0])
                        {
                            ignoredAsStale++;
                            continue;
                        }

                        if (periods == null)
                        {
                            current[row.SecurityIdentifier] = periods = new SortedList<DateTime, UniverseRow>();
                        }

                        if (periods.ContainsKey(row.PeriodEnd))
                        {
                            superseded++;
                        }

                        periods[row.PeriodEnd] = row;
                        RetireOvertakenQuarters(periods);
                    }
                }

                // Every day, weekends included, so the shelf life does not depend on which weekday
                // a deadline falls on.
                foreach (var security in current.Keys.ToList())
                {
                    var periods = current[security];
                    while (periods.Count > 0 && periods.Keys[0] < oldestLive)
                    {
                        periods.RemoveAt(0);
                        expiredQuarters++;
                    }

                    if (periods.Count == 0)
                    {
                        current.Remove(security);
                    }
                    else if (periods.Values[0].DelistingDate < day)
                    {
                        current.Remove(security);
                        delistedSecurities++;
                    }
                }

                if (day.DayOfWeek == DayOfWeek.Saturday || day.DayOfWeek == DayOfWeek.Sunday || current.Count == 0)
                {
                    continue;
                }

                // Security, then quarter.
                var rows = current
                    .OrderBy(security => security.Key, StringComparer.Ordinal)
                    .SelectMany(security => security.Value.Values.Select(row => row.Line))
                    .ToList();

                File.WriteAllLines(Path.Combine(universeDirectory, $"{day.ToStringInvariant("yyyyMMdd")}.csv"), rows);
                written++;
                rowsWritten += rows.Count;
            }

            Log.Trace($"SEC13FDownloader.BuildUniverseFiles(): forward-filled {written} business days " +
                      $"from {events.Keys.First():yyyy-MM-dd} to {events.Keys.Last():yyyy-MM-dd}, " +
                      $"{current.Count} securities and " +
                      $"{current.Values.Sum(periods => periods.Count)} rows in the last file, " +
                      $"{rowsWritten} rows written, {superseded} restated, " +
                      $"{ignoredAsStale} late rows ignored as stale, {expiredQuarters} quarters expired, " +
                      $"{delistedSecurities} securities delisted");
        }

        /// <summary>
        /// Keeps the newest quarter, and the one before it only while it still holds more managers.
        /// </summary>
        private static void RetireOvertakenQuarters(SortedList<DateTime, UniverseRow> periods)
        {
            while (periods.Count > 1)
            {
                var newest = periods.Values[periods.Count - 1].Holders;
                var previous = periods.Values[periods.Count - 2].Holders;

                if (periods.Count > 2 || previous <= newest)
                {
                    periods.RemoveAt(0);
                    continue;
                }

                break;
            }
        }

        /// <summary>
        /// The oldest quarter still live on a day: the one before the newest finished quarter stays
        /// live until that quarter's 45 day filing deadline passes.
        /// </summary>
        internal static DateTime OldestLivePeriod(DateTime day)
        {
            var latest = QuarterEndBefore(day.Date);
            return day.Date > latest.AddDays(FilingDeadlineDays) ? latest : QuarterEndBefore(latest);
        }

        /// <summary>The last quarter end strictly before a day.</summary>
        private static DateTime QuarterEndBefore(DateTime day)
        {
            return new DateTime(day.Year, (day.Month - 1) / 3 * 3 + 1, 1).AddDays(-1);
        }

        /// <summary>One universe line, with the fields the forward-fill reads kept alongside it.</summary>
        private readonly record struct UniverseRow(
            string SecurityIdentifier, DateTime PeriodEnd, decimal Holders, DateTime DelistingDate, string Line);

        /// <summary>One row of a per-security file, either an increment or the running total.</summary>
        internal sealed class HoldingsRow
        {
            public DateTime Time { get; init; }
            public DateTime PeriodEnd { get; init; }
            public decimal[] Values { get; init; }
            public bool ConfidentialOmitted { get; init; }
        }

        /// <summary>
        /// Folds a security's published rows and this run's increments into the running total per
        /// quarter, each row tagged with its ticker file, in time order. The published rows are
        /// differenced back to increments first, which is exact in decimal, and across every ticker
        /// the security traded under, which is what carries a quarter across a rename.
        /// </summary>
        internal static List<(string Ticker, HoldingsRow Row)> MergeSecurity(
            IEnumerable<(string Ticker, HoldingsRow Row)> published,
            IEnumerable<(string Ticker, HoldingsRow Row)> increments)
        {
            var merged = new Dictionary<(DateTime PeriodEnd, DateTime Time), (string Ticker, HoldingsRow Row)>();

            var shelf = published.ToList();
            var shelfTickers = new Dictionary<(DateTime PeriodEnd, DateTime Time), string>();
            foreach (var (ticker, row) in shelf)
            {
                shelfTickers[(row.PeriodEnd, row.Time)] = ticker;
            }

            foreach (var row in Difference(shelf.Select(entry => entry.Row)))
            {
                var key = (row.PeriodEnd, row.Time);
                merged[key] = (shelfTickers[key], row);
            }

            // Within this run, two increments for one key are two different filings and are summed.
            var fresh = new Dictionary<(DateTime PeriodEnd, DateTime Time), (string Ticker, HoldingsRow Row)>();
            foreach (var (ticker, row) in increments)
            {
                var key = (row.PeriodEnd, row.Time);
                fresh[key] = fresh.TryGetValue(key, out var held) ? (held.Ticker, Add(held.Row, row)) : (ticker, row);
            }

            // Against the published history this run wins: it read the archive again, so its
            // reading of a quarter and release date is the current one, and adding would double it.
            foreach (var (key, entry) in fresh)
            {
                merged[key] = entry;
            }

            return Accumulate(merged.Values.Select(entry => entry.Row))
                .Select(row => (Ticker: merged[(row.PeriodEnd, row.Time)].Ticker, Row: row))
                .OrderBy(entry => entry.Row.Time)
                .ThenBy(entry => entry.Row.PeriodEnd)
                .ToList();
        }

        /// <summary>Reads a per-security file, skipping blank lines.</summary>
        private static IEnumerable<HoldingsRow> ReadRows(string path)
        {
            return File.ReadAllLines(path).Where(line => !string.IsNullOrWhiteSpace(line)).Select(ParseRow);
        }

        /// <summary>
        /// Turns the running total a published file holds back into the increments it was built
        /// from, quarter by quarter and in release order.
        /// </summary>
        internal static IEnumerable<HoldingsRow> Difference(IEnumerable<HoldingsRow> rows)
        {
            foreach (var period in rows.GroupBy(row => row.PeriodEnd))
            {
                var previous = new decimal[ValueColumnCount];
                foreach (var row in period.OrderBy(row => row.Time))
                {
                    var values = new decimal[ValueColumnCount];
                    for (var i = 0; i < ValueColumnCount; i++)
                    {
                        values[i] = row.Values[i] - previous[i];
                        previous[i] = row.Values[i];
                    }

                    // The flag is carried rather than differenced: it is OR'd forward on the way
                    // back, and OR is idempotent, so this round-trips whatever it was set from.
                    yield return new HoldingsRow
                    {
                        Time = row.Time,
                        PeriodEnd = row.PeriodEnd,
                        Values = values,
                        ConfidentialOmitted = row.ConfidentialOmitted
                    };
                }
            }
        }

        /// <summary>
        /// Running-sums the increments within each quarter: every published row states what had been
        /// reported for its quarter as of its own release date.
        /// </summary>
        internal static List<HoldingsRow> Accumulate(IEnumerable<HoldingsRow> increments)
        {
            var cumulative = new List<HoldingsRow>();

            foreach (var period in increments.GroupBy(row => row.PeriodEnd).OrderBy(period => period.Key))
            {
                var running = new decimal[ValueColumnCount];
                var confidential = false;

                foreach (var row in period.OrderBy(row => row.Time))
                {
                    var values = new decimal[ValueColumnCount];
                    for (var i = 0; i < ValueColumnCount; i++)
                    {
                        running[i] += row.Values[i];
                        values[i] = running[i];
                    }

                    // Once a contributing filing withheld positions the quarter's total stays
                    // incomplete, so the flag only ever goes up within a quarter.
                    confidential |= row.ConfidentialOmitted;

                    cumulative.Add(new HoldingsRow
                    {
                        Time = row.Time,
                        PeriodEnd = row.PeriodEnd,
                        Values = values,
                        ConfidentialOmitted = confidential
                    });
                }
            }

            return cumulative;
        }

        /// <summary>Parses one per-security row.</summary>
        private static HoldingsRow ParseRow(string line)
        {
            var csv = line.Split(',');
            var values = new decimal[ValueColumnCount];
            for (var i = 0; i < ValueColumnCount; i++)
            {
                values[i] = ParseDecimal(csv[FirstValueColumn + i]);
            }

            return new HoldingsRow
            {
                Time = DateTime.ParseExact(csv[0], ReleaseFormat, CultureInfo.InvariantCulture),
                PeriodEnd = DateTime.ParseExact(csv[1], PeriodFormat, CultureInfo.InvariantCulture),
                Values = values,
                ConfidentialOmitted = csv[FirstValueColumn + ValueColumnCount] == "1"
            };
        }

        /// <summary>Formats one per-security row, in the layout SEC13FHoldings parses.</summary>
        private static string FormatRow(HoldingsRow row)
        {
            var line = new StringBuilder()
                .Append(row.Time.ToStringInvariant(ReleaseFormat)).Append(',')
                .Append(row.PeriodEnd.ToStringInvariant(PeriodFormat));

            foreach (var value in row.Values)
            {
                line.Append(',').Append(FormatValue(value));
            }

            return line.Append(',').Append(row.ConfidentialOmitted ? '1' : '0').ToString();
        }

        /// <summary>Reports how much of the dataset made it through identity resolution.</summary>
        private void LogResolutionSummary()
        {
            var totalValue = _resolvedValue + _unresolvedValue;
            var coverage = totalValue == 0m ? 0m : 100m * _resolvedValue / totalValue;

            Log.Trace($"SEC13FDownloader.LogResolutionSummary(): {_countedFilers.Count} counted filers " +
                      $"over {_securityIndexes.Count} securities, " +
                      $"working set {GC.GetTotalMemory(false) / (1024 * 1024)} MB");

            Log.Trace($"SEC13FDownloader.LogResolutionSummary(): {_resolvedByCusip} distinct CUSIPs resolved by CUSIP, " +
                      $"{_resolvedByIsin} by constructed ISIN, {_resolvedByTicker} by the N-PORT ticker crosswalk, " +
                      $"{_unresolvedCusips.Count} unresolved, {_malformedCusips} malformed");
            Log.Trace($"SEC13FDownloader.LogResolutionSummary(): {_unresolvedGroups} groups dropped, " +
                      $"{_conflictingTickers} of them, {(totalValue == 0m ? 0m : 100m * _conflictingValue / totalValue).ToStringInvariant("F2")}% " +
                      "of reported value, because the ticker belonged to another security that day, " +
                      $"{coverage.ToStringInvariant("F1")}% of reported value covered, " +
                      $"{_unchangedGroups} resolved groups added nothing new and wrote no row");

            if (_unresolvedCusips.Count > 0)
            {
                Log.Trace($"SEC13FDownloader.LogResolutionSummary(): unresolved sample: " +
                          $"{string.Join(", ", _unresolvedCusips.Take(25))}");
            }

            if (_conflictSample.Count > 0)
            {
                Log.Trace($"SEC13FDownloader.LogResolutionSummary(): ticker conflict sample: {string.Join("; ", _conflictSample)}");
            }
        }

        /// <summary>One table of an archive, where a short row means the layout moved and throws.</summary>
        private static IEnumerable<(Dictionary<string, int> Columns, string[] Fields)> ReadTable(
            ZipArchive zip, Archive archive, string table, params string[] required)
        {
            return SEC13FFiles.ReadTable(zip, archive.Name, table, skipShortRows: false, required);
        }

        /// <summary>
        /// Parses a date as the SEC writes it in these tables, "31-MAR-2026", with the ISO and US
        /// shapes accepted too because the older archives are not perfectly consistent.
        /// </summary>
        private static DateTime ParseSecDate(string value, Archive archive, string column)
        {
            // Some rows carry a time of day the tables do not otherwise use.
            var date = value.Trim().Split(' ')[0];
            if (DateTime.TryParseExact(date, SecDateFormats, CultureInfo.InvariantCulture,
                    DateTimeStyles.None, out var parsed))
            {
                return parsed;
            }

            throw new FormatException(
                $"SEC13FDownloader.ParseSecDate(): {archive.Name} {column} '{value}' matches none of " +
                $"{string.Join(", ", SecDateFormats)}");
        }

        /// <summary>Parses a reported amount, treating a blank as zero.</summary>
        private static decimal ParseDecimal(string value)
        {
            var trimmed = value.Trim();
            if (trimmed.Length == 0)
            {
                return 0m;
            }

            if (!decimal.TryParse(trimmed, NumberStyles.Any, CultureInfo.InvariantCulture, out var parsed))
            {
                throw new FormatException($"SEC13FDownloader.ParseDecimal(): '{value}' is not a number");
            }

            return parsed;
        }

        /// <summary>True for the "Y" the SEC writes in its flag columns.</summary>
        private static bool IsYes(string value)
        {
            return value.Trim().Equals("Y", StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>Formats an amount: whole numbers without a decimal point, everything else invariant.</summary>
        private static string FormatValue(decimal value)
        {
            return value == Math.Truncate(value)
                ? ((long)value).ToStringInvariant()
                : value.ToStringInvariant();
        }

        /// <summary>
        /// True when the ticker can name a file LEAN will ask for. Skips path separators and invalid
        /// file name characters, and the space and '|' a Symbol cannot hold: the map files carry
        /// "ua.c " for Under Armour's class C, and its file could never be read.
        /// </summary>
        internal static bool IsFileNameSafe(string ticker)
        {
            return ticker.IndexOf('/') < 0
                   && ticker.IndexOf('\\') < 0
                   && ticker.IndexOf('|') < 0
                   && !ticker.Any(char.IsWhiteSpace)
                   && ticker.IndexOfAny(Path.GetInvalidFileNameChars()) < 0;
        }

        /// <summary>
        /// Sets the User-Agent the SEC asks automated readers for, from the same config keys the
        /// reports dataset reads, on the first request: a run that never reaches the network, like
        /// the unit tests, does not need them.
        /// </summary>
        private void RequireUserAgent()
        {
            if (_userAgentSet)
            {
                return;
            }

            var companyName = Config.Get("sec-user-agent-company-name");
            var companyEmail = Config.Get("sec-user-agent-company-email");
            if (string.IsNullOrEmpty(companyName) || string.IsNullOrEmpty(companyEmail))
            {
                throw new ArgumentException("The SEC requires a company name and email to download data using " +
                    "automation. Set `sec-user-agent-company-name` and `sec-user-agent-company-email` in the config.");
            }

            _client.DefaultRequestHeaders.TryAddWithoutValidation("User-Agent", string.Join(" ", companyName, companyEmail));
            _userAgentSet = true;
        }

        /// <summary>GETs a URL as text, with retry and backoff.</summary>
        private string GetWithRetry(string url)
        {
            return WithRetry(url, () =>
            {
                using var response = _client.GetAsync(url).GetAwaiter().GetResult();
                response.EnsureSuccessStatusCode();
                return response.Content.ReadAsStringAsync().GetAwaiter().GetResult();
            });
        }

        /// <summary>
        /// Sends a request through the rate gate, retrying with a growing backoff whatever
        /// IsWorthRetrying accepts. The last failure is the one that surfaces.
        /// </summary>
        private T WithRetry<T>(string url, Func<T> request)
        {
            RequireUserAgent();
            for (var attempt = 1; ; attempt++)
            {
                try
                {
                    _rateGate.WaitToProceed();
                    return request();
                }
                catch (Exception err) when (attempt < MaxRetries && IsWorthRetrying(err))
                {
                    Log.Trace($"SEC13FDownloader.WithRetry(): {url} retry {attempt}/{MaxRetries} after: {err.Message}");
                    Thread.Sleep(TimeSpan.FromSeconds(2 * attempt));
                }
            }
        }

        /// <summary>
        /// True when a failed request is worth asking again: transport errors, server errors and
        /// throttling. A 403 or a 404 from the SEC does not change on a retry.
        /// </summary>
        private static bool IsWorthRetrying(Exception error)
        {
            var status = (error as HttpRequestException)?.StatusCode;

            return status == null
                   || (int)status >= 500
                   || status == HttpStatusCode.TooManyRequests
                   || status == HttpStatusCode.RequestTimeout;
        }

        /// <summary>Disposes unmanaged resources.</summary>
        public void Dispose()
        {
            _client.DisposeSafely();
            _rateGate.DisposeSafely();

            // A run that failed half way leaves its staging behind, and nothing else ever reads it.
            try
            {
                if (Directory.Exists(_stagingDirectory))
                {
                    Directory.Delete(_stagingDirectory, recursive: true);
                }
            }
            catch (IOException err)
            {
                Log.Error(err, $"SEC13FDownloader.Dispose(): could not delete {_stagingDirectory}");
            }

            GC.SuppressFinalize(this);
        }
    }
}
