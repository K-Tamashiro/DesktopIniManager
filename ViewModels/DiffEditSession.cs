using System;
using System.Collections.Generic;
using System.Linq;
using DesktopIniManager.Services;
using TextDocument = DesktopIniManager.ViewModels.DiffViewModel.TextDocument;

namespace DesktopIniManager.ViewModels;

// Keep the comparison's hunk boundaries until explicit recompare/save.
internal sealed class DiffEditSession
{
    private readonly List<Block> _blocks = new();
    private readonly List<List<Edit>> _history = new();
    private readonly TextDocument _sourceFormat, _targetFormat;
    private bool _sourceHasFinalNewline;
    private bool _targetHasFinalNewline;

    internal DiffEditSession(TextDocument source, TextDocument target, List<DiffLine> rows)
    {
        _sourceFormat = source;
        _targetFormat = target;
        _sourceHasFinalNewline = HasFinalNewline(source);
        _targetHasFinalNewline = HasFinalNewline(target);
        var id = 0;
        for (var i = 0; i < rows.Count;)
        {
            var changed = rows[i].Kind != DiffLineKind.Unchanged;
            var part = new List<DiffLine>();
            do
            {
                part.Add(rows[i++]);
            } while (i < rows.Count && rows[i].Kind != DiffLineKind.Unchanged == changed);

            var block = new Block { Id = changed ? id++ : -1, Rows = part };
            block.Left = part.Where(r => r.LeftNumber > 0)
                .Select(r => new Line(source.Lines[r.LeftNumber - 1], source.Endings[r.LeftNumber - 1])).ToArray();
            block.Right = part.Where(r => r.RightNumber > 0)
                .Select(r => new Line(target.Lines[r.RightNumber - 1], target.Endings[r.RightNumber - 1])).ToArray();
            foreach (var row in part) row.EditHunkId = block.Id;
            block.OriginalRows = part;
            block.OriginalLeft = block.Left;
            block.OriginalRight = block.Right;
            _blocks.Add(block);
        }
    }

    internal bool CanUndo(int id, bool fromSource)
    {
        return _history.SelectMany(g => g)
            .Any(e => e.Active && e.Block.Id == id && e.DestinationSource == !fromSource);
    }

    internal void Copy(IEnumerable<int> ids, bool fromSource)
    {
        var selected = ids.ToHashSet();
        var group = new List<Edit>();
        foreach (var block in _blocks.Where(b => b.Id >= 0 && selected.Contains(b.Id)))
        {
            if (CanUndo(block.Id, fromSource)) continue;
            var before = fromSource ? block.Right : block.Left;
            var donor = fromSource ? block.Left : block.Right;
            var format = fromSource ? _targetFormat : _sourceFormat;
            // Mixed files retain each existing destination ending. Additional
            // lines inherit a nearby ending. Uniform files keep their format.
            var uniform = format.Newlines.Contains('/') ? null : format.Endings.FirstOrDefault(e => e.Length > 0);
            var fallback = before.Select(l => l.Ending).FirstOrDefault(e => e.Length > 0)
                           ?? format.Endings.FirstOrDefault(e => e.Length > 0)
                           ?? (fromSource ? _sourceFormat : _targetFormat).Endings.FirstOrDefault(e => e.Length > 0) ??
                           "\r\n";
            var isLast = ReferenceEquals(block, _blocks.LastOrDefault());
            var hadFinalNewline = fromSource ? _targetHasFinalNewline : _sourceHasFinalNewline;
            var copied = donor.Select((l, i) => new Line(l.Text,
                uniform ?? (i < before.Length && before[i].Ending.Length > 0 ? before[i].Ending : fallback))).ToList();
            if (isLast && copied.Count > 0)
                copied[^1] = copied[^1] with { Ending = "" };

            group.Add(new Edit
            {
                Block = block, DestinationSource = !fromSource, Before = before,
                BeforeFinalNewline = isLast ? hadFinalNewline : null
            });
            if (isLast)
            {
                if (fromSource) _targetHasFinalNewline = _sourceHasFinalNewline;
                else _sourceHasFinalNewline = _targetHasFinalNewline;
            }

            if (fromSource) block.Right = copied.ToArray();
            else block.Left = copied.ToArray();
            block.Rows = null;
        }

        if (group.Count > 0) _history.Add(group);
    }

    private static bool HasFinalNewline(TextDocument document)
    {
        return document.Lines.Length > 1 && document.Lines[^1] == "" && document.Endings[^1] == "";
    }

    private void Undo(Edit edit)
    {
        if (edit.BeforeFinalNewline is { } finalNewline)
        {
            if (edit.DestinationSource) _sourceHasFinalNewline = finalNewline;
            else _targetHasFinalNewline = finalNewline;
        }

        if (edit.DestinationSource) edit.Block.Left = edit.Before;
        else edit.Block.Right = edit.Before;
        edit.Block.Rows = edit.Block.Left.SequenceEqual(edit.Block.OriginalLeft) &&
                          edit.Block.Right.SequenceEqual(edit.Block.OriginalRight)
            ? edit.Block.OriginalRows
            : null;
        edit.Active = false;
    }

    internal void UndoHunk(int id, bool fromSource)
    {
        var edit = _history.SelectMany(g => g)
            .LastOrDefault(e => e.Active && e.Block.Id == id && e.DestinationSource == !fromSource);
        if (edit != null) Undo(edit);
    }

    internal void UndoLast()
    {
        var group = _history.LastOrDefault(g => g.Any(e => e.Active));
        if (group != null)
            foreach (var edit in group.Where(e => e.Active).Reverse())
                Undo(edit);
    }

    internal TextDocument Document(bool source)
    {
        var format = source ? _sourceFormat : _targetFormat;
        var lines = _blocks.SelectMany(b => source ? b.Left : b.Right).ToArray();
        // A line that used to be EOF may now precede inserted lines. Conversely,
        // deleting EOF must not silently add a final newline to a no-newline file.
        var endings = lines.Select(l => l.Ending).ToArray();
        var fallback = format.Endings.FirstOrDefault(e => e.Length > 0)
                       ?? (source ? _targetFormat : _sourceFormat).Endings.FirstOrDefault(e => e.Length > 0) ?? "\r\n";
        for (var i = 0; i + 1 < endings.Length; i++)
            if (endings[i].Length == 0)
                endings[i] = fallback;
        if (endings.Length > 0 && !(source ? _sourceHasFinalNewline : _targetHasFinalNewline))
            endings[^1] = "";

        return new TextDocument
        {
            Encoding = format.Encoding,
            EncodingName = format.EncodingName,
            HasBom = format.HasBom,
            OriginalBytes = format.OriginalBytes,
            Newlines = format.Newlines,
            Lines = lines.Select(l => l.Text).ToArray(),
            Endings = endings
        };
    }

    internal List<DiffLine> Rows()
    {
        var rows = new List<DiffLine>();
        int left = 0, right = 0;
        foreach (var block in _blocks)
        {
            if (block.Rows != null)
            {
                foreach (var row in block.Rows)
                {
                    if (row.LeftNumber > 0) row.LeftNumber = ++left;
                    if (row.RightNumber > 0) row.RightNumber = ++right;
                    rows.Add(row);
                }

                continue;
            }

            block.Rows = new List<DiffLine>();
            for (var i = 0; i < Math.Max(1, Math.Max(block.Left.Length, block.Right.Length)); i++)
                block.Rows.Add(new DiffLine
                {
                    EditHunkId = block.Id,
                    Kind = DiffLineKind.Modified,
                    Left = i < block.Left.Length ? block.Left[i].Text : null,
                    Right = i < block.Right.Length ? block.Right[i].Text : null,
                    LeftNumber = i < block.Left.Length ? ++left : 0,
                    RightNumber = i < block.Right.Length ? ++right : 0
                });
            rows.AddRange(block.Rows);
        }

        return rows;
    }

    internal sealed record Line(string Text, string Ending);

    private sealed class Block
    {
        internal int Id = -1;
        internal Line[] Left, Right;
        internal Line[] OriginalLeft, OriginalRight;
        internal List<DiffLine> OriginalRows;
        internal List<DiffLine> Rows;
    }

    private sealed class Edit
    {
        internal bool Active = true;
        internal Line[] Before;
        internal bool? BeforeFinalNewline;
        internal Block Block;
        internal bool DestinationSource;
    }
}