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
    /// <summary>
    ///     2 つのテキストを行単位で比較し、左右に並べて表示するための行の一覧を作成します。
    ///     改行比較が OFF の場合、改行のみの行は対応付けに参加させず、内容行だけで整列した後に元の位置へ戻します。
    /// </summary>
    /// <param name="left">比較元の行。</param>
    /// <param name="right">比較先の行。</param>
    /// <param name="ignoreCase">大文字と小文字を区別しない場合は true。</param>
    /// <param name="ignoreSpaces">空白の違いを無視する場合は true。</param>
    /// <param name="leftEndings">比較元の各行の改行コード。指定した場合は改行コードの違いも差分として扱います。</param>
    /// <param name="rightEndings">比較先の各行の改行コード。</param>
    /// <param name="calculateInlineDifferences">変更行の文字単位の差分を計算する場合は true。</param>
    /// <param name="compareBlankLines">改行のみの行も比較対象にする場合は true。</param>
    /// <returns>左右に対応付けた行の一覧。</returns>
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

    /// <summary>
    ///     行頭・行末で取り除く空白文字 (半角空白、タブ、全角空白) かどうかを判定します。
    /// </summary>
    /// <param name="c">判定する文字。</param>
    /// <returns>取り除く空白文字の場合は true。</returns>
    private static bool IsEdgeSpace(char c) => c is ' ' or '\t' or '\u3000';
    /// <summary>
    ///     行中で 1 つにまとめる空白文字 (半角空白、タブ) かどうかを判定します。
    /// </summary>
    /// <param name="c">判定する文字。</param>
    /// <returns>まとめる空白文字の場合は true。</returns>
    private static bool IsBodySpace(char c) => c is ' ' or '\t';

    /// <summary>
    ///     各行の空白を正規化します。
    /// </summary>
    /// <param name="lines">正規化する行。</param>
    /// <returns>正規化した行。</returns>
    private static string[] NormalizeLines(string[] lines)
    {
        var result = new string[lines.Length];
        for (var i = 0; i < lines.Length; i++) result[i] = NormalizeSpaces(lines[i]);
        return result;
    }

    /// <summary>
    ///     各行を大文字に変換します。
    /// </summary>
    /// <param name="lines">変換する行。</param>
    /// <returns>大文字に変換した行。</returns>
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
    /// <summary>
    ///     改行のみの行を除いた、内容行のインデックスを取得します。最後の要素は EOF の疑似行として常に内容行に含めます。
    /// </summary>
    /// <param name="lines">元の行。</param>
    /// <param name="normal">空白を正規化した行。</param>
    /// <param name="ignoreSpaces">空白だけの行も改行のみの行として扱う場合は true。</param>
    /// <returns>内容行のインデックス。</returns>
    private static int[] ContentIndexes(string[] lines, string[] normal, bool ignoreSpaces)
    {
        var indexes = new List<int>(lines.Length);
        for (var i = 0; i < lines.Length; i++)
            if (i == lines.Length - 1 || !IsBlank(lines[i], normal[i], ignoreSpaces))
                indexes.Add(i);
        return indexes.ToArray();
    }

    /// <summary>
    ///     指定したインデックスの行だけを取り出します。
    /// </summary>
    /// <param name="lines">元の行。</param>
    /// <param name="indexes">取り出す行のインデックス。</param>
    /// <returns>取り出した行。</returns>
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

    /// <summary>
    ///     改行のみの行を表す、変更なしの行を作成します。
    /// </summary>
    /// <param name="left">比較元の行。片側だけの場合は null。</param>
    /// <param name="right">比較先の行。片側だけの場合は null。</param>
    /// <param name="leftNumber">比較元の行番号。片側だけの場合は 0。</param>
    /// <param name="rightNumber">比較先の行番号。片側だけの場合は 0。</param>
    /// <returns>作成した行。</returns>
    private static DiffLine BlankRow(string left, string right, int leftNumber, int rightNumber) => new()
    {
        Left = left,
        Right = right,
        LeftNumber = leftNumber,
        RightNumber = rightNumber,
        Kind = DiffLineKind.Unchanged
    };

    /// <summary>
    ///     行が空白と括弧・区切り記号だけで構成されているかどうかを判定します。
    /// </summary>
    /// <param name="text">判定する行。</param>
    /// <returns>構造上の記号だけの行の場合は true。</returns>
    private static bool IsStructuralLine(string text)
    {
        foreach (var character in text)
            if (!char.IsWhiteSpace(character) && "{}()[];,".IndexOf(character) < 0)
                return false;
        return true;
    }

    /// <summary>
    ///     両側で 1 回だけ現れる内容行を最長増加部分列でアンカーとして選び (patience diff)、アンカー間を Myers のアルゴリズムで比較します。
    ///     短い構造行だけを挟んだ差分は 1 つの差分にまとめます。
    /// </summary>
    /// <param name="left">比較元の比較キー。</param>
    /// <param name="right">比較先の比較キー。</param>
    /// <returns>差分項目の配列。</returns>
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
    /// <summary>
    ///     差分項目から左右の行を対応付け、変更の種類と文字単位の差分を設定した行の一覧を作成します。
    /// </summary>
    /// <param name="context">比較元・比較先の行と比較オプション。</param>
    /// <param name="diffs">差分項目。</param>
    /// <param name="leftIndexes">整列位置から元の比較元の行インデックスへの対応表。null の場合は同じ位置。</param>
    /// <param name="rightIndexes">整列位置から元の比較先の行インデックスへの対応表。null の場合は同じ位置。</param>
    /// <returns>左右に対応付けた行の一覧。</returns>
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

    /// <summary>
    ///     指定側の行が存在しないか、改行のみの行かどうかを判定します。
    /// </summary>
    /// <param name="context">比較元・比較先の行と比較オプション。</param>
    /// <param name="index">行のインデックス。存在しない場合は負の値。</param>
    /// <param name="source">比較元の場合は true、比較先の場合は false。</param>
    /// <returns>存在しないか改行のみの行の場合は true。</returns>
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
    // Number: the text of a number token (a run starting with a digit, see NumberEnd); null otherwise.
    private readonly record struct CharacterToken(int Code, int Start, int End, string Number = null);

    /// <summary>
    ///     指定位置が、英数字やアンダースコアに続かない数値の先頭かどうかを判定します。
    /// </summary>
    /// <param name="text">対象の文字列。</param>
    /// <param name="index">判定する位置。</param>
    /// <returns>数値の先頭の場合は true。</returns>
    private static bool IsNumberStart(string text, int index) =>
        char.IsAsciiDigit(text[index]) &&
        (index == 0 || !(char.IsAsciiLetterOrDigit(text[index - 1]) || text[index - 1] == '_'));

    // A number runs over ASCII letters/digits (hex such as 17A60) and dots followed by a
    // digit (versions such as 1.2.3).
    /// <summary>
    ///     数値トークンの終端を求めます。16 進数のような英数字の連続と、「1.2.3」のような数字が続くドットを含めます。
    /// </summary>
    /// <param name="text">対象の文字列。</param>
    /// <param name="index">数値の先頭位置。</param>
    /// <param name="end">検索範囲の終端。</param>
    /// <returns>数値トークンの終端位置 (含まない)。</returns>
    private static int NumberEnd(string text, int index, int end)
    {
        while (index < end && (char.IsAsciiLetterOrDigit(text[index]) ||
                               text[index] == '.' && index + 1 < end && char.IsAsciiDigit(text[index + 1])))
            index++;
        return index;
    }

    /// <summary>
    ///     行を文字単位の比較トークンに分割します。タブは 4 桁に展開し、数値は 1 トークンにまとめ、空白無視時は空白の連続を 1 トークンにします。
    /// </summary>
    /// <param name="text">分割する行。</param>
    /// <param name="ignoreSpaces">空白の違いを無視する場合は true。</param>
    /// <param name="displayLength">タブ展開後の表示上の長さ。</param>
    /// <param name="numberCodes">数値トークンとコードの対応表 (両側で共有)。</param>
    /// <returns>表示位置付きの比較トークンの一覧。</returns>
    private static List<CharacterToken> CharacterTokens(string text, bool ignoreSpaces, out int displayLength,
        Dictionary<string, int> numberCodes)
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
            if (i >= start && i < end && IsNumberStart(text, i))
            {
                // One token per number, so digits are never matched across different numbers.
                var numberEnd = NumberEnd(text, i, end);
                var number = text.Substring(i, numberEnd - i);
                if (!numberCodes.TryGetValue(number, out var code))
                    numberCodes.Add(number, code = -(numberCodes.Count + 1));
                tokens.Add(new CharacterToken(code, offset, offset + number.Length, number));
                offset += number.Length;
                i = numberEnd - 1;
                continue;
            }

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

    /// <summary>
    ///     トークン コードが空白 (半角空白または全角空白) かどうかを判定します。
    /// </summary>
    /// <param name="code">判定するトークン コード。</param>
    /// <returns>空白の場合は true。</returns>
    private static bool IsWhitespaceCode(int code) => code is ' ' or '\u3000';

    /// <summary>
    ///     2 つの行を文字単位で比較し、変更された表示位置をマークします。
    ///     同じ長さの数値の置き換えは桁ごとに比較し、空白は空白自体が変更された場合だけマークします。
    /// </summary>
    /// <param name="left">比較元の行。</param>
    /// <param name="right">比較先の行。</param>
    /// <param name="ignoreSpaces">空白の違いを無視する場合は true。</param>
    /// <returns>比較元と比較先の、表示位置ごとの変更フラグ。</returns>
    private static (bool[] Left, bool[] Right) CompareCharacters(string left, string right, bool ignoreSpaces)
    {
        var numberCodes = new Dictionary<string, int>(StringComparer.Ordinal);
        var leftTokens = CharacterTokens(left, ignoreSpaces, out var leftLength, numberCodes);
        var rightTokens = CharacterTokens(right, ignoreSpaces, out var rightLength, numberCodes);
        var leftCodes = new int[leftTokens.Count];
        var rightCodes = new int[rightTokens.Count];
        for (var i = 0; i < leftCodes.Length; i++) leftCodes[i] = leftTokens[i].Code;
        for (var i = 0; i < rightCodes.Length; i++) rightCodes[i] = rightTokens[i].Code;

        // Characters are their own equality codes: no per-character strings.
        var changes = Diff.DiffCodes(leftCodes, rightCodes);
        var a = new bool[leftLength];
        var b = new bool[rightLength];
        // Whitespace is marked only when it is the change itself (e.g. "( - 4)" -> "( -4)").
        // Inside a changed run that also holds other characters, only those characters are
        // marked, so rewritten text is not covered with whitespace markers.
        static void Mark(bool[] mask, List<CharacterToken> tokens, int start, int count)
        {
            var whitespaceOnly = true;
            for (var i = start; i < start + count && whitespaceOnly; i++)
                whitespaceOnly = IsWhitespaceCode(tokens[i].Code);
            for (var i = start; i < start + count; i++)
            {
                if (!whitespaceOnly && IsWhitespaceCode(tokens[i].Code)) continue;
                for (var n = tokens[i].Start; n < tokens[i].End; n++) mask[n] = true;
            }
        }

        // 数値を数値で置き換えた場合は桁ごとに比較する（桁数が違っても 1 の位から揃える。詳細は MarkNumbers）。
        // 17430 -> 17745 は "17" を残して "430"/"745" を、3739576 -> 399006 は 1 の位の 6 などを残してマークする。
        // それ以外の変更はランとしてマークする。
        bool Positional(Diff.Item change)
        {
            if (change.DeletedA != change.InsertedB || change.DeletedA == 0) return false;
            for (var k = 0; k < change.DeletedA; k++)
                if (leftTokens[change.StartA + k].Number == null || rightTokens[change.StartB + k].Number == null) return false;
            return true;
        }

        foreach (var change in changes)
        {
            if (Positional(change))
            {
                for (var k = 0; k < change.DeletedA; k++)
                {
                    var l = leftTokens[change.StartA + k];
                    var r = rightTokens[change.StartB + k];
                    MarkNumbers(l.Number, r.Number, a, l.Start, b, r.Start);
                }
                continue;
            }

            Mark(a, leftTokens, change.StartA, change.DeletedA);
            Mark(b, rightTokens, change.StartB, change.InsertedB);
        }

        return (a, b);
    }

    /// <summary>
    ///     2 つの数値を桁ごとに比較し、異なる桁をマークします。同じ長さの場合は同じ位置どうしで比較します。
    ///     長さが違う場合は、両方に 0x (0X) の接頭辞があれば接頭辞は位置で比べ、残りをドットで区切って区切りの単位を左から揃え、
    ///     各単位の中は 1 の位 (右) から揃えます。片側にしかない桁や区切りの単位 (直前のドットを含む) はマークします。
    /// </summary>
    /// <param name="left">比較元の数値。</param>
    /// <param name="right">比較先の数値。</param>
    /// <param name="leftMask">比較元の表示位置ごとの変更フラグ。</param>
    /// <param name="leftStart">比較元の数値の表示開始位置。</param>
    /// <param name="rightMask">比較先の表示位置ごとの変更フラグ。</param>
    /// <param name="rightStart">比較先の数値の表示開始位置。</param>
    private static void MarkNumbers(string left, string right, bool[] leftMask, int leftStart, bool[] rightMask, int rightStart)
    {
        // 同じ長さの数値は従来どおり同じ位置どうしで比較する (1.2.3 -> 1.3.0 は 2 桁目と 3 桁目だけ)。
        if (left.Length == right.Length)
        {
            for (var c = 0; c < left.Length; c++)
                if (left[c] != right[c]) leftMask[leftStart + c] = rightMask[rightStart + c] = true;
            return;
        }

        static bool HasHexPrefix(string value) => value.Length > 2 && value[0] == '0' && value[1] is 'x' or 'X';

        var body = 0;
        if (HasHexPrefix(left) && HasHexPrefix(right))
        {
            if (left[1] != right[1]) leftMask[leftStart + 1] = rightMask[rightStart + 1] = true;
            body = 2;
        }

        // ドット区切りの単位 (開始位置、終端位置)。先頭以外の単位の開始位置の直前はドット。
        static List<(int Start, int End)> Segments(string value, int start)
        {
            var segments = new List<(int, int)>();
            for (var i = start; i <= value.Length; i++)
            {
                if (i < value.Length && value[i] != '.') continue;
                segments.Add((start, i));
                start = i + 1;
            }
            return segments;
        }

        var leftSegments = Segments(left, body);
        var rightSegments = Segments(right, body);
        var paired = Math.Min(leftSegments.Count, rightSegments.Count);
        for (var s = 0; s < paired; s++)
        {
            var (leftFrom, leftTo) = leftSegments[s];
            var (rightFrom, rightTo) = rightSegments[s];
            for (int l = leftTo - 1, r = rightTo - 1; l >= leftFrom || r >= rightFrom; l--, r--)
            {
                if (l >= leftFrom && r >= rightFrom && left[l] == right[r]) continue;
                if (l >= leftFrom) leftMask[leftStart + l] = true;
                if (r >= rightFrom) rightMask[rightStart + r] = true;
            }
        }

        static void MarkExtra(List<(int Start, int End)> segments, int from, bool[] mask, int start)
        {
            for (var s = from; s < segments.Count; s++)
                for (var i = segments[s].Start - 1; i < segments[s].End; i++) mask[start + i] = true;
        }

        MarkExtra(leftSegments, paired, leftMask, leftStart);
        MarkExtra(rightSegments, paired, rightMask, rightStart);
    }
}
