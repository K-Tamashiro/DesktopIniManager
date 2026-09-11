using DesktopIniManager.Models;
using DesktopIniManager.Services;
using System;
using System.Collections.ObjectModel;
using System.Collections.Generic;
using System.Threading;
using System.Windows;
using System.Windows.Media;
using System.Windows.Threading;
using FastVolumeIndex;
using DesktopIniManager.Properties;

namespace DesktopIniManager.ViewModels
{
    internal sealed partial class MainWindowViewModel : ObservableObject
    {
        private readonly ObservableCollection<FolderMatch> _results = new ObservableCollection<FolderMatch>();
        private readonly ObservableCollection<FolderMatch> _treeRoots = new ObservableCollection<FolderMatch>();
        private readonly ObservableCollection<FolderMatch> _solutionRoots = new ObservableCollection<FolderMatch>();
        private readonly ObservableCollection<FolderMatch> _searchRoots = new ObservableCollection<FolderMatch>();
        private CancellationTokenSource _searchCts;
        private string _pendingSearchQuery;
        private bool _solutionView;
        private int _treeView; // 0 Physical, 1 Solution, 2 Search
        private int _selectedIconIndex;
        private ImageSource _selectedIconPreview;
        private VolumePathIndex _pathIndex;
        private int _searchResultCount;
        private readonly ObservableCollection<FileListItem> _files = new ObservableCollection<FileListItem>();
        private CancellationTokenSource _fileListCts;
        private CancellationTokenSource _filterCts;
        private IReadOnlyList<FolderMatch> _filteredViewItems;
        private string _folderTreeRoot;
        private int _baseTreeView;
        private bool _rebuildingFolderTrees;
        private FolderMatch _physicalCurrent, _solutionCurrent, _searchCurrent;
        private readonly StartupState _startup;
        private bool treeCompact;
        public bool TreeCompact { get => treeCompact; set => SetProperty(ref treeCompact, value); }
        internal void ResetSettings()
        {
            CancelOperations(); SettingsService.ClearAll(); FolderTreeStateService.Clear();
            _results.Clear(); _treeRoots.Clear(); _solutionRoots.Clear(); _searchRoots.Clear(); _files.Clear();
            _pathIndex = null; _searchResultCount = 0; _filteredViewItems = null; _folderTreeRoot = null;
            _physicalCurrent = _solutionCurrent = _searchCurrent = null;
            _selectedIconIndex = 0; _selectedIconPreview = null; _pendingSearchQuery = null; SelectedIcon = null;
        }
        private const int MaxFileListItems = 3000;
        private string _rootPath = string.Empty;
        public string RootPath { get => _rootPath; set => SetProperty(ref _rootPath, value); }
        private string _query = string.Empty;
        public string Query
        {
            get => _query;
            set
            {
                if (SetProperty(ref _query, value))
                    ClearQueryCommand?.NotifyCanExecuteChanged();
            }
        }
        private string _iconLibraryPath = string.Empty;
        public string IconLibraryPath { get => _iconLibraryPath; set => SetProperty(ref _iconLibraryPath, value); }
        private string _folderFilter = string.Empty;
        public string FolderFilter { get => _folderFilter; set => SetProperty(ref _folderFilter, value); }
        private bool? _addToGitIgnore = false;
        public bool? AddToGitIgnore { get => _addToGitIgnore; set => SetProperty(ref _addToGitIgnore, value); }
        private string _status = Strings.Common_Ready;
        public string Status { get => _status; set => SetProperty(ref _status, value); }
        private string _countLabel = Strings.Main_ZeroMatches;
        public string CountLabel { get => _countLabel; set => SetProperty(ref _countLabel, value); }
        private bool _allFoldersSelected;
        public bool AllFoldersSelected
        {
            get => _allFoldersSelected;
            set
            {
                if (!SetProperty(ref _allFoldersSelected, value)) return;
                foreach (FolderMatch item in SelectableFolders())
                    item.SetSelected(value);
            }
        }
        private ImageSource _selectedIcon = null;
        public ImageSource SelectedIcon { get => _selectedIcon; set => SetProperty(ref _selectedIcon, value); }
        private string _selectedIconLabel = Strings.Main_NotSelected;
        public string SelectedIconLabel { get => _selectedIconLabel; set => SetProperty(ref _selectedIconLabel, value); }
        private string _filePanelTitle = Strings.Common_Files;
        public string FilePanelTitle { get => _filePanelTitle; set => SetProperty(ref _filePanelTitle, value); }
        private string _filePanelPath = null;
        public string FilePanelPath { get => _filePanelPath; set => SetProperty(ref _filePanelPath, value); }
        private IEnumerable<FolderMatch> _treeItems = null;
        public IEnumerable<FolderMatch> TreeItems { get => _treeItems; set => SetProperty(ref _treeItems, value); }
        private bool _isTreeBusy = false;
        public bool IsTreeBusy { get => _isTreeBusy; set => SetProperty(ref _isTreeBusy, value); }
        private bool _isFileBusy = false;
        public bool IsFileBusy { get => _isFileBusy; set => SetProperty(ref _isFileBusy, value); }
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
        internal MainWindowViewModel(StartupState startup, Dispatcher dispatcher, IUserDialogService dialogs)
        {
            _startup = startup; Dispatcher = dispatcher; _dialogs = dialogs;
            InitializeInteractionCommands();
            TreeItems = _treeRoots;
            SearchCommand = new AsyncRelayCommand(SearchAsync, ReportCommandError, () => !IsSearching);
            GitSearchCommand = new AsyncRelayCommand(async () => { _pendingSearchQuery = ".git"; await SearchAsync(); }, ReportCommandError, () => !IsSearching);
            CancelCommand = new RelayCommand(() => _searchCts?.Cancel(), () => IsSearching);
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

        public event Action<MainWindowAction, object> InteractionRequested;
        public ParameterCommand ChooseRootCommand { get; private set; }
        public ParameterCommand ChooseIconLibraryCommand { get; private set; }
        public ParameterCommand ChooseIconCommand { get; private set; }
        public ParameterCommand UseAsSearchLocationCommand { get; private set; }
        public ParameterCommand OpenExplorerCommand { get; private set; }
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
        private void InitializeInteractionCommands()
        {
            ChooseRootCommand = new ParameterCommand(parameter => InteractionRequested?.Invoke(MainWindowAction.ChooseRoot, parameter));
            ChooseIconLibraryCommand = new ParameterCommand(parameter => InteractionRequested?.Invoke(MainWindowAction.ChooseIconLibrary, parameter));
            ChooseIconCommand = new ParameterCommand(parameter => InteractionRequested?.Invoke(MainWindowAction.ChooseIcon, parameter));
            UseAsSearchLocationCommand = new ParameterCommand(parameter => InteractionRequested?.Invoke(MainWindowAction.UseAsSearchLocation, parameter));
            OpenExplorerCommand = new ParameterCommand(parameter => InteractionRequested?.Invoke(MainWindowAction.OpenExplorer, parameter));
            GrepFolderCommand = new ParameterCommand(parameter => InteractionRequested?.Invoke(MainWindowAction.GrepFolder, parameter));
            CompactTreeCommand = new ParameterCommand(parameter => InteractionRequested?.Invoke(MainWindowAction.CompactTree, parameter));
            ComfortableTreeCommand = new ParameterCommand(parameter => InteractionRequested?.Invoke(MainWindowAction.ComfortableTree, parameter));
            FileListViewCommand = new ParameterCommand(parameter => InteractionRequested?.Invoke(MainWindowAction.FileListView, parameter));
            FileIconSmallCommand = new ParameterCommand(parameter => InteractionRequested?.Invoke(MainWindowAction.FileIconSmall, parameter));
            FileIconLargeCommand = new ParameterCommand(parameter => InteractionRequested?.Invoke(MainWindowAction.FileIconLarge, parameter));
            ClearFolderFilterCommand = new ParameterCommand(parameter => InteractionRequested?.Invoke(MainWindowAction.ClearFolderFilter, parameter));
            DeveloperDifferencerCommand = new ParameterCommand(parameter => InteractionRequested?.Invoke(MainWindowAction.DeveloperDifferencer, parameter));
            LightThemeCommand = new ParameterCommand(parameter => InteractionRequested?.Invoke(MainWindowAction.LightTheme, parameter));
            DarkThemeCommand = new ParameterCommand(parameter => InteractionRequested?.Invoke(MainWindowAction.DarkTheme, parameter));
            ResetCommand = new ParameterCommand(parameter => InteractionRequested?.Invoke(MainWindowAction.Reset, parameter), () => !IsSearching);
            CloseCommand = new ParameterCommand(parameter => InteractionRequested?.Invoke(MainWindowAction.Close, parameter));
        }

        private readonly Dispatcher Dispatcher;
        private readonly IUserDialogService _dialogs;
        public ObservableCollection<FolderMatch> PhysicalRoots => _treeRoots;
        public ObservableCollection<FolderMatch> SolutionRoots => _solutionRoots;
        public ObservableCollection<FolderMatch> SearchRoots => _searchRoots;
        public ObservableCollection<FileListItem> Files => _files;
        public int SelectedIconIndex => _selectedIconIndex;
        public FolderMatch SearchTabRoot => _searchRoots.Count == 0 ? null : _searchRoots[0];
        public int TreeViewIndex => _treeView;
        public int SelectedTreeView { get => _treeView; set { if (_treeView != value) ShowTreeView(value); } }

        public void RememberCurrentFolder(FolderMatch target)
        {
            if (_treeView == 0) _physicalCurrent = target;
            else if (_treeView == 1) _solutionCurrent = target;
            else _searchCurrent = target;
        }

        public void ApplySelectedIcon(int index, ImageSource preview)
        {
            _selectedIconIndex = index;
            _selectedIconPreview = preview;
            SelectedIcon = preview;
            SelectedIconLabel = preview == null
                ? Strings.Main_NotSelected
                : string.Format(Strings.Main_IndexN, index);
        }

        public void ClearSelectedIcon()
        {
            _selectedIconIndex = 0;
            _selectedIconPreview = null;
            SelectedIcon = null;
            SelectedIconLabel = Strings.Main_NotSelected;
        }

        public void RestoreStartupIcon(ImageSource preview, int index, bool available)
        {
            _selectedIconIndex = index;
            _selectedIconPreview = preview;
            SelectedIcon = preview;
            SelectedIconLabel = available
                ? string.Format(Strings.Main_IndexN, index)
                : Strings.Main_PreviewUnavailable;
        }
        public event Action SearchHistoryRequested;
        public event Action SearchRootSelectionRequested;
        public event Action<FileListItem> FileScrollRequested;
        public event Action<IReadOnlyList<string>> OpenGrepRequested;
        private bool _isSearching;
        public bool IsSearching { get => _isSearching; private set => SetProperty(ref _isSearching, value); }
        public bool CanEdit => !IsSearching;
        private void ReportCommandError(Exception error) => ShowError(Strings.App_Unhandled, error);
        private void SetSearching(bool value)
        {
            IsSearching = value; OnPropertyChanged(nameof(CanEdit)); ResetCommand.NotifyCanExecuteChanged();
            SearchCommand.NotifyCanExecuteChanged(); GitSearchCommand.NotifyCanExecuteChanged(); CancelCommand.NotifyCanExecuteChanged();
            InvertSelectionCommand.NotifyCanExecuteChanged(); ExpandAllCommand.NotifyCanExecuteChanged(); CollapseAllCommand.NotifyCanExecuteChanged(); HideSelectedCommand.NotifyCanExecuteChanged(); UnhideCommand.NotifyCanExecuteChanged(); ApplyCommand.NotifyCanExecuteChanged(); RemoveCommand.NotifyCanExecuteChanged(); GrepCommand.NotifyCanExecuteChanged();
            if (!value) IsTreeBusy = false;
        }
        private void SetTreePanelBusy(bool value) => IsTreeBusy = value;
        private void SetFilePanelBusy(bool value) => IsFileBusy = value;
        internal void CancelOperations() { _searchCts?.Cancel(); _fileListCts?.Cancel(); _filterCts?.Cancel(); }

        internal void ShowError(string message, Exception ex) { Status = message; _dialogs.Show(message + "\n\n" + ErrorMessages.English(ex), Strings.App_Title, MessageBoxButton.OK, MessageBoxImage.Error); }
        internal sealed class StandardSearchResult
        {
            public StandardSearchResult(VolumePathIndex paths, List<FolderMatch> matches)
            { Paths = paths; Matches = matches; }
            public VolumePathIndex Paths { get; }
            public List<FolderMatch> Matches { get; }
        }
        private sealed class TreeBuildResult
        {
            public TreeBuildResult(List<FolderMatch> items, List<FolderMatch> roots)
            { Items = items; Roots = roots; }
            public List<FolderMatch> Items { get; }
            public List<FolderMatch> Roots { get; }
        }

    }
}
