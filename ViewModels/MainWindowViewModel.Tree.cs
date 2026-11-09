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
    /// <summary>
    ///     フォルダー ツリーに表示しないフォルダー名 (.vs、.git、.vscode、.idea) かどうかを判定します。
    /// </summary>
    /// <param name="name">フォルダー名。</param>
    /// <returns>表示しない場合は true。</returns>
    internal static bool IsDroppedTreeFolder(string name)
    {
        return string.Equals(name, ".vs", StringComparison.OrdinalIgnoreCase)
               || string.Equals(name, ".git", StringComparison.OrdinalIgnoreCase)
               || string.Equals(name, ".vscode", StringComparison.OrdinalIgnoreCase)
               || string.Equals(name, ".idea", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    ///     パスに、フォルダー ツリーに表示しないフォルダーが含まれるかどうかを判定します。
    /// </summary>
    /// <param name="path">判定するパス。</param>
    /// <returns>含まれる場合は true。</returns>
    internal static bool IsDroppedTreePath(string path)
    {
        if (string.IsNullOrWhiteSpace(path)) return false;
        var parts = path.Split(new[] { Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar },
            StringSplitOptions.RemoveEmptyEntries);
        return parts.Any(part => IsDroppedTreeFolder(part));
    }

    /// <summary>
    ///     最初は折りたたんで表示するビルド出力フォルダー (bin、obj) かどうかを判定します。
    /// </summary>
    /// <param name="item">判定するフォルダー。</param>
    /// <returns>折りたたむ場合は true。</returns>
    internal static bool IsInitiallyCollapsedFolder(FolderMatch item)
    {
        var name = item?.Name;
        return string.Equals(name, "bin", StringComparison.OrdinalIgnoreCase)
               || string.Equals(name, "obj", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    ///     ビルド出力フォルダーを折りたたみます。
    /// </summary>
    /// <param name="items">対象のフォルダー。</param>
    internal static void CollapseBuildOutputFolders(IEnumerable<FolderMatch> items)
    {
        if (items == null) return;
        foreach (var item in items)
            if (IsInitiallyCollapsedFolder(item))
                item.IsExpanded = false;
    }

    /// <summary>
    ///     フォルダーを名前順・パス順に再帰的に並べ替えます。
    /// </summary>
    /// <param name="items">並べ替えるフォルダーのコレクション。</param>
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

    /// <summary>
    ///     非表示や絞り込みで隠れていない、選択できるフォルダーを列挙します。
    /// </summary>
    /// <returns>選択できるフォルダー。</returns>
    internal IEnumerable<FolderMatch> SelectableFolders()
    {
        return CurrentItems().Where(item => !item.IsHidden && !item.IsFilterHidden);
    }

    /// <summary>
    ///     選択できるフォルダーの選択状態を反転します。
    /// </summary>
    internal void InvertSelection()
    {
        foreach (var item in SelectableFolders())
            item.SetSelected(!item.IsSelected);
        SyncAllFoldersSelectedFlag();
    }

    /// <summary>
    ///     現在のツリーのフォルダーをすべて展開します。
    /// </summary>
    internal void ExpandAll()
    {
        foreach (var item in CurrentItems()) item.IsExpanded = true;
    }

    /// <summary>
    ///     現在のツリーのフォルダーをすべて折りたたみます。
    /// </summary>
    internal void CollapseAll()
    {
        foreach (var item in CurrentItems()) item.IsExpanded = false;
    }

    /// <summary>
    ///     表示するツリー (Physical、Solution、Search) を切り替え、絞り込みと件数を更新します。
    /// </summary>
    /// <param name="view">0 が Physical、1 が Solution、2 が Search。</param>
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
        SetStatus(view == 0 ? "Main_FoldersFound" : view == 1 ? "Main_SolutionsFound" : "Main_SearchResults",
            view == 0 ? _results.Count : view == 1 ? _solutionRoots.Count : _searchResultCount);
        if (view == 1) _ = EnsureSolutionViewAsync();
    }

    /// <summary>
    ///     すべてのフォルダーが選択されているかどうかのフラグを更新します。
    /// </summary>
    internal void SyncAllFoldersSelectedFlag()
    {
        var items = SelectableFolders().ToArray();
        var all = items.Any() && items.All(item => item.IsSelected);
        if (_allFoldersSelected != all)
        {
            _allFoldersSelected = all;
            OnPropertyChanged(nameof(AllFoldersSelected));
        }

        RefreshScopedLabel();
    }

    /// <summary>
    ///     現在のタブに対応するツリーを表示元に設定し直します。
    /// </summary>
    internal void RefreshTreeItemsSource()
    {
        TreeItems = TreeViewIndex == 0 ? _treeRoots : TreeViewIndex == 1 ? _solutionRoots : _searchRoots;
    }

    /// <summary>
    ///     表示中のフォルダー (または項目) の件数表示を更新します。
    /// </summary>
    internal void UpdateVisibleCount()
    {
        SetCountLabel(_solutionView ? "Main_NItems" : "Main_NFolders",
            CurrentItems().Count(item => !item.IsHidden && !item.IsFilterHidden));
    }

    /// <summary>
    ///     現在のツリーの全フォルダーを取得します。絞り込み中は絞り込み後のフォルダーです。
    /// </summary>
    /// <returns>フォルダーの一覧。</returns>
    internal IEnumerable<FolderMatch> CurrentItems()
    {
        return _filteredViewItems ??
               Flatten(TreeViewIndex == 0 ? _treeRoots : TreeViewIndex == 1 ? _solutionRoots : _searchRoots).ToList();
    }

    /// <summary>
    ///     現在のツリーで表示されているフォルダー (非表示でなく、親が展開されている) を列挙します。
    /// </summary>
    /// <returns>表示中のフォルダー。</returns>
    internal IEnumerable<FolderMatch> VisibleItems()
    {
        return _filteredViewItems != null
            ? _filteredViewItems.Where(item => !item.IsHidden)
            : FlattenVisible(TreeViewIndex == 0 ? _treeRoots : TreeViewIndex == 1 ? _solutionRoots : _searchRoots);
    }

    /// <summary>
    ///     子ノードに親ノードを再帰的に設定します。
    /// </summary>
    /// <param name="node">起点のノード。</param>
    internal static void AssignParents(FolderMatch node)
    {
        if (node == null) return;
        foreach (var child in node.Children)
        {
            child.Parent = node;
            AssignParents(child);
        }
    }

    /// <summary>
    ///     ツリーを深さ優先で平坦化して列挙します。
    /// </summary>
    /// <param name="roots">ルート ノード。</param>
    /// <returns>すべてのノード。</returns>
    internal static IEnumerable<FolderMatch> Flatten(IEnumerable<FolderMatch> roots)
    {
        foreach (var item in roots)
        {
            yield return item;
            foreach (var child in Flatten(item.Children)) yield return child;
        }
    }

    /// <summary>
    ///     非表示のノードを除き、展開されているノードの子だけをたどって列挙します。
    /// </summary>
    /// <param name="roots">ルート ノード。</param>
    /// <returns>表示中のノード。</returns>
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

    /// <summary>
    ///     検索結果のフォルダーをバックグラウンドで親子関係のツリーに組み立て、Physical または Search のツリーに追加します。
    /// </summary>
    /// <param name="items">追加するフォルダー。</param>
    /// <param name="token">キャンセル トークン。</param>
    /// <param name="intoSearch">Search タブに追加する場合は true。</param>
    internal async Task AddTreeResultsAsync(IEnumerable<FolderMatch> items, CancellationToken token,
        bool intoSearch = false)
    {
        var source = items.Where(item => !IsDroppedTreePath(item.Path)).ToList();
        SetStatus("Main_BuildingRows", source.Count.ToString("N0"));
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
            SetCountLabel("Main_SearchResults", _searchResultCount);
            return;
        }

        _results.Clear();
        foreach (var item in built.Items) _results.Add(item);
        _treeRoots.Clear();
        foreach (var root in built.Roots) _treeRoots.Add(root);
        RefreshTreeItemsSource();
        SetCountLabel("Main_NFolders", _results.Count);
    }

    /// <summary>
    ///     選択中のフォルダーを非表示にします。
    /// </summary>
    internal void HideSelected()
    {
        foreach (var item in CurrentItems())
            if (item.IsSelected)
                item.IsHidden = true;
        UpdateVisibleCount();
    }

    /// <summary>
    ///     非表示にしたフォルダーをすべて表示に戻します。
    /// </summary>
    internal void Unhide()
    {
        foreach (var item in CurrentItems())
            item.IsHidden = false;
        UpdateVisibleCount();
    }

    /// <summary>
    ///     現在のタブのツリーのルートを取得します。
    /// </summary>
    /// <returns>ルート ノードのコレクション。</returns>
    internal ObservableCollection<FolderMatch> CurrentTreeRoots()
    {
        if (TreeViewIndex == 1) return _solutionRoots;
        if (TreeViewIndex == 2) return _searchRoots;
        return _treeRoots;
    }

    /// <summary>
    ///     指定フォルダー、またはその最も近い祖先に対応するノードを探します。
    /// </summary>
    /// <param name="roots">探索するルート ノード。</param>
    /// <param name="directory">フォルダーのパス。</param>
    /// <returns>見つかったノード。見つからない場合は null。</returns>
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

    /// <summary>
    ///     フォルダー名の絞り込みを、前回の処理を取り消してからバックグラウンドで適用します。
    /// </summary>
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

    /// <summary>
    ///     選択中のフォルダーに desktop.ini でアイコンを設定し、必要に応じて .gitignore に追加します。
    /// </summary>
    internal void Apply()
    {
        var selected = VisibleItems().Where(item => item.IsActionable && item.IsSelected)
            .GroupBy(item => item.Path, StringComparer.OrdinalIgnoreCase)
            .Select(group => group.First()).ToList();
        var addToGitIgnore = AddToGitIgnore == true;
        if (selected.Count == 0)
        {
            _dialogs.Show(StringOverlay.Get("Main_SelectOneFolder"), StringOverlay.Get("App_Title"));
            return;
        }

        if (_dialogs.Show(string.Format(StringOverlay.Get("Main_ConfirmApply"), selected.Count), StringOverlay.Get("App_Title"),
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

        SetStatus("Main_AppliedTo", succeeded);
        _dialogs.Show(
            errors.Count == 0
                ? StringOverlay.Get("Main_ApplyOk")
                : string.Format(StringOverlay.Get("Main_ApplyResult"), succeeded, errors.Count) + "\n\n" +
                  string.Join("\n", errors.Take(5)), StringOverlay.Get("App_Title"));
    }

    /// <summary>
    ///     選択中のフォルダーのカスタム アイコンをリセットします。
    /// </summary>
    internal void Remove()
    {
        var selected = VisibleItems().Where(item => item.IsActionable && item.IsSelected)
            .GroupBy(item => item.Path, StringComparer.OrdinalIgnoreCase)
            .Select(group => group.First()).ToList();
        if (selected.Count == 0)
        {
            _dialogs.Show(StringOverlay.Get("Main_SelectOneFolder"), StringOverlay.Get("App_Title"));
            return;
        }

        if (_dialogs.Show(string.Format(StringOverlay.Get("Main_ConfirmRemove"), selected.Count), StringOverlay.Get("App_Title"),
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

        SetStatus("Main_RemovedFrom", succeeded);
        _dialogs.Show(
            errors.Count == 0
                ? StringOverlay.Get("Main_RemoveOk")
                : string.Format(StringOverlay.Get("Main_RemoveResult"), succeeded, errors.Count) + "\n\n" +
                  string.Join("\n", errors.Take(5)), StringOverlay.Get("App_Title"));
    }

    /// <summary>
    ///     ドロップされたフォルダーに対応するツリーのノードを選択します。
    /// </summary>
    /// <param name="folders">ドロップされたフォルダー。</param>
    /// <returns>選択したノードの数。</returns>
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

    /// <summary>
    ///     ソリューションのルート ノードにソリューションのアイコンを設定します。
    /// </summary>
    /// <param name="roots">ソリューションのルート ノード。</param>
    internal static void ApplySolutionRootIcons(IEnumerable<FolderMatch> roots)
    {
        var icon = DifferencerStatusIcons.GetSolutionIcon();
        if (icon == null || roots == null) return;
        foreach (var root in roots)
            if (root != null)
                root.IconPreview = icon;
    }

    /// <summary>
    ///     保存されたフォルダー ツリーを復元します。
    /// </summary>
    internal void RestoreFolderTrees()
    {
        try
        {
            if (_startup?.TreeError != null)
            {
                SetStatus("Main_RestoreTreesFailed", _startup.TreeError);
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
            SetStatus("Main_TreesRestored");
        }
        catch (Exception ex)
        {
            SetStatus("Main_RestoreTreesFailed", ErrorMessages.English(ex));
        }
    }

    /// <summary>
    ///     完成したフォルダー ツリーを保存します。再構築中やキャンセルされた状態では、前回の完全なツリーを上書きしません。
    /// </summary>
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
            SetStatus("Main_SaveTreesFailed", ErrorMessages.English(ex));
        }
    }
}