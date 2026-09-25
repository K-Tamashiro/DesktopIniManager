using Microsoft.Win32.SafeHandles;
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;

namespace DesktopIniManager.Services
{
    internal sealed class DiffStamp
    {
        public long Size { get; set; }
        public DateTime ModifiedUtc { get; set; }
        // Compare whole seconds without rounding; retain the original timestamp for copying.
        public long ModifiedUtcSeconds => ModifiedUtc.Ticks / TimeSpan.TicksPerSecond;
        public static DiffStamp Read(string path)
        {
            if (string.IsNullOrWhiteSpace(path)) return null;
            FileAttributes attributes;
            try { attributes = File.GetAttributes(path); }
            catch (FileNotFoundException) { return null; }
            catch (DirectoryNotFoundException) { return null; }
            catch (UnauthorizedAccessException) { return null; }
            catch (IOException) { return null; }
            if ((attributes & FileAttributes.Directory) != 0)
                return null;
            try
            {
                var file = new FileInfo(path);
                return new DiffStamp { Size = file.Length, ModifiedUtc = file.LastWriteTimeUtc };
            }
            catch (FileNotFoundException) { return null; }
            catch (DirectoryNotFoundException) { return null; }
            catch (UnauthorizedAccessException) { return null; }
            catch (IOException) { return null; }
        }
        public static bool Same(DiffStamp a, DiffStamp b)
        { return Same(a, b, true); }
        public static bool Same(DiffStamp a, DiffStamp b, bool compareTimestamp)
        { return a == null || b == null ? a == b : a.Size == b.Size && (!compareTimestamp || a.ModifiedUtcSeconds == b.ModifiedUtcSeconds); }
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
        public bool CanToggleSelection { get { return CanSync || Selected; } }
        public string State { get { return Kind == DiffKind.Same ? "Same" : Source == null ? "Target only" : Target == null ? "Source only" : Source.ModifiedUtcSeconds == Target.ModifiedUtcSeconds ? "Size differs" : "Time / size differs"; } }
        public string SourceInfo { get { return Describe(Source, Target); } }
        public string TargetInfo { get { return Describe(Target, Source); } }
        private static string Describe(DiffStamp own, DiffStamp other)
        { return own == null ? "missing" : (other == null ? "" : DiffStamp.Same(own, other, true) ? "Same\n" : own.ModifiedUtcSeconds == other.ModifiedUtcSeconds ? "Size differs\n" : own.ModifiedUtcSeconds > other.ModifiedUtcSeconds ? "NEW\n" : "OLD\n") + own.Describe(); }
        private bool selected;
        public bool Selected
        {
            get { return selected; }
            set
            {
                if (selected == value) return;
                selected = value;
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs("Selected"));
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs("CanToggleSelection"));
            }
        }
        public event PropertyChangedEventHandler PropertyChanged;
    }

    internal sealed class DiffFolderSync
    {
        public string RelativePath { get; set; }
        public bool SourceExists { get; set; }
        public bool TargetExists { get; set; }
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

        private static Dictionary<string, DiffStamp> ScanSelectedFolder(string root, string relativeFolder, HashSet<string> folders, CancellationToken token, IProgress<DiffProgress> progress = null, string stage = null, int offset = 0, int total = 0)
        {
            var files = new Dictionary<string, DiffStamp>(StringComparer.OrdinalIgnoreCase);
            string baseDirectory = relativeFolder.Length == 0
                ? root.TrimEnd('\\')
                : SafeFolderPath(root, relativeFolder);

            if (!Directory.Exists(baseDirectory)) return files;

            var pending = new Stack<string>();
            pending.Push(baseDirectory);
            int dirsDone = 0;
            int seen = 0;

            while (pending.Count > 0)
            {
                token.ThrowIfCancellationRequested();
                string directory = pending.Pop();
                dirsDone++;
                string directoryRelative = RelativeFromRoot(root, directory);
                folders.Add(directoryRelative);

                foreach (string childDirectory in Directory.EnumerateDirectories(directory))
                {
                    token.ThrowIfCancellationRequested();
                    try
                    {
                        FileAttributes attributes = File.GetAttributes(childDirectory);
                        if ((attributes & FileAttributes.Directory) == 0) continue;
                    }
                    catch (FileNotFoundException) { continue; }
                    catch (DirectoryNotFoundException) { continue; }
                    catch (UnauthorizedAccessException) { continue; }
                    catch (IOException) { continue; }

                    string relative = RelativeFromRoot(root, childDirectory);
                    if (Protected(relative)) continue;
                    folders.Add(relative);
                    pending.Push(childDirectory);
                }

                foreach (string file in Directory.EnumerateFiles(directory))
                {
                    token.ThrowIfCancellationRequested();
                    string relative = RelativeFromRoot(root, file);
                    if (Protected(relative)) continue;
                    DiffStamp stamp = DiffStamp.Read(file);
                    if (stamp != null) files[relative] = stamp;
                    seen++;
                    if (progress != null && stage != null && ((seen & 15) == 0))
                        progress.Report(ReportCompare(stage, offset + files.Count, total));
                }
            }

            if (progress != null && stage != null)
                progress.Report(ReportCompare(stage, offset + files.Count, total));
            return files;
        }

        private static int CountFiles(string root, CancellationToken token, IProgress<DiffProgress> progress, string stage)
        {
            if (!Directory.Exists(root)) return 0;
            var pending = new Stack<string>();
            pending.Push(root.TrimEnd('\\'));
            int count = 0;
            while (pending.Count > 0)
            {
                token.ThrowIfCancellationRequested();
                string directory = pending.Pop();
                try
                {
                    foreach (string childDirectory in Directory.EnumerateDirectories(directory))
                    {
                        token.ThrowIfCancellationRequested();
                        string relative = RelativeFromRoot(root, childDirectory);
                        if (Protected(relative)) continue;
                        pending.Push(childDirectory);
                    }
                    foreach (string file in Directory.EnumerateFiles(directory))
                    {
                        token.ThrowIfCancellationRequested();
                        string relative = RelativeFromRoot(root, file);
                        if (Protected(relative)) continue;
                        count++;
                        if (progress != null && (count & 63) == 0)
                            progress.Report(new DiffProgress { Stage = stage + "  " + count.ToString("N0") + " files", Completed = 0, Total = 0 });
                    }
                }
                catch (IOException) { }
                catch (UnauthorizedAccessException) { }
            }
            if (progress != null)
                progress.Report(new DiffProgress { Stage = stage + "  " + count.ToString("N0") + " files", Completed = 0, Total = 0 });
            return count;
        }

        private static DiffProgress ReportCompare(string stage, int completed, int total)
        {
            return new DiffProgress
            {
                Stage = stage + "  " + completed.ToString("N0") + " / " + Math.Max(1, total).ToString("N0"),
                Completed = completed,
                Total = Math.Max(1, total)
            };
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

            progress?.Report(new DiffProgress { Stage = "Counting source files…", Completed = 0, Total = 0 });
            int sourceCount = CountFiles(source, token, progress, "Counting source files…");
            progress?.Report(new DiffProgress { Stage = "Counting target files…", Completed = 0, Total = 0 });
            int targetCount = CountFiles(target, token, progress, "Counting target files…");
            int total = Math.Max(1, sourceCount + targetCount);

            progress?.Report(ReportCompare("Comparing source…", 0, total));
            var sourceFolders = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "" };
            var targetFolders = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "" };
            Dictionary<string, DiffStamp> left = ScanSelectedFolder(source, string.Empty, sourceFolders, token, progress, "Comparing source…", 0, total);

            progress?.Report(ReportCompare("Comparing target…", left.Count, total));
            Dictionary<string, DiffStamp> right = ScanSelectedFolder(target, string.Empty, targetFolders, token, progress, "Comparing target…", left.Count, total);
            result.Folders = new HashSet<string>(sourceFolders.Union(targetFolders, StringComparer.OrdinalIgnoreCase), StringComparer.OrdinalIgnoreCase);

            progress?.Report(ReportCompare("Classifying differences…", total, total));
            result.Files = Classify(left, right, true, compareTimestamp, token, progress, total);
            return result;
        }

        internal static List<DiffFile> Classify(Dictionary<string, DiffStamp> left, Dictionary<string, DiffStamp> right, bool includeSame = false, bool compareTimestamp = true, CancellationToken token = default(CancellationToken), IProgress<DiffProgress> progress = null, int offset = 0)
        {
            var files = new List<DiffFile>();
            string[] paths = left.Keys.Union(right.Keys, StringComparer.OrdinalIgnoreCase).OrderBy(p => p, StringComparer.OrdinalIgnoreCase).ToArray();
            for (int index = 0; index < paths.Length; index++)
            {
                token.ThrowIfCancellationRequested();
                string path = paths[index];
                if (Protected(path)) continue;
                DiffStamp a, b; left.TryGetValue(path, out a); right.TryGetValue(path, out b);
                if (includeSame || !DiffStamp.Same(a, b, compareTimestamp)) files.Add(new DiffFile { RelativePath = path, Source = a, Target = b, CompareTimestamp = compareTimestamp });
                if (progress != null && index + 1 == paths.Length)
                    progress.Report(ReportCompare("Classifying differences…", Math.Max(offset, paths.Length), Math.Max(offset, paths.Length)));
            }
            return files;
        }
        /// <summary>Returns the synchronization operation required for a difference.</summary>
        public static string Operation(DiffFile file, bool toTarget)
        {
            DiffStamp from = toTarget ? file.Source : file.Target, to = toTarget ? file.Target : file.Source;
            return from == null ? "Delete" : to == null ? "Copy" : "Overwrite";
        }
        /// <summary>Synchronizes selected differences and returns an operation log.</summary>
        public static List<string> Synchronize(DiffSnapshot snapshot, IEnumerable<DiffFile> selected, bool toTarget, Action<string> onLog = null, IEnumerable<DiffFolderSync> selectedFolders = null)
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
            // Directory differences are applied after file operations. This lets file deletions
            // empty one-sided directories before Directory.Delete(false), while file copies already
            // create their parent directories. Empty directories are therefore handled here.
            var folderOperations = (selectedFolders ?? Enumerable.Empty<DiffFolderSync>())
                .Where(f => f != null && !string.IsNullOrWhiteSpace(f.RelativePath) && f.SourceExists != f.TargetExists)
                .OrderBy(f => (toTarget ? f.SourceExists : f.TargetExists) ? FolderDepth(f.RelativePath) : -FolderDepth(f.RelativePath))
                .ToArray();

            foreach (DiffFolderSync folder in folderOperations)
            {
                bool fromExists = toTarget ? folder.SourceExists : folder.TargetExists;
                string operation = fromExists ? "CreateDir" : "DeleteDir";
                try
                {
                    string leftFolder = SafeFolderPath(snapshot.SourceRoot, folder.RelativePath);
                    string rightFolder = SafeFolderPath(snapshot.TargetRoot, folder.RelativePath);
                    string origin = toTarget ? leftFolder : rightFolder;
                    if (Directory.Exists(origin) != fromExists)
                        throw new IOException("Folder changed after compare. Compare again.");

                    string destination = toTarget ? rightFolder : leftFolder;
                    if (fromExists) Directory.CreateDirectory(destination);
                    else if (Directory.Exists(destination)) Directory.Delete(destination, false);

                    if (Directory.Exists(destination) != fromExists)
                        throw new IOException("Folder synchronization verification failed. Compare again.");
                    writeLog("OK " + operation + " " + folder.RelativePath);
                }
                catch (Exception ex)
                {
                    writeLog("FAIL " + operation + " " + folder.RelativePath + " : " + ErrorMessages.English(ex));
                }
            }

            return log;
        }
        private static int FolderDepth(string relativePath)
        {
            return relativePath.Split(new[] { '\\', '/' }, StringSplitOptions.RemoveEmptyEntries).Length;
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

        /// <summary>Builds an output ZIP path from a folder and a file name.</summary>
        public static string ComposeZipPath(string folder, string fileName)
        {
            if (string.IsNullOrWhiteSpace(folder))
                throw new IOException("ZIP output folder is empty.");
            string name = (fileName ?? "").Trim();
            if (name.Length == 0)
                throw new IOException("ZIP file name is empty.");
            if (name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
                throw new IOException("ZIP file name contains invalid characters: " + name);
            if (!name.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
                name += ".zip";
            string directory = Path.GetFullPath(folder.Trim());
            Directory.CreateDirectory(directory);
            return Path.Combine(directory, name);
        }

        /// <summary>Packs selected files and folders from one comparison side into a ZIP while keeping relative paths.</summary>
        public static List<string> PackZip(DiffSnapshot snapshot, IEnumerable<DiffFile> selected, IEnumerable<DiffFolderSync> selectedFolders, bool fromSource, string zipPath, Action<string> onLog = null)
        {
            if (snapshot == null) throw new ArgumentNullException(nameof(snapshot));
            string root = fromSource ? snapshot.SourceRoot : snapshot.TargetRoot;
            Root(root);
            var log = new List<string>();
            Action<string> writeLog = line =>
            {
                log.Add(line);
                onLog?.Invoke(line);
            };
            var files = new Dictionary<string, DiffFile>(StringComparer.OrdinalIgnoreCase);
            foreach (DiffFile file in selected ?? Enumerable.Empty<DiffFile>())
            {
                if (file == null || string.IsNullOrEmpty(file.RelativePath)) continue;
                DiffStamp stamp = fromSource ? file.Source : file.Target;
                if (stamp == null) continue;
                files[file.RelativePath] = file;
            }
            var folders = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (DiffFolderSync folder in selectedFolders ?? Enumerable.Empty<DiffFolderSync>())
            {
                if (folder == null || string.IsNullOrEmpty(folder.RelativePath)) continue;
                bool exists = fromSource ? folder.SourceExists : folder.TargetExists;
                if (!exists) continue;
                folders.Add(folder.RelativePath.Replace('/', '\\'));
            }

            // Expand selected folders in one pass. The previous implementation scanned the
            // complete snapshot once for every selected folder (O(folders * files)).
            if (folders.Count > 0)
            {
                string[] prefixes = folders
                    .Select(folder => folder.TrimEnd('\\') + "\\")
                    .ToArray();
                foreach (DiffFile file in snapshot.Files ?? Enumerable.Empty<DiffFile>())
                {
                    if (file == null || string.IsNullOrEmpty(file.RelativePath)) continue;
                    DiffStamp stamp = fromSource ? file.Source : file.Target;
                    if (stamp == null) continue;
                    string relative = file.RelativePath.Replace('/', '\\');
                    for (int i = 0; i < prefixes.Length; i++)
                    {
                        if (!relative.StartsWith(prefixes[i], StringComparison.OrdinalIgnoreCase)) continue;
                        files[file.RelativePath] = file;
                        break;
                    }
                }
            }
            if (files.Count == 0 && folders.Count == 0)
            {
                writeLog("FAIL no selected files exist on the chosen side");
                return log;
            }
            string directory = Path.GetDirectoryName(zipPath);
            if (!string.IsNullOrEmpty(directory))
                Directory.CreateDirectory(directory);
            string tempZipPath = Path.Combine(
                directory ?? Path.GetDirectoryName(Path.GetFullPath(zipPath)) ?? Environment.CurrentDirectory,
                "." + Path.GetFileName(zipPath) + "." + Guid.NewGuid().ToString("N") + ".tmp");
            string wrapper = ZipRootFolderName(root);
            try
            {
                using (var archive = ZipFile.Open(tempZipPath, ZipArchiveMode.Create))
                {
                    var directoryEntries = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                    AddZipDirectory(archive, wrapper, directoryEntries);
                    foreach (string folder in folders.OrderBy(path => path, StringComparer.OrdinalIgnoreCase))
                    {
                        try
                        {
                            AddZipDirectory(archive, ZipEntryName(wrapper, folder, true), directoryEntries);
                            writeLog("OK DIR " + folder);
                        }
                        catch (Exception ex)
                        {
                            writeLog("FAIL DIR " + folder + " " + ErrorMessages.English(ex));
                        }
                    }
                    foreach (DiffFile file in files.Values.OrderBy(item => item.RelativePath, StringComparer.OrdinalIgnoreCase))
                    {
                        try
                        {
                            string from = SafePath(root, file.RelativePath);
                            if (!File.Exists(from))
                            {
                                writeLog("FAIL missing " + file.RelativePath);
                                continue;
                            }
                            RejectHardLinks(from);
                            string parent = Path.GetDirectoryName(file.RelativePath.Replace('/', '\\'));
                            if (!string.IsNullOrEmpty(parent))
                                AddZipDirectory(archive, ZipEntryName(wrapper, parent, true), directoryEntries);
                            archive.CreateEntryFromFile(from, ZipEntryName(wrapper, file.RelativePath, false), CompressionLevel.Fastest);
                            writeLog("OK ZIP " + file.RelativePath);
                        }
                        catch (Exception ex)
                        {
                            if (IsFileLocked(SafePathOrEmpty(root, file.RelativePath)))
                                writeLog("LOCKED " + file.RelativePath);
                            else
                                writeLog("FAIL ZIP " + file.RelativePath + " " + ErrorMessages.English(ex));
                        }
                    }
                }
                File.Move(tempZipPath, zipPath, true);
                writeLog("OK archive " + zipPath);
            }
            catch (Exception ex)
            {
                writeLog("FAIL archive " + ErrorMessages.English(ex));
                try { if (File.Exists(tempZipPath)) File.Delete(tempZipPath); } catch { }
            }
            return log;
        }

        private static bool IsUnder(string relativePath, string folder)
        {
            if (string.IsNullOrEmpty(relativePath) || string.IsNullOrEmpty(folder)) return false;
            string path = relativePath.Replace('/', '\\');
            string prefix = folder.Replace('/', '\\').TrimEnd('\\') + "\\";
            return path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase);
        }

        private static string ZipRootFolderName(string root)
        {
            string name = Path.GetFileName((root ?? "").TrimEnd('\\', '/'));
            if (string.IsNullOrWhiteSpace(name)) name = "root";
            foreach (char invalid in Path.GetInvalidFileNameChars())
                name = name.Replace(invalid, '_');
            return name;
        }

        private static string ZipEntryName(string wrapper, string relative, bool directory)
        {
            string entry = (relative ?? "").Replace('\\', '/').Trim('/');
            string prefix = (wrapper ?? "").Replace('\\', '/').Trim('/');
            if (prefix.Length == 0) prefix = "root";
            string combined = entry.Length == 0 ? prefix : prefix + "/" + entry;
            return directory ? combined + "/" : combined;
        }

        private static void AddZipDirectory(ZipArchive archive, string entry, HashSet<string> created)
        {
            if (archive == null || string.IsNullOrEmpty(entry)) return;
            string normalized = entry.Replace('\\', '/').Trim('/');
            if (normalized.Length == 0) return;
            string[] parts = normalized.Split('/');
            string current = "";
            for (int i = 0; i < parts.Length; i++)
            {
                if (string.IsNullOrEmpty(parts[i])) continue;
                current = current.Length == 0 ? parts[i] : current + "/" + parts[i];
                string directoryEntry = current + "/";
                if (!created.Add(directoryEntry)) continue;
                archive.CreateEntry(directoryEntry);
            }
        }

        private static string SafePathOrEmpty(string root, string relative)
        {
            try { return SafePath(root, relative); }
            catch { return ""; }
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
