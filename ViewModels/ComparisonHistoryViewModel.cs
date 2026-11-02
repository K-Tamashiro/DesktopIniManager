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

    private static string RootName(string path)
    {
        return Path.GetFileName(DisplayRoot(path));
    }

    // Snapshot roots keep a trailing separator for SafePath; the input boxes and
    // path history must not, or "C:\foo" and "C:\foo\" become two history entries.
    internal static string DisplayRoot(string path)
    {
        if (string.IsNullOrWhiteSpace(path)) return path ?? "";
        var trimmed = path.TrimEnd('\\', '/');
        if (trimmed.Length == 2 && trimmed[1] == ':') return trimmed + "\\";
        return trimmed;
    }

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

    private void NotifyHistory()
    {
        OnPropertyChanged(nameof(SelectedHistoryTab));
        OnPropertyChanged(nameof(HistoryRoots));
        OnPropertyChanged(nameof(HistorySourceRoot));
        OnPropertyChanged(nameof(HistoryTargetRoot));
        OnPropertyChanged(nameof(HistoryRootsTooltip));
    }

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