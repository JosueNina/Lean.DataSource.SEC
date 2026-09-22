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
using QuantConnect.Interfaces;
using QuantConnect.Logging;
using QuantConnect.Util;

namespace QuantConnect.DataSource
{
    /// <summary>
    /// The names of the managers that file Form 13F, by CIK, read from the managers.csv published
    /// beside the dataset. A name is the one the manager's most recent cover page states, so an old
    /// filing shows the name the manager carries today.
    /// </summary>
    public static class SEC13FManagerNameProvider
    {
        private const string FileName = "managers.csv";

        /// <summary>How long a read that found no file waits before trying again.</summary>
        internal static TimeSpan RetryInterval = TimeSpan.FromMinutes(5);

        private static readonly object _lock = new();
        private static Dictionary<int, string> _names;
        private static DateTime _loaded;
        private static DateTime _attempted;

        /// <summary>The manager's name, or null for a CIK the file does not carry.</summary>
        public static string GetName(int managerCik)
        {
            lock (_lock)
            {
                // Read again when the day changes, since the file gains the managers that filed for
                // the first time each night. A read that found no file is not an answer: stamping
                // the day for it would leave every name null until midnight over one missed fetch.
                // It is retried, but not on every line, since a data folder that simply does not
                // carry the file would otherwise never stop asking for it.
                var now = DateTime.UtcNow;
                if (_names == null || (_loaded != now.Date && now - _attempted >= RetryInterval))
                {
                    _attempted = now;
                    var read = Read();
                    if (read != null)
                    {
                        _names = read;
                        _loaded = now.Date;
                    }

                    _names ??= [];
                }

                return _names.GetValueOrDefault(managerCik);
            }
        }

        /// <summary>Drops the names read so far, for tests that move the data folder.</summary>
        internal static void Reset()
        {
            lock (_lock)
            {
                _names = null;
                _loaded = default;
                _attempted = default;
            }
        }

        /// <summary>The names in the file, or null when there is no file to read.</summary>
        private static Dictionary<int, string> Read()
        {
            var path = Path.Combine(Globals.DataFolder, "alternative", "sec", SEC13FHolding.ReportFolder, FileName);

            // Through the data provider where there is one, so the cloud fetches the file.
            using var stream = Composer.Instance.GetPart<IDataProvider>()?.Fetch(path)
                ?? (File.Exists(path) ? File.OpenRead(path) : null);
            if (stream == null)
            {
                Log.Trace($"SEC13FManagerNameProvider.Read(): no {FileName} at {path}, so every ManagerName is null");
                return null;
            }

            Dictionary<int, string> names = [];
            using var reader = new StreamReader(stream);
            while (reader.ReadLine() is { } line)
            {
                var separator = line.IndexOf(',');
                if (separator > 0 && int.TryParse(line.AsSpan(0, separator), NumberStyles.Integer, CultureInfo.InvariantCulture, out var cik))
                {
                    names[cik] = line[(separator + 1)..];
                }
            }

            return names;
        }
    }
}
