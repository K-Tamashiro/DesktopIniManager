using FastVolumeIndex;
using System;
using System.Diagnostics;
using System.Text;

namespace FastVolumeIndex.Cli
{
    internal static class Program
    {
        private static int Main(string[] args)
        {
            Console.OutputEncoding = new UTF8Encoding(false);

            if (args.Length == 0 || args[0] == "--help" || args[0] == "-h")
            {
                PrintUsage();
                return args.Length == 0 ? 1 : 0;
            }

            string root = args[0];
            string command = args.Length == 1 ? "--git" : args[1];

            try
            {
                Console.WriteLine($"Reading NTFS MFT for {root} ...");
                NtfsVolumeIndex index = NtfsVolumeIndex.Create(root);
                Console.WriteLine($"Indexed {index.EntryCount:N0} entries " +
                    $"({index.DirectoryCount:N0} folders, {index.FileCount:N0} files) " +
                    $"in {index.EnumerationTime.TotalMilliseconds:N0} ms.");

                var searchTimer = Stopwatch.StartNew();
                if (string.Equals(command, "--git", StringComparison.OrdinalIgnoreCase))
                {
                    var repositories = index.FindGitRepositoryRoots(root);
                    searchTimer.Stop();
                    foreach (MftEntry repository in repositories)
                        Console.WriteLine(index.GetFullPath(repository));
                    Console.WriteLine($"Found {repositories.Count:N0} Git repositories in {searchTimer.Elapsed.TotalMilliseconds:N0} ms.");
                }
                else if (string.Equals(command, "--analyze", StringComparison.OrdinalIgnoreCase))
                {
                    DevelopmentInventory inventory = DevelopmentScanner.Analyze(index, root);
                    searchTimer.Stop();
                    PrintSection("Repositories", inventory.Repositories, index);
                    PrintSection("Solutions", inventory.Solutions, index);
                    PrintSection("Projects and build roots", inventory.Projects, index);
                    Console.WriteLine($"Analyzed {inventory.Repositories.Count:N0} repositories, " +
                        $"{inventory.Solutions.Count:N0} solutions and {inventory.Projects.Count:N0} projects " +
                        $"in {searchTimer.Elapsed.TotalMilliseconds:N0} ms.");
                }
                else if (string.Equals(command, "--tree", StringComparison.OrdinalIgnoreCase))
                {
                    int depth = ReadDepth(args, 3);
                    bool includeFiles = Array.Exists(args, value =>
                        string.Equals(value, "--files", StringComparison.OrdinalIgnoreCase));
                    MftEntry treeRoot = index.FindByPath(root);
                    if (treeRoot == null)
                        throw new InvalidOperationException($"The root '{root}' was not found in the MFT index.");
                    searchTimer.Stop();
                    Console.WriteLine(index.GetFullPath(treeRoot));
                    PrintTree(index, treeRoot, string.Empty, 0, depth, includeFiles);
                    Console.WriteLine($"Built tree in {searchTimer.Elapsed.TotalMilliseconds:N0} ms.");
                }
                else if (string.Equals(command, "--diagnose", StringComparison.OrdinalIgnoreCase))
                {
                    MftEntry diagnosticRoot = index.FindByPath(root);
                    searchTimer.Stop();
                    if (diagnosticRoot == null)
                        throw new InvalidOperationException($"The root '{root}' was not found in the MFT index.");

                    var children = index.GetChildren(diagnosticRoot, true);
                    Console.WriteLine("MFT root diagnostic");
                    Console.WriteLine($"  Name:       {diagnosticRoot.Name}");
                    Console.WriteLine($"  ID:         0x{diagnosticRoot.Id:X}");
                    Console.WriteLine($"  Parent ID:  0x{diagnosticRoot.ParentId:X}");
                    Console.WriteLine($"  Path:       {index.GetFullPath(diagnosticRoot)}");
                    Console.WriteLine($"  Attributes: {diagnosticRoot.Attributes}");
                    Console.WriteLine($"  Children:   {children.Count:N0}");
                    foreach (MftEntry child in children)
                        Console.WriteLine($"    {(child.IsDirectory ? "[D]" : "[F]")} 0x{child.Id:X} {child.Name}");
                    Console.WriteLine($"Diagnostic completed in {searchTimer.Elapsed.TotalMilliseconds:N0} ms.");
                }
                else if (string.Equals(command, "--solutions", StringComparison.OrdinalIgnoreCase))
                {
                    var maps = SolutionMapService.Build(index, root);
                    searchTimer.Stop();
                    int linked = 0;
                    int missing = 0;
                    int solutionFolders = 0;

                    foreach (SolutionMap map in maps)
                    {
                        Console.WriteLine();
                        Console.WriteLine(index.GetFullPath(map.Solution));
                        PrintSolutionTree(map, out int mapLinked, out int mapMissing, out int mapFolders);
                        linked += mapLinked;
                        missing += mapMissing;
                        solutionFolders += mapFolders;
                    }

                    Console.WriteLine();
                    Console.WriteLine($"Mapped {maps.Count:N0} solutions: {linked:N0} linked projects, " +
                        $"{solutionFolders:N0} solution folders and {missing:N0} missing projects " +
                        $"in {searchTimer.Elapsed.TotalMilliseconds:N0} ms.");
                }
                else if (string.Equals(command, "--solution-diff", StringComparison.OrdinalIgnoreCase))
                {
                    SolutionCoverage coverage = SolutionMapService.BuildCoverage(index, root);
                    searchTimer.Stop();

                    Console.WriteLine();
                    Console.WriteLine($"Physical projects not referenced by any solution ({coverage.UnreferencedProjects.Count:N0})");
                    foreach (MftEntry project in coverage.UnreferencedProjects)
                        Console.WriteLine("  " + index.GetFullPath(project));

                    Console.WriteLine();
                    Console.WriteLine($"Broken project references ({coverage.MissingProjects.Count:N0})");
                    foreach (SolutionProject project in coverage.MissingProjects)
                        Console.WriteLine($"  {project.Name}: {project.FullPath ?? project.RelativePath}");

                    Console.WriteLine();
                    Console.WriteLine($"Compared {coverage.Solutions.Count:N0} solutions in " +
                        $"{searchTimer.Elapsed.TotalMilliseconds:N0} ms.");
                }
                else
                {
                    var matches = index.SearchNames(root, command);
                    searchTimer.Stop();
                    foreach (MftEntry match in matches)
                        Console.WriteLine(index.GetFullPath(match));
                    Console.WriteLine($"Found {matches.Count:N0} matches for '{command}' in {searchTimer.Elapsed.TotalMilliseconds:N0} ms.");
                }

                return 0;
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine("MFT search failed: " + ex.Message);
                return 2;
            }
        }

        private static int ReadDepth(string[] args, int defaultDepth)
        {
            for (int index = 2; index < args.Length - 1; index++)
            {
                if (string.Equals(args[index], "--depth", StringComparison.OrdinalIgnoreCase))
                {
                    if (int.TryParse(args[index + 1], out int depth) && depth >= 0)
                        return depth;
                    throw new ArgumentException("--depth requires a non-negative integer.");
                }
            }
            return defaultDepth;
        }

        private static void PrintSection(string title, System.Collections.Generic.IReadOnlyList<MftEntry> entries,
            NtfsVolumeIndex index)
        {
            Console.WriteLine();
            Console.WriteLine($"{title} ({entries.Count:N0})");
            foreach (MftEntry entry in entries)
                Console.WriteLine("  " + index.GetFullPath(entry));
        }

        private static void PrintTree(NtfsVolumeIndex index, MftEntry parent, string prefix, int currentDepth,
            int maxDepth, bool includeFiles)
        {
            if (currentDepth >= maxDepth)
                return;

            var children = index.GetChildren(parent, includeFiles);
            for (int childIndex = 0; childIndex < children.Count; childIndex++)
            {
                MftEntry child = children[childIndex];
                bool last = childIndex == children.Count - 1;
                Console.WriteLine(prefix + (last ? "└─ " : "├─ ") + child.Name);
                if (child.IsDirectory)
                    PrintTree(index, child, prefix + (last ? "   " : "│  "), currentDepth + 1,
                        maxDepth, includeFiles);
            }
        }

        private static void PrintSolutionTree(SolutionMap map, out int linked, out int missing,
            out int solutionFolders)
        {
            linked = 0;
            missing = 0;
            solutionFolders = 0;
            var byParent = new System.Collections.Generic.Dictionary<string,
                System.Collections.Generic.List<SolutionProject>>(StringComparer.OrdinalIgnoreCase);
            var projectIds = new System.Collections.Generic.HashSet<string>(StringComparer.OrdinalIgnoreCase);

            foreach (SolutionProject project in map.Projects)
                projectIds.Add(project.ProjectId);

            foreach (SolutionProject project in map.Projects)
            {
                string parentId = !string.IsNullOrEmpty(project.ParentProjectId)
                    && projectIds.Contains(project.ParentProjectId)
                    ? project.ParentProjectId
                    : string.Empty;
                if (!byParent.TryGetValue(parentId, out System.Collections.Generic.List<SolutionProject> children))
                {
                    children = new System.Collections.Generic.List<SolutionProject>();
                    byParent[parentId] = children;
                }
                children.Add(project);
            }

            var visited = new System.Collections.Generic.HashSet<string>(StringComparer.OrdinalIgnoreCase);
            PrintSolutionChildren(string.Empty, string.Empty, byParent, visited, ref linked, ref missing,
                ref solutionFolders);
        }

        private static void PrintSolutionChildren(string parentId, string prefix,
            System.Collections.Generic.Dictionary<string, System.Collections.Generic.List<SolutionProject>> byParent,
            System.Collections.Generic.HashSet<string> visited, ref int linked, ref int missing,
            ref int solutionFolders)
        {
            if (!byParent.TryGetValue(parentId, out System.Collections.Generic.List<SolutionProject> children))
                return;

            children.Sort((left, right) =>
            {
                int folderOrder = right.IsSolutionFolder.CompareTo(left.IsSolutionFolder);
                return folderOrder != 0
                    ? folderOrder
                    : StringComparer.OrdinalIgnoreCase.Compare(left.Name, right.Name);
            });

            for (int index = 0; index < children.Count; index++)
            {
                SolutionProject project = children[index];
                bool last = index == children.Count - 1;
                string connector = last ? "└─ " : "├─ ";

                if (project.IsSolutionFolder)
                {
                    solutionFolders++;
                    Console.WriteLine($"{prefix}{connector}[Folder]  {project.Name}");
                }
                else if (project.Exists)
                {
                    linked++;
                    Console.WriteLine($"{prefix}{connector}[Project] {project.Name}");
                    Console.WriteLine($"{prefix}{(last ? "   " : "│  ")}           {project.FullPath}");
                }
                else
                {
                    missing++;
                    Console.WriteLine($"{prefix}{connector}[Missing] {project.Name}");
                    Console.WriteLine($"{prefix}{(last ? "   " : "│  ")}           {project.RelativePath}");
                }

                if (visited.Add(project.ProjectId))
                    PrintSolutionChildren(project.ProjectId, prefix + (last ? "   " : "│  "), byParent,
                        visited, ref linked, ref missing, ref solutionFolders);
            }
        }

        private static void PrintUsage()
        {
            Console.WriteLine("FastVolumeIndex MFT search prototype");
            Console.WriteLine();
            Console.WriteLine("Usage:");
            Console.WriteLine("  mftree <search-root> --git");
            Console.WriteLine("  mftree <search-root> --analyze");
            Console.WriteLine("  mftree <search-root> --tree [--depth N] [--files]");
            Console.WriteLine("  mftree <search-root> --diagnose");
            Console.WriteLine("  mftree <search-root> --solutions");
            Console.WriteLine("  mftree <search-root> --solution-diff");
            Console.WriteLine("  mftree <search-root> <name-query>");
            Console.WriteLine();
            Console.WriteLine("Examples:");
            Console.WriteLine(@"  mftree F:\Documents\Playground --git");
            Console.WriteLine(@"  mftree F:\Documents\Playground --analyze");
            Console.WriteLine(@"  mftree F:\Documents\Playground --solutions");
            Console.WriteLine(@"  mftree F:\Documents\Playground --solution-diff");
            Console.WriteLine(@"  mftree F:\Documents\Playground --tree --depth 3");
            Console.WriteLine(@"  mftree F:\Documents\Playground .sln");
            Console.WriteLine();
            Console.WriteLine("Run as administrator. Local NTFS volumes only.");
        }
    }
}
