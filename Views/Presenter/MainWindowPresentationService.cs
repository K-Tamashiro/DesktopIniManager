using DesktopIniManager.Models;
using DesktopIniManager.Properties;
using DesktopIniManager.Services;
using DesktopIniManager.ViewModels;
using Microsoft.Win32;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.Versioning;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;

namespace DesktopIniManager.Views;

[SupportedOSPlatform("windows")]
internal sealed class MainWindowPresentationService
{
    private static readonly HashSet<string> ScriptExtensions =
        new(StringComparer.OrdinalIgnoreCase) { ".ps1", ".bat", ".cmd", ".vbs" };

    private readonly DispatcherTimer _filterTimer;
    private readonly MsBuildMenuModel _msBuildMenu = new();

    // --- MainWindowPresentationService.cs ---

    private readonly MainWindow _window;
    private bool _analyzeSelectedRoot;

    private DeveloperDifferencerWindow _differencerWindow;
    private FileListItem _fileContextItem;
    private FileListItem _fileDragItem;
    private Point _fileDragStart;
    private GrepWindow _grepWindow;
    private bool _internalDragOut;

    private bool _languageReady;
    private bool _largeFileIcons;
    private bool _searchModeActive;
    private bool _syncingTreeFromFile;

    private bool _themeBoxReady;
    private FolderMatch _treeDragFolder;
    private bool _treeDragPreparing;
    private Point _treeDragStart;
    private bool _updatingToggles;
    private bool differencerSavedOnClose;

    /// <summary>Opens or activates the developer differencer window.</summary>
    private bool savingDifferencerOnClose;

    internal MainWindowPresentationService(MainWindow window, StartupState startup)
    {
        _window = window;
        ViewModel = new MainWindowViewModel(startup, Dispatcher, new UserDialogService(window));
        var commandLine = Environment.GetCommandLineArgs();
        var runGitSearch = commandLine.Any(argument =>
            string.Equals(argument, "--run-git-search", StringComparison.OrdinalIgnoreCase));
        var runSearch = commandLine.Any(argument =>
            string.Equals(argument, "--run-search", StringComparison.OrdinalIgnoreCase));
        var currentTheme = startup?.Theme ?? SettingsService.LoadTheme();
        _window.DataContext = ViewModel;
        ConnectView();
        ViewModel.SearchHistoryRequested += () =>
        {
            RootBox.CommitHistory();
            QueryBox.CommitHistory();
            IconPathBox.CommitHistory();
            ScriptBox.CommitHistory();
        };
        ViewModel.SearchRootSelectionRequested += SelectSearchRootForFileList;
        ViewModel.SearchModeRequested += EnterSearchMode;
        ViewModel.FolderSearchHitRequested += folder => RevealFolderInCurrentTree(folder.Path);
        ViewModel.FileScrollRequested += (item, revealOwningFolder) =>
        {
            if (item == null) return;
            _syncingTreeFromFile = true;
            try
            {
                FileList.SelectedItem = item;
                FileIconList.SelectedItem = item;
            }
            finally
            {
                _syncingTreeFromFile = false;
            }

            // Prev/Next hit navigation reveals the owning folder. Folder selection
            // loads the list without stealing the tree selection.
            if (revealOwningFolder && (ViewModel.TreeViewIndex == 0 || ViewModel.TreeViewIndex == 2))
                RevealFolderInCurrentTree(item.Path);

            // Selection can be outside the current viewport. Scroll after the tree reveal/layout
            // so the selected search hit is always visible to the user.
            Dispatcher.BeginInvoke(new Action(() =>
            {
                FileList.ScrollIntoView(item);
                FileIconList.ScrollIntoView(item);
            }), DispatcherPriority.Loaded);
        };
        ViewModel.OpenGrepRequested += OpenGrep;
        ViewModel.PropertyChanged += (sender, args) =>
        {
            if (args.PropertyName == nameof(ViewModel.IsSearching)) SetSearching(ViewModel.IsSearching);
        };
        SelectThemeBox(currentTheme);
        InitLanguageBox();
        _filterTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(250) };
        _filterTimer.Tick += (sender, args) =>
        {
            _filterTimer.Stop();
            _ = ViewModel.ApplyFolderFilterAsync();
        };
        var savedRoot = startup != null ? startup.Root : SettingsService.LoadSearchRoot();
        ViewModel.RootPath = savedRoot ?? string.Empty;
        var defaultLibrary = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Assets", "folder_set.icl");
        var savedLibrary = startup != null ? startup.IconLibrary : SettingsService.LoadIconLibraryPath();
        ViewModel.IconLibraryPath = !string.IsNullOrWhiteSpace(savedLibrary) ? savedLibrary : defaultLibrary;
        var savedQuery = startup != null ? startup.Query : SettingsService.LoadSearchQuery();
        ViewModel.Query = string.Equals(savedQuery, ".git", StringComparison.OrdinalIgnoreCase)
            ? string.Empty
            : savedQuery ?? string.Empty;
        RestoreWindowPlacement(commandLine);
        HookPathBox(RootBox);
        HookPathBox(IconPathBox);
        ApplyTreeDensity(startup != null ? startup.TreeCompact : SettingsService.LoadTreeCompact(), false);
        ViewModel.RestoreFolderTrees();
        if (startup != null)
        {
            startup.Tree = null;
            startup.Physical = null;
            startup.Solution = null;
        }

        _window.Loaded += (sender, args) =>
        {
            if (startup == null)
            {
                RefreshSelectedIconPreview();
            }
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

    private Dispatcher Dispatcher => _window.Dispatcher;

    private string Title
    {
        get => _window.Title;
        set => _window.Title = value;
    }

    private ComboBox ThemeBox => _window.ThemeBox;
    private HistoryTextBox RootBox => _window.RootBox;
    private HistoryTextBox QueryBox => _window.QueryBox;
    private HistoryTextBox IconPathBox => _window.IconPathBox;
    private ToggleButton NodeExpandToggle => _window.NodeExpandToggle;
    private ToggleButton TreeDensityToggle => _window.TreeDensityToggle;
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

    internal MainWindowViewModel ViewModel { get; }

    private object FindResource(object key)
    {
        return _window.FindResource(key);
    }

    private void ConnectView()
    {
        InputManager.Current.PreProcessInput += SwitchWindowShortcut;
        _window.PreviewKeyDown += MainSearchKeyDown;
        RootBox.HistoryItemApplied += (sender, args) => StartSelectedRootAnalysis();
        _window.FolderFilterBox.TextChanged += FolderFilter_TextChanged;
        _window.ResultsTree.SelectedItemChanged += ResultsTree_SelectedItemChanged;
        _window.ResultsTree.AddHandler(TreeViewItem.ExpandedEvent, new RoutedEventHandler(ResultsTree_ItemExpanded),
            true);
        _window.ResultsTree.PreviewMouseRightButtonDown += ResultsTree_PreviewMouseRightButtonDown;
        _window.ResultsTree.PreviewMouseLeftButtonDown += ResultsTree_PreviewMouseLeftButtonDown;
        _window.ResultsTree.PreviewMouseMove += ResultsTree_PreviewMouseMove;
        _window.FrozenRootTree.SelectedItemChanged += ResultsTree_SelectedItemChanged;
        _window.FrozenRootTree.AddHandler(TreeViewItem.ExpandedEvent, new RoutedEventHandler(ResultsTree_ItemExpanded),
            true);
        _window.FrozenRootTree.PreviewMouseRightButtonDown += ResultsTree_PreviewMouseRightButtonDown;
        _window.FrozenRootTree.PreviewMouseLeftButtonDown += ResultsTree_PreviewMouseLeftButtonDown;
        _window.FrozenRootTree.PreviewMouseMove += ResultsTree_PreviewMouseMove;
        _window.FrozenRootTree.AddHandler(FrameworkElement.ContextMenuOpeningEvent,
            new ContextMenuEventHandler(ResultsTree_ContextMenuOpening), true);
        FolderMatch.CheckedFolderIcon = DifferencerStatusIcons.GetCustomIcon(101);
        _window.ResultsTree.AddHandler(FrameworkElement.ContextMenuOpeningEvent,
            new ContextMenuEventHandler(ResultsTree_ContextMenuOpening), true);
        var treeMenu = _window.Resources["FolderTreeContextMenu"] as ContextMenu;
        if (treeMenu != null)
        {
            var buildMenu = FindTaggedMenuItem(treeMenu, "dim-msbuild-build");
            var rebuildMenu = FindTaggedMenuItem(treeMenu, "dim-msbuild-rebuild");
            if (buildMenu != null) buildMenu.ItemsSource = _msBuildMenu.BuildItems;
            if (rebuildMenu != null) rebuildMenu.ItemsSource = _msBuildMenu.RebuildItems;
            var sdkBuildMenu = FindTaggedMenuItem(treeMenu, "dim-sdk-build");
            var sdkRebuildMenu = FindTaggedMenuItem(treeMenu, "dim-sdk-rebuild");
            if (sdkBuildMenu != null) sdkBuildMenu.ItemsSource = _msBuildMenu.SdkBuildItems;
            if (sdkRebuildMenu != null) sdkRebuildMenu.ItemsSource = _msBuildMenu.SdkRebuildItems;
        }

        _window.FileList.SelectionChanged += FileList_SelectionChanged;
        _window.FileList.PreviewMouseLeftButtonDown += FileList_PreviewMouseLeftButtonDown;
        _window.FileList.PreviewMouseMove += FileList_PreviewMouseMove;
        _window.FileIconList.PreviewMouseLeftButtonDown += FileList_PreviewMouseLeftButtonDown;
        _window.FileIconList.PreviewMouseMove += FileList_PreviewMouseMove;
        InitializeFileContextMenu(_window.FileList);
        _window.FileList.ContextMenuOpening += FileList_ContextMenuOpening;
        _window.FileIconList.SelectionChanged += FileList_SelectionChanged;
        InitializeFileContextMenu(_window.FileIconList);
        _window.FileIconList.ContextMenuOpening += FileList_ContextMenuOpening;
        _window.QueryBox.TextChanged += QueryBox_TextChanged;
        _window.TreeTabs.SelectionChanged += TreeTabs_SelectionChanged;
        _window.LanguageBox.SelectionChanged += LanguageBox_SelectionChanged;
        _window.ThemeBox.SelectionChanged += ThemeBox_SelectionChanged;
        _window.RunScriptButton.Click += (sender, args) => RunScriptCommand();
        _window.BrowseScriptButton.Click += (sender, args) => BrowseScriptCommand();
        _window.ScriptBox.PreviewKeyDown += ScriptBox_PreviewKeyDown;
        _window.RunScriptButton.ToolTip = ScriptText("Main_RunScript",
            "Run  \u2014  execute the command (%d folder / %f file / %n name)");
        _window.BrowseScriptButton.ToolTip =
            ScriptText("Main_BrowseScript", "Browse  \u2014  pick .bat / .cmd / .ps1 / .vbs");
        _window.ScriptBox.ToolTip = ScriptText("Main_ScriptTooltip",
            "Command. %d = folder, %f = file path, %n = file name. Quote as \"%d\" to wrap.");
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
        foreach (var path in paths)
        {
            if (string.IsNullOrWhiteSpace(path)) continue;
            var folder = Directory.Exists(path)
                ? path
                : File.Exists(path)
                    ? Path.GetDirectoryName(path)
                    : null;
            if (string.IsNullOrEmpty(folder) || !Directory.Exists(folder)) continue;
            var normalized = MainWindowViewModel.NormalizeFolderPath(folder);
            if (seen.Add(normalized))
                folders.Add(normalized);
        }

        return folders;
    }

    private void MainWindow_Drop(object sender, DragEventArgs e)
    {
        if (_internalDragOut)
        {
            e.Handled = true;
            return;
        }

        var folders = FoldersFromDrop(e.Data);
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
        catch (Exception error)
        {
            ViewModel.ShowError(Strings.App_Unhandled, error);
        }
    }

    private void ChooseRoot()
    {
        try
        {
            var selectedPath = Directory.Exists(ViewModel.RootPath)
                ? ViewModel.RootPath
                : Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            var result = NativeFolderPicker.Show(new WindowInteropHelper(_window).Handle, selectedPath,
                Strings.Main_SelectSearchFolder);
            if (!string.IsNullOrEmpty(result))
            {
                ViewModel.RootPath = result;
                RootBox.CommitHistory();
                SettingsService.SaveSearchRoot(result);
                ShowTextEnd(RootBox);
                StartSelectedRootAnalysis();
            }
        }
        catch (Exception ex)
        {
            ViewModel.ShowError(Strings.Main_FolderPickerFailed, ex);
        }
    }

    private void StartSelectedRootAnalysis()
    {
        _analyzeSelectedRoot = true;
        if (ViewModel.IsSearching)
        {
            ViewModel.CancelSearch();
            return;
        }

        if (!ViewModel.GitSearchCommand.CanExecute(null)) return;
        _analyzeSelectedRoot = false;
        _searchModeActive = false;
        _window.PhysicalTreeTab.IsEnabled = true;
        _window.SolutionTreeTab.IsEnabled = true;
        ViewModel.GitSearchCommand.Execute(null);
    }

    private void MainSearchKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Handled || Keyboard.Modifiers != ModifierKeys.None || QueryBox.IsHistoryOpen) return;
        if (e.Key == Key.Enter && QueryBox.IsKeyboardFocusWithin)
        {
            QueryBox.GetBindingExpression(TextBox.TextProperty)?.UpdateSource();
            if (ViewModel.SearchCommand.CanExecute(null)) ViewModel.SearchCommand.Execute(null);
            e.Handled = true;
            return;
        }

        if (ViewModel.IsSearching || (e.Key != Key.Up && e.Key != Key.Down)) return;
        if (!QueryBox.IsKeyboardFocusWithin && !FileList.IsKeyboardFocusWithin
                                            && !FileIconList.IsKeyboardFocusWithin && !ResultsTree.IsKeyboardFocusWithin
                                            && !_window.FrozenRootTree.IsKeyboardFocusWithin) return;
        ICommand command = e.Key == Key.Up ? ViewModel.PrevSearchMatchCommand : ViewModel.NextSearchMatchCommand;
        if (!command.CanExecute(null)) return;
        command.Execute(null);
        e.Handled = true;
    }

    private void SwitchWindowShortcut(object sender, PreProcessInputEventArgs e)
    {
        if (!(e.StagingItem.Input is KeyEventArgs key) || key.RoutedEvent != Keyboard.PreviewKeyDownEvent
                                                       || key.Handled) return;
        var active = Application.Current.Windows.Cast<Window>().FirstOrDefault(window => window.IsActive);
        if (active == null) return;
        var viewers = Application.Current.Windows.OfType<DiffViewWindow>().Where(window => window.IsVisible).ToList();
        DiffViewWindow selectedViewer = null;
        int destination;
        if (key.Key == Key.Space && Keyboard.Modifiers == ModifierKeys.Control)
        {
            // Keep DIFF VIEW in the cycle, even when owned by the differencer.
            while (active.Owner != null && active != _window
                                        && active != _differencerWindow && active != _grepWindow &&
                                        active is not DiffViewWindow)
                active = active.Owner;
            var viewerIndex = viewers.IndexOf(active as DiffViewWindow);
            if (viewerIndex >= 0)
            {
                selectedViewer = viewers.Skip(viewerIndex + 1).FirstOrDefault();
                destination = selectedViewer == null ? 1 : 4;
            }
            else if (active == _grepWindow)
            {
                selectedViewer = viewers.FirstOrDefault();
                destination = selectedViewer == null ? 1 : 4;
            }
            else
            {
                destination = active == _differencerWindow ? 3 : 2;
            }
        }
        else if (Keyboard.Modifiers == ModifierKeys.Control)
        {
            switch (key.Key)
            {
                case Key.D1:
                case Key.NumPad1: destination = 1; break;
                case Key.D2:
                case Key.NumPad2: destination = 2; break;
                case Key.D3:
                case Key.NumPad3: destination = 3; break;
                case Key.D4:
                case Key.NumPad4:
                    destination = 4;
                    selectedViewer = viewers.FirstOrDefault(window => window.Owner == null);
                    break;

                default: return;
            }
        }
        else
        {
            return;
        }

        key.Handled = true;
        e.Cancel();
        if (key.IsRepeat) return;
        Dispatcher.BeginInvoke(new Action(() =>
        {
            try
            {
                if (destination == 1)
                {
                    WindowActivationService.BringToFront(_window, true);
                }
                else if (destination == 2)
                {
                    if (_differencerWindow == null) DeveloperDifferencer();
                    else WindowActivationService.BringToFront(_differencerWindow, true);
                }
                else if (destination == 4)
                {
                    if (selectedViewer == null)
                    {
                        var launch = ExternalDiffLaunch.Parse(new[] { "-diff" });
                        var snapshot = launch.CreateSnapshot();
                        selectedViewer =
                            new DiffViewWindow(snapshot, snapshot.Files[0], launch.SourcePath, launch.TargetPath)
                            {
                                Width = Math.Max(900, _window.ActualWidth),
                                Height = Math.Max(600, _window.ActualHeight)
                            };
                        selectedViewer.Show();
                    }

                    WindowActivationService.BringToFront(selectedViewer, true);
                }
                else if (_grepWindow != null)
                {
                    WindowActivationService.BringToFront(_grepWindow, true);
                }
                else
                {
                    OpenGrep(ViewModel.PrepareGrepScopes());
                }
            }
            catch (Exception ex)
            {
                ViewModel.ShowError(Strings.App_Unhandled, ex);
            }
        }));
    }

    private void ChooseIconLibrary()
    {
        try
        {
            var dialog = new OpenFileDialog { Filter = Strings.Main_IconFilter, CheckFileExists = true };
            var currentPath = ViewModel.IconLibraryPath.Trim();
            if (File.Exists(currentPath))
            {
                dialog.InitialDirectory = Path.GetDirectoryName(currentPath);
                dialog.FileName = Path.GetFileName(currentPath);
            }

            if (dialog.ShowDialog(_window) != true) return;
            ViewModel.IconLibraryPath = dialog.FileName;
            IconPathBox.CommitHistory();
            SettingsService.SaveIconLibraryPath(dialog.FileName);
            ShowTextEnd(IconPathBox);
            ViewModel.ClearSelectedIcon();
            RefreshSelectedIconPreview();
            ViewModel.Status = Path.GetFileName(dialog.FileName) + Strings.Main_SelectedSuffix;
        }
        catch (Exception ex)
        {
            ViewModel.ShowError(Strings.Main_LibrarySelectFailed, ex);
        }
    }

    private void ChooseIcon()
    {
        try
        {
            var iconPath = ViewModel.IconLibraryPath.Trim();
            if (!File.Exists(iconPath))
            {
                MessageBox.Show(Strings.Main_ChooseLibraryFirst, Title);
                return;
            }

            ViewModel.Status = Strings.Main_LoadingIcons;
            var groups = IconResourceReader.Read(iconPath);
            if (groups.Count == 0)
            {
                MessageBox.Show(Strings.Main_NoIconGroups, Title);
                return;
            }

            var browser = new IconGroupBrowserWindow(iconPath, groups, ViewModel.SelectedIconIndex) { Owner = _window };
            if (browser.ShowDialog() == true && browser.SelectedGroup != null)
            {
                ViewModel.ApplySelectedIcon(browser.SelectedGroup.ShellIndex, browser.SelectedGroup.Preview);
                ViewModel.Status = string.Format(Strings.Main_IconNSelected, browser.SelectedGroup.ShellIndex);
            }
            else
            {
                ViewModel.Status = Strings.Main_IconCancelled;
            }
        }
        catch (Exception ex)
        {
            ViewModel.ShowError(Strings.Main_IconBrowserFailed, ex);
        }
    }

    private void RefreshSelectedIconPreview()
    {
        try
        {
            var iconPath = ViewModel.IconLibraryPath.Trim();
            if (!File.Exists(iconPath)) return;
            var group = IconResourceReader.Read(iconPath)
                .FirstOrDefault(item => item.ShellIndex == ViewModel.SelectedIconIndex);
            if (group == null) ViewModel.ClearSelectedIcon();
            else ViewModel.ApplySelectedIcon(group.ShellIndex, group.Preview);
        }
        catch
        {
            ViewModel.ClearSelectedIcon();
            ViewModel.SelectedIconLabel = Strings.Main_PreviewUnavailable;
        }
    }

    private void UseAsSearchLocation(FolderMatch folder)
    {
        var path = MainWindowViewModel.ExistingFolderPath(folder?.Path);
        if (path == null)
        {
            MessageBox.Show(Strings.Main_LocationMissing, Title);
            return;
        }

        ViewModel.RootPath = path;
        RootBox.CommitHistory();
        SettingsService.SaveSearchRoot(path);
        ShowTextEnd(RootBox);
        StartSelectedRootAnalysis();
    }

    private void OpenExplorer(FolderMatch folder)
    {
        if (folder == null || !Directory.Exists(folder.Path)) return;
        try
        {
            Process.Start(new ProcessStartInfo("explorer.exe", "\"" + folder.Path + "\"") { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            ViewModel.ShowError(Strings.Main_ExplorerFailed, ex);
        }
    }

    private void FileListToEditor()
    {
        try
        {
            var list = FileList.Visibility == Visibility.Visible
                ? (ItemsControl)FileList
                : FileIconList;
            var text = new StringBuilder();
            foreach (FileListItem file in list.Items)
                text.AppendLine(file.Name);

            var outputPath = Path.Combine(Path.GetTempPath(), "DesktopIniManager-" + AppSlot.Name + "-file-list.txt");
            File.WriteAllText(outputPath, text.ToString(), new UTF8Encoding(true));
            OpenTextInEditor(outputPath);
            ViewModel.Status =
                string.Format(StringOverlay.Get("Main_TreeOpened"), "File List", ViewModel.FilePanelPath);
        }
        catch (Exception ex)
        {
            ViewModel.ShowError(string.Format(StringOverlay.Get("Main_TreeFailed"), "File List"), ex);
        }
    }

    private void TreeToEditor(FolderMatch folder, bool includeFiles)
    {
        if (folder == null) return;

        var label = includeFiles ? "Tree /F" : "Tree";
        var outputPath = Path.Combine(
            Path.GetTempPath(),
            "DesktopIniManager-" + AppSlot.Name + (includeFiles ? "-tree-f.txt" : "-tree.txt"));

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
        var files = includeFiles
            ? ViewModel.ContainedFileNames(folder)
            : Array.Empty<string>();
        var dirs = new List<FolderMatch>();
        foreach (var child in folder.Children)
        {
            if (child.IsHidden || child.IsFilterHidden) continue;
            dirs.Add(child);
        }

        for (var i = 0; i < files.Count; i++)
        {
            text.Append(prefix);
            text.Append(dirs.Count > 0 ? "\u2502  " : "    ");
            text.AppendLine(files[i]);
        }

        if (files.Count > 0 && dirs.Count > 0)
        {
            text.Append(prefix);
            text.AppendLine("\u2502");
        }

        for (var i = 0; i < dirs.Count; i++)
        {
            var last = i == dirs.Count - 1;
            text.Append(prefix);
            text.Append(last ? "\u2514\u2500" : "\u251C\u2500");
            text.AppendLine(dirs[i].Name);
            WriteContainedLevel(text, dirs[i], prefix + (last ? "    " : "\u2502   "), includeFiles);
        }
    }

    private static void OpenTextInEditor(string path)
    {
        var editor = GrepWindowViewModel.ExpandEditorPath(SettingsService.LoadEditorPath());
        if (string.IsNullOrWhiteSpace(editor))
            editor = "code";
        var arguments = (SettingsService.LoadEditorArguments() ?? string.Empty)
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
        foreach (var item in ViewModel.CurrentItems()) item.IsSelected = ReferenceEquals(item, folder);
        OpenGrep(new[] { folder.Path });
    }

    private void OpenAssociatedFile(FileListItem file)
    {
        if (file == null) return;
        try
        {
            Process.Start(new ProcessStartInfo(file.Path) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            ViewModel.ShowError(Strings.Main_OpenFileFailed, ex);
        }
    }

    private void InitializeFileContextMenu(Selector list)
    {
        list.PreviewMouseRightButtonDown += (sender, args) =>
        {
            _fileContextItem = FileFromElement(args.OriginalSource as DependencyObject);
            // Preserve all selected rows, including when modifiers are held.
            if (_fileContextItem != null && IsFileSelected(list, _fileContextItem))
                args.Handled = true;
        };
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
            menu.Items.Add(new MenuItem { Header = "SELECT Compare" });
            menu.Items.Add(new Separator());
            var explorer = new MenuItem { Header = "Explorerメニュー" };
            var shellMenuPending = false;
            explorer.Click += (s, args) =>
            {
                if (shellMenuPending || !explorer.IsEnabled) return;
                var paths = explorer.Tag as string[];
                if (paths == null || paths.Length == 0) return;
                var position = explorer.PointToScreen(new Point(explorer.ActualWidth, 0));
                shellMenuPending = true;
                menu.IsOpen = false;
                // Let WPF release its popup capture before entering the native menu loop.
                Dispatcher.BeginInvoke(new Action(() =>
                {
                    try
                    {
                        ExplorerContextMenu.Show(_window, paths, position);
                    }
                    catch (Exception ex)
                    {
                        ViewModel.ShowError("Explorerメニューを開けませんでした。", ex);
                    }
                    finally
                    {
                        shellMenuPending = false;
                    }
                }), DispatcherPriority.Input);
            };
            menu.Items.Add(explorer);
            list.ContextMenu = menu;
        }
    }

    private void FileList_ContextMenuOpening(object sender, ContextMenuEventArgs e)
    {
        var list = sender as Selector;
        if (list == null) return;
        var file = list.SelectedItem as FileListItem;
        var canRun = file != null && ScriptExtensions.Contains(file.Extension ?? string.Empty);

        if (list.ContextMenu.Items.Count > 0 && list.ContextMenu.Items[0] is MenuItem item)
        {
            item.Header = StringOverlay.Get("Main_RunScript");
            item.IsEnabled = canRun;
        }

        var compare = (MenuItem)list.ContextMenu.Items[1];
        compare.Items.Clear();
        var source = e.CursorLeft < 0 ? file : _fileContextItem;
        if (source != null && list.Items.Contains(source))
        {
            var visibleFiles = list.Items.Cast<FileListItem>().ToList();
            var selected = visibleFiles
                .Where(candidate => IsFileSelected(list, candidate)).ToList();
            // A search hit compares with all other hits, regardless of selection.
            // Files merely shown alongside the hits are not search results.
            var candidates = source.IsSearchMatch
                ? visibleFiles.Where(candidate => candidate.IsSearchMatch).ToList()
                : selected.Count > 1 && selected.Contains(source)
                    ? selected
                    : new List<FileListItem>();
            foreach (var target in candidates.Where(candidate =>
                         !string.Equals(candidate.Path, source.Path, StringComparison.OrdinalIgnoreCase)))
            {
                var duplicateName = candidates.Count(candidate =>
                    string.Equals(candidate.Name, target.Name, StringComparison.OrdinalIgnoreCase)) > 1;
                var choice = new MenuItem
                {
                    Header = new TextBlock { Text = duplicateName ? target.Path : target.Name },
                    ToolTip = target.Path
                };
                choice.Click += (s, args) => OpenFileComparison(source, target);
                compare.Items.Add(choice);
            }
        }

        compare.Visibility = compare.Items.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        var explorer = (MenuItem)list.ContextMenu.Items[3];
        var shellFiles = source == null
            ? new List<FileListItem>()
            : IsFileSelected(list, source)
                ? list.Items.Cast<FileListItem>().Where(candidate => IsFileSelected(list, candidate)).ToList()
                : new List<FileListItem> { source };
        explorer.Tag = shellFiles.Select(candidate => candidate.Path)
            .Where(path => !string.IsNullOrWhiteSpace(path)).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        var shellPaths = (string[])explorer.Tag;
        var sameFolder = shellPaths.Select(path => Path.GetDirectoryName(path))
            .Distinct(StringComparer.OrdinalIgnoreCase).Count() <= 1;
        explorer.IsEnabled = shellPaths.Length > 0 && sameFolder;
        explorer.ToolTip = sameFolder ? null : "同じフォルダー内のファイルを選択してください。";
        ToolTipService.SetShowOnDisabled(explorer, true);
    }

    private void OpenFileComparison(FileListItem source, FileListItem target)
    {
        try
        {
            foreach (var path in new[] { source.Path, target.Path })
                if (!File.Exists(path))
                    throw new FileNotFoundException(null, path);
            var file = new DiffFile
            {
                RelativePath = source.Name,
                Source = DiffStamp.Read(source.Path),
                Target = DiffStamp.Read(target.Path)
            };
            var snapshot = new DiffSnapshot
            {
                SourceRoot = Path.GetDirectoryName(source.Path),
                TargetRoot = Path.GetDirectoryName(target.Path),
                Files = new List<DiffFile> { file }
            };
            var diff = new DiffViewWindow(snapshot, file, source.Path, target.Path) { Owner = _window };
            diff.MatchOwnerSize();
            diff.Show();
        }
        catch (Exception ex)
        {
            ViewModel.ShowError(Strings.Main_OpenFileFailed, ex);
        }
    }

    private void RunSelectedScript(FileListItem file)
    {
        if (file == null || !File.Exists(file.Path)) return;

        var workDir = Path.GetDirectoryName(file.Path);
        if (string.IsNullOrEmpty(workDir) || !Directory.Exists(workDir))
        {
            ViewModel.ShowError(StringOverlay.Get("Main_RunScriptFailed"), new DirectoryNotFoundException(workDir));
            return;
        }

        var ext = file.Extension ?? string.Empty;
        string command;
        if (ext.Equals(".ps1", StringComparison.OrdinalIgnoreCase))
            command = "\"" + file.Path + "\"";
        else if (ext.Equals(".bat", StringComparison.OrdinalIgnoreCase)
                 || ext.Equals(".cmd", StringComparison.OrdinalIgnoreCase))
            command = "\"" + file.Path + "\"";
        else if (ext.Equals(".vbs", StringComparison.OrdinalIgnoreCase))
            command = "cscript.exe //nologo \"" + file.Path + "\"";
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
        var value = StringOverlay.Get(key);
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
            var current = (ScriptBox.Text ?? string.Empty).Trim();
            var first = FirstToken(current);
            if (File.Exists(first))
            {
                dialog.InitialDirectory = Path.GetDirectoryName(first);
                dialog.FileName = Path.GetFileName(first);
            }

            if (dialog.ShowDialog(_window) != true) return;
            var quoted = QuoteIfNeeded(dialog.FileName);
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
        var template = (ScriptBox.Text ?? string.Empty).Trim();
        if (string.IsNullOrEmpty(template))
        {
            ViewModel.Status = StringOverlay.Get("Main_ScriptEmpty");
            return;
        }

        var dir = ViewModel.FilePanelPath;
        if (string.IsNullOrWhiteSpace(dir))
            dir = ((ResultsTree.SelectedItem ?? _window.FrozenRootTree.SelectedItem) as FolderMatch)?.Path;
        var selectedFile = FileList.SelectedItem as FileListItem
                           ?? FileIconList.SelectedItem as FileListItem;
        var file = selectedFile?.Path;

        var needsDir = ContainsToken(template, "d");
        var needsFile = ContainsToken(template, "f") || ContainsToken(template, "n");
        if (needsDir && (string.IsNullOrWhiteSpace(dir) || !Directory.Exists(dir)))
        {
            ViewModel.ShowError(StringOverlay.Get("Main_ScriptMissingDir"),
                new DirectoryNotFoundException(dir ?? string.Empty));
            return;
        }

        if (needsFile && (string.IsNullOrWhiteSpace(file) || !File.Exists(file)))
        {
            ViewModel.ShowError(StringOverlay.Get("Main_ScriptMissingFile"),
                new FileNotFoundException(file ?? string.Empty));
            return;
        }

        var command = ExpandScriptTokens(template, dir, file);
        var answer = new UserDialogService(_window).Show(
            command + "\n\n" + StringOverlay.Get("Main_ScriptConfirmAsk"),
            StringOverlay.Get("Main_RunScript"),
            MessageBoxButton.OKCancel,
            MessageBoxImage.Question);
        if (answer != MessageBoxResult.OK) return;

        var workDir = dir;
        if (string.IsNullOrEmpty(workDir) || !Directory.Exists(workDir))
        {
            var exe = FirstToken(command);
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
        var folder = string.IsNullOrEmpty(workDir) ? Environment.CurrentDirectory : workDir;
        var exe = FirstToken(command);
        var args = RestAfterFirstToken(command);
        var powershell = Path.Combine(
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
        var text = command.Trim();
        if (text[0] == '"')
        {
            var end = text.IndexOf('"', 1);
            return end < 0 ? string.Empty : text.Substring(end + 1).TrimStart();
        }

        var space = text.IndexOfAny(new[] { ' ', '\t' });
        return space < 0 ? string.Empty : text.Substring(space + 1).TrimStart();
    }

    private static bool ContainsToken(string template, string name)
    {
        if (string.IsNullOrEmpty(template)) return false;
        return template.IndexOf("%" + name, StringComparison.OrdinalIgnoreCase) >= 0;
    }

    private static string ExpandScriptTokens(string template, string dir, string file)
    {
        var quotedDir = string.IsNullOrEmpty(dir) ? "\"%d\"" : "\"" + dir + "\"";
        var quotedFile = string.IsNullOrEmpty(file) ? "\"%f\"" : "\"" + file + "\"";
        var name = string.IsNullOrEmpty(file) ? null : Path.GetFileName(file);
        var quotedName = string.IsNullOrEmpty(name) ? "\"%n\"" : "\"" + name + "\"";
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
        var text = command.Trim();
        if (text[0] == '"')
        {
            var end = text.IndexOf('"', 1);
            return end > 0 ? text.Substring(1, end - 1) : text.Trim('"');
        }

        var space = text.IndexOfAny(new[] { ' ', '\t' });
        return space < 0 ? text : text.Substring(0, space);
    }

    private static string QuoteIfNeeded(string path)
    {
        if (string.IsNullOrEmpty(path)) return path;
        if (path.IndexOfAny(new[] { ' ', '\t' }) >= 0 && !(path.StartsWith("\"") && path.EndsWith("\"")))
            return "\"" + path + "\"";
        return path;
    }

    private async void OnClosing(object sender, CancelEventArgs e)
    {
        if (_differencerWindow != null && !differencerSavedOnClose)
        {
            e.Cancel = true;
            if (_differencerWindow.IsWorking || savingDifferencerOnClose) return;
            savingDifferencerOnClose = true;
            try
            {
                if (await _differencerWindow.PrepareCloseAsync())
                {
                    differencerSavedOnClose = true;
                    _ = _window.Dispatcher.BeginInvoke(new Action(() => _window.Close()));
                }
            }
            finally
            {
                savingDifferencerOnClose = false;
            }

            return;
        }

        if (!e.Cancel) ViewModel.SaveFolderTrees();
    }

    private void Close()
    {
        _window.Close();
    }

    private void OnClosed(object sender, EventArgs e)
    {
        InputManager.Current.PreProcessInput -= SwitchWindowShortcut;
        FolderMatch.SelectionChanged -= ViewModel.RefreshScopedLabel;
        _filterTimer.Stop();
        ViewModel.CancelOperations();
        try
        {
            _grepWindow?.Close();
        }
        catch
        {
        }

        try
        {
            _differencerWindow?.Close();
        }
        catch
        {
        }

        SettingsService.SaveIconLibraryPath(ViewModel.IconLibraryPath);
        SettingsService.SaveSearchQuery(ViewModel.Query);
        SettingsService.SaveSearchRoot(ViewModel.RootPath);
        SaveMainWindowPlacement();
    }

    private void InitLanguageBox()
    {
        var flags = LanguageIconService.LoadFlagIcons();
        var items = new[]
        {
            new LanguageChoice("en", flags[0], "English"),
            new LanguageChoice("ja", flags[1], "Japanese"),
            new LanguageChoice("zh-Hans", flags[2], "Chinese"),
            new LanguageChoice("ko", flags[3], "Korean")
        };
        LanguageBox.ItemsSource = items;
        var current = StringOverlay.ResolveCulture().Name;
        var selected = items[0];
        foreach (var item in items)
            if (string.Equals(item.Code, current, StringComparison.OrdinalIgnoreCase) ||
                (item.Code == "zh-Hans" && current.StartsWith("zh", StringComparison.OrdinalIgnoreCase)) ||
                (item.Code != "zh-Hans" && current.StartsWith(item.Code, StringComparison.OrdinalIgnoreCase)))
            {
                selected = item;
                break;
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
        var language = StringOverlay.ResolveCulture().TwoLetterISOLanguageName;
        string tip;
        if (language == "ja") tip = "設定・履歴・フォルダー一覧を初期化します";
        else if (language == "zh") tip = "清除设置、历史和文件夹列表";
        else if (language == "ko") tip = "설정, 기록, 폴더 목록을 초기화합니다";
        else tip = "Clear settings, history, and folder lists";
        ResetButton.ToolTip = tip;
    }

    private void Reset()
    {
        var language = StringOverlay.ResolveCulture().TwoLetterISOLanguageName;
        var title = language == "ja" ? "リセット" : language == "zh" ? "重置" : language == "ko" ? "초기화" : "Reset";
        var message = language == "ja"
            ? "設定、入力履歴、フォルダー一覧、GREP と差分比較の状態、エディターとアイコンの選択を初期化します。よろしいですか。"
            : language == "zh"
                ? "将清除设置、输入历史、文件夹列表、GREP 和差异比较器状态以及编辑器和图标选择。确定吗？"
                : language == "ko"
                    ? "설정, 입력 기록, 폴더 목록, GREP 및 차이 비교 상태, 편집기와 아이콘 선택을 초기화합니다. 계속할까요?"
                    : "This clears settings, input history, folder lists, GREP and differencer state, and editor and icon selections. Continue?";
        if (MessageBox.Show(_window, message, title, MessageBoxButton.OKCancel, MessageBoxImage.Warning) !=
            MessageBoxResult.OK)
            return;

        ViewModel.CancelOperations();
        _grepWindow?.Close();
        _differencerWindow?.Close();
        foreach (var window in Application.Current.Windows.Cast<Window>().ToArray())
            if (!ReferenceEquals(window, _window))
                try
                {
                    window.Close();
                }
                catch
                {
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
        ThemeService.Apply("Light");
        SelectThemeBox("Light");
        SettingsService.SaveTheme("Light");
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

    private void CompactTree()
    {
        ApplyTreeDensity(true, true);
    }

    private void ComfortableTree()
    {
        ApplyTreeDensity(false, true);
    }

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
            _window.TreeDensityIcon.Source = DifferencerStatusIcons.GetCustomIcon(ViewModel.TreeCompact ? 91 : 90);
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
        SetToolbarIcon(_window.PrevSearchMatchIcon, 126);
        SetToolbarIcon(_window.NextSearchMatchIcon, 127);
        SetToolbarIcon(_window.ChooseIconLibraryIcon, 61);
        SetToolbarIcon(_window.ChooseIconButtonIcon, 87);
        SetToolbarIcon(_window.CloseWindowIcon, 26);
        // Theme selection is displayed by ThemeBox.
    }

    private static void SetToolbarIcon(Image image, int index)
    {
        if (image != null)
            image.Source = DifferencerStatusIcons.GetCustomIcon(index);
    }

    private void NodeExpandToggle_Changed(object sender, RoutedEventArgs e)
    {
        if (_updatingToggles) return;

        var expanded = NodeExpandToggle.IsChecked == true;

        foreach (var folder in ViewModel.ScrollingTreeItems)
            SetTreeExpanded(folder, expanded);

        NodeExpandToggle.ToolTip =
            expanded ? Strings.Common_Collapse : Strings.Common_Expand;

        UpdateNodeExpandIcon();
    }

    private static void SetTreeExpanded(FolderMatch folder, bool expanded)
    {
        if (folder == null)
            return;

        folder.IsExpanded = expanded;

        foreach (var child in folder.Children)
            SetTreeExpanded(child, expanded);
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
        var expanded = NodeExpandToggle != null && NodeExpandToggle.IsChecked == true;
        _window.NodeExpandIcon.Source = DifferencerStatusIcons.GetCustomIcon(expanded ? 56 : 55);
    }

    private void FileListView()
    {
        FileList.Visibility = Visibility.Visible;
        FileIconList.Visibility = Visibility.Collapsed;
        HighlightFileViewButtons(true, false);
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
        FileIconList.ItemTemplate =
            (DataTemplate)FindResource(large ? "FileIconLargeTemplate" : "FileIconSmallTemplate");
        HighlightFileViewButtons(false, large);
        FileIconList.UpdateLayout();
        await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
        ViewModel.IsFileBusy = false;
    }

    private void HighlightFileViewButtons(bool list, bool large)
    {
        var selected = (Brush)FindResource("ThemeSelected");
        var secondary = (Brush)FindResource("Secondary");
        if (FileListViewButton != null) FileListViewButton.Background = list ? selected : secondary;
        if (FileIconLargeButton != null) FileIconLargeButton.Background = !list && large ? selected : secondary;
        if (FileIconSmallButton != null) FileIconSmallButton.Background = !list && !large ? selected : secondary;
    }

    private void FolderFilter_TextChanged(object sender, TextChangedEventArgs e)
    {
        _filterTimer?.Stop();
        _filterTimer?.Start();
    }

    private void ClearFolderFilter()
    {
        ViewModel.FolderFilter = string.Empty;
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
        }), DispatcherPriority.Loaded);
    }

    private void SetSearching(bool value)
    {
        if (value)
        {
            Dispatcher.BeginInvoke(new Action(() => AnimateSearchProgress(true)), DispatcherPriority.Loaded);
        }
        else
        {
            AnimateSearchProgress(false);
            SetTreePanelBusy(false);
            if (_analyzeSelectedRoot) Dispatcher.BeginInvoke(new Action(StartSelectedRootAnalysis));
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
        if (!value)
        {
            transform.BeginAnimation(TranslateTransform.XProperty, null);
            transform.X = 0;
            return;
        }

        var distance = Math.Max(0, SearchProgress.ActualWidth - (marquee.ActualWidth > 0 ? marquee.ActualWidth : 150));
        var animation = new DoubleAnimation(0, distance, TimeSpan.FromSeconds(1.2))
        { AutoReverse = true, RepeatBehavior = RepeatBehavior.Forever };
        transform.BeginAnimation(TranslateTransform.XProperty, animation, HandoffBehavior.SnapshotAndReplace);
    }

    private void SetTreePanelBusy(bool busy)
    {
        if (TreePanelBusy != null)
            ViewModel.IsTreeBusy = busy;
    }

    private void SelectThemeBox(string theme)
    {
        var normalized = ThemeService.Normalize(theme);
        _themeBoxReady = false;
        foreach (ComboBoxItem item in ThemeBox.Items)
            if (string.Equals(item.Content as string, normalized, StringComparison.OrdinalIgnoreCase))
            {
                ThemeBox.SelectedItem = item;
                break;
            }

        _themeBoxReady = true;
    }

    private void ThemeBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_themeBoxReady) return;
        var item = ThemeBox.SelectedItem as ComboBoxItem;
        if (item == null) return;
        SetTheme(item.Content as string);
    }

    private void LightTheme()
    {
        SetTheme("Light");
    }

    private void DarkTheme()
    {
        SetTheme("Dark");
    }

    private void SetTheme(string theme)
    {
        var normalized = ThemeService.Normalize(theme);
        ThemeService.Apply(normalized);
        SelectThemeBox(normalized);
        SettingsService.SaveTheme(normalized);
        HighlightTreeDensityButtons();
        HighlightFileViewButtons(FileList.Visibility == Visibility.Visible, _largeFileIcons);
    }

    // --- MainWindowPresentationService.Tree.cs ---

    private void EnterSearchMode()
    {
        _searchModeActive = true;
        _window.SearchTreeTab.IsEnabled = true;
        _window.PhysicalTreeTab.IsEnabled = false;
        _window.SolutionTreeTab.IsEnabled = false;
    }

    private void QueryBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (!_searchModeActive || !string.IsNullOrWhiteSpace(QueryBox.Text)) return;

        ViewModel.ClearSearchSession();
        _window.SearchTreeTab.IsEnabled = true;
        _window.PhysicalTreeTab.IsEnabled = true;
        _window.SolutionTreeTab.IsEnabled = true;
    }

    private void TreeTabs_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_searchModeActive || !string.IsNullOrWhiteSpace(QueryBox.Text)) return;
        if (_window.TreeTabs.SelectedIndex != 0 && _window.TreeTabs.SelectedIndex != 1) return;

        _searchModeActive = false;
        _window.SearchTreeTab.IsEnabled = false;
        _window.PhysicalTreeTab.IsEnabled = true;
        _window.SolutionTreeTab.IsEnabled = true;
    }

    private void SelectSearchRootForFileList()
    {
        var root = ViewModel.SearchTabRoot;
        if (root == null) return;
        root.IsExpanded = true;
        Dispatcher.BeginInvoke(new Action(() =>
        {
            if (ViewModel.TreeViewIndex != 2 || ViewModel.SearchTabRoot == null) return;
            var current = ViewModel.SearchTabRoot;
            current.IsExpanded = true;
            current.IsCurrent = true;
        }), DispatcherPriority.Loaded);
    }

    private void ResultsTree_PreviewMouseRightButtonDown(object sender, MouseButtonEventArgs e)
    {
        PrepareMsBuildMenu(FindFolderFromElement(e.OriginalSource as DependencyObject));
    }

    private void ResultsTree_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        _treeDragFolder = FindFolderFromElement(e.OriginalSource as DependencyObject);
        _treeDragStart = e.GetPosition(null);
        if (FindAncestor<CheckBox>(e.OriginalSource as DependencyObject) != null)
            _treeDragFolder = null;
    }

    private async void ResultsTree_PreviewMouseMove(object sender, MouseEventArgs e)
    {
        if (_treeDragPreparing || e.LeftButton != MouseButtonState.Pressed || _treeDragFolder == null) return;
        if (!DropOutService.MovedEnough(_treeDragStart, e.GetPosition(null))) return;

        var folder = _treeDragFolder;
        _treeDragFolder = null;
        _treeDragPreparing = true;

        try
        {
            // Folder drag-out is a flat temporary package: the selected folder and
            // its immediate files only. Enumerating/copying a cold or remote folder
            // must never run on the WPF UI thread.
            var staged = await DropOutService.StageFolderWithImmediateFilesAsync(folder.Path);
            if (string.IsNullOrEmpty(staged)) return;

            _internalDragOut = true;
            try
            {
                DropOutService.DragExisting(_window.ResultsTree, new[] { staged });
            }
            finally
            {
                _internalDragOut = false;
            }
        }
        catch (Exception ex)
        {
            ViewModel.ShowError(Strings.App_Unhandled, ex);
        }
        finally
        {
            _treeDragPreparing = false;
        }
    }

    private void FileList_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        _fileDragItem = FileFromElement(e.OriginalSource as DependencyObject);
        _fileDragStart = e.GetPosition(null);
        if (e.ClickCount == 2 && _fileDragItem != null)
        {
            var file = _fileDragItem;
            _fileDragItem = null;
            e.Handled = true;
            OpenAssociatedFile(file);
            return;
        }

        // Keep a multi-selection when dragging from an already selected row.
        if (_fileDragItem != null
            && Keyboard.Modifiers == ModifierKeys.None
            && IsFileSelected(sender as ItemsControl, _fileDragItem))
            e.Handled = true;
    }

    private void FileList_PreviewMouseMove(object sender, MouseEventArgs e)
    {
        if (e.LeftButton != MouseButtonState.Pressed || _fileDragItem == null) return;
        if (!DropOutService.MovedEnough(_fileDragStart, e.GetPosition(null))) return;
        var file = _fileDragItem;
        _fileDragItem = null;
        var paths = new List<string>();
        var list = FileList.Visibility == Visibility.Visible ? (ItemsControl)FileList : FileIconList;
        var selected = new List<FileListItem>();
        foreach (FileListItem item in list.Items)
            if (IsFileSelected(list, item))
                selected.Add(item);
        if (selected.Count > 1 &&
            selected.Exists(item => string.Equals(item.Path, file.Path, StringComparison.OrdinalIgnoreCase)))
            foreach (var item in selected)
                paths.Add(item.Path);
        else
            paths.Add(file.Path);
        _internalDragOut = true;
        try
        {
            DropOutService.DragExisting(list, paths);
        }
        finally
        {
            _internalDragOut = false;
        }
    }

    private static bool IsFileSelected(ItemsControl list, FileListItem item)
    {
        var box = list as ListBox;
        if (box != null)
        {
            foreach (var selected in box.SelectedItems)
                if (ReferenceEquals(selected, item))
                    return true;
            return false;
        }

        return ReferenceEquals((list as Selector)?.SelectedItem, item);
    }

    private static FileListItem FileFromElement(DependencyObject source)
    {
        for (var current = source;
             current != null;
             current = current is ContentElement content
                 ? ContentOperations.GetParent(content) ?? (content as FrameworkContentElement)?.Parent
                 : VisualTreeHelper.GetParent(current))
        {
            var element = current as FrameworkElement;
            if (element?.DataContext is FileListItem file)
                return file;
        }

        return null;
    }

    private static T FindAncestor<T>(DependencyObject current) where T : DependencyObject
    {
        while (current != null)
        {
            var match = current as T;
            if (match != null) return match;
            current = VisualTreeHelper.GetParent(current);
        }

        return null;
    }

    private void ResultsTree_ContextMenuOpening(object sender, ContextMenuEventArgs e)
    {
        var source = e.OriginalSource as FrameworkElement;
        var folder = FindFolderFromElement(source);
        PrepareMsBuildMenu(folder);
        var menu = FindContextMenu(source);
        ApplyMsBuildMenuVisibility(menu);
    }

    private void PrepareMsBuildMenu(FolderMatch folder)
    {
        _msBuildMenu.BuildItems.Clear();
        _msBuildMenu.RebuildItems.Clear();
        _msBuildMenu.SdkBuildItems.Clear();
        _msBuildMenu.SdkRebuildItems.Clear();
        if (ViewModel.TreeViewIndex != 1 || folder == null) return;

        var solution = folder.FindSolutionRoot();
        var sln = solution?.SolutionFile;
        var configs = solution?.BuildConfigurations;
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

        foreach (var pair in configs)
        {
            var captured = pair;
            var file = sln;
            var glyph = ConfigIconGlyph(captured);
            _msBuildMenu.BuildItems.Add(new MsBuildActionItem
            {
                Header = captured,
                IconGlyph = glyph,
                Command = new RelayCommand(() => RunMsBuild(file, captured, "Build"))
            });
            _msBuildMenu.RebuildItems.Add(new MsBuildActionItem
            {
                Header = captured,
                IconGlyph = glyph,
                Command = new RelayCommand(() => RunMsBuild(file, captured, "Rebuild"))
            });
            _msBuildMenu.SdkBuildItems.Add(new MsBuildActionItem
            {
                Header = captured,
                IconGlyph = glyph,
                Command = new RelayCommand(() => RunDotNetBuild(file, captured, false))
            });
            _msBuildMenu.SdkRebuildItems.Add(new MsBuildActionItem
            {
                Header = captured,
                IconGlyph = glyph,
                Command = new RelayCommand(() => RunDotNetBuild(file, captured, true))
            });
        }
    }

    private void ApplyMsBuildMenuVisibility(ContextMenu menu)
    {
        if (menu == null) return;
        var canBuild = ViewModel.TreeViewIndex == 1 && _msBuildMenu.BuildItems.Count > 0;
        var show = ViewModel.TreeViewIndex != 1 || canBuild;
        var visibility = show ? Visibility.Visible : Visibility.Collapsed;
        var rootMenu = FindTaggedMenuItem(menu, "dim-build");
        if (rootMenu != null)
        {
            rootMenu.Visibility = visibility;
            rootMenu.IsEnabled = canBuild;
        }

        var buildMenu = FindTaggedMenuItem(menu, "dim-msbuild-build");
        var rebuildMenu = FindTaggedMenuItem(menu, "dim-msbuild-rebuild");
        var buildSeparator = FindTaggedSeparator(menu, "dim-msbuild");
        if (buildMenu != null)
        {
            buildMenu.Visibility = visibility;
            buildMenu.IsEnabled = canBuild;
        }

        if (rebuildMenu != null)
        {
            rebuildMenu.Visibility = visibility;
            rebuildMenu.IsEnabled = canBuild;
        }

        if (buildSeparator != null) buildSeparator.Visibility = visibility;
    }

    private static MenuItem FindTaggedMenuItem(ItemsControl menu, string tag)
    {
        foreach (var item in menu.Items)
        {
            var menuItem = item as MenuItem;
            if (menuItem != null && Equals(menuItem.Tag, tag))
                return menuItem;
            if (menuItem != null)
            {
                var nested = FindTaggedMenuItem(menuItem, tag);
                if (nested != null) return nested;
            }
        }

        return null;
    }

    private static Separator FindTaggedSeparator(ItemsControl menu, string tag)
    {
        foreach (var item in menu.Items)
        {
            var separator = item as Separator;
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
        if (ViewModel.TreeViewIndex != 1) return;
        if (string.IsNullOrWhiteSpace(solutionFile) || !File.Exists(solutionFile))
        {
            ViewModel.ShowError(ScriptText("Main_MsBuildMissingSln", "Solution file was not found."),
                new FileNotFoundException(solutionFile ?? string.Empty));
            return;
        }

        var configuration = configurationPair;
        var platform = "Any CPU";
        var bar = configurationPair.LastIndexOf('|');
        if (bar >= 0)
        {
            configuration = configurationPair.Substring(0, bar);
            platform = configurationPair.Substring(bar + 1);
        }

        var msbuild = FindMsBuild();
        if (string.IsNullOrEmpty(msbuild))
        {
            ViewModel.ShowError(ScriptText("Main_MsBuildNotFound", "MSBuild.exe was not found."),
                new FileNotFoundException("MSBuild.exe"));
            return;
        }

        var command = "\"" + msbuild + "\" \"" + solutionFile + "\" /t:" + target
                      + " /p:Configuration=\"" + configuration + "\" /p:Platform=\"" + platform + "\" /m";
        var workDir = Path.GetDirectoryName(solutionFile);
        try
        {
            StartKeepOpenConsole(workDir, command);
            ViewModel.Status = string.Format(ScriptText("Main_MsBuildStarted", "Started {0} {1}"), target,
                configurationPair);
        }
        catch (Exception ex)
        {
            ViewModel.ShowError(ScriptText("Main_MsBuildFailed", "MSBuild could not be started."), ex);
        }
    }

    private void RunDotNetBuild(string solutionFile, string configurationPair, bool rebuild)
    {
        try
        {
            var configuration = configurationPair;
            var platform = "Any CPU";
            var bar = configurationPair.LastIndexOf('|');
            if (bar >= 0)
            {
                configuration = configurationPair.Substring(0, bar);
                platform = configurationPair.Substring(bar + 1);
            }

            var command = "dotnet build \"" + solutionFile + "\" --configuration \"" + configuration
                          + "\" -p:Platform=\"" + platform + "\""
                          + (rebuild ? " --no-incremental -t:Rebuild" : string.Empty);
            StartKeepOpenConsole(Path.GetDirectoryName(solutionFile), command);
            ViewModel.Status = "Started .NET SDK " + (rebuild ? "Rebuild " : "Build ") + configurationPair;
        }
        catch (Exception ex)
        {
            ViewModel.ShowError(".NET SDK build could not be started.", ex);
        }
    }

    private static string FindMsBuild()
    {
        var vswhere = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
            "Microsoft Visual Studio", "Installer", "vswhere.exe");
        if (File.Exists(vswhere))
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
                using (var process = Process.Start(psi))
                {
                    if (process != null)
                    {
                        var path = (process.StandardOutput.ReadLine() ?? string.Empty).Trim();
                        process.WaitForExit(4000);
                        if (File.Exists(path)) return path;
                    }
                }
            }
            catch (Exception)
            {
            }

        string[] editions = { "Enterprise", "Professional", "Community", "BuildTools" };
        string[] years = { "2022", "2019", "2017" };
        string[] roots =
        {
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86)
        };
        foreach (var root in roots)
            foreach (var year in years)
                foreach (var edition in editions)
                {
                    string[] candidates =
                    {
                Path.Combine(root, "Microsoft Visual Studio", year, edition, "MSBuild", "Current", "Bin", "amd64",
                    "MSBuild.exe"),
                Path.Combine(root, "Microsoft Visual Studio", year, edition, "MSBuild", "Current", "Bin", "MSBuild.exe")
            };
                    foreach (var candidate in candidates)
                        if (File.Exists(candidate))
                            return candidate;
                }

        return null;
    }

    private static FolderMatch FindFolderFromElement(DependencyObject source)
    {
        for (var current = source;
             current != null;
             current = current is ContentElement content
                 ? ContentOperations.GetParent(content) ?? (content as FrameworkContentElement)?.Parent
                 : VisualTreeHelper.GetParent(current))
        {
            var element = current as FrameworkElement;
            if (element?.DataContext is FolderMatch folder)
                return folder;
        }

        return null;
    }

    private static ContextMenu FindContextMenu(DependencyObject source)
    {
        for (var current = source;
             current != null;
             current = current is ContentElement content
                 ? ContentOperations.GetParent(content) ?? (content as FrameworkContentElement)?.Parent
                 : VisualTreeHelper.GetParent(current))
        {
            var element = current as FrameworkElement;
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
        for (var i = menu.Items.Count - 1; i >= 0; i--)
        {
            var item = menu.Items[i] as FrameworkElement;
            if (item != null && Equals(item.Tag, tag))
                menu.Items.RemoveAt(i);
        }
    }

    private static string FindSolutionFile(string directory)
    {
        var current = directory;
        for (var depth = 0; depth < 8 && !string.IsNullOrWhiteSpace(current); depth++)
        {
            if (Directory.Exists(current))
                try
                {
                    var sln = Directory.EnumerateFiles(current, "*.sln").FirstOrDefault();
                    if (!string.IsNullOrEmpty(sln)) return sln;
                }
                catch (UnauthorizedAccessException)
                {
                }
                catch (IOException)
                {
                }

            current = Path.GetDirectoryName(current);
        }

        return null;
    }

    private async void ResultsTree_ItemExpanded(object sender, RoutedEventArgs e)
    {
        if (!ViewModel.IsLazyNetworkTreeActive)
            return;

        var item = e.OriginalSource as TreeViewItem;
        var folder = item?.DataContext as FolderMatch;
        if (folder == null || folder.IsLazyPlaceholder || folder.IsLazyLoaded || folder.IsLazyLoading)
            return;

        try
        {
            await ViewModel.LoadLazyNetworkChildrenAsync(folder);
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            ViewModel.ShowError(Strings.App_Unhandled, ex);
        }
    }

    private async void ResultsTree_SelectedItemChanged(object sender, RoutedPropertyChangedEventArgs<object> e)
    {
        if (_syncingTreeFromFile || !(e.NewValue is FolderMatch selected)) return;
        var other = ReferenceEquals(sender, ResultsTree) ? _window.FrozenRootTree : ResultsTree;
        if (other.SelectedItem is FolderMatch previous && !ReferenceEquals(previous, selected))
            previous.IsCurrent = false;
        try
        {
            await ViewModel.LoadFilesAsync(selected);
        }
        catch (Exception ex)
        {
            ViewModel.ShowError(Strings.App_Unhandled, ex);
        }
    }

    private void FileList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_syncingTreeFromFile) return;
        if (Mouse.RightButton == MouseButtonState.Pressed) return;
        var file = (sender as Selector)?.SelectedItem as FileListItem;
        if (file == null) return;
        ViewModel.NoteSelectedSearchMatch(file);

        // File-to-folder tracking belongs to the Search tree only.
        // Physical / Solution keep their own selection and expansion state.
        if (ViewModel.TreeViewIndex != 0 && ViewModel.TreeViewIndex != 2) return;
        RevealFolderInCurrentTree(file.Path);
    }

    private void RevealFolderInCurrentTree(string directory)
    {
        var roots = ViewModel.CurrentTreeRoots();
        if (string.IsNullOrEmpty(directory) || roots.Count == 0) return;
        var target = ViewModel.FindClosestFolderInCurrentTree(directory);
        if (target == null) return;
        for (var ancestor = target.Parent; ancestor != null; ancestor = ancestor.Parent)
            ancestor.IsExpanded = true;
        target.IsExpanded = true;
        _syncingTreeFromFile = true;
        foreach (var item in MainWindowViewModel.Flatten(roots))
            if (item.IsCurrent && item != target)
                item.IsCurrent = false;
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
        for (var node = target; node != null; node = node.Parent)
        {
            node.IsExpanded = true;
            path.Add(node);
        }

        path.Reverse();

        if (path.Count > 0 && ReferenceEquals(path[0], ViewModel.FrozenRoot))
        {
            path.RemoveAt(0);
            if (path.Count == 0)
            {
                _window.FrozenRootTree.UpdateLayout();
                var rootItem = _window.FrozenRootTree.ItemContainerGenerator.ContainerFromItem(target) as TreeViewItem;
                if (rootItem != null) rootItem.IsSelected = true;
                _syncingTreeFromFile = false;
                return;
            }
        }

        Dispatcher.BeginInvoke(
            new Action(() => ExpandPathStep(ResultsTree, path, 0, 0)),
            DispatcherPriority.Loaded);
    }

    private void ExpandPathStep(ItemsControl host, List<FolderMatch> path, int level, int retry)
    {
        if (host == null || level >= path.Count)
        {
            _syncingTreeFromFile = false;
            return;
        }

        var node = path[level];
        host.ApplyTemplate();
        host.UpdateLayout();

        var container = host.ItemContainerGenerator.ContainerFromItem(node) as TreeViewItem;
        if (container == null)
        {
            var index = host.Items.IndexOf(node);
            if (index < 0 || retry >= 48)
            {
                _syncingTreeFromFile = false;
                return;
            }

            BringSiblingIndexIntoView(host, index);
            Dispatcher.BeginInvoke(
                new Action(() => ExpandPathStep(host, path, level, retry + 1)),
                DispatcherPriority.Loaded);
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
            DispatcherPriority.Loaded);
    }

    private void BringSiblingIndexIntoView(ItemsControl host, int index)
    {
        if (index < 0) return;

        var presenter = FindVisualChild<ItemsPresenter>(host);
        var panel = presenter != null ? FindVisualChild<VirtualizingPanel>(presenter) : null;
        if (panel != null)
        {
            var method = typeof(VirtualizingPanel).GetMethod(
                "BringIndexIntoView",
                BindingFlags.Instance | BindingFlags.NonPublic);
            if (method != null)
            {
                method.Invoke(panel, new object[] { index });
                return;
            }
        }

        var viewer = FindScrollViewer(ResultsTree);
        if (viewer != null)
            viewer.ScrollToVerticalOffset(Math.Max(0, index - 2));
    }

    private static T FindVisualChild<T>(DependencyObject root) where T : DependencyObject
    {
        if (root == null) return null;
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            var match = child as T ?? FindVisualChild<T>(child);
            if (match != null) return match;
        }

        return null;
    }

    private static bool TryGetFlatIndex(IEnumerable<FolderMatch> nodes, FolderMatch target, ref int index)
    {
        foreach (var node in nodes)
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
        var viewer = FindScrollViewer(tree);
        if (viewer == null) return;
        try
        {
            var pos = item.TransformToAncestor(viewer).Transform(new Point(0, 0));
            var top = pos.Y;
            var bottom = top + Math.Max(item.ActualHeight, 1);
            if (top < 0) viewer.ScrollToVerticalOffset(viewer.VerticalOffset + top - 8);
            else if (bottom > viewer.ViewportHeight)
                viewer.ScrollToVerticalOffset(viewer.VerticalOffset + bottom - viewer.ViewportHeight + 8);
        }
        catch (InvalidOperationException)
        {
        }
    }

    private static ScrollViewer FindScrollViewer(DependencyObject root)
    {
        if (root is ScrollViewer viewer && viewer.VerticalScrollBarVisibility != ScrollBarVisibility.Disabled)
            return viewer;
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var found = FindScrollViewer(VisualTreeHelper.GetChild(root, i));
            if (found != null) return found;
        }

        return null;
    }

    // --- MainWindowPresentationService.Windows.cs ---

    private void RestoreWindowPlacement(string[] arguments)
    {
        double left = 0, top = 0, width = 0, height = 0;
        var state = 0;
        var fromCommandLine = TryReadArgument(arguments, "--window-left", out left)
                              && TryReadArgument(arguments, "--window-top", out top)
                              && TryReadArgument(arguments, "--window-width", out width)
                              && TryReadArgument(arguments, "--window-height", out height);
        if (fromCommandLine)
        {
            if (arguments.Any(argument =>
                    string.Equals(argument, "--window-maximized", StringComparison.OrdinalIgnoreCase)))
                state = (int)WindowState.Maximized;
        }
        else if (!SettingsService.TryLoadMainWindowPlacement(out left, out top, out width, out height, out state))
        {
            return;
        }

        ApplyPlacement(_window, left, top, width, height, state);
    }

    private void SaveMainWindowPlacement()
    {
        var bounds = _window.WindowState == WindowState.Normal
            ? new Rect(_window.Left, _window.Top, _window.Width, _window.Height)
            : _window.RestoreBounds;
        var state = _window.WindowState == WindowState.Minimized
            ? (int)WindowState.Normal
            : (int)_window.WindowState;
        SettingsService.SaveMainWindowPlacement(bounds.Left, bounds.Top, bounds.Width, bounds.Height, state);
    }

    private void OverlayOnMain(Window child)
    {
        if (child == null) return;
        var bounds = _window.WindowState == WindowState.Normal
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
        for (var index = 0; index < arguments.Length - 1; index++)
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

    private static string ConfigIconGlyph(string pair)
    {
        var config = pair ?? string.Empty;
        var pipe = config.IndexOf('|');
        if (pipe >= 0) config = config.Substring(0, pipe);
        var debugAt = config.IndexOf("Debug", StringComparison.OrdinalIgnoreCase);
        var releaseAt = config.IndexOf("Release", StringComparison.OrdinalIgnoreCase);
        if (debugAt >= 0 && (releaseAt < 0 || debugAt < releaseAt))
            return "\uEBE8";
        if (releaseAt >= 0)
            return "\uE7B8";
        return "\uE74C";
    }

    private void OpenGrep(IReadOnlyList<string> scopes)
    {
        if (scopes == null || scopes.Count == 0)
            scopes = ViewModel.PrepareGrepScopes();
        if (scopes.Count == 0) return;
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

    private sealed class MsBuildMenuModel
    {
        public ObservableCollection<MsBuildActionItem> BuildItems { get; } = new();
        public ObservableCollection<MsBuildActionItem> RebuildItems { get; } = new();
        public ObservableCollection<MsBuildActionItem> SdkBuildItems { get; } = new();
        public ObservableCollection<MsBuildActionItem> SdkRebuildItems { get; } = new();
    }

    private sealed class MsBuildActionItem
    {
        public string Header { get; set; }
        public string IconGlyph { get; set; }
        public ICommand Command { get; set; }

        public object MenuIcon
        {
            get
            {
                var icon = new TextBlock
                {
                    Text = string.IsNullOrEmpty(IconGlyph) ? "\uE74C" : IconGlyph,
                    FontFamily = new FontFamily("Segoe MDL2 Assets"),
                    FontSize = 15,
                    HorizontalAlignment = HorizontalAlignment.Center,
                    VerticalAlignment = VerticalAlignment.Center
                };
                icon.SetResourceReference(TextBlock.ForegroundProperty, "Ink");
                return icon;
            }
        }
    }
}