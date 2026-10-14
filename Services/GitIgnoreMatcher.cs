using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using System.Linq;
using System.Threading;

namespace DesktopIniManager.Services;

internal sealed class GitIgnoreRuleInfo
{
    public GitIgnoreRuleInfo() { }
    public string Scope { get; set; } = "";
    public string Pattern { get; set; }
    public bool Source { get; set; }
    public bool Target { get; set; }
    public string Key => Scope + "\n" + Pattern;
}

// Scoped to the chosen comparison root; no Git executable or repository is required.
internal sealed class GitIgnoreMatcher
{
    private readonly string root;
    private readonly HashSet<string> disabled;
    internal List<GitIgnoreRuleInfo> Entries { get; } = new();
    private readonly Dictionary<string, List<Rule>> rules = new(StringComparer.Ordinal);
    private readonly Dictionary<string, bool> directories = new(StringComparer.Ordinal);
    private sealed record Rule(Regex Pattern, bool DirectoryOnly, bool Negated);
    internal GitIgnoreMatcher(string root, IEnumerable<string> disabled = null)
    {
        this.root = root;
        this.disabled = new HashSet<string>(disabled ?? Array.Empty<string>(), StringComparer.Ordinal);
    }

    internal void ReadDirectory(string relative) => Load(relative.Replace('\\', '/').Trim('/'));

    internal static List<GitIgnoreRuleInfo> Merge(IEnumerable<GitIgnoreRuleInfo> source, IEnumerable<GitIgnoreRuleInfo> target)
    {
        var merged = new Dictionary<string, GitIgnoreRuleInfo>(StringComparer.Ordinal);
        void Add(IEnumerable<GitIgnoreRuleInfo> entries, bool fromSource)
        {
            foreach (var entry in entries ?? Enumerable.Empty<GitIgnoreRuleInfo>())
            {
                if (!merged.TryGetValue(entry.Key, out var row))
                    merged.Add(entry.Key, row = new GitIgnoreRuleInfo { Scope = entry.Scope, Pattern = entry.Pattern });
                if (fromSource) row.Source = true; else row.Target = true;
            }
        }
        Add(source, true); Add(target, false);
        return merged.Values.OrderBy(r => r.Scope, StringComparer.Ordinal).ThenBy(r => r.Pattern, StringComparer.Ordinal).ToList();
    }

    // Also supports histories created before the rule list was persisted.
    internal static List<GitIgnoreRuleInfo> Discover(string root, IEnumerable<string> disabled, CancellationToken token)
    {
        var matcher = new GitIgnoreMatcher(root, disabled);
        if (!Directory.Exists(root)) return matcher.Entries;
        var pending = new Stack<string>(); pending.Push("");
        while (pending.Count > 0)
        {
            token.ThrowIfCancellationRequested();
            string scope = pending.Pop();
            matcher.ReadDirectory(scope);
            foreach (var entry in FastVolumeIndex.VolumePathIndex.EnumerateNativeDirectory(Path.Combine(root, scope), token))
            {
                if ((entry.Attributes & FileAttributes.Directory) == 0 || (entry.Attributes & FileAttributes.ReparsePoint) != 0) continue;
                string relative = Path.Combine(scope, entry.Name);
                if (!DeveloperDifferencerService.Protected(relative) && !matcher.IsIgnored(relative, true)) pending.Push(relative);
            }
        }
        return matcher.Entries;
    }

    internal bool IsIgnored(string relative, bool directory)
    {
        relative = relative.Replace('\\', '/').Trim('/');
        if (relative.Length == 0) return false;
        if (directory && directories.TryGetValue(relative, out bool cached)) return cached;
        int slash = relative.LastIndexOf('/');
        string parent = slash < 0 ? "" : relative.Substring(0, slash);
        // Git cannot re-include a child whose parent remains excluded.
        bool ignored = parent.Length > 0 && IsIgnored(parent, true);
        if (!ignored)
        {
            string scope = "";
            while (true)
            {
                string local = scope.Length == 0 ? relative : relative.Substring(scope.Length + 1);
                foreach (var rule in Load(scope))
                    if ((!rule.DirectoryOnly || directory) && rule.Pattern.IsMatch(local)) ignored = !rule.Negated;
                if (scope == parent) break;
                int next = parent.IndexOf('/', scope.Length == 0 ? 0 : scope.Length + 1);
                scope = next < 0 ? parent : parent.Substring(0, next);
            }
        }
        if (directory) directories[relative] = ignored;
        return ignored;
    }

    private List<Rule> Load(string scope)
    {
        if (rules.TryGetValue(scope, out var cached)) return cached;
        var result = new List<Rule>();
        rules.Add(scope, result);
        string path = Path.Combine(root, scope.Replace('/', Path.DirectorySeparatorChar), ".gitignore");
        if (!File.Exists(path) || (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0) return result;
        foreach (string line in File.ReadLines(path))
        {
            string pattern = line;
            while (pattern.EndsWith(' '))
            {
                int escapes = 0;
                for (int i = pattern.Length - 2; i >= 0 && pattern[i] == '\\'; i--) escapes++;
                if ((escapes & 1) != 0) break;
                pattern = pattern.Substring(0, pattern.Length - 1);
            }
            if (pattern.Length == 0 || pattern[0] == '#') continue;
            string original = pattern;
            bool negate = pattern[0] == '!';
            if (negate) pattern = pattern.Substring(1);
            if (pattern.Length == 0) continue;
            bool directoryOnly = pattern.EndsWith('/');
            if (directoryOnly) pattern = pattern.Substring(0, pattern.Length - 1);
            bool anchored = pattern.Contains('/');
            pattern = pattern.TrimStart('/');
            string expression = Glob(pattern);
            if (expression == null) continue;
            try
            {
                var regex = new Regex((anchored ? "^" : "(?:^|/)") + expression + "$",
                    RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1));
                if (!negate)
                {
                    var entry = new GitIgnoreRuleInfo { Scope = scope, Pattern = original };
                    Entries.Add(entry);
                    if (disabled.Contains(entry.Key)) continue;
                }
                result.Add(new Rule(regex, directoryOnly, negate));
            }
            catch (ArgumentException) { /* Invalid patterns do not match. */ }
        }
        return result;
    }

    private static string Glob(string pattern)
    {
        var result = new StringBuilder();
        for (int i = 0; i < pattern.Length; i++)
        {
            char c = pattern[i];
            if (c == '\\')
            {
                if (++i == pattern.Length) return null;
                result.Append(Regex.Escape(pattern[i].ToString()));
            }
            else if (c == '*')
            {
                int start = i;
                while (i + 1 < pattern.Length && pattern[i + 1] == '*') i++;
                bool globstar = i > start && (start == 0 || pattern[start - 1] == '/') &&
                    (i + 1 == pattern.Length || pattern[i + 1] == '/');
                if (globstar && i + 1 < pattern.Length) { result.Append("(?:[^/]+/)*"); i++; }
                else result.Append(globstar ? ".*" : "[^/]*");
            }
            else if (c == '?') result.Append("[^/]");
            else if (c == '[')
            {
                int end = i + 1;
                if (end < pattern.Length && (pattern[end] == '!' || pattern[end] == '^')) end++;
                if (end < pattern.Length && pattern[end] == ']') end++;
                while (end < pattern.Length && pattern[end] != ']')
                {
                    if (pattern[end] == '\\' && end + 1 < pattern.Length) { end += 2; continue; }
                    if (pattern[end] == '[' && end + 1 < pattern.Length && pattern[end + 1] == ':')
                    {
                        int close = pattern.IndexOf(":]", end + 2, StringComparison.Ordinal);
                        if (close < 0) return null;
                        end = close + 2;
                    }
                    else end++;
                }
                if (end == pattern.Length) return null;
                string chars = pattern.Substring(i + 1, end - i - 1);
                if (chars.StartsWith('!')) chars = "^" + chars.Substring(1);
                chars = chars.Replace("[:alnum:]", "a-zA-Z0-9").Replace("[:alpha:]", "a-zA-Z")
                    .Replace("[:blank:]", " \\t").Replace("[:cntrl:]", "\\x00-\\x1f\\x7f")
                    .Replace("[:digit:]", "0-9").Replace("[:graph:]", "\\x21-\\x7e")
                    .Replace("[:lower:]", "a-z").Replace("[:print:]", "\\x20-\\x7e")
                    .Replace("[:punct:]", "\\x21-\\x2f\\x3a-\\x40\\x5b-\\x60\\x7b-\\x7e")
                    .Replace("[:space:]", " \\t\\r\\n\\v\\f").Replace("[:upper:]", "A-Z").Replace("[:xdigit:]", "0-9a-fA-F");
                result.Append("(?!/)[").Append(chars).Append(']');
                i = end;
            }
            else result.Append(Regex.Escape(c.ToString()));
        }
        return result.ToString();
    }
}
