using System.Text;
using DesktopIniManager.Properties;
using DesktopIniManager.Services;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Threading;
using System.Xml.Serialization;

namespace DesktopIniManager.ViewModels
{
    internal sealed partial class DeveloperDifferencerViewModel : ObservableObject
    {
        internal static string StateDirectory => AppSlot.Directory;
        internal static string StatePath { get; set; } = Path.Combine(StateDirectory, "developer-differencer.xml");
        private DiffSnapshot snapshot;
        private int previewInFlight;
        private readonly SemaphoreSlim previewWorkers = new SemaphoreSlim(2);
        private CancellationTokenSource previewScope = new CancellationTokenSource();
        private bool closed;
        private bool syncingTreeFromFile;
        private bool treeCompact = SettingsService.LoadTreeCompact();
        public bool TreeCompact { get => treeCompact; set => SetProperty(ref treeCompact, value); }
        private DiffRow selectedRow;
        public DiffRow SelectedRow
        {
            get => selectedRow;
            set
            {
                if (!SetProperty(ref selectedRow, value)) return;
                OpenDiffCommand?.NotifyCanExecuteChanged();
                if (!syncingTreeFromFile && !IsBusy && snapshot != null && value != null)
                    RevealContainingFolder(Path.GetDirectoryName(value.File.RelativePath) ?? "");
            }
        }
        public Func<string, string, string> ChooseFolder { get; set; }
        public event Action CloseRequested;
        public event Action<bool> CommitBrowsedRootHistoryRequested;
        public event Action<DiffSnapshot, DiffFile> DiffRequested;
        public event Action<IReadOnlyList<DiffFolder>> FolderRevealRequested;
        public event Action<DiffRow> FileProgressRequested;
        public RelayCommand BrowseSourceCommand { get; }
        public RelayCommand BrowseTargetCommand { get; }
        public RelayCommand CloseCommand { get; }
        public RelayCommand CompactTreeCommand { get; }
        public RelayCommand ComfortableTreeCommand { get; }
        public RelayCommand OpenDiffCommand { get; }
        private readonly Dictionary<string, DiffFolder> folders = new Dictionary<string, DiffFolder>(StringComparer.OrdinalIgnoreCase);
        private List<DiffRow> rows = new List<DiffRow>();
        private ObservableCollection<DiffRow> streamingRows;
        private DiffIndex[] comparisonIndexes;
        private string selectedFolder = "";
        private bool bulk;
        private bool comparing;
        private CancellationTokenSource compareCts;
        private HashSet<string> cachedVisibleFolders;
        private DiffKind kindMask = DiffKind.Differences;
        private bool showObj, showBin;
        private string treeSource, treeTarget;
        public DiffSnapshot Snapshot => snapshot;
        public IReadOnlyDictionary<string, DiffFolder> Folders => folders;
        public void SelectFolder(string path)
        {
            if (syncingTreeFromFile || IsBusy || string.Equals(selectedFolder, path ?? "", StringComparison.OrdinalIgnoreCase)) return;
            selectedFolder = path ?? "";

            // A folder change starts a new Same-selection operation.
            // Reset the toggle state without applying OFF to any files so that
            // the first click in the newly selected folder always means ON.
            if (_selectAllFiles)
            {
                _selectAllFiles = false;
                OnPropertyChanged(nameof(SelectAllFiles));
            }

            Filter();
        }
        private string _sourcePath = string.Empty;
        public string SourcePath { get => _sourcePath; set { ApplyRootPath(true, value, force: false); } }
        private string _targetPath = string.Empty;
        public string TargetPath { get => _targetPath; set { ApplyRootPath(false, value, force: false); } }

        internal void ApplyRootPath(bool source, string path, bool force = false)
        {
            path = path ?? string.Empty;
            string current = source ? SourcePath : TargetPath;
            if (!force && string.Equals(MainWindowViewModel.TryNormalizeFolderPath(current),
                MainWindowViewModel.TryNormalizeFolderPath(path), StringComparison.OrdinalIgnoreCase)) return;
            if (source)
            {
                bool changed = SetProperty(ref _sourcePath, path, nameof(SourcePath));
                if (!changed && !force) return;
                if (!changed) OnPropertyChanged(nameof(SourcePath));
                ClearComparisonView();
                RestartIndex(true);
                if (changed) pendingRootLink = LinkTargetFolderAsync(path, TargetPath);
            }
            else
            {
                bool changed = SetProperty(ref _targetPath, path, nameof(TargetPath));
                if (!changed && !force) return;
                rootLinkVersion++;
                if (!changed) OnPropertyChanged(nameof(TargetPath));
                ClearComparisonView();
                RestartIndex(false);
            }
        }

        private int rootLinkVersion;
        private Task pendingRootLink = Task.CompletedTask;

        private async Task LinkTargetFolderAsync(string source, string target)
        {
            int version = ++rootLinkVersion;
            // Text input is debounced; network lookups must not block the UI thread.
            await Task.Delay(300);
            if (closed || IsBusy || version != rootLinkVersion || SourcePath != source || TargetPath != target) return;
            string match = await Task.Run(() =>
            {
                try
                {
                    if (!Directory.Exists(source) || !Directory.Exists(target)) return null;
                    string sourceRoot = Path.GetFullPath(source);
                    string name = new DirectoryInfo(sourceRoot).Name;
                    if (new DirectoryInfo(sourceRoot).Parent == null) return null;
                    string targetRoot = Path.GetFullPath(target);
                    if (string.Equals(new DirectoryInfo(targetRoot).Name, name, StringComparison.OrdinalIgnoreCase)) return null;
                    // Only select an existing direct child of the current Target.
                    string child = Path.Combine(targetRoot, name);
                    return Directory.Exists(child) ? child : null;
                }
                catch { return null; }
            });
            if (match != null && !closed && !IsBusy && version == rootLinkVersion && SourcePath == source && TargetPath == target)
                ApplyRootPath(false, match);
        }

        private PendingIndex sourceIndex, targetIndex;
        public bool IsSourceIndexing => !closed && sourceIndex != null && !sourceIndex.Task.IsCompleted;
        public bool IsTargetIndexing => !closed && targetIndex != null && !targetIndex.Task.IsCompleted;

        private void NotifyIndexProgress()
        {
            OnPropertyChanged(nameof(IsSourceIndexing));
            OnPropertyChanged(nameof(IsTargetIndexing));
        }

        private async Task ObserveIndexProgressAsync(PendingIndex pending)
        {
            NotifyIndexProgress();
            if (pending == null) return;
            // PendingIndex observes failures; this continuation only updates the activity indicators.
            await pending.Task;
            if (!closed && (ReferenceEquals(sourceIndex, pending) || ReferenceEquals(targetIndex, pending)))
                NotifyIndexProgress();
        }

        private sealed class PendingIndex
        {
            private readonly CancellationTokenSource cancellation = new CancellationTokenSource();
            public Task<DiffIndex> Task { get; }
            public string Path { get; }
            public Exception Error { get; private set; }
            public PendingIndex(string path) { Path = path; Task = BuildAsync(path); }
            public void Cancel()
            {
                try { cancellation.Cancel(); } catch (ObjectDisposedException) { }
            }
            private async Task<DiffIndex> BuildAsync(string path)
            {
                try
                {
                    return await System.Threading.Tasks.Task.Run(
                        () => DeveloperDifferencerService.BuildIndex(path, cancellation.Token), cancellation.Token);
                }
                catch (OperationCanceledException) { return null; }
                catch (Exception ex) { Error = ex; return null; }
                finally { cancellation.Dispose(); }
            }
        }

        private void RestartIndex(bool source)
        {
            compareCts?.Cancel();
            if (source)
            {
                sourceIndex?.Cancel();
                sourceIndex = closed || string.IsNullOrWhiteSpace(SourcePath) ? null : new PendingIndex(SourcePath);
            }
            else
            {
                targetIndex?.Cancel();
                targetIndex = closed || string.IsNullOrWhiteSpace(TargetPath) ? null : new PendingIndex(TargetPath);
            }
            _ = ObserveIndexProgressAsync(source ? sourceIndex : targetIndex);
        }

        internal void RescanRoot(bool source)
        {
            if (!IsBusy)
                ApplyRootPath(source, source ? SourcePath : TargetPath, force: true);
        }

        private void InvalidateIndexes()
        {
            sourceIndex?.Cancel();
            targetIndex?.Cancel();
            sourceIndex = targetIndex = null;
            NotifyIndexProgress();
        }
        private string _status = "Ready";
        public string Status { get => _status; set => SetProperty(ref _status, value); }
        private string _countLabel = string.Empty;
        public string CountLabel { get => _countLabel; set => SetProperty(ref _countLabel, value); }
        private string _filePanelTitle = "Files";
        public string FilePanelTitle { get => _filePanelTitle; set => SetProperty(ref _filePanelTitle, value); }
        private string _busyMessage = "Please wait…";
        public string BusyMessage { get => _busyMessage; set => SetProperty(ref _busyMessage, value); }
        private IEnumerable<DiffFolder> _folderItems = null;
        public IEnumerable<DiffFolder> FolderItems { get => _folderItems; set => SetProperty(ref _folderItems, value); }
        private IEnumerable<DiffRow> _fileItems = null;
        public IEnumerable<DiffRow> FileItems { get => _fileItems; set => SetProperty(ref _fileItems, value); }
        private bool _progressIndeterminate = false;
        public bool ProgressIndeterminate { get => _progressIndeterminate; set => SetProperty(ref _progressIndeterminate, value); }
        private double _progressMaximum = 1;
        public double ProgressMaximum { get => _progressMaximum; set => SetProperty(ref _progressMaximum, value); }
        private double _progressValue = 0;
        public double ProgressValue { get => _progressValue; set => SetProperty(ref _progressValue, value); }
        private bool _compareTimestamp = true;
        public bool CompareTimestamp { get => _compareTimestamp; set => SetProperty(ref _compareTimestamp, value); }
        private bool _showObj = false;
        public bool ShowObj { get => _showObj; set => SetProperty(ref _showObj, value); }
        private bool _showBin = false;
        public bool ShowBin { get => _showBin; set => SetProperty(ref _showBin, value); }
        private bool _showSame = false;
        public bool ShowSame { get => _showSame; set => SetProperty(ref _showSame, value); }
        private bool _showDifferent = true;
        public bool ShowDifferent { get => _showDifferent; set => SetProperty(ref _showDifferent, value); }
        private bool _showSourceOnly = true;
        public bool ShowSourceOnly { get => _showSourceOnly; set => SetProperty(ref _showSourceOnly, value); }
        private bool _showTargetOnly = true;
        public bool ShowTargetOnly { get => _showTargetOnly; set => SetProperty(ref _showTargetOnly, value); }
        private bool _selectAllFiles;
        public bool SelectAllFiles
        {
            get => _selectAllFiles;
            set
            {
                if (!SetProperty(ref _selectAllFiles, value)) return;
                ApplySelectAllFiles(value);
            }
        }
        private bool _isBusy = false;
        public bool IsBusy { get => _isBusy; set => SetProperty(ref _isBusy, value); }
        private bool _isProgressVisible = false;
        public bool IsProgressVisible { get => _isProgressVisible; set => SetProperty(ref _isProgressVisible, value); }
        private bool _isFileBusy = false;
        public bool IsFileBusy { get => _isFileBusy; set => SetProperty(ref _isFileBusy, value); }
        private bool _canFilter = false;
        public bool CanFilter { get => _canFilter; set => SetProperty(ref _canFilter, value); }
        private bool _canRefresh = false;
        public bool CanRefresh { get => _canRefresh; set => SetProperty(ref _canRefresh, value); }
        private bool _canSynchronize = false;
        public bool CanSynchronize { get => _canSynchronize; set => SetProperty(ref _canSynchronize, value); }
        private bool _checkedOnlyFilter;
        public bool CheckedOnlyFilter
        {
            get => _checkedOnlyFilter;
            set
            {
                if (!SetProperty(ref _checkedOnlyFilter, value)) return;
                if (snapshot == null) return;
                if (value)
                {
                    ApplyCheckedOnlyFolderCollapse();
                    Filter();
                }
                else
                    ApplyKindFilter();
            }
        }
        public event Action<int> SelectionCountChanged;
        private bool _canCancel = false;
        public bool CanCancel { get => _canCancel; set { if (SetProperty(ref _canCancel, value)) CancelCommand?.NotifyCanExecuteChanged(); } }

        private readonly IUserDialogService dialogs;
        internal delegate bool ConfirmSyncCallback(string direction, DiffFile[] files, DiffFolderSync[] folders, bool toTarget, bool zipMode, out string zipFileName, out string zipFolder);
        private readonly ConfirmSyncCallback confirmSync;
        private readonly Func<string, DiffSnapshot, ISynchronizationLog> openLog;
        private readonly System.Windows.Threading.Dispatcher dispatcher;
        public bool CanEdit => !IsBusy;
        public event Action ComparisonCleared;
        public event Action CommitRootHistoryRequested;
        public event Action<bool?, bool> SyncDirectionIconRequested;
        public AsyncRelayCommand CompareCommand { get; }
        public AsyncRelayCommand RefreshCommand { get; }
        public AsyncRelayCommand ForwardCommand { get; }
        public AsyncRelayCommand ReverseCommand { get; }
        public AsyncRelayCommand ZipSourceCommand { get; }
        public AsyncRelayCommand ZipTargetCommand { get; }
        public AsyncRelayCommand CleanCommand { get; }
        public RelayCommand CancelCommand { get; }
        public RelayCommand CategoryFilterCommand { get; }
        public RelayCommand BuildFolderFilterCommand { get; }
        public RelayCommand ExpandAllCommand { get; }
        public RelayCommand CollapseAllCommand { get; }
        internal DeveloperDifferencerViewModel(IUserDialogService dialogs, System.Windows.Threading.Dispatcher dispatcher,
            ConfirmSyncCallback confirmSync, Func<string, DiffSnapshot, ISynchronizationLog> openLog)
        {
            this.dialogs = dialogs; this.dispatcher = dispatcher; this.confirmSync = confirmSync; this.openLog = openLog;
            BrowseSourceCommand = new RelayCommand(() => BrowseFolder(true), () => !IsBusy);
            BrowseTargetCommand = new RelayCommand(() => BrowseFolder(false), () => !IsBusy);
            CloseCommand = new RelayCommand(() => CloseRequested?.Invoke(), () => !IsBusy);
            CompactTreeCommand = new RelayCommand(() => TreeCompact = true);
            ComfortableTreeCommand = new RelayCommand(() => TreeCompact = false);
            OpenDiffCommand = new RelayCommand(OpenDiff, () => !IsBusy && snapshot != null && SelectedRow != null);
            CompareCommand = new AsyncRelayCommand(CompareAsync, ShowError, () => !IsBusy);
            RefreshCommand = new AsyncRelayCommand(RefreshSelectedFolderAsync, ShowError, () => CanRefresh);
            ForwardCommand = new AsyncRelayCommand(() => SyncAsync(true), ShowError, () => CanSynchronize);
            ReverseCommand = new AsyncRelayCommand(() => SyncAsync(false), ShowError, () => CanSynchronize);
            ZipSourceCommand = new AsyncRelayCommand(() => ZipAsync(true), ShowError, () => CanSynchronize);
            ZipTargetCommand = new AsyncRelayCommand(() => ZipAsync(false), ShowError, () => CanSynchronize);
            CleanCommand = new AsyncRelayCommand(CleanAsync, ShowError, () => !IsBusy);
            CancelCommand = new RelayCommand(CancelCompare, () => CanCancel);
            CategoryFilterCommand = new RelayCommand(ApplyCategoryFilter, () => !IsBusy);
            BuildFolderFilterCommand = new RelayCommand(ApplyBuildFolderFilter, () => !IsBusy);
            ExpandAllCommand = new RelayCommand(() => { foreach (var folder in folders.Values) folder.Expanded = true; });
            CollapseAllCommand = new RelayCommand(() => { foreach (var folder in folders.Values) folder.Expanded = false; });
            DeleteHistoryTabCommand = new ParameterCommand(DeleteHistoryTab, () => !IsBusy);
            DeleteAllHistoryTabsCommand = new RelayCommand(DeleteAllHistoryTabs, () => !IsBusy && HistoryTabs.Count > 0);
            DeleteOtherHistoryTabsCommand = new ParameterCommand(DeleteOtherHistoryTabs, () => !IsBusy && HistoryTabs.Count > 1);
        }
        internal void SetFilePanelBusy(bool busyPanel, string message = null)
        { IsFileBusy = busyPanel; if (message != null) BusyMessage = message; }
        internal void ShowError(Exception error) => dialogs.Show(ErrorMessages.English(error), Strings.App_Title, MessageBoxButton.OK, MessageBoxImage.Error);
        internal void Close()
        {
            closed = true;
            sourceIndex?.Cancel();
            targetIndex?.Cancel();
            compareCts?.Cancel();
            compareCts?.Dispose();
            previewScope.Cancel();
            previewScope.Dispose();
            DetachSelectionHandlers();
        }

        private void BrowseFolder(bool source)
        {
            try
            {
                string path = ChooseFolder?.Invoke(source ? SourcePath : TargetPath, source ? "Source folder" : "Target folder");
                if (path == null) return;
                ApplyRootPath(source, path);
                CommitBrowsedRootHistoryRequested?.Invoke(source);
            }
            catch (Exception ex) { ShowError(ex); }
        }

        private void OpenDiff()
        {
            if (IsBusy || snapshot == null || SelectedRow == null) return;
            DiffFile file = SelectedRow.File;
            if (DiffMedia.IsBinary(file.RelativePath))
            {
                dialogs.Show(DiffMedia.BinaryMessage, "Diff View", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }
            try { DiffRequested?.Invoke(snapshot, file); }
            catch (Exception ex) { ShowError(ex); }
        }

        internal IReadOnlyList<DiffFile> GetVisibleComparableFiles()
        {
            var visible = FileItems as IEnumerable<DiffRow> ?? Enumerable.Empty<DiffRow>();
            return visible
                .Select(row => row?.File)
                .Where(file => file != null && !DiffMedia.IsBinary(file.RelativePath))
                .ToList();
        }

        internal DiffRow SelectDisplayedFile(DiffFile file)
        {
            if (file == null) return null;
            DiffRow row = rows.FirstOrDefault(item =>
                ReferenceEquals(item.File, file) ||
                string.Equals(item.File.RelativePath, file.RelativePath, StringComparison.OrdinalIgnoreCase));
            if (row == null) return null;
            SelectedRow = row;
            return row;
        }

        internal async Task LoadPreviewAsync(DiffRow row)
        {
            if (closed || row == null || (snapshot == null && !comparing)) return;
            bool wait = !row.IsPreviewReady;
            if (wait) previewInFlight++;
            try { await row.LoadPreviewAsync(previewWorkers, previewScope.Token); }
            finally
            {
                if (wait)
                {
                    previewInFlight--;
                    if (!closed && previewInFlight <= 0 && !IsBusy) SetFilePanelBusy(false);
                }
            }
        }

        private void RevealContainingFolder(string path)
        {
            if (folders.Count == 0) return;
            DiffFolder target = null;
            string current = path ?? "";
            while (true)
            {
                if (folders.TryGetValue(current, out target) && target.Visible) break;
                if (current.Length == 0) { target = folders.ContainsKey("") ? folders[""] : null; break; }
                current = Path.GetDirectoryName(current) ?? "";
            }
            if (target == null) return;

            string ancestor = target.Path;
            while (ancestor.Length > 0)
            {
                ancestor = Path.GetDirectoryName(ancestor) ?? "";
                DiffFolder parent;
                if (folders.TryGetValue(ancestor, out parent)) parent.Expanded = true;
            }

            syncingTreeFromFile = true;
            foreach (DiffFolder folder in folders.Values)
                if (folder.Active && folder != target) folder.Active = false;
            target.Active = true;
            var pathToTarget = new List<DiffFolder>();
            string folderPath = target.Path ?? "";
            while (true)
            {
                DiffFolder node;
                if (folders.TryGetValue(folderPath, out node)) pathToTarget.Add(node);
                if (folderPath.Length == 0) break;
                folderPath = Path.GetDirectoryName(folderPath) ?? "";
            }
            pathToTarget.Reverse();
            if (FolderRevealRequested == null) CompleteFolderReveal();
            else FolderRevealRequested(pathToTarget);
        }

        internal void CompleteFolderReveal() => syncingTreeFromFile = false;

        internal void RestoreState()
        {
            try
            {
                if (!File.Exists(StatePath)) return;
                DifferencerState state;
                using (var stream = File.OpenRead(StatePath)) state = (DifferencerState)new XmlSerializer(typeof(DifferencerState)).Deserialize(stream);
                SourcePath = DisplayRoot(state.Source); TargetPath = DisplayRoot(state.Target);
                rootLinkVersion++; // Restoring saved paths must not initiate automatic linking.
                treeSource = SourcePath; treeTarget = TargetPath; selectedFolder = ""; cachedVisibleFolders = null;
                Status = "Source and Target restored. Click Compare to build the difference tree.";
            }
            catch (Exception ex) { Status = "Failed to restore tree: " + ErrorMessages.English(ex); }
        }
        internal async Task SyncAsync(bool toTarget)
        {
            if (IsBusy || snapshot == null) return;
            DiffFile[] files = snapshot.Files.Where(f => f.Selected).ToArray();
            DiffFolderSync[] selectedFolders = folders.Values
                .Where(f => f.FolderCanSync && f.FolderSelected && IncludeBuildFolder(f.Path))
                .Select(f => new DiffFolderSync { RelativePath = f.Path, SourceExists = f.SourceExists, TargetExists = f.TargetExists })
                .ToArray();
            if (files.Length == 0 && selectedFolders.Length == 0) return;
            string direction = toTarget ? "Source to Target" : "Target to Source";
            bool confirmed;
            string unusedName;
            string unusedFolder;
            SyncDirectionIconRequested?.Invoke(toTarget, false);
            try
            {
                confirmed = confirmSync(direction, files, selectedFolders, toTarget, false, out unusedName, out unusedFolder);
            }
            finally
            {
                SyncDirectionIconRequested?.Invoke(null, false);
            }
            if (!confirmed) return;
            InvalidateIndexes();
            ISynchronizationLog liveLog = openLog(direction, snapshot);
            SetBusy(true); Status = direction + " — syncing…";
            try
            {
                List<string> log;
                try
                {
                    DiffSnapshot current = snapshot;
                    log = await Task.Run(() => DeveloperDifferencerService.Synchronize(current, files, toTarget,
                        line => dispatcher.Invoke(new Action(() => liveLog.AppendLine(line))), selectedFolders));
                }
                catch (Exception ex) { log = new List<string> { "FAIL " + ErrorMessages.English(ex) }; liveLog.AppendLine(log[0]); }
                string report = direction + "\n" + DateTime.Now.ToString("O") + "\n" + string.Join("\n", log);
                try
                {
                    Directory.CreateDirectory(StateDirectory);
                    string path = Path.Combine(StateDirectory, "differencer-sync-" + DateTime.Now.ToString("yyyyMMdd-HHmmss-fff") + ".log");
                    File.WriteAllText(path, report); liveLog.AppendLine("Log: " + path);
                }
                catch (Exception ex) { liveLog.AppendLine("Failed to save log: " + ErrorMessages.English(ex)); }
                liveLog.Complete(log.Count(l => l.StartsWith("OK ")), log.Count(l => l.StartsWith("FAIL ")), log.Count(l => l.StartsWith("LOCKED ")));
                liveLog.Activate();
            }
            finally { SetBusy(false); }

            // Synchronization changes files on disk. Rebuild the comparison only
            // after the busy state has been released; ZIP creation does not do this.
            await CompareAsync();
            liveLog.Activate();
        }

        internal async Task ZipAsync(bool fromSource)
        {
            if (IsBusy || snapshot == null) return;
            DiffFile[] files = snapshot.Files.Where(f => f.Selected).ToArray();
            DiffFolderSync[] selectedFolders = folders.Values
                .Where(f => f.FolderCanSync && f.FolderSelected && IncludeBuildFolder(f.Path))
                .Select(f => new DiffFolderSync { RelativePath = f.Path, SourceExists = f.SourceExists, TargetExists = f.TargetExists })
                .ToArray();
            if (files.Length == 0 && selectedFolders.Length == 0) return;
            string direction = fromSource
                ? StringOverlay.Get("Differencer_ZipSourceToZip")
                : StringOverlay.Get("Differencer_ZipTargetToZip");
            bool confirmed;
            string zipFileName;
            string zipFolder;
            SyncDirectionIconRequested?.Invoke(fromSource, true);
            try
            {
                confirmed = confirmSync(direction, files, selectedFolders, fromSource, true, out zipFileName, out zipFolder);
            }
            finally
            {
                SyncDirectionIconRequested?.Invoke(null, false);
            }
            if (!confirmed) return;
            string zipPath;
            try { zipPath = DeveloperDifferencerService.ComposeZipPath(zipFolder, zipFileName); }
            catch (Exception ex) { ShowError(ex); return; }
            ISynchronizationLog liveLog = openLog(direction, snapshot);
            SetBusy(true); Status = StringOverlay.Get("Differencer_Zipping").Replace("{0}", direction);
            try
            {
                List<string> log;
                try
                {
                    DiffSnapshot current = snapshot;
                    log = await Task.Run(() => DeveloperDifferencerService.PackZip(current, files, selectedFolders, fromSource, zipPath,
                        line => dispatcher.Invoke(new Action(() => liveLog.AppendLine(line)))));
                }
                catch (Exception ex) { log = new List<string> { "FAIL " + ErrorMessages.English(ex) }; liveLog.AppendLine(log[0]); }
                string report = direction + "\n" + DateTime.Now.ToString("O") + "\n" + zipPath + "\n" + string.Join("\n", log);
                try
                {
                    Directory.CreateDirectory(StateDirectory);
                    string path = Path.Combine(StateDirectory, "differencer-zip-" + DateTime.Now.ToString("yyyyMMdd-HHmmss-fff") + ".log");
                    File.WriteAllText(path, report); liveLog.AppendLine("Log: " + path);
                }
                catch (Exception ex) { liveLog.AppendLine("Failed to save log: " + ErrorMessages.English(ex)); }
                liveLog.Complete(log.Count(l => l.StartsWith("OK ")), log.Count(l => l.StartsWith("FAIL ")), log.Count(l => l.StartsWith("LOCKED ")));
                liveLog.Activate();
            }
            finally { SetBusy(false); }
        }
        public Func<IReadOnlyList<string>, string, SolutionCleanSelection> ChooseCleanSolutions { get; set; }
        public event Action<string, string, string> CleanReportRequested;
        internal async Task CleanAsync()
        {
            if (IsBusy) return;
            SetBusy(true); CanCancel = false;
            bool runComparison = false;
            string cleanSummary = null, cleanLogPath = null, cleanReport = null;
            try
            {
                string source = SourcePath, target = TargetPath;
                Status = Strings.Differencer_FindingSolutions;
                var solutions = await Task.Run(() => SolutionCleanService.FindSolutions(source, target));
                if (solutions.Count == 0) { Status = Strings.Differencer_NoSolutions; return; }
                var selection = ChooseCleanSolutions?.Invoke(solutions, source);
                if (selection == null) { Status = Strings.Differencer_CleanCancelled; return; }
                InvalidateIndexes();
                string[] configurations = selection.Configurations;
                string msbuild = await Task.Run(() => SolutionCleanService.FindMSBuild());
                ClearComparisonView();
                IsProgressVisible = true; ProgressIndeterminate = true;

                var details = new StringBuilder();
                int failures = 0, completed = 0;
                int total = selection.Solutions.Count() * configurations.Length;
                foreach (string solution in selection.Solutions)
                    foreach (string config in configurations)
                    {
                        string label = Path.GetFileName(solution) + " [" + config + "]";
                        Status = string.Format(Strings.Differencer_Cleaning, solution + " [" + config + "]");
                        SetFilePanelBusy(true, string.Format(Strings.Differencer_Cleaning, label + "…"));
                        completed++;
                        details.AppendLine("────────────────────────────────────────────────────────");
                        details.AppendLine("[" + completed + "/" + total + "] " + label);
                        details.AppendLine("Path: " + solution);
                        details.AppendLine();
                        try
                        {
                            var result = await Task.Run(() =>
                            {
                                string output;
                                int code = SolutionCleanService.Clean(msbuild, solution, config, out output);
                                return (Code: code, Output: output);
                            });
                            if (result.Code != 0) failures++;
                            if (!string.IsNullOrWhiteSpace(result.Output))
                                details.AppendLine(result.Output.TrimEnd());
                            details.AppendLine();
                            details.AppendLine(result.Code == 0 ? "Result: OK" : "Result: FAIL (exit " + result.Code + ")");
                        }
                        catch (Exception ex)
                        {
                            failures++;
                            details.AppendLine("Result: FAIL");
                            details.AppendLine(ErrorMessages.English(ex));
                        }
                        details.AppendLine();
                    }

                string summary = string.Format(Strings.Differencer_CleanComplete, completed - failures, failures);
                var report = new StringBuilder();
                report.AppendLine("Solution Clean");
                report.AppendLine("Date: " + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"));
                report.AppendLine("Total: " + completed + "    OK: " + (completed - failures) + "    FAIL: " + failures);
                report.AppendLine();
                report.Append(details);
                Directory.CreateDirectory(StateDirectory);
                string logPath = Path.Combine(StateDirectory, "solution-clean-" + DateTime.Now.ToString("yyyyMMdd-HHmmss-fff") + ".log");
                File.WriteAllText(logPath, report.ToString(), new UTF8Encoding(false));
                cleanSummary = summary;
                cleanLogPath = logPath;
                cleanReport = report.ToString();
                runComparison = true;
                Status = summary;
            }
            catch (Exception ex) { Status = string.Format(Strings.Differencer_CleanFailed, ErrorMessages.English(ex)); ShowError(ex); }
            finally { IsProgressVisible = false; ProgressIndeterminate = false; SetFilePanelBusy(false); SetBusy(false); }
            if (runComparison)
            {
                // CompareAsync refuses to start while IsBusy is true.
                // Run after releasing the Clean busy state, including when individual configurations failed.
                await CompareAsync();
                CleanReportRequested?.Invoke(cleanSummary, cleanLogPath, cleanReport);
            }
        }
        internal bool IncludeBuildFolderFile(DiffFile file)
        {
            string directory = Path.GetDirectoryName(file.RelativePath) ?? "";
            return directory.Split(new[] { '\\', '/' }, StringSplitOptions.RemoveEmptyEntries)
                .All(part => (showObj || !part.Equals("obj", StringComparison.OrdinalIgnoreCase)) &&
                             (showBin || !part.Equals("bin", StringComparison.OrdinalIgnoreCase)));
        }

        internal bool IncludeBuildFolder(string path)
        {
            if (string.IsNullOrEmpty(path)) return true;
            return path.Split(new[] { '\\', '/' }, StringSplitOptions.RemoveEmptyEntries)
                .All(part => (showObj || !part.Equals("obj", StringComparison.OrdinalIgnoreCase)) &&
                             (showBin || !part.Equals("bin", StringComparison.OrdinalIgnoreCase)));
        }

        internal void ApplyBuildFolderFilter()
        {
            showObj = ShowObj == true;
            showBin = ShowBin == true;
            if (snapshot == null) return;

            bulk = true;
            foreach (DiffFile file in snapshot.Files)
                if (!IncludeBuildFolderFile(file))
                    file.Selected = false;

            foreach (DiffFolder folder in folders.Values)
                if (!IncludeBuildFolder(folder.Path))
                    folder.SetFolderSelected(false, false);
            bulk = false;

            RefreshChecks();
            ApplyKindFilter();

            // OBJ/BIN filter changes alter the effective folder state.
            // Re-evaluate every tree icon from the filtered counts.
            foreach (DiffFolder folder in folders.Values)
                folder.RefreshIcon();

            Status = folders[""].CountFor(DiffKind.Differences) + " differences / " + folders[""].CountFor(DiffKind.Same) + " identical (OBJ/BIN filters applied). Check items to synchronize.";
        }

        private void ApplySelectAllFiles(bool value)
        {
            if (IsBusy || snapshot == null) return;

            // ZIP-side ON/OFF is dedicated to Same items. Difference selections are
            // controlled only by the existing tree/file check boxes and remain untouched.
            // While Same is visible, only the Same files in the current file list are
            // changed; Same selections made in other folders are preserved.
            // When the Same filter is OFF (Same is hidden), the button acts as a cleanup
            // operation and clears hidden Same selections so they cannot remain implicit
            // sync/ZIP targets.
            var visibleSameFiles = new HashSet<DiffFile>(
                (FileItems ?? Enumerable.Empty<DiffRow>())
                    .Select(row => row.File)
                    .Where(file => file != null && file.Kind == DiffKind.Same));
            bool sameVisible = ShowSame == true;

            bulk = true;
            try
            {
                foreach (DiffFile file in snapshot.Files)
                {
                    if (file.Kind != DiffKind.Same)
                        continue;

                    if (sameVisible)
                    {
                        if (visibleSameFiles.Contains(file))
                            file.Selected = value;
                    }
                    else if (file.Selected)
                    {
                        file.Selected = false;
                    }
                }
            }
            finally
            {
                bulk = false;
                RefreshSelectionChecks();
            }
        }

        internal void ClearSameFolderSelection(DiffFolder folder)
        {
            if (IsBusy || snapshot == null || folder == null || folder.CanSelect) return;

            bulk = true;
            try
            {
                foreach (DiffFile file in folder.Files)
                {
                    if (file.Kind == DiffKind.Same && file.Selected && IncludeBuildFolderFile(file) && (file.Kind & kindMask) != 0)
                        file.Selected = false;
                }
            }
            finally
            {
                bulk = false;
                RefreshSelectionChecks(folder);
            }
        }

        internal void ApplyCategoryFilter()
        {
            kindMask = (ShowSame == true ? DiffKind.Same : 0) |
                (ShowDifferent == true ? DiffKind.Different : 0) |
                (ShowSourceOnly == true ? DiffKind.SourceOnly : 0) |
                (ShowTargetOnly == true ? DiffKind.TargetOnly : 0);
            if (snapshot != null) ApplyKindFilter();
        }

        internal void DetachSelectionHandlers()
        { if (snapshot != null) foreach (DiffFile file in snapshot.Files) file.PropertyChanged -= FileSelectionChanged; }

        internal void ClearComparisonView()
        {
            CaptureHistoryTab();
            previewScope.Cancel();
            previewScope.Dispose();
            previewScope = new CancellationTokenSource();
            ComparisonCleared?.Invoke();
            DetachSelectionHandlers();
            FolderItems = null;
            FileItems = null;
            streamingRows = null;
            snapshot = null; rows.Clear();
            selectedHistoryTab = null;
            NotifyHistory();
            _selectAllFiles = false; OnPropertyChanged(nameof(SelectAllFiles));
            SelectedRow = null;
            OpenDiffCommand.NotifyCanExecuteChanged();
            CanFilter = false;
            FilePanelTitle = "Files";
            UpdateSelectionSummary();
        }

        internal async Task RefreshSelectedFolderAsync()
        {
            if (IsBusy || snapshot == null) return;
            if (!SelectedFolderHasDirectFiles()) return;

            string folder = selectedFolder ?? string.Empty;
            bool compareTimestamp = CompareTimestamp == true;
            DiffFile[] targets = snapshot.Files
                .Where(f => IsDirectChildFile(folder, f.RelativePath))
                .ToArray();

            SetBusy(true);
            CanCancel = false;
            SetFilePanelBusy(true, "Refreshing files in selected folder…");
            Status = "Refreshing " + targets.Length + " file(s)…";

            try
            {
                foreach (DiffFile file in targets)
                {
                    file.CompareTimestamp = compareTimestamp;
                    await RefreshFileAsync(file);
                }
                ApplyKindFilter();
                Status = "Selected folder files refreshed. " +
                    folders[""].CountFor(DiffKind.Differences) + " differences / " +
                    folders[""].CountFor(DiffKind.Same) + " identical.";
            }
            catch (Exception ex)
            {
                Status = "Refresh failed: " + ErrorMessages.English(ex);
                ShowError(ex);
            }
            finally
            {
                SetBusy(false);
                UpdateRefreshButtonState();
            }
        }

        internal static bool IsDirectChildFile(string folder, string relativePath)
        {
            string parent = Path.GetDirectoryName(relativePath) ?? string.Empty;
            return string.Equals(parent, folder ?? string.Empty, StringComparison.OrdinalIgnoreCase);
        }

        internal bool SelectedFolderHasDirectFiles()
        {
            if (snapshot == null) return false;
            string folder = selectedFolder ?? string.Empty;
            return snapshot.Files.Any(f => IsDirectChildFile(folder, f.RelativePath));
        }

        internal void UpdateRefreshButtonState() { CanRefresh = !IsBusy && !comparing && snapshot != null && SelectedFolderHasDirectFiles(); RefreshCommand.NotifyCanExecuteChanged(); }

        internal void CancelCompare()
        {
            if (!comparing) return;
            try { compareCts?.Cancel(); } catch (ObjectDisposedException) { }
            Status = "Cancelling comparison…";
            CanCancel = false;
        }

        internal void ResetAfterComparison()
        {
            selectedFolder = "";
            foreach (var folder in folders.Values)
            {
                folder.Expanded = false;
                folder.Active = folder.Path.Length == 0;
            }
            syncingTreeFromFile = true;
            try { SelectedRow = FileItems?.FirstOrDefault(); }
            finally { syncingTreeFromFile = false; }
        }
        internal event Action ComparisonCompleted;
        internal async Task CompareAsync()
        {
            if (IsBusy) return;
            await pendingRootLink;
            if (closed || IsBusy || !PrepareComparisonTab()) return;
            CaptureHistoryTab();
            CommitRootHistoryRequested?.Invoke();
            var expanded = folders.Values.Where(f => f.Expanded).Select(f => f.Path).ToList();
            ClearComparisonView(); SetBusy(true);
            comparing = true; CanCancel = true;
            SetFilePanelBusy(false);
            streamingRows = new ObservableCollection<DiffRow>();
            FileItems = streamingRows;
            compareCts?.Dispose();
            compareCts = new CancellationTokenSource();
            var token = compareCts.Token;
            IsProgressVisible = true;
            ProgressIndeterminate = true;
            Status = "Scanning files and comparing timestamps and sizes…";
            string source = SourcePath, target = TargetPath;
            if (sourceIndex != null && !string.Equals(sourceIndex.Path, source, StringComparison.OrdinalIgnoreCase))
            { sourceIndex.Cancel(); sourceIndex = null; }
            if (targetIndex != null && !string.Equals(targetIndex.Path, target, StringComparison.OrdinalIgnoreCase))
            { targetIndex.Cancel(); targetIndex = null; }
            PendingIndex leftIndex = sourceIndex ?? (sourceIndex = new PendingIndex(source));
            PendingIndex rightIndex = targetIndex ?? (targetIndex = new PendingIndex(target));
            _ = ObserveIndexProgressAsync(leftIndex);
            _ = ObserveIndexProgressAsync(rightIndex);
            bool classifying = true;
            try
            {
                var progress = new Progress<DiffProgress>(value =>
                {
                    if (classifying && !token.IsCancellationRequested) UpdateProgress(value);
                });
                bool compareTimestamp = CompareTimestamp == true;
                BusyMessage = Status = "Preparing source / target indexes…";
                DiffIndex[] indexes = await Task.WhenAll(leftIndex.Task, rightIndex.Task).WaitAsync(token);
                token.ThrowIfCancellationRequested();
                if (leftIndex.Error != null) throw leftIndex.Error;
                if (rightIndex.Error != null) throw rightIndex.Error;
                if (indexes[0] == null || indexes[1] == null) throw new OperationCanceledException();
                comparisonIndexes = indexes;
                if (!indexes[0].Folders.Contains(selectedFolder) && !indexes[1].Folders.Contains(selectedFolder)) selectedFolder = "";
                string displayFolder = selectedFolder;
                var pathComparer = StringComparer.CurrentCultureIgnoreCase;
                DiffSnapshot fresh = await Task.Run(() => DeveloperDifferencerService.Compare(indexes[0], indexes[1], progress, compareTimestamp, token,
                    batch => dispatcher.Invoke(() =>
                    {
                        token.ThrowIfCancellationRequested();
                        DiffRow last = null;
                        foreach (DiffFile file in batch)
                        {
                            var row = new DiffRow { File = file, SourceRoot = source, TargetRoot = target };
                            row.SetDirectInSelectedFolder(IsDirectChildFile(displayFolder, file.RelativePath));
                            rows.Add(row);
                            if (!IncludeBuildFolderFile(file) || (file.Kind & kindMask) == 0 || (_checkedOnlyFilter && !file.Selected) ||
                                (displayFolder.Length > 0 && !file.RelativePath.StartsWith(displayFolder + "\\", StringComparison.OrdinalIgnoreCase))) continue;
                            streamingRows.Add(row);
                            last = row;
                        }
                        FilePanelTitle = "Files — " + (displayFolder.Length == 0 ? "all levels" : displayFolder) + " (" + streamingRows.Count + ")";
                        if (last != null) FileProgressRequested?.Invoke(last);
                    }, DispatcherPriority.Background),
                    (left, right) =>
                    {
                        int direct = IsDirectChildFile(displayFolder, right).CompareTo(IsDirectChildFile(displayFolder, left));
                        if (direct != 0) return direct;
                        int name = pathComparer.Compare(Path.GetFileName(left), Path.GetFileName(right));
                        return name != 0 ? name : pathComparer.Compare(left, right);
                    }), token);
                classifying = false;
                token.ThrowIfCancellationRequested();
                Status = "Updating the difference tree…";
                BusyMessage = "Updating the difference tree and file list…";
                snapshot = fresh; treeSource = source; treeTarget = target;
                await BuildTreeAsync(snapshot.Folders, Array.Empty<string>(), "", token);
                ResetAfterComparison();
                ComparisonCompleted?.Invoke();
                Status = folders[""].CountFor(DiffKind.Differences) + " differences / " + folders[""].CountFor(DiffKind.Same) + " identical. Check items to synchronize.";
                AddComparisonTab();
                SaveState();
            }
            catch (OperationCanceledException) { ClearComparisonView(); Status = "Compare cancelled"; }
            catch (Exception ex) { ClearComparisonView(); Status = "Compare failed (sync disabled): " + ErrorMessages.English(ex); ShowError(ex); }
            finally
            {
                classifying = false;
                comparisonIndexes = null;
                streamingRows = null;
                SetFilePanelBusy(false);
                // A later comparison must see files/folders added or deleted since this run.
                leftIndex.Cancel();
                rightIndex.Cancel();
                if (ReferenceEquals(sourceIndex, leftIndex)) sourceIndex = null;
                if (ReferenceEquals(targetIndex, rightIndex)) targetIndex = null;
                NotifyIndexProgress();
                comparing = false;
                IsProgressVisible = false;
                ProgressIndeterminate = false;
                SetBusy(false);
            }
        }

        internal void UpdateProgress(DiffProgress progress)
        {
            if (!comparing) return;
            bool counting = progress.Total <= 0;
            ProgressIndeterminate = counting;
            if (!counting)
            {
                ProgressMaximum = Math.Max(1, progress.Total);
                ProgressValue = Math.Max(0, Math.Min(progress.Completed, ProgressMaximum));
            }
            string detail = string.IsNullOrEmpty(progress.Stage) ? "Please wait…" : progress.Stage;
            BusyMessage = detail;
            Status = detail;
        }

        private void SetTreeProgress(string stage, int completed, int total)
        {
            IsProgressVisible = true;
            ProgressIndeterminate = false;
            ProgressMaximum = Math.Max(1, total);
            ProgressValue = Math.Min(completed, ProgressMaximum);
            BusyMessage = Status = stage + " " + completed.ToString("N0") + " / " + total.ToString("N0");
        }

        private Task<Dictionary<string, HistoryFolderState>> ReadFolderStatesAsync(string[] paths, CancellationToken token)
        {
            var saved = restoringFolders;
            var indexes = comparisonIndexes;
            var current = snapshot;
            return Task.Run(() =>
            {
                var states = new Dictionary<string, HistoryFolderState>(StringComparer.OrdinalIgnoreCase);
                for (int i = 0; i < paths.Length; i++)
                {
                    token.ThrowIfCancellationRequested();
                    string path = paths[i];
                    HistoryFolderState state;
                    if (saved != null && saved.TryGetValue(path, out state)) states[path] = state;
                    else
                    {
                        state = new HistoryFolderState { Path = path };
                        if (indexes != null && indexes[0].NonEmptyFolders != null && indexes[1].NonEmptyFolders != null)
                        {
                            state.SourceExists = indexes[0].Folders.Contains(path);
                            state.TargetExists = indexes[1].Folders.Contains(path);
                            state.SourceEmpty = state.SourceExists && !indexes[0].NonEmptyFolders.Contains(path);
                            state.TargetEmpty = state.TargetExists && !indexes[1].NonEmptyFolders.Contains(path);
                        }
                        else if (current != null)
                        {
                            // Older history entries may lack folder state. Read those off the UI thread.
                            string source = Path.Combine(current.SourceRoot, path);
                            string target = Path.Combine(current.TargetRoot, path);
                            state.SourceExists = Directory.Exists(source);
                            state.TargetExists = Directory.Exists(target);
                            state.SourceEmpty = state.SourceExists && !Directory.EnumerateFileSystemEntries(source).Any();
                            state.TargetEmpty = state.TargetExists && !Directory.EnumerateFileSystemEntries(target).Any();
                        }
                        states[path] = state;
                    }
                    if ((i + 1) % 128 == 0 || i + 1 == paths.Length)
                    {
                        int done = i + 1;
                        dispatcher.Invoke(() =>
                        {
                            if (!closed && !token.IsCancellationRequested) SetTreeProgress("Reading folder state…", done, paths.Length);
                        }, DispatcherPriority.Background);
                    }
                }
                return states;
            }, token).WaitAsync(token);
        }

        internal async Task BuildTreeAsync(IEnumerable<string> paths, IEnumerable<string> expanded, string selected, CancellationToken token)
        {
            bool progressWasVisible = IsProgressVisible;
            try
            {
                SetTreeProgress("Preparing folders…", 0, 0);
                await Dispatcher.Yield(DispatcherPriority.Background);
                folders.Clear();
                var all = new HashSet<string>(paths, StringComparer.OrdinalIgnoreCase) { "" };
                foreach (string path in all.Where(p => p.Length > 0).ToArray())
                {
                    string p = Path.GetDirectoryName(path);
                    while (!string.IsNullOrEmpty(p))
                    {
                        all.Add(p);
                        p = Path.GetDirectoryName(p);
                    }
                }

                var expansion = new HashSet<string>(expanded ?? new string[0], StringComparer.OrdinalIgnoreCase);
                selectedFolder = selected != null && all.Contains(selected) ? selected : "";
                string[] orderedPaths = all.OrderBy(p => p.Length).ThenBy(p => p, StringComparer.OrdinalIgnoreCase).ToArray();
                var folderStates = await ReadFolderStatesAsync(orderedPaths, token);
                SetTreeProgress("Building folders…", 0, orderedPaths.Length);

                int processed = 0;
                foreach (string path in orderedPaths)
                {
                    token.ThrowIfCancellationRequested();

                    HistoryFolderState state = folderStates[path];

                    var node = new DiffFolder
                    {
                        Path = path,
                        Expanded = expansion.Contains(path) || path == "",
                        Active = path == selectedFolder,
                        SourceExists = state.SourceExists,
                        TargetExists = state.TargetExists,
                        SourceEmpty = state.SourceEmpty,
                        TargetEmpty = state.TargetEmpty
                    };
                    node.IncludeFile = IncludeBuildFolderFile;
                    node.IncludeFolder = folder => IncludeBuildFolder(folder.Path);
                    node.Toggle = (folder, value) =>
                    {
                        bulk = true;
                        try
                        {
                            foreach (DiffFile file in folder.Files)
                                if (file.CanSync && IncludeBuildFolderFile(file) && (file.Kind & kindMask) != 0)
                                    file.Selected = value;
                            SetFolderSelectionRecursive(folder, value);
                        }
                        finally
                        {
                            bulk = false;
                            RefreshSelectionChecks(folder);
                        }
                    };
                    folders.Add(path, node);
                    if (path.Length > 0)
                    {
                        DiffFolder parent = folders[Path.GetDirectoryName(path) ?? ""];
                        node.Parent = parent;
                        parent.Children.Add(node);
                    }

                    if (++processed % 64 == 0)
                    {
                        SetTreeProgress("Building folders…", processed, orderedPaths.Length);
                        await Dispatcher.Yield(DispatcherPriority.Background);
                    }
                }

                if (snapshot != null)
                {
                    processed = 0;
                    SetTreeProgress("Assigning files to folders…", 0, snapshot.Files.Count);
                    foreach (DiffFile file in snapshot.Files)
                    {
                        token.ThrowIfCancellationRequested();
                        string path = Path.GetDirectoryName(file.RelativePath) ?? "";
                        while (true)
                        {
                            DiffFolder folder;
                            if (folders.TryGetValue(path, out folder)) folder.Files.Add(file);
                            if (path.Length == 0) break;
                            path = Path.GetDirectoryName(path) ?? "";
                        }

                        if (++processed % 256 == 0)
                        {
                            SetTreeProgress("Assigning files to folders…", processed, snapshot.Files.Count);
                            await Dispatcher.Yield(DispatcherPriority.Background);
                        }
                    }
                }

                processed = 0;
                SetTreeProgress("Updating folder counts…", 0, folders.Count);
                foreach (DiffFolder folder in folders.Values)
                {
                    token.ThrowIfCancellationRequested();
                    folder.Refresh();
                    if (++processed % 128 == 0)
                    {
                        SetTreeProgress("Updating folder counts…", processed, folders.Count);
                        await Dispatcher.Yield(DispatcherPriority.Background);
                    }
                }

                if (snapshot != null)
                    cachedVisibleFolders = new HashSet<string>(folders.Values.Where(f => f.Path.Length == 0 || f.CountFor(DiffKind.Differences) > 0 || f.FolderCanSync).Select(f => f.Path), StringComparer.OrdinalIgnoreCase);

                DetachSelectionHandlers();
                if (snapshot != null)
                {
                    processed = 0;
                    foreach (DiffFile file in snapshot.Files)
                    {
                        file.PropertyChanged += FileSelectionChanged;
                        if (++processed % 512 == 0)
                            await Dispatcher.Yield(DispatcherPriority.Background);
                    }
                }

                SetTreeProgress("Updating folder display…", 0, folders.Count);
                await ApplyKindFilterAsync(token);
                SetTreeProgress("Updating folder display…", folders.Count, folders.Count);
            }
            finally { IsProgressVisible = progressWasVisible; }
        }

        internal async Task ApplyKindFilterAsync(CancellationToken token)
        {
            FolderItems = null;
            int processed = 0;
            foreach (DiffFolder folder in folders.Values)
            {
                token.ThrowIfCancellationRequested();
                folder.Mask = kindMask;
                folder.Visible = folder.Path.Length == 0 ||
                    (IncludeBuildFolder(folder.Path) &&
                     (snapshot != null
                         ? folder.CountFor(kindMask) > 0 || folder.MatchesFolderMask(kindMask)
                         : cachedVisibleFolders == null || cachedVisibleFolders.Contains(folder.Path)));
                folder.Label = (folder.Path.Length == 0 ? RootLabel() : Path.GetFileName(folder.Path)) + (snapshot == null ? " (not compared)" : " (" + folder.CountFor(kindMask) + ")");

                if (++processed % 128 == 0)
                    await Dispatcher.Yield(DispatcherPriority.Background);
            }

            PromoteVisibleFolderAncestors();

            if (!folders[selectedFolder].Visible)
            {
                folders[selectedFolder].Active = false;
                selectedFolder = "";
                folders[""].Active = true;
            }

            processed = 0;
            foreach (DiffFolder folder in folders.Values)
            {
                folder.UpdateDisplayChildren();
                if (++processed % 128 == 0)
                    await Dispatcher.Yield(DispatcherPriority.Background);
            }

            FolderItems = new[] { folders[""] };
            ApplyCheckedOnlyFolderCollapse();
            await Dispatcher.Yield(DispatcherPriority.Background);

            var visible = await Task.Run(() => rows.Where(r => IncludeBuildFolderFile(r.File) &&
                                                               (r.File.Kind & kindMask) != 0 &&
                                                               (selectedFolder.Length == 0 || r.File.RelativePath.StartsWith(selectedFolder + "\\", StringComparison.OrdinalIgnoreCase)) &&
                                                               (!_checkedOnlyFilter || r.File.Selected))
                                                   .ToList(), token);
            token.ThrowIfCancellationRequested();

            MarkDirectFileRows(visible);
            visible = OrderSelectedFolderFirst(visible);
            if (streamingRows != null && ReferenceEquals(FileItems, streamingRows))
            {
                // Keep the streamed collection and row objects. Usually the order already
                // matches; only reconcile when the selected folder fell back to the root.
                var wanted = new HashSet<DiffRow>(visible);
                for (int i = streamingRows.Count - 1; i >= 0; i--)
                {
                    token.ThrowIfCancellationRequested();
                    if (!wanted.Contains(streamingRows[i])) streamingRows.RemoveAt(i);
                }
                for (int i = 0; i < visible.Count; i++)
                {
                    token.ThrowIfCancellationRequested();
                    if (i >= streamingRows.Count || !ReferenceEquals(streamingRows[i], visible[i]))
                    {
                        int oldIndex = streamingRows.IndexOf(visible[i]);
                        if (oldIndex >= 0) streamingRows.Move(oldIndex, i);
                        else streamingRows.Insert(i, visible[i]);
                    }
                    if ((i + 1) % 256 == 0) await Dispatcher.Yield(DispatcherPriority.Background);
                }
            }
            else FileItems = visible;
            FilePanelTitle = "Files — " + (selectedFolder.Length == 0 ? "all levels" : selectedFolder) + " (" + visible.Count + ")";
            UpdateSelectionSummary();

            // Allow the current collection to render without hiding or replacing it.
            await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
        }

        internal void ApplyKindFilter()
        {
            // Counts were built once with the snapshot. Switching categories does not rescan files or rebuild the base tree.
            FolderItems = null;
            foreach (DiffFolder folder in folders.Values)
            {
                folder.Mask = kindMask;
                folder.Visible = folder.Path.Length == 0 ||
                    (IncludeBuildFolder(folder.Path) &&
                     (snapshot != null
                         ? folder.CountFor(kindMask) > 0 || folder.MatchesFolderMask(kindMask)
                         : cachedVisibleFolders == null || cachedVisibleFolders.Contains(folder.Path)));
                folder.Label = (folder.Path.Length == 0 ? RootLabel() : Path.GetFileName(folder.Path)) + (snapshot == null ? " (not compared)" : " (" + folder.CountFor(kindMask) + ")");
            }
            PromoteVisibleFolderAncestors();

            if (!folders[selectedFolder].Visible)
            {
                folders[selectedFolder].Active = false; selectedFolder = ""; folders[""].Active = true;
            }
            foreach (DiffFolder folder in folders.Values) folder.UpdateDisplayChildren();
            FolderItems = new[] { folders[""] };
            ApplyCheckedOnlyFolderCollapse();
            Filter(); UpdateSelectionSummary();
        }

        private void PromoteVisibleFolderAncestors()
        {
            foreach (DiffFolder folder in folders.Values.Where(f => f.Visible && f.Path.Length > 0).ToArray())
            {
                string parentPath = Path.GetDirectoryName(folder.Path) ?? "";
                while (true)
                {
                    if (folders.TryGetValue(parentPath, out DiffFolder parent))
                        parent.Visible = true;
                    if (parentPath.Length == 0) break;
                    parentPath = Path.GetDirectoryName(parentPath) ?? "";
                }
            }
        }

        private void SetFolderSelectionRecursive(DiffFolder folder, bool value)
        {
            if (!IncludeBuildFolder(folder.Path))
                return;

            if (folder.FolderCanSync && folder.MatchesFolderMask(kindMask))
                folder.SetFolderSelected(value, false);

            foreach (DiffFolder child in folder.Children)
                SetFolderSelectionRecursive(child, value);
        }

        internal void RefreshChecks()
        {
            foreach (DiffFolder folder in folders.Values) folder.Refresh();
            UpdateSelectionSummary();
        }

        internal void RefreshSelectionChecks(DiffFolder changedFolder = null)
        {
            // Bulk check/uncheck changes only selection state.
            // Do not rebuild difference counts, CanSelect or icons.
            if (changedFolder == null)
            {
                foreach (DiffFolder folder in folders.Values)
                    folder.RefreshSelectionCounts();
            }
            else
            {
                // Files are aggregated into every ancestor. Sibling branches are unchanged.
                var pending = new Stack<DiffFolder>();
                pending.Push(changedFolder);
                while (pending.Count > 0)
                {
                    DiffFolder folder = pending.Pop();
                    folder.RefreshSelectionCounts();
                    foreach (DiffFolder child in folder.Children) pending.Push(child);
                }
                for (DiffFolder parent = changedFolder.Parent; parent != null; parent = parent.Parent)
                    parent.RefreshSelectionCounts();
            }

            UpdateSelectionSummary();
        }

        internal void FileSelectionChanged(object sender, PropertyChangedEventArgs e)
        {
            if (bulk || snapshot == null || e.PropertyName != "Selected") return;
            var file = (DiffFile)sender;
            int delta = file.Selected ? 1 : -1;
            string path = Path.GetDirectoryName(file.RelativePath) ?? "";
            while (true)
            {
                DiffFolder folder; if (folders.TryGetValue(path, out folder)) folder.ChangeSelectionCount(delta, file.Kind, IncludeBuildFolderFile(file));
                if (path.Length == 0) break;
                path = Path.GetDirectoryName(path) ?? "";
            }
            UpdateSelectionSummary();
        }

        internal void UpdateSelectionSummary()
        {
            DiffFolder root = null;
            int selectedFolderCount = snapshot == null ? 0 : folders.Values.Count(f => f.FolderCanSync && f.FolderSelected && IncludeBuildFolder(f.Path));
            int fileCount = snapshot != null && folders.TryGetValue("", out root) ? root.SelectedCount : 0;
            int count = fileCount + selectedFolderCount;
            int visibleSelectedFolders = snapshot == null ? 0 : folders.Values.Count(f => f.FolderCanSync && f.FolderSelected && IncludeBuildFolder(f.Path) && f.MatchesFolderMask(kindMask));
            int hidden = root == null ? 0 : count - root.SelectedFor(kindMask) - visibleSelectedFolders;
            CountLabel = "Folder " + selectedFolderCount + " / Files " + fileCount
                + (hidden > 0 ? " (includes " + hidden + " hidden)" : "");
            CanSynchronize = !IsBusy && count > 0;
            ForwardCommand.NotifyCanExecuteChanged();
            ReverseCommand.NotifyCanExecuteChanged();
            ZipSourceCommand.NotifyCanExecuteChanged();
            ZipTargetCommand.NotifyCanExecuteChanged();
            SelectionCountChanged?.Invoke(fileCount);
        }

        private void MarkDirectFileRows(List<DiffRow> visible)
        {
            string folder = selectedFolder ?? string.Empty;
            foreach (DiffRow row in visible)
                row.SetDirectInSelectedFolder(IsDirectChildFile(folder, row.File.RelativePath));
        }

        private static List<DiffRow> OrderSelectedFolderFirst(List<DiffRow> visible)
        {
            if (visible == null || visible.Count == 0)
                return visible ?? new List<DiffRow>();

            var selected = new List<DiffRow>();
            var nested = new List<DiffRow>();
            foreach (DiffRow row in visible)
            {
                if (row.IsDirectInSelectedFolder) selected.Add(row);
                else nested.Add(row);
            }

            selected.Sort(CompareDiffRows);
            nested.Sort(CompareDiffRows);
            selected.AddRange(nested);
            return selected;
        }

        private static int CompareDiffRows(DiffRow left, DiffRow right)
        {
            int name = StringComparer.CurrentCultureIgnoreCase.Compare(
                left == null ? null : left.File.Name,
                right == null ? null : right.File.Name);
            if (name != 0) return name;
            return StringComparer.CurrentCultureIgnoreCase.Compare(
                left == null ? null : left.File.RelativePath,
                right == null ? null : right.File.RelativePath);
        }

        internal string RootLabel()
        { return string.IsNullOrWhiteSpace(treeSource) ? "All folders" : Path.GetFileName(treeSource.TrimEnd('\\', '/')) is string name && name.Length > 0 ? name : treeSource; }


        internal string StageCheckedCopy(bool fromSource)
        {
            if (snapshot == null) return null;
            string root = fromSource ? snapshot.SourceRoot : snapshot.TargetRoot;
            if (!Directory.Exists(root)) throw new DirectoryNotFoundException(root);
            var dirs = folders.Values
                .Where(f => f.FolderCanSync && f.FolderSelected && IncludeBuildFolder(f.Path) && f.Path.Length > 0
                    && (fromSource ? f.SourceExists : f.TargetExists))
                .Select(f => f.Path)
                .ToList();
            var files = snapshot.Files
                .Where(f => (fromSource ? f.Source : f.Target) != null
                    && (f.Selected || dirs.Any(dir => f.RelativePath.StartsWith(dir.TrimEnd('\\') + "\\", StringComparison.OrdinalIgnoreCase))))
                .Select(f => f.RelativePath).ToList();
            string staged = DropOutService.StageStructuredCopy(root, files, dirs);
            if (staged == null)
                throw new InvalidOperationException("No selected files or folders exist on " + (fromSource ? "Source." : "Target."));
            return staged;
        }

        private void ApplyCheckedOnlyFolderCollapse()
        {
            if (folders == null || folders.Count == 0 || !folders.ContainsKey(""))
                return;
            if (!_checkedOnlyFilter)
                return;

            if (!string.IsNullOrEmpty(selectedFolder) && folders.ContainsKey(selectedFolder))
            {
                folders[selectedFolder].Active = false;
                selectedFolder = "";
                folders[""].Active = true;
            }

            foreach (DiffFolder folder in folders.Values)
            {
                if (folder.Path.Length == 0)
                {
                    folder.Visible = true;
                    folder.Expanded = false;
                }
                else
                    folder.Visible = false;
            }

            folders[""].UpdateDisplayChildren();
            FolderItems = new[] { folders[""] };
        }

        internal void Filter()
        {
            var visible = rows.Where(r => IncludeBuildFolderFile(r.File) && (r.File.Kind & kindMask) != 0 && (selectedFolder.Length == 0 || r.File.RelativePath.StartsWith(selectedFolder + "\\", StringComparison.OrdinalIgnoreCase)) && (!_checkedOnlyFilter || r.File.Selected)).ToList();
            MarkDirectFileRows(visible);
            visible = OrderSelectedFolderFirst(visible);
            FileItems = visible;
            FilePanelTitle = "Files — " + (selectedFolder.Length == 0 ? "all levels" : selectedFolder) + " (" + visible.Count + ")";
            UpdateRefreshButtonState();
        }

        internal void SetBusy(bool value)
        {
            IsBusy = value; OnPropertyChanged(nameof(CanEdit)); CanFilter = !value && snapshot != null;
            CanCancel = value && comparing;
            SetFilePanelBusy(value, value ? "Please wait…" : null);
            UpdateSelectionSummary(); UpdateRefreshButtonState();
            CompareCommand.NotifyCanExecuteChanged(); CleanCommand.NotifyCanExecuteChanged();
            BrowseSourceCommand.NotifyCanExecuteChanged(); BrowseTargetCommand.NotifyCanExecuteChanged();
            CloseCommand.NotifyCanExecuteChanged(); OpenDiffCommand.NotifyCanExecuteChanged();
            DeleteHistoryTabCommand?.NotifyCanExecuteChanged();
            DeleteAllHistoryTabsCommand?.NotifyCanExecuteChanged();
            DeleteOtherHistoryTabsCommand?.NotifyCanExecuteChanged();
        }

        internal async Task<bool> RefreshFileAsync(DiffFile file)
        {
            if (snapshot == null || file == null || comparing) return false;

            var stamps = await Task.Run(() =>
            {
                string sourcePath = DeveloperDifferencerService.SafePath(snapshot.SourceRoot, file.RelativePath);
                string targetPath = DeveloperDifferencerService.SafePath(snapshot.TargetRoot, file.RelativePath);
                return Tuple.Create(DiffStamp.Read(sourcePath), DiffStamp.Read(targetPath));
            });

            DiffStamp source = stamps.Item1;
            DiffStamp target = stamps.Item2;
            bool changed = !DiffStamp.Same(file.Source, source, file.CompareTimestamp) ||
                           !DiffStamp.Same(file.Target, target, file.CompareTimestamp);
            if (!changed) return false;
            InvalidateIndexes();

            var refreshedFile = new DiffFile
            {
                RelativePath = file.RelativePath,
                Source = source,
                Target = target,
                CompareTimestamp = file.CompareTimestamp
            };
            refreshedFile.Selected = file.Selected;

            file.PropertyChanged -= FileSelectionChanged;
            refreshedFile.PropertyChanged += FileSelectionChanged;

            int fileIndex = snapshot.Files.FindIndex(f => ReferenceEquals(f, file));
            if (fileIndex < 0)
                fileIndex = snapshot.Files.FindIndex(f => string.Equals(f.RelativePath, file.RelativePath, StringComparison.OrdinalIgnoreCase));
            if (fileIndex >= 0)
                snapshot.Files[fileIndex] = refreshedFile;

            DiffRow row = rows.FirstOrDefault(r => ReferenceEquals(r.File, file));
            if (row == null)
                row = rows.FirstOrDefault(r => string.Equals(r.File.RelativePath, file.RelativePath, StringComparison.OrdinalIgnoreCase));
            if (row != null)
            {
                row.File = refreshedFile;
                row.RefreshFromFile();
            }

            foreach (DiffFolder folder in folders.Values)
            {
                for (int i = 0; i < folder.Files.Count; i++)
                {
                    if (ReferenceEquals(folder.Files[i], file) ||
                        string.Equals(folder.Files[i].RelativePath, file.RelativePath, StringComparison.OrdinalIgnoreCase))
                    {
                        folder.Files[i] = refreshedFile;
                        break;
                    }
                }
                folder.Refresh();
            }

            ApplyKindFilter();
            Status = folders[""].CountFor(DiffKind.Differences) + " differences / " +
                              folders[""].CountFor(DiffKind.Same) + " identical. External edit refreshed: " +
                              refreshedFile.RelativePath;
            return true;
        }

        internal void SaveState()
        {
            SaveHistory();
            try
            {
                Directory.CreateDirectory(StateDirectory);
                var state = new DifferencerState { Source = SourcePath, Target = TargetPath };
                string temporary = StatePath + ".tmp";
                using (var stream = File.Create(temporary)) new XmlSerializer(typeof(DifferencerState)).Serialize(stream, state);
                if (File.Exists(StatePath)) File.Replace(temporary, StatePath, null); else File.Move(temporary, StatePath);
            }
            catch (Exception ex) { Status = "Failed to save tree: " + ErrorMessages.English(ex); }
        }

    }
    internal sealed class SolutionCleanSelection
    {
        public string[] Solutions { get; set; }
        public string[] Configurations { get; set; }
    }
}
