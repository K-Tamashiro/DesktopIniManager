using DesktopIniManager.Models;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using System.Xml;

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

        private static FolderMatch Parse(string solutionPath, CancellationToken token)
        {
            string solutionDirectory = Path.GetDirectoryName(solutionPath);
            var entries = new Dictionary<string, Entry>(StringComparer.OrdinalIgnoreCase);
            var nested = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            bool inNested = false;

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
                    continue;
                }

                if (inNested && line.TrimStart().StartsWith("EndGlobalSection", StringComparison.OrdinalIgnoreCase))
                {
                    inNested = false;
                    continue;
                }

                if (inNested)
                {
                    Match relation = NestedLine.Match(line);
                    if (relation.Success)
                        nested[relation.Groups["child"].Value] = relation.Groups["parent"].Value;
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
                Reason = "Solution · " + entries.Values.Count(item => !IsSolutionFolder(item)) + " projects",
                IsActionable = false,
                IconPreview = FolderIconService.GetFolderIcon(solutionDirectory)
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
            string physicalPath = folder
                ? solutionDirectory
                : Path.GetFullPath(Path.Combine(
                    solutionDirectory,
                    entry.RelativePath.Replace('\\', Path.DirectorySeparatorChar)));

            if (!folder)
                physicalPath = Directory.Exists(physicalPath) ? physicalPath : Path.GetDirectoryName(physicalPath);

            var node = new FolderMatch
            {
                DisplayName = entry.Name,
                Path = physicalPath,
                Reason = folder ? "Solution folder" : "Project · " + Path.GetFileName(entry.RelativePath),
                IsActionable = !folder && Directory.Exists(physicalPath),
                IconPreview = FolderIconService.GetFolderIcon(physicalPath)
            };

            string projectFile = folder
                ? null
                : Path.GetFullPath(Path.Combine(solutionDirectory, entry.RelativePath));

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

            try
            {
                var document = new XmlDocument();
                document.Load(projectFile);
                sdk = document.DocumentElement != null && document.DocumentElement.HasAttribute("Sdk");

                foreach (XmlNode item in document.SelectNodes(
                    "//*[local-name()='Compile' or local-name()='None' or local-name()='Content' or local-name()='EmbeddedResource']"))
                {
                    token.ThrowIfCancellationRequested();

                    string include = item.Attributes?["Include"]?.Value;
                    string remove = item.Attributes?["Remove"]?.Value;
                    string link = item.SelectSingleNode("*[local-name()='Link']")?.InnerText;

                    if (!string.IsNullOrWhiteSpace(remove))
                        removed.Add(NormalizeProjectPath(remove));

                    if (string.IsNullOrWhiteSpace(include) || include.IndexOfAny(new[] { '*', '?' }) >= 0)
                        continue;

                    string full = Path.GetFullPath(Path.Combine(directory, NormalizeProjectPath(include)));
                    if (File.Exists(full))
                    {
                        files.Add(string.IsNullOrWhiteSpace(link)
                            ? full
                            : Path.Combine(directory, NormalizeProjectPath(link)));
                    }
                }
            }
            catch (XmlException) { return; }
            catch (IOException) { return; }

            if (sdk)
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

            AddVirtual(project, "Properties", directory, "Project properties");
            AddVirtual(project, "依存関係", directory, "Dependencies");

            // フォルダーノードを線形検索しないよう、論理パス -> ノードをキャッシュする。
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

        /// <summary>
        /// SDK-style project のファイルを列挙する。
        /// SearchOption.AllDirectories は使用せず、除外/Hidden ディレクトリには最初から降りない。
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

        private static string NormalizeProjectPath(string value) =>
            value.Replace('/', Path.DirectorySeparatorChar)
                 .Replace('\\', Path.DirectorySeparatorChar);

        private static void AddVirtual(FolderMatch parent, string name, string path, string reason)
        {
            parent.Children.Add(new FolderMatch
            {
                DisplayName = name,
                Path = path,
                Reason = reason,
                IsActionable = false,
                IconPreview = FolderIconService.GetFolderIcon(path)
            });
        }

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
                .OrderBy(item => item.Name == "Properties" ? 0 : item.Name == "依存関係" ? 1 : 2)
                .ThenBy(item => item.Name, StringComparer.CurrentCultureIgnoreCase)
                .ToArray();

            // IndexOf + Move の繰り返し(O(n^2))を避ける。
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
