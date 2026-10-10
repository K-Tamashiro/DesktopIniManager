using DesktopIniManager.Models;
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
using ExcelDataReader;
using UglyToad.PdfPig;

namespace DesktopIniManager.Services;

internal sealed class DocumentGrepService
{
    private static readonly string[] Extensions = { ".xls", ".xlsx", ".xlsm", ".pdf" };
    internal static bool IsDocumentPath(string path) =>
        Extensions.Contains(Path.GetExtension(path ?? string.Empty), StringComparer.OrdinalIgnoreCase);
    private static readonly HashSet<string> IgnoredDirectories = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        { ".git", ".vs", ".idea", "bin", "obj", "node_modules", "packages", "vendor", "dist", "build", "target", "coverage" };

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
        int processed = 0;
        int skipped = 0;
        Regex matcher = BuildMatcher(query, regex, matchCase, wholeWord);
        var options = new ParallelOptions { CancellationToken = token, MaxDegreeOfParallelism = 2 };

        Parallel.ForEach(files, options, file =>
        {
            try
            {
                if (new FileInfo(file).Length > 64L * 1024 * 1024) { Interlocked.Increment(ref skipped); return; }
                string scope = FindScope(file, orderedScopes);
                string extension = Path.GetExtension(file);
                if (extension.Equals(".pdf", StringComparison.OrdinalIgnoreCase))
                    SearchPdf(file, scope, matcher, matches, matchFound, token);
                else if (extension.Equals(".xls", StringComparison.OrdinalIgnoreCase))
                    SearchXls(file, scope, matcher, matches, matchFound, token);
                else
                    SearchOpenXml(file, scope, matcher, matches, matchFound, token);
            }
            catch (IOException) { Interlocked.Increment(ref skipped); }
            catch (UnauthorizedAccessException) { Interlocked.Increment(ref skipped); }
            catch (InvalidDataException) { Interlocked.Increment(ref skipped); }
            catch (XmlException) { Interlocked.Increment(ref skipped); }
            finally
            {
                int done = Interlocked.Increment(ref processed);
                if (((done & 15) == 0 || done == files.Count) && progress != null) progress(done, files.Count);
            }
        });

        token.ThrowIfCancellationRequested();
        return new GrepSearchResult(matches.OrderBy(item => item.ScopeName, StringComparer.CurrentCultureIgnoreCase)
            .ThenBy(item => item.RelativePath, StringComparer.CurrentCultureIgnoreCase)
            .ThenBy(item => item.LineNumber)
            .ThenBy(item => item.ColumnNumber).ToList(), files.Count, skipped);
    }

    private static void SearchPdf(string path, string scope, Regex matcher, ConcurrentBag<GrepMatch> matches, Action<GrepMatch> matchFound, CancellationToken token)
    {
        using (PdfDocument document = PdfDocument.Open(path))
        {
            foreach (var page in document.GetPages())
            {
                token.ThrowIfCancellationRequested();
                string text = page.Text ?? string.Empty;
                if (text.Length == 0) continue;
                string[] lines = text.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);
                if (lines.Length == 0) lines = new[] { text };
                for (int i = 0; i < lines.Length; i++)
                {
                    token.ThrowIfCancellationRequested();
                    Match match = matcher.Match(lines[i]);
                    if (!match.Success) continue;
                    Add(matches, matchFound, path, scope, page.Number, 1, "p." + page.Number + " " + lines[i].Trim(), match.Value);
                }
            }
        }
    }

    private static void SearchXls(string path, string scope, Regex matcher, ConcurrentBag<GrepMatch> matches, Action<GrepMatch> matchFound, CancellationToken token)
    {
        using (var stream = File.Open(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
        using (var reader = ExcelReaderFactory.CreateReader(stream))
        {
            do
            {
                token.ThrowIfCancellationRequested();
                string sheet = string.IsNullOrEmpty(reader.Name) ? "Sheet" : reader.Name;
                int row = 0;
                while (reader.Read())
                {
                    token.ThrowIfCancellationRequested();
                    row++;
                    for (int column = 0; column < reader.FieldCount; column++)
                    {
                        string value = reader.GetValue(column)?.ToString();
                        if (string.IsNullOrEmpty(value)) continue;
                        Match match = matcher.Match(value);
                        if (!match.Success) continue;
                        string cell = ColumnName(column + 1) + row;
                        Add(matches, matchFound, path, scope, row, column + 1, "[" + sheet + "][" + cell + "] " + value.Trim(), match.Value);
                    }
                }
            }
            while (reader.NextResult());
        }
    }

    private static void SearchOpenXml(string path, string scope, Regex matcher, ConcurrentBag<GrepMatch> matches, Action<GrepMatch> matchFound, CancellationToken token)
    {
        using (var stream = File.Open(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
        using (var zip = new ZipArchive(stream, ZipArchiveMode.Read))
        {
            var strings = ReadSharedStrings(zip, token);
            foreach (var sheet in ReadSheets(zip))
            {
                token.ThrowIfCancellationRequested();
                var entry = zip.GetEntry(sheet.Path);
                if (entry == null) continue;
                using (var sheetStream = entry.Open())
                using (var reader = XmlReader.Create(sheetStream, new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit }))
                {
                    while (reader.Read())
                    {
                        token.ThrowIfCancellationRequested();
                        if (reader.NodeType != XmlNodeType.Element || reader.LocalName != "c") continue;
                        string address = reader.GetAttribute("r");
                        string type = reader.GetAttribute("t");
                        string value = ReadCellValue(reader);
                        if (string.IsNullOrEmpty(value)) continue;
                        if (type == "s" && int.TryParse(value, out int index) && index >= 0 && index < strings.Count)
                            value = strings[index];
                        Match match = matcher.Match(value);
                        if (!match.Success) continue;
                        int row = RowOf(address);
                        int column = ColumnOf(address);
                        Add(matches, matchFound, path, scope, row, column, "[" + sheet.Name + "][" + address + "] " + value.Trim(), match.Value);
                    }
                }
            }
        }
    }

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

    private static string ReadSharedString(XmlReader reader)
    {
        if (reader.IsEmptyElement) return string.Empty;
        var text = new StringBuilder();
        using (var subtree = reader.ReadSubtree())
        {
            while (subtree.Read())
            {
                if (subtree.NodeType == XmlNodeType.Element && subtree.LocalName == "t")
                    text.Append(subtree.ReadElementContentAsString());
            }
        }
        return text.ToString();
    }

    private static string ReadCellValue(XmlReader reader)
    {
        if (reader.IsEmptyElement) return null;
        string value = null;
        var inline = new StringBuilder();
        using (var subtree = reader.ReadSubtree())
        {
            while (subtree.Read())
            {
                if (subtree.NodeType != XmlNodeType.Element) continue;
                if (subtree.LocalName == "v") value = subtree.ReadElementContentAsString();
                else if (subtree.LocalName == "t") inline.Append(subtree.ReadElementContentAsString());
            }
        }
        return inline.Length > 0 ? inline.ToString() : value;
    }

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
                string name = reader.GetAttribute("name") ?? "Sheet";
                string id = reader.GetAttribute("id", "http://schemas.openxmlformats.org/officeDocument/2006/relationships")
                    ?? reader.GetAttribute("id", "http://purl.oclc.org/ooxml/officeDocument/relationships");
                string target;
                if (id != null && targets.TryGetValue(id, out target))
                    names.Add(new SheetRef { Name = name, Path = target });
            }
        }
        return names;
    }

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
                string id = reader.GetAttribute("Id");
                string target = reader.GetAttribute("Target");
                if (string.IsNullOrEmpty(id) || string.IsNullOrEmpty(target)) continue;
                if (target.IndexOf("worksheet", StringComparison.OrdinalIgnoreCase) < 0) continue;
                target = target.Replace('\\', '/').TrimStart('/');
                if (!target.StartsWith("xl/", StringComparison.OrdinalIgnoreCase)) target = "xl/" + target;
                result[id] = target;
            }
        }
        return result;
    }

    private static void Add(ConcurrentBag<GrepMatch> matches, Action<GrepMatch> matchFound, string path, string scope, int line, int column, string text, string highlight)
    {
        var found = new GrepMatch
        {
            ScopeName = Path.GetFileName((scope ?? string.Empty).TrimEnd(Path.DirectorySeparatorChar)) ?? scope,
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
        var allowed = new HashSet<string>(Extensions, StringComparer.OrdinalIgnoreCase);
        var extensionSet = new HashSet<string>(
            (extensions ?? Extensions).Select(NormalizeExtension).Where(allowed.Contains),
            StringComparer.OrdinalIgnoreCase);
        if (extensionSet.Count == 0) return new List<string>();
        foreach (string scope in scopes)
        {
            token.ThrowIfCancellationRequested();
            if (string.IsNullOrWhiteSpace(scope) || !Directory.Exists(scope)) continue;
            var pending = new Stack<string>();
            pending.Push(scope);
            while (pending.Count > 0)
            {
                token.ThrowIfCancellationRequested();
                string folder = pending.Pop();
                try
                {
                    foreach (var entry in VolumePathIndex.EnumerateNativeDirectory(folder, token))
                    {
                        token.ThrowIfCancellationRequested();
                        string path = Path.Combine(folder, entry.Name);
                        if ((entry.Attributes & FileAttributes.Directory) != 0)
                        {
                            if (IgnoredDirectories.Contains(entry.Name)) continue;
                            if ((entry.Attributes & FileAttributes.Hidden) != 0) continue;
                            pending.Push(path);
                        }
                        else if (extensionSet.Contains(Path.GetExtension(entry.Name)))
                            files.Add(path);
                    }
                }
                catch (Win32Exception) { }
                catch (IOException) { }
                catch (UnauthorizedAccessException) { }
            }
        }
        return files.ToList();
    }

    private static string NormalizeExtension(string extension)
    {
        if (string.IsNullOrWhiteSpace(extension)) return string.Empty;
        extension = extension.Trim();
        return extension.StartsWith(".") ? extension : "." + extension;
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

    private static int RowOf(string address)
    {
        if (string.IsNullOrEmpty(address)) return 0;
        int value = 0;
        for (int i = 0; i < address.Length; i++)
            if (char.IsDigit(address[i])) value = value * 10 + (address[i] - '0');
        return value;
    }

    private static int ColumnOf(string address)
    {
        if (string.IsNullOrEmpty(address)) return 0;
        int value = 0;
        for (int i = 0; i < address.Length; i++)
        {
            char c = address[i];
            if (!char.IsLetter(c)) break;
            value = value * 26 + (char.ToUpperInvariant(c) - 'A' + 1);
        }
        return value;
    }

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
