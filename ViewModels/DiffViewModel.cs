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

    private readonly IUserDialogService dialogs;

    private readonly HashSet<int> expandedEqualBlocks = new();
    private readonly bool standalone;
    private List<DiffLine> allLines;
    private bool checkingExternalEdit;
    private string externalDiff = string.Empty;
    private bool externalDiffPending;
    private DiffStamp externalSourceStamp, externalTargetStamp;
    private DiffFile file;
    private int frameVersion;
    private DiffFile listFile;
    private bool loadingContent, navigating, closed;
    private TextDocument[] preparedText;
    private GifFrames sourceGif, targetGif;
    private string sourcePath, targetPath;
    private string status = string.Empty;

    internal DiffViewModel(DiffSnapshot snapshot, DiffFile file, IUserDialogService dialogs, string sourcePath = null,
        string targetPath = null)
    {
        this.sourcePath = sourcePath;
        this.targetPath = targetPath;
        standalone = sourcePath != null || targetPath != null;
        listFile = file;
        this.dialogs = dialogs;
        DifferencesOnly = SettingsService.LoadDiffDifferencesOnly();
        Snapshot = snapshot;
        File = file;
        PreviousHunkCommand = new RelayCommand(() => NavigateHunk(-1));
        NextHunkCommand = new RelayCommand(() => NavigateHunk(1));
        PreviousFileCommand = new AsyncRelayCommand(() => NavigateFileAsync(-1), ReportError, () => !standalone);
        NextFileCommand = new AsyncRelayCommand(() => NavigateFileAsync(1), ReportError, () => !standalone);
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
        get => file;
        set
        {
            if (SetProperty(ref file, value)) RefreshFileDetails();
        }
    }

    internal bool IsDetachedComparison => sourcePath != null || targetPath != null;
    internal bool IsEmpty => string.IsNullOrEmpty(File.RelativePath);

    public string Title => IsEmpty
        ? StringOverlay.Get("Diff_Heading")
        : string.Format(StringOverlay.Get("Diff_TitleFile"),
            !IsDetachedComparison
                ? File.RelativePath
                : Path.GetFileName(sourcePath) + " \u2194 " + Path.GetFileName(targetPath));

    public string SourceHeader => HeaderMeta(File.SourceInfo);
    public string TargetHeader => HeaderMeta(File.TargetInfo);
    public bool IsImage => DiffMedia.IsImage(File.RelativePath);
    public bool IsVideo => DiffMedia.IsVideo(File.RelativePath);
    public bool IsAudio => DiffMedia.IsAudio(File.RelativePath);

    public string ExternalDiff
    {
        get => externalDiff;
        set => SetProperty(ref externalDiff, value);
    }

    public BitmapSource SourceImage { get; private set; }
    public BitmapSource TargetImage { get; private set; }
    internal int ImageFrameCount => Math.Max(sourceGif?.Count ?? 1, targetGif?.Count ?? 1);
    internal int ImageFrameIndex { get; private set; }

    internal string ImageFrameDetails =>
        $"Frame {ImageFrameIndex + 1}/{ImageFrameCount}  Source: {FrameDetails(sourceGif)}  Target: {FrameDetails(targetGif)}";

    internal string SourceImageFormat { get; private set; }
    internal string TargetImageFormat { get; private set; }
    public List<DiffLine> Lines { get; private set; }
    internal IReadOnlyList<DiffLine> AllLines => allLines;
    internal bool DifferencesOnly { get; private set; }
    internal bool CompareNewlines { get; set; } = SettingsService.LoadDiffFlag("newlines", false);
    internal bool CompareCase { get; set; } = SettingsService.LoadDiffFlag("case", false);
    internal bool CompareSpaces { get; set; } = SettingsService.LoadDiffFlag("spaces", false);

    internal bool ShowSpaces { get; set; } =
        SettingsService.LoadDiffFlag("show-spaces", SettingsService.LoadDiffFlag("spaces", false));

    internal bool ShowNewlines { get; set; } =
        SettingsService.LoadDiffFlag("show-newlines", SettingsService.LoadDiffFlag("newlines", false));

    internal bool CanChangeComparison => !loadingContent && !navigating && !checkingExternalEdit && !closed;
    internal TextDocument SourceText { get; private set; }
    internal TextDocument TargetText { get; private set; }
    public List<int> Hunks { get; } = new();
    internal int CurrentHunk { get; set; } = -1;

    public string Status
    {
        get => status;
        set => SetProperty(ref status, value);
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
    internal Action<DiffFile> FileClosed { get; set; }

    private string FrameDetails(GifFrames gif)
    {
        return gif == null
            ? "still"
            : ImageFrameIndex >= gif.Count
                ? $"missing ({gif.Count} frames)"
                : $"{gif.DelayMilliseconds(ImageFrameIndex)} ms ({gif.Count} frames)";
    }

    internal async Task<bool> SelectImageFrameAsync(int index)
    {
        var version = ++frameVersion;
        var a = sourceGif;
        var b = targetGif;
        var images = await Task.Run(() => (a?.Compose(index), b?.Compose(index)));
        if (closed || version != frameVersion) return false;
        if (a != null) SourceImage = images.Item1;
        if (b != null) TargetImage = images.Item2;
        ImageFrameIndex = index;
        return true;
    }

    internal bool ToggleDifferencesOnly()
    {
        if (loadingContent || navigating || closed || IsImage || allLines == null) return false;
        DifferencesOnly = !DifferencesOnly;
        expandedEqualBlocks.Clear();
        UpdateDisplayedLines();
        return true;
    }

    internal bool ExpandFilteredLines(int index)
    {
        if (!CanChangeComparison || !DifferencesOnly || Lines == null || index < 0 || index >= Lines.Count ||
            Lines[index].FilteredLineCount == 0) return false;
        var originalStart = 0;
        for (var i = 0; i < index; i++) originalStart += Math.Max(1, Lines[i].FilteredLineCount);
        expandedEqualBlocks.Add(originalStart);
        UpdateDisplayedLines();
        return true;
    }

    internal int CollapseExpandedLines(int index)
    {
        if (!CanChangeComparison || !DifferencesOnly || Lines == null || index < 0 || index >= Lines.Count ||
            Lines[index].Kind != DiffLineKind.Unchanged || Lines[index].FilteredLineCount > 0) return -1;
        var originalIndex = 0;
        for (var i = 0; i < index; i++) originalIndex += Math.Max(1, Lines[i].FilteredLineCount);
        var start = originalIndex;
        while (start > 0 && allLines[start - 1].Kind == DiffLineKind.Unchanged) start--;
        if (!expandedEqualBlocks.Remove(start)) return -1;
        var collapsedIndex = index - (originalIndex - start);
        UpdateDisplayedLines();
        return collapsedIndex;
    }

    private void UpdateDisplayedLines()
    {
        if (!DifferencesOnly)
        {
            Lines = allLines;
        }
        else
        {
            var visible = new List<DiffLine>();
            for (var i = 0; i < allLines.Count; i++)
            {
                if (allLines[i].Kind != DiffLineKind.Unchanged)
                {
                    visible.Add(allLines[i]);
                    continue;
                }

                var start = i;
                while (i + 1 < allLines.Count && allLines[i + 1].Kind == DiffLineKind.Unchanged) i++;
                var count = i - start + 1;
                if (expandedEqualBlocks.Contains(start))
                {
                    visible.AddRange(allLines.GetRange(start, count));
                    continue;
                }

                var label = string.Format(StringOverlay.Get("Diff_FilteredLines"), count);
                visible.Add(new DiffLine
                { Kind = DiffLineKind.Unchanged, Left = label, Right = label, FilteredLineCount = count });
            }

            Lines = visible;
        }

        Hunks.Clear();
        CurrentHunk = -1;
        for (var i = 0; i < Lines.Count; i++)
            if (Lines[i].Kind != DiffLineKind.Unchanged && (i == 0 || Lines[i - 1].Kind == DiffLineKind.Unchanged))
                Hunks.Add(i);
        OnPropertyChanged(nameof(Lines));
        OnPropertyChanged(nameof(Hunks));
    }

    public event Action CloseRequested;

    public event Action ExternalDiffHistoryRequested;

    public event Action<int> JumpRequested;

    internal void Close()
    {
        closed = true;
        SettingsService.SaveDiffDifferencesOnly(DifferencesOnly);
        SettingsService.SaveDiffFlag("newlines", CompareNewlines);
        SettingsService.SaveDiffFlag("case", CompareCase);
        SettingsService.SaveDiffFlag("spaces", CompareSpaces);
        SettingsService.SaveDiffFlag("show-spaces", ShowSpaces);
        SettingsService.SaveDiffFlag("show-newlines", ShowNewlines);
        externalDiffPending = false;
        if (!standalone) FileClosed?.Invoke(listFile);
        if (string.IsNullOrWhiteSpace(ExternalDiff)) return;
        ExternalDiffHistoryRequested?.Invoke();
        SettingsService.SaveExternalDiff(ExternalDiff.Trim());
    }

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

    internal async Task<bool> LoadContentAsync()
    {
        if (closed || loadingContent) return false;
        loadingContent = true;
        ResetContent();
        RefreshFileDetails();
        SourceImage = TargetImage = null;
        sourceGif = targetGif = null;
        ImageFrameIndex = 0;
        frameVersion++;
        SourceImageFormat = TargetImageFormat = null;
        // Status = StringOverlay.Get("Diff_Loading");  // プログレスパネルで表示するため重複を避ける
        try
        {
            if (DiffMedia.IsBinary(File.RelativePath)) throw new InvalidDataException(DiffMedia.BinaryMessage);
            if (IsVideo || IsAudio)
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
                sourceGif = images[0].Gif;
                targetGif = images[1].Gif;
                Status = string.Empty;
            }
            else
            {
                await LoadTextAsync();
                Status = IsEmpty ? StringOverlay.Get("Diff_DropHint")
                    : Hunks.Count == 0 ? "Identical content"
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
            loadingContent = false;
        }

        return false;
    }

    private void ReportUnsupportedContent()
    {
        dialogs.Show(DiffMedia.BinaryMessage, "Diff View", MessageBoxButton.OK, MessageBoxImage.Information);
        CloseRequested?.Invoke();
    }

    internal void ReportError(Exception ex)
    {
        Status = string.Format(StringOverlay.Get("Diff_Unable"), ErrorMessages.English(ex));
    }

    internal void ClearInputs()
    {
        if (!CanChangeComparison) return;
        ReplaceInputs("", "");
    }

    private void ReplaceInputs(string source, string target)
    {
        // Manual pairs must not modify or refresh the owning comparison snapshot.
        sourcePath = source ?? "";
        targetPath = target ?? "";
        externalDiffPending = false;
        preparedText = null;
        frameVersion++;
        var sourceStamp = DiffStamp.Read(sourcePath);
        var targetStamp = DiffStamp.Read(targetPath);
        File = new DiffFile
        {
            RelativePath = Path.GetFileName(sourceStamp != null ? sourcePath : targetPath),
            Source = sourceStamp,
            Target = targetStamp
        };
    }

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
        navigating = true;
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
                    else if (!videoMode && !audioMode) ReadDocument(path);
                }
            });
            if (closed) return false;
            if (full.Length == 2) ReplaceInputs(full[0], full[1]);
            else ReplaceInputs(sourceSide ? full[0] : currentSource, sourceSide ? currentTarget : full[0]);
            return true;
        }
        finally
        {
            navigating = false;
        }
    }

    internal string GetPath(bool source)
    {
        return (source ? sourcePath : targetPath)
               ?? DeveloperDifferencerService.SafePath(source ? Snapshot.SourceRoot : Snapshot.TargetRoot,
                   File.RelativePath);
    }

    internal void ResetContent()
    {
        Hunks.Clear();
        CurrentHunk = -1;
    }

    internal async Task LoadTextAsync()
    {
        expandedEqualBlocks.Clear();
        string left = GetPath(true), right = GetPath(false);
        var prepared = preparedText;
        preparedText = null;
        var documents = prepared ?? await Task.Run(() => new[] { ReadDocument(left), ReadDocument(right) });
        SourceText = documents[0];
        TargetText = documents[1];
        originalSource = SourceText;
        originalTarget = TargetText;
        await RecompareTextAsync();
    }

    internal async Task RecompareTextAsync()
    {
        expandedEqualBlocks.Clear();
        allLines = await Task.Run(() => DiffTextService.Compare(SourceText.Lines, TargetText.Lines,
            !CompareCase, !CompareSpaces, CompareNewlines ? SourceText.Endings : null,
            CompareNewlines ? TargetText.Endings : null,
            CompareCase));
        edits = new DiffEditSession(SourceText, TargetText, allLines);
        UpdateDisplayedLines();
        Status = IsEmpty ? StringOverlay.Get("Diff_DropHint")
            : Hunks.Count == 0 ? StringOverlay.Get("Diff_Identical")
            : string.Format(StringOverlay.Get("Diff_HunkStatus"), Hunks.Count);
    }

    private void NavigateHunk(int direction)
    {
        if (Hunks.Count == 0) return;
        CurrentHunk = CurrentHunk < 0
            ? direction > 0 ? 0 : Hunks.Count - 1
            : (CurrentHunk + direction + Hunks.Count) % Hunks.Count;
        JumpRequested?.Invoke(Hunks[CurrentHunk]);
    }

    private async Task NavigateFileAsync(int direction)
    {
        if (standalone || closed || navigating || loadingContent || checkingExternalEdit) return;
        if (ConfirmChangesRequested != null && !await ConfirmChangesRequested()) return;
        navigating = true;
        try
        {
            var requested = VisibleFilesRequested?.Invoke();
            var candidates = (requested == null
                ? Snapshot.Files.Where(IsVisibleComparableFile)
                : requested.Where(IsVisibleComparableFile)).ToList();
            if (candidates.Count == 0) return;
            var index = candidates.FindIndex(candidate =>
                ReferenceEquals(candidate, listFile) || string.Equals(candidate.RelativePath, listFile.RelativePath,
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

                if (closed) return;
                externalDiffPending = false;
                preparedText = text;
                sourcePath = targetPath = null;
                listFile = candidate;
                File = candidate;
                if (ReloadRequested != null) await ReloadRequested();
                return;
            }
        }
        finally
        {
            navigating = false;
        }
    }

    private static bool IsVisibleComparableFile(DiffFile candidate)
    {
        return candidate != null && !DiffMedia.IsBinary(candidate.RelativePath);
    }

    internal static string[] ReadText(string path)
    {
        return ReadDocument(path).Lines;
    }

    internal static TextDocument ReadDocument(string path)
    {
        var stamp = DiffStamp.Read(path);

        if (stamp == null)
            return new TextDocument();

        if (stamp.Size > 8 * 1024 * 1024)
            throw new IOException("Files over 8 MB should be opened in an external editor.");

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
                throw new IOException("Files over 8 MB should be opened in an external editor.");
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
                    DecoderFallback.ExceptionFallback)
                : DetectUnicodeEncoding(bytes) ?? new UTF8Encoding(false, true);
        }

        string text;
        try
        {
            text = encoding.GetString(bytes, preamble, bytes.Length - preamble);
        }
        catch (DecoderFallbackException) when (preamble == 0 && encoding.CodePage == 65001)
        {
            encoding = CodePagesEncodingProvider.Instance.GetEncoding(932, EncoderFallback.ExceptionFallback,
                DecoderFallback.ExceptionFallback);
            text = encoding.GetString(bytes);
        }

        var document = new TextDocument
        {
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
                        "Files over 100,000 lines should be opened in an external editor.");

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
                throw new InvalidDataException(DiffMedia.BinaryMessage);
        }

        lines.Add(text[start..]);

        if (lines.Count > 100000)
            throw new IOException(
                "Files over 100,000 lines should be opened in an external editor.");

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

    private static string HeaderMeta(string info)
    {
        var clean = Regex.Replace(info ?? string.Empty, @"(\d{2}:\d{2}:\d{2})\.\d+", "$1");
        var kept = new List<string>();
        foreach (var line in clean.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries))
        {
            var text = line.Trim();
            if (text.IndexOf('/') >= 0 || text.EndsWith("bytes", StringComparison.OrdinalIgnoreCase))
                kept.Add(text);
        }

        return string.Join("\n", kept);
    }

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

    internal async Task ReloadFromDiskAsync()
    {
        if (!CanChangeComparison || checkingExternalEdit) return;
        navigating = true;
        try
        {
            if (!IsDetachedComparison && RefreshFileRequested != null) await RefreshFileRequested(File);
            File.Source = DiffStamp.Read(GetPath(true));
            File.Target = DiffStamp.Read(GetPath(false));
            preparedText = null;
            RefreshFileDetails();
            if (ReloadRequested != null) await ReloadRequested();
            else await LoadContentAsync();
        }
        finally
        {
            navigating = false;
        }
    }

    private static string ImageSize(BitmapSource image)
    {
        return image == null ? "none" : image.PixelWidth + " \u00D7 " + image.PixelHeight + " px";
    }

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
        catch
        {
        }
    }

    private void OpenAssociatedApplication(bool source)
    {
        try
        {
            var path = GetPath(source);
            if (!System.IO.File.Exists(path))
                throw new FileNotFoundException((source ? "Source" : "Target") + " file does not exist.");

            Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            dialogs.Show(ErrorMessages.English(ex), Title, MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void OpenExternalDiff()
    {
        try
        {
            var sourcePath = GetPath(true);
            var targetPath = GetPath(false);
            if (!System.IO.File.Exists(sourcePath)) throw new FileNotFoundException("Source file does not exist.");
            if (!System.IO.File.Exists(targetPath)) throw new FileNotFoundException("Target file does not exist.");

            var command = (ExternalDiff ?? string.Empty).Trim();
            if (command.Length == 0) throw new InvalidOperationException("External diff command is empty.");

            ExternalDiffHistoryRequested?.Invoke();

            string executable;
            string arguments;
            SplitCommand(command, out executable, out arguments);

            executable = Environment.ExpandEnvironmentVariables(executable);
            arguments = arguments
                .Replace("{source}", sourcePath)
                .Replace("{target}", targetPath);

            externalSourceStamp = DiffStamp.Read(sourcePath);
            externalTargetStamp = DiffStamp.Read(targetPath);
            externalDiffPending = true;

            Process.Start(CreateExternalDiffStartInfo(executable, arguments));
        }
        catch (Exception ex)
        {
            dialogs.Show(ErrorMessages.English(ex), Title, MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    internal static ProcessStartInfo CreateExternalDiffStartInfo(string executable, string arguments)
    {
        return new ProcessStartInfo(executable, arguments)
        {
            UseShellExecute = true,
            WindowStyle = ProcessWindowStyle.Normal
        };
    }

    internal async Task RefreshAfterExternalEditAsync()
    {
        if (HasEdits) return;
        if (closed || navigating || loadingContent || !externalDiffPending || checkingExternalEdit) return;

        try
        {
            checkingExternalEdit = true;
            var source = DiffStamp.Read(GetPath(true));
            var target = DiffStamp.Read(GetPath(false));
            if (SameStamp(source, externalSourceStamp) && SameStamp(target, externalTargetStamp)) return;

            externalSourceStamp = source;
            externalTargetStamp = target;

            if (!IsDetachedComparison && RefreshFileRequested != null) await RefreshFileRequested(File);
            RefreshFileDetails();
            if (ReloadRequested != null) await ReloadRequested();
        }
        catch (Exception ex)
        {
            Status = "Unable to refresh external edit: " + ErrorMessages.English(ex);
        }
        finally
        {
            checkingExternalEdit = false;
        }
    }

    private static bool SameStamp(DiffStamp left, DiffStamp right)
    {
        return left == null || right == null
            ? left == right
            : left.Size == right.Size && left.ModifiedUtc == right.ModifiedUtc;
    }

    private static void SplitCommand(string command, out string executable, out string arguments)
    {
        command = command.Trim();
        if (command.StartsWith("\"", StringComparison.Ordinal))
        {
            var closingQuote = command.IndexOf('"', 1);
            if (closingQuote < 0)
                throw new FormatException("The external diff executable path has an unmatched quote.");
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
    }
}