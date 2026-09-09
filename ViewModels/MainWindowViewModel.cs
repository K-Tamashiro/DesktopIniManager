using DesktopIniManager.Models;
using DesktopIniManager.Services;
using Microsoft.Win32;
using System;
using System.Collections.ObjectModel;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Globalization;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Media.Animation;
using System.Windows.Controls;
using System.Windows.Threading;
using FastVolumeIndex;
using DesktopIniManager.Properties;

namespace DesktopIniManager.ViewModels
{
    internal sealed class MainWindowViewModel : ObservableObject
    {
        internal readonly ObservableCollection<FolderMatch> _results = new ObservableCollection<FolderMatch>();
        internal readonly ObservableCollection<FolderMatch> _treeRoots = new ObservableCollection<FolderMatch>();
        internal readonly ObservableCollection<FolderMatch> _solutionRoots = new ObservableCollection<FolderMatch>();
        internal readonly ObservableCollection<FolderMatch> _searchRoots = new ObservableCollection<FolderMatch>();
        internal CancellationTokenSource _searchCts;
        internal string _pendingSearchQuery;
        internal bool _solutionView;
        internal int _treeView; // 0 Physical, 1 Solution, 2 Search
        internal int _selectedIconIndex;
        internal ImageSource _selectedIconPreview;
        internal VolumePathIndex _pathIndex;
        internal int _searchResultCount;
        internal readonly ObservableCollection<FileListItem> _files = new ObservableCollection<FileListItem>();
        internal CancellationTokenSource _fileListCts;
        internal CancellationTokenSource _filterCts;
        internal IReadOnlyList<FolderMatch> _filteredViewItems;
        internal string _folderTreeRoot;
        internal int _baseTreeView;
        internal bool _rebuildingFolderTrees;
        internal FolderMatch _physicalCurrent, _solutionCurrent, _searchCurrent;
        internal readonly StartupState _startup;
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
        public string Query { get => _query; set => SetProperty(ref _query, value); }
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
        public int SelectedTreeView { get => _treeView; set { if (_treeView != value) ShowTreeView(value); } }
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
        internal async Task SearchAsync()
        {
            // Snapshot bindable state before starting background work.
            SearchHistoryRequested?.Invoke();
            string root = RootPath.Trim();
            string visibleQuery = Query.Trim();
            string query = _pendingSearchQuery ?? visibleQuery;
            bool folderListMode = string.IsNullOrWhiteSpace(query);
            bool gitSearchRequested = _pendingSearchQuery != null && string.Equals(query, ".git", StringComparison.OrdinalIgnoreCase);
            // Searches own the Search tab. Physical and Solution
            // are populated exclusively by the Git acquisition workflow.
            bool searchOnly = !gitSearchRequested;
            _pendingSearchQuery = null;
            SettingsService.SaveSearchRoot(root);
            SettingsService.SaveSearchQuery(visibleQuery);
            if (!Directory.Exists(root)) { _dialogs.Show(Strings.Main_LocationMissing, Strings.App_Title); return; }
            _searchCts?.Cancel();
            var searchCts = new CancellationTokenSource();
            _searchCts = searchCts;
            if (searchOnly)
            {
                _searchRoots.Clear();
                _searchResultCount = 0;
            }
            else
            {
                SaveFolderTrees();
                _rebuildingFolderTrees = true;
                _results.Clear();
                _treeRoots.Clear();
                _solutionRoots.Clear();
                _searchRoots.Clear();
                _searchResultCount = 0;
            }
            if (gitSearchRequested) ShowTreeView(0);
            CountLabel = Strings.Main_ZeroMatches; SetSearching(true);
            try
            {
                System.Collections.Generic.List<FolderMatch> solutionRoots = new List<FolderMatch>();
                if (folderListMode)
                {
                    StandardSearchResult standard = await RunStandardIndexedSearch(root, string.Empty, searchCts.Token);
                    await AddTreeResultsAsync(standard.Matches, searchCts.Token, searchOnly);
                    _pathIndex = standard.Paths;
                    RefreshTreeItemsSource();
                }
                else
                {
                    StandardSearchResult standard = await RunStandardIndexedSearch(root, query, searchCts.Token);
                    await AddTreeResultsAsync(standard.Matches, searchCts.Token, searchOnly);
                    _pathIndex = standard.Paths;
                    RefreshTreeItemsSource();
                    await ApplyStandardDevelopmentAnalysis(gitSearchRequested, standard.Paths, searchCts.Token);
                    if (!searchOnly) solutionRoots = await BuildSolutions(root, searchCts.Token);
                }
                foreach (FolderMatch solution in solutionRoots)
                {
                    AssignParents(solution);
                    _solutionRoots.Add(solution);
                }
                if (!searchOnly)
                {
                    _folderTreeRoot = root;
                    _physicalCurrent = _solutionCurrent = null;
                    _rebuildingFolderTrees = false;
                    SaveFolderTrees();
                }
                if (searchOnly)
                {
                    ShowTreeView(2);
                    SearchRootSelectionRequested?.Invoke();
                }
                else
                    ShowTreeView(0);
            }
            catch (OperationCanceledException) { Status = Strings.Main_SearchCancelled; }
            catch (Exception ex) { _dialogs.Show(ErrorMessages.English(ex), Strings.App_Title); Status = Strings.Main_SearchFailed; }
            finally
            {
                if (ReferenceEquals(_searchCts, searchCts)) { _searchCts = null; SetSearching(false); }
                searchCts.Dispose();
            }
        }

        internal Task<List<FolderMatch>> RunStandardSearch(string root, string query, CancellationToken token)
        {
            ImageSource defaultFolderIcon = FolderIconService.GetDefaultFolderIcon();
            return Task.Run(() =>
            {
                var matches = new List<FolderMatch>();
                new FolderSearchService().Search(root, query,
                    item => { item.IconPreview = string.Equals(item.Reason, "Folder", StringComparison.Ordinal) ? defaultFolderIcon : FolderIconService.GetFolderIcon(item.Path); matches.Add(item); },
                    count => Dispatcher.BeginInvoke(new Action(() => Status = string.Format(Strings.Main_ScanningFolders, count.ToString("N0")))), token);
                token.ThrowIfCancellationRequested();
                return matches;
            });
        }

        private Task<StandardSearchResult> RunStandardIndexedSearch(string root, string query, CancellationToken token)
        {
            ImageSource defaultFolderIcon = FolderIconService.GetDefaultFolderIcon();
            return Task.Run(() =>
            {
                int lastReport = Environment.TickCount;
                VolumePathIndex paths = VolumePathIndex.BuildFromDirCommand(root,
                    count =>
                    {
                        int now = Environment.TickCount;
                        if (unchecked(now - lastReport) < 125) return;
                        lastReport = now;
                        Dispatcher.BeginInvoke(new Action(() => Status = string.Format(Strings.Main_IndexedFoldersEllipsis, count.ToString("N0"))), System.Windows.Threading.DispatcherPriority.Background);
                    }, token);
                token.ThrowIfCancellationRequested();
                Dispatcher.BeginInvoke(new Action(() => Status = Strings.Main_BuildingTree));
                List<FolderMatch> matches = new FastFolderSearchService().Search(paths, query, token);
                foreach (FolderMatch item in matches) item.IconPreview = defaultFolderIcon;
                return new StandardSearchResult(paths, matches);
            }, token);
        }

        internal async Task ApplyStandardDevelopmentAnalysis(bool enabled, VolumePathIndex paths, CancellationToken token)
        {
            if (!enabled) return;
            Status = string.Format(Strings.Main_FoldersAnalyzing, _results.Count);
            await System.Windows.Threading.Dispatcher.Yield(System.Windows.Threading.DispatcherPriority.Background);
            Dictionary<string, string> analysis = await Task.Run(() => new FastFolderSearchService().AnalyzeDevelopment(paths, token), token);
            int updated = 0;
            foreach (FolderMatch item in _results)
            {
                token.ThrowIfCancellationRequested();
                string reason;
                if (analysis.TryGetValue(VolumePathIndex.Normalize(item.Path), out reason)) item.Reason = reason;
                if ((++updated % 60) == 0)
                {
                    await System.Windows.Threading.Dispatcher.Yield(System.Windows.Threading.DispatcherPriority.Background);
                }
            }
        }

        internal Task<List<FolderMatch>> RunFolderList(string root, CancellationToken token)
        {
            ImageSource defaultFolderIcon = FolderIconService.GetDefaultFolderIcon();
            return Task.Run(() =>
            {
                var folders = new List<FolderMatch>();
                foreach (string folder in Directory.EnumerateDirectories(root))
                {
                    token.ThrowIfCancellationRequested();
                    folders.Add(new FolderMatch { Path = folder, Reason = "Folder", IconPreview = defaultFolderIcon });
                }
                return folders;
            });
        }

        internal void SortPhysicalTree()
        {
            SortCollection(_treeRoots);
        }

        internal static void SortCollection(ObservableCollection<FolderMatch> items)
        {
            foreach (FolderMatch item in items)
                SortCollection(item.Children);

            FolderMatch[] ordered = items
                .OrderBy(item => item.Name, StringComparer.CurrentCultureIgnoreCase)
                .ThenBy(item => item.Path, StringComparer.CurrentCultureIgnoreCase)
                .ToArray();

            items.Clear();
            foreach (FolderMatch item in ordered)
                items.Add(item);
        }

        internal void InvertSelection()
        {
            foreach (FolderMatch item in CurrentItems().Where(item => item.IsActionable && !item.IsHidden && !item.IsFilterHidden))
                item.SetSelected(!item.IsSelected);
        }

        internal void ExpandAll() { foreach (var item in CurrentItems()) item.IsExpanded = true; }

        internal void CollapseAll() { foreach (var item in CurrentItems()) item.IsExpanded = false; }

        internal void ShowTreeView(int view) { if (view != 2) _baseTreeView = view; _treeView = view; _solutionView = view == 1; OnPropertyChanged(nameof(SelectedTreeView)); TreeItems = view == 0 ? _treeRoots : view == 1 ? _solutionRoots : _searchRoots; _ = ApplyFolderFilterAsync(); UpdateVisibleCount(); Status = view == 0 ? string.Format(Strings.Main_FoldersFound, _results.Count) : view == 1 ? string.Format(Strings.Main_SolutionsFound, _solutionRoots.Count) : string.Format(Strings.Main_SearchResults, _searchResultCount); }

        internal void RefreshTreeItemsSource() { TreeItems = _treeView == 0 ? _treeRoots : _treeView == 1 ? _solutionRoots : _searchRoots; }

        internal void ShowSolutionView() => ShowTreeView(1);

        internal void UpdateVisibleCount() { CountLabel = string.Format(_solutionView ? Strings.Main_NItems : Strings.Main_NFolders, CurrentItems().Count(item => !item.IsHidden && !item.IsFilterHidden)); }

        internal System.Collections.Generic.IEnumerable<FolderMatch> CurrentItems() => _filteredViewItems ?? Flatten(_treeView == 0 ? _treeRoots : _treeView == 1 ? _solutionRoots : _searchRoots).ToList();

        internal System.Collections.Generic.IEnumerable<FolderMatch> VisibleItems() => _filteredViewItems != null
            ? _filteredViewItems.Where(item => !item.IsHidden) : FlattenVisible(_treeView == 0 ? _treeRoots : _treeView == 1 ? _solutionRoots : _searchRoots);

        internal static void AssignParents(FolderMatch node)
        {
            if (node == null) return;
            foreach (FolderMatch child in node.Children)
            {
                child.Parent = node;
                AssignParents(child);
            }
        }

        internal static System.Collections.Generic.IEnumerable<FolderMatch> Flatten(System.Collections.Generic.IEnumerable<FolderMatch> roots)
        {
            foreach (FolderMatch item in roots) { yield return item; foreach (FolderMatch child in Flatten(item.Children)) yield return child; }
        }

        internal static System.Collections.Generic.IEnumerable<FolderMatch> FlattenVisible(System.Collections.Generic.IEnumerable<FolderMatch> roots)
        {
            foreach (FolderMatch item in roots)
            {
                if (item.IsHidden) continue;
                yield return item;
                if (item.IsExpanded) foreach (FolderMatch child in FlattenVisible(item.Children)) yield return child;
            }
        }

        internal void AddTreeResult(FolderMatch item)
        {
            FolderMatch parent = _results
                .Where(candidate => IsAncestorPath(candidate.Path, item.Path))
                .OrderByDescending(candidate => candidate.Path.Length)
                .FirstOrDefault();
            _results.Add(item);
            if (parent == null) { item.Parent = null; _treeRoots.Add(item); }
            else { item.Parent = parent; parent.Children.Add(item); }

            // Re-parent roots that arrived before their newly discovered ancestor.
            foreach (FolderMatch root in _treeRoots.Where(candidate => !ReferenceEquals(candidate, item) && IsAncestorPath(item.Path, candidate.Path)).ToList())
            {
                _treeRoots.Remove(root);
                root.Parent = item;
                item.Children.Add(root);
            }
            CountLabel = string.Format(Strings.Main_NMatches, _results.Count);
        }

        internal void AddTreeResults(IEnumerable<FolderMatch> items)
        {
            bool physicalViewVisible = ReferenceEquals(TreeItems, _treeRoots);
            if (physicalViewVisible) TreeItems = null;
            var byPath = new Dictionary<string, FolderMatch>(StringComparer.OrdinalIgnoreCase);
            try
            {
                foreach (FolderMatch item in items.OrderBy(item => item.Path.Length).ThenBy(item => item.Path, StringComparer.CurrentCultureIgnoreCase))
                {
                    _results.Add(item);
                    byPath[item.Path.TrimEnd(Path.DirectorySeparatorChar)] = item;
                    string parentPath = Path.GetDirectoryName(item.Path.TrimEnd(Path.DirectorySeparatorChar));
                    FolderMatch parent = null;
                    while (!string.IsNullOrEmpty(parentPath))
                    {
                        if (byPath.TryGetValue(parentPath.TrimEnd(Path.DirectorySeparatorChar), out parent)) break;
                        parentPath = Path.GetDirectoryName(parentPath);
                    }
                    if (parent == null) { item.Parent = null; _treeRoots.Add(item); }
                    else { item.Parent = parent; parent.Children.Add(item); }
                }
            }
            finally { if (physicalViewVisible) TreeItems = _treeRoots; }
            CountLabel = string.Format(Strings.Main_NMatches, _results.Count);
        }

        internal async Task AddTreeResultsAsync(IEnumerable<FolderMatch> items, CancellationToken token, bool intoSearch = false)
        {
            List<FolderMatch> source = items.ToList();
            Status = string.Format(Strings.Main_BuildingRows, source.Count.ToString("N0"));
            TreeBuildResult built = await Task.Run(() =>
            {
                var roots = new List<FolderMatch>();
                var byPath = new Dictionary<string, FolderMatch>(StringComparer.OrdinalIgnoreCase);
                foreach (FolderMatch item in source.OrderBy(item => item.Path.Length).ThenBy(item => item.Path, StringComparer.CurrentCultureIgnoreCase))
                {
                    token.ThrowIfCancellationRequested();
                    item.Children.Clear();
                    string normalized = item.Path.TrimEnd(Path.DirectorySeparatorChar);
                    byPath[normalized] = item;
                    string parentPath = Path.GetDirectoryName(normalized);
                    FolderMatch parent = null;
                    while (!string.IsNullOrEmpty(parentPath))
                    {
                        if (byPath.TryGetValue(parentPath.TrimEnd(Path.DirectorySeparatorChar), out parent)) break;
                        parentPath = Path.GetDirectoryName(parentPath);
                    }
                    if (parent == null) { item.Parent = null; roots.Add(item); }
                    else { item.Parent = parent; parent.Children.Add(item); }
                }
                foreach (FolderMatch root in roots) SortCollection(root.Children);
                foreach (FolderMatch item in source) item.IsExpanded = false;
                foreach (FolderMatch root in roots) root.IsExpanded = true;
                roots.Sort((left, right) =>
                {
                    int name = StringComparer.CurrentCultureIgnoreCase.Compare(left.Name, right.Name);
                    return name != 0 ? name : StringComparer.CurrentCultureIgnoreCase.Compare(left.Path, right.Path);
                });
                return new TreeBuildResult(source, roots);
            }, token);
            token.ThrowIfCancellationRequested();
            if (intoSearch)
            {
                _searchRoots.Clear();
                foreach (FolderMatch root in built.Roots) _searchRoots.Add(root);
                _searchResultCount = built.Items.Count;
                RefreshTreeItemsSource();
                CountLabel = string.Format(Strings.Main_SearchResults, _searchResultCount);
                return;
            }
            _results.Clear();
            foreach (FolderMatch item in built.Items) _results.Add(item);
            _treeRoots.Clear();
            foreach (FolderMatch root in built.Roots) _treeRoots.Add(root);
            RefreshTreeItemsSource();
            CountLabel = string.Format(Strings.Main_NFolders, _results.Count);
        }

        internal static bool IsAncestorPath(string parent, string child)
        {
            string prefix = parent.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
            return child.StartsWith(prefix, StringComparison.OrdinalIgnoreCase);
        }

        internal void HideSelected()
        {
            foreach (FolderMatch item in CurrentItems())
                item.IsHidden = !item.IsSelected;
            UpdateVisibleCount();
        }

        internal void Unhide()
        {
            foreach (FolderMatch item in CurrentItems())
                item.IsHidden = false;
            UpdateVisibleCount();
        }

        internal async Task LoadFilesAsync(FolderMatch folder)
        {
            _fileListCts?.Cancel();
            var fileListCts = new CancellationTokenSource();
            _fileListCts = fileListCts;
            _files.Clear();
            if (folder != null)
            {
                if (_treeView == 0) _physicalCurrent = folder;
                else if (_treeView == 1) _solutionCurrent = folder;
                else _searchCurrent = folder;
            }
            FilePanelTitle = folder == null ? Strings.Common_Files : string.Format(Strings.Main_FilesHeader, folder.Name);
            FilePanelPath = folder?.Path;
            if (folder == null || string.IsNullOrEmpty(folder.Path)) { SetFilePanelBusy(false); return; }
            SetFilePanelBusy(true);
            await System.Windows.Threading.Dispatcher.Yield(System.Windows.Threading.DispatcherPriority.Render);
            string[] searchKeys = (Query ?? string.Empty).Split((char[])null, StringSplitOptions.RemoveEmptyEntries)
                .Select(key => key.Trim().TrimStart('*')).Where(key => key.Length > 0).Distinct(StringComparer.CurrentCultureIgnoreCase).ToArray();

            VolumePathIndex index = _pathIndex;
            int treeView = _treeView;
            string folderPath = folder.Path;
            // Snapshot descendant folders on the UI thread for the Search tab.
            List<string> searchFolderPaths = null;
            if (treeView == 2)
            {
                searchFolderPaths = new List<string>();
                var stack = new Stack<FolderMatch>();
                stack.Push(folder);
                while (stack.Count > 0)
                {
                    FolderMatch node = stack.Pop();
                    searchFolderPaths.Add(node.Path);
                    for (int i = node.Children.Count - 1; i >= 0; i--)
                        stack.Push(node.Children[i]);
                }
            }

            try
            {
                var loaded = await Task.Run(() =>
                {
                    string[] paths = treeView == 2
                        ? CollectFilesFromFolders(index, searchFolderPaths, fileListCts.Token)
                        : CollectFilesUnder(index, folderPath, fileListCts.Token);

                    int total = paths.Length;
                    bool truncated = total > MaxFileListItems;
                    if (truncated)
                    {
                        var limited = new string[MaxFileListItems];
                        Array.Copy(paths, limited, MaxFileListItems);
                        paths = limited;
                    }

                    var items = new List<FileListItem>(paths.Length);
                    foreach (string path in paths)
                    {
                        fileListCts.Token.ThrowIfCancellationRequested();
                        items.Add(new FileListItem(path, searchKeys));
                    }
                    return Tuple.Create(items, total, truncated);
                }, fileListCts.Token);

                if (fileListCts.IsCancellationRequested || !ReferenceEquals(_fileListCts, fileListCts)) return;

                foreach (FileListItem item in loaded.Item1)
                {
                    _files.Add(item);
                    if ((_files.Count % 64) == 0) await System.Windows.Threading.Dispatcher.Yield();
                    if (fileListCts.IsCancellationRequested) return;
                }

                if (loaded.Item3)
                    Status = string.Format("{0:N0} / {1:N0} files", loaded.Item1.Count, loaded.Item2);

                FileListItem firstMatch = loaded.Item1.FirstOrDefault(item => item.IsSearchMatch);
                if (firstMatch != null)
                {
                    FileScrollRequested?.Invoke(firstMatch);
                }
            }
            catch (OperationCanceledException) { }
            finally
            {
                if (ReferenceEquals(_fileListCts, fileListCts) && !fileListCts.IsCancellationRequested)
                    SetFilePanelBusy(false);
            }
        }

        internal string[] FilesInFolder(string path)
        {
            VolumePathNode node = _pathIndex?.Find(path);
            if (node != null)
                return node.Files.Select(file => file.Path).ToArray();
            // Avoid disk enumeration while an index exists because cloud folders can block.
            if (_pathIndex != null)
                return Array.Empty<string>();
            try { return Directory.EnumerateFiles(path).OrderBy(item => item, StringComparer.CurrentCultureIgnoreCase).ToArray(); }
            catch { return Array.Empty<string>(); }
        }

        internal static string[] CollectFilesUnder(VolumePathIndex index, string folderPath, CancellationToken token)
        {
            if (index == null || string.IsNullOrEmpty(folderPath))
                return Array.Empty<string>();

            VolumePathNode root = index.Find(folderPath);
            if (root == null)
                return Array.Empty<string>();

            var paths = new List<string>();
            var stack = new Stack<VolumePathNode>();
            stack.Push(root);
            while (stack.Count > 0)
            {
                token.ThrowIfCancellationRequested();
                VolumePathNode node = stack.Pop();
                foreach (VolumePathNode file in node.Files)
                {
                    paths.Add(file.Path);
                    if (paths.Count >= MaxFileListItems) goto Done;
                }
                for (int i = node.Directories.Count - 1; i >= 0; i--)
                    stack.Push(node.Directories[i]);
            }
        Done:
            paths.Sort(StringComparer.CurrentCultureIgnoreCase);
            return paths.ToArray();
        }

        internal static string[] CollectFilesFromFolders(VolumePathIndex index, List<string> folderPaths, CancellationToken token)
        {
            if (folderPaths == null || folderPaths.Count == 0)
                return Array.Empty<string>();

            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var paths = new List<string>();

            if (index != null)
            {
                foreach (string folderPath in folderPaths)
                {
                    token.ThrowIfCancellationRequested();
                    VolumePathNode node = index.Find(folderPath);
                    if (node == null) continue;
                    foreach (VolumePathNode file in node.Files)
                        if (seen.Add(file.Path)) paths.Add(file.Path);
                }
            }

            paths.Sort(StringComparer.CurrentCultureIgnoreCase);
            return paths.ToArray();
        }

        internal string[] CollectSearchTabFiles(FolderMatch folder)
        {
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var paths = new List<string>();
            var stack = new Stack<FolderMatch>();
            stack.Push(folder);
            while (stack.Count > 0)
            {
                FolderMatch node = stack.Pop();
                foreach (string path in FilesInFolder(node.Path))
                    if (seen.Add(path)) paths.Add(path);
                for (int index = node.Children.Count - 1; index >= 0; index--)
                    stack.Push(node.Children[index]);
            }
            paths.Sort(StringComparer.CurrentCultureIgnoreCase);
            return paths.ToArray();
        }

        internal ObservableCollection<FolderMatch> CurrentTreeRoots()
        {
            if (_treeView == 1) return _solutionRoots;
            if (_treeView == 2) return _searchRoots;
            return _treeRoots;
        }

        internal FolderMatch FindFolderInRoots(ObservableCollection<FolderMatch> roots, string directory)
        {
            string current = directory.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            while (!string.IsNullOrEmpty(current))
            {
                foreach (FolderMatch item in Flatten(roots))
                {
                    string path = (item.Path ?? string.Empty).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
                    if (string.Equals(path, current, StringComparison.OrdinalIgnoreCase)) return item;
                }
                current = Path.GetDirectoryName(current);
            }
            return roots.Count > 0 ? roots[0] : null;
        }

        internal async Task ApplyFolderFilterAsync()
        {
            _filterCts?.Cancel();
            var filterCts = new CancellationTokenSource();
            _filterCts = filterCts;
            string[] terms = (FolderFilter ?? string.Empty).Split((char[])null, StringSplitOptions.RemoveEmptyEntries);
            List<FolderMatch> all = Flatten(_treeView == 0 ? _treeRoots : _treeView == 1 ? _solutionRoots : _searchRoots).ToList();
            try
            {
                List<FolderMatch> matches = terms.Length == 0 ? null : await Task.Run(() =>
                {
                    var filtered = new List<FolderMatch>();
                    foreach (FolderMatch item in all)
                    {
                        filterCts.Token.ThrowIfCancellationRequested();
                        string folderName = item.Name ?? string.Empty;
                        if (terms.Any(term => folderName.IndexOf(term, StringComparison.CurrentCultureIgnoreCase) >= 0))
                            filtered.Add(item);
                    }
                    return filtered;
                }, filterCts.Token);
                if (filterCts.IsCancellationRequested || !ReferenceEquals(_filterCts, filterCts)) return;
                TreeItems = null;
                if (matches != null) foreach (FolderMatch item in matches) item.IsExpanded = false;
                _filteredViewItems = matches;
                TreeItems = matches == null
                    ? (IEnumerable<FolderMatch>)(_treeView == 0 ? _treeRoots : _treeView == 1 ? _solutionRoots : _searchRoots) : matches;
                UpdateVisibleCount();
            }
            catch (OperationCanceledException) { }
        }

        internal void Apply()
        {
            var selected = VisibleItems().Where(item => item.IsActionable && item.IsSelected)
                .GroupBy(item => item.Path, StringComparer.OrdinalIgnoreCase)
                .Select(group => group.First()).ToList();
            bool addToGitIgnore = AddToGitIgnore == true;
            if (selected.Count == 0) { _dialogs.Show(Strings.Main_SelectOneFolder, Strings.App_Title); return; }
            if (_dialogs.Show(string.Format(Strings.Main_ConfirmApply, selected.Count), Strings.App_Title, MessageBoxButton.OKCancel, MessageBoxImage.Question) != MessageBoxResult.OK) return;

            int succeeded = 0;
            var errors = new System.Collections.Generic.List<string>();
            var changedFolders = new List<string>();
            var service = new DesktopIniService();

            foreach (var item in selected)
            {
                try
                {
                    service.Apply(item.Path, IconLibraryPath, _selectedIconIndex, addToGitIgnore, false);
                    FolderIconService.Invalidate(item.Path);
                    item.IconPreview = _selectedIconPreview ?? FolderIconService.GetFolderIcon(item.Path);
                    changedFolders.Add(item.Path);
                    succeeded++;
                }
                catch (Exception ex)
                {
                    errors.Add(item.Path + ": " + ErrorMessages.English(ex));
                }
            }

            if (changedFolders.Count > 0)
                service.NotifyExplorer(changedFolders);

            Status = string.Format(Strings.Main_AppliedTo, succeeded);
            _dialogs.Show(errors.Count == 0 ? Strings.Main_ApplyOk : string.Format(Strings.Main_ApplyResult, succeeded, errors.Count) + "\n\n" + string.Join("\n", errors.Take(5)), Strings.App_Title);
        }

        internal void Remove()
        {
            var selected = VisibleItems().Where(item => item.IsActionable && item.IsSelected)
                .GroupBy(item => item.Path, StringComparer.OrdinalIgnoreCase)
                .Select(group => group.First()).ToList();
            if (selected.Count == 0) { _dialogs.Show(Strings.Main_SelectOneFolder, Strings.App_Title); return; }
            if (_dialogs.Show(string.Format(Strings.Main_ConfirmRemove, selected.Count), Strings.App_Title, MessageBoxButton.OKCancel, MessageBoxImage.Warning) != MessageBoxResult.OK) return;

            int succeeded = 0;
            var errors = new System.Collections.Generic.List<string>();
            var changedFolders = new List<string>();
            var service = new DesktopIniService();

            foreach (var item in selected)
            {
                try
                {
                    service.Remove(item.Path, false);
                    FolderIconService.Invalidate(item.Path);
                    item.IconPreview = FolderIconService.GetDefaultFolderIcon();
                    changedFolders.Add(item.Path);
                    succeeded++;
                }
                catch (Exception ex)
                {
                    errors.Add(item.Path + ": " + ErrorMessages.English(ex));
                }
            }

            if (changedFolders.Count > 0)
                service.NotifyExplorer(changedFolders);

            Status = string.Format(Strings.Main_RemovedFrom, succeeded);
            _dialogs.Show(errors.Count == 0 ? Strings.Main_RemoveOk : string.Format(Strings.Main_RemoveResult, succeeded, errors.Count) + "\n\n" + string.Join("\n", errors.Take(5)), Strings.App_Title);
        }

        internal void Grep()
        {
            var visible = CurrentItems()
                .Where(item => item.IsActionable && !item.IsHidden && !item.IsFilterHidden && Directory.Exists(item.Path))
                .ToList();

            if (visible.Count == 0)
            {
                _dialogs.Show(Strings.Main_NoGrepFolders, Strings.App_Title, MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            IReadOnlyList<string> scopes = GetSelectedGrepScopes();
            if (scopes.Count == 0)
            {
                var candidates = new HashSet<FolderMatch>(visible);
                foreach (var item in visible)
                {
                    var ancestor = item.Parent;
                    while (ancestor != null && !candidates.Contains(ancestor)) ancestor = ancestor.Parent;
                    if (ancestor == null) item.SetSelected(true);
                }
                scopes = GetSelectedGrepScopes();
            }

            if (scopes.Count == 0)
            {
                _dialogs.Show(Strings.Main_NoGrepFolders, Strings.App_Title, MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            OpenGrepRequested?.Invoke(scopes);
        }

        internal IReadOnlyList<string> GetSelectedGrepScopes()
        {
            List<string> selected = CurrentItems()
                .Where(item => item.IsActionable && item.IsSelected && Directory.Exists(item.Path))
                .Select(item => Path.GetFullPath(item.Path).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();

            var selectedSet = new HashSet<string>(selected, StringComparer.OrdinalIgnoreCase);
            var scopes = new List<string>();

            foreach (string path in selected)
            {
                bool hasSelectedAncestor = false;
                string parent = Path.GetDirectoryName(path);

                while (!string.IsNullOrEmpty(parent))
                {
                    if (selectedSet.Contains(parent.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)))
                    {
                        hasSelectedAncestor = true;
                        break;
                    }
                    parent = Path.GetDirectoryName(parent);
                }

                if (!hasSelectedAncestor)
                    scopes.Add(path);
            }

            scopes.Sort(StringComparer.CurrentCultureIgnoreCase);
            return scopes;
        }

        internal async Task<List<FolderMatch>> BuildSolutions(string root, CancellationToken token)
        {
            ShowTreeView(1);
            SetTreePanelBusy(true);
            try
            {
                IReadOnlyList<string> projectFiles = _pathIndex != null
                    ? _pathIndex.ProjectFiles
                    : (IReadOnlyList<string>)new string[0];

                return await Task.Run(() =>
                {
                    List<FolderMatch> fromIndex = SolutionTreeService.BuildFromProjectFiles(projectFiles, token);
                    if (fromIndex.Count > 0) return fromIndex;
                    return SolutionTreeService.Build(root, token);
                }, token);
            }
            finally
            {
                SetTreePanelBusy(false);
            }
        }

        internal void RestoreFolderTrees()
        {
            try
            {
                if (_startup?.TreeError != null) { Status = string.Format(Strings.Main_RestoreTreesFailed, _startup.TreeError); return; }
                var state = _startup != null ? _startup.Tree : FolderTreeStateService.Load();
                if (state == null) return;
                var icons = _startup == null ? FolderTreeStateService.RestoreIcons(state.Icons) : null;
                var physical = _startup != null ? _startup.Physical : FolderTreeStateService.Restore(state.Physical, icons: icons);
                var solution = _startup != null ? _startup.Solution : FolderTreeStateService.Restore(state.Solution, icons: icons);
                foreach (var node in physical) _treeRoots.Add(node);
                foreach (var node in solution) _solutionRoots.Add(node);
                foreach (var node in Flatten(_treeRoots)) _results.Add(node);
                _physicalCurrent = _results.FirstOrDefault(node => node.IsCurrent);
                _solutionCurrent = Flatten(_solutionRoots).FirstOrDefault(node => node.IsCurrent);
                _folderTreeRoot = state.Root;
                ShowTreeView(state.View == 1 ? 1 : 0);
                Status = Strings.Main_TreesRestored;
            }
            catch (Exception ex) { Status = string.Format(Strings.Main_RestoreTreesFailed, ErrorMessages.English(ex)); }
        }

        internal void SaveFolderTrees()
        {
            // A cancelled/in-progress rebuild must not replace the last complete trees.
            if (_rebuildingFolderTrees || _folderTreeRoot == null) return;
            try
            {
                var icons = new List<byte[]>();
                var iconIds = new Dictionary<ImageSource, int>();
                FolderTreeStateService.Save(new FolderTreeState
                {
                    Root = _folderTreeRoot,
                    View = _baseTreeView,
                    Icons = icons,
                    Physical = FolderTreeStateService.Capture(_treeRoots, _physicalCurrent, icons, iconIds),
                    Solution = FolderTreeStateService.Capture(_solutionRoots, _solutionCurrent, icons, iconIds)
                });
            }
            catch (Exception ex) { Status = string.Format(Strings.Main_SaveTreesFailed, ErrorMessages.English(ex)); }
        }

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
