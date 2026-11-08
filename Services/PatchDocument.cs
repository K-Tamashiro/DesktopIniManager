using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

namespace DesktopIniManager.Services;

// Crlf: the patch line carried a CR before its LF, i.e. the file line ended with CRLF.
internal sealed record PatchLine(char Kind, string Text, bool NoNewline = false, bool Crlf = false)
{
    internal string Ending => NoNewline ? "" : Crlf ? "\r\n" : "\n";
}

internal sealed class PatchHunk
{
    internal int OldStart { get; init; }
    internal int OldCount { get; init; }
    internal int NewStart { get; init; }
    internal int NewCount { get; init; }
    internal List<PatchLine> Lines { get; } = [];
    internal bool HasDecodingErrors { get; set; }
    internal string[] Before => Lines.Where(line => line.Kind != '+').Select(line => line.Text).ToArray();
    internal string[] After => Lines.Where(line => line.Kind != '-').Select(line => line.Text).ToArray();
    internal string[] BeforeEndings => Lines.Where(line => line.Kind != '+').Select(line => line.Ending).ToArray();
    internal string[] AfterEndings => Lines.Where(line => line.Kind != '-').Select(line => line.Ending).ToArray();
}

internal sealed class PatchFile
{
    internal string OldPath { get; set; }
    internal string NewPath { get; set; }
    internal string UnsupportedReason { get; set; }
    internal List<PatchHunk> Hunks { get; } = [];

    // .rej（適用に失敗したハンク）のエントリ。適用先で完全一致しない前提で、おおよその位置を探す。
    internal bool IsReject { get; set; }

    internal bool IsNew => OldPath == "/dev/null";
    internal string RelativePath => NewPath == "/dev/null" ? OldPath : NewPath;
    /// <summary>
    ///     パッチ エントリの対象パスを返します。
    /// </summary>
    /// <returns>対象の相対パス。対応していないエントリの場合はその旨の文字列。</returns>
    public override string ToString() => RelativePath ?? "(unsupported entry)";
}

internal sealed class PatchDocument
{
    private static readonly Regex HunkHeader = new(@"^@@ -(\d+)(?:,(\d+))? \+(\d+)(?:,(\d+))? @@", RegexOptions.CultureInvariant);
    internal List<PatchFile> Files { get; } = [];
    internal int DecodingErrorLines { get; private set; }
    internal string CommitTitle { get; private set; }
    internal string CommitMessage { get; private set; }
    internal string Author { get; private set; }

    /// <summary>
    ///     ファイルが .patch、.diff、.rej のパッチ ファイルかどうかを判定します。
    /// </summary>
    /// <param name="path">ファイルのパス。</param>
    /// <returns>パッチ ファイルの場合は true。</returns>
    internal static bool IsPatch(string path) =>
        string.Equals(Path.GetExtension(path), ".patch", StringComparison.OrdinalIgnoreCase) ||
        string.Equals(Path.GetExtension(path), ".diff", StringComparison.OrdinalIgnoreCase) ||
        IsReject(path);

    /// <summary>
    ///     ファイルが .rej (git apply --reject や patch が出力する、適用に失敗したハンク) かどうかを判定します。
    /// </summary>
    /// <param name="path">ファイルのパス。</param>
    /// <returns>.rej の場合は true。</returns>
    private static bool IsReject(string path) =>
        string.Equals(Path.GetExtension(path), ".rej", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    ///     パッチ ファイルを読み込んで解析します。32 MB を超えるファイルには対応しません。
    /// </summary>
    /// <param name="path">パッチ ファイルのパス。</param>
    /// <returns>解析したパッチ。</returns>
    internal static PatchDocument Read(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        if (stream.Length > 32 * 1024 * 1024) throw new IOException("Patches over 32 MB are not supported.");
        var bytes = new byte[stream.Length];
        stream.ReadExactly(bytes);
        var result = Parse(bytes);
        if (IsReject(path))
            foreach (var file in result.Files) file.IsReject = true;
        return result;
    }

    /// <summary>
    ///     unified 形式のパッチを解析し、ファイルごとのハンクに分割します。各行は UTF-8、Shift_JIS の順にデコードします。
    /// </summary>
    /// <param name="bytes">パッチのバイト列。</param>
    /// <returns>解析したパッチ。</returns>
    internal static PatchDocument Parse(byte[] bytes)
    {
        if (bytes.Contains((byte)0)) throw new InvalidDataException("A text patch cannot contain NUL bytes.");
        var result = new PatchDocument();
        var utf8 = new UTF8Encoding(false, true);
        var sjis = CodePagesEncodingProvider.Instance.GetEncoding(932, EncoderFallback.ExceptionFallback,
            DecoderFallback.ExceptionFallback) ?? throw new NotSupportedException("Shift-JIS is unavailable.");
        var lines = new List<(string Text, bool Invalid, bool Crlf)>();
        var start = bytes.AsSpan().StartsWith(new byte[] { 239, 187, 191 }) ? 3 : 0;
        for (var i = start; i <= bytes.Length; i++)
        {
            if (i < bytes.Length && bytes[i] != 10) continue;
            var length = i - start;
            var crlf = length > 0 && bytes[i - 1] == 13;
            if (crlf) length--;
            var invalid = false;
            string text;
            try { text = utf8.GetString(bytes, start, length); }
            catch (DecoderFallbackException)
            {
                try { text = sjis.GetString(bytes, start, length); }
                catch (DecoderFallbackException)
                {
                    var replacing = CodePagesEncodingProvider.Instance.GetEncoding(932,
                        EncoderFallback.ExceptionFallback, new DecoderReplacementFallback("\uFFFD"))
                        ?? throw new NotSupportedException("Shift-JIS is unavailable.");
                    text = replacing.GetString(bytes, start, length);
                    invalid = true;
                    result.DecodingErrorLines++;
                }
            }
            lines.Add((text, invalid, crlf));
            start = i + 1;
            if (lines.Count > 200000) throw new InvalidDataException("Patch exceeds 200,000 lines.");
        }

        result.ReadCommitMessage(lines.Select(line => line.Text).ToArray());
        PatchFile file = null;
        PatchHunk hunk = null;
        var oldRemaining = 0;
        var newRemaining = 0;
        for (var i = 0; i < lines.Count; i++)
        {
            var (line, invalid, crlf) = lines[i];
            if (line == "\\ No newline at end of file" && hunk?.Lines.Count > 0)
            {
                hunk.Lines[^1] = hunk.Lines[^1] with { NoNewline = true };
                continue;
            }
            if (oldRemaining > 0 || newRemaining > 0)
            {
                if (line.Length == 0 || line[0] is not (' ' or '+' or '-'))
                    throw new InvalidDataException($"Invalid hunk body at patch line {i + 1}.");
                if (line[0] != '+') oldRemaining--;
                if (line[0] != '-') newRemaining--;
                if (oldRemaining < 0 || newRemaining < 0)
                    throw new InvalidDataException($"Hunk line count mismatch at patch line {i + 1}.");
                if (hunk == null) throw new InvalidDataException($"Hunk body without a header at patch line {i + 1}.");
                hunk.Lines.Add(new PatchLine(line[0], line[1..], false, crlf));
                hunk.HasDecodingErrors |= invalid;
                continue;
            }
            if (line.StartsWith("diff --git ", StringComparison.Ordinal))
            {
                var paths = Regex.Matches(line[11..], "\"(?:\\\\.|[^\"])*\"|[^ ]+");
                file = new PatchFile();
                if (paths.Count == 2)
                {
                    file.OldPath = DecodePath(paths[0].Value);
                    file.NewPath = DecodePath(paths[1].Value);
                }
                result.Files.Add(file);
                hunk = null;
            }
            else if (line.StartsWith("diff a/", StringComparison.Ordinal))
            {
                // git apply --reject の .rej：「diff a/パス b/パス<TAB>(rejected hunks)」の後に --- / +++ なしでハンクが続く。
                // パスは引用符なしで空白を含みうるため、前後が同じパスになる位置で分割する。
                var names = line[5..].Split('\t')[0];
                var middle = names.Length / 2;
                file = new PatchFile { IsReject = true };
                if (names.Length % 2 == 1 && names[middle] == ' ' && names[(middle + 1)..].StartsWith("b/", StringComparison.Ordinal) &&
                    names[2..middle] == names[(middle + 3)..])
                {
                    file.OldPath = names[2..middle];
                    file.NewPath = names[(middle + 3)..];
                }
                else
                {
                    var paths = Regex.Matches(names, "[^ ]+");
                    if (paths.Count == 2)
                    {
                        file.OldPath = DecodePath(paths[0].Value);
                        file.NewPath = DecodePath(paths[1].Value);
                    }
                }
                result.Files.Add(file);
                hunk = null;
            }
            else if (line.StartsWith("--- ", StringComparison.Ordinal) && i + 1 < lines.Count &&
                     lines[i + 1].Text.StartsWith("+++ ", StringComparison.Ordinal))
            {
                if (file == null || file.Hunks.Count > 0)
                {
                    file = new PatchFile();
                    result.Files.Add(file);
                }
                file.OldPath = DecodePath(line[4..].Split('\t')[0]);
                file.NewPath = DecodePath(lines[++i].Text[4..].Split('\t')[0]);
                hunk = null;
            }
            else if (line.StartsWith("@@ ", StringComparison.Ordinal))
            {
                var match = HunkHeader.Match(line);
                if (file == null || !match.Success) throw new InvalidDataException("Invalid unified patch header.");
                int Number(int index, int fallback = 1) => match.Groups[index].Success
                    ? int.Parse(match.Groups[index].Value, CultureInfo.InvariantCulture) : fallback;
                hunk = new PatchHunk { OldStart = Number(1), OldCount = Number(2), NewStart = Number(3), NewCount = Number(4) };
                if (Math.Max(hunk.OldStart, hunk.NewStart) > 100000 || Math.Max(hunk.OldCount, hunk.NewCount) > 100000)
                    throw new InvalidDataException("Patch line coordinates exceed 100,000 lines.");
                file.Hunks.Add(hunk);
                oldRemaining = hunk.OldCount;
                newRemaining = hunk.NewCount;
            }
            else if (file != null && (line.StartsWith("GIT binary patch", StringComparison.Ordinal) ||
                     line.StartsWith("Binary files ", StringComparison.Ordinal) ||
                     line.Contains("120000", StringComparison.Ordinal) && line.Contains("mode", StringComparison.Ordinal)))
                file.UnsupportedReason = "Binary or symbolic-link patch";
        }
        if (oldRemaining != 0 || newRemaining != 0) throw new InvalidDataException("Truncated patch hunk.");
        foreach (var entry in result.Files)
        {
            if (entry.Hunks.Count == 0) entry.UnsupportedReason ??= "No text hunks (metadata-only change)";
            if (!IsSafeRelativePath(entry.RelativePath) || entry.OldPath != "/dev/null" && !IsSafeRelativePath(entry.OldPath))
                entry.UnsupportedReason = "Unsafe relative path";
        }
        if (result.Files.Count == 0) throw new InvalidDataException("No unified patch entries were found.");
        return result;
    }

    /// <summary>
    ///     引用符と 8 進エスケープを含む Git のパス表記をデコードし、a/ または b/ の接頭辞を取り除きます。
    /// </summary>
    /// <param name="value">パッチ内のパス表記。</param>
    /// <returns>デコードした相対パス。</returns>
    private static string DecodePath(string value)
    {
        if (value.StartsWith('"') && value.EndsWith('"'))
        {
            var bytes = new List<byte>();
            for (var i = 1; i < value.Length - 1; i++)
            {
                var c = value[i];
                if (c == '\\' && i + 1 < value.Length - 1)
                {
                    c = value[++i];
                    if (c is >= '0' and <= '7' && i + 2 < value.Length - 1 &&
                        value[i + 1] is >= '0' and <= '7' && value[i + 2] is >= '0' and <= '7')
                    {
                        bytes.Add(Convert.ToByte(value.Substring(i, 3), 8));
                        i += 2;
                        continue;
                    }
                    c = c switch { 't' => '\t', 'n' => '\n', 'r' => '\r', _ => c };
                }
                bytes.AddRange(Encoding.UTF8.GetBytes(c.ToString()));
            }
            value = Encoding.UTF8.GetString(bytes.ToArray());
        }
        return value.StartsWith("a/", StringComparison.Ordinal) || value.StartsWith("b/", StringComparison.Ordinal)
            ? value[2..] : value;
    }

    /// <summary>
    ///     git format-patch 形式のメール ヘッダーから件名、作成者、コミット メッセージを読み取ります。
    /// </summary>
    /// <param name="lines">パッチの全行。</param>
    private void ReadCommitMessage(string[] lines)
    {
        if (lines.Length == 0 || !lines[0].StartsWith("From ", StringComparison.Ordinal)) return;
        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        string key = null;
        var index = 1;
        for (; index < lines.Length && lines[index].Length > 0; index++)
        {
            var line = lines[index];
            if (char.IsWhiteSpace(line[0]) && key != null) { headers[key] += " " + line.Trim(); continue; }
            var colon = line.IndexOf(':');
            if (colon <= 0) continue;
            key = line[..colon];
            headers[key] = line[(colon + 1)..].Trim();
        }
        CommitTitle = headers.TryGetValue("Subject", out var subject) ? DecodeHeader(subject) : null;
        Author = headers.TryGetValue("From", out var author) ? DecodeHeader(author) : null;
        var body = new List<string>();
        for (index++; index < lines.Length; index++)
        {
            var line = lines[index];
            if (line == "---" || line.StartsWith("diff --git ", StringComparison.Ordinal) || line.StartsWith("--- ", StringComparison.Ordinal)) break;
            body.Add(line);
        }
        CommitMessage = string.Join(Environment.NewLine, body).Trim();
    }

    /// <summary>
    ///     MIME エンコードされたヘッダー値 (=?charset?B/Q?...?=) をデコードします。
    /// </summary>
    /// <param name="value">ヘッダー値。</param>
    /// <returns>デコードした文字列。</returns>
    private static string DecodeHeader(string value)
    {
        value = Regex.Replace(value, @"(?<=\?=)\s+(?==\?)", "");
        return Regex.Replace(value, @"=\?([^?]+)\?([bBqQ])\?([^?]*)\?=", match =>
        {
            try
            {
                var encoding = match.Groups[1].Value.Equals("utf-8", StringComparison.OrdinalIgnoreCase)
                    ? Encoding.UTF8 : Encoding.GetEncoding(match.Groups[1].Value);
                if (match.Groups[2].Value.Equals("b", StringComparison.OrdinalIgnoreCase))
                    return encoding.GetString(Convert.FromBase64String(match.Groups[3].Value));
                var bytes = new List<byte>();
                var payload = match.Groups[3].Value;
                for (var i = 0; i < payload.Length; i++)
                    if (payload[i] == '=' && i + 2 < payload.Length && byte.TryParse(payload.AsSpan(i + 1, 2),
                            NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var decoded))
                    { bytes.Add(decoded); i += 2; }
                    else bytes.Add((byte)(payload[i] == '_' ? ' ' : payload[i]));
                return encoding.GetString(bytes.ToArray());
            }
            catch (FormatException) { return match.Value; }
            catch (ArgumentException) { return match.Value; }
        });
    }

    /// <summary>
    ///     パスが、絶対パス・ドライブ指定・「.」「..」・無効な文字を含まない安全な相対パスかどうかを判定します。
    /// </summary>
    /// <param name="path">判定するパス。</param>
    /// <returns>安全な相対パスの場合は true。</returns>
    internal static bool IsSafeRelativePath(string path) => !string.IsNullOrWhiteSpace(path) &&
        !Path.IsPathRooted(path) && !path.Contains(':') &&
        path.Split('/', '\\').All(part => part.Length > 0 && part is not ("." or "..") &&
            part.IndexOfAny(Path.GetInvalidFileNameChars()) < 0 && !part.EndsWith('.') && !part.EndsWith(' '));
}
