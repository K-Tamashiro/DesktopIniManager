using DesktopIniManager.Properties;
using DesktopIniManager.Services;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Threading;
using System.Xml.Serialization;

namespace DesktopIniManager.ViewModels;

internal sealed partial class DeveloperDifferencerViewModel : ObservableObject
{
    private readonly ConfirmSyncCallback _confirmSync;

    private readonly IUserDialogService _dialogs;
    private readonly Dispatcher _dispatcher;
    private readonly Dictionary<string, DiffFolder> _folders = new(StringComparer.OrdinalIgnoreCase);
    private readonly Func<string, DiffSnapshot, ISynchronizationLog> _openLog;
    private readonly SemaphoreSlim _previewWorkers = new(2);
    private bool _appliedShowBin;
    private bool _appliedShowObj;
    private bool _bulk;
    private string _busyMessage = "Please wait\u2026";
    private HashSet<string> _cachedVisibleFolders;
    private bool _canCancel;
    private bool _canFilter;
    private bool _canRefresh;
    private bool _canSynchronize;
    private bool _checkedOnlyFilter;
    private bool _closed;
    private bool _compareAttributes;
    private CancellationTokenSource _compareCts;
    private bool _compareSize = true;
    private bool _compareTimestamp = true;
    private bool _comparing;
    private DiffIndex[] _comparisonIndexes;
    private string _countLabel = string.Empty;
    private HashSet<string> _disabledIgnoreRules = new(StringComparer.Ordinal);
    private IEnumerable<DiffRow> _fileItems;
    private string _filePanelTitle = "Files";
    private IReadOnlyList<DiffFolder> _folderItems;
    private bool _isBusy;
    private bool _isFileBusy;
    private bool _isProgressVisible;
    private DiffKind _kindMask = DiffKind.Differences;
    private Task _pendingRootLink = Task.CompletedTask;
    private bool _preciseCompare;
    private int _previewInFlight;
    private CancellationTokenSource _previewScope = new();
    private bool _progressIndeterminate;
    private double _progressMaximum = 1;
    private double _progressValue;

    private int _rootLinkVersion;
    private List<DiffRow> _rows = new();
    private bool _selectAllFiles;
    private string _selectedFolder = "";
    private DiffRow _selectedRow;
    private bool _showDifferent = true;
    private bool _showObj, _showBin;
    private bool _showSame;
    private bool _showSourceOnly = true;
    private bool _showTargetOnly = true;
    private PendingIndex _sourceIndex, _targetIndex;
    private string _sourcePath = string.Empty;
    private string _status = "Ready";
    private ObservableCollection<DiffRow> _streamingRows;
    private bool _syncingTreeFromFile;
    private string _targetPath = string.Empty;
    private bool _treeCompact = SettingsService.LoadTreeCompact();
    private string _treeSource;

    internal DeveloperDifferencerViewModel(IUserDialogService dialogs, Dispatcher dispatcher,
        ConfirmSyncCallback confirmSync, Func<string, DiffSnapshot, ISynchronizationLog> openLog)
    {
        _dialogs = dialogs;
        _dispatcher = dispatcher;
        _confirmSync = confirmSync;
        _openLog = openLog;
        BrowseSourceCommand = new RelayCommand(() => BrowseFolder(true), () => !IsBusy);
        BrowseTargetCommand = new RelayCommand(() => BrowseFolder(false), () => !IsBusy);
        CloseCommand = new RelayCommand(() => CloseRequested?.Invoke(), () => !IsBusy);
        CompactTreeCommand = new RelayCommand(() => TreeCompact = true);
        ComfortableTreeCommand = new RelayCommand(() => TreeCompact = false);
        OpenDiffCommand = new RelayCommand(OpenDiff, () => !IsBusy && Snapshot != null && SelectedRow != null);
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
        ExpandAllCommand = new RelayCommand(() =>
        {
            foreach (var folder in _folders.Values) folder.Expanded = true;
        });
        CollapseAllCommand = new RelayCommand(() =>
        {
            foreach (var folder in _folders.Values) folder.Expanded = false;
        });
        DeleteHistoryTabCommand = new ParameterCommand(DeleteHistoryTab, () => !IsBusy);
        DeleteAllHistoryTabsCommand = new RelayCommand(DeleteAllHistoryTabs, () => !IsBusy && HistoryTabs.Count > 0);
        DeleteOtherHistoryTabsCommand =
            new ParameterCommand(DeleteOtherHistoryTabs, () => !IsBusy && HistoryTabs.Count > 1);
    }

    internal static string StateDirectory => AppSlot.Directory;
    internal static string StatePath { get; set; } = Path.Combine(StateDirectory, "developer-differencer.xml");

    public bool TreeCompact
    {
        get => _treeCompact;
        set => SetProperty(ref _treeCompact, value);
    }

    public DiffRow SelectedRow
    {
        get => _selectedRow;
        set
        {
            if (!SetProperty(ref _selectedRow, value)) return;
            OpenDiffCommand?.NotifyCanExecuteChanged();
            if (!_syncingTreeFromFile && !IsBusy && Snapshot != null && value != null)
                RevealContainingFolder(Path.GetDirectoryName(value.File.RelativePath) ?? "");
        }
    }

    public Func<string, string, string> ChooseFolder { get; set; }
    public RelayCommand BrowseSourceCommand { get; }
    public RelayCommand BrowseTargetCommand { get; }
    public RelayCommand CloseCommand { get; }
    public RelayCommand CompactTreeCommand { get; }
    public RelayCommand ComfortableTreeCommand { get; }
    public RelayCommand OpenDiffCommand { get; }
    public DiffSnapshot Snapshot { get; private set; }

    public bool CanConfigureIgnore => !IsBusy && (Snapshot?.IgnoreRules?.Count > 0 ||
                                                  _sourceIndex != null || _targetIndex != null);

    public IReadOnlyDictionary<string, DiffFolder> Folders => _folders;

    public string SourcePath
    {
        get => _sourcePath;
        set => ApplyRootPath(true, value);
    }

    public string TargetPath
    {
        get => _targetPath;
        set => ApplyRootPath(false, value);
    }

    public bool IsSourceIndexing => !_closed && _sourceIndex != null && !_sourceIndex.Task.IsCompleted;
    public bool IsTargetIndexing => !_closed && _targetIndex != null && !_targetIndex.Task.IsCompleted;

    public string Status
    {
        get => _status;
        set => SetProperty(ref _status, value);
    }

    public string CountLabel
    {
        get => _countLabel;
        set => SetProperty(ref _countLabel, value);
    }

    public string FilePanelTitle
    {
        get => _filePanelTitle;
        set => SetProperty(ref _filePanelTitle, value);
    }

    public string BusyMessage
    {
        get => _busyMessage;
        set => SetProperty(ref _busyMessage, value);
    }

    public IReadOnlyList<DiffFolder> FolderItems
    {
        get => _folderItems;
        set => SetProperty(ref _folderItems, value);
    }

    public IEnumerable<DiffRow> FileItems
    {
        get => _fileItems;
        set => SetProperty(ref _fileItems, value);
    }

    public bool ProgressIndeterminate
    {
        get => _progressIndeterminate;
        set => SetProperty(ref _progressIndeterminate, value);
    }

    public double ProgressMaximum
    {
        get => _progressMaximum;
        set => SetProperty(ref _progressMaximum, value);
    }

    public double ProgressValue
    {
        get => _progressValue;
        set => SetProperty(ref _progressValue, value);
    }

    public bool CompareTimestamp
    {
        get => _compareTimestamp;
        set => SetProperty(ref _compareTimestamp, value);
    }

    public bool CompareSize
    {
        get => _compareSize;
        set => SetProperty(ref _compareSize, value);
    }

    public bool CompareAttributes
    {
        get => _compareAttributes;
        set => SetProperty(ref _compareAttributes, value);
    }

    public bool PreciseCompare
    {
        get => _preciseCompare;
        set => SetProperty(ref _preciseCompare, value);
    }

    public bool ShowObj
    {
        get => _showObj;
        set => SetProperty(ref _showObj, value);
    }

    public bool ShowBin
    {
        get => _showBin;
        set => SetProperty(ref _showBin, value);
    }

    public bool ShowSame
    {
        get => _showSame;
        set => SetProperty(ref _showSame, value);
    }

    public bool ShowDifferent
    {
        get => _showDifferent;
        set => SetProperty(ref _showDifferent, value);
    }

    public bool ShowSourceOnly
    {
        get => _showSourceOnly;
        set => SetProperty(ref _showSourceOnly, value);
    }

    public bool ShowTargetOnly
    {
        get => _showTargetOnly;
        set => SetProperty(ref _showTargetOnly, value);
    }

    public bool SelectAllFiles
    {
        get => _selectAllFiles;
        set
        {
            if (!SetProperty(ref _selectAllFiles, value)) return;
            ApplySelectAllFiles(value);
        }
    }

    public bool IsBusy
    {
        get => _isBusy;
        set => SetProperty(ref _isBusy, value);
    }

    public bool IsProgressVisible
    {
        get => _isProgressVisible;
        set => SetProperty(ref _isProgressVisible, value);
    }

    public bool IsFileBusy
    {
        get => _isFileBusy;
        set => SetProperty(ref _isFileBusy, value);
    }

    public bool CanFilter
    {
        get => _canFilter;
        set => SetProperty(ref _canFilter, value);
    }

    public bool CanRefresh
    {
        get => _canRefresh;
        set => SetProperty(ref _canRefresh, value);
    }

    public bool CanSynchronize
    {
        get => _canSynchronize;
        set => SetProperty(ref _canSynchronize, value);
    }

    public bool CheckedOnlyFilter
    {
        get => _checkedOnlyFilter;
        set
        {
            if (!SetProperty(ref _checkedOnlyFilter, value)) return;
            if (Snapshot == null) return;
            if (value)
            {
                ApplyCheckedOnlyFolderCollapse();
                Filter();
            }
            else
            {
                ApplyKindFilter();
            }
        }
    }

    public bool CanCancel
    {
        get => _canCancel;
        set
        {
            if (SetProperty(ref _canCancel, value)) CancelCommand?.NotifyCanExecuteChanged();
        }
    }

    public bool CanEdit => !IsBusy;
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
    public Func<IReadOnlyList<string>, string, SolutionCleanSelection> ChooseCleanSolutions { get; set; }

    public event Action CloseRequested;

    public event Action<bool> CommitBrowsedRootHistoryRequested;

    public event Action<DiffSnapshot, DiffFile> DiffRequested;

    public event Action<IReadOnlyList<DiffFolder>> FolderRevealRequested;

    public event Action<DiffRow> FileProgressRequested;

    internal async Task<DiffSnapshot> GetIgnoreSnapshotAsync()
    {
        if (_closed || IsBusy) return null;
        if (Snapshot != null) return Snapshot;
        var left = _sourceIndex;
        var right = _targetIndex;
        if (left == null && right == null) return null;
        var indexes = await Task.WhenAll(
            left?.Task ?? Task.FromResult<DiffIndex>(null),
            right?.Task ?? Task.FromResult<DiffIndex>(null));
        if (_closed || IsBusy || !ReferenceEquals(left, _sourceIndex) || !ReferenceEquals(right, _targetIndex))
            return null;
        if (left?.Error != null) throw left.Error;
        if (right?.Error != null) throw right.Error;
        var rules = GitIgnoreMatcher.Merge(indexes[0]?.IgnoreRules, indexes[1]?.IgnoreRules);
        if (rules.Count == 0) return null;
        return new DiffSnapshot
        {
            SourceRoot = SourcePath,
            TargetRoot = TargetPath,
            IgnoreRules = rules,
            DisabledIgnoreRules = _disabledIgnoreRules.ToList()
        };
    }

    internal async Task ApplyIgnoreRulesAsync(IEnumerable<string> disabled)
    {
        if (!CanConfigureIgnore) return;
        var ignoreSnapshot = await GetIgnoreSnapshotAsync();
        if (ignoreSnapshot == null) return;
        var selection = new HashSet<string>(disabled, StringComparer.Ordinal);
        var visible = new HashSet<string>(ignoreSnapshot.IgnoreRules.Select(r => r.Key), StringComparer.Ordinal);
        selection.UnionWith(_disabledIgnoreRules.Where(key => !visible.Contains(key)));
        if (_disabledIgnoreRules.SetEquals(selection)) return;
        var previousTab = _selectedHistoryTab;
        var hadComparison = Snapshot != null;
        _disabledIgnoreRules = selection;
        InvalidateIndexes();
        if (!hadComparison)
        {
            RestartIndex(true);
            RestartIndex(false);
            return;
        }

        await CompareCoreAsync(true);
        if (Snapshot == null && previousTab != null && HistoryTabs.Contains(previousTab))
            await SelectHistoryTabAsync(previousTab);
    }

    public void SelectFolder(string path)
    {
        if (_syncingTreeFromFile || IsBusy ||
            string.Equals(_selectedFolder, path ?? "", StringComparison.OrdinalIgnoreCase)) return;
        _selectedFolder = path ?? "";

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

    internal void ApplyRootPath(bool source, string path, bool force = false)
    {
        path = path ?? string.Empty;
        var current = source ? SourcePath : TargetPath;
        if (!force && string.Equals(MainWindowViewModel.TryNormalizeFolderPath(current),
                MainWindowViewModel.TryNormalizeFolderPath(path), StringComparison.OrdinalIgnoreCase)) return;
        if (!string.Equals(MainWindowViewModel.TryNormalizeFolderPath(current),
                MainWindowViewModel.TryNormalizeFolderPath(path), StringComparison.OrdinalIgnoreCase))
        {
            _disabledIgnoreRules.Clear();
            InvalidateIndexes();
        }

        if (source)
        {
            var changed = SetProperty(ref _sourcePath, path, nameof(SourcePath));
            if (!changed && !force) return;
            if (!changed) OnPropertyChanged(nameof(SourcePath));
            ClearComparisonView();
            RestartIndex(true);
            if (changed) _pendingRootLink = LinkTargetFolderAsync(path, TargetPath);
        }
        else
        {
            var changed = SetProperty(ref _targetPath, path, nameof(TargetPath));
            if (!changed && !force) return;
            _rootLinkVersion++;
            if (!changed) OnPropertyChanged(nameof(TargetPath));
            ClearComparisonView();
            RestartIndex(false);
        }
    }

    private async Task LinkTargetFolderAsync(string source, string target)
    {
        var version = ++_rootLinkVersion;
        // Text input is debounced; network lookups must not block the UI thread.
        await Task.Delay(300);
        if (_closed || IsBusy || version != _rootLinkVersion || SourcePath != source || TargetPath != target) return;
        var match = await Task.Run(() => FindTargetHistoryMatch(source,
            new InputHistoryStore(Path.Combine(AppSlot.Directory, "input-history")).Load("Differencer-Target")));
        if (match != null && !_closed && !IsBusy && version == _rootLinkVersion && SourcePath == source &&
            TargetPath == target)
            ApplyRootPath(false, match);
    }

    internal void MatchTargetHistory()
    {
        if (!_closed && !IsBusy) _pendingRootLink = LinkTargetFolderAsync(SourcePath, TargetPath);
    }

    internal void CancelTargetHistoryMatch()
    {
        _rootLinkVersion++;
    }

    internal static string FindTargetHistoryMatch(string source, IEnumerable<string> history)
    {
        string FolderName(string path)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(path) || !Path.IsPathFullyQualified(path)) return null;
                var directory = new DirectoryInfo(Path.GetFullPath(path));
                return directory.Parent == null ? null : directory.Name;
            }
            catch (ArgumentException)
            {
                return null;
            }
            catch (NotSupportedException)
            {
                return null;
            }
            catch (IOException)
            {
                return null;
            }
        }

        var name = FolderName(source);
        if (name == null || history == null) return null;
        foreach (var entry in history)
            if (string.Equals(name, FolderName(entry), StringComparison.OrdinalIgnoreCase))
                return entry;
        return null;
    }

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
        if (!_closed && (ReferenceEquals(_sourceIndex, pending) || ReferenceEquals(_targetIndex, pending)))
        {
            NotifyIndexProgress();
            OnPropertyChanged(nameof(CanConfigureIgnore));
        }
    }

    private void RestartIndex(bool source)
    {
        _compareCts?.Cancel();
        if (source)
        {
            _sourceIndex?.Cancel();
            _sourceIndex = _closed || string.IsNullOrWhiteSpace(SourcePath)
                ? null
                : new PendingIndex(SourcePath, _disabledIgnoreRules);
        }
        else
        {
            _targetIndex?.Cancel();
            _targetIndex = _closed || string.IsNullOrWhiteSpace(TargetPath)
                ? null
                : new PendingIndex(TargetPath, _disabledIgnoreRules);
        }

        _ = ObserveIndexProgressAsync(source ? _sourceIndex : _targetIndex);
        OnPropertyChanged(nameof(CanConfigureIgnore));
    }

    internal void RescanRoot(bool source)
    {
        if (!IsBusy)
            ApplyRootPath(source, source ? SourcePath : TargetPath, true);
    }

    private void InvalidateIndexes()
    {
        _sourceIndex?.Cancel();
        _targetIndex?.Cancel();
        _sourceIndex = _targetIndex = null;
        NotifyIndexProgress();
        OnPropertyChanged(nameof(CanConfigureIgnore));
    }

    public event Action<int> SelectionCountChanged;

    public event Action ComparisonCleared;

    public event Action CommitRootHistoryRequested;

    public event Action<bool?, bool> SyncDirectionIconRequested;

    internal void SetFilePanelBusy(bool busyPanel, string message = null)
    {
        IsFileBusy = busyPanel;
        if (message != null) BusyMessage = message;
    }

    internal void ShowError(Exception error)
    {
        _dialogs.Show(ErrorMessages.English(error), Strings.App_Title, MessageBoxButton.OK, MessageBoxImage.Error);
    }

    internal void Close()
    {
        _closed = true;
        _sourceIndex?.Cancel();
        _targetIndex?.Cancel();
        _compareCts?.Cancel();
        _compareCts?.Dispose();
        _previewScope.Cancel();
        _previewScope.Dispose();
        DetachSelectionHandlers();
    }

    private void BrowseFolder(bool source)
    {
        try
        {
            var path = ChooseFolder?.Invoke(source ? SourcePath : TargetPath,
                source ? "Source folder" : "Target folder");
            if (path == null) return;
            ApplyRootPath(source, path);
            if (source) MatchTargetHistory();
            else CancelTargetHistoryMatch();
            CommitBrowsedRootHistoryRequested?.Invoke(source);
        }
        catch (Exception ex)
        {
            ShowError(ex);
        }
    }

    private void OpenDiff()
    {
        if (IsBusy || Snapshot == null || SelectedRow == null) return;
        var file = SelectedRow.File;
        if (DiffMedia.IsBinary(file.RelativePath))
        {
            _dialogs.Show(DiffMedia.BinaryMessage, "Diff View", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        try
        {
            DiffRequested?.Invoke(Snapshot, file);
        }
        catch (Exception ex)
        {
            ShowError(ex);
        }
    }

    internal IReadOnlyList<DiffFile> GetVisibleComparableFiles()
    {
        var visible = FileItems ?? Enumerable.Empty<DiffRow>();
        return visible
            .Select(row => row?.File)
            .Where(file => file != null && !DiffMedia.IsBinary(file.RelativePath))
            .ToList();
    }

    internal DiffRow SelectDisplayedFile(DiffFile file)
    {
        if (file == null) return null;
        var row = _rows.FirstOrDefault(item =>
            ReferenceEquals(item.File, file) ||
            string.Equals(item.File.RelativePath, file.RelativePath, StringComparison.OrdinalIgnoreCase));
        if (row == null) return null;
        SelectedRow = row;
        return row;
    }

    internal async Task LoadPreviewAsync(DiffRow row)
    {
        if (_closed || row == null || (Snapshot == null && !_comparing)) return;
        var wait = !row.IsPreviewReady;
        if (wait) _previewInFlight++;
        try
        {
            await row.LoadPreviewAsync(_previewWorkers, _previewScope.Token);
        }
        finally
        {
            if (wait)
            {
                _previewInFlight--;
                if (!_closed && _previewInFlight <= 0 && !IsBusy) SetFilePanelBusy(false);
            }
        }
    }

    private void RevealContainingFolder(string path)
    {
        if (_folders.Count == 0) return;
        DiffFolder target;
        var current = path ?? "";
        while (true)
        {
            if (_folders.TryGetValue(current, out target) && target.Visible) break;
            if (current.Length == 0)
            {
                target = _folders.ContainsKey("") ? _folders[""] : null;
                break;
            }

            current = Path.GetDirectoryName(current) ?? "";
        }

        if (target == null) return;

        var ancestor = target.Path;
        while (ancestor.Length > 0)
        {
            ancestor = Path.GetDirectoryName(ancestor) ?? "";
            DiffFolder parent;
            if (_folders.TryGetValue(ancestor, out parent)) parent.Expanded = true;
        }

        _syncingTreeFromFile = true;
        foreach (var folder in _folders.Values)
            if (folder.Active && folder != target)
                folder.Active = false;
        target.Active = true;
        var pathToTarget = new List<DiffFolder>();
        var folderPath = target.Path ?? "";
        while (true)
        {
            DiffFolder node;
            if (_folders.TryGetValue(folderPath, out node)) pathToTarget.Add(node);
            if (folderPath.Length == 0) break;
            folderPath = Path.GetDirectoryName(folderPath) ?? "";
        }

        pathToTarget.Reverse();
        if (FolderRevealRequested == null) CompleteFolderReveal();
        else FolderRevealRequested(pathToTarget);
    }

    internal void CompleteFolderReveal()
    {
        _syncingTreeFromFile = false;
    }

    internal void RestoreState()
    {
        try
        {
            if (!File.Exists(StatePath)) return;
            DifferencerState state;
            using (var stream = File.OpenRead(StatePath))
            {
                state = (DifferencerState)new XmlSerializer(typeof(DifferencerState)).Deserialize(stream)
                        ?? throw new InvalidDataException("The comparison state is empty.");
            }

            _sourcePath = DisplayRoot(state.Source);
            _targetPath = DisplayRoot(state.Target);
            OnPropertyChanged(nameof(SourcePath));
            OnPropertyChanged(nameof(TargetPath));
            _rootLinkVersion++; // Restoring saved paths must not initiate automatic linking.
            _treeSource = SourcePath;
            _selectedFolder = "";
            _cachedVisibleFolders = null;
            RestartIndex(true);
            RestartIndex(false);
            Status = "Source and Target restored. Click Compare to build the difference tree.";
        }
        catch (Exception ex)
        {
            Status = "Failed to restore tree: " + ErrorMessages.English(ex);
        }
    }

    internal async Task SyncAsync(bool toTarget)
    {
        if (IsBusy || Snapshot == null) return;
        var files = Snapshot.Files.Where(f => f.Selected).ToArray();
        var selectedFolders = _folders.Values
            .Where(f => f.FolderCanSync && f.FolderSelected && IncludeBuildFolder(f.Path))
            .Select(f => new DiffFolderSync
            { RelativePath = f.Path, SourceExists = f.SourceExists, TargetExists = f.TargetExists })
            .ToArray();
        if (files.Length == 0 && selectedFolders.Length == 0) return;
        var direction = toTarget ? "Source to Target" : "Target to Source";
        bool confirmed;
        SyncDirectionIconRequested?.Invoke(toTarget, false);
        try
        {
            confirmed = _confirmSync(direction, files, selectedFolders, toTarget, false, out _, out _);
        }
        finally
        {
            SyncDirectionIconRequested?.Invoke(null, false);
        }

        if (!confirmed) return;
        InvalidateIndexes();
        var liveLog = _openLog(direction, Snapshot);
        SetBusy(true);
        Status = direction + " \u2014 syncing\u2026";
        try
        {
            List<string> log;
            try
            {
                var current = Snapshot;
                log = await Task.Run(() => DeveloperDifferencerService.Synchronize(current, files, toTarget,
                    line => _dispatcher.Invoke(() => liveLog.AppendLine(line)), selectedFolders));
            }
            catch (Exception ex)
            {
                log = new List<string> { "FAIL " + ErrorMessages.English(ex) };
                liveLog.AppendLine(log[0]);
            }

            var report = direction + "\n" + DateTime.Now.ToString("O") + "\n" + string.Join("\n", log);
            try
            {
                Directory.CreateDirectory(StateDirectory);
                var path = Path.Combine(StateDirectory,
                    "differencer-sync-" + DateTime.Now.ToString("yyyyMMdd-HHmmss-fff") + ".log");
                File.WriteAllText(path, report);
                liveLog.AppendLine("Log: " + path);
            }
            catch (Exception ex)
            {
                liveLog.AppendLine("Failed to save log: " + ErrorMessages.English(ex));
            }

            liveLog.Complete(log.Count(l => l.StartsWith("OK ")), log.Count(l => l.StartsWith("FAIL ")),
                log.Count(l => l.StartsWith("LOCKED ")));
            liveLog.Activate();
        }
        finally
        {
            SetBusy(false);
        }

        // Synchronization changes files on disk. Rebuild the comparison only
        // after the busy state has been released; ZIP creation does not do this.
        await CompareAsync();
        liveLog.Activate();
    }

    internal async Task ZipAsync(bool fromSource)
    {
        if (IsBusy || Snapshot == null) return;
        var files = Snapshot.Files.Where(f => f.Selected).ToArray();
        var selectedFolders = _folders.Values
            .Where(f => f.FolderCanSync && f.FolderSelected && IncludeBuildFolder(f.Path))
            .Select(f => new DiffFolderSync
            { RelativePath = f.Path, SourceExists = f.SourceExists, TargetExists = f.TargetExists })
            .ToArray();
        if (files.Length == 0 && selectedFolders.Length == 0) return;
        var direction = fromSource
            ? StringOverlay.Get("Differencer_ZipSourceToZip")
            : StringOverlay.Get("Differencer_ZipTargetToZip");
        bool confirmed;
        string zipFileName;
        string zipFolder;
        SyncDirectionIconRequested?.Invoke(fromSource, true);
        try
        {
            confirmed = _confirmSync(direction, files, selectedFolders, fromSource, true, out zipFileName,
                out zipFolder);
        }
        finally
        {
            SyncDirectionIconRequested?.Invoke(null, false);
        }

        if (!confirmed) return;
        string zipPath;
        try
        {
            zipPath = DeveloperDifferencerService.ComposeZipPath(zipFolder, zipFileName);
        }
        catch (Exception ex)
        {
            ShowError(ex);
            return;
        }

        var liveLog = _openLog(direction, Snapshot);
        SetBusy(true);
        Status = StringOverlay.Get("Differencer_Zipping").Replace("{0}", direction);
        try
        {
            List<string> log;
            try
            {
                var current = Snapshot;
                log = await Task.Run(() => DeveloperDifferencerService.PackZip(current, files, selectedFolders,
                    fromSource, zipPath,
                    line => _dispatcher.Invoke(() => liveLog.AppendLine(line))));
            }
            catch (Exception ex)
            {
                log = new List<string> { "FAIL " + ErrorMessages.English(ex) };
                liveLog.AppendLine(log[0]);
            }

            var report = direction + "\n" + DateTime.Now.ToString("O") + "\n" + zipPath + "\n" + string.Join("\n", log);
            try
            {
                Directory.CreateDirectory(StateDirectory);
                var path = Path.Combine(StateDirectory,
                    "differencer-zip-" + DateTime.Now.ToString("yyyyMMdd-HHmmss-fff") + ".log");
                File.WriteAllText(path, report);
                liveLog.AppendLine("Log: " + path);
            }
            catch (Exception ex)
            {
                liveLog.AppendLine("Failed to save log: " + ErrorMessages.English(ex));
            }

            liveLog.Complete(log.Count(l => l.StartsWith("OK ")), log.Count(l => l.StartsWith("FAIL ")),
                log.Count(l => l.StartsWith("LOCKED ")));
            liveLog.Activate();
        }
        finally
        {
            SetBusy(false);
        }
    }

    public event Action<string, string, string> CleanReportRequested;

    internal async Task CleanAsync()
    {
        if (IsBusy) return;
        SetBusy(true);
        CanCancel = false;
        var runComparison = false;
        string cleanSummary = null, cleanLogPath = null, cleanReport = null;
        try
        {
            string source = SourcePath, target = TargetPath;
            Status = Strings.Differencer_FindingSolutions;
            var solutions = await Task.Run(() => SolutionCleanService.FindSolutions(source, target));
            if (solutions.Count == 0)
            {
                Status = Strings.Differencer_NoSolutions;
                return;
            }

            var selection = ChooseCleanSolutions?.Invoke(solutions, source);
            if (selection == null)
            {
                Status = Strings.Differencer_CleanCancelled;
                return;
            }

            InvalidateIndexes();
            var configurations = selection.Configurations;
            var msbuild = await Task.Run(() => SolutionCleanService.FindMsBuild());
            ClearComparisonView();
            IsProgressVisible = true;
            ProgressIndeterminate = true;

            var details = new StringBuilder();
            int failures = 0, completed = 0;
            var total = selection.Solutions.Count() * configurations.Length;
            foreach (var solution in selection.Solutions)
                foreach (var config in configurations)
                {
                    var label = Path.GetFileName(solution) + " [" + config + "]";
                    Status = string.Format(Strings.Differencer_Cleaning, solution + " [" + config + "]");
                    SetFilePanelBusy(true, string.Format(Strings.Differencer_Cleaning, label + "\u2026"));
                    completed++;
                    details.AppendLine(new string('\u2500', 56));
                    details.AppendLine("[" + completed + "/" + total + "] " + label);
                    details.AppendLine("Path: " + solution);
                    details.AppendLine();
                    try
                    {
                        var result = await Task.Run(() =>
                        {
                            string output;
                            var code = SolutionCleanService.Clean(msbuild, solution, config, out output);
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

            var summary = string.Format(Strings.Differencer_CleanComplete, completed - failures, failures);
            var report = new StringBuilder();
            report.AppendLine("Solution Clean");
            report.AppendLine("Date: " + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"));
            report.AppendLine("Total: " + completed + "    OK: " + (completed - failures) + "    FAIL: " + failures);
            report.AppendLine();
            report.Append(details);
            Directory.CreateDirectory(StateDirectory);
            var logPath = Path.Combine(StateDirectory,
                "solution-clean-" + DateTime.Now.ToString("yyyyMMdd-HHmmss-fff") + ".log");
            File.WriteAllText(logPath, report.ToString(), new UTF8Encoding(false));
            cleanSummary = summary;
            cleanLogPath = logPath;
            cleanReport = report.ToString();
            runComparison = true;
            Status = summary;
        }
        catch (Exception ex)
        {
            Status = string.Format(Strings.Differencer_CleanFailed, ErrorMessages.English(ex));
            ShowError(ex);
        }
        finally
        {
            IsProgressVisible = false;
            ProgressIndeterminate = false;
            SetFilePanelBusy(false);
            SetBusy(false);
        }

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
        var directory = Path.GetDirectoryName(file.RelativePath) ?? "";
        return directory.Split(new[] { '\\', '/' }, StringSplitOptions.RemoveEmptyEntries)
            .All(part => (_showObj || !part.Equals("obj", StringComparison.OrdinalIgnoreCase)) &&
                         (_showBin || !part.Equals("bin", StringComparison.OrdinalIgnoreCase)));
    }

    internal bool IncludeBuildFolder(string path)
    {
        if (string.IsNullOrEmpty(path)) return true;
        return path.Split(new[] { '\\', '/' }, StringSplitOptions.RemoveEmptyEntries)
            .All(part => (_appliedShowObj || !part.Equals("obj", StringComparison.OrdinalIgnoreCase)) &&
                         (_appliedShowBin || !part.Equals("bin", StringComparison.OrdinalIgnoreCase)));
    }

    internal void ApplyBuildFolderFilter()
    {
        _appliedShowObj = ShowObj;
        _appliedShowBin = ShowBin;
        if (Snapshot == null) return;

        _bulk = true;
        foreach (var file in Snapshot.Files)
            if (!IncludeBuildFolderFile(file))
                file.Selected = false;

        foreach (var folder in _folders.Values)
            if (!IncludeBuildFolder(folder.Path))
                folder.SetFolderSelected(false, false);
        _bulk = false;

        RefreshChecks();
        ApplyKindFilter();

        // OBJ/BIN filter changes alter the effective folder state.
        // Re-evaluate every tree icon from the filtered counts.
        foreach (var folder in _folders.Values)
            folder.RefreshIcon();

        Status = _folders[""].CountFor(DiffKind.Differences) + " differences / " +
                 _folders[""].CountFor(DiffKind.Same) +
                 " identical (OBJ/BIN filters applied). Check items to synchronize.";
    }

    private void ApplySelectAllFiles(bool value)
    {
        if (IsBusy || Snapshot == null) return;

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
        var sameVisible = ShowSame;

        _bulk = true;
        try
        {
            foreach (var file in Snapshot.Files)
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
            _bulk = false;
            RefreshSelectionChecks();
        }
    }

    internal void ClearSameFolderSelection(DiffFolder folder)
    {
        if (IsBusy || Snapshot == null || folder == null || folder.CanSelect) return;

        _bulk = true;
        try
        {
            foreach (var file in folder.Files)
                if (file.Kind == DiffKind.Same && file.Selected && IncludeBuildFolderFile(file) &&
                    (file.Kind & _kindMask) != 0)
                    file.Selected = false;
        }
        finally
        {
            _bulk = false;
            RefreshSelectionChecks(folder);
        }
    }

    internal void ApplyCategoryFilter()
    {
        _kindMask = (ShowSame ? DiffKind.Same : 0) |
                    (ShowDifferent ? DiffKind.Different : 0) |
                    (ShowSourceOnly ? DiffKind.SourceOnly : 0) |
                    (ShowTargetOnly ? DiffKind.TargetOnly : 0);
        if (Snapshot != null) ApplyKindFilter();
    }

    internal void DetachSelectionHandlers()
    {
        if (Snapshot != null)
            foreach (var file in Snapshot.Files)
                file.PropertyChanged -= FileSelectionChanged;
    }

    internal void ClearComparisonView()
    {
        CaptureHistoryTab();
        _previewScope.Cancel();
        _previewScope.Dispose();
        _previewScope = new CancellationTokenSource();
        ComparisonCleared?.Invoke();
        DetachSelectionHandlers();
        FolderItems = null;
        FileItems = null;
        _streamingRows = null;
        Snapshot = null;
        _rows.Clear();
        OnPropertyChanged(nameof(CanConfigureIgnore));
        _selectedHistoryTab = null;
        NotifyHistory();
        _selectAllFiles = false;
        OnPropertyChanged(nameof(SelectAllFiles));
        SelectedRow = null;
        OpenDiffCommand.NotifyCanExecuteChanged();
        CanFilter = false;
        FilePanelTitle = "Files";
        UpdateSelectionSummary();
    }

    internal async Task RefreshSelectedFolderAsync()
    {
        if (IsBusy || Snapshot == null) return;

        // FileItems already contains the selected folder and all its descendants,
        // with the current category/selection filters applied.
        var targets = (FileItems ?? Enumerable.Empty<DiffRow>())
            .Select(row => row.File)
            .Distinct()
            .ToArray();
        if (targets.Length == 0) return;

        var compareTimestamp = CompareTimestamp;
        var preciseCompare = PreciseCompare;
        var compareSize = CompareSize;
        var compareAttributes = CompareAttributes;
        var refreshed = 0;
        SetBusy(true);
        CanCancel = false;
        SetFilePanelBusy(true, "Refreshing visible files\u2026");
        Status = "Refreshing " + targets.Length + " visible file(s)\u2026";

        try
        {
            foreach (var file in targets)
                if (await RefreshFileAsync(file, false,
                        compareTimestamp, preciseCompare, compareSize,
                        compareAttributes))
                    refreshed++;

            // Rebuild the visible list only once; individual rows may have changed kind.
            ApplyKindFilter(true);
            Status = "Visible files refreshed: " + targets.Length + " checked, " + refreshed +
                     " updated. " + _folders[""].CountFor(DiffKind.Differences) +
                     " differences / " + _folders[""].CountFor(DiffKind.Same) + " identical.";
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
        var parent = Path.GetDirectoryName(relativePath) ?? string.Empty;
        return string.Equals(parent, folder ?? string.Empty, StringComparison.OrdinalIgnoreCase);
    }

    internal bool SelectedFolderHasDirectFiles()
    {
        return FileItems != null && FileItems.Any();
    }

    internal void UpdateRefreshButtonState()
    {
        CanRefresh = !IsBusy && !_comparing && Snapshot != null && SelectedFolderHasDirectFiles();
        RefreshCommand.NotifyCanExecuteChanged();
    }

    internal void CancelCompare()
    {
        if (!_comparing) return;
        try
        {
            _compareCts?.Cancel();
        }
        catch (ObjectDisposedException)
        {
        }

        Status = "Cancelling comparison\u2026";
        CanCancel = false;
    }

    internal void ResetAfterComparison()
    {
        _selectedFolder = "";
        foreach (var folder in _folders.Values)
        {
            folder.Expanded = false;
            folder.Active = folder.Path.Length == 0;
        }

        _syncingTreeFromFile = true;
        try
        {
            SelectedRow = FileItems?.FirstOrDefault();
        }
        finally
        {
            _syncingTreeFromFile = false;
        }
    }

    internal event Action ComparisonCompleted;

    internal Task CompareAsync()
    {
        return CompareCoreAsync(false);
    }

    private async Task CompareCoreAsync(bool replaceCurrent)
    {
        if (IsBusy) return;
        await _pendingRootLink;
        if (_closed || IsBusy || (!replaceCurrent && !PrepareComparisonTab())) return;
        var replacedTab = replaceCurrent ? _selectedHistoryTab : null;
        CaptureHistoryTab();
        CommitRootHistoryRequested?.Invoke();
        ClearComparisonView();
        SetBusy(true);
        _comparing = true;
        CanCancel = true;
        SetFilePanelBusy(false);
        _streamingRows = new ObservableCollection<DiffRow>();
        FileItems = _streamingRows;
        _compareCts?.Dispose();
        _compareCts = new CancellationTokenSource();
        var token = _compareCts.Token;
        IsProgressVisible = true;
        ProgressIndeterminate = true;
        Status = PreciseCompare
            ? "Scanning files and calculating XXH128 hashes\u2026"
            : "Scanning files and comparing timestamps and sizes\u2026";
        string source = SourcePath, target = TargetPath;
        if (_sourceIndex != null && !string.Equals(_sourceIndex.Path, source, StringComparison.OrdinalIgnoreCase))
        {
            _sourceIndex.Cancel();
            _sourceIndex = null;
        }

        if (_targetIndex != null && !string.Equals(_targetIndex.Path, target, StringComparison.OrdinalIgnoreCase))
        {
            _targetIndex.Cancel();
            _targetIndex = null;
        }

        var leftIndex = _sourceIndex ?? (_sourceIndex = new PendingIndex(source, _disabledIgnoreRules));
        var rightIndex = _targetIndex ?? (_targetIndex = new PendingIndex(target, _disabledIgnoreRules));
        _ = ObserveIndexProgressAsync(leftIndex);
        _ = ObserveIndexProgressAsync(rightIndex);
        using var classification = CancellationTokenSource.CreateLinkedTokenSource(token);
        var classificationToken = classification.Token;
        try
        {
            var progress = new Progress<DiffProgress>(value =>
            {
                if (!classificationToken.IsCancellationRequested) UpdateProgress(value);
            });
            var compareTimestamp = CompareTimestamp;
            BusyMessage = Status = "Preparing source / target indexes\u2026";
            var indexes = await Task.WhenAll(leftIndex.Task, rightIndex.Task).WaitAsync(token);
            token.ThrowIfCancellationRequested();
            if (leftIndex.Error != null) throw leftIndex.Error;
            if (rightIndex.Error != null) throw rightIndex.Error;
            if (indexes[0] == null || indexes[1] == null) throw new OperationCanceledException();
            _comparisonIndexes = indexes;
            if (!indexes[0].Folders.Contains(_selectedFolder) && !indexes[1].Folders.Contains(_selectedFolder))
                _selectedFolder = "";
            var displayFolder = _selectedFolder;
            var pathComparer = StringComparer.CurrentCultureIgnoreCase;
            var fresh = await Task.Run(() => DeveloperDifferencerService.Compare(indexes[0], indexes[1], progress,
                compareTimestamp, token,
                batch => _dispatcher.Invoke(() =>
                {
                    token.ThrowIfCancellationRequested();
                    DiffRow last = null;
                    foreach (var file in batch)
                    {
                        var row = new DiffRow { File = file, SourceRoot = source, TargetRoot = target };
                        row.SetDirectInSelectedFolder(IsDirectChildFile(displayFolder, file.RelativePath));
                        _rows.Add(row);
                        if (!IncludeBuildFolderFile(file) || (file.Kind & _kindMask) == 0 ||
                            (_checkedOnlyFilter && !file.Selected) ||
                            (displayFolder.Length > 0 && !file.RelativePath.StartsWith(displayFolder + "\\",
                                StringComparison.OrdinalIgnoreCase))) continue;
                        _streamingRows.Add(row);
                        last = row;
                    }

                    FilePanelTitle = "Files \u2014 " + (displayFolder.Length == 0 ? "all levels" : displayFolder) +
                                     " (" + _streamingRows.Count + ")";
                    if (last != null) FileProgressRequested?.Invoke(last);
                }, DispatcherPriority.Background),
                (left, right) =>
                {
                    var direct = IsDirectChildFile(displayFolder, right)
                        .CompareTo(IsDirectChildFile(displayFolder, left));
                    if (direct != 0) return direct;
                    var name = pathComparer.Compare(Path.GetFileName(left), Path.GetFileName(right));
                    return name != 0 ? name : pathComparer.Compare(left, right);
                }, PreciseCompare, CompareSize, CompareAttributes), token);
            classification.Cancel();
            token.ThrowIfCancellationRequested();
            Status = "Updating the difference tree\u2026";
            BusyMessage = "Updating the difference tree and file list\u2026";
            Snapshot = fresh;
            _treeSource = source;
            await BuildTreeAsync(Snapshot.Folders, Array.Empty<string>(), "", token);
            ResetAfterComparison();
            ComparisonCompleted?.Invoke();
            Status = _folders[""].CountFor(DiffKind.Differences) + " differences / " +
                     _folders[""].CountFor(DiffKind.Same) + " identical. Check items to synchronize.";
            if (replacedTab != null)
            {
                _selectedHistoryTab = replacedTab;
                CaptureHistoryTab();
                NotifyHistory();
            }
            else
            {
                AddComparisonTab();
            }

            await SaveStateAsync();
        }
        catch (OperationCanceledException)
        {
            ClearComparisonView();
            Status = "Compare cancelled";
        }
        catch (Exception ex)
        {
            ClearComparisonView();
            Status = "Compare failed (sync disabled): " + ErrorMessages.English(ex);
            ShowError(ex);
        }
        finally
        {
            classification.Cancel();
            _comparisonIndexes = null;
            _streamingRows = null;
            SetFilePanelBusy(false);
            // A later comparison must see files/folders added or deleted since this run.
            leftIndex.Cancel();
            rightIndex.Cancel();
            if (ReferenceEquals(_sourceIndex, leftIndex)) _sourceIndex = null;
            if (ReferenceEquals(_targetIndex, rightIndex)) _targetIndex = null;
            NotifyIndexProgress();
            _comparing = false;
            IsProgressVisible = false;
            ProgressIndeterminate = false;
            SetBusy(false);
        }
    }

    internal void UpdateProgress(DiffProgress progress)
    {
        if (!_comparing) return;
        var counting = progress.Total <= 0;
        ProgressIndeterminate = counting;
        if (!counting)
        {
            ProgressMaximum = Math.Max(1, progress.Total);
            ProgressValue = Math.Max(0, Math.Min(progress.Completed, ProgressMaximum));
        }

        var detail = string.IsNullOrEmpty(progress.Stage) ? "Please wait\u2026" : progress.Stage;
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
        var saved = _restoringFolders;
        var indexes = _comparisonIndexes;
        var current = Snapshot;
        return Task.Run(() =>
        {
            var states = new Dictionary<string, HistoryFolderState>(StringComparer.OrdinalIgnoreCase);
            for (var i = 0; i < paths.Length; i++)
            {
                token.ThrowIfCancellationRequested();
                var path = paths[i];
                HistoryFolderState state;
                if (saved != null && saved.TryGetValue(path, out state))
                {
                    states[path] = state;
                }
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
                        var source = Path.Combine(current.SourceRoot, path);
                        var target = Path.Combine(current.TargetRoot, path);
                        state.SourceExists = Directory.Exists(source);
                        state.TargetExists = Directory.Exists(target);
                        state.SourceEmpty = state.SourceExists && !Directory.EnumerateFileSystemEntries(source).Any();
                        state.TargetEmpty = state.TargetExists && !Directory.EnumerateFileSystemEntries(target).Any();
                    }

                    states[path] = state;
                }

                if ((i + 1) % 128 == 0 || i + 1 == paths.Length)
                {
                    var done = i + 1;
                    _dispatcher.Invoke(() =>
                    {
                        if (!_closed && !token.IsCancellationRequested)
                            SetTreeProgress("Reading folder state\u2026", done, paths.Length);
                    }, DispatcherPriority.Background);
                }
            }

            return states;
        }, token).WaitAsync(token);
    }

    internal async Task BuildTreeAsync(IEnumerable<string> paths, IEnumerable<string> expanded, string selected,
        CancellationToken token)
    {
        var progressWasVisible = IsProgressVisible;
        try
        {
            SetTreeProgress("Preparing folders\u2026", 0, 0);
            await Dispatcher.Yield(DispatcherPriority.Background);
            token.ThrowIfCancellationRequested();
            _folders.Clear();
            var all = new HashSet<string>(paths, StringComparer.OrdinalIgnoreCase) { "" };
            var prepared = 0;
            foreach (var path in all.Where(p => p.Length > 0).ToArray())
            {
                token.ThrowIfCancellationRequested();
                var p = Path.GetDirectoryName(path);
                while (!string.IsNullOrEmpty(p))
                {
                    all.Add(p);
                    p = Path.GetDirectoryName(p);
                }

                if (++prepared % 256 == 0) await Dispatcher.Yield(DispatcherPriority.Background);
            }

            var expansion = new HashSet<string>(expanded ?? new string[0], StringComparer.OrdinalIgnoreCase);
            _selectedFolder = selected != null && all.Contains(selected) ? selected : "";
            var orderedPaths = all
                .OrderBy(p => p.Count(c => c == '\\' || c == '/'))
                .ThenBy(p => p, StringComparer.CurrentCultureIgnoreCase)
                .ToArray();
            var folderStates = await ReadFolderStatesAsync(orderedPaths, token);
            SetTreeProgress("Building folders\u2026", 0, orderedPaths.Length);

            var processed = 0;
            foreach (var path in orderedPaths)
            {
                token.ThrowIfCancellationRequested();

                var state = folderStates[path];

                var node = new DiffFolder
                {
                    Path = path,
                    Expanded = expansion.Contains(path) || path == "",
                    Active = path == _selectedFolder,
                    SourceExists = state.SourceExists,
                    TargetExists = state.TargetExists,
                    SourceEmpty = state.SourceEmpty,
                    TargetEmpty = state.TargetEmpty
                };
                node.IncludeFile = IncludeBuildFolderFile;
                node.IncludeFolder = folder => IncludeBuildFolder(folder.Path);
                node.Toggle = (folder, value) =>
                {
                    _bulk = true;
                    try
                    {
                        foreach (var file in folder.Files)
                            if (file.CanSync && IncludeBuildFolderFile(file) && (file.Kind & _kindMask) != 0)
                                file.Selected = value;
                        SetFolderSelectionRecursive(folder, value);
                    }
                    finally
                    {
                        _bulk = false;
                        RefreshSelectionChecks(folder);
                    }
                };
                _folders.Add(path, node);
                if (path.Length > 0)
                {
                    var parent = _folders[Path.GetDirectoryName(path) ?? ""];
                    node.Parent = parent;
                    parent.Children.Add(node);
                }

                if (++processed % 64 == 0)
                {
                    SetTreeProgress("Building folders\u2026", processed, orderedPaths.Length);
                    await Dispatcher.Yield(DispatcherPriority.Background);
                }
            }

            if (Snapshot != null)
            {
                processed = 0;
                SetTreeProgress("Assigning files to folders\u2026", 0, Snapshot.Files.Count);
                foreach (var file in Snapshot.Files)
                {
                    token.ThrowIfCancellationRequested();
                    var path = Path.GetDirectoryName(file.RelativePath) ?? "";
                    while (true)
                    {
                        DiffFolder folder;
                        if (_folders.TryGetValue(path, out folder)) folder.Files.Add(file);
                        if (path.Length == 0) break;
                        path = Path.GetDirectoryName(path) ?? "";
                    }

                    if (++processed % 256 == 0)
                    {
                        SetTreeProgress("Assigning files to folders\u2026", processed, Snapshot.Files.Count);
                        await Dispatcher.Yield(DispatcherPriority.Background);
                    }
                }
            }

            processed = 0;
            SetTreeProgress("Updating folder counts\u2026", 0, _folders.Count);
            foreach (var folder in _folders.Values)
            {
                token.ThrowIfCancellationRequested();
                folder.Refresh();
                if (++processed % 128 == 0)
                {
                    SetTreeProgress("Updating folder counts\u2026", processed, _folders.Count);
                    await Dispatcher.Yield(DispatcherPriority.Background);
                }
            }

            if (Snapshot != null)
                _cachedVisibleFolders =
                    new HashSet<string>(
                        _folders.Values
                            .Where(f => f.Path.Length == 0 || f.CountFor(DiffKind.Differences) > 0 || f.FolderCanSync)
                            .Select(f => f.Path), StringComparer.OrdinalIgnoreCase);

            DetachSelectionHandlers();
            if (Snapshot != null)
            {
                processed = 0;
                foreach (var file in Snapshot.Files)
                {
                    token.ThrowIfCancellationRequested();
                    file.PropertyChanged += FileSelectionChanged;
                    if (++processed % 512 == 0)
                        await Dispatcher.Yield(DispatcherPriority.Background);
                }
            }

            SetTreeProgress("Updating folder display\u2026", 0, _folders.Count);
            await ApplyKindFilterAsync(token);
            SetTreeProgress("Updating folder display\u2026", _folders.Count, _folders.Count);
        }
        finally
        {
            IsProgressVisible = progressWasVisible;
        }
    }

    internal async Task ApplyKindFilterAsync(CancellationToken token)
    {
        FolderItems = null;
        var processed = 0;
        foreach (var folder in _folders.Values)
        {
            token.ThrowIfCancellationRequested();
            folder.Mask = _kindMask;
            folder.Visible = folder.Path.Length == 0 ||
                             (IncludeBuildFolder(folder.Path) &&
                              (Snapshot != null
                                  ? folder.CountFor(_kindMask) > 0 || folder.MatchesFolderMask(_kindMask)
                                  : _cachedVisibleFolders == null || _cachedVisibleFolders.Contains(folder.Path)));
            folder.Label = (folder.Path.Length == 0 ? RootLabel() : Path.GetFileName(folder.Path)) +
                           (Snapshot == null ? " (not compared)" : " (" + folder.CountFor(_kindMask) + ")");

            if (++processed % 128 == 0)
                await Dispatcher.Yield(DispatcherPriority.Background);
        }

        PromoteVisibleFolderAncestors();

        if (!_folders[_selectedFolder].Visible)
        {
            _folders[_selectedFolder].Active = false;
            _selectedFolder = "";
            _folders[""].Active = true;
        }

        processed = 0;
        foreach (var folder in _folders.Values)
        {
            folder.UpdateDisplayChildren();
            if (++processed % 128 == 0)
                await Dispatcher.Yield(DispatcherPriority.Background);
        }

        FolderItems = new[] { _folders[""] };
        ApplyCheckedOnlyFolderCollapse();
        await Dispatcher.Yield(DispatcherPriority.Background);

        var visible = await Task.Run(() => _rows.Where(r => IncludeBuildFolderFile(r.File) &&
                                                            (r.File.Kind & _kindMask) != 0 &&
                                                            (_selectedFolder.Length == 0 ||
                                                             r.File.RelativePath.StartsWith(_selectedFolder + "\\",
                                                                 StringComparison.OrdinalIgnoreCase)) &&
                                                            (!_checkedOnlyFilter || r.File.Selected))
            .ToList(), token);
        token.ThrowIfCancellationRequested();

        MarkDirectFileRows(visible);
        visible = OrderSelectedFolderFirst(visible);
        if (_streamingRows != null && ReferenceEquals(FileItems, _streamingRows))
        {
            // Keep the streamed collection and row objects. Usually the order already
            // matches; only reconcile when the selected folder fell back to the root.
            var wanted = new HashSet<DiffRow>(visible);
            for (var i = _streamingRows.Count - 1; i >= 0; i--)
            {
                token.ThrowIfCancellationRequested();
                if (!wanted.Contains(_streamingRows[i])) _streamingRows.RemoveAt(i);
            }

            for (var i = 0; i < visible.Count; i++)
            {
                token.ThrowIfCancellationRequested();
                if (i >= _streamingRows.Count || !ReferenceEquals(_streamingRows[i], visible[i]))
                {
                    var oldIndex = _streamingRows.IndexOf(visible[i]);
                    if (oldIndex >= 0) _streamingRows.Move(oldIndex, i);
                    else _streamingRows.Insert(i, visible[i]);
                }

                if ((i + 1) % 256 == 0) await Dispatcher.Yield(DispatcherPriority.Background);
            }
        }
        else
        {
            FileItems = visible;
        }

        FilePanelTitle = "Files \u2014 " + (_selectedFolder.Length == 0 ? "all levels" : _selectedFolder) + " (" +
                         visible.Count + ")";
        UpdateSelectionSummary();

        // Allow the current collection to render without hiding or replacing it.
        await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
    }

    internal void ApplyKindFilter(bool preserveSelectedFolder = false)
    {
        // Counts were built once with the snapshot. Switching categories does not rescan files or rebuild the base tree.
        FolderItems = null;
        foreach (var folder in _folders.Values)
        {
            folder.Mask = _kindMask;
            folder.Visible = folder.Path.Length == 0 ||
                             (IncludeBuildFolder(folder.Path) &&
                              (Snapshot != null
                                  ? folder.CountFor(_kindMask) > 0 || folder.MatchesFolderMask(_kindMask)
                                  : _cachedVisibleFolders == null || _cachedVisibleFolders.Contains(folder.Path)));
            folder.Label = (folder.Path.Length == 0 ? RootLabel() : Path.GetFileName(folder.Path)) +
                           (Snapshot == null ? " (not compared)" : " (" + folder.CountFor(_kindMask) + ")");
        }

        if (preserveSelectedFolder) _folders[_selectedFolder].Visible = true;
        PromoteVisibleFolderAncestors();

        if (!_folders[_selectedFolder].Visible)
        {
            _folders[_selectedFolder].Active = false;
            _selectedFolder = "";
            _folders[""].Active = true;
        }

        foreach (var folder in _folders.Values) folder.UpdateDisplayChildren();
        FolderItems = new[] { _folders[""] };
        ApplyCheckedOnlyFolderCollapse();
        Filter();
        UpdateSelectionSummary();
    }

    private void PromoteVisibleFolderAncestors()
    {
        foreach (var folder in _folders.Values.Where(f => f.Visible && f.Path.Length > 0).ToArray())
        {
            var parentPath = Path.GetDirectoryName(folder.Path) ?? "";
            while (true)
            {
                if (_folders.TryGetValue(parentPath, out var parent))
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

        if (folder.FolderCanSync && folder.MatchesFolderMask(_kindMask))
            folder.SetFolderSelected(value, false);

        foreach (var child in folder.Children)
            SetFolderSelectionRecursive(child, value);
    }

    internal void RefreshChecks()
    {
        foreach (var folder in _folders.Values) folder.Refresh();
        UpdateSelectionSummary();
    }

    internal void RefreshSelectionChecks(DiffFolder changedFolder = null)
    {
        // Bulk check/uncheck changes only selection state.
        // Do not rebuild difference counts, CanSelect or icons.
        if (changedFolder == null)
        {
            foreach (var folder in _folders.Values)
                folder.RefreshSelectionCounts();
        }
        else
        {
            // Files are aggregated into every ancestor. Sibling branches are unchanged.
            var pending = new Stack<DiffFolder>();
            pending.Push(changedFolder);
            while (pending.Count > 0)
            {
                var folder = pending.Pop();
                folder.RefreshSelectionCounts();
                foreach (var child in folder.Children) pending.Push(child);
            }

            for (var parent = changedFolder.Parent; parent != null; parent = parent.Parent)
                parent.RefreshSelectionCounts();
        }

        UpdateSelectionSummary();
    }

    internal void FileSelectionChanged(object sender, PropertyChangedEventArgs e)
    {
        if (_bulk || Snapshot == null || e.PropertyName != "Selected") return;
        var file = (DiffFile)sender;
        var delta = file.Selected ? 1 : -1;
        var path = Path.GetDirectoryName(file.RelativePath) ?? "";
        while (true)
        {
            DiffFolder folder;
            if (_folders.TryGetValue(path, out folder))
                folder.ChangeSelectionCount(delta, file.Kind, IncludeBuildFolderFile(file));
            if (path.Length == 0) break;
            path = Path.GetDirectoryName(path) ?? "";
        }

        UpdateSelectionSummary();
    }

    internal void UpdateSelectionSummary()
    {
        DiffFolder root = null;
        var selectedFolderCount = Snapshot == null
            ? 0
            : _folders.Values.Count(f => f.FolderCanSync && f.FolderSelected && IncludeBuildFolder(f.Path));
        var fileCount = Snapshot != null && _folders.TryGetValue("", out root) ? root.SelectedCount : 0;
        var count = fileCount + selectedFolderCount;
        var visibleSelectedFolders = Snapshot == null
            ? 0
            : _folders.Values.Count(f =>
                f.FolderCanSync && f.FolderSelected && IncludeBuildFolder(f.Path) && f.MatchesFolderMask(_kindMask));
        var hidden = root == null ? 0 : count - root.SelectedFor(_kindMask) - visibleSelectedFolders;
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
        var folder = _selectedFolder ?? string.Empty;
        foreach (var row in visible)
            row.SetDirectInSelectedFolder(IsDirectChildFile(folder, row.File.RelativePath));
    }

    private static List<DiffRow> OrderSelectedFolderFirst(List<DiffRow> visible)
    {
        if (visible == null || visible.Count == 0)
            return visible ?? new List<DiffRow>();

        var selected = new List<DiffRow>();
        var nested = new List<DiffRow>();
        foreach (var row in visible)
            if (row.IsDirectInSelectedFolder) selected.Add(row);
            else nested.Add(row);

        selected.Sort(CompareDiffRows);
        nested.Sort(CompareDiffRows);
        selected.AddRange(nested);
        return selected;
    }

    private static int CompareDiffRows(DiffRow left, DiffRow right)
    {
        var name = StringComparer.CurrentCultureIgnoreCase.Compare(
            left == null ? null : left.File.Name,
            right == null ? null : right.File.Name);
        if (name != 0) return name;
        return StringComparer.CurrentCultureIgnoreCase.Compare(
            left == null ? null : left.File.RelativePath,
            right == null ? null : right.File.RelativePath);
    }

    internal string RootLabel()
    {
        return string.IsNullOrWhiteSpace(_treeSource) ? "All folders" :
            Path.GetFileName(_treeSource.TrimEnd('\\', '/')) is { Length: > 0 } name ? name : _treeSource;
    }

    internal string StageCheckedCopy(bool fromSource)
    {
        if (Snapshot == null) return null;
        var root = fromSource ? Snapshot.SourceRoot : Snapshot.TargetRoot;
        if (!Directory.Exists(root)) throw new DirectoryNotFoundException(root);
        var dirs = _folders.Values
            .Where(f => f.FolderCanSync && f.FolderSelected && IncludeBuildFolder(f.Path) && f.Path.Length > 0
                        && (fromSource ? f.SourceExists : f.TargetExists))
            .Select(f => f.Path)
            .ToList();
        var files = Snapshot.Files
            .Where(f => (fromSource ? f.Source : f.Target) != null
                        && (f.Selected || dirs.Any(dir =>
                            f.RelativePath.StartsWith(dir.TrimEnd('\\') + "\\", StringComparison.OrdinalIgnoreCase))))
            .Select(f => f.RelativePath).ToList();
        var staged = DropOutService.StageStructuredCopy(root, files, dirs);
        if (staged == null)
            throw new InvalidOperationException("No selected files or folders exist on " +
                                                (fromSource ? "Source." : "Target."));
        return staged;
    }

    private void ApplyCheckedOnlyFolderCollapse()
    {
        if (_folders == null || _folders.Count == 0 || !_folders.ContainsKey(""))
            return;
        if (!_checkedOnlyFilter)
            return;

        if (!string.IsNullOrEmpty(_selectedFolder) && _folders.ContainsKey(_selectedFolder))
        {
            _folders[_selectedFolder].Active = false;
            _selectedFolder = "";
            _folders[""].Active = true;
        }

        foreach (var folder in _folders.Values)
            if (folder.Path.Length == 0)
            {
                folder.Visible = true;
                folder.Expanded = false;
            }
            else
            {
                folder.Visible = false;
            }

        _folders[""].UpdateDisplayChildren();
        FolderItems = new[] { _folders[""] };
    }

    internal void Filter()
    {
        var visible = _rows.Where(r =>
            IncludeBuildFolderFile(r.File) && (r.File.Kind & _kindMask) != 0 &&
            (_selectedFolder.Length == 0 ||
             r.File.RelativePath.StartsWith(_selectedFolder + "\\", StringComparison.OrdinalIgnoreCase)) &&
            (!_checkedOnlyFilter || r.File.Selected)).ToList();
        MarkDirectFileRows(visible);
        visible = OrderSelectedFolderFirst(visible);
        var wasSyncing = _syncingTreeFromFile;
        _syncingTreeFromFile = true;
        try
        {
            FileItems = visible;
        }
        finally
        {
            _syncingTreeFromFile = wasSyncing;
        }

        FilePanelTitle = "Files \u2014 " + (_selectedFolder.Length == 0 ? "all levels" : _selectedFolder) + " (" +
                         visible.Count + ")";
        UpdateRefreshButtonState();
    }

    internal void SetBusy(bool value)
    {
        IsBusy = value;
        OnPropertyChanged(nameof(CanEdit));
        CanFilter = !value && Snapshot != null;
        OnPropertyChanged(nameof(CanConfigureIgnore));
        CanCancel = value && _comparing;
        SetFilePanelBusy(value, value ? "Please wait\u2026" : null);
        UpdateSelectionSummary();
        UpdateRefreshButtonState();
        CompareCommand.NotifyCanExecuteChanged();
        CleanCommand.NotifyCanExecuteChanged();
        BrowseSourceCommand.NotifyCanExecuteChanged();
        BrowseTargetCommand.NotifyCanExecuteChanged();
        CloseCommand.NotifyCanExecuteChanged();
        OpenDiffCommand.NotifyCanExecuteChanged();
        DeleteHistoryTabCommand?.NotifyCanExecuteChanged();
        DeleteAllHistoryTabsCommand?.NotifyCanExecuteChanged();
        DeleteOtherHistoryTabsCommand?.NotifyCanExecuteChanged();
    }

    internal async Task<bool> RefreshFileAsync(DiffFile file, bool applyFilter = true,
        bool? compareTimestamp = null, bool? preciseCompare = null, bool? compareSize = null,
        bool? compareAttributes = null)
    {
        if (Snapshot == null || file == null || _comparing) return false;
        var useTimestamp = compareTimestamp ?? file.CompareTimestamp;
        var useHash = preciseCompare ?? file.PreciseCompare;
        var useSize = compareSize ?? file.CompareSize;
        var useAttributes = compareAttributes ?? file.CompareAttributes;

        var stamps = await Task.Run(() =>
        {
            var sourcePath = DeveloperDifferencerService.SafePath(Snapshot.SourceRoot, file.RelativePath);
            var targetPath = DeveloperDifferencerService.SafePath(Snapshot.TargetRoot, file.RelativePath);
            var sourceStamp = DiffStamp.Read(sourcePath);
            var targetStamp = DiffStamp.Read(targetPath);
            if (useAttributes)
            {
                // Keep an indexed but unavailable file distinct from a missing side.
                sourceStamp ??= file.Source;
                targetStamp ??= file.Target;
            }

            var sourceBom = useAttributes && sourceStamp != null
                ? DeveloperDifferencerService.HasBom(sourcePath)
                : false;
            var targetBom = useAttributes && targetStamp != null
                ? DeveloperDifferencerService.HasBom(targetPath)
                : false;
            var hashRequired = DeveloperDifferencerService.ShouldHash(sourceStamp, targetStamp, useTimestamp, useSize,
                useAttributes, sourceBom, targetBom, useHash);
            var sourceHash = hashRequired ? DeveloperDifferencerService.HashFile(sourcePath) : null;
            var targetHash = hashRequired ? DeveloperDifferencerService.HashFile(targetPath) : null;
            if (useHash && ((hashRequired && (sourceHash == null || targetHash == null)) ||
                            !DiffStamp.Same(sourceStamp, DiffStamp.Read(sourcePath), true) ||
                            !DiffStamp.Same(targetStamp, DiffStamp.Read(targetPath), true)))
                throw new IOException("File changed during comparison. Compare again: " + file.RelativePath);
            return (sourceStamp, targetStamp, sourceHash, targetHash, sourceBom, targetBom);
        });

        var source = stamps.Item1;
        var target = stamps.Item2;
        var changed = file.CompareTimestamp != useTimestamp || file.PreciseCompare != useHash ||
                      file.CompareSize != useSize ||
                      file.CompareAttributes != useAttributes || file.SourceBom != stamps.Item5 ||
                      file.TargetBom != stamps.Item6 ||
                      !DiffStamp.Same(file.Source, source, true) ||
                      !DiffStamp.Same(file.Target, target, true) ||
                      file.SourceHash != stamps.Item3 || file.TargetHash != stamps.Item4;
        if (!changed) return false;
        InvalidateIndexes();

        var refreshedFile = new DiffFile
        {
            RelativePath = file.RelativePath,
            Source = source,
            Target = target,
            CompareTimestamp = useTimestamp,
            CompareSize = useSize,
            CompareAttributes = useAttributes,
            SourceBom = stamps.Item5,
            TargetBom = stamps.Item6,
            PreciseCompare = useHash,
            SourceHash = stamps.Item3,
            TargetHash = stamps.Item4
        };
        refreshedFile.Selected = file.Selected && !refreshedFile.AttributeUnreadable;

        file.PropertyChanged -= FileSelectionChanged;
        refreshedFile.PropertyChanged += FileSelectionChanged;

        var fileIndex = Snapshot.Files.FindIndex(f => ReferenceEquals(f, file));
        if (fileIndex < 0)
            fileIndex = Snapshot.Files.FindIndex(f =>
                string.Equals(f.RelativePath, file.RelativePath, StringComparison.OrdinalIgnoreCase));
        if (fileIndex >= 0)
            Snapshot.Files[fileIndex] = refreshedFile;

        var row = _rows.FirstOrDefault(r => ReferenceEquals(r.File, file));
        if (row == null)
            row = _rows.FirstOrDefault(r =>
                string.Equals(r.File.RelativePath, file.RelativePath, StringComparison.OrdinalIgnoreCase));
        if (row != null)
        {
            row.File = refreshedFile;
            row.RefreshFromFile();
        }

        foreach (var folder in _folders.Values)
        {
            for (var i = 0; i < folder.Files.Count; i++)
                if (ReferenceEquals(folder.Files[i], file) ||
                    string.Equals(folder.Files[i].RelativePath, file.RelativePath, StringComparison.OrdinalIgnoreCase))
                {
                    folder.Files[i] = refreshedFile;
                    break;
                }

            folder.Refresh();
        }

        if (applyFilter)
        {
            ApplyKindFilter();
            Status = _folders[""].CountFor(DiffKind.Differences) + " differences / " +
                     _folders[""].CountFor(DiffKind.Same) + " identical. External edit refreshed: " +
                     refreshedFile.RelativePath;
        }

        return true;
    }

    internal void SaveState()
    {
        SaveHistory();
        try
        {
            Directory.CreateDirectory(StateDirectory);
            var state = new DifferencerState { Source = SourcePath, Target = TargetPath };
            var temporary = StatePath + ".tmp";
            using (var stream = File.Create(temporary))
            {
                new XmlSerializer(typeof(DifferencerState)).Serialize(stream, state);
            }

            if (File.Exists(StatePath)) File.Replace(temporary, StatePath, null);
            else File.Move(temporary, StatePath);
        }
        catch (Exception ex)
        {
            Status = "Failed to save tree: " + ErrorMessages.English(ex);
        }
    }

    // Call while interaction is disabled (closing) or IsBusy (comparison).
    internal async Task SaveStateAsync()
    {
        CaptureHistoryTab();
        var history = new HistoryFile<ComparisonHistoryTab>
        { Tabs = HistoryTabs.ToList(), SelectedIndex = HistoryTabs.IndexOf(_selectedHistoryTab) };
        var state = new DifferencerState { Source = SourcePath, Target = TargetPath };
        var path = StatePath;
        await Task.Run(() =>
        {
            ResultHistoryStore.Save("comparison", history);
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))
                                      ?? throw new IOException("The comparison state path has no parent directory."));
            var temporary = path + ".tmp";
            using (var stream = File.Create(temporary))
            {
                new XmlSerializer(typeof(DifferencerState)).Serialize(stream, state);
            }

            File.Move(temporary, path, true);
        });
    }

    private sealed class PendingIndex
    {
        private readonly CancellationTokenSource _cancellation = new();
        private readonly string[] _disabled;

        public PendingIndex(string path, IEnumerable<string> disabled)
        {
            Path = path;
            _disabled = disabled.ToArray();
            Task = BuildAsync(path);
        }

        public Task<DiffIndex> Task { get; }
        public string Path { get; }
        public Exception Error { get; private set; }

        public void Cancel()
        {
            try
            {
                _cancellation.Cancel();
            }
            catch (ObjectDisposedException)
            {
            }
        }

        private async Task<DiffIndex> BuildAsync(string path)
        {
            try
            {
                return await System.Threading.Tasks.Task.Run(
                    () => DeveloperDifferencerService.BuildIndex(path, _cancellation.Token, _disabled),
                    _cancellation.Token);
            }
            catch (OperationCanceledException)
            {
                return null;
            }
            catch (Exception ex)
            {
                Error = ex;
                return null;
            }
            finally
            {
                _cancellation.Dispose();
            }
        }
    }

    internal delegate bool ConfirmSyncCallback(string direction, DiffFile[] files, DiffFolderSync[] folders,
        bool toTarget, bool zipMode, out string zipFileName, out string zipFolder);
}

internal sealed class SolutionCleanSelection
{
    public string[] Solutions { get; set; }
    public string[] Configurations { get; set; }
}