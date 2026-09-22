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
using QuantConnect.Logging;
using QuantConnect.Util;
using System;
using System.Diagnostics;

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
    ///  - "13f" (<see cref="SEC13FDownloader"/>): Form 13F institutional holdings, every position
    ///    as its manager filed it, from the SEC's structured data sets and EDGAR's daily indexes.
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

        /// <summary>Config key that asks the 13F run to rebuild the whole history instead of one date.</summary>
        internal const string RebuildHistoryKey = "sec-13f-rebuild-history";

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
            // The reports dataset has no full rebuild, so it always needs a date.
            if (!SECProcessingContext.TryCreate(null, out var context))
            {
                return 1;
            }

            var processingDate = context.DeploymentDate.Value;
            var temporaryFolder = context.OutputRoot;
            var secDataDirectory = context.RawDirectory;
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
        /// Downloads and converts the Form 13F institutional holdings dataset for the deployment date,
        /// folding it into the published history, or rebuilds the whole history when asked to.
        /// </summary>
        /// <returns>Zero on success, one on any failure</returns>
        private static int Process13F()
        {
            if (!SECProcessingContext.TryCreate(RebuildHistoryKey, out var context))
            {
                return 1;
            }

            Log.Trace($"DataProcessing.Process13F(): writing {SEC13FDownloader.DatasetName} to {context.OutputDirectory}"
                      + (context.DeploymentDate == null ? " for the full history" : $" for {context.DeploymentDate:yyyy-MM-dd}"));

            var timer = Stopwatch.StartNew();
            SEC13FDownloader downloader;
            try
            {
                downloader = new SEC13FDownloader(context.OutputDirectory, context.ProcessedDirectory,
                    context.DeploymentDate, context.RawDirectory);
            }
            catch (Exception err)
            {
                Log.Error(err, $"DataProcessing.Process13F(): The {SEC13FDownloader.DatasetName} downloader failed to be constructed");
                return 1;
            }

            try
            {
                downloader.Run();

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

    }
}
