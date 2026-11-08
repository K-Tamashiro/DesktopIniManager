using System;
using System.Collections.Generic;

namespace DesktopIniManager.Views;

internal sealed partial class DiffViewWindow
{
    // PowerShell の字句状態。
    private const int PowerShellBlockComment = 1;
    private const int PowerShellHereDouble = 2;
    private const int PowerShellHereSingle = 3;

    private static readonly HashSet<string> BatchKeywords = new(StringComparer.OrdinalIgnoreCase)
    {
        "call", "cd", "chdir", "cls", "copy", "defined", "del", "disabledelayedexpansion", "do", "echo", "else",
        "enabledelayedexpansion", "enableextensions", "endlocal", "equ", "erase", "errorlevel", "exist", "exit",
        "for", "geq", "goto", "gtr", "if", "in", "leq", "lss", "md", "mkdir", "move", "neq", "not", "nul", "off",
        "pause", "popd", "pushd", "rd", "ren", "rmdir", "set", "setlocal", "shift", "start", "title", "type"
    };

    private static readonly HashSet<string> PowerShellKeywords = new(StringComparer.OrdinalIgnoreCase)
    {
        "begin", "break", "catch", "class", "continue", "data", "do", "dynamicparam", "else", "elseif", "end",
        "enum", "exit", "filter", "finally", "for", "foreach", "function", "hidden", "if", "in", "param",
        "process", "return", "static", "switch", "throw", "trap", "try", "until", "using", "while"
    };

    private static readonly HashSet<string> PowerShellOperators = new(StringComparer.OrdinalIgnoreCase)
    {
        "and", "as", "band", "bnot", "bor", "bxor", "ccontains", "ceq", "cge", "cgt", "cin", "cle", "clike", "clt",
        "cmatch", "cne", "cnotlike", "cnotmatch", "contains", "creplace", "csplit", "eq", "f", "ge", "gt", "icontains",
        "ieq", "ilike", "imatch", "in", "ine", "ireplace", "is", "isnot", "join", "le", "like", "lt", "match", "ne",
        "not", "notcontains", "notin", "notlike", "notmatch", "or", "replace", "shl", "shr", "split", "xor"
    };

    private static readonly HashSet<string> ShellKeywords = new(StringComparer.Ordinal)
    {
        "break", "case", "continue", "declare", "do", "done", "echo", "elif", "else", "esac", "eval", "exec",
        "exit", "export", "false", "fi", "for", "function", "if", "in", "local", "printf", "read", "readonly",
        "return", "select", "set", "shift", "source", "test", "then", "trap", "true", "typeset", "unset", "until",
        "while"
    };

    private static readonly HashSet<string> MakeDirectives = new(StringComparer.Ordinal)
    {
        "-include", "define", "else", "endef", "endif", "export", "ifdef", "ifeq", "ifndef", "ifneq", "include",
        "override", "sinclude", "undefine", "unexport", "vpath"
    };

    private static readonly HashSet<string> CMakeKeywords = new(StringComparer.Ordinal)
    {
        "ALIAS", "AND", "APPEND", "BOOL", "CACHE", "COMPONENTS", "DEFINED", "DESTINATION", "DIRECTORY", "EQUAL",
        "EXISTS", "FALSE", "FATAL_ERROR", "FILEPATH", "FILES", "FORCE", "GLOB", "GLOB_RECURSE", "GREATER",
        "HINTS", "IMPORTED", "INTERFACE", "INTERNAL", "LESS", "MATCHES", "MODULE", "NAMES", "NOT", "OBJECT", "OFF",
        "ON", "OR", "PATH", "PATHS", "PRIVATE", "PROPERTIES", "PUBLIC", "REMOVE_ITEM", "REQUIRED", "SEND_ERROR",
        "SHARED", "STATIC", "STATUS", "STREQUAL", "STRING", "TARGET", "TARGETS", "TRUE", "VERSION",
        "VERSION_GREATER", "VERSION_LESS", "WARNING"
    };

    private static readonly HashSet<string> ProGuardKeywords = new(StringComparer.Ordinal)
    {
        "abstract", "class", "enum", "extends", "final", "implements", "interface", "native", "private",
        "protected", "public", "static", "synchronized", "transient", "void", "volatile"
    };

    /// <summary>
    ///     $(...) / ${...} の終端 (閉じ括弧の次) を、入れ子を考慮して返します。閉じない場合は行末を返します。
    /// </summary>
    /// <param name="text">行のテキスト。</param>
    /// <param name="open">開き括弧の位置。</param>
    /// <returns>終端の位置。</returns>
    private static int ReferenceEnd(string text, int open)
    {
        var close = text[open] == '(' ? ')' : '}';
        var depth = 0;
        for (var i = open; i < text.Length; i++)
        {
            if (text[i] == text[open]) depth++;
            else if (text[i] == close && --depth == 0) return i + 1;
        }

        return text.Length;
    }

    /// <summary>
    ///     Makefile / Android.mk / Application.mk の行を色分けします。
    ///     # 以降はコメント、$(VAR) / $(call ...) と変数の代入先は属性、include / ifeq などの命令とターゲットはキーワードにします。
    /// </summary>
    /// <param name="w">出力先。</param>
    private static void LexMake(SyntaxWriter w)
    {
        var text = w.Text;
        var i = 0;
        var recipe = text.StartsWith('\t');
        while (i < text.Length && text[i] is ' ' or '\t') i++;
        if (!recipe && i < text.Length && (char.IsLetter(text[i]) || text[i] is '_' or '-' or '.' or '$'))
        {
            // 行頭の命令、代入 (NAME := / = / += / ?=)、ターゲット (name:)。
            var start = i;
            var end = i;
            while (end < text.Length && text[end] is not (' ' or '\t' or ':' or '=' or '?' or '+' or '#' or '!'))
                end = text[end] == '$' && end + 1 < text.Length && text[end + 1] is '(' or '{'
                    ? ReferenceEnd(text, end + 1) : end + 1;
            var word = text.Substring(start, end - start);
            var after = end;
            while (after < text.Length && text[after] is ' ' or '\t') after++;
            if (MakeDirectives.Contains(word))
            {
                w.Token(start, end, "keyword");
                i = end;
            }
            else if (after < text.Length && (text[after] == '=' ||
                                             after + 1 < text.Length && text[after + 1] == '=' &&
                                             text[after] is ':' or '+' or '?' or '!'))
            {
                if (!word.Contains('$')) w.Token(start, end, "attribute");
                i = end;
            }
            else if (after < text.Length && text[after] == ':' && !word.Contains('$'))
            {
                w.Token(start, end, "keyword");
                i = end;
            }
        }

        while (i < text.Length)
        {
            var c = text[i];
            var next = i + 1 < text.Length ? text[i + 1] : '\0';
            if (c == '#' && (i == 0 || text[i - 1] != '\\'))
            {
                w.Token(i, text.Length, "comment");
                return;
            }

            if (c == '$' && next is '(' or '{')
            {
                var end = ReferenceEnd(text, i + 1);
                w.Token(i, end, "attribute");
                i = end;
                continue;
            }

            if (c == '$' && next is '@' or '<' or '^' or '?' or '*' or '+' or '%' or '$')
            {
                w.Token(i, i + 2, "attribute");
                i += 2;
                continue;
            }

            i++;
        }
    }

    /// <summary>
    ///     CMakeLists.txt / .cmake の行を色分けします。
    ///     コマンド名と PUBLIC / SHARED などの引数キーワードはキーワード、${VAR} は属性、"..." は文字列、# 以降はコメントにします。
    /// </summary>
    /// <param name="w">出力先。</param>
    private static void LexCMake(SyntaxWriter w)
    {
        var text = w.Text;
        var i = 0;
        while (i < text.Length)
        {
            var c = text[i];
            var next = i + 1 < text.Length ? text[i + 1] : '\0';
            if (c == '#')
            {
                w.Token(i, text.Length, "comment");
                return;
            }

            if (c == '"')
            {
                var start = i++;
                while (i < text.Length && text[i] != '"')
                    i += text[i] == '\\' ? 2 : 1;
                i = Math.Min(text.Length, i + 1);
                w.Token(start, i, "string");
                continue;
            }

            if (c == '$' && (next == '{' || string.CompareOrdinal(text, i + 1, "ENV{", 0, 4) == 0))
            {
                var open = text.IndexOf('{', i);
                var end = ReferenceEnd(text, open);
                w.Token(i, end, "attribute");
                i = end;
                continue;
            }

            if (char.IsLetter(c) || c == '_')
            {
                var start = i++;
                while (i < text.Length && IsIdentifierChar(text[i])) i++;
                var after = i;
                while (after < text.Length && text[after] is ' ' or '\t') after++;
                if (after < text.Length && text[after] == '(' ||
                    CMakeKeywords.Contains(text.Substring(start, i - start)))
                    w.Token(start, i, "keyword");
                continue;
            }

            if (char.IsDigit(c) && (i == 0 || !IsIdentifierChar(text[i - 1])))
            {
                var start = i++;
                while (i < text.Length && (char.IsDigit(text[i]) || text[i] == '.')) i++;
                w.Token(start, i, "number");
                continue;
            }

            i++;
        }
    }

    /// <summary>
    ///     ProGuard / R8 の設定 (.pro) の行を色分けします。
    ///     # 以降はコメント、-keep などのオプションと Java の修飾子はキーワード、@注釈と &lt;init&gt; などは属性にします。
    /// </summary>
    /// <param name="w">出力先。</param>
    private static void LexProGuard(SyntaxWriter w)
    {
        var text = w.Text;
        var i = 0;
        while (i < text.Length)
        {
            var c = text[i];
            var next = i + 1 < text.Length ? text[i + 1] : '\0';
            if (c == '#')
            {
                w.Token(i, text.Length, "comment");
                return;
            }

            if (c == '-' && char.IsLetter(next) && (i == 0 || text[i - 1] is ' ' or '\t'))
            {
                var start = i++;
                while (i < text.Length && (char.IsLetter(text[i]) || text[i] == '-')) i++;
                w.Token(start, i, "keyword");
                continue;
            }

            if (c == '@' && char.IsLetter(next))
            {
                var start = i++;
                while (i < text.Length && (IsIdentifierChar(text[i]) || text[i] == '.')) i++;
                w.Token(start, i, "attribute");
                continue;
            }

            if (c == '<' && char.IsLetter(next))
            {
                var end = text.IndexOf('>', i);
                if (end > i)
                {
                    w.Token(i, end + 1, "attribute");
                    i = end + 1;
                    continue;
                }
            }

            if (char.IsLetter(c) || c == '_')
            {
                var start = i++;
                while (i < text.Length && IsIdentifierChar(text[i])) i++;
                if (!IsMemberName(text, start) && ProGuardKeywords.Contains(text.Substring(start, i - start)))
                    w.Token(start, i, "keyword");
                continue;
            }

            i++;
        }
    }

    /// <summary>
    ///     バッチ ファイル (.bat / .cmd) の行を色分けします。REM と :: はコメント、:ラベルと %変数% / !変数! は属性にします。
    /// </summary>
    /// <param name="w">出力先。</param>
    private static void LexBatch(SyntaxWriter w)
    {
        var text = w.Text;
        var i = 0;
        while (i < text.Length && text[i] is ' ' or '\t' or '@') i++;
        if (string.CompareOrdinal(text, i, "::", 0, 2) == 0)
        {
            w.Token(i, text.Length, "comment");
            return;
        }

        if (i < text.Length && text[i] == ':')
        {
            w.Token(i, text.Length, "attribute");
            return;
        }

        while (i < text.Length)
        {
            var c = text[i];
            var next = i + 1 < text.Length ? text[i + 1] : '\0';
            if (c == '"')
            {
                var end = text.IndexOf('"', i + 1);
                var stop = end < 0 ? text.Length : end + 1;
                w.Token(i, stop, "string");
                i = stop;
                continue;
            }

            if (c == '%')
            {
                // %%i (for の変数)、%1 / %~dp0 (引数)、%NAME% / %NAME:~0,4% (環境変数)。
                var start = i;
                if (next is '%' or '~')
                {
                    i += next == '%' ? 2 : 1;
                    if (i < text.Length && text[i] == '~')
                    {
                        // %~dp0 / %%~nxi などの修飾子付き。
                        i++;
                        while (i < text.Length && char.IsLetterOrDigit(text[i])) i++;
                    }
                    else if (i < text.Length && IsIdentifierChar(text[i])) i++;
                }
                else if (char.IsDigit(next) || next == '*') i += 2;
                else
                {
                    var close = text.IndexOf('%', i + 1);
                    if (close <= i + 1 || text.IndexOf(' ', i, close - i) >= 0)
                    {
                        i++;
                        continue;
                    }

                    i = close + 1;
                }

                w.Token(start, i, "attribute");
                continue;
            }

            if (c == '!' && (char.IsLetter(next) || next == '_'))
            {
                var close = text.IndexOf('!', i + 1);
                if (close > i && text.IndexOf(' ', i, close - i) < 0)
                {
                    w.Token(i, close + 1, "attribute");
                    i = close + 1;
                    continue;
                }
            }

            if (char.IsLetter(c) || c == '_')
            {
                var start = i++;
                while (i < text.Length && (IsIdentifierChar(text[i]) || text[i] == '.')) i++;
                var word = text.Substring(start, i - start);
                if (word.Equals("rem", StringComparison.OrdinalIgnoreCase) &&
                    (i == text.Length || text[i] is ' ' or '\t'))
                {
                    w.Token(start, text.Length, "comment");
                    return;
                }

                if (BatchKeywords.Contains(word)) w.Token(start, i, "keyword");
                continue;
            }

            if (char.IsDigit(c) && (i == 0 || !IsIdentifierChar(text[i - 1])))
            {
                var start = i++;
                while (i < text.Length && char.IsDigit(text[i])) i++;
                if (i == text.Length || !IsIdentifierChar(text[i])) w.Token(start, i, "number");
                continue;
            }

            i++;
        }
    }

    /// <summary>
    ///     PowerShell (.ps1 / .psm1 / .psd1) の行を色分けします。&lt;# #&gt; のコメントとヒア文字列 (@" "@ / @' '@) は行をまたいで引き継ぎます。
    /// </summary>
    /// <param name="w">出力先。</param>
    /// <param name="state">行をまたぐ字句状態。次の行に引き継ぎます。</param>
    private static void LexPowerShell(SyntaxWriter w, ref int state)
    {
        var text = w.Text;
        var i = 0;
        if (state is PowerShellHereDouble or PowerShellHereSingle)
        {
            // ヒア文字列は、行頭の "@ / '@ で終わる。
            var close = state == PowerShellHereDouble ? "\"@" : "'@";
            if (!text.StartsWith(close, StringComparison.Ordinal))
            {
                w.Token(0, text.Length, "string");
                return;
            }

            w.Token(0, 2, "string");
            state = 0;
            i = 2;
        }

        while (i < text.Length)
        {
            if (state == PowerShellBlockComment)
            {
                var end = text.IndexOf("#>", i, StringComparison.Ordinal);
                var stop = end < 0 ? text.Length : end + 2;
                w.Token(i, stop, "comment");
                if (end >= 0) state = 0;
                i = stop;
                continue;
            }

            var c = text[i];
            var next = i + 1 < text.Length ? text[i + 1] : '\0';
            if (c == '<' && next == '#')
            {
                state = PowerShellBlockComment;
                w.Token(i, i + 2, "comment");
                i += 2;
                continue;
            }

            if (c == '#')
            {
                w.Token(i, text.Length, "comment");
                return;
            }

            if (c == '@' && next is '"' or '\'' && text.AsSpan(i + 2).Trim().IsEmpty)
            {
                state = next == '"' ? PowerShellHereDouble : PowerShellHereSingle;
                w.Token(i, text.Length, "string");
                return;
            }

            if (c is '"' or '\'')
            {
                // "..." は ` でエスケープ、どちらも引用符を 2 つ重ねると引用符そのもの。
                var start = i++;
                while (i < text.Length)
                {
                    if (c == '"' && text[i] == '`')
                    {
                        i += 2;
                        continue;
                    }

                    if (text[i] == c)
                    {
                        if (i + 1 < text.Length && text[i + 1] == c)
                        {
                            i += 2;
                            continue;
                        }

                        i++;
                        break;
                    }

                    i++;
                }

                i = Math.Min(i, text.Length);
                w.Token(start, i, "string");
                continue;
            }

            if (c == '$' && (IsIdentifierChar(next) || next is '{' or '_' or '?' or '$'))
            {
                // $name、$env:Path、${name}。
                var start = i++;
                if (next == '{')
                {
                    var end = text.IndexOf('}', i);
                    i = end < 0 ? text.Length : end + 1;
                }
                else if (next is '?' or '$') i++;
                else
                    while (i < text.Length && (IsIdentifierChar(text[i]) || text[i] == ':' && i + 1 < text.Length &&
                               IsIdentifierChar(text[i + 1]))) i++;

                w.Token(start, i, "attribute");
                continue;
            }

            if (c == '-' && char.IsLetter(next) && (i == 0 || text[i - 1] is ' ' or '\t' or '(' or '{'))
            {
                // -eq などの演算子。-Path などのパラメーター名は色分けしない。
                var start = i++;
                while (i < text.Length && char.IsLetter(text[i])) i++;
                if (PowerShellOperators.Contains(text.Substring(start + 1, i - start - 1))) w.Token(start, i, "keyword");
                continue;
            }

            if (c == '[' && (char.IsLetter(next) || next == '_'))
            {
                // [string] / [System.IO.File] などの型。
                var end = i + 1;
                while (end < text.Length && (IsIdentifierChar(text[end]) || text[end] is '.' or '[' or ']' && text[end - 1] == '['))
                    end++;
                if (end < text.Length && text[end] == ']')
                {
                    w.Token(i, end + 1, "attribute");
                    i = end + 1;
                    continue;
                }
            }

            if (char.IsLetter(c) || c == '_')
            {
                var start = i++;
                while (i < text.Length && (IsIdentifierChar(text[i]) || text[i] == '-')) i++;
                if (!IsMemberName(text, start) && PowerShellKeywords.Contains(text.Substring(start, i - start)))
                    w.Token(start, i, "keyword");
                continue;
            }

            if (char.IsDigit(c))
            {
                var start = i++;
                while (i < text.Length && (char.IsLetterOrDigit(text[i]) || text[i] == '.')) i++;
                w.Token(start, i, "number");
                continue;
            }

            i++;
        }
    }

    /// <summary>
    ///     シェル スクリプト (.sh / .bash) の行を色分けします。# 以降はコメント、$変数は属性にします。
    /// </summary>
    /// <param name="w">出力先。</param>
    private static void LexShell(SyntaxWriter w)
    {
        var text = w.Text;
        var i = 0;
        while (i < text.Length)
        {
            var c = text[i];
            var next = i + 1 < text.Length ? text[i + 1] : '\0';
            if (c == '#' && (i == 0 || text[i - 1] is ' ' or '\t' or ';'))
            {
                w.Token(i, text.Length, "comment");
                return;
            }

            if (c is '"' or '\'')
            {
                var start = i++;
                while (i < text.Length && text[i] != c)
                    i += c == '"' && text[i] == '\\' ? 2 : 1;
                i = Math.Min(text.Length, i + 1);
                w.Token(start, i, "string");
                continue;
            }

            if (c == '$')
            {
                // $NAME、${NAME}、$1、$@ などの特殊変数。
                var start = i++;
                if (next == '{')
                {
                    var end = text.IndexOf('}', i);
                    i = end < 0 ? text.Length : end + 1;
                }
                else if (char.IsLetter(next) || next == '_')
                    while (i < text.Length && IsIdentifierChar(text[i])) i++;
                else if (char.IsDigit(next) || next is '@' or '#' or '?' or '*' or '!' or '$' or '-') i++;
                else continue;

                w.Token(start, i, "attribute");
                continue;
            }

            if (char.IsLetter(c) || c == '_')
            {
                var start = i++;
                while (i < text.Length && (IsIdentifierChar(text[i]) || text[i] == '-')) i++;
                if (ShellKeywords.Contains(text.Substring(start, i - start))) w.Token(start, i, "keyword");
                continue;
            }

            if (char.IsDigit(c))
            {
                var start = i++;
                while (i < text.Length && IsIdentifierChar(text[i])) i++;
                if (start == 0 || !IsIdentifierChar(text[start - 1])) w.Token(start, i, "number");
                continue;
            }

            i++;
        }
    }

    /// <summary>
    ///     INI 形式 (.ini / .vbp / .vbg) と .properties の行を色分けします。
    ///     コメント行、[セクション]、キー、値 (引用符で囲んだ文字列、数値) を色分けします。
    /// </summary>
    /// <param name="w">出力先。</param>
    /// <param name="properties">.properties の場合は true (= のほかに : も区切りにする)。</param>
    private static void LexIni(SyntaxWriter w, bool properties)
    {
        var text = w.Text;
        var i = 0;
        while (i < text.Length && text[i] is ' ' or '\t') i++;
        if (i == text.Length) return;
        if (text[i] is ';' or '#' or '!')
        {
            w.Token(i, text.Length, "comment");
            return;
        }

        if (text[i] == '[')
        {
            var end = text.IndexOf(']', i);
            w.Token(i, end < 0 ? text.Length : end + 1, "keyword");
            return;
        }

        var separator = text.IndexOf('=', i);
        if (properties)
        {
            var colon = text.IndexOf(':', i);
            if (colon >= 0 && (separator < 0 || colon < separator)) separator = colon;
        }

        if (separator < 0) return;
        var keyEnd = separator;
        while (keyEnd > i && text[keyEnd - 1] is ' ' or '\t') keyEnd--;
        w.Token(i, keyEnd, "attribute");

        var valueStart = separator + 1;
        while (valueStart < text.Length && text[valueStart] is ' ' or '\t') valueStart++;
        var valueEnd = text.Length;
        while (valueEnd > valueStart && text[valueEnd - 1] is ' ' or '\t') valueEnd--;
        if (valueEnd <= valueStart) return;
        var value = text.AsSpan(valueStart, valueEnd - valueStart);
        if (value.Length >= 2 && value[0] == '"' && value[^1] == '"')
            w.Token(valueStart, valueEnd, "string");
        else if (double.TryParse(value, System.Globalization.NumberStyles.Float,
                     System.Globalization.CultureInfo.InvariantCulture, out _) ||
                 value.Length > 2 && value[0] == '&' && value[1] is 'H' or 'h')
            w.Token(valueStart, valueEnd, "number");
    }
}
