using DesktopIniManager.Models;
using DesktopIniManager.ViewModels;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using System.Xml.Linq;

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
        private static readonly Regex IncludeAttr = new Regex(
            "\\bInclude\\s*=\\s*\"([^\"]+)\"",
            RegexOptions.Compiled | RegexOptions.IgnoreCase);

        private static readonly Regex RemoveAttr = new Regex(
            "\\bRemove\\s*=\\s*\"([^\"]+)\"",
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
                    foreach (string solution in Directory.EnumerateFiles(folder, "*.sln"))
                    {
                        token.ThrowIfCancellationRequested();
                        solutions.Add(Parse(solution, token));
                    }

                    foreach (string child in Directory.EnumerateDirectories(folder))
                    {
                        token.ThrowIfCancellationRequested();

                        if (ShouldSkipDirectory(child))
                            continue;

                        pending.Push(child);
                    }
                }
                catch (UnauthorizedAccessException) { }
                catch (IOException) { }
            }

            solutions.Sort((x, y) => StringComparer.CurrentCultureIgnoreCase.Compare(x.Name, y.Name));
            return solutions;
        }

        public static List<FolderMatch> BuildFromProjectFiles(IReadOnlyList<string> projectFiles, CancellationToken token)
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

                try { solutions.Add(Parse(path, token)); }
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

        private static FolderMatch Parse(string solutionPath, CancellationToken token)
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
                nodes[pair.Key] = CreateNode(pair.Value, solutionDirectory, token);
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

        private static FolderMatch CreateNode(Entry entry, string solutionDirectory, CancellationToken token)
        {
            bool folder = IsSolutionFolder(entry);
            string projectFile = folder
                ? null
                : Path.GetFullPath(Path.Combine(
                    solutionDirectory,
                    entry.RelativePath.Replace('\\', Path.DirectorySeparatorChar)));

            if (!folder)
                projectFile = ResolveProjectFile(projectFile);

            string physicalPath = folder
                ? solutionDirectory
                : (File.Exists(projectFile)
                    ? Path.GetDirectoryName(projectFile)
                    : (Directory.Exists(projectFile) ? projectFile : Path.GetDirectoryName(projectFile)));

            var node = new FolderMatch
            {
                DisplayName = entry.Name,
                Path = physicalPath,
                Reason = folder ? "Solution folder" : "Project · " + Path.GetFileName(entry.RelativePath),
                IsActionable = !folder && Directory.Exists(physicalPath),
                IconPreview = FolderIconService.GetFolderIcon(physicalPath)
            };

            if (File.Exists(projectFile))
                PopulateProject(node, projectFile, token);

            return node;
        }

        private static void PopulateProject(FolderMatch project, string projectFile, CancellationToken token)
        {
            string directory = Path.GetDirectoryName(projectFile);
            var files = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var removed = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            bool sdk = false;
            bool defaultItems = true;

            try
            {
                XElement projectElement = XElement.Load(projectFile);
                XElement defaultItemsElement = projectElement.Elements()
                    .Where(element => element.Name.LocalName == "PropertyGroup" && element.Attribute("Condition") == null)
                    .SelectMany(element => element.Elements())
                    .LastOrDefault(element => element.Name.LocalName == "EnableDefaultItems" && element.Attribute("Condition") == null);
                if (defaultItemsElement != null)
                    defaultItems = !string.Equals(defaultItemsElement.Value.Trim(), "false", StringComparison.OrdinalIgnoreCase);

                foreach (string raw in File.ReadLines(projectFile))
                {
                    token.ThrowIfCancellationRequested();
                    string line = raw;
                    if (line.IndexOf("Sdk=", StringComparison.OrdinalIgnoreCase) >= 0
                        && line.IndexOf("<Project", StringComparison.OrdinalIgnoreCase) >= 0)
                        sdk = true;

                    bool itemLine =
                        line.IndexOf("<Compile", StringComparison.OrdinalIgnoreCase) >= 0
                        || line.IndexOf("<None", StringComparison.OrdinalIgnoreCase) >= 0
                        || line.IndexOf("<Content", StringComparison.OrdinalIgnoreCase) >= 0
                        || line.IndexOf("<EmbeddedResource", StringComparison.OrdinalIgnoreCase) >= 0
                        || line.IndexOf("<ClCompile", StringComparison.OrdinalIgnoreCase) >= 0
                        || line.IndexOf("<ClInclude", StringComparison.OrdinalIgnoreCase) >= 0
                        || line.IndexOf("<Page", StringComparison.OrdinalIgnoreCase) >= 0
                        || line.IndexOf("<Resource", StringComparison.OrdinalIgnoreCase) >= 0
                        || line.IndexOf("<ProjectReference", StringComparison.OrdinalIgnoreCase) >= 0;

                    if (!itemLine) continue;

                    Match remove = RemoveAttr.Match(line);
                    if (remove.Success)
                        removed.Add(NormalizeProjectPath(remove.Groups[1].Value));

                    Match include = IncludeAttr.Match(line);
                    if (!include.Success) continue;

                    string value = include.Groups[1].Value;
                    if (string.IsNullOrWhiteSpace(value))
                        continue;

                    // ProjectReference points to another project file and is not a source item.
                    if (line.IndexOf("<ProjectReference", StringComparison.OrdinalIgnoreCase) >= 0)
                        continue;

                    foreach (string pattern in value.Split(new[] { ';' }, StringSplitOptions.RemoveEmptyEntries))
                        foreach (string file in ExpandProjectInclude(directory, pattern.Trim(), token))
                            files.Add(file);
                }
            }
            catch (IOException) { return; }
            catch (UnauthorizedAccessException) { return; }
            catch (System.Xml.XmlException) { return; }

            if (sdk && defaultItems)
            {
                foreach (string file in EnumerateProjectFiles(directory, token))
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

        private static IEnumerable<string> ExpandProjectInclude(string directory, string pattern, CancellationToken token)
        {
            if (string.IsNullOrWhiteSpace(pattern) || pattern.Contains("$(") || pattern.Contains("@("))
                yield break;
            string full = Path.GetFullPath(Path.Combine(directory, NormalizeProjectPath(pattern)));
            if (full.IndexOfAny(new[] { '*', '?' }) < 0)
            {
                if (File.Exists(full)) yield return full;
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
                string[] entries;
                try
                {
                    entries = last
                        ? Directory.GetFiles(current.Item1, part)
                        : Directory.GetDirectories(current.Item1, part == "**" ? "*" : part);
                }
                catch (UnauthorizedAccessException) { continue; }
                catch (IOException) { continue; }

                if (last)
                {
                    foreach (string file in entries) yield return file;
                    continue;
                }
                if (part == "**") pending.Push(Tuple.Create(current.Item1, current.Item2 + 1));
                foreach (string child in entries)
                {
                    if (ShouldSkipDirectory(child)) continue;
                    try
                    {
                        if ((File.GetAttributes(child) & FileAttributes.ReparsePoint) != 0) continue;
                    }
                    catch (UnauthorizedAccessException) { continue; }
                    catch (IOException) { continue; }
                    pending.Push(Tuple.Create(child, part == "**" ? current.Item2 : current.Item2 + 1));
                }
            }
        }

        /// <summary>
        /// Enumerates files in an SDK-style project without descending into excluded or hidden directories.
        /// </summary>
        private static IEnumerable<string> EnumerateProjectFiles(string root, CancellationToken token)
        {
            var pending = new Stack<string>();
            pending.Push(root);

            while (pending.Count > 0)
            {
                token.ThrowIfCancellationRequested();
                string directory = pending.Pop();

                IEnumerable<string> files;
                try
                {
                    files = Directory.EnumerateFiles(directory);
                }
                catch (UnauthorizedAccessException)
                {
                    continue;
                }
                catch (IOException)
                {
                    continue;
                }

                foreach (string file in files)
                {
                    token.ThrowIfCancellationRequested();
                    yield return file;
                }

                IEnumerable<string> children;
                try
                {
                    children = Directory.EnumerateDirectories(directory);
                }
                catch (UnauthorizedAccessException)
                {
                    continue;
                }
                catch (IOException)
                {
                    continue;
                }

                foreach (string child in children)
                {
                    token.ThrowIfCancellationRequested();

                    if (!ShouldSkipDirectory(child))
                        pending.Push(child);
                }
            }
        }

        private static bool ShouldSkipDirectory(string path)
        {
            if (Ignored.Contains(Path.GetFileName(path)))
                return true;

            try
            {
                return (File.GetAttributes(path) & FileAttributes.Hidden) != 0;
            }
            catch (UnauthorizedAccessException)
            {
                return true;
            }
            catch (IOException)
            {
                return true;
            }
        }

        private static string ResolveProjectFile(string projectFile)
        {
            if (string.IsNullOrWhiteSpace(projectFile)) return projectFile;
            if (File.Exists(projectFile)) return projectFile;

            string directory = Path.GetDirectoryName(projectFile);
            string fileName = Path.GetFileName(projectFile);
            string projectName = Path.GetFileNameWithoutExtension(projectFile);
            if (string.IsNullOrEmpty(directory) || string.IsNullOrEmpty(fileName) || string.IsNullOrEmpty(projectName))
                return projectFile;

            string nested = Path.Combine(directory, projectName, fileName);
            return File.Exists(nested) ? nested : projectFile;
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
                        IsActionable = Directory.Exists(currentPhysicalPath),
                        IconPreview = FolderIconService.GetFolderIcon(currentPhysicalPath)
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
