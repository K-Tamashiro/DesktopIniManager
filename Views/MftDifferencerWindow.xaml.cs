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
            var result = new ImageSource[11];
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
        public static ImageSource GetBuildFolderIcon(bool obj) { return Get(obj ? 8 : 9); }
        public static ImageSource GetRefreshIcon() { return Get(10); }

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
        private string label;
        public string Label
        {
            get { return label; }
            set
            {
                if (string.Equals(label, value, StringComparison.Ordinal)) return;
                label = value;
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs("Label"));
            }
        }
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
        public void UpdateDisplayChildren()
        {
            DisplayChildren = Children.Where(f => f.Visible).ToList();
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs("DisplayChildren"));
        }
        public List<DiffFile> Files { get; } = new List<DiffFile>();
        private bool expanded;
        public bool Expanded { get { return expanded; } set { if (expanded == value) return; expanded = value; PropertyChanged?.Invoke(this, new PropertyChangedEventArgs("Expanded")); } }
        private bool active;
        public bool Active
        {
            get { return active; }
            set { if (active == value) return; active = value; PropertyChanged?.Invoke(this, new PropertyChangedEventArgs("Active")); }
        }
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
        public int AllDifferenceCount { get; private set; }
        public Func<DiffFile, bool> IncludeFile { get; set; }
        public bool? Checked
        {
            get { int selected = SelectedFor(Mask); return selected == 0 ? false : selected == CountFor(Mask & DiffKind.Differences) ? (bool?)true : null; }
            set { Toggle?.Invoke(this, value == true); }
        }
        public void Refresh()
        {
            Array.Clear(counts, 0, counts.Length); Array.Clear(selectedCounts, 0, selectedCounts.Length);
            SelectedCount = 0; AllDifferenceCount = 0;
            foreach (var file in Files)
            {
                if (file.CanSync) AllDifferenceCount++;
                if (file.Selected) SelectedCount++;
                if (IncludeFile != null && !IncludeFile(file)) continue;
                counts[(int)file.Kind]++;
                if (file.Selected) selectedCounts[(int)file.Kind]++;
            }
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs("Checked"));
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs("IconPreview"));
        }
        public void ChangeSelectionCount(int delta, DiffKind kind, bool included = true)
        {
            if (delta == 0) return;
            SelectedCount += delta;
            if (!included) return;
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
            catch (Exception ex) { result.Dimensions = "Preview unavailable: " + ErrorMessages.English(ex); }
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
        public bool IsPreviewReady { get { return previewLoaded; } }
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
        internal void RefreshFromFile()
        {
            ReleasePreview();
            source = null;
            target = null;
            Icon = null;
            previewLoaded = false;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs("Source"));
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs("Target"));
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs("StatusIcon"));
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs("Icon"));
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
        private CancellationTokenSource compareCts;
        private int previewInFlight;
        private bool syncingTreeFromFile;
        private readonly SemaphoreSlim previewWorkers = new SemaphoreSlim(2);
        private CancellationTokenSource previewScope = new CancellationTokenSource();
        private HashSet<string> cachedVisibleFolders;
        private DiffKind kindMask = DiffKind.Differences;
        private bool showObj, showBin;
        private bool IncludeBuildFolderFile(DiffFile file)
        {
            string directory = Path.GetDirectoryName(file.RelativePath) ?? "";
            return directory.Split(new[] { '\\', '/' }, StringSplitOptions.RemoveEmptyEntries)
                .All(part => (showObj || !part.Equals("obj", StringComparison.OrdinalIgnoreCase)) &&
                             (showBin || !part.Equals("bin", StringComparison.OrdinalIgnoreCase)));
        }
        private void BuildFolderFilter_Click(object sender, RoutedEventArgs e)
        {
            showObj = ObjFilter.IsChecked == true; showBin = BinFilter.IsChecked == true;
            if (snapshot == null) return;
            RefreshChecks(); ApplyKindFilter();
            StatusText.Text = folders[""].CountFor(DiffKind.Differences) + " differences / " + folders[""].CountFor(DiffKind.Same) + " identical (OBJ/BIN filters applied). Check items to synchronize.";
        }
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
            SameFilterIcon.Source = MftDiffStatusIcons.GetFileIcon(DiffKind.Same);
            DifferentFilterIcon.Source = MftDiffStatusIcons.GetFileIcon(DiffKind.Different);
            SourceOnlyFilterIcon.Source = MftDiffStatusIcons.GetFileIcon(DiffKind.SourceOnly);
            TargetOnlyFilterIcon.Source = MftDiffStatusIcons.GetFileIcon(DiffKind.TargetOnly);
            RefreshCompareIcon.Source = MftDiffStatusIcons.GetRefreshIcon();
            ObjFilterIcon.Source = MftDiffStatusIcons.GetBuildFolderIcon(true);
            BinFilterIcon.Source = MftDiffStatusIcons.GetBuildFolderIcon(false);
            AttachElevationToggle();
            TreeCompact = SettingsService.LoadTreeCompact();
            SourceBox.TextChanged += RootsChanged; TargetBox.TextChanged += RootsChanged;
            // HistoryTextBox persists Source/Target via HistoryKey.
            Closing += (s, e) => { if (busy) { e.Cancel = true; return; } SaveState(); };
            Closed += (s, e) => { compareCts?.Cancel(); compareCts?.Dispose(); previewScope.Cancel(); previewScope.Dispose(); DetachSelectionHandlers(); };
            Loaded += (s, e) => SetFilePanelBusy(false);
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
            catch (Exception ex) { StatusText.Text = "Failed to restore tree: " + ErrorMessages.English(ex); }
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

            var toggle = ElevationService.Shared.CreateToggle(this, "mft");
            toggle.VerticalAlignment = VerticalAlignment.Center;
            Grid.SetColumn(toggle, 1);
            header.Children.Add(toggle);
        }

        internal void CaptureElevation(ElevationResumeState session)
        {
            session.Source = SourceBox.Text;
            session.Target = TargetBox.Text;
            session.CompareDates = CompareTimestampBox.IsChecked == true;
        }

        internal void RestoreElevation(ElevationResumeState session)
        {
            SourceBox.Text = session.Source ?? string.Empty;
            TargetBox.Text = session.Target ?? string.Empty;
            CompareTimestampBox.IsChecked = session.CompareDates;
            treeSource = SourceBox.Text;
            treeTarget = TargetBox.Text;
            selectedFolder = string.Empty;
            cachedVisibleFolders = null;
            StatusText.Text = "Source and Target restored. Click Compare to build the difference tree.";
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
            if (row == null || snapshot == null) return;
            bool wait = !row.IsPreviewReady;
            if (wait) previewInFlight++;
            try { await row.LoadPreviewAsync(previewWorkers, previewScope.Token); }
            finally
            {
                if (wait)
                {
                    previewInFlight--;
                    if (previewInFlight <= 0 && !busy) SetFilePanelBusy(false);
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
                box.Text = path;
                box.CommitHistory();
            }
            catch (Exception ex) { ShowError(ex); }
        }
        private async void CompareClick(object sender, RoutedEventArgs e) { await Compare(); }
        private async void RefreshCompare_Click(object sender, RoutedEventArgs e)
        {
            await RefreshSelectedFolderAsync();
        }

        private async Task RefreshSelectedFolderAsync()
        {
            if (busy || snapshot == null) return;
            if (!SelectedFolderHasDirectFiles()) return;

            string folder = selectedFolder ?? string.Empty;
            bool compareTimestamp = CompareTimestampBox.IsChecked == true;
            DiffFile[] targets = snapshot.Files
                .Where(f => IsDirectChildFile(folder, f.RelativePath))
                .ToArray();

            SetBusy(true);
            CancelCompareButton.IsEnabled = false;
            SetFilePanelBusy(true, "Refreshing files in selected folder…");
            StatusText.Text = "Refreshing " + targets.Length + " file(s)…";

            try
            {
                foreach (DiffFile file in targets)
                {
                    file.CompareTimestamp = compareTimestamp;
                    await RefreshFileAsync(file);
                }
                ApplyKindFilter();
                StatusText.Text = "Selected folder files refreshed. " +
                    folders[""].CountFor(DiffKind.Differences) + " differences / " +
                    folders[""].CountFor(DiffKind.Same) + " identical.";
            }
            catch (Exception ex)
            {
                StatusText.Text = "Refresh failed: " + ErrorMessages.English(ex);
                ShowError(ex);
            }
            finally
            {
                SetBusy(false);
                UpdateRefreshButtonState();
            }
        }
        private static bool IsDirectChildFile(string folder, string relativePath)
        {
            string parent = Path.GetDirectoryName(relativePath) ?? string.Empty;
            return string.Equals(parent, folder ?? string.Empty, StringComparison.OrdinalIgnoreCase);
        }

        private bool SelectedFolderHasDirectFiles()
        {
            if (snapshot == null) return false;
            string folder = selectedFolder ?? string.Empty;
            return snapshot.Files.Any(f => IsDirectChildFile(folder, f.RelativePath));
        }

        private void UpdateRefreshButtonState()
        {
            if (RefreshCompareButton == null) return;
            RefreshCompareButton.IsEnabled =
                !busy && !comparing && snapshot != null && SelectedFolderHasDirectFiles();
        }
        private void RemoveFileFromFolderHierarchy(DiffFile file)
        {
            string path = Path.GetDirectoryName(file.RelativePath) ?? string.Empty;
            while (true)
            {
                DiffFolder folder;
                if (folders.TryGetValue(path, out folder))
                    folder.Files.Remove(file);
                if (path.Length == 0) break;
                path = Path.GetDirectoryName(path) ?? string.Empty;
            }
        }

        private void AddFileToFolderHierarchy(DiffFile file)
        {
            string path = Path.GetDirectoryName(file.RelativePath) ?? string.Empty;
            while (true)
            {
                DiffFolder folder;
                if (folders.TryGetValue(path, out folder))
                    folder.Files.Add(file);
                if (path.Length == 0) break;
                path = Path.GetDirectoryName(path) ?? string.Empty;
            }
        }

        private void UpdateSelectedFolderNodes(string requestedFolder, HashSet<string> refreshedFolders, Func<string, bool> inSelectedFolder)
        {
            var desired = new HashSet<string>(refreshedFolders ?? new HashSet<string>(), StringComparer.OrdinalIgnoreCase);
            if (requestedFolder.Length == 0)
                desired.Add(string.Empty);

            string[] stale = folders.Keys
                .Where(path => path.Length > 0 && inSelectedFolder(path) && !desired.Contains(path))
                .OrderByDescending(path => path.Length)
                .ToArray();

            foreach (string path in stale)
            {
                DiffFolder node;
                if (!folders.TryGetValue(path, out node)) continue;
                string parentPath = Path.GetDirectoryName(path) ?? string.Empty;
                DiffFolder parent;
                if (folders.TryGetValue(parentPath, out parent))
                    parent.Children.Remove(node);
                folders.Remove(path);
            }

            foreach (string path in desired
                .Where(path => path.Length > 0)
                .OrderBy(path => path.Count(ch => ch == '\\'))
                .ThenBy(path => path, StringComparer.OrdinalIgnoreCase))
            {
                DiffFolder node;
                if (!folders.TryGetValue(path, out node))
                {
                    node = CreateFolderNode(path, string.Equals(path, requestedFolder, StringComparison.OrdinalIgnoreCase));
                    folders.Add(path, node);
                    string parentPath = Path.GetDirectoryName(path) ?? string.Empty;
                    DiffFolder parent;
                    if (folders.TryGetValue(parentPath, out parent))
                        parent.Children.Add(node);
                }
                UpdateFolderExistence(node);
            }

            DiffFolder root;
            if (folders.TryGetValue(string.Empty, out root))
                UpdateFolderExistence(root);
        }

        private DiffFolder CreateFolderNode(string path, bool expanded)
        {
            var node = new DiffFolder
            {
                Path = path,
                Expanded = expanded,
                Active = false
            };
            node.IncludeFile = IncludeBuildFolderFile;
            node.Toggle = (folder, value) =>
            {
                bulk = true;
                foreach (DiffFile file in folder.Files)
                    if (file.CanSync && IncludeBuildFolderFile(file) && (file.Kind & kindMask) != 0)
                        file.Selected = value;
                bulk = false;
                RefreshChecks();
            };
            UpdateFolderExistence(node);
            return node;
        }

        private void UpdateFolderExistence(DiffFolder node)
        {
            string sourcePath = node.Path.Length == 0
                ? snapshot.SourceRoot.TrimEnd('\\')
                : System.IO.Path.Combine(snapshot.SourceRoot, node.Path);
            string targetPath = node.Path.Length == 0
                ? snapshot.TargetRoot.TrimEnd('\\')
                : System.IO.Path.Combine(snapshot.TargetRoot, node.Path);

            node.SourceExists = Directory.Exists(sourcePath);
            node.TargetExists = Directory.Exists(targetPath);
            node.SourceEmpty = node.SourceExists && !Directory.EnumerateFileSystemEntries(sourcePath).Any();
            node.TargetEmpty = node.TargetExists && !Directory.EnumerateFileSystemEntries(targetPath).Any();
        }

        private void RefreshSelectedFolderPresentation(string requestedFolder, Func<string, bool> inSelectedFolder)
        {
            Func<string, bool> isAncestor = path =>
                path.Length == 0 ||
                requestedFolder.Length == 0 ||
                string.Equals(path, requestedFolder, StringComparison.OrdinalIgnoreCase) ||
                requestedFolder.StartsWith(path + "\\", StringComparison.OrdinalIgnoreCase);

            DiffFolder[] affected = folders.Values
                .Where(folder => inSelectedFolder(folder.Path) || isAncestor(folder.Path))
                .ToArray();

            foreach (DiffFolder folder in affected)
            {
                folder.Refresh();
                folder.Mask = kindMask;
                folder.Visible = folder.Path.Length == 0 || folder.CountFor(kindMask) > 0;
                folder.Label = (folder.Path.Length == 0 ? RootLabel() : Path.GetFileName(folder.Path)) +
                               " (" + folder.CountFor(kindMask) + ")";
            }

            foreach (DiffFolder folder in affected.OrderByDescending(folder => folder.Path.Length))
                folder.UpdateDisplayChildren();

            cachedVisibleFolders = new HashSet<string>(
                folders.Values
                    .Where(folder => folder.Path.Length == 0 || folder.CountFor(DiffKind.Differences) > 0)
                    .Select(folder => folder.Path),
                StringComparer.OrdinalIgnoreCase);
        }

        private void SelectNearestExistingFolder(string path)
        {
            string current = path ?? string.Empty;
            while (!folders.ContainsKey(current) && current.Length > 0)
                current = Path.GetDirectoryName(current) ?? string.Empty;

            foreach (DiffFolder folder in folders.Values)
                if (folder.Active) folder.Active = false;

            selectedFolder = folders.ContainsKey(current) ? current : string.Empty;
            DiffFolder selected;
            if (folders.TryGetValue(selectedFolder, out selected))
                selected.Active = true;
        }
        private void CancelCompareClick(object sender, RoutedEventArgs e)
        {
            if (!comparing) return;
            try { compareCts?.Cancel(); } catch (ObjectDisposedException) { }
            StatusText.Text = "Cancelling comparison…";
            CancelCompareButton.IsEnabled = false;
        }
        private async Task Compare()
        {
            var expanded = folders.Values.Where(f => f.Expanded).Select(f => f.Path).ToList();
            ClearComparisonView(); SetBusy(true);
            comparing = true;
            compareCts?.Dispose();
            compareCts = new CancellationTokenSource();
            var token = compareCts.Token;
            CompareProgress.Visibility = Visibility.Visible;
            CompareProgress.IsIndeterminate = true;
            StatusText.Text = "Enumerating MFT and comparing timestamps and sizes…";
            string source = SourceBox.Text, target = TargetBox.Text;
            try
            {
                var progress = new Progress<DiffProgress>(UpdateProgress);
                bool compareTimestamp = CompareTimestampBox.IsChecked == true;
                DiffSnapshot fresh = await Task.Run(() => MftDifferencerService.Compare(source, target, progress, compareTimestamp, token), token);
                token.ThrowIfCancellationRequested();
                StatusText.Text = "Updating the difference tree…";
                if (FilePanelBusyText != null) FilePanelBusyText.Text = "Updating the difference tree and file list…";
                snapshot = fresh; treeSource = source; treeTarget = target;
                rows = await Task.Run(() => snapshot.Files
                    .Select(f => new DiffRow { File = f, SourceRoot = snapshot.SourceRoot, TargetRoot = snapshot.TargetRoot })
                    .ToList(), token);
                token.ThrowIfCancellationRequested();
                await BuildTreeAsync(snapshot.Folders, expanded, selectedFolder, token);
                StatusText.Text = folders[""].CountFor(DiffKind.Differences) + " differences / " + folders[""].CountFor(DiffKind.Same) + " identical. Check items to synchronize.";
                SaveState();
            }
            catch (OperationCanceledException) { StatusText.Text = "Compare cancelled"; }
            catch (Exception ex) { StatusText.Text = "Compare failed (sync disabled): " + ErrorMessages.English(ex); ShowError(ex); }
            finally
            {
                comparing = false;
                CompareProgress.Visibility = Visibility.Collapsed;
                CompareProgress.IsIndeterminate = false;
                SetBusy(false);
            }
        }
        private void UpdateProgress(DiffProgress progress)
        {
            if (!comparing) return;
            bool unknown = progress.Total == 0;
            CompareProgress.IsIndeterminate = unknown;
            CompareProgress.Maximum = Math.Max(1, progress.Total);
            CompareProgress.Value = progress.Completed;
            string detail = unknown ? progress.Stage : progress.Stage + " — " + progress.Completed.ToString("N0") + " / " + progress.Total.ToString("N0");
            if (FilePanelBusyText != null) FilePanelBusyText.Text = string.IsNullOrEmpty(detail) ? "Please wait…" : detail;
            StatusText.Text = progress.Stage + (unknown ? "" : " — " + progress.Completed.ToString("N0") + " / " + progress.Total.ToString("N0") + " items");
        }
        private async Task BuildTreeAsync(IEnumerable<string> paths, IEnumerable<string> expanded, string selected, CancellationToken token)
        {
            folders.Clear();
            var all = new HashSet<string>(paths, StringComparer.OrdinalIgnoreCase) { "" };
            foreach (string path in all.Where(p => p.Length > 0).ToArray())
            {
                string p = Path.GetDirectoryName(path);
                while (!string.IsNullOrEmpty(p))
                {
                    all.Add(p);
                    p = Path.GetDirectoryName(p);
                }
            }

            var expansion = new HashSet<string>(expanded ?? new string[0], StringComparer.OrdinalIgnoreCase);
            selectedFolder = selected != null && all.Contains(selected) ? selected : "";

            int processed = 0;
            foreach (string path in all.OrderBy(p => p.Length).ThenBy(p => p, StringComparer.OrdinalIgnoreCase))
            {
                token.ThrowIfCancellationRequested();

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
                node.IncludeFile = IncludeBuildFolderFile;
                node.Toggle = (folder, value) => { bulk = true; foreach (DiffFile file in folder.Files) if (file.CanSync && IncludeBuildFolderFile(file) && (file.Kind & kindMask) != 0) file.Selected = value; bulk = false; RefreshChecks(); };
                folders.Add(path, node);
                if (path.Length > 0) folders[Path.GetDirectoryName(path) ?? ""].Children.Add(node);

                if (++processed % 64 == 0)
                    await Dispatcher.Yield(DispatcherPriority.Background);
            }

            if (snapshot != null)
            {
                processed = 0;
                foreach (DiffFile file in snapshot.Files)
                {
                    token.ThrowIfCancellationRequested();
                    string path = Path.GetDirectoryName(file.RelativePath) ?? "";
                    while (true)
                    {
                        DiffFolder folder;
                        if (folders.TryGetValue(path, out folder)) folder.Files.Add(file);
                        if (path.Length == 0) break;
                        path = Path.GetDirectoryName(path) ?? "";
                    }

                    if (++processed % 256 == 0)
                        await Dispatcher.Yield(DispatcherPriority.Background);
                }
            }

            processed = 0;
            foreach (DiffFolder folder in folders.Values)
            {
                token.ThrowIfCancellationRequested();
                folder.Refresh();
                if (++processed % 128 == 0)
                    await Dispatcher.Yield(DispatcherPriority.Background);
            }

            if (snapshot != null)
                cachedVisibleFolders = new HashSet<string>(folders.Values.Where(f => f.Path.Length == 0 || f.CountFor(DiffKind.Differences) > 0).Select(f => f.Path), StringComparer.OrdinalIgnoreCase);

            DetachSelectionHandlers();
            if (snapshot != null)
            {
                processed = 0;
                foreach (DiffFile file in snapshot.Files)
                {
                    file.PropertyChanged += FileSelectionChanged;
                    if (++processed % 512 == 0)
                        await Dispatcher.Yield(DispatcherPriority.Background);
                }
            }

            await ApplyKindFilterAsync(token);
        }

        private async Task ApplyKindFilterAsync(CancellationToken token)
        {
            FolderTree.ItemsSource = null;
            int processed = 0;
            foreach (DiffFolder folder in folders.Values)
            {
                token.ThrowIfCancellationRequested();
                folder.Mask = kindMask;
                folder.Visible = folder.Path.Length == 0 || (snapshot != null ? folder.CountFor(kindMask) > 0 : cachedVisibleFolders == null || cachedVisibleFolders.Contains(folder.Path));
                folder.Label = (folder.Path.Length == 0 ? RootLabel() : Path.GetFileName(folder.Path)) + (snapshot == null ? " (not compared)" : " (" + folder.CountFor(kindMask) + ")");

                if (++processed % 128 == 0)
                    await Dispatcher.Yield(DispatcherPriority.Background);
            }

            if (!folders[selectedFolder].Visible)
            {
                folders[selectedFolder].Active = false;
                selectedFolder = "";
                folders[""].Active = true;
            }

            processed = 0;
            foreach (DiffFolder folder in folders.Values)
            {
                folder.UpdateDisplayChildren();
                if (++processed % 128 == 0)
                    await Dispatcher.Yield(DispatcherPriority.Background);
            }

            FolderTree.ItemsSource = new[] { folders[""] };
            await Dispatcher.Yield(DispatcherPriority.Background);

            var visible = await Task.Run(() => rows.Where(r => IncludeBuildFolderFile(r.File) &&
                                                               (r.File.Kind & kindMask) != 0 &&
                                                               (selectedFolder.Length == 0 || r.File.RelativePath.StartsWith(selectedFolder + "\\", StringComparison.OrdinalIgnoreCase)))
                                                   .ToList(), token);
            token.ThrowIfCancellationRequested();

            FilesGrid.ItemsSource = visible;
            FilePanelTitle.Text = "Files — " + (selectedFolder.Length == 0 ? "all levels" : selectedFolder) + " (" + visible.Count + ")";
            UpdateSelectionSummary();

            // Give WPF one render pass before the busy overlay is removed in Compare().
            await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
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
                DiffFolder folder; if (folders.TryGetValue(path, out folder)) folder.ChangeSelectionCount(delta, file.Kind, IncludeBuildFolderFile(file));
                if (path.Length == 0) break;
                path = Path.GetDirectoryName(path) ?? "";
            }
            UpdateSelectionSummary();
        }
        private void UpdateSelectionSummary()
        {
            DiffFolder root = null;
            int count = snapshot != null && folders.TryGetValue("", out root) ? root.SelectedCount : 0;
            int total = root == null ? 0 : root.AllDifferenceCount;
            int hidden = root == null ? 0 : count - root.SelectedFor(kindMask);
            CountText.Text = "Selected " + count + " / " + total + (hidden > 0 ? " (includes " + hidden + " hidden)" : "");
            ForwardButton.IsEnabled = ReverseButton.IsEnabled = !busy && count > 0;
        }
        private string RootLabel()
        { return string.IsNullOrWhiteSpace(treeSource) ? "All folders" : Path.GetFileName(treeSource.TrimEnd('\\', '/')) is string name && name.Length > 0 ? name : treeSource; }
        private void FolderChanged(object sender, RoutedPropertyChangedEventArgs<object> e)
        {
            if (syncingTreeFromFile) return;
            var folder = e.NewValue as DiffFolder; if (folder == null) return; selectedFolder = folder.Path; Filter();
        }
        private void FileListSelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (syncingTreeFromFile || busy || snapshot == null) return;
            var row = FilesGrid.SelectedItem as DiffRow;
            if (row == null) return;
            RevealContainingFolder(Path.GetDirectoryName(row.File.RelativePath) ?? "");
        }
        private void RevealContainingFolder(string path)
        {
            if (folders.Count == 0) return;
            DiffFolder target = null;
            string current = path ?? "";
            while (true)
            {
                if (folders.TryGetValue(current, out target) && target.Visible) break;
                if (current.Length == 0) { target = folders.ContainsKey("") ? folders[""] : null; break; }
                current = Path.GetDirectoryName(current) ?? "";
            }
            if (target == null) return;

            string ancestor = target.Path;
            while (ancestor.Length > 0)
            {
                ancestor = Path.GetDirectoryName(ancestor) ?? "";
                DiffFolder parent;
                if (folders.TryGetValue(ancestor, out parent)) parent.Expanded = true;
            }

            syncingTreeFromFile = true;
            foreach (DiffFolder folder in folders.Values)
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
                if (folders.TryGetValue(current, out node)) path.Add(node);
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
        private void Filter()
        {
            var visible = rows.Where(r => IncludeBuildFolderFile(r.File) && (r.File.Kind & kindMask) != 0 && (selectedFolder.Length == 0 || r.File.RelativePath.StartsWith(selectedFolder + "\\", StringComparison.OrdinalIgnoreCase))).ToList();
            FilesGrid.ItemsSource = visible;
            FilePanelTitle.Text = "Files — " + (selectedFolder.Length == 0 ? "all levels" : selectedFolder) + " (" + visible.Count + ")";
            UpdateRefreshButtonState();
        }
        private void SetBusy(bool value)
        {
            busy = value;
            ElevationService.Shared.SetBusy(this, value);
            RootControls.IsEnabled = CompareButton.IsEnabled = CompareTimestampBox.IsEnabled = CleanSolutionButton.IsEnabled = !value;
            CancelCompareButton.IsEnabled = value;
            CategoryFilters.IsEnabled = !value && snapshot != null;
            FolderTree.IsHitTestVisible = FilesGrid.IsHitTestVisible = !value;
            FolderTree.Focusable = FilesGrid.Focusable = !value;
            SetFilePanelBusy(value, value ? "Please wait…" : null);
            UpdateSelectionSummary();
        }
        private void SetFilePanelBusy(bool busyPanel, string message = null)
        {
            if (FilePanelBusy == null) return;
            FilePanelBusy.Visibility = busyPanel ? Visibility.Visible : Visibility.Collapsed;
            if (FilePanelBusyText != null && message != null) FilePanelBusyText.Text = message;
            if (busyPanel) RestartPanelProgress();
            else StopPanelProgress();
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
                Content = ActionContent("\uE711", "Cancel"),
                Style = (Style)FindResource("MftActionButton"),
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
                Style = (Style)FindResource("MftActionButton"),
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

        private async Task Sync(bool toTarget)
        {
            if (busy || snapshot == null) return;
            DiffFile[] files = snapshot.Files.Where(f => f.CanSync && f.Selected).ToArray();
            if (files.Length == 0) return;

            string direction = toTarget ? "Source to Target" : "Target to Source";
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
                log = new List<string> { "FAIL " + ErrorMessages.English(ex) };
                if (logWindow.IsVisible)
                {
                    liveLog.AppendText("FAIL " + ErrorMessages.English(ex) + Environment.NewLine);
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
                report = "Failed to save log: " + ErrorMessages.English(ex) + "\n\n" + report;
                if (logWindow.IsVisible)
                    liveLog.AppendText("Failed to save log: " + ErrorMessages.English(ex) + Environment.NewLine);
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
        internal async Task<bool> RefreshFileAsync(DiffFile file)
        {
            if (snapshot == null || file == null || comparing) return false;

            var stamps = await Task.Run(() =>
            {
                string sourcePath = MftDifferencerService.SafePath(snapshot.SourceRoot, file.RelativePath);
                string targetPath = MftDifferencerService.SafePath(snapshot.TargetRoot, file.RelativePath);
                return Tuple.Create(DiffStamp.Read(sourcePath), DiffStamp.Read(targetPath));
            });

            DiffStamp source = stamps.Item1;
            DiffStamp target = stamps.Item2;
            bool changed = !DiffStamp.Same(file.Source, source, file.CompareTimestamp) ||
                           !DiffStamp.Same(file.Target, target, file.CompareTimestamp);
            if (!changed) return false;

            var refreshedFile = new DiffFile
            {
                RelativePath = file.RelativePath,
                Source = source,
                Target = target,
                CompareTimestamp = file.CompareTimestamp
            };
            refreshedFile.Selected = file.Selected;

            file.PropertyChanged -= FileSelectionChanged;
            refreshedFile.PropertyChanged += FileSelectionChanged;

            int fileIndex = snapshot.Files.FindIndex(f => ReferenceEquals(f, file));
            if (fileIndex < 0)
                fileIndex = snapshot.Files.FindIndex(f => string.Equals(f.RelativePath, file.RelativePath, StringComparison.OrdinalIgnoreCase));
            if (fileIndex >= 0)
                snapshot.Files[fileIndex] = refreshedFile;

            DiffRow row = rows.FirstOrDefault(r => ReferenceEquals(r.File, file));
            if (row == null)
                row = rows.FirstOrDefault(r => string.Equals(r.File.RelativePath, file.RelativePath, StringComparison.OrdinalIgnoreCase));
            if (row != null)
            {
                row.File = refreshedFile;
                row.RefreshFromFile();
            }

            foreach (DiffFolder folder in folders.Values)
            {
                for (int i = 0; i < folder.Files.Count; i++)
                {
                    if (ReferenceEquals(folder.Files[i], file) ||
                        string.Equals(folder.Files[i].RelativePath, file.RelativePath, StringComparison.OrdinalIgnoreCase))
                    {
                        folder.Files[i] = refreshedFile;
                        break;
                    }
                }
                folder.Refresh();
            }

            ApplyKindFilter();
            StatusText.Text = folders[""].CountFor(DiffKind.Differences) + " differences / " +
                              folders[""].CountFor(DiffKind.Same) + " identical. External edit refreshed: " +
                              refreshedFile.RelativePath;
            return true;
        }

        private void OpenDiff(object sender, MouseButtonEventArgs e)
        {
            if (busy || snapshot == null || !(FilesGrid.SelectedItem is DiffRow)) return;
            // Only data rows open a viewer; header/scrollbar double-clicks do not.
            if (!(ItemsControl.ContainerFromElement(FilesGrid, e.OriginalSource as DependencyObject) is ListViewItem)) return;
            var selectedFile = ((DiffRow)FilesGrid.SelectedItem).File;
            if (DiffMedia.IsBinary(selectedFile.RelativePath))
            {
                MessageBox.Show(this, DiffMedia.BinaryMessage, "Diff View", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }
            try { new DiffViewWindow(snapshot, selectedFile) { Owner = this }.Show(); } catch (Exception ex) { ShowError(ex); }
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
            catch (Exception ex) { StatusText.Text = "Failed to save tree: " + ErrorMessages.English(ex); }
        }
        private void ShowError(Exception ex) { MessageBox.Show(this, ErrorMessages.English(ex), Title, MessageBoxButton.OK, MessageBoxImage.Error); }
    }
}
