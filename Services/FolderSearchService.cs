using DesktopIniManager.Models; using System; using System.Collections.Generic; using System.IO; using System.Linq; using System.Threading;
namespace DesktopIniManager.Services
{
    internal sealed class FolderSearchService
    {
        private static readonly HashSet<string> DevelopmentMarkers = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            ".git", ".vs", ".vscode"
        };
        private static readonly HashSet<string> IgnoredDirectories = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            ".git", ".vs", ".vscode", ".idea", ".gradle", "bin", "obj", "node_modules", "vendor", "packages", "dist", "build", "target", "coverage"
        };
        private static readonly Dictionary<string, string> LanguageByExtension = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            { "cs", "C#" }, { "cshtml", "Razor" }, { "razor", "Razor" }, { "xaml", "XAML" },
            { "c", "C / C++" }, { "cc", "C / C++" }, { "cpp", "C / C++" }, { "cxx", "C / C++" }, { "h", "C / C++" }, { "hpp", "C / C++" }, { "hxx", "C / C++" },
            { "js", "JavaScript" }, { "jsx", "JavaScript / JSX" }, { "mjs", "JavaScript" }, { "cjs", "JavaScript" }, { "ts", "TypeScript" }, { "tsx", "TypeScript / TSX" },
            { "php", "PHP" }, { "phtml", "PHP" }, { "py", "Python" }, { "pyw", "Python" }, { "java", "Java" }, { "jsp", "Java / JSP" },
            { "kt", "Kotlin" }, { "kts", "Kotlin" }, { "go", "Go" }, { "rs", "Rust" }, { "rb", "Ruby" },
            { "pas", "Delphi / Object Pascal" }, { "dpr", "Delphi / Object Pascal" }, { "dpk", "Delphi / Object Pascal" }, { "dproj", "Delphi / Object Pascal" }, { "dfm", "Delphi / Object Pascal" }, { "fmx", "Delphi / Object Pascal" },
            { "vbp", "Visual Basic 6 / BASIC" }, { "bas", "Visual Basic 6 / BASIC" }, { "frm", "Visual Basic 6 / BASIC" }, { "cls", "Visual Basic 6 / BASIC" },
            { "vb", "VB.NET" }, { "vbproj", "VB.NET" }, { "fs", "F#" }, { "fsx", "F#" },
            { "html", "HTML" }, { "htm", "HTML" }, { "css", "CSS" }, { "scss", "CSS / Sass" }, { "sass", "CSS / Sass" },
            { "xml", "XML" }, { "sql", "SQL" }, { "ps1", "PowerShell" }, { "psm1", "PowerShell" }, { "sh", "Shell" }, { "bash", "Shell" }, { "zsh", "Shell" },
            { "swift", "Swift" }, { "m", "Objective-C" }, { "mm", "Objective-C++" }, { "dart", "Dart" }, { "lua", "Lua" }, { "r", "R" },
            { "vue", "Vue" }, { "svelte", "Svelte" }, { "scala", "Scala" }, { "groovy", "Groovy" }, { "ex", "Elixir" }, { "exs", "Elixir" },
            { "erl", "Erlang" }, { "hrl", "Erlang" }, { "hs", "Haskell" }, { "clj", "Clojure" }, { "cljs", "ClojureScript" }
        };
        private static readonly HashSet<string> ProjectExtensions = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "sln", "slnx", "csproj", "vbproj", "fsproj", "vcxproj", "vbp", "dproj", "dpr", "xcodeproj"
        };
        private static readonly HashSet<string> ProjectFileNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "package.json", "composer.json", "pyproject.toml", "cargo.toml", "go.mod", "pom.xml", "build.gradle", "settings.gradle", "cmakelists.txt", "makefile"
        };

        public void Search(string root, string query, Action<FolderMatch> found, Action<int> progress, CancellationToken token)
        {
            var keys = query.Split((char[])null, StringSplitOptions.RemoveEmptyEntries).Select(x => x.Trim().TrimStart('*').ToLowerInvariant()).Distinct().ToArray();
            if (keys.Length == 0) return;
            bool developerMode = keys.Any(key => MarkerMatches(".git", key));
            var pending = new Stack<SearchNode>(); pending.Push(new SearchNode(root, false)); int scanned = 0;
            while (pending.Count > 0)
            {
                token.ThrowIfCancellationRequested(); SearchNode node = pending.Pop(); string folder = node.Path;
                bool hasGit = HasGitMarker(folder);
                bool insideRepository = node.InsideRepository || hasGit;
                string reason = null;
                if (developerMode && hasGit) reason = AnalyzeDevelopmentFolder(folder, token, "Repository");
                else if (developerMode && node.InsideRepository && IsProjectFolder(folder)) reason = AnalyzeDevelopmentFolder(folder, token, "Project");
                if (reason == null) reason = Match(folder, keys, token);
                if (reason != null) found(new FolderMatch { Path = folder, Reason = reason });
                try
                {
                    foreach (var child in Directory.EnumerateDirectories(folder))
                        if (!IgnoredDirectories.Contains(Path.GetFileName(child))) pending.Push(new SearchNode(child, insideRepository));
                }
                catch (UnauthorizedAccessException) { } catch (IOException) { }
                if ((++scanned & 127) == 0) progress(scanned);
            }
            progress(scanned);
        }
        private static string Match(string folder, string[] keys, CancellationToken token)
        {
            string name = Path.GetFileName(folder.TrimEnd(Path.DirectorySeparatorChar)).ToLowerInvariant();
            foreach (string key in keys) if (name.Contains(key)) return "Folder name: " + key;
            try
            {
                // A marker directory is evidence about its parent. The marker itself
                // is deliberately never returned or traversed.
                foreach (string child in Directory.EnumerateDirectories(folder))
                {
                    string childName = Path.GetFileName(child);
                    if (string.Equals(childName, ".git", StringComparison.OrdinalIgnoreCase) && keys.Any(key => MarkerMatches(childName, key)))
                        return AnalyzeDevelopmentFolder(folder, token, "Repository");
                    if (DevelopmentMarkers.Contains(childName) && keys.Any(key => MarkerMatches(childName, key))) return "Development folder: contains " + childName;
                }

                var extensionCounts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
                foreach (string entry in Directory.EnumerateFileSystemEntries(folder))
                {
                    string entryName = Path.GetFileName(entry).ToLowerInvariant();
                    string ext = Path.GetExtension(entryName).TrimStart('.');
                    foreach (string key in keys)
                    {
                        string normalizedKey = key.TrimStart('.');
                        if (File.Exists(entry) && (entryName.Contains(key) || ext == normalizedKey))
                        {
                            if (!extensionCounts.ContainsKey(normalizedKey)) extensionCounts[normalizedKey] = 0;
                            extensionCounts[normalizedKey]++;
                        }
                        else if (Directory.Exists(entry) && entryName.Contains(key)) return "Contents: " + Path.GetFileName(entry);
                    }
                }
                if (extensionCounts.Count > 0)
                {
                    var counts = keys.Select(key => key.TrimStart('.'))
                        .Distinct(StringComparer.OrdinalIgnoreCase)
                        .Where(key => extensionCounts.ContainsKey(key))
                        .Select(key => "." + key + " × " + extensionCounts[key]);
                    return "Contents: " + string.Join(" · ", counts);
                }
            }
            catch (UnauthorizedAccessException) { } catch (IOException) { }
            return null;
        }

        private static string AnalyzeDevelopmentFolder(string repository, CancellationToken token, string kind)
        {
            var parts = new List<string>();
            try
            {
                var rootDirectories = new HashSet<string>(Directory.EnumerateDirectories(repository).Select(Path.GetFileName), StringComparer.OrdinalIgnoreCase);
                if (rootDirectories.Contains(".vs")) parts.Add("Visual Studio");
                if (rootDirectories.Contains(".vscode")) parts.Add("VS Code");
            }
            catch (UnauthorizedAccessException) { } catch (IOException) { }

            var languages = new Dictionary<string, LanguageAggregate>(StringComparer.OrdinalIgnoreCase);
            bool hasVisualStudioSolution = false;
            var pending = new Stack<string>(); pending.Push(repository);
            while (pending.Count > 0)
            {
                token.ThrowIfCancellationRequested();
                string folder = pending.Pop();
                try
                {
                    foreach (string file in Directory.EnumerateFiles(folder))
                    {
                        string extension = Path.GetExtension(file).TrimStart('.');
                        if (string.Equals(extension, "sln", StringComparison.OrdinalIgnoreCase) || string.Equals(extension, "slnx", StringComparison.OrdinalIgnoreCase)) hasVisualStudioSolution = true;
                        if (!LanguageByExtension.TryGetValue(extension, out string language)) continue;
                        if (!languages.TryGetValue(language, out LanguageAggregate aggregate)) languages[language] = aggregate = new LanguageAggregate();
                        aggregate.Count++;
                        aggregate.Extensions.Add(extension.ToLowerInvariant());
                    }
                    foreach (string child in Directory.EnumerateDirectories(folder))
                        if (!IgnoredDirectories.Contains(Path.GetFileName(child))) pending.Push(child);
                }
                catch (UnauthorizedAccessException) { } catch (IOException) { }
            }

            if (hasVisualStudioSolution && !parts.Contains("Visual Studio")) parts.Insert(0, "Visual Studio");
            int total = languages.Values.Sum(item => item.Count);
            foreach (var item in languages.OrderByDescending(item => item.Value.Count).ThenBy(item => item.Key).Take(10))
            {
                int percent = total == 0 ? 0 : (int)Math.Round(item.Value.Count * 100.0 / total);
                string extensions = string.Join("/", item.Value.Extensions.OrderBy(extension => extension).Take(5).Select(extension => "." + extension));
                parts.Add(item.Key + " (" + extensions + ") ×" + item.Value.Count + " (" + percent + "%)");
            }
            if (languages.Count > 10) parts.Add("+" + (languages.Count - 10) + " more");
            if (parts.Count == 0) return kind + " · no recognized source files";
            return kind + " · " + string.Join(" · ", parts);
        }

        private static bool HasGitMarker(string folder)
        {
            string marker = Path.Combine(folder, ".git");
            return Directory.Exists(marker) || File.Exists(marker);
        }

        private static bool IsProjectFolder(string folder)
        {
            try
            {
                int recognizedSourceFiles = 0;
                foreach (string file in Directory.EnumerateFiles(folder))
                {
                    if (ProjectExtensions.Contains(Path.GetExtension(file).TrimStart('.')) || ProjectFileNames.Contains(Path.GetFileName(file))) return true;
                    if (LanguageByExtension.ContainsKey(Path.GetExtension(file).TrimStart('.')) && ++recognizedSourceFiles >= 2) return true;
                }
                foreach (string directory in Directory.EnumerateDirectories(folder))
                    if (ProjectExtensions.Contains(Path.GetExtension(directory).TrimStart('.'))) return true;
            }
            catch (UnauthorizedAccessException) { } catch (IOException) { }
            return false;
        }

        private static bool MarkerMatches(string marker, string key)
        {
            return string.Equals(marker, key, StringComparison.OrdinalIgnoreCase)
                || string.Equals(marker.TrimStart('.'), key.TrimStart('.'), StringComparison.OrdinalIgnoreCase);
        }

        private sealed class LanguageAggregate
        {
            public int Count;
            public readonly HashSet<string> Extensions = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        }

        private sealed class SearchNode
        {
            public SearchNode(string path, bool insideRepository) { Path = path; InsideRepository = insideRepository; }
            public string Path { get; }
            public bool InsideRepository { get; }
        }
    }
}
