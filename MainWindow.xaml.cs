using DesktopIniManager.Models;
using DesktopIniManager.Services;
using DesktopIniManager.Views;
using Microsoft.Win32;
using System;
using System.Collections.ObjectModel;
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
            Loaded += (sender, args) =>
            {
                RefreshSelectedIconPreview();
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
                if (!string.IsNullOrEmpty(result)) { RootBox.Text = result; SettingsService.SaveSearchRoot(result); }
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
            bool gitSearchRequested = _pendingSearchQuery != null && string.Equals(query, ".git", StringComparison.OrdinalIgnoreCase);
            _pendingSearchQuery = null;
            bool fastSearch = FastNtfsSearchBox.IsChecked == true;
            SettingsService.SaveSearchRoot(root);
            SettingsService.SaveSearchQuery(visibleQuery);
            if (!Directory.Exists(root)) { MessageBox.Show("The search location does not exist.", Title); return; }
            if (query.Length == 0) { MessageBox.Show("Enter at least one keyword.", Title); return; }
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
                System.Collections.Generic.List<FolderMatch> solutionRoots;
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
                        foreach (FolderMatch item in fastResult.Matches)
                        {
                            searchCts.Token.ThrowIfCancellationRequested();
                            item.IconPreview = FolderIconService.GetFolderIcon(item.Path);
                            AddTreeResult(item);
                        }
                        solutionRoots = await Task.Run(() => new FastFolderSearchService().BuildSolutionTree(fastResult.Index, root, searchCts.Token));
                    }
                    else
                    {
                        await RunStandardSearch(root, query, searchCts.Token);
                        solutionRoots = await Task.Run(() => SolutionTreeService.Build(root, searchCts.Token));
                    }
                }
                else
                {
                    await RunStandardSearch(root, query, searchCts.Token);
                    solutionRoots = await Task.Run(() => SolutionTreeService.Build(root, searchCts.Token));
                }
                SortPhysicalTree();
                foreach (FolderMatch solution in solutionRoots) _solutionRoots.Add(solution);
                if (_solutionView) ShowSolutionView();
                StatusText.Text = _results.Count + " matches found";
            }
            catch (OperationCanceledException) { StatusText.Text = "Search cancelled"; }
            catch (Exception ex) { MessageBox.Show(ex.Message, Title); StatusText.Text = "Search failed"; }
            finally
            {
                if (ReferenceEquals(_searchCts, searchCts)) { _searchCts = null; SetSearching(false); }
                searchCts.Dispose();
            }
        }

        private Task RunStandardSearch(string root, string query, CancellationToken token)
        {
            return Task.Run(() => new FolderSearchService().Search(root, query,
                item => { item.IconPreview = FolderIconService.GetFolderIcon(item.Path); Dispatcher.BeginInvoke(new Action(() => AddTreeResult(item))); },
                count => Dispatcher.BeginInvoke(new Action(() => StatusText.Text = "Scanning " + count.ToString("N0") + " folders…")), token));
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
            _pendingSearchQuery = string.IsNullOrWhiteSpace(QueryBox.Text) ? ".git" : QueryBox.Text.Trim();
            Search_Click(sender, e);
        }
        private void Cancel_Click(object sender, RoutedEventArgs e) => _searchCts?.Cancel();
        private void SelectAll_Click(object sender, RoutedEventArgs e)
        {
            bool value = SelectAllBox.IsChecked == true;
            var targets = value ? VisibleItems() : CurrentItems();
            foreach (var item in targets.Where(item => item.IsActionable)) item.IsSelected = value;
        }
        private void ExpandAll_Click(object sender, RoutedEventArgs e) { foreach (var item in CurrentItems()) item.IsExpanded = true; }
        private void CollapseAll_Click(object sender, RoutedEventArgs e) { foreach (var item in CurrentItems()) item.IsExpanded = false; }
        private void PhysicalView_Click(object sender, RoutedEventArgs e) { _solutionView = false; ResultsTree.ItemsSource = _treeRoots; UpdateVisibleCount(); StatusText.Text = _results.Count + " matches found"; }
        private void SolutionView_Click(object sender, RoutedEventArgs e) { _solutionView = true; ShowSolutionView(); }
        private void ShowSolutionView() { ResultsTree.ItemsSource = _solutionRoots; UpdateVisibleCount(); StatusText.Text = _solutionRoots.Count + " solutions found"; }
        private void UpdateVisibleCount() { CountText.Text = CurrentItems().Count() + (_solutionView ? " items" : " matches"); }
        private System.Collections.Generic.IEnumerable<FolderMatch> CurrentItems() => Flatten(_solutionView ? _solutionRoots : _treeRoots);
        private System.Collections.Generic.IEnumerable<FolderMatch> VisibleItems() => FlattenVisible(_solutionView ? _solutionRoots : _treeRoots);
        private static System.Collections.Generic.IEnumerable<FolderMatch> Flatten(System.Collections.Generic.IEnumerable<FolderMatch> roots)
        {
            foreach (FolderMatch item in roots) { yield return item; foreach (FolderMatch child in Flatten(item.Children)) yield return child; }
        }
        private static System.Collections.Generic.IEnumerable<FolderMatch> FlattenVisible(System.Collections.Generic.IEnumerable<FolderMatch> roots)
        {
            foreach (FolderMatch item in roots)
            {
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
            if (parent == null) _treeRoots.Add(item); else parent.Children.Add(item);

            // Re-parent roots that arrived before their newly discovered ancestor.
            foreach (FolderMatch root in _treeRoots.Where(candidate => !ReferenceEquals(candidate, item) && IsAncestorPath(item.Path, candidate.Path)).ToList())
            {
                _treeRoots.Remove(root);
                item.Children.Add(root);
            }
            CountText.Text = _results.Count + " matches";
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

        private void SetSearching(bool value) { GitSearchButton.IsEnabled = !value; SearchButton.IsEnabled = !value; CancelButton.IsEnabled = value; ApplyButton.IsEnabled = !value; RemoveButton.IsEnabled = !value; FastNtfsSearchBox.IsEnabled = !value; SearchProgress.Visibility = value ? Visibility.Visible : Visibility.Collapsed; }
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
        protected override void OnClosed(EventArgs e) { _searchCts?.Cancel(); SettingsService.SaveIconLibraryPath(IconPathBox.Text); SettingsService.SaveSearchQuery(QueryBox.Text); SettingsService.SaveSearchRoot(RootBox.Text); base.OnClosed(e); }

    }
}
