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
            token.ThrowIfCancellationRequested();
            var matches = new Dictionary<string, FolderMatch>(StringComparer.OrdinalIgnoreCase);
            string[] keys = query.Split((char[])null, StringSplitOptions.RemoveEmptyEntries)
                .Select(value => value.Trim().TrimStart('*')).Where(value => value.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
            bool gitMode = keys.Any(key => string.Equals(key.TrimStart('.'), "git", StringComparison.OrdinalIgnoreCase));

            if (gitMode)
            {
                DevelopmentInventory inventory = DevelopmentScanner.Analyze(index, root);
                foreach (MftEntry repository in inventory.Repositories)
                    Add(matches, index.GetFullPath(repository), "Repository · Fast NTFS index");

                foreach (MftEntry solutionFile in inventory.Solutions)
                {
                    string solutionPath = index.GetFullPath(solutionFile);
                    string folder = Path.GetDirectoryName(solutionPath);
                    if (!string.IsNullOrEmpty(folder))
                        Add(matches, folder, "Solution · " + solutionFile.Name);
                }

                foreach (MftEntry projectFile in inventory.Projects)
                {
                    string projectFilePath = index.GetFullPath(projectFile);
                    string folder = Path.GetDirectoryName(projectFilePath);
                    if (!string.IsNullOrEmpty(folder))
                        Add(matches, folder, "Project · " + projectFile.Name);
                }

                AddSourceFolders(index, inventory.Repositories, matches, token);
            }
            else
            {
                foreach (string key in keys)
                {
                    token.ThrowIfCancellationRequested();
                    foreach (MftEntry entry in index.SearchNames(root, key))
                    {
                        string path = index.GetFullPath(entry);
                        string folder = entry.IsDirectory ? path : Path.GetDirectoryName(path);
                        if (!string.IsNullOrEmpty(folder)) Add(matches, folder, "Name: " + key);
                    }

                    string extension = "." + key.TrimStart('.');
                    foreach (var group in index.FindFiles(root, new[] { extension })
                        .GroupBy(entry => Path.GetDirectoryName(index.GetFullPath(entry)), StringComparer.OrdinalIgnoreCase))
                    {
                        if (!string.IsNullOrEmpty(group.Key))
                            Add(matches, group.Key, "Contents: " + extension + " × " + group.Count());
                    }
                }
            }

            progress(index.DirectoryCount);
            return new FastSearchResult(index, matches.Values.OrderBy(item => item.Path, StringComparer.OrdinalIgnoreCase).ToList());
        }

        public List<FolderMatch> BuildSolutionTree(NtfsVolumeIndex index, string root, CancellationToken token)
        {
            var result = new List<FolderMatch>();
            foreach (SolutionMap map in SolutionMapService.Build(index, root))
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

        private static void AddSourceFolders(NtfsVolumeIndex index, IReadOnlyList<MftEntry> repositories,
            Dictionary<string, FolderMatch> matches, CancellationToken token)
        {
            string[] repositoryPaths = repositories.Select(index.GetFullPath)
                .OrderByDescending(path => path.Length).ToArray();
            var folders = new Dictionary<string, SourceFolderAggregate>(StringComparer.OrdinalIgnoreCase);
            int inspected = 0;

            foreach (MftEntry entry in index.Entries)
            {
                if ((++inspected & 4095) == 0) token.ThrowIfCancellationRequested();
                if (entry.IsDirectory) continue;
                string extension = Path.GetExtension(entry.Name).TrimStart('.');
                if (!FolderSearchService.LanguageByExtension.TryGetValue(extension, out string language)) continue;

                string filePath = index.GetFullPath(entry);
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
        public FastSearchResult(NtfsVolumeIndex index, List<FolderMatch> matches) { Index = index; Matches = matches; }
        public NtfsVolumeIndex Index { get; }
        public List<FolderMatch> Matches { get; }
    }
}
