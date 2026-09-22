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
using System.Globalization;
using System.IO;
using QuantConnect.Configuration;
using QuantConnect.Logging;

namespace QuantConnect.DataProcessing
{
    /// <summary>
    /// What the job hands every SEC dataset alike: the date it runs for and the folders it reads
    /// and writes, from the environment and config keys the data fleet sets.
    /// </summary>
    /// <param name="DeploymentDate">The date the run is for, or null when it rebuilds the whole history</param>
    /// <param name="OutputRoot">The temp-output-directory, which the job hands over empty and publishes</param>
    internal sealed record SECProcessingContext(DateTime? DeploymentDate, string OutputRoot)
    {
        private const string DeploymentDateVariable = "QC_DATAFLEET_DEPLOYMENT_DATE";
        private const string VendorName = "sec";

        /// <summary>Where the SEC datasets are written: {temp-output-directory}/alternative/sec.</summary>
        public string OutputDirectory => VendorFolder(OutputRoot);

        /// <summary>
        /// The published SEC data an incremental run builds on. Not the output, which arrives empty.
        /// </summary>
        public string ProcessedDirectory { get; } = VendorFolder(Config.Get("processed-data-directory", Globals.DataFolder));

        /// <summary>
        /// Where downloads land. The job archives this folder after every run but does not restore
        /// it before the next.
        /// </summary>
        public string RawDirectory { get; } = VendorFolder(Config.Get("raw-data-folder", "/raw"));

        /// <summary>
        /// Reads the context, or logs why it cannot. A missing date is a misconfigured job rather
        /// than a request for the whole history, which a dataset that supports it is asked for by
        /// name with <paramref name="rebuildHistoryKey"/>.
        /// </summary>
        /// <param name="rebuildHistoryKey">The config key that asks the dataset for a full rebuild, or null when it has none</param>
        /// <param name="context">The context, whose date is null when the run rebuilds the whole history</param>
        /// <returns>True when the date is well formed, or absent with the rebuild asked for</returns>
        public static bool TryCreate(string rebuildHistoryKey, out SECProcessingContext context)
        {
            context = null;
            if (!TryParseDeploymentDate(rebuildHistoryKey, out var deploymentDate))
            {
                return false;
            }

            context = new SECProcessingContext(deploymentDate, Config.Get("temp-output-directory", "/temp-output-directory"));
            return true;
        }

        internal static bool TryParseDeploymentDate(string rebuildHistoryKey, out DateTime? deploymentDate)
        {
            deploymentDate = null;

            var raw = Environment.GetEnvironmentVariable(DeploymentDateVariable);
            if (string.IsNullOrWhiteSpace(raw))
            {
                if (rebuildHistoryKey != null && Config.GetBool(rebuildHistoryKey))
                {
                    return true;
                }

                Log.Error($"SECProcessingContext.TryParseDeploymentDate(): {DeploymentDateVariable} is not set" +
                          (rebuildHistoryKey == null ? string.Empty : $". Set it, or set \"{rebuildHistoryKey}\": true to rebuild the whole history"));
                return false;
            }

            // A malformed date must not quietly become a full history run: that would turn a daily
            // job into a complete refetch of five gigabytes without anyone noticing.
            if (!DateTime.TryParseExact(raw.Trim(), "yyyyMMdd", CultureInfo.InvariantCulture,
                    DateTimeStyles.None, out var parsed))
            {
                Log.Error($"SECProcessingContext.TryParseDeploymentDate(): {DeploymentDateVariable} '{raw}' is not yyyyMMdd");
                return false;
            }

            deploymentDate = parsed;
            return true;
        }

        private static string VendorFolder(string root) => Path.Combine(root, "alternative", VendorName);
    }
}
