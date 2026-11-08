using System;
using System.Collections.Generic;
using System.IO;
using System.Windows.Documents;

namespace DesktopIniManager.Views;

internal sealed partial class DiffViewWindow
{
    /// <summary>
    ///     シンタックス ハイライトの言語。拡張子から自動判定します。
    /// </summary>
    private enum SyntaxKind
    {
        None,
        CSharp,
        Xml,
        Json,
        VisualBasic,
        Sql,
        Java,
        Kotlin,
        Groovy,
        JavaScript,
        TypeScript,
        Php,
        Css,
        Scss,
        Html,
        Batch,
        PowerShell,
        Shell,
        Ini,
        Properties,
        Cpp,
        Make,
        CMake,
        ProGuard
    }

    // C# の字句状態（複数行コメントの中）。
    private const int CSharpBlockComment = 1;

    // XML の字句状態。
    private const int XmlComment = 1;
    private const int XmlInTag = 2;
    private const int XmlDoubleQuoted = 3;
    private const int XmlSingleQuoted = 4;
    private const int XmlCData = 5;

    // SQL の字句状態。
    private const int SqlBlockComment = 1;
    private const int SqlString = 2;

    private static readonly Dictionary<string, SyntaxKind> SyntaxByExtension = new(StringComparer.OrdinalIgnoreCase)
    {
        [".cs"] = SyntaxKind.CSharp,
        [".xaml"] = SyntaxKind.Xml,
        [".xml"] = SyntaxKind.Xml,
        [".csproj"] = SyntaxKind.Xml,
        [".vbproj"] = SyntaxKind.Xml,
        [".vcxproj"] = SyntaxKind.Xml,
        [".props"] = SyntaxKind.Xml,
        [".targets"] = SyntaxKind.Xml,
        [".config"] = SyntaxKind.Xml,
        [".resx"] = SyntaxKind.Xml,
        [".settings"] = SyntaxKind.Xml,
        [".manifest"] = SyntaxKind.Xml,
        [".nuspec"] = SyntaxKind.Xml,
        [".xsd"] = SyntaxKind.Xml,
        [".xsl"] = SyntaxKind.Xml,
        [".xslt"] = SyntaxKind.Xml,
        [".svg"] = SyntaxKind.Xml,
        [".htm"] = SyntaxKind.Html,
        [".html"] = SyntaxKind.Html,
        [".json"] = SyntaxKind.Json,
        [".bas"] = SyntaxKind.VisualBasic,
        [".frm"] = SyntaxKind.VisualBasic,
        [".cls"] = SyntaxKind.VisualBasic,
        [".ctl"] = SyntaxKind.VisualBasic,
        [".dsr"] = SyntaxKind.VisualBasic,
        [".dob"] = SyntaxKind.VisualBasic,
        [".pag"] = SyntaxKind.VisualBasic,
        [".vb"] = SyntaxKind.VisualBasic,
        [".vbs"] = SyntaxKind.VisualBasic,
        [".sql"] = SyntaxKind.Sql,
        [".java"] = SyntaxKind.Java,
        [".kt"] = SyntaxKind.Kotlin,
        [".kts"] = SyntaxKind.Kotlin,
        [".gradle"] = SyntaxKind.Groovy,
        [".groovy"] = SyntaxKind.Groovy,
        [".js"] = SyntaxKind.JavaScript,
        [".mjs"] = SyntaxKind.JavaScript,
        [".cjs"] = SyntaxKind.JavaScript,
        [".jsx"] = SyntaxKind.JavaScript,
        [".ts"] = SyntaxKind.TypeScript,
        [".mts"] = SyntaxKind.TypeScript,
        [".cts"] = SyntaxKind.TypeScript,
        [".tsx"] = SyntaxKind.TypeScript,
        [".php"] = SyntaxKind.Php,
        [".phtml"] = SyntaxKind.Php,
        [".css"] = SyntaxKind.Css,
        [".scss"] = SyntaxKind.Scss,
        [".less"] = SyntaxKind.Scss,
        [".bat"] = SyntaxKind.Batch,
        [".cmd"] = SyntaxKind.Batch,
        [".ps1"] = SyntaxKind.PowerShell,
        [".psm1"] = SyntaxKind.PowerShell,
        [".psd1"] = SyntaxKind.PowerShell,
        [".sh"] = SyntaxKind.Shell,
        [".bash"] = SyntaxKind.Shell,
        [".ini"] = SyntaxKind.Ini,
        [".vbp"] = SyntaxKind.Ini,
        [".vbg"] = SyntaxKind.Ini,
        [".properties"] = SyntaxKind.Properties,
        [".c"] = SyntaxKind.Cpp,
        [".cpp"] = SyntaxKind.Cpp,
        [".cc"] = SyntaxKind.Cpp,
        [".cxx"] = SyntaxKind.Cpp,
        [".h"] = SyntaxKind.Cpp,
        [".hpp"] = SyntaxKind.Cpp,
        [".hh"] = SyntaxKind.Cpp,
        [".hxx"] = SyntaxKind.Cpp,
        [".inl"] = SyntaxKind.Cpp,
        [".mk"] = SyntaxKind.Make,
        [".cmake"] = SyntaxKind.CMake,
        [".pro"] = SyntaxKind.ProGuard,
        [".aidl"] = SyntaxKind.Java
    };

    // 拡張子で判定できないファイル (拡張子より優先)。
    private static readonly Dictionary<string, SyntaxKind> SyntaxByFileName = new(StringComparer.OrdinalIgnoreCase)
    {
        ["Makefile"] = SyntaxKind.Make,
        ["GNUmakefile"] = SyntaxKind.Make,
        ["CMakeLists.txt"] = SyntaxKind.CMake
    };

    private static readonly HashSet<string> VisualBasicKeywords = new(StringComparer.OrdinalIgnoreCase)
    {
        "AddHandler", "AddressOf", "Alias", "And", "AndAlso", "As", "Async", "Attribute", "Await", "Begin",
        "Boolean", "ByRef", "Byte", "ByVal", "Call", "Case", "Catch", "CBool", "CByte", "CChar", "CDate", "CDbl",
        "CDec", "Char", "CInt", "Class", "CLng", "CObj", "Const", "Continue", "CShort", "CSng", "CStr", "CType",
        "Currency", "Date", "Decimal", "Declare", "Default", "Delegate", "Dim", "DirectCast", "Do", "Double",
        "Each", "Else", "ElseIf", "End", "Enum", "Erase", "Error", "Event", "Exit", "Explicit", "False",
        "Finally", "For", "Friend", "Function", "Get", "GetType", "Global", "GoSub", "GoTo", "Handles", "If",
        "Implements", "Imports", "In", "Inherits", "Integer", "Interface", "Is", "IsNot", "Let", "Lib", "Like",
        "Long", "Loop", "Me", "Mod", "Module", "MustInherit", "MustOverride", "MyBase", "MyClass", "Namespace",
        "New", "Next", "Not", "Nothing", "NotInheritable", "NotOverridable", "Object", "Of", "On", "Operator",
        "Option", "Optional", "Or", "OrElse", "Overloads", "Overridable", "Overrides", "ParamArray", "Partial",
        "Preserve", "Private", "Property", "Protected", "Public", "RaiseEvent", "ReadOnly", "ReDim",
        "RemoveHandler", "Resume", "Return", "Select", "Set", "Shadows", "Shared", "Short", "Single", "Static",
        "Step", "Stop", "String", "Structure", "Sub", "Then", "Throw", "To", "True", "Try", "TryCast", "Type",
        "TypeOf", "Until", "Using", "Variant", "Wend", "When", "While", "With", "WithEvents", "WriteOnly", "Xor",
        "Yield"
    };

    private static readonly HashSet<string> SqlKeywords = new(StringComparer.OrdinalIgnoreCase)
    {
        "ADD", "ALL", "ALTER", "AND", "ANY", "APPLY", "AS", "ASC", "AVG", "BEGIN", "BETWEEN", "BIGINT", "BINARY",
        "BIT", "BY", "CASCADE", "CASE", "CAST", "CATCH", "CHAR", "CHECK", "CLOSE", "CLUSTERED", "COALESCE",
        "COLUMN", "COMMIT", "CONSTRAINT", "CONVERT", "COUNT", "CREATE", "CROSS", "CURSOR", "DATABASE", "DATE",
        "DATETIME", "DATETIME2", "DEALLOCATE", "DECIMAL", "DECLARE", "DEFAULT", "DELETE", "DESC", "DISTINCT",
        "DROP", "ELSE", "END", "EXCEPT", "EXEC", "EXECUTE", "EXISTS", "FETCH", "FLOAT", "FOR", "FOREIGN", "FROM",
        "FULL", "FUNCTION", "GO", "GRANT", "GROUP", "HAVING", "IDENTITY", "IF", "IN", "INDEX", "INNER", "INSERT",
        "INT", "INTEGER", "INTERSECT", "INTO", "IS", "ISNULL", "JOIN", "KEY", "LEFT", "LIKE", "LIMIT", "MAX",
        "MERGE", "MIN", "MONEY", "NCHAR", "NEXT", "NOCOUNT", "NOLOCK", "NONCLUSTERED", "NOT", "NTEXT", "NULL",
        "NUMERIC", "NVARCHAR", "OFFSET", "ON", "ONLY", "OPEN", "OR", "ORDER", "OUTER", "OUTPUT", "OVER",
        "PARTITION", "PRIMARY", "PRINT", "PROC", "PROCEDURE", "RAISERROR", "REAL", "REFERENCES", "RETURN",
        "RETURNS", "REVOKE", "RIGHT", "ROLLBACK", "ROW_NUMBER", "ROWS", "SCHEMA", "SELECT", "SET", "SMALLINT",
        "SUM", "TABLE", "TEXT", "THEN", "THROW", "TIME", "TIMESTAMP", "TINYINT", "TOP", "TRAN", "TRANSACTION",
        "TRIGGER", "TRUNCATE", "TRY", "UNION", "UNIQUE", "UNIQUEIDENTIFIER", "UPDATE", "USE", "USING", "VALUES",
        "VARBINARY", "VARCHAR", "VIEW", "WHEN", "WHERE", "WHILE", "WITH"
    };

    // Evaluated once per pane render; recompute only when the file changes.
    private string _syntaxSourcePath;
    private SyntaxKind _syntaxKind;

    /// <summary>
    ///     表示中のファイルに適用するシンタックス ハイライトの言語。チェック ボックスが OFF の場合は None。
    /// </summary>
    private SyntaxKind SyntaxLanguage
    {
        get
        {
            if (!ViewModel.SyntaxHighlight) return SyntaxKind.None;
            var path = ViewModel.CurrentPatch?.Entry.RelativePath ?? ViewModel.File?.RelativePath ?? string.Empty;
            if (!ReferenceEquals(path, _syntaxSourcePath))
            {
                _syntaxSourcePath = path;
                _syntaxKind = SyntaxByFileName.TryGetValue(Path.GetFileName(path), out var byName)
                    ? byName
                    : SyntaxByExtension.GetValueOrDefault(Path.GetExtension(path), SyntaxKind.None);
            }

            return _syntaxKind;
        }
    }

    /// <summary>
    ///     行をまたぐ字句状態 (複数行コメントなど) を持つ言語かどうかを判定します。
    /// </summary>
    /// <param name="language">言語。</param>
    /// <returns>行をまたぐ状態を持つ場合は true。</returns>
    private static bool HasLexState(SyntaxKind language)
    {
        return language is not (SyntaxKind.None or SyntaxKind.Json or SyntaxKind.VisualBasic or SyntaxKind.Batch or
            SyntaxKind.Shell or SyntaxKind.Ini or SyntaxKind.Properties or SyntaxKind.Make or SyntaxKind.CMake or
            SyntaxKind.ProGuard);
    }

    /// <summary>
    ///     チェック ボックスの切り替え後、先頭の行を保ったまま両側のペインを描画し直します。
    /// </summary>
    private void RefreshSyntaxHighlight()
    {
        if (_leftList == null || _rightList == null || !ViewModel.IsText || _renderingContent) return;
        var scroll = FindScroll(_leftList);
        var topRow = RowAt(scroll?.VerticalOffset ?? 0);
        var horizontal = scroll?.HorizontalOffset ?? 0;
        // Retained paragraphs carry the previous colors; rebuild them.
        _renderedPanes.Clear();
        RefreshFoldedPanes(RowTop(topRow), horizontal, Current);
    }

    /// <summary>
    ///     言語に応じたシンタックス ハイライトを付けて、行のテキストを段落に追加します。
    /// </summary>
    /// <param name="paragraph">追加先の段落。null の場合は字句状態の更新だけ行います。</param>
    /// <param name="text">行のテキスト。</param>
    /// <param name="language">言語。</param>
    /// <param name="state">行をまたぐ字句状態。次の行に引き継ぎます。</param>
    private void AddSyntaxRuns(Paragraph paragraph, string text, SyntaxKind language, ref int state)
    {
        switch (language)
        {
            case SyntaxKind.CSharp:
                var blockComment = state == CSharpBlockComment;
                AddCSharpRuns(paragraph, text, ref blockComment);
                state = blockComment ? CSharpBlockComment : 0;
                return;
            case SyntaxKind.Xml:
                AddXmlRuns(paragraph, text, ref state);
                return;
            case SyntaxKind.Json:
                if (paragraph != null) AddJsonRuns(paragraph, text);
                return;
            case SyntaxKind.VisualBasic:
                if (paragraph != null) AddVisualBasicRuns(paragraph, text);
                return;
            case SyntaxKind.Sql:
                AddSqlRuns(paragraph, text, ref state);
                return;
            case SyntaxKind.None:
                paragraph?.Inlines.Add(new Run(text));
                return;
        }

        // 範囲単位の字句解析 (Web 系・スクリプト系)。
        var writer = new SyntaxWriter(this, paragraph, text);
        switch (language)
        {
            case SyntaxKind.Java:
                LexCode(writer, 0, text.Length, JavaStyle, ref state);
                break;
            case SyntaxKind.Kotlin:
                LexCode(writer, 0, text.Length, KotlinStyle, ref state);
                break;
            case SyntaxKind.Groovy:
                LexCode(writer, 0, text.Length, GroovyStyle, ref state);
                break;
            case SyntaxKind.JavaScript:
                LexCode(writer, 0, text.Length, JavaScriptStyle, ref state);
                break;
            case SyntaxKind.TypeScript:
                LexCode(writer, 0, text.Length, TypeScriptStyle, ref state);
                break;
            case SyntaxKind.Php:
                LexPhp(writer, ref state);
                break;
            case SyntaxKind.Css:
            case SyntaxKind.Scss:
                LexCss(writer, 0, text.Length, language == SyntaxKind.Scss, ref state);
                break;
            case SyntaxKind.Html:
                state = LexHtml(writer, 0, text.Length, state);
                break;
            case SyntaxKind.Batch:
                LexBatch(writer);
                break;
            case SyntaxKind.PowerShell:
                LexPowerShell(writer, ref state);
                break;
            case SyntaxKind.Shell:
                LexShell(writer);
                break;
            case SyntaxKind.Ini:
            case SyntaxKind.Properties:
                LexIni(writer, language == SyntaxKind.Properties);
                break;
            case SyntaxKind.Cpp:
                LexCode(writer, 0, text.Length, CppStyle, ref state);
                break;
            case SyntaxKind.Make:
                LexMake(writer);
                break;
            case SyntaxKind.CMake:
                LexCMake(writer);
                break;
            case SyntaxKind.ProGuard:
                LexProGuard(writer);
                break;
        }

        writer.Finish();
    }

    /// <summary>
    ///     XML / XAML の要素名・属性名 (x:Name など)・属性値・コメントを色分けして、行のテキストを段落に追加します。
    ///     複数行にまたがるタグ・コメント・属性値は字句状態で引き継ぎます。
    /// </summary>
    /// <param name="paragraph">追加先の段落。null の場合は字句状態の更新だけ行います。</param>
    /// <param name="text">行のテキスト。</param>
    /// <param name="state">行をまたぐ字句状態。次の行に引き継ぎます。</param>
    private void AddXmlRuns(Paragraph paragraph, string text, ref int state)
    {
        int i = 0, plain = 0;

        void Token(int start, int end, string kind)
        {
            if (paragraph == null)
            {
                plain = end;
                return;
            }

            AddPlainRun(paragraph, text, plain, start - plain);
            AddColoredRun(paragraph, text.Substring(start, end - start), kind);
            plain = end;
        }

        while (i < text.Length)
        {
            switch (state)
            {
                case XmlComment:
                case XmlCData:
                {
                    var close = state == XmlComment ? "-->" : "]]>";
                    var end = text.IndexOf(close, i, StringComparison.Ordinal);
                    var stop = end < 0 ? text.Length : end + close.Length;
                    Token(i, stop, state == XmlComment ? "comment" : "string");
                    if (end >= 0) state = 0;
                    i = stop;
                    continue;
                }
                case XmlDoubleQuoted:
                case XmlSingleQuoted:
                {
                    var end = text.IndexOf(state == XmlDoubleQuoted ? '"' : '\'', i);
                    var stop = end < 0 ? text.Length : end + 1;
                    Token(i, stop, "string");
                    if (end >= 0) state = XmlInTag;
                    i = stop;
                    continue;
                }
                case XmlInTag:
                {
                    var c = text[i];
                    if (char.IsWhiteSpace(c))
                    {
                        i++;
                        continue;
                    }

                    if (c == '>' || c == '=')
                    {
                        Token(i, i + 1, "keyword");
                        if (c == '>') state = 0;
                        i++;
                        continue;
                    }

                    if ((c == '/' || c == '?') && i + 1 < text.Length && text[i + 1] == '>')
                    {
                        Token(i, i + 2, "keyword");
                        state = 0;
                        i += 2;
                        continue;
                    }

                    if (c == '"' || c == '\'')
                    {
                        state = c == '"' ? XmlDoubleQuoted : XmlSingleQuoted;
                        var end = text.IndexOf(c, i + 1);
                        var stop = end < 0 ? text.Length : end + 1;
                        Token(i, stop, "string");
                        if (end >= 0) state = XmlInTag;
                        i = stop;
                        continue;
                    }

                    var start = i;
                    while (i < text.Length && !char.IsWhiteSpace(text[i]) &&
                           text[i] != '=' && text[i] != '>' && text[i] != '"' && text[i] != '\'' &&
                           !(text[i] == '/' && i + 1 < text.Length && text[i + 1] == '>')) i++;
                    if (i == start) i++;
                    Token(start, i, "attribute");
                    continue;
                }
                default:
                {
                    if (string.CompareOrdinal(text, i, "<!--", 0, 4) == 0)
                    {
                        state = XmlComment;
                        Token(i, i + 4, "comment");
                        i += 4;
                        continue;
                    }

                    if (string.CompareOrdinal(text, i, "<![CDATA[", 0, 9) == 0)
                    {
                        state = XmlCData;
                        Token(i, i + 9, "keyword");
                        i += 9;
                        continue;
                    }

                    if (text[i] == '<' && i + 1 < text.Length &&
                        (char.IsLetter(text[i + 1]) || text[i + 1] is '/' or '?' or '!' or '_' or ':'))
                    {
                        // "<" や "</" と要素名 (x:Static など) をまとめて色分けする。
                        var start = i++;
                        if (text[i] is '/' or '?' or '!') i++;
                        while (i < text.Length && !char.IsWhiteSpace(text[i]) && text[i] != '>' && text[i] != '/')
                            i++;
                        Token(start, i, "keyword");
                        state = XmlInTag;
                        continue;
                    }

                    if (text[i] == '&')
                    {
                        var end = text.IndexOf(';', i + 1);
                        if (end > i + 1 && end - i <= 12)
                        {
                            Token(i, end + 1, "keyword");
                            i = end + 1;
                            continue;
                        }
                    }

                    i++;
                    continue;
                }
            }
        }

        AddPlainRun(paragraph, text, plain, text.Length - plain);
    }

    /// <summary>
    ///     VB6 / VB.NET / VBScript のキーワード・文字列・コメント (' と Rem)・数値を色分けして、行のテキストを段落に追加します。
    /// </summary>
    /// <param name="paragraph">追加先の段落。</param>
    /// <param name="text">行のテキスト。</param>
    private void AddVisualBasicRuns(Paragraph paragraph, string text)
    {
        int i = 0, plain = 0;

        void Token(int start, int end, string kind)
        {
            if (paragraph == null)
            {
                plain = end;
                return;
            }

            AddPlainRun(paragraph, text, plain, start - plain);
            AddColoredRun(paragraph, text.Substring(start, end - start), kind);
            plain = end;
        }

        while (i < text.Length)
        {
            var c = text[i];
            if (c == '\'')
            {
                Token(i, text.Length, "comment");
                return;
            }

            if (c == '"')
            {
                // "" は文字列中の引用符。
                var start = i++;
                while (i < text.Length)
                {
                    if (text[i] != '"')
                    {
                        i++;
                        continue;
                    }

                    if (i + 1 < text.Length && text[i + 1] == '"')
                    {
                        i += 2;
                        continue;
                    }

                    i++;
                    break;
                }

                Token(start, i, "string");
                continue;
            }

            if (char.IsLetter(c) || c == '_')
            {
                var start = i++;
                while (i < text.Length && (char.IsLetterOrDigit(text[i]) || text[i] == '_')) i++;
                var word = text.Substring(start, i - start);
                // メンバー アクセス (obj.Text など) の名前はキーワードとして扱わない。
                if (IsMemberName(text, start)) continue;
                if (string.Equals(word, "Rem", StringComparison.OrdinalIgnoreCase) &&
                    (i == text.Length || char.IsWhiteSpace(text[i])))
                {
                    Token(start, text.Length, "comment");
                    return;
                }

                if (VisualBasicKeywords.Contains(word)) Token(start, i, "keyword");
                continue;
            }

            if (c == '&' && i + 1 < text.Length && (text[i + 1] is 'H' or 'h' or 'O' or 'o'))
            {
                var start = i;
                i += 2;
                while (i < text.Length && Uri.IsHexDigit(text[i])) i++;
                if (i < text.Length && text[i] == '&') i++;
                if (i > start + 2) Token(start, i, "number");
                continue;
            }

            if (char.IsDigit(c))
            {
                var start = i++;
                while (i < text.Length && (char.IsLetterOrDigit(text[i]) || text[i] == '.')) i++;
                Token(start, i, "number");
                continue;
            }

            i++;
        }

        AddPlainRun(paragraph, text, plain, text.Length - plain);
    }

    /// <summary>
    ///     SQL のキーワード・文字列・コメント (-- と /* */)・数値を色分けして、行のテキストを段落に追加します。
    ///     複数行にまたがるコメントと文字列は字句状態で引き継ぎます。
    /// </summary>
    /// <param name="paragraph">追加先の段落。null の場合は字句状態の更新だけ行います。</param>
    /// <param name="text">行のテキスト。</param>
    /// <param name="state">行をまたぐ字句状態。次の行に引き継ぎます。</param>
    private void AddSqlRuns(Paragraph paragraph, string text, ref int state)
    {
        int i = 0, plain = 0;

        void Token(int start, int end, string kind)
        {
            if (paragraph == null)
            {
                plain = end;
                return;
            }

            AddPlainRun(paragraph, text, plain, start - plain);
            AddColoredRun(paragraph, text.Substring(start, end - start), kind);
            plain = end;
        }

        // '' は文字列中の引用符。閉じた位置の次を返し、行末まで閉じない場合は -1 を返す。
        int StringEnd(int from)
        {
            while (from < text.Length)
            {
                if (text[from] != '\'')
                {
                    from++;
                    continue;
                }

                if (from + 1 < text.Length && text[from + 1] == '\'')
                {
                    from += 2;
                    continue;
                }

                return from + 1;
            }

            return -1;
        }

        while (i < text.Length)
        {
            if (state == SqlBlockComment)
            {
                var end = text.IndexOf("*/", i, StringComparison.Ordinal);
                var stop = end < 0 ? text.Length : end + 2;
                Token(i, stop, "comment");
                if (end >= 0) state = 0;
                i = stop;
                continue;
            }

            if (state == SqlString)
            {
                var end = StringEnd(i);
                Token(i, end < 0 ? text.Length : end, "string");
                if (end < 0) return;
                state = 0;
                i = end;
                continue;
            }

            var c = text[i];
            if (c == '-' && i + 1 < text.Length && text[i + 1] == '-')
            {
                Token(i, text.Length, "comment");
                return;
            }

            if (c == '/' && i + 1 < text.Length && text[i + 1] == '*')
            {
                state = SqlBlockComment;
                Token(i, i + 2, "comment");
                i += 2;
                continue;
            }

            if (c == '\'' || ((c is 'N' or 'n') && i + 1 < text.Length && text[i + 1] == '\'' &&
                              (i == 0 || !IsIdentifierChar(text[i - 1]))))
            {
                var start = i;
                i += c == '\'' ? 1 : 2;
                var end = StringEnd(i);
                if (end < 0)
                {
                    Token(start, text.Length, "string");
                    state = SqlString;
                    return;
                }

                Token(start, end, "string");
                i = end;
                continue;
            }

            if (c == '[' || c == '"')
            {
                // 区切り識別子 ([Order] / "Order") はキーワードとして扱わない。
                var end = text.IndexOf(c == '[' ? ']' : '"', i + 1);
                i = end < 0 ? text.Length : end + 1;
                continue;
            }

            if (char.IsLetter(c) || c is '_' or '@' or '#')
            {
                var start = i++;
                while (i < text.Length && (IsIdentifierChar(text[i]) || text[i] is '@' or '#' or '$')) i++;
                if (!IsMemberName(text, start) && SqlKeywords.Contains(text.Substring(start, i - start)))
                    Token(start, i, "keyword");
                continue;
            }

            if (char.IsDigit(c))
            {
                var start = i++;
                while (i < text.Length && (char.IsLetterOrDigit(text[i]) || text[i] == '.')) i++;
                Token(start, i, "number");
                continue;
            }

            i++;
        }

        AddPlainRun(paragraph, text, plain, text.Length - plain);
    }

    /// <summary>
    ///     識別子に使える文字かどうかを判定します。
    /// </summary>
    /// <param name="c">文字。</param>
    /// <returns>英数字またはアンダースコアの場合は true。</returns>
    private static bool IsIdentifierChar(char c)
    {
        return char.IsLetterOrDigit(c) || c == '_';
    }

    /// <summary>
    ///     指定位置の名前が "." の直後 (メンバー アクセス) にあるかどうかを判定します。
    /// </summary>
    /// <param name="text">行のテキスト。</param>
    /// <param name="start">名前の開始位置。</param>
    /// <returns>メンバー アクセスの名前の場合は true。</returns>
    private static bool IsMemberName(string text, int start)
    {
        var i = start - 1;
        while (i >= 0 && text[i] == ' ') i--;
        return i >= 0 && text[i] == '.';
    }
}
