using DesktopIniManager.Models;
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
                    foreach (string child in Directory.EnumerateDirectories(folder)) if (!Ignored.Contains(Path.GetFileName(child))) pending.Push(child);
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
            return root;
        }

        private static FolderMatch CreateNode(Entry entry, string solutionDirectory)
        {
            bool folder = IsSolutionFolder(entry);
            string physicalPath = folder ? solutionDirectory : Path.GetFullPath(Path.Combine(solutionDirectory, entry.RelativePath.Replace('\\', Path.DirectorySeparatorChar)));
            if (!folder) physicalPath = Directory.Exists(physicalPath) ? physicalPath : Path.GetDirectoryName(physicalPath);
            return new FolderMatch { DisplayName = entry.Name, Path = physicalPath, Reason = folder ? "Solution folder" : "Project · " + Path.GetFileName(entry.RelativePath), IsActionable = !folder && Directory.Exists(physicalPath), IconPreview = FolderIconService.GetFolderIcon(physicalPath) };
        }
        private static bool IsSolutionFolder(Entry entry) => string.Equals(entry.Type, SolutionFolderType, StringComparison.OrdinalIgnoreCase);
        private sealed class Entry { public string Id; public string Type; public string Name; public string RelativePath; }
    }
}
