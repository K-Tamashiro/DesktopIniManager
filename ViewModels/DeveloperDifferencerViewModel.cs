using System.Text;
using DesktopIniManager.Properties;
using DesktopIniManager.Services;
using System;
using System.Collections.Generic;
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
    internal sealed class DeveloperDifferencerViewModel : ObservableObject
    {
        internal static readonly string StateDirectory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "DesktopIniManager");
        internal static string StatePath = Path.Combine(StateDirectory, "developer-differencer.xml");
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
        public RelayCommand BrowseSourceCommand { get; }
        public RelayCommand BrowseTargetCommand { get; }
        public RelayCommand CloseCommand { get; }
        public RelayCommand CompactTreeCommand { get; }
        public RelayCommand ComfortableTreeCommand { get; }
        public RelayCommand OpenDiffCommand { get; }
        private readonly Dictionary<string, DiffFolder> folders = new Dictionary<string, DiffFolder>(StringComparer.OrdinalIgnoreCase);
        private List<DiffRow> rows = new List<DiffRow>();
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
            if (syncingTreeFromFile) return;
            selectedFolder = path ?? "";
            Filter();
        }
        private string _sourcePath = string.Empty;
        public string SourcePath { get => _sourcePath; set { if (SetProperty(ref _sourcePath, value)) ClearComparisonView(); } }
        private string _targetPath = string.Empty;
        public string TargetPath { get => _targetPath; set { if (SetProperty(ref _targetPath, value)) ClearComparisonView(); } }
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
        private bool _canCancel = false;
        public bool CanCancel { get => _canCancel; set { if (SetProperty(ref _canCancel, value)) CancelCommand?.NotifyCanExecuteChanged(); } }

        private readonly IUserDialogService dialogs;
        private readonly Func<string, DiffFile[], DiffFolderSync[], bool, bool> confirmSync;
        private readonly Func<string, DiffSnapshot, ISynchronizationLog> openLog;
        private readonly System.Windows.Threading.Dispatcher dispatcher;
        public bool CanEdit => !IsBusy;
        public event Action ComparisonCleared;
        public event Action CommitRootHistoryRequested;
        public event Action<bool?> SyncDirectionIconRequested;
        public AsyncRelayCommand CompareCommand { get; }
        public AsyncRelayCommand RefreshCommand { get; }
        public AsyncRelayCommand ForwardCommand { get; }
        public AsyncRelayCommand ReverseCommand { get; }
        public AsyncRelayCommand CleanCommand { get; }
        public RelayCommand CancelCommand { get; }
        public RelayCommand CategoryFilterCommand { get; }
        public RelayCommand BuildFolderFilterCommand { get; }
        public RelayCommand ExpandAllCommand { get; }
        public RelayCommand CollapseAllCommand { get; }
        internal DeveloperDifferencerViewModel(IUserDialogService dialogs, System.Windows.Threading.Dispatcher dispatcher,
            Func<string, DiffFile[], DiffFolderSync[], bool, bool> confirmSync, Func<string, DiffSnapshot, ISynchronizationLog> openLog)
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
            CleanCommand = new AsyncRelayCommand(CleanAsync, ShowError, () => !IsBusy);
            CancelCommand = new RelayCommand(CancelCompare, () => CanCancel);
            CategoryFilterCommand = new RelayCommand(ApplyCategoryFilter, () => !IsBusy);
            BuildFolderFilterCommand = new RelayCommand(ApplyBuildFolderFilter, () => !IsBusy);
            ExpandAllCommand = new RelayCommand(() => { foreach (var folder in folders.Values) folder.Expanded = true; });
            CollapseAllCommand = new RelayCommand(() => { foreach (var folder in folders.Values) folder.Expanded = false; });
        }
        internal void SetFilePanelBusy(bool busyPanel, string message = null)
        { IsFileBusy = busyPanel; if (message != null) BusyMessage = message; }
        internal void ShowError(Exception error) => dialogs.Show(ErrorMessages.English(error), Strings.App_Title, MessageBoxButton.OK, MessageBoxImage.Error);
        internal void Close()
        {
            closed = true;
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
                if (source) SourcePath = path;
                else TargetPath = path;
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
            if (closed || row == null || snapshot == null) return;
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
                SourcePath = state.Source; TargetPath = state.Target;
                treeSource = state.Source; treeTarget = state.Target; selectedFolder = ""; cachedVisibleFolders = null;
                Status = "Source and Target restored. Click Compare to build the difference tree.";
            }
            catch (Exception ex) { Status = "Failed to restore tree: " + ErrorMessages.English(ex); }
        }
        internal async Task SyncAsync(bool toTarget)
        {
            if (IsBusy || snapshot == null) return;
            DiffFile[] files = snapshot.Files.Where(f => f.CanSync && f.Selected).ToArray();
            DiffFolderSync[] selectedFolders = folders.Values
                .Where(f => f.FolderCanSync && f.FolderSelected && IncludeBuildFolder(f.Path))
                .Select(f => new DiffFolderSync { RelativePath = f.Path, SourceExists = f.SourceExists, TargetExists = f.TargetExists })
                .ToArray();
            if (files.Length == 0 && selectedFolders.Length == 0) return;
            string direction = toTarget ? "Source to Target" : "Target to Source";
            bool confirmed;
            SyncDirectionIconRequested?.Invoke(toTarget);
            try
            {
                confirmed = confirmSync(direction, files, selectedFolders, toTarget);
            }
            finally
            {
                SyncDirectionIconRequested?.Invoke(null);
            }
            if (!confirmed) return;
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
                await CompareAsync();
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
            try
            {
                string source = SourcePath, target = TargetPath;
                Status = Strings.Differencer_FindingSolutions;
                var solutions = await Task.Run(() => SolutionCleanService.FindSolutions(source, target));
                if (solutions.Count == 0) { Status = Strings.Differencer_NoSolutions; return; }
                var selection = ChooseCleanSolutions?.Invoke(solutions, source);
                if (selection == null) { Status = Strings.Differencer_CleanCancelled; return; }
                string[] configurations = selection.Configurations;
                string msbuild = await Task.Run(() => SolutionCleanService.FindMSBuild());
                ClearComparisonView();
                IsProgressVisible = true; ProgressIndeterminate = true;
                var log = new StringBuilder(); int failures = 0, completed = 0;
                foreach (string solution in selection.Solutions)
                    foreach (string config in configurations)
                    {
                        string label = solution + " [" + config + "]";
                        Status = string.Format(Strings.Differencer_Cleaning, label);
                        SetFilePanelBusy(true, string.Format(Strings.Differencer_Cleaning, Path.GetFileName(solution) + " [" + config + "]…"));
                        log.AppendLine(label);
                        try
                        {
                            int exit = await Task.Run(() => { string output; int code = SolutionCleanService.Clean(msbuild, solution, config, out output); log.AppendLine(output); return code; });
                            if (exit != 0) failures++;
                            log.AppendLine(exit == 0 ? Strings.Common_OK : string.Format(Strings.Differencer_FailExit, exit));
                        }
                        catch (Exception ex) { failures++; log.AppendLine(Strings.Common_Fail + " " + ErrorMessages.English(ex)); }
                        completed++;
                    }
                string summary = string.Format(Strings.Differencer_CleanComplete, completed - failures, failures);
                Directory.CreateDirectory(StateDirectory);
                string logPath = Path.Combine(StateDirectory, "solution-clean-" + DateTime.Now.ToString("yyyyMMdd-HHmmss-fff") + ".log");
                File.WriteAllText(logPath, log.ToString());
                await CompareAsync();
                Status = summary + " " + Status;
                CleanReportRequested?.Invoke(summary, logPath, log.ToString());
            }
            catch (Exception ex) { Status = string.Format(Strings.Differencer_CleanFailed, ErrorMessages.English(ex)); ShowError(ex); }
            finally { IsProgressVisible = false; ProgressIndeterminate = false; SetBusy(false); }
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
            previewScope.Cancel();
            previewScope.Dispose();
            previewScope = new CancellationTokenSource();
            ComparisonCleared?.Invoke();
            DetachSelectionHandlers();
            FolderItems = null;
            FileItems = null;
            snapshot = null; rows.Clear();
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

        internal async Task CompareAsync()
        {
            CommitRootHistoryRequested?.Invoke();
            var expanded = folders.Values.Where(f => f.Expanded).Select(f => f.Path).ToList();
            ClearComparisonView(); SetBusy(true);
            comparing = true; CanCancel = true;
            compareCts?.Dispose();
            compareCts = new CancellationTokenSource();
            var token = compareCts.Token;
            IsProgressVisible = true;
            ProgressIndeterminate = true;
            Status = "Scanning files and comparing timestamps and sizes…";
            string source = SourcePath, target = TargetPath;
            try
            {
                var progress = new Progress<DiffProgress>(UpdateProgress);
                bool compareTimestamp = CompareTimestamp == true;
                DiffSnapshot fresh = await Task.Run(() => DeveloperDifferencerService.Compare(source, target, progress, compareTimestamp, token), token);
                token.ThrowIfCancellationRequested();
                Status = "Updating the difference tree…";
                BusyMessage = "Updating the difference tree and file list…";
                snapshot = fresh; treeSource = source; treeTarget = target;
                rows = await Task.Run(() => snapshot.Files
                    .Select(f => new DiffRow { File = f, SourceRoot = snapshot.SourceRoot, TargetRoot = snapshot.TargetRoot })
                    .ToList(), token);
                token.ThrowIfCancellationRequested();
                await BuildTreeAsync(snapshot.Folders, expanded, selectedFolder, token);
                Status = folders[""].CountFor(DiffKind.Differences) + " differences / " + folders[""].CountFor(DiffKind.Same) + " identical. Check items to synchronize.";
                SaveState();
            }
            catch (OperationCanceledException) { Status = "Compare cancelled"; }
            catch (Exception ex) { Status = "Compare failed (sync disabled): " + ErrorMessages.English(ex); ShowError(ex); }
            finally
            {
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

        internal async Task BuildTreeAsync(IEnumerable<string> paths, IEnumerable<string> expanded, string selected, CancellationToken token)
        {
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

            int processed = 0;
            foreach (string path in all.OrderBy(p => p.Length).ThenBy(p => p, StringComparer.OrdinalIgnoreCase))
            {
                token.ThrowIfCancellationRequested();

                string sourceFolderPath = snapshot == null ? null : System.IO.Path.Combine(snapshot.SourceRoot, path);
                string targetFolderPath = snapshot == null ? null : System.IO.Path.Combine(snapshot.TargetRoot, path);
                bool sourceExists = sourceFolderPath != null && Directory.Exists(sourceFolderPath);
                bool targetExists = targetFolderPath != null && Directory.Exists(targetFolderPath);
                bool sourceEmpty = sourceExists && !Directory.EnumerateFileSystemEntries(sourceFolderPath).Any();
                bool targetEmpty = targetExists && !Directory.EnumerateFileSystemEntries(targetFolderPath).Any();

                var node = new DiffFolder
                {
                    Path = path,
                    Expanded = expansion.Contains(path) || path == "",
                    Active = path == selectedFolder,
                    SourceExists = sourceExists,
                    TargetExists = targetExists,
                    SourceEmpty = sourceEmpty,
                    TargetEmpty = targetEmpty
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
                    await Dispatcher.Yield(DispatcherPriority.Background);
            }

            if (snapshot != null)
            {
                processed = 0;
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
                        await Dispatcher.Yield(DispatcherPriority.Background);
                }
            }

            processed = 0;
            foreach (DiffFolder folder in folders.Values)
            {
                token.ThrowIfCancellationRequested();
                folder.Refresh();
                if (++processed % 128 == 0)
                    await Dispatcher.Yield(DispatcherPriority.Background);
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

            await ApplyKindFilterAsync(token);
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
            await Dispatcher.Yield(DispatcherPriority.Background);

            var visible = await Task.Run(() => rows.Where(r => IncludeBuildFolderFile(r.File) &&
                                                               (r.File.Kind & kindMask) != 0 &&
                                                               (selectedFolder.Length == 0 || r.File.RelativePath.StartsWith(selectedFolder + "\\", StringComparison.OrdinalIgnoreCase)))
                                                   .ToList(), token);
            token.ThrowIfCancellationRequested();

            FileItems = visible;
            FilePanelTitle = "Files — " + (selectedFolder.Length == 0 ? "all levels" : selectedFolder) + " (" + visible.Count + ")";
            UpdateSelectionSummary();

            // Give WPF one render pass before the busy overlay is removed in Compare().
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
            FolderItems = new[] { folders[""] }; Filter(); UpdateSelectionSummary();
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
            int folderDifferenceCount = snapshot == null ? 0 : folders.Values.Count(f => f.FolderCanSync && IncludeBuildFolder(f.Path));
            int count = (snapshot != null && folders.TryGetValue("", out root) ? root.SelectedCount : 0) + selectedFolderCount;
            int total = (root == null ? 0 : root.AllDifferenceCount) + folderDifferenceCount;
            int visibleSelectedFolders = snapshot == null ? 0 : folders.Values.Count(f => f.FolderCanSync && f.FolderSelected && IncludeBuildFolder(f.Path) && f.MatchesFolderMask(kindMask));
            int hidden = root == null ? 0 : count - root.SelectedFor(kindMask) - visibleSelectedFolders;
            CountLabel = "Selected " + count + " / " + total + (hidden > 0 ? " (includes " + hidden + " hidden)" : "");
            CanSynchronize = !IsBusy && count > 0; ForwardCommand.NotifyCanExecuteChanged(); ReverseCommand.NotifyCanExecuteChanged();
        }

        internal string RootLabel()
        { return string.IsNullOrWhiteSpace(treeSource) ? "All folders" : Path.GetFileName(treeSource.TrimEnd('\\', '/')) is string name && name.Length > 0 ? name : treeSource; }

        internal void Filter()
        {
            var visible = rows.Where(r => IncludeBuildFolderFile(r.File) && (r.File.Kind & kindMask) != 0 && (selectedFolder.Length == 0 || r.File.RelativePath.StartsWith(selectedFolder + "\\", StringComparison.OrdinalIgnoreCase))).ToList();
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
