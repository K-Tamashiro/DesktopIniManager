using DesktopIniManager.Models;
using FastVolumeIndex;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace DesktopIniManager.Services
{
    internal sealed class CodeGrepService
    {
        private static readonly HashSet<string> IgnoredDirectories = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        { ".git", ".vs", ".idea", "bin", "obj", "node_modules", "packages", "vendor", "dist", "build", "target", "coverage" };

        public GrepSearchResult Search(IReadOnlyList<string> scopes, LanguageProfile profile, string query,
            bool regex, bool matchCase, bool wholeWord, Action<int, int> progress, CancellationToken token,
            Action<GrepMatch> matchFound = null)
        {
            token.ThrowIfCancellationRequested();
            var files = CollectFiles(scopes, profile.Extensions, token);
            var orderedScopes = scopes
                .Where(root => !string.IsNullOrWhiteSpace(root))
                .OrderByDescending(root => root.Length)
                .ToArray();
            var matches = new ConcurrentBag<GrepMatch>();
            int processed = 0;
            int skipped = 0;
            Regex matcher = BuildMatcher(query, regex, matchCase, wholeWord);
            var options = new ParallelOptions { CancellationToken = token, MaxDegreeOfParallelism = 4 };

            Parallel.ForEach(files, options, file =>
            {
                try
                {
                    if (new FileInfo(file).Length > 64L * 1024 * 1024) { Interlocked.Increment(ref skipped); return; }
                    string scope = FindScope(file, orderedScopes);
                    int lineNumber = 0;
                    foreach (string line in ReadLines(file))
                    {
                        options.CancellationToken.ThrowIfCancellationRequested();
                        lineNumber++;
                        Match match = matcher.Match(line);
                        if (!match.Success) continue;
                        var found = new GrepMatch
                        {
                            ScopeName = Path.GetFileName((scope ?? string.Empty).TrimEnd(Path.DirectorySeparatorChar)) ?? scope,
                            FilePath = file,
                            RelativePath = scope == null ? file : MakeRelativePath(scope, file),
                            LineNumber = lineNumber,
                            ColumnNumber = match.Index + 1,
                            LineText = line.Trim()
                        };
                        matches.Add(found);
                        matchFound?.Invoke(found);
                    }
                }
                catch (IOException) { Interlocked.Increment(ref skipped); }
                catch (UnauthorizedAccessException) { Interlocked.Increment(ref skipped); }
                finally
                {
                    int done = Interlocked.Increment(ref processed);
                    if (((done & 31) == 0 || done == files.Count) && progress != null) progress(done, files.Count);
                }
            });

            token.ThrowIfCancellationRequested();
            return new GrepSearchResult(matches.OrderBy(item => item.ScopeName, StringComparer.CurrentCultureIgnoreCase)
                .ThenBy(item => item.RelativePath, StringComparer.CurrentCultureIgnoreCase)
                .ThenBy(item => item.LineNumber).ToList(), files.Count, skipped);
        }

        private static Regex BuildMatcher(string query, bool regex, bool matchCase, bool wholeWord)
        {
            string pattern = regex ? query : Regex.Escape(query);
            if (wholeWord) pattern = @"\b(?:" + pattern + @")\b";
            RegexOptions options = RegexOptions.Compiled;
            if (!matchCase) options |= RegexOptions.IgnoreCase;
            return new Regex(pattern, options, TimeSpan.FromSeconds(2));
        }

        private static List<string> CollectFiles(IReadOnlyList<string> scopes, string[] extensions, CancellationToken token)
        {
            var files = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var volumeGroup in scopes.GroupBy(Path.GetPathRoot, StringComparer.OrdinalIgnoreCase))
            {
                NtfsVolumeIndex index = null;
                token.ThrowIfCancellationRequested();
                try { index = NtfsVolumeIndex.Create(volumeGroup.First(), token); }
                catch (Exception ex) when (ex is UnauthorizedAccessException || ex is NotSupportedException || ex is IOException) { }

                foreach (string scope in volumeGroup)
                {
                    token.ThrowIfCancellationRequested();
                    if (index != null)
                    {
                        foreach (MftEntry entry in index.FindFiles(scope, extensions, null, token))
                        {
                            token.ThrowIfCancellationRequested();
                            string path = index.GetFullPath(entry);
                            if (!ContainsIgnoredDirectory(path, scope)) files.Add(path);
                        }
                    }
                    else CollectFilesStandard(scope, new HashSet<string>(extensions, StringComparer.OrdinalIgnoreCase), files, token);
                }
            }
            return files.ToList();
        }

        private static void CollectFilesStandard(string root, HashSet<string> extensions, HashSet<string> files, CancellationToken token)
        {
            var pending = new Stack<string>(); pending.Push(root);
            while (pending.Count > 0)
            {
                token.ThrowIfCancellationRequested();
                string folder = pending.Pop();
                try
                {
                    foreach (string file in Directory.EnumerateFiles(folder))
                    {
                        token.ThrowIfCancellationRequested();
                        if (extensions.Contains(Path.GetExtension(file))) files.Add(file);
                    }
                    foreach (string child in Directory.EnumerateDirectories(folder))
                    {
                        token.ThrowIfCancellationRequested();
                        string name = Path.GetFileName(child);
                        if (IgnoredDirectories.Contains(name)) continue;
                        try
                        {
                            if ((File.GetAttributes(child) & FileAttributes.Hidden) != 0) continue;
                        }
                        catch (IOException) { continue; }
                        catch (UnauthorizedAccessException) { continue; }
                        pending.Push(child);
                    }
                }
                catch (IOException) { }
                catch (UnauthorizedAccessException) { }
            }
        }

        private static IEnumerable<string> ReadLines(string path)
        {
            Encoding encoding = DetectEncoding(path);
            using (var reader = new StreamReader(path, encoding, true))
            {
                string line;
                while ((line = reader.ReadLine()) != null) yield return line;
            }
        }

        private static Encoding DetectEncoding(string path)
        {
            byte[] sample = new byte[4096];
            int count;
            using (var stream = File.Open(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
                count = stream.Read(sample, 0, sample.Length);
            if (count >= 2 && sample[0] == 0xFF && sample[1] == 0xFE) return Encoding.Unicode;
            if (count >= 2 && sample[0] == 0xFE && sample[1] == 0xFF) return Encoding.BigEndianUnicode;
            if (count >= 3 && sample[0] == 0xEF && sample[1] == 0xBB && sample[2] == 0xBF) return Encoding.UTF8;
            try { new UTF8Encoding(false, true).GetString(sample, 0, count); return new UTF8Encoding(false); }
            catch (DecoderFallbackException) { return Encoding.GetEncoding(932); }
        }

        private static bool ContainsIgnoredDirectory(string file, string root)
        {
            string relative = MakeRelativePath(root, file);
            return relative.Split(Path.DirectorySeparatorChar).Any(part => IgnoredDirectories.Contains(part));
        }


        private static string FindScope(string path, IReadOnlyList<string> orderedScopes)
        {
            for (int i = 0; i < orderedScopes.Count; i++)
                if (IsUnderPath(path, orderedScopes[i])) return orderedScopes[i];
            return null;
        }

        private static bool IsUnderPath(string path, string root)
        {
            string normalizedRoot = root.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
            return path.StartsWith(normalizedRoot, StringComparison.OrdinalIgnoreCase);
        }

        private static string MakeRelativePath(string root, string path)
        {
            string normalizedRoot = root.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
            return path.StartsWith(normalizedRoot, StringComparison.OrdinalIgnoreCase) ? path.Substring(normalizedRoot.Length) : path;
        }
    }

    internal sealed class GrepSearchResult
    {
        public GrepSearchResult(List<GrepMatch> matches, int files, int skipped) { Matches = matches; FileCount = files; SkippedCount = skipped; }
        public List<GrepMatch> Matches { get; }
        public int FileCount { get; }
        public int SkippedCount { get; }
    }
}
