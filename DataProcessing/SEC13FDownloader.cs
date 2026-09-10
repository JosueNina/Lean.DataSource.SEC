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
    /// Downloads the SEC Form 13F structured data sets and converts them to LEAN's per-security CSV
    /// format plus one universe file per release date. Selected by "dataset-name": "13f".
    ///
    /// The SEC publishes one zip per filing-receipt window: quarterly archives from 2013Q2 to
    /// 2023Q4, rolling three-month windows since. Each archive carries eight tab separated tables;
    /// this processor reads SUBMISSION (who filed, when, for which quarter), COVERPAGE and
    /// SUMMARYPAGE (the confidential treatment flags) and INFOTABLE (the positions themselves).
    ///
    /// Output: {destination}/13f/{ticker}.csv and {destination}/13f/universe/{yyyyMMdd}.csv
    ///
    /// Modes: full history (no env), incremental (QC_DATAFLEET_DEPLOYMENT_DATE, the production job),
    /// which reads only the archive whose window covers the deployment date and folds it into the
    /// published history.
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

        // The SEC asks for a descriptive User-Agent rather than a key, and rate limits to ten
        // requests a second.
        private const string UserAgentHeader = "QuantConnect Dataset Processing (data@quantconnect.com)";

        private const int MaxRetries = 5;

        // A 13F-NT is a notice that the manager's holdings are reported on someone else's filing. It
        // carries no INFOTABLE at all, so it is not a zero position and must not become one.
        private const string NoticeSubmissionTypePrefix = "13F-NT";

        // A 13F-HR/A amends a report the same manager already made for the same quarter, and about
        // four percent of filings are amendments. Most are RESTATEMENTs, which replace the whole
        // report, so adding their lines on top of the original counts the manager's positions
        // twice: on Apple's March 2026 quarter that was 993 million shares, 9.6 percent of the
        // total. An amendment's lines therefore count only for a security and quarter the manager
        // had not reported before, which is also exactly what a NEW HOLDINGS amendment carries.
        // The cost is that a restatement's corrections to a position already reported are not
        // applied: the original figure stands. See ReadInfoTable and AdmitAmendment.
        private const string AmendmentSubmissionTypeSuffix = "/A";

        // EDGAR stops accepting same-day filings at 17:30 ET, so everything stamped with a given
        // FILING_DATE was disseminated by then. Publishing the point at that time keeps it after the
        // close of the day it became public, which is what the lag in this dataset is worth paying
        // for: it can never be read earlier than it existed.
        private static readonly TimeSpan ReleaseTimeOfDay = new(17, 30, 0);

        private const string ReleaseFormat = "yyyyMMdd HH:mm";
        private const string PeriodFormat = "yyyyMMdd";

        // The published row is release, period, then the numeric columns and the confidential flag.
        // Spelled out here because both the writer and the merge that differences the file back to
        // increments index into it, and they cannot be allowed to disagree.
        private const int FirstValueColumn = 2;
        private const int ValueColumnCount = 8;
        private const int PublishedColumnCount = FirstValueColumn + ValueColumnCount + 1;

        // How many N-PORT quarters feed the CUSIP to ticker crosswalk. Four covers a year, which is
        // enough for every security still trading; a name that stopped trading before N-PORT began
        // in late 2019 is not reachable this way at any depth.
        private const int NPortQuartersToFold = 4;

        /// <summary>Days after a quarter end that managers have to file their 13F for it.</summary>
        private const int FilingDeadlineDays = 45;

        // First FILING_DATE reported in whole dollars. Filings before this date report VALUE in
        // thousands and are scaled up so the published series has one unit end to end. See the
        // measurement in the aggregation loop.
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
        private readonly string _crosswalkCacheDirectory;
        private readonly DateTime? _deploymentDate;

        private readonly HttpClient _client;
        private readonly RateGate _rateGate = new(10, TimeSpan.FromSeconds(1));

        private readonly IMapFileProvider _mapFileProvider;
        private readonly SecurityDefinitionSymbolResolver _symbolResolver;

        /// <summary>
        /// CUSIP and filing date to security. Every miss walks the entire security database and the
        /// same CUSIP is asked for on every filing date of every quarter, so this is what makes the
        /// full pass finish. Misses are cached as a null entry too: they are the lookups that scan
        /// the list to the end.
        ///
        /// The date belongs in the key because the resolution is point in time and tickers get
        /// reused. Keyed by CUSIP alone, whichever filing date asked first froze the answer for the
        /// whole run, and a full pass asks from 2013 onwards: Meta's CUSIP resolved through a 2013
        /// filing, when META named nothing, so 19.3 billion dollars of holdings went elsewhere and
        /// meta.csv shipped three debt-only rows. Cleared between archives, which keeps the cache
        /// the size of one window rather than of thirteen years.
        /// </summary>
        private readonly Dictionary<(string Cusip, DateTime Date), SecurityIdentifier> _resolvedCusips = new();

        /// <summary>CUSIPs already tallied in the resolution summary, so its totals stay per CUSIP.</summary>
        private readonly HashSet<string> _countedCusips = new(StringComparer.Ordinal);

        /// <summary>Map file per security, so the point-in-time ticker costs one lookup per date.</summary>
        private readonly Dictionary<SecurityIdentifier, MapFile> _mapFiles = new();

        /// <summary>
        /// Accessions already folded in. The archives are keyed by filing-receipt window and do not
        /// overlap, but a submission counted twice would emit a second row for the same
        /// (security, period, release) key with different values, which the dedup could not collapse.
        /// </summary>
        private readonly HashSet<string> _processedAccessions = new(StringComparer.Ordinal);

        /// <summary>
        /// Increment rows waiting to be staged, flushed once per archive to bound memory. Keyed by
        /// security and then by (period, release), because several CUSIPs reach the same security:
        /// a debt CUSIP, a share class the map files fold in, or an issuer that simply carries more
        /// than one. Their contributions have to be summed here, while the whole day is still in
        /// memory. Measured on Apple's March 2026 quarter: three of the forty-three release dates
        /// carry a second CUSIP, worth 152,560 shares. Each row keeps the ticker the security traded
        /// under that day, which is the file it is finally written to.
        /// </summary>
        private readonly Dictionary<string, Dictionary<(DateTime PeriodEnd, DateTime Time), (string Ticker, HoldingsRow Row)>>
            _pendingSecurityRows = new(StringComparer.Ordinal);

        /// <summary>
        /// Where increments wait, one file per security, between the archive that produced them and
        /// the finalize pass. Keyed by security rather than by ticker because the running total per
        /// quarter belongs to the security: a quarter still filing when a company is renamed has to
        /// carry on in the new ticker's file from where the old one stopped, not restart at zero.
        /// Kept outside the output folder, since everything there is published.
        /// </summary>
        private readonly string _stagingDirectory =
            Path.Combine(Path.GetTempPath(), "sec-13f-staging", Guid.NewGuid().ToString("N"));

        /// <summary>Every security with increments staged, which is every security this run touched.</summary>
        private readonly HashSet<string> _stagedSecurities = new(StringComparer.Ordinal);

        /// <summary>
        /// Amendments admitted in this archive, as (filer key, filing date). One amendment can name
        /// several CUSIPs of the same security; the first marks the manager counted and the rest of
        /// the same filing still have to be let in. Cleared per archive, since a filing date never
        /// spans two.
        /// </summary>
        private readonly HashSet<(long FilerKey, int Stamp)> _admittedAmendments = new();

        /// <summary>Ticker and date to the security the map files say owns that ticker then, per archive.</summary>
        private readonly Dictionary<(string Ticker, DateTime Date), string> _tickerOwners = new();

        private long _conflictingTickers;
        private long _unchangedGroups;

        /// <summary>
        /// CUSIP to ticker, from the SEC's N-PORT filings. Built on first use rather than at
        /// construction: the archives may all resolve through the security database, and building
        /// it downloads a few hundred megabytes per quarter folded in.
        /// </summary>
        private Dictionary<string, string> _tickerCrosswalk;

        private Dictionary<string, string> TickerCrosswalk => _tickerCrosswalk ??=
            SEC13FTickerCrosswalk.Load(_client, _crosswalkCacheDirectory, NPortQuartersToFold);

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

            // Archives are ~100 MB each and the N-PORT crosswalk takes 1.8 GB to rebuild, so both are
            // kept where the next run can find them. That is the raw folder: the job syncs it to the
            // archive store after every run, which makes it the only place in the container that
            // outlives the run. The system temp folder does not, so a cache there is rebuilt from
            // the SEC on every run. Without a raw folder, which is how the unit tests construct
            // this, the caches fall back to temp and the processed folder as before.
            var rawCache = rawDataDirectory == null
                ? null
                : Path.Combine(rawDataDirectory, SEC13FHoldings.ReportFolder);
            _archiveCacheDirectory = rawCache == null
                ? Path.Combine(Path.GetTempPath(), "sec-13f-archives")
                : Path.Combine(rawCache, "archives");
            _crosswalkCacheDirectory = rawCache ?? _processedDataDirectory;
            Directory.CreateDirectory(_archiveCacheDirectory);
            Directory.CreateDirectory(_crosswalkCacheDirectory);

            _client = new HttpClient { Timeout = TimeSpan.FromMinutes(30) };
            _client.DefaultRequestHeaders.UserAgent.ParseAdd(UserAgentHeader);

            // Zip, not disk. LocalDiskMapFileProvider only enumerates loose .csv files in
            // equity/usa/map_files, but `lean data download --dataset "US Equity Security Master"`
            // delivers map_files_<yyyyMMdd>.zip, so on any machine seeded that way the disk
            // provider sees a couple of dozen securities out of 30,740 and resolves almost
            // nothing. It fails silently: the run finishes and writes a dataset covering the
            // handful of tickers that happened to be loose. The zip provider walks back from
            // yesterday to find the newest dated archive, which is what the download produces.
            // Point-in-time behaviour is identical either way, since the mapping still comes from
            // MapFile.GetMappedSymbol(tradingDate).
            _mapFileProvider = new LocalZipMapFileProvider();
            _mapFileProvider.Initialize(new DefaultDataProvider());

            // Reads symbol-properties/security-database.csv from the same LEAN data folder the map
            // files come from, so the job needs nothing new mounted. The resolver pulls its map file
            // provider out of the Composer rather than taking one, so it has to be registered first
            // or the constructor dereferences null.
            var dataProvider = new DefaultDataProvider();
            Composer.Instance.AddPart<IDataProvider>(dataProvider);
            Composer.Instance.AddPart(_mapFileProvider);
            _symbolResolver = SecurityDefinitionSymbolResolver.GetInstance(dataProvider);

            // security-database.csv is not distributable, and without it the resolver answers null to
            // every lookup and the run writes an empty dataset while reporting success. Say so at
            // startup rather than at the end: the processor is expected to run where the file exists.
            var securityDatabasePath = Path.Combine(
                Globals.GetDataFolderPath("symbol-properties"), "security-database.csv");
            if (!File.Exists(securityDatabasePath))
            {
                Log.Error($"SEC13FDownloader(): {securityDatabasePath} is missing. It is not distributable, " +
                          "and without it no CUSIP or ISIN resolves and the run produces no data.");
            }
        }

        /// <summary>Runs the download/convert. Returns true on success.</summary>
        public bool Run()
        {
            try
            {
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
                return true;
            }
            catch (Exception err)
            {
                Log.Error(err, "SEC13FDownloader.Run(): failed");
                return false;
            }
        }

        /// <summary>
        /// Stops an incremental run that cannot see the published history.
        ///
        /// The destination arrives empty on every deployment, so the published copy is the only
        /// history an incremental run has. When it is missing, wrong or unmounted the merge finds
        /// nothing to fold in and quietly degrades into a rebuild from scratch: thirteen years of
        /// per-security history are republished as the single three month window just read, every
        /// universe file is rebuilt from that window, and the run returns success. No row count
        /// catches it, because the files come out the right shape. A full pass has no history to
        /// read and is exempt.
        /// </summary>
        internal void RequirePublishedHistoryForIncrementalRun()
        {
            if (_deploymentDate == null)
            {
                return;
            }

            // The crosswalk keeps its cache alongside the security files without being one.
            var published = Directory.Exists(_processedDataDirectory)
                ? Directory.EnumerateFiles(_processedDataDirectory, "*.csv").Count(file =>
                    !Path.GetFileName(file).Equals(SEC13FTickerCrosswalk.CacheFileName, StringComparison.OrdinalIgnoreCase))
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
        /// Stops an incremental run that cannot see the filers earlier runs counted.
        ///
        /// Without the state file every manager in tonight's archive looks new, so a name already
        /// carrying six thousand holders would be handed six thousand more. Nothing downstream
        /// notices: the file has the right shape, the row count is unchanged, and only the one
        /// column is wrong. That is the same silent degradation the published history guard above
        /// exists for, which is why this fails the run rather than rebuilding what it cannot read.
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
        /// Scrapes the data sets page for every published archive, ordered oldest first. The list is
        /// read rather than held here because a new window arrives every quarter and a hard-coded
        /// list would silently stop at whatever was published the day it was written.
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
        /// Picks the archives a run reads: all of them for a full pass, and for an incremental pass
        /// the one whose window covers the deployment date, falling back to the newest published
        /// archive when the deployment date runs ahead of what the SEC has released.
        /// </summary>
        private List<Archive> SelectArchives(List<Archive> archives)
        {
            if (_deploymentDate == null)
            {
                Log.Trace("SEC13FDownloader.SelectArchives(): full history");
                return archives;
            }

            var date = _deploymentDate.Value.Date;
            var covering = archives.Where(x => x.Start <= date && date <= x.End).ToList();
            if (covering.Count > 0)
            {
                Log.Trace($"SEC13FDownloader.SelectArchives(): incremental for {date:yyyy-MM-dd}");
                return covering;
            }

            var newest = archives[archives.Count - 1];
            Log.Trace($"SEC13FDownloader.SelectArchives(): incremental for {date:yyyy-MM-dd} falls outside every " +
                      $"published window, reading the newest archive {newest.Name} instead");
            return new List<Archive> { newest };
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
                // The published archives are keyed by non-overlapping filing-receipt windows, so
                // each one holds filings no other did. Zero means the table was empty, or held
                // nothing but notices, or the windows began to overlap. Carrying on would publish a
                // history with this window missing from it and still report success.
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

        /// <summary>
        /// Downloads an archive, reusing a copy already on disk. The body is written to a ".part"
        /// file and moved into place only once the transfer completed, so an interrupted run cannot
        /// leave a truncated archive that the next run would happily read.
        /// </summary>
        private string DownloadArchive(Archive archive)
        {
            var path = Path.Combine(_archiveCacheDirectory, archive.Name);
            if (File.Exists(path))
            {
                Log.Trace($"SEC13FDownloader.DownloadArchive(): {archive.Name} already on disk");
                return path;
            }

            var temporaryPath = path + ".part";
            for (var attempt = 1; attempt <= MaxRetries; attempt++)
            {
                try
                {
                    _rateGate.WaitToProceed();
                    using (var response = _client
                               .GetAsync(archive.Url, HttpCompletionOption.ResponseHeadersRead)
                               .GetAwaiter().GetResult())
                    {
                        response.EnsureSuccessStatusCode();
                        using var source = response.Content.ReadAsStreamAsync().GetAwaiter().GetResult();
                        using var destination = new FileStream(temporaryPath, FileMode.Create, FileAccess.Write);
                        source.CopyTo(destination);
                    }

                    File.Move(temporaryPath, path, overwrite: true);
                    Log.Trace($"SEC13FDownloader.DownloadArchive(): {archive.Name}, {new FileInfo(path).Length} bytes");
                    return path;
                }
                catch (Exception err) when (attempt < MaxRetries && IsWorthRetrying(err))
                {
                    Log.Trace($"SEC13FDownloader.DownloadArchive(): {archive.Name} retry {attempt}/{MaxRetries} after: {err.Message}");
                    Thread.Sleep(TimeSpan.FromSeconds(2 * attempt));
                }
            }

            throw new IOException($"SEC13FDownloader.DownloadArchive(): {archive.Url} failed after {MaxRetries} attempts");
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
        /// Marks the filings that withheld positions under confidential treatment, which is the
        /// SUMMARYPAGE ISCONFIDENTIALOMITTED column: the filing is incomplete by design and the
        /// withheld positions surface in a later release.
        ///
        /// COVERPAGE also carries CONFDENIEDEXPIRED, and it is deliberately NOT read here. It
        /// means the opposite: confidential treatment was denied or has expired, so positions
        /// previously withheld are being disclosed in this filing. Raising the same flag for both
        /// would tell an algorithm that the most complete filings are the incomplete ones.
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
        /// The reported lines sharing one (CUSIP, period, release). The measures are the ORIGINAL
        /// filings only; each amending filer's lines are held apart in Amendments until
        /// AdmitAmendment knows whether that manager had already reported the security.
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
        /// Every (security, quarter, filer) already counted, so a filer reaches Holders once no
        /// matter how many filings it makes. Packed into a long rather than kept as a set per
        /// quarter: the published history holds 57.4 million of these, and one HashSet per
        /// (security, quarter) pair costs several times what a single flat set does.
        ///
        /// Layout, 62 bits: security index in 42..61, period index in 32..41, CIK in 0..31.
        /// Both indexes are dense and assigned on first sight, so a PERIODOFREPORT that is not a
        /// quarter end gets its own slot instead of colliding with the quarter it falls in.
        ///
        /// The value is the filing date the filer was first counted on, as yyyyMMdd. It is what
        /// makes re-reading an archive idempotent: the run drops every filer it first counted inside
        /// the window it is about to read, so recounting reproduces the same increments instead of
        /// finding all of them already known and publishing zeros. See PruneFilerState.
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
        /// them counted as it goes. This is the whole of what Holders means: a manager reaches the
        /// count once, on the first day it reports the security for that quarter, no matter how many
        /// filings it makes, how many CUSIPs of the same issuer it names, or whether the filing that
        /// first names the security is an original or an amendment.
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
        /// Forgets every filer first counted on a filing date these archives cover, so reading them
        /// again recounts from the same starting point.
        ///
        /// Without this an incremental run is destructive rather than idempotent. The rolling window
        /// covers three months and the job reads it every night, so the second night finds every
        /// filer of that window already counted, publishes an increment of zero for each of its
        /// days, and the merge lets the fresh reading win: a quarter that carried six thousand
        /// holders drops to none. The file keeps its shape and only that one column moves, so
        /// nothing downstream would have caught it.
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
        /// The counted filers, carried next to the security files so the next run can tell a filer
        /// it has already seen from a new one.
        ///
        /// Holders is a distinct count, and a distinct count cannot be recovered from the published
        /// totals the way the summed measures can: Difference turns a running total back into the
        /// per day increments it was built from, but nothing in the published file says WHICH
        /// managers those increments were. An incremental run reads one archive, so without this
        /// file it cannot know whether tonight's amendment comes from a manager already in the
        /// count. Zipped because it is the largest thing this dataset ships.
        /// </summary>
        private const string FilerStateFileName = "filers.zip";

        private const string FilerStateEntryName = "filers.csv";

        /// <summary>
        /// The stamp written on the state entry, fixed so the archive is byte reproducible. Any
        /// constant would do; this is the date the rule that needs the file was measured.
        /// </summary>
        private static readonly DateTimeOffset FilerStateTimestamp =
            new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

        /// <summary>
        /// Loads the counted filers an earlier run published. A full history run rebuilds every
        /// count from the archives themselves, so it deliberately starts from an empty state and
        /// ignores whatever is on the shelf.
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
        /// Publishes the counted filers for the next run, sorted by security, quarter and CIK. The
        /// filers of one security and quarter sit next to each other, which makes the file compress
        /// to a fraction of its size.
        ///
        /// The order comes from the values, not from the packed keys. A key's indexes are assigned
        /// in the order securities and quarters were first seen, which a full run and an
        /// incremental one reading this file back do not share: in key order the same 58 million
        /// filers came out in a different order and the archive changed bytes on a night that
        /// changed nothing. Each index is swapped for its rank, so the sort stays one over longs.
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

            var temporaryPath = path + ".tmp";
            using (var file = File.Create(temporaryPath))
            using (var zip = new ZipArchive(file, ZipArchiveMode.Create))
            {
                var entry = zip.CreateEntry(FilerStateEntryName, CompressionLevel.Optimal);

                // A zip stamps each entry with the moment it was written, so the same content would
                // hash differently on every run and the delivery archive would look changed when
                // nothing had. The per-security files are byte reproducible; this one has to be too.
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
            }

            File.Move(temporaryPath, path, overwrite: true);
            Log.Trace($"SEC13FDownloader.WriteFilerState(): wrote {keys.Length} counted filers to {path}, " +
                      $"{new FileInfo(path).Length / (1024 * 1024)} MB");
        }

        /// <summary>The group a reported line belongs to.</summary>
        internal readonly record struct HoldingKey(string Cusip, DateTime Period, DateTime FilingDate);

        /// <summary>
        /// Streams INFOTABLE.tsv and sums every reported line into its group.
        ///
        /// The rule is the raw sum: no dedup by INVESTMENTDISCRETION and no dropping of lines that
        /// name an OTHERMANAGER. It was measured against published institutional ownership
        /// (MSFT 71.6%, AAPL 63.1%, NVDA 66.0%, inside or just under what providers publish per name); both of the
        /// obvious dedup rules understate the total by roughly three times.
        /// </summary>
        internal Dictionary<HoldingKey, Holding> ReadInfoTable(
            ZipArchive zip, Archive archive, Dictionary<string, Submission> submissions)
        {
            var holdings = new Dictionary<HoldingKey, Holding>();

            foreach (var (columns, fields) in ReadTable(zip, archive, "INFOTABLE.tsv",
                         "ACCESSION_NUMBER", "CUSIP", "VALUE", "SSHPRNAMT", "SSHPRNAMTTYPE", "PUTCALL",
                         "VOTING_AUTH_SOLE", "VOTING_AUTH_SHARED"))
            {
                if (!submissions.TryGetValue(fields[columns["ACCESSION_NUMBER"]], out var submission))
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

                // Holders counts every manager that reported the security at all, options included,
                // not only the ones contributing to Shares. An original filing's lines go straight
                // into the group. An amendment's are held apart per filer, because only the filer
                // state can say whether this manager already reported the security for the quarter,
                // possibly in an earlier archive, and a restatement of a position already counted
                // must not be summed on top of it. See AdmitAmendment.
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

                // VALUE changed unit with the 2023 filings: through 2022 it is reported in
                // THOUSANDS of dollars, from 2023 in whole dollars. Measured on the real archives
                // as the median VALUE / SSHPRNAMT of share lines, which is a price per share:
                //
                //     NOV-2022 0.0460 · DEC-2022 0.0759   -> thousands
                //     FEB-2023 39.86  · MAR-2023 36.50    -> whole dollars
                //     2013q2   0.0373            2023q4 50.46
                //
                // Left raw this puts a 1000x step in the middle of the history and breaks every
                // cross-period comparison, so pre-2023 filings are scaled up and the whole series
                // is published in whole dollars. The cut is on FILING_DATE, not on the reported
                // period, because the rule changed for filings made from January 2023 onwards.
                //
                // Never sum VALUE without the share filter below: unfiltered it totals $70.1
                // trillion for a single quarter.
                var value = ParseDecimal(fields[columns["VALUE"]]);
                if (submission.FilingDate < ValueInWholeDollarsFrom)
                {
                    value *= 1000m;
                }

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

                if (shareType.Equals("PRN", StringComparison.OrdinalIgnoreCase))
                {
                    holding.PrincipalValue += value;
                    continue;
                }

                if (!shareType.Equals("SH", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                holding.Shares += amount;
                holding.HoldingValue += value;
                holding.VotingSole += ParseDecimal(fields[columns["VOTING_AUTH_SOLE"]]);
                holding.VotingShared += ParseDecimal(fields[columns["VOTING_AUTH_SHARED"]]);
            }

            return holdings;
        }

        /// <summary>
        /// Resolves each group to a security and queues its row. Groups that resolve to nothing are
        /// dropped: they are the foreign issuers and the securities LEAN carries no definition for,
        /// and the measured coverage of what remains is 88.9% of value.
        ///
        /// The row queued here is the INCREMENT, only the filings received that day. The finalize
        /// pass turns it into the running total per quarter that gets published, because that
        /// transform also has to reach the increments earlier runs already wrote.
        /// </summary>
        private void EmitHoldings(Dictionary<HoldingKey, Holding> holdings)
        {
            // Filing date order, because a filer counts on the first day it reports the security and
            // the dictionary hands its groups back in whatever order it stored them. Without this
            // the same archive would credit the count to an arbitrary one of the days a manager
            // appears on, and two runs over the same archive could disagree.
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

                    // The row lands in the ticker's file, and everything that reads that file back,
                    // the universe and LEAN itself, takes it to be the security the map files say
                    // owns the ticker on that day. When that is some other security the two would be
                    // mixed in one file, so the group is dropped instead.
                    if (TickerOwner(ticker, key.FilingDate) != security.ToString())
                    {
                        _conflictingTickers++;
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

                    // A day that brought nothing but restatements of positions already counted
                    // changes no total, and a data point identical to the one before it is not a
                    // release.
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
        /// filer is new to the count. They are added only when the manager had not reported the
        /// security for the quarter yet, which is what a NEW HOLDINGS amendment carries and what a
        /// restatement carries for a security its original left out. A restatement of a position
        /// already counted is not summed on top of it; see AmendmentSubmissionTypeSuffix.
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
        /// The security the map files say trades under a ticker on a date, as its identifier string,
        /// or null when none does. This is the reading everything downstream applies to a ticker
        /// file, so writing and reading agree on whose rows a file holds.
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
        /// Resolves a CUSIP to a security, point in time. The SEC ships the nine character CUSIP
        /// including its check digit and LEAN stores the eight character body, so the check digit
        /// comes off first. When that misses, the US ISIN is built arithmetically from the same
        /// CUSIP and tried in turn, which lifts the measured coverage from 76.1% to 88.9% of value.
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

            // Step 1, the identifier the SEC actually gives us. LEAN stores the CUSIP without its
            // check digit, so the ninth character has to come off. Verified in a research notebook
            // against the real security database: the nine character form resolves 0 of 30 of the
            // largest holdings, the eight character form resolves 26.
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
                // Step 2, the US ISIN built arithmetically from the same CUSIP. It reaches issuers
                // whose CUSIP column is blank in the security database, and in the same notebook it
                // resolved 28 of those 30 where the CUSIP resolved 26.
                symbol = _symbolResolver.ISIN(BuildUnitedStatesIsin(cusip), tradingDate);
                if (symbol != null && firstSighting)
                {
                    _resolvedByIsin++;
                }
            }

            // Step 3, the ticker the SEC's own N-PORT filings report for this CUSIP, taken back to
            // a Symbol through the map files. This is the step that does not need the security
            // database at all, and it is the one that reaches Alphabet, every other issuer missing
            // from that file, and the CINS foreign issuers the constructed ISIN cannot represent.
            // See SEC13FTickerCrosswalk for the measured coverage.
            if (symbol == null)
            {
                var identifier = ResolveThroughTicker(cusip, tradingDate);
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
        /// Brings a reported CUSIP to the nine character form, or returns null when it cannot be
        /// read as one.
        ///
        /// Most rows are already nine characters. A minority are shorter, and the full history has
        /// 12,372 of them, arriving for two different reasons that need opposite repairs:
        ///
        ///   "37833100" is Apple's 037833100 with the leading zero eaten, presumably by a
        ///              spreadsheet that treated the identifier as a number.
        ///   "46428722" is the iShares Aggregate Bond fund, whose CUSIP is 464287226. Here it is
        ///              the CHECK DIGIT that is missing, not a leading zero.
        ///
        /// Padding the second case with a zero would silently invent a different security, so the
        /// two are told apart by the check digit itself, which is computable from the body. A short
        /// value is read as a truncated leading zero only when the padded result checks out;
        /// otherwise it is read as an eight character body and completed with its own check digit.
        /// A value holding a character no CUSIP can contain is rejected rather than completed.
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
                        // Not a character a CUSIP can hold. There is no check digit to compute and
                        // no sentinel value that could not be mistaken for one, so the answer is
                        // that there is no answer.
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
        /// Resolves a CUSIP through the N-PORT ticker crosswalk and the map files.
        ///
        /// The map file has to be checked before the identifier is built.
        /// <see cref="SecurityIdentifier.GenerateEquity(string, string, bool, IMapFileProvider, DateTime?)"/>
        /// falls back to the ticker it was handed and the default date when no map file exists, so
        /// it never returns null and an unknown ticker would otherwise become a plausible looking
        /// Symbol with no data behind it.
        /// </summary>
        private SecurityIdentifier ResolveThroughTicker(string cusip, DateTime tradingDate)
        {
            if (!TickerCrosswalk.TryGetValue(cusip, out var ticker))
            {
                return null;
            }

            var mapFile = _mapFileProvider
                .Get(new AuxiliaryDataKey(Market.USA, SecurityType.Equity))
                .ResolveMapFile(ticker, tradingDate);

            return mapFile.Any()
                ? SecurityIdentifier.GenerateEquity(mapFile.FirstDate, mapFile.FirstTicker, Market.USA)
                : null;
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
        /// The ticker a security traded under on a date, or null when its map file does not cover the
        /// date. The map file is held per security, so a rename splits the rows across the files the
        /// reader will look for, one per ticker.
        ///
        /// There is deliberately no fallback to the last known ticker. The map file ends at the
        /// delisting and managers keep reporting a dead CUSIP for years, so the fallback wrote those
        /// filings under a ticker that could belong to another company by then: DirecTV, delisted in
        /// 2015, sat in the May 2026 universe, and gold.csv mixed the holders of two companies. A row
        /// dated after the delisting is one LEAN would never read anyway.
        /// </summary>
        internal string ResolveTicker(SecurityIdentifier security, DateTime tradingDate)
        {
            var ticker = MapFileOf(security)?.GetMappedSymbol(tradingDate, null);
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
        /// Appends the archive's increments to the staging file of their security. They are appended
        /// rather than merged here because merging on every archive would re-read and re-sort the
        /// whole dataset fifty-three times; the merge happens once, in the finalize pass.
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
        /// files, which is what the dataset publishes.
        ///
        /// The total is kept per security, not per file: a quarter still filing when a company is
        /// renamed carries on in the new ticker's file from where the old one stopped. An
        /// incremental run folds in the published history of every security it touched, from every
        /// ticker the security ever traded under, and a ticker file it rewrites keeps the rows of
        /// any other security that held the ticker at another time.
        ///
        /// The merge key is (period, release), so a restatement filed later ADDS a row instead of
        /// overwriting the one that was public at the time. Rows are deterministic, so re-running
        /// the same day only re-derives lines the set already holds.
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

                // Time order, which is not the order Accumulate() hands rows back in: LEAN's
                // SubscriptionDataReader drops a point whose timestamp moves backwards, and it does
                // so silently, so a file sorted any other way loses rows with nothing in the log to
                // show for it. Ties keep the older quarter first, the order the filings arrived in.
                File.WriteAllLines(
                    Path.Combine(_destinationDirectory, $"{ticker}.csv"),
                    rows.OrderBy(row => row.Time).ThenBy(row => row.PeriodEnd).Select(FormatRow));
            }

            // What was read off the shelf is stated, not assumed. A merge against an empty history
            // does not fail, it silently becomes a rebuild from scratch, and the file still comes
            // out the right shape, so the row count is the only thing that tells the two apart.
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
        /// Rebuilds the universe files from the per-security files, which by now hold the finalized
        /// running totals, then forward-fills the result across business days. Reading the published
        /// series back rather than emitting universe rows alongside it is what keeps the two views of
        /// the dataset from drifting apart, and it is how the FINRA processor builds its universe.
        ///
        /// The forward-fill is not cosmetic. A universe file has to answer "what is the state of
        /// the whole cross-section on this date", not "who filed today": a quiet filing day would
        /// otherwise leave a universe of three names and any ranking built on it would be
        /// meaningless. This is what FINRA does for short interest.
        ///
        /// The carry-forward rule is NOT the FINRA one. FINRA keeps the newest release per
        /// security, which is safe when reports arrive on schedule. 13F amendments arrive years
        /// late (the measured worst case is 6,596 days), so newest-release-wins would let a 2018
        /// amendment landing in 2026 overwrite the 2026 state with stale figures. A row therefore
        /// only replaces the one held when its PeriodEnd is greater than or equal to the held
        /// PeriodEnd: equal supersedes, because that is a restatement of the same quarter, and
        /// older is ignored for carry-forward purposes even though it is the more recent release.
        /// The row still exists in its own release-date file, so nothing is lost.
        ///
        /// Nor is anything carried forever. A quarter leaves once it is older than
        /// OldestLivePeriod, and a security leaves after the delisting date of its map file:
        /// without that, a security nobody reports any more sat in every file with its last
        /// quarter, back to 2013 for some of them.
        /// </summary>
        internal void BuildUniverseFiles()
        {
            // Release date -> the rows that became public that day. An incremental run reads the
            // published history as well, or it would forward-fill a single window and drop every
            // security that has not filed since. This run's copy comes first and wins: for a
            // ticker it touched, that file already carries the published history folded in, and
            // the published copy is the stale half of the pair. A full run rebuilds every file, so
            // whatever the shelf holds is the history being replaced and is not read.
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
                    // The crosswalk keeps its cache in the published folder so a rerun does not
                    // re-download gigabytes of N-PORT. It sits next to the security files without
                    // being one.
                    var name = Path.GetFileName(file);
                    if (name.Equals(SEC13FTickerCrosswalk.CacheFileName, StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }

                    var ticker = Path.GetFileNameWithoutExtension(name).ToUpperInvariant();
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

                        // Point in time, on the release date, so a security that was renamed later
                        // still gets the identifier it had when the filing became public.
                        //
                        // The map file is checked first, exactly as ResolveThroughTicker does.
                        // SecurityIdentifier.GenerateEquity never returns null: with no map file it
                        // falls back to the ticker it was handed and the default date, so an unknown
                        // ticker becomes a plausible looking Symbol with no data behind it. The
                        // published folder is walked here too, and it holds securities this run never
                        // resolved, so the guard cannot be skipped on the grounds that EmitHoldings
                        // already applied one.
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

                        // Everything from PeriodEnd on is carried across unchanged, so the universe
                        // row and the per-security row cannot report different numbers. The three
                        // fields the forward-fill reads are kept alongside the line, so the row is
                        // split once here rather than again on every comparison downstream.
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

            // Keyed by SecurityIdentifier, not by ticker: the SID is the stable identity across a
            // rename, and the row already carries the ticker the security traded under on its own
            // release date.
            //
            // Two quarters are carried per security, not one, and this is the important part. A
            // quarter fills in over roughly fifty days, so on the second of January a security has
            // a brand new quarter with five managers in it and a finished one with two thousand.
            // Publishing only the newest would throw the finished quarter away and leave six weeks
            // of every year where the cross-section ranks by who filed earliest rather than by how
            // broadly a name is held. Publishing only the finished one would hide the fresh data.
            //
            // ExtractAlphaTrueBeats has the same problem with analyst estimates for a fiscal period
            // that has not reported yet, and its answer is not to choose: it ships every live period
            // as its own point, each carrying its own FiscalPeriod and its own AnalystEstimatesCount,
            // and lets the algorithm decide. Holders is our estimate count, so the same applies. The
            // older quarter is dropped once the newer one overtakes it in Holders, which is the
            // point where it stops adding anything, so the doubling only lasts through the handover.
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

                        // An amendment for a quarter already retired, or already past its shelf
                        // life, is not resurrected: it would put a stale cross-section back into the
                        // current file.
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

                // Ordered by the keys the two containers already hold, security then quarter, rather
                // than by comparing whole lines to recover an order that is known.
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
        /// Keeps the newest quarter, and the one before it only while it still holds more managers
        /// than the newest does. Anything older than that is dropped: once a quarter has been
        /// overtaken in breadth it adds nothing a consumer would choose.
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
        /// The oldest quarter still live on a day. Managers have 45 days after a quarter end to
        /// report it, so until the newest finished quarter's deadline passes the one before it is
        /// still the complete cross-section, and after that it is stale for everyone who files on
        /// time.
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

        /// <summary>
        /// One universe row on its way to a file: the line itself, plus the security, the reported
        /// quarter, the Holders count and the delisting date the forward-fill and the retirement
        /// rule read. They are carried rather than re-parsed, so the source row is split once.
        /// </summary>
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
        /// quarter, each row tagged with the ticker file it lands in, in time order.
        ///
        /// The published rows are cumulative, so they are differenced back to the increments they
        /// were built from before the merge: an increment is the unit that can be unioned, while two
        /// running totals covering different sets of filings cannot be combined at all. Everything
        /// is decimal, so the difference is the exact inverse of the sum and a run that reads no new
        /// filing reproduces the rows it read value for value. The rows of every ticker the security
        /// traded under are differenced together, which is what carries a quarter across a rename.
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
        /// Running-sums the increments within each quarter, which is the published shape: every row
        /// states what had been reported for its quarter as of its own release date. The managers of
        /// one quarter file across roughly fifty days, so the increment alone answers a question
        /// nobody asks.
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

        /// <summary>
        /// Reports how much of the dataset made it through identity resolution. The unresolved side
        /// is the foreign issuers and everything LEAN carries no definition for.
        /// </summary>
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
                      $"{_conflictingTickers} of them because the ticker belonged to another security that day, " +
                      $"{coverage.ToStringInvariant("F1")}% of reported value covered, " +
                      $"{_unchangedGroups} resolved groups added nothing new and wrote no row");

            if (_unresolvedCusips.Count > 0)
            {
                Log.Trace($"SEC13FDownloader.LogResolutionSummary(): unresolved sample: " +
                          $"{string.Join(", ", _unresolvedCusips.Take(25))}");
            }
        }

        /// <summary>
        /// Streams one tab separated table out of the archive, yielding the header's column index map
        /// alongside each row's fields. A missing table or a missing required column throws: either
        /// means the upstream layout moved, and a run that carried on would publish a dataset with
        /// silently empty columns.
        /// </summary>
        private static IEnumerable<(Dictionary<string, int> columns, string[] fields)> ReadTable(
            ZipArchive zip, Archive archive, string table, params string[] required)
        {
            var entry = zip.Entries.FirstOrDefault(x =>
                string.Equals(Path.GetFileName(x.FullName), table, StringComparison.OrdinalIgnoreCase));
            if (entry == null)
            {
                throw new FileNotFoundException(
                    $"SEC13FDownloader.ReadTable(): {archive.Name} carries no {table}. Entries: " +
                    $"{string.Join(", ", zip.Entries.Select(x => x.FullName))}");
            }

            using var stream = entry.Open();
            using var reader = new StreamReader(stream);

            var header = reader.ReadLine();
            if (header == null)
            {
                throw new InvalidDataException($"SEC13FDownloader.ReadTable(): {archive.Name} {table} is empty");
            }

            var columns = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            var names = header.Split('\t');
            for (var i = 0; i < names.Length; i++)
            {
                columns[names[i].Trim()] = i;
            }

            var missing = required.Where(x => !columns.ContainsKey(x)).ToList();
            if (missing.Count > 0)
            {
                throw new InvalidDataException(
                    $"SEC13FDownloader.ReadTable(): {archive.Name} {table} is missing {string.Join(", ", missing)}. " +
                    $"Header: {header}");
            }

            var maximum = required.Max(x => columns[x]);
            string line;
            while ((line = reader.ReadLine()) != null)
            {
                if (line.Length == 0)
                {
                    continue;
                }

                var fields = line.Split('\t');
                if (fields.Length <= maximum)
                {
                    throw new InvalidDataException(
                        $"SEC13FDownloader.ReadTable(): {archive.Name} {table} row holds {fields.Length} fields, " +
                        $"fewer than the {maximum + 1} the required columns need: {line}");
                }

                yield return (columns, fields);
            }
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
        /// True when the ticker is a safe file name. Skips warrants, units and preferreds whose
        /// tickers carry path separators or other invalid characters.
        /// </summary>
        private static bool IsFileNameSafe(string ticker)
        {
            return ticker.IndexOf('/') < 0
                   && ticker.IndexOf('\\') < 0
                   && ticker.IndexOfAny(Path.GetInvalidFileNameChars()) < 0;
        }

        /// <summary>GETs a URL as text, with retry and backoff.</summary>
        private string GetWithRetry(string url)
        {
            for (var attempt = 1; attempt <= MaxRetries; attempt++)
            {
                try
                {
                    _rateGate.WaitToProceed();
                    using var response = _client.GetAsync(url).GetAwaiter().GetResult();
                    response.EnsureSuccessStatusCode();
                    return response.Content.ReadAsStringAsync().GetAwaiter().GetResult();
                }
                catch (Exception err) when (attempt < MaxRetries && IsWorthRetrying(err))
                {
                    Log.Trace($"SEC13FDownloader.GetWithRetry(): {url} retry {attempt}/{MaxRetries} after: {err.Message}");
                    Thread.Sleep(TimeSpan.FromSeconds(2 * attempt));
                }
            }

            throw new HttpRequestException($"SEC13FDownloader.GetWithRetry(): {url} failed after {MaxRetries} attempts");
        }

        /// <summary>
        /// True when a failed request is worth asking again. The SEC answers 403 to a request whose
        /// User-Agent it does not like and 404 to an archive it has not published, and neither
        /// changes on the fourth attempt: retrying them only spends twenty seconds of backoff before
        /// reporting the same failure. Anything without a status is a transport error, which is
        /// exactly what backoff is for.
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
