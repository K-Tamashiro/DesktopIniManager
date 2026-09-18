using DesktopIniManager.Models;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using FastVolumeIndex;
using DesktopIniManager.Properties;

namespace DesktopIniManager.ViewModels
{
    internal sealed partial class MainWindowViewModel
    {
        internal async Task LoadFilesAsync(FolderMatch folder)
        {
            _fileListCts?.Cancel();
            var fileListCts = new CancellationTokenSource();
            _fileListCts = fileListCts;
            _files.Clear();
            if (folder != null)
            {
                if (_treeView == 0) _physicalCurrent = folder;
                else if (_treeView == 1) _solutionCurrent = folder;
                else _searchCurrent = folder;
            }
            FilePanelTitle = folder == null ? Strings.Common_Files : string.Format(Strings.Main_FilesHeader, folder.Name);
            FilePanelPath = folder?.Path;
            if (folder == null || string.IsNullOrEmpty(folder.Path)) { SetFilePanelBusy(false); return; }
            SetFilePanelBusy(true);
            await System.Windows.Threading.Dispatcher.Yield(System.Windows.Threading.DispatcherPriority.Render);
            string[] searchKeys = (Query ?? string.Empty).Split((char[])null, StringSplitOptions.RemoveEmptyEntries)
                .Select(key => key.Trim().TrimStart('*')).Where(key => key.Length > 0).Distinct(StringComparer.CurrentCultureIgnoreCase).ToArray();

            VolumePathIndex index = _pathIndex;
            int treeView = _treeView;
            string folderPath = folder.Path;
            List<string> searchFolderPaths = null;
            if (treeView == 2)
            {
                searchFolderPaths = new List<string>();
                var stack = new Stack<FolderMatch>();
                stack.Push(folder);
                while (stack.Count > 0)
                {
                    FolderMatch node = stack.Pop();
                    searchFolderPaths.Add(node.Path);
                    for (int i = node.Children.Count - 1; i >= 0; i--)
                        stack.Push(node.Children[i]);
                }
            }

            try
            {
                var loaded = await Task.Run(() =>
                {
                    string[] paths;
                    if (treeView == 2)
                        paths = CollectSearchFiles(searchFolderPaths, fileListCts.Token);
                    else if (treeView == 1)
                        paths = CollectImmediateFiles(folderPath, fileListCts.Token);
                    else
                        paths = CollectFilesUnder(index, folderPath, fileListCts.Token);

                    int total = paths.Length;
                    bool truncated = total > MaxFileListItems;
                    if (truncated)
                    {
                        var limited = new string[MaxFileListItems];
                        Array.Copy(paths, limited, MaxFileListItems);
                        paths = limited;
                    }

                    var items = new List<FileListItem>(paths.Length);
                    foreach (string path in paths)
                    {
                        fileListCts.Token.ThrowIfCancellationRequested();
                        items.Add(new FileListItem(path, searchKeys));
                    }
                    return Tuple.Create(items, total, truncated);
                }, fileListCts.Token);

                if (fileListCts.IsCancellationRequested || !ReferenceEquals(_fileListCts, fileListCts)) return;

                foreach (FileListItem item in loaded.Item1)
                {
                    _files.Add(item);
                    if ((_files.Count % 64) == 0) await System.Windows.Threading.Dispatcher.Yield();
                    if (fileListCts.IsCancellationRequested) return;
                }

                if (loaded.Item3)
                    Status = string.Format("{0:N0} / {1:N0} files", loaded.Item1.Count, loaded.Item2);

                FileListItem firstMatch = loaded.Item1.FirstOrDefault(item => item.IsSearchMatch);
                if (firstMatch != null)
                {
                    FileScrollRequested?.Invoke(firstMatch);
                }
            }
            catch (OperationCanceledException) { }
            finally
            {
                if (ReferenceEquals(_fileListCts, fileListCts) && !fileListCts.IsCancellationRequested)
                    SetFilePanelBusy(false);
            }
        }

        internal FolderMatch FindBestSearchFolder()
        {
            List<FolderMatch> nodes = Flatten(_searchRoots).ToList();
            FolderMatch named = nodes
                .Where(item => !string.IsNullOrEmpty(item.Reason)
                    && item.Reason.StartsWith("Name:", StringComparison.OrdinalIgnoreCase))
                .OrderByDescending(item => (item.Path ?? string.Empty).Length)
                .FirstOrDefault();
            if (named != null) return named;

            FolderMatch contents = nodes
                .Where(item => !string.IsNullOrEmpty(item.Reason)
                    && item.Reason.StartsWith("Contents:", StringComparison.OrdinalIgnoreCase))
                .OrderByDescending(item => (item.Path ?? string.Empty).Length)
                .FirstOrDefault();
            return contents ?? (_searchRoots.Count > 0 ? _searchRoots[0] : null);
        }

        internal FolderMatch FindClosestFolderInCurrentTree(string path)
        {
            return FindClosestFolder(CurrentTreeRoots(), path);
        }

        internal FolderMatch FindFolderForFile(string filePath, out int treeView)
        {
            treeView = _treeView;
            string directory = NormalizeComparePath(File.Exists(filePath) && !Directory.Exists(filePath)
                ? Path.GetDirectoryName(filePath)
                : filePath);
            if (string.IsNullOrEmpty(directory)) return null;

            var trees = new[]
            {
                new { View = _treeView, Roots = (IEnumerable<FolderMatch>)CurrentTreeRoots() },
                new { View = 2, Roots = (IEnumerable<FolderMatch>)_searchRoots },
                new { View = 0, Roots = (IEnumerable<FolderMatch>)_treeRoots },
                new { View = 1, Roots = (IEnumerable<FolderMatch>)_solutionRoots }
            };

            foreach (var tree in trees)
            {
                FolderMatch exact = FindExactFolder(tree.Roots, directory);
                if (exact != null)
                {
                    treeView = tree.View;
                    return exact;
                }
            }

            foreach (var tree in trees)
            {
                FolderMatch closest = FindClosestFolder(tree.Roots, directory);
                if (closest != null)
                {
                    treeView = tree.View;
                    return closest;
                }
            }

            return null;
        }

        internal static FolderMatch FindExactFolder(IEnumerable<FolderMatch> roots, string directory)
        {
            if (roots == null || string.IsNullOrWhiteSpace(directory)) return null;
            string current = NormalizeComparePath(directory);
            return Flatten(roots).FirstOrDefault(item =>
                string.Equals(NormalizeComparePath(item.Path), current, StringComparison.OrdinalIgnoreCase));
        }

        internal static FolderMatch FindClosestFolder(IEnumerable<FolderMatch> roots, string path)
        {
            if (roots == null || string.IsNullOrWhiteSpace(path)) return null;

            string current = NormalizeComparePath(File.Exists(path) && !Directory.Exists(path)
                ? Path.GetDirectoryName(path)
                : path);
            List<FolderMatch> nodes = Flatten(roots).ToList();
            while (!string.IsNullOrEmpty(current))
            {
                FolderMatch exact = nodes.FirstOrDefault(item =>
                    string.Equals(NormalizeComparePath(item.Path), current, StringComparison.OrdinalIgnoreCase));
                if (exact != null) return exact;
                string parent = Path.GetDirectoryName(current);
                string next = NormalizeComparePath(parent);
                if (string.Equals(next, current, StringComparison.OrdinalIgnoreCase)) break;
                current = next;
            }
            return null;
        }

        private static string NormalizeComparePath(string path)
        {
            if (string.IsNullOrWhiteSpace(path)) return null;
            try { return VolumePathIndex.Normalize(path); }
            catch (ArgumentException) { return path.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar); }
            catch (NotSupportedException) { return path.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar); }
            catch (PathTooLongException) { return path.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar); }
        }

        internal static string[] CollectFilesUnder(VolumePathIndex index, string folderPath, CancellationToken token)
        {
            if (index == null || string.IsNullOrEmpty(folderPath))
                return Array.Empty<string>();

            VolumePathNode root = index.Find(folderPath);
            if (root == null)
                return Array.Empty<string>();

            var paths = new List<string>();
            var stack = new Stack<VolumePathNode>();
            stack.Push(root);
            while (stack.Count > 0)
            {
                token.ThrowIfCancellationRequested();
                VolumePathNode node = stack.Pop();
                foreach (VolumePathNode file in node.Files)
                {
                    paths.Add(file.Path);
                    if (paths.Count >= MaxFileListItems) goto Done;
                }
                for (int i = node.Directories.Count - 1; i >= 0; i--)
                {
                    VolumePathNode child = node.Directories[i];
                    if (IsDroppedTreeFolder(child.Name))
                        continue;
                    stack.Push(child);
                }
            }
        Done:
            paths.Sort(StringComparer.CurrentCultureIgnoreCase);
            return paths.ToArray();
        }

        internal static string[] CollectSearchFiles(List<string> folderPaths, CancellationToken token)
        {
            if (folderPaths == null || folderPaths.Count == 0)
                return Array.Empty<string>();

            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var paths = new List<string>();
            foreach (string folderPath in folderPaths)
            {
                token.ThrowIfCancellationRequested();
                foreach (string file in CollectImmediateFiles(folderPath, token))
                    if (seen.Add(file)) paths.Add(file);
            }

            paths.Sort(StringComparer.CurrentCultureIgnoreCase);
            return paths.ToArray();
        }

        internal static string[] CollectImmediateFiles(string folderPath, CancellationToken token)
        {
            string directory = ResolveSolutionDirectory(folderPath);
            if (string.IsNullOrEmpty(directory) || !Directory.Exists(directory))
                return Array.Empty<string>();

            var paths = new List<string>();
            try
            {
                foreach (string file in Directory.EnumerateFiles(directory))
                {
                    token.ThrowIfCancellationRequested();
                    if (Directory.Exists(file)) continue;
                    paths.Add(file);
                }
            }
            catch (UnauthorizedAccessException) { }
            catch (IOException) { }

            paths.Sort(StringComparer.CurrentCultureIgnoreCase);
            return paths.ToArray();
        }

        internal static string ResolveSolutionDirectory(string path)
        {
            if (string.IsNullOrWhiteSpace(path)) return null;
            if (File.Exists(path) && !Directory.Exists(path))
                path = Path.GetDirectoryName(path);
            if (string.IsNullOrEmpty(path) || !Directory.Exists(path))
                return path;

            if (HasImmediateFiles(path))
                return path;

            string name = Path.GetFileName(path.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
            if (string.IsNullOrEmpty(name))
                return path;

            string nested = Path.Combine(path, name);
            if (Directory.Exists(nested) && HasImmediateFiles(nested))
                return nested;

            return path;
        }

        private static bool HasImmediateFiles(string directory)
        {
            try
            {
                return Directory.EnumerateFiles(directory).Any();
            }
            catch (UnauthorizedAccessException)
            {
                return false;
            }
            catch (IOException)
            {
                return false;
            }
        }

        internal static string ResolveDirectoryPath(VolumePathIndex index, string path)
        {
            if (string.IsNullOrWhiteSpace(path)) return null;
            if (Directory.Exists(path)) return path;
            if (File.Exists(path))
            {
                string parent = Path.GetDirectoryName(path);
                return string.IsNullOrEmpty(parent) ? null : parent;
            }
            if (index != null)
            {
                VolumePathNode node = index.Find(path);
                if (node != null)
                    return node.IsDirectory ? node.Path : node.Parent?.Path;
            }
            string fallback = Path.GetDirectoryName(path);
            return string.IsNullOrEmpty(fallback) ? path : fallback;
        }

        internal static string[] CollectFilesFromFolders(VolumePathIndex index, List<string> folderPaths, CancellationToken token)
        {
            if (folderPaths == null || folderPaths.Count == 0)
                return Array.Empty<string>();

            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var paths = new List<string>();

            foreach (string raw in folderPaths)
            {
                token.ThrowIfCancellationRequested();
                string folderPath = ResolveDirectoryPath(index, raw);
                if (string.IsNullOrEmpty(folderPath)) continue;

                int before = paths.Count;
                if (index != null)
                {
                    VolumePathNode node = index.Find(folderPath);
                    if (node != null && !node.IsDirectory)
                        node = node.Parent;
                    if (node != null && node.IsDirectory)
                    {
                        foreach (VolumePathNode file in node.Files)
                            if (seen.Add(file.Path)) paths.Add(file.Path);
                    }
                }

                if (paths.Count == before && Directory.Exists(folderPath))
                {
                    try
                    {
                        foreach (string file in Directory.EnumerateFiles(folderPath))
                            if (seen.Add(file)) paths.Add(file);
                    }
                    catch (UnauthorizedAccessException) { }
                    catch (IOException) { }
                }
            }

            paths.Sort(StringComparer.CurrentCultureIgnoreCase);
            return paths.ToArray();
        }


        internal void Grep()
        {
            var visible = Flatten(CurrentTreeRoots())
                .Where(item => item.IsActionable && !item.IsHidden && !item.IsFilterHidden && Directory.Exists(item.Path))
                .ToList();

            if (visible.Count == 0)
            {
                _dialogs.Show(Strings.Main_NoGrepFolders, Strings.App_Title, MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            IReadOnlyList<string> scopes = GetSelectedGrepScopes();
            if (scopes.Count == 0)
            {
                var candidates = new HashSet<FolderMatch>(visible);
                foreach (var item in visible)
                {
                    var ancestor = item.Parent;
                    while (ancestor != null && !candidates.Contains(ancestor)) ancestor = ancestor.Parent;
                    if (ancestor == null) item.SetSelected(true);
                }
                scopes = GetSelectedGrepScopes();
            }

            if (scopes.Count == 0)
            {
                _dialogs.Show(Strings.Main_NoGrepFolders, Strings.App_Title, MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            OpenGrepRequested?.Invoke(scopes);
        }

        internal IReadOnlyList<string> GetSelectedGrepScopes()
        {
            // Walk the live tree, not the folder-filter flat list.
            // Same-branch children are dropped only when a selected ancestor already covers them.
            List<string> selected = Flatten(CurrentTreeRoots())
                .Where(item => item.IsActionable && item.IsSelected && !string.IsNullOrWhiteSpace(item.Path) && Directory.Exists(item.Path))
                .Select(item => NormalizeFolderPath(item.Path))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();

            return ExcludeNestedFolders(selected);
        }

        internal static string NormalizeFolderPath(string path)
        {
            return Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        }

        internal static IReadOnlyList<string> ExcludeNestedFolders(IReadOnlyList<string> folders)
        {
            var scopes = new List<string>();
            foreach (string path in folders
                .OrderBy(candidate => candidate.Length)
                .ThenBy(candidate => candidate, StringComparer.OrdinalIgnoreCase))
            {
                if (scopes.Any(parent => IsFolderAncestor(parent, path)))
                    continue;
                scopes.Add(path);
            }

            scopes.Sort(StringComparer.CurrentCultureIgnoreCase);
            return scopes;
        }

        internal static bool IsFolderAncestor(string parent, string child)
        {
            if (string.IsNullOrEmpty(parent) || string.IsNullOrEmpty(child))
                return false;
            if (string.Equals(parent, child, StringComparison.OrdinalIgnoreCase))
                return false;

            string prefix = parent.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
                            + Path.DirectorySeparatorChar;
            return child.StartsWith(prefix, StringComparison.OrdinalIgnoreCase);
        }

    }
}
