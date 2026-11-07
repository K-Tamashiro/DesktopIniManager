using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;

namespace FastVolumeIndex;

public sealed partial class VolumePathIndex
{
    private static readonly HashSet<string> ProjectFileExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".sln", ".slnx",
        ".csproj", ".vbproj", ".fsproj", ".vcxproj", ".vcproj",
        ".dproj", ".dpr", ".vbp"
    };

    private readonly Dictionary<string, VolumePathNode> _nodes;

    /// <summary>
    ///     構築済みのノード辞書からパス インデックスを作成します。
    /// </summary>
    /// <param name="rootPath">インデックスのルート パス。</param>
    /// <param name="nodes">パスをキーとするノード辞書。</param>
    /// <param name="root">ルート ノード。</param>
    /// <param name="projectFiles">列挙中に見つかったプロジェクト ファイルのパス。</param>
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
        var rootPath = Normalize(searchRoot);
        if (!Directory.Exists(rootPath)) throw new DirectoryNotFoundException(rootPath);
        var nodes = new Dictionary<string, VolumePathNode>(StringComparer.OrdinalIgnoreCase);
        var root = new VolumePathNode(rootPath, FileAttributes.Directory, true);
        nodes[rootPath] = root;
        var pending = new Stack<string>();
        pending.Push(rootPath);
        var scanned = 0;
        while (pending.Count > 0)
        {
            token.ThrowIfCancellationRequested();
            var folder = pending.Pop();
            try
            {
                foreach (var directory in Directory.EnumerateDirectories(folder))
                {
                    token.ThrowIfCancellationRequested();
                    var path = Normalize(directory);
                    if (nodes.ContainsKey(path)) continue;
                    FileAttributes attributes;
                    try
                    {
                        attributes = File.GetAttributes(path);
                    }
                    catch
                    {
                        attributes = 0;
                    }

                    if ((attributes & FileAttributes.Hidden) != 0) continue;
                    nodes[path] = new VolumePathNode(path, attributes, true);
                    if ((attributes & FileAttributes.ReparsePoint) == 0) pending.Push(path);
                }

                foreach (var file in Directory.EnumerateFiles(folder))
                {
                    token.ThrowIfCancellationRequested();
                    var path = Normalize(file);
                    if (!nodes.ContainsKey(path)) nodes[path] = new VolumePathNode(path, File.GetAttributes(path));
                }
            }
            catch (UnauthorizedAccessException)
            {
            }
            catch (IOException)
            {
            }

            if ((++scanned & 63) == 0) progress?.Invoke(scanned);
        }

        LinkNodes(nodes, root);
        progress?.Invoke(scanned);
        return new VolumePathIndex(rootPath, nodes, root, new List<string>());
    }

    /// <summary>
    ///     各ノードを親ディレクトリ ノードに関連付けて階層と深さを設定し、子ノードを名前順に並べ替えます。
    /// </summary>
    /// <param name="nodes">パスをキーとするノード辞書。</param>
    /// <param name="root">ルート ノード。</param>
    private static void LinkNodes(Dictionary<string, VolumePathNode> nodes, VolumePathNode root)
    {
        foreach (var node in nodes.Values.OrderBy(node => node.Path.Length).ToArray())
        {
            if (ReferenceEquals(node, root)) continue;
            // Node paths are already normalized, so their directory name normally is a key as-is;
            // fall back to full normalization (GetFullPath) only when it is not.
            var directoryName = Path.GetDirectoryName(node.Path);
            VolumePathNode parent;
            if ((directoryName == null || !nodes.TryGetValue(directoryName, out parent)) &&
                !nodes.TryGetValue(Normalize(directoryName), out parent)) continue;
            if (!parent.IsDirectory) continue;
            node.Parent = parent;
            node.Depth = parent.Depth + 1;
            if (node.IsDirectory) parent.MutableDirectories.Add(node);
            else parent.MutableFiles.Add(node);
        }

        foreach (var node in nodes.Values)
        {
            node.MutableDirectories.Sort((a, b) => StringComparer.CurrentCultureIgnoreCase.Compare(a.Name, b.Name));
            node.MutableFiles.Sort((a, b) => StringComparer.CurrentCultureIgnoreCase.Compare(a.Name, b.Name));
        }
    }

    /// <summary>
    ///     指定パスに対応するノードを検索します。
    /// </summary>
    /// <param name="path">検索するパス。</param>
    /// <returns>見つかったノード。存在しない場合は null。</returns>
    public VolumePathNode Find(string path)
    {
        VolumePathNode node;
        return _nodes.TryGetValue(Normalize(path), out node) ? node : null;
    }

    /// <summary>
    ///     パスに検索語のいずれかを含むノードを列挙します。検索語が空の場合はすべてのディレクトリを返します。
    /// </summary>
    /// <param name="query">空白区切りの検索語。</param>
    /// <returns>一致したノード。</returns>
    public IEnumerable<VolumePathNode> Search(string query)
    {
        if (string.IsNullOrWhiteSpace(query)) return Directories;
        var terms = query.Split((char[])null, StringSplitOptions.RemoveEmptyEntries);
        return _nodes.Values.Where(node =>
            terms.Any(term => node.Path.IndexOf(term.TrimStart('*'), StringComparison.OrdinalIgnoreCase) >= 0));
    }

    /// <summary>
    ///     拡張子またはファイル名が一致するファイル ノードを列挙します。
    /// </summary>
    /// <param name="extensions">対象とする拡張子。</param>
    /// <param name="exactNames">完全一致で対象とするファイル名。</param>
    /// <returns>一致したファイル ノード。</returns>
    public IEnumerable<VolumePathNode> FindFiles(IEnumerable<string> extensions, IEnumerable<string> exactNames = null)
    {
        var extensionSet =
            new HashSet<string>(extensions ?? Enumerable.Empty<string>(), StringComparer.OrdinalIgnoreCase);
        var nameSet = new HashSet<string>(exactNames ?? Enumerable.Empty<string>(), StringComparer.OrdinalIgnoreCase);
        return Files.Where(node => extensionSet.Contains(Path.GetExtension(node.Name)) || nameSet.Contains(node.Name));
    }

    /// <summary>
    ///     .git を含むフォルダーをリポジトリのルートとして列挙します。
    /// </summary>
    /// <returns>リポジトリのルート ノード。</returns>
    public IEnumerable<VolumePathNode> RepositoryRoots()
    {
        return _nodes.Values
            .Where(node => string.Equals(node.Name, ".git", StringComparison.OrdinalIgnoreCase))
            .Select(node => node.Parent).Where(node => node != null).Distinct();
    }

    /// <summary>
    ///     パスを絶対パスに変換し、ドライブ ルート以外の末尾の区切り文字を取り除きます。
    /// </summary>
    /// <param name="path">正規化するパス。</param>
    /// <returns>正規化したパス。空の場合は空文字列。</returns>
    public static string Normalize(string path)
    {
        if (string.IsNullOrWhiteSpace(path)) return string.Empty;
        var full = Path.GetFullPath(path.Replace(Path.AltDirectorySeparatorChar, Path.DirectorySeparatorChar));
        var root = Path.GetPathRoot(full);
        return string.Equals(full.TrimEnd(Path.DirectorySeparatorChar), root!.TrimEnd(Path.DirectorySeparatorChar),
            StringComparison.OrdinalIgnoreCase)
            ? root
            : full.TrimEnd(Path.DirectorySeparatorChar);
    }

    /// <summary>
    ///     パスがルートと同じか、ルート配下にあるかどうかを判定します。
    /// </summary>
    /// <param name="path">判定するパス。</param>
    /// <param name="root">基準となるルート パス。</param>
    /// <returns>ルートと同じかその配下にある場合は true。</returns>
    public static bool IsWithin(string path, string root)
    {
        return string.Equals(path, root, StringComparison.OrdinalIgnoreCase) ||
               path.StartsWith(root.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar,
                   StringComparison.OrdinalIgnoreCase);
    }
}

public sealed class VolumePathNode
{
    /// <summary>
    ///     パスと属性からノードを作成します。
    /// </summary>
    /// <param name="path">ノードのフル パス。</param>
    /// <param name="attributes">ファイル属性。</param>
    /// <param name="forceDirectory">属性に関係なくディレクトリとして扱う場合は true。</param>
    internal VolumePathNode(string path, FileAttributes attributes, bool forceDirectory = false)
    {
        Path = path;
        // Computed once: Name is read in sort comparers and search loops for every node.
        Name = System.IO.Path.GetFileName(path.TrimEnd(System.IO.Path.DirectorySeparatorChar));
        Attributes = attributes;
        IsDirectory = forceDirectory || (attributes & FileAttributes.Directory) != 0;
    }

    public string Path { get; }
    public string Name { get; }
    public bool IsDirectory { get; }
    public bool IsReparsePoint => (Attributes & FileAttributes.ReparsePoint) != 0;
    public int Depth { get; internal set; }
    public FileAttributes Attributes { get; }
    public VolumePathNode Parent { get; internal set; }
    internal List<VolumePathNode> MutableDirectories { get; } = new();
    internal List<VolumePathNode> MutableFiles { get; } = new();
    public IReadOnlyList<VolumePathNode> Directories => MutableDirectories;
    public IReadOnlyList<VolumePathNode> Files => MutableFiles;
}