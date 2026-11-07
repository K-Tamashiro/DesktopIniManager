using DesktopIniManager.Services;
using System;
using System.Collections.Generic;
using System.Linq;
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

    /// <summary>
    ///     比較結果の行を、変更ブロック・変更なしブロック・改行のみのギャップに分け、HUNK 単位で編集できるセッションを作成します。
    /// </summary>
    /// <param name="source">比較元の文書。</param>
    /// <param name="target">比較先の文書。</param>
    /// <param name="rows">比較結果の行。</param>
    internal DiffEditSession(TextDocument source, TextDocument target, List<DiffLine> rows)
    {
        _sourceFormat = source;
        _targetFormat = target;
        _sourceHasFinalNewline = HasFinalNewline(source);
        _targetHasFinalNewline = HasFinalNewline(target);
        var id = 0;
        for (var i = 0; i < rows.Count;)
        {
            var kind = BlockKind(rows[i], source, target);
            var part = new List<DiffLine>();
            do
            {
                part.Add(rows[i++]);
            } while (i < rows.Count && BlockKind(rows[i], source, target) == kind);

            var block = new Block { Id = kind == ChangedBlock ? id++ : -1, IsGap = kind == GapBlock, Rows = part };
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

    private const int UnchangedBlock = 0, ChangedBlock = 1, GapBlock = 2;

    // A gap is a run of unchanged newline-only rows present on one side only
    // (newline comparison off). It is never a hunk, but separates two hunks.
    // The EOF pseudo-line keeps its existing final-newline handling.
    /// <summary>
    ///     行が属するブロックの種類を判定します。ギャップは、改行比較 OFF のときに片側だけにある改行のみの行の連続で、HUNK にはなりませんが 2 つの HUNK を区切ります。
    /// </summary>
    /// <param name="row">判定する行。</param>
    /// <param name="source">比較元の文書。</param>
    /// <param name="target">比較先の文書。</param>
    /// <returns>ブロックの種類。</returns>
    private static int BlockKind(DiffLine row, TextDocument source, TextDocument target)
    {
        if (row.Kind != DiffLineKind.Unchanged) return ChangedBlock;
        if ((row.LeftNumber > 0) == (row.RightNumber > 0)) return UnchangedBlock;
        return row.LeftNumber > 0
            ? row.LeftNumber < source.Lines.Length ? GapBlock : UnchangedBlock
            : row.RightNumber < target.Lines.Length ? GapBlock : UnchangedBlock;
    }

    /// <summary>
    ///     指定した HUNK の、指定方向へのコピーを元に戻せるかどうかを判定します。
    /// </summary>
    /// <param name="id">HUNK の ID。</param>
    /// <param name="fromSource">比較元から比較先へのコピーの場合は true。</param>
    /// <returns>元に戻せる場合は true。</returns>
    internal bool CanUndo(int id, bool fromSource)
    {
        return _history.SelectMany(g => g)
            .Any(e => e.Active && e.HunkId == id && e.DestinationSource == !fromSource);
    }

    /// <summary>
    ///     選択した HUNK の内容を反対側へコピーし、隣接する改行のみのギャップも同期して、1 つの編集グループとして履歴に記録します。
    /// </summary>
    /// <param name="ids">コピーする HUNK の ID。</param>
    /// <param name="fromSource">比較元から比較先へコピーする場合は true。</param>
    internal void Copy(IEnumerable<int> ids, bool fromSource)
    {
        var selected = ids.ToHashSet();
        var group = new List<Edit>();
        foreach (var block in _blocks.Where(b => b.Id >= 0 && selected.Contains(b.Id)))
        {
            if (CanUndo(block.Id, fromSource)) continue;
            var before = fromSource ? block.Right : block.Left;
            var donor = fromSource ? block.Left : block.Right;
            var isLast = ReferenceEquals(block, _blocks.LastOrDefault());
            var hadFinalNewline = fromSource ? _targetHasFinalNewline : _sourceHasFinalNewline;
            var copied = Adapt(donor, before, fromSource);
            if (isLast && copied.Count > 0)
                copied[^1] = copied[^1] with { Ending = "" };

            group.Add(new Edit
            {
                Block = block,
                HunkId = block.Id,
                DestinationSource = !fromSource,
                Before = before,
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

        CopyGaps(selected, fromSource, group);
        if (group.Count > 0) _history.Add(group);
    }

    // Newline-only gaps next to a copied hunk follow the copy direction:
    // - one adjacent hunk copied: the destination gap becomes the donor gap
    //   (source -> target inserts the blank rows, target -> source removes them);
    // - hunks on both sides copied together: the gap is not copied, so the
    //   destination receives one contiguous block without newline-only rows.
    // Rows matched as newline-only on both sides are never gaps and are kept.
    /// <summary>
    ///     コピーした HUNK に隣接する改行のみのギャップを、コピーの方向に合わせて同期します。
    ///     片側の HUNK だけをコピーした場合はコピー元のギャップに合わせ、両側の HUNK を同時にコピーした場合はギャップを除いて 1 つの連続したブロックにします。
    /// </summary>
    /// <param name="selected">コピーする HUNK の ID。</param>
    /// <param name="fromSource">比較元から比較先へコピーする場合は true。</param>
    /// <param name="group">編集の追加先のグループ。</param>
    private void CopyGaps(HashSet<int> selected, bool fromSource, List<Edit> group)
    {
        for (var i = 0; i < _blocks.Count; i++)
        {
            var gap = _blocks[i];
            if (!gap.IsGap) continue;
            var previous = i > 0 && _blocks[i - 1].Id >= 0 && selected.Contains(_blocks[i - 1].Id)
                ? _blocks[i - 1]
                : null;
            var next = i + 1 < _blocks.Count && _blocks[i + 1].Id >= 0 && selected.Contains(_blocks[i + 1].Id)
                ? _blocks[i + 1]
                : null;
            if (previous == null && next == null) continue;
            if (_history.SelectMany(g => g).Concat(group)
                .Any(e => e.Active && ReferenceEquals(e.Block, gap) && e.DestinationSource == !fromSource))
                continue;

            var before = fromSource ? gap.Right : gap.Left;
            var after = previous != null && next != null
                ? Array.Empty<Line>()
                : Adapt(fromSource ? gap.Left : gap.Right, before, fromSource).ToArray();
            if (after.SequenceEqual(before)) continue;

            group.Add(new Edit
            {
                Block = gap,
                HunkId = (previous ?? next).Id,
                DestinationSource = !fromSource,
                Before = before
            });
            if (fromSource) gap.Right = after;
            else gap.Left = after;
            gap.Rows = null;
        }
    }

    // Mixed files retain each existing destination ending. Additional
    // lines inherit a nearby ending. Uniform files keep their format.
    /// <summary>
    ///     コピーする行の改行コードを、コピー先のファイルに合わせます。改行コードが統一されたファイルはその形式を保ち、混在するファイルは既存の行の改行コードを引き継ぎます。
    /// </summary>
    /// <param name="donor">コピー元の行。</param>
    /// <param name="before">コピー先の置き換え前の行。</param>
    /// <param name="fromSource">比較元から比較先へコピーする場合は true。</param>
    /// <returns>改行コードを調整した行。</returns>
    private List<Line> Adapt(Line[] donor, Line[] before, bool fromSource)
    {
        var format = fromSource ? _targetFormat : _sourceFormat;
        var uniform = format.Newlines.Contains('/') ? null : format.Endings.FirstOrDefault(e => e.Length > 0);
        var fallback = before.Select(l => l.Ending).FirstOrDefault(e => e.Length > 0)
                       ?? format.Endings.FirstOrDefault(e => e.Length > 0)
                       ?? (fromSource ? _sourceFormat : _targetFormat).Endings.FirstOrDefault(e => e.Length > 0) ??
                       "\r\n";
        return donor.Select((l, i) => new Line(l.Text,
            uniform ?? (i < before.Length && before[i].Ending.Length > 0 ? before[i].Ending : fallback))).ToList();
    }

    /// <summary>
    ///     文書が最終行の末尾に改行を持つかどうかを判定します。
    /// </summary>
    /// <param name="document">判定する文書。</param>
    /// <returns>末尾に改行がある場合は true。</returns>
    private static bool HasFinalNewline(TextDocument document)
    {
        return document.Lines.Length > 1 && document.Lines[^1] == "" && document.Endings[^1] == "";
    }

    /// <summary>
    ///     1 つの編集を元に戻し、ブロックが元の状態に戻った場合は元の行を復元します。
    /// </summary>
    /// <param name="edit">元に戻す編集。</param>
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

    /// <summary>
    ///     指定した HUNK の最後のコピーを、同期したギャップと一緒に元に戻します。
    /// </summary>
    /// <param name="id">HUNK の ID。</param>
    /// <param name="fromSource">比較元から比較先へのコピーの場合は true。</param>
    internal void UndoHunk(int id, bool fromSource)
    {
        // A hunk copy and the newline-only gaps it synchronized are undone together.
        var group = _history.LastOrDefault(g =>
            g.Any(e => e.Active && e.HunkId == id && e.DestinationSource == !fromSource));
        if (group == null) return;
        foreach (var edit in group.Where(e => e.Active && e.HunkId == id && e.DestinationSource == !fromSource)
                     .Reverse())
            Undo(edit);
    }

    /// <summary>
    ///     最後に行った有効な編集グループを元に戻します。
    /// </summary>
    internal void UndoLast()
    {
        var group = _history.LastOrDefault(g => g.Any(e => e.Active));
        if (group != null)
            foreach (var edit in group.Where(e => e.Active).Reverse())
                Undo(edit);
    }

    /// <summary>
    ///     編集後のブロックから、元のエンコーディングと最終行の改行の有無を保った文書を組み立てます。
    /// </summary>
    /// <param name="source">比較元の文書の場合は true。</param>
    /// <returns>編集後の文書。</returns>
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

    /// <summary>
    ///     編集後のブロックから表示用の行を組み立て、行番号を振り直します。
    /// </summary>
    /// <returns>表示用の行の一覧。</returns>
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
            if (block.IsGap)
            {
                // Gaps stay unchanged rows: paired when both sides now hold them.
                for (var i = 0; i < Math.Max(block.Left.Length, block.Right.Length); i++)
                    block.Rows.Add(new DiffLine
                    {
                        Kind = DiffLineKind.Unchanged,
                        Left = i < block.Left.Length ? block.Left[i].Text : null,
                        Right = i < block.Right.Length ? block.Right[i].Text : null,
                        LeftNumber = i < block.Left.Length ? ++left : 0,
                        RightNumber = i < block.Right.Length ? ++right : 0
                    });
                rows.AddRange(block.Rows);
                continue;
            }

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
        internal bool IsGap;
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
        internal int HunkId = -1;
    }
}