using DesktopIniManager.Models;
using FastVolumeIndex;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace DesktopIniManager.Services;

internal sealed class CodeGrepService
{
    private static readonly HashSet<string> IgnoredDirectories = new(StringComparer.OrdinalIgnoreCase)
    {
        ".git", ".vs", ".idea", "bin", "obj", "node_modules", "packages", "vendor", "dist", "build", "target",
        "coverage"
    };

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
        var processed = 0;
        var skipped = 0;
        var matcher = BuildMatcher(query, regex, matchCase, wholeWord);
        // A literal (escaped) pattern cannot use ^/$ or span lines, so a match anywhere in the
        // whole text is exactly a match in some line. User regexes are always tested per line.
        var prefilter = regex ? null : matcher;
        var options = new ParallelOptions { CancellationToken = token, MaxDegreeOfParallelism = 4 };

        Parallel.ForEach(files, options, file =>
        {
            try
            {
                var lines = ReadLines(file, prefilter, out var tooLarge);
                if (tooLarge)
                {
                    Interlocked.Increment(ref skipped);
                    return;
                }

                if (lines == null) return; // The literal query does not occur in this file.
                var scope = FindScope(file, orderedScopes);
                var lineNumber = 0;
                foreach (var line in lines)
                {
                    options.CancellationToken.ThrowIfCancellationRequested();
                    lineNumber++;
                    var match = matcher.Match(line);
                    if (!match.Success) continue;
                    var found = new GrepMatch
                    {
                        ScopeName = Path.GetFileName((scope ?? string.Empty).TrimEnd(Path.DirectorySeparatorChar)),
                        FilePath = file,
                        RelativePath = scope == null ? file : MakeRelativePath(scope, file),
                        LineNumber = lineNumber,
                        ColumnNumber = match.Index + 1,
                        LineText = line.Trim(),
                        Highlight = match.Value
                    };
                    matches.Add(found);
                    matchFound?.Invoke(found);
                }
            }
            catch (IOException)
            {
                Interlocked.Increment(ref skipped);
            }
            catch (UnauthorizedAccessException)
            {
                Interlocked.Increment(ref skipped);
            }
            finally
            {
                var done = Interlocked.Increment(ref processed);
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
        var pattern = regex ? query : Regex.Escape(query);
        if (wholeWord) pattern = @"\b(?:" + pattern + @")\b";
        var options = RegexOptions.Compiled;
        if (!matchCase) options |= RegexOptions.IgnoreCase;
        return new Regex(pattern, options, TimeSpan.FromSeconds(2));
    }

    private static List<string> CollectFiles(IReadOnlyList<string> scopes, string[] extensions, CancellationToken token)
    {
        var files = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var extensionSet = new HashSet<string>(extensions ?? Array.Empty<string>(), StringComparer.OrdinalIgnoreCase);
        foreach (var scope in scopes)
        {
            token.ThrowIfCancellationRequested();
            if (string.IsNullOrWhiteSpace(scope) || !Directory.Exists(scope)) continue;
            CollectFilesNative(scope, extensionSet, files, token);
        }

        return files.ToList();
    }

    private static void CollectFilesNative(string root, HashSet<string> extensions, HashSet<string> files,
        CancellationToken token)
    {
        var pending = new Stack<string>();
        pending.Push(root);
        while (pending.Count > 0)
        {
            token.ThrowIfCancellationRequested();
            var folder = pending.Pop();
            try
            {
                foreach (var entry in VolumePathIndex.EnumerateNativeDirectory(folder, token))
                {
                    token.ThrowIfCancellationRequested();
                    var path = Path.Combine(folder, entry.Name);
                    if ((entry.Attributes & FileAttributes.Directory) != 0)
                    {
                        if (IgnoredDirectories.Contains(entry.Name)) continue;
                        if ((entry.Attributes & FileAttributes.Hidden) != 0) continue;
                        pending.Push(path);
                    }
                    else if (extensions.Contains(Path.GetExtension(entry.Name)))
                    {
                        files.Add(path);
                    }
                }
            }
            // Native enumeration reports directory access/I/O failures as Win32Exception.
            // Preserve Grep's existing behavior: skip the unreadable part and keep searching.
            catch (Win32Exception)
            {
            }
            catch (IOException)
            {
            }
            catch (UnauthorizedAccessException)
            {
            }
        }
    }

    private const long MaximumFileSize = 64L * 1024 * 1024;

    // Files up to this size are read whole so a literal query can reject non-matching files
    // without splitting them into lines; larger files are still streamed line by line.
    private const long WholeReadLimit = 4L * 1024 * 1024;

    /// <summary>
    ///     Opens the file once for size, encoding detection and reading. Returns null when the
    ///     whole-text prefilter proves the file has no match.
    /// </summary>
    private static IEnumerable<string> ReadLines(string path, Regex prefilter, out bool tooLarge)
    {
        var stream = File.Open(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        try
        {
            var length = stream.Length;
            tooLarge = length > MaximumFileSize;
            if (tooLarge)
            {
                stream.Dispose();
                return null;
            }

            var encoding = DetectEncoding(stream);
            stream.Position = 0;
            var reader = new StreamReader(stream, encoding, true);
            if (prefilter == null || length > WholeReadLimit) return StreamLines(reader);
            string text;
            using (reader)
            {
                text = reader.ReadToEnd();
            }

            return prefilter.IsMatch(text) ? TextLines(text) : null;
        }
        catch
        {
            stream.Dispose();
            throw;
        }
    }

    private static IEnumerable<string> StreamLines(StreamReader reader)
    {
        using (reader)
        {
            string line;
            while ((line = reader.ReadLine()) != null) yield return line;
        }
    }

    private static IEnumerable<string> TextLines(string text)
    {
        // StringReader.ReadLine splits exactly like StreamReader.ReadLine (CR, LF, CRLF).
        using var reader = new StringReader(text);
        string line;
        while ((line = reader.ReadLine()) != null) yield return line;
    }

    private static Encoding DetectEncoding(Stream stream)
    {
        var sample = new byte[4096];
        var count = stream.ReadAtLeast(sample, sample.Length, false);

        if (count >= 2 && sample[0] == 0xFF && sample[1] == 0xFE) return Encoding.Unicode;
        if (count >= 2 && sample[0] == 0xFE && sample[1] == 0xFF) return Encoding.BigEndianUnicode;
        if (count >= 3 && sample[0] == 0xEF && sample[1] == 0xBB && sample[2] == 0xBF) return Encoding.UTF8;
        try
        {
            // Do not flush when the sample was cut at 4096 bytes: a multi-byte character split
            // at the boundary is incomplete, not invalid, and must not force Shift-JIS.
            new UTF8Encoding(false, true).GetDecoder()
                .GetCharCount(sample, 0, count, count < sample.Length);
            return new UTF8Encoding(false);
        }
        catch (DecoderFallbackException)
        {
            return CodePagesEncodingProvider.Instance.GetEncoding(932);
        }
    }

    private static string FindScope(string path, IReadOnlyList<string> orderedScopes)
    {
        for (var i = 0; i < orderedScopes.Count; i++)
            if (IsUnderPath(path, orderedScopes[i]))
                return orderedScopes[i];
        return null;
    }

    private static bool IsUnderPath(string path, string root)
    {
        var normalizedRoot = root.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        return path.StartsWith(normalizedRoot, StringComparison.OrdinalIgnoreCase);
    }

    private static string MakeRelativePath(string root, string path)
    {
        var normalizedRoot = root.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        return path.StartsWith(normalizedRoot, StringComparison.OrdinalIgnoreCase)
            ? path.Substring(normalizedRoot.Length)
            : path;
    }
}

internal sealed class GrepSearchResult
{
    public GrepSearchResult(List<GrepMatch> matches, int files, int skipped)
    {
        Matches = matches;
        FileCount = files;
        SkippedCount = skipped;
    }

    public List<GrepMatch> Matches { get; }
    public int FileCount { get; }
    public int SkippedCount { get; }
}