using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Reflection;
using System.Text;
using System.Threading;
using DesktopIniManager.Services;

namespace DesktopIniManager.Properties;

internal static class StringOverlay
{
    private static readonly Lock Gate = new();
    private static Dictionary<string, string> _map = new(StringComparer.Ordinal);
    private static bool _loaded;

    // Two-letter language of the loaded overlay. Get() is called for every
    // localized string, so it must not probe culture.txt on disk each time.
    private static string _language;

    internal static event EventHandler CultureChanged;

    /// <summary>
    ///     culture.txt の設定から表示カルチャを決定します。未設定・auto・system・不明なカルチャの場合は英語を使います。
    /// </summary>
    /// <returns>使用する表示カルチャ。</returns>
    internal static CultureInfo ResolveCulture()
    {
        var raw = ReadFirstLine(Find("culture.txt"));
        if (string.IsNullOrWhiteSpace(raw) ||
            string.Equals(raw, "auto", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(raw, "system", StringComparison.OrdinalIgnoreCase))
            return CultureInfo.GetCultureInfo("en");
        try
        {
            return CultureInfo.GetCultureInfo(raw.Trim());
        }
        catch (CultureNotFoundException)
        {
            return CultureInfo.GetCultureInfo("en");
        }
    }

    /// <summary>
    ///     指定カルチャの言語ファイルを読み込み、オーバーレイ文字列として保持します。
    /// </summary>
    /// <param name="culture">読み込むカルチャ。null の場合は英語。</param>
    internal static void Load(CultureInfo culture)
    {
        lock (Gate)
        {
            culture ??= CultureInfo.GetCultureInfo("en");
            _map = ReadMap(culture);
            _language = culture.TwoLetterISOLanguageName;
            _loaded = true;
        }
    }

    /// <summary>
    ///     表示カルチャを切り替えて言語ファイルを再読み込みし、設定を保存して CultureChanged を通知します。
    /// </summary>
    /// <param name="cultureName">切り替え先のカルチャ名。</param>
    internal static void SetCulture(string cultureName)
    {
        CultureInfo culture;
        try
        {
            culture = CultureInfo.GetCultureInfo((cultureName ?? "en").Trim());
        }
        catch (CultureNotFoundException)
        {
            culture = CultureInfo.GetCultureInfo("en");
        }

        Load(culture);
        Strings.Culture = culture;
        Thread.CurrentThread.CurrentUICulture = culture;
        Thread.CurrentThread.CurrentCulture = culture;
        WriteCulture(culture.Name);
        var handler = CultureChanged;
        if (handler != null) handler(null, EventArgs.Empty);
    }

    /// <summary>
    ///     選択したカルチャ名を culture.txt に保存します。
    /// </summary>
    /// <param name="name">保存するカルチャ名。</param>
    private static void WriteCulture(string name)
    {
        foreach (var dir in PersistDirs())
            try
            {
                Directory.CreateDirectory(dir);
                File.WriteAllText(Path.Combine(dir, "culture.txt"), name ?? "en", new UTF8Encoding(false));
                return;
            }
            catch (UnauthorizedAccessException)
            {
            }
    }

    /// <summary>
    ///     culture.txt の保存先候補となるフォルダーを列挙します。
    /// </summary>
    /// <returns>保存先フォルダーの列挙。</returns>
    private static IEnumerable<string> PersistDirs()
    {
        yield return AppSlot.Directory;
    }

    /// <summary>
    ///     キーに対応するローカライズ文字列を、組み込みの上書き・言語ファイル・リソースの順に検索して取得します。
    /// </summary>
    /// <param name="key">リソース キー。</param>
    /// <returns>ローカライズ文字列。見つからない場合はキー自体。</returns>
    internal static string Get(string key)
    {
        Dictionary<string, string> current;
        string language;
        lock (Gate)
        {
            if (!_loaded)
            {
                var culture = ResolveCulture();
                _map = ReadMap(culture);
                _language = culture.TwoLetterISOLanguageName;
                _loaded = true;
            }

            current = _map;
            language = _language;
        }

        var builtin = Builtin(key, language);
        if (!string.IsNullOrEmpty(builtin)) return builtin;
        if (current.TryGetValue(key, out string value) && !string.IsNullOrEmpty(value))
            return Unescape(value);
        value = Strings.ResourceManager.GetString(key, Strings.Culture);
        if (!string.IsNullOrEmpty(value)) return value;
        return Strings.ResourceManager.GetString(key, CultureInfo.InvariantCulture) ?? key;
    }

    /// <summary>
    ///     言語ファイルより優先するコード内蔵の翻訳を取得します。
    /// </summary>
    /// <param name="key">リソース キー。</param>
    /// <param name="language">2 文字の言語コード。</param>
    /// <returns>内蔵の翻訳。対象外のキーの場合は null。</returns>
    private static string Builtin(string key, string language)
    {
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

    /// <summary>
    ///     カルチャ名と言語名の順に言語ファイルを探し、キーと文字列の対応表を作成します。
    /// </summary>
    /// <param name="culture">読み込むカルチャ。</param>
    /// <returns>キーと翻訳文字列の辞書。</returns>
    private static Dictionary<string, string> ReadMap(CultureInfo culture)
    {
        var dest = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var name in Names(culture))
        {
            var path = Find(name);
            if (path != null) Merge(dest, path);
        }

        return dest;
    }

    /// <summary>
    ///     カルチャに対応する言語ファイル名の候補を列挙します。
    /// </summary>
    /// <param name="culture">対象のカルチャ。</param>
    /// <returns>言語ファイル名の候補。</returns>
    private static IEnumerable<string> Names(CultureInfo culture)
    {
        if (!string.IsNullOrEmpty(culture.Name))
            yield return culture.Name + ".txt";
        if (!string.IsNullOrEmpty(culture.TwoLetterISOLanguageName))
            yield return culture.TwoLetterISOLanguageName + ".txt";
    }

    /// <summary>
    ///     検索フォルダーとその Languages サブフォルダーから指定ファイルを探します。
    /// </summary>
    /// <param name="fileName">探すファイル名。</param>
    /// <returns>見つかったファイルのパス。見つからない場合は null。</returns>
    private static string Find(string fileName)
    {
        foreach (var dir in SearchDirs())
            try
            {
                var path = Path.Combine(dir, fileName);
                if (File.Exists(path)) return path;
                path = Path.Combine(dir, "Languages", fileName);
                if (File.Exists(path)) return path;
            }
            catch (UnauthorizedAccessException)
            {
            }

        return null;
    }

    /// <summary>
    ///     言語ファイルや culture.txt を探すフォルダーの候補を優先順に列挙します。
    /// </summary>
    /// <returns>検索フォルダーの列挙。</returns>
    private static IEnumerable<string> SearchDirs()
    {
        yield return AppSlot.Directory;
        yield return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "DesktopIniManager");
        if (!string.IsNullOrEmpty(AppDomain.CurrentDomain.BaseDirectory))
            yield return AppDomain.CurrentDomain.BaseDirectory;
        string asm = null;
        try
        {
            asm = Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location);
        }
        catch (UnauthorizedAccessException)
        {
        }

        if (!string.IsNullOrEmpty(asm))
            yield return asm;
        var baseDir = AppDomain.CurrentDomain.BaseDirectory;
        if (!string.IsNullOrEmpty(baseDir))
        {
            yield return Path.GetFullPath(Path.Combine(baseDir, "..", ".."));
            yield return Path.GetFullPath(Path.Combine(baseDir, "..", "..", "Languages"));
        }
    }

    /// <summary>
    ///     ファイルから空行とコメント行を除いた最初の行を読み取ります。
    /// </summary>
    /// <param name="path">読み取るファイルのパス。</param>
    /// <returns>最初の有効な行。ファイルがない場合や有効な行がない場合は null。</returns>
    private static string ReadFirstLine(string path)
    {
        if (path == null) return null;
        try
        {
            foreach (var raw in File.ReadAllLines(path, Encoding.UTF8))
            {
                var line = raw.Trim().Trim('\uFEFF');
                if (line.Length == 0 || line[0] == '#') continue;
                return line;
            }
        }
        catch (UnauthorizedAccessException)
        {
        }

        return null;
    }

    /// <summary>
    ///     key=value 形式の言語ファイルを読み込み、辞書に追加または上書きします。
    /// </summary>
    /// <param name="dest">追加先の辞書。</param>
    /// <param name="path">言語ファイルのパス。</param>
    private static void Merge(Dictionary<string, string> dest, string path)
    {
        string[] lines;
        try
        {
            lines = File.ReadAllLines(path, Encoding.UTF8);
        }
        catch
        {
            return;
        }

        foreach (var raw in lines)
        {
            var line = raw.Trim().Trim('\uFEFF');
            if (line.Length == 0 || line[0] == '#') continue;
            var eq = line.IndexOf('=');
            if (eq <= 0) continue;
            dest[line.Substring(0, eq).Trim()] = line.Substring(eq + 1);
        }
    }

    /// <summary>
    ///     言語ファイル内の \n、\r、\t エスケープを実際の制御文字に戻します。
    /// </summary>
    /// <param name="value">変換する文字列。</param>
    /// <returns>変換後の文字列。</returns>
    private static string Unescape(string value)
    {
        return value.Replace("\\n", "\n").Replace("\\r", "\r").Replace("\\t", "\t");
    }
}