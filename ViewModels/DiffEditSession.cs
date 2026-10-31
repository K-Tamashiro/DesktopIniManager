using DesktopIniManager.Services;
using System;
using System.Collections.Generic;
using System.Linq;
using TextDocument = DesktopIniManager.ViewModels.DiffViewModel.TextDocument;

namespace DesktopIniManager.ViewModels;

// Keep the comparison's hunk boundaries until explicit recompare/save.
internal sealed class DiffEditSession
{
    private readonly List<Block> blocks = new();
    private readonly List<List<Edit>> history = new();
    private readonly TextDocument sourceFormat, targetFormat;

    internal DiffEditSession(TextDocument source, TextDocument target, List<DiffLine> rows)
    {
        sourceFormat = source;
        targetFormat = target;
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
            blocks.Add(block);
        }
    }

    internal bool CanUndo(int id, bool fromSource)
    {
        return history.SelectMany(g => g)
            .Any(e => e.Active && e.Block.Id == id && e.DestinationSource == !fromSource);
    }

    internal void Copy(IEnumerable<int> ids, bool fromSource)
    {
        var selected = ids.ToHashSet();
        var group = new List<Edit>();
        foreach (var block in blocks.Where(b => b.Id >= 0 && selected.Contains(b.Id)))
        {
            if (CanUndo(block.Id, fromSource)) continue;
            var before = fromSource ? block.Right : block.Left;
            var donor = fromSource ? block.Left : block.Right;
            var format = fromSource ? targetFormat : sourceFormat;
            // Mixed files retain each existing destination ending. Additional
            // lines inherit a nearby ending. Uniform files keep their format.
            var uniform = format.Newlines.Contains('/') ? null : format.Endings.FirstOrDefault(e => e.Length > 0);
            var fallback = before.Select(l => l.Ending).FirstOrDefault(e => e.Length > 0)
                           ?? format.Endings.FirstOrDefault(e => e.Length > 0)
                           ?? (fromSource ? sourceFormat : targetFormat).Endings.FirstOrDefault(e => e.Length > 0) ??
                           "\r\n";
            var isLast = ReferenceEquals(block, blocks.LastOrDefault());
            var hadFinalNewline = format.Lines.Length > 1 && format.Lines[^1] == "" && format.Endings[^1] == "";
            var copied = donor.Select((l, i) => new Line(l.Text,
                uniform ?? (i < before.Length && before[i].Ending.Length > 0 ? before[i].Ending : fallback))).ToList();
            if (isLast && copied.Count > 0)
            {
                if (donor[^1].Text == "" && donor[^1].Ending == "") copied.RemoveAt(copied.Count - 1);
                if (copied.Count > 0)
                {
                    if (hadFinalNewline) copied.Add(new Line("", ""));
                    else copied[^1] = copied[^1] with { Ending = "" };
                }
            }

            group.Add(new Edit { Block = block, DestinationSource = !fromSource, Before = before });
            if (fromSource) block.Right = copied.ToArray();
            else block.Left = copied.ToArray();
            block.Rows = null;
        }

        if (group.Count > 0) history.Add(group);
    }

    private static void Undo(Edit edit)
    {
        if (edit.DestinationSource) edit.Block.Left = edit.Before;
        else edit.Block.Right = edit.Before;
        edit.Block.Rows = null;
        edit.Active = false;
    }

    internal void UndoHunk(int id, bool fromSource)
    {
        var edit = history.SelectMany(g => g)
            .LastOrDefault(e => e.Active && e.Block.Id == id && e.DestinationSource == !fromSource);
        if (edit != null) Undo(edit);
    }

    internal void UndoLast()
    {
        var group = history.LastOrDefault(g => g.Any(e => e.Active));
        if (group != null)
            foreach (var edit in group.Where(e => e.Active).Reverse())
                Undo(edit);
    }

    internal TextDocument Document(bool source)
    {
        var format = source ? sourceFormat : targetFormat;
        var lines = blocks.SelectMany(b => source ? b.Left : b.Right).ToArray();
        // A line that used to be EOF may now precede inserted lines. Conversely,
        // deleting EOF must not silently add a final newline to a no-newline file.
        var endings = lines.Select(l => l.Ending).ToArray();
        var fallback = format.Endings.FirstOrDefault(e => e.Length > 0)
                       ?? (source ? targetFormat : sourceFormat).Endings.FirstOrDefault(e => e.Length > 0) ?? "\r\n";
        for (var i = 0; i + 1 < endings.Length; i++)
            if (endings[i].Length == 0)
                endings[i] = fallback;
        if (endings.Length > 0 && format.Lines.Length > 0)
        {
            var finalNewline = format.Lines.Length > 1 && format.Lines[^1] == "" && format.Endings[^1] == "";
            if (!finalNewline) endings[^1] = "";
        }

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
        foreach (var block in blocks)
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

            for (var i = 0; i < Math.Max(1, Math.Max(block.Left.Length, block.Right.Length)); i++)
                rows.Add(new DiffLine
                {
                    EditHunkId = block.Id,
                    Kind = DiffLineKind.Modified,
                    Left = i < block.Left.Length ? block.Left[i].Text : null,
                    Right = i < block.Right.Length ? block.Right[i].Text : null,
                    LeftNumber = i < block.Left.Length ? ++left : 0,
                    RightNumber = i < block.Right.Length ? ++right : 0
                });
        }

        return rows;
    }

    internal sealed record Line(string Text, string Ending);

    private sealed class Block
    {
        internal int Id = -1;
        internal Line[] Left, Right;
        internal List<DiffLine> Rows;
    }

    private sealed class Edit
    {
        internal bool Active = true;
        internal Line[] Before;
        internal Block Block;
        internal bool DestinationSource;
    }
}