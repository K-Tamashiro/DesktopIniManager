using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;

namespace FastVolumeIndex
{
    public sealed class VolumePathIndex
    {
        private readonly Dictionary<string, VolumePathNode> _nodes;
        private VolumePathIndex(string rootPath, Dictionary<string, VolumePathNode> nodes, VolumePathNode root)
        { RootPath = rootPath; _nodes = nodes; Root = root; }
        public string RootPath { get; }
        public VolumePathNode Root { get; }
        public IReadOnlyDictionary<string, VolumePathNode> Nodes => _nodes;
        public IEnumerable<VolumePathNode> Directories => _nodes.Values.Where(node => node.IsDirectory);
        public IEnumerable<VolumePathNode> Files => _nodes.Values.Where(node => !node.IsDirectory);

        public static VolumePathIndex Build(NtfsVolumeIndex source, string searchRoot)
        {
            if (source == null) throw new ArgumentNullException(nameof(source));
            string rootPath = Normalize(searchRoot);
            var nodes = new Dictionary<string, VolumePathNode>(StringComparer.OrdinalIgnoreCase);
            foreach (MftEntry entry in source.Entries)
            {
                string path;
                try { path = Normalize(source.GetFullPath(entry)); } catch { continue; }
                if (!IsWithin(path, rootPath) || nodes.ContainsKey(path)) continue;
                nodes.Add(path, new VolumePathNode(path, entry));
            }
            VolumePathNode root;
            if (!nodes.TryGetValue(rootPath, out root))
            {
                root = new VolumePathNode(rootPath, source.FindByPath(rootPath), true);
                nodes[rootPath] = root;
            }
            foreach (VolumePathNode node in nodes.Values.OrderBy(node => node.Path.Length).ToArray())
            {
                if (ReferenceEquals(node, root)) continue;
                string parentPath = Normalize(Path.GetDirectoryName(node.Path));
                VolumePathNode parent;
                if (!nodes.TryGetValue(parentPath, out parent) || !parent.IsDirectory || parent.IsReparsePoint) continue;
                node.Parent = parent; node.Depth = parent.Depth + 1;
                if (node.IsDirectory) parent.MutableDirectories.Add(node); else parent.MutableFiles.Add(node);
            }
            var reachable = new HashSet<VolumePathNode>();
            var pending = new Stack<VolumePathNode>(); pending.Push(root);
            while (pending.Count > 0)
            {
                VolumePathNode node = pending.Pop();
                if (!reachable.Add(node)) continue;
                foreach (VolumePathNode child in node.MutableDirectories) pending.Push(child);
                foreach (VolumePathNode file in node.MutableFiles) reachable.Add(file);
            }
            foreach (string orphan in nodes.Where(pair => !reachable.Contains(pair.Value)).Select(pair => pair.Key).ToArray())
                nodes.Remove(orphan);
            foreach (VolumePathNode node in nodes.Values)
            {
                node.MutableDirectories.Sort((a, b) => StringComparer.CurrentCultureIgnoreCase.Compare(a.Name, b.Name));
                node.MutableFiles.Sort((a, b) => StringComparer.CurrentCultureIgnoreCase.Compare(a.Name, b.Name));
            }
            return new VolumePathIndex(rootPath, nodes, root);
        }
        public static VolumePathIndex BuildFromFileSystem(string searchRoot, Action<int> progress, CancellationToken token)
        {
            string rootPath = Normalize(searchRoot);
            if (!Directory.Exists(rootPath)) throw new DirectoryNotFoundException(rootPath);
            var nodes = new Dictionary<string, VolumePathNode>(StringComparer.OrdinalIgnoreCase);
            var root = new VolumePathNode(rootPath, null, true);
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
                        nodes[path] = new VolumePathNode(path, null, true, (attributes & FileAttributes.ReparsePoint) != 0);
                        if ((attributes & FileAttributes.ReparsePoint) == 0) pending.Push(path);
                    }
                    foreach (string file in Directory.EnumerateFiles(folder))
                    {
                        token.ThrowIfCancellationRequested();
                        string path = Normalize(file);
                        if (!nodes.ContainsKey(path)) nodes[path] = new VolumePathNode(path, null, false);
                    }
                }
                catch (UnauthorizedAccessException) { }
                catch (IOException) { }
                if ((++scanned & 63) == 0) progress?.Invoke(scanned);
            }
            LinkNodes(nodes, root);
            progress?.Invoke(scanned);
            return new VolumePathIndex(rootPath, nodes, root);
        }

        private static void LinkNodes(Dictionary<string, VolumePathNode> nodes, VolumePathNode root)
        {
            foreach (VolumePathNode node in nodes.Values.OrderBy(node => node.Path.Length).ToArray())
            {
                if (ReferenceEquals(node, root)) continue;
                string parentPath = Normalize(Path.GetDirectoryName(node.Path));
                VolumePathNode parent;
                if (!nodes.TryGetValue(parentPath, out parent) || !parent.IsDirectory || parent.IsReparsePoint) continue;
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
        internal VolumePathNode(string path, MftEntry entry, bool forceDirectory = false, bool forceReparsePoint = false)
        { Path = path; Entry = entry; IsDirectory = forceDirectory || (entry != null && entry.IsDirectory); _forceReparsePoint = forceReparsePoint; }
        public string Path { get; }
        public string Name => System.IO.Path.GetFileName(Path.TrimEnd(System.IO.Path.DirectorySeparatorChar));
        public bool IsDirectory { get; }
        private readonly bool _forceReparsePoint;
        public bool IsReparsePoint => _forceReparsePoint || Entry != null && (Entry.Attributes & FileAttributes.ReparsePoint) != 0;
        public int Depth { get; internal set; }
        public MftEntry Entry { get; }
        public VolumePathNode Parent { get; internal set; }
        internal List<VolumePathNode> MutableDirectories { get; } = new List<VolumePathNode>();
        internal List<VolumePathNode> MutableFiles { get; } = new List<VolumePathNode>();
        public IReadOnlyList<VolumePathNode> Directories => MutableDirectories;
        public IReadOnlyList<VolumePathNode> Files => MutableFiles;
    }
}
