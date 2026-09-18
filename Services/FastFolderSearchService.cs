using DesktopIniManager.Models;
using FastVolumeIndex;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;

namespace DesktopIniManager.Services
{
    internal sealed class FastFolderSearchService
    {
        /// <summary>Finds matching folders in an existing file-system path index.</summary>
        public List<FolderMatch> Search(VolumePathIndex paths, string query, CancellationToken token)
        {
            var matches = new Dictionary<string, FolderMatch>(StringComparer.OrdinalIgnoreCase);
            string[] keys = (query ?? string.Empty).Split((char[])null, StringSplitOptions.RemoveEmptyEntries)
                .Select(value => value.Trim().TrimStart('*')).Where(value => value.Length > 0)
                .Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
            bool gitMode = keys.Any(key => string.Equals(key.TrimStart('.'), "git", StringComparison.OrdinalIgnoreCase));
            if (keys.Length == 0 || gitMode)
            {
                foreach (VolumePathNode node in paths.Directories)
                { token.ThrowIfCancellationRequested(); if (IsDisplayable(node)) Add(matches, node.Path, "Folder"); }
            }
            else
            {
                foreach (string key in keys)
                {
                    token.ThrowIfCancellationRequested();
                    bool extensionQuery = IsExtensionQuery(key);
                    bool fileNameQuery = !extensionQuery && IsFileNameQuery(key);
                    if (!extensionQuery)
                    {
                        foreach (VolumePathNode entry in paths.Files.Concat(paths.Directories))
                        {
                            if (!NameMatches(entry.Name, key, fileNameQuery)) continue;
                            string folder = FolderPathOf(entry);
                            VolumePathNode folderNode = !string.IsNullOrEmpty(folder) ? paths.Find(folder) : null;
                            if (folderNode != null && IsDisplayable(folderNode)) Add(matches, folder, "Name: " + key);
                        }
                    }
                    if (fileNameQuery) continue;
                    string extension = "." + key.TrimStart('.');
                    foreach (var group in paths.FindFiles(new[] { extension })
                        .GroupBy(FolderPathOf, StringComparer.OrdinalIgnoreCase))
                        if (!string.IsNullOrEmpty(group.Key) && IsDisplayable(paths.Find(group.Key)))
                            Add(matches, group.Key, "Contents: " + extension + " × " + group.Count());
                }
            }
            foreach (string path in matches.Keys.ToArray())
                AddAncestors(matches, paths, path);
            return matches.Values.OrderBy(item => item.Path, StringComparer.OrdinalIgnoreCase).ToList();
        }

        private static bool IsExtensionQuery(string key)
        {
            if (string.IsNullOrWhiteSpace(key)) return false;
            if (key[0] == '.') return key.Length > 1 && key.IndexOf('.', 1) < 0;
            return false;
        }

        private static bool IsFileNameQuery(string key)
        {
            if (string.IsNullOrWhiteSpace(key) || key[0] == '.') return false;
            string extension = Path.GetExtension(key);
            return extension.Length > 1 && key.Length > extension.Length;
        }

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

        private static string FolderPathOf(VolumePathNode entry)
        {
            if (entry == null) return null;
            if (entry.IsDirectory) return entry.Path;
            if (entry.Parent != null) return entry.Parent.Path;
            string parent = Path.GetDirectoryName(entry.Path);
            return string.IsNullOrEmpty(parent) ? null : VolumePathIndex.Normalize(parent);
        }

        private static void AddAncestors(
            Dictionary<string, FolderMatch> matches, VolumePathIndex paths, string path)
        {
            string current = path;
            while (!string.IsNullOrEmpty(current)
                && !string.Equals(current, paths.RootPath, StringComparison.OrdinalIgnoreCase))
            {
                string parentPath = Path.GetDirectoryName(current);
                if (string.IsNullOrEmpty(parentPath)) break;
                parentPath = VolumePathIndex.Normalize(parentPath);
                if (!VolumePathIndex.IsWithin(parentPath, paths.RootPath)
                    && !string.Equals(parentPath, paths.RootPath, StringComparison.OrdinalIgnoreCase))
                    break;
                VolumePathNode node = paths.Find(parentPath);
                if (node != null && IsDisplayable(node)) Add(matches, parentPath, "Folder");
                current = parentPath;
            }
        }

        private static bool IsDisplayable(VolumePathNode node)
        {
            while (node != null)
            {
                if ((node.Attributes & FileAttributes.Hidden) != 0) return false;
                if (string.Equals(node.Name, ".git", StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(node.Name, ".vs", StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(node.Name, ".vscode", StringComparison.OrdinalIgnoreCase)) return false;
                node = node.Parent;
            }
            return true;
        }

        /// <summary>Summarizes development artifacts found in each indexed folder.</summary>
        public Dictionary<string, string> AnalyzeDevelopment(VolumePathIndex paths, CancellationToken token)
        {
            var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            var projectExtensions = new HashSet<string>(
                new[] { ".csproj", ".vbproj", ".vcxproj", ".fsproj", ".vbp", ".dproj" },
                StringComparer.OrdinalIgnoreCase);

            int inspected = 0;
            foreach (VolumePathNode folder in paths.Directories)
            {
                if ((++inspected & 2047) == 0) token.ThrowIfCancellationRequested();

                var parts = new List<string>();
                bool repository = false;
                int solutions = 0;
                int projects = 0;
                var extensionCounts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

                foreach (VolumePathNode child in folder.Directories)
                {
                    if (string.Equals(child.Name, ".git", StringComparison.OrdinalIgnoreCase))
                    {
                        repository = true;
                        break;
                    }
                }

                foreach (VolumePathNode file in folder.Files)
                {
                    string name = file.Name;
                    if (string.Equals(name, ".git", StringComparison.OrdinalIgnoreCase))
                        repository = true;

                    string extensionWithDot = Path.GetExtension(name);
                    if (string.Equals(extensionWithDot, ".sln", StringComparison.OrdinalIgnoreCase))
                        solutions++;
                    if (projectExtensions.Contains(extensionWithDot))
                        projects++;

                    if (string.Equals(name, "desktop.ini", StringComparison.OrdinalIgnoreCase))
                        continue;

                    string extension = extensionWithDot.TrimStart('.');
                    extensionCounts.TryGetValue(extension, out int count);
                    extensionCounts[extension] = count + 1;
                }

                if (repository) parts.Add("Repository");
                if (solutions > 0) parts.Add("SLN ×" + solutions);
                if (projects > 0) parts.Add("Project ×" + projects);

                foreach (var extension in extensionCounts
                    .OrderByDescending(pair => pair.Value)
                    .ThenBy(pair => pair.Key, StringComparer.OrdinalIgnoreCase)
                    .Take(12))
                {
                    string name = string.IsNullOrEmpty(extension.Key)
                        ? "(no extension)"
                        : extension.Key.ToLowerInvariant();
                    parts.Add(name + " ×" + extension.Value);
                }

                result[folder.Path] = parts.Count == 0 ? "Empty folder" : string.Join(" · ", parts);
            }

            return result;
        }

        private static void Add(Dictionary<string, FolderMatch> matches, string path, string reason)
        {
            if (!matches.ContainsKey(path)) matches[path] = new FolderMatch { Path = path, Reason = reason };
        }
    }

}
