using DesktopIniManager.Models;
using DesktopIniManager.Properties;
using DesktopIniManager.Services;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Media;

namespace DesktopIniManager.ViewModels;

internal sealed partial class MainWindowViewModel
{
    internal static bool IsDroppedTreeFolder(string name)
    {
        return string.Equals(name, ".vs", StringComparison.OrdinalIgnoreCase)
               || string.Equals(name, ".git", StringComparison.OrdinalIgnoreCase)
               || string.Equals(name, ".vscode", StringComparison.OrdinalIgnoreCase)
               || string.Equals(name, ".idea", StringComparison.OrdinalIgnoreCase);
    }

    internal static bool IsDroppedTreePath(string path)
    {
        if (string.IsNullOrWhiteSpace(path)) return false;
        var parts = path.Split(new[] { Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar },
            StringSplitOptions.RemoveEmptyEntries);
        return parts.Any(part => IsDroppedTreeFolder(part));
    }

    internal static bool IsInitiallyCollapsedFolder(FolderMatch item)
    {
        var name = item?.Name;
        return string.Equals(name, "bin", StringComparison.OrdinalIgnoreCase)
               || string.Equals(name, "obj", StringComparison.OrdinalIgnoreCase);
    }

    internal static void CollapseBuildOutputFolders(IEnumerable<FolderMatch> items)
    {
        if (items == null) return;
        foreach (var item in items)
            if (IsInitiallyCollapsedFolder(item))
                item.IsExpanded = false;
    }

    internal static void SortCollection(ObservableCollection<FolderMatch> items)
    {
        foreach (var item in items)
            SortCollection(item.Children);

        var ordered = items
            .OrderBy(item => item.Name, StringComparer.CurrentCultureIgnoreCase)
            .ThenBy(item => item.Path, StringComparer.CurrentCultureIgnoreCase)
            .ToArray();

        items.Clear();
        foreach (var item in ordered)
            items.Add(item);
    }

    internal IEnumerable<FolderMatch> SelectableFolders()
    {
        return CurrentItems().Where(item => !item.IsHidden && !item.IsFilterHidden);
    }

    internal void InvertSelection()
    {
        foreach (var item in SelectableFolders())
            item.SetSelected(!item.IsSelected);
        SyncAllFoldersSelectedFlag();
    }

    internal void ExpandAll()
    {
        foreach (var item in CurrentItems()) item.IsExpanded = true;
    }

    internal void CollapseAll()
    {
        foreach (var item in CurrentItems()) item.IsExpanded = false;
    }

    internal void ShowTreeView(int view)
    {
        _treeViewGeneration++;
        SetTreePanelBusy(false);
        if (view != 2) _baseTreeView = view;
        TreeViewIndex = view;
        _solutionView = view == 1;
        OnPropertyChanged(nameof(SelectedTreeView));
        TreeItems = view == 0 ? _treeRoots : view == 1 ? _solutionRoots : _searchRoots;
        _ = ApplyFolderFilterAsync();
        UpdateVisibleCount();
        SyncAllFoldersSelectedFlag();
        Status = view == 0
            ? string.Format(Strings.Main_FoldersFound, _results.Count)
            : view == 1
                ? string.Format(Strings.Main_SolutionsFound, _solutionRoots.Count)
                : string.Format(Strings.Main_SearchResults, _searchResultCount);
        if (view == 1) _ = EnsureSolutionViewAsync();
    }

    internal void SyncAllFoldersSelectedFlag()
    {
        var items = SelectableFolders();
        var all = items.Any() && items.All(item => item.IsSelected);
        if (_allFoldersSelected != all)
        {
            _allFoldersSelected = all;
            OnPropertyChanged(nameof(AllFoldersSelected));
        }

        RefreshScopedLabel();
    }

    internal void RefreshTreeItemsSource()
    {
        TreeItems = TreeViewIndex == 0 ? _treeRoots : TreeViewIndex == 1 ? _solutionRoots : _searchRoots;
    }

    internal void UpdateVisibleCount()
    {
        CountLabel = string.Format(_solutionView ? Strings.Main_NItems : Strings.Main_NFolders,
            CurrentItems().Count(item => !item.IsHidden && !item.IsFilterHidden));
    }

    internal IEnumerable<FolderMatch> CurrentItems()
    {
        return _filteredViewItems ??
               Flatten(TreeViewIndex == 0 ? _treeRoots : TreeViewIndex == 1 ? _solutionRoots : _searchRoots).ToList();
    }

    internal IEnumerable<FolderMatch> VisibleItems()
    {
        return _filteredViewItems != null
            ? _filteredViewItems.Where(item => !item.IsHidden)
            : FlattenVisible(TreeViewIndex == 0 ? _treeRoots : TreeViewIndex == 1 ? _solutionRoots : _searchRoots);
    }

    internal static void AssignParents(FolderMatch node)
    {
        if (node == null) return;
        foreach (var child in node.Children)
        {
            child.Parent = node;
            AssignParents(child);
        }
    }

    internal static IEnumerable<FolderMatch> Flatten(IEnumerable<FolderMatch> roots)
    {
        foreach (var item in roots)
        {
            yield return item;
            foreach (var child in Flatten(item.Children)) yield return child;
        }
    }

    internal static IEnumerable<FolderMatch> FlattenVisible(IEnumerable<FolderMatch> roots)
    {
        foreach (var item in roots)
        {
            if (item.IsHidden) continue;
            yield return item;
            if (item.IsExpanded)
                foreach (var child in FlattenVisible(item.Children))
                    yield return child;
        }
    }

    internal async Task AddTreeResultsAsync(IEnumerable<FolderMatch> items, CancellationToken token,
        bool intoSearch = false)
    {
        var source = items.Where(item => !IsDroppedTreePath(item.Path)).ToList();
        Status = string.Format(Strings.Main_BuildingRows, source.Count.ToString("N0"));
        var built = await Task.Run(() =>
        {
            var roots = new List<FolderMatch>();
            var byPath = new Dictionary<string, FolderMatch>(StringComparer.OrdinalIgnoreCase);
            foreach (var item in source.OrderBy(item => item.Path.Length)
                         .ThenBy(item => item.Path, StringComparer.CurrentCultureIgnoreCase))
            {
                token.ThrowIfCancellationRequested();
                item.Children.Clear();
                var normalized = item.Path.TrimEnd(Path.DirectorySeparatorChar);
                byPath[normalized] = item;
                var parentPath = Path.GetDirectoryName(normalized);
                FolderMatch parent = null;
                while (!string.IsNullOrEmpty(parentPath))
                {
                    if (byPath.TryGetValue(parentPath.TrimEnd(Path.DirectorySeparatorChar), out parent)) break;
                    parentPath = Path.GetDirectoryName(parentPath);
                }

                if (parent == null)
                {
                    item.Parent = null;
                    roots.Add(item);
                }
                else
                {
                    item.Parent = parent;
                    parent.Children.Add(item);
                }
            }

            foreach (var root in roots) SortCollection(root.Children);
            foreach (var item in source)
                item.IsExpanded = false;
            foreach (var root in roots)
                root.IsExpanded = !IsInitiallyCollapsedFolder(root);
            CollapseBuildOutputFolders(source);
            roots.Sort((left, right) =>
            {
                var name = StringComparer.CurrentCultureIgnoreCase.Compare(left.Name, right.Name);
                return name != 0 ? name : StringComparer.CurrentCultureIgnoreCase.Compare(left.Path, right.Path);
            });
            return new TreeBuildResult(source, roots);
        }, token);
        token.ThrowIfCancellationRequested();
        if (intoSearch)
        {
            _searchRoots.Clear();
            foreach (var root in built.Roots) _searchRoots.Add(root);
            SetSearchResultCount(built.Items.Count);
            RefreshTreeItemsSource();
            CountLabel = string.Format(Strings.Main_SearchResults, _searchResultCount);
            return;
        }

        _results.Clear();
        foreach (var item in built.Items) _results.Add(item);
        _treeRoots.Clear();
        foreach (var root in built.Roots) _treeRoots.Add(root);
        RefreshTreeItemsSource();
        CountLabel = string.Format(Strings.Main_NFolders, _results.Count);
    }

    internal void HideSelected()
    {
        foreach (var item in CurrentItems())
            if (item.IsSelected)
                item.IsHidden = true;
        UpdateVisibleCount();
    }

    internal void Unhide()
    {
        foreach (var item in CurrentItems())
            item.IsHidden = false;
        UpdateVisibleCount();
    }

    internal ObservableCollection<FolderMatch> CurrentTreeRoots()
    {
        if (TreeViewIndex == 1) return _solutionRoots;
        if (TreeViewIndex == 2) return _searchRoots;
        return _treeRoots;
    }

    internal FolderMatch FindFolderInRoots(ObservableCollection<FolderMatch> roots, string directory)
    {
        var current = directory.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        while (!string.IsNullOrEmpty(current))
        {
            foreach (var item in Flatten(roots))
            {
                var path = (item.Path ?? string.Empty).TrimEnd(Path.DirectorySeparatorChar,
                    Path.AltDirectorySeparatorChar);
                if (string.Equals(path, current, StringComparison.OrdinalIgnoreCase)) return item;
            }

            current = Path.GetDirectoryName(current);
        }

        return roots.Count > 0 ? roots[0] : null;
    }

    internal async Task ApplyFolderFilterAsync()
    {
        _filterCts?.Cancel();
        var filterCts = new CancellationTokenSource();
        _filterCts = filterCts;
        var terms = (FolderFilter ?? string.Empty).Split((char[])null, StringSplitOptions.RemoveEmptyEntries);
        var all = Flatten(TreeViewIndex == 0 ? _treeRoots : TreeViewIndex == 1 ? _solutionRoots : _searchRoots)
            .ToList();
        try
        {
            var matches = terms.Length == 0
                ? null
                : await Task.Run(() =>
                {
                    var filtered = new List<FolderMatch>();
                    foreach (var item in all)
                    {
                        filterCts.Token.ThrowIfCancellationRequested();
                        var folderName = item.Name ?? string.Empty;
                        if (terms.Any(term => folderName.IndexOf(term, StringComparison.CurrentCultureIgnoreCase) >= 0))
                            filtered.Add(item);
                    }

                    return filtered;
                }, filterCts.Token);
            if (filterCts.IsCancellationRequested || !ReferenceEquals(_filterCts, filterCts)) return;
            TreeItems = null;
            if (matches != null)
                foreach (var item in matches)
                    item.IsExpanded = false;
            _filteredViewItems = matches;
            TreeItems = matches == null
                ? TreeViewIndex == 0 ? _treeRoots : TreeViewIndex == 1 ? _solutionRoots : _searchRoots
                : matches;
            UpdateVisibleCount();
        }
        catch (OperationCanceledException)
        {
        }
    }

    internal void Apply()
    {
        var selected = VisibleItems().Where(item => item.IsActionable && item.IsSelected)
            .GroupBy(item => item.Path, StringComparer.OrdinalIgnoreCase)
            .Select(group => group.First()).ToList();
        var addToGitIgnore = AddToGitIgnore == true;
        if (selected.Count == 0)
        {
            _dialogs.Show(Strings.Main_SelectOneFolder, Strings.App_Title);
            return;
        }

        if (_dialogs.Show(string.Format(Strings.Main_ConfirmApply, selected.Count), Strings.App_Title,
                MessageBoxButton.OKCancel, MessageBoxImage.Question) != MessageBoxResult.OK) return;

        var succeeded = 0;
        var errors = new List<string>();
        var changedFolders = new List<string>();
        var service = new DesktopIniService();

        foreach (var item in selected)
            try
            {
                service.Apply(item.Path, IconLibraryPath, SelectedIconIndex, addToGitIgnore, false);
                FolderIconService.Invalidate(item.Path);
                item.IconPreview = _selectedIconPreview ?? FolderIconService.GetFolderIcon(item.Path);
                changedFolders.Add(item.Path);
                succeeded++;
            }
            catch (Exception ex)
            {
                errors.Add(item.Path + ": " + ErrorMessages.English(ex));
            }

        if (changedFolders.Count > 0)
            service.NotifyExplorer(changedFolders);

        Status = string.Format(Strings.Main_AppliedTo, succeeded);
        _dialogs.Show(
            errors.Count == 0
                ? Strings.Main_ApplyOk
                : string.Format(Strings.Main_ApplyResult, succeeded, errors.Count) + "\n\n" +
                  string.Join("\n", errors.Take(5)), Strings.App_Title);
    }

    internal void Remove()
    {
        var selected = VisibleItems().Where(item => item.IsActionable && item.IsSelected)
            .GroupBy(item => item.Path, StringComparer.OrdinalIgnoreCase)
            .Select(group => group.First()).ToList();
        if (selected.Count == 0)
        {
            _dialogs.Show(Strings.Main_SelectOneFolder, Strings.App_Title);
            return;
        }

        if (_dialogs.Show(string.Format(Strings.Main_ConfirmRemove, selected.Count), Strings.App_Title,
                MessageBoxButton.OKCancel, MessageBoxImage.Warning) != MessageBoxResult.OK) return;

        var succeeded = 0;
        var errors = new List<string>();
        var changedFolders = new List<string>();
        var service = new DesktopIniService();

        foreach (var item in selected)
            try
            {
                service.Remove(item.Path, false);
                FolderIconService.Invalidate(item.Path);
                item.IconPreview = FolderIconService.GetStateIcon(item.Path, item.Reason) ??
                                   FolderIconService.GetDefaultFolderIcon();
                changedFolders.Add(item.Path);
                succeeded++;
            }
            catch (Exception ex)
            {
                errors.Add(item.Path + ": " + ErrorMessages.English(ex));
            }

        if (changedFolders.Count > 0)
            service.NotifyExplorer(changedFolders);

        Status = string.Format(Strings.Main_RemovedFrom, succeeded);
        _dialogs.Show(
            errors.Count == 0
                ? Strings.Main_RemoveOk
                : string.Format(Strings.Main_RemoveResult, succeeded, errors.Count) + "\n\n" +
                  string.Join("\n", errors.Take(5)), Strings.App_Title);
    }

    internal int SelectDroppedFolders(IReadOnlyList<string> folders)
    {
        if (folders == null || folders.Count == 0) return 0;

        var selected = 0;
        var nodes = Flatten(CurrentTreeRoots()).ToList();
        foreach (var folder in folders)
        {
            var normalized = NormalizeFolderPath(folder);
            var node = nodes.FirstOrDefault(item =>
                !string.IsNullOrWhiteSpace(item.Path)
                && string.Equals(NormalizeFolderPath(item.Path), normalized, StringComparison.OrdinalIgnoreCase));
            if (node == null) continue;

            for (var ancestor = node.Parent; ancestor != null; ancestor = ancestor.Parent)
                ancestor.IsExpanded = true;
            node.SetSelected(true);
            selected++;
        }

        return selected;
    }

    internal static void ApplySolutionRootIcons(IEnumerable<FolderMatch> roots)
    {
        var icon = DifferencerStatusIcons.GetSolutionIcon();
        if (icon == null || roots == null) return;
        foreach (var root in roots)
            if (root != null)
                root.IconPreview = icon;
    }

    internal void RestoreFolderTrees()
    {
        try
        {
            if (_startup?.TreeError != null)
            {
                Status = string.Format(Strings.Main_RestoreTreesFailed, _startup.TreeError);
                return;
            }

            var state = _startup != null ? _startup.Tree : FolderTreeStateService.Load();
            if (state == null) return;
            var icons = _startup == null ? FolderTreeStateService.RestoreIcons(state.Icons) : null;
            var physical = _startup != null
                ? _startup.Physical
                : FolderTreeStateService.Restore(state.Physical, icons: icons);
            var solution = _startup != null
                ? _startup.Solution
                : FolderTreeStateService.Restore(state.Solution, icons: icons);
            foreach (var node in physical) _treeRoots.Add(node);
            foreach (var node in solution) _solutionRoots.Add(node);
            ApplySolutionRootIcons(_solutionRoots);
            foreach (var node in Flatten(_treeRoots)) _results.Add(node);
            _physicalCurrent = _results.FirstOrDefault(node => node.IsCurrent);
            _solutionCurrent = Flatten(_solutionRoots).FirstOrDefault(node => node.IsCurrent);
            _folderTreeRoot = state.Root;
            _solutionReady = true;
            ShowTreeView(state.View == 1 ? 1 : 0);
            Status = Strings.Main_TreesRestored;
        }
        catch (Exception ex)
        {
            Status = string.Format(Strings.Main_RestoreTreesFailed, ErrorMessages.English(ex));
        }
    }

    internal void SaveFolderTrees()
    {
        // A cancelled/in-progress rebuild must not replace the last complete trees.
        if (_rebuildingFolderTrees || _folderTreeRoot == null || !_solutionReady) return;
        try
        {
            var icons = new List<byte[]>();
            var iconIds = new Dictionary<ImageSource, int>();
            FolderTreeStateService.Save(new FolderTreeState
            {
                Root = _folderTreeRoot,
                View = _baseTreeView,
                Icons = icons,
                Physical = FolderTreeStateService.Capture(_treeRoots, _physicalCurrent, icons, iconIds),
                Solution = FolderTreeStateService.Capture(_solutionRoots, _solutionCurrent, icons, iconIds)
            });
        }
        catch (Exception ex)
        {
            Status = string.Format(Strings.Main_SaveTreesFailed, ErrorMessages.English(ex));
        }
    }
}