using DesktopIniManager.Properties;
using DesktopIniManager.Services;
using DesktopIniManager.ViewModels;
using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.Versioning;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;

namespace DesktopIniManager.Views;

[SupportedOSPlatform("windows")]
public partial class DeveloperDifferencerWindow
{
    internal DeveloperDifferencerViewModel ViewModel { get; }

    internal bool IsWorking
    { get { return ViewModel.IsBusy; } }

    private Point _zipDragStart;
    private bool _zipDragFromSource;
    private bool _zipDragging;
    private bool _zipDragArmed;
    private Task _historyLoad = Task.CompletedTask;
    private Task<bool> _closePreparation;
    private bool _closeReady;

    /// <summary>Initializes a new developer differencer window.</summary>
    public DeveloperDifferencerWindow()
    {
        ViewModel = new DeveloperDifferencerViewModel(new UserDialogService(this), Dispatcher,
            ShowSyncConfirmation, (direction, currentSnapshot) => new SynchronizationLogWindow(this, direction, currentSnapshot));
        InitializeComponent();
        Icon = DifferencerStatusIcons.GetCustomIcon(51) ?? Icon;
        RestoreGridLayout();
        DataContext = ViewModel;
        SourceBox.HistoryItemApplied += (_, _) => ViewModel.MatchTargetHistory();
        TargetBox.HistoryItemApplied += (_, _) => ViewModel.CancelTargetHistoryMatch();
        SourceLabelIcon.MouseLeftButtonUp += (_, _) => ViewModel.RescanRoot(true);
        TargetLabelIcon.MouseLeftButtonUp += (_, _) => ViewModel.RescanRoot(false);
        ViewModel.ChooseCleanSolutions = ChooseCleanSolutions;
        ViewModel.CleanReportRequested += ShowCleanReport;
        ViewModel.ChooseFolder = (initialPath, title) =>
            NativeFolderPicker.Show(new WindowInteropHelper(this).Handle, initialPath, title);
        ViewModel.CloseRequested += Close;
        ViewModel.DiffRequested += (snapshot, file) =>
        {
            var diff = new DiffViewWindow(snapshot, file) { Owner = this };
            diff.MatchOwnerSize();
            diff.Show();
        };
        ViewModel.FolderRevealRequested += ScheduleFolderIntoView;
        ViewModel.FileProgressRequested += ScrollToProgressFile;
        ViewModel.ComparisonCompleted += () =>
        {
            _pendingProgressRow = null;
            Dispatcher.BeginInvoke(new Action(() =>
            {
                FindScrollViewer(FolderTree)?.ScrollToTop();
                if (FilesGrid.Items.Count == 0) return;
                FilesGrid.SelectedIndex = 0;
                ViewModel.SelectedRow = FilesGrid.SelectedItem as DiffRow;
                FilesGrid.ScrollIntoView(FilesGrid.SelectedItem);
                FindScrollViewer(FilesGrid)?.ScrollToTop();
            }), DispatcherPriority.ContextIdle);
        };
        ViewModel.CommitBrowsedRootHistoryRequested += source =>
        {
            if (source) SourceBox.CommitHistory();
            else TargetBox.CommitHistory();
        };
        ViewModel.CommitRootHistoryRequested += () => { SourceBox.CommitHistory(); TargetBox.CommitHistory(); };
        ViewModel.SyncDirectionIconRequested += UpdateSyncDirectionIcons;
        ViewModel.PropertyChanged += (_, e) => { if (e.PropertyName == nameof(ViewModel.IsFileBusy)) { if (ViewModel.IsFileBusy) RestartPanelProgress(); else StopPanelProgress(); } };
        SameFilterIcon.Source = DifferencerStatusIcons.GetFileIcon(DiffKind.Same);
        DifferentFilterIcon.Source = DifferencerStatusIcons.GetFileIcon(DiffKind.Different);
        SourceOnlyFilterIcon.Source = DifferencerStatusIcons.GetFileIcon(DiffKind.SourceOnly);
        TargetOnlyFilterIcon.Source = DifferencerStatusIcons.GetFileIcon(DiffKind.TargetOnly);
        RefreshCompareIcon.Source = DifferencerStatusIcons.GetRefreshIcon();
        ObjFilterIcon.Source = DifferencerStatusIcons.GetBuildFolderIcon(true);
        BinFilterIcon.Source = DifferencerStatusIcons.GetBuildFolderIcon(false);
        ApplyToolbarIcons();
        ViewModel.SelectionCountChanged += UpdateZipStageIcons;
        if (ZipSourceButton != null)
        {
            ZipSourceButton.PreviewMouseLeftButtonDown += ZipButton_PreviewMouseLeftButtonDown;
            ZipSourceButton.PreviewMouseMove += ZipButton_PreviewMouseMove;
        }
        if (ZipTargetButton != null)
        {
            ZipTargetButton.PreviewMouseLeftButtonDown += ZipButton_PreviewMouseLeftButtonDown;
            ZipTargetButton.PreviewMouseMove += ZipButton_PreviewMouseMove;
        }
        if (NodeExpandToggle != null)
        {
            NodeExpandToggle.Checked += NodeExpandToggle_Changed;
            NodeExpandToggle.Unchecked += NodeExpandToggle_Changed;
        }
        if (TreeDensityToggle != null)
        {
            TreeDensityToggle.IsChecked = !ViewModel.TreeCompact;
            TreeDensityToggle.Checked += TreeDensityToggle_Changed;
            TreeDensityToggle.Unchecked += TreeDensityToggle_Changed;
        }
        // HistoryTextBox persists Source/Target via HistoryKey.
        Closing += async (s, e) =>
        {
            if (_closeReady) return;
            e.Cancel = true;
            if (ViewModel.IsBusy || _closePreparation != null) return;
            if (await PrepareCloseAsync()) _ = Dispatcher.BeginInvoke(new Action(Close));
        };
        Closed += (_, _) =>
        {
            StringOverlay.CultureChanged -= OnCultureChanged;
            ViewModel.Close();
        };
        AllowDrop = true;
        DropZoneHighlight.Attach(this, () => !_zipDragging, SourceBox, TargetBox);
        PreviewDragEnter += FolderDropPreview;
        PreviewDragOver += FolderDropPreview;
        PreviewDrop += Differencer_Drop;
        Loaded += async (_, _) =>
        {
            IsEnabled = false;
            ViewModel.SetFilePanelBusy(false);
            try { await (_historyLoad = ViewModel.RestoreHistoryAsync()); }
            finally { if (_closePreparation == null) IsEnabled = true; }
        };
        StringOverlay.CultureChanged += OnCultureChanged;
        ViewModel.RestoreState();
    }

    private DiffRow _pendingProgressRow;

    /// <summary>
    ///     .gitignore ルールの除外設定ウィンドウを表示し、変更があれば設定を反映します。
    /// </summary>
    /// <param name="sender">イベントの送信元。</param>
    /// <param name="e">イベントデータ。</param>
    private async void IgnoreListButton_Click(object sender, RoutedEventArgs e)
    {
        if (!ViewModel.CanConfigureIgnore) return;
        DiffSnapshot ignoreSnapshot;
        try { ignoreSnapshot = await ViewModel.GetIgnoreSnapshotAsync(); }
        catch (Exception ex) { ViewModel.ShowError(ex); return; }
        if (ignoreSnapshot == null) return;
        var dialog = new IgnoreRulesWindow(ignoreSnapshot) { Owner = this };
        if (dialog.ShowDialog() == true)
        {
            try { await ViewModel.ApplyIgnoreRulesAsync(dialog.DisabledRules); }
            catch (Exception ex) { ViewModel.ShowError(ex); }
        }
    }

    private bool _progressScrollPending;

    /// <summary>
    ///     比較中に処理しているファイルの行が見えるよう、まとめてスクロールします。
    /// </summary>
    /// <param name="row">表示する行。</param>
    private void ScrollToProgressFile(DiffRow row)
    {
        _pendingProgressRow = row;
        if (_progressScrollPending || row == null) return;
        _progressScrollPending = true;
        Dispatcher.BeginInvoke(new Action(() =>
        {
            _progressScrollPending = false;
            DiffRow latest = _pendingProgressRow;
            if (latest == null || !FilesGrid.Items.Contains(latest)) return;
            FilesGrid.ScrollIntoView(latest);
            FindScrollViewer(FilesGrid)?.ScrollToEnd();
        }), DispatcherPriority.Background);
    }

    /// <summary>
    ///     フォルダーのドラッグ中にコピーのカーソルを表示します。ZIP のドラッグ中はドロップを受け付けません。
    /// </summary>
    /// <param name="sender">イベントの送信元。</param>
    /// <param name="e">ドラッグ イベントのデータ。</param>
    private void FolderDropPreview(object sender, DragEventArgs e)
    {
        if (_zipDragging)
        {
            e.Effects = DragDropEffects.None;
            e.Handled = true;
            return;
        }

        if (e.Data == null || !e.Data.GetDataPresent(DataFormats.FileDrop))
            return;
        e.Effects = DragDropEffects.Copy;
        e.Handled = true;
    }

    /// <summary>
    ///     ドロップされたフォルダーを、ドロップ位置に応じて比較元または比較先に設定します。
    /// </summary>
    /// <param name="sender">イベントの送信元。</param>
    /// <param name="e">ドラッグ イベントのデータ。</param>
    private void Differencer_Drop(object sender, DragEventArgs e)
    {
        if (_zipDragging)
        {
            e.Effects = DragDropEffects.None;
            e.Handled = true;
            return;
        }

        List<string> folders = FoldersFromDrop(e.Data);
        if (folders.Count == 0) return;

        Point point = e.GetPosition(this);
        bool toTarget = IsOver(TargetBox, point) || (!IsOver(SourceBox, point) && point.X >= ActualWidth / 2.0);
        if (toTarget)
        {
            ViewModel.ApplyRootPath(false, folders[0]);
            ViewModel.CancelTargetHistoryMatch();
            TargetBox.CommitHistory();
        }
        else
        {
            ViewModel.ApplyRootPath(true, folders[0]);
            ViewModel.MatchTargetHistory();
            SourceBox.CommitHistory();
        }
        e.Handled = true;
    }

    /// <summary>
    ///     ウィンドウ上の位置が要素の範囲内にあるかどうかを判定します。
    /// </summary>
    /// <param name="element">判定する要素。</param>
    /// <param name="windowPoint">ウィンドウ上の位置。</param>
    /// <returns>範囲内の場合は true。</returns>
    private bool IsOver(FrameworkElement element, Point windowPoint)
    {
        if (element == null) return false;
        try
        {
            Point origin = element.TransformToAncestor(this).Transform(new Point(0, 0));
            return new Rect(origin, element.RenderSize).Contains(windowPoint);
        }
        catch (InvalidOperationException)
        {
            return false;
        }
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

    /// <summary>
    ///     表示言語が変わったときに、進捗のアニメーションを再開します。
    /// </summary>
    /// <param name="sender">イベントの送信元。</param>
    /// <param name="e">イベントデータ。</param>
    private void OnCultureChanged(object sender, EventArgs e)
    {
        if (ViewModel.IsFileBusy) RestartPanelProgress();
        if (ViewModel.IsProgressVisible && ViewModel.ProgressIndeterminate)
        {
            ViewModel.ProgressIndeterminate = false;
            ViewModel.ProgressIndeterminate = true;
        }
    }

    /// <summary>
    ///     ファイル一覧の行が読み込まれたときに、プレビューの読み込みを開始します。
    /// </summary>
    /// <param name="sender">対象の行。</param>
    /// <param name="e">イベントデータ。</param>
    private void FileRowLoaded(object sender, RoutedEventArgs e)
    {
        var item = (ListViewItem)sender;
        item.DataContextChanged -= FileRowContextChanged;
        item.DataContextChanged += FileRowContextChanged;
        LoadFileRow(item);
    }

    /// <summary>
    ///     ファイル一覧の行がアンロードされたときに、プレビューを解放します。
    /// </summary>
    /// <param name="sender">対象の行。</param>
    /// <param name="e">イベントデータ。</param>
    private void FileRowUnloaded(object sender, RoutedEventArgs e)
    {
        var item = (ListViewItem)sender;
        item.DataContextChanged -= FileRowContextChanged;
        (item.Tag as DiffRow)?.ReleasePreview(); item.Tag = null;
    }

    /// <summary>
    ///     行の表示内容が変わったときに、プレビューを読み込み直します。
    /// </summary>
    /// <param name="sender">対象の行。</param>
    /// <param name="e">イベントデータ。</param>
    private void FileRowContextChanged(object sender, DependencyPropertyChangedEventArgs e)
    { if (((ListViewItem)sender).IsLoaded) LoadFileRow((ListViewItem)sender); }

    /// <summary>
    ///     行のプレビューを読み込みます。前に表示していた行のプレビューは解放します。
    /// </summary>
    /// <param name="item">対象の行。</param>
    private async void LoadFileRow(ListViewItem item)
    {
        if (ReferenceEquals(item.Tag, item.DataContext)) return;
        (item.Tag as DiffRow)?.ReleasePreview();
        var row = item.DataContext as DiffRow; item.Tag = row;
        await ViewModel.LoadPreviewAsync(row);
    }

    private bool _changingFolderSelection;

    /// <summary>
    ///     フォルダー ツリーの選択が変わったときに、選択フォルダーを ViewModel に反映します。
    /// </summary>
    /// <param name="sender">イベントの送信元。</param>
    /// <param name="e">選択変更のイベントデータ。</param>
    private void FolderChanged(object sender, RoutedPropertyChangedEventArgs<object> e)
    {
        var folder = e.NewValue as DiffFolder;
        if (folder == null || _changingFolderSelection) return;
        // The pinned root and scrolling descendants have separate TreeViews.
        _changingFolderSelection = true;
        try
        {
            foreach (var node in ViewModel.Folders.Values) node.Active = ReferenceEquals(node, folder);
            ViewModel.SelectFolder(folder.Path);
        }
        finally { _changingFolderSelection = false; }
    }

    /// <summary>
    ///     指定したフォルダーがツリーで見えるよう、描画の前後に分けてスクロールします。
    /// </summary>
    /// <param name="path">ルートから対象フォルダーまでのフォルダーの並び。</param>
    private void ScheduleFolderIntoView(IReadOnlyList<DiffFolder> path)
    {
        Action bring = () => ScrollTreeItemIntoView(FolderTree, ContainerAlongPath(FolderTree, path));
        bring();
        Dispatcher.BeginInvoke(bring, DispatcherPriority.Loaded);
        Dispatcher.BeginInvoke(new Action(() =>
        {
            try { bring(); }
            finally { ViewModel.CompleteFolderReveal(); }
        }), DispatcherPriority.ContextIdle);
    }

    /// <summary>
    ///     フォルダーの並びに沿ってツリーを展開しながら、対象フォルダーの TreeViewItem を探します。
    /// </summary>
    /// <param name="parent">探し始めるツリー。</param>
    /// <param name="path">ルートから対象フォルダーまでのフォルダーの並び。</param>
    /// <returns>見つかった TreeViewItem。見つからない場合は null。</returns>
    private static TreeViewItem ContainerAlongPath(ItemsControl parent, IReadOnlyList<DiffFolder> path)
    {
        if (parent == null) return null;
        TreeViewItem current = null;
        ItemsControl host = parent;
        foreach (DiffFolder node in path)
        {
            if (node.Path.Length == 0) continue; // Root is displayed in the fixed tree.
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

    /// <summary>
    ///     ツリーの項目が見えるようスクロールします。
    /// </summary>
    /// <param name="tree">対象のツリー。</param>
    /// <param name="item">表示する項目。</param>
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

    /// <summary>
    ///     ビジュアル ツリーから ScrollViewer を探します。
    /// </summary>
    /// <param name="root">探し始める要素。</param>
    /// <returns>見つかった ScrollViewer。見つからない場合は null。</returns>
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

    /// <summary>
    ///     ファイル一覧の処理中アニメーションを停止します。
    /// </summary>
    private void StopPanelProgress()
    {
        if (FilePanelMarquee == null) return;
        var transform = FilePanelMarquee.RenderTransform as TranslateTransform;
        if (transform == null) return;
        transform.BeginAnimation(TranslateTransform.XProperty, null);
        transform.X = 0;
    }

    /// <summary>
    ///     ファイル一覧の処理中アニメーションを最初から再開します。
    /// </summary>
    private void RestartPanelProgress()
    {
        Dispatcher.BeginInvoke(new Action(() =>
        {
            try
            {
                if (FilePanelMarquee == null) return;
                if (FilePanelProgress != null) FilePanelProgress.ClipToBounds = true;
                var transform = FilePanelMarquee.RenderTransform as TranslateTransform ?? new TranslateTransform();
                FilePanelMarquee.RenderTransform = transform;
                transform.BeginAnimation(TranslateTransform.XProperty, null);
                double track = FilePanelProgress != null && FilePanelProgress.ActualWidth > 0 ? FilePanelProgress.ActualWidth : 160;
                double thumb = FilePanelMarquee.ActualWidth > 1 ? FilePanelMarquee.ActualWidth : 48;
                double distance = Math.Max(8, track - thumb);
                var animation = new DoubleAnimation(0, distance, TimeSpan.FromSeconds(1.4))
                { AutoReverse = true, RepeatBehavior = RepeatBehavior.Forever };
                transform.BeginAnimation(TranslateTransform.XProperty, animation, HandoffBehavior.SnapshotAndReplace);
            }
            catch (InvalidOperationException) { }
        }), DispatcherPriority.Loaded);
    }

    /// <summary>
    ///     クリーンするソリューションとビルド構成を選ぶダイアログを表示します。
    /// </summary>
    /// <param name="solutions">候補のソリューション ファイル。</param>
    /// <param name="source">比較元のルート。</param>
    /// <returns>選択結果。取り消した場合は null。</returns>
    private SolutionCleanSelection ChooseCleanSolutions(IReadOnlyList<string> solutions, string source)
        => CleanSolutionsWindow.Choose(this, solutions, source);

    /// <summary>
    ///     Solution Clean の結果を表示します。
    /// </summary>
    /// <param name="summary">結果の概要。</param>
    /// <param name="logPath">ログ ファイルのパス。</param>
    /// <param name="log">ログの内容。</param>
    private void ShowCleanReport(string summary, string logPath, string log)
        => CleanReportWindow.Show(this, summary, logPath, log);

    /// <summary>
    ///     同期または ZIP 出力の確認ダイアログを表示します。
    /// </summary>
    /// <param name="direction">処理の方向の表示文字列。</param>
    /// <param name="files">対象のファイル。</param>
    /// <param name="folders">対象のフォルダー。</param>
    /// <param name="toTarget">比較元から比較先への処理の場合は true。</param>
    /// <param name="zipMode">ZIP 出力の場合は true。</param>
    /// <param name="zipFileName">指定された ZIP ファイル名。</param>
    /// <param name="zipFolder">指定された ZIP の出力先フォルダー。</param>
    /// <returns>実行する場合は true。</returns>
    private bool ShowSyncConfirmation(string direction, DiffFile[] files, DiffFolderSync[] folders, bool toTarget, bool zipMode, out string zipFileName, out string zipFolder)
    {
        return SynchronizeConfirmWindow.Confirm(this, direction, files, folders, toTarget, ViewModel.Snapshot.SourceRoot, ViewModel.Snapshot.TargetRoot, zipMode, out zipFileName, out zipFolder);
    }

    /// <summary>
    ///     ファイルの比較結果を最新の状態に更新します。
    /// </summary>
    /// <param name="file">更新するファイル。</param>
    /// <returns>更新できた場合に true を返すタスク。</returns>
    internal Task<bool> RefreshFileAsync(DiffFile file) => ViewModel.RefreshFileAsync(file);

    /// <summary>
    ///     ファイル一覧に表示されている、DIFF ビューで比較できるファイルを取得します。
    /// </summary>
    /// <returns>比較できるファイルの一覧。</returns>
    internal IReadOnlyList<DiffFile> GetVisibleComparableFiles() => ViewModel.GetVisibleComparableFiles();

    /// <summary>
    ///     DIFF ビューで最後に表示したファイルを一覧で選択し、見える位置にスクロールします。
    /// </summary>
    /// <param name="file">選択するファイル。</param>
    internal void SelectLastViewedFile(DiffFile file)
    {
        DiffRow row = ViewModel.SelectDisplayedFile(file);
        if (row == null) return;
        // Single-selection lists replace the selection through SelectedItem.
        FilesGrid.SelectedItem = row;
        ViewModel.SelectedRow = row;
        Dispatcher.BeginInvoke(new Action(() =>
        {
            FilesGrid.UpdateLayout();
            FilesGrid.ScrollIntoView(row);
        }), DispatcherPriority.Loaded);
    }

    /// <summary>
    ///     比較の状態と一覧のレイアウトを保存します。
    /// </summary>
    internal void SaveState()
    {
        ViewModel.SaveState();
        SaveGridLayout();
    }

    /// <summary>
    ///     閉じる前の保存処理を 1 回だけ開始します。
    /// </summary>
    /// <returns>閉じてよい場合に true を返すタスク。</returns>
    internal Task<bool> PrepareCloseAsync() => _closePreparation ??= SaveForCloseAsync();

    /// <summary>
    ///     履歴の読み込み完了を待ってから、レイアウトと比較履歴を保存します。
    /// </summary>
    /// <returns>閉じてよい場合に true を返すタスク。</returns>
    private async Task<bool> SaveForCloseAsync()
    {
        IsEnabled = false;
        try
        {
            await _historyLoad;
            SaveGridLayout();
            await ViewModel.SaveStateAsync();
            _closeReady = true;
            return true;
        }
        catch (Exception ex)
        {
            ViewModel.ShowError(ex);
            _ = Dispatcher.BeginInvoke(new Action(() => _closePreparation = null));
            IsEnabled = true;
            return false;
        }
    }

    /// <summary>
    ///     保存されたペイン幅と列幅を復元します。
    /// </summary>
    private void RestoreGridLayout()
    {
        var values = SettingsService.LoadDifferencerLayout();
        if (FilesGrid.View is not GridView grid || values == null || values.Length != grid.Columns.Count + 2) return;
        if (values[0] > 0 && values[1] > 0)
        {
            FolderPaneColumn.Width = new GridLength(values[0], GridUnitType.Star);
            FilePaneColumn.Width = new GridLength(values[1], GridUnitType.Star);
        }
        for (int i = 0; i < grid.Columns.Count; i++) grid.Columns[i].Width = values[i + 2];
    }

    /// <summary>
    ///     現在のペイン幅と列幅を保存します。
    /// </summary>
    private void SaveGridLayout()
    {
        if (FilesGrid.View is not GridView grid || FolderPaneColumn.ActualWidth <= 0 || FilePaneColumn.ActualWidth <= 0) return;
        var values = new double[grid.Columns.Count + 2];
        values[0] = FolderPaneColumn.ActualWidth;
        values[1] = FilePaneColumn.ActualWidth;
        for (int i = 0; i < grid.Columns.Count; i++)
        {
            var column = grid.Columns[i];
            values[i + 2] = double.IsNaN(column.Width) ? column.ActualWidth : column.Width;
            if (!double.IsFinite(values[i + 2]) || values[i + 2] < 0) return;
        }
        SettingsService.SaveDifferencerLayout(values);
    }

    /// <summary>
    ///     Same のファイルだけのフォルダーは手動でチェックできないため、チェックをクリックしたときは選択済みのファイルを解除するだけにします。
    /// </summary>
    /// <param name="sender">対象のチェック ボックス。</param>
    /// <param name="e">マウス イベントのデータ。</param>
    private void FolderCheckBox_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (!(sender is CheckBox checkBox) || !(checkBox.DataContext is DiffFolder folder) || folder.CanSelect)
            return;

        // Same-only folders cannot be checked manually.
        // If the file-list ON/OFF button selected their files, clicking the folder check only clears them.
        if (checkBox.IsChecked != false)
            ViewModel.ClearSameFolderSelection(folder);

        e.Handled = true;
    }

    /// <summary>
    ///     ファイル一覧の行のダブルクリックで DIFF ビューを開きます。見出しやスクロール バーでは開きません。
    /// </summary>
    /// <param name="sender">イベントの送信元。</param>
    /// <param name="e">マウス イベントのデータ。</param>
    private void OpenDiff(object sender, MouseButtonEventArgs e)
    {
        // Keep the row hit test in the View so headers and scrollbars cannot open a viewer.
        if (e.OriginalSource is not DependencyObject originalSource ||
            ItemsControl.ContainerFromElement(FilesGrid, originalSource) is not ListViewItem) return;
        ViewModel.OpenDiffCommand.Execute(null);
    }

    /// <summary>
    ///     ツール バーの各ボタンにアイコンを設定します。
    /// </summary>
    private void ApplyToolbarIcons()
    {
        SetIcon(PatchViewIcon, 156);
        SetIcon(CloseButtonIcon, 26);
        SetIcon(SourceLabelIcon, 102);
        SetIcon(TargetLabelIcon, 103);
        SetIcon(BrowseSourceIcon, 74);
        SetIcon(BrowseTargetIcon, 75);
        SetIcon(CompareButtonIcon, 84);
        SetIcon(CancelCompareIcon, 24);
        SetIcon(CleanSolutionIcon, 83);
        SetIcon(SelectAllFilesIcon, 48);
        UpdateCompareModeIcons();
        SetIcon(ZipSourceButtonIcon, 93);
        SetIcon(ZipTargetButtonIcon, 92);
        SetIcon(CheckedOnlyIcon, 100);
        SetIcon(ForwardButtonIcon, 85);
        SetIcon(ReverseButtonIcon, 86);
        SetIcon(IgnoreListIcon, 139);
        UpdateNodeExpandIcon();
        UpdateDensityIcon();
    }

    /// <summary>
    ///     パッチ ファイルを選んで、比較先をリポジトリとして Patch View を開きます。
    /// </summary>
    /// <param name="sender">イベントの送信元。</param>
    /// <param name="e">イベントデータ。</param>
    private async void PatchViewButton_Click(object sender, RoutedEventArgs e)
    {
        if (ViewModel.IsBusy) return;
        var picker = new Microsoft.Win32.OpenFileDialog { Filter = StringOverlay.Get("Patch_FileFilter"), CheckFileExists = true };
        if (picker.ShowDialog(this) != true) return;
        PatchViewButton.IsEnabled = false;
        try
        {
            var patch = await Task.Run(() => PatchDocument.Read(picker.FileName));
            var snapshot = new DiffSnapshot { SourceRoot = Path.GetDirectoryName(picker.FileName), TargetRoot = ViewModel.TargetPath };
            var file = new DiffFile { RelativePath = Path.GetFileName(picker.FileName), Source = DiffStamp.Read(picker.FileName) };
            var viewer = new DiffViewWindow(snapshot, file, picker.FileName, "") { Owner = this };
            viewer.ViewModel.InitializePatch(patch, picker.FileName,
                Directory.Exists(ViewModel.TargetPath) ? ViewModel.TargetPath : null);
            viewer.MatchOwnerSize();
            viewer.Show();
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, ErrorMessages.English(ex), StringOverlay.Get("Patch_ViewTitle"), MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally { PatchViewButton.IsEnabled = true; }
    }

    /// <summary>
    ///     比較方法の切り替えに合わせてアイコンを更新します。
    /// </summary>
    /// <param name="sender">イベントの送信元。</param>
    /// <param name="e">イベントデータ。</param>
    private void CompareModeToggle_Changed(object sender, RoutedEventArgs e) => UpdateCompareModeIcons();

    /// <summary>
    ///     日時・サイズ・属性・精密比較の各トグルの状態に合わせてアイコンを更新します。
    /// </summary>
    private void UpdateCompareModeIcons()
    {
        if (CompareTimestampBox != null) SetIcon(CompareTimestampIcon, CompareTimestampBox.IsChecked == true ? 130 : 131);
        if (CompareSizeBox != null) SetIcon(CompareSizeIcon, CompareSizeBox.IsChecked == true ? 134 : 135);
        if (CompareAttributesBox != null) SetIcon(CompareAttributesIcon, CompareAttributesBox.IsChecked == true ? 132 : 133);
        if (PreciseCompareBox != null) SetIcon(PreciseCompareIcon, PreciseCompareBox.IsChecked == true ? 128 : 129);
    }

    /// <summary>
    ///     Image にアイコン ライブラリのアイコンを設定します。
    /// </summary>
    /// <param name="image">対象の Image。</param>
    /// <param name="index">アイコンのインデックス。</param>
    private static void SetIcon(Image image, int index)
    {
        if (image != null)
            image.Source = DifferencerStatusIcons.GetCustomIcon(index);
    }

    /// <summary>
    ///     同期または ZIP 出力の方向に合わせて、比較元・比較先のラベルのアイコンを更新します。
    /// </summary>
    /// <param name="toTarget">比較元から比較先への処理の場合は true。方向がない場合は null。</param>
    /// <param name="zipMode">ZIP 出力の場合は true。</param>
    private void UpdateSyncDirectionIcons(bool? toTarget, bool zipMode)
    {
        if (zipMode && toTarget == true)
        {
            SetIcon(SourceLabelIcon, 93);
            SetIcon(TargetLabelIcon, 60);
        }
        else if (zipMode && toTarget == false)
        {
            SetIcon(SourceLabelIcon, 61);
            SetIcon(TargetLabelIcon, 92);
        }
        else if (toTarget == true)
        {
            // Source -> Target
            SetIcon(SourceLabelIcon, 64);
            SetIcon(TargetLabelIcon, 79);
        }
        else if (toTarget == false)
        {
            // Target -> Source
            SetIcon(SourceLabelIcon, 76);
            SetIcon(TargetLabelIcon, 66);
        }
        else
        {
            // Normal
            SetIcon(SourceLabelIcon, 102);
            SetIcon(TargetLabelIcon, 103);
        }
    }

    /// <summary>
    ///     フォルダー ツリーのすべての展開と折りたたみを切り替えます。
    /// </summary>
    /// <param name="sender">イベントの送信元。</param>
    /// <param name="e">イベントデータ。</param>
    private void NodeExpandToggle_Changed(object sender, RoutedEventArgs e)
    {
        if (NodeExpandToggle.IsChecked == true) ViewModel.ExpandAllCommand.Execute(null);
        else ViewModel.CollapseAllCommand.Execute(null);
        UpdateNodeExpandIcon();
    }

    /// <summary>
    ///     フォルダー ツリーの表示密度を切り替えます。
    /// </summary>
    /// <param name="sender">イベントの送信元。</param>
    /// <param name="e">イベントデータ。</param>
    private void TreeDensityToggle_Changed(object sender, RoutedEventArgs e)
    {
        if (TreeDensityToggle.IsChecked == true) ViewModel.ComfortableTreeCommand.Execute(null);
        else ViewModel.CompactTreeCommand.Execute(null);
        UpdateDensityIcon();
    }

    /// <summary>
    ///     展開・折りたたみボタンのアイコンとツールチップを更新します。
    /// </summary>
    private void UpdateNodeExpandIcon()
    {
        bool expanded = NodeExpandToggle != null && NodeExpandToggle.IsChecked == true;
        SetIcon(NodeExpandIcon, expanded ? 56 : 55);
        if (NodeExpandToggle != null)
            NodeExpandToggle.ToolTip = expanded ? StringOverlay.Get("Common_Collapse") : StringOverlay.Get("Common_Expand");
    }

    /// <summary>
    ///     表示密度ボタンのアイコンとツールチップを更新します。
    /// </summary>
    private void UpdateDensityIcon()
    {
        bool compact = ViewModel.TreeCompact;
        SetIcon(TreeDensityIcon, compact ? 91 : 90);
        if (TreeDensityToggle != null)
            TreeDensityToggle.ToolTip = compact ? StringOverlay.Get("Main_TreeCompact") : StringOverlay.Get("Main_TreeComfortable");
    }

    /// <summary>
    ///     比較履歴タブのダブルクリックで、そのタブを削除します。
    /// </summary>
    /// <param name="sender">イベントの送信元。</param>
    /// <param name="e">マウス イベントのデータ。</param>
    private void HistoryTabStrip_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (sender is not ItemsControl owner || e.OriginalSource is not DependencyObject originalSource) return;
        var item = ItemsControl.ContainerFromElement(owner, originalSource) as ListBoxItem;
        if (item?.DataContext == null) return;
        if (ViewModel.DeleteHistoryTabCommand.CanExecute(item.DataContext))
            ViewModel.DeleteHistoryTabCommand.Execute(item.DataContext);
    }

    /// <summary>
    ///     すべての比較履歴タブを削除します。
    /// </summary>
    /// <param name="sender">イベントの送信元。</param>
    /// <param name="e">イベントデータ。</param>
    private void DeleteAllHistoryTabs_Click(object sender, RoutedEventArgs e)
    {
        if (ViewModel.DeleteAllHistoryTabsCommand.CanExecute(null))
            ViewModel.DeleteAllHistoryTabsCommand.Execute(null);
    }

    /// <summary>
    ///     メニューを開いたタブ以外の比較履歴タブを削除します。
    /// </summary>
    /// <param name="sender">イベントの送信元。</param>
    /// <param name="e">イベントデータ。</param>
    private void DeleteOtherHistoryTabs_Click(object sender, RoutedEventArgs e)
    {
        var menu = (sender as MenuItem)?.Parent as ContextMenu;
        object tab = (menu?.PlacementTarget as FrameworkElement)?.DataContext;
        if (tab != null && ViewModel.DeleteOtherHistoryTabsCommand.CanExecute(tab))
            ViewModel.DeleteOtherHistoryTabsCommand.Execute(tab);
    }

    /// <summary>
    ///     選択中のファイル数に応じて、ZIP 出力ボタンのアイコンを更新します。
    /// </summary>
    /// <param name="selectedFiles">選択中のファイル数。</param>
    private void UpdateZipStageIcons(int selectedFiles)
    {
        if (selectedFiles <= 0)
        {
            SetIcon(ZipSourceButtonIcon, 93);
            SetIcon(ZipTargetButtonIcon, 92);
            return;
        }
        if (selectedFiles <= 5)
        {
            SetIcon(ZipSourceButtonIcon, 98);
            SetIcon(ZipTargetButtonIcon, 96);
        }
        else
        {
            SetIcon(ZipSourceButtonIcon, 99);
            SetIcon(ZipTargetButtonIcon, 97);
        }
    }

    /// <summary>
    ///     ZIP 出力ボタンからのドラッグに備えて、押した位置と方向を記録します。
    /// </summary>
    /// <param name="sender">押されたボタン。</param>
    /// <param name="e">マウス イベントのデータ。</param>
    private void ZipButton_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        _zipDragArmed = true;
        _zipDragging = false;
        _zipDragFromSource = ReferenceEquals(sender, ZipSourceButton);
        _zipDragStart = e.GetPosition(null);
    }

    /// <summary>
    ///     ZIP 出力ボタンから一定距離ドラッグしたときに、選択ファイルを構造を保ったまま一時フォルダーにコピーして外部へドラッグします。
    /// </summary>
    /// <param name="sender">イベントの送信元。</param>
    /// <param name="e">マウス イベントのデータ。</param>
    private void ZipButton_PreviewMouseMove(object sender, MouseEventArgs e)
    {
        if (e.LeftButton != MouseButtonState.Pressed) { _zipDragArmed = false; return; }
        if (!_zipDragArmed || _zipDragging) return;
        if (!DropOutService.MovedEnough(_zipDragStart, e.GetPosition(null))) return;
        if (!ViewModel.CanSynchronize) return;
        _zipDragArmed = false;
        _zipDragging = true;
        e.Handled = true;
        // End the button press before OLE takes over mouse input.
        (sender as UIElement)?.ReleaseMouseCapture();
        try
        {
            string staged = ViewModel.StageCheckedCopy(_zipDragFromSource);
            if (!string.IsNullOrEmpty(staged))
                DropOutService.DragExisting(sender as DependencyObject, new[] { staged });
        }
        catch (Exception ex) { ViewModel.ShowError(ex); }
        finally { _zipDragging = false; }
    }
}

internal sealed class OutlinedPairCaption : FrameworkElement
{
    public static readonly DependencyProperty SourceTextProperty = DependencyProperty.Register(nameof(SourceText), typeof(string), typeof(OutlinedPairCaption), new FrameworkPropertyMetadata(string.Empty, FrameworkPropertyMetadataOptions.AffectsMeasure | FrameworkPropertyMetadataOptions.AffectsRender));
    public static readonly DependencyProperty TargetTextProperty = DependencyProperty.Register(nameof(TargetText), typeof(string), typeof(OutlinedPairCaption), new FrameworkPropertyMetadata(string.Empty, FrameworkPropertyMetadataOptions.AffectsMeasure | FrameworkPropertyMetadataOptions.AffectsRender));
    public static readonly DependencyProperty SourceFillProperty = DependencyProperty.Register(nameof(SourceFill), typeof(Brush), typeof(OutlinedPairCaption), new FrameworkPropertyMetadata(Brushes.Goldenrod, FrameworkPropertyMetadataOptions.AffectsRender));
    public static readonly DependencyProperty TargetFillProperty = DependencyProperty.Register(nameof(TargetFill), typeof(Brush), typeof(OutlinedPairCaption), new FrameworkPropertyMetadata(Brushes.SteelBlue, FrameworkPropertyMetadataOptions.AffectsRender));
    public static readonly DependencyProperty OutlineFillProperty = DependencyProperty.Register(nameof(OutlineFill), typeof(Brush), typeof(OutlinedPairCaption), new FrameworkPropertyMetadata(Brushes.White, FrameworkPropertyMetadataOptions.AffectsRender));

    public string SourceText { get => (string)GetValue(SourceTextProperty); set => SetValue(SourceTextProperty, value); }
    public string TargetText { get => (string)GetValue(TargetTextProperty); set => SetValue(TargetTextProperty, value); }
    public Brush SourceFill { get => (Brush)GetValue(SourceFillProperty); set => SetValue(SourceFillProperty, value); }
    public Brush TargetFill { get => (Brush)GetValue(TargetFillProperty); set => SetValue(TargetFillProperty, value); }
    public Brush OutlineFill { get => (Brush)GetValue(OutlineFillProperty); set => SetValue(OutlineFillProperty, value); }

    /// <summary>
    ///     縁取り付きで 2 つの文字列を並べて表示するキャプションを作成します。
    /// </summary>
    public OutlinedPairCaption()
    {
        SnapsToDevicePixels = true;
        UseLayoutRounding = true;
    }

    /// <summary>
    ///     縁取りの幅を含めた表示サイズを求めます。
    /// </summary>
    /// <param name="available">利用できるサイズ。</param>
    /// <returns>希望サイズ。</returns>
    protected override Size MeasureOverride(Size available)
    {
        double width = Limit(available.Width);
        FormattedText text = Build(width);
        return new Size(Math.Min(width, text.WidthIncludingTrailingWhitespace + 3), text.Height + 3);
    }

    /// <summary>
    ///     文字列を縁取り付きで描画します。
    /// </summary>
    /// <param name="dc">描画コンテキスト。</param>
    protected override void OnRender(DrawingContext dc)
    {
        FormattedText text = Build(Math.Max(1, ActualWidth));
        var geometry = text.BuildGeometry(new Point(1.5, 1.5));
        var pen = new Pen(OutlineFill ?? Brushes.White, 2.6)
        {
            LineJoin = PenLineJoin.Round,
            StartLineCap = PenLineCap.Round,
            EndLineCap = PenLineCap.Round
        };
        pen.Freeze();
        dc.DrawGeometry(null, pen, geometry);
        dc.DrawText(text, new Point(1.5, 1.5));
    }

    /// <summary>
    ///     表示に使う最大幅を求めます。
    /// </summary>
    /// <param name="available">利用できる幅。</param>
    /// <returns>表示に使う幅。</returns>
    private double Limit(double available)
    {
        double max = double.IsNaN(MaxWidth) || double.IsInfinity(MaxWidth) ? 420 : MaxWidth;
        if (double.IsInfinity(available) || available <= 0) return max;
        return Math.Max(1, Math.Min(available, max));
    }

    /// <summary>
    ///     比較元と比較先の文字列を、それぞれの色で 1 行に並べた書式付きテキストを作成します。収まらない部分は省略記号にします。
    /// </summary>
    /// <param name="width">最大幅。</param>
    /// <returns>書式付きテキスト。</returns>
    private FormattedText Build(double width)
    {
        string source = SourceText ?? string.Empty;
        string target = TargetText ?? string.Empty;
        string gap = string.IsNullOrEmpty(source) || string.IsNullOrEmpty(target) ? string.Empty : "   ";
        string whole = source + gap + target;
        var typeface = new Typeface(new FontFamily("Yu Gothic UI, Meiryo UI, Segoe UI"), FontStyles.Normal, FontWeights.SemiBold, FontStretches.Normal);
        double dpi = VisualTreeHelper.GetDpi(this).PixelsPerDip;
        var text = new FormattedText(whole, System.Globalization.CultureInfo.CurrentCulture, FlowDirection.LeftToRight, typeface, 12, SourceFill ?? Brushes.Goldenrod, dpi)
        {
            MaxTextWidth = Math.Max(1, width - 3),
            Trimming = TextTrimming.CharacterEllipsis,
            MaxLineCount = 1
        };
        if (gap.Length + target.Length > 0 && source.Length < whole.Length)
            text.SetForegroundBrush(TargetFill ?? Brushes.SteelBlue, source.Length + gap.Length, target.Length);
        return text;
    }
}
