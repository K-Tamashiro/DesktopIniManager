using System;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using DesktopIniManager.Properties;

namespace DesktopIniManager.ViewModels
{
    internal sealed partial class DeveloperDifferencerViewModel
    {
        public ObservableCollection<ComparisonHistoryTab> HistoryTabs { get; } = new ObservableCollection<ComparisonHistoryTab>();
        private ComparisonHistoryTab selectedHistoryTab;
        private bool changingHistoryTabs;
        private System.Collections.Generic.Dictionary<string, HistoryFolderState> restoringFolders;
        public ComparisonHistoryTab SelectedHistoryTab
        {
            get => selectedHistoryTab;
            set { if (!changingHistoryTabs && !IsBusy && value != null && value != selectedHistoryTab) SelectHistoryTab(value); }
        }
        public ParameterCommand DeleteHistoryTabCommand { get; private set; }
        public RelayCommand DeleteAllHistoryTabsCommand { get; private set; }
        public ParameterCommand DeleteOtherHistoryTabsCommand { get; private set; }
        public string HistoryRoots => snapshot == null ? "" : HistorySourceRoot + "   " + HistoryTargetRoot;
        public string HistorySourceRoot => snapshot == null ? "" : "Source: …" + RootName(snapshot.SourceRoot);
        public string HistoryTargetRoot => snapshot == null ? "" : "Target: …" + RootName(snapshot.TargetRoot);
        public string HistoryRootsTooltip => snapshot == null ? "" : "Source: " + snapshot.SourceRoot + "\nTarget: " + snapshot.TargetRoot;
        private static string RootName(string path) => System.IO.Path.GetFileName(DisplayRoot(path));

        // Snapshot roots keep a trailing separator for SafePath; the input boxes and
        // path history must not, or "C:\foo" and "C:\foo\" become two history entries.
        internal static string DisplayRoot(string path)
        {
            if (string.IsNullOrWhiteSpace(path)) return path ?? "";
            string trimmed = path.TrimEnd('\\', '/');
            if (trimmed.Length == 2 && trimmed[1] == ':') return trimmed + "\\";
            return trimmed;
        }

        private void CaptureHistoryTab()
        {
            if (selectedHistoryTab == null || snapshot == null) return;
            selectedHistoryTab.Snapshot = snapshot;
            selectedHistoryTab.Expanded = folders.Values.Where(f => f.Expanded).Select(f => f.Path).ToList();
            selectedHistoryTab.SelectedFolder = selectedFolder;
            selectedHistoryTab.KindMask = kindMask;
            selectedHistoryTab.ShowObj = showObj; selectedHistoryTab.ShowBin = showBin;
            selectedHistoryTab.Status = Status;
            selectedHistoryTab.FolderStates = folders.Values.Select(f => new HistoryFolderState { Path = f.Path,
                SourceExists = f.SourceExists, TargetExists = f.TargetExists, SourceEmpty = f.SourceEmpty, TargetEmpty = f.TargetEmpty }).ToList();
        }
        private bool PrepareComparisonTab()
        {
            if (HistoryTabs.Count < ResultHistoryStore.Limit) return true;
            if (dialogs.Show(StringOverlay.Get("History_ComparisonLimit"), StringOverlay.Get("History_ComparisonTitle"), MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return false;
            var oldest = HistoryTabs.OrderBy(t => t.CreatedAt).First();
            changingHistoryTabs = true;
            try { HistoryTabs.Remove(oldest); }
            finally { changingHistoryTabs = false; }
            if (oldest == selectedHistoryTab) { selectedHistoryTab = null; OnPropertyChanged(nameof(SelectedHistoryTab)); }
            return true;
        }
        private void AddComparisonTab()
        {
            var now = DateTime.Now;
            var tab = new ComparisonHistoryTab { CreatedAt = now, Title = now.ToString("yyyy.MM.dd HH:mm:ss"), Snapshot = snapshot };
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
            try { await SelectHistoryTabAsync(tab); }
            catch (Exception ex) { ShowError(ex); }
        }
        internal async Task SelectHistoryTabAsync(ComparisonHistoryTab tab)
        {
            if (IsBusy || tab == null) return;
            CaptureHistoryTab();
            ClearComparisonView();
            selectedHistoryTab = tab;
            SetBusy(true);
            try
            {
                // Assign backing fields so restoring roots does not clear the selected snapshot.
                _sourcePath = DisplayRoot(tab.Snapshot.SourceRoot); _targetPath = DisplayRoot(tab.Snapshot.TargetRoot);
                if (sourceIndex == null || !string.Equals(sourceIndex.Path, _sourcePath, StringComparison.OrdinalIgnoreCase)) RestartIndex(true);
                if (targetIndex == null || !string.Equals(targetIndex.Path, _targetPath, StringComparison.OrdinalIgnoreCase)) RestartIndex(false);
                OnPropertyChanged(nameof(SourcePath)); OnPropertyChanged(nameof(TargetPath));
                snapshot = tab.Snapshot; treeSource = SourcePath; treeTarget = TargetPath;
                CompareTimestamp = snapshot.CompareTimestamp;
                kindMask = tab.KindMask; showObj = tab.ShowObj; showBin = tab.ShowBin;
                ShowObj = showObj; ShowBin = showBin;
                ShowSame = (kindMask & Services.DiffKind.Same) != 0;
                ShowDifferent = (kindMask & Services.DiffKind.Different) != 0;
                ShowSourceOnly = (kindMask & Services.DiffKind.SourceOnly) != 0;
                ShowTargetOnly = (kindMask & Services.DiffKind.TargetOnly) != 0;
                rows = snapshot.Files.Select(f => new DiffRow { File = f, SourceRoot = snapshot.SourceRoot, TargetRoot = snapshot.TargetRoot }).ToList();
                restoringFolders = tab.FolderStates.ToDictionary(f => f.Path, StringComparer.OrdinalIgnoreCase);
                await BuildTreeAsync(snapshot.Folders, tab.Expanded, tab.SelectedFolder, CancellationToken.None);
                Status = tab.Status;
                NotifyHistory();
            }
            finally { restoringFolders = null; SetBusy(false); }
        }
        private async void DeleteHistoryTab(object item)
        {
            var tab = item as ComparisonHistoryTab;
            if (IsBusy || tab == null || !HistoryTabs.Contains(tab)) return;
            if (dialogs.Show(string.Format(StringOverlay.Get("History_DeleteConfirm"), tab.Title), StringOverlay.Get("History_ComparisonTitle"), MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;
            bool active = tab == selectedHistoryTab;
            changingHistoryTabs = true;
            try { HistoryTabs.Remove(tab); }
            finally { changingHistoryTabs = false; }
            if (active)
            {
                selectedHistoryTab = null;
                ClearComparisonView();
                if (HistoryTabs.Count > 0)
                {
                    try { await SelectHistoryTabAsync(HistoryTabs.First()); }
                    catch (Exception ex) { ShowError(ex); }
                }
                NotifyHistory();
            }
            SaveHistory();
        }
        private void DeleteAllHistoryTabs()
        {
            if (IsBusy || HistoryTabs.Count == 0) return;
            if (dialogs.Show(StringOverlay.Get("History_DeleteAllConfirm"), StringOverlay.Get("History_ComparisonTitle"), MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;
            changingHistoryTabs = true;
            try { HistoryTabs.Clear(); }
            finally { changingHistoryTabs = false; }
            selectedHistoryTab = null;
            ClearComparisonView();
            NotifyHistory();
            SaveHistory();
        }
        private async void DeleteOtherHistoryTabs(object item)
        {
            var tab = item as ComparisonHistoryTab;
            if (IsBusy || tab == null || !HistoryTabs.Contains(tab) || HistoryTabs.Count <= 1) return;
            if (dialogs.Show(string.Format(StringOverlay.Get("History_DeleteOthersConfirm"), tab.Title), StringOverlay.Get("History_ComparisonTitle"), MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;
            changingHistoryTabs = true;
            try
            {
                for (int i = HistoryTabs.Count - 1; i >= 0; i--)
                    if (!ReferenceEquals(HistoryTabs[i], tab)) HistoryTabs.RemoveAt(i);
            }
            finally { changingHistoryTabs = false; }
            if (selectedHistoryTab != tab)
            {
                try { await SelectHistoryTabAsync(tab); }
                catch (Exception ex) { ShowError(ex); }
            }
            SaveHistory();
        }
        internal void SaveHistory()
        {
            try
            {
                CaptureHistoryTab();
                ResultHistoryStore.Save("comparison", new HistoryFile<ComparisonHistoryTab> { Tabs = HistoryTabs.ToList(), SelectedIndex = HistoryTabs.IndexOf(selectedHistoryTab) });
            }
            catch (Exception ex) { ShowError(ex); }
        }
        internal async Task RestoreHistoryAsync()
        {
            try
            {
                var saved = ResultHistoryStore.Load<HistoryFile<ComparisonHistoryTab>>("comparison");
                foreach (var tab in saved.Tabs.Where(t => t.Snapshot != null).OrderByDescending(t => t.CreatedAt).Take(ResultHistoryStore.Limit)) HistoryTabs.Add(tab);
                if (HistoryTabs.Count > 0) await SelectHistoryTabAsync(HistoryTabs[0]);
            }
            catch (Exception ex) { ShowError(ex); }
        }
    }
}
