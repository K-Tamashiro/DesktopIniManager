using DesktopIniManager.ViewModels;
using DesktopIniManager.Services;
using DesktopIniManager.Properties;
using System;
using System.Runtime.Versioning;
using System.Collections.Generic;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using System.IO;

namespace DesktopIniManager.Views;

[SupportedOSPlatform("windows")]
public partial class DeveloperDifferencerWindow : Window
{
    internal DeveloperDifferencerViewModel ViewModel { get; }
    internal bool IsWorking { get { return ViewModel.IsBusy; } }
    private Point _zipDragStart;
    private bool _zipDragFromSource;
    private bool _zipDragging;
    private bool _zipDragArmed;
    /// <summary>Initializes a new developer differencer window.</summary>
    public DeveloperDifferencerWindow()
    {
        ViewModel = new DeveloperDifferencerViewModel(new UserDialogService(this), Dispatcher,
            ShowSyncConfirmation, (direction, currentSnapshot) => new SynchronizationLogWindow(this, direction, currentSnapshot));
        InitializeComponent();
        RestoreGridLayout();
        DataContext = ViewModel;
        SourceBox.HistoryItemApplied += (_, _) => ViewModel.MatchTargetHistory();
        TargetBox.HistoryItemApplied += (_, _) => ViewModel.CancelTargetHistoryMatch();
        SourceLabelIcon.MouseLeftButtonUp += (sender, args) => ViewModel.RescanRoot(true);
        TargetLabelIcon.MouseLeftButtonUp += (sender, args) => ViewModel.RescanRoot(false);
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
            pendingProgressRow = null;
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
        ViewModel.PropertyChanged += (s, e) => { if (e.PropertyName == nameof(ViewModel.IsFileBusy)) { if (ViewModel.IsFileBusy) RestartPanelProgress(); else StopPanelProgress(); } };
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
        Closing += (s, e) => { if (ViewModel.IsBusy) { e.Cancel = true; return; } SaveState(); };
        Closed += (s, e) =>
        {
            StringOverlay.CultureChanged -= OnCultureChanged;
            ViewModel.Close();
        };
        AllowDrop = true;
        PreviewDragEnter += FolderDropPreview;
        PreviewDragOver += FolderDropPreview;
        PreviewDrop += Differencer_Drop;
        Loaded += async (s, e) => { ViewModel.SetFilePanelBusy(false); await ViewModel.RestoreHistoryAsync(); };
        StringOverlay.CultureChanged += OnCultureChanged;
        ViewModel.RestoreState();
    }

    private DiffRow pendingProgressRow;
    private bool progressScrollPending;

    private void ScrollToProgressFile(DiffRow row)
    {
        pendingProgressRow = row;
        if (progressScrollPending || row == null) return;
        progressScrollPending = true;
        Dispatcher.BeginInvoke(new Action(() =>
        {
            progressScrollPending = false;
            DiffRow latest = pendingProgressRow;
            if (latest == null || !FilesGrid.Items.Contains(latest)) return;
            FilesGrid.ScrollIntoView(latest);
            FindScrollViewer(FilesGrid)?.ScrollToEnd();
        }), DispatcherPriority.Background);
    }

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

    private void OnCultureChanged(object sender, EventArgs e)
    {
        if (ViewModel.IsFileBusy) RestartPanelProgress();
        if (ViewModel.IsProgressVisible && ViewModel.ProgressIndeterminate)
        {
            ViewModel.ProgressIndeterminate = false;
            ViewModel.ProgressIndeterminate = true;
        }
    }
    private void FileRowLoaded(object sender, RoutedEventArgs e)
    {
        var item = (ListViewItem)sender;
        item.DataContextChanged -= FileRowContextChanged;
        item.DataContextChanged += FileRowContextChanged;
        LoadFileRow(item);
    }
    private void FileRowUnloaded(object sender, RoutedEventArgs e)
    {
        var item = (ListViewItem)sender;
        item.DataContextChanged -= FileRowContextChanged;
        (item.Tag as DiffRow)?.ReleasePreview(); item.Tag = null;
    }
    private void FileRowContextChanged(object sender, DependencyPropertyChangedEventArgs e)
    { if (((ListViewItem)sender).IsLoaded) LoadFileRow((ListViewItem)sender); }
    private async void LoadFileRow(ListViewItem item)
    {
        if (ReferenceEquals(item.Tag, item.DataContext)) return;
        (item.Tag as DiffRow)?.ReleasePreview();
        var row = item.DataContext as DiffRow; item.Tag = row;
        await ViewModel.LoadPreviewAsync(row);
    }

    private void FolderChanged(object sender, RoutedPropertyChangedEventArgs<object> e)
    {
        var folder = e.NewValue as DiffFolder;
        if (folder == null) return;
        // The pinned root and scrolling descendants have separate TreeViews.
        foreach (var node in ViewModel.Folders.Values) node.Active = ReferenceEquals(node, folder);
        ViewModel.SelectFolder(folder.Path);
    }
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

    private static TreeViewItem ContainerAlongPath(ItemsControl parent, IReadOnlyList<DiffFolder> path)
    {
        TreeViewItem current = null;
        ItemsControl host = parent;
        foreach (DiffFolder node in path)
        {
            if (node.Path.Length == 0) continue; // Root is displayed in the fixed tree.
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

    private void StopPanelProgress()
    {
        if (FilePanelMarquee == null) return;
        var transform = FilePanelMarquee.RenderTransform as TranslateTransform;
        if (transform == null) return;
        transform.BeginAnimation(TranslateTransform.XProperty, null);
        transform.X = 0;
    }

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

    private SolutionCleanSelection ChooseCleanSolutions(IReadOnlyList<string> solutions, string source)
        => CleanSolutionsWindow.Choose(this, solutions, source);

    private void ShowCleanReport(string summary, string logPath, string log)
        => CleanReportWindow.Show(this, summary, logPath, log);

    private bool ShowSyncConfirmation(string direction, DiffFile[] files, DiffFolderSync[] folders, bool toTarget, bool zipMode, out string zipFileName, out string zipFolder)
    {
        return SynchronizeConfirmWindow.Confirm(this, direction, files, folders, toTarget, ViewModel.Snapshot.SourceRoot, ViewModel.Snapshot.TargetRoot, zipMode, out zipFileName, out zipFolder);
    }

    internal Task<bool> RefreshFileAsync(DiffFile file) => ViewModel.RefreshFileAsync(file);
    internal IReadOnlyList<DiffFile> GetVisibleComparableFiles() => ViewModel.GetVisibleComparableFiles();
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
    internal void SaveState()
    {
        ViewModel.SaveState();
        SaveGridLayout();
    }

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

    private void OpenDiff(object sender, MouseButtonEventArgs e)
    {
        // Keep the row hit test in the View so headers and scrollbars cannot open a viewer.
        if (!(ItemsControl.ContainerFromElement(FilesGrid, e.OriginalSource as DependencyObject) is ListViewItem)) return;
        ViewModel.OpenDiffCommand.Execute(null);
    }

    private void ApplyToolbarIcons()
    {
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
        UpdateNodeExpandIcon();
        UpdateDensityIcon();
    }

    private void CompareModeToggle_Changed(object sender, RoutedEventArgs e) => UpdateCompareModeIcons();

    private void UpdateCompareModeIcons()
    {
        if (CompareTimestampBox != null) SetIcon(CompareTimestampIcon, CompareTimestampBox.IsChecked == true ? 130 : 131);
        if (PreciseCompareBox != null) SetIcon(PreciseCompareIcon, PreciseCompareBox.IsChecked == true ? 128 : 129);
    }

    private static void SetIcon(Image image, int index)
    {
        if (image != null)
            image.Source = DifferencerStatusIcons.GetCustomIcon(index);
    }

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

    private void NodeExpandToggle_Changed(object sender, RoutedEventArgs e)
    {
        if (NodeExpandToggle.IsChecked == true) ViewModel.ExpandAllCommand.Execute(null);
        else ViewModel.CollapseAllCommand.Execute(null);
        UpdateNodeExpandIcon();
    }

    private void TreeDensityToggle_Changed(object sender, RoutedEventArgs e)
    {
        if (TreeDensityToggle.IsChecked == true) ViewModel.ComfortableTreeCommand.Execute(null);
        else ViewModel.CompactTreeCommand.Execute(null);
        UpdateDensityIcon();
    }

    private void UpdateNodeExpandIcon()
    {
        bool expanded = NodeExpandToggle != null && NodeExpandToggle.IsChecked == true;
        SetIcon(NodeExpandIcon, expanded ? 56 : 55);
        if (NodeExpandToggle != null)
            NodeExpandToggle.ToolTip = expanded ? Strings.Common_Collapse : Strings.Common_Expand;
    }

    private void UpdateDensityIcon()
    {
        bool compact = ViewModel.TreeCompact;
        SetIcon(TreeDensityIcon, compact ? 91 : 90);
        if (TreeDensityToggle != null)
            TreeDensityToggle.ToolTip = compact ? Strings.Main_TreeCompact : Strings.Main_TreeComfortable;
    }

    private void HistoryTabStrip_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        var item = ItemsControl.ContainerFromElement(sender as ItemsControl, e.OriginalSource as DependencyObject) as ListBoxItem;
        if (item?.DataContext == null) return;
        if (ViewModel.DeleteHistoryTabCommand.CanExecute(item.DataContext))
            ViewModel.DeleteHistoryTabCommand.Execute(item.DataContext);
    }

    private void DeleteAllHistoryTabs_Click(object sender, RoutedEventArgs e)
    {
        if (ViewModel.DeleteAllHistoryTabsCommand.CanExecute(null))
            ViewModel.DeleteAllHistoryTabsCommand.Execute(null);
    }

    private void DeleteOtherHistoryTabs_Click(object sender, RoutedEventArgs e)
    {
        var menu = (sender as MenuItem)?.Parent as ContextMenu;
        object tab = (menu?.PlacementTarget as FrameworkElement)?.DataContext;
        if (tab != null && ViewModel.DeleteOtherHistoryTabsCommand.CanExecute(tab))
            ViewModel.DeleteOtherHistoryTabsCommand.Execute(tab);
    }

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

    private void ZipButton_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        _zipDragArmed = true;
        _zipDragging = false;
        _zipDragFromSource = ReferenceEquals(sender, ZipSourceButton);
        _zipDragStart = e.GetPosition(null);
    }

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

    public OutlinedPairCaption()
    {
        SnapsToDevicePixels = true;
        UseLayoutRounding = true;
    }

    protected override Size MeasureOverride(Size available)
    {
        double width = Limit(available.Width);
        FormattedText text = Build(width);
        return new Size(Math.Min(width, text.WidthIncludingTrailingWhitespace + 3), text.Height + 3);
    }

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

    private double Limit(double available)
    {
        double max = double.IsNaN(MaxWidth) || double.IsInfinity(MaxWidth) ? 420 : MaxWidth;
        if (double.IsInfinity(available) || available <= 0) return max;
        return Math.Max(1, Math.Min(available, max));
    }

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
