using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Windows;
using System.Windows.Media.Imaging;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using DesktopIniManager.Services;
using DesktopIniManager.Properties;

namespace DesktopIniManager.ViewModels
{
    internal sealed class DiffViewModel : ObservableObject
    {
        internal DiffSnapshot Snapshot { get; }
        private DiffFile file;
        public DiffFile File
        {
            get => file;
            set
            {
                if (SetProperty(ref file, value)) RefreshFileDetails();
            }
        }
        private readonly string sourcePath, targetPath;
        public string Title => string.Format(StringOverlay.Get("Diff_TitleFile"), sourcePath == null ? File.RelativePath : Path.GetFileName(sourcePath) + " ↔ " + Path.GetFileName(targetPath));
        public string SourceHeader => HeaderMeta(File.SourceInfo);
        public string TargetHeader => HeaderMeta(File.TargetInfo);
        public bool IsImage => DiffMedia.IsImage(File.RelativePath);
        private string externalDiff = string.Empty;
        public string ExternalDiff { get => externalDiff; set => SetProperty(ref externalDiff, value); }
        public BitmapSource SourceImage { get; private set; }
        public BitmapSource TargetImage { get; private set; }
        internal string SourceImageFormat { get; private set; }
        internal string TargetImageFormat { get; private set; }
        private readonly IUserDialogService dialogs;
        private bool externalDiffPending;
        private DiffStamp externalSourceStamp, externalTargetStamp;
        private bool checkingExternalEdit;
        private bool loadingContent, navigating, closed;
        private TextDocument[] preparedText;
        public List<DiffLine> Lines { get; private set; }
        private List<DiffLine> allLines;
        internal IReadOnlyList<DiffLine> AllLines => allLines;
        internal bool DifferencesOnly { get; private set; }
        internal bool CompareNewlines { get; set; } = SettingsService.LoadDiffFlag("newlines", false);
        internal bool CompareCase { get; set; } = SettingsService.LoadDiffFlag("case", true);
        internal bool CompareSpaces { get; set; } = SettingsService.LoadDiffFlag("spaces", false);
        internal bool CanChangeComparison => !loadingContent && !navigating && !closed;
        internal TextDocument SourceText { get; private set; }
        internal TextDocument TargetText { get; private set; }
        internal sealed class TextDocument
        {
            internal string[] Lines = Array.Empty<string>();
            internal string[] Endings = Array.Empty<string>();
            internal string EncodingName = "—";
            internal bool HasBom;
            internal string Newlines => string.Join("/", Endings.Where(e => e.Length > 0).Distinct().Select(e => e == "\r\n" ? "CRLF" : e == "\r" ? "CR" : "LF"));
        }

        internal bool ToggleDifferencesOnly()
        {
            if (loadingContent || navigating || closed || IsImage || allLines == null) return false;
            DifferencesOnly = !DifferencesOnly;
            expandedEqualBlocks.Clear();
            UpdateDisplayedLines();
            return true;
        }

        private readonly HashSet<int> expandedEqualBlocks = new HashSet<int>();

        internal bool ExpandFilteredLines(int index)
        {
            if (!CanChangeComparison || !DifferencesOnly || Lines == null || index < 0 || index >= Lines.Count || Lines[index].FilteredLineCount == 0) return false;
            int originalStart = 0;
            for (int i = 0; i < index; i++) originalStart += Math.Max(1, Lines[i].FilteredLineCount);
            expandedEqualBlocks.Add(originalStart);
            UpdateDisplayedLines();
            return true;
        }

        internal int CollapseExpandedLines(int index)
        {
            if (!CanChangeComparison || !DifferencesOnly || Lines == null || index < 0 || index >= Lines.Count ||
                Lines[index].Kind != DiffLineKind.Unchanged || Lines[index].FilteredLineCount > 0) return -1;
            int originalIndex = 0;
            for (int i = 0; i < index; i++) originalIndex += Math.Max(1, Lines[i].FilteredLineCount);
            int start = originalIndex;
            while (start > 0 && allLines[start - 1].Kind == DiffLineKind.Unchanged) start--;
            if (!expandedEqualBlocks.Remove(start)) return -1;
            int collapsedIndex = index - (originalIndex - start);
            UpdateDisplayedLines();
            return collapsedIndex;
        }

        private void UpdateDisplayedLines()
        {
            if (!DifferencesOnly) Lines = allLines;
            else
            {
                var visible = new List<DiffLine>();
                for (int i = 0; i < allLines.Count; i++)
                {
                    if (allLines[i].Kind != DiffLineKind.Unchanged)
                    {
                        visible.Add(allLines[i]);
                        continue;
                    }
                    int start = i;
                    while (i + 1 < allLines.Count && allLines[i + 1].Kind == DiffLineKind.Unchanged) i++;
                    int count = i - start + 1;
                    if (expandedEqualBlocks.Contains(start))
                    {
                        visible.AddRange(allLines.GetRange(start, count));
                        continue;
                    }
                    string label = string.Format(StringOverlay.Get("Diff_FilteredLines"), count);
                    visible.Add(new DiffLine { Kind = DiffLineKind.Unchanged, Left = label, Right = label, FilteredLineCount = count });
                }
                Lines = visible;
            }
            Hunks.Clear();
            CurrentHunk = -1;
            for (int i = 0; i < Lines.Count; i++)
                if (Lines[i].Kind != DiffLineKind.Unchanged && (i == 0 || Lines[i - 1].Kind == DiffLineKind.Unchanged)) Hunks.Add(i);
            OnPropertyChanged(nameof(Lines));
            OnPropertyChanged(nameof(Hunks));
        }
        public List<int> Hunks { get; } = new List<int>();
        internal int CurrentHunk { get; set; } = -1;
        private string status = string.Empty;
        public string Status { get => status; set => SetProperty(ref status, value); }
        public RelayCommand PreviousHunkCommand { get; }
        public RelayCommand NextHunkCommand { get; }
        public AsyncRelayCommand PreviousFileCommand { get; }
        public AsyncRelayCommand NextFileCommand { get; }
        public RelayCommand OpenSourceCommand { get; }
        public RelayCommand OpenTargetCommand { get; }
        public RelayCommand OpenExternalDiffCommand { get; }
        public RelayCommand CloseCommand { get; }
        public event Action CloseRequested;
        public event Action ExternalDiffHistoryRequested;
        public event Action<int> JumpRequested;
        internal Func<DiffFile, Task> RefreshFileRequested { get; set; }
        internal Func<Task> ReloadRequested { get; set; }
        internal Func<IReadOnlyList<DiffFile>> VisibleFilesRequested { get; set; }
        internal Action<DiffFile> FileClosed { get; set; }
        internal DiffViewModel(DiffSnapshot snapshot, DiffFile file, IUserDialogService dialogs, string sourcePath = null, string targetPath = null)
        {
            this.sourcePath = sourcePath;
            this.targetPath = targetPath;
            this.dialogs = dialogs;
            DifferencesOnly = SettingsService.LoadDiffDifferencesOnly();
            Snapshot = snapshot; File = file;
            PreviousHunkCommand = new RelayCommand(() => NavigateHunk(-1));
            NextHunkCommand = new RelayCommand(() => NavigateHunk(1));
            PreviousFileCommand = new AsyncRelayCommand(() => NavigateFileAsync(-1), ReportError);
            NextFileCommand = new AsyncRelayCommand(() => NavigateFileAsync(1), ReportError);
            OpenSourceCommand = new RelayCommand(() => OpenAssociatedApplication(true));
            OpenTargetCommand = new RelayCommand(() => OpenAssociatedApplication(false));
            OpenExternalDiffCommand = new RelayCommand(OpenExternalDiff);
            CloseCommand = new RelayCommand(() => CloseRequested?.Invoke());
            SeedExternalDiffPresets();
            string savedExternalDiff = SettingsService.LoadExternalDiff();
            ExternalDiff = string.IsNullOrWhiteSpace(savedExternalDiff) ? ExternalDiffPresets[0] : savedExternalDiff;
        }
        internal void Close()
        {
            closed = true;
            SettingsService.SaveDiffDifferencesOnly(DifferencesOnly);
            SettingsService.SaveDiffFlag("newlines", CompareNewlines);
            SettingsService.SaveDiffFlag("case", CompareCase);
            SettingsService.SaveDiffFlag("spaces", CompareSpaces);
            externalDiffPending = false;
            FileClosed?.Invoke(File);
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
        }

        internal async Task<bool> LoadContentAsync()
        {
            if (closed || loadingContent) return false;
            loadingContent = true;
            ResetContent();
            RefreshFileDetails();
            SourceImage = TargetImage = null;
            SourceImageFormat = TargetImageFormat = null;
            Status = StringOverlay.Get("Diff_Loading");
            try
            {
                if (DiffMedia.IsBinary(File.RelativePath)) throw new InvalidDataException(DiffMedia.BinaryMessage);
                if (IsImage)
                {
                    string leftPath = GetPath(true), rightPath = GetPath(false);
                    var images = await Task.Run(() => new[] { LoadImage(leftPath), LoadImage(rightPath) });
                    SourceImage = images[0].Image;
                    TargetImage = images[1].Image;
                    SourceImageFormat = images[0].Format;
                    TargetImageFormat = images[1].Format;
                    Status = string.Empty;
                }
                else
                {
                    await LoadTextAsync();
                    Status = Hunks.Count == 0 ? "Identical content"
                        : string.Format(StringOverlay.Get("Diff_HunkStatus"), Hunks.Count);
                }
                return true;
            }
            catch (InvalidDataException) { ReportUnsupportedContent(); }
            catch (DecoderFallbackException) { ReportUnsupportedContent(); }
            catch (Exception ex) { ReportError(ex); }
            finally { loadingContent = false; }
            return false;
        }

        private void ReportUnsupportedContent()
        {
            dialogs.Show(DiffMedia.BinaryMessage, "Diff View", MessageBoxButton.OK, MessageBoxImage.Information);
            CloseRequested?.Invoke();
        }

        internal void ReportError(Exception ex) => Status = string.Format(StringOverlay.Get("Diff_Unable"), ErrorMessages.English(ex));
        internal string GetPath(bool source) => (source ? sourcePath : targetPath)
            ?? DeveloperDifferencerService.SafePath(source ? Snapshot.SourceRoot : Snapshot.TargetRoot, File.RelativePath);
        internal void ResetContent() { Hunks.Clear(); CurrentHunk = -1; }
        internal async Task LoadTextAsync()
        {
            expandedEqualBlocks.Clear();
            string left = GetPath(true), right = GetPath(false);
            var prepared = preparedText;
            preparedText = null;
            var documents = prepared ?? await Task.Run(() => new[] { ReadDocument(left), ReadDocument(right) });
            SourceText = documents[0]; TargetText = documents[1];
            allLines = await Task.Run(() => DiffTextService.Compare(SourceText.Lines, TargetText.Lines,
                !CompareCase, !CompareSpaces, CompareNewlines ? SourceText.Endings : null, CompareNewlines ? TargetText.Endings : null));
            UpdateDisplayedLines();
        }
        private void NavigateHunk(int direction)
        {
            if (Hunks.Count == 0) return;
            CurrentHunk = CurrentHunk < 0 ? (direction > 0 ? 0 : Hunks.Count - 1) : (CurrentHunk + direction + Hunks.Count) % Hunks.Count;
            JumpRequested?.Invoke(Hunks[CurrentHunk]);
        }
        private async Task NavigateFileAsync(int direction)
        {
            if (closed || navigating || loadingContent || checkingExternalEdit) return;
            navigating = true;
            try
            {
                var requested = VisibleFilesRequested?.Invoke();
                var candidates = (requested == null
                    ? Snapshot.Files.Where(IsVisibleComparableFile)
                    : requested.Where(IsVisibleComparableFile)).ToList();
                if (candidates.Count == 0) return;
                int index = candidates.FindIndex(candidate => ReferenceEquals(candidate, File) || string.Equals(candidate.RelativePath, File.RelativePath, StringComparison.OrdinalIgnoreCase));
                if (candidates.Count == 1 && index == 0) return;
                if (index < 0) index = direction > 0 ? -1 : 0;
                for (int visited = 0; visited < candidates.Count; visited++)
                {
                    index = (index + direction + candidates.Count) % candidates.Count;
                    var candidate = candidates[index];
                    if (string.Equals(candidate.RelativePath, File.RelativePath, StringComparison.OrdinalIgnoreCase)) return;
                    TextDocument[] text = null;
                    if (!DiffMedia.IsImage(candidate.RelativePath))
                    {
                        string left = DeveloperDifferencerService.SafePath(Snapshot.SourceRoot, candidate.RelativePath);
                        string right = DeveloperDifferencerService.SafePath(Snapshot.TargetRoot, candidate.RelativePath);
                        try { text = await Task.Run(() => new[] { ReadDocument(left), ReadDocument(right) }); }
                        catch (InvalidDataException) { continue; }
                        catch (DecoderFallbackException) { continue; }
                        catch (IOException ex) { ReportError(ex); return; }
                        catch (UnauthorizedAccessException ex) { ReportError(ex); return; }
                    }
                    if (closed) return;
                    externalDiffPending = false;
                    preparedText = text;
                    File = candidate;
                    if (ReloadRequested != null) await ReloadRequested();
                    return;
                }
            }
            finally { navigating = false; }
        }

        private static bool IsVisibleComparableFile(DiffFile candidate)
        {
            return candidate != null && !DiffMedia.IsBinary(candidate.RelativePath);
        }
        internal static string[] ReadText(string path) => ReadDocument(path).Lines;
        internal static TextDocument ReadDocument(string path)
        {
            DiffStamp stamp = DiffStamp.Read(path);

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
            int preamble = 0;
            foreach (Encoding candidate in new Encoding[] { new UTF32Encoding(false, true, true), new UTF32Encoding(true, true, true), new UTF8Encoding(true, true), new UnicodeEncoding(false, true, true), new UnicodeEncoding(true, true, true) })
            {
                byte[] bom = candidate.GetPreamble();
                if (bytes.AsSpan().StartsWith(bom)) { encoding = candidate; preamble = bom.Length; break; }
            }
            if (encoding == null)
            {
                // ISO-2022-JP is ASCII-compatible; recognize escape designators before UTF-8.
                bool jis = false;
                for (int i = 0; i + 2 < bytes.Length; i++)
                    if (bytes[i] == 0x1b && ((bytes[i + 1] == '$' && (bytes[i + 2] == '@' || bytes[i + 2] == 'B')) ||
                        (bytes[i + 1] == '(' && (bytes[i + 2] == 'B' || bytes[i + 2] == 'J' || bytes[i + 2] == 'I')))) jis = true;
                encoding = jis ? CodePagesEncodingProvider.Instance.GetEncoding(50220, EncoderFallback.ExceptionFallback, DecoderFallback.ExceptionFallback)
                    : DetectUnicodeEncoding(bytes) ?? new UTF8Encoding(false, true);
            }
            string text;
            try { text = encoding.GetString(bytes, preamble, bytes.Length - preamble); }
            catch (DecoderFallbackException) when (preamble == 0 && encoding.CodePage == 65001)
            {
                encoding = CodePagesEncodingProvider.Instance.GetEncoding(932, EncoderFallback.ExceptionFallback, DecoderFallback.ExceptionFallback);
                text = encoding.GetString(bytes);
            }
            var document = new TextDocument { HasBom = preamble > 0, EncodingName = encoding.CodePage switch
            {
                65001 => "UTF-8", 932 => "S-JIS", 50220 => "ISO-2022-JP", 1200 => "UTF-16 LE",
                1201 => "UTF-16 BE", 12000 => "UTF-32 LE", 12001 => "UTF-32 BE", _ => encoding.WebName
            }};
            if (text.Length == 0) return document;
            var endings = new List<string>();
            var lines = new List<string>(
                Math.Min(4096, Math.Max(1, text.Length / 32)));

            int start = 0;

            for (int i = 0; i < text.Length; i++)
            {
                char c = text[i];

                if (c == '\r' || c == '\n')
                {
                    lines.Add(text[start..i]);
                    endings.Add(c == '\r' && i + 1 < text.Length && text[i + 1] == '\n' ? "\r\n" : c.ToString());

                    if (lines.Count > 100000)
                        throw new IOException(
                            "Files over 100,000 lines should be opened in an external editor.");

                    if (c == '\r' &&
                        i + 1 < text.Length &&
                        text[i + 1] == '\n')
                    {
                        i++;
                    }

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
                {
                    throw new InvalidDataException(DiffMedia.BinaryMessage);
                }
            }

            lines.Add(text[start..]);

            if (lines.Count > 100000)
                throw new IOException(
                    "Files over 100,000 lines should be opened in an external editor.");

            endings.Add(string.Empty);
            document.Lines = lines.ToArray(); document.Endings = endings.ToArray();
            return document;
        }
        private static Encoding DetectUnicodeEncoding(byte[] bytes)
        {
            // BOMs are detected before this fallback. Infer BOM-less UTF-16 only from
            // a strong alternating-NUL pattern, never from the file extension.
            if (bytes.Length < 4 || bytes.Length % 2 != 0) return null;
            int pairs = Math.Min(bytes.Length / 2, 2048), evenZeros = 0, oddZeros = 0;
            for (int i = 0; i < pairs; i++)
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
            string clean = System.Text.RegularExpressions.Regex.Replace(info ?? string.Empty, @"(\d{2}:\d{2}:\d{2})\.\d+", "$1");
            var kept = new List<string>();
            foreach (string line in clean.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries))
            {
                string text = line.Trim();
                if (text.IndexOf('/') >= 0 || text.EndsWith("bytes", StringComparison.OrdinalIgnoreCase))
                    kept.Add(text);
            }
            return string.Join("\n", kept);
        }

        private static (BitmapSource Image, string Format) LoadImage(string path)
        {
            if (DiffStamp.Read(path) == null) return (null, null);
            using var stream = System.IO.File.OpenRead(path);
            var decoder = BitmapDecoder.Create(stream, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad);
            string format = decoder switch
            {
                PngBitmapDecoder => "PNG", JpegBitmapDecoder => "JPG", TiffBitmapDecoder => "TIF",
                BmpBitmapDecoder => "BMP", GifBitmapDecoder => "GIF", IconBitmapDecoder => "ICO",
                WmpBitmapDecoder => "WDP", _ => Path.GetExtension(path).TrimStart('.').ToUpperInvariant()
            };
            var frame = decoder.Frames[0];
            frame.Freeze();
            return (frame, format);
        }

        internal async Task ReloadFromDiskAsync()
        {
            if (!CanChangeComparison || checkingExternalEdit) return;
            navigating = true;
            try
            {
                if (RefreshFileRequested != null) await RefreshFileRequested(File);
                File.Source = DiffStamp.Read(GetPath(true));
                File.Target = DiffStamp.Read(GetPath(false));
                preparedText = null;
                RefreshFileDetails();
                if (ReloadRequested != null) await ReloadRequested();
                else await LoadContentAsync();
            }
            finally { navigating = false; }
        }

        private static string ImageSize(BitmapSource image) { return image == null ? "none" : image.PixelWidth + " × " + image.PixelHeight + " px"; }

        private static readonly string[] ExternalDiffPresets =
        {
            @"code --diff ""{source}"" ""{target}""",
            @"""C:\Program Files\MIFES11\MIW.exe"" /diff ""{source}"" ""{target}""",
            @"""C:\Program Files\WinMerge\WinMergeU.exe"" ""{source}"" ""{target}""",
            @"devenv /Diff ""{source}"" ""{target}""",
            @"""C:\Program Files\Beyond Compare 5\BCompare.exe"" ""{source}"" ""{target}"""
        };

        private static void SeedExternalDiffPresets()
        {
            string settingsDirectory = AppSlot.Directory;
            string markerPath = System.IO.Path.Combine(settingsDirectory, "diff-view-presets-v2.txt");
            if (System.IO.File.Exists(markerPath)) return;

            var store = new InputHistoryStore(System.IO.Path.Combine(settingsDirectory, "input-history"));
            store.Replace("DiffView-ExternalDiff", ExternalDiffPresets);
            try
            {
                Directory.CreateDirectory(settingsDirectory);
                System.IO.File.WriteAllText(markerPath, "code+mifes+winmerge+visualstudio");
            }
            catch { }
        }

        private void OpenAssociatedApplication(bool source)
        {
            try
            {
                string path = GetPath(source);
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
                string sourcePath = GetPath(true);
                string targetPath = GetPath(false);
                if (!System.IO.File.Exists(sourcePath)) throw new FileNotFoundException("Source file does not exist.");
                if (!System.IO.File.Exists(targetPath)) throw new FileNotFoundException("Target file does not exist.");

                string command = (ExternalDiff ?? string.Empty).Trim();
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

                Process.Start(new ProcessStartInfo(executable, arguments)
                {
                    UseShellExecute = true,
                    WindowStyle = ProcessWindowStyle.Hidden
                });
            }
            catch (Exception ex)
            {
                dialogs.Show(ErrorMessages.English(ex), Title, MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        internal async Task RefreshAfterExternalEditAsync()
        {
            if (closed || navigating || loadingContent || !externalDiffPending || checkingExternalEdit) return;

            try
            {
                checkingExternalEdit = true;
                DiffStamp source = DiffStamp.Read(GetPath(true));
                DiffStamp target = DiffStamp.Read(GetPath(false));
                if (SameStamp(source, externalSourceStamp) && SameStamp(target, externalTargetStamp)) return;

                externalSourceStamp = source;
                externalTargetStamp = target;

                if (RefreshFileRequested != null) await RefreshFileRequested(File);
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
                int closingQuote = command.IndexOf('"', 1);
                if (closingQuote < 0) throw new FormatException("The external diff executable path has an unmatched quote.");
                executable = command.Substring(1, closingQuote - 1);
                arguments = command.Substring(closingQuote + 1).TrimStart();
                return;
            }

            int separator = command.IndexOfAny(new[] { ' ', '\t' });
            if (separator < 0)
            {
                executable = command;
                arguments = string.Empty;
                return;
            }

            executable = command.Substring(0, separator);
            arguments = command.Substring(separator + 1).TrimStart();
        }
    }
}
