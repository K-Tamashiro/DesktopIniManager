using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;

namespace DesktopIniManager.Services
{
    internal sealed class InputHistoryStore
    {
        internal const int Capacity = 20;
        private readonly string directory;
        internal InputHistoryStore(string directory) { this.directory = directory; }

        private string FilePath(string key)
        {
            if (string.IsNullOrEmpty(key) || key.Any(c => !char.IsLetterOrDigit(c) && c != '-' && c != '_'))
                throw new ArgumentException(nameof(key));
            return Path.Combine(directory, key + ".txt");
        }

        internal List<string> Load(string key)
        {
            var result = new List<string>();
            try
            {
                foreach (string line in File.ReadLines(FilePath(key)))
                {
                    try
                    {
                        string value = Encoding.UTF8.GetString(Convert.FromBase64String(line));
                        if (IsFolderHistoryKey(key)) value = NormalizeFolderPath(value);
                        if (!string.IsNullOrWhiteSpace(value) && !result.Exists(entry => FolderHistoryEquals(key, entry, value))) result.Add(value);
                        if (result.Count == Capacity) break;
                    }
                    catch (FormatException) { }
                }
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
            return result;
        }

        internal List<string> Remember(string key, string value, bool promote = true)
        {
            var entries = Load(key);
            // Preserve whitespace and case: both can be significant in searches and arguments.
            if (string.IsNullOrWhiteSpace(value)) return entries;
            if (IsFolderHistoryKey(key)) value = NormalizeFolderPath(value);
            if (!promote && entries.Exists(entry => FolderHistoryEquals(key, entry, value))) return entries;
            entries.RemoveAll(entry => FolderHistoryEquals(key, entry, value));
            if (promote) entries.Insert(0, value);
            else entries.Add(value);
            return Save(key, entries);
        }

        internal void Clear(string key)
        {
            try { File.Delete(FilePath(key)); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }

        internal List<string> Replace(string key, IEnumerable<string> values)
        {
            var entries = new List<string>();
            if (values != null)
            {
                foreach (string value in values)
                {
                    if (string.IsNullOrWhiteSpace(value) || entries.Contains(value)) continue;
                    entries.Add(value);
                    if (entries.Count == Capacity) break;
                }
            }
            return Save(key, entries);
        }

        internal List<string> Remove(string key, string value)
        {
            var entries = Load(key);
            if (string.IsNullOrWhiteSpace(value)) return entries;
            entries.RemoveAll(entry => FolderHistoryEquals(key, entry, value));
            return Save(key, entries);
        }

        private List<string> Save(string key, List<string> entries)
        {
            if (entries.Count > Capacity) entries.RemoveRange(Capacity, entries.Count - Capacity);
            try
            {
                Directory.CreateDirectory(directory);
                File.WriteAllLines(FilePath(key), entries.Select(s => Convert.ToBase64String(Encoding.UTF8.GetBytes(s))), new UTF8Encoding(false));
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
            return entries;
        }

        private static bool IsFolderHistoryKey(string key)
        {
            return string.Equals(key, "Differencer-Source", StringComparison.OrdinalIgnoreCase)
                || string.Equals(key, "Differencer-Target", StringComparison.OrdinalIgnoreCase);
        }

        private static string NormalizeFolderPath(string path)
        {
            if (string.IsNullOrWhiteSpace(path)) return path ?? "";
            string trimmed = path.TrimEnd('\\', '/');
            if (trimmed.Length == 2 && trimmed[1] == ':') return trimmed + "\\";
            return trimmed;
        }

        private static bool FolderHistoryEquals(string key, string left, string right)
        {
            if (!IsFolderHistoryKey(key)) return left == right;
            return string.Equals(NormalizeFolderPath(left), NormalizeFolderPath(right), StringComparison.OrdinalIgnoreCase);
        }
    }
}
