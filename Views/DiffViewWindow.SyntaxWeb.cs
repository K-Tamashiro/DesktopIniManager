using System;
using System.Collections.Generic;
using System.Windows.Documents;

namespace DesktopIniManager.Views;

internal sealed partial class DiffViewWindow
{
    // C 系 (Java / Kotlin / Groovy / JavaScript / TypeScript / PHP) の字句状態。
    private const int CodeBlockComment = 1;
    private const int CodeTemplate = 2;
    private const int CodeTripleDouble = 3;
    private const int CodeTripleSingle = 4;
    private const int CodeDoubleQuoted = 5;
    private const int CodeSingleQuoted = 6;

    // CSS の字句状態 (ビット)。bit0：コメントの中、bit1：値の中、bit2-5：ブロックの深さ。
    private const int CssComment = 1;
    private const int CssValue = 2;

    // HTML の字句状態。下位 4 ビット：モード、bit4-5：タグの種類 (script / style)、bit8-15：埋め込み言語の状態。
    private const int HtmlComment = 1;
    private const int HtmlInTag = 2;
    private const int HtmlDoubleQuoted = 3;
    private const int HtmlSingleQuoted = 4;
    private const int HtmlScript = 5;
    private const int HtmlStyle = 6;
    private const int HtmlScriptTag = 1;
    private const int HtmlStyleTag = 2;

    // PHP の字句状態。下位 16 ビット：PHP の外側 (HTML) の状態、bit16：PHP の中、bit20-23：PHP コードの状態。
    private const int PhpInside = 1 << 16;

    private static readonly HashSet<string> JavaKeywords = new(StringComparer.Ordinal)
    {
        "abstract", "assert", "boolean", "break", "byte", "case", "catch", "char", "class", "const", "continue",
        "default", "do", "double", "else", "enum", "extends", "false", "final", "finally", "float", "for", "goto",
        "if", "implements", "import", "instanceof", "int", "interface", "long", "native", "new", "null", "package",
        "permits", "private", "protected", "public", "record", "return", "sealed", "short", "static", "strictfp",
        "super", "switch", "synchronized", "this", "throw", "throws", "transient", "true", "try", "var", "void",
        "volatile", "while", "yield"
    };

    private static readonly HashSet<string> KotlinKeywords = new(StringComparer.Ordinal)
    {
        "abstract", "actual", "annotation", "as", "break", "by", "catch", "class", "companion", "const",
        "constructor", "continue", "crossinline", "data", "do", "else", "enum", "expect", "external", "false",
        "final", "finally", "for", "fun", "get", "if", "import", "in", "infix", "init", "inline", "inner",
        "interface", "internal", "is", "lateinit", "noinline", "null", "object", "open", "operator", "out",
        "override", "package", "private", "protected", "public", "reified", "return", "sealed", "set", "super",
        "suspend", "tailrec", "this", "throw", "true", "try", "typealias", "val", "var", "vararg", "when", "where",
        "while"
    };

    private static readonly HashSet<string> GroovyKeywords = new(StringComparer.Ordinal)
    {
        "abstract", "as", "assert", "boolean", "break", "byte", "case", "catch", "char", "class", "continue",
        "def", "default", "do", "double", "else", "enum", "extends", "false", "final", "finally", "float", "for",
        "if", "implements", "import", "in", "instanceof", "int", "interface", "long", "new", "null", "package",
        "private", "protected", "public", "return", "short", "static", "super", "switch", "this", "throw",
        "throws", "trait", "true", "try", "var", "void", "while"
    };

    private static readonly HashSet<string> JavaScriptKeywords = new(StringComparer.Ordinal)
    {
        "async", "await", "break", "case", "catch", "class", "const", "continue", "debugger", "default", "delete",
        "do", "else", "export", "extends", "false", "finally", "for", "from", "function", "get", "if", "import",
        "in", "instanceof", "let", "new", "null", "of", "return", "set", "static", "super", "switch", "this",
        "throw", "true", "try", "typeof", "undefined", "var", "void", "while", "with", "yield"
    };

    private static readonly HashSet<string> TypeScriptKeywords = new(JavaScriptKeywords, StringComparer.Ordinal)
    {
        "abstract", "any", "as", "asserts", "boolean", "declare", "enum", "implements", "infer", "interface", "is",
        "keyof", "module", "namespace", "never", "number", "object", "override", "private", "protected", "public",
        "readonly", "satisfies", "string", "symbol", "type", "unique", "unknown"
    };

    private static readonly HashSet<string> PhpKeywords = new(StringComparer.OrdinalIgnoreCase)
    {
        "abstract", "and", "array", "as", "bool", "break", "callable", "case", "catch", "class", "clone", "const",
        "continue", "declare", "default", "do", "echo", "else", "elseif", "empty", "enddeclare", "endfor",
        "endforeach", "endif", "endswitch", "endwhile", "enum", "extends", "false", "final", "finally", "float",
        "fn", "for", "foreach", "function", "global", "goto", "if", "implements", "include", "include_once",
        "instanceof", "insteadof", "int", "interface", "isset", "iterable", "list", "match", "mixed", "namespace",
        "never", "new", "null", "object", "or", "parent", "print", "private", "protected", "public", "readonly",
        "require", "require_once", "return", "self", "static", "string", "switch", "throw", "trait", "true", "try",
        "unset", "use", "var", "void", "while", "xor", "yield"
    };

    private static readonly HashSet<string> CppKeywords = new(StringComparer.Ordinal)
    {
        "_Bool", "alignas", "alignof", "asm", "auto", "bool", "break", "case", "catch", "char", "char16_t",
        "char32_t", "char8_t", "class", "co_await", "co_return", "co_yield", "concept", "const", "const_cast",
        "consteval", "constexpr", "constinit", "continue", "decltype", "default", "delete", "do", "double",
        "dynamic_cast", "else", "enum", "explicit", "export", "extern", "false", "final", "float", "for", "friend",
        "goto", "if", "inline", "int", "long", "mutable", "namespace", "new", "noexcept", "NULL", "nullptr",
        "operator", "override", "private", "protected", "public", "register", "reinterpret_cast", "requires",
        "restrict", "return", "short", "signed", "sizeof", "static", "static_assert", "static_cast", "struct",
        "switch", "template", "this", "thread_local", "throw", "true", "try", "typedef", "typeid", "typename",
        "union", "unsigned", "using", "virtual", "void", "volatile", "wchar_t", "while"
    };

    private static readonly CodeStyle CppStyle = new(CppKeywords) { Preprocessor = true };
    private static readonly CodeStyle JavaStyle = new(JavaKeywords) { TripleQuotes = true, Annotations = true };
    private static readonly CodeStyle KotlinStyle = new(KotlinKeywords) { TripleQuotes = true, Annotations = true };
    private static readonly CodeStyle GroovyStyle = new(GroovyKeywords) { TripleQuotes = true, Annotations = true };
    private static readonly CodeStyle JavaScriptStyle = new(JavaScriptKeywords) { Backtick = true, Annotations = true };
    private static readonly CodeStyle TypeScriptStyle = new(TypeScriptKeywords) { Backtick = true, Annotations = true };

    private static readonly CodeStyle PhpStyle = new(PhpKeywords)
    {
        HashComments = true, Variables = true, MultilineStrings = true, PhpCloseTag = true
    };

    /// <summary>
    ///     C 系言語の字句規則。キーワードと、言語ごとに有効な構文を持ちます。
    /// </summary>
    /// <param name="keywords">キーワード。</param>
    private sealed class CodeStyle(HashSet<string> keywords)
    {
        internal HashSet<string> Keywords { get; } = keywords;

        // ` で囲むテンプレート文字列 (複数行可)。
        internal bool Backtick { get; init; }

        // """ / ''' で囲む複数行文字列。
        internal bool TripleQuotes { get; init; }

        // @Override などの注釈・デコレーター。
        internal bool Annotations { get; init; }

        // # で始まる行コメント (#[ は PHP 8 の属性なので除く)。
        internal bool HashComments { get; init; }

        // $name の変数。
        internal bool Variables { get; init; }

        // "..." / '...' が行をまたぐ。
        internal bool MultilineStrings { get; init; }

        // ?> で PHP コードを抜ける。
        internal bool PhpCloseTag { get; init; }

        // 行頭の #include / #define などのプリプロセッサー命令 (C / C++)。
        internal bool Preprocessor { get; init; }
    }

    /// <summary>
    ///     行の範囲ごとに色付きのトークンを段落へ追加します。トークンは位置の順に渡します。
    ///     段落が null の場合は何も追加しません (字句状態の更新だけを行う場合)。
    /// </summary>
    /// <param name="owner">色を決めるウィンドウ。</param>
    /// <param name="paragraph">追加先の段落。</param>
    /// <param name="text">行のテキスト。</param>
    private sealed class SyntaxWriter(DiffViewWindow owner, Paragraph paragraph, string text)
    {
        private int _plain;
        internal string Text { get; } = text;

        /// <summary>
        ///     直前のトークンから開始位置までを色なしで、開始位置から終了位置までを指定の種類の色で追加します。
        /// </summary>
        /// <param name="start">開始位置。</param>
        /// <param name="end">終了位置 (含まない)。</param>
        /// <param name="kind">トークンの種類。</param>
        internal void Token(int start, int end, string kind)
        {
            if (end <= start) return;
            if (paragraph != null)
            {
                AddPlainRun(paragraph, Text, _plain, start - _plain);
                owner.AddColoredRun(paragraph, Text.Substring(start, end - start), kind);
            }

            _plain = end;
        }

        /// <summary>
        ///     最後のトークンから行末までを色なしで追加します。
        /// </summary>
        internal void Finish()
        {
            AddPlainRun(paragraph, Text, _plain, Text.Length - _plain);
        }
    }

    /// <summary>
    ///     範囲内で文字列を探します。
    /// </summary>
    /// <param name="text">行のテキスト。</param>
    /// <param name="value">探す文字列。</param>
    /// <param name="from">開始位置。</param>
    /// <param name="to">終了位置 (含まない)。</param>
    /// <param name="comparison">比較方法。</param>
    /// <returns>見つかった位置。見つからない場合は -1。</returns>
    private static int IndexIn(string text, string value, int from, int to,
        StringComparison comparison = StringComparison.Ordinal)
    {
        return from >= to ? -1 : text.IndexOf(value, from, to - from, comparison);
    }

    /// <summary>
    ///     C 系言語 (Java / Kotlin / Groovy / JavaScript / TypeScript / PHP) の範囲を色分けします。
    /// </summary>
    /// <param name="w">出力先。</param>
    /// <param name="from">開始位置。</param>
    /// <param name="to">終了位置 (含まない)。</param>
    /// <param name="style">言語の字句規則。</param>
    /// <param name="state">行をまたぐ字句状態。次の範囲に引き継ぎます。</param>
    /// <returns>処理を終えた位置。PHP で ?> に達した場合はその位置。</returns>
    private static int LexCode(SyntaxWriter w, int from, int to, CodeStyle style, ref int state)
    {
        var text = w.Text;
        var i = from;
        var lineEnd = to == text.Length;

        // 引用符で囲んだ文字列の終端 (閉じた引用符の次) を返す。範囲内で閉じない場合は -1。
        int QuotedEnd(int start, char quote)
        {
            for (var j = start; j < to; j++)
            {
                if (text[j] == '\\') j++;
                else if (text[j] == quote) return j + 1;
            }

            return -1;
        }

        while (i < to)
        {
            switch (state)
            {
                case CodeBlockComment:
                {
                    var end = IndexIn(text, "*/", i, to);
                    var stop = end < 0 ? to : end + 2;
                    w.Token(i, stop, "comment");
                    if (end >= 0) state = 0;
                    i = stop;
                    continue;
                }
                case CodeTemplate:
                case CodeDoubleQuoted:
                case CodeSingleQuoted:
                {
                    var end = QuotedEnd(i, state == CodeTemplate ? '`' : state == CodeDoubleQuoted ? '"' : '\'');
                    w.Token(i, end < 0 ? to : end, "string");
                    if (end < 0)
                    {
                        // 1 行で閉じる文字列は、範囲が行の途中で切れた (PHP の埋め込み) 場合だけ続きを引き継ぐ。
                        if (lineEnd && state != CodeTemplate && !style.MultilineStrings) state = 0;
                        return to;
                    }

                    state = 0;
                    i = end;
                    continue;
                }
                case CodeTripleDouble:
                case CodeTripleSingle:
                {
                    var end = IndexIn(text, state == CodeTripleDouble ? "\"\"\"" : "'''", i, to);
                    var stop = end < 0 ? to : end + 3;
                    w.Token(i, stop, "string");
                    if (end >= 0) state = 0;
                    i = stop;
                    continue;
                }
            }

            var c = text[i];
            var next = i + 1 < to ? text[i + 1] : '\0';
            if (style.PhpCloseTag && c == '?' && next == '>') return i;

            if (style.Preprocessor && c == '#' && text.AsSpan(from, i - from).IsWhiteSpace())
            {
                // 「#  include」のような空白も含めて命令名までをキーワードにし、#include の <...> は文字列にする。
                var start = i++;
                while (i < to && text[i] is ' ' or '\t') i++;
                var nameStart = i;
                while (i < to && char.IsLetter(text[i])) i++;
                w.Token(start, i, "keyword");
                var directive = text.Substring(nameStart, i - nameStart);
                if (directive is "include" or "include_next" or "import")
                {
                    while (i < to && text[i] is ' ' or '\t') i++;
                    var close = i < to && text[i] == '<' ? IndexIn(text, ">", i, to) : -1;
                    if (close >= 0)
                    {
                        w.Token(i, close + 1, "string");
                        i = close + 1;
                    }
                }

                continue;
            }

            if (c == '/' && next == '/' || style.HashComments && c == '#' && next != '[')
            {
                // PHP の行コメントは ?> で終わる。
                var close = style.PhpCloseTag ? IndexIn(text, "?>", i, to) : -1;
                var stop = close < 0 ? to : close;
                w.Token(i, stop, "comment");
                if (close >= 0) return close;
                i = stop;
                continue;
            }

            if (c == '/' && next == '*')
            {
                state = CodeBlockComment;
                w.Token(i, i + 2, "comment");
                i += 2;
                continue;
            }

            if (style.TripleQuotes && c is '"' or '\'' && i + 2 < to && next == c && text[i + 2] == c)
            {
                state = c == '"' ? CodeTripleDouble : CodeTripleSingle;
                w.Token(i, i + 3, "string");
                i += 3;
                continue;
            }

            if (c is '"' or '\'' || style.Backtick && c == '`')
            {
                state = c == '`' ? CodeTemplate : c == '"' ? CodeDoubleQuoted : CodeSingleQuoted;
                w.Token(i, i + 1, "string");
                i++;
                continue;
            }

            if (style.Variables && c == '$' && (char.IsLetter(next) || next == '_'))
            {
                var start = i++;
                while (i < to && IsIdentifierChar(text[i])) i++;
                w.Token(start, i, "attribute");
                continue;
            }

            if (style.Annotations && c == '@' && char.IsLetter(next))
            {
                var start = i++;
                while (i < to && (IsIdentifierChar(text[i]) || text[i] == '.')) i++;
                w.Token(start, i, "attribute");
                continue;
            }

            if (char.IsLetter(c) || c is '_' or '$')
            {
                var start = i++;
                while (i < to && (IsIdentifierChar(text[i]) || text[i] == '$')) i++;
                if (!IsMemberName(text, start) && style.Keywords.Contains(text.Substring(start, i - start)))
                    w.Token(start, i, "keyword");
                continue;
            }

            if (char.IsDigit(c))
            {
                var start = i++;
                while (i < to && (char.IsLetterOrDigit(text[i]) || text[i] is '.' or '_')) i++;
                w.Token(start, i, "number");
                continue;
            }

            i++;
        }

        return to;
    }

    /// <summary>
    ///     CSS / SCSS / Less の範囲を色分けします。セレクター・@ルールはキーワード、プロパティ名は属性、値の数値・色・文字列はそれぞれの色にします。
    /// </summary>
    /// <param name="w">出力先。</param>
    /// <param name="from">開始位置。</param>
    /// <param name="to">終了位置 (含まない)。</param>
    /// <param name="scss">// コメントと $変数 / @変数 を使う SCSS / Less の場合は true。</param>
    /// <param name="state">行をまたぐ字句状態。次の範囲に引き継ぎます。</param>
    private static void LexCss(SyntaxWriter w, int from, int to, bool scss, ref int state)
    {
        var text = w.Text;
        var i = from;
        var depth = (state >> 2) & 15;
        var value = (state & CssValue) != 0;
        var comment = (state & CssComment) != 0;

        bool IsNameChar(char c) => char.IsLetterOrDigit(c) || c is '-' or '_';

        // ":" が宣言 (color: red) の区切りかどうか。変数 ($x: / @x:) と括弧内 ((max-width: 600px)) は宣言。
        // それ以外は、後ろに ";" より先に "{" があればセレクター (a:hover {)。
        bool IsDeclaration(int colon)
        {
            var k = colon - 1;
            while (k >= from && text[k] is ' ' or '\t') k--;
            while (k >= from && IsNameChar(text[k])) k--;
            if (k >= from && text[k] is '$' or '@') return true;
            while (k >= from && text[k] is ' ' or '\t') k--;
            if (k >= from && text[k] == '(') return true;
            if (depth == 0) return false;
            var brace = IndexIn(text, "{", colon, to);
            var semicolon = IndexIn(text, ";", colon, to);
            return brace < 0 || semicolon >= 0 && semicolon < brace;
        }

        while (i < to)
        {
            if (comment)
            {
                var end = IndexIn(text, "*/", i, to);
                var stop = end < 0 ? to : end + 2;
                w.Token(i, stop, "comment");
                if (end >= 0) comment = false;
                i = stop;
                continue;
            }

            var c = text[i];
            var next = i + 1 < to ? text[i + 1] : '\0';
            if (c == '/' && next == '*')
            {
                comment = true;
                w.Token(i, i + 2, "comment");
                i += 2;
                continue;
            }

            // SCSS の // コメント。url(http://...) を避けるため、直前が ":" の場合は除く。
            if (scss && c == '/' && next == '/' && (i == 0 || text[i - 1] != ':'))
            {
                w.Token(i, to, "comment");
                break;
            }

            if (c is '"' or '\'')
            {
                var start = i++;
                while (i < to && text[i] != c)
                    i += text[i] == '\\' ? 2 : 1;
                i = Math.Min(to, i + 1);
                w.Token(start, i, "string");
                continue;
            }

            if (c == '{')
            {
                depth = Math.Min(15, depth + 1);
                value = false;
                i++;
                continue;
            }

            if (c == '}')
            {
                depth = Math.Max(0, depth - 1);
                value = false;
                i++;
                continue;
            }

            if (c == ';')
            {
                value = false;
                i++;
                continue;
            }

            if (scss && c is '$' or '@' && (char.IsLetter(next) || next is '-' or '_'))
            {
                // SCSS の $変数、Less の @変数 (@name: の形。@media などの @ルールは除く)。
                var end = i + 1;
                while (end < to && IsNameChar(text[end])) end++;
                var after = end;
                while (after < to && text[after] is ' ' or '\t') after++;
                if (c == '$' || after < to && text[after] == ':')
                {
                    w.Token(i, end, "attribute");
                    i = end;
                    continue;
                }
            }

            if (value)
            {
                if (c == '#' && Uri.IsHexDigit(next))
                {
                    var start = i++;
                    while (i < to && char.IsLetterOrDigit(text[i])) i++;
                    w.Token(start, i, "number");
                    continue;
                }

                if (char.IsDigit(c) || c == '.' && char.IsDigit(next) ||
                    c == '-' && (char.IsDigit(next) || next == '.') && (i == 0 || !IsNameChar(text[i - 1])))
                {
                    var start = i++;
                    while (i < to && (char.IsLetterOrDigit(text[i]) || text[i] is '.' or '%')) i++;
                    w.Token(start, i, "number");
                    continue;
                }

                if (c == '!' && char.IsLetter(next))
                {
                    var start = i++;
                    while (i < to && char.IsLetter(text[i])) i++;
                    w.Token(start, i, "keyword");
                    continue;
                }

                if (IsNameChar(c))
                {
                    while (i < to && IsNameChar(text[i])) i++;
                    continue;
                }

                i++;
                continue;
            }

            if (c == '@' && char.IsLetter(next))
            {
                var start = i++;
                while (i < to && IsNameChar(text[i])) i++;
                w.Token(start, i, "keyword");
                continue;
            }

            if (c == ':')
            {
                if (IsDeclaration(i))
                {
                    value = true;
                    i++;
                    continue;
                }

                // :hover / ::before などの擬似クラス・擬似要素。
                var start = i++;
                if (i < to && text[i] == ':') i++;
                while (i < to && IsNameChar(text[i])) i++;
                w.Token(start, i, "keyword");
                continue;
            }

            if (c is '.' or '#' && (char.IsLetter(next) || next is '-' or '_'))
            {
                var start = i++;
                while (i < to && IsNameChar(text[i])) i++;
                w.Token(start, i, "keyword");
                continue;
            }

            if (char.IsLetter(c) || c is '-' or '_' or '*')
            {
                var start = i++;
                while (i < to && IsNameChar(text[i])) i++;
                var after = i;
                while (after < to && text[after] is ' ' or '\t') after++;
                w.Token(start, i, after < to && text[after] == ':' && IsDeclaration(after) ? "attribute" : "keyword");
                continue;
            }

            i++;
        }

        state = (comment ? CssComment : 0) | (value ? CssValue : 0) | depth << 2;
    }

    /// <summary>
    ///     HTML の範囲を色分けします。&lt;script&gt; の中は JavaScript、&lt;style&gt; の中は CSS として色分けします。
    /// </summary>
    /// <param name="w">出力先。</param>
    /// <param name="from">開始位置。</param>
    /// <param name="to">終了位置 (含まない)。</param>
    /// <param name="state">行をまたぐ字句状態。</param>
    /// <returns>次の範囲に引き継ぐ字句状態。</returns>
    private static int LexHtml(SyntaxWriter w, int from, int to, int state)
    {
        var text = w.Text;
        var i = from;
        var mode = state & 15;
        var tag = (state >> 4) & 3;
        var inner = (state >> 8) & 255;

        while (i < to)
        {
            switch (mode)
            {
                case HtmlComment:
                {
                    var end = IndexIn(text, "-->", i, to);
                    var stop = end < 0 ? to : end + 3;
                    w.Token(i, stop, "comment");
                    if (end >= 0) mode = 0;
                    i = stop;
                    continue;
                }
                case HtmlDoubleQuoted:
                case HtmlSingleQuoted:
                {
                    var end = IndexIn(text, mode == HtmlDoubleQuoted ? "\"" : "'", i, to);
                    var stop = end < 0 ? to : end + 1;
                    w.Token(i, stop, "string");
                    if (end >= 0) mode = HtmlInTag;
                    i = stop;
                    continue;
                }
                case HtmlScript:
                case HtmlStyle:
                {
                    // 閉じタグは文字列やコメントの中でも要素を終える (HTML の仕様)。
                    var close = IndexIn(text, mode == HtmlScript ? "</script" : "</style", i, to,
                        StringComparison.OrdinalIgnoreCase);
                    var end = close < 0 ? to : close;
                    if (mode == HtmlScript) LexCode(w, i, end, JavaScriptStyle, ref inner);
                    else LexCss(w, i, end, false, ref inner);
                    i = end;
                    if (close >= 0)
                    {
                        mode = 0;
                        inner = 0;
                    }

                    continue;
                }
                case HtmlInTag:
                {
                    var c = text[i];
                    if (char.IsWhiteSpace(c))
                    {
                        i++;
                        continue;
                    }

                    if (c is '>' or '=')
                    {
                        w.Token(i, i + 1, "keyword");
                        i++;
                        if (c == '>')
                        {
                            mode = tag == HtmlScriptTag ? HtmlScript : tag == HtmlStyleTag ? HtmlStyle : 0;
                            tag = 0;
                            inner = 0;
                        }

                        continue;
                    }

                    if (c is '/' or '?' && i + 1 < to && text[i + 1] == '>')
                    {
                        w.Token(i, i + 2, "keyword");
                        mode = 0;
                        tag = 0;
                        i += 2;
                        continue;
                    }

                    if (c is '"' or '\'')
                    {
                        mode = c == '"' ? HtmlDoubleQuoted : HtmlSingleQuoted;
                        w.Token(i, i + 1, "string");
                        i++;
                        continue;
                    }

                    var start = i;
                    while (i < to && !char.IsWhiteSpace(text[i]) && text[i] is not ('=' or '>' or '"' or '\'') &&
                           !(text[i] == '/' && i + 1 < to && text[i + 1] == '>')) i++;
                    if (i == start) i++;
                    w.Token(start, i, "attribute");
                    continue;
                }
                default:
                {
                    if (string.CompareOrdinal(text, i, "<!--", 0, 4) == 0 && i + 4 <= to)
                    {
                        mode = HtmlComment;
                        w.Token(i, i + 4, "comment");
                        i += 4;
                        continue;
                    }

                    if (text[i] == '<' && i + 1 < to && (char.IsLetter(text[i + 1]) || text[i + 1] is '/' or '!'))
                    {
                        var start = i++;
                        var closing = text[i] == '/';
                        if (text[i] is '/' or '!') i++;
                        var nameStart = i;
                        while (i < to && !char.IsWhiteSpace(text[i]) && text[i] is not ('>' or '/')) i++;
                        w.Token(start, i, "keyword");
                        var name = text.Substring(nameStart, i - nameStart);
                        tag = closing ? 0
                            : name.Equals("script", StringComparison.OrdinalIgnoreCase) ? HtmlScriptTag
                            : name.Equals("style", StringComparison.OrdinalIgnoreCase) ? HtmlStyleTag : 0;
                        mode = HtmlInTag;
                        continue;
                    }

                    if (text[i] == '&')
                    {
                        var end = IndexIn(text, ";", i + 1, to);
                        if (end > i + 1 && end - i <= 12)
                        {
                            w.Token(i, end + 1, "keyword");
                            i = end + 1;
                            continue;
                        }
                    }

                    i++;
                    continue;
                }
            }
        }

        return mode | tag << 4 | inner << 8;
    }

    /// <summary>
    ///     PHP ファイルの行を色分けします。&lt;?php ～ ?&gt; の中は PHP、外側は HTML (script / style を含む) として色分けします。
    /// </summary>
    /// <param name="w">出力先。</param>
    /// <param name="state">行をまたぐ字句状態。次の行に引き継ぎます。</param>
    private static void LexPhp(SyntaxWriter w, ref int state)
    {
        var text = w.Text;
        var html = state & 0xFFFF;
        var inside = (state & PhpInside) != 0;
        var code = (state >> 20) & 15;
        var i = 0;
        while (i < text.Length)
        {
            if (inside)
            {
                var stop = LexCode(w, i, text.Length, PhpStyle, ref code);
                if (stop >= text.Length) break;
                w.Token(stop, stop + 2, "keyword");
                i = stop + 2;
                inside = false;
                code = 0;
                continue;
            }

            var open = IndexIn(text, "<?", i, text.Length);
            html = LexHtml(w, i, open < 0 ? text.Length : open, html);
            if (open < 0) break;
            var length = string.Compare(text, open, "<?php", 0, 5, StringComparison.OrdinalIgnoreCase) == 0 &&
                         open + 5 <= text.Length ? 5
                : open + 2 < text.Length && text[open + 2] == '=' ? 3 : 2;
            w.Token(open, open + length, "keyword");
            i = open + length;
            inside = true;
            code = 0;
        }

        state = html | (inside ? PhpInside : 0) | code << 20;
    }
}
