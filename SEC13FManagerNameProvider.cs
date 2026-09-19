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

        private static readonly object _lock = new();
        private static Dictionary<int, string> _names;
        private static DateTime _loaded;

        /// <summary>The manager's name, or null for a CIK the file does not carry.</summary>
        public static string GetName(int managerCik)
        {
            lock (_lock)
            {
                // Read again when the day changes, since the file gains the managers that filed
                // for the first time each night.
                var today = DateTime.UtcNow.Date;
                if (_names == null || _loaded != today)
                {
                    _names = Read();
                    _loaded = today;
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
            }
        }

        private static Dictionary<int, string> Read()
        {
            Dictionary<int, string> names = [];
            var path = Path.Combine(Globals.DataFolder, "alternative", "sec", SEC13FHolding.ReportFolder, FileName);

            // Through the data provider where there is one, so the cloud fetches the file.
            using var stream = Composer.Instance.GetPart<IDataProvider>()?.Fetch(path)
                ?? (File.Exists(path) ? File.OpenRead(path) : null);
            if (stream == null)
            {
                return names;
            }

            using var reader = new StreamReader(stream);
            while (reader.ReadLine() is { } line)
            {
                // Only the first comma separates: a name may carry its own.
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
