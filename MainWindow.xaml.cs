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
using System.Windows.Media.Animation;
using System.Windows.Controls;
using FastVolumeIndex;

namespace DesktopIniManager
{
    public partial class MainWindow : Window
    {
        private readonly ObservableCollection<FolderMatch> _results = new ObservableCollection<FolderMatch>();
        private readonly ObservableCollection<FolderMatch> _treeRoots = new ObservableCollection<FolderMatch>();
        private readonly ObservableCollection<FolderMatch> _solutionRoots = new ObservableCollection<FolderMatch>();
        private CancellationTokenSource _searchCts;
        private string _pendingSearchQuery;
        private bool _solutionView;
        private int _selectedIconIndex;
        private ImageSource _selectedIconPreview;
        private GrepWindow _grepWindow;
        private VolumePathIndex _pathIndex;
        private SolutionCatalog _solutionCatalog;
        private readonly ObservableCollection<FileListItem> _files = new ObservableCollection<FileListItem>();
        private CancellationTokenSource _fileListCts;
        private CancellationTokenSource _filterCts;
        private IReadOnlyList<FolderMatch> _filteredViewItems;
        private readonly System.Windows.Threading.DispatcherTimer _filterTimer;
        private bool _largeFileIcons;

        public MainWindow()
        {
            string[] commandLine = Environment.GetCommandLineArgs();
            bool fastSearchRequested = commandLine.Any(argument => string.Equals(argument, "--fast-search", StringComparison.OrdinalIgnoreCase));
            bool runGitSearch = commandLine.Any(argument => string.Equals(argument, "--run-git-search", StringComparison.OrdinalIgnoreCase));
            bool runSearch = commandLine.Any(argument => string.Equals(argument, "--run-search", StringComparison.OrdinalIgnoreCase));
            bool darkMode = SettingsService.LoadDarkMode();
            ThemeService.Apply(darkMode);
            InitializeComponent();
            LightThemeButton.IsChecked = !darkMode;
            DarkThemeButton.IsChecked = darkMode;
            ResultsTree.ItemsSource = _treeRoots;
            FileList.ItemsSource = _files;
            FileIconList.ItemsSource = _files;
            _filterTimer = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromMilliseconds(250) };
            _filterTimer.Tick += (sender, args) => { _filterTimer.Stop(); ApplyFolderFilter(); };
            string savedRoot = SettingsService.LoadSearchRoot();
            RootBox.Text = !string.IsNullOrWhiteSpace(savedRoot) ? savedRoot : Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            string defaultLibrary = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Assets", "folder_set.icl");
            string savedLibrary = SettingsService.LoadIconLibraryPath();
            IconPathBox.Text = !string.IsNullOrWhiteSpace(savedLibrary) ? savedLibrary : defaultLibrary;
            string savedQuery = SettingsService.LoadSearchQuery();
            QueryBox.Text = string.Equals(savedQuery, ".git", StringComparison.OrdinalIgnoreCase) ? string.Empty : (savedQuery ?? string.Empty);
            // Reflect the actual process state as well as an elevation restart request.
            // Users who always run the executable as administrator can still uncheck it
            // to compare the standard search during the current session.
            FastNtfsSearchBox.IsChecked = fastSearchRequested || IsAdministrator();
            RestoreWindowPlacement(commandLine);
            HookPathBox(RootBox);
            HookPathBox(IconPathBox);
            Loaded += (sender, args) =>
            {
                RefreshSelectedIconPreview();
                ShowTextEnd(RootBox);
                ShowTextEnd(IconPathBox);
                Activate(); Topmost = true; Topmost = false; Focus();
                if (runGitSearch) Dispatcher.BeginInvoke(new Action(() => GitSearch_Click(this, new RoutedEventArgs())));
                else if (runSearch) Dispatcher.BeginInvoke(new Action(() => Search_Click(this, new RoutedEventArgs())));
            };
        }

        private void ChooseRoot_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                string selectedPath = Directory.Exists(RootBox.Text) ? RootBox.Text : Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
                string result = NativeFolderPicker.Show(new WindowInteropHelper(this).Handle, selectedPath, "Select a search folder");
                if (!string.IsNullOrEmpty(result)) { RootBox.Text = result; SettingsService.SaveSearchRoot(result); ShowTextEnd(RootBox); }
            }
            catch (Exception ex) { ShowError("Could not open the folder picker.", ex); }
        }

        private void ChooseIconLibrary_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                var dialog = new OpenFileDialog { Filter = "Icon resources|*.ico;*.icl;*.dll;*.exe|All files|*.*", CheckFileExists = true };
                string currentPath = IconPathBox.Text.Trim();
                if (File.Exists(currentPath)) { dialog.InitialDirectory = Path.GetDirectoryName(currentPath); dialog.FileName = Path.GetFileName(currentPath); }
                if (dialog.ShowDialog(this) != true) return;
                IconPathBox.Text = dialog.FileName;
                SettingsService.SaveIconLibraryPath(dialog.FileName);
                ShowTextEnd(IconPathBox);
                _selectedIconIndex = 0;
                RefreshSelectedIconPreview();
                StatusText.Text = Path.GetFileName(dialog.FileName) + " selected";
            }
            catch (Exception ex) { ShowError("Could not select the icon library.", ex); }
        }

        private void ChooseIcon_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                string iconPath = IconPathBox.Text.Trim();
                if (!File.Exists(iconPath)) { MessageBox.Show("Choose an icon library first.", Title); return; }
                StatusText.Text = "Loading icons…";
                var groups = IconResourceReader.Read(iconPath);
                if (groups.Count == 0) { MessageBox.Show("No icon groups were found.", Title); return; }
                var browser = new IconGroupBrowserWindow(iconPath, groups, _selectedIconIndex) { Owner = this };
                if (browser.ShowDialog() == true && browser.SelectedGroup != null)
                {
                    _selectedIconIndex = browser.SelectedGroup.ShellIndex;
                    SelectedIconImage.Source = browser.SelectedGroup.Preview;
                    _selectedIconPreview = browser.SelectedGroup.Preview;
                    SelectedIconText.Text = "Index " + browser.SelectedGroup.ShellIndex;
                    StatusText.Text = "Icon " + browser.SelectedGroup.ShellIndex + " selected";
                }
                else StatusText.Text = "Icon selection cancelled";
            }
            catch (Exception ex) { ShowError("Could not open the icon browser.", ex); }
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
                SelectedIconText.Text = group == null ? "Not selected" : "Index " + group.ShellIndex;
            }
            catch { SelectedIconImage.Source = null; _selectedIconPreview = null; SelectedIconText.Text = "Preview unavailable"; }
        }

        private async void Search_Click(object sender, RoutedEventArgs e)
        {
            // WPF controls must only be read from the UI thread. Capture every value
            // before Task.Run so the worker never touches a DispatcherObject.
            string root = RootBox.Text.Trim();
            string visibleQuery = QueryBox.Text.Trim();
            string query = _pendingSearchQuery ?? visibleQuery;
            bool folderListMode = string.IsNullOrWhiteSpace(query);
            bool gitSearchRequested = _pendingSearchQuery != null && string.Equals(query, ".git", StringComparison.OrdinalIgnoreCase);
            _pendingSearchQuery = null;
            bool fastSearch = FastNtfsSearchBox.IsChecked == true;
            SettingsService.SaveSearchRoot(root);
            SettingsService.SaveSearchQuery(visibleQuery);
            if (!Directory.Exists(root)) { MessageBox.Show("The search location does not exist.", Title); return; }
            if (fastSearch && !IsAdministrator())
            {
                RestartForFastSearch(gitSearchRequested);
                return;
            }
            _searchCts?.Cancel();
            var searchCts = new CancellationTokenSource();
            _searchCts = searchCts;
            _results.Clear(); _treeRoots.Clear(); _solutionRoots.Clear(); CountText.Text = "0 matches"; SetSearching(true);
            try
            {
                System.Collections.Generic.List<FolderMatch> solutionRoots = new List<FolderMatch>();
                if (fastSearch)
                {
                    FastSearchResult fastResult = null;
                    try
                    {
                        fastResult = await Task.Run(() => new FastFolderSearchService().Search(root, query,
                            count => Dispatcher.BeginInvoke(new Action(() => StatusText.Text = count == 0 ? "Reading the NTFS index…" : "Indexed " + count.ToString("N0") + " folders")), searchCts.Token));
                    }
                    catch (NotSupportedException)
                    {
                        StatusText.Text = "Fast NTFS search is unavailable here. Using standard search…";
                    }
                    catch (UnauthorizedAccessException)
                    {
                        StatusText.Text = "Fast NTFS search permission was unavailable. Using standard search…";
                    }
                    catch (Win32Exception)
                    {
                        StatusText.Text = "The drive index could not be read. Using standard search…";
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
                        await AddTreeResultsAsync(fastResult.Matches, searchCts.Token);
                        _pathIndex = fastResult.Paths;
                        _solutionView = false;
                        ResultsTree.ItemsSource = _treeRoots;
                        StatusText.Text = _results.Count + (folderListMode ? " folders found · analyzing projects…" : " matches found · analyzing projects…");
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
                                    await System.Windows.Threading.Dispatcher.Yield(System.Windows.Threading.DispatcherPriority.Background);
                            }
                        }
                        _solutionCatalog = await Task.Run(() => fastService.AnalyzeSolutions(fastResult.Index, fastResult.Paths, searchCts.Token));
                        solutionRoots = await Task.Run(() => fastService.BuildSolutionTree(fastResult.Index, _solutionCatalog.Maps, searchCts.Token));
                    }
                    else
                    {
                        StandardSearchResult standard = await RunStandardIndexedSearch(root, query, searchCts.Token);
                        await AddTreeResultsAsync(standard.Matches, searchCts.Token);
                        _pathIndex = standard.Paths;
                        ResultsTree.ItemsSource = _treeRoots;
                        await ApplyStandardDevelopmentAnalysis(gitSearchRequested, standard.Paths, searchCts.Token);
                        solutionRoots = await Task.Run(() => SolutionTreeService.Build(root, searchCts.Token));
                    }
                }
                else if (folderListMode)
                {
                    StandardSearchResult standard = await RunStandardIndexedSearch(root, string.Empty, searchCts.Token);
                    await AddTreeResultsAsync(standard.Matches, searchCts.Token);
                    _pathIndex = standard.Paths;
                    _solutionView = false;
                    ResultsTree.ItemsSource = _treeRoots;
                }
                else
                {
                    StandardSearchResult standard = await RunStandardIndexedSearch(root, query, searchCts.Token);
                    await AddTreeResultsAsync(standard.Matches, searchCts.Token);
                    _pathIndex = standard.Paths;
                    ResultsTree.ItemsSource = _treeRoots;
                    await ApplyStandardDevelopmentAnalysis(gitSearchRequested, standard.Paths, searchCts.Token);
                    solutionRoots = await Task.Run(() => SolutionTreeService.Build(root, searchCts.Token));
                }
                foreach (FolderMatch solution in solutionRoots)
                {
                    AssignParents(solution);
                    _solutionRoots.Add(solution);
                }
                if (_solutionView) ShowSolutionView();
                StatusText.Text = folderListMode ? _results.Count + " folders found" : _results.Count + " matches found";
            }
            catch (OperationCanceledException) { StatusText.Text = "Search cancelled"; }
            catch (Exception ex) { MessageBox.Show(ex.Message, Title); StatusText.Text = "Search failed"; }
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
                    count => Dispatcher.BeginInvoke(new Action(() => StatusText.Text = "Scanning " + count.ToString("N0") + " folders…")), token);
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
                        Dispatcher.BeginInvoke(new Action(() => StatusText.Text = "Indexed " + count.ToString("N0") + " folders…"), System.Windows.Threading.DispatcherPriority.Background);
                    }, token);
                token.ThrowIfCancellationRequested();
                Dispatcher.BeginInvoke(new Action(() => StatusText.Text = "Building folder tree…"));
                List<FolderMatch> matches = new FastFolderSearchService().Search(paths, query, token);
                foreach (FolderMatch item in matches) item.IconPreview = defaultFolderIcon;
                return new StandardSearchResult(paths, matches);
            }, token);
        }

        private async Task ApplyStandardDevelopmentAnalysis(bool enabled, VolumePathIndex paths, CancellationToken token)
        {
            if (!enabled) return;
            StatusText.Text = _results.Count + " folders found · analyzing projects…";
            await System.Windows.Threading.Dispatcher.Yield(System.Windows.Threading.DispatcherPriority.Background);
            Dictionary<string, string> analysis = await Task.Run(() => new FastFolderSearchService().AnalyzeDevelopment(paths, token), token);
            int updated = 0;
            foreach (FolderMatch item in _results)
            {
                token.ThrowIfCancellationRequested();
                string reason;
                if (analysis.TryGetValue(VolumePathIndex.Normalize(item.Path), out reason)) item.Reason = reason;
                if ((++updated % 60) == 0) await System.Windows.Threading.Dispatcher.Yield(System.Windows.Threading.DispatcherPriority.Background);
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
            for (int target = 0; target < ordered.Length; target++)
            {
                int current = items.IndexOf(ordered[target]);
                if (current != target) items.Move(current, target);
            }
        }

        private static bool IsAdministrator()
        {
            using (WindowsIdentity identity = WindowsIdentity.GetCurrent())
                return new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
        }

        private void RestartForFastSearch(bool gitSearchRequested)
        {
            string message = "Fast NTFS search reads the local drive index. Windows asks for administrator permission only because direct access to this index is protected.\n\nRestart DesktopIniManager with permission and continue the search?";
            if (MessageBox.Show(message, Title, MessageBoxButton.OKCancel, MessageBoxImage.Information) != MessageBoxResult.OK)
            {
                StatusText.Text = "Fast NTFS search was not started";
                return;
            }

            try
            {
                string executable = Assembly.GetExecutingAssembly().Location;
                Rect bounds = WindowState == WindowState.Normal
                    ? new Rect(Left, Top, ActualWidth, ActualHeight)
                    : RestoreBounds;
                string placement = string.Format(CultureInfo.InvariantCulture,
                    " --window-left {0:R} --window-top {1:R} --window-width {2:R} --window-height {3:R}{4}",
                    bounds.Left, bounds.Top, bounds.Width, bounds.Height,
                    WindowState == WindowState.Maximized ? " --window-maximized" : string.Empty);
                var startInfo = new ProcessStartInfo(executable)
                {
                    UseShellExecute = true,
                    Verb = "runas",
                    Arguments = "--fast-search " + (gitSearchRequested ? "--run-git-search" : "--run-search") + placement
                };
                Process.Start(startInfo);
                Application.Current.Shutdown();
            }
            catch (Win32Exception ex) when (ex.NativeErrorCode == 1223)
            {
                StatusText.Text = "Administrator permission was cancelled";
            }
            catch (Exception ex)
            {
                ShowError("Could not restart for fast NTFS search.", ex);
            }
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
            List<FolderMatch> visible = VisibleItems().Where(item => item.IsActionable).ToList();
            var visibleSet = new HashSet<FolderMatch>(visible);
            foreach (FolderMatch item in visible.Where(item => item.Parent == null || !visibleSet.Contains(item.Parent)))
                item.SetSelectedFromUi(!item.IsSelected);
        }

        private void FolderCheck_Click(object sender, RoutedEventArgs e)
        {
            var box = sender as System.Windows.Controls.CheckBox;
            var item = box?.DataContext as FolderMatch;
            if (item == null) return;
            item.SetSelectedFromUi(box.IsChecked == true);
        }
        private void ExpandAll_Click(object sender, RoutedEventArgs e) { foreach (var item in CurrentItems()) item.IsExpanded = true; }
        private void CollapseAll_Click(object sender, RoutedEventArgs e) { foreach (var item in CurrentItems()) item.IsExpanded = false; }
        private void PhysicalView_Click(object sender, RoutedEventArgs e) { _solutionView = false; ResultsTree.ItemsSource = _treeRoots; ApplyFolderFilter(); StatusText.Text = _results.Count + " folders found"; }
        private void SolutionView_Click(object sender, RoutedEventArgs e) { _solutionView = true; ShowSolutionView(); ApplyFolderFilter(); }
        private void ShowSolutionView() { ResultsTree.ItemsSource = _solutionRoots; UpdateVisibleCount(); StatusText.Text = _solutionRoots.Count + " solutions found"; }
        private void UpdateVisibleCount() { CountText.Text = CurrentItems().Count(item => !item.IsHidden && !item.IsFilterHidden) + (_solutionView ? " items" : " folders"); }
        private System.Collections.Generic.IEnumerable<FolderMatch> CurrentItems() => _filteredViewItems ?? Flatten(_solutionView ? _solutionRoots : _treeRoots).ToList();
        private System.Collections.Generic.IEnumerable<FolderMatch> VisibleItems() => _filteredViewItems != null
            ? _filteredViewItems.Where(item => !item.IsHidden) : FlattenVisible(_solutionView ? _solutionRoots : _treeRoots);
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
            CountText.Text = _results.Count + " matches";
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
            CountText.Text = _results.Count + " matches";
        }

        private async Task AddTreeResultsAsync(IEnumerable<FolderMatch> items, CancellationToken token)
        {
            List<FolderMatch> source = items.ToList();
            StatusText.Text = "Building " + source.Count.ToString("N0") + " folder rows…";
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
            ResultsTree.ItemsSource = null;
            _results.Clear();
            foreach (FolderMatch item in built.Items) _results.Add(item);
            _treeRoots.Clear();
            foreach (FolderMatch root in built.Roots) _treeRoots.Add(root);
            ResultsTree.ItemsSource = _treeRoots;
            CountText.Text = _results.Count + " folders";
            await System.Windows.Threading.Dispatcher.Yield(System.Windows.Threading.DispatcherPriority.Background);
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
            RootBox.Text = folder.Path;
            SettingsService.SaveSearchRoot(folder.Path);
            StatusText.Text = "Search location set to " + folder.Path;
        }

        private static FolderMatch ContextFolder(object sender)
        { return (sender as MenuItem)?.CommandParameter as FolderMatch; }

        private void OpenExplorer_Click(object sender, RoutedEventArgs e)
        {
            FolderMatch folder = ContextFolder(sender);
            if (folder == null || !Directory.Exists(folder.Path)) return;
            try { Process.Start(new ProcessStartInfo("explorer.exe", "\"" + folder.Path + "\"") { UseShellExecute = true }); }
            catch (Exception ex) { ShowError("Could not open the folder in Explorer.", ex); }
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
            _fileListCts?.Cancel();
            var fileListCts = new CancellationTokenSource();
            _fileListCts = fileListCts;
            _files.Clear();
            FolderMatch folder = e.NewValue as FolderMatch;
            FilePanelTitle.Text = folder == null ? "Files" : "Files — " + folder.Name;
            FilePanelTitle.ToolTip = folder?.Path;
            if (folder == null || string.IsNullOrEmpty(folder.Path)) { SetFilePanelBusy(false); return; }
            SetFilePanelBusy(true);
            VolumePathNode node = _pathIndex?.Find(folder.Path);
            string[] searchKeys = (QueryBox.Text ?? string.Empty).Split((char[])null, StringSplitOptions.RemoveEmptyEntries)
                .Select(key => key.Trim().TrimStart('*')).Where(key => key.Length > 0).Distinct(StringComparer.CurrentCultureIgnoreCase).ToArray();
            string[] paths;
            if (node != null) paths = node.Files.Select(file => file.Path).ToArray();
            else
            {
                try { paths = Directory.EnumerateFiles(folder.Path).OrderBy(path => path, StringComparer.CurrentCultureIgnoreCase).ToArray(); }
                catch { paths = Array.Empty<string>(); }
            }
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

        private void FileList_MouseDoubleClick(object sender, System.Windows.Input.MouseButtonEventArgs e)
        {
            FileListItem file = (sender as System.Windows.Controls.Primitives.Selector)?.SelectedItem as FileListItem;
            if (file == null) return;
            try
            {
                Process.Start(new ProcessStartInfo(file.Path) { UseShellExecute = true });
            }
            catch (Exception ex) { ShowError("Could not open the file.", ex); }
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
            List<FolderMatch> all = Flatten(_solutionView ? _solutionRoots : _treeRoots).ToList();
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
                    ? (IEnumerable<FolderMatch>)(_solutionView ? _solutionRoots : _treeRoots) : matches;
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
            var selected = VisibleItems().Where(item => item.IsActionable && item.IsSelected).GroupBy(item => item.Path, StringComparer.OrdinalIgnoreCase).Select(group => group.First()).ToList();
            bool addToGitIgnore = AddToGitIgnoreBox.IsChecked == true;
            if (selected.Count == 0) { MessageBox.Show("Select at least one folder.", Title); return; }
            if (MessageBox.Show("Apply the selected icon to " + selected.Count + " folders?", Title, MessageBoxButton.OKCancel, MessageBoxImage.Question) != MessageBoxResult.OK) return;
            int succeeded = 0;
            var errors = new System.Collections.Generic.List<string>();
            var service = new DesktopIniService();
            foreach (var item in selected)
                try { service.Apply(item.Path, IconPathBox.Text, _selectedIconIndex, addToGitIgnore); item.IconPreview = _selectedIconPreview ?? FolderIconService.GetFolderIcon(item.Path); succeeded++; }
                catch (Exception ex) { errors.Add(item.Path + ": " + ex.Message); }
            StatusText.Text = "Applied to " + succeeded + " folders";
            MessageBox.Show(errors.Count == 0 ? "Icon settings applied." : succeeded + " succeeded, " + errors.Count + " failed\n\n" + string.Join("\n", errors.Take(5)), Title);
        }

        private void Remove_Click(object sender, RoutedEventArgs e)
        {
            var selected = VisibleItems().Where(item => item.IsActionable && item.IsSelected).GroupBy(item => item.Path, StringComparer.OrdinalIgnoreCase).Select(group => group.First()).ToList();
            if (selected.Count == 0) { MessageBox.Show("Select at least one folder.", Title); return; }
            if (MessageBox.Show("Delete desktop.ini and remove icon settings from " + selected.Count + " folders?", Title, MessageBoxButton.OKCancel, MessageBoxImage.Warning) != MessageBoxResult.OK) return;
            int succeeded = 0;
            var errors = new System.Collections.Generic.List<string>();
            var service = new DesktopIniService();
            foreach (var item in selected)
                try { service.Remove(item.Path); item.IconPreview = FolderIconService.GetDefaultFolderIcon(); succeeded++; }
                catch (Exception ex) { errors.Add(item.Path + ": " + ex.Message); }
            StatusText.Text = "Removed settings from " + succeeded + " folders";
            MessageBox.Show(errors.Count == 0 ? "Icon settings removed." : succeeded + " succeeded, " + errors.Count + " failed\n\n" + string.Join("\n", errors.Take(5)), Title);
        }

        private void Grep_Click(object sender, RoutedEventArgs e)
        {
            IReadOnlyList<string> scopes = GetSelectedGrepScopes();
            if (scopes.Count == 0) { MessageBox.Show("Select at least one project folder.", Title); return; }
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
                _grepWindow.Activate();
            }
        }

        private IReadOnlyList<string> GetSelectedGrepScopes()
        {
            return CurrentItems().Where(item => item.IsActionable && item.IsSelected && Directory.Exists(item.Path))
                .Select(item => Path.GetFullPath(item.Path).TrimEnd(Path.DirectorySeparatorChar))
                .Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(path => path.Length)
                .Where(path => !CurrentItems().Where(item => item.IsActionable && item.IsSelected)
                    .Select(item => item.Path.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar)
                    .Any(parent => path.StartsWith(parent, StringComparison.OrdinalIgnoreCase)))
                .OrderBy(path => path, StringComparer.CurrentCultureIgnoreCase).ToList();
        }

        private void SetSearching(bool value)
        {
            GitSearchButton.IsEnabled = !value; SearchButton.IsEnabled = !value; CancelButton.IsEnabled = value;
            ApplyButton.IsEnabled = !value; RemoveButton.IsEnabled = !value; GrepButton.IsEnabled = !value;
            FastNtfsSearchBox.IsEnabled = !value; SearchProgress.Visibility = value ? Visibility.Visible : Visibility.Collapsed;
            if (value)
            {
                Dispatcher.BeginInvoke(new Action(() => AnimateSearchProgress(true)), System.Windows.Threading.DispatcherPriority.Loaded);
                return;
            }
            AnimateSearchProgress(false);
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
        private void LightTheme_Click(object sender, RoutedEventArgs e) => SetTheme(false);
        private void DarkTheme_Click(object sender, RoutedEventArgs e) => SetTheme(true);
        private void SetTheme(bool dark)
        {
            ThemeService.Apply(dark);
            LightThemeButton.IsChecked = !dark;
            DarkThemeButton.IsChecked = dark;
            SettingsService.SaveDarkMode(dark);
        }
        private void Close_Click(object sender, RoutedEventArgs e) => Close();
        private void ShowError(string message, Exception ex) { StatusText.Text = message; MessageBox.Show(message + "\n\n" + ex.Message, Title, MessageBoxButton.OK, MessageBoxImage.Error); }
        protected override void OnClosed(EventArgs e) { _searchCts?.Cancel(); _grepWindow?.Close(); SettingsService.SaveIconLibraryPath(IconPathBox.Text); SettingsService.SaveSearchQuery(QueryBox.Text); SettingsService.SaveSearchRoot(RootBox.Text); base.OnClosed(e); }

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
