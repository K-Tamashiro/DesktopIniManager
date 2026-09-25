using DesktopIniManager.Models;
using DesktopIniManager.ViewModels;
using FastVolumeIndex;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;

namespace DesktopIniManager.Services
{
    internal static class SolutionTreeService
    {
        private const string SolutionFolderType = "{66A26720-8FB5-11D2-AA7E-00C04F688DDE}";

        private static readonly Regex ProjectLine = new Regex(
            "^Project\\(\"(?<type>[^\"]+)\"\\) = \"(?<name>[^\"]+)\", \"(?<path>[^\"]+)\", \"(?<id>[^\"]+)\"",
            RegexOptions.Compiled);

        private static readonly Regex NestedLine = new Regex(
            "^\\s*(?<child>\\{[^}]+\\})\\s*=\\s*(?<parent>\\{[^}]+\\})",
            RegexOptions.Compiled);
        private static readonly Regex ProjectConfigLine = new Regex(
            "^\\s*\\{[^}]+\\}\\.(?<pair>[^=]+?)\\.(ActiveCfg|Build\\.0)\\s*=",
            RegexOptions.Compiled | RegexOptions.IgnoreCase);
        private static readonly HashSet<string> Ignored = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            ".git", ".vs", ".vscode", "bin", "obj", "node_modules",
            "packages", "vendor", "dist", "build", "target"
        };

        public static List<FolderMatch> Build(string root, CancellationToken token)
        {
            var solutions = new List<FolderMatch>();
            var pending = new Stack<string>();
            pending.Push(root);

            while (pending.Count > 0)
            {
                token.ThrowIfCancellationRequested();
                string folder = pending.Pop();

                try
                {
                    foreach (VolumePathIndex.NativeDirectoryEntry entry in VolumePathIndex.EnumerateNativeDirectory(folder, token))
                    {
                        token.ThrowIfCancellationRequested();
                        string path = Path.Combine(folder, entry.Name);
                        bool directory = (entry.Attributes & FileAttributes.Directory) != 0;

                        if (!directory)
                        {
                            if (string.Equals(Path.GetExtension(entry.Name), ".sln", StringComparison.OrdinalIgnoreCase))
                                solutions.Add(Parse(path, token, null));
                            continue;
                        }

                        if (ShouldSkipDirectory(entry.Name, entry.Attributes))
                            continue;
                        if ((entry.Attributes & FileAttributes.ReparsePoint) != 0)
                            continue;

                        pending.Push(path);
                    }
                }
                catch (UnauthorizedAccessException) { }
                catch (IOException) { }
                catch (System.ComponentModel.Win32Exception) { }
            }

            solutions.Sort((x, y) => StringComparer.CurrentCultureIgnoreCase.Compare(x.Name, y.Name));
            return solutions;
        }

        public static List<FolderMatch> BuildFromProjectFiles(
            IReadOnlyList<string> projectFiles,
            VolumePathIndex pathIndex,
            CancellationToken token)
        {
            var solutions = new List<FolderMatch>();
            if (projectFiles == null) return solutions;

            foreach (string path in projectFiles)
            {
                token.ThrowIfCancellationRequested();
                if (string.IsNullOrWhiteSpace(path)) continue;
                if (!string.Equals(Path.GetExtension(path), ".sln", StringComparison.OrdinalIgnoreCase))
                    continue;
                if (!File.Exists(path)) continue;

                try { solutions.Add(Parse(path, token, pathIndex)); }
                catch (UnauthorizedAccessException) { }
                catch (IOException) { }
            }

            solutions.Sort((x, y) => StringComparer.CurrentCultureIgnoreCase.Compare(x.Name, y.Name));
            return solutions;
        }

        public static IList<string> ReadBuildConfigurations(string solutionPath)
        {
            if (string.IsNullOrWhiteSpace(solutionPath) || !File.Exists(solutionPath))
                return new string[0];

            var configurations = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
            bool inSolutionConfigs = false;
            bool inProjectConfigs = false;
            foreach (string line in File.ReadLines(solutionPath))
            {
                if (line.IndexOf("GlobalSection(SolutionConfigurationPlatforms)", StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    inSolutionConfigs = true;
                    inProjectConfigs = false;
                    continue;
                }
                if (line.IndexOf("GlobalSection(ProjectConfigurationPlatforms)", StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    inProjectConfigs = true;
                    inSolutionConfigs = false;
                    continue;
                }
                if ((inSolutionConfigs || inProjectConfigs)
                    && line.TrimStart().StartsWith("EndGlobalSection", StringComparison.OrdinalIgnoreCase))
                {
                    inSolutionConfigs = false;
                    inProjectConfigs = false;
                    continue;
                }
                if (inSolutionConfigs)
                {
                    int equals = line.IndexOf('=');
                    if (equals > 0)
                    {
                        string pair = line.Substring(0, equals).Trim();
                        if (pair.IndexOf('|') >= 0)
                            configurations.Add(pair);
                    }
                    continue;
                }
                if (inProjectConfigs)
                {
                    Match config = ProjectConfigLine.Match(line);
                    if (config.Success)
                        configurations.Add(config.Groups["pair"].Value.Trim());
                }
            }
            return configurations.ToList();
        }

        private static FolderMatch Parse(string solutionPath, CancellationToken token, VolumePathIndex pathIndex)
        {
            string solutionDirectory = Path.GetDirectoryName(solutionPath);
            var entries = new Dictionary<string, Entry>(StringComparer.OrdinalIgnoreCase);
            var nested = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            var configurations = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
            bool inNested = false;
            bool inSolutionConfigs = false;
            bool inProjectConfigs = false;

            foreach (string line in File.ReadLines(solutionPath))
            {
                token.ThrowIfCancellationRequested();

                Match project = ProjectLine.Match(line);
                if (project.Success)
                {
                    string id = project.Groups["id"].Value;
                    entries[id] = new Entry
                    {
                        Id = id,
                        Type = project.Groups["type"].Value,
                        Name = project.Groups["name"].Value,
                        RelativePath = project.Groups["path"].Value
                    };
                    continue;
                }

                if (line.IndexOf("GlobalSection(NestedProjects)", StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    inNested = true;
                    inSolutionConfigs = false;
                    inProjectConfigs = false;
                    continue;
                }

                if (line.IndexOf("GlobalSection(SolutionConfigurationPlatforms)", StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    inSolutionConfigs = true;
                    inNested = false;
                    inProjectConfigs = false;
                    continue;
                }

                if (line.IndexOf("GlobalSection(ProjectConfigurationPlatforms)", StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    inProjectConfigs = true;
                    inNested = false;
                    inSolutionConfigs = false;
                    continue;
                }

                if ((inNested || inSolutionConfigs || inProjectConfigs)
                    && line.TrimStart().StartsWith("EndGlobalSection", StringComparison.OrdinalIgnoreCase))
                {
                    inNested = false;
                    inSolutionConfigs = false;
                    inProjectConfigs = false;
                    continue;
                }

                if (inNested)
                {
                    Match relation = NestedLine.Match(line);
                    if (relation.Success)
                        nested[relation.Groups["child"].Value] = relation.Groups["parent"].Value;
                    continue;
                }

                if (inSolutionConfigs)
                {
                    int equals = line.IndexOf('=');
                    if (equals > 0)
                    {
                        string pair = line.Substring(0, equals).Trim();
                        if (pair.IndexOf('|') >= 0)
                            configurations.Add(pair);
                    }
                    continue;
                }

                if (inProjectConfigs)
                {
                    Match config = ProjectConfigLine.Match(line);
                    if (config.Success)
                        configurations.Add(config.Groups["pair"].Value.Trim());
                }
            }

            var nodes = new Dictionary<string, FolderMatch>(entries.Count, StringComparer.OrdinalIgnoreCase);
            foreach (var pair in entries)
            {
                token.ThrowIfCancellationRequested();
                nodes[pair.Key] = CreateNode(pair.Value, solutionDirectory, pathIndex, token);
            }

            var root = new FolderMatch
            {
                DisplayName = Path.GetFileName(solutionPath),
                Path = solutionDirectory,
                SolutionFile = solutionPath,
                BuildConfigurations = configurations.ToList(),
                Reason = "Solution · " + entries.Values.Count(item => !IsSolutionFolder(item)) + " projects",
                IsActionable = false,
                IconPreview = DifferencerStatusIcons.GetSolutionIcon() ?? FolderIconService.GetFolderIcon(solutionDirectory)
            };

            foreach (var pair in nodes)
            {
                if (nested.TryGetValue(pair.Key, out string parentId) &&
                    nodes.TryGetValue(parentId, out FolderMatch parent))
                {
                    parent.Children.Add(pair.Value);
                }
                else
                {
                    root.Children.Add(pair.Value);
                }
            }

            SortProjectChildren(root.Children);
            return root;
        }

        private static FolderMatch CreateNode(
            Entry entry,
            string solutionDirectory,
            VolumePathIndex pathIndex,
            CancellationToken token)
        {
            bool folder = IsSolutionFolder(entry);
            string projectFile = folder
                ? null
                : Path.GetFullPath(Path.Combine(
                    solutionDirectory,
                    entry.RelativePath.Replace('\\', Path.DirectorySeparatorChar)));

            if (!folder)
                projectFile = ResolveProjectFile(projectFile, pathIndex);

            bool projectExists = !folder && PathExistsAsFile(projectFile, pathIndex);
            string physicalPath = folder
                ? solutionDirectory
                : projectExists
                    ? Path.GetDirectoryName(projectFile)
                    : (PathExistsAsDirectory(projectFile, pathIndex) ? projectFile : Path.GetDirectoryName(projectFile));
            bool physicalDirectoryExists = !folder && PathExistsAsDirectory(physicalPath, pathIndex);

            var node = new FolderMatch
            {
                DisplayName = entry.Name,
                Path = physicalPath,
                Reason = folder ? "Solution folder" : "Project · " + Path.GetFileName(entry.RelativePath),
                IsActionable = physicalDirectoryExists,
                IconPreview = FolderIconService.GetFolderIcon(physicalPath)
            };

            if (projectExists)
                PopulateProject(node, projectFile, pathIndex, token);

            return node;
        }

        private static void PopulateProject(
            FolderMatch project,
            string projectFile,
            VolumePathIndex pathIndex,
            CancellationToken token)
        {
            string directory = Path.GetDirectoryName(projectFile);
            var files = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var removed = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            bool sdk = false;
            bool defaultItems = true;

            try
            {
                foreach (string raw in File.ReadLines(projectFile))
                {
                    token.ThrowIfCancellationRequested();

                    string line = raw.Trim();
                    if (line.Length == 0)
                        continue;

                    if (!sdk
                        && line.IndexOf("<Project", StringComparison.OrdinalIgnoreCase) >= 0
                        && line.IndexOf("Sdk=", StringComparison.OrdinalIgnoreCase) >= 0)
                    {
                        sdk = true;
                    }

                    if (line.IndexOf("<EnableDefaultItems", StringComparison.OrdinalIgnoreCase) >= 0)
                    {
                        int valueStart = line.IndexOf('>');
                        int valueEnd = valueStart >= 0 ? line.IndexOf('<', valueStart + 1) : -1;
                        if (valueStart >= 0 && valueEnd > valueStart)
                        {
                            string value = line.Substring(valueStart + 1, valueEnd - valueStart - 1).Trim();
                            defaultItems = !string.Equals(value, "false", StringComparison.OrdinalIgnoreCase);
                        }
                    }

                    bool projectReference = line.IndexOf("<ProjectReference", StringComparison.OrdinalIgnoreCase) >= 0;
                    bool projectItem = projectReference
                        || line.IndexOf("<Compile", StringComparison.OrdinalIgnoreCase) >= 0
                        || line.IndexOf("<None", StringComparison.OrdinalIgnoreCase) >= 0
                        || line.IndexOf("<Content", StringComparison.OrdinalIgnoreCase) >= 0
                        || line.IndexOf("<EmbeddedResource", StringComparison.OrdinalIgnoreCase) >= 0
                        || line.IndexOf("<ClCompile", StringComparison.OrdinalIgnoreCase) >= 0
                        || line.IndexOf("<ClInclude", StringComparison.OrdinalIgnoreCase) >= 0
                        || line.IndexOf("<Page", StringComparison.OrdinalIgnoreCase) >= 0
                        || line.IndexOf("<Resource", StringComparison.OrdinalIgnoreCase) >= 0;

                    if (!projectItem)
                        continue;

                    string removeValue = GetAttributeValue(line, "Remove");
                    if (!string.IsNullOrWhiteSpace(removeValue))
                    {
                        foreach (string value in removeValue.Split(new[] { ';' }, StringSplitOptions.RemoveEmptyEntries))
                            removed.Add(NormalizeProjectPath(value.Trim()));
                    }

                    if (projectReference)
                        continue;

                    string includeValue = GetAttributeValue(line, "Include");
                    if (string.IsNullOrWhiteSpace(includeValue))
                        continue;

                    foreach (string pattern in includeValue.Split(new[] { ';' }, StringSplitOptions.RemoveEmptyEntries))
                        foreach (string file in ExpandProjectInclude(directory, pattern.Trim(), pathIndex, token))
                            files.Add(file);
                }
            }
            catch (IOException) { return; }
            catch (UnauthorizedAccessException) { return; }

            if (sdk && defaultItems)
            {
                foreach (string file in EnumerateProjectFiles(directory, pathIndex, token))
                {
                    string relative = NormalizeProjectPath(
                        file.Substring(directory.Length).TrimStart(Path.DirectorySeparatorChar));
                    if (removed.Contains(relative))
                        continue;
                    files.Add(file);
                }
            }

            var folderNodes = new Dictionary<string, FolderMatch>(StringComparer.OrdinalIgnoreCase)
            {
                [string.Empty] = project
            };

            foreach (string file in files)
            {
                token.ThrowIfCancellationRequested();

                string relative = file.StartsWith(directory, StringComparison.OrdinalIgnoreCase)
                    ? file.Substring(directory.Length).TrimStart(Path.DirectorySeparatorChar)
                    : Path.GetFileName(file);

                if (!string.Equals(relative, Path.GetFileName(projectFile), StringComparison.OrdinalIgnoreCase))
                    AddProjectFile(project, directory, relative, folderNodes);
            }

            SortProjectChildren(project.Children);
        }

        private static string GetAttributeValue(string line, string attributeName)
        {
            int name = line.IndexOf(attributeName, StringComparison.OrdinalIgnoreCase);
            while (name >= 0)
            {
                int p = name + attributeName.Length;
                while (p < line.Length && char.IsWhiteSpace(line[p]))
                    p++;

                if (p < line.Length && line[p] == '=')
                {
                    p++;
                    while (p < line.Length && char.IsWhiteSpace(line[p]))
                        p++;

                    if (p < line.Length && (line[p] == '"' || line[p] == '\''))
                    {
                        char quote = line[p++];
                        int end = line.IndexOf(quote, p);
                        if (end >= 0)
                            return line.Substring(p, end - p);
                    }
                }

                name = line.IndexOf(attributeName, name + attributeName.Length, StringComparison.OrdinalIgnoreCase);
            }

            return null;
        }

        private static IEnumerable<string> ExpandProjectInclude(
            string directory,
            string pattern,
            VolumePathIndex pathIndex,
            CancellationToken token)
        {
            if (string.IsNullOrWhiteSpace(pattern) || pattern.Contains("$(") || pattern.Contains("@("))
                yield break;

            string normalizedPattern = NormalizeProjectPath(pattern);
            string full = Path.GetFullPath(Path.Combine(directory, normalizedPattern));

            if (full.IndexOfAny(new[] { '*', '?' }) < 0)
            {
                if (PathExistsAsFile(full, pathIndex))
                    yield return full;
                yield break;
            }

            VolumePathNode indexedRoot = pathIndex?.Find(directory);
            if (indexedRoot != null && indexedRoot.IsDirectory)
            {
                foreach (string file in EnumerateProjectFiles(directory, pathIndex, token))
                {
                    token.ThrowIfCancellationRequested();

                    string relative = file.Substring(directory.Length)
                        .TrimStart(Path.DirectorySeparatorChar);

                    if (MatchesProjectPath(normalizedPattern, relative))
                        yield return file;
                }
                yield break;
            }

            int wildcard = full.IndexOfAny(new[] { '*', '?' });
            int separator = full.LastIndexOf(Path.DirectorySeparatorChar, wildcard);
            string root = full.Substring(0, separator + 1);
            string[] parts = full.Substring(root.Length).Split(Path.DirectorySeparatorChar);
            var pending = new Stack<Tuple<string, int>>();
            pending.Push(Tuple.Create(root, 0));

            while (pending.Count > 0)
            {
                token.ThrowIfCancellationRequested();
                var current = pending.Pop();
                string part = parts[current.Item2];
                bool last = current.Item2 == parts.Length - 1;

                using (IEnumerator<VolumePathIndex.NativeDirectoryEntry> enumerator =
                    VolumePathIndex.EnumerateNativeDirectory(current.Item1, token).GetEnumerator())
                {
                    while (TryMoveNext(enumerator, out VolumePathIndex.NativeDirectoryEntry entry))
                    {
                        token.ThrowIfCancellationRequested();
                        bool isDirectory = (entry.Attributes & FileAttributes.Directory) != 0;

                        if (last)
                        {
                            if (!isDirectory && System.IO.Enumeration.FileSystemName.MatchesSimpleExpression(part, entry.Name, true))
                                yield return Path.Combine(current.Item1, entry.Name);
                            continue;
                        }

                        if (!isDirectory)
                            continue;
                        if (ShouldSkipDirectory(entry.Name, entry.Attributes))
                            continue;
                        if ((entry.Attributes & FileAttributes.ReparsePoint) != 0)
                            continue;

                        string child = Path.Combine(current.Item1, entry.Name);
                        if (part == "**")
                        {
                            pending.Push(Tuple.Create(child, current.Item2));
                        }
                        else if (System.IO.Enumeration.FileSystemName.MatchesSimpleExpression(part, entry.Name, true))
                        {
                            pending.Push(Tuple.Create(child, current.Item2 + 1));
                        }
                    }
                }

                if (part == "**" && !last)
                    pending.Push(Tuple.Create(current.Item1, current.Item2 + 1));
            }
        }

        /// <summary>
        /// Enumerates SDK default items from the already-built volume index.
        /// Falls back to native enumeration only when the project directory is outside the index.
        /// </summary>
        private static bool MatchesProjectPath(string pattern, string relativePath)
        {
            string[] patternParts = pattern.Split(
                new[] { Path.DirectorySeparatorChar },
                StringSplitOptions.RemoveEmptyEntries);
            string[] pathParts = relativePath.Split(
                new[] { Path.DirectorySeparatorChar },
                StringSplitOptions.RemoveEmptyEntries);

            return MatchesProjectPath(patternParts, 0, pathParts, 0);
        }

        private static bool MatchesProjectPath(
            string[] patternParts,
            int patternIndex,
            string[] pathParts,
            int pathIndex)
        {
            while (patternIndex < patternParts.Length)
            {
                string part = patternParts[patternIndex];

                if (part == "**")
                {
                    if (++patternIndex >= patternParts.Length)
                        return true;

                    while (pathIndex <= pathParts.Length)
                    {
                        if (MatchesProjectPath(patternParts, patternIndex, pathParts, pathIndex))
                            return true;
                        pathIndex++;
                    }
                    return false;
                }

                if (pathIndex >= pathParts.Length
                    || !System.IO.Enumeration.FileSystemName.MatchesSimpleExpression(
                        part, pathParts[pathIndex], true))
                    return false;

                patternIndex++;
                pathIndex++;
            }

            return pathIndex == pathParts.Length;
        }

        private static IEnumerable<string> EnumerateProjectFiles(
            string root,
            VolumePathIndex pathIndex,
            CancellationToken token)
        {
            VolumePathNode indexedRoot = pathIndex?.Find(root);
            if (indexedRoot != null && indexedRoot.IsDirectory)
            {
                var pending = new Stack<VolumePathNode>();
                pending.Push(indexedRoot);

                while (pending.Count > 0)
                {
                    token.ThrowIfCancellationRequested();
                    VolumePathNode directory = pending.Pop();

                    foreach (VolumePathNode file in directory.Files)
                        yield return file.Path;

                    foreach (VolumePathNode child in directory.Directories)
                    {
                        if (ShouldSkipDirectory(child.Name, child.Attributes))
                            continue;
                        if (child.IsReparsePoint)
                            continue;
                        pending.Push(child);
                    }
                }
                yield break;
            }

            var nativePending = new Stack<string>();
            nativePending.Push(root);

            while (nativePending.Count > 0)
            {
                token.ThrowIfCancellationRequested();
                string directory = nativePending.Pop();

                using (IEnumerator<VolumePathIndex.NativeDirectoryEntry> enumerator =
                    VolumePathIndex.EnumerateNativeDirectory(directory, token).GetEnumerator())
                {
                    while (TryMoveNext(enumerator, out VolumePathIndex.NativeDirectoryEntry entry))
                    {
                        token.ThrowIfCancellationRequested();
                        string path = Path.Combine(directory, entry.Name);
                        bool isDirectory = (entry.Attributes & FileAttributes.Directory) != 0;

                        if (!isDirectory)
                        {
                            yield return path;
                            continue;
                        }

                        if (ShouldSkipDirectory(entry.Name, entry.Attributes))
                            continue;
                        if ((entry.Attributes & FileAttributes.ReparsePoint) != 0)
                            continue;

                        nativePending.Push(path);
                    }
                }
            }
        }

        private static bool TryMoveNext(
            IEnumerator<VolumePathIndex.NativeDirectoryEntry> enumerator,
            out VolumePathIndex.NativeDirectoryEntry entry)
        {
            try
            {
                if (enumerator.MoveNext())
                {
                    entry = enumerator.Current;
                    return true;
                }
            }
            catch (UnauthorizedAccessException) { }
            catch (IOException) { }
            catch (System.ComponentModel.Win32Exception) { }

            entry = null;
            return false;
        }

        private static bool ShouldSkipDirectory(string name, FileAttributes attributes)
        {
            if (Ignored.Contains(Path.GetFileName(name)))
                return true;
            return (attributes & FileAttributes.Hidden) != 0;
        }

        private static string ResolveProjectFile(string projectFile, VolumePathIndex pathIndex)
        {
            if (string.IsNullOrWhiteSpace(projectFile))
                return projectFile;
            if (PathExistsAsFile(projectFile, pathIndex))
                return projectFile;

            string directory = Path.GetDirectoryName(projectFile);
            string fileName = Path.GetFileName(projectFile);
            string projectName = Path.GetFileNameWithoutExtension(projectFile);
            if (string.IsNullOrEmpty(directory) || string.IsNullOrEmpty(fileName) || string.IsNullOrEmpty(projectName))
                return projectFile;

            string nested = Path.Combine(directory, projectName, fileName);
            return PathExistsAsFile(nested, pathIndex) ? nested : projectFile;
        }

        private static bool PathExistsAsFile(string path, VolumePathIndex pathIndex)
        {
            if (string.IsNullOrWhiteSpace(path))
                return false;

            VolumePathNode node = pathIndex?.Find(path);
            if (node != null)
                return !node.IsDirectory;

            return File.Exists(path);
        }

        private static bool PathExistsAsDirectory(string path, VolumePathIndex pathIndex)
        {
            if (string.IsNullOrWhiteSpace(path))
                return false;

            VolumePathNode node = pathIndex?.Find(path);
            if (node != null)
                return node.IsDirectory;

            return Directory.Exists(path);
        }

        private static string NormalizeProjectPath(string value) =>
            value.Replace('/', Path.DirectorySeparatorChar)
                 .Replace('\\', Path.DirectorySeparatorChar);

        private static void AddProjectFile(
            FolderMatch root,
            string directory,
            string relative,
            Dictionary<string, FolderMatch> folderNodes)
        {
            string[] parts = relative.Split(
                new[] { Path.DirectorySeparatorChar },
                StringSplitOptions.RemoveEmptyEntries);

            if (parts.Length <= 1)
                return;

            FolderMatch parent = root;
            string currentPhysicalPath = directory;
            string currentLogicalPath = string.Empty;

            for (int i = 0; i < parts.Length - 1; i++)
            {
                string part = parts[i];
                currentPhysicalPath = Path.Combine(currentPhysicalPath, part);
                currentLogicalPath = currentLogicalPath.Length == 0
                    ? part
                    : Path.Combine(currentLogicalPath, part);

                if (!folderNodes.TryGetValue(currentLogicalPath, out FolderMatch child))
                {
                    child = new FolderMatch
                    {
                        DisplayName = part,
                        Path = currentPhysicalPath,
                        Reason = "Folder",
                        // These folders came from files already present in VolumePathIndex.
                        // Avoid hitting the file system and Shell again for every logical node.
                        IsActionable = true,
                        IconPreview = FolderIconService.GetDefaultFolderIcon()
                    };

                    parent.Children.Add(child);
                    folderNodes[currentLogicalPath] = child;
                }

                parent = child;
            }

            // Files are intentionally omitted here. Selecting the logical folder already
            // shows its physical files in the file list on the right.
        }

        private static void SortProjectChildren(
            System.Collections.ObjectModel.ObservableCollection<FolderMatch> items)
        {
            foreach (FolderMatch item in items)
                SortProjectChildren(item.Children);

            if (items.Count <= 1)
                return;

            FolderMatch[] ordered = items
                .OrderBy(item => item.Name == "Properties" ? 0 : item.Name == "Dependencies" ? 1 : 2)
                .ThenBy(item => item.Name, StringComparer.CurrentCultureIgnoreCase)
                .ToArray();

            // Rebuild the collection to avoid repeated O(n²) IndexOf and Move operations.
            items.Clear();
            foreach (FolderMatch item in ordered)
                items.Add(item);
        }

        private static bool IsSolutionFolder(Entry entry) =>
            string.Equals(entry.Type, SolutionFolderType, StringComparison.OrdinalIgnoreCase);

        private sealed class Entry
        {
            public string Id;
            public string Type;
            public string Name;
            public string RelativePath;
        }
    }
}
