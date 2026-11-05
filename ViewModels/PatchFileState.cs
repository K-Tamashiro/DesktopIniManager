using DesktopIniManager.Services;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using TextDocument = DesktopIniManager.ViewModels.DiffViewModel.TextDocument;

namespace DesktopIniManager.ViewModels;

internal sealed class PatchFileState
{
    internal sealed record TextLine(string Text, string Ending);

    /// <summary>
    ///     One displayed hunk: a run of changed rows inside one patch hunk. Unchanged
    ///     rows (context, or rows equal under the comparison options) are never part
    ///     of a run. Applying a run replaces exactly its target rows with its patch rows.
    /// </summary>
    internal sealed record PatchRun(int HunkId, int TargetIndex, int TargetCount, string[] Texts, PatchEdit Applied);

    /// <summary>A change to the target buffer, positioned relative to its patch hunk.</summary>
    internal sealed class PatchEdit
    {
        internal int HunkId;
        internal int Offset;
        internal int InsertedCount;
        internal bool WholeHunk;
        internal TextLine[] Removed;
    }

    private readonly List<TextLine> _working = [];
    // Active edits; the stack groups the edits made by one copy for Undo().
    private readonly List<PatchEdit> _edits = [];
    private readonly Stack<PatchEdit[]> _undo = [];
    // Hunks whose post-change text is already in the target (e.g. a repository
    // that already contains the patch). They are compared over NewCount lines.
    private readonly HashSet<int> _present = [];
    private readonly int[] _lengths;
    private List<PatchRun> _runs = [];
    internal PatchFile Entry { get; }
    internal int[] Positions { get; }
    internal string TargetPath { get; private set; }
    internal TextDocument Original { get; private set; } = new();
    internal bool HasTarget { get; private set; }
    internal bool IsDirty => HasTarget && (DiffViewModel.Text(Document()) != DiffViewModel.Text(Original) ||
        Entry.IsNew && Original.OriginalBytes == null && _edits.Count > 0);
    internal bool CanApply => HasTarget && Entry.UnsupportedReason == null;
    internal int AppliedCount => Enumerable.Range(0, Entry.Hunks.Count).Count(IsEdited);
    internal int PresentCount => _present.Count;
    internal bool IsPresent(int id) => _present.Contains(id);
    /// <summary>Runs of the last <see cref="Rows" /> call; row EditHunkId values index this list.</summary>
    internal IReadOnlyList<PatchRun> Runs => _runs;

    internal PatchFileState(PatchFile entry)
    {
        Entry = entry;
        HasTarget = entry.IsNew && entry.UnsupportedReason == null;
        Positions = entry.Hunks.Select(hunk => Math.Max(0, hunk.OldStart - (hunk.OldCount == 0 ? 0 : 1))).ToArray();
        _lengths = entry.Hunks.Select(hunk => hunk.OldCount).ToArray();
    }

    internal void Attach(string path)
    {
        if (IsDirty) throw new InvalidOperationException("Save or discard this target before replacing it.");
        TargetPath = path;
        HasTarget = System.IO.File.Exists(path) || Entry.IsNew;
        Original = System.IO.File.Exists(path) ? DiffViewModel.ReadDocument(path) : new TextDocument { EncodingName = "UTF-8", Newlines = "CRLF" };
        _working.Clear();
        for (var i = 0; i < Original.Lines.Length; i++)
            if (i < Original.Lines.Length - 1 || Original.Lines[i].Length > 0 || Original.Endings[i].Length > 0)
                _working.Add(new TextLine(Original.Lines[i], Original.Endings[i]));
        _edits.Clear();
        _undo.Clear();
        _present.Clear();
        _runs = [];
        // Like git apply: look for each hunk's preimage at the patch's line number,
        // shifted by the drift of earlier hunks; if it is not there, use the nearest
        // position where it is (the target may have gained or lost lines above).
        // Where only the postimage is found, the target already contains that
        // hunk. Not found at all: keep the expected position and report a mismatch.
        var offset = 0;
        for (var i = 0; i < Positions.Length; i++)
        {
            var hunk = Entry.Hunks[i];
            var baseline = Math.Max(0, hunk.OldStart - (hunk.OldCount == 0 ? 0 : 1));
            var expected = Math.Max(0, baseline + offset);
            var lower = i == 0 ? 0 : Positions[i - 1] + _lengths[i - 1];
            _lengths[i] = hunk.OldCount;
            var found = Nearest(hunk.Before, expected, lower);
            if (found >= 0)
            {
                Positions[i] = found;
                offset = found - baseline;
                continue;
            }

            var after = hunk.After;
            var contained = after.Length > 0 ? Nearest(after, expected, lower) : -1;
            if (contained >= 0)
            {
                Positions[i] = contained;
                _present.Add(i);
                _lengths[i] = hunk.NewCount;
                offset = contained - baseline + hunk.NewCount - hunk.OldCount;
                continue;
            }

            Positions[i] = Math.Max(expected, lower);
        }
    }

    // Nearest position at or after lower where the lines match, searching outward
    // from expected; -1 when they occur nowhere. Mismatches end on the first line.
    private int Nearest(string[] lines, int expected, int lower)
    {
        expected = Math.Max(expected, lower);
        for (var distance = 0; ; distance++)
        {
            var below = expected + distance;
            var above = expected - distance;
            var inRange = false;
            if (below + lines.Length <= _working.Count)
            {
                inRange = true;
                if (LinesAt(lines, below)) return below;
            }
            if (distance > 0 && above >= lower)
            {
                inRange = true;
                if (LinesAt(lines, above)) return above;
            }
            if (!inRange) return -1;
        }
    }

    private bool LinesAt(string[] expected, int position)
    {
        if (position < 0 || position + expected.Length > _working.Count) return false;
        for (var i = 0; i < expected.Length; i++)
            if (!string.Equals(_working[position + i].Text, expected[i], StringComparison.Ordinal)) return false;
        return true;
    }

    internal void AssignNewPath(string path)
    {
        if (!Entry.IsNew || Original.OriginalBytes != null) throw new InvalidOperationException("Not a new patch target.");
        if (System.IO.File.Exists(path)) throw new IOException("The new target already exists. Select it as an existing target before applying hunks.");
        TargetPath = path;
        HasTarget = true;
    }

    private bool IsEdited(int id) => _edits.Any(edit => edit.HunkId == id);

    /// <summary>The whole patch hunk was applied by <see cref="Copy" />.</summary>
    internal bool IsApplied(int id) => _edits.Any(edit => edit.HunkId == id && edit.WholeHunk);

    internal void AttachAfterDiscard()
    {
        _working.Clear();
        _edits.Clear();
        HasTarget = false;
        Attach(TargetPath);
    }

    private int Length(int id) => _lengths[id];

    internal int HunkOfRun(int run) => run >= 0 && run < _runs.Count ? _runs[run].HunkId : -1;

    internal bool CanMove(int id, int delta)
    {
        if (!CanApply || id < 0 || id >= Positions.Length || IsEdited(id) || IsPresent(id) || Math.Abs(delta) != 1) return false;
        var next = Positions[id] + delta;
        var lower = id == 0 ? 0 : Positions[id - 1] + Length(id - 1);
        var upper = id == Positions.Length - 1 ? _working.Count : Math.Min(_working.Count, Positions[id + 1]);
        return next >= lower && next + Length(id) <= upper;
    }

    internal void Move(int id, int delta)
    {
        if (CanMove(id, delta)) Positions[id] += delta;
    }

    internal bool Matches(int id)
    {
        if (IsEdited(id) && !IsApplied(id)) return true; // partially applied runs: the user chose the lines
        var hunk = Entry.Hunks[id];
        return LinesAt(IsApplied(id) || IsPresent(id) ? hunk.After : hunk.Before, Positions[id]);
    }

    private bool InBounds(int id) => Positions[id] + Length(id) <= _working.Count &&
        (id == 0 || Positions[id] >= Positions[id - 1] + Length(id - 1)) &&
        (id == Positions.Length - 1 || Positions[id] + Length(id) <= Positions[id + 1]);

    internal bool CanCopy(int id) => CanApply && id >= 0 && id < Positions.Length && !IsEdited(id) && !IsPresent(id) &&
        !Entry.Hunks[id].HasDecodingErrors && InBounds(id);

    internal bool CanCopyRun(int run) => CanApply && run >= 0 && run < _runs.Count && _runs[run].Applied == null &&
        !Entry.Hunks[_runs[run].HunkId].HasDecodingErrors && InBounds(_runs[run].HunkId);

    internal bool IsRunApplied(int run) => run >= 0 && run < _runs.Count && _runs[run].Applied != null &&
        _edits.Contains(_runs[run].Applied);

    private string DefaultEnding => Original.Endings.FirstOrDefault(value => value.Length > 0) ?? "\r\n";

    /// <summary>Applies whole patch hunks (context lines keep the target's text).</summary>
    internal void Copy(IEnumerable<int> ids)
    {
        var selected = ids.Distinct().OrderBy(id => id).ToArray();
        if (selected.Any(id => !CanCopy(id))) throw new InvalidOperationException("A selected hunk cannot be applied at its current position.");
        var group = new List<PatchEdit>();
        foreach (var id in selected)
        {
            var hunk = Entry.Hunks[id];
            var position = Positions[id];
            var removed = _working.Skip(position).Take(Length(id)).ToArray();
            var replacement = new List<TextLine>();
            var index = 0;
            foreach (var line in hunk.Lines)
            {
                if (line.Kind == ' ')
                    replacement.Add(removed[index++]);
                else if (line.Kind == '-') index++;
                else replacement.Add(new TextLine(line.Text, line.NoNewline ? "" : DefaultEnding));
            }
            group.Add(Replace(id, 0, removed.Length, replacement, true));
        }
        if (group.Count > 0) _undo.Push(group.ToArray());
    }

    /// <summary>Applies displayed runs: each run's target rows become its patch rows.</summary>
    internal void CopyRuns(IEnumerable<int> runs)
    {
        // Offsets are taken relative to each hunk before any edit: applying one hunk
        // shifts the positions of later hunks. Inside a hunk, apply last first.
        var selected = runs.Distinct().Where(CanCopyRun).Select(run => _runs[run])
            .Select(run => (Run: run, Offset: run.TargetIndex - Positions[run.HunkId]))
            .OrderBy(item => item.Run.HunkId).ThenByDescending(item => item.Offset).ToArray();
        var group = new List<PatchEdit>();
        foreach (var (run, offset) in selected)
        {
            var start = Positions[run.HunkId] + offset;
            var atEnd = run.TargetCount > 0 && start + run.TargetCount == _working.Count &&
                        _working[^1].Ending.Length == 0;
            var replacement = run.Texts.Select((text, i) =>
                new TextLine(text, atEnd && i == run.Texts.Length - 1 ? "" : DefaultEnding)).ToList();
            group.Add(Replace(run.HunkId, offset, run.TargetCount, replacement, false));
        }
        if (group.Count > 0) _undo.Push(group.ToArray());
    }

    private PatchEdit Replace(int id, int offset, int count, List<TextLine> replacement, bool whole)
    {
        var start = Positions[id] + offset;
        var removed = _working.GetRange(start, count).ToArray();
        _working.RemoveRange(start, count);
        _working.InsertRange(start, replacement);
        var delta = replacement.Count - count;
        foreach (var other in _edits.Where(edit => edit.HunkId == id && edit.Offset >= offset + count))
            other.Offset += delta;
        _lengths[id] += delta;
        ShiftFollowing(id, delta);
        var edit = new PatchEdit { HunkId = id, Offset = offset, InsertedCount = replacement.Count, WholeHunk = whole, Removed = removed };
        _edits.Add(edit);
        return edit;
    }

    private void Revert(PatchEdit edit)
    {
        if (!_edits.Remove(edit)) return;
        var removed = edit.Removed;
        var start = Positions[edit.HunkId] + edit.Offset;
        _working.RemoveRange(start, edit.InsertedCount);
        _working.InsertRange(start, removed);
        var delta = removed.Length - edit.InsertedCount;
        foreach (var other in _edits.Where(other => other.HunkId == edit.HunkId && other.Offset > edit.Offset))
            other.Offset += delta;
        _lengths[edit.HunkId] += delta;
        ShiftFollowing(edit.HunkId, delta);
    }

    private void ShiftFollowing(int id, int delta)
    {
        for (var i = id + 1; i < Positions.Length; i++) Positions[i] += delta;
    }

    /// <summary>Reverts every edit of one patch hunk, newest first.</summary>
    internal void UndoHunk(int id)
    {
        foreach (var edit in _edits.Where(edit => edit.HunkId == id).OrderByDescending(edit => edit.Offset).ToArray())
            Revert(edit);
    }

    internal void UndoRun(int run)
    {
        if (IsRunApplied(run)) Revert(_runs[run].Applied);
    }

    internal void Undo()
    {
        while (_undo.TryPop(out var group))
        {
            var active = group.Where(_edits.Contains).ToArray();
            if (active.Length == 0) continue;
            foreach (var edit in Enumerable.Reverse(active)) Revert(edit);
            return;
        }
    }

    internal TextDocument Document()
    {
        var lines = _working.Select(line => line.Text).ToList();
        var endings = _working.Select(line => line.Ending).ToList();
        var fallback = DefaultEnding;
        for (var i = 0; i + 1 < endings.Count; i++)
            if (endings[i].Length == 0) endings[i] = fallback;
        if (endings.Count > 0 && endings[^1].Length > 0) { lines.Add(""); endings.Add(""); }
        return new TextDocument { Lines = lines.ToArray(), Endings = endings.ToArray(), Encoding = Original.Encoding,
            EncodingName = Original.EncodingName, HasBom = Original.HasBom, Newlines = Original.Newlines,
            OriginalBytes = Original.OriginalBytes };
    }

    internal void Save(Encoding newEncoding = null, string newEnding = null)
    {
        if (!IsDirty) return;
        if (string.IsNullOrWhiteSpace(TargetPath)) throw new IOException("Select a target path before saving.");
        var document = Document();
        if (Original.OriginalBytes == null)
        {
            document.Encoding = newEncoding ?? new UTF8Encoding(false, true);
            document.HasBom = document.Encoding.GetPreamble().Length > 0;
            if (newEnding != null)
                document.Endings = document.Endings.Select(ending => ending.Length == 0 ? "" : newEnding).ToArray();
        }
        var bytes = DiffViewModel.EncodeDocument(document);
        var parent = Path.GetDirectoryName(TargetPath);
        if (parent != null) Directory.CreateDirectory(parent);
        DiffViewModel.WriteDocument(TargetPath, bytes, Original.OriginalBytes);
        Original = DiffViewModel.ReadDocument(TargetPath);
        _working.Clear();
        for (var i = 0; i < Original.Lines.Length; i++)
            if (i < Original.Lines.Length - 1 || Original.Lines[i].Length > 0 || Original.Endings[i].Length > 0)
                _working.Add(new TextLine(Original.Lines[i], Original.Endings[i]));
    }

    // Same rules as a text comparison: line endings and newline-only rows count
    // only when compareNewlines is on; otherwise such rows are unchanged and fold.
    // Changed rows get EditHunkId = their run's index in Runs; unchanged rows -1.
    // Rows of an applied run stay visible (Modified) so the run can be undone.
    internal List<DiffLine> Rows(bool inline, bool ignoreCase, bool ignoreSpaces, bool compareNewlines = false)
    {
        var rows = new List<DiffLine>();
        var runs = new List<PatchRun>();
        var cursor = 0;
        for (var id = 0; id < Entry.Hunks.Count; id++)
        {
            var hunk = Entry.Hunks[id];
            var position = HasTarget ? Positions[id] : Math.Max(0, hunk.OldStart - (hunk.OldCount == 0 ? 0 : 1));
            while (cursor < position)
            {
                rows.Add(new DiffLine { Left = null, Right = HasTarget && cursor < _working.Count ? _working[cursor].Text : null,
                    RightNumber = HasTarget && cursor < _working.Count ? cursor + 1 : 0, Kind = DiffLineKind.Unchanged });
                cursor++;
            }
            var left = HasTarget ? hunk.After : hunk.Before;
            var leftEndings = HasTarget ? hunk.AfterEndings : hunk.BeforeEndings;
            var window = HasTarget ? _working.Skip(position).Take(Length(id)).ToArray() : null;
            var right = window?.Select(line => line.Text).ToArray() ?? hunk.After;
            var rightEndings = window?.Select(line => line.Ending).ToArray() ?? hunk.AfterEndings;
            var compared = DiffTextService.Compare(left, right, ignoreCase, ignoreSpaces,
                compareNewlines ? leftEndings : null, compareNewlines ? rightEndings : null, inline, compareNewlines);
            var edits = HasTarget ? _edits.Where(edit => edit.HunkId == id).ToArray() : Array.Empty<PatchEdit>();
            PatchEdit AppliedAt(int rightIndex) => edits.FirstOrDefault(edit =>
                rightIndex >= edit.Offset && rightIndex < edit.Offset + edit.InsertedCount);

            // Target index (relative to the window) after the last row seen: where an
            // insertion-only run goes.
            var nextRight = 0;
            var insertAt = new int[compared.Count];
            var runStart = -1;
            PatchEdit runEdit = null;
            void CloseRun(int end)
            {
                if (runStart < 0) return;
                var part = compared.GetRange(runStart, end - runStart);
                var targets = part.Where(row => row.RightNumber > 0).Select(row => row.RightNumber - 1).ToArray();
                var targetIndex = position + (targets.Length > 0 ? targets[0] : insertAt[runStart]);
                var texts = part.Where(row => row.LeftNumber > 0).Select(row => row.Left).ToArray();
                foreach (var row in part) row.EditHunkId = runs.Count;
                runs.Add(new PatchRun(id, targetIndex, targets.Length, texts, runEdit));
                runStart = -1;
                runEdit = null;
            }

            for (var i = 0; i < compared.Count; i++)
            {
                var row = compared[i];
                insertAt[i] = nextRight;
                if (row.RightNumber > 0) nextRight = row.RightNumber;
                var applied = row.RightNumber > 0 ? AppliedAt(row.RightNumber - 1) : null;
                if (applied != null && row.Kind == DiffLineKind.Unchanged) row.Kind = DiffLineKind.Modified;
                var changed = row.Kind != DiffLineKind.Unchanged;
                if (runStart >= 0 && (!changed || applied != runEdit)) CloseRun(i);
                if (changed && runStart < 0)
                {
                    runStart = i;
                    runEdit = applied;
                }
            }
            CloseRun(compared.Count);

            foreach (var row in compared)
            {
                if (row.LeftNumber > 0) row.LeftNumber += Math.Max(0, (HasTarget ? hunk.NewStart : hunk.OldStart) - 1);
                if (row.RightNumber > 0) row.RightNumber += HasTarget ? position : Math.Max(0, hunk.NewStart - 1);
                rows.Add(row);
            }
            cursor = Math.Max(cursor, position + (HasTarget ? Length(id) : hunk.OldCount));
        }
        while (HasTarget && cursor < _working.Count)
        {
            rows.Add(new DiffLine { Right = _working[cursor].Text, RightNumber = cursor + 1, Kind = DiffLineKind.Unchanged });
            cursor++;
        }
        _runs = runs;
        return rows;
    }
}
