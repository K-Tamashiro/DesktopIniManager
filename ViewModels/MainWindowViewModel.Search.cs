using DesktopIniManager.Models;
using DesktopIniManager.Services;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Media;
using System.Windows.Threading;
using FastVolumeIndex;
using DesktopIniManager.Properties;

namespace DesktopIniManager.ViewModels
{
    internal sealed partial class MainWindowViewModel
    {
        private int _statusEpoch;

        internal event Action SearchModeRequested;

        internal void ClearSearchSession()
        {
            _searchCts?.Cancel();
            _searchRoots.Clear();
            _searchResultCount = 0;
            _searchCurrent = null;
            _files.Clear();
            ClearSearchHits();
            FileListCountLabel = string.Format(Strings.Main_NItems, 0);
            FilePanelTitle = Strings.Common_Files;
            FilePanelPath = null;
            RefreshTreeItemsSource();
        }

        internal Task PrepareTreesAtStartupAsync()
        {
            if (string.IsNullOrWhiteSpace(RootPath) || !Directory.Exists(RootPath.Trim()))
                return Task.CompletedTask;

            string root = RootPath.Trim();
            if (IsNetworkPath(root))
                return PrepareLazyNetworkTreeAtStartupAsync(root);

            _pendingSearchQuery = ".git";
            return SearchAsync(quietMissingRoot: true);
        }

        private async Task PrepareLazyNetworkTreeAtStartupAsync(string root)
        {
            _searchCts?.Cancel();
            var searchCts = new CancellationTokenSource();
            _searchCts = searchCts;
            SaveFolderTrees();
            InvalidateSolutionAnalysis();
            _rebuildingFolderTrees = true;
            _results.Clear();
            _treeRoots.Clear();
            _solutionRoots.Clear();
            _searchRoots.Clear();
            _searchResultCount = 0;
            ShowTreeView(0);
            SetSearching(true);
            try
            {
                await BuildLazyNetworkTreeAsync(root, searchCts.Token, intoSearch: false);
                searchCts.Token.ThrowIfCancellationRequested();
                _pathIndex = null;
                _folderTreeRoot = root;
                _physicalCurrent = _solutionCurrent = null;
                _rebuildingFolderTrees = false;
                ShowTreeView(0);
                _ = StartSolutionAnalysis(root, null);
            }
            catch (OperationCanceledException)
            {
                Status = Strings.Main_SearchCancelled;
            }
            catch (Exception ex)
            {
                _dialogs.Show(ErrorMessages.English(ex), Strings.App_Title);
                Status = Strings.Main_SearchFailed;
            }
            finally
            {
                _rebuildingFolderTrees = false;
                if (ReferenceEquals(_searchCts, searchCts))
                {
                    _searchCts = null;
                    SetSearching(false);
                }
                searchCts.Dispose();
            }
        }

        internal Task SearchAsync() => SearchAsync(false);

        internal void CancelSearch()
        {
            _searchCts?.Cancel();
            if (_treeView == 1) _solutionCts?.Cancel();
        }

        internal async Task SearchAsync(bool quietMissingRoot)
        {
            // Snapshot bindable state before starting background work.
            int statusEpoch = ++_statusEpoch;
            SearchHistoryRequested?.Invoke();
            string root = RootPath.Trim();
            string visibleQuery = Query.Trim();
            string query = _pendingSearchQuery ?? visibleQuery;
            bool folderListMode = string.IsNullOrWhiteSpace(query);
            bool gitSearchRequested = _pendingSearchQuery != null && string.Equals(query, ".git", StringComparison.OrdinalIgnoreCase);
            // Searches own the Search tab. Physical and Solution
            // are populated exclusively by the Git acquisition workflow.
            bool searchOnly = !gitSearchRequested;
            _pendingSearchQuery = null;
            SettingsService.SaveSearchRoot(root);
            SettingsService.SaveSearchQuery(visibleQuery);
            if (!Directory.Exists(root))
            {
                if (!quietMissingRoot)
                    _dialogs.Show(Strings.Main_LocationMissing, Strings.App_Title);
                return;
            }

            if (searchOnly)
                SearchModeRequested?.Invoke();

            // NAS Git analysis must use the same lazy tree path as startup.
            // Do not fall through to the recursive full VolumePathIndex scan.
            if (gitSearchRequested && IsNetworkPath(root))
            {
                await PrepareLazyNetworkTreeAtStartupAsync(root);
                return;
            }

            _searchCts?.Cancel();
            var searchCts = new CancellationTokenSource();
            _searchCts = searchCts;
            if (searchOnly)
            {
                _searchRoots.Clear();
                _searchResultCount = 0;
            }
            else
            {
                SaveFolderTrees();
                InvalidateSolutionAnalysis();
                _rebuildingFolderTrees = true;
                _results.Clear();
                _treeRoots.Clear();
                _solutionRoots.Clear();
                _searchRoots.Clear();
                _searchResultCount = 0;
            }
            if (gitSearchRequested) ShowTreeView(0);
            CountLabel = Strings.Main_ZeroMatches; SetSearching(true);
            try
            {
                if (folderListMode)
                {
                    if (IsNetworkPath(root))
                    {
                        await BuildLazyNetworkTreeAsync(root, searchCts.Token, searchOnly);
                        _pathIndex = null;
                    }
                    else
                    {
                        StandardSearchResult standard = await RunStandardIndexedSearch(root, string.Empty, searchCts.Token);
                        await AddTreeResultsAsync(standard.Matches, searchCts.Token, searchOnly);
                        _pathIndex = standard.Paths;
                        RefreshTreeItemsSource();
                    }
                }
                else
                {
                    StandardSearchResult standard = await RunStandardIndexedSearch(root, query, searchCts.Token);
                    await AddTreeResultsAsync(standard.Matches, searchCts.Token, searchOnly);
                    _pathIndex = standard.Paths;
                    RefreshTreeItemsSource();
                    await ApplyStandardDevelopmentAnalysis(gitSearchRequested, standard.Paths, searchCts.Token);
                }
                if (!searchOnly)
                {
                    searchCts.Token.ThrowIfCancellationRequested();
                    _folderTreeRoot = root;
                    _physicalCurrent = _solutionCurrent = null;
                    _rebuildingFolderTrees = false;
                }
                if (searchOnly)
                {
                    ShowTreeView(2);
                    SearchRootSelectionRequested?.Invoke();
                }
                else
                {
                    ShowTreeView(0);
                    _ = StartSolutionAnalysis(root, _pathIndex);
                }
            }
            catch (OperationCanceledException) { Status = Strings.Main_SearchCancelled; }
            catch (Exception ex) { _dialogs.Show(ErrorMessages.English(ex), Strings.App_Title); Status = Strings.Main_SearchFailed; }
            finally
            {
                _statusEpoch++;
                if (ReferenceEquals(_searchCts, searchCts)) { _searchCts = null; SetSearching(false); }
                searchCts.Dispose();
            }
        }

        internal bool IsLazyNetworkTreeActive
        {
            get
            {
                IEnumerable<FolderMatch> roots = _treeView == 0
                    ? _treeRoots
                    : _treeView == 2 ? _searchRoots : Enumerable.Empty<FolderMatch>();
                return roots.Any(item => item.IsLazyLoaded || item.Children.Any(child => child.IsLazyPlaceholder));
            }
        }

        internal async Task LoadLazyNetworkChildrenAsync(FolderMatch folder)
        {
            if (folder == null || folder.IsLazyPlaceholder || folder.IsLazyLoaded || folder.IsLazyLoading)
                return;

            folder.IsLazyLoading = true;
            try
            {
                CancellationToken token = _searchCts?.Token ?? CancellationToken.None;
                List<FolderMatch> children = await Task.Run(
                    () => EnumerateLazyFolders(folder.Path, folder, token), token);

                folder.Children.Clear();
                foreach (FolderMatch child in children)
                    folder.Children.Add(child);
                folder.IsLazyLoaded = true;

                if (_treeView == 0)
                {
                    foreach (FolderMatch child in children)
                        if (!_results.Contains(child))
                            _results.Add(child);
                    CountLabel = string.Format(Strings.Main_NFolders, _results.Count);
                }
                else if (_treeView == 2)
                {
                    _searchResultCount += children.Count;
                    CountLabel = string.Format(Strings.Main_NFolders, _searchResultCount);
                }
            }
            finally
            {
                folder.IsLazyLoading = false;
            }
        }

        private async Task BuildLazyNetworkTreeAsync(string root, CancellationToken token, bool intoSearch)
        {
            ImageSource defaultFolderIcon = FolderIconService.GetDefaultFolderIcon();
            var rootNode = new FolderMatch
            {
                Path = root,
                DisplayName = GetLazyRootName(root),
                Reason = "Folder",
                IconPreview = defaultFolderIcon,
                IsExpanded = true
            };

            List<FolderMatch> children = await Task.Run(
                () => EnumerateLazyFolders(root, rootNode, token, defaultFolderIcon), token);

            foreach (FolderMatch child in children)
                rootNode.Children.Add(child);
            rootNode.IsLazyLoaded = true;

            if (intoSearch)
            {
                _searchRoots.Clear();
                _searchRoots.Add(rootNode);
                _searchResultCount = 1 + children.Count;
                RefreshTreeItemsSource();
                CountLabel = string.Format(Strings.Main_NFolders, _searchResultCount);
            }
            else
            {
                _results.Clear();
                _results.Add(rootNode);
                foreach (FolderMatch child in children)
                    _results.Add(child);
                _treeRoots.Clear();
                _treeRoots.Add(rootNode);
                RefreshTreeItemsSource();
                CountLabel = string.Format(Strings.Main_NFolders, _results.Count);
            }
        }

        private static List<FolderMatch> EnumerateLazyFolders(
            string folder,
            FolderMatch parent,
            CancellationToken token,
            ImageSource defaultFolderIcon = null)
        {
            defaultFolderIcon ??= FolderIconService.GetDefaultFolderIcon();
            var result = new List<FolderMatch>();

            foreach (VolumePathIndex.NativeDirectoryEntry entry in VolumePathIndex.EnumerateNativeDirectory(folder, token))
            {
                token.ThrowIfCancellationRequested();
                if ((entry.Attributes & FileAttributes.Directory) == 0)
                    continue;
                if ((entry.Attributes & FileAttributes.Hidden) != 0)
                    continue;
                if (IsDroppedTreeFolder(entry.Name))
                    continue;

                string path = Path.Combine(folder, entry.Name);
                var child = new FolderMatch
                {
                    Path = path,
                    DisplayName = entry.Name,
                    Reason = "Folder",
                    Parent = parent,
                    IconPreview = defaultFolderIcon,
                    IsExpanded = false
                };

                // The placeholder gives WPF an expander without touching the child directory.
                if ((entry.Attributes & FileAttributes.ReparsePoint) == 0)
                {
                    child.Children.Add(new FolderMatch
                    {
                        Parent = child,
                        Path = path,
                        DisplayName = string.Empty,
                        IsActionable = false,
                        IsHidden = true,
                        IsLazyPlaceholder = true,
                        IsLazyLoaded = true
                    });
                }
                else
                {
                    child.IsLazyLoaded = true;
                }

                result.Add(child);
            }

            result.Sort((left, right) =>
            {
                int name = StringComparer.CurrentCultureIgnoreCase.Compare(left.Name, right.Name);
                return name != 0 ? name : StringComparer.CurrentCultureIgnoreCase.Compare(left.Path, right.Path);
            });
            return result;
        }

        private static string GetLazyRootName(string root)
        {
            string normalized = root.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            string name = Path.GetFileName(normalized);
            return string.IsNullOrEmpty(name) ? root : name;
        }

        private static bool IsNetworkPath(string path)
        {
            if (string.IsNullOrWhiteSpace(path))
                return false;
            if (path.StartsWith(@"\\", StringComparison.Ordinal))
                return true;
            try
            {
                string root = Path.GetPathRoot(Path.GetFullPath(path));
                return !string.IsNullOrEmpty(root) && new DriveInfo(root).DriveType == DriveType.Network;
            }
            catch
            {
                return false;
            }
        }

        private Task<StandardSearchResult> RunStandardIndexedSearch(string root, string query, CancellationToken token)
        {
            ImageSource defaultFolderIcon = FolderIconService.GetDefaultFolderIcon();
            return Task.Run(() =>
            {
                int lastReport = Environment.TickCount;
                VolumePathIndex paths = VolumePathIndex.BuildFromNativeEnumeration(root,
                    count =>
                    {
                        int now = Environment.TickCount;
                        if (unchecked(now - lastReport) < 125) return;
                        lastReport = now;
                        Dispatcher.BeginInvoke(new Action(() =>
                        {
                            if (!IsSearching) return;
                            Status = string.Format(Strings.Main_IndexedFoldersEllipsis, count.ToString("N0"));
                        }), System.Windows.Threading.DispatcherPriority.Background);
                    }, token);
                token.ThrowIfCancellationRequested();
                Dispatcher.BeginInvoke(new Action(() =>
                {
                    if (!IsSearching) return;
                    Status = Strings.Main_BuildingTree;
                }));
                List<FolderMatch> matches = new FastFolderSearchService().Search(paths, query, token);
                foreach (FolderMatch item in matches) item.IconPreview = defaultFolderIcon;
                return new StandardSearchResult(paths, matches);
            }, token);
        }

        internal async Task ApplyStandardDevelopmentAnalysis(bool enabled, VolumePathIndex paths, CancellationToken token)
        {
            if (!enabled) return;
            Status = string.Format(Strings.Main_FoldersAnalyzing, _results.Count);
            await System.Windows.Threading.Dispatcher.Yield(System.Windows.Threading.DispatcherPriority.Background);
            Dictionary<string, string> analysis = await Task.Run(() => new FastFolderSearchService().AnalyzeDevelopment(paths, token), token);
            int updated = 0;
            foreach (FolderMatch item in _results)
            {
                token.ThrowIfCancellationRequested();
                string reason;
                if (analysis.TryGetValue(VolumePathIndex.Normalize(item.Path), out reason)) item.Reason = reason;
                if ((++updated % 60) == 0)
                {
                    await System.Windows.Threading.Dispatcher.Yield(System.Windows.Threading.DispatcherPriority.Background);
                }
            }
        }

        // These fields belong to the physical acquisition, not ordinary searches.
        private CancellationTokenSource _solutionCts;
        private Task _solutionTask;
        private VolumePathIndex _solutionPathIndex;
        private string _solutionAnalysisRoot;
        private Exception _solutionError;
        private bool _solutionReady;
        private int _solutionGeneration;
        private int _treeViewGeneration;

        private void InvalidateSolutionAnalysis()
        {
            _solutionGeneration++;
            _solutionCts?.Cancel();
            _solutionCts = null;
            _solutionTask = null;
            _solutionPathIndex = null;
            _solutionAnalysisRoot = null;
            _folderTreeRoot = null;
            _solutionReady = false;
            _solutionError = null;
            _solutionRoots.Clear();
            _solutionCurrent = null;
            if (_treeView == 1)
            {
                _filterCts?.Cancel();
                _filteredViewItems = null;
                RefreshTreeItemsSource();
                UpdateVisibleCount();
            }
            SetTreePanelBusy(false);
        }

        private Task StartSolutionAnalysis(string root, VolumePathIndex paths)
        {
            if (_solutionTask != null) return _solutionTask;
            _solutionAnalysisRoot = root;
            _solutionPathIndex = paths;
            var cts = new CancellationTokenSource();
            _solutionCts = cts;
            _solutionTask = AnalyzeSolutionsAsync(root, paths, cts, _solutionGeneration);
            return _solutionTask;
        }

        private async Task AnalyzeSolutionsAsync(string root, VolumePathIndex paths,
            CancellationTokenSource cts, int generation)
        {
            try
            {
                // Let physical rendering and the foreground command finish first.
                await System.Windows.Threading.Dispatcher.Yield(DispatcherPriority.Background);
                CancellationToken token = cts.Token;
                List<FolderMatch> roots = await Task.Run(() =>
                {
                    List<string> solutions = (paths?.ProjectFiles ?? (IReadOnlyList<string>)Array.Empty<string>())
                        .Where(path => !string.IsNullOrWhiteSpace(path)
                            && string.Equals(Path.GetExtension(path), ".sln", StringComparison.OrdinalIgnoreCase))
                        .Distinct(StringComparer.OrdinalIgnoreCase).ToList();
                    var built = new List<FolderMatch>();
                    if (solutions.Count == 0)
                        built = SolutionTreeService.Build(root, token);
                    else
                    {
                        int lastReport = Environment.TickCount;
                        foreach (string path in solutions)
                        {
                            token.ThrowIfCancellationRequested();
                            built.AddRange(SolutionTreeService.BuildFromProjectFiles(new[] { path }, paths, token));
                            int now = Environment.TickCount;
                            if (unchecked(now - lastReport) < 120) continue;
                            lastReport = now;
                            int count = built.Count;
                            string name = Path.GetFileName(path);
                            Dispatcher.BeginInvoke(new Action(() =>
                            {
                                if (generation != _solutionGeneration || token.IsCancellationRequested
                                    || _solutionReady || _treeView != 1 || !IsTreeBusy) return;
                                CountLabel = string.Format(Strings.Main_NItems, count);
                                Status = string.Format(Strings.Main_SolutionsFound, count)
                                    + " / " + solutions.Count.ToString("N0") + "  " + name;
                            }), DispatcherPriority.Background);
                        }
                    }
                    foreach (FolderMatch solution in built) AssignParents(solution);
                    return built;
                }, token);
                token.ThrowIfCancellationRequested();
                if (generation != _solutionGeneration) return;
                foreach (FolderMatch solution in roots) _solutionRoots.Add(solution);
                ApplySolutionRootIcons(_solutionRoots);
                _solutionReady = true;
            }
            catch (OperationCanceledException) { }
            catch (Exception ex)
            {
                // Observe background failures; report them when this view is requested.
                if (generation == _solutionGeneration) _solutionError = ex;
            }
            finally
            {
                if (ReferenceEquals(_solutionCts, cts)) _solutionCts = null;
                cts.Dispose();
            }
        }

        private async Task EnsureSolutionViewAsync()
        {
            if (_solutionReady || _rebuildingFolderTrees || _folderTreeRoot == null) return;
            int generation = _solutionGeneration;
            int viewGeneration = _treeViewGeneration;
            Task task = StartSolutionAnalysis(_solutionAnalysisRoot ?? _folderTreeRoot, _solutionPathIndex);
            SetTreePanelBusy(true);
            Status = Strings.Main_BuildingTree;
            try
            {
                await task;
                if (generation != _solutionGeneration || viewGeneration != _treeViewGeneration || _treeView != 1) return;
                if (_solutionReady) ShowTreeView(1);
                else if (_solutionError != null)
                    ShowError(Strings.Main_SearchFailed, _solutionError);
                else Status = Strings.Main_SearchCancelled;
            }
            finally
            {
                if (generation == _solutionGeneration && viewGeneration == _treeViewGeneration && _treeView == 1)
                    SetTreePanelBusy(false);
            }
        }

    }
}
