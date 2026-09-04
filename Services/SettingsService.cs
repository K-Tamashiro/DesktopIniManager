using System;
using System.IO;
using System.Globalization;
using System.Text;

namespace DesktopIniManager.Services
{
    internal static class SettingsService
    {
        private static readonly string SettingsDirectory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "DesktopIniManager");
        private static readonly string SettingsPath = Path.Combine(SettingsDirectory, "settings.txt");
        private static readonly string SearchQueryPath = Path.Combine(SettingsDirectory, "search-query.txt");
        private static readonly string SearchRootPath = Path.Combine(SettingsDirectory, "search-root.txt");
        private static readonly string ThemePath = Path.Combine(SettingsDirectory, "theme.txt");
        private static readonly string EditorPath = Path.Combine(SettingsDirectory, "editor.txt");
        private static readonly string EditorArgumentsPath = Path.Combine(SettingsDirectory, "editor-arguments.txt");
        private static readonly string GrepProfilePath = Path.Combine(SettingsDirectory, "grep-profile.txt");
        private static readonly string GrepColumnWidthsPath = Path.Combine(SettingsDirectory, "grep-column-widths.txt");
        private static readonly string GrepFreeExtensionsPath = Path.Combine(SettingsDirectory, "grep-free-extensions.txt");
        private static readonly string TreeDensityPath = Path.Combine(SettingsDirectory, "tree-density.txt");

        public static string LoadIconLibraryPath()
        {
            try { return File.Exists(SettingsPath) ? File.ReadAllText(SettingsPath, Encoding.UTF8).Trim() : null; }
            catch { return null; }
        }

        public static void SaveIconLibraryPath(string path)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(path)) return;
                Directory.CreateDirectory(SettingsDirectory);
                File.WriteAllText(SettingsPath, path.Trim(), new UTF8Encoding(false));
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

        public static bool LoadDarkMode()
        {
            try { return File.Exists(ThemePath) && string.Equals(File.ReadAllText(ThemePath).Trim(), "dark", StringComparison.OrdinalIgnoreCase); }
            catch { return false; }
        }

        public static void SaveDarkMode(bool dark)
        {
            try { Directory.CreateDirectory(SettingsDirectory); File.WriteAllText(ThemePath, dark ? "dark" : "light", new UTF8Encoding(false)); }
            catch { }
        }

        public static string LoadEditorPath() => ReadSetting(EditorPath, "code");
        public static string LoadEditorArguments() => ReadSetting(EditorArgumentsPath, "--goto \"{file}:{line}:{column}\"");
        public static void SaveEditor(string executable, string arguments)
        {
            WriteSetting(EditorPath, executable);
            WriteSetting(EditorArgumentsPath, arguments);
        }

        public static string LoadGrepProfile() => ReadSetting(GrepProfilePath, null);
        public static void SaveGrepProfile(string profile) => WriteSetting(GrepProfilePath, profile);
        internal const string DefaultGrepFreeExtensions = ".txt .log .ini .json .xml .html .htm .eml";
        public static string LoadGrepFreeExtensions()
        {
            string value = ReadSetting(GrepFreeExtensionsPath, DefaultGrepFreeExtensions);
            return string.IsNullOrWhiteSpace(value) || string.Equals(value.Trim(), ".", StringComparison.Ordinal)
                ? DefaultGrepFreeExtensions : value;
        }
        public static void SaveGrepFreeExtensions(string extensions)
        {
            if (string.IsNullOrWhiteSpace(extensions) || string.Equals(extensions.Trim(), ".", StringComparison.Ordinal)) return;
            WriteSetting(GrepFreeExtensionsPath, extensions);
        }

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
    }
}
