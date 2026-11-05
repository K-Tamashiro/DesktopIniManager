using System;
using System.Collections.Generic;
using System.Linq;

namespace DesktopIniManager.Services;

/// <summary>Identifies how a line differs between the source and target text.</summary>
internal enum DiffLineKind
{
    Unchanged,
    Added,
    Removed,
    Modified
}

/// <summary>Represents one aligned row in a side-by-side text comparison.</summary>
internal sealed class DiffLine
{
    public string Left { get; set; }
    public string Right { get; set; }
    public DiffLineKind Kind { get; set; }
    public int LeftNumber { get; set; }
    public int RightNumber { get; set; }
    public int FilteredLineCount { get; set; }
    internal DiffLine FilteredAnchor { get; set; }
    internal bool IsDesignerHeader { get; set; }
    internal int EditHunkId { get; set; } = -1;
    internal bool[] LeftChanges { get; set; }
    internal bool[] RightChanges { get; set; }
}

/// <summary>
///     Adapter between Matthias Hertel's O(ND) Diff engine and DIM's
///     side-by-side DiffLine model.
/// </summary>
internal static class DiffTextService
{
    public static List<DiffLine> Compare(
        string[] left,
        string[] right,
        bool ignoreCase = false,
        bool ignoreSpaces = false,
        string[] leftEndings = null,
        string[] rightEndings = null,
        bool calculateInlineDifferences = false,
        bool compareBlankLines = true)
    {
        left ??= [];
        right ??= [];

        if (left.Length == 0 && right.Length == 0)
            return [];

        // Normalize every line exactly once. Alignment always uses the normalized
        // form; the spacing option only decides whether a spacing-only row differs.
        var leftNormal = NormalizeLines(left);
        var rightNormal = NormalizeLines(right);
        var leftKeys = ignoreCase ? UpperLines(leftNormal) : leftNormal;
        var rightKeys = ignoreCase ? UpperLines(rightNormal) : rightNormal;
        var context = new RowContext(left, right, leftNormal, rightNormal, leftEndings, rightEndings,
            ignoreCase, ignoreSpaces, calculateInlineDifferences, compareBlankLines);

        if (compareBlankLines)
            return BuildRows(context, AlignLines(leftKeys, rightKeys), null, null);

        // 改行比較OFF: 改行のみの行は対応付けに参加させず、内容行だけで整列する。
        // その後、改行のみの行を元の位置へ単独行(Unchanged)として戻すため、
        // そこで Hunk は上下に分かれ、反対側も同じ位置で区切られる。
        var leftContent = ContentIndexes(left, leftNormal, ignoreSpaces);
        var rightContent = ContentIndexes(right, rightNormal, ignoreSpaces);
        var rows = BuildRows(context,
            AlignLines(Pick(leftKeys, leftContent), Pick(rightKeys, rightContent)),
            leftContent, rightContent);
        return InsertBlankRows(rows, left, right);
    }

    /// <summary>
    ///     空白無視時の比較キー。行頭・行末の空白を除き、行中の空白/タブの連続は1つの空白とする。
    ///     空白の有無そのものは残すため、"( - 4)" と "( -4)" は一致しない。
    /// </summary>
    internal static string NormalizeSpaces(string text)
    {
        if (string.IsNullOrEmpty(text)) return string.Empty;
        int start = 0, end = text.Length;
        while (start < end && IsEdgeSpace(text[start])) start++;
        while (end > start && IsEdgeSpace(text[end - 1])) end--;

        // Most lines need no rewrite; avoid allocating for them.
        var clean = true;
        for (var i = start; i < end; i++)
        {
            // text[end - 1] is not a space, so i + 1 < end whenever text[i] is a space.
            if (text[i] == '\t' || text[i] == ' ' && IsBodySpace(text[i + 1]))
            {
                clean = false;
                break;
            }
        }

        if (clean) return start == 0 && end == text.Length ? text : text.Substring(start, end - start);

        var buffer = new char[end - start];
        var length = 0;
        for (var i = start; i < end; i++)
        {
            if (!IsBodySpace(text[i]))
            {
                buffer[length++] = text[i];
                continue;
            }

            buffer[length++] = ' ';
            while (i + 1 < end && IsBodySpace(text[i + 1])) i++;
        }

        return new string(buffer, 0, length);
    }

    private static bool IsEdgeSpace(char c) => c is ' ' or '\t' or '\u3000';
    private static bool IsBodySpace(char c) => c is ' ' or '\t';

    private static string[] NormalizeLines(string[] lines)
    {
        var result = new string[lines.Length];
        for (var i = 0; i < lines.Length; i++) result[i] = NormalizeSpaces(lines[i]);
        return result;
    }

    private static string[] UpperLines(string[] lines)
    {
        var result = new string[lines.Length];
        for (var i = 0; i < lines.Length; i++) result[i] = lines[i].ToUpperInvariant();
        return result;
    }

    /// <summary>改行のみの行か。空白無視時は空白だけの行も改行のみとして扱う。</summary>
    private static bool IsBlank(string line, string normal, bool ignoreSpaces) =>
        ignoreSpaces ? normal.Length == 0 : line.Length == 0;

    // The last element after a final newline is the EOF pseudo-line. It stays a
    // content row so that EOF handling and the last edit block are unchanged.
    private static int[] ContentIndexes(string[] lines, string[] normal, bool ignoreSpaces)
    {
        var indexes = new List<int>(lines.Length);
        for (var i = 0; i < lines.Length; i++)
            if (i == lines.Length - 1 || !IsBlank(lines[i], normal[i], ignoreSpaces))
                indexes.Add(i);
        return indexes.ToArray();
    }

    private static string[] Pick(string[] lines, int[] indexes)
    {
        var result = new string[indexes.Length];
        for (var i = 0; i < indexes.Length; i++) result[i] = lines[indexes[i]];
        return result;
    }

    /// <summary>
    ///     除外した改行のみの行を元の位置に戻す。両側に同じ位置の改行のみ行があれば
    ///     1行に対にし、片側だけの行は反対側を空けた単独行にする。どちらも Unchanged。
    /// </summary>
    private static List<DiffLine> InsertBlankRows(List<DiffLine> rows, string[] left, string[] right)
    {
        var result = new List<DiffLine>(rows.Count + 16);
        int nextLeft = 0, nextRight = 0;

        void Flush(int leftEnd, int rightEnd)
        {
            while (nextLeft < leftEnd && nextRight < rightEnd)
            {
                result.Add(BlankRow(left[nextLeft], right[nextRight], nextLeft + 1, nextRight + 1));
                nextLeft++;
                nextRight++;
            }

            for (; nextLeft < leftEnd; nextLeft++)
                result.Add(BlankRow(left[nextLeft], null, nextLeft + 1, 0));
            for (; nextRight < rightEnd; nextRight++)
                result.Add(BlankRow(null, right[nextRight], 0, nextRight + 1));
        }

        foreach (var row in rows)
        {
            Flush(row.LeftNumber > 0 ? row.LeftNumber - 1 : nextLeft,
                row.RightNumber > 0 ? row.RightNumber - 1 : nextRight);
            result.Add(row);
            if (row.LeftNumber > 0) nextLeft = row.LeftNumber;
            if (row.RightNumber > 0) nextRight = row.RightNumber;
        }

        Flush(left.Length, right.Length);
        return result;
    }

    private static DiffLine BlankRow(string left, string right, int leftNumber, int rightNumber) => new()
    {
        Left = left,
        Right = right,
        LeftNumber = leftNumber,
        RightNumber = rightNumber,
        Kind = DiffLineKind.Unchanged
    };

    private static bool IsStructuralLine(string text)
    {
        foreach (var character in text)
            if (!char.IsWhiteSpace(character) && "{}()[];,".IndexOf(character) < 0)
                return false;
        return true;
    }

    private static Diff.Item[] AlignLines(string[] left, string[] right)
    {
        // Unique content lines are stronger anchors than repeated braces or blank lines.
        // Keep anchors in order (patience diff), then use Myers within each gap.
        static Dictionary<string, int> UniqueLines(string[] lines)
        {
            var positions = new Dictionary<string, int>(StringComparer.Ordinal);
            for (var index = 0; index < lines.Length; index++)
            {
                var text = lines[index];
                if (IsStructuralLine(text)) continue;
                if (!positions.TryAdd(text, index)) positions[text] = -1;
            }

            return positions;
        }

        var leftPositions = UniqueLines(left);
        var rightPositions = UniqueLines(right);

        // Encode every line once; each gap between anchors is then diffed as integer codes
        // instead of re-hashing the same strings per gap.
        var codeMap = new Dictionary<string, int>(left.Length + right.Length, StringComparer.Ordinal);

        int[] Encode(string[] lines)
        {
            var codes = new int[lines.Length];
            for (var i = 0; i < lines.Length; i++)
            {
                if (!codeMap.TryGetValue(lines[i], out var code))
                    codeMap.Add(lines[i], code = codeMap.Count + 1);
                codes[i] = code;
            }

            return codes;
        }

        var leftCodes = Encode(left);
        var rightCodes = Encode(right);
        var candidates = new List<(int Left, int Right)>();
        for (var index = 0; index < left.Length; index++)
            if (leftPositions.TryGetValue(left[index], out var position) && position == index &&
                rightPositions.TryGetValue(left[index], out var other) && other >= 0)
                candidates.Add((index, other));

        var tails = new List<int>();
        var predecessors = new int[candidates.Count];
        for (var index = 0; index < candidates.Count; index++)
        {
            var low = 0;
            var high = tails.Count;
            while (low < high)
            {
                var middle = (low + high) / 2;
                if (candidates[tails[middle]].Right < candidates[index].Right) low = middle + 1;
                else high = middle;
            }

            predecessors[index] = low > 0 ? tails[low - 1] : -1;
            if (low == tails.Count) tails.Add(index);
            else tails[low] = index;
        }

        var anchors = new List<(int Left, int Right)>();
        for (var index = tails.Count == 0 ? -1 : tails[^1]; index >= 0; index = predecessors[index])
            anchors.Add(candidates[index]);
        anchors.Reverse();
        anchors.Add((left.Length, right.Length));

        var differences = new List<Diff.Item>();
        var leftStart = 0;
        var rightStart = 0;
        foreach (var anchor in anchors)
        {
            if (anchor.Left > leftStart || anchor.Right > rightStart)
                foreach (var difference in Diff.DiffCodes(leftCodes[leftStart..anchor.Left],
                             rightCodes[rightStart..anchor.Right]))
                    differences.Add(difference with
                    {
                        StartA = difference.StartA + leftStart,
                        StartB = difference.StartB + rightStart
                    });
            leftStart = anchor.Left + 1;
            rightStart = anchor.Right + 1;
        }

        var merged = new List<Diff.Item>();
        foreach (var difference in differences)
        {
            if (merged.Count > 0)
            {
                var previous = merged[^1];
                var gapStart = previous.StartA + previous.DeletedA;
                var gapLength = difference.StartA - gapStart;
                // Include a short structural island in its surrounding edit. Otherwise a
                // lone unchanged brace splits one logical replacement into unsafe fragments.
                if (gapLength <= 3 && gapLength == difference.StartB - previous.StartB - previous.InsertedB &&
                    left.AsSpan(gapStart, gapLength).ToArray().All(IsStructuralLine))
                {
                    merged[^1] = previous with
                    {
                        DeletedA = difference.StartA + difference.DeletedA - previous.StartA,
                        InsertedB = difference.StartB + difference.InsertedB - previous.StartB
                    };
                    continue;
                }
            }

            merged.Add(difference);
        }

        return merged.ToArray();
    }

    private sealed record RowContext(
        string[] Left,
        string[] Right,
        string[] LeftNormal,
        string[] RightNormal,
        string[] LeftEndings,
        string[] RightEndings,
        bool IgnoreCase,
        bool IgnoreSpaces,
        bool CalculateInlineDifferences,
        bool CompareBlankLines);

    // leftIndexes/rightIndexes map aligned positions to original line indexes
    // when newline-only rows were excluded from alignment; null means identity.
    private static List<DiffLine> BuildRows(RowContext context, Diff.Item[] diffs, int[] leftIndexes,
        int[] rightIndexes)
    {
        var leftCount = leftIndexes?.Length ?? context.Left.Length;
        var rightCount = rightIndexes?.Length ?? context.Right.Length;
        var rows = new List<DiffLine>(Math.Max(leftCount, rightCount));
        var leftIndex = 0;
        var rightIndex = 0;
        var comparison = context.IgnoreCase ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;

        void AddRow(int alignedLeft, int alignedRight, DiffLineKind kind)
        {
            var l = alignedLeft < 0 ? -1 : leftIndexes?[alignedLeft] ?? alignedLeft;
            var r = alignedRight < 0 ? -1 : rightIndexes?[alignedRight] ?? alignedRight;
            var leftText = l < 0 ? null : context.Left[l];
            var rightText = r < 0 ? null : context.Right[r];
            bool[] leftChanges = null, rightChanges = null;
            if (!context.CompareBlankLines && IsBlankOrMissing(context, l, true) && IsBlankOrMissing(context, r, false))
                kind = DiffLineKind.Unchanged;
            else if (kind == DiffLineKind.Unchanged)
            {
                var same = context.IgnoreSpaces
                    ? string.Equals(context.LeftNormal[l], context.RightNormal[r], comparison)
                    : string.Equals(leftText, rightText, comparison);
                if (!same)
                    kind = DiffLineKind.Modified;
                else if (context.LeftEndings != null && context.RightEndings != null &&
                         !string.Equals(context.LeftEndings[l], context.RightEndings[r], StringComparison.Ordinal))
                    kind = DiffLineKind.Modified;
            }

            // Calculate once per aligned text change, shared by both panes.
            if (kind == DiffLineKind.Modified && context.CalculateInlineDifferences && leftText != null &&
                rightText != null)
                (leftChanges, rightChanges) = CompareCharacters(leftText, rightText, context.IgnoreSpaces);

            rows.Add(new DiffLine
            {
                Left = leftText,
                Right = rightText,
                LeftNumber = l + 1,
                RightNumber = r + 1,
                Kind = kind,
                LeftChanges = leftChanges,
                RightChanges = rightChanges
            });
        }

        foreach (var diff in diffs)
        {
            while (leftIndex < diff.StartA && rightIndex < diff.StartB)
                AddRow(leftIndex++, rightIndex++, DiffLineKind.Unchanged);

            var deletedEnd = diff.StartA + diff.DeletedA;
            var insertedEnd = diff.StartB + diff.InsertedB;

            leftIndex = diff.StartA;
            rightIndex = diff.StartB;

            while (leftIndex < deletedEnd && rightIndex < insertedEnd)
                AddRow(leftIndex++, rightIndex++, DiffLineKind.Modified);

            while (leftIndex < deletedEnd)
                AddRow(leftIndex++, -1, DiffLineKind.Removed);

            while (rightIndex < insertedEnd)
                AddRow(-1, rightIndex++, DiffLineKind.Added);
        }

        while (leftIndex < leftCount && rightIndex < rightCount)
            AddRow(leftIndex++, rightIndex++, DiffLineKind.Unchanged);

        return rows;
    }

    private static bool IsBlankOrMissing(RowContext context, int index, bool source)
    {
        if (index < 0) return true;
        return source
            ? IsBlank(context.Left[index], context.LeftNormal[index], context.IgnoreSpaces)
            : IsBlank(context.Right[index], context.RightNormal[index], context.IgnoreSpaces);
    }

    // One comparison token per displayed character (tabs expanded to four
    // columns), or, when spacing is ignored, one token per whitespace run.
    // Start/End are display offsets, so a changed run marks every column it covers.
    private readonly record struct CharacterToken(int Code, int Start, int End);

    private static List<CharacterToken> CharacterTokens(string text, bool ignoreSpaces, out int displayLength)
    {
        var tokens = new List<CharacterToken>(text.Length);
        int start = 0, end = text.Length;
        if (ignoreSpaces)
        {
            while (start < end && IsEdgeSpace(text[start])) start++;
            while (end > start && IsEdgeSpace(text[end - 1])) end--;
        }

        var offset = 0;
        for (var i = 0; i < text.Length; i++)
        {
            var width = text[i] == '\t' ? 4 : 1;
            if (i >= start && i < end)
            {
                if (ignoreSpaces && IsBodySpace(text[i]))
                {
                    if (tokens.Count > 0 && tokens[^1].Code == ' ' && tokens[^1].End == offset &&
                        i > start && IsBodySpace(text[i - 1]))
                        tokens[^1] = tokens[^1] with { End = offset + width };
                    else
                        tokens.Add(new CharacterToken(' ', offset, offset + width));
                }
                else if (text[i] == '\t')
                    for (var n = 0; n < width; n++)
                        tokens.Add(new CharacterToken(' ', offset + n, offset + n + 1));
                else
                    tokens.Add(new CharacterToken(text[i], offset, offset + 1));
            }

            offset += width;
        }

        displayLength = offset;
        return tokens;
    }

    private static (bool[] Left, bool[] Right) CompareCharacters(string left, string right, bool ignoreSpaces)
    {
        var leftTokens = CharacterTokens(left, ignoreSpaces, out var leftLength);
        var rightTokens = CharacterTokens(right, ignoreSpaces, out var rightLength);
        var leftCodes = new int[leftTokens.Count];
        var rightCodes = new int[rightTokens.Count];
        for (var i = 0; i < leftCodes.Length; i++) leftCodes[i] = leftTokens[i].Code;
        for (var i = 0; i < rightCodes.Length; i++) rightCodes[i] = rightTokens[i].Code;

        // Characters are their own equality codes: no per-character strings.
        var changes = Diff.DiffCodes(leftCodes, rightCodes);
        var a = new bool[leftLength];
        var b = new bool[rightLength];
        foreach (var change in changes)
        {
            for (var i = change.StartA; i < change.StartA + change.DeletedA; i++)
                for (var n = leftTokens[i].Start; n < leftTokens[i].End; n++) a[n] = true;
            for (var i = change.StartB; i < change.StartB + change.InsertedB; i++)
                for (var n = rightTokens[i].Start; n < rightTokens[i].End; n++) b[n] = true;
        }

        return (a, b);
    }
}
