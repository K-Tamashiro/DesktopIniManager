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
using System.Windows.Threading;

namespace DesktopIniManager.ViewModels;

internal sealed partial class DeveloperDifferencerViewModel
{
    private bool _changingHistoryTabs;
    private Dictionary<string, HistoryFolderState> _restoringFolders;
    private ComparisonHistoryTab _selectedHistoryTab;
    public ObservableCollection<ComparisonHistoryTab> HistoryTabs { get; } = new();

    public ComparisonHistoryTab SelectedHistoryTab
    {
        get => _selectedHistoryTab;
        set
        {
            if (!_changingHistoryTabs && !IsBusy && value != null && value != _selectedHistoryTab)
                SelectHistoryTab(value);
        }
    }

    public ParameterCommand DeleteHistoryTabCommand { get; }
    public RelayCommand DeleteAllHistoryTabsCommand { get; }
    public ParameterCommand DeleteOtherHistoryTabsCommand { get; }
    public string HistoryRoots => Snapshot == null ? "" : HistorySourceRoot + "   " + HistoryTargetRoot;
    public string HistorySourceRoot => Snapshot == null ? "" : "Source: \u2026" + RootName(Snapshot.SourceRoot);
    public string HistoryTargetRoot => Snapshot == null ? "" : "Target: \u2026" + RootName(Snapshot.TargetRoot);

    public string HistoryRootsTooltip =>
        Snapshot == null ? "" : "Source: " + Snapshot.SourceRoot + "\nTarget: " + Snapshot.TargetRoot;

    /// <summary>
    ///     比較ルートのフォルダー名を取得します。
    /// </summary>
    /// <param name="path">比較ルートのパス。</param>
    /// <returns>フォルダー名。</returns>
    private static string RootName(string path)
    {
        return Path.GetFileName(DisplayRoot(path));
    }

    // Snapshot roots keep a trailing separator for SafePath; the input boxes and
    // path history must not, or "C:\foo" and "C:\foo\" become two history entries.
    /// <summary>
    ///     比較ルートを表示用に整えます。末尾の区切り文字を取り除き、ドライブ ルートは「C:\」の形にします。
    ///     スナップショットのルートは末尾に区切り文字を持ちますが、入力欄と履歴で同じパスが 2 件にならないようにするためです。
    /// </summary>
    /// <param name="path">比較ルートのパス。</param>
    /// <returns>表示用のパス。</returns>
    internal static string DisplayRoot(string path)
    {
        if (string.IsNullOrWhiteSpace(path)) return path ?? "";
        var trimmed = path.TrimEnd('\\', '/');
        if (trimmed.Length == 2 && trimmed[1] == ':') return trimmed + "\\";
        return trimmed;
    }

    /// <summary>
    ///     現在の比較結果と表示状態 (展開フォルダー、選択フォルダー、フィルター、状態) を選択中の履歴タブに保存します。
    /// </summary>
    private void CaptureHistoryTab()
    {
        if (_selectedHistoryTab == null || Snapshot == null) return;
        _selectedHistoryTab.Snapshot = Snapshot;
        _selectedHistoryTab.Expanded = _folders.Values.Where(f => f.Expanded).Select(f => f.Path).ToList();
        _selectedHistoryTab.SelectedFolder = _selectedFolder;
        _selectedHistoryTab.KindMask = _kindMask;
        _selectedHistoryTab.ShowObj = _appliedShowObj;
        _selectedHistoryTab.ShowBin = _appliedShowBin;
        _selectedHistoryTab.Status = Status;
        _selectedHistoryTab.FolderStates = _folders.Values.Select(f => new HistoryFolderState
        {
            Path = f.Path,
            SourceExists = f.SourceExists,
            TargetExists = f.TargetExists,
            SourceEmpty = f.SourceEmpty,
            TargetEmpty = f.TargetEmpty
        }).ToList();
    }

    /// <summary>
    ///     履歴タブが上限に達している場合、確認のうえ最も古いタブを削除して新しいタブの空きを作ります。
    /// </summary>
    /// <returns>新しいタブを追加できる場合は true。利用者が取り消した場合は false。</returns>
    private bool PrepareComparisonTab()
    {
        if (HistoryTabs.Count < ResultHistoryStore.Limit) return true;
        if (_dialogs.Show(StringOverlay.Get("History_ComparisonLimit"), StringOverlay.Get("History_ComparisonTitle"),
                MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return false;
        var oldest = HistoryTabs.OrderBy(t => t.CreatedAt).First();
        _changingHistoryTabs = true;
        try
        {
            HistoryTabs.Remove(oldest);
        }
        finally
        {
            _changingHistoryTabs = false;
        }

        if (oldest == _selectedHistoryTab)
        {
            _selectedHistoryTab = null;
            OnPropertyChanged(nameof(SelectedHistoryTab));
        }

        return true;
    }

    /// <summary>
    ///     現在の比較結果を、作成日時をタイトルとする新しい履歴タブとして先頭に追加します。
    /// </summary>
    private void AddComparisonTab()
    {
        var now = DateTime.Now;
        var tab = new ComparisonHistoryTab
        { CreatedAt = now, Title = now.ToString("yyyy.MM.dd HH:mm:ss"), Snapshot = Snapshot };
        HistoryTabs.Insert(0, tab);
        _selectedHistoryTab = tab;
        CaptureHistoryTab();
        NotifyHistory();
    }

    /// <summary>
    ///     履歴タブに関係するプロパティの変更を通知します。
    /// </summary>
    private void NotifyHistory()
    {
        OnPropertyChanged(nameof(SelectedHistoryTab));
        OnPropertyChanged(nameof(HistoryRoots));
        OnPropertyChanged(nameof(HistorySourceRoot));
        OnPropertyChanged(nameof(HistoryTargetRoot));
        OnPropertyChanged(nameof(HistoryRootsTooltip));
    }

    /// <summary>
    ///     履歴タブを選択して表示します。発生した例外はエラー表示します。
    /// </summary>
    /// <param name="tab">選択する履歴タブ。</param>
    private async void SelectHistoryTab(ComparisonHistoryTab tab)
    {
        try
        {
            await SelectHistoryTabAsync(tab);
        }
        catch (Exception ex)
        {
            ShowError(ex);
        }
    }

    /// <summary>
    ///     現在のタブの状態を保存してから、指定した履歴タブの比較結果と表示状態を復元します。ファイル システムは再走査しません。
    /// </summary>
    /// <param name="tab">選択する履歴タブ。</param>
    /// <param name="resetView">保存された表示状態を使わずに初期表示にする場合は true。</param>
    internal async Task SelectHistoryTabAsync(ComparisonHistoryTab tab, bool resetView = false)
    {
        if (IsBusy || tab == null) return;
        CaptureHistoryTab();
        ClearComparisonView();
        _selectedHistoryTab = tab;
        _compareCts?.Dispose();
        var cancellation = _compareCts = new CancellationTokenSource();
        var token = cancellation.Token;
        _comparing = true;
        SetBusy(true);
        try
        {
            await Dispatcher.Yield(DispatcherPriority.Background);
            token.ThrowIfCancellationRequested();
            // Assign backing fields so restoring roots does not clear the selected snapshot.
            _sourcePath = DisplayRoot(tab.Snapshot.SourceRoot);
            _targetPath = DisplayRoot(tab.Snapshot.TargetRoot);
            _rootLinkVersion++;
            // A historical snapshot does not need a filesystem scan. Compare refreshes indexes.
            OnPropertyChanged(nameof(SourcePath));
            OnPropertyChanged(nameof(TargetPath));
            Snapshot = tab.Snapshot;
            _treeSource = SourcePath;
            _disabledIgnoreRules =
                new HashSet<string>(Snapshot.DisabledIgnoreRules ?? new List<string>(), StringComparer.Ordinal);
            InvalidateIndexes();
            if (Snapshot.IgnoreRules == null)
            {
                var savedSnapshot = Snapshot;
                var rules = await Task.Run(() => GitIgnoreMatcher.Merge(
                        GitIgnoreMatcher.Discover(savedSnapshot.SourceRoot, savedSnapshot.DisabledIgnoreRules, token),
                        GitIgnoreMatcher.Discover(savedSnapshot.TargetRoot, savedSnapshot.DisabledIgnoreRules, token)),
                    token);
                token.ThrowIfCancellationRequested();
                savedSnapshot.IgnoreRules = rules;
            }

            CompareTimestamp = Snapshot.CompareTimestamp;
            CompareSize = Snapshot.CompareSize;
            CompareAttributes = Snapshot.CompareAttributes;
            PreciseCompare = Snapshot.PreciseCompare;
            _kindMask = tab.KindMask;
            _appliedShowObj = tab.ShowObj;
            _appliedShowBin = tab.ShowBin;
            ShowObj = _appliedShowObj;
            ShowBin = _appliedShowBin;
            ShowSame = (_kindMask & DiffKind.Same) != 0;
            ShowDifferent = (_kindMask & DiffKind.Different) != 0;
            ShowSourceOnly = (_kindMask & DiffKind.SourceOnly) != 0;
            ShowTargetOnly = (_kindMask & DiffKind.TargetOnly) != 0;
            _rows = new List<DiffRow>(Snapshot.Files.Count);
            foreach (var file in Snapshot.Files)
            {
                token.ThrowIfCancellationRequested();
                _rows.Add(new DiffRow
                { File = file, SourceRoot = Snapshot.SourceRoot, TargetRoot = Snapshot.TargetRoot });
                if (_rows.Count % 256 == 0) await Dispatcher.Yield(DispatcherPriority.Background);
            }

            _restoringFolders = new Dictionary<string, HistoryFolderState>(StringComparer.OrdinalIgnoreCase);
            foreach (var folder in tab.FolderStates)
            {
                token.ThrowIfCancellationRequested();
                _restoringFolders.Add(folder.Path, folder);
                if (_restoringFolders.Count % 256 == 0) await Dispatcher.Yield(DispatcherPriority.Background);
            }

            await BuildTreeAsync(Snapshot.Folders, resetView ? Array.Empty<string>() : tab.Expanded,
                resetView ? "" : tab.SelectedFolder, token);
            token.ThrowIfCancellationRequested();
            if (resetView)
            {
                ResetAfterComparison();
                ComparisonCompleted?.Invoke();
            }

            Status = tab.Status;
            NotifyHistory();
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            // Do not capture a partially restored tree over the saved history.
            _selectedHistoryTab = null;
            ClearComparisonView();
            _folders.Clear();
            Status = "Compare cancelled";
        }
        catch
        {
            _selectedHistoryTab = null;
            ClearComparisonView();
            _folders.Clear();
            throw;
        }
        finally
        {
            _restoringFolders = null;
            _comparing = false;
            IsProgressVisible = false;
            ProgressIndeterminate = false;
            if (ReferenceEquals(_compareCts, cancellation)) _compareCts = null;
            cancellation.Dispose();
            SetBusy(false);
        }
    }

    /// <summary>
    ///     履歴タブを削除します。選択中のタブを削除した場合は、残りのタブを選択し直します。
    /// </summary>
    /// <param name="item">削除する履歴タブ。</param>
    private async void DeleteHistoryTab(object item)
    {
        var tab = item as ComparisonHistoryTab;
        if (IsBusy || tab == null || !HistoryTabs.Contains(tab)) return;
        var active = tab == _selectedHistoryTab;
        _changingHistoryTabs = true;
        try
        {
            HistoryTabs.Remove(tab);
        }
        finally
        {
            _changingHistoryTabs = false;
        }

        if (active)
        {
            _selectedHistoryTab = null;
            ClearComparisonView();
            if (HistoryTabs.Count > 0)
                try
                {
                    await SelectHistoryTabAsync(HistoryTabs.First());
                }
                catch (Exception ex)
                {
                    ShowError(ex);
                }

            NotifyHistory();
        }

        SaveHistory();
    }

    /// <summary>
    ///     すべての履歴タブを削除して比較表示をクリアし、履歴を保存します。
    /// </summary>
    private void DeleteAllHistoryTabs()
    {
        if (IsBusy || HistoryTabs.Count == 0) return;
        _changingHistoryTabs = true;
        try
        {
            HistoryTabs.Clear();
        }
        finally
        {
            _changingHistoryTabs = false;
        }

        _selectedHistoryTab = null;
        ClearComparisonView();
        NotifyHistory();
        SaveHistory();
    }

    /// <summary>
    ///     指定したタブ以外の履歴タブを削除し、指定したタブを選択します。
    /// </summary>
    /// <param name="item">残す履歴タブ。</param>
    private async void DeleteOtherHistoryTabs(object item)
    {
        var tab = item as ComparisonHistoryTab;
        if (IsBusy || tab == null || !HistoryTabs.Contains(tab) || HistoryTabs.Count <= 1) return;
        _changingHistoryTabs = true;
        try
        {
            for (var i = HistoryTabs.Count - 1; i >= 0; i--)
                if (!ReferenceEquals(HistoryTabs[i], tab))
                    HistoryTabs.RemoveAt(i);
        }
        finally
        {
            _changingHistoryTabs = false;
        }

        if (_selectedHistoryTab != tab)
            try
            {
                await SelectHistoryTabAsync(tab);
            }
            catch (Exception ex)
            {
                ShowError(ex);
            }

        SaveHistory();
    }

    /// <summary>
    ///     現在の状態を取り込んでから、比較履歴タブを保存します。
    /// </summary>
    internal void SaveHistory()
    {
        try
        {
            CaptureHistoryTab();
            ResultHistoryStore.Save("comparison",
                new HistoryFile<ComparisonHistoryTab>
                { Tabs = HistoryTabs.ToList(), SelectedIndex = HistoryTabs.IndexOf(_selectedHistoryTab) });
        }
        catch (Exception ex)
        {
            ShowError(ex);
        }
    }

    /// <summary>
    ///     保存された比較履歴タブを新しい順に上限まで読み込みます。タブの見出しだけを復元し、比較結果はタブを選んだときに表示します。
    /// </summary>
    internal async Task RestoreHistoryAsync()
    {
        try
        {
            var saved = await Task.Run(() => ResultHistoryStore.Load<HistoryFile<ComparisonHistoryTab>>("comparison"));
            if (_closed) return;
            _changingHistoryTabs = true;
            try
            {
                foreach (var tab in saved.Tabs.Where(t => t.Snapshot != null).OrderByDescending(t => t.CreatedAt)
                             .Take(ResultHistoryStore.Limit)) HistoryTabs.Add(tab);
            }
            finally
            {
                _changingHistoryTabs = false;
            }

            // Opening the window only restores tab labels. Select a tab to display its snapshot.
            _selectedHistoryTab = null;
            NotifyHistory();
        }
        catch (Exception ex)
        {
            ShowError(ex);
        }
    }
}