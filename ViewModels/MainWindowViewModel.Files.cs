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
            // Snapshot descendant folders on the UI thread for the Search tab.
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
                    string[] paths = treeView == 2
                        ? CollectFilesFromFolders(index, searchFolderPaths, fileListCts.Token)
                        : CollectFilesUnder(index, folderPath, fileListCts.Token);

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

        internal string[] FilesInFolder(string path)
        {
            VolumePathNode node = _pathIndex?.Find(path);
            if (node != null)
                return node.Files.Select(file => file.Path).ToArray();
            // Avoid disk enumeration while an index exists because cloud folders can block.
            if (_pathIndex != null)
                return Array.Empty<string>();
            try { return Directory.EnumerateFiles(path).OrderBy(item => item, StringComparer.CurrentCultureIgnoreCase).ToArray(); }
            catch { return Array.Empty<string>(); }
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
                    stack.Push(node.Directories[i]);
            }
        Done:
            paths.Sort(StringComparer.CurrentCultureIgnoreCase);
            return paths.ToArray();
        }

        internal static string[] CollectFilesFromFolders(VolumePathIndex index, List<string> folderPaths, CancellationToken token)
        {
            if (folderPaths == null || folderPaths.Count == 0)
                return Array.Empty<string>();

            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var paths = new List<string>();

            if (index != null)
            {
                foreach (string folderPath in folderPaths)
                {
                    token.ThrowIfCancellationRequested();
                    VolumePathNode node = index.Find(folderPath);
                    if (node == null) continue;
                    foreach (VolumePathNode file in node.Files)
                        if (seen.Add(file.Path)) paths.Add(file.Path);
                }
            }

            paths.Sort(StringComparer.CurrentCultureIgnoreCase);
            return paths.ToArray();
        }

        internal string[] CollectSearchTabFiles(FolderMatch folder)
        {
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var paths = new List<string>();
            var stack = new Stack<FolderMatch>();
            stack.Push(folder);
            while (stack.Count > 0)
            {
                FolderMatch node = stack.Pop();
                foreach (string path in FilesInFolder(node.Path))
                    if (seen.Add(path)) paths.Add(path);
                for (int index = node.Children.Count - 1; index >= 0; index--)
                    stack.Push(node.Children[index]);
            }
            paths.Sort(StringComparer.CurrentCultureIgnoreCase);
            return paths.ToArray();
        }


        internal void Grep()
        {
            var visible = CurrentItems()
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
