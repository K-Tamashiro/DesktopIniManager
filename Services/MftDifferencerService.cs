using FastVolumeIndex;
using System;
using System.Threading;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;
using DesktopIniManager.Properties;

namespace DesktopIniManager.Services
{
    internal sealed class DiffStamp
    {
        public long Size { get; set; }
        public DateTime ModifiedUtc { get; set; }
        public static DiffStamp Read(string path)
        {
            // GetAttributes distinguishes missing files from access/IO errors.
            FileAttributes attributes;
            try { attributes = File.GetAttributes(path); }
            catch (FileNotFoundException) { return null; }
            catch (DirectoryNotFoundException) { return null; }
            if ((attributes & (FileAttributes.Directory | FileAttributes.ReparsePoint)) != 0)
                throw new IOException("Not a regular file: " + path);
            var file = new FileInfo(path);
            return new DiffStamp { Size = file.Length, ModifiedUtc = file.LastWriteTimeUtc };
        }
        public static bool Same(DiffStamp a, DiffStamp b)
        { return Same(a, b, true); }
        public static bool Same(DiffStamp a, DiffStamp b, bool compareTimestamp)
        { return a == null || b == null ? a == b : a.Size == b.Size && (!compareTimestamp || a.ModifiedUtc == b.ModifiedUtc); }
        public string Describe() { return ModifiedUtc.ToLocalTime().ToString("yyyy/MM/dd HH:mm:ss.fffffff") + "\n" + Size.ToString("N0") + " bytes"; }
    }

    [Flags]
    internal enum DiffKind { Same = 1, Different = 2, SourceOnly = 4, TargetOnly = 8, Differences = Different | SourceOnly | TargetOnly, All = Same | Differences }

    internal sealed class DiffFile : INotifyPropertyChanged
    {
        public string RelativePath { get; set; }
        public string Name { get { return Path.GetFileName(RelativePath); } }
        public DiffStamp Source { get; set; }
        public DiffStamp Target { get; set; }
        public bool CompareTimestamp { get; set; } = true;
        public DiffKind Kind { get { return Source == null ? DiffKind.TargetOnly : Target == null ? DiffKind.SourceOnly : DiffStamp.Same(Source, Target, CompareTimestamp) ? DiffKind.Same : DiffKind.Different; } }
        public bool CanSync { get { return Kind != DiffKind.Same; } }
        public string State { get { return Kind == DiffKind.Same ? Strings.Mft_StateSame : Source == null ? Strings.Mft_StateTargetOnly : Target == null ? Strings.Mft_StateSourceOnly : Source.ModifiedUtc == Target.ModifiedUtc ? Strings.Mft_StateSizeDiffers : Strings.Mft_StateTimeSizeDiffers; } }
        public string SourceInfo { get { return Describe(Source, Target); } }
        public string TargetInfo { get { return Describe(Target, Source); } }
        private static string Describe(DiffStamp own, DiffStamp other)
        { return own == null ? Strings.Diff_Missing : (other == null ? "" : DiffStamp.Same(own, other, true) ? Strings.Mft_StateSame + "\n" : own.ModifiedUtc == other.ModifiedUtc ? Strings.Mft_StateSizeDiffers + "\n" : own.ModifiedUtc > other.ModifiedUtc ? Strings.Mft_StateNew + "\n" : Strings.Mft_StateOld + "\n") + own.Describe(); }
        private bool selected;
        public bool Selected { get { return selected; } set { value = value && CanSync; if (selected == value) return; selected = value; PropertyChanged?.Invoke(this, new PropertyChangedEventArgs("Selected")); } }
        public event PropertyChangedEventHandler PropertyChanged;
    }

    internal sealed class DiffProgress
    {
        public string Stage { get; set; }
        public int Completed { get; set; }
        public int Total { get; set; }
    }

    internal sealed class DiffSnapshot
    {
        public string SourceRoot;
        public string TargetRoot;
        public bool CompareTimestamp = true;
        public List<DiffFile> Files = new List<DiffFile>();
        public HashSet<string> Folders = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "" };
    }

    internal static class MftDifferencerService
    {
        private static readonly HashSet<string> IgnoredDirectories = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            ".git",
            ".vs",
            ".vscode"
        };

        public static bool Protected(string path)
        {
            return path.Replace('/', '\\').Split('\\')
                .Any(p => IgnoredDirectories.Contains(p.TrimEnd(' ', '.')));
        }
        public static string Root(string path)
        {
            string root = Path.GetFullPath(path).TrimEnd('\\') + "\\";
            if (root.StartsWith(@"\\") || Protected(root)) throw new IOException(Strings.Mft_ChooseLocalRoot);
            CheckComponents(root);
            if (!Directory.Exists(root)) throw new DirectoryNotFoundException(root);
            return root;
        }
        public static void ValidateRoots(string source, string target)
        {
            if (source.StartsWith(target, StringComparison.OrdinalIgnoreCase) || target.StartsWith(source, StringComparison.OrdinalIgnoreCase))
                throw new IOException(Strings.Mft_RootsNested);
        }
        public static string SafePath(string root, string relative)
        {
            if (string.IsNullOrWhiteSpace(relative) || Path.IsPathRooted(relative) || relative.Contains(':') || Protected(relative) ||
                relative.Replace('/', '\\').Split('\\').Any(p => p == ".." || p == "." || p.Length == 0 || p.EndsWith(" ") || p.EndsWith(".")))
                throw new IOException(string.Format(Strings.Mft_ProtectedPath, relative));
            string path = Path.GetFullPath(Path.Combine(root, relative));
            if (!path.StartsWith(root.TrimEnd('\\') + "\\", StringComparison.OrdinalIgnoreCase) || Protected(path))
                throw new IOException(Strings.Mft_RefusedPath);
            CheckComponents(path);
            return path;
        }
        private static void CheckComponents(string path)
        {
            string current = Path.GetPathRoot(path);
            foreach (string part in path.Substring(current.Length).Split(new[] { '\\', '/' }, StringSplitOptions.RemoveEmptyEntries))
            {
                current = Path.Combine(current, part);
                try
                {
                    if ((File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                        throw new IOException(string.Format(Strings.Mft_LinkExcluded, current));
                }
                catch (FileNotFoundException) { }
                catch (DirectoryNotFoundException) { }
            }
        }
        public static DiffSnapshot Compare(string source, string target, IProgress<DiffProgress> progress = null, bool compareTimestamp = true, CancellationToken token = default(CancellationToken))
        {
            token.ThrowIfCancellationRequested();
            source = Root(source); target = Root(target); ValidateRoots(source, target);
            var result = new DiffSnapshot { SourceRoot = source, TargetRoot = target, CompareTimestamp = compareTimestamp };
            var indexes = new Dictionary<string, NtfsVolumeIndex>(StringComparer.OrdinalIgnoreCase);

            NtfsVolumeIndex sourceIndex = null;
            NtfsVolumeIndex targetIndex = null;
            List<MftEntry> sourceEntries = null;
            List<MftEntry> targetEntries = null;

            Exception sourceMftError = null;
            Exception targetMftError = null;

            try
            {
                sourceIndex = GetIndex(source, indexes, progress, token);
                sourceEntries = sourceIndex.EnumerateDescendants(source, token).Select(entry => { token.ThrowIfCancellationRequested(); return entry; }).ToList();
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                sourceMftError = ex;
            }

            try
            {
                targetIndex = GetIndex(target, indexes, progress, token);
                targetEntries = targetIndex.EnumerateDescendants(target, token).Select(entry => { token.ThrowIfCancellationRequested(); return entry; }).ToList();
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                targetMftError = ex;
            }

            Dictionary<string, DiffStamp> left;
            Dictionary<string, DiffStamp> right;

            // Normal MFT path: source + target are one continuous progress range.
            // When both roots are on the same volume, GetIndex() also reuses the same MFT index.
            if (sourceEntries != null && targetEntries != null)
            {
                int total = sourceEntries.Count + targetEntries.Count;
                int completed = 0;
                var timer = System.Diagnostics.Stopwatch.StartNew();

                progress?.Report(new DiffProgress
                {
                    Stage = Strings.Mft_ReadingMeta,
                    Completed = 0,
                    Total = total
                });

                left = Scan(source, sourceIndex, sourceEntries, result.Folders,
                    ref completed, total, timer, progress, token);

                right = Scan(target, targetIndex, targetEntries, result.Folders,
                    ref completed, total, timer, progress, token);
            }
            else
            {
                if (sourceEntries != null)
                {
                    int completed = 0;
                    int total = sourceEntries.Count;
                    var timer = System.Diagnostics.Stopwatch.StartNew();
                    progress?.Report(new DiffProgress { Stage = Strings.Mft_ReadingMeta, Completed = 0, Total = total });
                    left = Scan(source, sourceIndex, sourceEntries, result.Folders, ref completed, total, timer, progress, token);
                }
                else
                {
                    progress?.Report(new DiffProgress
                    {
                        Stage = string.Format(Strings.Mft_MftUnavailableFor, source) + " " + (sourceMftError == null ? "" : ErrorMessages.English(sourceMftError))
                    });
                    left = ScanFileSystem(source, result.Folders, progress, token);
                }

                if (targetEntries != null)
                {
                    int completed = 0;
                    int total = targetEntries.Count;
                    var timer = System.Diagnostics.Stopwatch.StartNew();
                    progress?.Report(new DiffProgress { Stage = Strings.Mft_ReadingMeta, Completed = 0, Total = total });
                    right = Scan(target, targetIndex, targetEntries, result.Folders, ref completed, total, timer, progress, token);
                }
                else
                {
                    progress?.Report(new DiffProgress
                    {
                        Stage = string.Format(Strings.Mft_MftUnavailableFor, target) + " " + (targetMftError == null ? "" : ErrorMessages.English(targetMftError))
                    });
                    right = ScanFileSystem(target, result.Folders, progress, token);
                }
            }

            progress?.Report(new DiffProgress { Stage = Strings.Mft_Classifying });
            result.Files = Classify(left, right, true, compareTimestamp, token);
            return result;
        }

        private static NtfsVolumeIndex GetIndex(string root, Dictionary<string, NtfsVolumeIndex> indexes, IProgress<DiffProgress> progress, CancellationToken token)
        {
            string volume = Path.GetPathRoot(root);
            NtfsVolumeIndex index;
            if (!indexes.TryGetValue(volume, out index))
            {
                progress?.Report(new DiffProgress { Stage = string.Format(Strings.Mft_ReadingMftFor, volume) });
                indexes.Add(volume, index = NtfsVolumeIndex.Create(root, token));
            }
            return index;
        }

        private static Dictionary<string, DiffStamp> ScanFileSystem(string root, HashSet<string> folders,
            IProgress<DiffProgress> progress, CancellationToken token)
        {
            var files = new Dictionary<string, DiffStamp>(StringComparer.OrdinalIgnoreCase);
            var pending = new Stack<string>();
            pending.Push(root.TrimEnd('\\'));
            int completed = 0;
            var timer = System.Diagnostics.Stopwatch.StartNew();

            while (pending.Count > 0)
            {
                token.ThrowIfCancellationRequested();
                string directory = pending.Pop();

                foreach (string childDirectory in Directory.EnumerateDirectories(directory))
                {
                    token.ThrowIfCancellationRequested();
                    string relative = childDirectory.Substring(root.Length);
                    if (relative.Length == 0 || Protected(relative)) continue;

                    FileAttributes attributes = File.GetAttributes(childDirectory);
                    if ((attributes & FileAttributes.ReparsePoint) != 0) continue;

                    ScanPath(root, relative);
                    folders.Add(relative);
                    pending.Push(childDirectory);
                }

                foreach (string file in Directory.EnumerateFiles(directory))
                {
                    token.ThrowIfCancellationRequested();
                    string relative = file.Substring(root.Length);
                    if (relative.Length == 0 || Protected(relative)) continue;

                    FileAttributes attributes = File.GetAttributes(file);
                    if ((attributes & FileAttributes.ReparsePoint) != 0) continue;

                    string path = ScanPath(root, relative);
                    DiffStamp stamp = DiffStamp.Read(path);
                    if (stamp == null) throw new IOException(string.Format(Strings.Mft_FileDisappeared, path));
                    files.Add(relative, stamp);

                    completed++;
                    if (completed == 1 || timer.ElapsedMilliseconds >= 100)
                    {
                        progress?.Report(new DiffProgress
                        {
                            Stage = Strings.Mft_ScanWithoutMft,
                            Completed = completed,
                            Total = 0
                        });
                        timer.Restart();
                    }
                }
            }

            progress?.Report(new DiffProgress
            {
                Stage = Strings.Mft_ScanComplete,
                Completed = completed,
                Total = completed
            });
            return files;
        }

        internal static List<DiffFile> Classify(Dictionary<string, DiffStamp> left, Dictionary<string, DiffStamp> right, bool includeSame = false, bool compareTimestamp = true, CancellationToken token = default(CancellationToken))
        {
            var files = new List<DiffFile>();
            foreach (string path in left.Keys.Union(right.Keys, StringComparer.OrdinalIgnoreCase).OrderBy(p => p, StringComparer.OrdinalIgnoreCase))
            {
                token.ThrowIfCancellationRequested();
                if (Protected(path)) continue;
                DiffStamp a, b; left.TryGetValue(path, out a); right.TryGetValue(path, out b);
                if (includeSame || !DiffStamp.Same(a, b, compareTimestamp)) files.Add(new DiffFile { RelativePath = path, Source = a, Target = b, CompareTimestamp = compareTimestamp });
            }
            return files;
        }
        private static Dictionary<string, DiffStamp> Scan(string root, NtfsVolumeIndex index, List<MftEntry> entries,
            HashSet<string> folders, ref int completed, int total, System.Diagnostics.Stopwatch timer,
            IProgress<DiffProgress> progress, CancellationToken token)
        {
            var files = new Dictionary<string, DiffStamp>(StringComparer.OrdinalIgnoreCase);
            foreach (MftEntry entry in entries)
            {
                token.ThrowIfCancellationRequested();
                completed++;
                if (completed == 1 || completed == total || timer.ElapsedMilliseconds >= 100)
                {
                    progress?.Report(new DiffProgress { Stage = Strings.Mft_ReadingMeta, Completed = completed, Total = total });
                    timer.Restart();
                }
                string path = index.GetFullPath(entry);
                if (!path.StartsWith(root, StringComparison.OrdinalIgnoreCase)) continue;
                string relative = path.Substring(root.Length);
                if (relative.Length == 0 || Protected(relative)) continue;

                // The path came from EnumerateDescendants(root), so do not run SafePath here.
                // SafePath walks every parent with File.GetAttributes and made the MFT scan
                // proportional to "file count x directory depth".  Keep the inexpensive
                // lexical/root checks during comparison; the full physical safety checks
                // still run immediately before every synchronization operation.
                path = ScanPath(root, relative);
                if (entry.IsDirectory) folders.Add(relative);
                else
                {
                    DiffStamp stamp = DiffStamp.Read(path);
                    if (stamp == null) throw new IOException(string.Format(Strings.Mft_FileDisappeared, path));
                    files.Add(relative, stamp);
                }
            }
            return files;
        }
        private static string ScanPath(string root, string relative)
        {
            if (string.IsNullOrWhiteSpace(relative) || Path.IsPathRooted(relative) || relative.Contains(':') || Protected(relative) ||
                relative.Replace('/', '\\').Split('\\').Any(p => p == ".." || p == "." || p.Length == 0 || p.EndsWith(" ") || p.EndsWith(".")))
                throw new IOException(string.Format(Strings.Mft_ProtectedPath, relative));

            string path = Path.GetFullPath(Path.Combine(root, relative));
            if (!path.StartsWith(root, StringComparison.OrdinalIgnoreCase) || Protected(path))
                throw new IOException(Strings.Mft_RefusedPath);
            return path;
        }
        public static string Operation(DiffFile file, bool toTarget)
        {
            DiffStamp from = toTarget ? file.Source : file.Target, to = toTarget ? file.Target : file.Source;
            return from == null ? "Delete" : to == null ? "Copy" : "Overwrite";
        }
        public static List<string> Synchronize(DiffSnapshot snapshot, IEnumerable<DiffFile> selected, bool toTarget, Action<string> onLog = null)
        {
            Root(snapshot.SourceRoot); Root(snapshot.TargetRoot); ValidateRoots(snapshot.SourceRoot, snapshot.TargetRoot);
            var log = new List<string>();
            Action<string> writeLog = line =>
            {
                log.Add(line);
                onLog?.Invoke(line);
            };
            foreach (DiffFile file in selected)
            {
                string operation = Operation(file, toTarget);
                string left = null;
                string right = null;
                string from = null;
                string to = null;
                try
                {
                    left = SafePath(snapshot.SourceRoot, file.RelativePath);
                    right = SafePath(snapshot.TargetRoot, file.RelativePath);
                    if (!file.CanSync) { writeLog(string.Format(Strings.Mft_SkipSame, file.RelativePath)); continue; }
                    if (!DiffStamp.Same(file.Source, DiffStamp.Read(left), snapshot.CompareTimestamp) || !DiffStamp.Same(file.Target, DiffStamp.Read(right), snapshot.CompareTimestamp))
                        throw new IOException(Strings.Mft_ChangedAfterCompare);
                    from = toTarget ? left : right;
                    to = toTarget ? right : left;
                    if (File.Exists(from)) RejectHardLinks(from);
                    if (File.Exists(to)) RejectHardLinks(to);
                    if (operation == "Delete") File.Delete(to);
                    else
                    {
                        Directory.CreateDirectory(Path.GetDirectoryName(to));
                        string destinationRoot = toTarget ? snapshot.TargetRoot : snapshot.SourceRoot;
                        string temporaryRelative = Path.Combine(Path.GetDirectoryName(file.RelativePath) ?? "", ".dim-sync-" + Guid.NewGuid().ToString("N") + ".tmp");
                        string temporary = SafePath(destinationRoot, temporaryRelative);
                        try
                        {
                            File.Copy(from, temporary, false);
                            File.SetAttributes(temporary, File.GetAttributes(temporary) & ~FileAttributes.ReadOnly);
                            File.SetLastWriteTimeUtc(temporary, (toTarget ? file.Source : file.Target).ModifiedUtc);
                            SafePath(snapshot.SourceRoot, file.RelativePath); SafePath(snapshot.TargetRoot, file.RelativePath);
                            if (!DiffStamp.Same(file.Source, DiffStamp.Read(left), snapshot.CompareTimestamp) || !DiffStamp.Same(file.Target, DiffStamp.Read(right), snapshot.CompareTimestamp))
                                throw new IOException(Strings.Mft_ChangedDuringCopy);
                            if (File.Exists(to)) { RejectHardLinks(to); File.Replace(temporary, to, null); }
                            else File.Move(temporary, to);
                            // Apply the timestamp to the final file, after replacement/rename.
                            // The temporary file's metadata alone does not guarantee the final state.
                            SafePath(destinationRoot, file.RelativePath);
                            RejectHardLinks(to);
                            File.SetLastWriteTimeUtc(to, (toTarget ? file.Source : file.Target).ModifiedUtc);
                        }
                        finally
                        {
                            SafePath(destinationRoot, temporaryRelative);
                            if (File.Exists(temporary)) File.Delete(temporary);
                        }
                    }
                    SafePath(snapshot.SourceRoot, file.RelativePath); SafePath(snapshot.TargetRoot, file.RelativePath);
                    if (!DiffStamp.Same(toTarget ? file.Source : file.Target, DiffStamp.Read(to)))
                        throw new IOException(Strings.Mft_VerifyFailed);
                    writeLog(Strings.Common_OK + " " + operation + " " + file.RelativePath);
                }
                catch (Exception ex)
                {
                    // Do not classify every "access denied" as a lock.
                    // Probe the actual source/destination file and report LOCKED only when
                    // Windows refuses an exclusive open because another process is using it.
                    bool locked = IsFileLocked(to) || IsFileLocked(from);

                    if (locked)
                        writeLog(Strings.Common_Locked + " " + operation + " " + file.RelativePath + " : " + ErrorMessages.English(ex));
                    else
                        writeLog(Strings.Common_Fail + " " + operation + " " + file.RelativePath + " : " + ErrorMessages.English(ex));
                }
            }
            return log;
        }
        private static bool IsFileLocked(string path)
        {
            if (string.IsNullOrWhiteSpace(path) || !File.Exists(path)) return false;

            try
            {
                // A normal accessible file can be opened exclusively for read.
                // A file held by another process with incompatible sharing fails here
                // with ERROR_SHARING_VIOLATION / ERROR_LOCK_VIOLATION.
                using (var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.None))
                {
                }
                return false;
            }
            catch (IOException ex)
            {
                int code = ex.HResult & 0xFFFF;
                return code == 32 || code == 33;
            }
            catch (UnauthorizedAccessException)
            {
                // Permission/ACL/read-only issues are real failures, not "locked".
                return false;
            }
        }

        public static void RejectHardLinks(string path)
        {
            using (var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
            {
                HandleInfo info;
                if (!GetFileInformationByHandle(stream.SafeFileHandle, out info)) throw new Win32Exception(Marshal.GetLastWin32Error());
                if (info.Links > 1) throw new IOException(string.Format(Strings.Mft_HardLinkExcluded, path));
            }
        }
        [StructLayout(LayoutKind.Sequential)]
        private struct HandleInfo
        {
            public uint Attributes;
            public System.Runtime.InteropServices.ComTypes.FILETIME Creation, Access, Write;
            public uint Volume, SizeHigh, SizeLow, Links, IndexHigh, IndexLow;
        }
        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool GetFileInformationByHandle(SafeFileHandle handle, out HandleInfo info);
    }
}
