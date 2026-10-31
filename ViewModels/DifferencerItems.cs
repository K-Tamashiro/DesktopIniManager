using DesktopIniManager.Services;
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace DesktopIniManager.ViewModels;

public sealed class DifferencerState
{
    public string Source { get; set; }
    public string Target { get; set; }
    public string Selected { get; set; }
    public List<string> Folders { get; set; } = new();
    public List<string> Expanded { get; set; } = new();
    public List<string> VisibleFolders { get; set; }
}

internal static class DifferencerStatusIcons
{
    private static readonly ImageSource[] icons = Load();

    private static ImageSource[] Load()
    {
        var result = new ImageSource[180];
        try
        {
            var baseDir = AppDomain.CurrentDomain.BaseDirectory;
            string[] candidates =
            {
                Path.Combine(baseDir, "Assets", "DeveloperDifferencer_iconset.icl"),
                Path.GetFullPath(Path.Combine(baseDir, "..", "..", "Assets", "DeveloperDifferencer_iconset.icl")),
                Path.GetFullPath(Path.Combine(baseDir, "..", "..", "..", "Assets", "DeveloperDifferencer_iconset.icl"))
            };

            var path = candidates.FirstOrDefault(File.Exists);
            if (path == null) return result;

            var groups = IconResourceReader.Read(path);
            for (var index = 0; index < result.Length; index++)
            {
                var group = groups.FirstOrDefault(item => item.ShellIndex == index);
                if (group != null)
                    result[index] = group.Preview;
            }
        }
        catch
        {
        }

        return result;
    }

    public static ImageSource GetFolderIcon(DiffKind kind)
    {
        return Get(Index(kind, 0));
    }

    public static ImageSource GetFileIcon(DiffKind kind)
    {
        return Get(Index(kind, 4));
    }

    public static ImageSource GetBuildFolderIcon(bool obj)
    {
        return Get(obj ? 8 : 9);
    }

    public static ImageSource GetRefreshIcon()
    {
        return Get(10);
    }

    public static ImageSource GetCustomIcon(int index)
    {
        return Get(index);
    }

    public static ImageSource GetSolutionIcon()
    {
        return Get(22) ?? Get(21);
    }

    // ...
    private static int Index(DiffKind kind, int offset)
    {
        if (kind == DiffKind.SourceOnly) return offset + 0;
        if (kind == DiffKind.TargetOnly) return offset + 1;
        if (kind == DiffKind.Same) return offset + 2;
        return offset + 3;
    }

    private static ImageSource Get(int index)
    {
        return index >= 0 && index < icons.Length ? icons[index] : null;
    }
}

internal sealed class DiffFolder : INotifyPropertyChanged
{
    private readonly int[] counts = new int[9];
    private readonly int[] selectedCounts = new int[9];
    public Action<DiffFolder, bool> Toggle;
    private bool active;
    private bool expanded;
    private bool folderSelected;
    private string label;
    public string Path { get; set; }

    public string Label
    {
        get => label;
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

    public DiffKind FolderKind => SourceExists == TargetExists ? DiffKind.Same :
        SourceExists ? DiffKind.SourceOnly : DiffKind.TargetOnly;

    public bool FolderCanSync => !string.IsNullOrEmpty(Path) && SourceExists != TargetExists;

    public bool FolderSelected
    {
        get => folderSelected;
        set
        {
            value = value && FolderCanSync;
            if (folderSelected == value) return;
            folderSelected = value;
            NotifyCheckedUpward();
        }
    }

    public ImageSource IconPreview
    {
        get
        {
            if (SourceExists && !TargetExists)
                return DifferencerStatusIcons.GetFolderIcon(DiffKind.SourceOnly);

            if (!SourceExists && TargetExists)
                return DifferencerStatusIcons.GetFolderIcon(DiffKind.TargetOnly);

            // Judge the folder icon only from differences enabled by the current category filter.
            // For example, when TargetOnly is OFF, TargetOnly differences must not make the folder Different.
            if (CountFor(Mask & DiffKind.Differences) > 0)
                return DifferencerStatusIcons.GetFolderIcon(DiffKind.Different);

            // Otherwise the folder contents match.
            return DifferencerStatusIcons.GetFolderIcon(DiffKind.Same);
        }
    }

    public DiffFolder Parent { get; set; }
    public List<DiffFolder> Children { get; } = new();
    public bool Visible { get; set; } = true;
    public List<DiffFolder> DisplayChildren { get; private set; } = new();
    public List<DiffFile> Files { get; } = new();

    public bool Expanded
    {
        get => expanded;
        set
        {
            if (expanded == value) return;
            expanded = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs("Expanded"));
        }
    }

    public bool Active
    {
        get => active;
        set
        {
            if (active == value) return;
            active = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs("Active"));
        }
    }

    public DiffKind Mask { get; set; } = DiffKind.Differences;

    public bool CanSelect
    {
        get
        {
            // Allow a common parent/root to select differences below it.
            return CountFor(Mask & DiffKind.Differences) > 0
                   || (IsFolderIncluded && MatchesFolderMask(Mask))
                   || Children.Any(child => child.CanSelect);
        }
    }

    public int SelectedCount { get; private set; }
    public int AllDifferenceCount { get; private set; }
    public Func<DiffFile, bool> IncludeFile { get; set; }
    public Func<DiffFolder, bool> IncludeFolder { get; set; }
    private bool IsFolderIncluded => IncludeFolder == null || IncludeFolder(this);

    public bool? Checked
    {
        get
        {
            var selected = SelectedFor(Mask);
            var total = CountFor(Mask & DiffKind.Differences);

            AddFolderSelectionCounts(this, Mask, ref selected, ref total);

            return selected == 0 ? false : selected == total ? true : null;
        }
        set => Toggle?.Invoke(this, value == true);
    }

    public event PropertyChangedEventHandler PropertyChanged;

    private void NotifyCheckedUpward()
    {
        for (var folder = this; folder != null; folder = folder.Parent)
            folder.PropertyChanged?.Invoke(folder, new PropertyChangedEventArgs(nameof(Checked)));
    }

    public bool MatchesFolderMask(DiffKind mask)
    {
        return FolderCanSync && (FolderKind & mask) != 0;
    }

    public void UpdateDisplayChildren()
    {
        DisplayChildren = Children.Where(f => f.Visible).ToList();
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs("DisplayChildren"));
    }

    public void RefreshIcon()
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IconPreview)));
    }

    private static int Sum(int[] values, DiffKind mask)
    {
        var result = 0;
        for (var kind = 1; kind <= 8; kind <<= 1)
            if (((int)mask & kind) != 0)
                result += values[kind];
        return result;
    }

    public int CountFor(DiffKind mask)
    {
        return Sum(counts, mask);
    }

    public int SelectedFor(DiffKind mask)
    {
        return Sum(selectedCounts, mask);
    }

    private static void AddFolderSelectionCounts(DiffFolder folder, DiffKind mask, ref int selected, ref int total)
    {
        if (folder.IsFolderIncluded && folder.MatchesFolderMask(mask))
        {
            total++;
            if (folder.FolderSelected)
                selected++;
        }

        foreach (var child in folder.Children)
            AddFolderSelectionCounts(child, mask, ref selected, ref total);
    }

    public void Refresh()
    {
        Array.Clear(counts, 0, counts.Length);
        Array.Clear(selectedCounts, 0, selectedCounts.Length);
        SelectedCount = 0;
        AllDifferenceCount = 0;
        foreach (var file in Files)
        {
            if (file.CanSync) AllDifferenceCount++;
            if (file.Selected) SelectedCount++;
            if (IncludeFile != null && !IncludeFile(file)) continue;
            counts[(int)file.Kind]++;
            if (file.Selected) selectedCounts[(int)file.Kind]++;
        }

        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs("Checked"));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs("CanSelect"));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs("IconPreview"));
    }

    public void SetFolderSelected(bool value, bool notify = true)
    {
        value = value && FolderCanSync;
        if (folderSelected == value) return;
        folderSelected = value;
        if (notify)
            NotifyCheckedUpward();
    }

    public void RefreshSelectionCounts()
    {
        Array.Clear(selectedCounts, 0, selectedCounts.Length);
        SelectedCount = 0;

        foreach (var file in Files)
        {
            if (file.Selected)
                SelectedCount++;

            if (IncludeFile != null && !IncludeFile(file))
                continue;

            if (file.Selected)
                selectedCounts[(int)file.Kind]++;
        }

        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Checked)));
    }

    public void ChangeSelectionCount(int delta, DiffKind kind, bool included = true)
    {
        if (delta == 0) return;
        SelectedCount += delta;
        if (!included) return;
        selectedCounts[(int)kind] += delta;
        if ((Mask & kind) != 0) PropertyChanged?.Invoke(this, new PropertyChangedEventArgs("Checked"));
    }
}

internal sealed class DiffSide : INotifyPropertyChanged
{
    private string info;

    public string Info
    {
        get => info;
        set
        {
            info = value;
            SummaryInfo = Regex.Replace(value ?? "", @"(\d{2}:\d{2}:\d{2})\.\d{7}", "$1");
        }
    }

    public string SummaryInfo { get; private set; }
    public string Root { get; set; }
    public string Relative { get; set; }
    public bool Exists { get; set; }
    public bool HasImage => Exists && DiffMedia.IsImage(Relative);
    public BitmapSource Thumbnail { get; private set; }
    public string Dimensions { get; private set; }

    public event PropertyChangedEventHandler PropertyChanged;

    internal Preview ReadPreview()
    {
        var result = new Preview();
        if (!HasImage) return result;
        try
        {
            var path = DeveloperDifferencerService.SafePath(Root, Relative);
            int width, height;
            using (var stream = File.OpenRead(path))
            {
                var decoder = BitmapDecoder.Create(stream, BitmapCreateOptions.DelayCreation, BitmapCacheOption.None);
                width = decoder.Frames[0].PixelWidth;
                height = decoder.Frames[0].PixelHeight;
                result.Dimensions = width + " \u00D7 " + height;
            }

            using (var stream = File.OpenRead(path))
            {
                var image = new BitmapImage();
                image.BeginInit();
                image.CacheOption = BitmapCacheOption.OnLoad;
                if ((double)width / height >= 96.0 / 64) image.DecodePixelWidth = Math.Min(96, width);
                else image.DecodePixelHeight = Math.Min(64, height);
                image.StreamSource = stream;
                image.EndInit();
                image.Freeze();
                result.Thumbnail = image;
            }
        }
        catch (Exception ex)
        {
            result.Dimensions = "Preview unavailable: " + ErrorMessages.English(ex);
        }

        return result;
    }

    internal void ApplyPreview(Preview preview)
    {
        Thumbnail = preview.Thumbnail;
        Dimensions = preview.Dimensions;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs("Thumbnail"));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs("Dimensions"));
    }

    internal void ReleaseThumbnail()
    {
        Thumbnail = null;
    }

    internal sealed class Preview
    {
        public string Dimensions;
        public BitmapSource Thumbnail;
    }
}

internal sealed class DiffRow : INotifyPropertyChanged
{
    private CancellationTokenSource previewCancellation;
    private DiffSide source, target;
    public DiffFile File { get; set; }
    public string SourceRoot { get; set; }

    public string TargetRoot { get; set; }

    // Identical entries can be numerous; format side details only for rows that are actually displayed.
    public DiffSide Source
    {
        get => source ?? (source = new DiffSide
        { Info = File.SourceInfo, Root = SourceRoot, Relative = File.RelativePath, Exists = File.Source != null });
        set => source = value;
    }

    public DiffSide Target
    {
        get => target ?? (target = new DiffSide
        { Info = File.TargetInfo, Root = TargetRoot, Relative = File.RelativePath, Exists = File.Target != null });
        set => target = value;
    }

    public string Extension => Path.GetExtension(File.RelativePath);
    public bool IsDirectInSelectedFolder { get; private set; } = true;

    public string PathFileName => File == null ? string.Empty : Path.GetFileName(File.RelativePath ?? string.Empty);

    public ImageSource StatusIcon => DifferencerStatusIcons.GetFileIcon(File.Kind);
    public ImageSource Icon { get; private set; }
    public bool IsPreviewReady { get; private set; }

    public event PropertyChangedEventHandler PropertyChanged;

    public void SetDirectInSelectedFolder(bool value)
    {
        if (IsDirectInSelectedFolder == value) return;
        IsDirectInSelectedFolder = value;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsDirectInSelectedFolder)));
    }

    public void CancelPreview()
    {
        previewCancellation?.Cancel();
    }

    public void ReleasePreview()
    {
        CancelPreview();
        if (!Source.HasImage && !Target.HasImage) return;
        Source.ReleaseThumbnail();
        Target.ReleaseThumbnail();
        Icon = null;
        IsPreviewReady = false;
    }

    public async Task LoadPreviewAsync(SemaphoreSlim workers, CancellationToken scope)
    {
        if (IsPreviewReady || (previewCancellation != null && !previewCancellation.IsCancellationRequested)) return;
        var request = CancellationTokenSource.CreateLinkedTokenSource(scope);
        previewCancellation = request;
        var entered = false;
        try
        {
            // Scrolled-away rows cancel this delay before any disk or shell work starts.
            await Task.Delay(120, request.Token);
            await workers.WaitAsync(request.Token);
            entered = true;
            var result = await Task.Run(() =>
            {
                request.Token.ThrowIfCancellationRequested();
                var left = Source.ReadPreview();
                request.Token.ThrowIfCancellationRequested();
                var right = Target.ReadPreview();
                request.Token.ThrowIfCancellationRequested();
                var side = Source.Exists ? Source : Target;
                ImageSource icon = Source.Exists ? left.Thumbnail : right.Thumbnail;
                if (icon == null && side.Exists)
                    icon = FileIconService.GetTypeIcon(Extension);
                return Tuple.Create(left, right, icon);
            }, request.Token);
            request.Token.ThrowIfCancellationRequested();
            Source.ApplyPreview(result.Item1);
            Target.ApplyPreview(result.Item2);
            Icon = result.Item3;
            IsPreviewReady = true;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs("Icon"));
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception)
        {
            if (!request.IsCancellationRequested)
                IsPreviewReady = true; /* Unavailable previews must not break selection or synchronization. */
        }
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
        IsPreviewReady = false;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs("Source"));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs("Target"));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs("StatusIcon"));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs("Icon"));
    }
}

/// <summary>Displays and synchronizes differences between development directories.</summary>