using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Threading;
using DesktopIniManager.Properties;
using DesktopIniManager.Services;

namespace DesktopIniManager.ViewModels;

internal sealed partial class DeveloperDifferencerViewModel
{
    private bool changingHistoryTabs;
    private Dictionary<string, HistoryFolderState> restoringFolders;
    private ComparisonHistoryTab selectedHistoryTab;
    public ObservableCollection<ComparisonHistoryTab> HistoryTabs { get; } = new();

    public ComparisonHistoryTab SelectedHistoryTab
    {
        get => selectedHistoryTab;
        set
        {
            if (!changingHistoryTabs && !IsBusy && value != null && value != selectedHistoryTab)
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
        if (selectedHistoryTab == null || Snapshot == null) return;
        selectedHistoryTab.Snapshot = Snapshot;
        selectedHistoryTab.Expanded = folders.Values.Where(f => f.Expanded).Select(f => f.Path).ToList();
        selectedHistoryTab.SelectedFolder = selectedFolder;
        selectedHistoryTab.KindMask = kindMask;
        selectedHistoryTab.ShowObj = showObj;
        selectedHistoryTab.ShowBin = showBin;
        selectedHistoryTab.Status = Status;
        selectedHistoryTab.FolderStates = folders.Values.Select(f => new HistoryFolderState
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
        if (dialogs.Show(StringOverlay.Get("History_ComparisonLimit"), StringOverlay.Get("History_ComparisonTitle"),
                MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return false;
        var oldest = HistoryTabs.OrderBy(t => t.CreatedAt).First();
        changingHistoryTabs = true;
        try
        {
            HistoryTabs.Remove(oldest);
        }
        finally
        {
            changingHistoryTabs = false;
        }

        if (oldest == selectedHistoryTab)
        {
            selectedHistoryTab = null;
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
        selectedHistoryTab = tab;
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
        selectedHistoryTab = tab;
        compareCts?.Dispose();
        var cancellation = compareCts = new CancellationTokenSource();
        var token = cancellation.Token;
        comparing = true;
        SetBusy(true);
        try
        {
            await Dispatcher.Yield(DispatcherPriority.Background);
            token.ThrowIfCancellationRequested();
            // Assign backing fields so restoring roots does not clear the selected snapshot.
            _sourcePath = DisplayRoot(tab.Snapshot.SourceRoot);
            _targetPath = DisplayRoot(tab.Snapshot.TargetRoot);
            rootLinkVersion++;
            // A historical snapshot does not need a filesystem scan. Compare refreshes indexes.
            OnPropertyChanged(nameof(SourcePath));
            OnPropertyChanged(nameof(TargetPath));
            Snapshot = tab.Snapshot;
            treeSource = SourcePath;
            treeTarget = TargetPath;
            disabledIgnoreRules =
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
            kindMask = tab.KindMask;
            showObj = tab.ShowObj;
            showBin = tab.ShowBin;
            ShowObj = showObj;
            ShowBin = showBin;
            ShowSame = (kindMask & DiffKind.Same) != 0;
            ShowDifferent = (kindMask & DiffKind.Different) != 0;
            ShowSourceOnly = (kindMask & DiffKind.SourceOnly) != 0;
            ShowTargetOnly = (kindMask & DiffKind.TargetOnly) != 0;
            rows = new List<DiffRow>(Snapshot.Files.Count);
            foreach (var file in Snapshot.Files)
            {
                token.ThrowIfCancellationRequested();
                rows.Add(new DiffRow
                    { File = file, SourceRoot = Snapshot.SourceRoot, TargetRoot = Snapshot.TargetRoot });
                if (rows.Count % 256 == 0) await Dispatcher.Yield(DispatcherPriority.Background);
            }

            restoringFolders = new Dictionary<string, HistoryFolderState>(StringComparer.OrdinalIgnoreCase);
            foreach (var folder in tab.FolderStates)
            {
                token.ThrowIfCancellationRequested();
                restoringFolders.Add(folder.Path, folder);
                if (restoringFolders.Count % 256 == 0) await Dispatcher.Yield(DispatcherPriority.Background);
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
            selectedHistoryTab = null;
            ClearComparisonView();
            folders.Clear();
            Status = "Compare cancelled";
        }
        catch
        {
            selectedHistoryTab = null;
            ClearComparisonView();
            folders.Clear();
            throw;
        }
        finally
        {
            restoringFolders = null;
            comparing = false;
            IsProgressVisible = false;
            ProgressIndeterminate = false;
            if (ReferenceEquals(compareCts, cancellation)) compareCts = null;
            cancellation.Dispose();
            SetBusy(false);
        }
    }

    private async void DeleteHistoryTab(object item)
    {
        var tab = item as ComparisonHistoryTab;
        if (IsBusy || tab == null || !HistoryTabs.Contains(tab)) return;
        var active = tab == selectedHistoryTab;
        changingHistoryTabs = true;
        try
        {
            HistoryTabs.Remove(tab);
        }
        finally
        {
            changingHistoryTabs = false;
        }

        if (active)
        {
            selectedHistoryTab = null;
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
        changingHistoryTabs = true;
        try
        {
            HistoryTabs.Clear();
        }
        finally
        {
            changingHistoryTabs = false;
        }

        selectedHistoryTab = null;
        ClearComparisonView();
        NotifyHistory();
        SaveHistory();
    }

    private async void DeleteOtherHistoryTabs(object item)
    {
        var tab = item as ComparisonHistoryTab;
        if (IsBusy || tab == null || !HistoryTabs.Contains(tab) || HistoryTabs.Count <= 1) return;
        changingHistoryTabs = true;
        try
        {
            for (var i = HistoryTabs.Count - 1; i >= 0; i--)
                if (!ReferenceEquals(HistoryTabs[i], tab))
                    HistoryTabs.RemoveAt(i);
        }
        finally
        {
            changingHistoryTabs = false;
        }

        if (selectedHistoryTab != tab)
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
                    { Tabs = HistoryTabs.ToList(), SelectedIndex = HistoryTabs.IndexOf(selectedHistoryTab) });
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
            if (closed) return;
            changingHistoryTabs = true;
            try
            {
                foreach (var tab in saved.Tabs.Where(t => t.Snapshot != null).OrderByDescending(t => t.CreatedAt)
                             .Take(ResultHistoryStore.Limit)) HistoryTabs.Add(tab);
            }
            finally
            {
                changingHistoryTabs = false;
            }

            // Opening the window only restores tab labels. Select a tab to display its snapshot.
            selectedHistoryTab = null;
            NotifyHistory();
        }
        catch (Exception ex)
        {
            ShowError(ex);
        }
    }
}