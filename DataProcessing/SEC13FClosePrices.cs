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

namespace QuantConnect.DataProcessing
{
    /// <summary>
    /// Raw closing prices from LEAN's coarse universe files, one per trading day, which is what a
    /// reported VALUE over SSHPRNAMT is checked against. The close of the quarter's last trading day
    /// is known before any filing for that quarter can be made, so the check needs no other filing
    /// and gives the same answer on the day a filing is read as in a rebuild years later.
    /// </summary>
    internal sealed class SEC13FClosePrices
    {
        /// <summary>Days walked back from a quarter end for its last trading day: a long weekend and a holiday.</summary>
        private const int DaysToLookBack = 7;

        private readonly string _coarseDirectory;
        private readonly Dictionary<DateTime, Dictionary<string, decimal>> _closesByDay = new();

        public SEC13FClosePrices(string coarseDirectory)
        {
            _coarseDirectory = coarseDirectory;
        }

        /// <summary>Whether the coarse files are there at all, so the caller can say so once.</summary>
        public bool Available => Directory.Exists(_coarseDirectory);

        /// <summary>
        /// The raw close of a security on the last trading day on or before the quarter end, or
        /// null. Never a day after the filing date: a period typed years ahead would otherwise read
        /// a price nobody had when the filing was made.
        /// </summary>
        public decimal? Close(SecurityIdentifier security, DateTime periodEnd, DateTime filingDate)
        {
            var last = periodEnd.Date < filingDate.Date ? periodEnd.Date : filingDate.Date;
            for (var back = 0; back < DaysToLookBack; back++)
            {
                var closes = ClosesOn(last.AddDays(-back));
                if (closes != null)
                {
                    // The last trading day decides: a security missing from it did not trade then.
                    return closes.TryGetValue(security.ToString(), out var close) && close > 0m ? close : null;
                }
            }

            return null;
        }

        /// <summary>The closes of one trading day, keyed by security identifier, or null when the day has no file.</summary>
        private Dictionary<string, decimal> ClosesOn(DateTime day)
        {
            if (_closesByDay.TryGetValue(day, out var closes))
            {
                return closes;
            }

            var path = Path.Combine(_coarseDirectory, $"{day.ToString(DateFormat.EightCharacter, CultureInfo.InvariantCulture)}.csv");
            if (File.Exists(path))
            {
                closes = new Dictionary<string, decimal>(StringComparer.Ordinal);

                // sid, ticker, close, volume, dollar volume, has fundamentals, price factor, split factor
                foreach (var line in File.ReadLines(path))
                {
                    var fields = line.Split(',');
                    if (fields.Length > 2 && decimal.TryParse(fields[2], NumberStyles.Any, CultureInfo.InvariantCulture, out var close))
                    {
                        closes[fields[0]] = close;
                    }
                }
            }

            _closesByDay[day] = closes;
            return closes;
        }
    }
}
