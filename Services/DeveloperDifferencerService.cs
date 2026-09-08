using Microsoft.Win32.SafeHandles;
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;

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

    /// <summary>Compares and synchronizes two development directory trees.</summary>
    internal static class DeveloperDifferencerService
    {
        private static readonly HashSet<string> IgnoredDirectories = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            ".git",
            ".vs",
            ".vscode"
        };

        /// <summary>Returns whether a path enters a protected metadata directory.</summary>
        public static bool Protected(string path)
        {
            return path.Replace('/', '\\').Split('\\')
                .Any(p => IgnoredDirectories.Contains(p.TrimEnd(' ', '.')));
        }
        /// <summary>Normalizes and validates a comparison root directory.</summary>
        public static string Root(string path)
        {
            string root = Path.GetFullPath(path).TrimEnd('\\') + "\\";
            if (Protected(root)) throw new IOException("Choose a root outside .git.");
            if (!Directory.Exists(root)) throw new DirectoryNotFoundException(root);
            return root;
        }
        /// <summary>Ensures that the comparison roots are distinct and non-overlapping.</summary>
        public static void ValidateRoots(string source, string target)
        {
            if (source.StartsWith(target, StringComparison.OrdinalIgnoreCase) || target.StartsWith(source, StringComparison.OrdinalIgnoreCase))
                throw new IOException("Roots must not be equal or nested.");
        }
        /// <summary>Resolves a relative file path while enforcing comparison boundaries.</summary>
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
        }
        /// <summary>Compares one relative folder beneath two development roots.</summary>
        public static DiffSnapshot CompareFolder(string sourceRoot, string targetRoot, string relativeFolder, bool compareTimestamp = true, CancellationToken token = default(CancellationToken))
        {
            token.ThrowIfCancellationRequested();
            sourceRoot = Root(sourceRoot);
            targetRoot = Root(targetRoot);
            ValidateRoots(sourceRoot, targetRoot);

            relativeFolder = (relativeFolder ?? string.Empty).Trim().Trim('\\', '/');
            if (relativeFolder.Length > 0 && Protected(relativeFolder))
                throw new IOException("Protected folder: " + relativeFolder);

            var result = new DiffSnapshot
            {
                SourceRoot = sourceRoot,
                TargetRoot = targetRoot,
                CompareTimestamp = compareTimestamp
            };

            Dictionary<string, DiffStamp> left = ScanSelectedFolder(sourceRoot, relativeFolder, result.Folders, token);
            Dictionary<string, DiffStamp> right = ScanSelectedFolder(targetRoot, relativeFolder, result.Folders, token);
            result.Files = Classify(left, right, true, compareTimestamp, token);
            return result;
        }

        private static Dictionary<string, DiffStamp> ScanSelectedFolder(string root, string relativeFolder, HashSet<string> folders, CancellationToken token)
        {
            var files = new Dictionary<string, DiffStamp>(StringComparer.OrdinalIgnoreCase);
            string baseDirectory = relativeFolder.Length == 0
                ? root.TrimEnd('\\')
                : SafeFolderPath(root, relativeFolder);

            if (!Directory.Exists(baseDirectory)) return files;

            var pending = new Stack<string>();
            pending.Push(baseDirectory);

            while (pending.Count > 0)
            {
                token.ThrowIfCancellationRequested();
                string directory = pending.Pop();
                string directoryRelative = RelativeFromRoot(root, directory);
                folders.Add(directoryRelative);

                foreach (string childDirectory in Directory.EnumerateDirectories(directory))
                {
                    token.ThrowIfCancellationRequested();
                    FileAttributes attributes = File.GetAttributes(childDirectory);
                    string relative = RelativeFromRoot(root, childDirectory);
                    if (Protected(relative)) continue;
                    folders.Add(relative);
                    pending.Push(childDirectory);
                }

                foreach (string file in Directory.EnumerateFiles(directory))
                {
                    token.ThrowIfCancellationRequested();
                    FileAttributes attributes = File.GetAttributes(file);
                    string relative = RelativeFromRoot(root, file);
                    if (Protected(relative)) continue;
                    DiffStamp stamp = DiffStamp.Read(file);
                    if (stamp != null) files[relative] = stamp;
                }
            }

            return files;
        }

        private static string SafeFolderPath(string root, string relativeFolder)
        {
            if (string.IsNullOrWhiteSpace(relativeFolder) || Path.IsPathRooted(relativeFolder) || relativeFolder.Contains(':') || Protected(relativeFolder) ||
                relativeFolder.Replace('/', '\\').Split('\\').Any(p => p == ".." || p == "." || p.Length == 0 || p.EndsWith(" ") || p.EndsWith(".")))
                throw new IOException("Protected or invalid relative folder: " + relativeFolder);

            string path = Path.GetFullPath(Path.Combine(root, relativeFolder));
            if (!path.StartsWith(root.TrimEnd('\\') + "\\", StringComparison.OrdinalIgnoreCase) || Protected(path))
                throw new IOException("Refused a folder outside the root or inside .git.");
            CheckComponents(path);
            return path;
        }

        private static string RelativeFromRoot(string root, string path)
        {
            string normalizedRoot = root.TrimEnd('\\') + "\\";
            string full = Path.GetFullPath(path);
            if (string.Equals(full.TrimEnd('\\'), root.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase))
                return string.Empty;
            if (!full.StartsWith(normalizedRoot, StringComparison.OrdinalIgnoreCase))
                throw new IOException("Path is outside the comparison root: " + full);
            return full.Substring(normalizedRoot.Length);
        }

        /// <summary>Compares all eligible files beneath two development roots.</summary>
        public static DiffSnapshot Compare(string source, string target, IProgress<DiffProgress> progress = null, bool compareTimestamp = true, CancellationToken token = default(CancellationToken))
        {
            token.ThrowIfCancellationRequested();
            source = Root(source); target = Root(target); ValidateRoots(source, target);
            var result = new DiffSnapshot { SourceRoot = source, TargetRoot = target, CompareTimestamp = compareTimestamp };

            progress?.Report(new DiffProgress { Stage = "Scanning source files…" });
            Dictionary<string, DiffStamp> left = ScanSelectedFolder(source, string.Empty, result.Folders, token);

            progress?.Report(new DiffProgress { Stage = "Scanning target files…" });
            Dictionary<string, DiffStamp> right = ScanSelectedFolder(target, string.Empty, result.Folders, token);

            progress?.Report(new DiffProgress { Stage = "Classifying differences by relative path…" });
            result.Files = Classify(left, right, true, compareTimestamp, token);
            return result;
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
        /// <summary>Returns the synchronization operation required for a difference.</summary>
        public static string Operation(DiffFile file, bool toTarget)
        {
            DiffStamp from = toTarget ? file.Source : file.Target, to = toTarget ? file.Target : file.Source;
            return from == null ? "Delete" : to == null ? "Copy" : "Overwrite";
        }
        /// <summary>Synchronizes selected differences and returns an operation log.</summary>
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
                    if (!DiffStamp.Same(toTarget ? file.Source : file.Target, DiffStamp.Read(to), snapshot.CompareTimestamp))
                        throw new IOException("Synchronization verification failed: destination timestamp, size or existence differs. Compare again.");
                    writeLog("OK " + operation + " " + file.RelativePath);
                }
                catch (Exception ex)
                {
                    // Do not classify every "access denied" as a lock.
                    // Probe the actual source/destination file and report LOCKED only when
                    // Windows refuses an exclusive open because another process is using it.
                    bool locked = IsFileLocked(to) || IsFileLocked(from);

                    if (locked)
                        writeLog("LOCKED " + operation + " " + file.RelativePath + " : " + ErrorMessages.English(ex));
                    else
                        writeLog("FAIL " + operation + " " + file.RelativePath + " : " + ErrorMessages.English(ex));
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

        /// <summary>Rejects files with multiple hard-link references.</summary>
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
