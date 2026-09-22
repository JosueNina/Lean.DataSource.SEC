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
using System.IO.Compression;
using System.Linq;

namespace QuantConnect.DataProcessing
{
    /// <summary>File helpers shared by the 13F downloader and the N-PORT crosswalk.</summary>
    internal static class SEC13FFiles
    {
        /// <summary>
        /// Streams one tab separated table out of an SEC archive, yielding the header's column index
        /// map with each row. A missing table or required column throws, since either means the
        /// layout moved; so does a row too short for the required columns, unless
        /// <paramref name="skipShortRows"/> asks for it to be skipped.
        /// </summary>
        public static IEnumerable<(Dictionary<string, int> Columns, string[] Fields)> ReadTable(
            ZipArchive zip, string source, string table, bool skipShortRows, params string[] required)
        {
            var entry = zip.Entries.FirstOrDefault(x =>
                string.Equals(Path.GetFileName(x.FullName), table, StringComparison.OrdinalIgnoreCase));
            if (entry == null)
            {
                throw new FileNotFoundException(
                    $"SEC13FFiles.ReadTable(): {source} carries no {table}. Entries: " +
                    $"{string.Join(", ", zip.Entries.Select(x => x.FullName))}");
            }

            using var reader = new StreamReader(entry.Open());

            var header = reader.ReadLine();
            if (header == null)
            {
                throw new InvalidDataException($"SEC13FFiles.ReadTable(): {source} {table} is empty");
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
                    $"SEC13FFiles.ReadTable(): {source} {table} is missing {string.Join(", ", missing)}. Header: {header}");
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
                    if (skipShortRows)
                    {
                        continue;
                    }

                    throw new InvalidDataException(
                        $"SEC13FFiles.ReadTable(): {source} {table} row holds {fields.Length} fields, " +
                        $"fewer than the {maximum + 1} the required columns need: {line}");
                }

                yield return (columns, fields);
            }
        }

        /// <summary>
        /// Writes a file through a temporary sibling that is moved into place at the end, so an
        /// interrupted run never leaves a half written file for the next one to read.
        /// </summary>
        public static void WriteThenMove(string path, Action<Stream> write)
        {
            var temporaryPath = path + ".tmp";
            using (var stream = File.Create(temporaryPath))
            {
                write(stream);
            }

            File.Move(temporaryPath, path, overwrite: true);
        }
    }
}
