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
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using System.Xml.Serialization;

namespace DesktopIniManager.ViewModels
{
    internal sealed class DeveloperDifferencerViewModel : ObservableObject
    {
        internal static readonly string StateDirectory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "DesktopIniManager");
        internal static string StatePath = Path.Combine(StateDirectory, "developer-differencer.xml");
        private DiffSnapshot snapshot;
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
        public string SelectedFolderPath => selectedFolder;
        public void SelectFolder(string path)
        {
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
        private readonly Func<string, DiffFile[], bool, bool> confirmSync;
        private readonly Func<string, DiffSnapshot, ISynchronizationLog> openLog;
        private readonly System.Windows.Threading.Dispatcher dispatcher;
        public bool CanEdit => !IsBusy;
        public event Action ComparisonCleared;
        public event Action CommitRootHistoryRequested;
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
            Func<string, DiffFile[], bool, bool> confirmSync, Func<string, DiffSnapshot, ISynchronizationLog> openLog)
        {
            this.dialogs = dialogs; this.dispatcher = dispatcher; this.confirmSync = confirmSync; this.openLog = openLog;
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
        internal void ShowError(Exception error) => dialogs.Show(ErrorMessages.English(error), "MFT Differencer", MessageBoxButton.OK, MessageBoxImage.Error);
        internal void Close() { compareCts?.Cancel(); compareCts?.Dispose(); DetachSelectionHandlers(); }
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
            if (files.Length == 0) return;
            string direction = toTarget ? "Source to Target" : "Target to Source";
            if (!confirmSync(direction, files, toTarget)) return;
            ISynchronizationLog liveLog = openLog(direction, snapshot);
            SetBusy(true); Status = direction + " — syncing…";
            try
            {
                List<string> log;
                try
                {
                    DiffSnapshot current = snapshot;
                    log = await Task.Run(() => DeveloperDifferencerService.Synchronize(current, files, toTarget,
                        line => dispatcher.BeginInvoke(new Action(() => liveLog.AppendLine(line)))));
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
                await CompareAsync(); liveLog.Activate();
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

        internal void ApplyBuildFolderFilter()
        {
            showObj = ShowObj == true; showBin = ShowBin == true;
            if (snapshot == null) return;
            RefreshChecks(); ApplyKindFilter();
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
            ComparisonCleared?.Invoke();
            DetachSelectionHandlers();
            FolderItems = null;
            FileItems = null;
            snapshot = null; rows.Clear();
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

        internal void RemoveFileFromFolderHierarchy(DiffFile file)
        {
            string path = Path.GetDirectoryName(file.RelativePath) ?? string.Empty;
            while (true)
            {
                DiffFolder folder;
                if (folders.TryGetValue(path, out folder))
                    folder.Files.Remove(file);
                if (path.Length == 0) break;
                path = Path.GetDirectoryName(path) ?? string.Empty;
            }
        }

        internal void AddFileToFolderHierarchy(DiffFile file)
        {
            string path = Path.GetDirectoryName(file.RelativePath) ?? string.Empty;
            while (true)
            {
                DiffFolder folder;
                if (folders.TryGetValue(path, out folder))
                    folder.Files.Add(file);
                if (path.Length == 0) break;
                path = Path.GetDirectoryName(path) ?? string.Empty;
            }
        }

        internal void UpdateSelectedFolderNodes(string requestedFolder, HashSet<string> refreshedFolders, Func<string, bool> inSelectedFolder)
        {
            var desired = new HashSet<string>(refreshedFolders ?? new HashSet<string>(), StringComparer.OrdinalIgnoreCase);
            if (requestedFolder.Length == 0)
                desired.Add(string.Empty);

            string[] stale = folders.Keys
                .Where(path => path.Length > 0 && inSelectedFolder(path) && !desired.Contains(path))
                .OrderByDescending(path => path.Length)
                .ToArray();

            foreach (string path in stale)
            {
                DiffFolder node;
                if (!folders.TryGetValue(path, out node)) continue;
                string parentPath = Path.GetDirectoryName(path) ?? string.Empty;
                DiffFolder parent;
                if (folders.TryGetValue(parentPath, out parent))
                    parent.Children.Remove(node);
                folders.Remove(path);
            }

            foreach (string path in desired
                .Where(path => path.Length > 0)
                .OrderBy(path => path.Count(ch => ch == '\\'))
                .ThenBy(path => path, StringComparer.OrdinalIgnoreCase))
            {
                DiffFolder node;
                if (!folders.TryGetValue(path, out node))
                {
                    node = CreateFolderNode(path, string.Equals(path, requestedFolder, StringComparison.OrdinalIgnoreCase));
                    folders.Add(path, node);
                    string parentPath = Path.GetDirectoryName(path) ?? string.Empty;
                    DiffFolder parent;
                    if (folders.TryGetValue(parentPath, out parent))
                        parent.Children.Add(node);
                }
                UpdateFolderExistence(node);
            }

            DiffFolder root;
            if (folders.TryGetValue(string.Empty, out root))
                UpdateFolderExistence(root);
        }

        internal DiffFolder CreateFolderNode(string path, bool expanded)
        {
            var node = new DiffFolder
            {
                Path = path,
                Expanded = expanded,
                Active = false
            };
            node.IncludeFile = IncludeBuildFolderFile;
            node.Toggle = (folder, value) =>
            {
                bulk = true;
                foreach (DiffFile file in folder.Files)
                    if (file.CanSync && IncludeBuildFolderFile(file) && (file.Kind & kindMask) != 0)
                        file.Selected = value;
                bulk = false;
                RefreshChecks();
            };
            UpdateFolderExistence(node);
            return node;
        }

        internal void UpdateFolderExistence(DiffFolder node)
        {
            string sourcePath = node.Path.Length == 0
                ? snapshot.SourceRoot.TrimEnd('\\')
                : System.IO.Path.Combine(snapshot.SourceRoot, node.Path);
            string targetPath = node.Path.Length == 0
                ? snapshot.TargetRoot.TrimEnd('\\')
                : System.IO.Path.Combine(snapshot.TargetRoot, node.Path);

            node.SourceExists = Directory.Exists(sourcePath);
            node.TargetExists = Directory.Exists(targetPath);
            node.SourceEmpty = node.SourceExists && !Directory.EnumerateFileSystemEntries(sourcePath).Any();
            node.TargetEmpty = node.TargetExists && !Directory.EnumerateFileSystemEntries(targetPath).Any();
        }

        internal void RefreshSelectedFolderPresentation(string requestedFolder, Func<string, bool> inSelectedFolder)
        {
            Func<string, bool> isAncestor = path =>
                path.Length == 0 ||
                requestedFolder.Length == 0 ||
                string.Equals(path, requestedFolder, StringComparison.OrdinalIgnoreCase) ||
                requestedFolder.StartsWith(path + "\\", StringComparison.OrdinalIgnoreCase);

            DiffFolder[] affected = folders.Values
                .Where(folder => inSelectedFolder(folder.Path) || isAncestor(folder.Path))
                .ToArray();

            foreach (DiffFolder folder in affected)
            {
                folder.Refresh();
                folder.Mask = kindMask;
                folder.Visible = folder.Path.Length == 0 || folder.CountFor(kindMask) > 0;
                folder.Label = (folder.Path.Length == 0 ? RootLabel() : Path.GetFileName(folder.Path)) +
                               " (" + folder.CountFor(kindMask) + ")";
            }

            foreach (DiffFolder folder in affected.OrderByDescending(folder => folder.Path.Length))
                folder.UpdateDisplayChildren();

            cachedVisibleFolders = new HashSet<string>(
                folders.Values
                    .Where(folder => folder.Path.Length == 0 || folder.CountFor(DiffKind.Differences) > 0)
                    .Select(folder => folder.Path),
                StringComparer.OrdinalIgnoreCase);
        }

        internal void SelectNearestExistingFolder(string path)
        {
            string current = path ?? string.Empty;
            while (!folders.ContainsKey(current) && current.Length > 0)
                current = Path.GetDirectoryName(current) ?? string.Empty;

            foreach (DiffFolder folder in folders.Values)
                if (folder.Active) folder.Active = false;

            selectedFolder = folders.ContainsKey(current) ? current : string.Empty;
            DiffFolder selected;
            if (folders.TryGetValue(selectedFolder, out selected))
                selected.Active = true;
        }

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
            bool unknown = progress.Total == 0;
            ProgressIndeterminate = unknown;
            ProgressMaximum = Math.Max(1, progress.Total);
            ProgressValue = progress.Completed;
            string detail = unknown ? progress.Stage : progress.Stage + " — " + progress.Completed.ToString("N0") + " / " + progress.Total.ToString("N0");
            BusyMessage = string.IsNullOrEmpty(detail) ? "Please wait…" : detail;
            Status = progress.Stage + (unknown ? "" : " — " + progress.Completed.ToString("N0") + " / " + progress.Total.ToString("N0") + " items");
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
                node.Toggle = (folder, value) => { bulk = true; foreach (DiffFile file in folder.Files) if (file.CanSync && IncludeBuildFolderFile(file) && (file.Kind & kindMask) != 0) file.Selected = value; bulk = false; RefreshChecks(); };
                folders.Add(path, node);
                if (path.Length > 0) folders[Path.GetDirectoryName(path) ?? ""].Children.Add(node);

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
                cachedVisibleFolders = new HashSet<string>(folders.Values.Where(f => f.Path.Length == 0 || f.CountFor(DiffKind.Differences) > 0).Select(f => f.Path), StringComparer.OrdinalIgnoreCase);

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
                folder.Visible = folder.Path.Length == 0 || (snapshot != null ? folder.CountFor(kindMask) > 0 : cachedVisibleFolders == null || cachedVisibleFolders.Contains(folder.Path));
                folder.Label = (folder.Path.Length == 0 ? RootLabel() : Path.GetFileName(folder.Path)) + (snapshot == null ? " (not compared)" : " (" + folder.CountFor(kindMask) + ")");

                if (++processed % 128 == 0)
                    await Dispatcher.Yield(DispatcherPriority.Background);
            }

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
                folder.Visible = folder.Path.Length == 0 || (snapshot != null ? folder.CountFor(kindMask) > 0 : cachedVisibleFolders == null || cachedVisibleFolders.Contains(folder.Path));
                folder.Label = (folder.Path.Length == 0 ? RootLabel() : Path.GetFileName(folder.Path)) + (snapshot == null ? " (not compared)" : " (" + folder.CountFor(kindMask) + ")");
            }
            if (!folders[selectedFolder].Visible)
            {
                folders[selectedFolder].Active = false; selectedFolder = ""; folders[""].Active = true;
            }
            foreach (DiffFolder folder in folders.Values) folder.UpdateDisplayChildren();
            FolderItems = new[] { folders[""] }; Filter(); UpdateSelectionSummary();
        }

        internal void RefreshChecks()
        {
            foreach (DiffFolder folder in folders.Values) folder.Refresh();
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
            int count = snapshot != null && folders.TryGetValue("", out root) ? root.SelectedCount : 0;
            int total = root == null ? 0 : root.AllDifferenceCount;
            int hidden = root == null ? 0 : count - root.SelectedFor(kindMask);
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
