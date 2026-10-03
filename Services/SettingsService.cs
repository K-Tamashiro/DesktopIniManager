using System;
using System.IO;
using System.Globalization;
using System.Text;

namespace DesktopIniManager.Services
{
    internal static class SettingsService
    {
        private static string SettingsDirectory => AppSlot.Directory;
        private static string SettingsPath => Path.Combine(SettingsDirectory, "settings.txt");
        private static string SearchQueryPath => Path.Combine(SettingsDirectory, "search-query.txt");
        private static string SearchRootPath => Path.Combine(SettingsDirectory, "search-root.txt");
        private static string ThemePath => Path.Combine(SettingsDirectory, "theme.txt");
        private static string EditorPath => Path.Combine(SettingsDirectory, "editor.txt");
        private static string EditorArgumentsPath => Path.Combine(SettingsDirectory, "editor-arguments.txt");
        private static string ExternalDiffPath => Path.Combine(SettingsDirectory, "external-diff.txt");
        private static string DiffDisplayModePath => Path.Combine(SettingsDirectory, "diff-display-mode.txt");
        private static string GrepProfilePath => Path.Combine(SettingsDirectory, "grep-profile.txt");
        private static string GrepColumnWidthsPath => Path.Combine(SettingsDirectory, "grep-column-widths.txt");
        private static string GrepFreeExtensionsPath => Path.Combine(SettingsDirectory, "grep-free-extensions.txt");
        private static string TreeDensityPath => Path.Combine(SettingsDirectory, "tree-density.txt");
        private static string MainWindowPlacementPath => Path.Combine(SettingsDirectory, "main-window.txt");
        private static string MainListLayoutPath => Path.Combine(SettingsDirectory, "main-list-layout.txt");

        public static double[] LoadMainListLayout()
        {
            string text = ReadSetting(MainListLayoutPath, null);
            if (string.IsNullOrWhiteSpace(text)) return null;
            string[] parts = text.Split(',');
            var values = new double[parts.Length];
            for (int i = 0; i < parts.Length; i++)
                if (!double.TryParse(parts[i], NumberStyles.Float, CultureInfo.InvariantCulture, out values[i]) ||
                    double.IsNaN(values[i]) || double.IsInfinity(values[i]) || values[i] < 0)
                    return null;
            return values;
        }

        public static void SaveMainListLayout(double[] values) => WriteSetting(MainListLayoutPath,
            string.Join(",", Array.ConvertAll(values, value => value.ToString("R", CultureInfo.InvariantCulture))));

        public static string LoadIconLibraryPath()
        {
            try
            {
                string path = File.Exists(SettingsPath) ? File.ReadAllText(SettingsPath, Encoding.UTF8).Trim() : null;
                if (string.IsNullOrWhiteSpace(path)) return null;
                // Older settings stored the bundled library's absolute installation path.
                string normalized = path.Replace('/', '\\');
                if (normalized.EndsWith("\\Assets\\folder_set.icl", StringComparison.OrdinalIgnoreCase))
                    return Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Assets", "folder_set.icl");
                return Path.IsPathRooted(path) ? path : Path.GetFullPath(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, path));
            }
            catch { return null; }
        }

        public static void SaveIconLibraryPath(string path)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(path)) return;
                string baseDirectory = Path.GetFullPath(AppDomain.CurrentDomain.BaseDirectory)
                    .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
                string fullPath = Path.GetFullPath(Path.Combine(baseDirectory, path.Trim()));
                string savedPath = fullPath.StartsWith(baseDirectory, StringComparison.OrdinalIgnoreCase)
                    ? fullPath.Substring(baseDirectory.Length) : fullPath;
                Directory.CreateDirectory(SettingsDirectory);
                File.WriteAllText(SettingsPath, savedPath, new UTF8Encoding(false));
            }
            catch { /* Settings persistence must never prevent the app from closing. */ }
        }

        public static string LoadSearchQuery()
        {
            try { return File.Exists(SearchQueryPath) ? File.ReadAllText(SearchQueryPath, Encoding.UTF8).Trim() : null; }
            catch { return null; }
        }

        public static void SaveSearchQuery(string query)
        {
            try
            {
                Directory.CreateDirectory(SettingsDirectory);
                File.WriteAllText(SearchQueryPath, (query ?? string.Empty).Trim(), new UTF8Encoding(false));
            }
            catch { }
        }

        public static string LoadSearchRoot()
        {
            try { return File.Exists(SearchRootPath) ? File.ReadAllText(SearchRootPath, Encoding.UTF8).Trim() : null; }
            catch { return null; }
        }

        public static void SaveSearchRoot(string path)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(path)) return;
                Directory.CreateDirectory(SettingsDirectory);
                File.WriteAllText(SearchRootPath, path.Trim(), new UTF8Encoding(false));
            }
            catch { }
        }

        public static bool LoadTreeCompact()
        {
            try { return File.Exists(TreeDensityPath) && string.Equals(File.ReadAllText(TreeDensityPath).Trim(), "compact", StringComparison.OrdinalIgnoreCase); }
            catch { return false; }
        }

        public static void SaveTreeCompact(bool compact)
        {
            try { Directory.CreateDirectory(SettingsDirectory); File.WriteAllText(TreeDensityPath, compact ? "compact" : "comfortable", new UTF8Encoding(false)); }
            catch { }
        }

        public static string LoadTheme()
        {
            string saved = ReadSetting(ThemePath, "Dark");
            return ThemeService.Normalize(saved);
        }

        public static void SaveTheme(string theme) => WriteSetting(ThemePath, ThemeService.Normalize(theme));

        // Kept for existing callers/startup state compatibility.
        public static bool LoadDarkMode() => !string.Equals(LoadTheme(), "Light", StringComparison.OrdinalIgnoreCase);
        public static void SaveDarkMode(bool dark) => SaveTheme(dark ? "Dark" : "Light");

        public static string LoadEditorPath() => ReadSetting(EditorPath, "code");
        public static string LoadExternalDiff() => ReadSetting(ExternalDiffPath, null);
        public static bool LoadDiffDifferencesOnly() => string.Equals(
            ReadSetting(DiffDisplayModePath, "all"), "differences", StringComparison.OrdinalIgnoreCase);
        public static void SaveDiffDifferencesOnly(bool differencesOnly) =>
            WriteSetting(DiffDisplayModePath, differencesOnly ? "differences" : "all");

        public static void SaveExternalDiff(string command) =>
            WriteSetting(ExternalDiffPath, command);
        public static string LoadEditorArguments()
        {
            string saved = ReadSetting(EditorArgumentsPath, null);
            if (!string.IsNullOrWhiteSpace(saved)) return saved;
            return DefaultEditorArguments(LoadEditorPath());
        }
        public static void SaveEditor(string executable, string arguments)
        {
            string command = (executable ?? string.Empty).Trim();
            if (command.IndexOf("sakura", StringComparison.OrdinalIgnoreCase) >= 0)
                command = "code";
            WriteSetting(EditorPath, command);
            string args = (arguments ?? string.Empty).Trim();
            if (string.IsNullOrEmpty(args))
                args = DefaultEditorArguments(command);
            WriteSetting(EditorArgumentsPath, args);
        }

        public static string LoadGrepProfile() => ReadSetting(GrepProfilePath, null);
        public static void SaveGrepProfile(string profile) => WriteSetting(GrepProfilePath, profile);
        public static string LoadGrepFreeExtensions() => ReadSetting(GrepFreeExtensionsPath, ".txt .log .ini .json .xml .html .htm .eml");
        public static void SaveGrepFreeExtensions(string extensions) => WriteSetting(GrepFreeExtensionsPath, extensions);

        public static double[] LoadGrepColumnWidths()
        {
            string value = ReadSetting(GrepColumnWidthsPath, null);
            if (string.IsNullOrWhiteSpace(value)) return null;
            string[] parts = value.Split(',');
            var result = new double[parts.Length];
            for (int index = 0; index < parts.Length; index++)
                if (!double.TryParse(parts[index], NumberStyles.Float, CultureInfo.InvariantCulture, out result[index])) return null;
            return result;
        }

        public static void SaveGrepColumnWidths(double[] widths)
        {
            if (widths == null) return;
            WriteSetting(GrepColumnWidthsPath, string.Join(",", Array.ConvertAll(widths,
                width => width.ToString("R", CultureInfo.InvariantCulture))));
        }

        private static string DefaultEditorArguments(string command)
        {
            if (!string.IsNullOrWhiteSpace(command) &&
                command.IndexOf("MIW", StringComparison.OrdinalIgnoreCase) >= 0)
                return "/+{line}@{column} \"{file}\"";
            if (!string.IsNullOrWhiteSpace(command) &&
                command.IndexOf("Hidemaru", StringComparison.OrdinalIgnoreCase) >= 0)
                return "/j{line},{column} \"{file}\"";
            if (!string.IsNullOrWhiteSpace(command) &&
                command.IndexOf("Mery", StringComparison.OrdinalIgnoreCase) >= 0)
                return "/l {line} /c {column} \"{file}\"";
            return "--goto \"{file}:{line}:{column}\"";
        }

        public static bool TryLoadMainWindowPlacement(out double left, out double top, out double width, out double height, out int state)
        {
            left = top = width = height = 0;
            state = 0;
            string text = ReadSetting(MainWindowPlacementPath, null);
            if (string.IsNullOrWhiteSpace(text)) return false;
            string[] parts = text.Split(',');
            if (parts.Length < 4) return false;
            if (!double.TryParse(parts[0], NumberStyles.Float, CultureInfo.InvariantCulture, out left)) return false;
            if (!double.TryParse(parts[1], NumberStyles.Float, CultureInfo.InvariantCulture, out top)) return false;
            if (!double.TryParse(parts[2], NumberStyles.Float, CultureInfo.InvariantCulture, out width)) return false;
            if (!double.TryParse(parts[3], NumberStyles.Float, CultureInfo.InvariantCulture, out height)) return false;
            if (parts.Length >= 5)
                int.TryParse(parts[4], NumberStyles.Integer, CultureInfo.InvariantCulture, out state);
            return width > 0 && height > 0;
        }

        public static void SaveMainWindowPlacement(double left, double top, double width, double height, int state)
        {
            WriteSetting(MainWindowPlacementPath, string.Join(",",
                left.ToString("R", CultureInfo.InvariantCulture),
                top.ToString("R", CultureInfo.InvariantCulture),
                width.ToString("R", CultureInfo.InvariantCulture),
                height.ToString("R", CultureInfo.InvariantCulture),
                state.ToString(CultureInfo.InvariantCulture)));
        }

        private static string ReadSetting(string path, string fallback)
        {
            try { return File.Exists(path) ? File.ReadAllText(path, Encoding.UTF8).Trim() : fallback; }
            catch { return fallback; }
        }

        private static void WriteSetting(string path, string value)
        {
            try { Directory.CreateDirectory(SettingsDirectory); File.WriteAllText(path, value ?? string.Empty, new UTF8Encoding(false)); }
            catch { }
        }

        public static void ClearAll()
        {
            try
            {
                if (!Directory.Exists(SettingsDirectory)) return;
                foreach (string file in Directory.GetFiles(SettingsDirectory, "*", SearchOption.AllDirectories))
                {
                    try { File.Delete(file); } catch { }
                }
                foreach (string dir in Directory.GetDirectories(SettingsDirectory))
                {
                    try { Directory.Delete(dir, true); } catch { }
                }
            }
            catch { }
        }
    }
}
