using FastVolumeIndex;
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

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
        public string State { get { return Kind == DiffKind.Same ? "Same" : Source == null ? "Target only" : Target == null ? "Source only" : Source.ModifiedUtc == Target.ModifiedUtc ? "Size differs" : "Time / size differs"; } }
        public string SourceInfo { get { return Describe(Source, Target); } }
        public string TargetInfo { get { return Describe(Target, Source); } }
        private static string Describe(DiffStamp own, DiffStamp other)
        { return own == null ? "missing" : (other == null ? "" : DiffStamp.Same(own, other, true) ? "Same\n" : own.ModifiedUtc == other.ModifiedUtc ? "Size differs\n" : own.ModifiedUtc > other.ModifiedUtc ? "NEW\n" : "OLD\n") + own.Describe(); }
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
            if (root.StartsWith(@"\\") || Protected(root)) throw new IOException("Choose a local root outside .git.");
            CheckComponents(root);
            if (!Directory.Exists(root)) throw new DirectoryNotFoundException(root);
            return root;
        }
        public static void ValidateRoots(string source, string target)
        {
            if (source.StartsWith(target, StringComparison.OrdinalIgnoreCase) || target.StartsWith(source, StringComparison.OrdinalIgnoreCase))
                throw new IOException("Roots must not be equal or nested.");
        }
        public static string SafePath(string root, string relative)
        {
            if (string.IsNullOrWhiteSpace(relative) || Path.IsPathRooted(relative) || relative.Contains(':') || Protected(relative) ||
                relative.Replace('/', '\\').Split('\\').Any(p => p == ".." || p == "." || p.Length == 0 || p.EndsWith(" ") || p.EndsWith(".")))
                throw new IOException("Protected or invalid relative path: " + relative);
            string path = Path.GetFullPath(Path.Combine(root, relative));
            if (!path.StartsWith(root.TrimEnd('\\') + "\\", StringComparison.OrdinalIgnoreCase) || Protected(path))
                throw new IOException("Refused a path outside the root or inside .git.");
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
                        throw new IOException("Links and junctions are excluded: " + current);
                }
                catch (FileNotFoundException) { }
                catch (DirectoryNotFoundException) { }
            }
        }
        public static DiffSnapshot Compare(string source, string target, IProgress<DiffProgress> progress = null, bool compareTimestamp = true)
        {
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
                sourceIndex = GetIndex(source, indexes, progress);
                sourceEntries = sourceIndex.EnumerateDescendants(source).ToList();
            }
            catch (Exception ex)
            {
                sourceMftError = ex;
            }

            try
            {
                targetIndex = GetIndex(target, indexes, progress);
                targetEntries = targetIndex.EnumerateDescendants(target).ToList();
            }
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
                    Stage = "Reading timestamps and sizes…",
                    Completed = 0,
                    Total = total
                });

                left = Scan(source, sourceIndex, sourceEntries, result.Folders,
                    ref completed, total, timer, progress);

                right = Scan(target, targetIndex, targetEntries, result.Folders,
                    ref completed, total, timer, progress);
            }
            else
            {
                if (sourceEntries != null)
                {
                    int completed = 0;
                    int total = sourceEntries.Count;
                    var timer = System.Diagnostics.Stopwatch.StartNew();
                    progress?.Report(new DiffProgress { Stage = "Reading timestamps and sizes…", Completed = 0, Total = total });
                    left = Scan(source, sourceIndex, sourceEntries, result.Folders, ref completed, total, timer, progress);
                }
                else
                {
                    progress?.Report(new DiffProgress
                    {
                        Stage = "MFT unavailable for " + source + ". Falling back to file-system scan: " + sourceMftError?.Message
                    });
                    left = ScanFileSystem(source, result.Folders, progress);
                }

                if (targetEntries != null)
                {
                    int completed = 0;
                    int total = targetEntries.Count;
                    var timer = System.Diagnostics.Stopwatch.StartNew();
                    progress?.Report(new DiffProgress { Stage = "Reading timestamps and sizes…", Completed = 0, Total = total });
                    right = Scan(target, targetIndex, targetEntries, result.Folders, ref completed, total, timer, progress);
                }
                else
                {
                    progress?.Report(new DiffProgress
                    {
                        Stage = "MFT unavailable for " + target + ". Falling back to file-system scan: " + targetMftError?.Message
                    });
                    right = ScanFileSystem(target, result.Folders, progress);
                }
            }

            progress?.Report(new DiffProgress { Stage = "Classifying differences by relative path…" });
            result.Files = Classify(left, right, true, compareTimestamp);
            return result;
        }

        private static NtfsVolumeIndex GetIndex(string root, Dictionary<string, NtfsVolumeIndex> indexes, IProgress<DiffProgress> progress)
        {
            string volume = Path.GetPathRoot(root);
            NtfsVolumeIndex index;
            if (!indexes.TryGetValue(volume, out index))
            {
                progress?.Report(new DiffProgress { Stage = "Reading MFT for " + volume + "…" });
                indexes.Add(volume, index = NtfsVolumeIndex.Create(root));
            }
            return index;
        }

        private static Dictionary<string, DiffStamp> ScanFileSystem(string root, HashSet<string> folders,
            IProgress<DiffProgress> progress)
        {
            var files = new Dictionary<string, DiffStamp>(StringComparer.OrdinalIgnoreCase);
            var pending = new Stack<string>();
            pending.Push(root.TrimEnd('\\'));
            int completed = 0;
            var timer = System.Diagnostics.Stopwatch.StartNew();

            while (pending.Count > 0)
            {
                string directory = pending.Pop();

                foreach (string childDirectory in Directory.EnumerateDirectories(directory))
                {
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
                    string relative = file.Substring(root.Length);
                    if (relative.Length == 0 || Protected(relative)) continue;

                    FileAttributes attributes = File.GetAttributes(file);
                    if ((attributes & FileAttributes.ReparsePoint) != 0) continue;

                    string path = ScanPath(root, relative);
                    DiffStamp stamp = DiffStamp.Read(path);
                    if (stamp == null) throw new IOException("A file disappeared during compare. Compare again: " + path);
                    files.Add(relative, stamp);

                    completed++;
                    if (completed == 1 || timer.ElapsedMilliseconds >= 100)
                    {
                        progress?.Report(new DiffProgress
                        {
                            Stage = "Scanning files without MFT…",
                            Completed = completed,
                            Total = 0
                        });
                        timer.Restart();
                    }
                }
            }

            progress?.Report(new DiffProgress
            {
                Stage = "File-system scan complete.",
                Completed = completed,
                Total = completed
            });
            return files;
        }

        internal static List<DiffFile> Classify(Dictionary<string, DiffStamp> left, Dictionary<string, DiffStamp> right, bool includeSame = false, bool compareTimestamp = true)
        {
            var files = new List<DiffFile>();
            foreach (string path in left.Keys.Union(right.Keys, StringComparer.OrdinalIgnoreCase).OrderBy(p => p, StringComparer.OrdinalIgnoreCase))
            {
                if (Protected(path)) continue;
                DiffStamp a, b; left.TryGetValue(path, out a); right.TryGetValue(path, out b);
                if (includeSame || !DiffStamp.Same(a, b, compareTimestamp)) files.Add(new DiffFile { RelativePath = path, Source = a, Target = b, CompareTimestamp = compareTimestamp });
            }
            return files;
        }
        private static Dictionary<string, DiffStamp> Scan(string root, NtfsVolumeIndex index, List<MftEntry> entries,
            HashSet<string> folders, ref int completed, int total, System.Diagnostics.Stopwatch timer,
            IProgress<DiffProgress> progress)
        {
            var files = new Dictionary<string, DiffStamp>(StringComparer.OrdinalIgnoreCase);
            foreach (MftEntry entry in entries)
            {
                completed++;
                if (completed == 1 || completed == total || timer.ElapsedMilliseconds >= 100)
                {
                    progress?.Report(new DiffProgress { Stage = "Reading timestamps and sizes…", Completed = completed, Total = total });
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
                    if (stamp == null) throw new IOException("A file disappeared during compare. Compare again: " + path);
                    files.Add(relative, stamp);
                }
            }
            return files;
        }
        private static string ScanPath(string root, string relative)
        {
            if (string.IsNullOrWhiteSpace(relative) || Path.IsPathRooted(relative) || relative.Contains(':') || Protected(relative) ||
                relative.Replace('/', '\\').Split('\\').Any(p => p == ".." || p == "." || p.Length == 0 || p.EndsWith(" ") || p.EndsWith(".")))
                throw new IOException("Protected or invalid relative path: " + relative);

            string path = Path.GetFullPath(Path.Combine(root, relative));
            if (!path.StartsWith(root, StringComparison.OrdinalIgnoreCase) || Protected(path))
                throw new IOException("Refused a path outside the root or inside .git.");
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
                    if (!file.CanSync) { writeLog("SKIP same " + file.RelativePath); continue; }
                    if (!DiffStamp.Same(file.Source, DiffStamp.Read(left), snapshot.CompareTimestamp) || !DiffStamp.Same(file.Target, DiffStamp.Read(right), snapshot.CompareTimestamp))
                        throw new IOException("Changed after compare. Compare again.");
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
                                throw new IOException("Changed during copy. Compare again.");
                            if (File.Exists(to)) { RejectHardLinks(to); File.Replace(temporary, to, null); }
                            else File.Move(temporary, to);
                        }
                        finally
                        {
                            SafePath(destinationRoot, temporaryRelative);
                            if (File.Exists(temporary)) File.Delete(temporary);
                        }
                    }
                    writeLog("OK " + operation + " " + file.RelativePath);
                }
                catch (Exception ex)
                {
                    // Do not classify every "access denied" as a lock.
                    // Probe the actual source/destination file and report LOCKED only when
                    // Windows refuses an exclusive open because another process is using it.
                    bool locked = IsFileLocked(to) || IsFileLocked(from);

                    if (locked)
                        writeLog("LOCKED " + operation + " " + file.RelativePath + " : " + ex.Message);
                    else
                        writeLog("FAIL " + operation + " " + file.RelativePath + " : " + ex.Message);
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
                if (info.Links > 1) throw new IOException("Hard links are excluded: " + path);
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
