using DesktopIniManager.ViewModels;
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
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Media.Animation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Threading;
using FastVolumeIndex;
using DesktopIniManager.Properties;
using System.Runtime.Versioning;

namespace DesktopIniManager.Views
{
    [SupportedOSPlatform("windows")]
    internal sealed class MainWindowPresentationService
    {

        // --- MainWindowPresentationService.cs ---

        private readonly MainWindow _window;
        private Dispatcher Dispatcher => _window.Dispatcher;
        private string Title { get => _window.Title; set => _window.Title = value; }
        private object FindResource(object key) => _window.FindResource(key);
        private ToggleButton LightThemeButton => _window.LightThemeButton;
        private ToggleButton DarkThemeButton => _window.DarkThemeButton;
        private HistoryTextBox RootBox => _window.RootBox;
        private HistoryTextBox QueryBox => _window.QueryBox;
        private HistoryTextBox IconPathBox => _window.IconPathBox;
        private Button CompactTreeButton => _window.CompactTreeButton;
        private Button ComfortableTreeButton => _window.ComfortableTreeButton;
        private CheckBox AddToGitIgnoreBox => _window.AddToGitIgnoreBox;
        private HistoryTextBox FolderFilterBox => _window.FolderFilterBox;
        private TreeView ResultsTree => _window.ResultsTree;
        private Border TreePanelBusy => _window.TreePanelBusy;
        private Button FileListViewButton => _window.FileListViewButton;
        private Button FileIconLargeButton => _window.FileIconLargeButton;
        private Button FileIconSmallButton => _window.FileIconSmallButton;
        private ListView FileList => _window.FileList;
        private ListBox FileIconList => _window.FileIconList;
        private Border FilePanelBusy => _window.FilePanelBusy;
        private ProgressBar SearchProgress => _window.SearchProgress;
        private Button ResetButton => _window.ResetButton;
        private TextBlock ResetButtonLabel => _window.ResetButtonLabel;
        private ComboBox LanguageBox => _window.LanguageBox;

        private void ConnectView()
        {
            _window.FolderFilterBox.TextChanged += FolderFilter_TextChanged;
            _window.ResultsTree.SelectedItemChanged += ResultsTree_SelectedItemChanged;
            _window.FileList.SelectionChanged += FileList_SelectionChanged;
            _window.FileList.MouseDoubleClick += FileList_MouseDoubleClick;
            _window.FileIconList.SelectionChanged += FileList_SelectionChanged;
            _window.FileIconList.MouseDoubleClick += FileList_MouseDoubleClick;
            _window.LanguageBox.SelectionChanged += LanguageBox_SelectionChanged;
            _window.Closing += OnClosing;
            _window.Closed += OnClosed;
            ViewModel.InteractionRequested += HandleInteraction;
            _window.AllowDrop = true;
            _window.PreviewDragEnter += FolderDropPreview;
            _window.PreviewDragOver += FolderDropPreview;
            _window.PreviewDrop += MainWindow_Drop;
        }

        private static void FolderDropPreview(object sender, DragEventArgs e)
        {
            if (e.Data == null || !e.Data.GetDataPresent(DataFormats.FileDrop))
                return;

            e.Effects = DragDropEffects.Copy;
            e.Handled = true;
        }

        private static List<string> FoldersFromDrop(IDataObject data)
        {
            var folders = new List<string>();
            if (data == null || !data.GetDataPresent(DataFormats.FileDrop))
                return folders;

            var paths = data.GetData(DataFormats.FileDrop) as string[] ?? Array.Empty<string>();
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (string path in paths)
            {
                if (string.IsNullOrWhiteSpace(path)) continue;
                string folder = Directory.Exists(path)
                    ? path
                    : File.Exists(path) ? Path.GetDirectoryName(path) : null;
                if (string.IsNullOrEmpty(folder) || !Directory.Exists(folder)) continue;
                string normalized = Path.GetFullPath(folder)
                    .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
                if (seen.Add(normalized))
                    folders.Add(normalized);
            }

            return folders;
        }

        private void MainWindow_Drop(object sender, DragEventArgs e)
        {
            List<string> folders = FoldersFromDrop(e.Data);
            if (folders.Count == 0) return;

            ViewModel.RootPath = folders[0];
            RootBox.CommitHistory();
            SettingsService.SaveSearchRoot(folders[0]);
            ShowTextEnd(RootBox);
            ViewModel.SelectDroppedFolders(folders);
            e.Handled = true;
        }
        private void HandleInteraction(MainWindowAction action, object parameter)
        {
            try
            {
                switch (action)
                {
                case MainWindowAction.ChooseRoot: ChooseRoot(); break;
                case MainWindowAction.ChooseIconLibrary: ChooseIconLibrary(); break;
                case MainWindowAction.ChooseIcon: ChooseIcon(); break;
                case MainWindowAction.UseAsSearchLocation: UseAsSearchLocation(parameter as FolderMatch); break;
                case MainWindowAction.OpenExplorer: OpenExplorer(parameter as FolderMatch); break;
                case MainWindowAction.GrepFolder: GrepFolder(parameter as FolderMatch); break;
                case MainWindowAction.CompactTree: CompactTree(); break;
                case MainWindowAction.ComfortableTree: ComfortableTree(); break;
                case MainWindowAction.FileListView: FileListView(); break;
                case MainWindowAction.FileIconSmall: FileIconSmall(); break;
                case MainWindowAction.FileIconLarge: FileIconLarge(); break;
                case MainWindowAction.ClearFolderFilter: ClearFolderFilter(); break;
                case MainWindowAction.DeveloperDifferencer: DeveloperDifferencer(); break;
                case MainWindowAction.LightTheme: LightTheme(); break;
                case MainWindowAction.DarkTheme: DarkTheme(); break;
                case MainWindowAction.Reset: Reset(); break;
                case MainWindowAction.Close: Close(); break;
                }
            }
            catch (Exception error) { ViewModel.ShowError(Strings.App_Unhandled, error); }
        }

        internal MainWindowViewModel ViewModel { get; }
        private GrepWindow _grepWindow;
        private readonly System.Windows.Threading.DispatcherTimer _filterTimer;
        private bool _largeFileIcons;
        private bool _syncingTreeFromFile;

        internal MainWindowPresentationService(MainWindow window, StartupState startup)
        {
            _window = window;
            ViewModel = new MainWindowViewModel(startup, Dispatcher, new UserDialogService(window));
            string[] commandLine = Environment.GetCommandLineArgs();
            bool runGitSearch = commandLine.Any(argument => string.Equals(argument, "--run-git-search", StringComparison.OrdinalIgnoreCase));
            bool runSearch = commandLine.Any(argument => string.Equals(argument, "--run-search", StringComparison.OrdinalIgnoreCase));
            bool darkMode = startup != null ? startup.DarkMode : SettingsService.LoadDarkMode();
            _window.DataContext = ViewModel;
            ConnectView();
            ViewModel.SearchHistoryRequested += () => { RootBox.CommitHistory(); QueryBox.CommitHistory(); IconPathBox.CommitHistory(); };
            ViewModel.SearchRootSelectionRequested += SelectSearchRootForFileList;
            ViewModel.FileScrollRequested += item => { FileList.ScrollIntoView(item); FileIconList.ScrollIntoView(item); };
            ViewModel.OpenGrepRequested += OpenGrep;
            ViewModel.PropertyChanged += (sender, args) => { if (args.PropertyName == nameof(ViewModel.IsSearching)) SetSearching(ViewModel.IsSearching); };
            LightThemeButton.IsChecked = !darkMode;
            DarkThemeButton.IsChecked = darkMode;
            InitLanguageBox();
            _filterTimer = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromMilliseconds(250) };
            _filterTimer.Tick += (sender, args) => { _filterTimer.Stop(); _ = ViewModel.ApplyFolderFilterAsync(); };
            string savedRoot = startup != null ? startup.Root : SettingsService.LoadSearchRoot();
            ViewModel.RootPath = savedRoot ?? string.Empty;
            string defaultLibrary = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Assets", "folder_set.icl");
            string savedLibrary = startup != null ? startup.IconLibrary : SettingsService.LoadIconLibraryPath();
            ViewModel.IconLibraryPath = !string.IsNullOrWhiteSpace(savedLibrary) ? savedLibrary : defaultLibrary;
            string savedQuery = startup != null ? startup.Query : SettingsService.LoadSearchQuery();
            ViewModel.Query = string.Equals(savedQuery, ".git", StringComparison.OrdinalIgnoreCase) ? string.Empty : (savedQuery ?? string.Empty);
            RestoreWindowPlacement(commandLine);
            HookPathBox(RootBox);
            HookPathBox(IconPathBox);
            ApplyTreeDensity(startup != null ? startup.TreeCompact : SettingsService.LoadTreeCompact(), false);
            ViewModel.RestoreFolderTrees();
            if (startup != null) { startup.Tree = null; startup.Physical = null; startup.Solution = null; }
            _window.Loaded += (sender, args) =>
            {
                if (startup == null) RefreshSelectedIconPreview();
                else
                {
                    var icon = startup.SelectedIcon;
                    ViewModel.RestoreStartupIcon(icon?.Preview, icon?.ShellIndex ?? 0, icon != null);
                }
                ShowTextEnd(RootBox);
                ShowTextEnd(IconPathBox);
                WindowActivationService.BringToFront(_window);
                if (runGitSearch) Dispatcher.BeginInvoke(new Action(() => ViewModel.GitSearchCommand.Execute(null)));
                else if (runSearch) Dispatcher.BeginInvoke(new Action(() => ViewModel.SearchCommand.Execute(null)));
            };
        }

        private void ChooseRoot()
        {
            try
            {
                string selectedPath = Directory.Exists(ViewModel.RootPath) ? ViewModel.RootPath : Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
                string result = NativeFolderPicker.Show(new WindowInteropHelper(_window).Handle, selectedPath, Strings.Main_SelectSearchFolder);
                if (!string.IsNullOrEmpty(result)) { ViewModel.RootPath = result; RootBox.CommitHistory(); SettingsService.SaveSearchRoot(result); ShowTextEnd(RootBox); }
            }
            catch (Exception ex) { ViewModel.ShowError(Strings.Main_FolderPickerFailed, ex); }
        }

        private void ChooseIconLibrary()
        {
            try
            {
                var dialog = new OpenFileDialog { Filter = Strings.Main_IconFilter, CheckFileExists = true };
                string currentPath = ViewModel.IconLibraryPath.Trim();
                if (File.Exists(currentPath)) { dialog.InitialDirectory = Path.GetDirectoryName(currentPath); dialog.FileName = Path.GetFileName(currentPath); }
                if (dialog.ShowDialog(_window) != true) return;
                ViewModel.IconLibraryPath = dialog.FileName; IconPathBox.CommitHistory();
                SettingsService.SaveIconLibraryPath(dialog.FileName);
                ShowTextEnd(IconPathBox);
                ViewModel.ClearSelectedIcon();
                RefreshSelectedIconPreview();
                ViewModel.Status = Path.GetFileName(dialog.FileName) + Strings.Main_SelectedSuffix;
            }
            catch (Exception ex) { ViewModel.ShowError(Strings.Main_LibrarySelectFailed, ex); }
        }

        private void ChooseIcon()
        {
            try
            {
                string iconPath = ViewModel.IconLibraryPath.Trim();
                if (!File.Exists(iconPath)) { MessageBox.Show(Strings.Main_ChooseLibraryFirst, Title); return; }
                ViewModel.Status = Strings.Main_LoadingIcons;
                var groups = IconResourceReader.Read(iconPath);
                if (groups.Count == 0) { MessageBox.Show(Strings.Main_NoIconGroups, Title); return; }
                var browser = new IconGroupBrowserWindow(iconPath, groups, ViewModel.SelectedIconIndex) { Owner = _window };
                if (browser.ShowDialog() == true && browser.SelectedGroup != null)
                {
                    ViewModel.ApplySelectedIcon(browser.SelectedGroup.ShellIndex, browser.SelectedGroup.Preview);
                    ViewModel.Status = string.Format(Strings.Main_IconNSelected, browser.SelectedGroup.ShellIndex);
                }
                else ViewModel.Status = Strings.Main_IconCancelled;
            }
            catch (Exception ex) { ViewModel.ShowError(Strings.Main_IconBrowserFailed, ex); }
        }

        private void RefreshSelectedIconPreview()
        {
            try
            {
                string iconPath = ViewModel.IconLibraryPath.Trim();
                if (!File.Exists(iconPath)) return;
                var group = IconResourceReader.Read(iconPath).FirstOrDefault(item => item.ShellIndex == ViewModel.SelectedIconIndex);
                if (group == null) ViewModel.ClearSelectedIcon();
                else ViewModel.ApplySelectedIcon(group.ShellIndex, group.Preview);
            }
            catch { ViewModel.ClearSelectedIcon(); ViewModel.SelectedIconLabel = Strings.Main_PreviewUnavailable; }
        }

        private void UseAsSearchLocation(FolderMatch folder)
        {
            if (folder == null) return;
            ViewModel.RootPath = folder.Path; RootBox.CommitHistory();
            SettingsService.SaveSearchRoot(folder.Path);
            ViewModel.Status = string.Format(Strings.Main_LocationSet, folder.Path);
        }

        private void OpenExplorer(FolderMatch folder)
        {
            if (folder == null || !Directory.Exists(folder.Path)) return;
            try { Process.Start(new ProcessStartInfo("explorer.exe", "\"" + folder.Path + "\"") { UseShellExecute = true }); }
            catch (Exception ex) { ViewModel.ShowError(Strings.Main_ExplorerFailed, ex); }
        }

        private void GrepFolder(FolderMatch folder)
        {
            if (folder == null || !Directory.Exists(folder.Path)) return;
            foreach (FolderMatch item in ViewModel.CurrentItems()) item.IsSelected = ReferenceEquals(item, folder);
            OpenGrep(new[] { folder.Path });
        }

        private void FileList_MouseDoubleClick(object sender, System.Windows.Input.MouseButtonEventArgs e)
        {
            FileListItem file = (sender as System.Windows.Controls.Primitives.Selector)?.SelectedItem as FileListItem;
            if (file == null) return;
            try
            {
                Process.Start(new ProcessStartInfo(file.Path) { UseShellExecute = true });
            }
            catch (Exception ex) { ViewModel.ShowError(Strings.Main_OpenFileFailed, ex); }
        }

        private DeveloperDifferencerWindow _differencerWindow;

        /// <summary>Opens or activates the developer differencer window.</summary>

        private void OnClosing(object sender, System.ComponentModel.CancelEventArgs e)
        {
            if (_differencerWindow != null)
            {
                if (_differencerWindow.IsWorking) e.Cancel = true;
                else _differencerWindow.SaveState();
            }
            if (!e.Cancel) ViewModel.SaveFolderTrees();
        }

        private void Close() => _window.Close();

        private void OnClosed(object sender, EventArgs e)
        {
            _filterTimer.Stop();
            ViewModel.CancelOperations();
            try { _grepWindow?.Close(); } catch { }
            try { _differencerWindow?.Close(); } catch { }
            SettingsService.SaveIconLibraryPath(ViewModel.IconLibraryPath);
            SettingsService.SaveSearchQuery(ViewModel.Query);
            SettingsService.SaveSearchRoot(ViewModel.RootPath);
            SaveMainWindowPlacement();
        }

        private bool _languageReady;

        private void InitLanguageBox()
        {
            ImageSource[] flags = LanguageIconService.LoadFlagIcons();
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

        private void LanguageBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (!_languageReady) return;
            var choice = LanguageBox.SelectedItem as LanguageChoice;
            if (choice == null) return;
            StringOverlay.SetCulture(choice.Code);
            Title = Strings.App_Title;
            ScheduleResetCaption();
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

        private void Reset()
        {
            string language = StringOverlay.ResolveCulture().TwoLetterISOLanguageName;
            string title = language == "ja" ? "リセット" : language == "zh" ? "重置" : language == "ko" ? "초기화" : "Reset";
            string message = language == "ja"
                ? "設定、入力履歴、フォルダー一覧、GREP と差分比較の状態、エディターとアイコンの選択を初期化します。よろしいですか。"
                : language == "zh"
                ? "将清除设置、输入历史、文件夹列表、GREP 和差异比较器状态以及编辑器和图标选择。确定吗？"
                : language == "ko"
                ? "설정, 입력 기록, 폴더 목록, GREP 및 차이 비교 상태, 편집기와 아이콘 선택을 초기화합니다. 계속할까요?"
                : "This clears settings, input history, folder lists, GREP and differencer state, and editor and icon selections. Continue?";
            if (MessageBox.Show(_window, message, title, MessageBoxButton.OKCancel, MessageBoxImage.Warning) != MessageBoxResult.OK)
                return;

            ViewModel.CancelOperations();
            _grepWindow?.Close();
            _differencerWindow?.Close();
            foreach (Window window in Application.Current.Windows.Cast<Window>().ToArray())
            {
                if (!ReferenceEquals(window, _window))
                    try { window.Close(); } catch { }
            }

            ViewModel.ResetSettings();
            RootBox.ResetField(string.Empty);
            IconPathBox.ResetField(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Assets", "folder_set.icl"));
            QueryBox.ResetField(string.Empty);
            FolderFilterBox.ResetField(string.Empty);
            SettingsService.SaveSearchQuery(string.Empty);
            SettingsService.SaveIconLibraryPath(ViewModel.IconLibraryPath);
            if (AddToGitIgnoreBox != null) ViewModel.AddToGitIgnore = false;
            ApplyTreeDensity(false, false);
            ThemeService.Apply(false);
            LightThemeButton.IsChecked = true;
            DarkThemeButton.IsChecked = false;
            ViewModel.ShowTreeView(0);
            ViewModel.CountLabel = string.Format(Strings.Main_NFolders, 0);
            ViewModel.Status = Strings.Common_Ready;
            _languageReady = false;
            StringOverlay.SetCulture("en");
            InitLanguageBox();
            Title = Strings.App_Title;
            RefreshSelectedIconPreview();
            ScheduleResetCaption();
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

        // --- MainWindowPresentationService.Appearance.cs ---

        private void CompactTree() => ApplyTreeDensity(true, true);

        private void ComfortableTree() => ApplyTreeDensity(false, true);

        private void ApplyTreeDensity(bool compact, bool announce)
        {
            ViewModel.TreeCompact = compact;
            HighlightTreeDensityButtons();
            SettingsService.SaveTreeCompact(compact);
            if (announce) ViewModel.Status = compact ? Strings.Main_TreeCompact : Strings.Main_TreeComfortable;
        }

        private void HighlightTreeDensityButtons()
        {
            System.Windows.Media.Brush selected = (System.Windows.Media.Brush)FindResource("ThemeSelected");
            System.Windows.Media.Brush secondary = (System.Windows.Media.Brush)FindResource("Secondary");
            if (CompactTreeButton != null) CompactTreeButton.Background = ViewModel.TreeCompact ? selected : secondary;
            if (ComfortableTreeButton != null) ComfortableTreeButton.Background = ViewModel.TreeCompact ? secondary : selected;
        }

        private void FileListView()
        {
            FileList.Visibility = Visibility.Visible;
            FileIconList.Visibility = Visibility.Collapsed;
            HighlightFileViewButtons(list: true, large: false);
        }

        private void FileIconSmall()
        {
            ApplyFileIconSize(false);
        }

        private void FileIconLarge()
        {
            ApplyFileIconSize(true);
        }

        private async void ApplyFileIconSize(bool large)
        {
            _largeFileIcons = large;
            ViewModel.IsFileBusy = true;
            await Dispatcher.Yield(DispatcherPriority.Render);
            FileList.Visibility = Visibility.Collapsed;
            FileIconList.Visibility = Visibility.Visible;
            FileIconList.ItemsPanel = (ItemsPanelTemplate)FindResource(large ? "FileIconLargePanel" : "FileIconSmallPanel");
            FileIconList.ItemTemplate = (DataTemplate)FindResource(large ? "FileIconLargeTemplate" : "FileIconSmallTemplate");
            HighlightFileViewButtons(list: false, large: large);
            FileIconList.UpdateLayout();
            await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
            ViewModel.IsFileBusy = false;
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
            if (ViewModel.Files.Count < 40) return;
            SetFilePanelBusy(true);
            Dispatcher.BeginInvoke(new Action(() => SetFilePanelBusy(false)), System.Windows.Threading.DispatcherPriority.ContextIdle);
        }

        private void SetFilePanelBusy(bool busy)
        {
            if (FilePanelBusy != null)
                ViewModel.IsFileBusy = busy;
        }

        private void FolderFilter_TextChanged(object sender, TextChangedEventArgs e)
        { _filterTimer?.Stop(); _filterTimer?.Start(); }

        private void ClearFolderFilter()
        { ViewModel.FolderFilter = string.Empty; }

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

        private void SetSearching(bool value)
        {
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

        private void SetTreePanelBusy(bool busy)
        {
            if (TreePanelBusy != null)
                ViewModel.IsTreeBusy = busy;
        }

        private void LightTheme() => SetTheme(false);

        private void DarkTheme() => SetTheme(true);

        private void SetTheme(bool dark)
        {
            ThemeService.Apply(dark);
            LightThemeButton.IsChecked = !dark;
            DarkThemeButton.IsChecked = dark;
            SettingsService.SaveDarkMode(dark);
            HighlightTreeDensityButtons();
            HighlightFileViewButtons(FileList.Visibility == Visibility.Visible, _largeFileIcons);
        }

        // --- MainWindowPresentationService.Tree.cs ---

        private void SelectSearchRootForFileList()
        {
            if (ViewModel.SearchTabRoot == null) return;
            FolderMatch root = ViewModel.SearchTabRoot;
            root.IsExpanded = true;
            Dispatcher.BeginInvoke(new Action(() =>
            {
                if (ViewModel.TreeViewIndex != 2 || ViewModel.SearchTabRoot == null) return;
                FolderMatch current = ViewModel.SearchTabRoot;
                current.IsExpanded = true;
                current.IsCurrent = true;
            }), System.Windows.Threading.DispatcherPriority.Loaded);
        }

        private async void ResultsTree_SelectedItemChanged(object sender, RoutedPropertyChangedEventArgs<object> e)
        {
            if (_syncingTreeFromFile) return;
            try { await ViewModel.LoadFilesAsync(e.NewValue as FolderMatch); }
            catch (Exception ex) { ViewModel.ShowError(Strings.App_Unhandled, ex); }
        }

        private void FileList_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (_syncingTreeFromFile) return;
            FileListItem file = (sender as System.Windows.Controls.Primitives.Selector)?.SelectedItem as FileListItem;
            if (file == null) return;
            RevealFolderInCurrentTree(Path.GetDirectoryName(file.Path));
        }

        private void RevealFolderInCurrentTree(string directory)
        {
            ObservableCollection<FolderMatch> roots = ViewModel.CurrentTreeRoots();
            if (string.IsNullOrEmpty(directory) || roots.Count == 0) return;
            FolderMatch target = ViewModel.FindFolderInRoots(roots, directory);
            if (target == null) return;
            for (FolderMatch ancestor = target.Parent; ancestor != null; ancestor = ancestor.Parent)
                ancestor.IsExpanded = true;
            target.IsExpanded = true;
            _syncingTreeFromFile = true;
            foreach (FolderMatch item in MainWindowViewModel.Flatten(roots))
                if (item.IsCurrent && item != target) item.IsCurrent = false;
            target.IsCurrent = true;
            ViewModel.RememberCurrentFolder(target);
            ResultsTree.UpdateLayout();
            ScheduleFolderIntoView(target);
        }

        private void ScheduleFolderIntoView(FolderMatch target)
        {
            if (target == null)
            {
                _syncingTreeFromFile = false;
                return;
            }

            var path = new List<FolderMatch>();
            for (FolderMatch node = target; node != null; node = node.Parent)
            {
                node.IsExpanded = true;
                path.Add(node);
            }
            path.Reverse();

            Dispatcher.BeginInvoke(
                new Action(() => ExpandPathStep(ResultsTree, path, 0, 0)),
                System.Windows.Threading.DispatcherPriority.Loaded);
        }

        private void ExpandPathStep(ItemsControl host, List<FolderMatch> path, int level, int retry)
        {
            if (host == null || level >= path.Count)
            {
                _syncingTreeFromFile = false;
                return;
            }

            FolderMatch node = path[level];
            host.ApplyTemplate();
            host.UpdateLayout();

            TreeViewItem container = host.ItemContainerGenerator.ContainerFromItem(node) as TreeViewItem;
            if (container == null)
            {
                int index = host.Items.IndexOf(node);
                if (index < 0 || retry >= 48)
                {
                    _syncingTreeFromFile = false;
                    return;
                }

                BringSiblingIndexIntoView(host, index);
                Dispatcher.BeginInvoke(
                    new Action(() => ExpandPathStep(host, path, level, retry + 1)),
                    System.Windows.Threading.DispatcherPriority.Loaded);
                return;
            }

            container.IsExpanded = true;
            container.UpdateLayout();

            if (level == path.Count - 1)
            {
                container.IsSelected = true;
                container.BringIntoView();
                ScrollTreeItemIntoView(ResultsTree, container);
                _syncingTreeFromFile = false;
                return;
            }

            container.BringIntoView();
            Dispatcher.BeginInvoke(
                new Action(() => ExpandPathStep(container, path, level + 1, 0)),
                System.Windows.Threading.DispatcherPriority.Loaded);
        }

        private void BringSiblingIndexIntoView(ItemsControl host, int index)
        {
            if (index < 0) return;

            ItemsPresenter presenter = FindVisualChild<ItemsPresenter>(host);
            VirtualizingPanel panel = presenter != null ? FindVisualChild<VirtualizingPanel>(presenter) : null;
            if (panel != null)
            {
                System.Reflection.MethodInfo method = typeof(VirtualizingPanel).GetMethod(
                    "BringIndexIntoView",
                    System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
                if (method != null)
                {
                    method.Invoke(panel, new object[] { index });
                    return;
                }
            }

            ScrollViewer viewer = FindScrollViewer(ResultsTree);
            if (viewer != null)
                viewer.ScrollToVerticalOffset(Math.Max(0, index - 2));
        }

        private static T FindVisualChild<T>(DependencyObject root) where T : DependencyObject
        {
            if (root == null) return null;
            for (int i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
            {
                DependencyObject child = VisualTreeHelper.GetChild(root, i);
                T match = child as T ?? FindVisualChild<T>(child);
                if (match != null) return match;
            }
            return null;
        }

        private static bool TryGetFlatIndex(System.Collections.Generic.IEnumerable<FolderMatch> nodes, FolderMatch target, ref int index)
        {
            foreach (FolderMatch node in nodes)
            {
                if (node.IsHidden || node.IsFilterHidden)
                    continue;
                if (ReferenceEquals(node, target))
                    return true;
                index++;
                if (node.IsExpanded && node.Children.Count > 0
                    && TryGetFlatIndex(node.Children, target, ref index))
                    return true;
            }
            return false;
        }

        private static TreeViewItem BringFolderIntoView(TreeView tree, FolderMatch target)
        {
            if (tree == null || target == null) return null;
            var path = new List<FolderMatch>();
            for (FolderMatch node = target; node != null; node = node.Parent)
            {
                node.IsExpanded = true;
                path.Add(node);
            }
            path.Reverse();
            tree.UpdateLayout();
            return ContainerAlongPath(tree, path);
        }

        private static TreeViewItem ContainerAlongPath(ItemsControl parent, List<FolderMatch> path)
        {
            TreeViewItem current = null;
            ItemsControl host = parent;
            foreach (FolderMatch node in path)
            {
                if (host == null) return null;
                host.ApplyTemplate();
                host.UpdateLayout();
                var generator = host.ItemContainerGenerator;
                if (generator.Status != System.Windows.Controls.Primitives.GeneratorStatus.ContainersGenerated)
                    return null;

                var item = generator.ContainerFromItem(node) as TreeViewItem;
                if (item == null)
                {
                    int index = host.Items.IndexOf(node);
                    if (index >= 0)
                        item = generator.ContainerFromIndex(index) as TreeViewItem;
                }
                if (item == null)
                {
                    // Bring the parent into view and retry when virtualization has not created the item yet.
                    if (current != null) current.BringIntoView();
                    return null;
                }

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
            item.IsSelected = true;
            item.BringIntoView();
            ScrollViewer viewer = FindScrollViewer(tree);
            if (viewer == null) return;
            try
            {
                Point pos = item.TransformToAncestor(viewer).Transform(new Point(0, 0));
                double top = pos.Y;
                double bottom = top + Math.Max(item.ActualHeight, 1);
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

        // --- MainWindowPresentationService.Windows.cs ---

        private void RestoreWindowPlacement(string[] arguments)
        {
            double left = 0, top = 0, width = 0, height = 0;
            int state = 0;
            bool fromCommandLine = TryReadArgument(arguments, "--window-left", out left)
                && TryReadArgument(arguments, "--window-top", out top)
                && TryReadArgument(arguments, "--window-width", out width)
                && TryReadArgument(arguments, "--window-height", out height);
            if (fromCommandLine)
            {
                if (arguments.Any(argument => string.Equals(argument, "--window-maximized", StringComparison.OrdinalIgnoreCase)))
                    state = (int)WindowState.Maximized;
            }
            else if (!SettingsService.TryLoadMainWindowPlacement(out left, out top, out width, out height, out state))
                return;

            ApplyPlacement(_window, left, top, width, height, state);
        }

        private void SaveMainWindowPlacement()
        {
            Rect bounds = _window.WindowState == WindowState.Normal
                ? new Rect(_window.Left, _window.Top, _window.Width, _window.Height)
                : _window.RestoreBounds;
            int state = _window.WindowState == WindowState.Minimized
                ? (int)WindowState.Normal
                : (int)_window.WindowState;
            SettingsService.SaveMainWindowPlacement(bounds.Left, bounds.Top, bounds.Width, bounds.Height, state);
        }

        private void OverlayOnMain(Window child)
        {
            if (child == null) return;
            Rect bounds = _window.WindowState == WindowState.Normal
                ? new Rect(_window.Left, _window.Top, _window.Width, _window.Height)
                : _window.RestoreBounds;
            ApplyPlacement(child, bounds.Left, bounds.Top, bounds.Width, bounds.Height,
                _window.WindowState == WindowState.Minimized ? (int)WindowState.Normal : (int)_window.WindowState);
        }

        private void ApplyPlacement(Window window, double left, double top, double width, double height, int state)
        {
            if (window == null) return;
            width = Math.Max(window.MinWidth, width);
            height = Math.Max(window.MinHeight, height);
            var requested = new Rect(left, top, width, height);
            var virtualDesktop = new Rect(
                SystemParameters.VirtualScreenLeft,
                SystemParameters.VirtualScreenTop,
                SystemParameters.VirtualScreenWidth,
                SystemParameters.VirtualScreenHeight);
            if (!requested.IntersectsWith(virtualDesktop))
                return;

            window.WindowStartupLocation = WindowStartupLocation.Manual;
            window.WindowState = WindowState.Normal;
            window.Left = left;
            window.Top = top;
            window.Width = width;
            window.Height = height;
            if (state == (int)WindowState.Maximized)
                window.WindowState = WindowState.Maximized;
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

        private void DeveloperDifferencer()
        {
            if (_differencerWindow == null)
            {
                _differencerWindow = new DeveloperDifferencerWindow();
                _differencerWindow.Closed += (s, args) => _differencerWindow = null;
                OverlayOnMain(_differencerWindow);
                _differencerWindow.Show();
            }
            else
            {
                OverlayOnMain(_differencerWindow);
                WindowActivationService.BringToFront(_differencerWindow);
            }
        }

        private void OpenGrep(IReadOnlyList<string> scopes)
        {
            if (_grepWindow == null)
            {
                _grepWindow = new GrepWindow(ViewModel.GetSelectedGrepScopes, scopes);
                _grepWindow.Closed += (closedSender, args) => _grepWindow = null;
                OverlayOnMain(_grepWindow);
                _grepWindow.Show();
            }
            else
            {
                _grepWindow.SetExplicitScopes(scopes);
                OverlayOnMain(_grepWindow);
                WindowActivationService.BringToFront(_grepWindow);
            }
        }

    }
}
