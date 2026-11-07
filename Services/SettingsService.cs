using System;
using System.Globalization;
using System.IO;
using System.Text;

namespace DesktopIniManager.Services;

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

    /// <summary>
    ///     DIFF ビューのフォント名とサイズを読み込みます。未設定や範囲外の場合は Consolas、13 を使います。
    /// </summary>
    /// <returns>フォント名とサイズ。</returns>
    internal static (string Family, double Size) LoadDiffFont()
    {
        var parts = ReadSetting(Path.Combine(SettingsDirectory, "diff-font.txt"), string.Empty).Split('\n');
        var family = parts[0].Trim();
        var size = parts.Length > 1 &&
                   double.TryParse(parts[1], NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed) &&
                   double.IsFinite(parsed) && parsed is >= 8 and <= 72 ? parsed : 13;
        return (string.IsNullOrWhiteSpace(family) ? "Consolas" : family, size);
    }

    /// <summary>
    ///     DIFF ビューのフォント名とサイズを保存します。
    /// </summary>
    /// <param name="family">フォント名。</param>
    /// <param name="size">フォント サイズ。</param>
    internal static void SaveDiffFont(string family, double size)
    {
        WriteSetting(Path.Combine(SettingsDirectory, "diff-font.txt"),
            family + "\n" + size.ToString("R", CultureInfo.InvariantCulture));
    }

    /// <summary>
    ///     メイン画面のペイン幅と列幅を読み込みます。
    /// </summary>
    /// <returns>保存された幅の配列。未保存または不正な値を含む場合は null。</returns>
    public static double[] LoadMainListLayout()
    {
        var text = ReadSetting(MainListLayoutPath, null);
        if (string.IsNullOrWhiteSpace(text)) return null;
        var parts = text.Split(',');
        var values = new double[parts.Length];
        for (var i = 0; i < parts.Length; i++)
            if (!double.TryParse(parts[i], NumberStyles.Float, CultureInfo.InvariantCulture, out values[i]) ||
                double.IsNaN(values[i]) || double.IsInfinity(values[i]) || values[i] < 0)
                return null;
        return values;
    }

    /// <summary>
    ///     メイン画面のペイン幅と列幅を保存します。
    /// </summary>
    /// <param name="values">保存する幅の配列。</param>
    public static void SaveMainListLayout(double[] values)
    {
        WriteSetting(MainListLayoutPath,
            string.Join(",", Array.ConvertAll(values, value => value.ToString("R", CultureInfo.InvariantCulture))));
    }

    /// <summary>
    ///     Differencer 画面のレイアウト値を読み込みます。
    /// </summary>
    /// <returns>保存されたレイアウト値。未保存または不正な値を含む場合は null。</returns>
    internal static double[] LoadDifferencerLayout()
    {
        var text = ReadSetting(Path.Combine(SettingsDirectory, "differencer-layout.txt"), null);
        if (string.IsNullOrWhiteSpace(text)) return null;
        var parts = text.Split(',');
        var values = new double[parts.Length];
        for (var i = 0; i < parts.Length; i++)
            if (!double.TryParse(parts[i], NumberStyles.Float, CultureInfo.InvariantCulture, out values[i]) ||
                !double.IsFinite(values[i]) || values[i] < 0)
                return null;
        return values;
    }

    /// <summary>
    ///     Differencer 画面のレイアウト値を保存します。
    /// </summary>
    /// <param name="values">保存するレイアウト値。</param>
    internal static void SaveDifferencerLayout(double[] values)
    {
        WriteSetting(
            Path.Combine(SettingsDirectory, "differencer-layout.txt"),
            string.Join(",", Array.ConvertAll(values, value => value.ToString("R", CultureInfo.InvariantCulture))));
    }

    /// <summary>
    ///     アイコン ライブラリのパスを読み込みます。相対パスはアプリケーション フォルダー基準で解決し、旧形式の同梱ライブラリの絶対パスは現在の場所に置き換えます。
    /// </summary>
    /// <returns>アイコン ライブラリの絶対パス。未設定の場合は null。</returns>
    public static string LoadIconLibraryPath()
    {
        try
        {
            var path = File.Exists(SettingsPath) ? File.ReadAllText(SettingsPath, Encoding.UTF8).Trim() : null;
            if (string.IsNullOrWhiteSpace(path)) return null;
            // Older settings stored the bundled library's absolute installation path.
            var normalized = path.Replace('/', '\\');
            if (normalized.EndsWith("\\Assets\\folder_set.icl", StringComparison.OrdinalIgnoreCase))
                return Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Assets", "folder_set.icl");
            return Path.IsPathRooted(path)
                ? path
                : Path.GetFullPath(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, path));
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    ///     アイコン ライブラリのパスを保存します。アプリケーション フォルダー配下の場合は相対パスで保存します。
    /// </summary>
    /// <param name="path">保存するパス。</param>
    public static void SaveIconLibraryPath(string path)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(path)) return;
            var baseDirectory = Path.GetFullPath(AppDomain.CurrentDomain.BaseDirectory)
                .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
            var fullPath = Path.GetFullPath(Path.Combine(baseDirectory, path.Trim()));
            var savedPath = fullPath.StartsWith(baseDirectory, StringComparison.OrdinalIgnoreCase)
                ? fullPath.Substring(baseDirectory.Length)
                : fullPath;
            Directory.CreateDirectory(SettingsDirectory);
            File.WriteAllText(SettingsPath, savedPath, new UTF8Encoding(false));
        }
        catch
        {
            /* Settings persistence must never prevent the app from closing. */
        }
    }

    /// <summary>
    ///     前回の検索語を読み込みます。
    /// </summary>
    /// <returns>検索語。未保存の場合は null。</returns>
    public static string LoadSearchQuery()
    {
        try
        {
            return File.Exists(SearchQueryPath) ? File.ReadAllText(SearchQueryPath, Encoding.UTF8).Trim() : null;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    ///     検索語を保存します。
    /// </summary>
    /// <param name="query">保存する検索語。</param>
    public static void SaveSearchQuery(string query)
    {
        try
        {
            Directory.CreateDirectory(SettingsDirectory);
            File.WriteAllText(SearchQueryPath, (query ?? string.Empty).Trim(), new UTF8Encoding(false));
        }
        catch
        {
            // Settings persistence must never prevent the app from closing.
        }
    }

    /// <summary>
    ///     前回の検索ルート フォルダーを読み込みます。
    /// </summary>
    /// <returns>検索ルートのパス。未保存の場合は null。</returns>
    public static string LoadSearchRoot()
    {
        try
        {
            return File.Exists(SearchRootPath) ? File.ReadAllText(SearchRootPath, Encoding.UTF8).Trim() : null;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    ///     検索ルート フォルダーを保存します。
    /// </summary>
    /// <param name="path">保存するパス。</param>
    public static void SaveSearchRoot(string path)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(path)) return;
            Directory.CreateDirectory(SettingsDirectory);
            File.WriteAllText(SearchRootPath, path.Trim(), new UTF8Encoding(false));
        }
        catch
        {
            // Settings persistence must never prevent the app from closing.
        }
    }

    /// <summary>
    ///     フォルダー ツリーをコンパクト表示にするかどうかを読み込みます。
    /// </summary>
    /// <returns>コンパクト表示の場合は true。</returns>
    public static bool LoadTreeCompact()
    {
        try
        {
            return File.Exists(TreeDensityPath) && string.Equals(File.ReadAllText(TreeDensityPath).Trim(), "compact",
                StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    ///     フォルダー ツリーの表示密度を保存します。
    /// </summary>
    /// <param name="compact">コンパクト表示の場合は true。</param>
    public static void SaveTreeCompact(bool compact)
    {
        try
        {
            Directory.CreateDirectory(SettingsDirectory);
            File.WriteAllText(TreeDensityPath, compact ? "compact" : "comfortable", new UTF8Encoding(false));
        }
        catch
        {
            // Settings persistence must never prevent the app from closing.
        }
    }

    /// <summary>
    ///     保存されたテーマ名を読み込みます。
    /// </summary>
    /// <returns>正規化したテーマ名。未保存の場合は Dark。</returns>
    public static string LoadTheme()
    {
        var saved = ReadSetting(ThemePath, "Dark");
        return ThemeService.Normalize(saved);
    }

    /// <summary>
    ///     テーマ名を保存します。
    /// </summary>
    /// <param name="theme">保存するテーマ名。</param>
    public static void SaveTheme(string theme)
    {
        WriteSetting(ThemePath, ThemeService.Normalize(theme));
    }

    // Kept for existing callers/startup state compatibility.
    /// <summary>
    ///     ライト テーマ以外が選択されているかどうかを取得します。既存の呼び出し元との互換性のために残しています。
    /// </summary>
    /// <returns>ライト テーマ以外の場合は true。</returns>
    public static bool LoadDarkMode()
    {
        return !string.Equals(LoadTheme(), "Light", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    ///     ダークまたはライトのテーマを保存します。
    /// </summary>
    /// <param name="dark">ダーク テーマの場合は true。</param>
    public static void SaveDarkMode(bool dark)
    {
        SaveTheme(dark ? "Dark" : "Light");
    }

    /// <summary>
    ///     外部エディターの実行ファイルを読み込みます。
    /// </summary>
    /// <returns>エディターの実行ファイル。未保存の場合は "code"。</returns>
    public static string LoadEditorPath()
    {
        return ReadSetting(EditorPath, "code");
    }

    /// <summary>
    ///     外部 DIFF ツールのコマンドを読み込みます。
    /// </summary>
    /// <returns>外部 DIFF ツールのコマンド。未保存の場合は null。</returns>
    public static string LoadExternalDiff()
    {
        return ReadSetting(ExternalDiffPath, null);
    }

    /// <summary>
    ///     音声比較の音量を読み込みます。
    /// </summary>
    /// <param name="source">比較元の音量の場合は true、比較先の場合は false。</param>
    /// <returns>0 から 1 の音量。未保存の場合は 1。</returns>
    internal static double LoadDiffAudioVolume(bool source)
    {
        var text = ReadSetting(DiffAudioVolumePath(source), null);
        return double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var value) &&
               double.IsFinite(value)
            ? Math.Clamp(value, 0, 1)
            : 1;
    }

    /// <summary>
    ///     音声比較の音量を 0 から 1 の範囲で保存します。
    /// </summary>
    /// <param name="source">比較元の音量の場合は true、比較先の場合は false。</param>
    /// <param name="value">保存する音量。</param>
    internal static void SaveDiffAudioVolume(bool source, double value)
    {
        WriteSetting(DiffAudioVolumePath(source),
            (double.IsFinite(value) ? Math.Clamp(value, 0, 1) : 1).ToString("R", CultureInfo.InvariantCulture));
    }

    /// <summary>
    ///     音量設定のファイル パスを取得します。
    /// </summary>
    /// <param name="source">比較元の場合は true、比較先の場合は false。</param>
    /// <returns>設定ファイルのパス。</returns>
    private static string DiffAudioVolumePath(bool source)
    {
        return Path.Combine(SettingsDirectory,
            source ? "diff-audio-source-volume.txt" : "diff-audio-target-volume.txt");
    }

    /// <summary>
    ///     DIFF ビューで差分行だけを表示するかどうかを読み込みます。
    /// </summary>
    /// <returns>差分行だけを表示する場合は true。</returns>
    public static bool LoadDiffDifferencesOnly()
    {
        return string.Equals(
            ReadSetting(DiffDisplayModePath, "all"), "differences", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    ///     DIFF ビューの表示モードを保存します。
    /// </summary>
    /// <param name="differencesOnly">差分行だけを表示する場合は true。</param>
    public static void SaveDiffDifferencesOnly(bool differencesOnly)
    {
        WriteSetting(DiffDisplayModePath, differencesOnly ? "differences" : "all");
    }

    /// <summary>
    ///     外部 DIFF ツールのコマンドを保存します。
    /// </summary>
    /// <param name="command">保存するコマンド。</param>
    public static void SaveExternalDiff(string command)
    {
        WriteSetting(ExternalDiffPath, command);
    }

    /// <summary>
    ///     外部エディターの引数を読み込みます。未保存の場合はエディターに応じた既定の引数を返します。
    /// </summary>
    /// <returns>エディターの引数テンプレート。</returns>
    public static string LoadEditorArguments()
    {
        var saved = ReadSetting(EditorArgumentsPath, null);
        if (!string.IsNullOrWhiteSpace(saved)) return saved;
        return DefaultEditorArguments(LoadEditorPath());
    }

    /// <summary>
    ///     外部エディターの実行ファイルと引数を保存します。サクラエディタは VS Code に置き換え、引数が空の場合は既定値を使います。
    /// </summary>
    /// <param name="executable">エディターの実行ファイル。</param>
    /// <param name="arguments">エディターの引数テンプレート。</param>
    public static void SaveEditor(string executable, string arguments)
    {
        var command = (executable ?? string.Empty).Trim();
        if (command.IndexOf("sakura", StringComparison.OrdinalIgnoreCase) >= 0)
            command = "code";
        WriteSetting(EditorPath, command);
        var args = (arguments ?? string.Empty).Trim();
        if (string.IsNullOrEmpty(args))
            args = DefaultEditorArguments(command);
        WriteSetting(EditorArgumentsPath, args);
    }

    /// <summary>
    ///     Grep の言語プロファイル名を読み込みます。
    /// </summary>
    /// <returns>プロファイル名。未保存の場合は null。</returns>
    public static string LoadGrepProfile()
    {
        return ReadSetting(GrepProfilePath, null);
    }

    /// <summary>
    ///     Grep の言語プロファイル名を保存します。
    /// </summary>
    /// <param name="profile">保存するプロファイル名。</param>
    public static void SaveGrepProfile(string profile)
    {
        WriteSetting(GrepProfilePath, profile);
    }

    /// <summary>
    ///     Grep の自由指定プロファイルで使う拡張子を読み込みます。
    /// </summary>
    /// <returns>空白区切りの拡張子。</returns>
    public static string LoadGrepFreeExtensions()
    {
        return ReadSetting(GrepFreeExtensionsPath, ".txt .log .ini .json .xml .html .htm .eml");
    }

    /// <summary>
    ///     Grep の自由指定プロファイルで使う拡張子を保存します。
    /// </summary>
    /// <param name="extensions">空白区切りの拡張子。</param>
    public static void SaveGrepFreeExtensions(string extensions)
    {
        WriteSetting(GrepFreeExtensionsPath, extensions);
    }

    /// <summary>
    ///     Grep 結果一覧の列幅を読み込みます。
    /// </summary>
    /// <returns>列幅の配列。未保存または不正な値を含む場合は null。</returns>
    public static double[] LoadGrepColumnWidths()
    {
        var value = ReadSetting(GrepColumnWidthsPath, null);
        if (string.IsNullOrWhiteSpace(value)) return null;
        var parts = value.Split(',');
        var result = new double[parts.Length];
        for (var index = 0; index < parts.Length; index++)
            if (!double.TryParse(parts[index], NumberStyles.Float, CultureInfo.InvariantCulture, out result[index]))
                return null;
        return result;
    }

    /// <summary>
    ///     Grep 結果一覧の列幅を保存します。
    /// </summary>
    /// <param name="widths">保存する列幅。</param>
    public static void SaveGrepColumnWidths(double[] widths)
    {
        if (widths == null) return;
        WriteSetting(GrepColumnWidthsPath, string.Join(",", Array.ConvertAll(widths,
            width => width.ToString("R", CultureInfo.InvariantCulture))));
    }

    /// <summary>
    ///     エディターの種類 (MIW、秀丸、Mery、その他) に応じた既定の引数テンプレートを取得します。
    /// </summary>
    /// <param name="command">エディターの実行ファイル。</param>
    /// <returns>{file}、{line}、{column} を含む引数テンプレート。</returns>
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

    /// <summary>
    ///     メイン ウィンドウの位置、サイズ、状態を読み込みます。
    /// </summary>
    /// <param name="left">ウィンドウの左端。</param>
    /// <param name="top">ウィンドウの上端。</param>
    /// <param name="width">ウィンドウの幅。</param>
    /// <param name="height">ウィンドウの高さ。</param>
    /// <param name="state">ウィンドウの状態。</param>
    /// <returns>有効な値を読み込めた場合は true。</returns>
    public static bool TryLoadMainWindowPlacement(out double left, out double top, out double width, out double height,
        out int state)
    {
        left = top = width = height = 0;
        state = 0;
        var text = ReadSetting(MainWindowPlacementPath, null);
        if (string.IsNullOrWhiteSpace(text)) return false;
        var parts = text.Split(',');
        if (parts.Length < 4) return false;
        if (!double.TryParse(parts[0], NumberStyles.Float, CultureInfo.InvariantCulture, out left)) return false;
        if (!double.TryParse(parts[1], NumberStyles.Float, CultureInfo.InvariantCulture, out top)) return false;
        if (!double.TryParse(parts[2], NumberStyles.Float, CultureInfo.InvariantCulture, out width)) return false;
        if (!double.TryParse(parts[3], NumberStyles.Float, CultureInfo.InvariantCulture, out height)) return false;
        if (parts.Length >= 5)
            int.TryParse(parts[4], NumberStyles.Integer, CultureInfo.InvariantCulture, out state);
        return width > 0 && height > 0;
    }

    /// <summary>
    ///     メイン ウィンドウの位置、サイズ、状態を保存します。
    /// </summary>
    /// <param name="left">ウィンドウの左端。</param>
    /// <param name="top">ウィンドウの上端。</param>
    /// <param name="width">ウィンドウの幅。</param>
    /// <param name="height">ウィンドウの高さ。</param>
    /// <param name="state">ウィンドウの状態。</param>
    public static void SaveMainWindowPlacement(double left, double top, double width, double height, int state)
    {
        WriteSetting(MainWindowPlacementPath, string.Join(",",
            left.ToString("R", CultureInfo.InvariantCulture),
            top.ToString("R", CultureInfo.InvariantCulture),
            width.ToString("R", CultureInfo.InvariantCulture),
            height.ToString("R", CultureInfo.InvariantCulture),
            state.ToString(CultureInfo.InvariantCulture)));
    }

    /// <summary>
    ///     DIFF ビュー ウィンドウの位置、サイズ、状態を読み込みます。
    /// </summary>
    /// <param name="left">ウィンドウの左端。</param>
    /// <param name="top">ウィンドウの上端。</param>
    /// <param name="width">ウィンドウの幅。</param>
    /// <param name="height">ウィンドウの高さ。</param>
    /// <param name="state">ウィンドウの状態。</param>
    /// <returns>有効な値を読み込めた場合は true。</returns>
    public static bool TryLoadDiffWindowPlacement(out double left, out double top, out double width, out double height,
        out int state)
    {
        left = top = width = height = 0;
        state = 0;
        var text = ReadSetting(Path.Combine(SettingsDirectory, "diff-window.txt"), null);
        if (string.IsNullOrWhiteSpace(text)) return false;
        var parts = text.Split(',');
        if (parts.Length < 4) return false;
        if (!double.TryParse(parts[0], NumberStyles.Float, CultureInfo.InvariantCulture, out left)) return false;
        if (!double.TryParse(parts[1], NumberStyles.Float, CultureInfo.InvariantCulture, out top)) return false;
        if (!double.TryParse(parts[2], NumberStyles.Float, CultureInfo.InvariantCulture, out width)) return false;
        if (!double.TryParse(parts[3], NumberStyles.Float, CultureInfo.InvariantCulture, out height)) return false;
        if (parts.Length >= 5)
            int.TryParse(parts[4], NumberStyles.Integer, CultureInfo.InvariantCulture, out state);
        return width > 0 && height > 0;
    }

    /// <summary>
    ///     DIFF ビュー ウィンドウの位置、サイズ、状態を保存します。
    /// </summary>
    /// <param name="left">ウィンドウの左端。</param>
    /// <param name="top">ウィンドウの上端。</param>
    /// <param name="width">ウィンドウの幅。</param>
    /// <param name="height">ウィンドウの高さ。</param>
    /// <param name="state">ウィンドウの状態。</param>
    public static void SaveDiffWindowPlacement(double left, double top, double width, double height, int state)
    {
        WriteSetting(Path.Combine(SettingsDirectory, "diff-window.txt"), string.Join(",",
            left.ToString("R", CultureInfo.InvariantCulture),
            top.ToString("R", CultureInfo.InvariantCulture),
            width.ToString("R", CultureInfo.InvariantCulture),
            height.ToString("R", CultureInfo.InvariantCulture),
            state.ToString(CultureInfo.InvariantCulture)));
    }

    /// <summary>
    ///     DIFF ビューの真偽値設定を読み込みます。
    /// </summary>
    /// <param name="name">設定名。</param>
    /// <param name="fallback">未保存の場合の既定値。</param>
    /// <returns>設定値。</returns>
    internal static bool LoadDiffFlag(string name, bool fallback)
    {
        return bool.TryParse(ReadSetting(Path.Combine(SettingsDirectory, "diff-" + name + ".txt"), null),
            out var value)
            ? value
            : fallback;
    }

    /// <summary>
    ///     DIFF ビューの真偽値設定を保存します。
    /// </summary>
    /// <param name="name">設定名。</param>
    /// <param name="value">保存する値。</param>
    internal static void SaveDiffFlag(string name, bool value)
    {
        WriteSetting(Path.Combine(SettingsDirectory, "diff-" + name + ".txt"), value.ToString());
    }

    /// <summary>
    ///     パッチ適用先のルート フォルダーを読み込みます。
    /// </summary>
    /// <returns>ルート フォルダーのパス。未保存の場合は null。</returns>
    internal static string LoadPatchRoot()
    {
        return ReadSetting(Path.Combine(SettingsDirectory, "diff-patch-root.txt"), null);
    }

    /// <summary>
    ///     パッチ適用先のルート フォルダーを保存します。
    /// </summary>
    /// <param name="root">保存するパス。</param>
    internal static void SavePatchRoot(string root)
    {
        WriteSetting(Path.Combine(SettingsDirectory, "diff-patch-root.txt"), root ?? string.Empty);
    }

    /// <summary>
    ///     設定ファイルの内容を読み込みます。
    /// </summary>
    /// <param name="path">設定ファイルのパス。</param>
    /// <param name="fallback">ファイルがない場合や読み込めない場合の既定値。</param>
    /// <returns>前後の空白を除いた設定値。</returns>
    private static string ReadSetting(string path, string fallback)
    {
        try
        {
            return File.Exists(path) ? File.ReadAllText(path, Encoding.UTF8).Trim() : fallback;
        }
        catch
        {
            return fallback;
        }
    }

    /// <summary>
    ///     設定ファイルに値を書き込みます。失敗しても例外は送出しません。
    /// </summary>
    /// <param name="path">設定ファイルのパス。</param>
    /// <param name="value">書き込む値。</param>
    private static void WriteSetting(string path, string value)
    {
        try
        {
            Directory.CreateDirectory(SettingsDirectory);
            File.WriteAllText(path, value ?? string.Empty, new UTF8Encoding(false));
        }
        catch
        {
            // Settings persistence must never prevent the app from closing.
        }
    }

    /// <summary>
    ///     現在のスロットの設定フォルダー内にあるファイルとサブフォルダーをすべて削除します。
    /// </summary>
    public static void ClearAll()
    {
        try
        {
            if (!Directory.Exists(SettingsDirectory)) return;
            foreach (var file in Directory.GetFiles(SettingsDirectory, "*", SearchOption.AllDirectories))
                try
                {
                    File.Delete(file);
                }
                catch
                {
                    // Settings persistence must never prevent the app from closing.
                }

            foreach (var dir in Directory.GetDirectories(SettingsDirectory))
                try
                {
                    Directory.Delete(dir, true);
                }
                catch
                {
                    // Settings persistence must never prevent the app from closing.
                }
        }
        catch
        {
            // Settings persistence must never prevent the app from closing.
        }
    }
}
