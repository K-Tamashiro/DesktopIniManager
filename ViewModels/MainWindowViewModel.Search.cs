using DesktopIniManager.Models;
using DesktopIniManager.Services;
using Microsoft.Win32;
using System;
using System.Collections.ObjectModel;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Globalization;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Media.Animation;
using System.Windows.Controls;
using System.Windows.Threading;
using FastVolumeIndex;
using DesktopIniManager.Properties;

namespace DesktopIniManager.ViewModels
{
    internal sealed partial class MainWindowViewModel
    {
        internal async Task SearchAsync()
        {
            // Snapshot bindable state before starting background work.
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
            if (!Directory.Exists(root)) { _dialogs.Show(Strings.Main_LocationMissing, Strings.App_Title); return; }
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
                System.Collections.Generic.List<FolderMatch> solutionRoots = new List<FolderMatch>();
                if (folderListMode)
                {
                    StandardSearchResult standard = await RunStandardIndexedSearch(root, string.Empty, searchCts.Token);
                    await AddTreeResultsAsync(standard.Matches, searchCts.Token, searchOnly);
                    _pathIndex = standard.Paths;
                    RefreshTreeItemsSource();
                }
                else
                {
                    StandardSearchResult standard = await RunStandardIndexedSearch(root, query, searchCts.Token);
                    await AddTreeResultsAsync(standard.Matches, searchCts.Token, searchOnly);
                    _pathIndex = standard.Paths;
                    RefreshTreeItemsSource();
                    await ApplyStandardDevelopmentAnalysis(gitSearchRequested, standard.Paths, searchCts.Token);
                    if (!searchOnly) solutionRoots = await BuildSolutions(root, searchCts.Token);
                }
                foreach (FolderMatch solution in solutionRoots)
                {
                    AssignParents(solution);
                    _solutionRoots.Add(solution);
                }
                if (!searchOnly)
                {
                    _folderTreeRoot = root;
                    _physicalCurrent = _solutionCurrent = null;
                    _rebuildingFolderTrees = false;
                    SaveFolderTrees();
                }
                if (searchOnly)
                {
                    ShowTreeView(2);
                    SearchRootSelectionRequested?.Invoke();
                }
                else
                    ShowTreeView(0);
            }
            catch (OperationCanceledException) { Status = Strings.Main_SearchCancelled; }
            catch (Exception ex) { _dialogs.Show(ErrorMessages.English(ex), Strings.App_Title); Status = Strings.Main_SearchFailed; }
            finally
            {
                if (ReferenceEquals(_searchCts, searchCts)) { _searchCts = null; SetSearching(false); }
                searchCts.Dispose();
            }
        }

        internal Task<List<FolderMatch>> RunStandardSearch(string root, string query, CancellationToken token)
        {
            ImageSource defaultFolderIcon = FolderIconService.GetDefaultFolderIcon();
            return Task.Run(() =>
            {
                var matches = new List<FolderMatch>();
                new FolderSearchService().Search(root, query,
                    item => { item.IconPreview = string.Equals(item.Reason, "Folder", StringComparison.Ordinal) ? defaultFolderIcon : FolderIconService.GetFolderIcon(item.Path); matches.Add(item); },
                    count => Dispatcher.BeginInvoke(new Action(() => Status = string.Format(Strings.Main_ScanningFolders, count.ToString("N0")))), token);
                token.ThrowIfCancellationRequested();
                return matches;
            });
        }

        private Task<StandardSearchResult> RunStandardIndexedSearch(string root, string query, CancellationToken token)
        {
            ImageSource defaultFolderIcon = FolderIconService.GetDefaultFolderIcon();
            return Task.Run(() =>
            {
                int lastReport = Environment.TickCount;
                VolumePathIndex paths = VolumePathIndex.BuildFromDirCommand(root,
                    count =>
                    {
                        int now = Environment.TickCount;
                        if (unchecked(now - lastReport) < 125) return;
                        lastReport = now;
                        Dispatcher.BeginInvoke(new Action(() => Status = string.Format(Strings.Main_IndexedFoldersEllipsis, count.ToString("N0"))), System.Windows.Threading.DispatcherPriority.Background);
                    }, token);
                token.ThrowIfCancellationRequested();
                Dispatcher.BeginInvoke(new Action(() => Status = Strings.Main_BuildingTree));
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

        internal Task<List<FolderMatch>> RunFolderList(string root, CancellationToken token)
        {
            ImageSource defaultFolderIcon = FolderIconService.GetDefaultFolderIcon();
            return Task.Run(() =>
            {
                var folders = new List<FolderMatch>();
                foreach (string folder in Directory.EnumerateDirectories(root))
                {
                    token.ThrowIfCancellationRequested();
                    folders.Add(new FolderMatch { Path = folder, Reason = "Folder", IconPreview = defaultFolderIcon });
                }
                return folders;
            });
        }

        internal async Task<List<FolderMatch>> BuildSolutions(string root, CancellationToken token)
        {
            ShowTreeView(1);
            SetTreePanelBusy(true);
            try
            {
                IReadOnlyList<string> projectFiles = _pathIndex != null
                    ? _pathIndex.ProjectFiles
                    : (IReadOnlyList<string>)new string[0];

                List<string> solutions = projectFiles
                    .Where(path => !string.IsNullOrWhiteSpace(path)
                        && string.Equals(Path.GetExtension(path), ".sln", StringComparison.OrdinalIgnoreCase))
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToList();

                CountLabel = string.Format(Strings.Main_NItems, 0);
                Status = solutions.Count == 0
                    ? Strings.Main_BuildingTree
                    : string.Format(Strings.Main_SolutionsFound, 0) + " / " + solutions.Count.ToString("N0");

                return await Task.Run(() =>
                {
                    if (solutions.Count == 0)
                    {
                        List<FolderMatch> scanned = SolutionTreeService.Build(root, token);
                        Dispatcher.BeginInvoke(new Action(() =>
                        {
                            CountLabel = string.Format(Strings.Main_NItems, scanned.Count);
                            Status = string.Format(Strings.Main_SolutionsFound, scanned.Count);
                        }), System.Windows.Threading.DispatcherPriority.Background);
                        return scanned;
                    }

                    var built = new List<FolderMatch>(solutions.Count);
                    int lastReport = Environment.TickCount;
                    for (int index = 0; index < solutions.Count; index++)
                    {
                        token.ThrowIfCancellationRequested();
                        string path = solutions[index];
                        List<FolderMatch> parsed = SolutionTreeService.BuildFromProjectFiles(new[] { path }, token);
                        built.AddRange(parsed);

                        int now = Environment.TickCount;
                        if (index == solutions.Count - 1 || unchecked(now - lastReport) >= 120)
                        {
                            lastReport = now;
                            int count = built.Count;
                            string name = Path.GetFileName(path);
                            Dispatcher.BeginInvoke(new Action(() =>
                            {
                                CountLabel = string.Format(Strings.Main_NItems, count);
                                Status = string.Format(Strings.Main_SolutionsFound, count)
                                    + " / " + solutions.Count.ToString("N0")
                                    + "  " + name;
                            }), System.Windows.Threading.DispatcherPriority.Background);
                        }
                    }

                    return built;
                }, token);
            }
            finally
            {
                SetTreePanelBusy(false);
            }
        }

    }
}
