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
                    foreach (VolumePathNode entry in paths.Search(key))
                    {
                        string folder = entry.IsDirectory ? entry.Path : entry.Parent?.Path;
                        VolumePathNode folderNode = !string.IsNullOrEmpty(folder) ? paths.Find(folder) : null;
                        if (folderNode != null && IsDisplayable(folderNode)) Add(matches, folder, "Name: " + key);
                    }
                    string extension = "." + key.TrimStart('.');
                    foreach (var group in paths.FindFiles(new[] { extension }).GroupBy(entry => entry.Parent?.Path, StringComparer.OrdinalIgnoreCase))
                        if (!string.IsNullOrEmpty(group.Key) && IsDisplayable(paths.Find(group.Key))) Add(matches, group.Key, "Contents: " + extension + " × " + group.Count());
                }
            }
            foreach (string path in matches.Keys.ToArray())
            {
                VolumePathNode node = paths.Find(path)?.Parent;
                while (node != null) { if (IsDisplayable(node)) Add(matches, node.Path, "Folder"); node = node.Parent; }
            }
            return matches.Values.OrderBy(item => item.Path, StringComparer.OrdinalIgnoreCase).ToList();
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

        private static void AddSourceFolders(VolumePathIndex index, IReadOnlyList<VolumePathNode> repositories,
            Dictionary<string, FolderMatch> matches, CancellationToken token)
        {
            var repositoryPaths = new HashSet<string>(
                repositories.Select(item => item.Path),
                StringComparer.OrdinalIgnoreCase);
            var folders = new Dictionary<string, SourceFolderAggregate>(StringComparer.OrdinalIgnoreCase);
            int inspected = 0;

            foreach (VolumePathNode entry in index.Files)
            {
                if ((++inspected & 4095) == 0) token.ThrowIfCancellationRequested();
                if (entry.IsDirectory) continue;
                string extension = Path.GetExtension(entry.Name).TrimStart('.');
                if (!FolderSearchService.LanguageByExtension.TryGetValue(extension, out string language)) continue;

                string filePath = entry.Path;
                string repository = FindRepository(entry.Parent, repositoryPaths);
                if (repository == null) continue;
                string folder = entry.Parent?.Path ?? Path.GetDirectoryName(filePath);
                if (string.IsNullOrEmpty(folder) || ContainsIgnoredDirectory(folder, repository)) continue;

                if (!folders.TryGetValue(folder, out SourceFolderAggregate aggregate))
                    folders[folder] = aggregate = new SourceFolderAggregate();
                aggregate.Total++;
                if (!aggregate.Languages.TryGetValue(language, out LanguageCount count))
                    aggregate.Languages[language] = count = new LanguageCount();
                count.Count++;
                count.Extensions.Add(extension.ToLowerInvariant());
            }

            foreach (var folder in folders.Where(pair => pair.Value.Total >= 2))
                Add(matches, folder.Key, BuildSourceReason(folder.Value));
        }


        private static string FindRepository(VolumePathNode node, HashSet<string> repositoryPaths)
        {
            while (node != null)
            {
                if (repositoryPaths.Contains(node.Path))
                    return node.Path;
                node = node.Parent;
            }
            return null;
        }

        private static bool IsUnderPath(string path, string parent)
        {
            string prefix = parent.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
            return string.Equals(path, parent, StringComparison.OrdinalIgnoreCase)
                || path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase);
        }

        private static bool ContainsIgnoredDirectory(string folder, string repository)
        {
            string relative = folder.Length > repository.Length ? folder.Substring(repository.Length).TrimStart(Path.DirectorySeparatorChar) : string.Empty;
            return relative.Split(new[] { Path.DirectorySeparatorChar }, StringSplitOptions.RemoveEmptyEntries)
                .Any(segment => FolderSearchService.IgnoredDirectories.Contains(segment));
        }

        private static string BuildSourceReason(SourceFolderAggregate aggregate)
        {
            var parts = aggregate.Languages.OrderByDescending(item => item.Value.Count).ThenBy(item => item.Key)
                .Take(10).Select(item =>
                {
                    int percent = (int)Math.Round(item.Value.Count * 100.0 / aggregate.Total);
                    string extensions = string.Join("/", item.Value.Extensions.OrderBy(value => value).Take(5).Select(value => "." + value));
                    return item.Key + " (" + extensions + ") ×" + item.Value.Count + " (" + percent + "%)";
                });
            return "Project · " + string.Join(" · ", parts);
        }

        private sealed class SourceFolderAggregate
        {
            public int Total;
            public readonly Dictionary<string, LanguageCount> Languages = new Dictionary<string, LanguageCount>(StringComparer.OrdinalIgnoreCase);
        }

        private sealed class LanguageCount
        {
            public int Count;
            public readonly HashSet<string> Extensions = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        }
    }

}
