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
        private string _searchHitLabel = "0/0";
        private int _searchMatchIndex;
        private readonly List<object> _searchMatches = new List<object>();
        public event Action<FolderMatch> FolderSearchHitRequested;
        private RelayCommand _prevSearchMatchCommand;
        private RelayCommand _nextSearchMatchCommand;

        public string SearchHitLabel
        {
            get => _searchHitLabel;
            set => SetProperty(ref _searchHitLabel, value);
        }

        private string _fileListCountLabel = string.Format(Strings.Main_NItems, 0);
        public string FileListCountLabel
        {
            get => _fileListCountLabel;
            set => SetProperty(ref _fileListCountLabel, value);
        }

        public RelayCommand PrevSearchMatchCommand =>
            _prevSearchMatchCommand ?? (_prevSearchMatchCommand = new RelayCommand(PrevSearchMatch, () => _searchMatches.Count > 0));

        public RelayCommand NextSearchMatchCommand =>
            _nextSearchMatchCommand ?? (_nextSearchMatchCommand = new RelayCommand(NextSearchMatch, () => _searchMatches.Count > 0));

        internal void SetSearchResultCount(int count)
        {
            _searchResultCount = count;
        }

        public void ClearSearchHits()
        {
            _searchMatchIndex = 0;
            SyncSearchMatches(null, null);
        }

        private void RefreshSearchHitLabel()
        {
            int total = _searchMatches.Count;
            SearchHitLabel = total == 0
                ? "0/0"
                : _searchMatchIndex + "/" + total;
            PrevSearchMatchCommand.NotifyCanExecuteChanged();
            NextSearchMatchCommand.NotifyCanExecuteChanged();
        }

        private void SyncSearchMatches(IEnumerable<FileListItem> items, FileListItem preferred, bool revealOwningFolder = false, FolderMatch folder = null)
        {
            _searchMatches.Clear();
            if (folder != null && _treeView == 2)
                _searchMatches.AddRange(Flatten(new[] { folder }).Where(item => item.IsSearchMatch));
            if (items != null)
                _searchMatches.AddRange(items.Where(item => item.IsSearchMatch));
            if (_searchMatches.Count == 0)
            {
                _searchMatchIndex = 0;
                RefreshSearchHitLabel();
                return;
            }

            int index = preferred == null ? 0 : _searchMatches.IndexOf(preferred);
            _searchMatchIndex = index >= 0 ? index + 1 : 1;
            RefreshSearchHitLabel();
            RevealSearchMatch(revealOwningFolder);
        }

        private void RevealSearchMatch(bool revealOwningFolder)
        {
            object match = _searchMatches[_searchMatchIndex - 1];
            if (match is FileListItem file) FileScrollRequested?.Invoke(file, revealOwningFolder);
            else if (revealOwningFolder && match is FolderMatch folder) FolderSearchHitRequested?.Invoke(folder);
        }

        internal void NoteSelectedSearchMatch(FileListItem file)
        {
            if (file == null || !file.IsSearchMatch) return;
            int index = _searchMatches.IndexOf(file);
            if (index < 0) return;
            _searchMatchIndex = index + 1;
            RefreshSearchHitLabel();
        }

        private void PrevSearchMatch()
        {
            if (_searchMatches.Count == 0) return;
            _searchMatchIndex = _searchMatchIndex <= 1 ? _searchMatches.Count : _searchMatchIndex - 1;
            RefreshSearchHitLabel();
            RevealSearchMatch(true);
        }

        private void NextSearchMatch()
        {
            if (_searchMatches.Count == 0) return;
            _searchMatchIndex = _searchMatchIndex >= _searchMatches.Count ? 1 : _searchMatchIndex + 1;
            RefreshSearchHitLabel();
            RevealSearchMatch(true);
        }

        private const int FileListBatchSize = 256;
        private readonly object _physicalFileScanGate = new object();
        private readonly List<string> _physicalFileCache = new List<string>();
        private readonly HashSet<string> _displayedPhysicalFiles = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        private CancellationTokenSource _physicalFileScanCts;
        private Task _physicalFileScanTask;
        private string _physicalFileScanRoot;
        private bool _physicalFileScanComplete;

        internal void ResetPhysicalFileScan()
        {
            CancellationTokenSource cts;
            lock (_physicalFileScanGate)
            {
                cts = _physicalFileScanCts;
                _physicalFileScanCts = null;
                _physicalFileScanTask = null;
                _physicalFileScanRoot = null;
                _physicalFileScanComplete = false;
                _physicalFileCache.Clear();
            }
            cts?.Cancel();
            cts?.Dispose();
            _displayedPhysicalFiles.Clear();
        }

        private void EnsurePhysicalFileScan()
        {
            string root = RootPath?.Trim();
            if (string.IsNullOrWhiteSpace(root) || !Directory.Exists(root)) return;

            lock (_physicalFileScanGate)
            {
                if (_physicalFileScanTask != null
                    && string.Equals(_physicalFileScanRoot, root, StringComparison.OrdinalIgnoreCase))
                    return;
            }

            ResetPhysicalFileScan();
            var cts = new CancellationTokenSource();
            lock (_physicalFileScanGate)
            {
                _physicalFileScanRoot = root;
                _physicalFileScanCts = cts;
                _physicalFileScanTask = Task.Run(() => ScanPhysicalFiles(root, cts.Token), cts.Token);
            }
        }

        private void ScanPhysicalFiles(string root, CancellationToken token)
        {
            var pending = new Stack<string>();
            var batch = new List<string>(FileListBatchSize);
            pending.Push(root);

            try
            {
                while (pending.Count > 0)
                {
                    token.ThrowIfCancellationRequested();
                    string directory = pending.Pop();
                    try
                    {
                        foreach (VolumePathIndex.NativeDirectoryEntry entry in VolumePathIndex.EnumerateNativeDirectory(directory, token))
                        {
                            token.ThrowIfCancellationRequested();
                            string path = Path.Combine(directory, entry.Name);
                            if ((entry.Attributes & FileAttributes.Directory) != 0)
                            {
                                if ((entry.Attributes & FileAttributes.ReparsePoint) != 0 || IsDroppedTreeFolder(entry.Name))
                                    continue;
                                pending.Push(path);
                                continue;
                            }

                            batch.Add(path);
                            if (batch.Count >= FileListBatchSize)
                                PublishPhysicalFileBatch(batch, token);
                        }
                    }
                    catch (UnauthorizedAccessException) { }
                    catch (IOException) { }
                }

                if (batch.Count > 0)
                    PublishPhysicalFileBatch(batch, token);

                lock (_physicalFileScanGate)
                    _physicalFileScanComplete = true;

                Dispatcher.BeginInvoke(new Action(() =>
                {
                    if (_treeView == 0 && string.Equals(
                            NormalizeComparePath(FilePanelPath),
                            NormalizeComparePath(root),
                            StringComparison.OrdinalIgnoreCase))
                        SetFilePanelBusy(false);
                }), System.Windows.Threading.DispatcherPriority.Background);
            }
            catch (OperationCanceledException) { }
        }

        private void PublishPhysicalFileBatch(List<string> batch, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            string[] published = batch.ToArray();
            batch.Clear();

            lock (_physicalFileScanGate)
                _physicalFileCache.AddRange(published);

            Dispatcher.BeginInvoke(new Action(() =>
            {
                if (token.IsCancellationRequested || _treeView != 0 || string.IsNullOrEmpty(FilePanelPath)) return;

                // Streaming root batches belong only to the root view. Subfolder views
                // are either temporary direct scans (while root acquisition is running)
                // or snapshots from the completed root cache. Never mix the two sources.
                string scanRoot;
                lock (_physicalFileScanGate)
                    scanRoot = _physicalFileScanRoot;

                if (!string.Equals(
                        NormalizeComparePath(FilePanelPath),
                        NormalizeComparePath(scanRoot),
                        StringComparison.OrdinalIgnoreCase))
                    return;

                AppendPhysicalFiles(published, FilePanelPath);
            }), System.Windows.Threading.DispatcherPriority.Background);
        }

        private void AppendPhysicalFiles(IEnumerable<string> paths, string selectedFolder)
        {
            if (string.IsNullOrEmpty(selectedFolder)) return;
            string[] searchKeys = GetFileSearchKeys();
            foreach (string path in paths)
            {
                if (!IsPathUnder(path, selectedFolder) || !_displayedPhysicalFiles.Add(path)) continue;
                _files.Add(new FileListItem(path, searchKeys, selectedFolder));
            }
            FileListCountLabel = string.Format(Strings.Main_NItems, _files.Count);
        }

        private static bool IsPathUnder(string path, string folder)
        {
            if (string.IsNullOrWhiteSpace(path) || string.IsNullOrWhiteSpace(folder)) return false;
            string normalizedPath = NormalizeComparePath(path);
            string normalizedFolder = NormalizeComparePath(folder);
            if (normalizedPath == null || normalizedFolder == null) return false;
            if (string.Equals(normalizedPath, normalizedFolder, StringComparison.OrdinalIgnoreCase)) return true;
            string prefix = normalizedFolder.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
                + Path.DirectorySeparatorChar;
            return normalizedPath.StartsWith(prefix, StringComparison.OrdinalIgnoreCase);
        }

        private string[] GetFileSearchKeys() => (Query ?? string.Empty)
            .Split((char[])null, StringSplitOptions.RemoveEmptyEntries)
            .Select(key => key.Trim().TrimStart('*'))
            .Where(key => key.Length > 0)
            .Distinct(StringComparer.CurrentCultureIgnoreCase)
            .ToArray();

        internal async Task LoadFilesAsync(FolderMatch folder)
        {
            _fileListCts?.Cancel();
            var fileListCts = new CancellationTokenSource();
            _fileListCts = fileListCts;
            _files.Clear();
            _displayedPhysicalFiles.Clear();
            FileListCountLabel = string.Format(Strings.Main_NItems, 0);
            SyncSearchMatches(null, null);

            if (folder != null)
            {
                if (_treeView == 0) _physicalCurrent = folder;
                else if (_treeView == 1) _solutionCurrent = folder;
                else _searchCurrent = folder;
            }

            FilePanelTitle = folder == null ? Strings.Common_Files : string.Format(Strings.Main_FilesHeader, folder.Name);
            FilePanelPath = folder?.Path;
            if (folder == null || string.IsNullOrEmpty(folder.Path))
            {
                SetFilePanelBusy(false);
                return;
            }

            SetFilePanelBusy(true);
            await System.Windows.Threading.Dispatcher.Yield(System.Windows.Threading.DispatcherPriority.Render);
            if (fileListCts.IsCancellationRequested || !ReferenceEquals(_fileListCts, fileListCts)) return;

            string[] searchKeys = GetFileSearchKeys();
            VolumePathIndex index = _pathIndex;
            int treeView = _treeView;
            string folderPath = folder.Path;

            try
            {
                if (treeView == 0)
                {
                    // A completed VolumePathIndex is already the root acquisition result.
                    // Use it directly; otherwise (notably lazy NAS trees) keep one root scan
                    // alive independently of folder selection and consume its growing cache.
                    if (index != null)
                    {
                        string[] paths = await Task.Run(
                            () => CollectFilesUnder(index, folderPath, fileListCts.Token), fileListCts.Token);
                        foreach (string[] chunk in paths.Chunk(FileListBatchSize))
                        {
                            if (fileListCts.IsCancellationRequested || !ReferenceEquals(_fileListCts, fileListCts)) return;
                            AppendPhysicalFiles(chunk, folderPath);
                            await System.Windows.Threading.Dispatcher.Yield(System.Windows.Threading.DispatcherPriority.Background);
                        }
                        SetFilePanelBusy(false);
                        return;
                    }

                    EnsurePhysicalFileScan();

                    string scanRoot;
                    string[] cached;
                    bool complete;
                    lock (_physicalFileScanGate)
                    {
                        scanRoot = _physicalFileScanRoot;
                        complete = _physicalFileScanComplete;
                        cached = _physicalFileCache.Where(path => IsPathUnder(path, folderPath)).ToArray();
                    }

                    bool selectedRoot = string.Equals(
                        NormalizeComparePath(folderPath),
                        NormalizeComparePath(scanRoot),
                        StringComparison.OrdinalIgnoreCase);

                    if (!complete && !selectedRoot)
                    {
                        // The root acquisition is authoritative and continues independently.
                        // Until it completes, a subfolder selection gets a disposable direct
                        // scan for responsiveness. Its results are display-only: they are not
                        // merged into _physicalFileCache and do not affect root progress/count.
                        string[] temporary = await Task.Run(
                            () => CollectFilesOnDisk(folderPath, true, fileListCts.Token),
                            fileListCts.Token);

                        foreach (string[] chunk in temporary.Chunk(FileListBatchSize))
                        {
                            if (fileListCts.IsCancellationRequested || !ReferenceEquals(_fileListCts, fileListCts)) return;
                            AppendPhysicalFiles(chunk, folderPath);
                            await System.Windows.Threading.Dispatcher.Yield(System.Windows.Threading.DispatcherPriority.Background);
                        }

                        SetFilePanelBusy(false);
                        return;
                    }

                    string selectedKey = NormalizeComparePath(folderPath);
                    cached = cached
                        .OrderBy(path => string.Equals(NormalizeComparePath(Path.GetDirectoryName(path)), selectedKey, StringComparison.OrdinalIgnoreCase) ? 0 : 1)
                        .ThenBy(path => path, StringComparer.CurrentCultureIgnoreCase)
                        .ToArray();

                    foreach (string[] chunk in cached.Chunk(FileListBatchSize))
                    {
                        if (fileListCts.IsCancellationRequested || !ReferenceEquals(_fileListCts, fileListCts)) return;
                        AppendPhysicalFiles(chunk, folderPath);
                        await System.Windows.Threading.Dispatcher.Yield(System.Windows.Threading.DispatcherPriority.Background);
                    }

                    if (complete) SetFilePanelBusy(false);
                    return;
                }

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

                List<FileListItem> loaded = await Task.Run(() =>
                {
                    string[] paths = treeView == 2
                        ? CollectSearchFiles(searchFolderPaths, folderPath, fileListCts.Token)
                        : CollectImmediateFiles(folderPath, fileListCts.Token);
                    var items = new List<FileListItem>(paths.Length);
                    foreach (string path in paths)
                    {
                        fileListCts.Token.ThrowIfCancellationRequested();
                        items.Add(new FileListItem(path, searchKeys, folderPath));
                    }
                    return items;
                }, fileListCts.Token);

                if (fileListCts.IsCancellationRequested || !ReferenceEquals(_fileListCts, fileListCts)) return;
                foreach (FileListItem item in loaded)
                {
                    _files.Add(item);
                    if ((_files.Count % FileListBatchSize) == 0)
                        await System.Windows.Threading.Dispatcher.Yield(System.Windows.Threading.DispatcherPriority.Background);
                }

                FileListCountLabel = string.Format(Strings.Main_NItems, loaded.Count);
                FileListItem preferred = loaded.FirstOrDefault(item => item.IsSearchMatch && item.IsDirectChild)
                    ?? loaded.FirstOrDefault(item => item.IsSearchMatch);
                SyncSearchMatches(loaded, preferred, folder: folder);
            }
            catch (OperationCanceledException) { }
            finally
            {
                if (treeView != 0 && ReferenceEquals(_fileListCts, fileListCts) && !fileListCts.IsCancellationRequested)
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

        private static string[] OrderSelectedFolderFirst(List<string> paths, string selectedFolder)
        {
            if (paths == null || paths.Count == 0)
                return Array.Empty<string>();

            string selectedKey = NormalizeComparePath(selectedFolder);
            var selected = new List<string>();
            var nested = new List<string>();
            foreach (string path in paths)
            {
                string parent = NormalizeComparePath(Path.GetDirectoryName(path));
                if (string.Equals(parent, selectedKey, StringComparison.OrdinalIgnoreCase))
                    selected.Add(path);
                else
                    nested.Add(path);
            }

            selected.Sort(StringComparer.CurrentCultureIgnoreCase);
            nested.Sort(StringComparer.CurrentCultureIgnoreCase);
            selected.AddRange(nested);
            return selected.ToArray();
        }

        internal static string[] CollectFilesUnder(VolumePathIndex index, string folderPath, CancellationToken token)
        {
            if (index == null || string.IsNullOrEmpty(folderPath))
                return Array.Empty<string>();

            VolumePathNode root = index.Find(folderPath);
            if (root == null || !root.IsDirectory)
            {
                if (Directory.Exists(folderPath))
                    return CollectFilesOnDisk(folderPath, true, token);
                return Array.Empty<string>();
            }

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
                }
                for (int i = node.Directories.Count - 1; i >= 0; i--)
                {
                    VolumePathNode child = node.Directories[i];
                    if (IsDroppedTreeFolder(child.Name))
                        continue;
                    stack.Push(child);
                }
            }
            return OrderSelectedFolderFirst(paths, folderPath);
        }

        internal static string[] CollectFilesOnDisk(string folderPath, bool recursive, CancellationToken token)
        {
            if (string.IsNullOrWhiteSpace(folderPath) || !Directory.Exists(folderPath))
                return Array.Empty<string>();

            var paths = new List<string>();
            var pending = new Stack<string>();
            pending.Push(folderPath);
            while (pending.Count > 0)
            {
                token.ThrowIfCancellationRequested();
                string directory = pending.Pop();
                try
                {
                    foreach (string file in Directory.EnumerateFiles(directory))
                    {
                        token.ThrowIfCancellationRequested();
                        if (Directory.Exists(file)) continue;
                        paths.Add(file);
                        }
                    if (!recursive) continue;
                    foreach (string child in Directory.EnumerateDirectories(directory))
                    {
                        token.ThrowIfCancellationRequested();
                        if (IsDroppedTreeFolder(Path.GetFileName(child))) continue;
                        pending.Push(child);
                    }
                }
                catch (UnauthorizedAccessException) { }
                catch (IOException) { }
            }
            if (recursive)
                return OrderSelectedFolderFirst(paths, folderPath);
            paths.Sort(StringComparer.CurrentCultureIgnoreCase);
            return paths.ToArray();
        }

        internal static string[] CollectSearchFiles(List<string> folderPaths, string selectedFolder, CancellationToken token)
        {
            if (folderPaths == null || folderPaths.Count == 0)
                return Array.Empty<string>();

            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var selected = new List<string>();
            var nested = new List<string>();
            string selectedKey = NormalizeComparePath(selectedFolder);
            foreach (string folderPath in folderPaths)
            {
                token.ThrowIfCancellationRequested();
                bool direct = string.Equals(NormalizeComparePath(folderPath), selectedKey, StringComparison.OrdinalIgnoreCase);
                foreach (string file in CollectFilesOnDisk(folderPath, false, token))
                {
                    if (!seen.Add(file)) continue;
                    if (direct) selected.Add(file);
                    else nested.Add(file);
                }
            }

            selected.Sort(StringComparer.CurrentCultureIgnoreCase);
            nested.Sort(StringComparer.CurrentCultureIgnoreCase);
            selected.AddRange(nested);
            return selected.ToArray();
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
            return path;
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
            IReadOnlyList<string> scopes = PrepareGrepScopes();
            if (scopes.Count == 0)
            {
                _dialogs.Show(Strings.Main_NoGrepFolders, Strings.App_Title, MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            OpenGrepRequested?.Invoke(scopes);
        }

        internal IReadOnlyList<string> CollectSelectedFolderPaths(bool requireExists = true)
        {
            // Walk the live tree, not the folder-filter flat list.
            // Same-branch children are dropped only when a selected ancestor already covers them.
            List<string> selected = new List<string>();
            foreach (FolderMatch item in Flatten(CurrentTreeRoots()))
            {
                if (!item.IsActionable || !item.IsSelected || string.IsNullOrWhiteSpace(item.Path))
                    continue;
                string path = TryNormalizeFolderPath(item.Path);
                if (path == null) continue;
                if (requireExists && !Directory.Exists(path)) continue;
                selected.Add(path);
            }

            return ExcludeNestedFolders(selected.Distinct(StringComparer.OrdinalIgnoreCase).ToList());
        }

        internal IReadOnlyList<string> GetSelectedGrepScopes()
        {
            return PrepareGrepScopes();
        }

        internal IReadOnlyList<string> ResolveSearchScopes(string fallbackRoot = null)
        {
            return EnsureScopedFolders(fallbackRoot ?? RootPath);
        }

        internal IReadOnlyList<string> PrepareGrepScopes()
        {
            return EnsureScopedFolders(RootPath);
        }

        private IReadOnlyList<string> EnsureScopedFolders(string fallbackRoot)
        {
            IReadOnlyList<string> selected = CollectSelectedFolderPaths(true);
            if (selected.Count > 0) return selected;

            FolderMatch root = CurrentTreeRoots().FirstOrDefault(item =>
                item.IsActionable && ExistingFolderPath(item.Path) != null);
            if (root != null)
            {
                root.SetSelected(true);
                SyncAllFoldersSelectedFlag();
                return CollectSelectedFolderPaths(true);
            }

            string path = ExistingFolderPath(fallbackRoot);
            return path == null ? Array.Empty<string>() : new[] { path };
        }

        internal static string ExistingFolderPath(string path)
        {
            string normalized = TryNormalizeFolderPath(path);
            if (normalized == null) return null;
            return Directory.Exists(normalized) ? normalized : null;
        }

        internal void RefreshScopedLabel()
        {
            int count = CollectSelectedFolderPaths(false).Count;
            string label = "Scoped " + count;
            if (!string.Equals(_scopedLabel, label, StringComparison.Ordinal))
                ScopedLabel = label;
        }

        internal static string NormalizeFolderPath(string path)
        {
            string fullPath = Path.GetFullPath(path);
            string root = Path.GetPathRoot(fullPath);
            return fullPath.Length == root.Length ? fullPath
                : fullPath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        }

        internal static string TryNormalizeFolderPath(string path)
        {
            if (string.IsNullOrWhiteSpace(path)) return null;
            try
            {
                return NormalizeFolderPath(path);
            }
            catch (ArgumentException) { return path.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar); }
            catch (NotSupportedException) { return path.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar); }
            catch (PathTooLongException) { return path.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar); }
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
