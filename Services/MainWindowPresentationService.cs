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

namespace DesktopIniManager.Services
{
    internal sealed partial class MainWindowPresentationService
    {
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

        private sealed class DiffViewState
        {
            public DiffSnapshot Snapshot;
            public DiffFile Difference;
            public Rect Bounds;
            public WindowState WindowState;
        }

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
                    ViewModel._selectedIconPreview = startup.SelectedIcon?.Preview;
                    ViewModel.SelectedIcon = ViewModel._selectedIconPreview;
                    ViewModel.SelectedIconLabel = startup.SelectedIcon == null ? Strings.Main_PreviewUnavailable : string.Format(Strings.Main_IndexN, startup.SelectedIcon.ShellIndex);
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
                ViewModel._selectedIconIndex = 0;
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
                var browser = new IconGroupBrowserWindow(iconPath, groups, ViewModel._selectedIconIndex) { Owner = _window };
                if (browser.ShowDialog() == true && browser.SelectedGroup != null)
                {
                    ViewModel._selectedIconIndex = browser.SelectedGroup.ShellIndex;
                    ViewModel.SelectedIcon = browser.SelectedGroup.Preview;
                    ViewModel._selectedIconPreview = browser.SelectedGroup.Preview;
                    ViewModel.SelectedIconLabel = string.Format(Strings.Main_IndexN, browser.SelectedGroup.ShellIndex);
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
                var group = IconResourceReader.Read(iconPath).FirstOrDefault(item => item.ShellIndex == ViewModel._selectedIconIndex);
                ViewModel.SelectedIcon = group?.Preview;
                ViewModel._selectedIconPreview = group?.Preview;
                ViewModel.SelectedIconLabel = group == null ? Strings.Main_NotSelected : string.Format(Strings.Main_IndexN, group.ShellIndex);
            }
            catch { ViewModel.SelectedIcon = null; ViewModel._selectedIconPreview = null; ViewModel.SelectedIconLabel = Strings.Main_PreviewUnavailable; }
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

        private void OnClosed(object sender, EventArgs e) { _filterTimer.Stop(); ViewModel.CancelOperations(); _grepWindow?.Close(); SettingsService.SaveIconLibraryPath(ViewModel.IconLibraryPath); SettingsService.SaveSearchQuery(ViewModel.Query); SettingsService.SaveSearchRoot(ViewModel.RootPath); }

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

            ViewModel._searchCts?.Cancel();
            ViewModel._fileListCts?.Cancel();
            ViewModel._filterCts?.Cancel();
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

    }
}
