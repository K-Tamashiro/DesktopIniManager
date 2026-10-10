// SPDX-License-Identifier: BSD-3-Clause
// Copyright (c) by Matthias Hertel, http://www.mathertel.de
// Copyright (c) 2023, Matthias Hertel
// Derived from https://github.com/mathertel/Diff/blob/main/Diff.cs.
// DIM modifications: typed collections, immutable results, direct line-array API,
// invariant case handling and modern C# naming. See docs/licenses/Mathertel-Diff-LICENSE.txt
// for the full copyright notice, redistribution conditions and disclaimer.
using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;

namespace DesktopIniManager.Services;

/// <summary>
/// An O(ND) Difference Algorithm (Eugene Myers, 1986).
/// Modernized and optimized for side-by-side text diffing.
/// </summary>
internal static class Diff
{
    public readonly record struct Item(int StartA, int StartB, int DeletedA, int InsertedB);

    private readonly record struct MiddleSnake(int X, int Y);

    /// <summary>
    /// 行配列を直接受け取り、無駄な結合・再分割なしで差分を計算します。
    /// </summary>
    public static Item[] DiffLines(
        string[] linesA,
        string[] linesB,
        bool trimSpace = false,
        bool ignoreSpace = false,
        bool ignoreCase = false)
    {
        ArgumentNullException.ThrowIfNull(linesA);
        ArgumentNullException.ThrowIfNull(linesB);
        var codeMap = new Dictionary<string, int>(linesA.Length + linesB.Length, StringComparer.Ordinal);

        var dataA = new DiffData(EncodeLines(linesA, codeMap, trimSpace, ignoreSpace, ignoreCase));
        var dataB = new DiffData(EncodeLines(linesB, codeMap, trimSpace, ignoreSpace, ignoreCase));

        int max = dataA.Length + dataB.Length + 1;
        int[] downVector = new int[2 * max + 2];
        int[] upVector = new int[2 * max + 2];

        MarkLongestCommonSubsequence(dataA, 0, dataA.Length, dataB, 0, dataB.Length, downVector, upVector);

        Optimize(dataA);
        Optimize(dataB);

        return CreateDiffs(dataA, dataB);
    }

    private static int[] EncodeLines(
        string[] lines,
        Dictionary<string, int> codeMap,
        bool trimSpace,
        bool ignoreSpace,
        bool ignoreCase)
    {
        var codes = new int[lines.Length];

        for (int i = 0; i < lines.Length; i++)
        {
            string s = lines[i];

            if (trimSpace)
                s = s.Trim();

            if (ignoreSpace)
                s = Regex.Replace(s, @"\s+", " ");

            if (ignoreCase)
                s = s.ToLowerInvariant();

            if (!codeMap.TryGetValue(s, out int code))
            {
                code = codeMap.Count + 1;
                codeMap[s] = code;
            }

            codes[i] = code;
        }

        return codes;
    }

    private static void Optimize(DiffData data)
    {
        int startPos = 0;
        while (startPos < data.Length)
        {
            while (startPos < data.Length && !data.Modified[startPos])
                startPos++;

            int endPos = startPos;
            while (endPos < data.Length && data.Modified[endPos])
                endPos++;

            if (endPos < data.Length && data.Data[startPos] == data.Data[endPos])
            {
                data.Modified[startPos] = false;
                data.Modified[endPos] = true;
            }
            else
            {
                startPos = endPos;
            }
        }
    }

    private static MiddleSnake FindMiddleSnake(
        DiffData dataA, int lowerA, int upperA,
        DiffData dataB, int lowerB, int upperB,
        int[] downVector, int[] upVector)
    {
        int max = dataA.Length + dataB.Length + 1;
        int downK = lowerA - lowerB;
        int upK = upperA - upperB;
        int delta = (upperA - lowerA) - (upperB - lowerB);
        bool oddDelta = (delta & 1) != 0;

        int downOffset = max - downK;
        int upOffset = max - upK;
        int maxD = ((upperA - lowerA + upperB - lowerB) / 2) + 1;

        downVector[downOffset + downK + 1] = lowerA;
        upVector[upOffset + upK - 1] = upperA;

        for (int d = 0; d <= maxD; d++)
        {
            for (int k = downK - d; k <= downK + d; k += 2)
            {
                int x;
                if (k == downK - d)
                    x = downVector[downOffset + k + 1];
                else
                {
                    x = downVector[downOffset + k - 1] + 1;
                    if (k < downK + d && downVector[downOffset + k + 1] >= x)
                        x = downVector[downOffset + k + 1];
                }

                int y = x - k;
                while (x < upperA && y < upperB && dataA.Data[x] == dataB.Data[y])
                {
                    x++;
                    y++;
                }
                downVector[downOffset + k] = x;

                if (oddDelta && upK - d < k && k < upK + d)
                {
                    if (upVector[upOffset + k] <= downVector[downOffset + k])
                        return new MiddleSnake(downVector[downOffset + k], downVector[downOffset + k] - k);
                }
            }

            for (int k = upK - d; k <= upK + d; k += 2)
            {
                int x;
                if (k == upK + d)
                    x = upVector[upOffset + k - 1];
                else
                {
                    x = upVector[upOffset + k + 1] - 1;
                    if (k > upK - d && upVector[upOffset + k - 1] < x)
                        x = upVector[upOffset + k - 1];
                }

                int y = x - k;
                while (x > lowerA && y > lowerB && dataA.Data[x - 1] == dataB.Data[y - 1])
                {
                    x--;
                    y--;
                }
                upVector[upOffset + k] = x;

                if (!oddDelta && downK - d <= k && k <= downK + d)
                {
                    if (upVector[upOffset + k] <= downVector[downOffset + k])
                        return new MiddleSnake(downVector[downOffset + k], downVector[downOffset + k] - k);
                }
            }
        }

        throw new InvalidOperationException("Myers diff FindMiddleSnake did not converge.");
    }

    private static void MarkLongestCommonSubsequence(
        DiffData dataA, int lowerA, int upperA,
        DiffData dataB, int lowerB, int upperB,
        int[] downVector, int[] upVector)
    {
        while (lowerA < upperA && lowerB < upperB && dataA.Data[lowerA] == dataB.Data[lowerB])
        {
            lowerA++;
            lowerB++;
        }

        while (lowerA < upperA && lowerB < upperB && dataA.Data[upperA - 1] == dataB.Data[upperB - 1])
        {
            upperA--;
            upperB--;
        }

        if (lowerA == upperA)
        {
            while (lowerB < upperB)
                dataB.Modified[lowerB++] = true;
        }
        else if (lowerB == upperB)
        {
            while (lowerA < upperA)
                dataA.Modified[lowerA++] = true;
        }
        else
        {
            MiddleSnake middle = FindMiddleSnake(dataA, lowerA, upperA, dataB, lowerB, upperB, downVector, upVector);
            MarkLongestCommonSubsequence(dataA, lowerA, middle.X, dataB, lowerB, middle.Y, downVector, upVector);
            MarkLongestCommonSubsequence(dataA, middle.X, upperA, dataB, middle.Y, upperB, downVector, upVector);
        }
    }

    private static Item[] CreateDiffs(DiffData dataA, DiffData dataB)
    {
        var diffs = new List<Item>();
        int lineA = 0;
        int lineB = 0;

        while (lineA < dataA.Length || lineB < dataB.Length)
        {
            if (lineA < dataA.Length && !dataA.Modified[lineA] &&
                lineB < dataB.Length && !dataB.Modified[lineB])
            {
                lineA++;
                lineB++;
            }
            else
            {
                int startA = lineA;
                int startB = lineB;

                while (lineA < dataA.Length && (lineB >= dataB.Length || dataA.Modified[lineA]))
                    lineA++;

                while (lineB < dataB.Length && (lineA >= dataA.Length || dataB.Modified[lineB]))
                    lineB++;

                if (startA < lineA || startB < lineB)
                {
                    diffs.Add(new Item(startA, startB, lineA - startA, lineB - startB));
                }
            }
        }

        return diffs.ToArray();
    }
}

internal sealed class DiffData
{
    public int[] Data { get; }
    public bool[] Modified { get; }
    public int Length => Data.Length;

    public DiffData(int[] data)
    {
        ArgumentNullException.ThrowIfNull(data);

        Data = data;
        Modified = new bool[data.Length + 2];
    }
}
