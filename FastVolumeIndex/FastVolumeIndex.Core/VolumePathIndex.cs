using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;

namespace FastVolumeIndex
{
    public sealed class VolumePathIndex
    {
        private static readonly HashSet<string> ProjectFileExtensions = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            ".sln", ".slnx",
            ".csproj", ".vbproj", ".fsproj", ".vcxproj", ".vcproj",
            ".dproj", ".dpr", ".vbp"
        };

        private readonly Dictionary<string, VolumePathNode> _nodes;

        private VolumePathIndex(
            string rootPath,
            Dictionary<string, VolumePathNode> nodes,
            VolumePathNode root,
            List<string> projectFiles)
        {
            RootPath = rootPath;
            _nodes = nodes;
            Root = root;
            ProjectFiles = projectFiles ?? new List<string>();
        }

        public string RootPath { get; }
        public VolumePathNode Root { get; }
        public IReadOnlyList<string> ProjectFiles { get; }
        public IReadOnlyDictionary<string, VolumePathNode> Nodes => _nodes;
        public IEnumerable<VolumePathNode> Directories => _nodes.Values.Where(node => node.IsDirectory);
        public IEnumerable<VolumePathNode> Files => _nodes.Values.Where(node => !node.IsDirectory);

        /// <summary>Builds a path index by enumerating the file system directly.</summary>
        public static VolumePathIndex BuildFromFileSystem(string searchRoot, Action<int> progress, CancellationToken token)
        {
            string rootPath = Normalize(searchRoot);
            if (!Directory.Exists(rootPath)) throw new DirectoryNotFoundException(rootPath);
            var nodes = new Dictionary<string, VolumePathNode>(StringComparer.OrdinalIgnoreCase);
            var root = new VolumePathNode(rootPath, FileAttributes.Directory, true);
            nodes[rootPath] = root;
            var pending = new Stack<string>();
            pending.Push(rootPath);
            int scanned = 0;
            while (pending.Count > 0)
            {
                token.ThrowIfCancellationRequested();
                string folder = pending.Pop();
                try
                {
                    foreach (string directory in Directory.EnumerateDirectories(folder))
                    {
                        token.ThrowIfCancellationRequested();
                        string path = Normalize(directory);
                        if (nodes.ContainsKey(path)) continue;
                        FileAttributes attributes;
                        try { attributes = File.GetAttributes(path); } catch { attributes = 0; }
                        if ((attributes & FileAttributes.Hidden) != 0) continue;
                        nodes[path] = new VolumePathNode(path, attributes, true);
                        if ((attributes & FileAttributes.ReparsePoint) == 0) pending.Push(path);
                    }
                    foreach (string file in Directory.EnumerateFiles(folder))
                    {
                        token.ThrowIfCancellationRequested();
                        string path = Normalize(file);
                        if (!nodes.ContainsKey(path)) nodes[path] = new VolumePathNode(path, File.GetAttributes(path));
                    }
                }
                catch (UnauthorizedAccessException) { }
                catch (IOException) { }
                if ((++scanned & 63) == 0) progress?.Invoke(scanned);
            }
            LinkNodes(nodes, root);
            progress?.Invoke(scanned);
            return new VolumePathIndex(rootPath, nodes, root, new List<string>());
        }

        private static string[] RunDirBare(string rootPath, CancellationToken token)
        {
            var psi = new ProcessStartInfo
            {
                FileName = "cmd.exe",
                Arguments = "/u /c dir /s /b \"" + rootPath.TrimEnd('\\') + "\"",
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
                StandardOutputEncoding = Encoding.Unicode
            };
            using (var process = Process.Start(psi))
            {
                if (process == null) throw new InvalidOperationException("Failed to start dir.");
                string output = process.StandardOutput.ReadToEnd();
                process.WaitForExit();
                token.ThrowIfCancellationRequested();
                if (string.IsNullOrWhiteSpace(output))
                    return Array.Empty<string>();
                return output.Replace("\r\n", "\n").Split('\n');
            }
        }

        private static void EnsureDirectory(Dictionary<string, VolumePathNode> nodes, string directoryPath)
        {
            string path = Normalize(directoryPath);
            if (string.IsNullOrEmpty(path) || nodes.ContainsKey(path)) return;
            string parentPath = Normalize(Path.GetDirectoryName(path));
            if (!string.IsNullOrEmpty(parentPath) && !nodes.ContainsKey(parentPath))
                EnsureDirectory(nodes, parentPath);
            nodes[path] = new VolumePathNode(path, FileAttributes.Directory, true);
        }

        /// <summary>Builds a path index from the platform directory-listing command.</summary>
        public static VolumePathIndex BuildFromDirCommand(string searchRoot, Action<int> progress, CancellationToken token)
        {
            string rootPath = Normalize(searchRoot);
            if (!Directory.Exists(rootPath)) throw new DirectoryNotFoundException(rootPath);
            string[] lines = RunDirBare(rootPath, token);
            var nodes = new Dictionary<string, VolumePathNode>(StringComparer.OrdinalIgnoreCase);
            var root = new VolumePathNode(rootPath, FileAttributes.Directory, true);
            nodes[rootPath] = root;
            var projectFiles = new List<string>();
            var all = new List<string>();
            foreach (string raw in lines)
            {
                token.ThrowIfCancellationRequested();
                if (string.IsNullOrWhiteSpace(raw)) continue;
                string path;
                try { path = Normalize(raw.Trim()); }
                catch (ArgumentException) { continue; }
                catch (NotSupportedException) { continue; }
                catch (PathTooLongException) { continue; }
                if (!IsWithin(path, rootPath)) continue;
                if (string.Equals(path, rootPath, StringComparison.OrdinalIgnoreCase)) continue;
                all.Add(path);
            }
            all.Sort(StringComparer.OrdinalIgnoreCase);
            // Infer directories from entries that serve as parents; no attribute lookup is required.
            var directoryHints = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            for (int i = 0; i < all.Count - 1; i++)
            {
                string current = all[i];
                string next = all[i + 1];
                if (next.StartsWith(current + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                    directoryHints.Add(current);
            }
            int scanned = 0;
            foreach (string path in all)
            {
                token.ThrowIfCancellationRequested();
                // Materialize every ancestor so the hierarchy remains connected.
                string ancestor = Normalize(Path.GetDirectoryName(path));
                while (!string.IsNullOrEmpty(ancestor) && IsWithin(ancestor, rootPath))
                {
                    EnsureDirectory(nodes, ancestor);
                    if (string.Equals(ancestor, rootPath, StringComparison.OrdinalIgnoreCase)) break;
                    ancestor = Normalize(Path.GetDirectoryName(ancestor));
                }
                if (directoryHints.Contains(path))
                {
                    EnsureDirectory(nodes, path);
                }
                else
                {
                    if (!nodes.ContainsKey(path))
                        nodes[path] = new VolumePathNode(path, FileAttributes.Normal);
                    string ext = Path.GetExtension(path);
                    if (ProjectFileExtensions.Contains(ext))
                        projectFiles.Add(path);
                }
                if ((++scanned & 63) == 0) progress?.Invoke(scanned);
            }
            EnsureFolderTreeFromFileSystem(rootPath, nodes, token);
            LinkNodes(nodes, root);
            progress?.Invoke(scanned);
            return new VolumePathIndex(rootPath, nodes, root, projectFiles);
        }
        private static void EnsureFolderTreeFromFileSystem(string rootPath, Dictionary<string, VolumePathNode> nodes, CancellationToken token)
        {
            var pending = new Stack<string>();
            pending.Push(rootPath);
            while (pending.Count > 0)
            {
                token.ThrowIfCancellationRequested();
                string folder = pending.Pop();
                IEnumerable<string> children;
                try { children = Directory.EnumerateDirectories(folder); }
                catch (UnauthorizedAccessException) { continue; }
                catch (IOException) { continue; }
                foreach (string child in children)
                {
                    token.ThrowIfCancellationRequested();
                    string name = Path.GetFileName(child);
                    if (string.Equals(name, ".git", StringComparison.OrdinalIgnoreCase)
                        || string.Equals(name, ".vs", StringComparison.OrdinalIgnoreCase)
                        || string.Equals(name, ".vscode", StringComparison.OrdinalIgnoreCase))
                        continue;
                    string path;
                    try { path = Normalize(child); }
                    catch (ArgumentException) { continue; }
                    catch (NotSupportedException) { continue; }
                    catch (PathTooLongException) { continue; }
                    if (!IsWithin(path, rootPath)) continue;
                    EnsureDirectory(nodes, path);
                    pending.Push(child);
                }
            }
        }
        private static void LinkNodes(Dictionary<string, VolumePathNode> nodes, VolumePathNode root)
        {
            foreach (VolumePathNode node in nodes.Values.OrderBy(node => node.Path.Length).ToArray())
            {
                if (ReferenceEquals(node, root)) continue;
                string parentPath = Normalize(Path.GetDirectoryName(node.Path));
                VolumePathNode parent;
                if (!nodes.TryGetValue(parentPath, out parent) || !parent.IsDirectory) continue;
                node.Parent = parent; node.Depth = parent.Depth + 1;
                if (node.IsDirectory) parent.MutableDirectories.Add(node); else parent.MutableFiles.Add(node);
            }
            foreach (VolumePathNode node in nodes.Values)
            {
                node.MutableDirectories.Sort((a, b) => StringComparer.CurrentCultureIgnoreCase.Compare(a.Name, b.Name));
                node.MutableFiles.Sort((a, b) => StringComparer.CurrentCultureIgnoreCase.Compare(a.Name, b.Name));
            }
        }

        public VolumePathNode Find(string path) { VolumePathNode node; return _nodes.TryGetValue(Normalize(path), out node) ? node : null; }

        public IEnumerable<VolumePathNode> Search(string query)
        {
            if (string.IsNullOrWhiteSpace(query)) return Directories;
            string[] terms = query.Split((char[])null, StringSplitOptions.RemoveEmptyEntries);
            return _nodes.Values.Where(node => terms.Any(term => node.Path.IndexOf(term.TrimStart('*'), StringComparison.OrdinalIgnoreCase) >= 0));
        }

        public IEnumerable<VolumePathNode> FindFiles(IEnumerable<string> extensions, IEnumerable<string> exactNames = null)
        {
            var extensionSet = new HashSet<string>(extensions ?? Enumerable.Empty<string>(), StringComparer.OrdinalIgnoreCase);
            var nameSet = new HashSet<string>(exactNames ?? Enumerable.Empty<string>(), StringComparer.OrdinalIgnoreCase);
            return Files.Where(node => extensionSet.Contains(Path.GetExtension(node.Name)) || nameSet.Contains(node.Name));
        }

        public IEnumerable<VolumePathNode> RepositoryRoots() => _nodes.Values
            .Where(node => string.Equals(node.Name, ".git", StringComparison.OrdinalIgnoreCase))
            .Select(node => node.Parent).Where(node => node != null).Distinct();

        public static string Normalize(string path)
        {
            if (string.IsNullOrWhiteSpace(path)) return string.Empty;
            string full = Path.GetFullPath(path.Replace(Path.AltDirectorySeparatorChar, Path.DirectorySeparatorChar));
            string root = Path.GetPathRoot(full);
            return string.Equals(full.TrimEnd(Path.DirectorySeparatorChar), root.TrimEnd(Path.DirectorySeparatorChar), StringComparison.OrdinalIgnoreCase)
                ? root : full.TrimEnd(Path.DirectorySeparatorChar);
        }

        public static bool IsWithin(string path, string root) => string.Equals(path, root, StringComparison.OrdinalIgnoreCase) ||
            path.StartsWith(root.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
    }



    public sealed class VolumePathNode
    {
        internal VolumePathNode(string path, FileAttributes attributes, bool forceDirectory = false)
        { Path = path; Attributes = attributes; IsDirectory = forceDirectory || (attributes & FileAttributes.Directory) != 0; }
        public string Path { get; }
        public string Name => System.IO.Path.GetFileName(Path.TrimEnd(System.IO.Path.DirectorySeparatorChar));
        public bool IsDirectory { get; }
        public bool IsReparsePoint => (Attributes & FileAttributes.ReparsePoint) != 0;
        public int Depth { get; internal set; }
        public FileAttributes Attributes { get; }
        public VolumePathNode Parent { get; internal set; }
        internal List<VolumePathNode> MutableDirectories { get; } = new List<VolumePathNode>();
        internal List<VolumePathNode> MutableFiles { get; } = new List<VolumePathNode>();
        public IReadOnlyList<VolumePathNode> Directories => MutableDirectories;
        public IReadOnlyList<VolumePathNode> Files => MutableFiles;
    }
}
