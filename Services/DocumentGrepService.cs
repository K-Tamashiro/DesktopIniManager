using DesktopIniManager.Models;
using ExcelDataReader;
using FastVolumeIndex;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Xml;
using UglyToad.PdfPig;

namespace DesktopIniManager.Services;

internal sealed class DocumentGrepService
{
    private static readonly string[] Extensions = { ".xls", ".xlsx", ".xlsm", ".pdf" };

    private static readonly HashSet<string> IgnoredDirectories = new(StringComparer.OrdinalIgnoreCase)
    {
        ".git", ".vs", ".idea", "bin", "obj", "node_modules", "packages", "vendor", "dist", "build", "target",
        "coverage"
    };

    /// <summary>
    ///     ファイルが Grep の対象となるドキュメント (Excel または PDF) かどうかを判定します。
    /// </summary>
    /// <param name="path">ファイルのパス。</param>
    /// <returns>対象のドキュメントの場合は true。</returns>
    internal static bool IsDocumentPath(string path)
    {
        return Extensions.Contains(Path.GetExtension(path ?? string.Empty), StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>
    ///     検索範囲内の Excel と PDF を並列に走査し、セルやページのテキストから検索語に一致する箇所を集めます。
    /// </summary>
    /// <param name="scopes">検索対象のルート フォルダー。</param>
    /// <param name="extensions">対象とする拡張子。null の場合はすべての対応形式。</param>
    /// <param name="query">検索語または正規表現。</param>
    /// <param name="regex">検索語を正規表現として扱う場合は true。</param>
    /// <param name="matchCase">大文字と小文字を区別する場合は true。</param>
    /// <param name="wholeWord">単語単位で一致させる場合は true。</param>
    /// <param name="progress">処理済みファイル数と総ファイル数を受け取る進捗通知。</param>
    /// <param name="token">キャンセル トークン。</param>
    /// <param name="matchFound">一致が見つかるたびに呼び出されるコールバック。</param>
    /// <returns>並べ替え済みの一致結果、対象ファイル数、読み飛ばしたファイル数。</returns>
    public GrepSearchResult Search(IReadOnlyList<string> scopes, string[] extensions, string query,
        bool regex, bool matchCase, bool wholeWord, Action<int, int> progress, CancellationToken token,
        Action<GrepMatch> matchFound = null)
    {
        token.ThrowIfCancellationRequested();
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        var files = CollectFiles(scopes, extensions, token);
        var orderedScopes = scopes
            .Where(root => !string.IsNullOrWhiteSpace(root))
            .OrderByDescending(root => root.Length)
            .ToArray();
        var matches = new ConcurrentBag<GrepMatch>();
        var processed = 0;
        var skipped = 0;
        var matcher = BuildMatcher(query, regex, matchCase, wholeWord);
        var options = new ParallelOptions { CancellationToken = token, MaxDegreeOfParallelism = 2 };

        Parallel.ForEach(files, options, file =>
        {
            try
            {
                if (new FileInfo(file).Length > 64L * 1024 * 1024)
                {
                    Interlocked.Increment(ref skipped);
                    return;
                }

                var scope = FindScope(file, orderedScopes);
                var extension = Path.GetExtension(file);
                if (extension.Equals(".pdf", StringComparison.OrdinalIgnoreCase))
                    SearchPdf(file, scope, matcher, matches, matchFound, token);
                else if (extension.Equals(".xls", StringComparison.OrdinalIgnoreCase))
                    SearchXls(file, scope, matcher, matches, matchFound, token);
                else
                    SearchOpenXml(file, scope, matcher, matches, matchFound, token);
            }
            catch (IOException)
            {
                Interlocked.Increment(ref skipped);
            }
            catch (UnauthorizedAccessException)
            {
                Interlocked.Increment(ref skipped);
            }
            catch (InvalidDataException)
            {
                Interlocked.Increment(ref skipped);
            }
            catch (XmlException)
            {
                Interlocked.Increment(ref skipped);
            }
            finally
            {
                var done = Interlocked.Increment(ref processed);
                if (((done & 15) == 0 || done == files.Count) && progress != null) progress(done, files.Count);
            }
        });

        token.ThrowIfCancellationRequested();
        return new GrepSearchResult(matches.OrderBy(item => item.ScopeName, StringComparer.CurrentCultureIgnoreCase)
            .ThenBy(item => item.RelativePath, StringComparer.CurrentCultureIgnoreCase)
            .ThenBy(item => item.LineNumber)
            .ThenBy(item => item.ColumnNumber).ToList(), files.Count, skipped);
    }

    /// <summary>
    ///     PDF の各ページのテキストを行に分割して検索します。行番号にはページ番号を使います。
    /// </summary>
    /// <param name="path">PDF ファイルのパス。</param>
    /// <param name="scope">ファイルが属する検索範囲。</param>
    /// <param name="matcher">検索に使う正規表現。</param>
    /// <param name="matches">一致結果の追加先。</param>
    /// <param name="matchFound">一致が見つかるたびに呼び出されるコールバック。</param>
    /// <param name="token">キャンセル トークン。</param>
    private static void SearchPdf(string path, string scope, Regex matcher, ConcurrentBag<GrepMatch> matches,
        Action<GrepMatch> matchFound, CancellationToken token)
    {
        using (var document = PdfDocument.Open(path))
        {
            foreach (var page in document.GetPages())
            {
                token.ThrowIfCancellationRequested();
                var text = page.Text;
                if (text.Length == 0) continue;
                var lines = text.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);
                if (lines.Length == 0) lines = new[] { text };
                for (var i = 0; i < lines.Length; i++)
                {
                    token.ThrowIfCancellationRequested();
                    var match = matcher.Match(lines[i]);
                    if (!match.Success) continue;
                    Add(matches, matchFound, path, scope, page.Number, 1, "p." + page.Number + " " + lines[i].Trim(),
                        match.Value);
                }
            }
        }
    }

    /// <summary>
    ///     旧形式の Excel ブック (.xls) の全シート・全セルを検索します。
    /// </summary>
    /// <param name="path">Excel ファイルのパス。</param>
    /// <param name="scope">ファイルが属する検索範囲。</param>
    /// <param name="matcher">検索に使う正規表現。</param>
    /// <param name="matches">一致結果の追加先。</param>
    /// <param name="matchFound">一致が見つかるたびに呼び出されるコールバック。</param>
    /// <param name="token">キャンセル トークン。</param>
    private static void SearchXls(string path, string scope, Regex matcher, ConcurrentBag<GrepMatch> matches,
        Action<GrepMatch> matchFound, CancellationToken token)
    {
        using (var stream = File.Open(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
        using (var reader = ExcelReaderFactory.CreateReader(stream))
        {
            do
            {
                token.ThrowIfCancellationRequested();
                var sheet = string.IsNullOrEmpty(reader.Name) ? "Sheet" : reader.Name;
                var row = 0;
                while (reader.Read())
                {
                    token.ThrowIfCancellationRequested();
                    row++;
                    for (var column = 0; column < reader.FieldCount; column++)
                    {
                        // Empty cells return null.
                        var value = Convert.ToString(reader.GetValue(column));
                        if (string.IsNullOrEmpty(value)) continue;
                        var match = matcher.Match(value);
                        if (!match.Success) continue;
                        var cell = ColumnName(column + 1) + row;
                        Add(matches, matchFound, path, scope, row, column + 1,
                            "[" + sheet + "][" + cell + "] " + value.Trim(), match.Value);
                    }
                }
            } while (reader.NextResult());
        }
    }

    /// <summary>
    ///     Open XML 形式の Excel ブック (.xlsx/.xlsm) のシート XML を直接読み取り、セルの値を検索します。共有文字列の照合結果は再利用します。
    /// </summary>
    /// <param name="path">Excel ファイルのパス。</param>
    /// <param name="scope">ファイルが属する検索範囲。</param>
    /// <param name="matcher">検索に使う正規表現。</param>
    /// <param name="matches">一致結果の追加先。</param>
    /// <param name="matchFound">一致が見つかるたびに呼び出されるコールバック。</param>
    /// <param name="token">キャンセル トークン。</param>
    private static void SearchOpenXml(string path, string scope, Regex matcher, ConcurrentBag<GrepMatch> matches,
        Action<GrepMatch> matchFound, CancellationToken token)
    {
        using (var stream = File.Open(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
        using (var zip = new ZipArchive(stream, ZipArchiveMode.Read))
        {
            var strings = ReadSharedStrings(zip, token);
            // Shared strings are referenced by many cells; match each one at most once.
            var sharedMatches = new Match[strings.Count];
            foreach (var sheet in ReadSheets(zip))
            {
                token.ThrowIfCancellationRequested();
                var entry = zip.GetEntry(sheet.Path);
                if (entry == null) continue;
                using (var sheetStream = entry.Open())
                using (var reader = XmlReader.Create(sheetStream,
                           new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit }))
                {
                    while (reader.Read())
                    {
                        token.ThrowIfCancellationRequested();
                        if (reader.NodeType != XmlNodeType.Element || reader.LocalName != "c") continue;
                        var address = reader.GetAttribute("r");
                        var type = reader.GetAttribute("t");
                        var value = ReadCellValue(reader);
                        if (string.IsNullOrEmpty(value)) continue;
                        Match match;
                        if (type == "s" && int.TryParse(value, out var index) && index >= 0 && index < strings.Count)
                        {
                            value = strings[index];
                            match = sharedMatches[index] ??= matcher.Match(value);
                        }
                        else
                        {
                            match = matcher.Match(value);
                        }

                        if (!match.Success) continue;
                        var row = RowOf(address);
                        var column = ColumnOf(address);
                        Add(matches, matchFound, path, scope, row, column,
                            "[" + sheet.Name + "][" + address + "] " + value.Trim(), match.Value);
                    }
                }
            }
        }
    }

    /// <summary>
    ///     ブックの共有文字列テーブル (sharedStrings.xml) を読み込みます。
    /// </summary>
    /// <param name="zip">ブックの ZIP アーカイブ。</param>
    /// <param name="token">キャンセル トークン。</param>
    /// <returns>インデックス順の共有文字列。</returns>
    private static List<string> ReadSharedStrings(ZipArchive zip, CancellationToken token)
    {
        var result = new List<string>();
        var entry = zip.GetEntry("xl/sharedStrings.xml");
        if (entry == null) return result;
        using (var stream = entry.Open())
        using (var reader = XmlReader.Create(stream, new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit }))
        {
            while (reader.Read())
            {
                token.ThrowIfCancellationRequested();
                if (reader.NodeType != XmlNodeType.Element || reader.LocalName != "si") continue;
                result.Add(ReadSharedString(reader));
            }
        }

        return result;
    }

    /// <summary>
    ///     共有文字列の si 要素から、すべてのテキスト ランを連結した文字列を読み取ります。
    /// </summary>
    /// <param name="reader">si 要素に位置する XML リーダー。</param>
    /// <returns>共有文字列。</returns>
    private static string ReadSharedString(XmlReader reader)
    {
        if (reader.IsEmptyElement) return string.Empty;
        var text = new StringBuilder();
        using (var subtree = reader.ReadSubtree())
        {
            while (subtree.Read())
                if (subtree.NodeType == XmlNodeType.Element && subtree.LocalName == "t")
                    text.Append(subtree.ReadElementContentAsString());
        }

        return text.ToString();
    }

    /// <summary>
    ///     セル要素から値を読み取ります。インライン文字列がある場合はそちらを優先します。
    /// </summary>
    /// <param name="reader">c 要素に位置する XML リーダー。</param>
    /// <returns>セルの値。値がない場合は null。</returns>
    private static string ReadCellValue(XmlReader reader)
    {
        if (reader.IsEmptyElement) return null;
        string value = null;
        StringBuilder inline = null; // Only inline-string cells need it.
        using (var subtree = reader.ReadSubtree())
        {
            while (subtree.Read())
            {
                if (subtree.NodeType != XmlNodeType.Element) continue;
                if (subtree.LocalName == "v") value = subtree.ReadElementContentAsString();
                else if (subtree.LocalName == "t")
                    (inline ??= new StringBuilder()).Append(subtree.ReadElementContentAsString());
            }
        }

        return inline is { Length: > 0 } ? inline.ToString() : value;
    }

    /// <summary>
    ///     workbook.xml からシート名と、対応するシート XML のパスを読み取ります。
    /// </summary>
    /// <param name="zip">ブックの ZIP アーカイブ。</param>
    /// <returns>シートの一覧。</returns>
    private static List<SheetRef> ReadSheets(ZipArchive zip)
    {
        var names = new List<SheetRef>();
        var entry = zip.GetEntry("xl/workbook.xml");
        if (entry == null) return names;
        var targets = ReadSheetTargets(zip);
        using (var stream = entry.Open())
        using (var reader = XmlReader.Create(stream, new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit }))
        {
            while (reader.Read())
            {
                if (reader.NodeType != XmlNodeType.Element || reader.LocalName != "sheet") continue;
                var name = reader.GetAttribute("name") ?? "Sheet";
                var id = reader.GetAttribute("id",
                             "http://schemas.openxmlformats.org/officeDocument/2006/relationships")
                         ?? reader.GetAttribute("id", "http://purl.oclc.org/ooxml/officeDocument/relationships");
                string target;
                if (id != null && targets.TryGetValue(id, out target))
                    names.Add(new SheetRef { Name = name, Path = target });
            }
        }

        return names;
    }

    /// <summary>
    ///     workbook.xml.rels からリレーションシップ ID とワークシート XML のパスの対応を読み取ります。
    /// </summary>
    /// <param name="zip">ブックの ZIP アーカイブ。</param>
    /// <returns>リレーションシップ ID をキーとするシートのパス。</returns>
    private static Dictionary<string, string> ReadSheetTargets(ZipArchive zip)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var entry = zip.GetEntry("xl/_rels/workbook.xml.rels");
        if (entry == null) return result;
        using (var stream = entry.Open())
        using (var reader = XmlReader.Create(stream, new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit }))
        {
            while (reader.Read())
            {
                if (reader.NodeType != XmlNodeType.Element || reader.LocalName != "Relationship") continue;
                var id = reader.GetAttribute("Id");
                var target = reader.GetAttribute("Target");
                if (string.IsNullOrEmpty(id) || string.IsNullOrEmpty(target)) continue;
                if (target.IndexOf("worksheet", StringComparison.OrdinalIgnoreCase) < 0) continue;
                target = target.Replace('\\', '/').TrimStart('/');
                if (!target.StartsWith("xl/", StringComparison.OrdinalIgnoreCase)) target = "xl/" + target;
                result[id] = target;
            }
        }

        return result;
    }

    /// <summary>
    ///     一致結果を作成して追加し、コールバックに通知します。
    /// </summary>
    /// <param name="matches">一致結果の追加先。</param>
    /// <param name="matchFound">一致が見つかるたびに呼び出されるコールバック。</param>
    /// <param name="path">ファイルのパス。</param>
    /// <param name="scope">ファイルが属する検索範囲。</param>
    /// <param name="line">行番号 (Excel では行、PDF ではページ)。</param>
    /// <param name="column">列番号。</param>
    /// <param name="text">表示するテキスト。</param>
    /// <param name="highlight">一致した文字列。</param>
    private static void Add(ConcurrentBag<GrepMatch> matches, Action<GrepMatch> matchFound, string path, string scope,
        int line, int column, string text, string highlight)
    {
        var found = new GrepMatch
        {
            ScopeName = Path.GetFileName((scope ?? string.Empty).TrimEnd(Path.DirectorySeparatorChar)),
            FilePath = path,
            RelativePath = scope == null ? path : MakeRelativePath(scope, path),
            LineNumber = line,
            ColumnNumber = column,
            LineText = text,
            Highlight = highlight
        };
        matches.Add(found);
        matchFound?.Invoke(found);
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
    ///     各検索範囲から、指定された拡張子のうち対応形式のファイルを収集します。除外フォルダーと隠しフォルダーは走査しません。
    /// </summary>
    /// <param name="scopes">検索対象のルート フォルダー。</param>
    /// <param name="extensions">対象とする拡張子。</param>
    /// <param name="token">キャンセル トークン。</param>
    /// <returns>収集したファイル パスの一覧。</returns>
    private static List<string> CollectFiles(IReadOnlyList<string> scopes, string[] extensions, CancellationToken token)
    {
        var files = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var allowed = new HashSet<string>(Extensions, StringComparer.OrdinalIgnoreCase);
        var extensionSet = new HashSet<string>(
            (extensions ?? Extensions).Select(NormalizeExtension).Where(allowed.Contains),
            StringComparer.OrdinalIgnoreCase);
        if (extensionSet.Count == 0) return new List<string>();
        foreach (var scope in scopes)
        {
            token.ThrowIfCancellationRequested();
            if (string.IsNullOrWhiteSpace(scope) || !Directory.Exists(scope)) continue;
            var pending = new Stack<string>();
            pending.Push(scope);
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
                        else if (extensionSet.Contains(Path.GetExtension(entry.Name)))
                        {
                            files.Add(path);
                        }
                    }
                }
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

        return files.ToList();
    }

    /// <summary>
    ///     拡張子の前後の空白を取り除き、先頭にドットを付けます。
    /// </summary>
    /// <param name="extension">正規化する拡張子。</param>
    /// <returns>正規化した拡張子。</returns>
    private static string NormalizeExtension(string extension)
    {
        if (string.IsNullOrWhiteSpace(extension)) return string.Empty;
        extension = extension.Trim();
        return extension.StartsWith(".") ? extension : "." + extension;
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

    /// <summary>
    ///     「B12」のようなセル アドレスから行番号を取得します。
    /// </summary>
    /// <param name="address">セル アドレス。</param>
    /// <returns>行番号。アドレスがない場合は 0。</returns>
    private static int RowOf(string address)
    {
        if (string.IsNullOrEmpty(address)) return 0;
        var value = 0;
        for (var i = 0; i < address.Length; i++)
            if (char.IsDigit(address[i]))
                value = value * 10 + (address[i] - '0');
        return value;
    }

    /// <summary>
    ///     「B12」のようなセル アドレスから列番号を取得します。
    /// </summary>
    /// <param name="address">セル アドレス。</param>
    /// <returns>1 から始まる列番号。アドレスがない場合は 0。</returns>
    private static int ColumnOf(string address)
    {
        if (string.IsNullOrEmpty(address)) return 0;
        var value = 0;
        for (var i = 0; i < address.Length; i++)
        {
            var c = address[i];
            if (!char.IsLetter(c)) break;
            value = value * 26 + (char.ToUpperInvariant(c) - 'A') + 1;
        }

        return value;
    }

    /// <summary>
    ///     列番号を「A」「AB」のような列名に変換します。
    /// </summary>
    /// <param name="index">1 から始まる列番号。</param>
    /// <returns>列名。</returns>
    private static string ColumnName(int index)
    {
        var chars = new Stack<char>();
        while (index > 0)
        {
            index--;
            chars.Push((char)('A' + index % 26));
            index /= 26;
        }

        return new string(chars.ToArray());
    }

    private sealed class SheetRef
    {
        public string Name;
        public string Path;
    }
}