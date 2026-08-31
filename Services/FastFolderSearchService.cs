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
        public FastSearchResult Search(string root, string query, Action<int> progress, CancellationToken token)
        {
            progress(0);
            NtfsVolumeIndex index = NtfsVolumeIndex.Create(root);
            VolumePathIndex paths = VolumePathIndex.Build(index, root);
            token.ThrowIfCancellationRequested();
            var matches = new Dictionary<string, FolderMatch>(StringComparer.OrdinalIgnoreCase);
            string[] keys = query.Split((char[])null, StringSplitOptions.RemoveEmptyEntries)
                .Select(value => value.Trim().TrimStart('*')).Where(value => value.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
            bool gitMode = keys.Any(key => string.Equals(key.TrimStart('.'), "git", StringComparison.OrdinalIgnoreCase));

            if (keys.Length == 0)
            {
                foreach (VolumePathNode node in paths.Directories)
                {
                    token.ThrowIfCancellationRequested();
                    if (IsDisplayable(node)) Add(matches, node.Path, "Folder");
                }
            }
            else if (gitMode)
            {
                // Show the completed physical tree first. Repository/project analysis
                // is deliberately performed by AnalyzeDevelopment after the UI binds.
                foreach (VolumePathNode node in paths.Directories)
                    if (IsDisplayable(node)) Add(matches, node.Path, "Folder");
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
                    foreach (var group in paths.FindFiles(new[] { extension })
                        .GroupBy(entry => entry.Parent?.Path, StringComparer.OrdinalIgnoreCase))
                    {
                        if (!string.IsNullOrEmpty(group.Key) && IsDisplayable(paths.Find(group.Key)))
                            Add(matches, group.Key, "Contents: " + extension + " × " + group.Count());
                    }
                }
            }

            // Include the real ancestors so the presentation never invents or compresses hierarchy.
            foreach (string path in matches.Keys.ToArray())
            {
                VolumePathNode node = paths.Find(path)?.Parent;
                while (node != null) { if (IsDisplayable(node)) Add(matches, node.Path, "Folder"); node = node.Parent; }
            }

            progress(index.DirectoryCount);
            return new FastSearchResult(index, paths, matches.Values.OrderBy(item => item.Path, StringComparer.OrdinalIgnoreCase).ToList());
        }

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
                if (string.Equals(node.Name, ".git", StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(node.Name, ".vs", StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(node.Name, ".vscode", StringComparison.OrdinalIgnoreCase)) return false;
                node = node.Parent;
            }
            return true;
        }

        public SolutionCatalog AnalyzeSolutions(NtfsVolumeIndex index, VolumePathIndex paths, CancellationToken token)
        { token.ThrowIfCancellationRequested(); return SolutionCatalog.Build(index, paths); }

        public Dictionary<string, string> AnalyzeDevelopment(VolumePathIndex paths, CancellationToken token)
        {
            var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            var projectExtensions = new HashSet<string>(new[] { ".csproj", ".vbproj", ".vcxproj", ".fsproj", ".vbp", ".dproj" }, StringComparer.OrdinalIgnoreCase);
            int inspected = 0;
            foreach (VolumePathNode folder in paths.Directories)
            {
                if ((++inspected & 2047) == 0) token.ThrowIfCancellationRequested();
                var parts = new List<string>();
                if (folder.Directories.Any(child => string.Equals(child.Name, ".git", StringComparison.OrdinalIgnoreCase)) ||
                    folder.Files.Any(file => string.Equals(file.Name, ".git", StringComparison.OrdinalIgnoreCase)))
                    parts.Add("Repository");
                int solutions = folder.Files.Count(file => string.Equals(Path.GetExtension(file.Name), ".sln", StringComparison.OrdinalIgnoreCase));
                int projects = folder.Files.Count(file => projectExtensions.Contains(Path.GetExtension(file.Name)));
                if (solutions > 0) parts.Add("SLN ×" + solutions);
                if (projects > 0) parts.Add("Project ×" + projects);

                foreach (var extension in folder.Files
                    .Where(file => !string.Equals(file.Name, "desktop.ini", StringComparison.OrdinalIgnoreCase))
                    .GroupBy(file => Path.GetExtension(file.Name).TrimStart('.'), StringComparer.OrdinalIgnoreCase)
                    .OrderByDescending(group => group.Count()).ThenBy(group => group.Key, StringComparer.OrdinalIgnoreCase).Take(12))
                    parts.Add((string.IsNullOrEmpty(extension.Key) ? "(no extension)" : extension.Key.ToLowerInvariant()) + " ×" + extension.Count());

                result[folder.Path] = parts.Count == 0 ? "Empty folder" : string.Join(" · ", parts);
            }
            return result;
        }

        public List<FolderMatch> BuildSolutionTree(NtfsVolumeIndex index, string root, CancellationToken token)
        {
            return BuildSolutionTree(index, SolutionMapService.Build(index, root), token);
        }

        public List<FolderMatch> BuildSolutionTree(NtfsVolumeIndex index, IReadOnlyList<SolutionMap> maps, CancellationToken token)
        {
            var result = new List<FolderMatch>();
            foreach (SolutionMap map in maps)
            {
                token.ThrowIfCancellationRequested();
                string solutionPath = index.GetFullPath(map.Solution);
                string solutionDirectory = Path.GetDirectoryName(solutionPath);
                var rootNode = new FolderMatch
                {
                    DisplayName = Path.GetFileName(solutionPath), Path = solutionDirectory,
                    Reason = "Solution · " + map.Projects.Count(project => !project.IsSolutionFolder) + " projects",
                    IsActionable = false, IconPreview = FolderIconService.GetFolderIcon(solutionDirectory)
                };
                var nodes = map.Projects.ToDictionary(project => project.ProjectId, project => CreateSolutionNode(project, solutionDirectory), StringComparer.OrdinalIgnoreCase);
                foreach (SolutionProject project in map.Projects)
                {
                    FolderMatch node = nodes[project.ProjectId];
                    if (!string.IsNullOrEmpty(project.ParentProjectId) && nodes.TryGetValue(project.ParentProjectId, out FolderMatch parent))
                        parent.Children.Add(node);
                    else rootNode.Children.Add(node);
                }
                result.Add(rootNode);
            }
            return result.OrderBy(item => item.Name, StringComparer.OrdinalIgnoreCase).ToList();
        }

        private static FolderMatch CreateSolutionNode(SolutionProject project, string solutionDirectory)
        {
            bool actionable = !project.IsSolutionFolder && project.Exists;
            string projectPath = actionable ? Path.GetDirectoryName(project.FullPath) : solutionDirectory;
            return new FolderMatch
            {
                DisplayName = project.Name,
                Path = projectPath,
                Reason = project.IsSolutionFolder ? "Solution folder" : project.Exists ? "Project · " + Path.GetFileName(project.FullPath) : "Missing · " + project.RelativePath,
                IsActionable = actionable,
                IconPreview = FolderIconService.GetFolderIcon(projectPath)
            };
        }

        private static void Add(Dictionary<string, FolderMatch> matches, string path, string reason)
        {
            if (!matches.ContainsKey(path)) matches[path] = new FolderMatch { Path = path, Reason = reason };
        }

        private static void AddSourceFolders(VolumePathIndex index, IReadOnlyList<VolumePathNode> repositories,
            Dictionary<string, FolderMatch> matches, CancellationToken token)
        {
            string[] repositoryPaths = repositories.Select(item => item.Path)
                .OrderByDescending(path => path.Length).ToArray();
            var folders = new Dictionary<string, SourceFolderAggregate>(StringComparer.OrdinalIgnoreCase);
            int inspected = 0;

            foreach (VolumePathNode entry in index.Files)
            {
                if ((++inspected & 4095) == 0) token.ThrowIfCancellationRequested();
                if (entry.IsDirectory) continue;
                string extension = Path.GetExtension(entry.Name).TrimStart('.');
                if (!FolderSearchService.LanguageByExtension.TryGetValue(extension, out string language)) continue;

                string filePath = entry.Path;
                string repository = repositoryPaths.FirstOrDefault(path => IsUnderPath(filePath, path));
                if (repository == null) continue;
                string folder = Path.GetDirectoryName(filePath);
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

    internal sealed class FastSearchResult
    {
        public FastSearchResult(NtfsVolumeIndex index, VolumePathIndex paths, List<FolderMatch> matches)
        { Index = index; Paths = paths; Matches = matches; }
        public NtfsVolumeIndex Index { get; }
        public VolumePathIndex Paths { get; }
        public List<FolderMatch> Matches { get; }
    }
}
