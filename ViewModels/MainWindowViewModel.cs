using DesktopIniManager.Models;
using DesktopIniManager.Properties;
using DesktopIniManager.Services;
using FastVolumeIndex;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.IO;
using System.Linq;
using System.Threading;
using System.Windows;
using System.Windows.Media;
using System.Windows.Threading;

namespace DesktopIniManager.ViewModels;

internal sealed partial class MainWindowViewModel : ObservableObject
{
    private readonly Dispatcher Dispatcher;
    private readonly IUserDialogService _dialogs;
    private readonly ObservableCollection<FolderMatch> _results = new();
    private readonly ObservableCollection<FolderMatch> _searchRoots = new();
    private readonly ObservableCollection<FolderMatch> _solutionRoots = new();
    private readonly StartupState _startup;
    private readonly ObservableCollection<FolderMatch> _treeRoots = new();
    private bool? _addToGitIgnore = false;
    private bool _allFoldersSelected;
    private int _baseTreeView;
    private string _countLabel = Strings.Main_ZeroMatches;
    private CancellationTokenSource _fileListCts;
    private string _filePanelPath;
    private string _filePanelTitle = Strings.Common_Files;
    private CancellationTokenSource _filterCts;
    private IReadOnlyList<FolderMatch> _filteredViewItems;
    private string _folderFilter = string.Empty;
    private string _folderTreeRoot;
    private string _iconLibraryPath = string.Empty;
    private bool _isFileBusy;
    private bool _isSearching;
    private bool _isTreeBusy;
    private VolumePathIndex _pathIndex;
    private string _pendingSearchQuery;
    private FolderMatch _physicalCurrent, _solutionCurrent, _searchCurrent;
    private string _query = string.Empty;
    private bool _rebuildingFolderTrees;
    private string _rootPath = string.Empty;
    private string _scopedLabel = string.Empty;
    private CancellationTokenSource _searchCts;
    private int _searchResultCount;
    private ImageSource _selectedIcon;
    private string _selectedIconLabel = Strings.Main_NotSelected;
    private ImageSource _selectedIconPreview;
    private bool _solutionView;
    private string _status = Strings.Common_Ready;
    private IEnumerable<FolderMatch> _treeItems;
    private bool treeCompact;

    internal MainWindowViewModel(StartupState startup, Dispatcher dispatcher, IUserDialogService dialogs)
    {
        _startup = startup;
        Dispatcher = dispatcher;
        _dialogs = dialogs;
        FolderMatch.SelectionChanged += RefreshScopedLabel;
        InitializeInteractionCommands();
        TreeItems = _treeRoots;
        RefreshScopedLabel();
        SearchCommand = new AsyncRelayCommand(SearchAsync, ReportCommandError, () => !IsSearching);
        GitSearchCommand = new AsyncRelayCommand(async () =>
        {
            _pendingSearchQuery = ".git";
            await SearchAsync();
        }, ReportCommandError, () => !IsSearching);
        CancelCommand = new RelayCommand(CancelSearch, () => IsSearching || IsTreeBusy);
        InvertSelectionCommand = new RelayCommand(InvertSelection, () => !IsSearching);
        ExpandAllCommand = new RelayCommand(ExpandAll, () => !IsSearching);
        CollapseAllCommand = new RelayCommand(CollapseAll, () => !IsSearching);
        HideSelectedCommand = new RelayCommand(HideSelected, () => !IsSearching);
        UnhideCommand = new RelayCommand(Unhide, () => !IsSearching);
        ApplyCommand = new RelayCommand(Apply, () => !IsSearching);
        RemoveCommand = new RelayCommand(Remove, () => !IsSearching);
        GrepCommand = new RelayCommand(Grep, () => !IsSearching);
        ClearQueryCommand = new RelayCommand(() => Query = string.Empty, () => !string.IsNullOrEmpty(Query));
    }

    public bool TreeCompact
    {
        get => treeCompact;
        set => SetProperty(ref treeCompact, value);
    }

    public string RootPath
    {
        get => _rootPath;
        set
        {
            if (string.Equals(_rootPath, value, StringComparison.Ordinal)) return;
            SaveFolderTrees();
            _searchCts?.Cancel();
            InvalidateSolutionAnalysis();
            // A new root must not reuse checked folders or indexed paths from
            // the previous workspace while acquisition is being restarted.
            _fileListCts?.Cancel();
            ResetPhysicalFileScan();
            _filterCts?.Cancel();
            _pathIndex = null;
            _filteredViewItems = null;
            _results.Clear();
            _treeRoots.Clear();
            _searchRoots.Clear();
            Files.Clear();
            _physicalCurrent = _solutionCurrent = _searchCurrent = null;
            _searchResultCount = 0;
            ClearSearchHits();
            FilePanelPath = null;
            FilePanelTitle = Strings.Common_Files;
            FileListCountLabel = string.Format(Strings.Main_NItems, 0);
            SetFilePanelBusy(false);
            SetProperty(ref _rootPath, value);
            RefreshTreeItemsSource();
            RefreshScopedLabel();
        }
    }

    public string Query
    {
        get => _query;
        set
        {
            if (SetProperty(ref _query, value))
            {
                ClearQueryCommand?.NotifyCanExecuteChanged();
                if (string.IsNullOrEmpty(_query))
                    ClearSearchHits();
            }
        }
    }

    public string IconLibraryPath
    {
        get => _iconLibraryPath;
        set => SetProperty(ref _iconLibraryPath, value);
    }

    public string FolderFilter
    {
        get => _folderFilter;
        set => SetProperty(ref _folderFilter, value);
    }

    public bool? AddToGitIgnore
    {
        get => _addToGitIgnore;
        set => SetProperty(ref _addToGitIgnore, value);
    }

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

    public bool AllFoldersSelected
    {
        get => _allFoldersSelected;
        set
        {
            if (!SetProperty(ref _allFoldersSelected, value)) return;
            foreach (var item in SelectableFolders())
                item.SetSelected(value);
        }
    }

    public ImageSource SelectedIcon
    {
        get => _selectedIcon;
        set => SetProperty(ref _selectedIcon, value);
    }

    public string SelectedIconLabel
    {
        get => _selectedIconLabel;
        set => SetProperty(ref _selectedIconLabel, value);
    }

    public string FilePanelTitle
    {
        get => _filePanelTitle;
        set => SetProperty(ref _filePanelTitle, value);
    }

    public string FilePanelPath
    {
        get => _filePanelPath;
        set => SetProperty(ref _filePanelPath, value);
    }

    public IEnumerable<FolderMatch> TreeItems
    {
        get => _treeItems;
        set
        {
            if (ReferenceEquals(_treeItems, value)) return;
            if (_treeItems is INotifyCollectionChanged oldItems)
                oldItems.CollectionChanged -= TreeRootsChanged;
            SetProperty(ref _treeItems, value);
            if (_treeItems is INotifyCollectionChanged newItems)
                newItems.CollectionChanged += TreeRootsChanged;
            RefreshFrozenRoot();
        }
    }

    public FolderMatch FrozenRoot { get; private set; }

    public IEnumerable<FolderMatch> FrozenRootItems =>
        FrozenRoot == null ? Array.Empty<FolderMatch>() : new[] { FrozenRoot };

    public IEnumerable<FolderMatch> ScrollingTreeItems => FrozenRoot == null ? TreeItems : FrozenRoot.Children;

    public bool IsTreeBusy
    {
        get => _isTreeBusy;
        set
        {
            if (SetProperty(ref _isTreeBusy, value)) CancelCommand?.NotifyCanExecuteChanged();
        }
    }

    public bool IsFileBusy
    {
        get => _isFileBusy;
        set => SetProperty(ref _isFileBusy, value);
    }

    public AsyncRelayCommand SearchCommand { get; }
    public AsyncRelayCommand GitSearchCommand { get; }
    public RelayCommand CancelCommand { get; }
    public RelayCommand InvertSelectionCommand { get; }
    public RelayCommand ExpandAllCommand { get; }
    public RelayCommand CollapseAllCommand { get; }
    public RelayCommand HideSelectedCommand { get; }
    public RelayCommand UnhideCommand { get; }
    public RelayCommand ApplyCommand { get; }
    public RelayCommand RemoveCommand { get; }
    public RelayCommand GrepCommand { get; }
    public RelayCommand ClearQueryCommand { get; }

    public string ScopedLabel
    {
        get => _scopedLabel;
        set => SetProperty(ref _scopedLabel, value);
    }

    public ParameterCommand ChooseRootCommand { get; private set; }
    public ParameterCommand ChooseIconLibraryCommand { get; private set; }
    public ParameterCommand ChooseIconCommand { get; private set; }
    public ParameterCommand UseAsSearchLocationCommand { get; private set; }
    public ParameterCommand OpenExplorerCommand { get; private set; }
    public ParameterCommand TreeToEditorCommand { get; private set; }
    public ParameterCommand TreeFilesToEditorCommand { get; private set; }
    public ParameterCommand FileListToEditorCommand { get; private set; }
    public ParameterCommand GrepFolderCommand { get; private set; }
    public ParameterCommand CompactTreeCommand { get; private set; }
    public ParameterCommand ComfortableTreeCommand { get; private set; }
    public ParameterCommand FileListViewCommand { get; private set; }
    public ParameterCommand FileIconSmallCommand { get; private set; }
    public ParameterCommand FileIconLargeCommand { get; private set; }
    public ParameterCommand ClearFolderFilterCommand { get; private set; }
    public ParameterCommand DeveloperDifferencerCommand { get; private set; }
    public ParameterCommand LightThemeCommand { get; private set; }
    public ParameterCommand DarkThemeCommand { get; private set; }
    public ParameterCommand ResetCommand { get; private set; }
    public ParameterCommand CloseCommand { get; private set; }
    public ObservableCollection<FileListItem> Files { get; } = new();

    public int SelectedIconIndex { get; private set; }

    public FolderMatch SearchTabRoot => _searchRoots.Count == 0 ? null : _searchRoots[0];
    public int TreeViewIndex { get; private set; }

    public int SelectedTreeView
    {
        get => TreeViewIndex;
        set
        {
            if (TreeViewIndex != value) ShowTreeView(value);
        }
    }

    public bool IsSearching
    {
        get => _isSearching;
        private set => SetProperty(ref _isSearching, value);
    }

    public bool CanEdit => !IsSearching;

    internal void ResetSettings()
    {
        CancelOperations();
        ResetPhysicalFileScan();
        SettingsService.ClearAll();
        FolderTreeStateService.Clear();
        _results.Clear();
        _treeRoots.Clear();
        _solutionRoots.Clear();
        _searchRoots.Clear();
        Files.Clear();
        SyncSearchMatches(null, null);
        _pathIndex = null;
        _searchResultCount = 0;
        _filteredViewItems = null;
        _folderTreeRoot = null;
        _physicalCurrent = _solutionCurrent = _searchCurrent = null;
        SelectedIconIndex = 0;
        _selectedIconPreview = null;
        _pendingSearchQuery = null;
        SelectedIcon = null;
        RefreshScopedLabel();
    }

    private void TreeRootsChanged(object sender, NotifyCollectionChangedEventArgs e)
    {
        RefreshFrozenRoot();
    }

    private void RefreshFrozenRoot()
    {
        var roots = TreeItems?.Take(2).ToArray();
        FrozenRoot = roots != null && roots.Length == 1 && roots[0].Parent == null ? roots[0] : null;
        OnPropertyChanged(nameof(FrozenRoot));
        OnPropertyChanged(nameof(FrozenRootItems));
        OnPropertyChanged(nameof(ScrollingTreeItems));
    }

    public event Action<MainWindowAction, object> InteractionRequested;

    private void InitializeInteractionCommands()
    {
        ChooseRootCommand =
            new ParameterCommand(parameter => InteractionRequested?.Invoke(MainWindowAction.ChooseRoot, parameter));
        ChooseIconLibraryCommand = new ParameterCommand(parameter =>
            InteractionRequested?.Invoke(MainWindowAction.ChooseIconLibrary, parameter));
        ChooseIconCommand =
            new ParameterCommand(parameter => InteractionRequested?.Invoke(MainWindowAction.ChooseIcon, parameter));
        UseAsSearchLocationCommand = new ParameterCommand(parameter =>
            InteractionRequested?.Invoke(MainWindowAction.UseAsSearchLocation, parameter));
        OpenExplorerCommand = new ParameterCommand(parameter =>
            InteractionRequested?.Invoke(MainWindowAction.OpenExplorer, parameter));
        TreeToEditorCommand = new ParameterCommand(parameter =>
            InteractionRequested?.Invoke(MainWindowAction.TreeToEditor, parameter));
        TreeFilesToEditorCommand = new ParameterCommand(parameter =>
            InteractionRequested?.Invoke(MainWindowAction.TreeFilesToEditor, parameter));
        FileListToEditorCommand = new ParameterCommand(parameter =>
            InteractionRequested?.Invoke(MainWindowAction.FileListToEditor, parameter));
        GrepFolderCommand =
            new ParameterCommand(parameter => InteractionRequested?.Invoke(MainWindowAction.GrepFolder, parameter));
        CompactTreeCommand = new ParameterCommand(parameter =>
            InteractionRequested?.Invoke(MainWindowAction.CompactTree, parameter));
        ComfortableTreeCommand = new ParameterCommand(parameter =>
            InteractionRequested?.Invoke(MainWindowAction.ComfortableTree, parameter));
        FileListViewCommand = new ParameterCommand(parameter =>
            InteractionRequested?.Invoke(MainWindowAction.FileListView, parameter));
        FileIconSmallCommand = new ParameterCommand(parameter =>
            InteractionRequested?.Invoke(MainWindowAction.FileIconSmall, parameter));
        FileIconLargeCommand = new ParameterCommand(parameter =>
            InteractionRequested?.Invoke(MainWindowAction.FileIconLarge, parameter));
        ClearFolderFilterCommand = new ParameterCommand(parameter =>
            InteractionRequested?.Invoke(MainWindowAction.ClearFolderFilter, parameter));
        DeveloperDifferencerCommand = new ParameterCommand(parameter =>
            InteractionRequested?.Invoke(MainWindowAction.DeveloperDifferencer, parameter));
        LightThemeCommand =
            new ParameterCommand(parameter => InteractionRequested?.Invoke(MainWindowAction.LightTheme, parameter));
        DarkThemeCommand =
            new ParameterCommand(parameter => InteractionRequested?.Invoke(MainWindowAction.DarkTheme, parameter));
        ResetCommand =
            new ParameterCommand(parameter => InteractionRequested?.Invoke(MainWindowAction.Reset, parameter),
                () => !IsSearching);
        CloseCommand =
            new ParameterCommand(parameter => InteractionRequested?.Invoke(MainWindowAction.Close, parameter));
    }

    public void RememberCurrentFolder(FolderMatch target)
    {
        if (TreeViewIndex == 0) _physicalCurrent = target;
        else if (TreeViewIndex == 1) _solutionCurrent = target;
        else _searchCurrent = target;
    }

    public void ApplySelectedIcon(int index, ImageSource preview)
    {
        SelectedIconIndex = index;
        _selectedIconPreview = preview;
        SelectedIcon = preview;
        SelectedIconLabel = preview == null
            ? Strings.Main_NotSelected
            : string.Format(Strings.Main_IndexN, index);
    }

    public void ClearSelectedIcon()
    {
        SelectedIconIndex = 0;
        _selectedIconPreview = null;
        SelectedIcon = null;
        SelectedIconLabel = Strings.Main_NotSelected;
    }

    public void RestoreStartupIcon(ImageSource preview, int index, bool available)
    {
        SelectedIconIndex = index;
        _selectedIconPreview = preview;
        SelectedIcon = preview;
        SelectedIconLabel = available
            ? string.Format(Strings.Main_IndexN, index)
            : Strings.Main_PreviewUnavailable;
    }

    public event Action SearchHistoryRequested;

    public event Action SearchRootSelectionRequested;

    public event Action<FileListItem, bool> FileScrollRequested;

    public event Action<IReadOnlyList<string>> OpenGrepRequested;

    private void ReportCommandError(Exception error)
    {
        ShowError(Strings.App_Unhandled, error);
    }

    private void SetSearching(bool value)
    {
        IsSearching = value;
        OnPropertyChanged(nameof(CanEdit));
        ResetCommand.NotifyCanExecuteChanged();
        SearchCommand.NotifyCanExecuteChanged();
        GitSearchCommand.NotifyCanExecuteChanged();
        CancelCommand.NotifyCanExecuteChanged();
        InvertSelectionCommand.NotifyCanExecuteChanged();
        ExpandAllCommand.NotifyCanExecuteChanged();
        CollapseAllCommand.NotifyCanExecuteChanged();
        HideSelectedCommand.NotifyCanExecuteChanged();
        UnhideCommand.NotifyCanExecuteChanged();
        ApplyCommand.NotifyCanExecuteChanged();
        RemoveCommand.NotifyCanExecuteChanged();
        GrepCommand.NotifyCanExecuteChanged();
        if (!value) IsTreeBusy = false;
    }

    private void SetTreePanelBusy(bool value)
    {
        IsTreeBusy = value;
    }

    private void SetFilePanelBusy(bool value)
    {
        IsFileBusy = value;
    }

    internal void CancelOperations()
    {
        _searchCts?.Cancel();
        InvalidateSolutionAnalysis();
        _fileListCts?.Cancel();
        _filterCts?.Cancel();
    }

    internal IReadOnlyList<string> ContainedFileNames(FolderMatch folder)
    {
        if (folder == null || string.IsNullOrEmpty(folder.Path))
            return Array.Empty<string>();

        if (_pathIndex != null)
        {
            var node = _pathIndex.Find(folder.Path);
            if (node != null && node.Files != null)
                return node.Files
                    .Select(file => Path.GetFileName(file.Path))
                    .OrderBy(name => name, StringComparer.CurrentCultureIgnoreCase)
                    .ToList();
        }

        if (string.Equals(FilePanelPath, folder.Path, StringComparison.OrdinalIgnoreCase) && Files.Count > 0)
            return Files
                .Where(file => string.Equals(
                    NormalizeComparePath(Path.GetDirectoryName(file.Path)),
                    NormalizeComparePath(folder.Path), StringComparison.OrdinalIgnoreCase))
                .Select(file => file.Name)
                .OrderBy(name => name, StringComparer.CurrentCultureIgnoreCase)
                .ToList();

        return Array.Empty<string>();
    }

    internal void ShowError(string message, Exception ex)
    {
        Status = message;
        _dialogs.Show(message + "\n\n" + ErrorMessages.English(ex), Strings.App_Title, MessageBoxButton.OK,
            MessageBoxImage.Error);
    }

    internal sealed class StandardSearchResult
    {
        public StandardSearchResult(VolumePathIndex paths, List<FolderMatch> matches)
        {
            Paths = paths;
            Matches = matches;
        }

        public VolumePathIndex Paths { get; }
        public List<FolderMatch> Matches { get; }
    }

    private sealed class TreeBuildResult
    {
        public TreeBuildResult(List<FolderMatch> items, List<FolderMatch> roots)
        {
            Items = items;
            Roots = roots;
        }

        public List<FolderMatch> Items { get; }
        public List<FolderMatch> Roots { get; }
    }
}