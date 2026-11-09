using DesktopIniManager.Properties;
using DesktopIniManager.Services;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Media.Imaging;

namespace DesktopIniManager.ViewModels;

internal sealed partial class DiffViewModel : ObservableObject
{
    private static readonly string[] ExternalDiffPresets =
    {
        @"code --diff ""{source}"" ""{target}""",
        @"""C:\Program Files\MIFES11\MIW.exe"" /diff ""{source}"" ""{target}""",
        @"""C:\Program Files\WinMerge\WinMergeU.exe"" ""{source}"" ""{target}""",
        @"devenv /Diff ""{source}"" ""{target}""",
        @"""C:\Program Files\Beyond Compare 5\BCompare.exe"" ""{source}"" ""{target}"""
    };

    private readonly IUserDialogService _dialogs;

    private readonly HashSet<int> _expandedEqualBlocks = new();
    private readonly bool _standalone;
    private List<DiffLine> _allLines;
    private bool _checkingExternalEdit;
    private string _externalDiff = string.Empty;
    private bool _externalDiffPending;
    private DiffStamp _externalSourceStamp, _externalTargetStamp;
    private DiffFile _file;
    private int _frameVersion;
    private DiffFile _listFile;
    private bool _loadingContent, _navigating, _closed;
    private TextDocument[] _preparedText;

    private bool _showNewlines =
        SettingsService.LoadDiffFlag("show-newlines", SettingsService.LoadDiffFlag("newlines", false));

    private bool _showSpaces =
        SettingsService.LoadDiffFlag("show-spaces", SettingsService.LoadDiffFlag("spaces", false));

    private bool _syntaxHighlight = SettingsService.LoadDiffFlag("syntax-highlight", true);

    private GifFrames _sourceGif, _targetGif;
    private string _sourcePath, _targetPath;
    private string _status = string.Empty;

    /// <summary>
    ///     DIFF ビューの ViewModel を作成し、表示設定の読み込みとコマンドの初期化を行います。
    /// </summary>
    /// <param name="snapshot">比較スナップショット。</param>
    /// <param name="file">表示するファイル。</param>
    /// <param name="dialogs">メッセージ表示に使うダイアログ サービス。</param>
    /// <param name="sourcePath">単独で比較する場合の比較元のパス。</param>
    /// <param name="targetPath">単独で比較する場合の比較先のパス。</param>
    internal DiffViewModel(DiffSnapshot snapshot, DiffFile file, IUserDialogService dialogs, string sourcePath = null,
        string targetPath = null)
    {
        _sourcePath = sourcePath;
        _targetPath = targetPath;
        _standalone = sourcePath != null || targetPath != null;
        _listFile = file;
        _dialogs = dialogs;
        DifferencesOnly = SettingsService.LoadDiffDifferencesOnly();
        Snapshot = snapshot;
        File = file;
        PreviousHunkCommand = new RelayCommand(() => NavigateHunk(-1));
        NextHunkCommand = new RelayCommand(() => NavigateHunk(1));
        PreviousFileCommand = new AsyncRelayCommand(() => NavigateFileAsync(-1), ReportError, () => !_standalone);
        NextFileCommand = new AsyncRelayCommand(() => NavigateFileAsync(1), ReportError, () => !_standalone);
        OpenSourceCommand = new RelayCommand(() => OpenAssociatedApplication(true));
        OpenTargetCommand = new RelayCommand(() => OpenAssociatedApplication(false));
        OpenExternalDiffCommand = new RelayCommand(OpenExternalDiff);
        CloseCommand = new RelayCommand(() => CloseRequested?.Invoke());
        SeedExternalDiffPresets();
        var savedExternalDiff = SettingsService.LoadExternalDiff();
        ExternalDiff = string.IsNullOrWhiteSpace(savedExternalDiff) ? ExternalDiffPresets[0] : savedExternalDiff;
    }

    internal DiffSnapshot Snapshot { get; }

    public DiffFile File
    {
        get => _file;
        set
        {
            if (SetProperty(ref _file, value)) RefreshFileDetails();
        }
    }

    internal bool IsDetachedComparison => _sourcePath != null || _targetPath != null;
    internal bool IsEmpty => string.IsNullOrEmpty(File.RelativePath);

    public string Title => IsEmpty
        ? StringOverlay.Get("Diff_Heading")
        : string.Format(StringOverlay.Get("Diff_TitleFile"),
            IsPatch
                ? Path.GetFileName(GetPath(true)) + " \u2194 " + Path.GetFileName(GetPath(false) ?? string.Empty)
                : !IsDetachedComparison
                    ? File.RelativePath
                    : Path.GetFileName(_sourcePath) + " \u2194 " + Path.GetFileName(_targetPath));

    /// <summary>
    ///     File whose stamps feed the side headers and status icons. In Patch View the
    ///     target is the actual file resolved from the patch's relative path, not the
    ///     comparison entry the patch was opened with (which is left unchanged).
    /// </summary>
    internal DiffFile HeaderFile => IsPatch && _patchHeaderFile != null ? _patchHeaderFile : File;
    private DiffFile _patchHeaderFile;

    public string SourceHeader => HeaderMeta(HeaderFile.SourceInfo);
    public string TargetHeader => HeaderMeta(HeaderFile.TargetInfo);
    public bool IsImage => DiffMedia.IsImage(File.RelativePath);
    public bool IsVideo => DiffMedia.IsVideo(File.RelativePath);
    public bool IsAudio => DiffMedia.IsAudio(File.RelativePath);

    public string ExternalDiff
    {
        get => _externalDiff;
        set => SetProperty(ref _externalDiff, value);
    }

    public BitmapSource SourceImage { get; private set; }
    public BitmapSource TargetImage { get; private set; }
    internal int ImageFrameCount => Math.Max(_sourceGif?.Count ?? 1, _targetGif?.Count ?? 1);
    internal int ImageFrameIndex { get; private set; }

    internal string ImageFrameDetails =>
        string.Format(StringOverlay.Get("Diff_FrameStatus"), ImageFrameIndex + 1, ImageFrameCount, FrameDetails(_sourceGif), FrameDetails(_targetGif));

    internal string SourceImageFormat { get; private set; }
    internal string TargetImageFormat { get; private set; }
    public List<DiffLine> Lines { get; private set; }
    internal IReadOnlyList<DiffLine> AllLines => _allLines;
    internal bool DifferencesOnly { get; private set; }
    internal bool CompareNewlines { get; set; } = SettingsService.LoadDiffFlag("newlines", false);
    internal bool CompareCase { get; set; } = SettingsService.LoadDiffFlag("case", false);
    internal bool CompareSpaces { get; set; } = SettingsService.LoadDiffFlag("spaces", false);

    public bool ShowSpaces
    {
        get => _showSpaces;
        set => SetProperty(ref _showSpaces, value);
    }

    public bool ShowNewlines
    {
        get => _showNewlines;
        set => SetProperty(ref _showNewlines, value);
    }

    /// <summary>
    ///     テキストの比較で、拡張子から言語を自動判定してシンタックス ハイライトを付けるかどうか。既定は true。
    /// </summary>
    public bool SyntaxHighlight
    {
        get => _syntaxHighlight;
        set => SetProperty(ref _syntaxHighlight, value);
    }

    internal bool CanChangeComparison => !_loadingContent && !_navigating && !_checkingExternalEdit && !_closed;
    internal TextDocument SourceText { get; private set; }
    internal TextDocument TargetText { get; private set; }
    public List<int> Hunks { get; } = new();
    internal int CurrentHunk { get; set; } = -1;

    public string Status
    {
        get => _status;
        set => SetProperty(ref _status, value);
    }

    public RelayCommand PreviousHunkCommand { get; }
    public RelayCommand NextHunkCommand { get; }
    public AsyncRelayCommand PreviousFileCommand { get; }
    public AsyncRelayCommand NextFileCommand { get; }
    public RelayCommand OpenSourceCommand { get; }
    public RelayCommand OpenTargetCommand { get; }
    public RelayCommand OpenExternalDiffCommand { get; }
    public RelayCommand CloseCommand { get; }
    internal Func<DiffFile, Task> RefreshFileRequested { get; set; }
    internal Func<Task> ReloadRequested { get; set; }
    internal Func<IReadOnlyList<DiffFile>> VisibleFilesRequested { get; set; }

    /// <summary>
    ///     GIF の現在フレームの表示時間とフレーム数を表す文字列を作成します。
    /// </summary>
    /// <param name="gif">GIF のフレーム情報。静止画の場合は null。</param>
    /// <returns>表示用の文字列。</returns>
    private string FrameDetails(GifFrames gif)
    {
        return gif == null
            ? StringOverlay.Get("Diff_FrameStill")
            : ImageFrameIndex >= gif.Count
                ? string.Format(StringOverlay.Get("Diff_FrameMissing"), gif.Count)
                : string.Format(StringOverlay.Get("Diff_FrameDelay"), gif.DelayMilliseconds(ImageFrameIndex), gif.Count);
    }

    /// <summary>
    ///     両側の GIF の指定フレームをバックグラウンドで合成して表示します。途中で別のフレームが選ばれた場合は結果を破棄します。
    /// </summary>
    /// <param name="index">フレーム番号。</param>
    /// <returns>表示を更新した場合は true。</returns>
    internal async Task<bool> SelectImageFrameAsync(int index)
    {
        var version = ++_frameVersion;
        var a = _sourceGif;
        var b = _targetGif;
        var images = await Task.Run(() => (a?.Compose(index), b?.Compose(index)));
        if (_closed || version != _frameVersion) return false;
        if (a != null) SourceImage = images.Item1;
        if (b != null) TargetImage = images.Item2;
        ImageFrameIndex = index;
        return true;
    }

    /// <summary>
    ///     差分行だけの表示と全行の表示を切り替えます。
    /// </summary>
    /// <returns>切り替えた場合は true。</returns>
    internal bool ToggleDifferencesOnly()
    {
        if (_loadingContent || _navigating || _closed || IsImage || _allLines == null) return false;
        DifferencesOnly = !DifferencesOnly;
        _expandedEqualBlocks.Clear();
        UpdateDisplayedLines();
        return true;
    }

    /// <summary>
    ///     折りたたまれた変更なしの行 (またはデザイナー部分) を展開します。
    /// </summary>
    /// <param name="index">表示上の行のインデックス。</param>
    /// <returns>展開した場合は true。</returns>
    internal bool ExpandFilteredLines(int index)
    {
        if (!CanChangeComparison || Lines == null || index < 0 || index >= Lines.Count ||
            Lines[index].FilteredLineCount == 0) return false;
        if (Lines[index].IsDesignerHeader)
        {
            _designerHeaderExpanded = true;
            UpdateDisplayedLines();
            return true;
        }
        if (!DifferencesOnly) return false;
        var originalStart = 0;
        for (var i = 0; i < index; i++) originalStart += Math.Max(1, Lines[i].FilteredLineCount);
        _expandedEqualBlocks.Add(originalStart);
        UpdateDisplayedLines();
        return true;
    }

    /// <summary>
    ///     展開した変更なしの行のブロックを折りたたみ直します。
    /// </summary>
    /// <param name="index">表示上の行のインデックス。</param>
    /// <returns>折りたたんだ行の表示上のインデックス。折りたためない場合は -1。</returns>
    internal int CollapseExpandedLines(int index)
    {
        if (!CanChangeComparison || !DifferencesOnly || Lines == null || index < 0 || index >= Lines.Count ||
            Lines[index].Kind != DiffLineKind.Unchanged || Lines[index].FilteredLineCount > 0) return -1;
        var originalIndex = 0;
        for (var i = 0; i < index; i++) originalIndex += Math.Max(1, Lines[i].FilteredLineCount);
        var start = originalIndex;
        // Walk back exactly as UpdateDisplayedLines groups rows: never across the end of
        // the designer header, and never into it while it is folded.
        var header = DesignerHeaderRowCount;
        var first = start >= header || !_designerHeaderExpanded ? header : 0;
        while (start > first && _allLines[start - 1].Kind == DiffLineKind.Unchanged) start--;
        if (!_expandedEqualBlocks.Remove(start)) return -1;
        var collapsedIndex = index - (originalIndex - start);
        UpdateDisplayedLines();
        return collapsedIndex;
    }

    /// <summary>
    ///     差分のみ表示・展開状態・デザイナー部分の折りたたみに従って表示する行を組み立て、HUNK の一覧を更新します。
    /// </summary>
    private void UpdateDisplayedLines()
    {
        var designerCount = DesignerHeaderRowCount;
        var visible = new List<DiffLine>();
        if (designerCount > 0)
        {
            // Expanded header: all lines in the all-lines view; in the differences-only
            // view it folds like any other part (second stage: expand its folds).
            if (_designerHeaderExpanded)
            {
                if (!DifferencesOnly) visible.AddRange(_allLines.Take(designerCount));
            }
            else
            {
                var changed = _allLines.Take(designerCount).Any(row => row.Kind != DiffLineKind.Unchanged);
                var label = string.Format(StringOverlay.Get(changed ? "Diff_DesignerHeaderChanged" : "Diff_DesignerHeader"), designerCount);
                visible.Add(new DiffLine { Left = label, Right = label, FilteredLineCount = designerCount,
                    IsDesignerHeader = true, FilteredAnchor = _allLines[0] });
            }
        }
        if (!DifferencesOnly)
        {
            Lines = designerCount == 0 ? _allLines : visible.Concat(_allLines.Skip(designerCount)).ToList();
        }
        else
        {
            var filteredFormat = StringOverlay.Get("Diff_FilteredLines");
            for (var i = _designerHeaderExpanded ? 0 : designerCount; i < _allLines.Count; i++)
            {
                // Patch rows fold like text rows: unchanged context inside a hunk is not a difference.
                if (_allLines[i].Kind != DiffLineKind.Unchanged)
                {
                    visible.Add(_allLines[i]);
                    continue;
                }

                var start = i;
                // A fold never spans the end of the designer header.
                while (i + 1 < _allLines.Count && i + 1 != designerCount &&
                       _allLines[i + 1].Kind == DiffLineKind.Unchanged) i++;
                var count = i - start + 1;
                if (_expandedEqualBlocks.Contains(start))
                {
                    visible.AddRange(_allLines.GetRange(start, count));
                    continue;
                }

                var label = string.Format(filteredFormat, count);
                visible.Add(new DiffLine
                {
                    Kind = DiffLineKind.Unchanged,
                    Left = label,
                    Right = label,
                    FilteredLineCount = count,
                    FilteredAnchor = _allLines[start]
                });
            }

            Lines = visible;
        }

        Hunks.Clear();
        CurrentHunk = -1;
        // A hunk is a run of changed rows; unchanged rows are never inside one.
        // In a patch, each run has its own id (PatchFileState.Runs), so adjacent
        // runs of different patch hunks stay separate.
        for (var i = 0; i < Lines.Count; i++)
            if (Lines[i].Kind != DiffLineKind.Unchanged &&
                (i == 0 || Lines[i - 1].Kind == DiffLineKind.Unchanged || IsPatch && Lines[i - 1].EditHunkId != Lines[i].EditHunkId))
                Hunks.Add(i);
        OnPropertyChanged(nameof(Lines));
        OnPropertyChanged(nameof(Hunks));
    }

    public event Action CloseRequested;

    public event Action ExternalDiffHistoryRequested;

    public event Action<int> JumpRequested;

    /// <summary>
    ///     表示設定と外部 DIFF のコマンドを保存し、ビューを閉じた状態にします。
    /// </summary>
    internal void Close()
    {
        _closed = true;
        SettingsService.SaveDiffDifferencesOnly(DifferencesOnly);
        SettingsService.SaveDiffFlag("newlines", CompareNewlines);
        SettingsService.SaveDiffFlag("case", CompareCase);
        SettingsService.SaveDiffFlag("spaces", CompareSpaces);
        SettingsService.SaveDiffFlag("show-spaces", ShowSpaces);
        SettingsService.SaveDiffFlag("show-newlines", ShowNewlines);
        SettingsService.SaveDiffFlag("syntax-highlight", SyntaxHighlight);
        _externalDiffPending = false;
        if (string.IsNullOrWhiteSpace(ExternalDiff)) return;
        ExternalDiffHistoryRequested?.Invoke();
        SettingsService.SaveExternalDiff(ExternalDiff.Trim());
    }

    /// <summary>
    ///     ファイルに関係する表示プロパティの変更を通知します。
    /// </summary>
    private void RefreshFileDetails()
    {
        OnPropertyChanged(nameof(File));
        OnPropertyChanged(nameof(Title));
        OnPropertyChanged(nameof(SourceHeader));
        OnPropertyChanged(nameof(TargetHeader));
        OnPropertyChanged(nameof(IsImage));
        OnPropertyChanged(nameof(IsVideo));
        OnPropertyChanged(nameof(IsAudio));
    }

    /// <summary>
    ///     現在のファイルの種類 (テキスト・画像・動画・音声・パッチ) に応じて内容を読み込みます。
    /// </summary>
    /// <returns>読み込んだ場合は true。閉じている場合や読み込み中の場合は false。</returns>
    internal async Task<bool> LoadContentAsync()
    {
        if (_closed || _loadingContent) return false;
        _loadingContent = true;
        if (!PatchDocument.IsPatch(GetPath(true))) { Patch = null; PatchFiles = []; _loadedPatchPath = null; }
        ResetContent();
        RefreshFileDetails();
        SourceImage = TargetImage = null;
        _sourceGif = _targetGif = null;
        ImageFrameIndex = 0;
        _frameVersion++;
        SourceImageFormat = TargetImageFormat = null;
        // Status = StringOverlay.Get("Diff_Loading");  // プログレスパネルで表示するため重複を避ける
        try
        {
            if (DiffMedia.IsBinary(File.RelativePath)) throw new InvalidDataException(DiffMedia.BinaryMessage);
            if (PatchDocument.IsPatch(GetPath(true)))
            {
                await LoadPatchAsync(GetPath(true));
            }
            else if (IsVideo || IsAudio)
            {
                // Videos are played via MediaElement in the view; no frame extraction here.
                Status = string.Empty;
            }
            else if (IsImage)
            {
                string leftPath = GetPath(true), rightPath = GetPath(false);
                var images = await Task.Run(() => new[] { LoadImage(leftPath), LoadImage(rightPath) });
                SourceImage = images[0].Image;
                TargetImage = images[1].Image;
                SourceImageFormat = images[0].Format;
                TargetImageFormat = images[1].Format;
                _sourceGif = images[0].Gif;
                _targetGif = images[1].Gif;
                Status = string.Empty;
            }
            else
            {
                await LoadTextAsync();
                Status = IsEmpty ? StringOverlay.Get("Diff_DropHint")
                    : Hunks.Count == 0 ? StringOverlay.Get("Diff_IdenticalContent")
                    : string.Format(StringOverlay.Get("Diff_HunkStatus"), Hunks.Count);
            }

            return true;
        }
        catch (InvalidDataException)
        {
            ReportUnsupportedContent();
        }
        catch (DecoderFallbackException)
        {
            ReportUnsupportedContent();
        }
        catch (Exception ex)
        {
            ReportError(ex);
        }
        finally
        {
            _loadingContent = false;
        }

        return false;
    }

    /// <summary>
    ///     表示できないファイルであることを通知し、ビューを閉じるよう要求します。
    /// </summary>
    private void ReportUnsupportedContent()
    {
        _dialogs.Show(DiffMedia.BinaryMessage, StringOverlay.Get("Diff_ViewCaption"), MessageBoxButton.OK, MessageBoxImage.Information);
        CloseRequested?.Invoke();
    }

    /// <summary>
    ///     エラーの内容をステータスに表示します。
    /// </summary>
    /// <param name="ex">発生した例外。</param>
    internal void ReportError(Exception ex)
    {
        Status = string.Format(StringOverlay.Get("Diff_Unable"), ErrorMessages.English(ex));
    }

    /// <summary>
    ///     両側の入力をクリアします。
    /// </summary>
    internal void ClearInputs()
    {
        if (!CanChangeComparison) return;
        ReplaceInputs("", "");
    }

    /// <summary>
    ///     比較する 2 つのファイルを手動で指定したものに置き換えます。元の比較スナップショットは変更しません。
    /// </summary>
    /// <param name="source">比較元のパス。</param>
    /// <param name="target">比較先のパス。</param>
    private void ReplaceInputs(string source, string target)
    {
        Patch = null;
        PatchFiles = [];
        _loadedPatchPath = null;
        // Manual pairs must not modify or refresh the owning comparison snapshot.
        _sourcePath = source ?? "";
        _targetPath = target ?? "";
        _externalDiffPending = false;
        _preparedText = null;
        _frameVersion++;
        var sourceStamp = DiffStamp.Read(_sourcePath);
        var targetStamp = DiffStamp.Read(_targetPath);
        File = new DiffFile
        {
            RelativePath = Path.GetFileName(sourceStamp != null ? _sourcePath : _targetPath),
            Source = sourceStamp,
            Target = targetStamp
        };
    }

    /// <summary>
    ///     ドロップされたファイルを比較対象に設定します。重複や種類の異なるファイルの組み合わせは拒否します。
    /// </summary>
    /// <param name="paths">ドロップされたファイル (1 つまたは 2 つ)。</param>
    /// <param name="sourceSide">比較元側にドロップされた場合は true。</param>
    /// <returns>比較対象を変更した場合は true。</returns>
    internal async Task<bool> DropFilesAsync(string[] paths, bool sourceSide)
    {
        if (!CanChangeComparison || paths == null || paths.Length == 0 || paths.Length > 2) return false;
        var full = paths.Select(Path.GetFullPath).ToArray();
        string currentSource = GetPath(true), currentTarget = GetPath(false);
        if (full.Distinct(StringComparer.OrdinalIgnoreCase).Count() != full.Length || full.Any(path =>
                string.Equals(path, currentSource, StringComparison.OrdinalIgnoreCase) ||
                string.Equals(path, currentTarget, StringComparison.OrdinalIgnoreCase)))
            throw new InvalidOperationException(StringOverlay.Get("Diff_DropDuplicate"));
        var imageMode = DiffMedia.IsImage(full[0]);
        var videoMode = DiffMedia.IsVideo(full[0]);
        var audioMode = DiffMedia.IsAudio(full[0]);
        if (full.Any(path => DiffMedia.IsBinary(path) || DiffMedia.IsImage(path) != imageMode ||
                             DiffMedia.IsVideo(path) != videoMode || DiffMedia.IsAudio(path) != audioMode))
            throw new InvalidOperationException(StringOverlay.Get("Diff_DropMode"));
        // A mode change starts a new pair; never feed the previous text/image to the video player.
        if (imageMode != IsImage || videoMode != IsVideo || audioMode != IsAudio) currentSource = currentTarget = "";
        _navigating = true;
        try
        {
            // Validate before replacing either side, so a rejected drop retains the pair.
            await Task.Run(() =>
            {
                foreach (var path in full)
                {
                    if (!System.IO.File.Exists(path))
                        throw new FileNotFoundException(StringOverlay.Get("Diff_DropFilesOnly"), path);
                    if (imageMode) LoadImage(path);
                    else if (PatchDocument.IsPatch(path)) PatchDocument.Read(path);
                    else if (!videoMode && !audioMode) ReadDocument(path);
                }
            });
            if (_closed) return false;
            if (full.Length == 2) ReplaceInputs(full[0], full[1]);
            else ReplaceInputs(sourceSide ? full[0] : currentSource, sourceSide ? currentTarget : full[0]);
            return true;
        }
        finally
        {
            _navigating = false;
        }
    }

    /// <summary>
    ///     比較元または比較先のファイル パスを取得します。パッチ表示の比較先は適用先のファイルです。
    /// </summary>
    /// <param name="source">比較元の場合は true。</param>
    /// <returns>ファイル パス。</returns>
    internal string GetPath(bool source)
    {
        if (!source && IsPatch) return CurrentPatch?.TargetPath;
        return (source ? _sourcePath : _targetPath)
               ?? DeveloperDifferencerService.SafePath(source ? Snapshot.SourceRoot : Snapshot.TargetRoot,
                   File.RelativePath);
    }

    /// <summary>
    ///     HUNK の一覧と現在位置をリセットします。
    /// </summary>
    internal void ResetContent()
    {
        Hunks.Clear();
        CurrentHunk = -1;
    }

    /// <summary>
    ///     両側のテキスト ファイルを読み込んで比較します。
    /// </summary>
    internal async Task LoadTextAsync()
    {
        _designerHeaderExpanded = false;
        _expandedEqualBlocks.Clear();
        string left = GetPath(true), right = GetPath(false);
        var prepared = _preparedText;
        _preparedText = null;
        var documents = prepared ?? await Task.Run(() => new[] { ReadDocument(left), ReadDocument(right) });
        SourceText = documents[0];
        TargetText = documents[1];
        _originalSource = SourceText;
        _originalTarget = TargetText;
        await RecompareTextAsync();
    }

    /// <summary>
    ///     現在の比較オプションでテキストを比較し直し、編集セッションと表示を更新します。
    /// </summary>
    internal async Task RecompareTextAsync()
    {
        if (IsPatch) { RefreshPatchRows(); return; }
        _expandedEqualBlocks.Clear();
        _allLines = await Task.Run(() => DiffTextService.Compare(SourceText.Lines, TargetText.Lines,
            !CompareCase, !CompareSpaces, CompareNewlines ? SourceText.Endings : null,
            CompareNewlines ? TargetText.Endings : null,
            CompareCase, CompareNewlines));
        _edits = new DiffEditSession(SourceText, TargetText, _allLines);
        UpdateDisplayedLines();
        Status = (IsEmpty ? StringOverlay.Get("Diff_DropHint")
                     : Hunks.Count == 0 ? StringOverlay.Get("Diff_Identical")
                     : string.Format(StringOverlay.Get("Diff_HunkStatus"), Hunks.Count)) +
                 DecodingWarning(SourceText, StringOverlay.Get("Diff_SideSource")) + DecodingWarning(TargetText, StringOverlay.Get("Diff_SideTarget"));
    }

    /// <summary>
    ///     Shift_JIS としてデコードできないバイトがある場合の警告文を作成します。
    /// </summary>
    /// <param name="document">対象の文書。</param>
    /// <param name="side">表示する側の名前。</param>
    /// <returns>警告文。問題がない場合は空文字列。</returns>
    private static string DecodingWarning(TextDocument document, string side) =>
        document?.DecodingErrorLine > 0
            ? " | " + string.Format(StringOverlay.Get("Diff_DecodingWarning"), side, document.DecodingErrorLine)
            : string.Empty;

    /// <summary>
    ///     前後の HUNK に移動します。端に達した場合は反対側の端に戻ります。
    /// </summary>
    /// <param name="direction">次へは 1、前へは -1。</param>
    private void NavigateHunk(int direction)
    {
        if (Hunks.Count == 0) return;
        CurrentHunk = CurrentHunk < 0
            ? direction > 0 ? 0 : Hunks.Count - 1
            : (CurrentHunk + direction + Hunks.Count) % Hunks.Count;
        JumpRequested?.Invoke(Hunks[CurrentHunk]);
    }

    /// <summary>
    ///     比較一覧の前後の表示可能なファイルに移動します。未保存の変更がある場合は確認します。
    /// </summary>
    /// <param name="direction">次へは 1、前へは -1。</param>
    private async Task NavigateFileAsync(int direction)
    {
        if (_standalone || _closed || _navigating || _loadingContent || _checkingExternalEdit) return;
        if (ConfirmChangesRequested != null && !await ConfirmChangesRequested()) return;
        _navigating = true;
        try
        {
            var requested = VisibleFilesRequested?.Invoke();
            var candidates = (requested == null
                ? Snapshot.Files.Where(IsVisibleComparableFile)
                : requested.Where(IsVisibleComparableFile)).ToList();
            if (candidates.Count == 0) return;
            var index = candidates.FindIndex(candidate =>
                ReferenceEquals(candidate, _listFile) || string.Equals(candidate.RelativePath, _listFile.RelativePath,
                    StringComparison.OrdinalIgnoreCase));
            if (!IsDetachedComparison && candidates.Count == 1 && index == 0) return;
            if (index < 0) index = direction > 0 ? -1 : 0;
            for (var visited = 0; visited < candidates.Count; visited++)
            {
                index = (index + direction + candidates.Count) % candidates.Count;
                var candidate = candidates[index];
                if (!IsDetachedComparison && string.Equals(candidate.RelativePath, File.RelativePath,
                        StringComparison.OrdinalIgnoreCase)) return;
                TextDocument[] text = null;
                if (!DiffMedia.IsImage(candidate.RelativePath) && !DiffMedia.IsVideo(candidate.RelativePath) &&
                    !DiffMedia.IsAudio(candidate.RelativePath))
                {
                    var left = DeveloperDifferencerService.SafePath(Snapshot.SourceRoot, candidate.RelativePath);
                    var right = DeveloperDifferencerService.SafePath(Snapshot.TargetRoot, candidate.RelativePath);
                    try
                    {
                        text = await Task.Run(() => new[] { ReadDocument(left), ReadDocument(right) });
                    }
                    catch (InvalidDataException)
                    {
                        continue;
                    }
                    catch (DecoderFallbackException)
                    {
                        continue;
                    }
                    catch (IOException ex)
                    {
                        ReportError(ex);
                        return;
                    }
                    catch (UnauthorizedAccessException ex)
                    {
                        ReportError(ex);
                        return;
                    }
                }

                if (_closed) return;
                _externalDiffPending = false;
                _preparedText = text;
                _sourcePath = _targetPath = null;
                _listFile = candidate;
                File = candidate;
                if (ReloadRequested != null) await ReloadRequested();
                return;
            }
        }
        finally
        {
            _navigating = false;
        }
    }

    /// <summary>
    ///     DIFF ビューで表示できるファイルかどうかを判定します。
    /// </summary>
    /// <param name="candidate">判定するファイル。</param>
    /// <returns>表示できる場合は true。</returns>
    private static bool IsVisibleComparableFile(DiffFile candidate)
    {
        return candidate != null && !DiffMedia.IsBinary(candidate.RelativePath);
    }

    /// <summary>
    ///     テキスト ファイルを読み込み、行の配列を取得します。
    /// </summary>
    /// <param name="path">ファイルのパス。</param>
    /// <returns>行の配列。</returns>
    internal static string[] ReadText(string path)
    {
        return ReadDocument(path).Lines;
    }

    /// <summary>
    ///     テキスト ファイルを読み込み、エンコーディング・BOM・改行コードを判定して文書にします。8 MB を超えるファイルには対応しません。
    /// </summary>
    /// <param name="path">ファイルのパス。</param>
    /// <returns>読み込んだ文書。ファイルがない場合は空の文書。</returns>
    internal static TextDocument ReadDocument(string path)
    {
        var stamp = DiffStamp.Read(path);

        if (stamp == null)
            return new TextDocument();

        if (stamp.Size > 8 * 1024 * 1024)
            throw new IOException(StringOverlay.Get("Err_FileTooLarge")).Localized();

        byte[] bytes;

        using (var stream = new FileStream(
                   path,
                   FileMode.Open,
                   FileAccess.Read,
                   FileShare.ReadWrite | FileShare.Delete,
                   65536,
                   FileOptions.SequentialScan))
        {
            // Check the opened file too: it may have grown since the metadata read.
            if (stream.Length > 8 * 1024 * 1024)
                throw new IOException(StringOverlay.Get("Err_FileTooLarge")).Localized();
            bytes = new byte[stream.Length];
            stream.ReadExactly(bytes);
        }

        Encoding encoding = null;
        var preamble = 0;
        foreach (var candidate in new Encoding[]
                 {
                     new UTF32Encoding(false, true, true), new UTF32Encoding(true, true, true),
                     new UTF8Encoding(true, true), new UnicodeEncoding(false, true, true),
                     new UnicodeEncoding(true, true, true)
                 })
        {
            var bom = candidate.GetPreamble();
            if (bytes.AsSpan().StartsWith(bom))
            {
                encoding = candidate;
                preamble = bom.Length;
                break;
            }
        }

        if (encoding == null)
        {
            // ISO-2022-JP is ASCII-compatible; recognize escape designators before UTF-8.
            var jis = false;
            for (var i = 0; i + 2 < bytes.Length; i++)
                if (bytes[i] == 0x1b && ((bytes[i + 1] == '$' && (bytes[i + 2] == '@' || bytes[i + 2] == 'B')) ||
                                         (bytes[i + 1] == '(' && (bytes[i + 2] == 'B' || bytes[i + 2] == 'J' ||
                                                                  bytes[i + 2] == 'I'))))
                    jis = true;
            encoding = jis
                ? CodePagesEncodingProvider.Instance.GetEncoding(50220, EncoderFallback.ExceptionFallback,
                      DecoderFallback.ExceptionFallback) ??
                  throw new NotSupportedException("ISO-2022-JP encoding is unavailable.")
                : DetectUnicodeEncoding(bytes) ?? new UTF8Encoding(false, true);
        }

        // 拡張子で判定できないバイナリ (.so など) を、先頭 8 KB の NUL バイトで判定する。
        // UTF-16 / UTF-32 (BOM 付き、または BOM なしで判定されたもの) は NUL を含むので対象外。
        if (encoding.CodePage is not (1200 or 1201 or 12000 or 12001) &&
            bytes.AsSpan(0, Math.Min(bytes.Length, 8192)).Contains((byte)0))
            throw new InvalidDataException(DiffMedia.BinaryMessage);

        string text;
        var decodingErrorLine = 0;
        try
        {
            text = encoding.GetString(bytes, preamble, bytes.Length - preamble);
        }
        catch (DecoderFallbackException) when (preamble == 0 && encoding.CodePage == 65001)
        {
            encoding = CodePagesEncodingProvider.Instance.GetEncoding(932, EncoderFallback.ExceptionFallback,
                           DecoderFallback.ExceptionFallback) ??
                       throw new NotSupportedException("Shift-JIS encoding is unavailable.");
            try { text = encoding.GetString(bytes); }
            catch (DecoderFallbackException ex)
            {
                // Mostly Shift-JIS with a few invalid bytes (Beyond Compare reports an encoding
                // error): show those bytes as U+FFFD instead of refusing the whole file. The
                // encoding stays strict, so saving such text is refused rather than corrupting it.
                text = CodePagesEncodingProvider.Instance.GetEncoding(932, EncoderFallback.ExceptionFallback,
                    new DecoderReplacementFallback("\uFFFD"))!.GetString(bytes);
                var errorIndex = Math.Clamp(ex.Index, 0, bytes.Length);
                decodingErrorLine = 1 + bytes.AsSpan(0, errorIndex).Count((byte)'\n');
            }
        }

        var document = new TextDocument
        {
            DecodingErrorLine = decodingErrorLine,
            Encoding = encoding,
            OriginalBytes = bytes,
            HasBom = preamble > 0,
            EncodingName = encoding.CodePage switch
            {
                65001 => "UTF-8",
                932 => "S-JIS",
                50220 => "ISO-2022-JP",
                1200 => "UTF-16 LE",
                1201 => "UTF-16 BE",
                12000 => "UTF-32 LE",
                12001 => "UTF-32 BE",
                _ => encoding.WebName
            }
        };
        if (text.Length == 0) return document;
        var endings = new List<string>();
        var lines = new List<string>(
            Math.Min(4096, Math.Max(1, text.Length / 32)));
        var newlineFlags = 0;
        var start = 0;

        for (var i = 0; i < text.Length; i++)
        {
            var c = text[i];

            if (c == '\r' || c == '\n')
            {
                lines.Add(text[start..i]);
                var crlf = c == '\r' && i + 1 < text.Length && text[i + 1] == '\n';

                if (crlf)
                {
                    endings.Add("\r\n");
                    newlineFlags |= 2;
                }
                else if (c == '\r')
                {
                    endings.Add("\r");
                    newlineFlags |= 4;
                }
                else
                {
                    endings.Add("\n");
                    newlineFlags |= 1;
                }

                if (lines.Count > 100000)
                    throw new IOException(
                        StringOverlay.Get("Err_FileTooManyLines")).Localized();

                if (c == '\r' &&
                    i + 1 < text.Length &&
                    text[i + 1] == '\n')
                    i++;

                start = i + 1;
                continue;
            }

            if (char.IsControl(c) &&
                c != '\t' &&
                c != '\f' &&
                c != '\v' &&
                c != '\a' &&
                c != '\b' &&
                c != '\u001b' &&
                c != '\u001a')
                throw new IOException(string.Format(StringOverlay.Get("Err_ControlCharacter"), ((int)c).ToString("X4"), path, lines.Count + 1)).Localized();
        }

        lines.Add(text[start..]);

        if (lines.Count > 100000)
            throw new IOException(
                StringOverlay.Get("Err_FileTooManyLines")).Localized();

        endings.Add(string.Empty);
        document.Lines = lines.ToArray();
        document.Endings = endings.ToArray();
        document.Newlines = string.Join("/", new[]
        {
            (newlineFlags & 1) != 0 ? "LF" : null,
            (newlineFlags & 2) != 0 ? "CRLF" : null,
            (newlineFlags & 4) != 0 ? "CR" : null
        }.Where(s => s != null));
        return document;
    }

    /// <summary>
    ///     BOM のないファイルについて、NUL バイトが交互に現れるパターンから UTF-16 のエンディアンを推定します。
    /// </summary>
    /// <param name="bytes">ファイルの内容。</param>
    /// <returns>推定した UTF-16 エンコーディング。該当しない場合は null。</returns>
    private static Encoding DetectUnicodeEncoding(byte[] bytes)
    {
        // BOMs are detected before this fallback. Infer BOM-less UTF-16 only from
        // a strong alternating-NUL pattern, never from the file extension.
        if (bytes.Length < 4 || bytes.Length % 2 != 0) return null;
        int pairs = Math.Min(bytes.Length / 2, 2048), evenZeros = 0, oddZeros = 0;
        for (var i = 0; i < pairs; i++)
        {
            if (bytes[i * 2] == 0) evenZeros++;
            if (bytes[i * 2 + 1] == 0) oddZeros++;
        }

        if (oddZeros > pairs / 2 && evenZeros == 0) return new UnicodeEncoding(false, false, true);
        if (evenZeros > pairs / 2 && oddZeros == 0) return new UnicodeEncoding(true, false, true);
        return null;
    }

    /// <summary>
    ///     ファイル情報から、ヘッダーに表示する日時とサイズの行だけを取り出します。日時の秒未満は省きます。
    /// </summary>
    /// <param name="info">ファイル情報の文字列。</param>
    /// <returns>ヘッダー用の文字列。</returns>
    private static string HeaderMeta(string info)
    {
        var clean = Regex.Replace(info ?? string.Empty, @"(\d{2}:\d{2}:\d{2})\.\d+", "$1");
        var kept = new List<string>();
        foreach (var line in clean.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries))
        {
            var text = line.Trim();
            if (text.IndexOf('/') >= 0 || text.EndsWith("bytes", StringComparison.OrdinalIgnoreCase))
                kept.Add(text);
        }

        return string.Join("\n", kept);
    }

    /// <summary>
    ///     画像ファイルを読み込み、形式名を判定します。GIF の場合はフレーム情報も作成します。
    /// </summary>
    /// <param name="path">画像ファイルのパス。</param>
    /// <returns>画像、形式名、GIF のフレーム情報。ファイルがない場合はすべて null。</returns>
    private static (BitmapSource Image, string Format, GifFrames Gif) LoadImage(string path)
    {
        if (DiffStamp.Read(path) == null) return (null, null, null);
        using var stream = System.IO.File.OpenRead(path);
        var decoder = BitmapDecoder.Create(stream, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad);
        var format = decoder switch
        {
            PngBitmapDecoder => "PNG",
            JpegBitmapDecoder => "JPG",
            TiffBitmapDecoder => "TIF",
            BmpBitmapDecoder => "BMP",
            GifBitmapDecoder => "GIF",
            IconBitmapDecoder => "ICO",
            WmpBitmapDecoder => "WDP",
            _ => Path.GetExtension(path).TrimStart('.').ToUpperInvariant()
        };
        if (decoder is GifBitmapDecoder gifDecoder)
        {
            var gif = new GifFrames(gifDecoder);
            return (gif.Compose(0), format, gif);
        }

        var frame = decoder.Frames[0];
        frame.Freeze();
        return (frame, format, null);
    }

    /// <summary>
    ///     比較一覧のファイル情報を更新し、ディスク上の最新の内容を読み込み直します。パッチ表示では未保存の編集がある場合は拒否します。
    /// </summary>
    internal async Task ReloadFromDiskAsync()
    {
        if (!CanChangeComparison || _checkingExternalEdit) return;
        _navigating = true;
        try
        {
            if (!IsDetachedComparison && RefreshFileRequested != null) await RefreshFileRequested(File);
            if (IsPatch)
            {
                if (PatchFiles.Any(file => file.HasUserEdits))
                    throw new InvalidOperationException(StringOverlay.Get("Err_PatchSaveBeforeReload")).Localized();
                var root = PatchRoot;
                _loadedPatchPath = null;
                await LoadPatchAsync(GetPath(true));
                if (root != null) SetPatchRoot(root);
            }
            File.Source = DiffStamp.Read(GetPath(true));
            File.Target = DiffStamp.Read(GetPath(false));
            _preparedText = null;
            RefreshFileDetails();
            if (ReloadRequested != null) await ReloadRequested();
            else await LoadContentAsync();
        }
        finally
        {
            _navigating = false;
        }
    }

    /// <summary>
    ///     外部 DIFF ツールのプリセットを、初回だけ入力履歴に登録します。
    /// </summary>
    private static void SeedExternalDiffPresets()
    {
        var settingsDirectory = AppSlot.Directory;
        var markerPath = Path.Combine(settingsDirectory, "diff-view-presets-v2.txt");
        if (System.IO.File.Exists(markerPath)) return;

        var store = new InputHistoryStore(Path.Combine(settingsDirectory, "input-history"));
        store.Replace("DiffView-ExternalDiff", ExternalDiffPresets);
        try
        {
            Directory.CreateDirectory(settingsDirectory);
            System.IO.File.WriteAllText(markerPath, "code+mifes+winmerge+visualstudio");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Trace.TraceWarning("Unable to record external diff preset initialization: {0}", ex);
        }
    }

    /// <summary>
    ///     比較元または比較先のファイルを、関連付けられたアプリケーションで開きます。
    /// </summary>
    /// <param name="source">比較元の場合は true。</param>
    private void OpenAssociatedApplication(bool source)
    {
        try
        {
            var path = GetPath(source);
            if (!System.IO.File.Exists(path))
                throw new FileNotFoundException(StringOverlay.Get(source ? "Err_SourceFileMissing" : "Err_TargetFileMissing")).Localized();

            Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            _dialogs.Show(ErrorMessages.English(ex), Title, MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    /// <summary>
    ///     設定された外部 DIFF ツールで 2 つのファイルを開き、編集後に再読み込みできるよう状態を記録します。
    /// </summary>
    private void OpenExternalDiff()
    {
        try
        {
            var sourcePath = GetPath(true);
            var targetPath = GetPath(false);
            if (!System.IO.File.Exists(sourcePath)) throw new FileNotFoundException(StringOverlay.Get("Err_SourceFileMissing")).Localized();
            if (!System.IO.File.Exists(targetPath)) throw new FileNotFoundException(StringOverlay.Get("Err_TargetFileMissing")).Localized();

            var command = (ExternalDiff ?? string.Empty).Trim();
            if (command.Length == 0) throw new InvalidOperationException(StringOverlay.Get("Err_ExternalDiffEmpty")).Localized();

            ExternalDiffHistoryRequested?.Invoke();

            string executable;
            string arguments;
            SplitCommand(command, out executable, out arguments);

            executable = Environment.ExpandEnvironmentVariables(executable);
            arguments = arguments
                .Replace("{source}", sourcePath)
                .Replace("{target}", targetPath);

            _externalSourceStamp = DiffStamp.Read(sourcePath);
            _externalTargetStamp = DiffStamp.Read(targetPath);
            _externalDiffPending = true;

            Process.Start(CreateExternalDiffStartInfo(executable, arguments));
        }
        catch (Exception ex)
        {
            _dialogs.Show(ErrorMessages.English(ex), Title, MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    /// <summary>
    ///     外部 DIFF ツールを通常のウィンドウで起動するための起動情報を作成します。
    /// </summary>
    /// <param name="executable">実行ファイル。</param>
    /// <param name="arguments">コマンドライン引数。</param>
    /// <returns>起動情報。</returns>
    internal static ProcessStartInfo CreateExternalDiffStartInfo(string executable, string arguments)
    {
        return new ProcessStartInfo(executable, arguments)
        {
            UseShellExecute = true,
            WindowStyle = ProcessWindowStyle.Normal
        };
    }

    /// <summary>
    ///     外部 DIFF ツールでファイルが変更されていれば、比較を読み込み直します。未保存の編集がある場合は何もしません。
    /// </summary>
    internal async Task RefreshAfterExternalEditAsync()
    {
        if (HasEdits) return;
        if (_closed || _navigating || _loadingContent || !_externalDiffPending || _checkingExternalEdit) return;

        try
        {
            _checkingExternalEdit = true;
            var source = DiffStamp.Read(GetPath(true));
            var target = DiffStamp.Read(GetPath(false));
            if (SameStamp(source, _externalSourceStamp) && SameStamp(target, _externalTargetStamp)) return;

            _externalSourceStamp = source;
            _externalTargetStamp = target;

            if (!IsDetachedComparison && RefreshFileRequested != null) await RefreshFileRequested(File);
            RefreshFileDetails();
            if (ReloadRequested != null) await ReloadRequested();
        }
        catch (Exception ex)
        {
            Status = string.Format(StringOverlay.Get("Diff_ExternalRefreshFailed"), ErrorMessages.English(ex));
        }
        finally
        {
            _checkingExternalEdit = false;
        }
    }

    /// <summary>
    ///     2 つのファイル情報のサイズと更新日時が同じかどうかを判定します。
    /// </summary>
    /// <param name="left">比較するファイル情報。</param>
    /// <param name="right">比較するファイル情報。</param>
    /// <returns>同じ場合、または両方とも null の場合は true。</returns>
    private static bool SameStamp(DiffStamp left, DiffStamp right)
    {
        return left == null || right == null
            ? left == right
            : left.Size == right.Size && left.ModifiedUtc == right.ModifiedUtc;
    }

    /// <summary>
    ///     コマンド文字列を実行ファイルと引数に分けます。実行ファイルは引用符で囲むことができます。
    /// </summary>
    /// <param name="command">コマンド文字列。</param>
    /// <param name="executable">実行ファイル。</param>
    /// <param name="arguments">引数。</param>
    private static void SplitCommand(string command, out string executable, out string arguments)
    {
        command = command.Trim();
        if (command.StartsWith("\"", StringComparison.Ordinal))
        {
            var closingQuote = command.IndexOf('"', 1);
            if (closingQuote < 0)
                throw new FormatException(StringOverlay.Get("Err_ExternalDiffQuote")).Localized();
            executable = command.Substring(1, closingQuote - 1);
            arguments = command.Substring(closingQuote + 1).TrimStart();
            return;
        }

        var separator = command.IndexOfAny(new[] { ' ', '\t' });
        if (separator < 0)
        {
            executable = command;
            arguments = string.Empty;
            return;
        }

        executable = command.Substring(0, separator);
        arguments = command.Substring(separator + 1).TrimStart();
    }

    internal sealed class TextDocument
    {
        internal Encoding Encoding = new UTF8Encoding(false, true);
        internal string EncodingName = "\u2014";
        internal string[] Endings = Array.Empty<string>();
        internal bool HasBom;
        internal string[] Lines = Array.Empty<string>();
        internal string Newlines = string.Empty;
        internal byte[] OriginalBytes;
        /// <summary>First line holding bytes that could not be decoded (shown as U+FFFD); 0 when none.</summary>
        internal int DecodingErrorLine;
    }
}
