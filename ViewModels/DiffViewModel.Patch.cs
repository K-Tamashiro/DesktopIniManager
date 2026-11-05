using DesktopIniManager.Services;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace DesktopIniManager.ViewModels;

internal sealed partial class DiffViewModel
{
    private string _loadedPatchPath;
    internal PatchDocument Patch { get; private set; }
    internal List<PatchFileState> PatchFiles { get; private set; } = [];
    internal int PatchIndex { get; private set; }
    internal string PatchRoot { get; private set; }
    internal PatchFileState CurrentPatch => PatchFiles.Count == 0 ? null : PatchFiles[PatchIndex];
    internal bool IsPatch => Patch != null;
    internal bool HasPatchEdits => PatchFiles.Any(file => file.IsDirty);

    internal void InitializePatch(PatchDocument document, string path, string root)
    {
        Patch = document;
        _loadedPatchPath = path;
        PatchFiles = document.Files.Select(entry => new PatchFileState(entry)).ToList();
        PatchIndex = 0;
        PatchRoot = null;
        DifferencesOnly = true;
        if (root != null) SetPatchRoot(root);
        else RefreshPatchRows();
    }

    private async Task LoadPatchAsync(string path)
    {
        if (!string.Equals(_loadedPatchPath, path, StringComparison.OrdinalIgnoreCase) || Patch == null)
        {
            var parsed = await Task.Run(() => PatchDocument.Read(path));
            Patch = parsed;
            _loadedPatchPath = path;
            PatchFiles = parsed.Files.Select(entry => new PatchFileState(entry)).ToList();
            PatchIndex = 0;
            PatchRoot = null;
            DifferencesOnly = true;
            if (!IsDetachedComparison && Directory.Exists(Snapshot.TargetRoot)) SetPatchRoot(Snapshot.TargetRoot);
        }
        RefreshPatchRows();
    }

    internal void SelectPatchFile(int index)
    {
        if (index < 0 || index >= PatchFiles.Count) return;
        PatchIndex = index;
        _expandedEqualBlocks.Clear();
        RefreshPatchRows();
    }

    internal void SetPatchRoot(string root)
    {
        if (PatchFiles.Any(file => file.IsDirty && (!file.Entry.IsNew || file.TargetPath != null)))
            throw new InvalidOperationException("Save or discard patch edits before changing the repository.");
        root = Path.GetFullPath(root);
        if (!Directory.Exists(root)) throw new DirectoryNotFoundException(root);
        // Resolve every entry before changing state: a rejected path must not
        // leave a half-switched repository.
        var paths = PatchFiles.Select(file =>
        {
            if (file.Entry.UnsupportedReason != null) return null;
            var path = ResolvePatchPath(root, file.Entry.RelativePath);
            if (!System.IO.File.Exists(path) && !file.Entry.IsNew && file.Entry.OldPath != file.Entry.RelativePath)
            {
                var oldPath = ResolvePatchPath(root, file.Entry.OldPath);
                if (System.IO.File.Exists(oldPath)) return oldPath;
            }
            return path;
        }).ToArray();
        for (var i = 0; i < paths.Length; i++)
            if (paths[i] != null)
            {
                if (PatchFiles[i].IsDirty) PatchFiles[i].AssignNewPath(paths[i]);
                else PatchFiles[i].Attach(paths[i]);
            }
        PatchRoot = root;
        RefreshPatchRows();
    }

    /// <summary>
    ///     Establishes the repository root from one actual file. The file may be the
    ///     target of any patch entry, not only the selected one: that entry becomes
    ///     selected, so the patch and target selectors stay synchronized. A file that
    ///     is not in the patch still identifies the nearest ancestor folder under
    ///     which patch entries exist.
    /// </summary>
    internal bool SelectPatchTarget(string path)
    {
        var full = Path.GetFullPath(path);
        var order = Enumerable.Range(0, PatchFiles.Count).OrderBy(index => index == PatchIndex ? 0 : 1);
        foreach (var index in order)
        {
            var entry = PatchFiles[index].Entry;
            if (entry.UnsupportedReason != null) continue;
            foreach (var candidate in new[] { entry.RelativePath, entry.OldPath })
            {
                if (!PatchDocument.IsSafeRelativePath(candidate)) continue;
                var relative = candidate.Replace('/', Path.DirectorySeparatorChar);
                if (!full.EndsWith(Path.DirectorySeparatorChar + relative, StringComparison.OrdinalIgnoreCase)) continue;
                SetPatchRoot(full[..^relative.Length]);
                SelectPatchFile(index);
                return true;
            }
        }

        var root = RepositoryRootFrom(Path.GetDirectoryName(full));
        if (root == null) return false;
        SetPatchRoot(root);
        return true;
    }

    /// <summary>
    ///     Sets the repository root from a folder the user chose. The folder or the nearest
    ///     ancestor under which a patch path exists becomes the root, so choosing a
    ///     subfolder such as the one holding the files still finds the targets. When no
    ///     patch path exists anywhere, the chosen folder is used and the status reports it.
    /// </summary>
    internal void SelectPatchRoot(string folder)
    {
        SetPatchRoot(RepositoryRootFrom(Path.GetFullPath(folder)) ?? folder);
    }

    private string RepositoryRootFrom(string folder)
    {
        for (; folder != null; folder = Path.GetDirectoryName(folder))
        {
            var candidate = folder;
            if (PatchFiles.Any(file => file.Entry.UnsupportedReason == null && !file.Entry.IsNew &&
                    PatchDocument.IsSafeRelativePath(file.Entry.RelativePath) &&
                    System.IO.File.Exists(Path.Combine(candidate,
                        file.Entry.RelativePath.Replace('/', Path.DirectorySeparatorChar)))))
                return candidate;
        }
        return null;
    }

    internal static string ResolvePatchPath(string root, string relative)
    {
        if (!PatchDocument.IsSafeRelativePath(relative)) throw new InvalidDataException("Unsafe patch path.");
        var basePath = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));
        var path = Path.GetFullPath(Path.Combine(basePath, relative.Replace('/', Path.DirectorySeparatorChar)));
        if (!path.StartsWith(basePath + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Patch path escapes the target repository.");
        for (var current = path; !string.Equals(current, basePath, StringComparison.OrdinalIgnoreCase);
             current = Path.GetDirectoryName(current))
        {
            if (current == null) throw new InvalidDataException("Invalid patch target.");
            if ((System.IO.File.Exists(current) || Directory.Exists(current)) &&
                (System.IO.File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                throw new InvalidDataException("Patch targets through symbolic links or junctions are not supported.");
        }
        return path;
    }

    internal void MovePatchHunk(int id, int delta)
    {
        // Displayed ids are runs; moving shifts the whole patch hunk they belong to.
        CurrentPatch.Move(CurrentPatch.HunkOfRun(id), delta);
        RefreshPatchRows();
    }

    internal void RefreshPatchRows()
    {
        if (CurrentPatch == null) return;
        _edits = null;
        _allLines = CurrentPatch.Rows(CompareCase, !CompareCase, !CompareSpaces, CompareNewlines);
        SourceText = PatchProjection(true);
        TargetText = CurrentPatch.HasTarget ? CurrentPatch.Document() : PatchProjection(false);
        _originalSource = SourceText;
        _originalTarget = CurrentPatch.Original;
        var targetPath = CurrentPatch.TargetPath;
        _patchHeaderFile = new DiffFile
        {
            RelativePath = CurrentPatch.Entry.RelativePath,
            Source = File.Source,
            Target = string.IsNullOrEmpty(targetPath) ? null : DiffStamp.Read(targetPath)
        };
        OnPropertyChanged(nameof(Title));
        OnPropertyChanged(nameof(SourceHeader));
        OnPropertyChanged(nameof(TargetHeader));
        UpdateDisplayedLines();
        var mismatches = CurrentPatch.HasTarget
            ? Enumerable.Range(0, CurrentPatch.Entry.Hunks.Count).Count(id => !CurrentPatch.Matches(id)) : 0;
        Status = CurrentPatch.Entry.UnsupportedReason ??
            (CurrentPatch.HasTarget ? "Patch → Target"
                : PatchRoot != null ? $"Target not found under repository: {CurrentPatch.Entry.RelativePath} (preview before → after)"
                : "Patch preview (before → after)") +
            $" | Applied {CurrentPatch.AppliedCount}/{CurrentPatch.Entry.Hunks.Count}" +
            (CurrentPatch.PresentCount > 0 ? $" | Already in target: {CurrentPatch.PresentCount}" : "") +
            (mismatches > 0 ? $" | Context mismatch: {mismatches}" : "") +
            (Patch.DecodingErrorLines > 0 ? $" | Undecodable lines: {Patch.DecodingErrorLines} (affected hunks cannot be applied)" : "");
    }

    private TextDocument PatchProjection(bool source)
    {
        var length = _allLines.Select(row => source ? row.LeftNumber : row.RightNumber).DefaultIfEmpty().Max();
        var text = Enumerable.Repeat("", length).ToArray();
        var endings = Enumerable.Repeat("", length).ToArray();
        foreach (var row in _allLines)
        {
            var number = source ? row.LeftNumber : row.RightNumber;
            if (number <= 0) continue;
            text[number - 1] = source ? row.Left : row.Right;
            endings[number - 1] = "\n";
        }
        foreach (var hunk in CurrentPatch.Entry.Hunks)
        {
            var after = !source || CurrentPatch.HasTarget;
            var number = (after ? hunk.NewStart : hunk.OldStart) - 1;
            foreach (var line in hunk.Lines.Where(line => line.Kind != (after ? '-' : '+')))
            {
                if (number >= 0 && number < endings.Length) endings[number] = line.Ending;
                number++;
            }
        }
        return new TextDocument { Lines = text, Endings = endings, EncodingName = "PATCH", Newlines = "LF" };
    }

    internal void SavePatchTarget(Encoding encoding = null, string ending = null)
    {
        if (PatchRoot != null) ResolvePatchPath(PatchRoot, CurrentPatch.Entry.RelativePath);
        CurrentPatch.Save(encoding, ending);
        RefreshPatchRows();
    }

    internal void DiscardPatchEdits()
    {
        foreach (var state in PatchFiles.Where(file => file.IsDirty)) state.AttachAfterDiscard();
        RefreshPatchRows();
    }
}
