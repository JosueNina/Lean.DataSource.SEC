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

using QuantConnect.Configuration;
using QuantConnect.DataSource;
using QuantConnect.Logging;
using QuantConnect.Util;
using System;
using System.Diagnostics;
using System.Globalization;
using System.IO;

namespace QuantConnect.DataProcessing
{
    /// <summary>
    /// Console program to convert from raw SEC data to a formatted form usable by LEAN.
    ///
    /// The repository ships two unrelated SEC datasets and the "dataset-name" config key selects
    /// which one a run processes:
    ///
    ///  - "reports" (the default): the 10-K, 10-Q and 8-K filings, downloaded with
    ///    <see cref="SECDataDownloader"/> and converted with <see cref="SECDataConverter"/>.
    ///  - "13f" (<see cref="SEC13FDownloader"/>): Form 13F institutional holdings, aggregated per
    ///    security from the quarterly structured data sets.
    ///
    /// The default keeps a job that sets no dataset-name doing exactly what it did before the key
    /// existed.
    /// </summary>
    public class Program
    {
        /// <summary>
        /// The "dataset-name" config value that selects the shipped SEC reports dataset, and the
        /// value assumed when the key is not set.
        /// </summary>
        private const string ReportsDatasetName = "reports";

        /// <summary>
        /// Entrypoint of the program. The exit code is returned rather than handed to
        /// <see cref="Environment.Exit"/> from inside the work: that call does not unwind the stack,
        /// so every finally block written for the failure paths would be skipped on all of them.
        /// </summary>
        /// <returns>Zero on success, one on any failure</returns>
        public static int Main()
        {
            var dataset = Config.Get("dataset-name", ReportsDatasetName).Trim().ToLowerInvariant();

            switch (dataset)
            {
                case ReportsDatasetName:
                    return ProcessReports();

                case SEC13FDownloader.DatasetName:
                    return Process13F();

                default:
                    Log.Error($"DataProcessing.Main(): Unknown dataset-name '{dataset}'. Valid options: " +
                              $"{ReportsDatasetName}, {SEC13FDownloader.DatasetName}");
                    return 1;
            }
        }

        /// <summary>
        /// Downloads and converts the SEC reports dataset for the deployment date.
        /// </summary>
        /// <returns>Zero on success, one on any failure</returns>
        private static int ProcessReports()
        {
            var processingDateValue = Environment.GetEnvironmentVariable("QC_DATAFLEET_DEPLOYMENT_DATE");
            var processingDate = DateTime.ParseExact(processingDateValue, "yyyyMMdd", CultureInfo.InvariantCulture);
            var temporaryFolder = Config.Get("temp-output-directory", "/temp-output-directory");
            var rawDataDirectory = Config.Get("raw-data-folder", "/raw");
            var secDataDirectory = Path.Combine(rawDataDirectory, "alternative", "sec");
            Log.Trace($"DataProcessing.Main(): Processing {processingDate:yyyy-MM-dd}");

            var timer = Stopwatch.StartNew();

            try 
            {
                var download = new SECDataDownloader();
                Log.Trace("DataProcessing.Main(): Begin downloading raw files from SEC website...");
                download.Download(secDataDirectory, processingDate, processingDate);
                timer.Stop();
                Log.Trace($"DataProcessing.Main(): {processingDate} Downloading finished in time {timer.Elapsed}");
            }
            catch (Exception err) 
            {
                Log.Error(err, $"DataProcessing.Main(): {processingDate} Exception occurred while downloading SEC data");
            }

            timer.Restart();
            try
            {
                var processor = new SECDataConverter(secDataDirectory, temporaryFolder);
                processor.Process(processingDate);
                timer.Stop();
                Log.Trace($"DataProcessing.Main(): {processingDate} Conversion finished in time {timer.Elapsed}");
            }
            catch (Exception e)
            {
                Log.Error(e, $"DataProcessing.Main(): {processingDate} Exception while processing SEC data");
                return 1;
            }

            return 0;
        }

        /// <summary>
        /// Downloads and converts the Form 13F institutional holdings dataset. Without a deployment
        /// date the run walks every published archive; with one it reads the archive whose window
        /// covers that date and folds it into the published history.
        /// </summary>
        /// <returns>Zero on success, one on any failure</returns>
        private static int Process13F()
        {
            // Output root: {temp-output-directory}/alternative/sec. The downloader writes its report
            // under the folder its data class names.
            var destinationDirectory = Path.Combine(
                Config.Get("temp-output-directory", "/temp-output-directory"),
                "alternative",
                SEC13FDownloader.VendorName);

            // The published history an incremental run merges its rows into. The destination is
            // handed to the job empty, so reading history back from there would find nothing.
            var processedDataDirectory = Path.Combine(
                Config.Get("processed-data-directory", Globals.DataFolder),
                "alternative",
                SEC13FDownloader.VendorName);

            // Where downloads are kept between runs. The job syncs this folder to the archive store
            // after every run, the same one the reports dataset keeps its feed archives in, so it is
            // the one place a cache survives the container.
            var rawDataDirectory = Path.Combine(
                Config.Get("raw-data-folder", "/raw"),
                "alternative",
                SEC13FDownloader.VendorName);

            if (!TryParseDeploymentDate(out var deploymentDate))
            {
                return 1;
            }

            Log.Trace($"DataProcessing.Process13F(): writing {SEC13FDownloader.DatasetName} to {destinationDirectory}"
                      + (deploymentDate == null ? " for the full history" : $" for {deploymentDate:yyyy-MM-dd}"));

            var timer = Stopwatch.StartNew();
            SEC13FDownloader downloader;
            try
            {
                downloader = new SEC13FDownloader(destinationDirectory, processedDataDirectory, deploymentDate,
                    rawDataDirectory);
            }
            catch (Exception err)
            {
                Log.Error(err, $"DataProcessing.Process13F(): The {SEC13FDownloader.DatasetName} downloader failed to be constructed");
                return 1;
            }

            try
            {
                if (!downloader.Run())
                {
                    Log.Error($"DataProcessing.Process13F(): Failed to download/process {SEC13FDownloader.DatasetName} data");
                    return 1;
                }

                timer.Stop();
                Log.Trace($"DataProcessing.Process13F(): Conversion finished in time {timer.Elapsed}");
                return 0;
            }
            catch (Exception err)
            {
                Log.Error(err, $"DataProcessing.Process13F(): The {SEC13FDownloader.DatasetName} downloader exited unexpectedly");
                return 1;
            }
            finally
            {
                downloader.DisposeSafely();
            }
        }

        /// <summary>
        /// Reads the deployment date the job runs for.
        /// </summary>
        /// <param name="deploymentDate">The date, or null when the run should take the whole history</param>
        /// <returns>True when the variable was absent or well formed</returns>
        private static bool TryParseDeploymentDate(out DateTime? deploymentDate)
        {
            deploymentDate = null;

            var raw = Environment.GetEnvironmentVariable("QC_DATAFLEET_DEPLOYMENT_DATE");
            if (string.IsNullOrWhiteSpace(raw))
            {
                return true;
            }

            // A malformed date must not quietly become a full history run: that would turn a daily
            // job into a complete refetch of five gigabytes without anyone noticing.
            if (!DateTime.TryParseExact(raw.Trim(), "yyyyMMdd", CultureInfo.InvariantCulture,
                    DateTimeStyles.None, out var parsed))
            {
                Log.Error($"DataProcessing.TryParseDeploymentDate(): QC_DATAFLEET_DEPLOYMENT_DATE '{raw}' is not yyyyMMdd");
                return false;
            }

            deploymentDate = parsed;
            return true;
        }
    }
}
