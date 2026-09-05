using DesktopIniManager.Models;
using DesktopIniManager.Services;
using DesktopIniManager.Views;
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
using System.Security.Principal;
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

namespace DesktopIniManager
{
    public partial class MainWindow : Window
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
        private GrepWindow _grepWindow;
        private VolumePathIndex _pathIndex;
        private int _searchResultCount;
        private readonly ObservableCollection<FileListItem> _files = new ObservableCollection<FileListItem>();
        private CancellationTokenSource _fileListCts;
        private CancellationTokenSource _filterCts;
        private IReadOnlyList<FolderMatch> _filteredViewItems;
        private readonly System.Windows.Threading.DispatcherTimer _filterTimer;
        private bool _largeFileIcons;
        private string _folderTreeRoot;
        private int _baseTreeView;
        private bool _rebuildingFolderTrees;
        private FolderMatch _physicalCurrent, _solutionCurrent, _searchCurrent;
        private bool _syncingTreeFromFile;
        private readonly StartupState _startup;

        public static readonly DependencyProperty TreeCompactProperty =
            DependencyProperty.Register(nameof(TreeCompact), typeof(bool), typeof(MainWindow), new PropertyMetadata(false));

        public bool TreeCompact
        {
            get { return (bool)GetValue(TreeCompactProperty); }
            set { SetValue(TreeCompactProperty, value); }
        }

        public MainWindow() : this(null) { }

        internal MainWindow(StartupState startup)
        {
            _startup = startup;
            string[] commandLine = Environment.GetCommandLineArgs();
            var resume = ElevationResumeState.Load(commandLine);
            bool fastSearchRequested = commandLine.Any(argument => string.Equals(argument, "--fast-search", StringComparison.OrdinalIgnoreCase));
            bool runGitSearch = commandLine.Any(argument => string.Equals(argument, "--run-git-search", StringComparison.OrdinalIgnoreCase));
            bool runSearch = commandLine.Any(argument => string.Equals(argument, "--run-search", StringComparison.OrdinalIgnoreCase));
            bool darkMode = startup != null ? startup.DarkMode : SettingsService.LoadDarkMode();
            ThemeService.Apply(darkMode);
            InitializeComponent();
            LightThemeButton.IsChecked = !darkMode;
            DarkThemeButton.IsChecked = darkMode;
            InitLanguageBox();
            ResultsTree.ItemsSource = _treeRoots;
            FileList.ItemsSource = _files;
            FileIconList.ItemsSource = _files;
            _filterTimer = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromMilliseconds(250) };
            _filterTimer.Tick += (sender, args) => { _filterTimer.Stop(); ApplyFolderFilter(); };
            string savedRoot = startup != null ? startup.Root : SettingsService.LoadSearchRoot();
            RootBox.Text = savedRoot ?? string.Empty;
            string defaultLibrary = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Assets", "folder_set.icl");
            string savedLibrary = startup != null ? startup.IconLibrary : SettingsService.LoadIconLibraryPath();
            IconPathBox.Text = !string.IsNullOrWhiteSpace(savedLibrary) ? savedLibrary : defaultLibrary;
            string savedQuery = startup != null ? startup.Query : SettingsService.LoadSearchQuery();
            QueryBox.Text = string.Equals(savedQuery, ".git", StringComparison.OrdinalIgnoreCase) ? string.Empty : (savedQuery ?? string.Empty);
            // Reflect the actual process state as well as an elevation restart request.
            // Users who always run the executable as administrator can still uncheck it
            // to compare the standard search during the current session.
            ElevationService.Shared.Initialize(fastSearchRequested);
            ElevationService.Shared.Bind(FastNtfsSearchBox, this, "main");
            RestoreWindowPlacement(commandLine);
            HookPathBox(RootBox);
            HookPathBox(IconPathBox);
            ApplyTreeDensity(startup != null ? startup.TreeCompact : SettingsService.LoadTreeCompact(), false);
            RestoreFolderTrees();
            if (startup != null) { startup.Tree = null; startup.Physical = null; startup.Solution = null; }
            Loaded += (sender, args) =>
            {
                if (startup == null) RefreshSelectedIconPreview();
                else
                {
                    _selectedIconPreview = startup.SelectedIcon?.Preview;
                    SelectedIconImage.Source = _selectedIconPreview;
                    SelectedIconText.Text = startup.SelectedIcon == null ? Strings.Main_PreviewUnavailable : string.Format(Strings.Main_IndexN, startup.SelectedIcon.ShellIndex);
                }
                ShowTextEnd(RootBox);
                ShowTextEnd(IconPathBox);
                WindowActivationService.BringToFront(this);
                if (resume != null) { RestoreElevation(resume); return; }
                if (runGitSearch) Dispatcher.BeginInvoke(new Action(() => GitSearch_Click(this, new RoutedEventArgs())));
                else if (runSearch) Dispatcher.BeginInvoke(new Action(() => Search_Click(this, new RoutedEventArgs())));
            };
        }

        private void ChooseRoot_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                string selectedPath = Directory.Exists(RootBox.Text) ? RootBox.Text : Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
                string result = NativeFolderPicker.Show(new WindowInteropHelper(this).Handle, selectedPath, Strings.Main_SelectSearchFolder);
                if (!string.IsNullOrEmpty(result)) { RootBox.Text = result; RootBox.CommitHistory(); SettingsService.SaveSearchRoot(result); ShowTextEnd(RootBox); }
            }
            catch (Exception ex) { ShowError(Strings.Main_FolderPickerFailed, ex); }
        }

        private void ChooseIconLibrary_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                var dialog = new OpenFileDialog { Filter = Strings.Main_IconFilter, CheckFileExists = true };
                string currentPath = IconPathBox.Text.Trim();
                if (File.Exists(currentPath)) { dialog.InitialDirectory = Path.GetDirectoryName(currentPath); dialog.FileName = Path.GetFileName(currentPath); }
                if (dialog.ShowDialog(this) != true) return;
                IconPathBox.Text = dialog.FileName; IconPathBox.CommitHistory();
                SettingsService.SaveIconLibraryPath(dialog.FileName);
                ShowTextEnd(IconPathBox);
                _selectedIconIndex = 0;
                RefreshSelectedIconPreview();
                StatusText.Text = Path.GetFileName(dialog.FileName) + Strings.Main_SelectedSuffix;
            }
            catch (Exception ex) { ShowError(Strings.Main_LibrarySelectFailed, ex); }
        }

        private void ChooseIcon_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                string iconPath = IconPathBox.Text.Trim();
                if (!File.Exists(iconPath)) { MessageBox.Show(Strings.Main_ChooseLibraryFirst, Title); return; }
                StatusText.Text = Strings.Main_LoadingIcons;
                var groups = IconResourceReader.Read(iconPath);
                if (groups.Count == 0) { MessageBox.Show(Strings.Main_NoIconGroups, Title); return; }
                var browser = new IconGroupBrowserWindow(iconPath, groups, _selectedIconIndex) { Owner = this };
                if (browser.ShowDialog() == true && browser.SelectedGroup != null)
                {
                    _selectedIconIndex = browser.SelectedGroup.ShellIndex;
                    SelectedIconImage.Source = browser.SelectedGroup.Preview;
                    _selectedIconPreview = browser.SelectedGroup.Preview;
                    SelectedIconText.Text = string.Format(Strings.Main_IndexN, browser.SelectedGroup.ShellIndex);
                    StatusText.Text = string.Format(Strings.Main_IconNSelected, browser.SelectedGroup.ShellIndex);
                }
                else StatusText.Text = Strings.Main_IconCancelled;
            }
            catch (Exception ex) { ShowError(Strings.Main_IconBrowserFailed, ex); }
        }

        private void RefreshSelectedIconPreview()
        {
            try
            {
                string iconPath = IconPathBox.Text.Trim();
                if (!File.Exists(iconPath)) return;
                var group = IconResourceReader.Read(iconPath).FirstOrDefault(item => item.ShellIndex == _selectedIconIndex);
                SelectedIconImage.Source = group?.Preview;
                _selectedIconPreview = group?.Preview;
                SelectedIconText.Text = group == null ? Strings.Main_NotSelected : string.Format(Strings.Main_IndexN, group.ShellIndex);
            }
            catch { SelectedIconImage.Source = null; _selectedIconPreview = null; SelectedIconText.Text = Strings.Main_PreviewUnavailable; }
        }

        private async void Search_Click(object sender, RoutedEventArgs e)
        {
            // WPF controls must only be read from the UI thread. Capture every value
            // before Task.Run so the worker never touches a DispatcherObject.
            RootBox.CommitHistory(); QueryBox.CommitHistory(); IconPathBox.CommitHistory();
            string root = RootBox.Text.Trim();
            string visibleQuery = QueryBox.Text.Trim();
            string query = _pendingSearchQuery ?? visibleQuery;
            bool folderListMode = string.IsNullOrWhiteSpace(query);
            bool gitSearchRequested = _pendingSearchQuery != null && string.Equals(query, ".git", StringComparison.OrdinalIgnoreCase);
            // The Search button always owns only the Search tab. Physical and Solution
            // are populated exclusively by the Git acquisition workflow.
            bool searchOnly = !gitSearchRequested;
            _pendingSearchQuery = null;
            bool fastSearch = FastNtfsSearchBox.IsChecked == true;
            SettingsService.SaveSearchRoot(root);
            SettingsService.SaveSearchQuery(visibleQuery);
            if (!Directory.Exists(root)) { MessageBox.Show(Strings.Main_LocationMissing, Title); return; }
            if (fastSearch && !IsAdministrator())
            {
                RestartForFastSearch(gitSearchRequested);
                return;
            }
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
            CountText.Text = Strings.Main_ZeroMatches; SetSearching(true);
            try
            {
                System.Collections.Generic.List<FolderMatch> solutionRoots = new List<FolderMatch>();
                if (fastSearch)
                {
                    FastSearchResult fastResult = null;
                    try
                    {
                        fastResult = await Task.Run(() => new FastFolderSearchService().Search(root, query,
                            count => Dispatcher.BeginInvoke(new Action(() => StatusText.Text = count == 0 ? Strings.Main_ReadingNtfs : string.Format(Strings.Main_IndexedFolders, count.ToString("N0")))), searchCts.Token));
                    }
                    catch (NotSupportedException)
                    {
                        StatusText.Text = Strings.Main_FastUnavailable;
                    }
                    catch (UnauthorizedAccessException)
                    {
                        StatusText.Text = Strings.Main_FastPermissionUnavailable;
                    }
                    catch (Win32Exception)
                    {
                        StatusText.Text = Strings.Main_DriveIndexFailed;
                    }

                    if (fastResult != null)
                    {
                        ImageSource defaultFolderIcon = FolderIconService.GetDefaultFolderIcon();
                        await Task.Run(() =>
                        {
                            foreach (FolderMatch item in fastResult.Matches)
                            {
                                searchCts.Token.ThrowIfCancellationRequested();
                                item.IconPreview = string.Equals(item.Reason, "Folder", StringComparison.Ordinal)
                                    ? defaultFolderIcon : FolderIconService.GetFolderIcon(item.Path);
                            }
                        }, searchCts.Token);
                        await AddTreeResultsAsync(fastResult.Matches, searchCts.Token, searchOnly);
                        _pathIndex = fastResult.Paths;
                        RefreshTreeItemsSource();
                        StatusText.Text = folderListMode ? string.Format(Strings.Main_FoldersAnalyzing, _results.Count) : string.Format(Strings.Main_MatchesAnalyzing, _results.Count);
                        await System.Windows.Threading.Dispatcher.Yield(System.Windows.Threading.DispatcherPriority.Background);
                        var fastService = new FastFolderSearchService();
                        if (gitSearchRequested)
                        {
                            Dictionary<string, string> analysis = await Task.Run(() => fastService.AnalyzeDevelopment(fastResult.Paths, searchCts.Token));
                            int updated = 0;
                            foreach (FolderMatch item in _results)
                            {
                                string reason;
                                if (analysis.TryGetValue(VolumePathIndex.Normalize(item.Path), out reason)) item.Reason = reason;
                                if ((++updated % 60) == 0)
                                {
                                    await System.Windows.Threading.Dispatcher.Yield(System.Windows.Threading.DispatcherPriority.Background);
                                }
                            }
                        }
                        if (!searchOnly)
                            solutionRoots = await BuildSolutions(root, searchCts.Token);
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
                }
                else if (folderListMode)
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
                    SelectSearchRootForFileList();
                }
                else
                    ShowTreeView(0);
            }
            catch (OperationCanceledException) { StatusText.Text = Strings.Main_SearchCancelled; }
            catch (Exception ex) { MessageBox.Show(ErrorMessages.English(ex), Title); StatusText.Text = Strings.Main_SearchFailed; }
            finally
            {
                if (ReferenceEquals(_searchCts, searchCts)) { _searchCts = null; SetSearching(false); }
                searchCts.Dispose();
            }
        }

        private Task<List<FolderMatch>> RunStandardSearch(string root, string query, CancellationToken token)
        {
            ImageSource defaultFolderIcon = FolderIconService.GetDefaultFolderIcon();
            return Task.Run(() =>
            {
                var matches = new List<FolderMatch>();
                new FolderSearchService().Search(root, query,
                    item => { item.IconPreview = string.Equals(item.Reason, "Folder", StringComparison.Ordinal) ? defaultFolderIcon : FolderIconService.GetFolderIcon(item.Path); matches.Add(item); },
                    count => Dispatcher.BeginInvoke(new Action(() => StatusText.Text = string.Format(Strings.Main_ScanningFolders, count.ToString("N0")))), token);
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
                VolumePathIndex paths = VolumePathIndex.BuildFromFileSystem(root,
                    count =>
                    {
                        int now = Environment.TickCount;
                        if (unchecked(now - lastReport) < 125) return;
                        lastReport = now;
                        Dispatcher.BeginInvoke(new Action(() => StatusText.Text = string.Format(Strings.Main_IndexedFoldersEllipsis, count.ToString("N0"))), System.Windows.Threading.DispatcherPriority.Background);
                    }, token);
                token.ThrowIfCancellationRequested();
                Dispatcher.BeginInvoke(new Action(() => StatusText.Text = Strings.Main_BuildingTree));
                List<FolderMatch> matches = new FastFolderSearchService().Search(paths, query, token);
                foreach (FolderMatch item in matches) item.IconPreview = defaultFolderIcon;
                return new StandardSearchResult(paths, matches);
            }, token);
        }

        private async Task ApplyStandardDevelopmentAnalysis(bool enabled, VolumePathIndex paths, CancellationToken token)
        {
            if (!enabled) return;
            StatusText.Text = string.Format(Strings.Main_FoldersAnalyzing, _results.Count);
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

        private Task<List<FolderMatch>> RunFolderList(string root, CancellationToken token)
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

        private void SortPhysicalTree()
        {
            SortCollection(_treeRoots);
        }

        private static void SortCollection(ObservableCollection<FolderMatch> items)
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

        private static bool IsAdministrator()
        {
            using (WindowsIdentity identity = WindowsIdentity.GetCurrent())
                return new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
        }

        private void RestartForFastSearch(bool gitSearchRequested)
        { RestartElevated("main", gitSearchRequested ? "git" : "search"); }

        internal bool RestartElevated(string target, string mainAction = null)
        {
            if (!ElevationService.Shared.CanChange) return false;
            string sessionPath = null;
            try
            {
                SaveFolderTrees();
                SettingsService.SaveSearchRoot(RootBox.Text);
                SettingsService.SaveSearchQuery(QueryBox.Text);
                SettingsService.SaveIconLibraryPath(IconPathBox.Text);
                var session = new ElevationResumeState { TargetWindow = target, MainAction = mainAction, MainRoot = RootBox.Text, MainQuery = QueryBox.Text };
                if (target == "mft") _differencerWindow?.CaptureElevation(session);
                if (target == "grep") _grepWindow?.CaptureElevation(session);
                sessionPath = session.Save();
                Rect bounds = WindowState == WindowState.Normal ? new Rect(Left, Top, ActualWidth, ActualHeight) : RestoreBounds;
                string placement = string.Format(CultureInfo.InvariantCulture,
                    " --window-left {0:R} --window-top {1:R} --window-width {2:R} --window-height {3:R}{4}",
                    bounds.Left, bounds.Top, bounds.Width, bounds.Height,
                    WindowState == WindowState.Maximized ? " --window-maximized" : string.Empty);
                Process.Start(new ProcessStartInfo(Assembly.GetExecutingAssembly().Location)
                {
                    UseShellExecute = true, Verb = "runas",
                    Arguments = "--fast-search --resume-session \"" + sessionPath + "\"" + placement
                });
                Application.Current.Shutdown();
                return true;
            }
            catch (Win32Exception ex) when (ex.NativeErrorCode == 1223)
            { StatusText.Text = Strings.Main_ElevationCancelled; }
            catch (Exception ex) { ShowError(Strings.Main_ElevationFailed, ex); }
            if (sessionPath != null) { try { File.Delete(sessionPath); } catch { } }
            return false;
        }

        private void RestoreElevation(ElevationResumeState session)
        {
            RootBox.Text = session.MainRoot; QueryBox.Text = session.MainQuery;
            if (session.TargetWindow == "mft") { MftDifferencer_Click(this, new RoutedEventArgs()); _differencerWindow.RestoreElevation(session); }
            else if (session.TargetWindow == "grep") { OpenGrep(session.GrepScopes); _grepWindow.RestoreElevation(session); }
            else if (session.MainAction == "git") GitSearch_Click(this, new RoutedEventArgs());
            else if (session.MainAction == "search") Search_Click(this, new RoutedEventArgs());
        }
        private void RestoreWindowPlacement(string[] arguments)
        {
            if (!TryReadArgument(arguments, "--window-left", out double left)
                || !TryReadArgument(arguments, "--window-top", out double top)
                || !TryReadArgument(arguments, "--window-width", out double width)
                || !TryReadArgument(arguments, "--window-height", out double height))
                return;

            if (width < MinWidth || height < MinHeight)
                return;

            var requested = new Rect(left, top, width, height);
            var virtualDesktop = new Rect(SystemParameters.VirtualScreenLeft, SystemParameters.VirtualScreenTop,
                SystemParameters.VirtualScreenWidth, SystemParameters.VirtualScreenHeight);
            if (!requested.IntersectsWith(virtualDesktop))
                return;

            WindowStartupLocation = WindowStartupLocation.Manual;
            Left = left;
            Top = top;
            Width = width;
            Height = height;
            if (arguments.Any(argument => string.Equals(argument, "--window-maximized", StringComparison.OrdinalIgnoreCase)))
                WindowState = WindowState.Maximized;
        }

        private static bool TryReadArgument(string[] arguments, string name, out double value)
        {
            value = 0;
            for (int index = 0; index < arguments.Length - 1; index++)
                if (string.Equals(arguments[index], name, StringComparison.OrdinalIgnoreCase))
                    return double.TryParse(arguments[index + 1], NumberStyles.Float,
                        CultureInfo.InvariantCulture, out value);
            return false;
        }

        private void GitSearch_Click(object sender, RoutedEventArgs e)
        {
            _pendingSearchQuery = ".git";
            Search_Click(sender, e);
        }
        private void Cancel_Click(object sender, RoutedEventArgs e) => _searchCts?.Cancel();
        private void InvertSelection_Click(object sender, RoutedEventArgs e)
        {
            foreach (FolderMatch item in VisibleItems().Where(item => item.IsActionable))
                item.SetSelected(!item.IsSelected, false, false);
        }

        private void FolderCheck_Click(object sender, RoutedEventArgs e)
        {
            var box = sender as System.Windows.Controls.CheckBox;
            var item = box?.DataContext as FolderMatch;
            if (item == null) return;
            item.SetSelected(box.IsChecked == true, true, false);
        }
        private void ExpandAll_Click(object sender, RoutedEventArgs e) { foreach (var item in CurrentItems()) item.IsExpanded = true; }
        private void CollapseAll_Click(object sender, RoutedEventArgs e) { foreach (var item in CurrentItems()) item.IsExpanded = false; }
        private void TreeTabs_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (!IsInitialized || !ReferenceEquals(e.Source, sender)) return;
            var tabs = sender as TabControl;
            if (tabs != null && tabs.SelectedIndex >= 0) ShowTreeView(tabs.SelectedIndex);
        }
        private void ShowTreeView(int view) { if (view != 2) _baseTreeView = view; _treeView = view; _solutionView = view == 1; if (TreeTabs.SelectedIndex != view) TreeTabs.SelectedIndex = view; ResultsTree.ItemsSource = view == 0 ? _treeRoots : view == 1 ? _solutionRoots : _searchRoots; ApplyFolderFilter(); UpdateVisibleCount(); StatusText.Text = view == 0 ? string.Format(Strings.Main_FoldersFound, _results.Count) : view == 1 ? string.Format(Strings.Main_SolutionsFound, _solutionRoots.Count) : string.Format(Strings.Main_SearchResults, _searchResultCount); }
        private void RefreshTreeItemsSource() { ResultsTree.ItemsSource = _treeView == 0 ? _treeRoots : _treeView == 1 ? _solutionRoots : _searchRoots; }
        private void ShowSolutionView() => ShowTreeView(1);
        private void UpdateVisibleCount() { CountText.Text = string.Format(_solutionView ? Strings.Main_NItems : Strings.Main_NFolders, CurrentItems().Count(item => !item.IsHidden && !item.IsFilterHidden)); }
        private System.Collections.Generic.IEnumerable<FolderMatch> CurrentItems() => _filteredViewItems ?? Flatten(_treeView == 0 ? _treeRoots : _treeView == 1 ? _solutionRoots : _searchRoots).ToList();
        private System.Collections.Generic.IEnumerable<FolderMatch> VisibleItems() => _filteredViewItems != null
            ? _filteredViewItems.Where(item => !item.IsHidden) : FlattenVisible(_treeView == 0 ? _treeRoots : _treeView == 1 ? _solutionRoots : _searchRoots);
        private static void AssignParents(FolderMatch node)
        {
            if (node == null) return;
            foreach (FolderMatch child in node.Children)
            {
                child.Parent = node;
                AssignParents(child);
            }
        }

        private static System.Collections.Generic.IEnumerable<FolderMatch> Flatten(System.Collections.Generic.IEnumerable<FolderMatch> roots)
        {
            foreach (FolderMatch item in roots) { yield return item; foreach (FolderMatch child in Flatten(item.Children)) yield return child; }
        }
        private static System.Collections.Generic.IEnumerable<FolderMatch> FlattenVisible(System.Collections.Generic.IEnumerable<FolderMatch> roots)
        {
            foreach (FolderMatch item in roots)
            {
                if (item.IsHidden) continue;
                yield return item;
                if (item.IsExpanded) foreach (FolderMatch child in FlattenVisible(item.Children)) yield return child;
            }
        }

        private void AddTreeResult(FolderMatch item)
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
            CountText.Text = string.Format(Strings.Main_NMatches, _results.Count);
        }

        private void AddTreeResults(IEnumerable<FolderMatch> items)
        {
            bool physicalViewVisible = ReferenceEquals(ResultsTree.ItemsSource, _treeRoots);
            if (physicalViewVisible) ResultsTree.ItemsSource = null;
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
            finally { if (physicalViewVisible) ResultsTree.ItemsSource = _treeRoots; }
            CountText.Text = string.Format(Strings.Main_NMatches, _results.Count);
        }

        private async Task AddTreeResultsAsync(IEnumerable<FolderMatch> items, CancellationToken token, bool intoSearch = false)
        {
            List<FolderMatch> source = items.ToList();
            StatusText.Text = string.Format(Strings.Main_BuildingRows, source.Count.ToString("N0"));
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
                CountText.Text = string.Format(Strings.Main_SearchResults, _searchResultCount);
                return;
            }
            _results.Clear();
            foreach (FolderMatch item in built.Items) _results.Add(item);
            _treeRoots.Clear();
            foreach (FolderMatch root in built.Roots) _treeRoots.Add(root);
            RefreshTreeItemsSource();
            CountText.Text = string.Format(Strings.Main_NFolders, _results.Count);
        }

        private static bool IsAncestorPath(string parent, string child)
        {
            string prefix = parent.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
            return child.StartsWith(prefix, StringComparison.OrdinalIgnoreCase);
        }

        private void UseAsSearchLocation_Click(object sender, RoutedEventArgs e)
        {
            var menuItem = sender as System.Windows.Controls.MenuItem;
            var folder = menuItem?.CommandParameter as FolderMatch;
            if (folder == null) return;
            RootBox.Text = folder.Path; RootBox.CommitHistory();
            SettingsService.SaveSearchRoot(folder.Path);
            StatusText.Text = string.Format(Strings.Main_LocationSet, folder.Path);
        }

        private static FolderMatch ContextFolder(object sender)
        { return (sender as MenuItem)?.CommandParameter as FolderMatch; }

        private void OpenExplorer_Click(object sender, RoutedEventArgs e)
        {
            FolderMatch folder = ContextFolder(sender);
            if (folder == null || !Directory.Exists(folder.Path)) return;
            try { Process.Start(new ProcessStartInfo("explorer.exe", "\"" + folder.Path + "\"") { UseShellExecute = true }); }
            catch (Exception ex) { ShowError(Strings.Main_ExplorerFailed, ex); }
        }

        private void GrepFolder_Click(object sender, RoutedEventArgs e)
        {
            FolderMatch folder = ContextFolder(sender);
            if (folder == null || !Directory.Exists(folder.Path)) return;
            foreach (FolderMatch item in CurrentItems()) item.IsSelected = ReferenceEquals(item, folder);
            OpenGrep(new[] { folder.Path });
        }

        private void HideSelected_Click(object sender, RoutedEventArgs e)
        {
            foreach (FolderMatch item in CurrentItems())
                item.IsHidden = !item.IsSelected;
            UpdateVisibleCount();
        }

        private void Unhide_Click(object sender, RoutedEventArgs e)
        {
            foreach (FolderMatch item in CurrentItems())
                item.IsHidden = false;
            UpdateVisibleCount();
        }

        private async void ResultsTree_SelectedItemChanged(object sender, RoutedPropertyChangedEventArgs<object> e)
        {
            if (_syncingTreeFromFile) return;
            _fileListCts?.Cancel();
            var fileListCts = new CancellationTokenSource();
            _fileListCts = fileListCts;
            _files.Clear();
            FolderMatch folder = e.NewValue as FolderMatch;
            if (folder != null)
            {
                if (_treeView == 0) _physicalCurrent = folder;
                else if (_treeView == 1) _solutionCurrent = folder;
                else _searchCurrent = folder;
            }
            FilePanelTitle.Text = folder == null ? Strings.Common_Files : string.Format(Strings.Main_FilesHeader, folder.Name);
            FilePanelTitle.ToolTip = folder?.Path;
            if (folder == null || string.IsNullOrEmpty(folder.Path)) { SetFilePanelBusy(false); return; }
            SetFilePanelBusy(true);
            string[] searchKeys = (QueryBox.Text ?? string.Empty).Split((char[])null, StringSplitOptions.RemoveEmptyEntries)
                .Select(key => key.Trim().TrimStart('*')).Where(key => key.Length > 0).Distinct(StringComparer.CurrentCultureIgnoreCase).ToArray();
            string[] paths = _treeView == 2 ? CollectSearchTabFiles(folder) : FilesInFolder(folder.Path);
            try
            {
                List<FileListItem> items = await Task.Run(() =>
                {
                    var loaded = new List<FileListItem>(paths.Length);
                    foreach (string path in paths)
                    {
                        fileListCts.Token.ThrowIfCancellationRequested();
                        loaded.Add(new FileListItem(path, searchKeys));
                    }
                    return loaded;
                }, fileListCts.Token);
                if (fileListCts.IsCancellationRequested || !ReferenceEquals(_fileListCts, fileListCts)) return;
                foreach (FileListItem item in items)
                {
                    _files.Add(item);
                    if ((_files.Count % 32) == 0) await System.Windows.Threading.Dispatcher.Yield();
                    if (fileListCts.IsCancellationRequested) return;
                }
                FileListItem firstMatch = items.FirstOrDefault(item => item.IsSearchMatch);
                if (firstMatch != null)
                {
                    FileList.ScrollIntoView(firstMatch);
                    FileIconList.ScrollIntoView(firstMatch);
                }
            }
            catch (OperationCanceledException) { }
            finally
            {
                if (ReferenceEquals(_fileListCts, fileListCts) && !fileListCts.IsCancellationRequested)
                    SetFilePanelBusy(false);
            }
        }

        private void SelectSearchRootForFileList()
        {
            if (_searchRoots.Count == 0) return;
            FolderMatch root = _searchRoots[0];
            root.IsExpanded = true;
            Dispatcher.BeginInvoke(new Action(() =>
            {
                if (_treeView != 2 || _searchRoots.Count == 0) return;
                FolderMatch current = _searchRoots[0];
                current.IsExpanded = true;
                current.IsCurrent = true;
            }), System.Windows.Threading.DispatcherPriority.Loaded);
        }

        private string[] FilesInFolder(string path)
        {
            VolumePathNode node = _pathIndex?.Find(path);
            if (node != null) return node.Files.Select(file => file.Path).ToArray();
            try { return Directory.EnumerateFiles(path).OrderBy(item => item, StringComparer.CurrentCultureIgnoreCase).ToArray(); }
            catch { return Array.Empty<string>(); }
        }

        private string[] CollectSearchTabFiles(FolderMatch folder)
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

        private void FileList_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (_syncingTreeFromFile || _treeView != 2) return;
            FileListItem file = (sender as System.Windows.Controls.Primitives.Selector)?.SelectedItem as FileListItem;
            if (file == null) return;
            RevealSearchFolder(Path.GetDirectoryName(file.Path));
        }

        private void RevealSearchFolder(string directory)
        {
            if (string.IsNullOrEmpty(directory) || _searchRoots.Count == 0) return;
            FolderMatch target = FindSearchFolder(directory);
            if (target == null) return;
            for (FolderMatch ancestor = target.Parent; ancestor != null; ancestor = ancestor.Parent)
                ancestor.IsExpanded = true;
            target.IsExpanded = true;
            _syncingTreeFromFile = true;
            foreach (FolderMatch item in Flatten(_searchRoots))
                if (item.IsCurrent && item != target) item.IsCurrent = false;
            target.IsCurrent = true;
            ResultsTree.UpdateLayout();
            ScheduleFolderIntoView(target);
        }

        private void ScheduleFolderIntoView(FolderMatch target)
        {
            Action bring = () => BringFolderIntoView(ResultsTree, target);
            bring();
            Dispatcher.BeginInvoke(bring, System.Windows.Threading.DispatcherPriority.Loaded);
            Dispatcher.BeginInvoke(new Action(() =>
            {
                try { bring(); }
                finally { _syncingTreeFromFile = false; }
            }), System.Windows.Threading.DispatcherPriority.ContextIdle);
        }

        private FolderMatch FindSearchFolder(string directory)
        {
            string current = directory.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            while (!string.IsNullOrEmpty(current))
            {
                foreach (FolderMatch item in Flatten(_searchRoots))
                {
                    string path = (item.Path ?? string.Empty).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
                    if (string.Equals(path, current, StringComparison.OrdinalIgnoreCase)) return item;
                }
                current = Path.GetDirectoryName(current);
            }
            return _searchRoots.Count > 0 ? _searchRoots[0] : null;
        }

        private static void BringFolderIntoView(TreeView tree, FolderMatch target)
        {
            if (tree == null || target == null) return;
            var path = new List<FolderMatch>();
            for (FolderMatch node = target; node != null; node = node.Parent)
                path.Add(node);
            path.Reverse();
            TreeViewItem item = ContainerAlongPath(tree, path);
            ScrollTreeItemIntoView(tree, item);
        }

        private static TreeViewItem ContainerAlongPath(ItemsControl parent, List<FolderMatch> path)
        {
            TreeViewItem current = null;
            ItemsControl host = parent;
            foreach (FolderMatch node in path)
            {
                if (host == null) return current;
                host.ApplyTemplate();
                host.UpdateLayout();
                var item = host.ItemContainerGenerator.ContainerFromItem(node) as TreeViewItem;
                if (item == null)
                {
                    int index = host.Items.IndexOf(node);
                    if (index >= 0) item = host.ItemContainerGenerator.ContainerFromIndex(index) as TreeViewItem;
                }
                if (item == null) return current;
                item.IsExpanded = true;
                item.UpdateLayout();
                current = item;
                host = item;
            }
            return current;
        }

        private static void ScrollTreeItemIntoView(TreeView tree, TreeViewItem item)
        {
            if (tree == null || item == null) return;
            item.BringIntoView();
            ScrollViewer viewer = FindScrollViewer(tree);
            if (viewer == null || !item.IsVisible) return;
            try
            {
                Point pos = item.TransformToAncestor(viewer).Transform(new Point(0, 0));
                double top = pos.Y;
                double bottom = top + item.ActualHeight;
                if (top < 0) viewer.ScrollToVerticalOffset(viewer.VerticalOffset + top - 8);
                else if (bottom > viewer.ViewportHeight)
                    viewer.ScrollToVerticalOffset(viewer.VerticalOffset + bottom - viewer.ViewportHeight + 8);
            }
            catch (InvalidOperationException) { }
        }

        private static ScrollViewer FindScrollViewer(DependencyObject root)
        {
            if (root is ScrollViewer viewer) return viewer;
            for (int i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
            {
                ScrollViewer found = FindScrollViewer(VisualTreeHelper.GetChild(root, i));
                if (found != null) return found;
            }
            return null;
        }

        private void FileList_MouseDoubleClick(object sender, System.Windows.Input.MouseButtonEventArgs e)
        {
            FileListItem file = (sender as System.Windows.Controls.Primitives.Selector)?.SelectedItem as FileListItem;
            if (file == null) return;
            try
            {
                Process.Start(new ProcessStartInfo(file.Path) { UseShellExecute = true });
            }
            catch (Exception ex) { ShowError(Strings.Main_OpenFileFailed, ex); }
        }

        private void CompactTree_Click(object sender, RoutedEventArgs e) => ApplyTreeDensity(true, true);

        private void ComfortableTree_Click(object sender, RoutedEventArgs e) => ApplyTreeDensity(false, true);

        private void ApplyTreeDensity(bool compact, bool announce)
        {
            TreeCompact = compact;
            HighlightTreeDensityButtons();
            SettingsService.SaveTreeCompact(compact);
            if (announce) StatusText.Text = compact ? Strings.Main_TreeCompact : Strings.Main_TreeComfortable;
        }

        private void HighlightTreeDensityButtons()
        {
            System.Windows.Media.Brush selected = (System.Windows.Media.Brush)FindResource("ThemeSelected");
            System.Windows.Media.Brush secondary = (System.Windows.Media.Brush)FindResource("Secondary");
            if (CompactTreeButton != null) CompactTreeButton.Background = TreeCompact ? selected : secondary;
            if (ComfortableTreeButton != null) ComfortableTreeButton.Background = TreeCompact ? secondary : selected;
        }

        private void FileListView_Click(object sender, RoutedEventArgs e)
        {
            FileList.Visibility = Visibility.Visible;
            FileIconList.Visibility = Visibility.Collapsed;
            HighlightFileViewButtons(list: true, large: false);
        }

        private void FileIconSmall_Click(object sender, RoutedEventArgs e)
        {
            ApplyFileIconSize(false);
        }

        private void FileIconLarge_Click(object sender, RoutedEventArgs e)
        {
            ApplyFileIconSize(true);
        }

        private void ApplyFileIconSize(bool large)
        {
            _largeFileIcons = large;
            FileList.Visibility = Visibility.Collapsed;
            FileIconList.Visibility = Visibility.Visible;
            FileIconList.ItemsPanel = (ItemsPanelTemplate)FindResource(large ? "FileIconLargePanel" : "FileIconSmallPanel");
            FileIconList.ItemTemplate = (DataTemplate)FindResource(large ? "FileIconLargeTemplate" : "FileIconSmallTemplate");
            HighlightFileViewButtons(list: false, large: large);
            ShowIconLayoutBusy();
        }

        private void HighlightFileViewButtons(bool list, bool large)
        {
            System.Windows.Media.Brush selected = (System.Windows.Media.Brush)FindResource("ThemeSelected");
            System.Windows.Media.Brush secondary = (System.Windows.Media.Brush)FindResource("Secondary");
            if (FileListViewButton != null) FileListViewButton.Background = list ? selected : secondary;
            if (FileIconLargeButton != null) FileIconLargeButton.Background = !list && large ? selected : secondary;
            if (FileIconSmallButton != null) FileIconSmallButton.Background = !list && !large ? selected : secondary;
        }

        private void ShowIconLayoutBusy()
        {
            if (_files.Count < 40) return;
            SetFilePanelBusy(true);
            Dispatcher.BeginInvoke(new Action(() => SetFilePanelBusy(false)), System.Windows.Threading.DispatcherPriority.ContextIdle);
        }

        private void SetFilePanelBusy(bool busy)
        {
            if (FilePanelBusy != null)
                FilePanelBusy.Visibility = busy ? Visibility.Visible : Visibility.Collapsed;
        }

        private void FolderFilter_TextChanged(object sender, TextChangedEventArgs e)
        { _filterTimer.Stop(); _filterTimer.Start(); }

        private void ClearFolderFilter_Click(object sender, RoutedEventArgs e)
        { FolderFilterBox.Clear(); }

        private async void ApplyFolderFilter()
        {
            _filterCts?.Cancel();
            var filterCts = new CancellationTokenSource();
            _filterCts = filterCts;
            string[] terms = (FolderFilterBox.Text ?? string.Empty).Split((char[])null, StringSplitOptions.RemoveEmptyEntries);
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
                ResultsTree.ItemsSource = null;
                if (matches != null) foreach (FolderMatch item in matches) item.IsExpanded = false;
                _filteredViewItems = matches;
                ResultsTree.ItemsSource = matches == null
                    ? (IEnumerable<FolderMatch>)(_treeView == 0 ? _treeRoots : _treeView == 1 ? _solutionRoots : _searchRoots) : matches;
                UpdateVisibleCount();
            }
            catch (OperationCanceledException) { }
        }

        private void HookPathBox(TextBox box)
        {
            if (box == null) return;
            box.GotKeyboardFocus += (sender, args) => ShowTextEnd(box);
            box.LostKeyboardFocus += (sender, args) => ShowTextEnd(box);
            box.TextChanged += (sender, args) =>
            {
                if (!box.IsKeyboardFocusWithin) ShowTextEnd(box);
            };
        }

        private void ShowTextEnd(TextBox box)
        {
            if (box == null) return;
            Dispatcher.BeginInvoke(new Action(() =>
            {
                box.CaretIndex = (box.Text ?? string.Empty).Length;
                box.ScrollToHorizontalOffset(Math.Max(0, box.ExtentWidth - box.ViewportWidth));
            }), System.Windows.Threading.DispatcherPriority.Loaded);
        }

        private void Apply_Click(object sender, RoutedEventArgs e)
        {
            var selected = VisibleItems().Where(item => item.IsActionable && item.IsSelected)
                .GroupBy(item => item.Path, StringComparer.OrdinalIgnoreCase)
                .Select(group => group.First()).ToList();
            bool addToGitIgnore = AddToGitIgnoreBox.IsChecked == true;
            if (selected.Count == 0) { MessageBox.Show(Strings.Main_SelectOneFolder, Title); return; }
            if (MessageBox.Show(string.Format(Strings.Main_ConfirmApply, selected.Count), Title, MessageBoxButton.OKCancel, MessageBoxImage.Question) != MessageBoxResult.OK) return;

            int succeeded = 0;
            var errors = new System.Collections.Generic.List<string>();
            var changedFolders = new List<string>();
            var service = new DesktopIniService();

            foreach (var item in selected)
            {
                try
                {
                    service.Apply(item.Path, IconPathBox.Text, _selectedIconIndex, addToGitIgnore, false);
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

            StatusText.Text = string.Format(Strings.Main_AppliedTo, succeeded);
            MessageBox.Show(errors.Count == 0 ? Strings.Main_ApplyOk : string.Format(Strings.Main_ApplyResult, succeeded, errors.Count) + "\n\n" + string.Join("\n", errors.Take(5)), Title);
        }

        private void Remove_Click(object sender, RoutedEventArgs e)
        {
            var selected = VisibleItems().Where(item => item.IsActionable && item.IsSelected)
                .GroupBy(item => item.Path, StringComparer.OrdinalIgnoreCase)
                .Select(group => group.First()).ToList();
            if (selected.Count == 0) { MessageBox.Show(Strings.Main_SelectOneFolder, Title); return; }
            if (MessageBox.Show(string.Format(Strings.Main_ConfirmRemove, selected.Count), Title, MessageBoxButton.OKCancel, MessageBoxImage.Warning) != MessageBoxResult.OK) return;

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

            StatusText.Text = string.Format(Strings.Main_RemovedFrom, succeeded);
            MessageBox.Show(errors.Count == 0 ? Strings.Main_RemoveOk : string.Format(Strings.Main_RemoveResult, succeeded, errors.Count) + "\n\n" + string.Join("\n", errors.Take(5)), Title);
        }

        private MftDifferencerWindow _differencerWindow;
        private void MftDifferencer_Click(object sender, RoutedEventArgs e)
        {
            if (_differencerWindow == null)
            {
                _differencerWindow = new MftDifferencerWindow { Owner = this };
                _differencerWindow.Closed += (s, args) => _differencerWindow = null;
                _differencerWindow.Show();
            }
            else WindowActivationService.BringToFront(_differencerWindow);
        }

        private void Grep_Click(object sender, RoutedEventArgs e)
        {
            var visible = CurrentItems()
                .Where(item => item.IsActionable && !item.IsHidden && !item.IsFilterHidden && Directory.Exists(item.Path))
                .ToList();

            if (visible.Count == 0)
            {
                MessageBox.Show(Strings.Main_NoGrepFolders, Title, MessageBoxButton.OK, MessageBoxImage.Information);
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
                    if (ancestor == null) item.SetSelected(true, true, false);
                }
                scopes = GetSelectedGrepScopes();
            }

            if (scopes.Count == 0)
            {
                MessageBox.Show(Strings.Main_NoGrepFolders, Title, MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            OpenGrep(scopes);
        }

        private void OpenGrep(IReadOnlyList<string> scopes)
        {
            if (_grepWindow == null)
            {
                _grepWindow = new GrepWindow(GetSelectedGrepScopes, scopes) { Owner = this };
                _grepWindow.Closed += (closedSender, args) => _grepWindow = null;
                _grepWindow.Show();
            }
            else
            {
                _grepWindow.SetExplicitScopes(scopes);
                if (_grepWindow.WindowState == WindowState.Minimized) _grepWindow.WindowState = WindowState.Normal;
                WindowActivationService.BringToFront(_grepWindow);
            }
        }

        private IReadOnlyList<string> GetSelectedGrepScopes()
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

        private void SetSearching(bool value)
        {
            GitSearchButton.IsEnabled = !value; SearchButton.IsEnabled = !value; CancelButton.IsEnabled = value;
            ApplyButton.IsEnabled = !value; RemoveButton.IsEnabled = !value; GrepButton.IsEnabled = !value;
            ElevationService.Shared.SetBusy(this, value);
            SearchProgress.Visibility = value ? Visibility.Visible : Visibility.Collapsed;
            if (value)
                Dispatcher.BeginInvoke(new Action(() => AnimateSearchProgress(true)), DispatcherPriority.Loaded);
            else
            {
                AnimateSearchProgress(false);
                SetTreePanelBusy(false);
            }
        }

        private void AnimateSearchProgress(bool value)
        {
            SearchProgress.ApplyTemplate();
            var marquee = SearchProgress.Template.FindName("Marquee", SearchProgress) as Border;
            if (marquee == null) return;
            var currentTransform = marquee.RenderTransform as TranslateTransform;
            var transform = currentTransform == null ? new TranslateTransform() : currentTransform.CloneCurrentValue();
            marquee.RenderTransform = transform;
            if (!value) { transform.BeginAnimation(TranslateTransform.XProperty, null); transform.X = 0; return; }
            double distance = Math.Max(0, SearchProgress.ActualWidth - (marquee.ActualWidth > 0 ? marquee.ActualWidth : 150));
            var animation = new DoubleAnimation(0, distance, TimeSpan.FromSeconds(1.2))
            { AutoReverse = true, RepeatBehavior = RepeatBehavior.Forever };
            transform.BeginAnimation(TranslateTransform.XProperty, animation, HandoffBehavior.SnapshotAndReplace);
        }

        private async Task<List<FolderMatch>> BuildSolutions(string root, CancellationToken token)
        {
            ShowTreeView(1);
            SetTreePanelBusy(true);
            try
            {
                return await Task.Run(() => SolutionTreeService.Build(root, token), token);
            }
            finally
            {
                SetTreePanelBusy(false);
            }
        }

        private void SetTreePanelBusy(bool busy)
        {
            if (TreePanelBusy != null)
                TreePanelBusy.Visibility = busy ? Visibility.Visible : Visibility.Collapsed;
        }
        private void LightTheme_Click(object sender, RoutedEventArgs e) => SetTheme(false);
        private void DarkTheme_Click(object sender, RoutedEventArgs e) => SetTheme(true);
        private void SetTheme(bool dark)
        {
            ThemeService.Apply(dark);
            LightThemeButton.IsChecked = !dark;
            DarkThemeButton.IsChecked = dark;
            SettingsService.SaveDarkMode(dark);
            HighlightTreeDensityButtons();
            HighlightFileViewButtons(FileList.Visibility == Visibility.Visible, _largeFileIcons);
        }
        protected override void OnClosing(System.ComponentModel.CancelEventArgs e)
        {
            if (_differencerWindow != null)
            {
                if (_differencerWindow.IsWorking) e.Cancel = true;
                else _differencerWindow.SaveState();
            }
            base.OnClosing(e);
            if (!e.Cancel) SaveFolderTrees();
        }

        private void RestoreFolderTrees()
        {
            try
            {
                if (_startup?.TreeError != null) { StatusText.Text = string.Format(Strings.Main_RestoreTreesFailed, _startup.TreeError); return; }
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
                StatusText.Text = Strings.Main_TreesRestored;
            }
            catch (Exception ex) { StatusText.Text = string.Format(Strings.Main_RestoreTreesFailed, ErrorMessages.English(ex)); }
        }

        private void SaveFolderTrees()
        {
            // A cancelled/in-progress rebuild must not replace the last complete trees.
            if (_rebuildingFolderTrees || _folderTreeRoot == null) return;
            try
            {
                var icons = new List<byte[]>();
                var iconIds = new Dictionary<ImageSource, int>();
                FolderTreeStateService.Save(new FolderTreeState
                {
                    Root = _folderTreeRoot, View = _baseTreeView,
                    Icons = icons,
                    Physical = FolderTreeStateService.Capture(_treeRoots, _physicalCurrent, icons, iconIds),
                    Solution = FolderTreeStateService.Capture(_solutionRoots, _solutionCurrent, icons, iconIds)
                });
            }
            catch (Exception ex) { StatusText.Text = string.Format(Strings.Main_SaveTreesFailed, ErrorMessages.English(ex)); }
        }

        private void Close_Click(object sender, RoutedEventArgs e) => Close();
        private void ShowError(string message, Exception ex) { StatusText.Text = message; MessageBox.Show(message + "\n\n" + ErrorMessages.English(ex), Title, MessageBoxButton.OK, MessageBoxImage.Error); }
        protected override void OnClosed(EventArgs e) { _searchCts?.Cancel(); _grepWindow?.Close(); SettingsService.SaveIconLibraryPath(IconPathBox.Text); SettingsService.SaveSearchQuery(QueryBox.Text); SettingsService.SaveSearchRoot(RootBox.Text); base.OnClosed(e); }

        private bool _languageReady;

        private sealed class LanguageChoice
        {
            public LanguageChoice(string code, ImageSource flag, string name)
            {
                Code = code; Flag = flag; Name = name;
            }
            public string Code { get; }
            public ImageSource Flag { get; }
            public string Name { get; }
        }

        private static ImageSource[] LoadFlagIcons()
        {
            var flags = new ImageSource[4];
            string path = PrepareFlagLibrary(FindFlagLibrary());
            if (path == null) return flags;
            for (int index = 0; index < flags.Length; index++)
            {
                flags[index] = ExtractFlagIcon(path, index);
                if (flags[index] == null) flags[index] = ExtractFlagIconFromResources(path, index);
            }
            return flags;
        }

        private static string FindFlagLibrary()
        {
            string baseDir = AppDomain.CurrentDomain.BaseDirectory;
            string[] candidates =
            {
                Path.Combine(baseDir, "Assets", "Flag.icl"),
                Path.Combine(baseDir, "Assets", "flag.icl"),
                Path.GetFullPath(Path.Combine(baseDir, "..", "..", "Assets", "Flag.icl")),
                Path.GetFullPath(Path.Combine(baseDir, "..", "..", "Assets", "flag.icl")),
                Path.GetFullPath(Path.Combine(Environment.CurrentDirectory, "Assets", "Flag.icl"))
            };
            return candidates.FirstOrDefault(File.Exists);
        }

        private static string PrepareFlagLibrary(string path)
        {
            if (string.IsNullOrEmpty(path) || !File.Exists(path)) return null;
            UnblockInternetZone(path);
            try
            {
                string cacheDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "DesktopIniManager");
                Directory.CreateDirectory(cacheDir);
                string cache = Path.Combine(cacheDir, "Flag.icl");
                if (!File.Exists(cache) || File.GetLastWriteTimeUtc(path) > File.GetLastWriteTimeUtc(cache))
                    File.Copy(path, cache, true);
                UnblockInternetZone(cache);
                return File.Exists(cache) ? cache : path;
            }
            catch
            {
                return path;
            }
        }

        private static void UnblockInternetZone(string path)
        {
            try { File.Delete(path + ":Zone.Identifier"); }
            catch { }
        }

        private static ImageSource ExtractFlagIcon(string path, int index)
        {
            var large = new IntPtr[1];
            var small = new IntPtr[1];
            ExtractIconEx(path, index, large, small, 1);
            IntPtr handle = large[0] != IntPtr.Zero ? large[0] : small[0];
            if (handle == IntPtr.Zero) return null;
            try
            {
                var bitmap = Imaging.CreateBitmapSourceFromHIcon(handle, Int32Rect.Empty, BitmapSizeOptions.FromWidthAndHeight(16, 16));
                bitmap.Freeze();
                return bitmap;
            }
            catch { return null; }
            finally
            {
                if (large[0] != IntPtr.Zero) DestroyIcon(large[0]);
                if (small[0] != IntPtr.Zero) DestroyIcon(small[0]);
            }
        }

        private static ImageSource ExtractFlagIconFromResources(string path, int index)
        {
            try
            {
                var group = IconResourceReader.Read(path).FirstOrDefault(item => item.ShellIndex == index);
                return group != null ? group.Preview : null;
            }
            catch { return null; }
        }

        [System.Runtime.InteropServices.DllImport("shell32.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode)]
        private static extern uint ExtractIconEx(string file, int index, IntPtr[] large, IntPtr[] small, uint count);

        [System.Runtime.InteropServices.DllImport("user32.dll")]
        private static extern bool DestroyIcon(IntPtr icon);

        private void InitLanguageBox()
        {
            ImageSource[] flags = LoadFlagIcons();
            var items = new[]
            {
                new LanguageChoice("en", flags[0], "English"),
                new LanguageChoice("ja", flags[1], "Japanese"),
                new LanguageChoice("zh-Hans", flags[2], "Chinese"),
                new LanguageChoice("ko", flags[3], "Korean")
            };
            LanguageBox.ItemsSource = items;
            string current = StringOverlay.ResolveCulture().Name;
            LanguageChoice selected = items[0];
            foreach (LanguageChoice item in items)
            {
                if (string.Equals(item.Code, current, StringComparison.OrdinalIgnoreCase) ||
                    (item.Code == "zh-Hans" && current.StartsWith("zh", StringComparison.OrdinalIgnoreCase)) ||
                    (item.Code != "zh-Hans" && current.StartsWith(item.Code, StringComparison.OrdinalIgnoreCase)))
                {
                    selected = item;
                    break;
                }
            }
            LanguageBox.SelectedItem = selected;
            _languageReady = true;
            ScheduleResetCaption();
        }

        private async void LanguageBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (!_languageReady) return;
            var choice = LanguageBox.SelectedItem as LanguageChoice;
            if (choice == null) return;
            StringOverlay.SetCulture(choice.Code);
            Title = Strings.App_Title;
            ScheduleResetCaption();
            await RelayoutChildWindows();
        }

        private void ScheduleResetCaption()
        {
            Dispatcher.BeginInvoke(new Action(UpdateResetCaption), DispatcherPriority.ContextIdle);
        }

        private void UpdateResetCaption()
        {
            if (ResetButton == null) return;
            string language = StringOverlay.ResolveCulture().TwoLetterISOLanguageName;
            string label, tip;
            if (language == "ja") { label = "リセット"; tip = "設定・履歴・フォルダー一覧を初期化します"; }
            else if (language == "zh") { label = "重置"; tip = "清除设置、历史和文件夹列表"; }
            else if (language == "ko") { label = "초기화"; tip = "설정, 기록, 폴더 목록을 초기화합니다"; }
            else { label = "Reset"; tip = "Clear settings, history, and folder lists"; }
            ResetButton.ToolTip = tip;
            if (ResetButtonLabel != null) ResetButtonLabel.Text = label;
        }

        private void Reset_Click(object sender, RoutedEventArgs e)
        {
            string language = StringOverlay.ResolveCulture().TwoLetterISOLanguageName;
            string title = language == "ja" ? "リセット" : language == "zh" ? "重置" : language == "ko" ? "초기화" : "Reset";
            string message = language == "ja"
                ? "設定、入力履歴、フォルダー一覧、GREP / MFT の状態、エディターとアイコンの選択を初期化します。よろしいですか。"
                : language == "zh"
                ? "将清除设置、输入历史、文件夹列表、GREP / MFT 状态以及编辑器和图标选择。确定吗？"
                : language == "ko"
                ? "설정, 입력 기록, 폴더 목록, GREP / MFT 상태, 편집기와 아이콘 선택을 초기화합니다. 계속할까요?"
                : "This clears settings, input history, folder lists, GREP / MFT state, and editor and icon selections. Continue?";
            if (MessageBox.Show(this, message, title, MessageBoxButton.OKCancel, MessageBoxImage.Warning) != MessageBoxResult.OK)
                return;

            _searchCts?.Cancel();
            _fileListCts?.Cancel();
            _filterCts?.Cancel();
            _grepWindow?.Close();
            _differencerWindow?.Close();
            foreach (Window window in Application.Current.Windows.Cast<Window>().ToArray())
            {
                if (!ReferenceEquals(window, this))
                    try { window.Close(); } catch { }
            }

            SettingsService.ClearAll();
            FolderTreeStateService.Clear();

            _results.Clear();
            _treeRoots.Clear();
            _solutionRoots.Clear();
            _searchRoots.Clear();
            _files.Clear();
            _pathIndex = null;
            _searchResultCount = 0;
            _filteredViewItems = null;
            _folderTreeRoot = null;
            _physicalCurrent = _solutionCurrent = _searchCurrent = null;
            _selectedIconIndex = 0;
            _selectedIconPreview = null;
            _pendingSearchQuery = null;
            SelectedIconImage.Source = null;
            RootBox.ResetField(string.Empty);
            IconPathBox.ResetField(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Assets", "folder_set.icl"));
            QueryBox.ResetField(string.Empty);
            FolderFilterBox.ResetField(string.Empty);
            SettingsService.SaveSearchQuery(string.Empty);
            SettingsService.SaveIconLibraryPath(IconPathBox.Text);
            if (AddToGitIgnoreBox != null) AddToGitIgnoreBox.IsChecked = false;
            ElevationService.Shared.Enabled = false;
            ApplyTreeDensity(false, false);
            ThemeService.Apply(false);
            LightThemeButton.IsChecked = true;
            DarkThemeButton.IsChecked = false;
            ShowTreeView(0);
            CountText.Text = string.Format(Strings.Main_NFolders, 0);
            StatusText.Text = Strings.Common_Ready;
            _languageReady = false;
            StringOverlay.SetCulture("en");
            InitLanguageBox();
            Title = Strings.App_Title;
            RefreshSelectedIconPreview();
            ScheduleResetCaption();
        }

        private async Task RelayoutChildWindows()
        {
            bool reopenGrep = _grepWindow != null;
            bool reopenMft = _differencerWindow != null;
            IReadOnlyList<string> scopes = reopenGrep ? GetSelectedGrepScopes() : null;
            Rect grepBounds = reopenGrep ? CaptureBounds(_grepWindow) : Rect.Empty;
            WindowState grepState = reopenGrep ? _grepWindow.WindowState : WindowState.Normal;
            Rect mftBounds = reopenMft ? CaptureBounds(_differencerWindow) : Rect.Empty;
            WindowState mftState = reopenMft ? _differencerWindow.WindowState : WindowState.Normal;

            var children = Application.Current.Windows.Cast<Window>().Where(window => !ReferenceEquals(window, this)).ToArray();
            await FadeWindows(children, 1, 0, TimeSpan.FromMilliseconds(220));
            foreach (Window window in children)
            {
                try { window.Close(); } catch { }
            }

            if (reopenMft)
            {
                MftDifferencer_Click(this, new RoutedEventArgs());
                RestoreWindowPlacement(_differencerWindow, mftBounds, mftState);
                PrepareFadeIn(_differencerWindow);
            }
            if (reopenGrep)
            {
                OpenGrep(scopes);
                RestoreWindowPlacement(_grepWindow, grepBounds, grepState);
                PrepareFadeIn(_grepWindow);
            }

            await FadeWindows(new Window[] { _differencerWindow, _grepWindow }.Where(window => window != null).ToArray(), 0, 1, TimeSpan.FromMilliseconds(280));
        }

        private static void PrepareFadeIn(Window window)
        {
            if (window == null) return;
            window.BeginAnimation(OpacityProperty, null);
            window.Opacity = 0;
        }

        private static Task FadeWindows(Window[] windows, double from, double to, TimeSpan duration)
        {
            if (windows == null || windows.Length == 0) return Task.CompletedTask;
            var tasks = new List<Task>();
            foreach (Window window in windows)
            {
                if (window == null) continue;
                var done = new TaskCompletionSource<bool>();
                var animation = new DoubleAnimation(from, to, duration)
                {
                    EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseInOut }
                };
                animation.Completed += (sender, args) => done.TrySetResult(true);
                window.BeginAnimation(OpacityProperty, null);
                window.Opacity = from;
                window.BeginAnimation(OpacityProperty, animation);
                tasks.Add(done.Task);
            }
            return tasks.Count == 0 ? Task.CompletedTask : Task.WhenAll(tasks);
        }

        private static Rect CaptureBounds(Window window)
        {
            if (window == null) return Rect.Empty;
            return window.WindowState == WindowState.Normal
                ? new Rect(window.Left, window.Top, window.Width, window.Height)
                : window.RestoreBounds;
        }

        private static void RestoreWindowPlacement(Window window, Rect bounds, WindowState state)
        {
            if (window == null || bounds.IsEmpty) return;
            window.WindowStartupLocation = WindowStartupLocation.Manual;
            window.WindowState = WindowState.Normal;
            window.Left = bounds.Left;
            window.Top = bounds.Top;
            window.Width = Math.Max(window.MinWidth, bounds.Width);
            window.Height = Math.Max(window.MinHeight, bounds.Height);
            window.WindowState = state == WindowState.Minimized ? WindowState.Minimized : state;
        }

        private sealed class StandardSearchResult
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
