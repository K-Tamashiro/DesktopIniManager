using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Reflection;
using System.Text;

namespace DesktopIniManager.Properties
{
    internal static class StringOverlay
    {
        private static readonly object gate = new object();
        private static Dictionary<string, string> map = new Dictionary<string, string>(StringComparer.Ordinal);
        private static bool loaded;
        internal static event EventHandler CultureChanged;

        internal static CultureInfo ResolveCulture()
        {
            string raw = ReadFirstLine(Find("culture.txt"));
            if (string.IsNullOrWhiteSpace(raw) ||
                string.Equals(raw, "auto", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(raw, "system", StringComparison.OrdinalIgnoreCase))
                return CultureInfo.GetCultureInfo("en");
            try { return CultureInfo.GetCultureInfo(raw.Trim()); }
            catch (CultureNotFoundException) { return CultureInfo.GetCultureInfo("en"); }
        }

        internal static void Load(CultureInfo culture)
        {
            lock (gate)
            {
                map = ReadMap(culture ?? CultureInfo.GetCultureInfo("en"));
                loaded = true;
            }
        }

        internal static void SetCulture(string cultureName)
        {
            CultureInfo culture;
            try { culture = CultureInfo.GetCultureInfo((cultureName ?? "en").Trim()); }
            catch (CultureNotFoundException) { culture = CultureInfo.GetCultureInfo("en"); }
            Load(culture);
            Strings.Culture = culture;
            System.Threading.Thread.CurrentThread.CurrentUICulture = culture;
            System.Threading.Thread.CurrentThread.CurrentCulture = culture;
            WriteCulture(culture.Name);
            EventHandler handler = CultureChanged;
            if (handler != null) handler(null, EventArgs.Empty);
        }

        private static void WriteCulture(string name)
        {
            foreach (string dir in PersistDirs())
            {
                try
                {
                    Directory.CreateDirectory(dir);
                    File.WriteAllText(Path.Combine(dir, "culture.txt"), name ?? "en", new UTF8Encoding(false));
                    return;
                }
                catch { }
            }
        }

        private static IEnumerable<string> PersistDirs()
        {
            yield return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "DesktopIniManager");
            if (!string.IsNullOrEmpty(AppDomain.CurrentDomain.BaseDirectory))
                yield return AppDomain.CurrentDomain.BaseDirectory;
        }

        internal static string Get(string key)
        {
            Dictionary<string, string> current;
            lock (gate)
            {
                if (!loaded)
                {
                    map = ReadMap(ResolveCulture());
                    loaded = true;
                }
                current = map;
            }

            string value;
            string builtin = Builtin(key);
            if (!string.IsNullOrEmpty(builtin)) return builtin;
            if (current.TryGetValue(key, out value) && !string.IsNullOrEmpty(value))
                return Unescape(value);
            value = Strings.ResourceManager.GetString(key, Strings.Culture);
            if (!string.IsNullOrEmpty(value)) return value;
            return Strings.ResourceManager.GetString(key, CultureInfo.InvariantCulture) ?? key;
        }

        private static string Builtin(string key)
        {
            string language = ResolveCulture().TwoLetterISOLanguageName;
            if (key == "Main_IconRemove" || key == "Main_IconReset")
            {
                if (language == "ja") return "アイコンリセット";
                if (language == "zh") return "重置图标";
                if (language == "ko") return "아이콘 재설정";
                return "Icon Reset";
            }
            if (key == "Main_RemoveSettings")
            {
                if (language == "ja") return "フォルダーのカスタムアイコンをリセットします";
                if (language == "zh") return "重置文件夹自定义图标";
                if (language == "ko") return "폴더 사용자 지정 아이콘을 재설정합니다";
                return "Reset custom folder icons";
            }
            return null;
        }

        private static Dictionary<string, string> ReadMap(CultureInfo culture)
        {
            var dest = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (string name in Names(culture))
            {
                string path = Find(name);
                if (path != null) Merge(dest, path);
            }
            return dest;
        }

        private static IEnumerable<string> Names(CultureInfo culture)
        {
            if (!string.IsNullOrEmpty(culture.Name))
                yield return culture.Name + ".txt";
            if (!string.IsNullOrEmpty(culture.TwoLetterISOLanguageName))
                yield return culture.TwoLetterISOLanguageName + ".txt";
        }

        private static string Find(string fileName)
        {
            foreach (string dir in SearchDirs())
            {
                try
                {
                    string path = Path.Combine(dir, fileName);
                    if (File.Exists(path)) return path;
                    path = Path.Combine(dir, "Languages", fileName);
                    if (File.Exists(path)) return path;
                }
                catch { }
            }
            return null;
        }

        private static IEnumerable<string> SearchDirs()
        {
            yield return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "DesktopIniManager");
            if (!string.IsNullOrEmpty(AppDomain.CurrentDomain.BaseDirectory))
                yield return AppDomain.CurrentDomain.BaseDirectory;
            string asm = null;
            try { asm = Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location); }
            catch { }
            if (!string.IsNullOrEmpty(asm))
                yield return asm;
            string baseDir = AppDomain.CurrentDomain.BaseDirectory;
            if (!string.IsNullOrEmpty(baseDir))
            {
                yield return Path.GetFullPath(Path.Combine(baseDir, "..", ".."));
                yield return Path.GetFullPath(Path.Combine(baseDir, "..", "..", "Languages"));
            }
        }

        private static string ReadFirstLine(string path)
        {
            if (path == null) return null;
            try
            {
                foreach (string raw in File.ReadAllLines(path, Encoding.UTF8))
                {
                    string line = raw.Trim().Trim('\uFEFF');
                    if (line.Length == 0 || line[0] == '#') continue;
                    return line;
                }
            }
            catch { }
            return null;
        }

        private static void Merge(Dictionary<string, string> dest, string path)
        {
            string[] lines;
            try { lines = File.ReadAllLines(path, Encoding.UTF8); }
            catch { return; }
            foreach (string raw in lines)
            {
                string line = raw.Trim().Trim('\uFEFF');
                if (line.Length == 0 || line[0] == '#') continue;
                int eq = line.IndexOf('=');
                if (eq <= 0) continue;
                dest[line.Substring(0, eq).Trim()] = line.Substring(eq + 1);
            }
        }

        private static string Unescape(string value)
        {
            return value.Replace("\\n", "\n").Replace("\\r", "\r").Replace("\\t", "\t");
        }
    }
}
