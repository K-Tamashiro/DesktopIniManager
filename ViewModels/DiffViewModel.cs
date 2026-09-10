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
        public string Title => string.Format(StringOverlay.Get("Diff_TitleFile"), File.RelativePath);
        public string SourceHeader => HeaderText(StringOverlay.Get("Common_Source"), File.SourceInfo);
        public string TargetHeader => HeaderText(StringOverlay.Get("Common_Target"), File.TargetInfo);
        public bool IsImage => DiffMedia.IsImage(File.RelativePath);
        private string externalDiff = string.Empty;
        public string ExternalDiff { get => externalDiff; set => SetProperty(ref externalDiff, value); }
        public BitmapSource SourceImage { get; private set; }
        public BitmapSource TargetImage { get; private set; }
        private readonly IUserDialogService dialogs;
        private bool externalDiffPending;
        private DiffStamp externalSourceStamp, externalTargetStamp;
        private bool checkingExternalEdit;
        public List<DiffLine> Lines { get; private set; }
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
        internal DiffViewModel(DiffSnapshot snapshot, DiffFile file, IUserDialogService dialogs)
        {
            this.dialogs = dialogs;
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
            ResetContent();
            RefreshFileDetails();
            SourceImage = TargetImage = null;
            Status = StringOverlay.Get("Diff_Loading");
            try
            {
                if (DiffMedia.IsBinary(File.RelativePath)) throw new InvalidDataException(DiffMedia.BinaryMessage);
                if (IsImage)
                {
                    string leftPath = GetPath(true), rightPath = GetPath(false);
                    var images = await Task.Run(() => new[] { LoadImage(leftPath), LoadImage(rightPath) });
                    SourceImage = images[0];
                    TargetImage = images[1];
                    Status = "Source: " + ImageSize(SourceImage) + " | Target: " + ImageSize(TargetImage) + " | shared zoom, top-left aligned (GIF/ICO first frame)";
                }
                else
                {
                    await LoadTextAsync();
                    Status = Hunks.Count == 0 ? "Different timestamps, identical content"
                        : Hunks.Count + " hunks | left red = removed  right green = added | UTF-8 / BOM / Shift-JIS | large files use a simplified match";
                }
                return true;
            }
            catch (InvalidDataException) { ReportUnsupportedContent(); }
            catch (DecoderFallbackException) { ReportUnsupportedContent(); }
            catch (Exception ex) { ReportError(ex); }
            return false;
        }

        private void ReportUnsupportedContent()
        {
            dialogs.Show(DiffMedia.BinaryMessage, "Diff View", MessageBoxButton.OK, MessageBoxImage.Information);
            CloseRequested?.Invoke();
        }

        internal void ReportError(Exception ex) => Status = string.Format(StringOverlay.Get("Diff_Unable"), ErrorMessages.English(ex));
        internal string GetPath(bool source) => DeveloperDifferencerService.SafePath(source ? Snapshot.SourceRoot : Snapshot.TargetRoot, File.RelativePath);
        internal void ResetContent() { Hunks.Clear(); CurrentHunk = -1; }
        internal async Task LoadTextAsync()
        {
            string left = GetPath(true), right = GetPath(false);
            Lines = await Task.Run(() => DiffTextService.Compare(ReadText(left), ReadText(right)));
            Hunks.Clear();
            for (int i = 0; i < Lines.Count; i++)
                if (Lines[i].Kind != DiffLineKind.Unchanged && (i == 0 || Lines[i - 1].Kind == DiffLineKind.Unchanged)) Hunks.Add(i);
            OnPropertyChanged(nameof(Lines)); OnPropertyChanged(nameof(Hunks));
        }
        private void NavigateHunk(int direction)
        {
            if (Hunks.Count == 0) return;
            CurrentHunk = CurrentHunk < 0 ? (direction > 0 ? 0 : Hunks.Count - 1) : (CurrentHunk + direction + Hunks.Count) % Hunks.Count;
            JumpRequested?.Invoke(Hunks[CurrentHunk]);
        }
        private async Task NavigateFileAsync(int direction)
        {
            var requested = VisibleFilesRequested?.Invoke();
            var candidates = (requested == null
                ? Snapshot.Files.Where(IsVisibleComparableFile)
                : requested.Where(IsVisibleComparableFile)).ToList();
            if (candidates.Count == 0) return;
            int index = candidates.FindIndex(candidate => ReferenceEquals(candidate, File) || string.Equals(candidate.RelativePath, File.RelativePath, StringComparison.OrdinalIgnoreCase));
            if (candidates.Count == 1 && index == 0) return;
            index = index < 0 ? (direction > 0 ? 0 : candidates.Count - 1) : (index + direction + candidates.Count) % candidates.Count;
            externalDiffPending = false;
            File = candidates[index];
            if (ReloadRequested != null) await ReloadRequested();
        }

        private static bool IsVisibleComparableFile(DiffFile candidate)
        {
            return candidate != null && !DiffMedia.IsBinary(candidate.RelativePath);
        }
        internal static string[] ReadText(string path)
        {
            DiffStamp stamp = DiffStamp.Read(path); if (stamp == null) return new string[0];
            if (stamp.Size > 8 * 1024 * 1024) throw new IOException("Files over 8 MB should be opened in an external editor.");
            byte[] bytes = System.IO.File.ReadAllBytes(path);
            string text;
            try { using (var reader = new StreamReader(new MemoryStream(bytes), new UTF8Encoding(false, true), true)) text = reader.ReadToEnd(); }
            catch (DecoderFallbackException) { text = CodePagesEncodingProvider.Instance.GetEncoding(932, EncoderFallback.ExceptionFallback, DecoderFallback.ExceptionFallback).GetString(bytes); }
            if (text.Any(c => c == '\0' || (char.IsControl(c) && c != '\r' && c != '\n' && c != '\t' && c != '\f')))
                throw new InvalidDataException(DiffMedia.BinaryMessage);
            if (text.Length == 0) return new string[0];
            var lines = text.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');
            if (lines.Length > 100000) throw new IOException("Files over 100,000 lines should be opened in an external editor.");
            return lines;
        }

        private static string HeaderText(string title, string info)
        {
            string cleanInfo = System.Text.RegularExpressions.Regex.Replace(info ?? "", @"(\d{2}:\d{2}:\d{2})\.\d+", "$1");
            return title + "\n" + cleanInfo;
        }

        private static BitmapSource LoadImage(string path)
        {
            if (DiffStamp.Read(path) == null) return null;
            using (var stream = System.IO.File.OpenRead(path))
            { var frame = BitmapDecoder.Create(stream, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad).Frames[0]; frame.Freeze(); return frame; }
        }

        private static string ImageSize(BitmapSource image) { return image == null ? "none" : image.PixelWidth + " × " + image.PixelHeight + " px"; }

        private static readonly string[] ExternalDiffPresets =
        {
            @"code --diff ""{source}"" ""{target}""",
            @"""C:\Program Files\MIFES11\MIW.exe"" /diff ""{source}"" ""{target}""",
            @"""C:\Program Files\WinMerge\WinMergeU.exe"" ""{source}"" ""{target}""",
            @"devenv /Diff ""{source}"" ""{target}"""
        };

        private static void SeedExternalDiffPresets()
        {
            string settingsDirectory = System.IO.Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "DesktopIniManager");
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
            if (!externalDiffPending || checkingExternalEdit) return;

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
