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

    /// <summary>
    ///     検索範囲内でプロファイルの拡張子に一致するファイルを並列に走査し、各行から検索語に一致する箇所を集めます。
    /// </summary>
    /// <param name="scopes">検索対象のルート フォルダー。</param>
    /// <param name="profile">対象とする拡張子を定義した言語プロファイル。</param>
    /// <param name="query">検索語または正規表現。</param>
    /// <param name="regex">検索語を正規表現として扱う場合は true。</param>
    /// <param name="matchCase">大文字と小文字を区別する場合は true。</param>
    /// <param name="wholeWord">単語単位で一致させる場合は true。</param>
    /// <param name="progress">処理済みファイル数と総ファイル数を受け取る進捗通知。</param>
    /// <param name="token">キャンセル トークン。</param>
    /// <param name="matchFound">一致が見つかるたびに呼び出されるコールバック。</param>
    /// <returns>並べ替え済みの一致結果、対象ファイル数、読み飛ばしたファイル数。</returns>
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

    /// <summary>
    ///     検索条件から、タイムアウト付きのコンパイル済み正規表現を作成します。
    /// </summary>
    /// <param name="query">検索語または正規表現。</param>
    /// <param name="regex">検索語を正規表現として扱う場合は true。</param>
    /// <param name="matchCase">大文字と小文字を区別する場合は true。</param>
    /// <param name="wholeWord">単語境界で囲む場合は true。</param>
    /// <returns>作成した正規表現。</returns>
    private static Regex BuildMatcher(string query, bool regex, bool matchCase, bool wholeWord)
    {
        var pattern = regex ? query : Regex.Escape(query);
        if (wholeWord) pattern = @"\b(?:" + pattern + @")\b";
        var options = RegexOptions.Compiled;
        if (!matchCase) options |= RegexOptions.IgnoreCase;
        return new Regex(pattern, options, TimeSpan.FromSeconds(2));
    }

    /// <summary>
    ///     各検索範囲から対象拡張子のファイルを重複なく収集します。
    /// </summary>
    /// <param name="scopes">検索対象のルート フォルダー。</param>
    /// <param name="extensions">対象とする拡張子。</param>
    /// <param name="token">キャンセル トークン。</param>
    /// <returns>収集したファイル パスの一覧。</returns>
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

    /// <summary>
    ///     ネイティブ列挙でフォルダーを再帰的に走査し、除外フォルダーと隠しフォルダーを除いて対象拡張子のファイルを追加します。
    /// </summary>
    /// <param name="root">走査を開始するフォルダー。</param>
    /// <param name="extensions">対象とする拡張子の集合。</param>
    /// <param name="files">見つかったファイルの追加先。</param>
    /// <param name="token">キャンセル トークン。</param>
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

    /// <summary>
    ///     StreamReader から 1 行ずつ読み取り、読み終えたらリーダーを破棄します。
    /// </summary>
    /// <param name="reader">読み取り元のリーダー。</param>
    /// <returns>行の列挙。</returns>
    private static IEnumerable<string> StreamLines(StreamReader reader)
    {
        using (reader)
        {
            string line;
            while ((line = reader.ReadLine()) != null) yield return line;
        }
    }

    /// <summary>
    ///     読み込み済みのテキストを CR、LF、CRLF で行に分割して列挙します。
    /// </summary>
    /// <param name="text">分割するテキスト。</param>
    /// <returns>行の列挙。</returns>
    private static IEnumerable<string> TextLines(string text)
    {
        // StringReader.ReadLine splits exactly like StreamReader.ReadLine (CR, LF, CRLF).
        using var reader = new StringReader(text);
        string line;
        while ((line = reader.ReadLine()) != null) yield return line;
    }

    /// <summary>
    ///     先頭のバイト列から BOM を判定し、BOM がなければ UTF-8 として有効かどうかで UTF-8 または Shift_JIS を選びます。
    /// </summary>
    /// <param name="stream">判定するストリーム。</param>
    /// <returns>推定したエンコーディング。</returns>
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

    /// <summary>
    ///     ファイルが属する検索範囲を、長いパスから順に探します。
    /// </summary>
    /// <param name="path">ファイルのパス。</param>
    /// <param name="orderedScopes">パスの長い順に並べた検索範囲。</param>
    /// <returns>属する検索範囲。見つからない場合は null。</returns>
    private static string FindScope(string path, IReadOnlyList<string> orderedScopes)
    {
        for (var i = 0; i < orderedScopes.Count; i++)
            if (IsUnderPath(path, orderedScopes[i]))
                return orderedScopes[i];
        return null;
    }

    /// <summary>
    ///     パスがルート フォルダー配下にあるかどうかを判定します。
    /// </summary>
    /// <param name="path">判定するパス。</param>
    /// <param name="root">ルート フォルダー。</param>
    /// <returns>配下にある場合は true。</returns>
    private static bool IsUnderPath(string path, string root)
    {
        var normalizedRoot = root.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        return path.StartsWith(normalizedRoot, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    ///     ルート フォルダーからの相対パスを作成します。
    /// </summary>
    /// <param name="root">基準となるルート フォルダー。</param>
    /// <param name="path">変換するパス。</param>
    /// <returns>相対パス。ルート配下でない場合は元のパス。</returns>
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
    /// <summary>
    ///     Grep 検索の結果を作成します。
    /// </summary>
    /// <param name="matches">一致した箇所の一覧。</param>
    /// <param name="files">検索したファイル数。</param>
    /// <param name="skipped">読み飛ばしたファイル数。</param>
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