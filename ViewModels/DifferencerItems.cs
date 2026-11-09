using DesktopIniManager.Properties;
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
    private static readonly ImageSource[] Icons = Load();

    /// <summary>
    ///     Developer Differencer 用のアイコン ライブラリを候補の場所から探し、アイコンを読み込みます。
    /// </summary>
    /// <returns>インデックス順のアイコン配列。読み込めなかった要素は null。</returns>
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
            // Ignore errors; the icons will be missing but the differencer will still work.
        }

        return result;
    }

    /// <summary>
    ///     比較結果の種類に対応するフォルダー アイコンを取得します。
    /// </summary>
    /// <param name="kind">比較結果の種類。</param>
    /// <returns>フォルダー アイコン。</returns>
    public static ImageSource GetFolderIcon(DiffKind kind)
    {
        return Get(Index(kind, 0));
    }

    /// <summary>
    ///     比較結果の種類に対応するファイル アイコンを取得します。
    /// </summary>
    /// <param name="kind">比較結果の種類。</param>
    /// <returns>ファイル アイコン。</returns>
    public static ImageSource GetFileIcon(DiffKind kind)
    {
        return Get(Index(kind, 4));
    }

    /// <summary>
    ///     obj または bin フォルダーのアイコンを取得します。
    /// </summary>
    /// <param name="obj">obj フォルダーの場合は true、bin フォルダーの場合は false。</param>
    /// <returns>フォルダー アイコン。</returns>
    public static ImageSource GetBuildFolderIcon(bool obj)
    {
        return Get(obj ? 8 : 9);
    }

    /// <summary>
    ///     再読み込みのアイコンを取得します。
    /// </summary>
    /// <returns>再読み込みのアイコン。</returns>
    public static ImageSource GetRefreshIcon()
    {
        return Get(10);
    }

    /// <summary>
    ///     アイコン ライブラリ内の指定インデックスのアイコンを取得します。
    /// </summary>
    /// <param name="index">アイコンのインデックス。</param>
    /// <returns>アイコン。範囲外の場合は null。</returns>
    public static ImageSource GetCustomIcon(int index)
    {
        return Get(index);
    }

    /// <summary>
    ///     ソリューションのアイコンを取得します。
    /// </summary>
    /// <returns>ソリューションのアイコン。</returns>
    public static ImageSource GetSolutionIcon()
    {
        return Get(22) ?? Get(21);
    }

    // ...
    /// <summary>
    ///     比較結果の種類から、アイコン ライブラリ内のインデックスを求めます。
    /// </summary>
    /// <param name="kind">比較結果の種類。</param>
    /// <param name="offset">アイコン群の先頭インデックス。</param>
    /// <returns>アイコンのインデックス。</returns>
    private static int Index(DiffKind kind, int offset)
    {
        if (kind == DiffKind.SourceOnly) return offset + 0;
        if (kind == DiffKind.TargetOnly) return offset + 1;
        if (kind == DiffKind.Same) return offset + 2;
        return offset + 3;
    }

    /// <summary>
    ///     読み込み済みのアイコンを取得します。
    /// </summary>
    /// <param name="index">アイコンのインデックス。</param>
    /// <returns>アイコン。範囲外の場合は null。</returns>
    private static ImageSource Get(int index)
    {
        return index >= 0 && index < Icons.Length ? Icons[index] : null;
    }
}

internal sealed class DiffFolder : INotifyPropertyChanged
{
    private readonly int[] _counts = new int[9];
    private readonly int[] _selectedCounts = new int[9];
    public Action<DiffFolder, bool> Toggle;
    private bool _active;
    private bool _expanded;
    private bool _folderSelected;
    private string _label;
    public string Path { get; set; }

    public string Label
    {
        get => _label;
        set
        {
            if (string.Equals(_label, value, StringComparison.Ordinal)) return;
            _label = value;
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
        get => _folderSelected;
        set
        {
            value = value && FolderCanSync;
            if (_folderSelected == value) return;
            _folderSelected = value;
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
        get => _expanded;
        set
        {
            if (_expanded == value) return;
            _expanded = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs("Expanded"));
        }
    }

    public bool Active
    {
        get => _active;
        set
        {
            if (_active == value) return;
            _active = value;
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

    /// <summary>
    ///     このフォルダーと祖先フォルダーのチェック状態の変更を通知します。
    /// </summary>
    private void NotifyCheckedUpward()
    {
        for (var folder = this; folder != null; folder = folder.Parent)
            folder.PropertyChanged?.Invoke(folder, new PropertyChangedEventArgs(nameof(Checked)));
    }

    /// <summary>
    ///     フォルダーが同期可能で、その種類が指定したマスクに含まれるかどうかを判定します。
    /// </summary>
    /// <param name="mask">比較結果の種類のマスク。</param>
    /// <returns>含まれる場合は true。</returns>
    public bool MatchesFolderMask(DiffKind mask)
    {
        return FolderCanSync && (FolderKind & mask) != 0;
    }

    /// <summary>
    ///     表示対象の子フォルダーの一覧を更新します。
    /// </summary>
    public void UpdateDisplayChildren()
    {
        DisplayChildren = Children.Where(f => f.Visible).ToList();
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs("DisplayChildren"));
    }

    /// <summary>
    ///     アイコンの再取得を通知します。
    /// </summary>
    public void RefreshIcon()
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IconPreview)));
    }

    /// <summary>
    ///     マスクに含まれる種類ごとの件数を合計します。
    /// </summary>
    /// <param name="values">種類ごとの件数。</param>
    /// <param name="mask">比較結果の種類のマスク。</param>
    /// <returns>合計件数。</returns>
    private static int Sum(int[] values, DiffKind mask)
    {
        var result = 0;
        for (var kind = 1; kind <= 8; kind <<= 1)
            if (((int)mask & kind) != 0)
                result += values[kind];
        return result;
    }

    /// <summary>
    ///     マスクに含まれる種類のファイル数を取得します。
    /// </summary>
    /// <param name="mask">比較結果の種類のマスク。</param>
    /// <returns>ファイル数。</returns>
    public int CountFor(DiffKind mask)
    {
        return Sum(_counts, mask);
    }

    /// <summary>
    ///     マスクに含まれる種類の選択済みファイル数を取得します。
    /// </summary>
    /// <param name="mask">比較結果の種類のマスク。</param>
    /// <returns>選択済みのファイル数。</returns>
    public int SelectedFor(DiffKind mask)
    {
        return Sum(_selectedCounts, mask);
    }

    /// <summary>
    ///     フォルダーとその子孫のうち、マスクに一致する対象フォルダーの数と選択済みの数を数えます。
    /// </summary>
    /// <param name="folder">数え始めるフォルダー。</param>
    /// <param name="mask">比較結果の種類のマスク。</param>
    /// <param name="selected">選択済みのフォルダー数。</param>
    /// <param name="total">対象のフォルダー数。</param>
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

    /// <summary>
    ///     ファイルの種類ごとの件数、選択数、同期可能な差分の数を数え直し、変更を通知します。
    /// </summary>
    public void Refresh()
    {
        Array.Clear(_counts, 0, _counts.Length);
        Array.Clear(_selectedCounts, 0, _selectedCounts.Length);
        SelectedCount = 0;
        AllDifferenceCount = 0;
        foreach (var file in Files)
        {
            if (file.CanSync) AllDifferenceCount++;
            if (file.Selected) SelectedCount++;
            if (IncludeFile != null && !IncludeFile(file)) continue;
            _counts[(int)file.Kind]++;
            if (file.Selected) _selectedCounts[(int)file.Kind]++;
        }

        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs("Checked"));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs("CanSelect"));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs("IconPreview"));
    }

    /// <summary>
    ///     フォルダー自体の同期選択を設定します。同期できないフォルダーは選択できません。
    /// </summary>
    /// <param name="value">選択する場合は true。</param>
    /// <param name="notify">祖先フォルダーに変更を通知する場合は true。</param>
    public void SetFolderSelected(bool value, bool notify = true)
    {
        value = value && FolderCanSync;
        if (_folderSelected == value) return;
        _folderSelected = value;
        if (notify)
            NotifyCheckedUpward();
    }

    /// <summary>
    ///     ファイルの選択数を数え直します。
    /// </summary>
    public void RefreshSelectionCounts()
    {
        Array.Clear(_selectedCounts, 0, _selectedCounts.Length);
        SelectedCount = 0;

        foreach (var file in Files)
        {
            if (file.Selected)
                SelectedCount++;

            if (IncludeFile != null && !IncludeFile(file))
                continue;

            if (file.Selected)
                _selectedCounts[(int)file.Kind]++;
        }

        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Checked)));
    }

    /// <summary>
    ///     選択数を差分で更新し、表示中の種類であればチェック状態の変更を通知します。
    /// </summary>
    /// <param name="delta">選択数の増減。</param>
    /// <param name="kind">ファイルの比較結果の種類。</param>
    /// <param name="included">現在のフィルターで表示対象のファイルの場合は true。</param>
    public void ChangeSelectionCount(int delta, DiffKind kind, bool included = true)
    {
        if (delta == 0) return;
        SelectedCount += delta;
        if (!included) return;
        _selectedCounts[(int)kind] += delta;
        if ((Mask & kind) != 0) PropertyChanged?.Invoke(this, new PropertyChangedEventArgs("Checked"));
    }
}

internal sealed class DiffSide : INotifyPropertyChanged
{
    private string _info;

    public string Info
    {
        get => _info;
        set
        {
            _info = value;
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

    /// <summary>
    ///     画像ファイルのサイズを読み取り、プレビュー用のサムネイルを作成します。
    /// </summary>
    /// <returns>読み取ったプレビュー情報。</returns>
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
            result.Dimensions = string.Format(StringOverlay.Get("Differencer_TextPreviewUnavailable"), ErrorMessages.English(ex));
        }

        return result;
    }

    /// <summary>
    ///     読み取ったプレビュー情報を反映し、変更を通知します。
    /// </summary>
    /// <param name="preview">反映するプレビュー情報。</param>
    internal void ApplyPreview(Preview preview)
    {
        Thumbnail = preview.Thumbnail;
        Dimensions = preview.Dimensions;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs("Thumbnail"));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs("Dimensions"));
    }

    /// <summary>
    ///     サムネイルを解放します。
    /// </summary>
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
    private CancellationTokenSource _previewCancellation;
    private DiffSide _source, _target;
    public DiffFile File { get; set; }
    public string SourceRoot { get; set; }

    public string TargetRoot { get; set; }

    // Identical entries can be numerous; format side details only for rows that are actually displayed.
    public DiffSide Source
    {
        get => _source ?? (_source = new DiffSide
        { Info = File.SourceInfo, Root = SourceRoot, Relative = File.RelativePath, Exists = File.Source != null });
        set => _source = value;
    }

    public DiffSide Target
    {
        get => _target ?? (_target = new DiffSide
        { Info = File.TargetInfo, Root = TargetRoot, Relative = File.RelativePath, Exists = File.Target != null });
        set => _target = value;
    }

    public string Extension => Path.GetExtension(File.RelativePath);
    public bool IsDirectInSelectedFolder { get; private set; } = true;

    public string PathFileName => File == null ? string.Empty : Path.GetFileName(File.RelativePath ?? string.Empty);

    public ImageSource StatusIcon => DifferencerStatusIcons.GetFileIcon(File.Kind);
    public ImageSource Icon { get; private set; }
    public bool IsPreviewReady { get; private set; }

    public event PropertyChangedEventHandler PropertyChanged;

    /// <summary>
    ///     ファイルが選択中のフォルダーの直下にあるかどうかを設定し、変更を通知します。
    /// </summary>
    /// <param name="value">直下にある場合は true。</param>
    public void SetDirectInSelectedFolder(bool value)
    {
        if (IsDirectInSelectedFolder == value) return;
        IsDirectInSelectedFolder = value;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsDirectInSelectedFolder)));
    }

    /// <summary>
    ///     読み込み中のプレビューをキャンセルします。
    /// </summary>
    public void CancelPreview()
    {
        _previewCancellation?.Cancel();
    }

    /// <summary>
    ///     プレビューの読み込みをキャンセルし、両側のサムネイルとアイコンを解放します。
    /// </summary>
    public void ReleasePreview()
    {
        CancelPreview();
        if (!Source.HasImage && !Target.HasImage) return;
        Source.ReleaseThumbnail();
        Target.ReleaseThumbnail();
        Icon = null;
        IsPreviewReady = false;
    }

    /// <summary>
    ///     少し待ってから、同時実行数を制限してプレビューとアイコンをバックグラウンドで読み込みます。スクロールで表示外になった行は、ディスク アクセス前にキャンセルされます。
    /// </summary>
    /// <param name="workers">同時実行数を制限するセマフォ。</param>
    /// <param name="scope">読み込み全体のキャンセル トークン。</param>
    public async Task LoadPreviewAsync(SemaphoreSlim workers, CancellationToken scope)
    {
        if (IsPreviewReady || (_previewCancellation != null && !_previewCancellation.IsCancellationRequested)) return;
        var request = CancellationTokenSource.CreateLinkedTokenSource(scope);
        _previewCancellation = request;
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
            if (ReferenceEquals(_previewCancellation, request)) _previewCancellation = null;
            request.Dispose();
        }
    }

    /// <summary>
    ///     プレビューとファイル情報を破棄して、最新のファイルから読み直すよう変更を通知します。
    /// </summary>
    internal void RefreshFromFile()
    {
        ReleasePreview();
        _source = null;
        _target = null;
        Icon = null;
        IsPreviewReady = false;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs("Source"));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs("Target"));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs("StatusIcon"));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs("Icon"));
    }
}