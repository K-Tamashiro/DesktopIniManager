using DesktopIniManager.Models;
using DesktopIniManager.Properties;
using DesktopIniManager.Services;
using FastVolumeIndex;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Media;
using System.Windows.Threading;

namespace DesktopIniManager.ViewModels;

internal sealed partial class MainWindowViewModel
{
    private string _solutionAnalysisRoot;

    // These fields belong to the physical acquisition, not ordinary searches.
    private CancellationTokenSource _solutionCts;

    private Exception _solutionError;
    private int _solutionGeneration;
    private VolumePathIndex _solutionPathIndex;
    private bool _solutionReady;
    private Task _solutionTask;
    private int _statusEpoch;
    private int _treeViewGeneration;

    internal bool IsLazyNetworkTreeActive
    {
        get
        {
            var roots = TreeViewIndex == 0
                ? _treeRoots
                : TreeViewIndex == 2
                    ? _searchRoots
                    : Enumerable.Empty<FolderMatch>();
            return roots.Any(item => item.IsLazyLoaded || item.Children.Any(child => child.IsLazyPlaceholder));
        }
    }

    internal event Action SearchModeRequested;

    internal void ClearSearchSession()
    {
        _searchCts?.Cancel();
        _searchRoots.Clear();
        _searchResultCount = 0;
        _searchCurrent = null;
        Files.Clear();
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

        var root = RootPath.Trim();
        if (IsNetworkPath(root))
            return PrepareLazyNetworkTreeAtStartupAsync(root);

        _pendingSearchQuery = ".git";
        return SearchAsync(true);
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
            await BuildLazyNetworkTreeAsync(root, searchCts.Token, false);
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

    internal Task SearchAsync()
    {
        return SearchAsync(false);
    }

    internal void CancelSearch()
    {
        _searchCts?.Cancel();
        if (TreeViewIndex == 1) _solutionCts?.Cancel();
    }

    internal async Task SearchAsync(bool quietMissingRoot)
    {
        // Snapshot bindable state before starting background work.
        var statusEpoch = ++_statusEpoch;
        SearchHistoryRequested?.Invoke();
        var root = RootPath.Trim();
        var visibleQuery = Query.Trim();
        var query = _pendingSearchQuery ?? visibleQuery;
        var folderListMode = string.IsNullOrWhiteSpace(query);
        var gitSearchRequested = _pendingSearchQuery != null &&
                                 string.Equals(query, ".git", StringComparison.OrdinalIgnoreCase);
        var searchRoots = gitSearchRequested
            ? new[] { root }
            : ResolveSearchScopes(root);
        // Searches own the Search tab. Physical and Solution
        // are populated exclusively by the Git acquisition workflow.
        var searchOnly = !gitSearchRequested;
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
        CountLabel = Strings.Main_ZeroMatches;
        SetSearching(true);
        try
        {
            if (folderListMode)
            {
                if (searchRoots.Count == 1 && IsNetworkPath(searchRoots[0]))
                {
                    await BuildLazyNetworkTreeAsync(searchRoots[0], searchCts.Token, searchOnly);
                    _pathIndex = null;
                }
                else
                {
                    var standard = await RunStandardIndexedSearch(searchRoots, string.Empty, searchCts.Token);
                    await AddTreeResultsAsync(standard.Matches, searchCts.Token, searchOnly);
                    _pathIndex = standard.Paths;
                    RefreshTreeItemsSource();
                }
            }
            else
            {
                var standard = await RunStandardIndexedSearch(searchRoots, query, searchCts.Token);
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
            _statusEpoch++;
            if (ReferenceEquals(_searchCts, searchCts))
            {
                _searchCts = null;
                SetSearching(false);
            }

            searchCts.Dispose();
        }
    }

    internal async Task LoadLazyNetworkChildrenAsync(FolderMatch folder)
    {
        if (folder == null || folder.IsLazyPlaceholder || folder.IsLazyLoaded || folder.IsLazyLoading)
            return;

        folder.IsLazyLoading = true;
        try
        {
            var token = _searchCts?.Token ?? CancellationToken.None;
            var children = await Task.Run(
                () => EnumerateLazyFolders(folder.Path, folder, token), token);

            folder.Children.Clear();
            foreach (var child in children)
                folder.Children.Add(child);
            folder.IsLazyLoaded = true;

            if (TreeViewIndex == 0)
            {
                foreach (var child in children)
                    if (!_results.Contains(child))
                        _results.Add(child);
                CountLabel = string.Format(Strings.Main_NFolders, _results.Count);
            }
            else if (TreeViewIndex == 2)
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

        var children = await Task.Run(
            () => EnumerateLazyFolders(root, rootNode, token, defaultFolderIcon), token);

        foreach (var child in children)
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
            foreach (var child in children)
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

        foreach (var entry in VolumePathIndex.EnumerateNativeDirectory(folder, token))
        {
            token.ThrowIfCancellationRequested();
            if ((entry.Attributes & FileAttributes.Directory) == 0)
                continue;
            if ((entry.Attributes & FileAttributes.Hidden) != 0)
                continue;
            if (IsDroppedTreeFolder(entry.Name))
                continue;

            var path = Path.Combine(folder, entry.Name);
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
            else
                child.IsLazyLoaded = true;

            result.Add(child);
        }

        result.Sort((left, right) =>
        {
            var name = StringComparer.CurrentCultureIgnoreCase.Compare(left.Name, right.Name);
            return name != 0 ? name : StringComparer.CurrentCultureIgnoreCase.Compare(left.Path, right.Path);
        });
        return result;
    }

    private static string GetLazyRootName(string root)
    {
        var normalized = root.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var name = Path.GetFileName(normalized);
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
            var root = Path.GetPathRoot(Path.GetFullPath(path));
            return !string.IsNullOrEmpty(root) && new DriveInfo(root).DriveType == DriveType.Network;
        }
        catch
        {
            return false;
        }
    }

    private Task<StandardSearchResult> RunStandardIndexedSearch(IReadOnlyList<string> roots, string query,
        CancellationToken token)
    {
        ImageSource defaultFolderIcon = FolderIconService.GetDefaultFolderIcon();
        var searchRoots = roots == null || roots.Count == 0
            ? new[] { RootPath.Trim() }
            : roots;
        return Task.Run(() =>
        {
            VolumePathIndex merged = null;
            var matches = new List<FolderMatch>();
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var lastReport = Environment.TickCount;
            foreach (var root in searchRoots)
            {
                token.ThrowIfCancellationRequested();
                if (string.IsNullOrWhiteSpace(root) || !Directory.Exists(root)) continue;
                var paths = VolumePathIndex.BuildFromNativeEnumeration(root,
                    count =>
                    {
                        var now = Environment.TickCount;
                        if (unchecked(now - lastReport) < 125) return;
                        lastReport = now;
                        Dispatcher.BeginInvoke(new Action(() =>
                        {
                            if (!IsSearching) return;
                            Status = string.Format(Strings.Main_IndexedFoldersEllipsis, count.ToString("N0"));
                        }), DispatcherPriority.Background);
                    }, token);
                token.ThrowIfCancellationRequested();
                merged = paths;
                var found = new FastFolderSearchService().Search(paths, query, token);
                foreach (var item in found)
                {
                    if (!seen.Add(item.Path)) continue;
                    item.IconPreview = defaultFolderIcon;
                    matches.Add(item);
                }
            }

            Dispatcher.BeginInvoke(new Action(() =>
            {
                if (!IsSearching) return;
                Status = Strings.Main_BuildingTree;
            }));
            return new StandardSearchResult(merged, matches);
        }, token);
    }

    internal async Task ApplyStandardDevelopmentAnalysis(bool enabled, VolumePathIndex paths, CancellationToken token)
    {
        if (!enabled) return;
        Status = string.Format(Strings.Main_FoldersAnalyzing, _results.Count);
        await Dispatcher.Yield(DispatcherPriority.Background);
        var analysis = await Task.Run(() => new FastFolderSearchService().AnalyzeDevelopment(paths, token), token);
        var updated = 0;
        foreach (var item in _results)
        {
            token.ThrowIfCancellationRequested();
            string reason;
            if (analysis.TryGetValue(VolumePathIndex.Normalize(item.Path), out reason)) item.Reason = reason;
            if (++updated % 60 == 0) await Dispatcher.Yield(DispatcherPriority.Background);
        }
    }

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
        if (TreeViewIndex == 1)
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
            await Dispatcher.Yield(DispatcherPriority.Background);
            var token = cts.Token;
            var roots = await Task.Run(() =>
            {
                var solutions = (paths?.ProjectFiles ?? Array.Empty<string>())
                    .Where(path => !string.IsNullOrWhiteSpace(path)
                                   && string.Equals(Path.GetExtension(path), ".sln",
                                       StringComparison.OrdinalIgnoreCase))
                    .Distinct(StringComparer.OrdinalIgnoreCase).ToList();
                var built = new List<FolderMatch>();
                if (solutions.Count == 0)
                {
                    built = SolutionTreeService.Build(root, token);
                }
                else
                {
                    var lastReport = Environment.TickCount;
                    foreach (var path in solutions)
                    {
                        token.ThrowIfCancellationRequested();
                        built.AddRange(SolutionTreeService.BuildFromProjectFiles(new[] { path }, paths, token));
                        var now = Environment.TickCount;
                        if (unchecked(now - lastReport) < 120) continue;
                        lastReport = now;
                        var count = built.Count;
                        var name = Path.GetFileName(path);
                        Dispatcher.BeginInvoke(new Action(() =>
                        {
                            if (generation != _solutionGeneration || token.IsCancellationRequested
                                                                  || _solutionReady || TreeViewIndex != 1 ||
                                                                  !IsTreeBusy) return;
                            CountLabel = string.Format(Strings.Main_NItems, count);
                            Status = string.Format(Strings.Main_SolutionsFound, count)
                                     + " / " + solutions.Count.ToString("N0") + "  " + name;
                        }), DispatcherPriority.Background);
                    }
                }

                foreach (var solution in built) AssignParents(solution);
                return built;
            }, token);
            token.ThrowIfCancellationRequested();
            if (generation != _solutionGeneration) return;
            foreach (var solution in roots) _solutionRoots.Add(solution);
            ApplySolutionRootIcons(_solutionRoots);
            _solutionReady = true;
        }
        catch (OperationCanceledException)
        {
        }
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
        var generation = _solutionGeneration;
        var viewGeneration = _treeViewGeneration;
        var task = StartSolutionAnalysis(_solutionAnalysisRoot ?? _folderTreeRoot, _solutionPathIndex);
        SetTreePanelBusy(true);
        Status = Strings.Main_BuildingTree;
        try
        {
            await task;
            if (generation != _solutionGeneration || viewGeneration != _treeViewGeneration ||
                TreeViewIndex != 1) return;
            if (_solutionReady) ShowTreeView(1);
            else if (_solutionError != null)
                ShowError(Strings.Main_SearchFailed, _solutionError);
            else Status = Strings.Main_SearchCancelled;
        }
        finally
        {
            if (generation == _solutionGeneration && viewGeneration == _treeViewGeneration && TreeViewIndex == 1)
                SetTreePanelBusy(false);
        }
    }
}