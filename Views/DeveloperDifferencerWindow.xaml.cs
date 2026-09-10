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

namespace DesktopIniManager.Views
{
    [SupportedOSPlatform("windows")]
    public partial class DeveloperDifferencerWindow : Window
    {
        internal DeveloperDifferencerViewModel ViewModel { get; }
        internal bool IsWorking { get { return ViewModel.IsBusy; } }
        /// <summary>Initializes a new developer differencer window.</summary>
        public DeveloperDifferencerWindow()
        {
            ViewModel = new DeveloperDifferencerViewModel(new UserDialogService(this), Dispatcher,
                ShowSyncConfirmation, (direction, currentSnapshot) => new SynchronizationLogWindow(this, direction, currentSnapshot));
            InitializeComponent();
            DataContext = ViewModel;
            ViewModel.ChooseCleanSolutions = ChooseCleanSolutions;
            ViewModel.CleanReportRequested += ShowCleanReport;
            ViewModel.ChooseFolder = (initialPath, title) =>
                NativeFolderPicker.Show(new WindowInteropHelper(this).Handle, initialPath, title);
            ViewModel.CloseRequested += Close;
            ViewModel.DiffRequested += (snapshot, file) => new DiffViewWindow(snapshot, file) { Owner = this }.Show();
            ViewModel.FolderRevealRequested += ScheduleFolderIntoView;
            ViewModel.CommitBrowsedRootHistoryRequested += source =>
            {
                if (source) SourceBox.CommitHistory();
                else TargetBox.CommitHistory();
            };
            ViewModel.CommitRootHistoryRequested += () => { SourceBox.CommitHistory(); TargetBox.CommitHistory(); };
            ViewModel.PropertyChanged += (s, e) => { if (e.PropertyName == nameof(ViewModel.IsFileBusy)) { if (ViewModel.IsFileBusy) RestartPanelProgress(); else StopPanelProgress(); } };
            SameFilterIcon.Source = DifferencerStatusIcons.GetFileIcon(DiffKind.Same);
            DifferentFilterIcon.Source = DifferencerStatusIcons.GetFileIcon(DiffKind.Different);
            SourceOnlyFilterIcon.Source = DifferencerStatusIcons.GetFileIcon(DiffKind.SourceOnly);
            TargetOnlyFilterIcon.Source = DifferencerStatusIcons.GetFileIcon(DiffKind.TargetOnly);
            RefreshCompareIcon.Source = DifferencerStatusIcons.GetRefreshIcon();
            ObjFilterIcon.Source = DifferencerStatusIcons.GetBuildFolderIcon(true);
            BinFilterIcon.Source = DifferencerStatusIcons.GetBuildFolderIcon(false);
            // HistoryTextBox persists Source/Target via HistoryKey.
            Closing += (s, e) => { if (ViewModel.IsBusy) { e.Cancel = true; return; } ViewModel.SaveState(); };
            Closed += (s, e) =>
            {
                StringOverlay.CultureChanged -= OnCultureChanged;
                ViewModel.Close();
            };
            Loaded += (s, e) => ViewModel.SetFilePanelBusy(false);
            StringOverlay.CultureChanged += OnCultureChanged;
            ViewModel.RestoreState();
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
            var folder = e.NewValue as DiffFolder; if (folder == null) return; ViewModel.SelectFolder(folder.Path);
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

        private SolutionCleanSelection ChooseCleanSolutions(IReadOnlyList<string> solutions, string source)
            => CleanSolutionsWindow.Choose(this, solutions, source);

        private void ShowCleanReport(string summary, string logPath, string log)
            => CleanReportWindow.Show(this, summary, logPath, log);

        private bool ShowSyncConfirmation(string direction, DiffFile[] files, bool toTarget)
        {
            return SynchronizeConfirmWindow.Confirm(this, direction, files, toTarget, ViewModel.Snapshot.SourceRoot, ViewModel.Snapshot.TargetRoot);
        }

        internal Task<bool> RefreshFileAsync(DiffFile file) => ViewModel.RefreshFileAsync(file);
        internal void SaveState() => ViewModel.SaveState();
        private void OpenDiff(object sender, MouseButtonEventArgs e)
        {
            // Keep the row hit test in the View so headers and scrollbars cannot open a viewer.
            if (!(ItemsControl.ContainerFromElement(FilesGrid, e.OriginalSource as DependencyObject) is ListViewItem)) return;
            ViewModel.OpenDiffCommand.Execute(null);
        }
    }
}
