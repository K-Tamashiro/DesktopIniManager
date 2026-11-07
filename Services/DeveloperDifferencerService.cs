using FastVolumeIndex;
using Microsoft.Win32.SafeHandles;
using System;
using System.Buffers;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics.CodeAnalysis;
using System.IO;
using System.IO.Compression;
using System.IO.Hashing;
using System.Linq;
using System.Runtime.ExceptionServices;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.ComTypes;
using System.Threading;
using System.Threading.Tasks;

namespace DesktopIniManager.Services;

internal sealed class DiffStamp
{
    public long Size { get; set; }

    public DateTime ModifiedUtc { get; set; }

    // Compare whole seconds without rounding; retain the original timestamp for copying.
    public long ModifiedUtcSeconds => ModifiedUtc.Ticks / TimeSpan.TicksPerSecond;

    /// <summary>
    ///     ファイルのサイズと更新日時を読み取ります。フォルダーや存在しない・読み取れないパスは対象外です。
    /// </summary>
    /// <param name="path">ファイルのパス。</param>
    /// <returns>読み取った情報。対象外の場合は null。</returns>
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

    /// <summary>
    ///     サイズと更新日時の両方を比較して、2 つのファイル情報が同じかどうかを判定します。
    /// </summary>
    /// <param name="a">比較するファイル情報。</param>
    /// <param name="b">比較するファイル情報。</param>
    /// <returns>同じ場合、または両方とも null の場合は true。</returns>
    public static bool Same(DiffStamp a, DiffStamp b)
    {
        return Same(a, b, true);
    }

    /// <summary>
    ///     指定した項目 (サイズ、秒単位の更新日時) で、2 つのファイル情報が同じかどうかを判定します。
    /// </summary>
    /// <param name="a">比較するファイル情報。</param>
    /// <param name="b">比較するファイル情報。</param>
    /// <param name="compareTimestamp">更新日時を比較する場合は true。</param>
    /// <param name="compareSize">サイズを比較する場合は true。</param>
    /// <returns>同じ場合、または両方とも null の場合は true。</returns>
    public static bool Same(DiffStamp a, DiffStamp b, bool compareTimestamp, bool compareSize = true)
    {
        return a == null || b == null
            ? a == b
            : (!compareSize || a.Size == b.Size) && (!compareTimestamp || a.ModifiedUtcSeconds == b.ModifiedUtcSeconds);
    }

    /// <summary>
    ///     更新日時 (ローカル時刻) とサイズを表示用の文字列にします。
    /// </summary>
    /// <returns>表示用の文字列。</returns>
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
    private bool _selected;
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
        get => _selected;
        set
        {
            if (_selected == value) return;
            _selected = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs("Selected"));
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs("CanToggleSelection"));
        }
    }

    public event PropertyChangedEventHandler PropertyChanged;

    /// <summary>
    ///     一方のファイル情報を、相手と比べた状態 (NEW/OLD、属性の違い、内容の違いなど) 付きで表示用の文字列にします。
    /// </summary>
    /// <param name="own">表示する側のファイル情報。</param>
    /// <param name="other">比較相手のファイル情報。</param>
    /// <returns>表示用の文字列。ファイルがない場合は "missing"。</returns>
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

    private static readonly HashSet<string>.AlternateLookup<ReadOnlySpan<char>> IgnoredDirectoryLookup =
        IgnoredDirectories.GetAlternateLookup<ReadOnlySpan<char>>();

    /// <summary>Returns whether a path enters a protected metadata directory.</summary>
    public static bool Protected(string path)
    {
        // Allocation-free: this runs for every enumerated and classified path.
        var rest = path.AsSpan();
        while (true)
        {
            var separator = rest.IndexOfAny('\\', '/');
            var segment = (separator < 0 ? rest : rest[..separator]).TrimEnd(" .");
            if (IgnoredDirectoryLookup.Contains(segment)) return true;
            if (separator < 0) return false;
            rest = rest[(separator + 1)..];
        }
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
    [return: NotNull]
    public static string SafePath(string root, string relative)
    {
        ValidateRelative(relative);
        var path = Path.GetFullPath(Path.Combine(root, relative));
        if (!path.StartsWith(root.TrimEnd('\\') + "\\", StringComparison.OrdinalIgnoreCase) || Protected(path))
            throw new IOException("Refused a path outside the root or inside .git.");
        CheckComponents(path);
        return path;
    }

    /// <summary>
    ///     相対パスが、絶対パス・ドライブ指定・保護フォルダー・「.」「..」などを含まない安全なパスかどうかを検証します。
    /// </summary>
    /// <param name="relative">検証する相対パス。</param>
    private static void ValidateRelative(string relative)
    {
        if (string.IsNullOrWhiteSpace(relative) || Path.IsPathRooted(relative) || relative.Contains(':') ||
            Protected(relative) ||
            relative.Replace('/', '\\').Split('\\').Any(p =>
                p == ".." || p == "." || p.Length == 0 || p.EndsWith(" ") || p.EndsWith(".")))
            throw new IOException("Protected or invalid relative path: " + relative);
    }

    /// <summary>
    ///     パスの各要素をたどり、ジャンクションやシンボリック リンクなどのリパース ポイントを経由していないことを確認します。新しいファイルの既存の親フォルダーも対象です。
    /// </summary>
    /// <param name="path">確認するパス。</param>
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

    /// <summary>
    ///     指定フォルダー配下を走査し、.gitignore と保護フォルダーを除いたファイルのサイズと更新日時を集めます。
    /// </summary>
    /// <param name="root">比較ルート。</param>
    /// <param name="relativeFolder">走査するフォルダーの、比較ルートからの相対パス。空文字列の場合はルート全体。</param>
    /// <param name="folders">見つかったフォルダーの相対パスの追加先。</param>
    /// <param name="token">キャンセル トークン。</param>
    /// <param name="progress">進捗の通知先。</param>
    /// <param name="stage">進捗に表示する段階名。</param>
    /// <param name="offset">進捗の件数に加算する値。</param>
    /// <param name="total">進捗の総件数。</param>
    /// <param name="nonEmptyFolders">ファイルを含むフォルダーの相対パスの追加先。</param>
    /// <param name="ignore">.gitignore のマッチャー。null の場合は新しく作成します。</param>
    /// <returns>相対パスをキーとするファイル情報。</returns>
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
                var relative = ChildRelative(root, directoryRelative, path, entry.Name);
                // Ancestors were checked before this directory was queued; only the new segment can be protected.
                if (IsProtectedName(entry.Name) ||
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

    /// <summary>
    ///     比較の進捗情報を作成します。総件数が不明な場合は処理済みの件数だけを表示します。
    /// </summary>
    /// <param name="stage">段階名。</param>
    /// <param name="completed">処理済みの件数。</param>
    /// <param name="total">総件数。0 以下の場合は不明。</param>
    /// <returns>進捗情報。</returns>
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

    /// <summary>
    ///     比較ルートと相対フォルダーから絶対パスを作成し、ルート外・.git 配下・リパース ポイント経由でないことを検証します。
    /// </summary>
    /// <param name="root">比較ルート。</param>
    /// <param name="relativeFolder">比較ルートからの相対パス。</param>
    /// <returns>検証済みの絶対パス。</returns>
    [return: NotNull]
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

    /// <summary>
    ///     名前が比較対象外の保護フォルダー名 (末尾の空白とドットを除いて判定) かどうかを判定します。
    /// </summary>
    /// <param name="name">フォルダー名。</param>
    /// <returns>保護フォルダー名の場合は true。</returns>
    private static bool IsProtectedName(string name)
    {
        return IgnoredDirectoryLookup.Contains(name.AsSpan().TrimEnd(" ."));
    }

    /// <summary>
    ///     Builds the root-relative path of an enumerated child without calling GetFullPath per entry.
    ///     Names that Win32 normalization would alter (trailing dot or space) keep the original path.
    /// </summary>
    private static string ChildRelative(string root, string directoryRelative, string path, string name)
    {
        if (name.Length == 0 || name[^1] == '.' || name[^1] == ' ')
            return RelativeFromRoot(root, path);
        return directoryRelative.Length == 0 ? name : directoryRelative + "\\" + name;
    }

    /// <summary>
    ///     比較ルートからの相対パスを作成します。ルート外のパスは拒否します。
    /// </summary>
    /// <param name="root">比較ルート。</param>
    /// <param name="path">変換するパス。</param>
    /// <returns>相対パス。ルート自身の場合は空文字列。</returns>
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

    /// <summary>
    ///     比較ルート配下を走査して、ファイル情報、フォルダー、.gitignore ルールを含む比較用インデックスを作成します。
    /// </summary>
    /// <param name="root">比較ルート。</param>
    /// <param name="token">キャンセル トークン。</param>
    /// <param name="disabledIgnoreRules">無効にする .gitignore ルールのキー。</param>
    /// <returns>作成したインデックス。</returns>
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

    /// <summary>
    ///     2 つのインデックスの写しから .gitignore で除外されるパスを取り除き、ファイルを分類した比較スナップショットを作成します。
    /// </summary>
    /// <param name="source">比較元のインデックス。</param>
    /// <param name="target">比較先のインデックス。</param>
    /// <param name="progress">進捗の通知先。</param>
    /// <param name="compareTimestamp">更新日時を比較する場合は true。</param>
    /// <param name="token">キャンセル トークン。</param>
    /// <param name="onBatch">分類済みのファイルを一定件数ごとに受け取るコールバック。</param>
    /// <param name="pathComparison">パスの並び順。null の場合は大文字と小文字を区別しない順。</param>
    /// <param name="preciseCompare">XXH128 で内容を比較する場合は true。</param>
    /// <param name="compareSize">サイズを比較する場合は true。</param>
    /// <param name="compareAttributes">BOM の有無を比較する場合は true。</param>
    /// <returns>比較スナップショット。</returns>
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

    /// <summary>
    ///     インデックスのファイル辞書とフォルダー集合を複製します。キャッシュしたインデックスを別の相手と比較しても元が変わらないようにするためです。
    /// </summary>
    /// <param name="index">複製するインデックス。</param>
    /// <returns>複製したインデックス。</returns>
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

    /// <summary>
    ///     比較元・比較先どちらかの .gitignore で無視されるファイルとフォルダーを、両側から取り除きます。
    /// </summary>
    /// <param name="source">比較元のルート。</param>
    /// <param name="target">比較先のルート。</param>
    /// <param name="left">比較元のファイル情報。</param>
    /// <param name="right">比較先のファイル情報。</param>
    /// <param name="sourceFolders">比較元のフォルダー。</param>
    /// <param name="targetFolders">比較先のフォルダー。</param>
    /// <param name="token">キャンセル トークン。</param>
    /// <param name="disabled">無効にする .gitignore ルールのキー。</param>
    private static void ExcludeIgnored(string source, string target, Dictionary<string, DiffStamp> left,
        Dictionary<string, DiffStamp> right, HashSet<string> sourceFolders, HashSet<string> targetFolders,
        CancellationToken token,
        IEnumerable<string> disabled = null)
    {
        var disabledArray = disabled?.ToArray();

        var a = new GitIgnoreMatcher(source, disabledArray);
        var b = new GitIgnoreMatcher(target, disabledArray);
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

    /// <summary>
    ///     指定フォルダーの直下だけを走査し、フォルダーとファイル情報を追加します。
    /// </summary>
    /// <param name="root">比較ルート。</param>
    /// <param name="relativeFolder">走査するフォルダーの相対パス。</param>
    /// <param name="folders">フォルダーの相対パスの追加先。</param>
    /// <param name="files">ファイル情報の追加先。</param>
    /// <param name="token">キャンセル トークン。</param>
    private static void ScanFolderLevel(string root, string relativeFolder, HashSet<string> folders,
        Dictionary<string, DiffStamp> files, CancellationToken token)
    {
        var directory = relativeFolder.Length == 0 ? root : SafeFolderPath(root, relativeFolder);
        if (!Directory.Exists(directory)) return;
        folders.Add(relativeFolder);

        var directoryRelative = RelativeFromRoot(root, directory);
        foreach (var entry in VolumePathIndex.EnumerateNativeDirectory(directory, token))
        {
            token.ThrowIfCancellationRequested();
            var path = Path.Combine(directory, entry.Name);
            var relative = ChildRelative(root, directoryRelative, path, entry.Name);
            if (IsProtectedName(entry.Name)) continue;
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

    private const int ClassifyChunkSize = 128;

    // Content reads (BOM, XXH128, re-stat) overlap well on SSDs and network shares;
    // keep the degree small so spinning disks are not thrashed.
    private static readonly ParallelOptions ContentReadOptions = new() { MaxDegreeOfParallelism = 4 };

    private sealed class ClassifyWork
    {
        public DiffStamp Source, Target;
        public string SourcePath, TargetPath;
        public bool? SourceBom, TargetBom;
        public string SourceHash, TargetHash;
        public DiffStamp SourceNow, TargetNow;
        public bool IsProtected, HashMissing;
        public ExceptionDispatchInfo Error;
    }

    /// <summary>
    ///     両側のファイル情報を比較して、各ファイルを同一・新規・削除・変更などに分類します。パスの検証は順次、内容の読み取りは並列に行います。
    /// </summary>
    /// <param name="left">比較元のファイル情報。</param>
    /// <param name="right">比較先のファイル情報。</param>
    /// <param name="includeSame">同一のファイルも結果に含める場合は true。</param>
    /// <param name="compareTimestamp">更新日時を比較する場合は true。</param>
    /// <param name="token">キャンセル トークン。</param>
    /// <param name="progress">進捗の通知先。</param>
    /// <param name="offset">進捗の件数に加算する値。</param>
    /// <param name="onBatch">分類済みのファイルを一定件数ごとに受け取るコールバック。</param>
    /// <param name="pathComparison">パスの並び順。</param>
    /// <param name="sourceRoot">比較元のルート。</param>
    /// <param name="targetRoot">比較先のルート。</param>
    /// <param name="preciseCompare">XXH128 で内容を比較する場合は true。</param>
    /// <param name="compareSize">サイズを比較する場合は true。</param>
    /// <param name="compareAttributes">BOM の有無を比較する場合は true。</param>
    /// <returns>分類したファイルの一覧。</returns>
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
        var readsContent = compareAttributes || preciseCompare;
        var work = new ClassifyWork[ClassifyChunkSize];

        for (var chunkStart = 0; chunkStart < paths.Length; chunkStart += ClassifyChunkSize)
        {
            token.ThrowIfCancellationRequested();
            var count = Math.Min(ClassifyChunkSize, paths.Length - chunkStart);

            // 1) Sequential: stamps and path validation (the folder caches are not thread-safe).
            for (var j = 0; j < count; j++)
            {
                var path = paths[chunkStart + j];
                var item = work[j] = new ClassifyWork();
                if (Protected(path))
                {
                    item.IsProtected = true;
                    continue;
                }

                left.TryGetValue(path, out item.Source);
                right.TryGetValue(path, out item.Target);
                if (!readsContent) continue;
                try
                {
                    if (item.Source != null)
                        item.SourcePath = ValidatedFilePath(sourceRoot, path, sourceValidatedFolders);
                    if (item.Target != null)
                        item.TargetPath = ValidatedFilePath(targetRoot, path, targetValidatedFolders);
                }
                catch (Exception ex)
                {
                    item.Error = ExceptionDispatchInfo.Capture(ex);
                }
            }

            // 2) Parallel: file content reads. Failures are rethrown in path order below.
            if (readsContent)
            {
                Parallel.For(0, count, ContentReadOptions, j =>
                {
                    var item = work[j];
                    if (item.IsProtected || item.Error != null || token.IsCancellationRequested) return;
                    try
                    {
                        ReadContent(item, preciseCompare, compareTimestamp, compareSize, compareAttributes, token);
                    }
                    catch (Exception ex)
                    {
                        item.Error = ExceptionDispatchInfo.Capture(ex);
                    }
                });
                token.ThrowIfCancellationRequested();
            }

            // 3) Sequential: classification, batching and progress in the original order.
            for (var j = 0; j < count; j++)
            {
                var index = chunkStart + j;
                var path = paths[index];
                var item = work[j];
                work[j] = null;
                if (progress != null && index % 128 == 0)
                    progress.Report(ReportCompare("Classifying differences\u2026", index, paths.Length));
                if (item.IsProtected) continue;
                item.Error?.Throw();
                var a = item.Source;
                var b = item.Target;
                var sourceBom = compareAttributes && a != null ? item.SourceBom : false;
                var targetBom = compareAttributes && b != null ? item.TargetBom : false;
                if (item.HashMissing)
                {
                    progress?.Report(ReportCompare("Skipped changed file: " + path, index + 1, paths.Length));
                    continue;
                }

                if (preciseCompare)
                    // Files may disappear or change between enumeration and hashing
                    // (desktop.ini is a common example). Never interpret a vanished
                    // source as an instruction to delete its destination.
                    // Exclude this stale entry; a fresh comparison will pick it up.
                    if (!DiffStamp.Same(a, item.SourceNow, true) || !DiffStamp.Same(b, item.TargetNow, true))
                    {
                        progress?.Report(ReportCompare("Skipped changed file: " + path, index + 1, paths.Length));
                        continue;
                    }

                var sourceHash = item.SourceHash;
                var targetHash = item.TargetHash;
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
        }

        if (batch?.Count > 0) onBatch(batch.ToArray());
        progress?.Report(ReportCompare("Classifying differences\u2026", Math.Max(1, paths.Length),
            Math.Max(1, paths.Length)));
        return files;
    }

    /// <summary>
    ///     比較に必要な場合に、BOM の有無と XXH128 ハッシュを読み取ります。精密比較では読み取り後のファイル情報も取得します。
    /// </summary>
    /// <param name="item">分類中の項目。</param>
    /// <param name="preciseCompare">XXH128 で内容を比較する場合は true。</param>
    /// <param name="compareTimestamp">更新日時を比較する場合は true。</param>
    /// <param name="compareSize">サイズを比較する場合は true。</param>
    /// <param name="compareAttributes">BOM の有無を比較する場合は true。</param>
    /// <param name="token">キャンセル トークン。</param>
    private static void ReadContent(ClassifyWork item, bool preciseCompare, bool compareTimestamp,
        bool compareSize, bool compareAttributes, CancellationToken token)
    {
        var a = item.Source;
        var b = item.Target;
        item.SourceBom = compareAttributes && a != null ? HasBom(item.SourcePath) : false;
        item.TargetBom = compareAttributes && b != null ? HasBom(item.TargetPath) : false;
        if (ShouldHash(a, b, compareTimestamp, compareSize, compareAttributes, item.SourceBom, item.TargetBom,
                preciseCompare))
        {
            item.SourceHash = HashFile(item.SourcePath, token);
            item.TargetHash = HashFile(item.TargetPath, token);
            if (item.SourceHash == null || item.TargetHash == null)
            {
                item.HashMissing = true;
                return;
            }
        }

        if (!preciseCompare) return;
        item.SourceNow = a == null ? null : DiffStamp.Read(item.SourcePath);
        item.TargetNow = b == null ? null : DiffStamp.Read(item.TargetPath);
    }

    /// <summary>
    ///     ハッシュを計算すべきかどうかを判定します。両側が存在し、有効なサイズ・日時・BOM のチェックがすべて一致する場合だけ計算します。
    /// </summary>
    /// <param name="source">比較元のファイル情報。</param>
    /// <param name="target">比較先のファイル情報。</param>
    /// <param name="compareTimestamp">更新日時を比較する場合は true。</param>
    /// <param name="compareSize">サイズを比較する場合は true。</param>
    /// <param name="compareAttributes">BOM の有無を比較する場合は true。</param>
    /// <param name="sourceBom">比較元の BOM の有無。読み取れない場合は null。</param>
    /// <param name="targetBom">比較先の BOM の有無。読み取れない場合は null。</param>
    /// <param name="preciseCompare">精密比較が有効な場合は true。</param>
    /// <returns>ハッシュを計算する場合は true。</returns>
    internal static bool ShouldHash(DiffStamp source, DiffStamp target, bool compareTimestamp, bool compareSize,
        bool compareAttributes, bool? sourceBom, bool? targetBom, bool preciseCompare)
    {
        return preciseCompare && source != null && target != null &&
               DiffStamp.Same(source, target, compareTimestamp, compareSize) &&
               (!compareAttributes || (sourceBom.HasValue && targetBom.HasValue && sourceBom == targetBom));
    }

    /// <summary>
    ///     Resolves a file inside a comparison root for read-only content checks.
    ///     The first file of each folder receives the full <see cref="SafePath" /> check of every ancestor;
    ///     later files in that folder validate the relative name and their own final component only.
    /// </summary>
    private static string ValidatedFilePath(string root, string relative, HashSet<string> validatedFolders)
    {
        var folder = Path.GetDirectoryName(relative) ?? "";
        if (validatedFolders.Add(folder)) return SafePath(root, relative);

        ValidateRelative(relative);
        var path = Path.GetFullPath(Path.Combine(root, relative));
        if (!path.StartsWith(root.TrimEnd('\\') + "\\", StringComparison.OrdinalIgnoreCase))
            throw new IOException("Refused a path outside the root or inside .git.");
        try
        {
            if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
                throw new IOException("Reparse points are excluded: " + path);
        }
        catch (FileNotFoundException)
        {
            // Treated as unreadable/changed by the content checks.
        }
        catch (DirectoryNotFoundException)
        {
            // Treated as unreadable/changed by the content checks.
        }

        return path;
    }

    /// <summary>
    ///     ファイルの先頭に BOM (UTF-8、UTF-16、UTF-32 BE) があるかどうかを判定します。
    /// </summary>
    /// <param name="path">ファイルのパス。</param>
    /// <returns>BOM がある場合は true。読み取れない場合は null。</returns>
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

    private const int HashBufferSize = 131072;

    /// <summary>
    ///     ファイルの XXH128 ハッシュを、プールしたバッファーで順次読み取りながら計算します。
    /// </summary>
    /// <param name="path">ファイルのパス。</param>
    /// <param name="token">キャンセル トークン。</param>
    /// <returns>16 進文字列のハッシュ。ファイルがない場合は null。</returns>
    internal static string HashFile(string path, CancellationToken token = default)
    {
        if (string.IsNullOrEmpty(path) || !File.Exists(path)) return null;
        var xxh128 = new XxHash128();
        // bufferSize 0: reads are already large, so skip FileStream's own 128 KB buffer.
        // The work buffer is pooled; a fresh 128 KB array per file lands on the LOH
        // and drives frequent gen-2 collections when many files are hashed.
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 0,
            FileOptions.SequentialScan);
        var buffer = ArrayPool<byte>.Shared.Rent(HashBufferSize);
        try
        {
            int count;
            while ((count = stream.Read(buffer, 0, HashBufferSize)) > 0)
            {
                token.ThrowIfCancellationRequested();
                xxh128.Append(buffer.AsSpan(0, count));
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }

        return Convert.ToHexString(xxh128.GetCurrentHash());
    }

    /// <summary>
    ///     同期の直前に、ファイルが比較時のサイズ・更新日時・ハッシュから変わっていないかを確認します。比較の設定に関係なく確認します。
    /// </summary>
    /// <param name="path">ファイルのパス。</param>
    /// <param name="stamp">比較時のファイル情報。</param>
    /// <param name="hash">比較時のハッシュ。null の場合はハッシュを確認しません。</param>
    /// <returns>変わっていない場合は true。</returns>
    private static bool MatchesSnapshot(string path, DiffStamp stamp, string hash)
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
                string from = null;
                string to = null;
                try
                {
                    var left = SafePath(snapshot.SourceRoot, file.RelativePath);
                    var right = SafePath(snapshot.TargetRoot, file.RelativePath);
                    from = toTarget ? left : right;
                    to = toTarget ? right : left;
                    if (File.Exists(from)) RejectHardLinks(from);
                    if (File.Exists(to)) RejectHardLinks(to);
                    // A failure after replacement/deletion may already have produced the
                    // desired state. Verify it without overwriting a newly changed file.
                    if (retry && destinationCommitted
                              && MatchesSnapshot(from, toTarget ? file.Source : file.Target,
                                  toTarget ? file.SourceHash : file.TargetHash)
                              && MatchesSnapshot(to, toTarget ? file.Source : file.Target,
                                  toTarget ? file.SourceHash : file.TargetHash))
                    {
                        writeLog(logIndex, "OK " + operation + " " + file.RelativePath);
                        return;
                    }

                    if (!MatchesSnapshot(left, file.Source, file.SourceHash) ||
                        !MatchesSnapshot(right, file.Target, file.TargetHash))
                        throw new IOException("Changed after compare. Compare again.");
                    if (operation == "Delete")
                    {
                        File.Delete(to);
                        destinationCommitted = true;
                    }
                    else
                    {
                        Directory.CreateDirectory(Path.GetDirectoryName(to)
                                                  ?? throw new IOException("The destination has no parent directory."));
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
                            if (!MatchesSnapshot(left, file.Source, file.SourceHash) ||
                                !MatchesSnapshot(right, file.Target, file.TargetHash))
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
                            toTarget ? file.SourceHash : file.TargetHash))
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

    /// <summary>
    ///     相対パスのフォルダーの深さを求めます。
    /// </summary>
    /// <param name="relativePath">相対パス。</param>
    /// <returns>パス要素の数。</returns>
    private static int FolderDepth(string relativePath)
    {
        return relativePath.Split(new[] { '\\', '/' }, StringSplitOptions.RemoveEmptyEntries).Length;
    }

    /// <summary>
    ///     ファイルが他のプロセスにロックされているかどうかを、排他で開けるかどうかで判定します。アクセス権の問題はロックとみなしません。
    /// </summary>
    /// <param name="path">ファイルのパス。</param>
    /// <returns>共有違反またはロック違反の場合は true。</returns>
    private static bool IsFileLocked(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path)) return false;

        try
        {
            // A normal accessible file can be opened exclusively for read.
            // A file held by another process with incompatible sharing fails here
            // with ERROR_SHARING_VIOLATION / ERROR_LOCK_VIOLATION.
            using (new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.None))
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
                // Ignore cleanup failures; the temp file is not critical.
            }
        }

        return log;
    }

    /// <summary>
    ///     比較ルートのフォルダー名から、ZIP 内の最上位フォルダー名を作成します。
    /// </summary>
    /// <param name="root">比較ルート。</param>
    /// <returns>無効な文字を置き換えたフォルダー名。</returns>
    private static string ZipRootFolderName(string root)
    {
        var name = Path.GetFileName((root ?? "").TrimEnd('\\', '/'));
        if (string.IsNullOrWhiteSpace(name)) name = "root";
        foreach (var invalid in Path.GetInvalidFileNameChars())
            name = name.Replace(invalid, '_');
        return name;
    }

    /// <summary>
    ///     最上位フォルダーと相対パスから ZIP のエントリ名を作成します。
    /// </summary>
    /// <param name="wrapper">最上位フォルダー名。</param>
    /// <param name="relative">相対パス。</param>
    /// <param name="directory">フォルダーのエントリの場合は true。末尾にスラッシュを付けます。</param>
    /// <returns>ZIP のエントリ名。</returns>
    private static string ZipEntryName(string wrapper, string relative, bool directory)
    {
        var entry = (relative ?? "").Replace('\\', '/').Trim('/');
        var prefix = (wrapper ?? "").Replace('\\', '/').Trim('/');
        if (prefix.Length == 0) prefix = "root";
        var combined = entry.Length == 0 ? prefix : prefix + "/" + entry;
        return directory ? combined + "/" : combined;
    }

    /// <summary>
    ///     ZIP に、指定パスとその親のフォルダー エントリをまだなければ追加します。
    /// </summary>
    /// <param name="archive">追加先の ZIP。</param>
    /// <param name="entry">フォルダーのエントリ名。</param>
    /// <param name="created">作成済みのフォルダー エントリ。</param>
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

    /// <summary>
    ///     比較ルートと相対パスから安全な絶対パスを作成します。検証に失敗した場合は空文字列を返します。
    /// </summary>
    /// <param name="root">比較ルート。</param>
    /// <param name="relative">相対パス。</param>
    /// <returns>絶対パス。検証に失敗した場合は空文字列。</returns>
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

    /// <summary>
    ///     Win32 API の GetFileInformationByHandle を呼び出し、ファイル ハンドルから属性・日時・サイズなどを取得します。
    /// </summary>
    /// <param name="handle">ファイル ハンドル。</param>
    /// <param name="info">取得した情報。</param>
    /// <returns>成功した場合は true。</returns>
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