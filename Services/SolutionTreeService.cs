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
        private static readonly Regex ProjectLine = new Regex("^Project\\(\"(?<type>[^\"]+)\"\\) = \"(?<name>[^\"]+)\", \"(?<path>[^\"]+)\", \"(?<id>[^\"]+)\"", RegexOptions.Compiled);
        private static readonly Regex NestedLine = new Regex("^\\s*(?<child>\\{[^}]+\\})\\s*=\\s*(?<parent>\\{[^}]+\\})", RegexOptions.Compiled);
        private static readonly HashSet<string> Ignored = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { ".git", ".vs", ".vscode", "bin", "obj", "node_modules", "packages", "vendor", "dist", "build", "target" };

        public static List<FolderMatch> Build(string root, CancellationToken token)
        {
            var solutions = new List<FolderMatch>(); var pending = new Stack<string>(); pending.Push(root);
            while (pending.Count > 0)
            {
                token.ThrowIfCancellationRequested(); string folder = pending.Pop();
                try
                {
                    foreach (string solution in Directory.EnumerateFiles(folder, "*.sln")) solutions.Add(Parse(solution));
                    foreach (string child in Directory.EnumerateDirectories(folder))
                        if (!Ignored.Contains(Path.GetFileName(child)) && (File.GetAttributes(child) & FileAttributes.Hidden) == 0) pending.Push(child);
                }
                catch (UnauthorizedAccessException) { } catch (IOException) { }
            }
            return solutions.OrderBy(item => item.Name).ToList();
        }

        private static FolderMatch Parse(string solutionPath)
        {
            string solutionDirectory = Path.GetDirectoryName(solutionPath);
            var entries = new Dictionary<string, Entry>(StringComparer.OrdinalIgnoreCase);
            var nested = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            bool inNested = false;
            foreach (string line in File.ReadLines(solutionPath))
            {
                Match project = ProjectLine.Match(line);
                if (project.Success)
                {
                    string id = project.Groups["id"].Value;
                    entries[id] = new Entry { Id = id, Type = project.Groups["type"].Value, Name = project.Groups["name"].Value, RelativePath = project.Groups["path"].Value };
                    continue;
                }
                if (line.IndexOf("GlobalSection(NestedProjects)", StringComparison.OrdinalIgnoreCase) >= 0) { inNested = true; continue; }
                if (inNested && line.TrimStart().StartsWith("EndGlobalSection", StringComparison.OrdinalIgnoreCase)) { inNested = false; continue; }
                if (inNested) { Match relation = NestedLine.Match(line); if (relation.Success) nested[relation.Groups["child"].Value] = relation.Groups["parent"].Value; }
            }

            var nodes = entries.ToDictionary(pair => pair.Key, pair => CreateNode(pair.Value, solutionDirectory), StringComparer.OrdinalIgnoreCase);
            var root = new FolderMatch { DisplayName = Path.GetFileName(solutionPath), Path = solutionDirectory, Reason = "Solution · " + entries.Values.Count(item => !IsSolutionFolder(item)) + " projects", IsActionable = false, IconPreview = FolderIconService.GetFolderIcon(solutionDirectory) };
            foreach (var pair in nodes)
            {
                if (nested.TryGetValue(pair.Key, out string parentId) && nodes.TryGetValue(parentId, out FolderMatch parent)) parent.Children.Add(pair.Value);
                else root.Children.Add(pair.Value);
            }
            SortProjectChildren(root.Children);
            return root;
        }

        private static FolderMatch CreateNode(Entry entry, string solutionDirectory)
        {
            bool folder = IsSolutionFolder(entry);
            string physicalPath = folder ? solutionDirectory : Path.GetFullPath(Path.Combine(solutionDirectory, entry.RelativePath.Replace('\\', Path.DirectorySeparatorChar)));
            if (!folder) physicalPath = Directory.Exists(physicalPath) ? physicalPath : Path.GetDirectoryName(physicalPath);
            var node = new FolderMatch { DisplayName = entry.Name, Path = physicalPath, Reason = folder ? "Solution folder" : "Project · " + Path.GetFileName(entry.RelativePath), IsActionable = !folder && Directory.Exists(physicalPath), IconPreview = FolderIconService.GetFolderIcon(physicalPath) };
            string projectFile = folder ? null : Path.GetFullPath(Path.Combine(solutionDirectory, entry.RelativePath));
            if (File.Exists(projectFile)) PopulateProject(node, projectFile);
            return node;
        }
        private static void PopulateProject(FolderMatch project, string projectFile)
        {
            string directory = Path.GetDirectoryName(projectFile); var files = new HashSet<string>(StringComparer.OrdinalIgnoreCase); var removed = new HashSet<string>(StringComparer.OrdinalIgnoreCase); bool sdk = false;
            try
            {
                var document = new XmlDocument(); document.Load(projectFile); sdk = document.DocumentElement != null && document.DocumentElement.HasAttribute("Sdk");
                foreach (XmlNode item in document.SelectNodes("//*[local-name()='Compile' or local-name()='None' or local-name()='Content' or local-name()='EmbeddedResource']"))
                {
                    string include = item.Attributes?["Include"]?.Value; string remove = item.Attributes?["Remove"]?.Value; string link = item.SelectSingleNode("*[local-name()='Link']")?.InnerText;
                    if (!string.IsNullOrWhiteSpace(remove)) removed.Add(NormalizeProjectPath(remove));
                    if (string.IsNullOrWhiteSpace(include) || include.IndexOfAny(new[] { '*', '?' }) >= 0) continue;
                    string full = Path.GetFullPath(Path.Combine(directory, NormalizeProjectPath(include)));
                    if (File.Exists(full)) files.Add(string.IsNullOrWhiteSpace(link) ? full : Path.Combine(directory, NormalizeProjectPath(link)));
                }
            }
            catch (XmlException) { return; } catch (IOException) { return; }
            if (sdk) foreach (string file in Directory.EnumerateFiles(directory, "*", SearchOption.AllDirectories))
            {
                string relative = NormalizeProjectPath(file.Substring(directory.Length).TrimStart(Path.DirectorySeparatorChar));
                if (relative.Split(Path.DirectorySeparatorChar).Any(part => Ignored.Contains(part)) || removed.Contains(relative)) continue;
                files.Add(file);
            }
            AddVirtual(project, "Properties", directory, "Project properties"); AddVirtual(project, "依存関係", directory, "Dependencies");
            foreach (string file in files.OrderBy(value => value, StringComparer.CurrentCultureIgnoreCase))
            {
                string relative = file.StartsWith(directory, StringComparison.OrdinalIgnoreCase) ? file.Substring(directory.Length).TrimStart(Path.DirectorySeparatorChar) : Path.GetFileName(file);
                if (!string.Equals(relative, Path.GetFileName(projectFile), StringComparison.OrdinalIgnoreCase)) AddProjectFile(project, directory, relative, file);
            }
            SortProjectChildren(project.Children);
        }
        private static string NormalizeProjectPath(string value) => value.Replace('/', Path.DirectorySeparatorChar).Replace('\\', Path.DirectorySeparatorChar);
        private static void AddVirtual(FolderMatch parent, string name, string path, string reason) => parent.Children.Add(new FolderMatch { DisplayName = name, Path = path, Reason = reason, IsActionable = false, IconPreview = FolderIconService.GetFolderIcon(path) });
        private static void AddProjectFile(FolderMatch root, string directory, string relative, string physicalFile)
        {
            string[] parts = relative.Split(new[] { Path.DirectorySeparatorChar }, StringSplitOptions.RemoveEmptyEntries); FolderMatch parent = root; string current = directory;
            for (int i = 0; i < parts.Length - 1; i++) { current = Path.Combine(current, parts[i]); FolderMatch child = parent.Children.FirstOrDefault(item => string.Equals(item.Name, parts[i], StringComparison.OrdinalIgnoreCase)); if (child == null) { child = new FolderMatch { DisplayName = parts[i], Path = current, Reason = "Folder", IsActionable = Directory.Exists(current), IconPreview = FolderIconService.GetFolderIcon(current) }; parent.Children.Add(child); } parent = child; }
            // Files are intentionally omitted here. Selecting the logical folder already
            // shows its physical files in the file list on the right.
        }
        private static void SortProjectChildren(System.Collections.ObjectModel.ObservableCollection<FolderMatch> items)
        {
            foreach (FolderMatch item in items) SortProjectChildren(item.Children);
            FolderMatch[] ordered = items.OrderBy(item => item.Name == "Properties" ? 0 : item.Name == "依存関係" ? 1 : 2).ThenBy(item => item.Name, StringComparer.CurrentCultureIgnoreCase).ToArray();
            for (int i = 0; i < ordered.Length; i++) { int old = items.IndexOf(ordered[i]); if (old != i) items.Move(old, i); }
        }
        private static bool IsSolutionFolder(Entry entry) => string.Equals(entry.Type, SolutionFolderType, StringComparison.OrdinalIgnoreCase);
        private sealed class Entry { public string Id; public string Type; public string Name; public string RelativePath; }
    }
}
