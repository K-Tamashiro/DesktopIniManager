using DesktopIniManager.ViewModels;
using DesktopIniManager.Models;
using DesktopIniManager.Services;
using Microsoft.Win32;
using System;
using System.Collections.ObjectModel;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Globalization;
using System.Text;
using System.Linq;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Threading;
using DesktopIniManager.Properties;
using System.Runtime.Versioning;

namespace DesktopIniManager.Views
{
    [SupportedOSPlatform("windows")]
    internal sealed class MainWindowPresentationService
    {

        // --- MainWindowPresentationService.cs ---

        private readonly MainWindow _window;
        private readonly MsBuildMenuModel _msBuildMenu = new MsBuildMenuModel();
        private Dispatcher Dispatcher => _window.Dispatcher;
        private string Title { get => _window.Title; set => _window.Title = value; }
        private object FindResource(object key) => _window.FindResource(key);
        private ToggleButton LightThemeButton => _window.LightThemeButton;
        private ToggleButton DarkThemeButton => _window.DarkThemeButton;
        private HistoryTextBox RootBox => _window.RootBox;
        private HistoryTextBox QueryBox => _window.QueryBox;
        private HistoryTextBox IconPathBox => _window.IconPathBox;
        private ToggleButton NodeExpandToggle => _window.NodeExpandToggle;
        private ToggleButton TreeDensityToggle => _window.TreeDensityToggle;
        private bool _updatingToggles;
        private CheckBox AddToGitIgnoreBox => _window.AddToGitIgnoreBox;
        private HistoryTextBox FolderFilterBox => _window.FolderFilterBox;
        private HistoryTextBox ScriptBox => _window.ScriptBox;
        private TreeView ResultsTree => _window.ResultsTree;
        private Border TreePanelBusy => _window.TreePanelBusy;
        private Button FileListViewButton => _window.FileListViewButton;
        private Button FileIconLargeButton => _window.FileIconLargeButton;
        private Button FileIconSmallButton => _window.FileIconSmallButton;
        private ListView FileList => _window.FileList;
        private ListBox FileIconList => _window.FileIconList;
        private ProgressBar SearchProgress => _window.SearchProgress;
        private Button ResetButton => _window.ResetButton;
        private ComboBox LanguageBox => _window.LanguageBox;

        private void ConnectView()
        {
            _window.FolderFilterBox.TextChanged += FolderFilter_TextChanged;
            _window.ResultsTree.SelectedItemChanged += ResultsTree_SelectedItemChanged;
            _window.ResultsTree.PreviewMouseRightButtonDown += ResultsTree_PreviewMouseRightButtonDown;
            _window.ResultsTree.AddHandler(FrameworkElement.ContextMenuOpeningEvent, new ContextMenuEventHandler(ResultsTree_ContextMenuOpening), true);
            ContextMenu treeMenu = _window.Resources["FolderTreeContextMenu"] as ContextMenu;
            if (treeMenu != null)
            {
                MenuItem buildMenu = FindTaggedMenuItem(treeMenu, "dim-msbuild-build");
                MenuItem rebuildMenu = FindTaggedMenuItem(treeMenu, "dim-msbuild-rebuild");
                if (buildMenu != null) buildMenu.ItemsSource = _msBuildMenu.BuildItems;
                if (rebuildMenu != null) rebuildMenu.ItemsSource = _msBuildMenu.RebuildItems;
            }
            _window.FileList.SelectionChanged += FileList_SelectionChanged;
            _window.FileList.MouseDoubleClick += FileList_MouseDoubleClick;
            InitializeFileContextMenu(_window.FileList);
            _window.FileList.ContextMenuOpening += FileList_ContextMenuOpening;
            _window.FileIconList.SelectionChanged += FileList_SelectionChanged;
            _window.FileIconList.MouseDoubleClick += FileList_MouseDoubleClick;
            InitializeFileContextMenu(_window.FileIconList);
            _window.FileIconList.ContextMenuOpening += FileList_ContextMenuOpening;
            _window.LanguageBox.SelectionChanged += LanguageBox_SelectionChanged;
            _window.RunScriptButton.Click += (sender, args) => RunScriptCommand();
            _window.BrowseScriptButton.Click += (sender, args) => BrowseScriptCommand();
            _window.ScriptBox.PreviewKeyDown += ScriptBox_PreviewKeyDown;
            _window.RunScriptButton.ToolTip = ScriptText("Main_RunScript", "Run  —  execute the command (%d folder / %f file / %n name)");
            _window.BrowseScriptButton.ToolTip = ScriptText("Main_BrowseScript", "Browse  —  pick .bat / .cmd / .ps1 / .vbs");
            _window.ScriptBox.ToolTip = ScriptText("Main_ScriptTooltip", "Command. %d = folder, %f = file path, %n = file name. Quote as \"%d\" to wrap.");
            _window.Closing += OnClosing;
            _window.Closed += OnClosed;
            ViewModel.InteractionRequested += HandleInteraction;
            _window.AllowDrop = true;
            _window.PreviewDragEnter += FolderDropPreview;
            _window.PreviewDragOver += FolderDropPreview;
            _window.PreviewDrop += MainWindow_Drop;
            if (NodeExpandToggle != null)
            {
                NodeExpandToggle.Checked += NodeExpandToggle_Changed;
                NodeExpandToggle.Unchecked += NodeExpandToggle_Changed;
            }
            if (TreeDensityToggle != null)
            {
                TreeDensityToggle.Checked += TreeDensityToggle_Changed;
                TreeDensityToggle.Unchecked += TreeDensityToggle_Changed;
            }
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
                case MainWindowAction.TreeToEditor: TreeToEditor(parameter as FolderMatch, false); break;
                case MainWindowAction.TreeFilesToEditor: TreeToEditor(parameter as FolderMatch, true); break;
                case MainWindowAction.FileListToEditor: FileListToEditor(); break;
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
            ViewModel.SearchHistoryRequested += () => { RootBox.CommitHistory(); QueryBox.CommitHistory(); IconPathBox.CommitHistory(); ScriptBox.CommitHistory(); };
            ViewModel.SearchRootSelectionRequested += SelectSearchRootForFileList;
            ViewModel.FileScrollRequested += item =>
            {
                if (item == null) return;
                _syncingTreeFromFile = true;
                try
                {
                    FileList.SelectedItem = item;
                    FileIconList.SelectedItem = item;
                    FileList.ScrollIntoView(item);
                    FileIconList.ScrollIntoView(item);
                }
                finally
                {
                    _syncingTreeFromFile = false;
                }
            };
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
                ApplyMainToolbarIcons();
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

        private void FileListToEditor()
        {
            try
            {
                ItemsControl list = FileList.Visibility == Visibility.Visible
                    ? (ItemsControl)FileList : FileIconList;
                var text = new StringBuilder();
                foreach (FileListItem file in list.Items)
                    text.AppendLine(file.Name);

                string outputPath = Path.Combine(Path.GetTempPath(), "DesktopIniManager-file-list.txt");
                File.WriteAllText(outputPath, text.ToString(), new UTF8Encoding(true));
                OpenTextInEditor(outputPath);
                ViewModel.Status = string.Format(StringOverlay.Get("Main_TreeOpened"), "File List", ViewModel.FilePanelPath);
            }
            catch (Exception ex)
            {
                ViewModel.ShowError(string.Format(StringOverlay.Get("Main_TreeFailed"), "File List"), ex);
            }
        }

        private void TreeToEditor(FolderMatch folder, bool includeFiles)
        {
            if (folder == null) return;

            string label = includeFiles ? "tree /f" : "tree";
            string outputPath = Path.Combine(
                Path.GetTempPath(),
                includeFiles ? "DesktopIniManager-tree-f.txt" : "DesktopIniManager-tree.txt");

            try
            {
                var text = new StringBuilder();
                WriteContainedTree(text, folder, includeFiles);
                File.WriteAllText(outputPath, text.ToString(), new UTF8Encoding(true));
                OpenTextInEditor(outputPath);
                ViewModel.Status = string.Format(StringOverlay.Get("Main_TreeOpened"), label, folder.Path);
            }
            catch (Exception ex)
            {
                ViewModel.ShowError(string.Format(StringOverlay.Get("Main_TreeFailed"), label), ex);
            }
        }

        private void WriteContainedTree(StringBuilder text, FolderMatch folder, bool includeFiles)
        {
            if (folder == null) return;
            text.AppendLine(folder.Path);
            WriteContainedLevel(text, folder, string.Empty, includeFiles);
        }

        private void WriteContainedLevel(StringBuilder text, FolderMatch folder, string prefix, bool includeFiles)
        {
            IReadOnlyList<string> files = includeFiles
                ? ViewModel.ContainedFileNames(folder)
                : Array.Empty<string>();
            var dirs = new List<FolderMatch>();
            foreach (FolderMatch child in folder.Children)
            {
                if (child.IsHidden || child.IsFilterHidden) continue;
                dirs.Add(child);
            }

            for (int i = 0; i < files.Count; i++)
            {
                text.Append(prefix);
                text.Append(dirs.Count > 0 ? "│  " : "    ");
                text.AppendLine(files[i]);
            }
            if (files.Count > 0 && dirs.Count > 0)
            {
                text.Append(prefix);
                text.AppendLine("│");
            }
            for (int i = 0; i < dirs.Count; i++)
            {
                bool last = i == dirs.Count - 1;
                text.Append(prefix);
                text.Append(last ? "└─" : "├─");
                text.AppendLine(dirs[i].Name);
                WriteContainedLevel(text, dirs[i], prefix + (last ? "    " : "│   "), includeFiles);
            }
        }

        private static void OpenTextInEditor(string path)
        {
            string editor = GrepWindowViewModel.ExpandEditorPath(SettingsService.LoadEditorPath());
            if (string.IsNullOrWhiteSpace(editor))
                editor = "code";
            string arguments = (SettingsService.LoadEditorArguments() ?? string.Empty)
                .Replace("{file}", path)
                .Replace("{line}", "1")
                .Replace("{column}", "1");
            if (string.IsNullOrWhiteSpace(arguments))
                arguments = "\"" + path + "\"";
            Process.Start(new ProcessStartInfo(editor, arguments) { UseShellExecute = true });
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

        private static readonly HashSet<string> ScriptExtensions =
            new HashSet<string>(StringComparer.OrdinalIgnoreCase) { ".ps1", ".bat", ".cmd", ".vbs" };

        private void InitializeFileContextMenu(Selector list)
        {
            if (list.ContextMenu == null)
            {
                var runIcon = new TextBlock
                {
                    FontFamily = new FontFamily("Segoe MDL2 Assets"),
                    FontSize = 15,
                    Text = "\uE768",
                    HorizontalAlignment = HorizontalAlignment.Center,
                    VerticalAlignment = VerticalAlignment.Center
                };
                runIcon.SetResourceReference(TextBlock.ForegroundProperty, "Ink");
                var run = new MenuItem
                {
                    Header = ScriptText("Main_RunScript", "Run script"),
                    Icon = runIcon
                };
                run.Click += (s, args) => RunSelectedScript(
                    ((s as MenuItem)?.Parent as ContextMenu)?.PlacementTarget is Selector owner
                        ? owner.SelectedItem as FileListItem
                        : list.SelectedItem as FileListItem);
                var menu = new ContextMenu();
                menu.Items.Add(run);
                list.ContextMenu = menu;
            }
        }

        private void FileList_ContextMenuOpening(object sender, ContextMenuEventArgs e)
        {
            var list = sender as Selector;
            if (list == null) return;
            var file = list.SelectedItem as FileListItem;
            bool canRun = file != null && ScriptExtensions.Contains(file.Extension ?? string.Empty);

            if (list.ContextMenu.Items.Count > 0 && list.ContextMenu.Items[0] is MenuItem item)
                item.IsEnabled = canRun;
        }

        private void RunSelectedScript(FileListItem file)
        {
            if (file == null || !File.Exists(file.Path)) return;

            string workDir = Path.GetDirectoryName(file.Path);
            if (string.IsNullOrEmpty(workDir) || !Directory.Exists(workDir))
            {
                ViewModel.ShowError(StringOverlay.Get("Main_RunScriptFailed"), new DirectoryNotFoundException(workDir));
                return;
            }

            string ext = file.Extension ?? string.Empty;
            string command;
            if (ext.Equals(".ps1", StringComparison.OrdinalIgnoreCase))
            {
                command = "\"" + file.Path + "\"";
            }
            else if (ext.Equals(".bat", StringComparison.OrdinalIgnoreCase)
                  || ext.Equals(".cmd", StringComparison.OrdinalIgnoreCase))
            {
                command = "\"" + file.Path + "\"";
            }
            else if (ext.Equals(".vbs", StringComparison.OrdinalIgnoreCase))
            {
                command = "cscript.exe //nologo \"" + file.Path + "\"";
            }
            else return;

            try
            {
                StartKeepOpenConsole(workDir, command);
                ViewModel.Status = string.Format(StringOverlay.Get("Main_RunScriptStarted"), file.Name);
            }
            catch (Exception ex)
            {
                ViewModel.ShowError(ScriptText("Main_RunScriptFailed", "Could not run the script."), ex);
            }
        }

        private static string ScriptText(string key, string fallback)
        {
            string value = StringOverlay.Get(key);
            return string.IsNullOrEmpty(value) || value == key ? fallback : value;
        }

        private void ScriptBox_PreviewKeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Enter)
            {
                e.Handled = true;
                RunScriptCommand();
            }
        }

        private void BrowseScriptCommand()
        {
            try
            {
                var dialog = new OpenFileDialog
                {
                    Filter = "Scripts (*.bat;*.cmd;*.ps1;*.vbs)|*.bat;*.cmd;*.ps1;*.vbs|All files (*.*)|*.*",
                    CheckFileExists = true
                };
                string current = (ScriptBox.Text ?? string.Empty).Trim();
                string first = FirstToken(current);
                if (File.Exists(first))
                {
                    dialog.InitialDirectory = Path.GetDirectoryName(first);
                    dialog.FileName = Path.GetFileName(first);
                }
                if (dialog.ShowDialog(_window) != true) return;
                string quoted = QuoteIfNeeded(dialog.FileName);
                ScriptBox.Text = quoted + " \"%d\"";
                ScriptBox.CaretIndex = ScriptBox.Text.Length;
                ScriptBox.CommitHistory();
                ScriptBox.Focus();
            }
            catch (Exception ex)
            {
                ViewModel.ShowError(ScriptText("Main_RunScriptFailed", "Could not run the script."), ex);
            }
        }

        private void RunScriptCommand()
        {
            string template = (ScriptBox.Text ?? string.Empty).Trim();
            if (string.IsNullOrEmpty(template))
            {
                ViewModel.Status = StringOverlay.Get("Main_ScriptEmpty");
                return;
            }

            string dir = ViewModel.FilePanelPath;
            if (string.IsNullOrWhiteSpace(dir))
                dir = (ResultsTree.SelectedItem as FolderMatch)?.Path;
            FileListItem selectedFile = (FileList.SelectedItem as FileListItem)
                ?? (FileIconList.SelectedItem as FileListItem);
            string file = selectedFile?.Path;

            bool needsDir = ContainsToken(template, "d");
            bool needsFile = ContainsToken(template, "f") || ContainsToken(template, "n");
            if (needsDir && (string.IsNullOrWhiteSpace(dir) || !Directory.Exists(dir)))
            {
                ViewModel.ShowError(StringOverlay.Get("Main_ScriptMissingDir"), new DirectoryNotFoundException(dir ?? string.Empty));
                return;
            }
            if (needsFile && (string.IsNullOrWhiteSpace(file) || !File.Exists(file)))
            {
                ViewModel.ShowError(StringOverlay.Get("Main_ScriptMissingFile"), new FileNotFoundException(file ?? string.Empty));
                return;
            }

            string command = ExpandScriptTokens(template, dir, file);
            MessageBoxResult answer = new UserDialogService(_window).Show(
                command + "\n\n" + StringOverlay.Get("Main_ScriptConfirmAsk"),
                StringOverlay.Get("Main_RunScript"),
                MessageBoxButton.OKCancel,
                MessageBoxImage.Question);
            if (answer != MessageBoxResult.OK) return;

            string workDir = dir;
            if (string.IsNullOrEmpty(workDir) || !Directory.Exists(workDir))
            {
                string exe = FirstToken(command);
                if (File.Exists(exe))
                    workDir = Path.GetDirectoryName(exe);
            }
            if (string.IsNullOrEmpty(workDir) || !Directory.Exists(workDir))
                workDir = Environment.CurrentDirectory;

            try
            {
                StartKeepOpenConsole(workDir, command);
                ScriptBox.CommitHistory();
                ViewModel.Status = string.Format(StringOverlay.Get("Main_RunScriptStarted"), command);
            }
            catch (Exception ex)
            {
                ViewModel.ShowError(ScriptText("Main_RunScriptFailed", "Could not run the script."), ex);
            }
        }

        private static void StartKeepOpenConsole(string workDir, string command)
        {
            if (string.IsNullOrWhiteSpace(command)) return;
            string folder = string.IsNullOrEmpty(workDir) ? Environment.CurrentDirectory : workDir;
            string exe = FirstToken(command);
            string args = RestAfterFirstToken(command);
            string powershell = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.System),
                "WindowsPowerShell", "v1.0", "powershell.exe");

            var psi = new ProcessStartInfo
            {
                WorkingDirectory = folder,
                UseShellExecute = true
            };

            if (exe.EndsWith(".ps1", StringComparison.OrdinalIgnoreCase))
            {
                psi.FileName = powershell;
                psi.Arguments = "-NoExit -NoProfile -ExecutionPolicy Bypass -File \"" + exe + "\""
                    + (string.IsNullOrEmpty(args) ? string.Empty : " " + args);
            }
            else
            {
                psi.FileName = Path.Combine(Environment.SystemDirectory, "cmd.exe");
                psi.Arguments = "/d /s /k \"" + command + "\"";
            }

            Process.Start(psi);
        }

        private static string RestAfterFirstToken(string command)
        {
            if (string.IsNullOrWhiteSpace(command)) return string.Empty;
            string text = command.Trim();
            if (text[0] == '"')
            {
                int end = text.IndexOf('"', 1);
                return end < 0 ? string.Empty : text.Substring(end + 1).TrimStart();
            }
            int space = text.IndexOfAny(new[] { ' ', '\t' });
            return space < 0 ? string.Empty : text.Substring(space + 1).TrimStart();
        }

        private static bool ContainsToken(string template, string name)
        {
            if (string.IsNullOrEmpty(template)) return false;
            return template.IndexOf("%" + name, StringComparison.OrdinalIgnoreCase) >= 0;
        }

        private static string ExpandScriptTokens(string template, string dir, string file)
        {
            string quotedDir = string.IsNullOrEmpty(dir) ? "\"%d\"" : "\"" + dir + "\"";
            string quotedFile = string.IsNullOrEmpty(file) ? "\"%f\"" : "\"" + file + "\"";
            string name = string.IsNullOrEmpty(file) ? null : Path.GetFileName(file);
            string quotedName = string.IsNullOrEmpty(name) ? "\"%n\"" : "\"" + name + "\"";
            return template
                .Replace("\"%d\"", quotedDir)
                .Replace("\"%D\"", quotedDir)
                .Replace("\"%f\"", quotedFile)
                .Replace("\"%F\"", quotedFile)
                .Replace("\"%n\"", quotedName)
                .Replace("\"%N\"", quotedName)
                .Replace("%d", dir ?? "%d")
                .Replace("%D", dir ?? "%D")
                .Replace("%f", file ?? "%f")
                .Replace("%F", file ?? "%F")
                .Replace("%n", name ?? "%n")
                .Replace("%N", name ?? "%N");
        }

        private static string FirstToken(string command)
        {
            if (string.IsNullOrWhiteSpace(command)) return string.Empty;
            string text = command.Trim();
            if (text[0] == '"')
            {
                int end = text.IndexOf('"', 1);
                return end > 0 ? text.Substring(1, end - 1) : text.Trim('"');
            }
            int space = text.IndexOfAny(new[] { ' ', '\t' });
            return space < 0 ? text : text.Substring(0, space);
        }

        private static string QuoteIfNeeded(string path)
        {
            if (string.IsNullOrEmpty(path)) return path;
            if (path.IndexOfAny(new[] { ' ', '\t' }) >= 0 && !(path.StartsWith("\"") && path.EndsWith("\"")))
                return "\"" + path + "\"";
            return path;
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
            string tip;
            if (language == "ja") tip = "設定・履歴・フォルダー一覧を初期化します";
            else if (language == "zh") tip = "清除设置、历史和文件夹列表";
            else if (language == "ko") tip = "설정, 기록, 폴더 목록을 초기화합니다";
            else tip = "Clear settings, history, and folder lists";
            ResetButton.ToolTip = tip;
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
            ViewModel.SearchHitLabel = "0/0";
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
            if (TreeDensityToggle == null) return;
            _updatingToggles = true;
            TreeDensityToggle.IsChecked = !ViewModel.TreeCompact;
            TreeDensityToggle.ToolTip = ViewModel.TreeCompact ? Strings.Main_TreeCompact : Strings.Main_TreeComfortable;
            _updatingToggles = false;
            if (_window.TreeDensityIcon != null)
                _window.TreeDensityIcon.Source = DifferencerStatusIcons.GetCustomIcon(ViewModel.TreeCompact ? 35 : 36);
        }

        private void ApplyMainToolbarIcons()
        {
            UpdateNodeExpandIcon();
            HighlightTreeDensityButtons();
            SetToolbarIcon(_window.FolderFilterIcon, 57);
            SetToolbarIcon(_window.HideSelectedIcon, 46);
            SetToolbarIcon(_window.UnhideIcon, 47);
            SetToolbarIcon(_window.ClearFolderFilterIcon, 25);
            SetToolbarIcon(_window.FileListViewIcon, 37);
            SetToolbarIcon(_window.FileIconLargeIcon, 38);
            SetToolbarIcon(_window.FileIconSmallIcon, 39);
            SetToolbarIcon(_window.InvertSelectionIcon, 48);
            SetToolbarIcon(_window.RemoveButtonIcon, 50);
            SetToolbarIcon(_window.DifferencerButtonIcon, 51);
            SetToolbarIcon(_window.GrepButtonIcon, 71);
            SetToolbarIcon(_window.ApplyButtonIcon, 34);
            SetToolbarIcon(_window.RunScriptIcon, 89);
            SetToolbarIcon(_window.BrowseScriptIcon, 88);
            SetToolbarIcon(_window.ResetButtonIcon, 70);
            SetToolbarIcon(_window.ChooseRootIcon, 62);
            SetToolbarIcon(_window.ClearQueryIcon, 25);
            SetToolbarIcon(_window.GitSearchIcon, 31);
            SetToolbarIcon(_window.SearchButtonIcon, 29);
            SetToolbarIcon(_window.CancelButtonIcon, 24);
            SetToolbarIcon(_window.PrevSearchMatchIcon, 33);
            SetToolbarIcon(_window.NextSearchMatchIcon, 32);
            SetToolbarIcon(_window.ChooseIconLibraryIcon, 61);
            SetToolbarIcon(_window.ChooseIconButtonIcon, 87);
            SetToolbarIcon(_window.CloseWindowIcon, 26);
            UpdateThemeIcons();
        }

        private static void SetToolbarIcon(System.Windows.Controls.Image image, int index)
        {
            if (image != null)
                image.Source = DifferencerStatusIcons.GetCustomIcon(index);
        }

        private void NodeExpandToggle_Changed(object sender, RoutedEventArgs e)
        {
            if (_updatingToggles) return;
            bool expanded = NodeExpandToggle.IsChecked == true;
            if (expanded) ViewModel.ExpandAllCommand.Execute(null);
            else ViewModel.CollapseAllCommand.Execute(null);
            NodeExpandToggle.ToolTip = expanded ? Strings.Common_Collapse : Strings.Common_Expand;
            UpdateNodeExpandIcon();
        }

        private void TreeDensityToggle_Changed(object sender, RoutedEventArgs e)
        {
            if (_updatingToggles) return;
            if (TreeDensityToggle.IsChecked == true) ComfortableTree();
            else CompactTree();
        }

        private void UpdateNodeExpandIcon()
        {
            if (_window.NodeExpandIcon == null) return;
            bool expanded = NodeExpandToggle != null && NodeExpandToggle.IsChecked == true;
            _window.NodeExpandIcon.Source = DifferencerStatusIcons.GetCustomIcon(expanded ? 56 : 55);
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

        private void UpdateThemeIcons()
        {
            bool dark = DarkThemeButton != null && DarkThemeButton.IsChecked == true;
            SetToolbarIcon(_window.LightThemeIcon, dark ? 43 : 42);
            SetToolbarIcon(_window.DarkThemeIcon, dark ? 44 : 45);
        }

        private void LightTheme() => SetTheme(false);

        private void DarkTheme() => SetTheme(true);

        private void SetTheme(bool dark)
        {
            ThemeService.Apply(dark);
            LightThemeButton.IsChecked = !dark;
            DarkThemeButton.IsChecked = dark;
            UpdateThemeIcons();
            SettingsService.SaveDarkMode(dark);
            HighlightTreeDensityButtons();
            HighlightFileViewButtons(FileList.Visibility == Visibility.Visible, _largeFileIcons);
        }

        // --- MainWindowPresentationService.Tree.cs ---

        private void SelectSearchRootForFileList()
        {
            FolderMatch root = ViewModel.SearchTabRoot;
            if (root == null) return;
            root.IsExpanded = true;
            Dispatcher.BeginInvoke(new Action(() =>
            {
                if (ViewModel.TreeViewIndex != 2 || ViewModel.SearchTabRoot == null) return;
                FolderMatch current = ViewModel.SearchTabRoot;
                current.IsExpanded = true;
                current.IsCurrent = true;
            }), System.Windows.Threading.DispatcherPriority.Loaded);
        }

        private void ResultsTree_PreviewMouseRightButtonDown(object sender, MouseButtonEventArgs e)
        {
            PrepareMsBuildMenu(FindFolderFromElement(e.OriginalSource as DependencyObject));
        }

        private void ResultsTree_ContextMenuOpening(object sender, ContextMenuEventArgs e)
        {
            FrameworkElement source = e.OriginalSource as FrameworkElement;
            FolderMatch folder = FindFolderFromElement(source);
            PrepareMsBuildMenu(folder);
            ContextMenu menu = FindContextMenu(source);
            ApplyMsBuildMenuVisibility(menu);
        }

        private void PrepareMsBuildMenu(FolderMatch folder)
        {
            _msBuildMenu.BuildItems.Clear();
            _msBuildMenu.RebuildItems.Clear();
            if (folder == null) return;

            FolderMatch solution = folder.FindSolutionRoot();
            string sln = solution?.SolutionFile;
            IList<string> configs = solution?.BuildConfigurations;
            if (string.IsNullOrWhiteSpace(sln) || !File.Exists(sln))
            {
                sln = FindSolutionFile(folder.Path);
                if (string.IsNullOrWhiteSpace(sln)) return;
            }
            if (configs == null || configs.Count == 0)
                configs = SolutionTreeService.ReadBuildConfigurations(sln);
            if (configs == null || configs.Count == 0) return;

            if (solution != null)
            {
                solution.SolutionFile = sln;
                solution.BuildConfigurations = configs;
            }

            foreach (string pair in configs)
            {
                string captured = pair;
                string file = sln;
                _msBuildMenu.BuildItems.Add(new MsBuildActionItem
                {
                    Header = captured,
                    Command = new RelayCommand(() => RunMsBuild(file, captured, "Build"))
                });
                _msBuildMenu.RebuildItems.Add(new MsBuildActionItem
                {
                    Header = captured,
                    Command = new RelayCommand(() => RunMsBuild(file, captured, "Rebuild"))
                });
            }
        }

        private void ApplyMsBuildMenuVisibility(ContextMenu menu)
        {
            if (menu == null) return;
            bool show = _msBuildMenu.BuildItems.Count > 0;
            Visibility visibility = show ? Visibility.Visible : Visibility.Collapsed;
            MenuItem buildMenu = FindTaggedMenuItem(menu, "dim-msbuild-build");
            MenuItem rebuildMenu = FindTaggedMenuItem(menu, "dim-msbuild-rebuild");
            Separator buildSeparator = FindTaggedSeparator(menu, "dim-msbuild");
            if (buildMenu != null) buildMenu.Visibility = visibility;
            if (rebuildMenu != null) rebuildMenu.Visibility = visibility;
            if (buildSeparator != null) buildSeparator.Visibility = visibility;
        }

        private static MenuItem FindTaggedMenuItem(ItemsControl menu, string tag)
        {
            foreach (object item in menu.Items)
            {
                MenuItem menuItem = item as MenuItem;
                if (menuItem != null && Equals(menuItem.Tag, tag))
                    return menuItem;
            }
            return null;
        }

        private static Separator FindTaggedSeparator(ItemsControl menu, string tag)
        {
            foreach (object item in menu.Items)
            {
                Separator separator = item as Separator;
                if (separator != null && Equals(separator.Tag, tag))
                    return separator;
            }
            return null;
        }

        private static TextBlock MsBuildMenuIcon(string glyph)
        {
            return new TextBlock
            {
                Text = glyph,
                FontFamily = new FontFamily("Segoe MDL2 Assets"),
                FontSize = 15,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center
            };
        }

        private void RunMsBuild(string solutionFile, string configurationPair, string target)
        {
            if (string.IsNullOrWhiteSpace(solutionFile) || !File.Exists(solutionFile))
            {
                ViewModel.ShowError(ScriptText("Main_MsBuildMissingSln", "Solution file was not found."), new FileNotFoundException(solutionFile ?? string.Empty));
                return;
            }

            string configuration = configurationPair;
            string platform = "Any CPU";
            int bar = configurationPair.LastIndexOf('|');
            if (bar >= 0)
            {
                configuration = configurationPair.Substring(0, bar);
                platform = configurationPair.Substring(bar + 1);
            }

            string msbuild = FindMsBuild();
            if (string.IsNullOrEmpty(msbuild))
            {
                ViewModel.ShowError(ScriptText("Main_MsBuildNotFound", "MSBuild.exe was not found."), new FileNotFoundException("MSBuild.exe"));
                return;
            }

            string command = "\"" + msbuild + "\" \"" + solutionFile + "\" /t:" + target
                + " /p:Configuration=\"" + configuration + "\" /p:Platform=\"" + platform + "\" /m";
            string workDir = Path.GetDirectoryName(solutionFile);
            try
            {
                StartKeepOpenConsole(workDir, command);
                ViewModel.Status = string.Format(ScriptText("Main_MsBuildStarted", "Started {0} {1}"), target, configurationPair);
            }
            catch (Exception ex)
            {
                ViewModel.ShowError(ScriptText("Main_MsBuildFailed", "MSBuild could not be started."), ex);
            }
        }

        private static string FindMsBuild()
        {
            string vswhere = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
                "Microsoft Visual Studio", "Installer", "vswhere.exe");
            if (File.Exists(vswhere))
            {
                try
                {
                    var psi = new ProcessStartInfo
                    {
                        FileName = vswhere,
                        Arguments = "-latest -requires Microsoft.Component.MSBuild -find MSBuild\\**\\Bin\\MSBuild.exe",
                        UseShellExecute = false,
                        RedirectStandardOutput = true,
                        CreateNoWindow = true
                    };
                    using (Process process = Process.Start(psi))
                    {
                        if (process != null)
                        {
                            string path = (process.StandardOutput.ReadLine() ?? string.Empty).Trim();
                            process.WaitForExit(4000);
                            if (File.Exists(path)) return path;
                        }
                    }
                }
                catch (Exception) { }
            }

            string[] editions = { "Enterprise", "Professional", "Community", "BuildTools" };
            string[] years = { "2022", "2019", "2017" };
            string[] roots =
            {
                Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
                Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86)
            };
            foreach (string root in roots)
            {
                foreach (string year in years)
                {
                    foreach (string edition in editions)
                    {
                        string[] candidates =
                        {
                            Path.Combine(root, "Microsoft Visual Studio", year, edition, "MSBuild", "Current", "Bin", "amd64", "MSBuild.exe"),
                            Path.Combine(root, "Microsoft Visual Studio", year, edition, "MSBuild", "Current", "Bin", "MSBuild.exe")
                        };
                        foreach (string candidate in candidates)
                            if (File.Exists(candidate)) return candidate;
                    }
                }
            }
            return null;
        }

        private static FolderMatch FindFolderFromElement(DependencyObject source)
        {
            for (DependencyObject current = source; current != null; current = VisualTreeHelper.GetParent(current))
            {
                FrameworkElement element = current as FrameworkElement;
                if (element?.DataContext is FolderMatch folder)
                    return folder;
            }
            return null;
        }

        private static ContextMenu FindContextMenu(DependencyObject source)
        {
            for (DependencyObject current = source; current != null; current = VisualTreeHelper.GetParent(current))
            {
                FrameworkElement element = current as FrameworkElement;
                if (element?.ContextMenu != null)
                    return element.ContextMenu;
            }
            return null;
        }

        private static void RemoveTaggedMenuItems(ItemsControl menu)
        {
            RemoveTaggedMenuItems(menu, "dim-msbuild");
        }

        private static void RemoveTaggedMenuItems(ItemsControl menu, string tag)
        {
            if (menu == null) return;
            for (int i = menu.Items.Count - 1; i >= 0; i--)
            {
                FrameworkElement item = menu.Items[i] as FrameworkElement;
                if (item != null && Equals(item.Tag, tag))
                    menu.Items.RemoveAt(i);
            }
        }

        private static string FindSolutionFile(string directory)
        {
            string current = directory;
            for (int depth = 0; depth < 8 && !string.IsNullOrWhiteSpace(current); depth++)
            {
                if (Directory.Exists(current))
                {
                    try
                    {
                        string sln = Directory.EnumerateFiles(current, "*.sln").FirstOrDefault();
                        if (!string.IsNullOrEmpty(sln)) return sln;
                    }
                    catch (UnauthorizedAccessException) { }
                    catch (IOException) { }
                }
                current = Path.GetDirectoryName(current);
            }
            return null;
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
            if (Mouse.RightButton == MouseButtonState.Pressed) return;
            FileListItem file = (sender as System.Windows.Controls.Primitives.Selector)?.SelectedItem as FileListItem;
            if (file == null) return;
            ViewModel.NoteSelectedSearchMatch(file);
            if (ViewModel.TreeViewIndex != 2) return;
            RevealFolderInCurrentTree(file.Path);
        }

        private void RevealFolderInCurrentTree(string directory)
        {
            ObservableCollection<FolderMatch> roots = ViewModel.CurrentTreeRoots();
            if (string.IsNullOrEmpty(directory) || roots.Count == 0) return;
            FolderMatch target = ViewModel.FindClosestFolderInCurrentTree(directory);
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

        private sealed class MsBuildMenuModel
        {
            public ObservableCollection<MsBuildActionItem> BuildItems { get; } = new ObservableCollection<MsBuildActionItem>();
            public ObservableCollection<MsBuildActionItem> RebuildItems { get; } = new ObservableCollection<MsBuildActionItem>();
        }

        private sealed class MsBuildActionItem
        {
            public string Header { get; set; }
            public ICommand Command { get; set; }
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
