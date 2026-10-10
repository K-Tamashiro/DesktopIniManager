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
}

/// <summary>
/// Adapter between Matthias Hertel's O(ND) Diff engine and DIM's
/// side-by-side DiffLine model.
/// </summary>
internal static class DiffTextService
{
    public static List<DiffLine> Compare(string[] left, string[] right, bool ignoreCase = false, bool ignoreSpaces = false, string[] leftEndings = null, string[] rightEndings = null)
    {
        left ??= [];
        right ??= [];

        if (left.Length == 0 && right.Length == 0)
            return [];

        // Normalize comparison keys only; preserve original lines for display and keep empty arrays distinct from empty lines.
        string[] Keys(string[] lines, string[] endings) => lines.Select((line, i) =>
        {
            string key = ignoreSpaces ? line.Replace(" ", "").Replace("\t", "") : line;
            if (ignoreCase) key = key.ToUpperInvariant();
            return endings == null ? key : key + "\0" + endings[i];
        }).ToArray();
        Diff.Item[] diffs = Diff.DiffLines(Keys(left, leftEndings), Keys(right, rightEndings));

        return BuildRows(left, right, diffs);
    }

    private static List<DiffLine> BuildRows(
        string[] left,
        string[] right,
        Diff.Item[] diffs)
    {
        var rows = new List<DiffLine>(Math.Max(left.Length, right.Length));
        int leftIndex = 0;
        int rightIndex = 0;

        void AddRow(string l, string r, int lNum, int rNum, DiffLineKind kind) =>
            rows.Add(new DiffLine
            {
                Left = l,
                Right = r,
                LeftNumber = lNum,
                RightNumber = rNum,
                Kind = kind
            });

        foreach (var diff in diffs)
        {
            while (leftIndex < diff.StartA && rightIndex < diff.StartB)
            {
                AddRow(left[leftIndex], right[rightIndex], leftIndex + 1, rightIndex + 1, DiffLineKind.Unchanged);
                leftIndex++;
                rightIndex++;
            }

            int deletedEnd = diff.StartA + diff.DeletedA;
            int insertedEnd = diff.StartB + diff.InsertedB;
            leftIndex = diff.StartA;
            rightIndex = diff.StartB;
            while (leftIndex < deletedEnd && rightIndex < insertedEnd)
            {
                AddRow(left[leftIndex], right[rightIndex], leftIndex + 1, rightIndex + 1, DiffLineKind.Modified);
                leftIndex++;
                rightIndex++;
            }
            while (leftIndex < deletedEnd)
            {
                AddRow(left[leftIndex], null, leftIndex + 1, 0, DiffLineKind.Removed);
                leftIndex++;
            }
            while (rightIndex < insertedEnd)
            {
                AddRow(null, right[rightIndex], 0, rightIndex + 1, DiffLineKind.Added);
                rightIndex++;
            }
        }
        while (leftIndex < left.Length && rightIndex < right.Length)
        {
            AddRow(left[leftIndex], right[rightIndex], leftIndex + 1, rightIndex + 1, DiffLineKind.Unchanged);
            leftIndex++;
            rightIndex++;
        }

        return rows;
    }
}
