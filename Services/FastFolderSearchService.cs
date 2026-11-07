using DesktopIniManager.Models;
using FastVolumeIndex;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;

namespace DesktopIniManager.Services;

internal sealed class FastFolderSearchService
{
    /// <summary>Finds matching folders in an existing file-system path index.</summary>
    public List<FolderMatch> Search(VolumePathIndex paths, string query, CancellationToken token)
    {
        var matches = new Dictionary<string, FolderMatch>(StringComparer.OrdinalIgnoreCase);
        var keys = (query ?? string.Empty).Split((char[])null, StringSplitOptions.RemoveEmptyEntries)
            .Select(value => value.Trim().TrimStart('*')).Where(value => value.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        var gitMode = keys.Any(key => string.Equals(key.TrimStart('.'), "git", StringComparison.OrdinalIgnoreCase));
        // Displayability depends only on ancestors; remember it per folder for this search.
        var displayable = new Dictionary<VolumePathNode, bool>();

        bool IsVisible(VolumePathNode node)
        {
            return IsDisplayable(node, displayable);
        }

        if (keys.Length == 0 || gitMode)
            foreach (var node in paths.Directories)
            {
                token.ThrowIfCancellationRequested();
                if (IsVisible(node)) Add(matches, node.Path, "Folder");
            }
        else
            foreach (var key in keys)
            {
                token.ThrowIfCancellationRequested();
                var extensionQuery = IsExtensionQuery(key);
                var fileNameQuery = !extensionQuery && IsFileNameQuery(key);
                if (!extensionQuery)
                    foreach (var entry in paths.Files.Concat(paths.Directories))
                    {
                        if (!NameMatches(entry.Name, key, fileNameQuery)) continue;
                        var folder = FolderPathOf(entry);
                        // The linked node is the indexed folder itself; avoid Find's GetFullPath per match.
                        var folderNode = entry.IsDirectory ? entry : entry.Parent;
                        if (folderNode == null && !string.IsNullOrEmpty(folder)) folderNode = paths.Find(folder);
                        if (folderNode != null && IsVisible(folderNode))
                        {
                            Add(matches, folder, "Name: " + key);
                            if (entry.IsDirectory) matches[folder].IsSearchMatch = true;
                        }
                    }

                if (fileNameQuery) continue;
                var extension = "." + key.TrimStart('.');
                foreach (var group in paths.FindFiles(new[] { extension })
                             .GroupBy(FolderPathOf, StringComparer.OrdinalIgnoreCase))
                    if (!string.IsNullOrEmpty(group.Key) && IsVisible(paths.Find(group.Key)))
                        Add(matches, group.Key, "Contents: " + extension + " \u00D7 " + group.Count());
            }

        foreach (var path in matches.Keys.ToArray())
            AddAncestors(matches, paths, path, displayable);
        return matches.Values.OrderBy(item => item.Path, StringComparer.OrdinalIgnoreCase).ToList();
    }

    /// <summary>
    ///     検索キーが「.cs」のような拡張子だけの指定かどうかを判定します。
    /// </summary>
    /// <param name="key">検索キー。</param>
    /// <returns>拡張子指定の場合は true。</returns>
    private static bool IsExtensionQuery(string key)
    {
        if (string.IsNullOrWhiteSpace(key)) return false;
        if (key[0] == '.') return key.Length > 1 && key.IndexOf('.', 1) < 0;
        return false;
    }

    /// <summary>
    ///     検索キーが「name.ext」のような拡張子付きファイル名の指定かどうかを判定します。
    /// </summary>
    /// <param name="key">検索キー。</param>
    /// <returns>ファイル名指定の場合は true。</returns>
    private static bool IsFileNameQuery(string key)
    {
        if (string.IsNullOrWhiteSpace(key) || key[0] == '.') return false;
        var extension = Path.GetExtension(key);
        return extension.Length > 1 && key.Length > extension.Length;
    }

    /// <summary>
    ///     ファイル名が検索キー (拡張子・ファイル名・部分文字列) に一致するかどうかを判定します。
    /// </summary>
    /// <param name="name">判定するファイル名。</param>
    /// <param name="key">検索キー。</param>
    /// <returns>一致する場合は true。</returns>
    internal static bool FileNameMatches(string name, string key)
    {
        if (IsExtensionQuery(key))
            return string.Equals(Path.GetExtension(name), key, StringComparison.OrdinalIgnoreCase);
        return NameMatches(name, key, IsFileNameQuery(key));
    }

    /// <summary>
    ///     名前が検索キーに一致するかどうかを判定します。ファイル名指定では拡張子の一致と拡張子を除いた部分の部分一致を確認します。
    /// </summary>
    /// <param name="name">判定する名前。</param>
    /// <param name="key">検索キー。</param>
    /// <param name="fileNameQuery">検索キーが拡張子付きファイル名の場合は true。</param>
    /// <returns>一致する場合は true。</returns>
    private static bool NameMatches(string name, string key, bool fileNameQuery)
    {
        if (string.IsNullOrEmpty(name) || string.IsNullOrEmpty(key)) return false;
        if (!fileNameQuery)
            return name.IndexOf(key, StringComparison.OrdinalIgnoreCase) >= 0;

        if (!string.Equals(Path.GetExtension(name), Path.GetExtension(key), StringComparison.OrdinalIgnoreCase))
            return false;

        return Path.GetFileNameWithoutExtension(name)
            .IndexOf(Path.GetFileNameWithoutExtension(key), StringComparison.OrdinalIgnoreCase) >= 0;
    }

    /// <summary>
    ///     ノードがディレクトリならそのパスを、ファイルなら親フォルダーのパスを取得します。
    /// </summary>
    /// <param name="entry">対象のノード。</param>
    /// <returns>フォルダーのパス。取得できない場合は null。</returns>
    private static string FolderPathOf(VolumePathNode entry)
    {
        if (entry == null) return null;
        if (entry.IsDirectory) return entry.Path;
        if (entry.Parent != null) return entry.Parent.Path;
        var parent = Path.GetDirectoryName(entry.Path);
        return string.IsNullOrEmpty(parent) ? null : VolumePathIndex.Normalize(parent);
    }

    /// <summary>
    ///     一致したフォルダーからルートまでの、表示可能な祖先フォルダーを結果に追加します。
    /// </summary>
    /// <param name="matches">検索結果の辞書。</param>
    /// <param name="paths">パス インデックス。</param>
    /// <param name="path">起点のフォルダー パス。</param>
    /// <param name="displayable">ノードごとの表示可否のキャッシュ。</param>
    private static void AddAncestors(
        Dictionary<string, FolderMatch> matches, VolumePathIndex paths, string path,
        Dictionary<VolumePathNode, bool> displayable)
    {
        var current = path;
        while (!string.IsNullOrEmpty(current)
               && !string.Equals(current, paths.RootPath, StringComparison.OrdinalIgnoreCase))
        {
            var parentPath = Path.GetDirectoryName(current);
            if (string.IsNullOrEmpty(parentPath)) break;
            parentPath = VolumePathIndex.Normalize(parentPath);
            if (!VolumePathIndex.IsWithin(parentPath, paths.RootPath)
                && !string.Equals(parentPath, paths.RootPath, StringComparison.OrdinalIgnoreCase))
                break;
            var node = paths.Find(parentPath);
            if (node != null && IsDisplayable(node, displayable)) Add(matches, parentPath, "Folder");
            current = parentPath;
        }
    }

    /// <summary>
    ///     ノードとその祖先が隠しフォルダーや .git、.vs、.vscode でないかどうかを、キャッシュを使って判定します。
    /// </summary>
    /// <param name="node">判定するノード。</param>
    /// <param name="cache">判定結果のキャッシュ。</param>
    /// <returns>表示可能な場合は true。</returns>
    private static bool IsDisplayable(VolumePathNode node, Dictionary<VolumePathNode, bool> cache)
    {
        if (node == null) return true;
        if (cache.TryGetValue(node, out var known)) return known;
        var result = (node.Attributes & FileAttributes.Hidden) == 0 &&
                     !string.Equals(node.Name, ".git", StringComparison.OrdinalIgnoreCase) &&
                     !string.Equals(node.Name, ".vs", StringComparison.OrdinalIgnoreCase) &&
                     !string.Equals(node.Name, ".vscode", StringComparison.OrdinalIgnoreCase) &&
                     IsDisplayable(node.Parent, cache);
        cache[node] = result;
        return result;
    }

    /// <summary>Summarizes development artifacts found in each indexed folder.</summary>
    public Dictionary<string, string> AnalyzeDevelopment(VolumePathIndex paths, CancellationToken token)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var projectExtensions = new HashSet<string>(
            new[] { ".csproj", ".vbproj", ".vcxproj", ".fsproj", ".vbp", ".dproj" },
            StringComparer.OrdinalIgnoreCase);

        var inspected = 0;
        foreach (var folder in paths.Directories)
        {
            if ((++inspected & 2047) == 0) token.ThrowIfCancellationRequested();

            var parts = new List<string>();
            var repository = false;
            var solutions = 0;
            var projects = 0;
            var extensionCounts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

            foreach (var child in folder.Directories)
                if (string.Equals(child.Name, ".git", StringComparison.OrdinalIgnoreCase))
                {
                    repository = true;
                    break;
                }

            foreach (var file in folder.Files)
            {
                var name = file.Name;
                if (string.Equals(name, ".git", StringComparison.OrdinalIgnoreCase))
                    repository = true;

                var extensionWithDot = Path.GetExtension(name);
                if (string.Equals(extensionWithDot, ".sln", StringComparison.OrdinalIgnoreCase))
                    solutions++;
                if (projectExtensions.Contains(extensionWithDot))
                    projects++;

                if (string.Equals(name, "desktop.ini", StringComparison.OrdinalIgnoreCase))
                    continue;

                var extension = extensionWithDot.TrimStart('.');
                extensionCounts.TryGetValue(extension, out var count);
                extensionCounts[extension] = count + 1;
            }

            if (repository) parts.Add("Repository");
            if (solutions > 0) parts.Add("SLN \u00D7" + solutions);
            if (projects > 0) parts.Add("Project \u00D7" + projects);

            foreach (var extension in extensionCounts
                         .OrderByDescending(pair => pair.Value)
                         .ThenBy(pair => pair.Key, StringComparer.OrdinalIgnoreCase)
                         .Take(12))
            {
                var name = string.IsNullOrEmpty(extension.Key)
                    ? "(no extension)"
                    : extension.Key.ToLowerInvariant();
                parts.Add(name + " \u00D7" + extension.Value);
            }

            result[folder.Path] = parts.Count == 0 ? "Empty folder" : string.Join("  ", parts);
        }

        return result;
    }

    /// <summary>
    ///     まだ結果にないフォルダーを、理由付きで追加します。
    /// </summary>
    /// <param name="matches">検索結果の辞書。</param>
    /// <param name="path">追加するフォルダーのパス。</param>
    /// <param name="reason">一致した理由。</param>
    private static void Add(Dictionary<string, FolderMatch> matches, string path, string reason)
    {
        if (!matches.ContainsKey(path)) matches[path] = new FolderMatch { Path = path, Reason = reason };
    }
}