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
using System.Windows.Data;

namespace DesktopIniManager.Views.Presenter;

[SupportedOSPlatform("windows")]
public sealed class MainWindowPresentationService
{
    private static readonly HashSet<string> ScriptExtensions =
        new(StringComparer.OrdinalIgnoreCase) { ".ps1", ".bat", ".cmd", ".vbs" };

    private readonly DispatcherTimer _filterTimer;
    private readonly MsBuildMenuModel _msBuildMenu = new();

    // --- MainWindowPresentationService.cs ---

    private readonly MainWindow _window;
    private bool _analyzeSelectedRoot;
    private bool _differencerSavedOnClose;

    private DeveloperDifferencerWindow _differencerWindow;
    private FileListItem _fileContextItem;
    private FileListItem _fileDragItem;
    private Point _fileDragStart;
    private GrepWindow _grepWindow;
    private bool _internalDragOut;

    private bool _languageReady;
    private bool _largeFileIcons;

    /// <summary>Opens or activates the developer differencer window.</summary>
    private bool _savingDifferencerOnClose;

    private bool _searchModeActive;
    private bool _syncingTreeFromFile;

    private bool _themeBoxReady;
    private FolderMatch _treeDragFolder;
    private bool _treeDragPreparing;
    private Point _treeDragStart;
    private bool _updatingToggles;

    /// <summary>
    ///     メイン ウィンドウの表示処理を担うサービスを作成し、ViewModel、ウィンドウの配置、各コントロールのイベントを初期化します。
    /// </summary>
    /// <param name="window">対象のメイン ウィンドウ。</param>
    /// <param name="startup">事前に読み込んだ起動状態。</param>
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
        ViewModel.PropertyChanged += (_, args) =>
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

        _window.Loaded += (_, _) =>
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

    /// <summary>
    ///     メイン ウィンドウからリソースを検索します。
    /// </summary>
    /// <param name="key">リソース キー。</param>
    /// <returns>見つかったリソース。</returns>
    private object FindResource(object key)
    {
        return _window.FindResource(key);
    }

    /// <summary>
    ///     キー操作・ドラッグ アンド ドロップ・ツリー・ファイル一覧など、ビューのイベントを ViewModel と表示処理に接続します。
    /// </summary>
    private void ConnectView()
    {
        InputManager.Current.PreProcessInput += SwitchWindowShortcut;
        _window.PreviewKeyDown += MainSearchKeyDown;
        RootBox.HistoryItemApplied += (_, _) => StartSelectedRootAnalysis();
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
            var style = CreateBuildConfigurationItemStyle();

            var buildMenu = FindTaggedMenuItem(treeMenu, "dim-msbuild-build");
            var rebuildMenu = FindTaggedMenuItem(treeMenu, "dim-msbuild-rebuild");
            var sdkBuildMenu = FindTaggedMenuItem(treeMenu, "dim-sdk-build");
            var sdkRebuildMenu = FindTaggedMenuItem(treeMenu, "dim-sdk-rebuild");

            if (buildMenu != null)
            {
                buildMenu.ItemContainerStyle = style;
                buildMenu.ItemsSource = _msBuildMenu.BuildItems;
            }

            if (rebuildMenu != null)
            {
                rebuildMenu.ItemContainerStyle = style;
                rebuildMenu.ItemsSource = _msBuildMenu.RebuildItems;
            }

            if (sdkBuildMenu != null)
            {
                sdkBuildMenu.ItemContainerStyle = style;
                sdkBuildMenu.ItemsSource = _msBuildMenu.SdkBuildItems;
            }

            if (sdkRebuildMenu != null)
            {
                sdkRebuildMenu.ItemContainerStyle = style;
                sdkRebuildMenu.ItemsSource = _msBuildMenu.SdkRebuildItems;
            }
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
        _window.DiffViewButton.Click += (_, _) => OpenStandaloneDiffView();
        _window.RunScriptButton.Click += (_, _) => RunScriptCommand();
        _window.BrowseScriptButton.Click += (_, _) => BrowseScriptCommand();
        _window.ScriptBox.PreviewKeyDown += ScriptBox_PreviewKeyDown;
        _window.RunScriptButton.ToolTip = StringOverlay.Get("Main_RunScript");
        _window.BrowseScriptButton.ToolTip =
            StringOverlay.Get("Main_BrowseScript");
        _window.ScriptBox.ToolTip = StringOverlay.Get("Main_ScriptTooltip");
        _window.Closing += OnClosing;
        _window.Closed += OnClosed;
        ViewModel.InteractionRequested += HandleInteraction;
        _window.AllowDrop = true;
        DropZoneHighlight.Attach(_window, () => !_internalDragOut, RootBox);
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

    /// <summary>
    ///     ファイルのドラッグ中にコピーのカーソルを表示します。
    /// </summary>
    /// <param name="sender">イベントの送信元。</param>
    /// <param name="e">ドラッグ イベントのデータ。</param>
    private static void FolderDropPreview(object sender, DragEventArgs e)
    {
        if (e.Data == null || !e.Data.GetDataPresent(DataFormats.FileDrop))
            return;

        e.Effects = DragDropEffects.Copy;
        e.Handled = true;
    }

    /// <summary>
    ///     ドロップされたデータからフォルダーを取り出します。
    /// </summary>
    /// <param name="data">ドロップされたデータ。</param>
    /// <returns>フォルダーのパス。</returns>
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

    /// <summary>
    ///     ドロップされた最初のフォルダーを検索ルートに設定して保存し、ドロップされたフォルダーをツリーで選択します。ウィンドウ内からのドラッグ アウト中は無視します。
    /// </summary>
    /// <param name="sender">イベントの送信元。</param>
    /// <param name="e">ドラッグ イベントのデータ。</param>
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

    /// <summary>
    ///     ViewModel から要求されたビュー側の操作 (フォルダー選択、アイコン選択、Explorer で開くなど) を実行します。
    /// </summary>
    /// <param name="action">要求された操作。</param>
    /// <param name="parameter">操作のパラメーター。</param>
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
                case MainWindowAction.FileListView: FileListView(); break;
                case MainWindowAction.FileIconSmall: FileIconSmall(); break;
                case MainWindowAction.FileIconLarge: FileIconLarge(); break;
                case MainWindowAction.ClearFolderFilter: ClearFolderFilter(); break;
                case MainWindowAction.DeveloperDifferencer: DeveloperDifferencer(); break;
                case MainWindowAction.Reset: Reset(); break;
                case MainWindowAction.Close: Close(); break;
            }
        }
        catch (Exception error)
        {
            ViewModel.ShowError(StringOverlay.Get("App_Unhandled"), error);
        }
    }

    /// <summary>
    ///     フォルダー選択ダイアログで検索ルートを選び、解析を開始します。
    /// </summary>
    private void ChooseRoot()
    {
        try
        {
            var selectedPath = Directory.Exists(ViewModel.RootPath)
                ? ViewModel.RootPath
                : Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            var result = NativeFolderPicker.Show(new WindowInteropHelper(_window).Handle, selectedPath,
                StringOverlay.Get("Main_SelectSearchFolder"));
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
            ViewModel.ShowError(StringOverlay.Get("Main_FolderPickerFailed"), ex);
        }
    }

    /// <summary>
    ///     選択した検索ルートの解析を開始します。検索中の場合は取り消してから開始します。
    /// </summary>
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

    /// <summary>
    ///     検索欄の Enter で検索を実行します。
    /// </summary>
    /// <param name="sender">イベントの送信元。</param>
    /// <param name="e">キー イベントのデータ。</param>
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

    /// <summary>
    ///     ウィンドウ切り替えのショートカットを処理します。Ctrl+1/2/3/4 でメイン・Grep・Developer Differencer・DIFF ビューへ、Ctrl+Space で DIFF ビューを含めて順に切り替えます。
    /// </summary>
    /// <param name="sender">イベントの送信元。</param>
    /// <param name="e">入力の前処理のイベントデータ。</param>
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
                case Key.NumPad4: destination = 4; break;

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
                    // Ctrl+Space の巡回で選ばれた DIFF ビューはそのまま前面に出します。
                    if (selectedViewer != null) WindowActivationService.BringToFront(selectedViewer, true);
                    else OpenStandaloneDiffView();
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
                ViewModel.ShowError(StringOverlay.Get("App_Unhandled"), ex);
            }
        }));
    }

    /// <summary>
    ///     単体の DIFF ビューを前面に出します。開いていない場合は空の状態で新しく開きます。
    /// </summary>
    private void OpenStandaloneDiffView()
    {
        try
        {
            var viewer = Application.Current.Windows.OfType<DiffViewWindow>()
                .FirstOrDefault(window => window.IsVisible && window.Owner == null);
            if (viewer == null)
            {
                var launch = ExternalDiffLaunch.Parse(new[] { "-diff" });
                var snapshot = launch.CreateSnapshot();
                viewer = new DiffViewWindow(snapshot, snapshot.Files[0], launch.SourcePath, launch.TargetPath)
                {
                    Width = Math.Max(900, _window.ActualWidth),
                    Height = Math.Max(600, _window.ActualHeight)
                };
                viewer.Show();
            }

            WindowActivationService.BringToFront(viewer, true);
        }
        catch (Exception ex)
        {
            ViewModel.ShowError(StringOverlay.Get("App_Unhandled"), ex);
        }
    }

    /// <summary>
    ///     アイコン ライブラリのファイルを選び、設定に保存します。
    /// </summary>
    private void ChooseIconLibrary()
    {
        try
        {
            var dialog = new OpenFileDialog { Filter = StringOverlay.Get("Main_IconFilter"), CheckFileExists = true };
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
            ViewModel.Status = Path.GetFileName(dialog.FileName) + StringOverlay.Get("Main_SelectedSuffix");
        }
        catch (Exception ex)
        {
            ViewModel.ShowError(StringOverlay.Get("Main_LibrarySelectFailed"), ex);
        }
    }

    /// <summary>
    ///     アイコン ライブラリからアイコン グループを選ぶウィンドウを表示し、選んだアイコンを適用対象にします。
    /// </summary>
    private void ChooseIcon()
    {
        try
        {
            var iconPath = ViewModel.IconLibraryPath.Trim();
            if (!File.Exists(iconPath))
            {
                MessageBox.Show(StringOverlay.Get("Main_ChooseLibraryFirst"), Title);
                return;
            }

            ViewModel.SetStatus("Main_LoadingIcons");
            var groups = IconResourceReader.Read(iconPath);
            if (groups.Count == 0)
            {
                MessageBox.Show(StringOverlay.Get("Main_NoIconGroups"), Title);
                return;
            }

            var browser = new IconGroupBrowserWindow(iconPath, groups, ViewModel.SelectedIconIndex) { Owner = _window };
            if (browser.ShowDialog() == true && browser.SelectedGroup != null)
            {
                ViewModel.ApplySelectedIcon(browser.SelectedGroup.ShellIndex, browser.SelectedGroup.Preview);
                ViewModel.SetStatus("Main_IconNSelected", browser.SelectedGroup.ShellIndex);
            }
            else
            {
                ViewModel.SetStatus("Main_IconCancelled");
            }
        }
        catch (Exception ex)
        {
            ViewModel.ShowError(StringOverlay.Get("Main_IconBrowserFailed"), ex);
        }
    }

    /// <summary>
    ///     選択中のアイコンのプレビューを、アイコン ライブラリから読み込み直します。
    /// </summary>
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
            ViewModel.SelectedIconLabel = StringOverlay.Get("Main_PreviewUnavailable");
        }
    }

    /// <summary>
    ///     フォルダーを検索ルートに設定して解析を開始します。
    /// </summary>
    /// <param name="folder">対象のフォルダー。</param>
    private void UseAsSearchLocation(FolderMatch folder)
    {
        var path = MainWindowViewModel.ExistingFolderPath(folder?.Path);
        if (path == null)
        {
            MessageBox.Show(StringOverlay.Get("Main_LocationMissing"), Title);
            return;
        }

        ViewModel.RootPath = path;
        RootBox.CommitHistory();
        SettingsService.SaveSearchRoot(path);
        ShowTextEnd(RootBox);
        StartSelectedRootAnalysis();
    }

    /// <summary>
    ///     フォルダーを Explorer で開きます。
    /// </summary>
    /// <param name="folder">対象のフォルダー。</param>
    private void OpenExplorer(FolderMatch folder)
    {
        if (folder == null || !Directory.Exists(folder.Path)) return;
        try
        {
            Process.Start(new ProcessStartInfo("explorer.exe", "\"" + folder.Path + "\"") { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            ViewModel.ShowError(StringOverlay.Get("Main_ExplorerFailed"), ex);
        }
    }

    /// <summary>
    ///     表示中のファイル名の一覧をテキスト ファイルに書き出し、エディターで開きます。
    /// </summary>
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
            ViewModel.SetStatus("Main_TreeOpened", StringOverlay.Get("Main_LabelFileList"), ViewModel.FilePanelPath);
        }
        catch (Exception ex)
        {
            ViewModel.ShowError(string.Format(StringOverlay.Get("Main_TreeFailed"), StringOverlay.Get("Main_LabelFileList")), ex);
        }
    }

    /// <summary>
    ///     フォルダー ツリーを tree または tree /f 形式でテキスト ファイルに書き出し、エディターで開きます。
    /// </summary>
    /// <param name="folder">起点のフォルダー。</param>
    /// <param name="includeFiles">ファイルも含める (tree /f) 場合は true。</param>
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
            ViewModel.SetStatus("Main_TreeOpened", label, folder.Path);
        }
        catch (Exception ex)
        {
            ViewModel.ShowError(string.Format(StringOverlay.Get("Main_TreeFailed"), label), ex);
        }
    }

    /// <summary>
    ///     フォルダーのパスと、その配下のツリーを書き出します。
    /// </summary>
    /// <param name="text">書き込み先。</param>
    /// <param name="folder">起点のフォルダー。</param>
    /// <param name="includeFiles">ファイルも含める場合は true。</param>
    private void WriteContainedTree(StringBuilder text, FolderMatch folder, bool includeFiles)
    {
        if (folder == null) return;
        text.AppendLine(folder.Path);
        WriteContainedLevel(text, folder, string.Empty, includeFiles);
    }

    /// <summary>
    ///     フォルダー直下のファイルとサブフォルダーを、罫線付きの tree 形式で再帰的に書き出します。
    /// </summary>
    /// <param name="text">書き込み先。</param>
    /// <param name="folder">対象のフォルダー。</param>
    /// <param name="prefix">行頭の罫線。</param>
    /// <param name="includeFiles">ファイルも含める場合は true。</param>
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

    /// <summary>
    ///     設定されたエディターでテキスト ファイルを開きます。
    /// </summary>
    /// <param name="path">ファイルのパス。</param>
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

    /// <summary>
    ///     指定したフォルダーだけを選択して、Grep ウィンドウを開きます。
    /// </summary>
    /// <param name="folder">検索範囲のフォルダー。</param>
    private void GrepFolder(FolderMatch folder)
    {
        if (folder == null || !Directory.Exists(folder.Path)) return;
        foreach (var item in ViewModel.CurrentItems()) item.IsSelected = ReferenceEquals(item, folder);
        OpenGrep(new[] { folder.Path });
    }

    /// <summary>
    ///     ファイルを関連付けられたアプリケーションで開きます。
    /// </summary>
    /// <param name="file">対象のファイル。</param>
    private void OpenAssociatedFile(FileListItem file)
    {
        if (file == null) return;
        try
        {
            Process.Start(new ProcessStartInfo(file.Path) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            ViewModel.ShowError(StringOverlay.Get("Main_OpenFileFailed"), ex);
        }
    }

    /// <summary>
    ///     ファイル一覧の右クリックで、選択を保ったままコンテキスト メニューを開くよう設定します。
    /// </summary>
    /// <param name="list">対象の一覧。</param>
    private void InitializeFileContextMenu(Selector list)
    {
        list.PreviewMouseRightButtonDown += (_, args) =>
        {
            _fileContextItem = FileFromElement(args.OriginalSource as DependencyObject);
            // Preserve all selected rows, including when modifiers are held.
            if (_fileContextItem != null && IsFileSelected(list, _fileContextItem))
                args.Handled = true;
        };
        // A placeholder enables ContextMenuOpening; the complete popup is native.
        list.ContextMenu ??= new ContextMenu();
    }

    /// <summary>
    ///     ファイル一覧のコンテキスト メニューを、独自のコマンドと Explorer のシェル メニューを組み合わせて表示します。
    /// </summary>
    /// <param name="sender">対象の一覧。</param>
    /// <param name="e">イベントデータ。</param>
    private void FileList_ContextMenuOpening(object sender, ContextMenuEventArgs e)
    {
        if (sender is not Selector list) return;
        e.Handled = true;
        var selectedFile = list.SelectedItem as FileListItem;
        var source = e.CursorLeft < 0 ? selectedFile : _fileContextItem;
        var items = new List<ExplorerContextMenu.CommandItem>
        {
            new(StringOverlay.Get("Main_RunScript"), () => RunSelectedScript(source),
                source != null && ScriptExtensions.Contains(source.Extension ?? string.Empty))
        };
        if (source != null && list.Items.Contains(source))
        {
            var visibleFiles = list.Items.Cast<FileListItem>().ToList();
            var selected = visibleFiles.Where(candidate => IsFileSelected(list, candidate)).ToList();
            var candidates = source.IsSearchMatch
                ? visibleFiles.Where(candidate => candidate.IsSearchMatch).ToList()
                : selected.Count > 1 && selected.Contains(source) ? selected : new List<FileListItem>();
            var comparisons = new List<ExplorerContextMenu.CommandItem>();
            foreach (var target in candidates.Where(candidate =>
                         !string.Equals(candidate.Path, source.Path, StringComparison.OrdinalIgnoreCase)))
            {
                var duplicateName = candidates.Count(candidate =>
                    string.Equals(candidate.Name, target.Name, StringComparison.OrdinalIgnoreCase)) > 1;
                comparisons.Add(new ExplorerContextMenu.CommandItem(
                    (duplicateName ? target.Path : target.Name).Replace("&", "&&"),
                    () => OpenFileComparison(source, target)));
            }
            if (comparisons.Count > 0)
                items.Add(new ExplorerContextMenu.CommandItem(StringOverlay.Get("Main_SelectCompare"), null, Children: comparisons));
        }

        var shellFiles = source == null ? new List<FileListItem>()
            : IsFileSelected(list, source)
                ? list.Items.Cast<FileListItem>().Where(candidate => IsFileSelected(list, candidate)).ToList()
                : new List<FileListItem> { source };
        var paths = shellFiles.Select(candidate => candidate.Path)
            .Where(path => !string.IsNullOrWhiteSpace(path)).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        if (paths.Select(Path.GetDirectoryName).Distinct(StringComparer.OrdinalIgnoreCase).Count() > 1)
            paths = Array.Empty<string>();
        var position = list.PointToScreen(e.CursorLeft < 0
            ? new Point(16, 16) : Mouse.GetPosition(list));
        // One native menu loop owns both DIM commands and the shell submenu, so
        // hovering back to the parent, Left Arrow and Escape retain normal behavior.
        Dispatcher.BeginInvoke(new Action(() =>
        {
            try
            {
                ExplorerContextMenu.Show(_window, paths, position, items);
            }
            catch (Exception ex)
            {
                ViewModel.ShowError(StringOverlay.Get("Main_OpenFileFailed"), ex);
            }
        }), DispatcherPriority.Input);
    }

    /// <summary>
    ///     2 つのファイルを DIFF ビューで比較します。
    /// </summary>
    /// <param name="source">比較元のファイル。</param>
    /// <param name="target">比較先のファイル。</param>
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
            ViewModel.ShowError(StringOverlay.Get("Main_OpenFileFailed"), ex);
        }
    }

    /// <summary>
    ///     選択したスクリプト ファイルを、確認のうえそのフォルダーで実行します。
    /// </summary>
    /// <param name="file">実行するスクリプト。</param>
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
            ViewModel.SetStatus("Main_RunScriptStarted", file.Name);
        }
        catch (Exception ex)
        {
            ViewModel.ShowError(StringOverlay.Get("Main_RunScriptFailed"), ex);
        }
    }

    /// <summary>
    ///     スクリプト入力欄の Enter でコマンドを実行します。
    /// </summary>
    /// <param name="sender">イベントの送信元。</param>
    /// <param name="e">キー イベントのデータ。</param>
    private void ScriptBox_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            e.Handled = true;
            RunScriptCommand();
        }
    }

    /// <summary>
    ///     実行するスクリプト ファイルを選び、入力欄に設定します。
    /// </summary>
    private void BrowseScriptCommand()
    {
        try
        {
            var dialog = new OpenFileDialog
            {
                Filter = StringOverlay.Get("Main_ScriptFileFilter"),
                CheckFileExists = true
            };
            var current = ScriptBox.Text.Trim();
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
            ViewModel.ShowError(StringOverlay.Get("Main_RunScriptFailed"), ex);
        }
    }

    /// <summary>
    ///     入力したコマンドの %d、%f、%n を展開し、確認のうえ実行します。
    /// </summary>
    private void RunScriptCommand()
    {
        var template = ScriptBox.Text.Trim();
        if (string.IsNullOrEmpty(template))
        {
            ViewModel.SetStatus("Main_ScriptEmpty");
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
            ViewModel.SetStatus("Main_RunScriptStarted", command);
        }
        catch (Exception ex)
        {
            ViewModel.ShowError(StringOverlay.Get("Main_RunScriptFailed"), ex);
        }
    }

    /// <summary>
    ///     コマンドを、終了後も閉じないコンソールで実行します。.ps1 は PowerShell の -NoExit、それ以外は cmd /k で実行します。
    /// </summary>
    /// <param name="workDir">作業フォルダー。</param>
    /// <param name="command">実行するコマンド。</param>
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

    /// <summary>
    ///     コマンドから最初のトークン (実行ファイル) を除いた残りを取得します。
    /// </summary>
    /// <param name="command">コマンド。</param>
    /// <returns>残りの引数。</returns>
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

    /// <summary>
    ///     コマンドのテンプレートに %name 形式のトークンが含まれるかどうかを判定します。
    /// </summary>
    /// <param name="template">テンプレート。</param>
    /// <param name="name">トークン名。</param>
    /// <returns>含まれる場合は true。</returns>
    private static bool ContainsToken(string template, string name)
    {
        if (string.IsNullOrEmpty(template)) return false;
        return template.IndexOf("%" + name, StringComparison.OrdinalIgnoreCase) >= 0;
    }

    /// <summary>
    ///     テンプレートの %d (フォルダー)、%f (ファイル)、%n (ファイル名) を展開します。引用符で囲まれたトークンは引用符付きで置き換えます。
    /// </summary>
    /// <param name="template">テンプレート。</param>
    /// <param name="dir">フォルダーのパス。</param>
    /// <param name="file">ファイルのパス。</param>
    /// <returns>展開したコマンド。</returns>
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

    /// <summary>
    ///     コマンドの最初のトークン (引用符を考慮) を取得します。
    /// </summary>
    /// <param name="command">コマンド。</param>
    /// <returns>最初のトークン。</returns>
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

    /// <summary>
    ///     パスに空白が含まれ、引用符で囲まれていない場合は引用符で囲みます。
    /// </summary>
    /// <param name="path">対象のパス。</param>
    /// <returns>必要に応じて引用符で囲んだパス。</returns>
    private static string QuoteIfNeeded(string path)
    {
        if (string.IsNullOrEmpty(path)) return path;
        if (path.IndexOfAny(new[] { ' ', '\t' }) >= 0 && !(path.StartsWith("\"") && path.EndsWith("\"")))
            return "\"" + path + "\"";
        return path;
    }

    /// <summary>
    ///     閉じる前に Developer Differencer の状態を保存します。保存が終わるまでは閉じる処理を保留します。
    /// </summary>
    /// <param name="sender">イベントの送信元。</param>
    /// <param name="e">イベントデータ。</param>
    private async void OnClosing(object sender, CancelEventArgs e)
    {
        if (_differencerWindow != null && !_differencerSavedOnClose)
        {
            e.Cancel = true;
            if (_differencerWindow.IsWorking || _savingDifferencerOnClose) return;
            _savingDifferencerOnClose = true;
            try
            {
                if (await _differencerWindow.PrepareCloseAsync())
                {
                    _differencerSavedOnClose = true;
                    _ = _window.Dispatcher.BeginInvoke(new Action(() => _window.Close()));
                }
            }
            finally
            {
                _savingDifferencerOnClose = false;
            }

            return;
        }

        if (!e.Cancel) ViewModel.SaveFolderTrees();
    }

    /// <summary>
    ///     メイン ウィンドウを閉じます。
    /// </summary>
    private void Close()
    {
        _window.Close();
    }

    /// <summary>
    ///     ウィンドウが閉じたときに、イベントの購読を解除し、処理を取り消して、ツリー・設定・ウィンドウ位置を保存します。
    /// </summary>
    /// <param name="sender">イベントの送信元。</param>
    /// <param name="e">イベントデータ。</param>
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
            //
        }

        try
        {
            _differencerWindow?.Close();
        }
        catch
        {
            //
        }

        SettingsService.SaveIconLibraryPath(ViewModel.IconLibraryPath);
        SettingsService.SaveSearchQuery(ViewModel.Query);
        SettingsService.SaveSearchRoot(ViewModel.RootPath);
        SaveMainWindowPlacement();
    }

    /// <summary>
    ///     国旗アイコン付きの表示言語の選択欄を初期化し、現在の言語を選択します。
    /// </summary>
    private void InitLanguageBox()
    {
        var flags = LanguageIconService.LoadFlagIcons();
        var items = new[]
        {
            new LanguageChoice("en", flags[0], StringOverlay.Get("Language_NameEnglish")),
            new LanguageChoice("ja", flags[1], StringOverlay.Get("Language_NameJapanese")),
            new LanguageChoice("zh-Hans", flags[2], StringOverlay.Get("Language_NameChinese")),
            new LanguageChoice("ko", flags[3], StringOverlay.Get("Language_NameKorean"))
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

    /// <summary>
    ///     表示言語の選択に合わせて、カルチャを切り替えます。
    /// </summary>
    /// <param name="sender">イベントの送信元。</param>
    /// <param name="e">選択変更のイベントデータ。</param>
    private void LanguageBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_languageReady) return;
        var choice = LanguageBox.SelectedItem as LanguageChoice;
        if (choice == null) return;
        StringOverlay.SetCulture(choice.Code);
        ViewModel.RefreshLocalizedTexts();
        Title = StringOverlay.Get("App_Title");
        ScheduleResetCaption();
    }

    /// <summary>
    ///     リセット ボタンの表示の更新を、表示言語の切り替え後に実行するようスケジュールします。
    /// </summary>
    private void ScheduleResetCaption()
    {
        Dispatcher.BeginInvoke(new Action(UpdateResetCaption), DispatcherPriority.ContextIdle);
    }

    /// <summary>
    ///     リセット ボタンのツールチップを、現在の表示言語で更新します。
    /// </summary>
    private void UpdateResetCaption()
    {
        if (ResetButton == null) return;
        ResetButton.ToolTip = StringOverlay.Get("Main_ResetTip");
    }

    /// <summary>
    ///     確認のうえ、設定・入力履歴・フォルダー一覧・比較状態などをすべて初期化します。
    /// </summary>
    private void Reset()
    {
        var title = StringOverlay.Get("Main_ResetTitle");
        var message = StringOverlay.Get("Main_ResetConfirm");
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
                    //
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
        ViewModel.SetCountLabel("Main_NFolders", 0);
        ViewModel.SearchHitLabel = "0/0";
        ViewModel.SetStatus("Common_Ready");
        _languageReady = false;
        StringOverlay.SetCulture("en");
        ViewModel.RefreshLocalizedTexts();
        InitLanguageBox();
        Title = StringOverlay.Get("App_Title");
        RefreshSelectedIconPreview();
        ScheduleResetCaption();
    }

    /// <summary>
    ///     フォルダー ツリーをコンパクト表示にします。
    /// </summary>
    private void CompactTree()
    {
        ApplyTreeDensity(true, true);
    }

    /// <summary>
    ///     フォルダー ツリーをゆったりした表示にします。
    /// </summary>
    private void ComfortableTree()
    {
        ApplyTreeDensity(false, true);
    }

    /// <summary>
    ///     フォルダー ツリーの表示密度を適用して保存します。
    /// </summary>
    /// <param name="compact">コンパクト表示の場合は true。</param>
    /// <param name="announce">ステータスに表示する場合は true。</param>
    private void ApplyTreeDensity(bool compact, bool announce)
    {
        ViewModel.TreeCompact = compact;
        HighlightTreeDensityButtons();
        SettingsService.SaveTreeCompact(compact);
        if (announce) ViewModel.SetStatus(compact ? "Main_TreeCompact" : "Main_TreeComfortable");
    }

    /// <summary>
    ///     表示密度のトグルの状態とツールチップを更新します。
    /// </summary>
    private void HighlightTreeDensityButtons()
    {
        if (TreeDensityToggle == null) return;
        _updatingToggles = true;
        TreeDensityToggle.IsChecked = !ViewModel.TreeCompact;
        TreeDensityToggle.ToolTip = ViewModel.TreeCompact ? StringOverlay.Get("Main_TreeCompact") : StringOverlay.Get("Main_TreeComfortable");
        _updatingToggles = false;
        if (_window.TreeDensityIcon != null)
            _window.TreeDensityIcon.Source = DifferencerStatusIcons.GetCustomIcon(ViewModel.TreeCompact ? 91 : 90);
    }

    /// <summary>
    ///     メイン画面のツール バーの各ボタンにアイコンを設定します。
    /// </summary>
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
        SetToolbarIcon(_window.DiffViewButtonIcon, 52);
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

    /// <summary>
    ///     Image にアイコン ライブラリのアイコンを設定します。
    /// </summary>
    /// <param name="image">対象の Image。</param>
    /// <param name="index">アイコンのインデックス。</param>
    private static void SetToolbarIcon(Image image, int index)
    {
        if (image != null)
            image.Source = DifferencerStatusIcons.GetCustomIcon(index);
    }

    /// <summary>
    ///     フォルダー ツリーのすべての展開と折りたたみを切り替えます。
    /// </summary>
    /// <param name="sender">イベントの送信元。</param>
    /// <param name="e">イベントデータ。</param>
    private void NodeExpandToggle_Changed(object sender, RoutedEventArgs e)
    {
        if (_updatingToggles) return;

        var expanded = NodeExpandToggle.IsChecked == true;

        foreach (var folder in ViewModel.ScrollingTreeItems)
            SetTreeExpanded(folder, expanded);

        NodeExpandToggle.ToolTip =
            expanded ? StringOverlay.Get("Common_Collapse") : StringOverlay.Get("Common_Expand");

        UpdateNodeExpandIcon();
    }

    /// <summary>
    ///     フォルダーとその子孫の展開状態を一括で設定します。
    /// </summary>
    /// <param name="folder">起点のフォルダー。</param>
    /// <param name="expanded">展開する場合は true。</param>
    private static void SetTreeExpanded(FolderMatch folder, bool expanded)
    {
        if (folder == null)
            return;

        folder.IsExpanded = expanded;

        foreach (var child in folder.Children)
            SetTreeExpanded(child, expanded);
    }

    /// <summary>
    ///     表示密度のトグルに合わせて、フォルダー ツリーの表示密度を切り替えます。
    /// </summary>
    /// <param name="sender">イベントの送信元。</param>
    /// <param name="e">イベントデータ。</param>
    private void TreeDensityToggle_Changed(object sender, RoutedEventArgs e)
    {
        if (_updatingToggles) return;
        if (TreeDensityToggle.IsChecked == true) ComfortableTree();
        else CompactTree();
    }

    /// <summary>
    ///     展開・折りたたみボタンのアイコンを更新します。
    /// </summary>
    private void UpdateNodeExpandIcon()
    {
        if (_window.NodeExpandIcon == null) return;
        var expanded = NodeExpandToggle != null && NodeExpandToggle.IsChecked == true;
        _window.NodeExpandIcon.Source = DifferencerStatusIcons.GetCustomIcon(expanded ? 56 : 55);
    }

    /// <summary>
    ///     ファイルを一覧形式で表示します。
    /// </summary>
    private void FileListView()
    {
        FileList.Visibility = Visibility.Visible;
        FileIconList.Visibility = Visibility.Collapsed;
        HighlightFileViewButtons(true, false);
    }

    /// <summary>
    ///     ファイルを小さいアイコンで表示します。
    /// </summary>
    private void FileIconSmall()
    {
        ApplyFileIconSize(false);
    }

    /// <summary>
    ///     ファイルを大きいアイコンで表示します。
    /// </summary>
    private void FileIconLarge()
    {
        ApplyFileIconSize(true);
    }

    /// <summary>
    ///     ファイルのアイコン表示に切り替え、アイコンの大きさを適用します。
    /// </summary>
    /// <param name="large">大きいアイコンの場合は true。</param>
    private async void ApplyFileIconSize(bool large)
    {
        _largeFileIcons = large;
        ViewModel.IsFileBusy = true;
        await Dispatcher.Yield(DispatcherPriority.Render);
        FileList.Visibility = Visibility.Collapsed;
        FileIconList.Visibility = Visibility.Visible;
        FileIconList.Tag = large;
        HighlightFileViewButtons(false, large);
        FileIconList.UpdateLayout();
        await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
        ViewModel.IsFileBusy = false;
    }

    /// <summary>
    ///     選択中の表示形式のボタンを強調表示します。
    /// </summary>
    /// <param name="list">一覧形式の場合は true。</param>
    /// <param name="large">大きいアイコンの場合は true。</param>
    private void HighlightFileViewButtons(bool list, bool large)
    {
        var selected = (Brush)FindResource("ThemeSelected");
        var secondary = (Brush)FindResource("Secondary");
        if (FileListViewButton != null) FileListViewButton.Background = list ? selected : secondary;
        if (FileIconLargeButton != null) FileIconLargeButton.Background = !list && large ? selected : secondary;
        if (FileIconSmallButton != null) FileIconSmallButton.Background = !list && !large ? selected : secondary;
    }

    /// <summary>
    ///     フォルダーの絞り込み文字列が変わったときに、少し待ってから絞り込むようタイマーを再開します。
    /// </summary>
    /// <param name="sender">イベントの送信元。</param>
    /// <param name="e">イベントデータ。</param>
    private void FolderFilter_TextChanged(object sender, TextChangedEventArgs e)
    {
        _filterTimer?.Stop();
        _filterTimer?.Start();
    }

    /// <summary>
    ///     フォルダーの絞り込み文字列をクリアします。
    /// </summary>
    private void ClearFolderFilter()
    {
        ViewModel.FolderFilter = string.Empty;
    }

    /// <summary>
    ///     パスの入力欄で常に末尾が見えるよう、イベント ハンドラーを登録します。
    /// </summary>
    /// <param name="box">対象の入力欄。</param>
    private void HookPathBox(TextBox box)
    {
        if (box == null) return;
        box.GotKeyboardFocus += (_, _) => ShowTextEnd(box);
        box.LostKeyboardFocus += (_, _) => ShowTextEnd(box);
        box.TextChanged += (_, _) =>
        {
            if (!box.IsKeyboardFocusWithin) ShowTextEnd(box);
        };
    }

    /// <summary>
    ///     入力欄のキャレットを末尾に移動し、テキストの末尾を表示します。
    /// </summary>
    /// <param name="box">対象の入力欄。</param>
    private void ShowTextEnd(TextBox box)
    {
        if (box == null) return;
        Dispatcher.BeginInvoke(new Action(() =>
        {
            box.CaretIndex = box.Text.Length;
            box.ScrollToHorizontalOffset(Math.Max(0, box.ExtentWidth - box.ViewportWidth));
        }), DispatcherPriority.Loaded);
    }

    /// <summary>
    ///     検索中の進捗アニメーションを開始または停止します。
    /// </summary>
    /// <param name="value">検索中の場合は true。</param>
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

    /// <summary>
    ///     検索の進捗バーのマーキー アニメーションを開始または停止します。
    /// </summary>
    /// <param name="value">開始する場合は true。</param>
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

    /// <summary>
    ///     フォルダー ツリーの処理中表示を切り替えます。
    /// </summary>
    /// <param name="busy">処理中の場合は true。</param>
    private void SetTreePanelBusy(bool busy)
    {
        if (TreePanelBusy != null)
            ViewModel.IsTreeBusy = busy;
    }

    /// <summary>
    ///     テーマの選択欄で、指定したテーマを選択します。
    /// </summary>
    /// <param name="theme">テーマ名。</param>
    private void SelectThemeBox(string theme)
    {
        var normalized = ThemeService.Normalize(theme);
        _themeBoxReady = false;
        foreach (ComboBoxItem item in ThemeBox.Items)
            if (string.Equals(item.Tag as string, normalized, StringComparison.OrdinalIgnoreCase))
            {
                ThemeBox.SelectedItem = item;
                break;
            }

        _themeBoxReady = true;
    }

    /// <summary>
    ///     テーマの選択に合わせてテーマを適用します。
    /// </summary>
    /// <param name="sender">イベントの送信元。</param>
    /// <param name="e">選択変更のイベントデータ。</param>
    private void ThemeBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_themeBoxReady) return;
        var item = ThemeBox.SelectedItem as ComboBoxItem;
        if (item == null) return;
        SetTheme(item.Tag as string);
    }


    /// <summary>
    ///     テーマを適用して保存し、選択欄とボタンの強調表示を更新します。
    /// </summary>
    /// <param name="theme">テーマ名。</param>
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

    /// <summary>
    ///     検索モードにして、Search タブだけを使えるようにします。
    /// </summary>
    private void EnterSearchMode()
    {
        _searchModeActive = true;
        _window.SearchTreeTab.IsEnabled = true;
        _window.PhysicalTreeTab.IsEnabled = false;
        _window.SolutionTreeTab.IsEnabled = false;
    }

    /// <summary>
    ///     検索モード中に検索語が空になったときに、検索結果をクリアします。
    /// </summary>
    /// <param name="sender">イベントの送信元。</param>
    /// <param name="e">イベントデータ。</param>
    private void QueryBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (!_searchModeActive || !string.IsNullOrWhiteSpace(QueryBox.Text)) return;

        ViewModel.ClearSearchSession();
        _window.SearchTreeTab.IsEnabled = true;
        _window.PhysicalTreeTab.IsEnabled = true;
        _window.SolutionTreeTab.IsEnabled = true;
    }

    /// <summary>
    ///     検索語が空の状態で Physical または Solution タブを選んだときに、検索モードを終了します。
    /// </summary>
    /// <param name="sender">イベントの送信元。</param>
    /// <param name="e">選択変更のイベントデータ。</param>
    private void TreeTabs_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_searchModeActive || !string.IsNullOrWhiteSpace(QueryBox.Text)) return;
        if (_window.TreeTabs.SelectedIndex != 0 && _window.TreeTabs.SelectedIndex != 1) return;

        _searchModeActive = false;
        _window.SearchTreeTab.IsEnabled = false;
        _window.PhysicalTreeTab.IsEnabled = true;
        _window.SolutionTreeTab.IsEnabled = true;
    }

    /// <summary>
    ///     Search タブのルートを展開・選択して、ファイル一覧に表示します。
    /// </summary>
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

    /// <summary>
    ///     右クリックしたフォルダーの MSBuild メニューを準備します。
    /// </summary>
    /// <param name="sender">イベントの送信元。</param>
    /// <param name="e">マウス イベントのデータ。</param>
    private void ResultsTree_PreviewMouseRightButtonDown(object sender, MouseButtonEventArgs e)
    {
        PrepareMsBuildMenu(FindFolderFromElement(e.OriginalSource as DependencyObject));
    }

    /// <summary>
    ///     フォルダーのドラッグ アウトに備えて、押した位置とフォルダーを記録します。チェック ボックス上では記録しません。
    /// </summary>
    /// <param name="sender">イベントの送信元。</param>
    /// <param name="e">マウス イベントのデータ。</param>
    private void ResultsTree_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        _treeDragFolder = FindFolderFromElement(e.OriginalSource as DependencyObject);
        _treeDragStart = e.GetPosition(null);
        if (FindAncestor<CheckBox>(e.OriginalSource as DependencyObject) != null)
            _treeDragFolder = null;
    }

    /// <summary>
    ///     フォルダーを一定距離ドラッグしたときに、直下のファイルを一時フォルダーにコピーして外部へドラッグします。
    /// </summary>
    /// <param name="sender">イベントの送信元。</param>
    /// <param name="e">マウス イベントのデータ。</param>
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
            ViewModel.ShowError(StringOverlay.Get("App_Unhandled"), ex);
        }
        finally
        {
            _treeDragPreparing = false;
        }
    }

    /// <summary>
    ///     ファイルのドラッグ アウトに備えて押した位置を記録し、ダブルクリックでは関連付けアプリで開きます。
    /// </summary>
    /// <param name="sender">イベントの送信元。</param>
    /// <param name="e">マウス イベントのデータ。</param>
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

    /// <summary>
    ///     ファイルを一定距離ドラッグしたときに、選択中のファイルを外部へドラッグします。
    /// </summary>
    /// <param name="sender">イベントの送信元。</param>
    /// <param name="e">マウス イベントのデータ。</param>
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

    /// <summary>
    ///     ファイルが一覧で選択されているかどうかを判定します。
    /// </summary>
    /// <param name="list">対象の一覧。</param>
    /// <param name="item">判定するファイル。</param>
    /// <returns>選択されている場合は true。</returns>
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

    /// <summary>
    ///     要素から親方向にたどり、対応するファイルの項目を探します。
    /// </summary>
    /// <param name="source">探し始める要素。</param>
    /// <returns>ファイルの項目。見つからない場合は null。</returns>
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

    /// <summary>
    ///     ビジュアル ツリーを親方向にたどり、指定した型の要素を探します。
    /// </summary>
    /// <typeparam name="T">探す要素の型。</typeparam>
    /// <param name="current">探し始める要素。</param>
    /// <returns>見つかった要素。見つからない場合は null。</returns>
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

    /// <summary>
    ///     フォルダーのコンテキスト メニューを開くときに、MSBuild メニューを準備して表示項目を調整します。
    /// </summary>
    /// <param name="sender">イベントの送信元。</param>
    /// <param name="e">イベントデータ。</param>
    private void ResultsTree_ContextMenuOpening(object sender, ContextMenuEventArgs e)
    {
        var source = e.OriginalSource as FrameworkElement;
        var folder = FindFolderFromElement(source);
        PrepareMsBuildMenu(folder);
        var menu = FindContextMenu(source);
        ApplyMsBuildMenuVisibility(menu);
    }

    /// <summary>
    ///     フォルダーが属するソリューションのビルド構成から、Build と Rebuild のメニュー項目を作成します。
    /// </summary>
    /// <param name="folder">対象のフォルダー。</param>
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

    /// <summary>
    ///     Solution タブでビルドできる場合だけ、MSBuild のメニュー項目を表示します。
    /// </summary>
    /// <param name="menu">対象のコンテキスト メニュー。</param>
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

    /// <summary>
    ///     タグが一致するメニュー項目を探します。
    /// </summary>
    /// <param name="menu">対象のメニュー。</param>
    /// <param name="tag">探すタグ。</param>
    /// <returns>見つかったメニュー項目。見つからない場合は null。</returns>
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

    /// <summary>
    ///     タグが一致する区切り線を探します。
    /// </summary>
    /// <param name="menu">対象のメニュー。</param>
    /// <param name="tag">探すタグ。</param>
    /// <returns>見つかった区切り線。見つからない場合は null。</returns>
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

    /// <summary>
    ///     指定したビルド構成でソリューションを MSBuild でビルド (またはリビルド) し、コンソールで実行します。
    /// </summary>
    /// <param name="solutionFile">ソリューション ファイル。</param>
    /// <param name="configurationPair">「構成|プラットフォーム」の組み合わせ。</param>
    /// <param name="target">MSBuild のターゲット (Build または Rebuild)。</param>
    private void RunMsBuild(string solutionFile, string configurationPair, string target)
    {
        if (ViewModel.TreeViewIndex != 1) return;
        if (string.IsNullOrWhiteSpace(solutionFile) || !File.Exists(solutionFile))
        {
            ViewModel.ShowError(StringOverlay.Get("Main_MsBuildMissingSln"),
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
            ViewModel.ShowError(StringOverlay.Get("Main_MsBuildNotFound"),
                new FileNotFoundException("MSBuild.exe"));
            return;
        }

        var command = "\"" + msbuild + "\" \"" + solutionFile + "\" /t:" + target
                      + " /p:Configuration=\"" + configuration + "\" /p:Platform=\"" + platform + "\" /m";
        var workDir = Path.GetDirectoryName(solutionFile);
        try
        {
            StartKeepOpenConsole(workDir, command);
            ViewModel.SetStatus("Main_MsBuildStarted", target,
                configurationPair);
        }
        catch (Exception ex)
        {
            ViewModel.ShowError(StringOverlay.Get("Main_MsBuildFailed"), ex);
        }
    }

    /// <summary>
    ///     指定したビルド構成でソリューションを dotnet build でビルドし、コンソールで実行します。
    /// </summary>
    /// <param name="solutionFile">ソリューション ファイル。</param>
    /// <param name="configurationPair">「構成|プラットフォーム」の組み合わせ。</param>
    /// <param name="rebuild">リビルドする場合は true。</param>
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
            ViewModel.SetStatus(rebuild ? "Main_SdkRebuildStarted" : "Main_SdkBuildStarted", configurationPair);
        }
        catch (Exception ex)
        {
            ViewModel.ShowError(StringOverlay.Get("Main_SdkBuildFailed"), ex);
        }
    }

    /// <summary>
    ///     vswhere.exe で MSBuild.exe を探し、見つからない場合は Visual Studio の既定のインストール先を探します。
    /// </summary>
    /// <returns>MSBuild.exe のパス。見つからない場合は null。</returns>
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
                // Ignore and fall back to manual search.
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

    /// <summary>
    ///     要素から親方向にたどり、対応するフォルダーの項目を探します。
    /// </summary>
    /// <param name="source">探し始める要素。</param>
    /// <returns>フォルダーの項目。見つからない場合は null。</returns>
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

    /// <summary>
    ///     要素から親方向にたどり、設定されているコンテキスト メニューを探します。
    /// </summary>
    /// <param name="source">探し始める要素。</param>
    /// <returns>コンテキスト メニュー。見つからない場合は null。</returns>
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

    /// <summary>
    ///     フォルダーから親方向に最大 8 階層たどり、最初に見つかった .sln を返します。
    /// </summary>
    /// <param name="directory">探し始めるフォルダー。</param>
    /// <returns>ソリューション ファイルのパス。見つからない場合は null。</returns>
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

    /// <summary>
    ///     ネットワーク上のツリーで展開したフォルダーの子を遅延読み込みします。
    /// </summary>
    /// <param name="sender">イベントの送信元。</param>
    /// <param name="e">イベントデータ。</param>
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
            ViewModel.ShowError(StringOverlay.Get("App_Unhandled"), ex);
        }
    }

    /// <summary>
    ///     ツリーで選択したフォルダーを現在のフォルダーにし、ファイル一覧を読み込みます。
    /// </summary>
    /// <param name="sender">選択が変わったツリー。</param>
    /// <param name="e">選択変更のイベントデータ。</param>
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
            ViewModel.ShowError(StringOverlay.Get("App_Unhandled"), ex);
        }
    }

    /// <summary>
    ///     ファイルを選択したときに、そのファイルを含むフォルダーをツリーで表示します。右クリック時は除きます。
    /// </summary>
    /// <param name="sender">対象の一覧。</param>
    /// <param name="e">選択変更のイベントデータ。</param>
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

    /// <summary>
    ///     指定したフォルダーに最も近いフォルダーを、現在のツリーで表示します。
    /// </summary>
    /// <param name="directory">フォルダーのパス。</param>
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

    /// <summary>
    ///     ルートから対象フォルダーまでを順に展開して表示するよう、処理を開始します。
    /// </summary>
    /// <param name="target">表示するフォルダー。</param>
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

    /// <summary>
    ///     仮想化されたツリーで、パスの各階層の項目が生成されるのを待ちながら 1 階層ずつ展開します。
    /// </summary>
    /// <param name="host">現在の階層の項目を持つ要素。</param>
    /// <param name="path">ルートから対象フォルダーまでのフォルダーの並び。</param>
    /// <param name="level">展開する階層。</param>
    /// <param name="retry">項目の生成待ちの再試行回数。</param>
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

    /// <summary>
    ///     仮想化パネルで、指定したインデックスの項目を生成して表示させます。
    /// </summary>
    /// <param name="host">項目を持つ要素。</param>
    /// <param name="index">項目のインデックス。</param>
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

    /// <summary>
    ///     ビジュアル ツリーから指定した型の子孫要素を探します。
    /// </summary>
    /// <typeparam name="T">探す要素の型。</typeparam>
    /// <param name="root">探し始める要素。</param>
    /// <returns>見つかった要素。見つからない場合は null。</returns>
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

    /// <summary>
    ///     ツリーの項目を選択し、見えるようスクロールします。
    /// </summary>
    /// <param name="tree">対象のツリー。</param>
    /// <param name="item">表示する項目。</param>
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

    /// <summary>
    ///     ビジュアル ツリーから、縦スクロールできる ScrollViewer を探します。
    /// </summary>
    /// <param name="root">探し始める要素。</param>
    /// <returns>見つかった ScrollViewer。見つからない場合は null。</returns>
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

    /// <summary>
    ///     コマンドライン引数または保存された設定から、メイン ウィンドウの位置・サイズ・状態を復元します。
    /// </summary>
    /// <param name="arguments">コマンドライン引数。</param>
    private void RestoreWindowPlacement(string[] arguments)
    {
        double left, top = 0, width = 0, height = 0;
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

    /// <summary>
    ///     メイン ウィンドウの通常時の位置・サイズと状態を保存します。
    /// </summary>
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

    /// <summary>
    ///     子ウィンドウをメイン ウィンドウと同じ位置・サイズで重ねて表示します。
    /// </summary>
    /// <param name="child">対象の子ウィンドウ。</param>
    private void OverlayOnMain(Window child)
    {
        if (child == null) return;
        var bounds = _window.WindowState == WindowState.Normal
            ? new Rect(_window.Left, _window.Top, _window.Width, _window.Height)
            : _window.RestoreBounds;
        ApplyPlacement(child, bounds.Left, bounds.Top, bounds.Width, bounds.Height,
            _window.WindowState == WindowState.Minimized ? (int)WindowState.Normal : (int)_window.WindowState);
    }

    /// <summary>
    ///     ウィンドウの位置・サイズ・状態を、画面内に収まるよう調整して適用します。
    /// </summary>
    /// <param name="window">対象のウィンドウ。</param>
    /// <param name="left">左端。</param>
    /// <param name="top">上端。</param>
    /// <param name="width">幅。</param>
    /// <param name="height">高さ。</param>
    /// <param name="state">ウィンドウの状態。</param>
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

    /// <summary>
    ///     コマンドライン引数から、指定した名前に続く数値を読み取ります。
    /// </summary>
    /// <param name="arguments">コマンドライン引数。</param>
    /// <param name="name">引数名。</param>
    /// <param name="value">読み取った値。</param>
    /// <returns>読み取れた場合は true。</returns>
    private static bool TryReadArgument(string[] arguments, string name, out double value)
    {
        value = 0;
        for (var index = 0; index < arguments.Length - 1; index++)
            if (string.Equals(arguments[index], name, StringComparison.OrdinalIgnoreCase))
                return double.TryParse(arguments[index + 1], NumberStyles.Float,
                    CultureInfo.InvariantCulture, out value);
        return false;
    }

    /// <summary>
    ///     Developer Differencer ウィンドウを開きます。すでに開いている場合は前面に表示します。
    /// </summary>
    private void DeveloperDifferencer()
    {
        if (_differencerWindow == null)
        {
            _differencerWindow = new DeveloperDifferencerWindow();
            _differencerWindow.Closed += (_, _) => _differencerWindow = null;
            OverlayOnMain(_differencerWindow);
            _differencerWindow.Show();
        }
        else
        {
            OverlayOnMain(_differencerWindow);
            WindowActivationService.BringToFront(_differencerWindow);
        }
    }

    /// <summary>
    ///     ビルド構成名 (Debug、Release、その他) に応じたメニュー アイコンの記号を取得します。
    /// </summary>
    /// <param name="pair">「構成|プラットフォーム」の組み合わせ。</param>
    /// <returns>Segoe MDL2 Assets の記号。</returns>
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

    /// <summary>
    ///     指定した検索範囲で Grep ウィンドウを開きます。すでに開いている場合は検索範囲を置き換えて前面に表示します。
    /// </summary>
    /// <param name="scopes">検索範囲のフォルダー。null または空の場合は選択中のフォルダー。</param>
    private void OpenGrep(IReadOnlyList<string> scopes)
    {
        if (scopes == null || scopes.Count == 0)
            scopes = ViewModel.PrepareGrepScopes();
        if (scopes.Count == 0) return;
        if (_grepWindow == null)
        {
            _grepWindow = new GrepWindow(ViewModel.GetSelectedGrepScopes, scopes);
            _grepWindow.Closed += (_, _) => _grepWindow = null;
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

    public sealed class MsBuildActionItem
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

    /// <summary>
    ///     ビルド構成のメニュー項目のスタイルを作成します。
    /// </summary>
    /// <returns>作成したスタイル。</returns>
    private static Style CreateBuildConfigurationItemStyle()
    {
        var style = new Style(typeof(MenuItem))
        {
            BasedOn = (Style)Application.Current.FindResource(typeof(MenuItem))
        };

        style.Setters.Add(new Setter(HeaderedItemsControl.HeaderProperty, new Binding(nameof(MsBuildActionItem.Header))));
        style.Setters.Add(new Setter(MenuItem.CommandProperty, new Binding(nameof(MsBuildActionItem.Command))));
        style.Setters.Add(new Setter(MenuItem.IconProperty, new Binding(nameof(MsBuildActionItem.MenuIcon))));

        return style;
    }
}
