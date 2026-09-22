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
using System.Net;
using System.Net.Http;
using System.Threading;
using QuantConnect.Configuration;
using QuantConnect.Logging;
using QuantConnect.Util;

namespace QuantConnect.DataProcessing
{
    /// <summary>
    /// The HTTP side every SEC dataset processor shares: the User-Agent the SEC asks automated
    /// readers for, its rate limit, retries with backoff, and downloads that never leave a truncated
    /// file behind. Safe to call from several threads, which share the one rate gate.
    /// </summary>
    public class SECEdgarClient : IDisposable
    {
        private const int MaxRetries = 8;

        private readonly HttpClient _client = new() { Timeout = TimeSpan.FromMinutes(30) };
        private bool _userAgentSet;

        /// <summary>
        /// Config key for the requests a second the client sends. The SEC allows ten; a long rebuild
        /// runs lower, since EDGAR answered a day of sustained reading at ten with 503s and responses
        /// slowed to seconds for the whole address, even at one request at a time.
        /// </summary>
        public const string RequestsPerSecondKey = "sec-requests-per-second";

        private const int MaximumRequestsPerSecond = 10;

        private readonly RateGate _rateGate = new(
            Math.Clamp(Config.GetInt(RequestsPerSecondKey, MaximumRequestsPerSecond), 1, MaximumRequestsPerSecond),
            TimeSpan.FromSeconds(1));

        /// <summary>The requests a second the client sends.</summary>
        public int RequestsPerSecond => _rateGate.Occurrences;

        /// <summary>EDGAR's directory listings, by folder URL, read once per client.</summary>
        private readonly Dictionary<string, ISet<string>> _listings = new(StringComparer.Ordinal);

        /// <summary>
        /// GETs a URL as text, with retry and backoff. <paramref name="retryNotFound"/> false stops at
        /// the first 404, for a file the SEC removed long ago rather than one it has not served yet.
        /// </summary>
        public string GetText(string url, bool retryNotFound = true)
        {
            return WithRetry(url, retryNotFound, () =>
            {
                using var response = _client.GetAsync(url).GetAwaiter().GetResult();
                response.EnsureSuccessStatusCode();
                return response.Content.ReadAsStringAsync().GetAwaiter().GetResult();
            });
        }

        /// <summary>
        /// Downloads a file into a directory, reusing a copy already there. It lands as ".part" first,
        /// so an interrupted run cannot leave a truncated file for the next one.
        /// </summary>
        public string DownloadFile(string url, string name, string directory)
        {
            Directory.CreateDirectory(directory);
            var path = Path.Combine(directory, name);
            if (File.Exists(path))
            {
                Log.Trace($"SECEdgarClient.DownloadFile(): {name} already on disk");
                return path;
            }

            var temporaryPath = path + ".part";
            WithRetry(url, true, () =>
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
            Log.Trace($"SECEdgarClient.DownloadFile(): {name}, {new FileInfo(path).Length} bytes");
            return path;
        }

        /// <summary>
        /// Whether the SEC has published a file: false only on 404. Anything else fails once the
        /// retries run out, since a 403 taken for "not published" would change a result quietly.
        /// </summary>
        public bool UrlExists(string url)
        {
            return WithRetry(url, true, () =>
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

        /// <summary>
        /// The names in one of EDGAR's directory listings. Read once per client, so a day published
        /// after the run read its quarter is left to the next run. Fails like any other GET, a block included.
        /// </summary>
        public ISet<string> ListDirectory(string url)
        {
            lock (_listings)
            {
                if (!_listings.TryGetValue(url, out var names))
                {
                    names = SECEdgarIndex.ListingNames(GetText(url + "index.json"));
                    _listings[url] = names;
                }

                return names;
            }
        }

        /// <summary>
        /// True when a failed request is worth asking again: transport errors, server errors,
        /// throttling and a 404. Every file requested is one the SEC lists, so a 404 is its own
        /// hiccup: the first filings of two new filers, in the 24 and 27 July 2026 indexes, answered
        /// 404 for up to a minute and 200 afterwards. A 403 is a block, which outlasts any backoff.
        /// </summary>
        internal static bool IsWorthRetrying(Exception error)
        {
            var status = (error as HttpRequestException)?.StatusCode;

            return status == null
                   || (int)status >= 500
                   || status == HttpStatusCode.NotFound
                   || status == HttpStatusCode.TooManyRequests
                   || status == HttpStatusCode.RequestTimeout;
        }

        /// <summary>
        /// Sends a request through the rate gate, retrying whatever IsWorthRetrying accepts with a
        /// backoff that doubles up to two minutes, about four minutes in all: EDGAR served a new
        /// filer's listed filing as a 404 for over a minute. The last failure is the one that surfaces.
        /// </summary>
        private T WithRetry<T>(string url, bool retryNotFound, Func<T> request)
        {
            RequireUserAgent();
            for (var attempt = 1; ; attempt++)
            {
                try
                {
                    _rateGate.WaitToProceed();
                    return request();
                }
                catch (Exception err) when (attempt < MaxRetries && IsWorthRetrying(err)
                                            && (retryNotFound || (err as HttpRequestException)?.StatusCode != HttpStatusCode.NotFound))
                {
                    Log.Trace($"SECEdgarClient.WithRetry(): {url} retry {attempt}/{MaxRetries} after: {err.Message}");
                    Thread.Sleep(TimeSpan.FromSeconds(Math.Min(120, Math.Pow(2, attempt))));
                }
            }
        }

        /// <summary>
        /// Sets the User-Agent the SEC asks automated readers for, from the same config keys the
        /// reports dataset reads, on the first request: a run that never reaches the network, like
        /// the unit tests, does not need them.
        /// </summary>
        private void RequireUserAgent()
        {
            lock (_client)
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
        }

        /// <summary>Disposes the HTTP client and the rate gate.</summary>
        public void Dispose()
        {
            _client.DisposeSafely();
            _rateGate.DisposeSafely();
            GC.SuppressFinalize(this);
        }
    }
}
