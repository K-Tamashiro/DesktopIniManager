using DesktopIniManager.ViewModels;
using DesktopIniManager.Services;
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using System.Xml.Serialization;

namespace DesktopIniManager.Views
{
    public partial class DeveloperDifferencerWindow : Window
    {
        internal DeveloperDifferencerViewModel ViewModel { get; }
        private int previewInFlight;
        private bool syncingTreeFromFile;
        private readonly SemaphoreSlim previewWorkers = new SemaphoreSlim(2);
        private CancellationTokenSource previewScope = new CancellationTokenSource();



        internal bool IsWorking { get { return ViewModel.busy; } }
        public static readonly DependencyProperty TreeCompactProperty = DependencyProperty.Register("TreeCompact", typeof(bool), typeof(DeveloperDifferencerWindow), new PropertyMetadata(false));
        public bool TreeCompact { get { return (bool)GetValue(TreeCompactProperty); } set { SetValue(TreeCompactProperty, value); } }
        private void CompactTree_Click(object sender, RoutedEventArgs e) { TreeCompact = true; }
        private void ComfortableTree_Click(object sender, RoutedEventArgs e) { TreeCompact = false; }


        /// <summary>Initializes a new developer differencer window.</summary>
        public DeveloperDifferencerWindow()
        {
            ViewModel = new DeveloperDifferencerViewModel(new UserDialogService(this), Dispatcher,
                ShowSyncConfirmation, (direction, currentSnapshot) => new SynchronizationLogWindow(this, direction, currentSnapshot));
            InitializeComponent();
            DataContext = ViewModel;
            ViewModel.ChooseCleanSolutions = ChooseCleanSolutions;
            ViewModel.CleanReportRequested += ShowCleanReport;
            ViewModel.ComparisonCleared += () => { previewScope.Cancel(); previewScope.Dispose(); previewScope = new CancellationTokenSource(); };
            ViewModel.CommitRootHistoryRequested += () => { SourceBox.CommitHistory(); TargetBox.CommitHistory(); };
            ViewModel.PropertyChanged += (s, e) => { if (e.PropertyName == nameof(ViewModel.IsFileBusy)) { if (ViewModel.IsFileBusy) RestartPanelProgress(); else StopPanelProgress(); } };
            SameFilterIcon.Source = DifferencerStatusIcons.GetFileIcon(DiffKind.Same);
            DifferentFilterIcon.Source = DifferencerStatusIcons.GetFileIcon(DiffKind.Different);
            SourceOnlyFilterIcon.Source = DifferencerStatusIcons.GetFileIcon(DiffKind.SourceOnly);
            TargetOnlyFilterIcon.Source = DifferencerStatusIcons.GetFileIcon(DiffKind.TargetOnly);
            RefreshCompareIcon.Source = DifferencerStatusIcons.GetRefreshIcon();
            ObjFilterIcon.Source = DifferencerStatusIcons.GetBuildFolderIcon(true);
            BinFilterIcon.Source = DifferencerStatusIcons.GetBuildFolderIcon(false);
            TreeCompact = SettingsService.LoadTreeCompact();
            // HistoryTextBox persists Source/Target via HistoryKey.
            Closing += (s, e) => { if (ViewModel.busy) { e.Cancel = true; return; } ViewModel.SaveState(); };
            Closed += (s, e) => { ViewModel.Close(); previewScope.Cancel(); previewScope.Dispose(); ViewModel.DetachSelectionHandlers(); };
            Loaded += (s, e) => ViewModel.SetFilePanelBusy(false);
            ViewModel.RestoreState();
        }
        private void AttachElevationToggle()
        {
            var dock = Content as DockPanel;
            var top = dock?.Children.OfType<StackPanel>().FirstOrDefault();
            var header = top?.Children.OfType<Grid>().FirstOrDefault();
            if (header == null) return;

            while (header.ColumnDefinitions.Count < 3)
                header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            header.ColumnDefinitions[0].Width = new GridLength(1, GridUnitType.Star);
            header.ColumnDefinitions[1].Width = GridLength.Auto;
            header.ColumnDefinitions[2].Width = GridLength.Auto;

            var title = header.Children.OfType<TextBlock>().FirstOrDefault();
            var close = CloseButton ?? header.Children.OfType<Button>().FirstOrDefault();
            if (title != null) Grid.SetColumn(title, 0);
            if (close != null) Grid.SetColumn(close, 2);

        }

        private void RootsChanged(object sender, TextChangedEventArgs e)
        { ViewModel.ClearComparisonView(); }


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
            if (row == null || ViewModel.snapshot == null) return;
            bool wait = !row.IsPreviewReady;
            if (wait) previewInFlight++;
            try { await row.LoadPreviewAsync(previewWorkers, previewScope.Token); }
            finally
            {
                if (wait)
                {
                    previewInFlight--;
                    if (previewInFlight <= 0 && !ViewModel.busy) ViewModel.SetFilePanelBusy(false);
                }
            }
        }
        private void BrowseSource(object sender, RoutedEventArgs e) { Browse(SourceBox, "Source folder"); }
        private void BrowseTarget(object sender, RoutedEventArgs e) { Browse(TargetBox, "Target folder"); }
        private void CloseClick(object sender, RoutedEventArgs e) { Close(); }
        private void Browse(HistoryTextBox box, string title)
        {
            try
            {
                string path = NativeFolderPicker.Show(new WindowInteropHelper(this).Handle, box.Text, title);
                if (path == null) return;
                box.SetCurrentValue(HistoryTextBox.TextProperty, path);
                box.CommitHistory();
            }
            catch (Exception ex) { ShowError(ex); }
        }

































        private void FolderChanged(object sender, RoutedPropertyChangedEventArgs<object> e)
        {
            if (syncingTreeFromFile) return;
            var folder = e.NewValue as DiffFolder; if (folder == null) return; ViewModel.selectedFolder = folder.Path; ViewModel.Filter();
        }
        private void FileListSelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (syncingTreeFromFile || ViewModel.busy || ViewModel.snapshot == null) return;
            var row = FilesGrid.SelectedItem as DiffRow;
            if (row == null) return;
            RevealContainingFolder(Path.GetDirectoryName(row.File.RelativePath) ?? "");
        }
        private void RevealContainingFolder(string path)
        {
            if (ViewModel.folders.Count == 0) return;
            DiffFolder target = null;
            string current = path ?? "";
            while (true)
            {
                if (ViewModel.folders.TryGetValue(current, out target) && target.Visible) break;
                if (current.Length == 0) { target = ViewModel.folders.ContainsKey("") ? ViewModel.folders[""] : null; break; }
                current = Path.GetDirectoryName(current) ?? "";
            }
            if (target == null) return;

            string ancestor = target.Path;
            while (ancestor.Length > 0)
            {
                ancestor = Path.GetDirectoryName(ancestor) ?? "";
                DiffFolder parent;
                if (ViewModel.folders.TryGetValue(ancestor, out parent)) parent.Expanded = true;
            }

            syncingTreeFromFile = true;
            foreach (DiffFolder folder in ViewModel.folders.Values)
                if (folder.Active && folder != target) folder.Active = false;
            target.Active = true;
            ScheduleFolderIntoView(target);
        }
        private void ScheduleFolderIntoView(DiffFolder target)
        {
            Action bring = () => BringFolderIntoView(target);
            bring();
            Dispatcher.BeginInvoke(bring, DispatcherPriority.Loaded);
            Dispatcher.BeginInvoke(new Action(() =>
            {
                try { bring(); }
                finally { syncingTreeFromFile = false; }
            }), DispatcherPriority.ContextIdle);
        }
        private void BringFolderIntoView(DiffFolder target)
        {
            if (target == null) return;
            var path = new List<DiffFolder>();
            string current = target.Path ?? "";
            while (true)
            {
                DiffFolder node;
                if (ViewModel.folders.TryGetValue(current, out node)) path.Add(node);
                if (current.Length == 0) break;
                current = Path.GetDirectoryName(current) ?? "";
            }
            path.Reverse();
            TreeViewItem item = ContainerAlongPath(FolderTree, path);
            ScrollTreeItemIntoView(FolderTree, item);
        }
        private static TreeViewItem ContainerAlongPath(ItemsControl parent, List<DiffFolder> path)
        {
            TreeViewItem current = null;
            ItemsControl host = parent;
            foreach (DiffFolder node in path)
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
            try
            {
                if (FilePanelMarquee == null || FilePanelBusy == null || FilePanelBusy.Visibility != Visibility.Visible) return;
                var transform = FilePanelMarquee.RenderTransform as TranslateTransform ?? new TranslateTransform();
                FilePanelMarquee.RenderTransform = transform;
                transform.BeginAnimation(TranslateTransform.XProperty, null);
                double distance = 112;
                var animation = new DoubleAnimation(0, distance, TimeSpan.FromSeconds(1.1))
                { AutoReverse = true, RepeatBehavior = RepeatBehavior.Forever };
                transform.BeginAnimation(TranslateTransform.XProperty, animation, HandoffBehavior.SnapshotAndReplace);
            }
            catch (InvalidOperationException) { }
        }


        private bool ShowSyncConfirmation(string direction, DiffFile[] files, bool toTarget)
        {
            bool accepted = false;

            var dialog = new Window
            {
                Owner = this,
                Title = "Synchronize files",
                Width = 620,
                SizeToContent = SizeToContent.Height,
                ResizeMode = ResizeMode.NoResize,
                WindowStartupLocation = WindowStartupLocation.CenterOwner,
                ShowInTaskbar = false
            };
            dialog.SetResourceReference(Window.BackgroundProperty, "WindowBackground");
            dialog.SetResourceReference(Window.ForegroundProperty, "Ink");

            var root = new Grid { Margin = new Thickness(28) };
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

            var heading = new TextBlock
            {
                Text = "Synchronize selected files",
                FontSize = 24,
                FontWeight = FontWeights.SemiBold
            };
            heading.SetResourceReference(TextBlock.ForegroundProperty, "Accent");
            Grid.SetRow(heading, 0);
            root.Children.Add(heading);

            var sub = new TextBlock
            {
                Text = direction + "   •   " + files.Length + (files.Length == 1 ? " file" : " files"),
                Margin = new Thickness(0, 5, 0, 18),
                FontSize = 13
            };
            sub.SetResourceReference(TextBlock.ForegroundProperty, "Muted");
            Grid.SetRow(sub, 1);
            root.Children.Add(sub);

            var paths = new Border
            {
                Padding = new Thickness(16, 13, 16, 13),
                CornerRadius = new CornerRadius(6),
                BorderThickness = new Thickness(1)
            };
            paths.SetResourceReference(Border.BackgroundProperty, "CardBackground");
            paths.SetResourceReference(Border.BorderBrushProperty, "Line");

            var pathGrid = new Grid();
            pathGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(70) });
            pathGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            pathGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            pathGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

            var sourceLabel = new TextBlock { Text = "Source", FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 0, 12, 8) };
            sourceLabel.SetResourceReference(TextBlock.ForegroundProperty, "Muted");
            var sourcePath = new TextBlock { Text = ViewModel.snapshot.SourceRoot, TextTrimming = TextTrimming.CharacterEllipsis, Margin = new Thickness(0, 0, 0, 8) };
            sourcePath.SetResourceReference(TextBlock.ForegroundProperty, "Ink");

            var targetLabel = new TextBlock { Text = "Target", FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 0, 12, 0) };
            targetLabel.SetResourceReference(TextBlock.ForegroundProperty, "Muted");
            var targetPath = new TextBlock { Text = ViewModel.snapshot.TargetRoot, TextTrimming = TextTrimming.CharacterEllipsis };
            targetPath.SetResourceReference(TextBlock.ForegroundProperty, "Ink");

            Grid.SetRow(sourceLabel, 0); Grid.SetColumn(sourceLabel, 0);
            Grid.SetRow(sourcePath, 0); Grid.SetColumn(sourcePath, 1);
            Grid.SetRow(targetLabel, 1); Grid.SetColumn(targetLabel, 0);
            Grid.SetRow(targetPath, 1); Grid.SetColumn(targetPath, 1);
            pathGrid.Children.Add(sourceLabel); pathGrid.Children.Add(sourcePath);
            pathGrid.Children.Add(targetLabel); pathGrid.Children.Add(targetPath);
            paths.Child = pathGrid;

            Grid.SetRow(paths, 2);
            root.Children.Add(paths);

            var operations = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Margin = new Thickness(0, 16, 0, 16)
            };

            foreach (var group in files.GroupBy(f => DeveloperDifferencerService.Operation(f, toTarget)))
            {
                var badge = new Border
                {
                    CornerRadius = new CornerRadius(5),
                    Padding = new Thickness(10, 6, 10, 6),
                    Margin = new Thickness(0, 0, 8, 0),
                    BorderThickness = new Thickness(1)
                };
                badge.SetResourceReference(Border.BackgroundProperty, "AccentSoft");
                badge.SetResourceReference(Border.BorderBrushProperty, "Line");

                var label = new TextBlock
                {
                    Text = group.Key + "  " + group.Count(),
                    FontWeight = FontWeights.SemiBold
                };
                label.SetResourceReference(TextBlock.ForegroundProperty, "Ink");
                badge.Child = label;
                operations.Children.Add(badge);
            }

            Grid.SetRow(operations, 3);
            root.Children.Add(operations);

            var footer = new Grid();
            footer.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            footer.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            footer.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            var note = new TextBlock
            {
                Text = "Existing files may be overwritten or removed.",
                VerticalAlignment = VerticalAlignment.Center,
                FontSize = 12
            };
            note.SetResourceReference(TextBlock.ForegroundProperty, "Muted");
            Grid.SetColumn(note, 0);
            footer.Children.Add(note);

            var cancel = new Button
            {
                Content = ActionContent("\uE711", "Cancel"),
                Style = (Style)FindResource("DifferencerActionButton"),
                MinWidth = 96,
                Height = 34,
                Margin = new Thickness(12, 0, 0, 0),
                IsCancel = true
            };
            cancel.SetResourceReference(Button.BackgroundProperty, "Secondary");
            cancel.SetResourceReference(Button.ForegroundProperty, "Ink");
            Grid.SetColumn(cancel, 1);
            footer.Children.Add(cancel);

            var sync = new Button
            {
                Content = ActionContent(toTarget ? "\uE74B" : "\uE74A", "Synchronize"),
                Style = (Style)FindResource("DifferencerActionButton"),
                MinWidth = 118,
                Height = 34,
                Margin = new Thickness(8, 0, 0, 0),
                IsDefault = true
            };
            sync.Background = (Brush)FindResource(toTarget ? "SourceColor" : "TargetColor");
            sync.Foreground = toTarget ? (Brush)new BrushConverter().ConvertFromString("#20252B") : Brushes.White;
            sync.Click += (s, e) =>
            {
                accepted = true;
                dialog.DialogResult = true;
            };
            Grid.SetColumn(sync, 2);
            footer.Children.Add(sync);

            Grid.SetRow(footer, 4);
            root.Children.Add(footer);

            dialog.Content = root;
            dialog.ShowDialog();
            return accepted;
        }




        internal Task<bool> RefreshFileAsync(DiffFile file) => ViewModel.RefreshFileAsync(file);
        internal void SaveState() => ViewModel.SaveState();
        private void OpenDiff(object sender, MouseButtonEventArgs e)
        {
            if (ViewModel.busy || ViewModel.snapshot == null || !(FilesGrid.SelectedItem is DiffRow)) return;
            // Only data rows open a viewer; header/scrollbar double-clicks do not.
            if (!(ItemsControl.ContainerFromElement(FilesGrid, e.OriginalSource as DependencyObject) is ListViewItem)) return;
            var selectedFile = ((DiffRow)FilesGrid.SelectedItem).File;
            if (DiffMedia.IsBinary(selectedFile.RelativePath))
            {
                MessageBox.Show(this, DiffMedia.BinaryMessage, "Diff View", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }
            try { new DiffViewWindow(ViewModel.snapshot, selectedFile) { Owner = this }.Show(); } catch (Exception ex) { ShowError(ex); }
        }

        private void ShowError(Exception ex) { MessageBox.Show(this, ErrorMessages.English(ex), Title, MessageBoxButton.OK, MessageBoxImage.Error); }
    }
}
