using FastVolumeIndex;
using Microsoft.Win32.SafeHandles;
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.IO.Compression;
using System.IO.Hashing;
using System.Linq;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.ComTypes;
using System.Threading;

namespace DesktopIniManager.Services;

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
        try
        {
            attributes = File.GetAttributes(path);
        }
        catch (FileNotFoundException)
        {
            return null;
        }
        catch (DirectoryNotFoundException)
        {
            return null;
        }
        catch (UnauthorizedAccessException)
        {
            return null;
        }
        catch (IOException)
        {
            return null;
        }

        if ((attributes & FileAttributes.Directory) != 0)
            return null;
        try
        {
            var file = new FileInfo(path);
            return new DiffStamp { Size = file.Length, ModifiedUtc = file.LastWriteTimeUtc };
        }
        catch (FileNotFoundException)
        {
            return null;
        }
        catch (DirectoryNotFoundException)
        {
            return null;
        }
        catch (UnauthorizedAccessException)
        {
            return null;
        }
        catch (IOException)
        {
            return null;
        }
    }

    public static bool Same(DiffStamp a, DiffStamp b)
    {
        return Same(a, b, true);
    }

    public static bool Same(DiffStamp a, DiffStamp b, bool compareTimestamp, bool compareSize = true)
    {
        return a == null || b == null
            ? a == b
            : (!compareSize || a.Size == b.Size) && (!compareTimestamp || a.ModifiedUtcSeconds == b.ModifiedUtcSeconds);
    }

    public string Describe()
    {
        return ModifiedUtc.ToLocalTime().ToString("yyyy/MM/dd HH:mm:ss.fffffff") + "\n" + Size.ToString("N0") +
               " bytes";
    }
}

[Flags]
internal enum DiffKind
{
    Same = 1,
    Different = 2,
    SourceOnly = 4,
    TargetOnly = 8,
    Differences = Different | SourceOnly | TargetOnly,
    All = Same | Differences
}

internal sealed class DiffFile : INotifyPropertyChanged
{
    private bool selected;
    public string RelativePath { get; set; }
    public string Name => Path.GetFileName(RelativePath);
    public DiffStamp Source { get; set; }
    public DiffStamp Target { get; set; }
    public bool CompareTimestamp { get; set; } = true;
    public bool CompareSize { get; set; } = true;
    public bool CompareAttributes { get; set; }
    public bool? SourceBom { get; set; }
    public bool? TargetBom { get; set; }

    public bool AttributeUnreadable => CompareAttributes &&
                                       ((Source != null && SourceBom == null) || (Target != null && TargetBom == null));

    public bool AttributeDifferent => CompareAttributes && SourceBom != TargetBom;
    public bool PreciseCompare { get; set; }
    public string SourceHash { get; set; }
    public string TargetHash { get; set; }

    public bool ContentDifferent => PreciseCompare && SourceHash != null && TargetHash != null &&
                                    !string.Equals(SourceHash, TargetHash, StringComparison.Ordinal);

    public DiffKind Kind => Source == null ? DiffKind.TargetOnly :
        Target == null ? DiffKind.SourceOnly :
        DiffStamp.Same(Source, Target, CompareTimestamp, CompareSize) && !ContentDifferent && !AttributeDifferent &&
        !AttributeUnreadable ? DiffKind.Same : DiffKind.Different;

    public bool CanSync => !AttributeUnreadable && Kind != DiffKind.Same;
    public bool CanToggleSelection => CanSync || Selected;

    public string State => AttributeUnreadable ? "ATTR unreadable (BOM)" :
        Kind == DiffKind.Same ? "Same" :
        Source == null ? "Target only" :
        Target == null ? "Source only" :
        AttributeDifferent ? "ATTR differs (BOM)" :
        ContentDifferent ? "Content differs" :
        !CompareTimestamp || Source.ModifiedUtcSeconds == Target.ModifiedUtcSeconds ? "Size differs" :
        CompareSize && Source.Size != Target.Size ? "Time / size differs" : "Time differs";

    public string SourceInfo => Describe(Source, Target);
    public string TargetInfo => Describe(Target, Source);

    public bool Selected
    {
        get => selected;
        set
        {
            if (selected == value) return;
            selected = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs("Selected"));
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs("CanToggleSelection"));
        }
    }

    public event PropertyChangedEventHandler PropertyChanged;

    private string Describe(DiffStamp own, DiffStamp other)
    {
        if (own == null) return "missing";
        if (CompareAttributes && (ReferenceEquals(own, Source) ? SourceBom : TargetBom) == null)
            return "ATTR unreadable (BOM)\n" + own.Describe();
        var state = other == null
            ? ""
            : Kind == DiffKind.Same
                ? "Same\n"
                : CompareTimestamp && own.ModifiedUtcSeconds != other.ModifiedUtcSeconds
                    ? own.ModifiedUtcSeconds > other.ModifiedUtcSeconds ? "NEW\n" : "OLD\n"
                    : AttributeDifferent
                        ? "ATTR differs (BOM)\n"
                        : ContentDifferent
                            ? "Content differs\n"
                            : "Size differs\n";
        return state + own.Describe();
    }
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
    public bool CompareAttributes;
    public bool CompareSize = true;
    public bool CompareTimestamp = true;
    public List<string> DisabledIgnoreRules = new();
    public List<DiffFile> Files = new();
    public HashSet<string> Folders = new(StringComparer.OrdinalIgnoreCase) { "" };
    public List<GitIgnoreRuleInfo> IgnoreRules;
    public bool PreciseCompare;
    public HashSet<string> SourceFolders = new(StringComparer.OrdinalIgnoreCase) { "" };
    public string SourceRoot;
    public HashSet<string> TargetFolders = new(StringComparer.OrdinalIgnoreCase) { "" };
    public string TargetRoot;
}

internal sealed class DiffIndex
{
    public List<string> DisabledIgnoreRules = new();
    public Dictionary<string, DiffStamp> Files;
    public HashSet<string> Folders;
    public List<GitIgnoreRuleInfo> IgnoreRules = new();
    public HashSet<string> NonEmptyFolders;
    public string Root;
}

/// <summary>Compares and synchronizes two development directory trees.</summary>
internal static class DeveloperDifferencerService
{
    private static readonly HashSet<string> IgnoredDirectories = new(StringComparer.OrdinalIgnoreCase)
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
        var root = Path.GetFullPath(path).TrimEnd('\\') + "\\";
        if (Protected(root)) throw new IOException("Choose a root outside .git.");
        if (!Directory.Exists(root)) throw new DirectoryNotFoundException(root);
        CheckComponents(root);
        return root;
    }

    /// <summary>Ensures that the comparison roots are distinct and non-overlapping.</summary>
    public static void ValidateRoots(string source, string target)
    {
        if (source.StartsWith(target, StringComparison.OrdinalIgnoreCase) ||
            target.StartsWith(source, StringComparison.OrdinalIgnoreCase))
            throw new IOException("Roots must not be equal or nested.");
    }

    /// <summary>Resolves a relative file path while enforcing comparison boundaries.</summary>
    public static string SafePath(string root, string relative)
    {
        if (string.IsNullOrWhiteSpace(relative) || Path.IsPathRooted(relative) || relative.Contains(':') ||
            Protected(relative) ||
            relative.Replace('/', '\\').Split('\\').Any(p =>
                p == ".." || p == "." || p.Length == 0 || p.EndsWith(" ") || p.EndsWith(".")))
            throw new IOException("Protected or invalid relative path: " + relative);
        var path = Path.GetFullPath(Path.Combine(root, relative));
        if (!path.StartsWith(root.TrimEnd('\\') + "\\", StringComparison.OrdinalIgnoreCase) || Protected(path))
            throw new IOException("Refused a path outside the root or inside .git.");
        CheckComponents(path);
        return path;
    }

    private static void CheckComponents(string path)
    {
        // Do not follow junctions, symbolic links or other reparse points while
        // resolving a path. This includes existing ancestors of a new file.
        var full = Path.GetFullPath(path);
        var root = Path.GetPathRoot(full);
        if (string.IsNullOrEmpty(root)) throw new IOException("Invalid path: " + path);
        var current = root;
        foreach (var component in full.Substring(root.Length).Split(
                     new[] { Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar },
                     StringSplitOptions.RemoveEmptyEntries))
        {
            current = Path.Combine(current, component);
            FileAttributes attributes;
            try
            {
                attributes = File.GetAttributes(current);
            }
            catch (FileNotFoundException)
            {
                continue;
            }
            catch (DirectoryNotFoundException)
            {
                continue;
            }

            if ((attributes & FileAttributes.ReparsePoint) != 0)
                throw new IOException("Reparse points are excluded: " + current);
        }
    }

    private static Dictionary<string, DiffStamp> ScanSelectedFolder(string root, string relativeFolder,
        HashSet<string> folders, CancellationToken token, IProgress<DiffProgress> progress = null, string stage = null,
        int offset = 0, int total = 0,
        HashSet<string> nonEmptyFolders = null, GitIgnoreMatcher ignore = null)
    {
        var files = new Dictionary<string, DiffStamp>(StringComparer.OrdinalIgnoreCase);
        ignore ??= new GitIgnoreMatcher(root);
        var baseDirectory = relativeFolder.Length == 0
            ? root
            : SafeFolderPath(root, relativeFolder);

        if (!Directory.Exists(baseDirectory)) return files;

        var pending = new Stack<string>();
        pending.Push(baseDirectory);
        var seen = 0;

        while (pending.Count > 0)
        {
            token.ThrowIfCancellationRequested();
            var directory = pending.Pop();
            var directoryRelative = RelativeFromRoot(root, directory);
            if (ignore.IsIgnored(directoryRelative, true)) continue;
            ignore.ReadDirectory(directoryRelative);
            folders.Add(directoryRelative);

            foreach (var entry in VolumePathIndex.EnumerateNativeDirectory(directory, token))
            {
                token.ThrowIfCancellationRequested();
                // Protected entries still count for the physical empty-folder indicator.
                nonEmptyFolders?.Add(directoryRelative);
                var path = Path.Combine(directory, entry.Name);
                var relative = RelativeFromRoot(root, path);
                if (Protected(relative) ||
                    ignore.IsIgnored(relative, (entry.Attributes & FileAttributes.Directory) != 0)) continue;
                if ((entry.Attributes & FileAttributes.Directory) != 0)
                {
                    folders.Add(relative);
                    // Never recurse through a junction or symbolic-link directory.
                    if ((entry.Attributes & FileAttributes.ReparsePoint) == 0)
                        pending.Push(path);
                    continue;
                }

                // Preserve the existing metadata behavior for links/provider-managed files.
                // Ordinary files use the metadata already returned by enumeration.
                var stamp = (entry.Attributes & FileAttributes.ReparsePoint) != 0
                    ? DiffStamp.Read(path)
                    : new DiffStamp { Size = entry.Size, ModifiedUtc = entry.ModifiedUtc };
                if (stamp != null) files[relative] = stamp;
                seen++;
                if (progress != null && stage != null && (seen & 15) == 0)
                    progress.Report(ReportCompare(stage, offset + files.Count, total));
            }
        }

        if (progress != null && stage != null)
            progress.Report(ReportCompare(stage, offset + files.Count, total));
        return files;
    }

    private static DiffProgress ReportCompare(string stage, int completed, int total)
    {
        if (total <= 0)
            return new DiffProgress
            { Stage = stage + "  " + completed.ToString("N0") + " files", Completed = completed, Total = 0 };
        return new DiffProgress
        {
            Stage = stage + "  " + completed.ToString("N0") + " / " + Math.Max(1, total).ToString("N0"),
            Completed = completed,
            Total = Math.Max(1, total)
        };
    }

    private static string SafeFolderPath(string root, string relativeFolder)
    {
        if (string.IsNullOrWhiteSpace(relativeFolder) || Path.IsPathRooted(relativeFolder) ||
            relativeFolder.Contains(':') || Protected(relativeFolder) ||
            relativeFolder.Replace('/', '\\').Split('\\').Any(p =>
                p == ".." || p == "." || p.Length == 0 || p.EndsWith(" ") || p.EndsWith(".")))
            throw new IOException("Protected or invalid relative folder: " + relativeFolder);

        var path = Path.GetFullPath(Path.Combine(root, relativeFolder));
        if (!path.StartsWith(root.TrimEnd('\\') + "\\", StringComparison.OrdinalIgnoreCase) || Protected(path))
            throw new IOException("Refused a folder outside the root or inside .git.");
        CheckComponents(path);
        return path;
    }

    private static string RelativeFromRoot(string root, string path)
    {
        var normalizedRoot = root.TrimEnd('\\') + "\\";
        var full = Path.GetFullPath(path);
        if (string.Equals(full.TrimEnd('\\'), root.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase))
            return string.Empty;
        if (!full.StartsWith(normalizedRoot, StringComparison.OrdinalIgnoreCase))
            throw new IOException("Path is outside the comparison root: " + full);
        return full.Substring(normalizedRoot.Length);
    }

    /// <summary>Compares all eligible files beneath two development roots.</summary>
    public static DiffSnapshot Compare(string source, string target, IProgress<DiffProgress> progress = null,
        bool compareTimestamp = true, CancellationToken token = default)
    {
        token.ThrowIfCancellationRequested();
        source = Root(source);
        target = Root(target);
        ValidateRoots(source, target);
        var result = new DiffSnapshot { SourceRoot = source, TargetRoot = target, CompareTimestamp = compareTimestamp };

        // Scan each side once; the total becomes known after collecting its metadata.
        var total = 0;

        progress?.Report(ReportCompare("Comparing source\u2026", 0, total));
        var sourceFolders = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "" };
        var targetFolders = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "" };
        var left = ScanSelectedFolder(source, string.Empty, sourceFolders, token, progress, "Comparing source\u2026", 0,
            total);

        progress?.Report(ReportCompare("Comparing target\u2026", left.Count, total));
        var right = ScanSelectedFolder(target, string.Empty, targetFolders, token, progress, "Comparing target\u2026",
            left.Count, total);
        ExcludeIgnored(source, target, left, right, sourceFolders, targetFolders, token);
        result.SourceFolders = sourceFolders;
        result.TargetFolders = targetFolders;
        result.Folders = new HashSet<string>(sourceFolders.Union(targetFolders, StringComparer.OrdinalIgnoreCase),
            StringComparer.OrdinalIgnoreCase);

        total = Math.Max(1, left.Count + right.Count);
        progress?.Report(ReportCompare("Classifying differences\u2026", total, total));
        result.Files = Classify(left, right, true, compareTimestamp, token, progress, total);
        return result;
    }

    internal static DiffIndex BuildIndex(string root, CancellationToken token,
        IEnumerable<string> disabledIgnoreRules = null)
    {
        token.ThrowIfCancellationRequested();
        root = Root(root);
        var folders = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "" };
        var nonEmptyFolders = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var disabled = (disabledIgnoreRules ?? Array.Empty<string>()).ToList();
        var ignore = new GitIgnoreMatcher(root, disabled);
        var files = ScanSelectedFolder(root, string.Empty, folders, token, nonEmptyFolders: nonEmptyFolders,
            ignore: ignore);
        return new DiffIndex
        {
            Root = root,
            Files = files,
            Folders = folders,
            NonEmptyFolders = nonEmptyFolders,
            IgnoreRules = ignore.Entries,
            DisabledIgnoreRules = disabled
        };
    }

    internal static DiffSnapshot Compare(DiffIndex source, DiffIndex target,
        IProgress<DiffProgress> progress, bool compareTimestamp, CancellationToken token,
        Action<IReadOnlyList<DiffFile>> onBatch = null, Comparison<string> pathComparison = null,
        bool preciseCompare = false, bool compareSize = true, bool compareAttributes = false)
    {
        token.ThrowIfCancellationRequested();
        ValidateRoots(Root(source.Root), Root(target.Root));
        // Filter copies: cached indexes may also be used with another partner root.
        source = CopyIndex(source);
        target = CopyIndex(target);
        var disabled = source.DisabledIgnoreRules.Union(target.DisabledIgnoreRules, StringComparer.Ordinal).ToList();
        ExcludeIgnored(source.Root, target.Root, source.Files, target.Files, source.Folders, target.Folders, token,
            disabled);
        var total = Math.Max(1, source.Files.Count + target.Files.Count);
        progress?.Report(ReportCompare("Classifying differences\u2026", 0, total));
        return new DiffSnapshot
        {
            SourceRoot = source.Root,
            TargetRoot = target.Root,
            IgnoreRules = GitIgnoreMatcher.Merge(source.IgnoreRules, target.IgnoreRules),
            DisabledIgnoreRules = disabled,
            CompareTimestamp = compareTimestamp,
            PreciseCompare = preciseCompare,
            CompareSize = compareSize,
            CompareAttributes = compareAttributes,
            SourceFolders = new HashSet<string>(source.Folders, StringComparer.OrdinalIgnoreCase),
            TargetFolders = new HashSet<string>(target.Folders, StringComparer.OrdinalIgnoreCase),
            Folders = new HashSet<string>(source.Folders.Union(target.Folders), StringComparer.OrdinalIgnoreCase),
            Files = Classify(source.Files, target.Files, true, compareTimestamp, token, progress, total, onBatch,
                pathComparison, source.Root, target.Root, preciseCompare, compareSize, compareAttributes)
        };
    }

    /// <summary>Compares only the requested folder and its immediate child folders.</summary>
    public static DiffSnapshot CompareFolderLevel(string source, string target, string relativeFolder,
        bool compareTimestamp = true, CancellationToken token = default)
    {
        token.ThrowIfCancellationRequested();
        source = Root(source);
        target = Root(target);
        ValidateRoots(source, target);
        relativeFolder = (relativeFolder ?? string.Empty).Trim('\\', '/');

        var result = new DiffSnapshot { SourceRoot = source, TargetRoot = target, CompareTimestamp = compareTimestamp };
        var left = new Dictionary<string, DiffStamp>(StringComparer.OrdinalIgnoreCase);
        var right = new Dictionary<string, DiffStamp>(StringComparer.OrdinalIgnoreCase);
        ScanFolderLevel(source, relativeFolder, result.SourceFolders, left, token);
        ScanFolderLevel(target, relativeFolder, result.TargetFolders, right, token);
        ExcludeIgnored(source, target, left, right, result.SourceFolders, result.TargetFolders, token);
        result.Folders =
            new HashSet<string>(result.SourceFolders.Union(result.TargetFolders, StringComparer.OrdinalIgnoreCase),
                StringComparer.OrdinalIgnoreCase);
        result.Files = Classify(left, right, true, compareTimestamp, token);
        return result;
    }

    private static DiffIndex CopyIndex(DiffIndex index)
    {
        return new DiffIndex
        {
            Root = index.Root,
            Files = new Dictionary<string, DiffStamp>(index.Files, StringComparer.OrdinalIgnoreCase),
            Folders = new HashSet<string>(index.Folders, StringComparer.OrdinalIgnoreCase),
            NonEmptyFolders = index.NonEmptyFolders,
            IgnoreRules = index.IgnoreRules,
            DisabledIgnoreRules = index.DisabledIgnoreRules
        };
    }

    private static void ExcludeIgnored(string source, string target, Dictionary<string, DiffStamp> left,
        Dictionary<string, DiffStamp> right, HashSet<string> sourceFolders, HashSet<string> targetFolders,
        CancellationToken token,
        IEnumerable<string> disabled = null)
    {
        var a = new GitIgnoreMatcher(source, disabled);
        var b = new GitIgnoreMatcher(target, disabled);
        foreach (var path in left.Keys.Union(right.Keys, StringComparer.OrdinalIgnoreCase).ToArray())
        {
            token.ThrowIfCancellationRequested();
            if (a.IsIgnored(path, false) || b.IsIgnored(path, false))
            {
                left.Remove(path);
                right.Remove(path);
            }
        }

        foreach (var path in sourceFolders.Union(targetFolders, StringComparer.OrdinalIgnoreCase).ToArray())
        {
            token.ThrowIfCancellationRequested();
            if (a.IsIgnored(path, true) || b.IsIgnored(path, true))
            {
                sourceFolders.Remove(path);
                targetFolders.Remove(path);
            }
        }
    }

    private static void ScanFolderLevel(string root, string relativeFolder, HashSet<string> folders,
        Dictionary<string, DiffStamp> files, CancellationToken token)
    {
        var directory = relativeFolder.Length == 0 ? root : SafeFolderPath(root, relativeFolder);
        if (!Directory.Exists(directory)) return;
        folders.Add(relativeFolder);

        foreach (var entry in VolumePathIndex.EnumerateNativeDirectory(directory, token))
        {
            token.ThrowIfCancellationRequested();
            var path = Path.Combine(directory, entry.Name);
            var relative = RelativeFromRoot(root, path);
            if (Protected(relative)) continue;
            if ((entry.Attributes & FileAttributes.Directory) != 0)
            {
                folders.Add(relative);
                continue;
            }

            var stamp = (entry.Attributes & FileAttributes.ReparsePoint) != 0
                ? DiffStamp.Read(path)
                : new DiffStamp { Size = entry.Size, ModifiedUtc = entry.ModifiedUtc };
            if (stamp != null) files[relative] = stamp;
        }
    }

    internal static List<DiffFile> Classify(Dictionary<string, DiffStamp> left, Dictionary<string, DiffStamp> right,
        bool includeSame = false, bool compareTimestamp = true, CancellationToken token = default,
        IProgress<DiffProgress> progress = null, int offset = 0,
        Action<IReadOnlyList<DiffFile>> onBatch = null, Comparison<string> pathComparison = null,
        string sourceRoot = null, string targetRoot = null, bool preciseCompare = false, bool compareSize = true,
        bool compareAttributes = false)
    {
        var files = new List<DiffFile>();
        var batch = onBatch == null ? null : new List<DiffFile>(128);
        var sourceValidatedFolders = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var targetValidatedFolders = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var paths = left.Keys.Union(right.Keys, StringComparer.OrdinalIgnoreCase).ToArray();
        if (pathComparison != null) Array.Sort(paths, pathComparison);
        else Array.Sort(paths, StringComparer.OrdinalIgnoreCase);
        for (var index = 0; index < paths.Length; index++)
        {
            token.ThrowIfCancellationRequested();
            var path = paths[index];
            if (progress != null && index % 128 == 0)
                progress.Report(ReportCompare("Classifying differences\u2026", index, paths.Length));
            if (Protected(path)) continue;
            DiffStamp a, b;
            left.TryGetValue(path, out a);
            right.TryGetValue(path, out b);
            string sourceHash = null, targetHash = null;
            var sourceBom = compareAttributes && a != null
                ? HasBom(SafeAttributePath(sourceRoot, path, sourceValidatedFolders))
                : false;

            var targetBom = compareAttributes && b != null
                ? HasBom(SafeAttributePath(targetRoot, path, targetValidatedFolders))
                : false;
            if (ShouldHash(a, b, compareTimestamp, compareSize, compareAttributes, sourceBom, targetBom,
                    preciseCompare))
            {
                sourceHash = HashFile(SafePath(sourceRoot, path), token);
                targetHash = HashFile(SafePath(targetRoot, path), token);
                if (sourceHash == null || targetHash == null)
                {
                    progress?.Report(ReportCompare("Skipped changed file: " + path, index + 1, paths.Length));
                    continue;
                }
            }

            if (preciseCompare)
                // Files may disappear or change between enumeration and hashing
                // (desktop.ini is a common example). Never interpret a vanished
                // source as an instruction to delete its destination.
                // Exclude this stale entry; a fresh comparison will pick it up.
                if (!DiffStamp.Same(a, a == null ? null : DiffStamp.Read(SafePath(sourceRoot, path)), true) ||
                    !DiffStamp.Same(b, b == null ? null : DiffStamp.Read(SafePath(targetRoot, path)), true))
                {
                    progress?.Report(ReportCompare("Skipped changed file: " + path, index + 1, paths.Length));
                    continue;
                }

            if (includeSame || !DiffStamp.Same(a, b, compareTimestamp, compareSize) ||
                (preciseCompare && sourceHash != targetHash) || sourceBom != targetBom || sourceBom == null ||
                targetBom == null)
            {
                var file = new DiffFile
                {
                    RelativePath = path,
                    Source = a,
                    Target = b,
                    CompareTimestamp = compareTimestamp,
                    PreciseCompare = preciseCompare,
                    CompareSize = compareSize,
                    SourceHash = sourceHash,
                    TargetHash = targetHash,
                    CompareAttributes = compareAttributes,
                    SourceBom = sourceBom,
                    TargetBom = targetBom
                };
                files.Add(file);
                batch?.Add(file);
                if (batch?.Count >= 128)
                {
                    onBatch(batch.ToArray());
                    batch.Clear();
                }
            }
        }

        if (batch?.Count > 0) onBatch(batch.ToArray());
        progress?.Report(ReportCompare("Classifying differences\u2026", Math.Max(1, paths.Length),
            Math.Max(1, paths.Length)));
        return files;
    }

    internal static bool ShouldHash(DiffStamp source, DiffStamp target, bool compareTimestamp, bool compareSize,
        bool compareAttributes, bool? sourceBom, bool? targetBom, bool preciseCompare)
    {
        return preciseCompare && source != null && target != null &&
               DiffStamp.Same(source, target, compareTimestamp, compareSize) &&
               (!compareAttributes || (sourceBom.HasValue && targetBom.HasValue && sourceBom == targetBom));
    }

    private static string SafeAttributePath(
        string root,
        string relative,
        HashSet<string> validatedFolders)
    {
        var folder = Path.GetDirectoryName(relative) ?? "";

        if (!validatedFolders.Contains(folder))
        {
            // 親階層の検証は初回だけ行う。
            // 実体のないファイルはHasBom側で読取り不可と判定する。
            string validated;

            try
            {
                validated = SafePath(root, relative);
            }
            catch (FileNotFoundException)
            {
                validated = Path.GetFullPath(Path.Combine(root, relative));
            }
            catch (DirectoryNotFoundException)
            {
                validated = Path.GetFullPath(Path.Combine(root, relative));
            }

            validatedFolders.Add(folder);
            return validated;
        }

        var path = Path.GetFullPath(Path.Combine(root, relative));

        try
        {
            var attributes = File.GetAttributes(path);

            if ((attributes & FileAttributes.ReparsePoint) != 0)
                throw new IOException("Reparse points are excluded: " + path);
        }
        catch (FileNotFoundException)
        {
            // HasBomで読取り不可として扱う。
        }
        catch (DirectoryNotFoundException)
        {
            // HasBomで読取り不可として扱う。
        }

        return path;
    }

    internal static bool? HasBom(string path)
    {
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 1);
            Span<byte> prefix = stackalloc byte[4];
            var count = stream.ReadAtLeast(prefix, 4, false);
            return (count >= 2 &&
                    ((prefix[0] == 0xFF && prefix[1] == 0xFE) || (prefix[0] == 0xFE && prefix[1] == 0xFF))) ||
                   (count >= 3 && prefix[0] == 0xEF && prefix[1] == 0xBB && prefix[2] == 0xBF) ||
                   (count == 4 && prefix[0] == 0 && prefix[1] == 0 && prefix[2] == 0xFE && prefix[3] == 0xFF);
        }
        catch (IOException)
        {
            return null;
        }
        catch (UnauthorizedAccessException)
        {
            return null;
        }
    }

    internal static string HashFile(string path, CancellationToken token = default)
    {
        if (string.IsNullOrEmpty(path) || !File.Exists(path)) return null;
        var xxh128 = new XxHash128();
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 131072);
        var buffer = new byte[131072];
        int count;
        while ((count = stream.Read(buffer, 0, buffer.Length)) > 0)
        {
            token.ThrowIfCancellationRequested();
            xxh128.Append(buffer.AsSpan(0, count));
        }

        return Convert.ToHexString(xxh128.GetCurrentHash());
    }

    private static bool MatchesSnapshot(string path, DiffStamp stamp, string hash, bool compareTimestamp)
    {
        // Snapshot validation is independent of the user's comparison filters.
        // Even without a stored hash, reject size/time changes before synchronization.
        if (!DiffStamp.Same(stamp, DiffStamp.Read(path), true)) return false;
        return hash == null || string.Equals(hash, HashFile(path), StringComparison.Ordinal);
    }

    /// <summary>Returns the synchronization operation required for a difference.</summary>
    public static string Operation(DiffFile file, bool toTarget)
    {
        DiffStamp from = toTarget ? file.Source : file.Target, to = toTarget ? file.Target : file.Source;
        return from == null ? "Delete" : to == null ? "Copy" : "Overwrite";
    }

    /// <summary>Synchronizes selected differences and returns an operation log.</summary>
    public static List<string> Synchronize(DiffSnapshot snapshot, IEnumerable<DiffFile> selected, bool toTarget,
        Action<string> onLog = null, IEnumerable<DiffFolderSync> selectedFolders = null)
    {
        Root(snapshot.SourceRoot);
        Root(snapshot.TargetRoot);
        ValidateRoots(snapshot.SourceRoot, snapshot.TargetRoot);
        var log = new List<string>();
        var fileRetryQueue = new Queue<Action>();
        var folderRetryQueue = new Queue<Action>();
        var sourceIgnore = new GitIgnoreMatcher(snapshot.SourceRoot, snapshot.DisabledIgnoreRules);
        var targetIgnore = new GitIgnoreMatcher(snapshot.TargetRoot, snapshot.DisabledIgnoreRules);
        Action<int, string> writeLog = (index, line) =>
        {
            log[index] = line;
            onLog?.Invoke(line);
        };
        foreach (var file in selected)
        {
            if (sourceIgnore.IsIgnored(file.RelativePath, false) || targetIgnore.IsIgnored(file.RelativePath, false))
            {
                var skipped = "SKIP .gitignore: " + file.RelativePath;
                log.Add(skipped);
                onLog?.Invoke(skipped);
                continue;
            }

            if (file.AttributeUnreadable)
            {
                var failure = "FAIL ATTR unreadable (BOM): " + file.RelativePath;
                log.Add(failure);
                onLog?.Invoke(failure);
                continue;
            }

            var logIndex = log.Count;
            log.Add(null);
            var destinationCommitted = false;
            ExecuteFile(false);

            void ExecuteFile(bool retry)
            {
                var operation = Operation(file, toTarget);
                string left = null;
                string right = null;
                string from = null;
                string to = null;
                try
                {
                    left = SafePath(snapshot.SourceRoot, file.RelativePath);
                    right = SafePath(snapshot.TargetRoot, file.RelativePath);
                    from = toTarget ? left : right;
                    to = toTarget ? right : left;
                    if (File.Exists(from)) RejectHardLinks(from);
                    if (File.Exists(to)) RejectHardLinks(to);
                    // A failure after replacement/deletion may already have produced the
                    // desired state. Verify it without overwriting a newly changed file.
                    if (retry && destinationCommitted
                              && MatchesSnapshot(from, toTarget ? file.Source : file.Target,
                                  toTarget ? file.SourceHash : file.TargetHash, snapshot.CompareTimestamp)
                              && MatchesSnapshot(to, toTarget ? file.Source : file.Target,
                                  toTarget ? file.SourceHash : file.TargetHash, snapshot.CompareTimestamp))
                    {
                        writeLog(logIndex, "OK " + operation + " " + file.RelativePath);
                        return;
                    }

                    if (!MatchesSnapshot(left, file.Source, file.SourceHash, snapshot.CompareTimestamp) ||
                        !MatchesSnapshot(right, file.Target, file.TargetHash, snapshot.CompareTimestamp))
                        throw new IOException("Changed after compare. Compare again.");
                    if (operation == "Delete")
                    {
                        File.Delete(to);
                        destinationCommitted = true;
                    }
                    else
                    {
                        Directory.CreateDirectory(Path.GetDirectoryName(to));
                        var destinationRoot = toTarget ? snapshot.TargetRoot : snapshot.SourceRoot;
                        var temporaryRelative = Path.Combine(Path.GetDirectoryName(file.RelativePath) ?? "",
                            ".dim-sync-" + Guid.NewGuid().ToString("N") + ".tmp");
                        var temporary = SafePath(destinationRoot, temporaryRelative);
                        try
                        {
                            File.Copy(from, temporary, false);
                            File.SetAttributes(temporary, File.GetAttributes(temporary) & ~FileAttributes.ReadOnly);
                            File.SetLastWriteTimeUtc(temporary, (toTarget ? file.Source : file.Target).ModifiedUtc);
                            SafePath(snapshot.SourceRoot, file.RelativePath);
                            SafePath(snapshot.TargetRoot, file.RelativePath);
                            if (!MatchesSnapshot(left, file.Source, file.SourceHash, snapshot.CompareTimestamp) ||
                                !MatchesSnapshot(right, file.Target, file.TargetHash, snapshot.CompareTimestamp))
                                throw new IOException("Changed during copy. Compare again.");
                            if (File.Exists(to))
                            {
                                RejectHardLinks(to);
                                File.Replace(temporary, to, null);
                            }
                            else
                            {
                                File.Move(temporary, to);
                            }

                            destinationCommitted = true;
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

                    SafePath(snapshot.SourceRoot, file.RelativePath);
                    SafePath(snapshot.TargetRoot, file.RelativePath);
                    if (!MatchesSnapshot(to, toTarget ? file.Source : file.Target,
                            toTarget ? file.SourceHash : file.TargetHash, snapshot.CompareTimestamp))
                        throw new IOException(
                            "Synchronization verification failed: destination timestamp, size or existence differs. Compare again.");
                    writeLog(logIndex, "OK " + operation + " " + file.RelativePath);
                }
                catch (Exception ex)
                {
                    if (!retry)
                    {
                        fileRetryQueue.Enqueue(() => ExecuteFile(true));
                        onLog?.Invoke("QUEUED " + operation + " " + file.RelativePath + " : " +
                                      ErrorMessages.English(ex));
                        return;
                    }

                    // Do not classify every "access denied" as a lock.
                    // Probe the actual source/destination file and report LOCKED only when
                    // Windows refuses an exclusive open because another process is using it.
                    var locked = IsFileLocked(to) || IsFileLocked(from);

                    if (locked)
                        writeLog(logIndex,
                            "LOCKED " + operation + " " + file.RelativePath + " : " + ErrorMessages.English(ex));
                    else
                        writeLog(logIndex,
                            "FAIL " + operation + " " + file.RelativePath + " : " + ErrorMessages.English(ex));
                }
            }
        }

        // Complete file retries before directory deletion, so successfully retried
        // deletions can leave one-sided directories empty.
        if (fileRetryQueue.Count > 0) onLog?.Invoke("RETRY " + fileRetryQueue.Count + " queued file operation(s)");
        while (fileRetryQueue.Count > 0) fileRetryQueue.Dequeue()();

        // Directory differences are applied after file operations. This lets file deletions
        // empty one-sided directories before Directory.Delete(false), while file copies already
        // create their parent directories. Empty directories are therefore handled here.
        var folderOperations = (selectedFolders ?? Enumerable.Empty<DiffFolderSync>())
            .Where(f => f != null && !string.IsNullOrWhiteSpace(f.RelativePath) && f.SourceExists != f.TargetExists)
            .OrderBy(f =>
                (toTarget ? f.SourceExists : f.TargetExists)
                    ? FolderDepth(f.RelativePath)
                    : -FolderDepth(f.RelativePath))
            .ToArray();

        foreach (var folder in folderOperations)
        {
            if (sourceIgnore.IsIgnored(folder.RelativePath, true) || targetIgnore.IsIgnored(folder.RelativePath, true))
            {
                var skipped = "SKIP .gitignore: " + folder.RelativePath;
                log.Add(skipped);
                onLog?.Invoke(skipped);
                continue;
            }

            var logIndex = log.Count;
            log.Add(null);
            var folderCommitted = false;
            ExecuteFolder(false);

            void ExecuteFolder(bool retry)
            {
                var fromExists = toTarget ? folder.SourceExists : folder.TargetExists;
                var operation = fromExists ? "CreateDir" : "DeleteDir";
                try
                {
                    var leftFolder = SafeFolderPath(snapshot.SourceRoot, folder.RelativePath);
                    var rightFolder = SafeFolderPath(snapshot.TargetRoot, folder.RelativePath);
                    var origin = toTarget ? leftFolder : rightFolder;
                    if (Directory.Exists(origin) != fromExists)
                        throw new IOException("Folder changed after compare. Compare again.");

                    var destination = toTarget ? rightFolder : leftFolder;
                    var destinationExists = Directory.Exists(destination);
                    var expectedDestinationExists = toTarget ? folder.TargetExists : folder.SourceExists;
                    // A successful first attempt can be followed by a verification
                    // failure. On retry, accept the already-completed state only if
                    // the origin still matches the snapshot.
                    if (retry && folderCommitted && destinationExists == fromExists)
                    {
                        writeLog(logIndex, "OK " + operation + " " + folder.RelativePath);
                        return;
                    }

                    if (destinationExists != expectedDestinationExists)
                        throw new IOException("Destination folder changed after compare. Compare again.");
                    if (fromExists) Directory.CreateDirectory(destination);
                    else if (destinationExists) Directory.Delete(destination, false);
                    folderCommitted = true;

                    if (Directory.Exists(destination) != fromExists)
                        throw new IOException("Folder synchronization verification failed. Compare again.");
                    writeLog(logIndex, "OK " + operation + " " + folder.RelativePath);
                }
                catch (Exception ex)
                {
                    if (!retry)
                    {
                        folderRetryQueue.Enqueue(() => ExecuteFolder(true));
                        onLog?.Invoke("QUEUED " + operation + " " + folder.RelativePath + " : " +
                                      ErrorMessages.English(ex));
                        return;
                    }

                    writeLog(logIndex,
                        "FAIL " + operation + " " + folder.RelativePath + " : " + ErrorMessages.English(ex));
                }
            }
        }

        // Retry directory operations once after the normal directory pass.
        if (folderRetryQueue.Count > 0)
            onLog?.Invoke("RETRY " + folderRetryQueue.Count + " queued folder operation(s)");
        while (folderRetryQueue.Count > 0) folderRetryQueue.Dequeue()();
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
            var code = ex.HResult & 0xFFFF;
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
        var name = (fileName ?? "").Trim();
        if (name.Length == 0)
            throw new IOException("ZIP file name is empty.");
        if (name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
            throw new IOException("ZIP file name contains invalid characters: " + name);
        if (!name.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
            name += ".zip";
        var directory = Path.GetFullPath(folder.Trim());
        Directory.CreateDirectory(directory);
        return Path.Combine(directory, name);
    }

    /// <summary>Packs selected files and folders from one comparison side into a ZIP while keeping relative paths.</summary>
    public static List<string> PackZip(DiffSnapshot snapshot, IEnumerable<DiffFile> selected,
        IEnumerable<DiffFolderSync> selectedFolders, bool fromSource, string zipPath, Action<string> onLog = null)
    {
        if (snapshot == null) throw new ArgumentNullException(nameof(snapshot));
        var root = fromSource ? snapshot.SourceRoot : snapshot.TargetRoot;
        Root(root);
        var log = new List<string>();
        Action<string> writeLog = line =>
        {
            log.Add(line);
            onLog?.Invoke(line);
        };
        var files = new Dictionary<string, DiffFile>(StringComparer.OrdinalIgnoreCase);
        foreach (var file in selected ?? Enumerable.Empty<DiffFile>())
        {
            if (file == null || string.IsNullOrEmpty(file.RelativePath)) continue;
            var stamp = fromSource ? file.Source : file.Target;
            if (stamp == null) continue;
            files[file.RelativePath] = file;
        }

        var folders = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var folder in selectedFolders ?? Enumerable.Empty<DiffFolderSync>())
        {
            if (folder == null || string.IsNullOrEmpty(folder.RelativePath)) continue;
            var exists = fromSource ? folder.SourceExists : folder.TargetExists;
            if (!exists) continue;
            folders.Add(folder.RelativePath.Replace('/', '\\'));
        }

        // Expand selected folders in one pass. The previous implementation scanned the
        // complete snapshot once for every selected folder (O(folders * files)).
        if (folders.Count > 0)
        {
            var prefixes = folders
                .Select(folder => folder.TrimEnd('\\') + "\\")
                .ToArray();
            foreach (var file in snapshot.Files ?? Enumerable.Empty<DiffFile>())
            {
                if (file == null || string.IsNullOrEmpty(file.RelativePath)) continue;
                var stamp = fromSource ? file.Source : file.Target;
                if (stamp == null) continue;
                var relative = file.RelativePath.Replace('/', '\\');
                for (var i = 0; i < prefixes.Length; i++)
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

        var directory = Path.GetDirectoryName(zipPath);
        if (!string.IsNullOrEmpty(directory))
            Directory.CreateDirectory(directory);
        var tempZipPath = Path.Combine(
            directory ?? Path.GetDirectoryName(Path.GetFullPath(zipPath)) ?? Environment.CurrentDirectory,
            "." + Path.GetFileName(zipPath) + "." + Guid.NewGuid().ToString("N") + ".tmp");
        var wrapper = ZipRootFolderName(root);
        var entryFailed = false;
        try
        {
            using (var archive = ZipFile.Open(tempZipPath, ZipArchiveMode.Create))
            {
                var directoryEntries = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                AddZipDirectory(archive, wrapper, directoryEntries);
                foreach (var folder in folders.OrderBy(path => path, StringComparer.OrdinalIgnoreCase))
                    try
                    {
                        AddZipDirectory(archive, ZipEntryName(wrapper, folder, true), directoryEntries);
                        writeLog("OK DIR " + folder);
                    }
                    catch (Exception ex)
                    {
                        entryFailed = true;
                        writeLog("FAIL DIR " + folder + " " + ErrorMessages.English(ex));
                    }

                foreach (var file in files.Values.OrderBy(item => item.RelativePath, StringComparer.OrdinalIgnoreCase))
                    try
                    {
                        var from = SafePath(root, file.RelativePath);
                        if (!File.Exists(from))
                        {
                            entryFailed = true;
                            writeLog("FAIL missing " + file.RelativePath);
                            continue;
                        }

                        RejectHardLinks(from);
                        var parent = Path.GetDirectoryName(file.RelativePath.Replace('/', '\\'));
                        if (!string.IsNullOrEmpty(parent))
                            AddZipDirectory(archive, ZipEntryName(wrapper, parent, true), directoryEntries);
                        archive.CreateEntryFromFile(from, ZipEntryName(wrapper, file.RelativePath, false),
                            CompressionLevel.Fastest);
                        writeLog("OK ZIP " + file.RelativePath);
                    }
                    catch (Exception ex)
                    {
                        entryFailed = true;
                        if (IsFileLocked(SafePathOrEmpty(root, file.RelativePath)))
                            writeLog("LOCKED " + file.RelativePath);
                        else
                            writeLog("FAIL ZIP " + file.RelativePath + " " + ErrorMessages.English(ex));
                    }
            }

            if (entryFailed)
            {
                // Never replace an existing archive with an incomplete ZIP.
                File.Delete(tempZipPath);
                writeLog("FAIL archive incomplete; existing archive preserved: " + zipPath);
            }
            else
            {
                File.Move(tempZipPath, zipPath, true);
                writeLog("OK archive " + zipPath);
            }
        }
        catch (Exception ex)
        {
            writeLog("FAIL archive " + ErrorMessages.English(ex));
            try
            {
                if (File.Exists(tempZipPath)) File.Delete(tempZipPath);
            }
            catch
            {
            }
        }

        return log;
    }

    private static bool IsUnder(string relativePath, string folder)
    {
        if (string.IsNullOrEmpty(relativePath) || string.IsNullOrEmpty(folder)) return false;
        var path = relativePath.Replace('/', '\\');
        var prefix = folder.Replace('/', '\\').TrimEnd('\\') + "\\";
        return path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase);
    }

    private static string ZipRootFolderName(string root)
    {
        var name = Path.GetFileName((root ?? "").TrimEnd('\\', '/'));
        if (string.IsNullOrWhiteSpace(name)) name = "root";
        foreach (var invalid in Path.GetInvalidFileNameChars())
            name = name.Replace(invalid, '_');
        return name;
    }

    private static string ZipEntryName(string wrapper, string relative, bool directory)
    {
        var entry = (relative ?? "").Replace('\\', '/').Trim('/');
        var prefix = (wrapper ?? "").Replace('\\', '/').Trim('/');
        if (prefix.Length == 0) prefix = "root";
        var combined = entry.Length == 0 ? prefix : prefix + "/" + entry;
        return directory ? combined + "/" : combined;
    }

    private static void AddZipDirectory(ZipArchive archive, string entry, HashSet<string> created)
    {
        if (archive == null || string.IsNullOrEmpty(entry)) return;
        var normalized = entry.Replace('\\', '/').Trim('/');
        if (normalized.Length == 0) return;
        var parts = normalized.Split('/');
        var current = "";
        for (var i = 0; i < parts.Length; i++)
        {
            if (string.IsNullOrEmpty(parts[i])) continue;
            current = current.Length == 0 ? parts[i] : current + "/" + parts[i];
            var directoryEntry = current + "/";
            if (!created.Add(directoryEntry)) continue;
            archive.CreateEntry(directoryEntry);
        }
    }

    private static string SafePathOrEmpty(string root, string relative)
    {
        try
        {
            return SafePath(root, relative);
        }
        catch
        {
            return "";
        }
    }

    /// <summary>Rejects files with multiple hard-link references.</summary>
    public static void RejectHardLinks(string path)
    {
        using (var stream =
               new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
        {
            HandleInfo info;
            if (!GetFileInformationByHandle(stream.SafeFileHandle, out info))
                throw new Win32Exception(Marshal.GetLastWin32Error());
            if (info.Links > 1) throw new IOException("Hard links are excluded: " + path);
        }
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GetFileInformationByHandle(SafeFileHandle handle, out HandleInfo info);

    [StructLayout(LayoutKind.Sequential)]
    private struct HandleInfo
    {
        public uint Attributes;
        public FILETIME Creation, Access, Write;
        public uint Volume, SizeHigh, SizeLow, Links, IndexHigh, IndexLow;
    }
}