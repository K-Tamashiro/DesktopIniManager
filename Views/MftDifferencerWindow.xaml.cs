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
using System.Windows.Media.Imaging;
using System.Xml.Serialization;

namespace DesktopIniManager.Views
{
    public sealed class DifferencerState
    {
        public string Source { get; set; }
        public string Target { get; set; }
        public string Selected { get; set; }
        public List<string> Folders { get; set; } = new List<string>();
        public List<string> Expanded { get; set; } = new List<string>();
        public List<string> VisibleFolders { get; set; }
    }

    internal static class MftDiffStatusIcons
    {
        private static readonly ImageSource[] icons = Load();

        private static ImageSource[] Load()
        {
            var result = new ImageSource[8];
            try
            {
                string baseDir = AppDomain.CurrentDomain.BaseDirectory;
                string[] candidates =
                {
                    System.IO.Path.Combine(baseDir, "Assets", "MftDifferencer_iconset.icl"),
                    System.IO.Path.GetFullPath(System.IO.Path.Combine(baseDir, "..", "..", "Assets", "MftDifferencer_iconset.icl")),
                    System.IO.Path.GetFullPath(System.IO.Path.Combine(baseDir, "..", "..", "..", "Assets", "MftDifferencer_iconset.icl"))
                };

                string path = candidates.FirstOrDefault(System.IO.File.Exists);
                if (path == null) return result;

                var groups = IconResourceReader.Read(path);
                for (int index = 0; index < result.Length; index++)
                {
                    var group = groups.FirstOrDefault(item => item.ShellIndex == index);
                    if (group != null)
                        result[index] = group.Preview;
                }
            }
            catch { }
            return result;
        }

        public static ImageSource GetFolderIcon(DiffKind kind) { return Get(Index(kind, 0)); }
        public static ImageSource GetFileIcon(DiffKind kind) { return Get(Index(kind, 4)); }

        private static int Index(DiffKind kind, int offset)
        {
            if (kind == DiffKind.SourceOnly) return offset + 0;
            if (kind == DiffKind.TargetOnly) return offset + 1;
            if (kind == DiffKind.Same) return offset + 2;
            return offset + 3;
        }

        private static ImageSource Get(int index)
        { return index >= 0 && index < icons.Length ? icons[index] : null; }
    }
    internal sealed class DiffFolder : INotifyPropertyChanged
    {
        public string Path { get; set; }
        public string Label { get; set; }
        public bool SourceExists { get; set; }
        public bool TargetExists { get; set; }
        public bool SourceEmpty { get; set; }
        public bool TargetEmpty { get; set; }

        public ImageSource IconPreview
        {
            get
            {
                // Only a completely empty one-sided folder uses Left / Right.
                if (SourceExists && !TargetExists && SourceEmpty)
                    return MftDiffStatusIcons.GetFolderIcon(DiffKind.SourceOnly);

                if (!SourceExists && TargetExists && TargetEmpty)
                    return MftDiffStatusIcons.GetFolderIcon(DiffKind.TargetOnly);

                // Any differing/source-only/target-only file below this folder means X.
                if (CountFor(DiffKind.Differences) > 0)
                    return MftDiffStatusIcons.GetFolderIcon(DiffKind.Different);

                // Otherwise the folder contents match.
                return MftDiffStatusIcons.GetFolderIcon(DiffKind.Same);
            }
        }
        public List<DiffFolder> Children { get; } = new List<DiffFolder>();
        public bool Visible { get; set; } = true;
        public List<DiffFolder> DisplayChildren { get; private set; } = new List<DiffFolder>();
        public void UpdateDisplayChildren() { DisplayChildren = Children.Where(f => f.Visible).ToList(); }
        public List<DiffFile> Files { get; } = new List<DiffFile>();
        private bool expanded;
        public bool Expanded { get { return expanded; } set { if (expanded == value) return; expanded = value; PropertyChanged?.Invoke(this, new PropertyChangedEventArgs("Expanded")); } }
        public bool Active { get; set; }
        public Action<DiffFolder, bool> Toggle;
        public DiffKind Mask { get; set; } = DiffKind.Differences;
        private readonly int[] counts = new int[9];
        private readonly int[] selectedCounts = new int[9];
        private static int Sum(int[] values, DiffKind mask)
        { int result = 0; for (int kind = 1; kind <= 8; kind <<= 1) if (((int)mask & kind) != 0) result += values[kind]; return result; }
        public int CountFor(DiffKind mask) { return Sum(counts, mask); }
        public int SelectedFor(DiffKind mask) { return Sum(selectedCounts, mask); }
        public bool CanSelect { get { return CountFor(Mask & DiffKind.Differences) > 0; } }
        public int SelectedCount { get; private set; }
        public bool? Checked
        {
            get { int selected = SelectedFor(Mask); return selected == 0 ? false : selected == CountFor(Mask & DiffKind.Differences) ? (bool?)true : null; }
            set { Toggle?.Invoke(this, value == true); }
        }
        public void Refresh()
        {
            Array.Clear(counts, 0, counts.Length); Array.Clear(selectedCounts, 0, selectedCounts.Length);
            SelectedCount = 0;
            foreach (var file in Files) { counts[(int)file.Kind]++; if (file.Selected) { selectedCounts[(int)file.Kind]++; SelectedCount++; } }
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs("Checked"));
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs("IconPreview"));
        }
        public void ChangeSelectionCount(int delta, DiffKind kind)
        {
            if (delta == 0) return;
            SelectedCount += delta;
            selectedCounts[(int)kind] += delta;
            if ((Mask & kind) != 0) PropertyChanged?.Invoke(this, new PropertyChangedEventArgs("Checked"));
        }
        public event PropertyChangedEventHandler PropertyChanged;
    }
    internal sealed class DiffSide : INotifyPropertyChanged
    {
        private string info;
        public string Info
        {
            get { return info; }
            set { info = value; SummaryInfo = System.Text.RegularExpressions.Regex.Replace(value ?? "", @"(\d{2}:\d{2}:\d{2})\.\d{7}", "$1"); }
        }
        public string SummaryInfo { get; private set; }
        public string Root { get; set; }
        public string Relative { get; set; }
        public bool Exists { get; set; }
        public bool HasImage { get { return Exists && DiffMedia.IsImage(Relative); } }
        internal sealed class Preview
        {
            public BitmapSource Thumbnail;
            public string Dimensions;
        }
        public BitmapSource Thumbnail { get; private set; }
        public string Dimensions { get; private set; }
        internal Preview ReadPreview()
        {
            var result = new Preview();
            if (!HasImage) return result;
            try
            {
                string path = MftDifferencerService.SafePath(Root, Relative);
                int width, height;
                using (var stream = File.OpenRead(path))
                {
                    var decoder = BitmapDecoder.Create(stream, BitmapCreateOptions.DelayCreation, BitmapCacheOption.None);
                    width = decoder.Frames[0].PixelWidth; height = decoder.Frames[0].PixelHeight;
                    result.Dimensions = width + " × " + height;
                }
                using (var stream = File.OpenRead(path))
                {
                    var image = new BitmapImage(); image.BeginInit(); image.CacheOption = BitmapCacheOption.OnLoad;
                    if ((double)width / height >= 96.0 / 64) image.DecodePixelWidth = Math.Min(96, width);
                    else image.DecodePixelHeight = Math.Min(64, height);
                    image.StreamSource = stream; image.EndInit(); image.Freeze(); result.Thumbnail = image;
                }
            }
            catch (Exception ex) { result.Dimensions = "Preview unavailable: " + ex.Message; }
            return result;
        }
        internal void ApplyPreview(Preview preview)
        {
            Thumbnail = preview.Thumbnail; Dimensions = preview.Dimensions;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs("Thumbnail"));
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs("Dimensions"));
        }
        internal void ReleaseThumbnail() { Thumbnail = null; }
        public event PropertyChangedEventHandler PropertyChanged;
    }
    internal sealed class DiffRow : INotifyPropertyChanged
    {
        public DiffFile File { get; set; }
        public string SourceRoot { get; set; }
        public string TargetRoot { get; set; }
        private DiffSide source, target;
        // Identical entries can be numerous; format side details only for rows that are actually displayed.
        public DiffSide Source { get { return source ?? (source = new DiffSide { Info = File.SourceInfo, Root = SourceRoot, Relative = File.RelativePath, Exists = File.Source != null }); } set { source = value; } }
        public DiffSide Target { get { return target ?? (target = new DiffSide { Info = File.TargetInfo, Root = TargetRoot, Relative = File.RelativePath, Exists = File.Target != null }); } set { target = value; } }
        public string Extension { get { return Path.GetExtension(File.RelativePath); } }
        public ImageSource StatusIcon { get { return MftDiffStatusIcons.GetFileIcon(File.Kind); } }
        public System.Windows.Media.ImageSource Icon { get; private set; }
        private bool previewLoaded;
        private CancellationTokenSource previewCancellation;
        public void CancelPreview() { previewCancellation?.Cancel(); }
        public void ReleasePreview()
        {
            CancelPreview();
            if (!Source.HasImage && !Target.HasImage) return;
            Source.ReleaseThumbnail(); Target.ReleaseThumbnail(); Icon = null; previewLoaded = false;
        }
        public async Task LoadPreviewAsync(SemaphoreSlim workers, CancellationToken scope)
        {
            if (previewLoaded || (previewCancellation != null && !previewCancellation.IsCancellationRequested)) return;
            var request = CancellationTokenSource.CreateLinkedTokenSource(scope);
            previewCancellation = request;
            bool entered = false;
            try
            {
                // Scrolled-away rows cancel this delay before any disk or shell work starts.
                await Task.Delay(120, request.Token);
                await workers.WaitAsync(request.Token); entered = true;
                var result = await Task.Run(() =>
                {
                    request.Token.ThrowIfCancellationRequested();
                    var left = Source.ReadPreview();
                    request.Token.ThrowIfCancellationRequested();
                    var right = Target.ReadPreview();
                    request.Token.ThrowIfCancellationRequested();
                    DiffSide side = Source.Exists ? Source : Target;
                    System.Windows.Media.ImageSource icon = Source.Exists ? left.Thumbnail : right.Thumbnail;
                    if (icon == null && side.Exists)
                        icon = FileIconService.GetTypeIcon(Extension);
                    return Tuple.Create(left, right, icon);
                }, request.Token);
                request.Token.ThrowIfCancellationRequested();
                Source.ApplyPreview(result.Item1); Target.ApplyPreview(result.Item2); Icon = result.Item3;
                previewLoaded = true;
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs("Icon"));
            }
            catch (OperationCanceledException) { }
            catch (Exception) { if (!request.IsCancellationRequested) previewLoaded = true; /* Unavailable previews must not break selection or synchronization. */ }
            finally
            {
                if (entered) workers.Release();
                if (ReferenceEquals(previewCancellation, request)) previewCancellation = null;
                request.Dispose();
            }
        }
        public event PropertyChangedEventHandler PropertyChanged;
    }
    public partial class MftDifferencerWindow : Window
    {
        private static readonly string StateDirectory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "DesktopIniManager");
        private static readonly string StatePath = Path.Combine(StateDirectory, "mft-differencer.xml");
        private DiffSnapshot snapshot;
        private readonly Dictionary<string, DiffFolder> folders = new Dictionary<string, DiffFolder>(StringComparer.OrdinalIgnoreCase);
        private List<DiffRow> rows = new List<DiffRow>();
        private string selectedFolder = "";
        private bool busy, bulk;
        private bool comparing;
        private readonly SemaphoreSlim previewWorkers = new SemaphoreSlim(2);
        private CancellationTokenSource previewScope = new CancellationTokenSource();
        private HashSet<string> cachedVisibleFolders;
        private DiffKind kindMask = DiffKind.Differences;
        private void KindFilter_Click(object sender, RoutedEventArgs e)
        {
            kindMask = (SameFilter.IsChecked == true ? DiffKind.Same : 0) |
                (DifferentFilter.IsChecked == true ? DiffKind.Different : 0) |
                (SourceOnlyFilter.IsChecked == true ? DiffKind.SourceOnly : 0) |
                (TargetOnlyFilter.IsChecked == true ? DiffKind.TargetOnly : 0);
            if (snapshot != null) ApplyKindFilter();
        }
        internal bool IsWorking { get { return busy; } }
        private string treeSource, treeTarget;
        public static readonly DependencyProperty TreeCompactProperty = DependencyProperty.Register("TreeCompact", typeof(bool), typeof(MftDifferencerWindow), new PropertyMetadata(false));
        public bool TreeCompact { get { return (bool)GetValue(TreeCompactProperty); } set { SetValue(TreeCompactProperty, value); } }
        private void CompactTree_Click(object sender, RoutedEventArgs e) { TreeCompact = true; }
        private void ComfortableTree_Click(object sender, RoutedEventArgs e) { TreeCompact = false; }
        private void ExpandAll_Click(object sender, RoutedEventArgs e) { foreach (DiffFolder folder in folders.Values) folder.Expanded = true; }
        private void CollapseAll_Click(object sender, RoutedEventArgs e) { foreach (DiffFolder folder in folders.Values) folder.Expanded = false; }
        public MftDifferencerWindow()
        {
            InitializeComponent();
            TreeCompact = SettingsService.LoadTreeCompact();
            SourceBox.TextChanged += RootsChanged; TargetBox.TextChanged += RootsChanged;
            Closing += (s, e) => { if (busy) { e.Cancel = true; return; } SaveState(); };
            Closed += (s, e) => { previewScope.Cancel(); previewScope.Dispose(); DetachSelectionHandlers(); };
            try
            {
                if (File.Exists(StatePath))
                {
                    DifferencerState state;
                    using (var stream = File.OpenRead(StatePath)) state = (DifferencerState)new XmlSerializer(typeof(DifferencerState)).Deserialize(stream);
                    SourceBox.Text = state.Source; TargetBox.Text = state.Target;
                    treeSource = state.Source; treeTarget = state.Target;
                    selectedFolder = "";
                    cachedVisibleFolders = null;
                    StatusText.Text = "Source and Target restored. Click Compare to build the difference tree.";
                }
            }
            catch (Exception ex) { StatusText.Text = "Failed to restore tree: " + ex.Message; }
        }
        private void RootsChanged(object sender, TextChangedEventArgs e)
        { ClearComparisonView(); }
        private void DetachSelectionHandlers()
        { if (snapshot != null) foreach (DiffFile file in snapshot.Files) file.PropertyChanged -= FileSelectionChanged; }
        private void ClearComparisonView()
        {
            previewScope.Cancel(); previewScope.Dispose(); previewScope = new CancellationTokenSource();
            DetachSelectionHandlers();
            FolderTree.ItemsSource = null;
            FilesGrid.ItemsSource = null;
            snapshot = null; rows.Clear();
            CategoryFilters.IsEnabled = false;
            FilePanelTitle.Text = "Files";
            UpdateSelectionSummary();
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
            if (row != null && snapshot != null) await row.LoadPreviewAsync(previewWorkers, previewScope.Token);
        }
        private void BrowseSource(object sender, RoutedEventArgs e) { Browse(SourceBox, "Source folder"); }
        private void BrowseTarget(object sender, RoutedEventArgs e) { Browse(TargetBox, "Target folder"); }
        private void CloseClick(object sender, RoutedEventArgs e) { Close(); }
        private void Browse(TextBox box, string title)
        { try { string path = NativeFolderPicker.Show(new WindowInteropHelper(this).Handle, box.Text, title); if (path != null) box.Text = path; } catch (Exception ex) { ShowError(ex); } }
        private async void CompareClick(object sender, RoutedEventArgs e) { await Compare(); }
        private async Task Compare()
        {
            var expanded = folders.Values.Where(f => f.Expanded).Select(f => f.Path).ToList();
            ClearComparisonView(); SetBusy(true);
            comparing = true;
            CompareProgress.Visibility = Visibility.Visible;
            CompareProgress.IsIndeterminate = true;
            StatusText.Text = "Enumerating MFT and comparing timestamps and sizes…";
            string source = SourceBox.Text, target = TargetBox.Text;
            try
            {
                var progress = new Progress<DiffProgress>(UpdateProgress);
                bool compareTimestamp = CompareTimestampBox.IsChecked == true;
                DiffSnapshot fresh = await Task.Run(() => MftDifferencerService.Compare(source, target, progress, compareTimestamp));
                comparing = false;
                CompareProgress.IsIndeterminate = true;
                StatusText.Text = "Updating the difference tree…";
                snapshot = fresh; treeSource = source; treeTarget = target;
                rows = snapshot.Files.Select(f => new DiffRow { File = f, SourceRoot = snapshot.SourceRoot, TargetRoot = snapshot.TargetRoot }).ToList();
                BuildTree(snapshot.Folders, expanded, selectedFolder);
                StatusText.Text = folders[""].CountFor(DiffKind.Differences) + " differences / " + folders[""].CountFor(DiffKind.Same) + " identical. Check items to synchronize.";
                SaveState();
            }
            catch (Exception ex) { StatusText.Text = "Compare failed (sync disabled): " + ex.Message; ShowError(ex); }
            finally { comparing = false; CompareProgress.Visibility = Visibility.Collapsed; CompareProgress.IsIndeterminate = false; SetBusy(false); }
        }
        private void UpdateProgress(DiffProgress progress)
        {
            if (!comparing) return;
            CompareProgress.IsIndeterminate = progress.Total == 0;
            CompareProgress.Maximum = Math.Max(1, progress.Total);
            CompareProgress.Value = progress.Completed;
            StatusText.Text = progress.Stage + (progress.Total == 0 ? "" : " — " + progress.Completed.ToString("N0") + " / " + progress.Total.ToString("N0") + " items");
        }
        private void BuildTree(IEnumerable<string> paths, IEnumerable<string> expanded, string selected)
        {
            folders.Clear();
            var all = new HashSet<string>(paths, StringComparer.OrdinalIgnoreCase) { "" };
            foreach (string path in all.Where(p => p.Length > 0).ToArray())
            { string p = Path.GetDirectoryName(path); while (!string.IsNullOrEmpty(p)) { all.Add(p); p = Path.GetDirectoryName(p); } }
            var expansion = new HashSet<string>(expanded ?? new string[0], StringComparer.OrdinalIgnoreCase);
            selectedFolder = selected != null && all.Contains(selected) ? selected : "";
            foreach (string path in all.OrderBy(p => p.Length).ThenBy(p => p, StringComparer.OrdinalIgnoreCase))
            {
                string sourceFolderPath = snapshot == null ? null : System.IO.Path.Combine(snapshot.SourceRoot, path);
                string targetFolderPath = snapshot == null ? null : System.IO.Path.Combine(snapshot.TargetRoot, path);
                bool sourceExists = sourceFolderPath != null && Directory.Exists(sourceFolderPath);
                bool targetExists = targetFolderPath != null && Directory.Exists(targetFolderPath);
                bool sourceEmpty = sourceExists && !Directory.EnumerateFileSystemEntries(sourceFolderPath).Any();
                bool targetEmpty = targetExists && !Directory.EnumerateFileSystemEntries(targetFolderPath).Any();

                var node = new DiffFolder
                {
                    Path = path,
                    Expanded = expansion.Contains(path) || path == "",
                    Active = path == selectedFolder,
                    SourceExists = sourceExists,
                    TargetExists = targetExists,
                    SourceEmpty = sourceEmpty,
                    TargetEmpty = targetEmpty
                };
                node.Toggle = (folder, value) => { bulk = true; foreach (DiffFile file in folder.Files) if (file.CanSync && (file.Kind & kindMask) != 0) file.Selected = value; bulk = false; RefreshChecks(); };
                folders.Add(path, node);
                if (path.Length > 0) folders[Path.GetDirectoryName(path) ?? ""].Children.Add(node);
            }
            if (snapshot != null) foreach (DiffFile file in snapshot.Files)
            {
                string path = Path.GetDirectoryName(file.RelativePath) ?? "";
                while (true) { DiffFolder folder; if (folders.TryGetValue(path, out folder)) folder.Files.Add(file); if (path.Length == 0) break; path = Path.GetDirectoryName(path) ?? ""; }
            }
            foreach (DiffFolder folder in folders.Values) folder.Refresh();
            if (snapshot != null) cachedVisibleFolders = new HashSet<string>(folders.Values.Where(f => f.Path.Length == 0 || f.CountFor(DiffKind.Differences) > 0).Select(f => f.Path), StringComparer.OrdinalIgnoreCase);
            DetachSelectionHandlers();
            if (snapshot != null) foreach (DiffFile file in snapshot.Files) file.PropertyChanged += FileSelectionChanged;
            ApplyKindFilter();
        }
        private void ApplyKindFilter()
        {
            // Counts were built once with the snapshot. Switching categories does not rescan files or rebuild the base tree.
            FolderTree.ItemsSource = null;
            foreach (DiffFolder folder in folders.Values)
            {
                folder.Mask = kindMask;
                folder.Visible = folder.Path.Length == 0 || (snapshot != null ? folder.CountFor(kindMask) > 0 : cachedVisibleFolders == null || cachedVisibleFolders.Contains(folder.Path));
                folder.Label = (folder.Path.Length == 0 ? RootLabel() : Path.GetFileName(folder.Path)) + (snapshot == null ? " (not compared)" : " (" + folder.CountFor(kindMask) + ")");
            }
            if (!folders[selectedFolder].Visible)
            {
                folders[selectedFolder].Active = false; selectedFolder = ""; folders[""].Active = true;
            }
            foreach (DiffFolder folder in folders.Values) folder.UpdateDisplayChildren();
            FolderTree.ItemsSource = new[] { folders[""] }; Filter(); UpdateSelectionSummary();
        }
        private void RefreshChecks()
        {
            foreach (DiffFolder folder in folders.Values) folder.Refresh();
            UpdateSelectionSummary();
        }
        private void FileSelectionChanged(object sender, PropertyChangedEventArgs e)
        {
            if (bulk || snapshot == null || e.PropertyName != "Selected") return;
            var file = (DiffFile)sender;
            int delta = file.Selected ? 1 : -1;
            string path = Path.GetDirectoryName(file.RelativePath) ?? "";
            while (true)
            {
                DiffFolder folder; if (folders.TryGetValue(path, out folder)) folder.ChangeSelectionCount(delta, file.Kind);
                if (path.Length == 0) break;
                path = Path.GetDirectoryName(path) ?? "";
            }
            UpdateSelectionSummary();
        }
        private void UpdateSelectionSummary()
        {
            DiffFolder root = null;
            int count = snapshot != null && folders.TryGetValue("", out root) ? root.SelectedCount : 0;
            int total = root == null ? 0 : root.CountFor(DiffKind.Differences);
            int hidden = root == null ? 0 : count - root.SelectedFor(kindMask);
            CountText.Text = "Selected " + count + " / " + total + (hidden > 0 ? " (includes " + hidden + " hidden)" : "");
            ForwardButton.IsEnabled = ReverseButton.IsEnabled = !busy && count > 0;
        }
        private string RootLabel()
        { return string.IsNullOrWhiteSpace(treeSource) ? "All folders" : Path.GetFileName(treeSource.TrimEnd('\\', '/')) is string name && name.Length > 0 ? name : treeSource; }
        private void FolderChanged(object sender, RoutedPropertyChangedEventArgs<object> e)
        { var folder = e.NewValue as DiffFolder; if (folder == null) return; selectedFolder = folder.Path; Filter(); }
        private void Filter()
        {
            var visible = rows.Where(r => (r.File.Kind & kindMask) != 0 && (selectedFolder.Length == 0 || r.File.RelativePath.StartsWith(selectedFolder + "\\", StringComparison.OrdinalIgnoreCase))).ToList();
            FilesGrid.ItemsSource = visible;
            FilePanelTitle.Text = "Files — " + (selectedFolder.Length == 0 ? "all levels" : selectedFolder) + " (" + visible.Count + ")";
        }
        private void SetBusy(bool value)
        {
            busy = value;
            RootControls.IsEnabled = CompareButton.IsEnabled = CompareTimestampBox.IsEnabled = !value;
            CategoryFilters.IsEnabled = !value && snapshot != null;
            FolderTree.IsHitTestVisible = FilesGrid.IsHitTestVisible = !value;
            FolderTree.Focusable = FilesGrid.Focusable = !value;
            UpdateSelectionSummary();
        }
        private async void ForwardClick(object sender, RoutedEventArgs e) { await Sync(true); }
        private async void ReverseClick(object sender, RoutedEventArgs e) { await Sync(false); }
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
            var sourcePath = new TextBlock { Text = snapshot.SourceRoot, TextTrimming = TextTrimming.CharacterEllipsis, Margin = new Thickness(0, 0, 0, 8) };
            sourcePath.SetResourceReference(TextBlock.ForegroundProperty, "Ink");

            var targetLabel = new TextBlock { Text = "Target", FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 0, 12, 0) };
            targetLabel.SetResourceReference(TextBlock.ForegroundProperty, "Muted");
            var targetPath = new TextBlock { Text = snapshot.TargetRoot, TextTrimming = TextTrimming.CharacterEllipsis };
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

            foreach (var group in files.GroupBy(f => MftDifferencerService.Operation(f, toTarget)))
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
                Content = "Cancel",
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
                Content = "Synchronize",
                MinWidth = 118,
                Height = 34,
                Margin = new Thickness(8, 0, 0, 0),
                IsDefault = true
            };
            sync.SetResourceReference(Button.BackgroundProperty, "Accent");
            sync.SetResourceReference(Button.ForegroundProperty, "ButtonForeground");
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

        private async Task Sync(bool toTarget)
        {
            if (busy || snapshot == null) return;
            DiffFile[] files = snapshot.Files.Where(f => f.CanSync && f.Selected).ToArray();
            if (files.Length == 0) return;

            string direction = toTarget ? "Source → Target" : "Target → Source";
            if (!ShowSyncConfirmation(direction, files, toTarget)) return;

            var liveLog = new TextBox
            {
                IsReadOnly = true,
                TextWrapping = TextWrapping.NoWrap,
                HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                FontFamily = new FontFamily("Consolas"),
                FontSize = 13
            };

            var logWindow = new Window
            {
                Owner = this,
                Title = "Synchronizing — " + direction,
                Width = 850,
                Height = 480,
                Content = liveLog,
                WindowStartupLocation = WindowStartupLocation.CenterOwner
            };

            liveLog.AppendText(direction + Environment.NewLine);
            liveLog.AppendText("Source: " + snapshot.SourceRoot + Environment.NewLine);
            liveLog.AppendText("Target: " + snapshot.TargetRoot + Environment.NewLine);
            liveLog.AppendText(new string('-', 80) + Environment.NewLine);
            logWindow.Show();
            logWindow.Activate();

            SetBusy(true);
            StatusText.Text = direction + " — syncing…";

            List<string> log;
            try
            {
                DiffSnapshot current = snapshot;
                log = await Task.Run(() => MftDifferencerService.Synchronize(current, files, toTarget, line =>
                {
                    Dispatcher.BeginInvoke(new Action(() =>
                    {
                        if (!logWindow.IsVisible) return;
                        liveLog.AppendText(line + Environment.NewLine);
                        liveLog.ScrollToEnd();
                    }));
                }));
            }
            catch (Exception ex)
            {
                log = new List<string> { "FAIL " + ex.Message };
                if (logWindow.IsVisible)
                {
                    liveLog.AppendText("FAIL " + ex.Message + Environment.NewLine);
                    liveLog.ScrollToEnd();
                }
            }

            string report = direction + "\n" + DateTime.Now.ToString("O") + "\n" + string.Join("\n", log);
            try
            {
                Directory.CreateDirectory(StateDirectory);
                string path = Path.Combine(StateDirectory, "mft-sync-" + DateTime.Now.ToString("yyyyMMdd-HHmmss-fff") + ".log");
                File.WriteAllText(path, report);
                report = "Log: " + path + "\n\n" + report;
                if (logWindow.IsVisible)
                {
                    liveLog.AppendText(new string('-', 80) + Environment.NewLine);
                    liveLog.AppendText("Log: " + path + Environment.NewLine);
                }
            }
            catch (Exception ex)
            {
                report = "Failed to save log: " + ex.Message + "\n\n" + report;
                if (logWindow.IsVisible)
                    liveLog.AppendText("Failed to save log: " + ex.Message + Environment.NewLine);
            }

            int ok = log.Count(l => l.StartsWith("OK "));
            int fail = log.Count(l => l.StartsWith("FAIL "));
            int locked = log.Count(l => l.StartsWith("LOCKED "));
            if (logWindow.IsVisible)
            {
                liveLog.AppendText(new string('-', 80) + Environment.NewLine);
                liveLog.AppendText("Complete  OK " + ok + " / FAIL " + fail + " / LOCKED " + locked + Environment.NewLine);
                liveLog.ScrollToEnd();
                logWindow.Title = "Sync result — OK " + ok + " / FAIL " + fail + " / LOCKED " + locked;
            }

            await Compare();

            if (logWindow.IsVisible)
                logWindow.Activate();
        }
        private void OpenDiff(object sender, MouseButtonEventArgs e)
        {
            if (busy || snapshot == null || !(FilesGrid.SelectedItem is DiffRow)) return;
            // Only data rows open a viewer; header/scrollbar double-clicks do not.
            if (!(ItemsControl.ContainerFromElement(FilesGrid, e.OriginalSource as DependencyObject) is ListViewItem)) return;
            try { new DiffViewWindow(snapshot, ((DiffRow)FilesGrid.SelectedItem).File) { Owner = this }.Show(); } catch (Exception ex) { ShowError(ex); }
        }
        internal void SaveState()
        {
            try
            {
                Directory.CreateDirectory(StateDirectory);
                var state = new DifferencerState { Source = SourceBox.Text, Target = TargetBox.Text };
                string temporary = StatePath + ".tmp";
                using (var stream = File.Create(temporary)) new XmlSerializer(typeof(DifferencerState)).Serialize(stream, state);
                if (File.Exists(StatePath)) File.Replace(temporary, StatePath, null); else File.Move(temporary, StatePath);
            }
            catch (Exception ex) { StatusText.Text = "Failed to save tree: " + ex.Message; }
        }
        private void ShowError(Exception ex) { MessageBox.Show(this, ex.Message, Title, MessageBoxButton.OK, MessageBoxImage.Error); }
    }
}
