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
        bool calculateInlineDifferences = false)
    {
        left ??= [];
        right ??= [];

        if (left.Length == 0 && right.Length == 0)
            return [];

        // Align content independently of spacing and line endings. Refine same rows below.
        string[] Keys(string[] lines)
        {
            var keys = new string[lines.Length];

            for (var i = 0; i < lines.Length; i++)
            {
                var key = lines[i];

                key = key.Replace(" ", "").Replace("\t", "");

                if (ignoreCase)
                    key = key.ToUpperInvariant();

                keys[i] = key;
            }

            return keys;
        }

        var leftKeys = Keys(left);
        var rightKeys = Keys(right);
        var diffs = AlignLines(leftKeys, rightKeys);

        // 行の対応が確定した後で改行コードを比較する。
        return BuildRows(left, right, diffs, leftEndings, rightEndings, ignoreCase, ignoreSpaces,
            calculateInlineDifferences);
    }

    private static bool IsStructuralLine(string text)
    {
        return text.All(character => char.IsWhiteSpace(character) || "{}()[];,".Contains(character));
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
                foreach (var difference in Diff.DiffLines(left[leftStart..anchor.Left], right[rightStart..anchor.Right]))
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

    private static List<DiffLine> BuildRows(
        string[] left,
        string[] right,
        Diff.Item[] diffs,
        string[] leftEndings,
        string[] rightEndings,
        bool ignoreCase,
        bool ignoreSpaces,
        bool calculateInlineDifferences)
    {
        var rows = new List<DiffLine>(Math.Max(left.Length, right.Length));
        var leftIndex = 0;
        var rightIndex = 0;

        // 改行比較が有効な場合のみ判定する。
        var comparison = ignoreCase ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;

        void AddRow(string l, string r, int lNum, int rNum, DiffLineKind kind)
        {
            bool[] leftChanges = null, rightChanges = null;
            if (kind == DiffLineKind.Modified)
            {
                // Calculate once per aligned text change, shared by both panes.
                if (calculateInlineDifferences)
                    (leftChanges, rightChanges) = CompareCharacters(l, r, ignoreSpaces);
            }
            else if (kind == DiffLineKind.Unchanged)
            {
                if (!ignoreSpaces && !string.Equals(l, r, comparison))
                    kind = DiffLineKind.Modified;
                else if (leftEndings != null && rightEndings != null && !string.Equals(leftEndings[lNum - 1],
                             rightEndings[rNum - 1],
                             StringComparison.Ordinal))
                    kind = DiffLineKind.Modified;
            }

            rows.Add(new DiffLine
            {
                Left = l,
                Right = r,
                LeftNumber = lNum,
                RightNumber = rNum,
                Kind = kind,
                LeftChanges = leftChanges,
                RightChanges = rightChanges
            });
        }

        foreach (var diff in diffs)
        {
            while (leftIndex < diff.StartA && rightIndex < diff.StartB)
            {
                AddRow(left[leftIndex], right[rightIndex],
                    leftIndex + 1, rightIndex + 1, DiffLineKind.Unchanged);
                leftIndex++;
                rightIndex++;
            }

            var deletedEnd = diff.StartA + diff.DeletedA;
            var insertedEnd = diff.StartB + diff.InsertedB;

            leftIndex = diff.StartA;
            rightIndex = diff.StartB;

            while (leftIndex < deletedEnd && rightIndex < insertedEnd)
            {
                AddRow(left[leftIndex], right[rightIndex],
                    leftIndex + 1, rightIndex + 1, DiffLineKind.Modified);
                leftIndex++;
                rightIndex++;
            }

            while (leftIndex < deletedEnd)
            {
                AddRow(left[leftIndex], null,
                    leftIndex + 1, 0, DiffLineKind.Removed);
                leftIndex++;
            }

            while (rightIndex < insertedEnd)
            {
                AddRow(null, right[rightIndex],
                    0, rightIndex + 1, DiffLineKind.Added);
                rightIndex++;
            }
        }

        while (leftIndex < left.Length && rightIndex < right.Length)
        {
            AddRow(left[leftIndex], right[rightIndex],
                leftIndex + 1, rightIndex + 1, DiffLineKind.Unchanged);
            leftIndex++;
            rightIndex++;
        }

        return rows;
    }

    private static (bool[] Left, bool[] Right) CompareCharacters(string left, string right, bool ignoreSpaces)
    {
        left = left.Replace("\t", "    ");
        right = right.Replace("\t", "    ");
        var changes = Diff.DiffLines(left.Select(c => c.ToString()).ToArray(),
            right.Select(c => c.ToString()).ToArray());
        var a = new bool[left.Length];
        var b = new bool[right.Length];
        foreach (var change in changes)
        {
            for (var i = change.StartA; i < change.StartA + change.DeletedA; i++)
                a[i] = !ignoreSpaces || left[i] != '\u0020';
            for (var i = change.StartB; i < change.StartB + change.InsertedB; i++)
                b[i] = !ignoreSpaces || right[i] != '\u0020';
        }

        return (a, b);
    }
}
