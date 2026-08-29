using System;
using System.IO;
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
    }
}
