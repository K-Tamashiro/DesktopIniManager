using DesktopIniManager.Models;
using DesktopIniManager.Services;
using System;
using System.Collections.ObjectModel;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Media;
using DesktopIniManager.Properties;

namespace DesktopIniManager.ViewModels
{
    internal sealed partial class MainWindowViewModel
    {
        internal void SortPhysicalTree()
        {
            SortCollection(_treeRoots);
        }

        internal static void SortCollection(ObservableCollection<FolderMatch> items)
        {
            foreach (FolderMatch item in items)
                SortCollection(item.Children);

            FolderMatch[] ordered = items
                .OrderBy(item => item.Name, StringComparer.CurrentCultureIgnoreCase)
                .ThenBy(item => item.Path, StringComparer.CurrentCultureIgnoreCase)
                .ToArray();

            items.Clear();
            foreach (FolderMatch item in ordered)
                items.Add(item);
        }

        internal IEnumerable<FolderMatch> SelectableFolders() =>
            CurrentItems().Where(item => item.IsActionable && !item.IsHidden && !item.IsFilterHidden);

        internal void InvertSelection()
        {
            foreach (FolderMatch item in SelectableFolders())
                item.SetSelected(!item.IsSelected);
        }

        internal void ExpandAll() { foreach (var item in CurrentItems()) item.IsExpanded = true; }

        internal void CollapseAll() { foreach (var item in CurrentItems()) item.IsExpanded = false; }

        internal void ShowTreeView(int view) { if (view != 2) _baseTreeView = view; _treeView = view; _solutionView = view == 1; OnPropertyChanged(nameof(SelectedTreeView)); TreeItems = view == 0 ? _treeRoots : view == 1 ? _solutionRoots : _searchRoots; _ = ApplyFolderFilterAsync(); UpdateVisibleCount(); Status = view == 0 ? string.Format(Strings.Main_FoldersFound, _results.Count) : view == 1 ? string.Format(Strings.Main_SolutionsFound, _solutionRoots.Count) : string.Format(Strings.Main_SearchResults, _searchResultCount); }

        internal void RefreshTreeItemsSource() { TreeItems = _treeView == 0 ? _treeRoots : _treeView == 1 ? _solutionRoots : _searchRoots; }

        internal void ShowSolutionView() => ShowTreeView(1);

        internal void UpdateVisibleCount()
        {
            CountLabel = string.Format(_solutionView ? Strings.Main_NItems : Strings.Main_NFolders, CurrentItems().Count(item => !item.IsHidden && !item.IsFilterHidden));
        }

        internal System.Collections.Generic.IEnumerable<FolderMatch> CurrentItems() => _filteredViewItems ?? Flatten(_treeView == 0 ? _treeRoots : _treeView == 1 ? _solutionRoots : _searchRoots).ToList();

        internal System.Collections.Generic.IEnumerable<FolderMatch> VisibleItems() => _filteredViewItems != null
            ? _filteredViewItems.Where(item => !item.IsHidden) : FlattenVisible(_treeView == 0 ? _treeRoots : _treeView == 1 ? _solutionRoots : _searchRoots);

        internal static void AssignParents(FolderMatch node)
        {
            if (node == null) return;
            foreach (FolderMatch child in node.Children)
            {
                child.Parent = node;
                AssignParents(child);
            }
        }

        internal static System.Collections.Generic.IEnumerable<FolderMatch> Flatten(System.Collections.Generic.IEnumerable<FolderMatch> roots)
        {
            foreach (FolderMatch item in roots) { yield return item; foreach (FolderMatch child in Flatten(item.Children)) yield return child; }
        }

        internal static System.Collections.Generic.IEnumerable<FolderMatch> FlattenVisible(System.Collections.Generic.IEnumerable<FolderMatch> roots)
        {
            foreach (FolderMatch item in roots)
            {
                if (item.IsHidden) continue;
                yield return item;
                if (item.IsExpanded) foreach (FolderMatch child in FlattenVisible(item.Children)) yield return child;
            }
        }

        internal void AddTreeResult(FolderMatch item)
        {
            FolderMatch parent = _results
                .Where(candidate => IsAncestorPath(candidate.Path, item.Path))
                .OrderByDescending(candidate => candidate.Path.Length)
                .FirstOrDefault();
            _results.Add(item);
            if (parent == null) { item.Parent = null; _treeRoots.Add(item); }
            else { item.Parent = parent; parent.Children.Add(item); }

            // Re-parent roots that arrived before their newly discovered ancestor.
            foreach (FolderMatch root in _treeRoots.Where(candidate => !ReferenceEquals(candidate, item) && IsAncestorPath(item.Path, candidate.Path)).ToList())
            {
                _treeRoots.Remove(root);
                root.Parent = item;
                item.Children.Add(root);
            }
            CountLabel = string.Format(Strings.Main_NMatches, _results.Count);
        }

        internal void AddTreeResults(IEnumerable<FolderMatch> items)
        {
            bool physicalViewVisible = ReferenceEquals(TreeItems, _treeRoots);
            if (physicalViewVisible) TreeItems = null;
            var byPath = new Dictionary<string, FolderMatch>(StringComparer.OrdinalIgnoreCase);
            try
            {
                foreach (FolderMatch item in items.OrderBy(item => item.Path.Length).ThenBy(item => item.Path, StringComparer.CurrentCultureIgnoreCase))
                {
                    _results.Add(item);
                    byPath[item.Path.TrimEnd(Path.DirectorySeparatorChar)] = item;
                    string parentPath = Path.GetDirectoryName(item.Path.TrimEnd(Path.DirectorySeparatorChar));
                    FolderMatch parent = null;
                    while (!string.IsNullOrEmpty(parentPath))
                    {
                        if (byPath.TryGetValue(parentPath.TrimEnd(Path.DirectorySeparatorChar), out parent)) break;
                        parentPath = Path.GetDirectoryName(parentPath);
                    }
                    if (parent == null) { item.Parent = null; _treeRoots.Add(item); }
                    else { item.Parent = parent; parent.Children.Add(item); }
                }
            }
            finally { if (physicalViewVisible) TreeItems = _treeRoots; }
            CountLabel = string.Format(Strings.Main_NMatches, _results.Count);
        }

        internal async Task AddTreeResultsAsync(IEnumerable<FolderMatch> items, CancellationToken token, bool intoSearch = false)
        {
            List<FolderMatch> source = items.ToList();
            Status = string.Format(Strings.Main_BuildingRows, source.Count.ToString("N0"));
            TreeBuildResult built = await Task.Run(() =>
            {
                var roots = new List<FolderMatch>();
                var byPath = new Dictionary<string, FolderMatch>(StringComparer.OrdinalIgnoreCase);
                foreach (FolderMatch item in source.OrderBy(item => item.Path.Length).ThenBy(item => item.Path, StringComparer.CurrentCultureIgnoreCase))
                {
                    token.ThrowIfCancellationRequested();
                    item.Children.Clear();
                    string normalized = item.Path.TrimEnd(Path.DirectorySeparatorChar);
                    byPath[normalized] = item;
                    string parentPath = Path.GetDirectoryName(normalized);
                    FolderMatch parent = null;
                    while (!string.IsNullOrEmpty(parentPath))
                    {
                        if (byPath.TryGetValue(parentPath.TrimEnd(Path.DirectorySeparatorChar), out parent)) break;
                        parentPath = Path.GetDirectoryName(parentPath);
                    }
                    if (parent == null) { item.Parent = null; roots.Add(item); }
                    else { item.Parent = parent; parent.Children.Add(item); }
                }
                foreach (FolderMatch root in roots) SortCollection(root.Children);
                foreach (FolderMatch item in source) item.IsExpanded = false;
                foreach (FolderMatch root in roots) root.IsExpanded = true;
                roots.Sort((left, right) =>
                {
                    int name = StringComparer.CurrentCultureIgnoreCase.Compare(left.Name, right.Name);
                    return name != 0 ? name : StringComparer.CurrentCultureIgnoreCase.Compare(left.Path, right.Path);
                });
                return new TreeBuildResult(source, roots);
            }, token);
            token.ThrowIfCancellationRequested();
            if (intoSearch)
            {
                _searchRoots.Clear();
                foreach (FolderMatch root in built.Roots) _searchRoots.Add(root);
                _searchResultCount = built.Items.Count;
                RefreshTreeItemsSource();
                CountLabel = string.Format(Strings.Main_SearchResults, _searchResultCount);
                return;
            }
            _results.Clear();
            foreach (FolderMatch item in built.Items) _results.Add(item);
            _treeRoots.Clear();
            foreach (FolderMatch root in built.Roots) _treeRoots.Add(root);
            RefreshTreeItemsSource();
            CountLabel = string.Format(Strings.Main_NFolders, _results.Count);
        }

        internal static bool IsAncestorPath(string parent, string child)
        {
            string prefix = parent.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
            return child.StartsWith(prefix, StringComparison.OrdinalIgnoreCase);
        }

        internal void HideSelected()
        {
            foreach (FolderMatch item in CurrentItems())
                item.IsHidden = !item.IsSelected;
            UpdateVisibleCount();
        }

        internal void Unhide()
        {
            foreach (FolderMatch item in CurrentItems())
                item.IsHidden = false;
            UpdateVisibleCount();
        }


        internal ObservableCollection<FolderMatch> CurrentTreeRoots()
        {
            if (_treeView == 1) return _solutionRoots;
            if (_treeView == 2) return _searchRoots;
            return _treeRoots;
        }

        internal FolderMatch FindFolderInRoots(ObservableCollection<FolderMatch> roots, string directory)
        {
            string current = directory.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            while (!string.IsNullOrEmpty(current))
            {
                foreach (FolderMatch item in Flatten(roots))
                {
                    string path = (item.Path ?? string.Empty).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
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
            string[] terms = (FolderFilter ?? string.Empty).Split((char[])null, StringSplitOptions.RemoveEmptyEntries);
            List<FolderMatch> all = Flatten(_treeView == 0 ? _treeRoots : _treeView == 1 ? _solutionRoots : _searchRoots).ToList();
            try
            {
                List<FolderMatch> matches = terms.Length == 0 ? null : await Task.Run(() =>
                {
                    var filtered = new List<FolderMatch>();
                    foreach (FolderMatch item in all)
                    {
                        filterCts.Token.ThrowIfCancellationRequested();
                        string folderName = item.Name ?? string.Empty;
                        if (terms.Any(term => folderName.IndexOf(term, StringComparison.CurrentCultureIgnoreCase) >= 0))
                            filtered.Add(item);
                    }
                    return filtered;
                }, filterCts.Token);
                if (filterCts.IsCancellationRequested || !ReferenceEquals(_filterCts, filterCts)) return;
                TreeItems = null;
                if (matches != null) foreach (FolderMatch item in matches) item.IsExpanded = false;
                _filteredViewItems = matches;
                TreeItems = matches == null
                    ? (IEnumerable<FolderMatch>)(_treeView == 0 ? _treeRoots : _treeView == 1 ? _solutionRoots : _searchRoots) : matches;
                UpdateVisibleCount();
            }
            catch (OperationCanceledException) { }
        }

        internal void Apply()
        {
            var selected = VisibleItems().Where(item => item.IsActionable && item.IsSelected)
                .GroupBy(item => item.Path, StringComparer.OrdinalIgnoreCase)
                .Select(group => group.First()).ToList();
            bool addToGitIgnore = AddToGitIgnore == true;
            if (selected.Count == 0) { _dialogs.Show(Strings.Main_SelectOneFolder, Strings.App_Title); return; }
            if (_dialogs.Show(string.Format(Strings.Main_ConfirmApply, selected.Count), Strings.App_Title, MessageBoxButton.OKCancel, MessageBoxImage.Question) != MessageBoxResult.OK) return;

            int succeeded = 0;
            var errors = new System.Collections.Generic.List<string>();
            var changedFolders = new List<string>();
            var service = new DesktopIniService();

            foreach (var item in selected)
            {
                try
                {
                    service.Apply(item.Path, IconLibraryPath, _selectedIconIndex, addToGitIgnore, false);
                    FolderIconService.Invalidate(item.Path);
                    item.IconPreview = _selectedIconPreview ?? FolderIconService.GetFolderIcon(item.Path);
                    changedFolders.Add(item.Path);
                    succeeded++;
                }
                catch (Exception ex)
                {
                    errors.Add(item.Path + ": " + ErrorMessages.English(ex));
                }
            }

            if (changedFolders.Count > 0)
                service.NotifyExplorer(changedFolders);

            Status = string.Format(Strings.Main_AppliedTo, succeeded);
            _dialogs.Show(errors.Count == 0 ? Strings.Main_ApplyOk : string.Format(Strings.Main_ApplyResult, succeeded, errors.Count) + "\n\n" + string.Join("\n", errors.Take(5)), Strings.App_Title);
        }

        internal void Remove()
        {
            var selected = VisibleItems().Where(item => item.IsActionable && item.IsSelected)
                .GroupBy(item => item.Path, StringComparer.OrdinalIgnoreCase)
                .Select(group => group.First()).ToList();
            if (selected.Count == 0) { _dialogs.Show(Strings.Main_SelectOneFolder, Strings.App_Title); return; }
            if (_dialogs.Show(string.Format(Strings.Main_ConfirmRemove, selected.Count), Strings.App_Title, MessageBoxButton.OKCancel, MessageBoxImage.Warning) != MessageBoxResult.OK) return;

            int succeeded = 0;
            var errors = new System.Collections.Generic.List<string>();
            var changedFolders = new List<string>();
            var service = new DesktopIniService();

            foreach (var item in selected)
            {
                try
                {
                    service.Remove(item.Path, false);
                    FolderIconService.Invalidate(item.Path);
                    item.IconPreview = FolderIconService.GetDefaultFolderIcon();
                    changedFolders.Add(item.Path);
                    succeeded++;
                }
                catch (Exception ex)
                {
                    errors.Add(item.Path + ": " + ErrorMessages.English(ex));
                }
            }

            if (changedFolders.Count > 0)
                service.NotifyExplorer(changedFolders);

            Status = string.Format(Strings.Main_RemovedFrom, succeeded);
            _dialogs.Show(errors.Count == 0 ? Strings.Main_RemoveOk : string.Format(Strings.Main_RemoveResult, succeeded, errors.Count) + "\n\n" + string.Join("\n", errors.Take(5)), Strings.App_Title);
        }


        internal int SelectDroppedFolders(IReadOnlyList<string> folders)
        {
            if (folders == null || folders.Count == 0) return 0;

            int selected = 0;
            List<FolderMatch> nodes = Flatten(CurrentTreeRoots()).ToList();
            foreach (string folder in folders)
            {
                string normalized = NormalizeFolderPath(folder);
                FolderMatch node = nodes.FirstOrDefault(item =>
                    !string.IsNullOrWhiteSpace(item.Path)
                    && string.Equals(NormalizeFolderPath(item.Path), normalized, StringComparison.OrdinalIgnoreCase));
                if (node == null) continue;

                for (FolderMatch ancestor = node.Parent; ancestor != null; ancestor = ancestor.Parent)
                    ancestor.IsExpanded = true;
                node.SetSelected(true);
                selected++;
            }

            return selected;
        }

        internal void RestoreFolderTrees()
        {
            try
            {
                if (_startup?.TreeError != null) { Status = string.Format(Strings.Main_RestoreTreesFailed, _startup.TreeError); return; }
                var state = _startup != null ? _startup.Tree : FolderTreeStateService.Load();
                if (state == null) return;
                var icons = _startup == null ? FolderTreeStateService.RestoreIcons(state.Icons) : null;
                var physical = _startup != null ? _startup.Physical : FolderTreeStateService.Restore(state.Physical, icons: icons);
                var solution = _startup != null ? _startup.Solution : FolderTreeStateService.Restore(state.Solution, icons: icons);
                foreach (var node in physical) _treeRoots.Add(node);
                foreach (var node in solution) _solutionRoots.Add(node);
                foreach (var node in Flatten(_treeRoots)) _results.Add(node);
                _physicalCurrent = _results.FirstOrDefault(node => node.IsCurrent);
                _solutionCurrent = Flatten(_solutionRoots).FirstOrDefault(node => node.IsCurrent);
                _folderTreeRoot = state.Root;
                ShowTreeView(state.View == 1 ? 1 : 0);
                Status = Strings.Main_TreesRestored;
            }
            catch (Exception ex) { Status = string.Format(Strings.Main_RestoreTreesFailed, ErrorMessages.English(ex)); }
        }

        internal void SaveFolderTrees()
        {
            // A cancelled/in-progress rebuild must not replace the last complete trees.
            if (_rebuildingFolderTrees || _folderTreeRoot == null) return;
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
            catch (Exception ex) { Status = string.Format(Strings.Main_SaveTreesFailed, ErrorMessages.English(ex)); }
        }

    }
}
