using DesktopIniManager.ViewModels;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace DesktopIniManager.Services;

internal static class FolderIconService
{
    private static readonly object CacheLock = new();

    private static readonly Dictionary<string, BitmapSource> IconCache = new(StringComparer.OrdinalIgnoreCase);

    private static BitmapSource _defaultFolderIcon;

    // Called on a worker for visible folders only. Never cache emptiness by path:
    // files may have been added or removed since the last display.
    /// <summary>
    ///     フォルダーの状態 (カスタム アイコン、空、ネットワーク/クラウド) に応じた表示アイコンを取得します。
    ///     表示中のフォルダーに対してワーカー スレッドから呼び出されます。空かどうかはパスごとにキャッシュしません。
    /// </summary>
    /// <param name="path">フォルダーのパス。</param>
    /// <param name="reason">メイン画面の解析結果。「Empty folder」の場合はその判定を優先します。</param>
    /// <returns>状態アイコン。フォルダーを読み取れない場合は null。</returns>
    public static ImageSource GetStateIcon(string path, string reason = null)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(path) || !Directory.Exists(path)) return null;
            var network = IsNetworkOrCloud(path);
            // The main screen's analysis is authoritative, including when desktop.ini
            // or child folders exist. Do not reinterpret its Empty folder result.
            if (string.Equals(reason, "Empty folder", StringComparison.OrdinalIgnoreCase))
                return DifferencerStatusIcons.GetCustomIcon(network ? 107 : 105);
            var ini = Path.Combine(path, "desktop.ini");
            if (File.Exists(ini) && File.ReadLines(ini).Any(line =>
                    line.TrimStart().StartsWith("IconResource=", StringComparison.OrdinalIgnoreCase) ||
                    line.TrimStart().StartsWith("IconFile=", StringComparison.OrdinalIgnoreCase)))
                return GetFolderIcon(path);

            // Match AnalyzeDevelopment when analysis has not run: desktop.ini and
            // ordinary child folders do not count; a .git directory is a repository.
            var empty = !Directory.EnumerateFiles(path).Any(file =>
                            !string.Equals(Path.GetFileName(file), "desktop.ini",
                                StringComparison.OrdinalIgnoreCase)) &&
                        !Directory.EnumerateDirectories(path).Any(folder =>
                            string.Equals(Path.GetFileName(folder), ".git", StringComparison.OrdinalIgnoreCase));
            return DifferencerStatusIcons.GetCustomIcon(network ? empty ? 107 : 106 : empty ? 105 : 104);
        }
        catch
        {
            return null;
        } // Unreadable folders must not be presented as empty.
    }

    /// <summary>
    ///     パスがネットワーク ドライブ、UNC パス、OneDrive 配下、またはクラウド ファイルのプレースホルダーかどうかを判定します。
    /// </summary>
    /// <param name="path">判定するパス。</param>
    /// <returns>ネットワークまたはクラウド上の場合は true。</returns>
    private static bool IsNetworkOrCloud(string path)
    {
        var fullPath = Path.GetFullPath(path);
        var root = Path.GetPathRoot(fullPath);
        if (fullPath.StartsWith(@"\\", StringComparison.Ordinal) ||
            (!string.IsNullOrEmpty(root) && new DriveInfo(root).DriveType == DriveType.Network)) return true;
        foreach (var variable in new[] { "OneDrive", "OneDriveConsumer", "OneDriveCommercial" })
        {
            var cloudRoot = Environment.GetEnvironmentVariable(variable);
            if (string.IsNullOrWhiteSpace(cloudRoot)) continue;
            cloudRoot = Path.GetFullPath(cloudRoot).TrimEnd(Path.DirectorySeparatorChar);
            if (string.Equals(fullPath.TrimEnd(Path.DirectorySeparatorChar), cloudRoot,
                    StringComparison.OrdinalIgnoreCase) ||
                fullPath.StartsWith(cloudRoot + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                return true;
        }

        // Cloud Files providers expose recall/pinned flags on placeholders and their ancestors.
        for (var folder = new DirectoryInfo(fullPath); folder != null; folder = folder.Parent)
            if (((int)folder.Attributes & (0x00040000 | 0x00080000 | 0x00100000 | 0x00400000)) != 0)
                return true;
        return false;
    }

    /// <summary>
    ///     フォルダーのシェル アイコンを取得し、パスごとにキャッシュします。
    /// </summary>
    /// <param name="path">フォルダーのパス。</param>
    /// <returns>フォルダー アイコン。取得できない場合は既定のフォルダー アイコン。</returns>
    public static BitmapSource GetFolderIcon(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
            return GetDefaultFolderIcon();

        lock (CacheLock)
        {
            if (IconCache.TryGetValue(path, out var cached))
                return cached;
        }

        var icon = GetIcon(path, false);
        if (icon == null)
            return GetDefaultFolderIcon();

        lock (CacheLock)
        {
            if (!IconCache.ContainsKey(path))
                IconCache[path] = icon;

            return IconCache[path];
        }
    }

    /// <summary>
    ///     既定のフォルダー アイコンを取得し、キャッシュします。
    /// </summary>
    /// <returns>既定のフォルダー アイコン。</returns>
    public static BitmapSource GetDefaultFolderIcon()
    {
        lock (CacheLock)
        {
            if (_defaultFolderIcon != null)
                return _defaultFolderIcon;
        }

        var icon = GetIcon("folder", true);

        lock (CacheLock)
        {
            if (_defaultFolderIcon == null)
                _defaultFolderIcon = icon;

            return _defaultFolderIcon;
        }
    }

    /// <summary>
    ///     指定フォルダーのアイコン キャッシュを破棄します。
    /// </summary>
    /// <param name="path">フォルダーのパス。</param>
    public static void Invalidate(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
            return;

        lock (CacheLock)
        {
            IconCache.Remove(path);
        }
    }

    /// <summary>
    ///     SHGetFileInfo で大きいアイコンを取得し、32×32 のフリーズ済みビットマップに変換します。
    /// </summary>
    /// <param name="path">対象のパス。</param>
    /// <param name="useFileAttributes">実在しない名前に対してディレクトリ属性から種類アイコンを取得する場合は true。</param>
    /// <returns>取得したアイコン。取得できない場合は null。</returns>
    private static BitmapSource GetIcon(string path, bool useFileAttributes)
    {
        var info = new ShellFileInfo();
        uint flags = 0x000000100 | 0x000000000; // SHGFI_ICON | SHGFI_LARGEICON
        if (useFileAttributes) flags |= 0x000000010; // SHGFI_USEFILEATTRIBUTES

        var result = SHGetFileInfo(
            path,
            0x10,
            ref info,
            (uint)Marshal.SizeOf(info),
            flags); // FILE_ATTRIBUTE_DIRECTORY

        if (result == IntPtr.Zero || info.IconHandle == IntPtr.Zero)
            return null;

        try
        {
            var source = Imaging.CreateBitmapSourceFromHIcon(
                info.IconHandle,
                Int32Rect.Empty,
                BitmapSizeOptions.FromWidthAndHeight(32, 32));

            source.Freeze();
            return source;
        }
        finally
        {
            DestroyIcon(info.IconHandle);
        }
    }

    /// <summary>
    ///     Win32 API の SHGetFileInfo を呼び出し、ファイルやフォルダーのアイコン情報を取得します。
    /// </summary>
    /// <param name="path">対象のパス。</param>
    /// <param name="attributes">ファイル属性。</param>
    /// <param name="info">取得した情報。</param>
    /// <param name="size">構造体のサイズ。</param>
    /// <param name="flags">取得する情報のフラグ。</param>
    /// <returns>処理結果。失敗時は IntPtr.Zero。</returns>
    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr SHGetFileInfo(
        string path,
        uint attributes,
        ref ShellFileInfo info,
        uint size,
        uint flags);

    /// <summary>
    ///     Win32 API の DestroyIcon を呼び出し、アイコン ハンドルを破棄します。
    /// </summary>
    /// <param name="icon">破棄するアイコン ハンドル。</param>
    /// <returns>成功した場合は true。</returns>
    [DllImport("user32.dll")]
    private static extern bool DestroyIcon(IntPtr icon);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct ShellFileInfo
    {
        public IntPtr IconHandle;
        public int IconIndex;
        public uint Attributes;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)]
        public string DisplayName;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 80)]
        public string TypeName;
    }
}