using System;
using System.Collections.Generic;
using System.Linq;

namespace DesktopIniManager.Services;

/// <summary>Identifies how a line differs between the source and target text.</summary>
internal enum DiffLineKind { Unchanged, Added, Removed, Modified }

/// <summary>Represents one aligned row in a side-by-side text comparison.</summary>
internal sealed class DiffLine
{
    public string Left { get; set; }
    public string Right { get; set; }
    public DiffLineKind Kind { get; set; }
    public int LeftNumber { get; set; }
    public int RightNumber { get; set; }
    public int FilteredLineCount { get; set; }
    internal bool[] LeftChanges { get; set; }
    internal bool[] RightChanges { get; set; }
}

/// <summary>
/// Adapter between Matthias Hertel's O(ND) Diff engine and DIM's
/// side-by-side DiffLine model.
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

            for (int i = 0; i < lines.Length; i++)
            {
                string key = lines[i];

                key = key.Replace(" ", "").Replace("\t", "");

                if (ignoreCase)
                    key = key.ToUpperInvariant();

                keys[i] = key;
            }

            return keys;
        }

        Diff.Item[] diffs = Diff.DiffLines(Keys(left), Keys(right));

        // 行の対応が確定した後で改行コードを比較する。
        return BuildRows(left, right, diffs, leftEndings, rightEndings, ignoreCase, ignoreSpaces, calculateInlineDifferences);
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
        int leftIndex = 0;
        int rightIndex = 0;

        // 改行比較が有効な場合のみ判定する。
        bool compareNewlines = leftEndings != null && rightEndings != null;
        StringComparison comparison = ignoreCase ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;

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
                else if (compareNewlines && !string.Equals(leftEndings[lNum - 1], rightEndings[rNum - 1], StringComparison.Ordinal))
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

            int deletedEnd = diff.StartA + diff.DeletedA;
            int insertedEnd = diff.StartB + diff.InsertedB;

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
        var changes = Diff.DiffLines(left.Select(c => c.ToString()).ToArray(), right.Select(c => c.ToString()).ToArray());
        var a = new bool[left.Length];
        var b = new bool[right.Length];
        foreach (var change in changes)
        {
            for (int i = change.StartA; i < change.StartA + change.DeletedA; i++)
                a[i] = !ignoreSpaces || left[i] != '\u0020';
            for (int i = change.StartB; i < change.StartB + change.InsertedB; i++)
                b[i] = !ignoreSpaces || right[i] != '\u0020';
        }
        return (a, b);
    }
}
