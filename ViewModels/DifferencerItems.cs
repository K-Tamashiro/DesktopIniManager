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

namespace DesktopIniManager.ViewModels
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

    internal static class DifferencerStatusIcons
    {
        private static readonly ImageSource[] icons = Load();

        private static ImageSource[] Load()
        {
            var result = new ImageSource[23]; // 0〜22まで拡張
            try
            {
                string baseDir = AppDomain.CurrentDomain.BaseDirectory;
                string[] candidates =
                {
                    System.IO.Path.Combine(baseDir, "Assets", "DeveloperDifferencer_iconset.icl"),
                    System.IO.Path.GetFullPath(System.IO.Path.Combine(baseDir, "..", "..", "Assets", "DeveloperDifferencer_iconset.icl")),
                    System.IO.Path.GetFullPath(System.IO.Path.Combine(baseDir, "..", "..", "..", "Assets", "DeveloperDifferencer_iconset.icl"))
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
        public static ImageSource GetCustomIcon(int index) { return Get(index); } // 追加
        // ...
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
                    return DifferencerStatusIcons.GetFolderIcon(DiffKind.SourceOnly);

                if (!SourceExists && TargetExists && TargetEmpty)
                    return DifferencerStatusIcons.GetFolderIcon(DiffKind.TargetOnly);

                // Any differing/source-only/target-only file below this folder means X.
                if (CountFor(DiffKind.Differences) > 0)
                    return DifferencerStatusIcons.GetFolderIcon(DiffKind.Different);

                // Otherwise the folder contents match.
                return DifferencerStatusIcons.GetFolderIcon(DiffKind.Same);
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
                string path = DeveloperDifferencerService.SafePath(Root, Relative);
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
        public ImageSource StatusIcon { get { return DifferencerStatusIcons.GetFileIcon(File.Kind); } }
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
    /// <summary>Displays and synchronizes differences between development directories.</summary>
}
