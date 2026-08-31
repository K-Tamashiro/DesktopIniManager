using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;

namespace FastVolumeIndex
{
    public static class SolutionMapService
    {
        private const string SolutionFolderTypeId = "66A26720-8FB5-11D2-AA7E-00C04F688D29";
        private static readonly string[] VisualStudioProjectExtensions =
        {
            ".csproj", ".vbproj", ".vcxproj", ".fsproj", ".vbp", ".dproj"
        };
        private static readonly Regex ProjectLine = new Regex(
            "^Project\\(\"\\{(?<type>[^}]+)\\}\"\\)\\s*=\\s*\"(?<name>[^\"]*)\",\\s*\"(?<path>[^\"]*)\",\\s*\"\\{(?<id>[^}]+)\\}\"",
            RegexOptions.Compiled | RegexOptions.CultureInvariant);
        private static readonly Regex NestedProjectLine = new Regex(
            "^\\s*\\{(?<child>[^}]+)\\}\\s*=\\s*\\{(?<parent>[^}]+)\\}",
            RegexOptions.Compiled | RegexOptions.CultureInvariant);

        public static IReadOnlyList<SolutionMap> Build(NtfsVolumeIndex index, string searchRoot)
        {
            if (index == null)
                throw new ArgumentNullException(nameof(index));

            IReadOnlyList<MftEntry> solutions = index.FindFiles(searchRoot, new[] { ".sln" });
            var result = new List<SolutionMap>(solutions.Count);
            foreach (MftEntry solution in solutions)
                result.Add(Parse(index, solution));
            return result;
        }

        public static IReadOnlyList<SolutionMap> Build(NtfsVolumeIndex index, VolumePathIndex paths)
        {
            if (index == null) throw new ArgumentNullException(nameof(index));
            if (paths == null) throw new ArgumentNullException(nameof(paths));
            var result = new List<SolutionMap>();
            foreach (VolumePathNode node in paths.FindFiles(new[] { ".sln" })
                .OrderBy(node => node.Path, StringComparer.OrdinalIgnoreCase))
                if (node.Entry != null) result.Add(Parse(index, node.Entry));
            return result;
        }

        public static SolutionCoverage BuildCoverage(NtfsVolumeIndex index, string searchRoot)
        {
            IReadOnlyList<SolutionMap> solutions = Build(index, searchRoot);
            IReadOnlyList<MftEntry> physicalProjects = index.FindFiles(searchRoot,
                VisualStudioProjectExtensions);
            var linkedIds = new HashSet<ulong>(solutions
                .SelectMany(solution => solution.Projects)
                .Where(project => project.Entry != null)
                .Select(project => project.Entry.Id));

            IReadOnlyList<MftEntry> unreferenced = physicalProjects
                .Where(project => !linkedIds.Contains(project.Id))
                .OrderBy(index.GetFullPath, StringComparer.OrdinalIgnoreCase)
                .ToList();
            IReadOnlyList<SolutionProject> missing = solutions
                .SelectMany(solution => solution.Projects)
                .Where(project => !project.IsSolutionFolder && !project.Exists)
                .ToList();

            return new SolutionCoverage(solutions, unreferenced, missing);
        }

        private static SolutionMap Parse(NtfsVolumeIndex index, MftEntry solution)
        {
            string solutionPath = index.GetFullPath(solution);
            string solutionDirectory = Path.GetDirectoryName(solutionPath);
            var projects = new List<SolutionProject>();
            var nestedProjects = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            bool readingNestedProjects = false;

            foreach (string line in File.ReadLines(solutionPath))
            {
                string trimmedLine = line.Trim();
                if (trimmedLine.StartsWith("GlobalSection(NestedProjects)", StringComparison.OrdinalIgnoreCase))
                {
                    readingNestedProjects = true;
                    continue;
                }
                if (readingNestedProjects && trimmedLine.StartsWith("EndGlobalSection",
                    StringComparison.OrdinalIgnoreCase))
                {
                    readingNestedProjects = false;
                    continue;
                }
                if (readingNestedProjects)
                {
                    Match nestedMatch = NestedProjectLine.Match(line);
                    if (nestedMatch.Success)
                        nestedProjects[nestedMatch.Groups["child"].Value] = nestedMatch.Groups["parent"].Value;
                    continue;
                }

                Match match = ProjectLine.Match(line);
                if (!match.Success)
                    continue;

                string name = match.Groups["name"].Value;
                string relativePath = match.Groups["path"].Value;
                string typeId = match.Groups["type"].Value;
                string projectId = match.Groups["id"].Value;
                bool isSolutionFolder = string.Equals(typeId, SolutionFolderTypeId,
                    StringComparison.OrdinalIgnoreCase);

                string fullPath = null;
                MftEntry entry = null;
                if (!isSolutionFolder)
                {
                    try
                    {
                        fullPath = Path.GetFullPath(Path.Combine(solutionDirectory,
                            relativePath.Replace('/', Path.DirectorySeparatorChar)));
                        if (File.Exists(fullPath) || Directory.Exists(fullPath))
                            entry = index.FindByPath(fullPath);
                    }
                    catch (ArgumentException)
                    {
                        fullPath = null;
                    }
                    catch (NotSupportedException)
                    {
                        fullPath = null;
                    }
                }

                projects.Add(new SolutionProject(name, relativePath, typeId, projectId, fullPath, entry,
                    isSolutionFolder));
            }

            foreach (SolutionProject project in projects)
            {
                if (nestedProjects.TryGetValue(project.ProjectId, out string parentProjectId))
                    project.ParentProjectId = parentProjectId;
            }

            return new SolutionMap(solution, projects);
        }
    }
}
