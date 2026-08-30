using System.Collections.Generic;

namespace DesktopIniManager.Models
{
    internal sealed class LanguageProfile
    {
        public LanguageProfile(string name, params string[] extensions) { Name = name; Extensions = extensions; }
        public string Name { get; }
        public string[] Extensions { get; }
        public bool IsFree => Extensions.Length == 0;
        public string ExtensionText => string.Join(" ", Extensions);
        public override string ToString() => Name;

        public static IReadOnlyList<LanguageProfile> All { get; } = new[]
        {
            new LanguageProfile("Free / Plain text"),
            new LanguageProfile("C# / WPF", ".cs", ".xaml", ".cshtml", ".razor", ".json", ".config", ".xml", ".log"),
            new LanguageProfile("VB.NET", ".vb", ".xaml", ".json", ".config", ".xml", ".log"),
            new LanguageProfile("Visual Basic 6", ".bas", ".frm", ".cls", ".ctl", ".vbp", ".ini", ".log"),
            new LanguageProfile("C / C++", ".c", ".cc", ".cpp", ".cxx", ".h", ".hpp", ".rc", ".cmake", ".json", ".xml", ".log"),
            new LanguageProfile("JavaScript", ".js", ".jsx", ".mjs", ".cjs", ".json", ".html", ".htm", ".css", ".log"),
            new LanguageProfile("TypeScript", ".ts", ".tsx", ".json", ".html", ".htm", ".css", ".scss", ".log"),
            new LanguageProfile("PHP", ".php", ".phtml", ".html", ".htm", ".css", ".js", ".json", ".ini", ".log"),
            new LanguageProfile("Java", ".java", ".jsp", ".xml", ".properties", ".gradle", ".json", ".log"),
            new LanguageProfile("Delphi", ".pas", ".dpr", ".dpk", ".dfm", ".fmx", ".ini", ".log"),
            new LanguageProfile("SQL", ".sql", ".ddl", ".ini", ".json", ".xml", ".log")
        };
    }
}
