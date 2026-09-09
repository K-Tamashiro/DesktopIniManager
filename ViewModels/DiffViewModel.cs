using System;
using System.Collections.Generic;
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
        public DiffFile File { get => file; set => SetProperty(ref file, value); }
        public List<DiffLine> Lines { get; private set; }
        public List<int> Hunks { get; } = new List<int>();
        internal int CurrentHunk { get; set; } = -1;
        private string status = string.Empty;
        public string Status { get => status; set => SetProperty(ref status, value); }
        public RelayCommand PreviousHunkCommand { get; }
        public RelayCommand NextHunkCommand { get; }
        public AsyncRelayCommand PreviousFileCommand { get; }
        public AsyncRelayCommand NextFileCommand { get; }
        public event Action<int> JumpRequested;
        internal Func<Task> ReloadRequested { get; set; }
        internal DiffViewModel(DiffSnapshot snapshot, DiffFile file)
        {
            Snapshot = snapshot; File = file;
            PreviousHunkCommand = new RelayCommand(() => NavigateHunk(-1));
            NextHunkCommand = new RelayCommand(() => NavigateHunk(1));
            PreviousFileCommand = new AsyncRelayCommand(() => NavigateFileAsync(-1), ReportError);
            NextFileCommand = new AsyncRelayCommand(() => NavigateFileAsync(1), ReportError);
        }
        private void ReportError(Exception ex) => Status = string.Format(StringOverlay.Get("Diff_Unable"), ErrorMessages.English(ex));
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
            var candidates = Snapshot.Files.Where(candidate => candidate.Kind != DiffKind.Same && !DiffMedia.IsBinary(candidate.RelativePath))
                .OrderBy(candidate => candidate.RelativePath, StringComparer.CurrentCultureIgnoreCase).ToList();
            if (candidates.Count == 0) return;
            int index = candidates.FindIndex(candidate => ReferenceEquals(candidate, File) || string.Equals(candidate.RelativePath, File.RelativePath, StringComparison.OrdinalIgnoreCase));
            index = index < 0 ? (direction > 0 ? 0 : candidates.Count - 1) : (index + direction + candidates.Count) % candidates.Count;
            File = candidates[index];
            if (ReloadRequested != null) await ReloadRequested();
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
    }
}
