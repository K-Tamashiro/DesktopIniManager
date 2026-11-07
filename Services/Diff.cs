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
///     An O(ND) Difference Algorithm (Eugene Myers, 1986).
///     Modernized and optimized for side-by-side text diffing.
/// </summary>
internal static class Diff
{
    /// <summary>
    ///     行配列を直接受け取り、無駄な結合・再分割なしで差分を計算します。
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

        return DiffCodes(EncodeLines(linesA, codeMap, trimSpace, ignoreSpace, ignoreCase),
            EncodeLines(linesB, codeMap, trimSpace, ignoreSpace, ignoreCase));
    }

    /// <summary>
    ///     Computes differences between two sequences of equality codes (equal values mean equal items).
    ///     Callers that already have comparable integers, such as characters, avoid string keys.
    /// </summary>
    public static Item[] DiffCodes(int[] codesA, int[] codesB)
    {
        ArgumentNullException.ThrowIfNull(codesA);
        ArgumentNullException.ThrowIfNull(codesB);
        var dataA = new DiffData(codesA);
        var dataB = new DiffData(codesB);

        var max = dataA.Length + dataB.Length + 1;
        var downVector = new int[2 * max + 2];
        var upVector = new int[2 * max + 2];

        MarkLongestCommonSubsequence(dataA, 0, dataA.Length, dataB, 0, dataB.Length, downVector, upVector);

        Optimize(dataA);
        Optimize(dataB);

        return CreateDiffs(dataA, dataB);
    }

    /// <summary>
    ///     比較オプションを適用した各行を整数コードに変換します。同じ内容の行は同じコードになります。
    /// </summary>
    /// <param name="lines">変換する行。</param>
    /// <param name="codeMap">行内容とコードの対応表 (両側で共有)。</param>
    /// <param name="trimSpace">前後の空白を無視する場合は true。</param>
    /// <param name="ignoreSpace">連続する空白を 1 つにまとめる場合は true。</param>
    /// <param name="ignoreCase">大文字と小文字を区別しない場合は true。</param>
    /// <returns>行ごとのコード。</returns>
    private static int[] EncodeLines(
        string[] lines,
        Dictionary<string, int> codeMap,
        bool trimSpace,
        bool ignoreSpace,
        bool ignoreCase)
    {
        var codes = new int[lines.Length];

        for (var i = 0; i < lines.Length; i++)
        {
            var s = lines[i];

            if (trimSpace)
                s = s.Trim();

            if (ignoreSpace)
                s = Regex.Replace(s, @"\s+", " ");

            if (ignoreCase)
                s = s.ToLowerInvariant();

            if (!codeMap.TryGetValue(s, out var code))
            {
                code = codeMap.Count + 1;
                codeMap[s] = code;
            }

            codes[i] = code;
        }

        return codes;
    }

    /// <summary>
    ///     変更範囲の直後が先頭と同じ値の場合に範囲を後ろへずらし、差分をより自然な位置にそろえます。
    /// </summary>
    /// <param name="data">最適化する差分データ。</param>
    private static void Optimize(DiffData data)
    {
        var startPos = 0;
        while (startPos < data.Length)
        {
            while (startPos < data.Length && !data.Modified[startPos])
                startPos++;

            var endPos = startPos;
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

    /// <summary>
    ///     Myers のアルゴリズムで、指定範囲の最短編集経路の中間スネークを前方・後方の両方向から探索します。
    /// </summary>
    /// <param name="dataA">比較元のデータ。</param>
    /// <param name="lowerA">比較元の範囲の開始位置。</param>
    /// <param name="upperA">比較元の範囲の終了位置。</param>
    /// <param name="dataB">比較先のデータ。</param>
    /// <param name="lowerB">比較先の範囲の開始位置。</param>
    /// <param name="upperB">比較先の範囲の終了位置。</param>
    /// <param name="downVector">前方探索用の作業配列。</param>
    /// <param name="upVector">後方探索用の作業配列。</param>
    /// <returns>中間スネークの位置。</returns>
    private static MiddleSnake FindMiddleSnake(
        DiffData dataA, int lowerA, int upperA,
        DiffData dataB, int lowerB, int upperB,
        int[] downVector, int[] upVector)
    {
        var max = dataA.Length + dataB.Length + 1;
        var downK = lowerA - lowerB;
        var upK = upperA - upperB;
        var delta = upperA - lowerA - (upperB - lowerB);
        var oddDelta = (delta & 1) != 0;

        var downOffset = max - downK;
        var upOffset = max - upK;
        var maxD = (upperA - lowerA + upperB - lowerB) / 2 + 1;

        downVector[downOffset + downK + 1] = lowerA;
        upVector[upOffset + upK - 1] = upperA;

        for (var d = 0; d <= maxD; d++)
        {
            for (var k = downK - d; k <= downK + d; k += 2)
            {
                int x;
                if (k == downK - d)
                {
                    x = downVector[downOffset + k + 1];
                }
                else
                {
                    x = downVector[downOffset + k - 1] + 1;
                    if (k < downK + d && downVector[downOffset + k + 1] >= x)
                        x = downVector[downOffset + k + 1];
                }

                var y = x - k;
                while (x < upperA && y < upperB && dataA.Data[x] == dataB.Data[y])
                {
                    x++;
                    y++;
                }

                downVector[downOffset + k] = x;

                if (oddDelta && upK - d < k && k < upK + d)
                    if (upVector[upOffset + k] <= downVector[downOffset + k])
                        return new MiddleSnake(downVector[downOffset + k], downVector[downOffset + k] - k);
            }

            for (var k = upK - d; k <= upK + d; k += 2)
            {
                int x;
                if (k == upK + d)
                {
                    x = upVector[upOffset + k - 1];
                }
                else
                {
                    x = upVector[upOffset + k + 1] - 1;
                    if (k > upK - d && upVector[upOffset + k - 1] < x)
                        x = upVector[upOffset + k - 1];
                }

                var y = x - k;
                while (x > lowerA && y > lowerB && dataA.Data[x - 1] == dataB.Data[y - 1])
                {
                    x--;
                    y--;
                }

                upVector[upOffset + k] = x;

                if (!oddDelta && downK - d <= k && k <= downK + d)
                    if (upVector[upOffset + k] <= downVector[downOffset + k])
                        return new MiddleSnake(downVector[downOffset + k], downVector[downOffset + k] - k);
            }
        }

        throw new InvalidOperationException("Myers diff FindMiddleSnake did not converge.");
    }

    /// <summary>
    ///     共通の先頭と末尾を除いた範囲を中間スネークで再帰的に分割し、最長共通部分列に含まれない要素を変更済みとしてマークします。
    /// </summary>
    /// <param name="dataA">比較元のデータ。</param>
    /// <param name="lowerA">比較元の範囲の開始位置。</param>
    /// <param name="upperA">比較元の範囲の終了位置。</param>
    /// <param name="dataB">比較先のデータ。</param>
    /// <param name="lowerB">比較先の範囲の開始位置。</param>
    /// <param name="upperB">比較先の範囲の終了位置。</param>
    /// <param name="downVector">前方探索用の作業配列。</param>
    /// <param name="upVector">後方探索用の作業配列。</param>
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
            var middle = FindMiddleSnake(dataA, lowerA, upperA, dataB, lowerB, upperB, downVector, upVector);
            MarkLongestCommonSubsequence(dataA, lowerA, middle.X, dataB, lowerB, middle.Y, downVector, upVector);
            MarkLongestCommonSubsequence(dataA, middle.X, upperA, dataB, middle.Y, upperB, downVector, upVector);
        }
    }

    /// <summary>
    ///     両側の変更マークを走査し、連続する変更をまとめた差分項目を作成します。
    /// </summary>
    /// <param name="dataA">比較元のデータ。</param>
    /// <param name="dataB">比較先のデータ。</param>
    /// <returns>差分項目の配列。</returns>
    private static Item[] CreateDiffs(DiffData dataA, DiffData dataB)
    {
        var diffs = new List<Item>();
        var lineA = 0;
        var lineB = 0;

        while (lineA < dataA.Length || lineB < dataB.Length)
            if (lineA < dataA.Length && !dataA.Modified[lineA] &&
                lineB < dataB.Length && !dataB.Modified[lineB])
            {
                lineA++;
                lineB++;
            }
            else
            {
                var startA = lineA;
                var startB = lineB;

                while (lineA < dataA.Length && (lineB >= dataB.Length || dataA.Modified[lineA]))
                    lineA++;

                while (lineB < dataB.Length && (lineA >= dataA.Length || dataB.Modified[lineB]))
                    lineB++;

                if (startA < lineA || startB < lineB)
                    diffs.Add(new Item(startA, startB, lineA - startA, lineB - startB));
            }

        return diffs.ToArray();
    }

    public readonly record struct Item(int StartA, int StartB, int DeletedA, int InsertedB);

    private readonly record struct MiddleSnake(int X, int Y);
}

internal sealed class DiffData
{
    /// <summary>
    ///     比較対象のコード列と変更マーク用の配列を作成します。
    /// </summary>
    /// <param name="data">比較対象のコード列。</param>
    public DiffData(int[] data)
    {
        ArgumentNullException.ThrowIfNull(data);

        Data = data;
        Modified = new bool[data.Length + 2];
    }

    public int[] Data { get; }
    public bool[] Modified { get; }
    public int Length => Data.Length;
}