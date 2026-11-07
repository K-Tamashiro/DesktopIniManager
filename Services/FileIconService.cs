using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace DesktopIniManager.Services;

internal static class FileIconService
{
    private static readonly Dictionary<string, ImageSource> TypeIcons = new(StringComparer.OrdinalIgnoreCase);

    private static readonly string[] ImageExtensions =
        [".png", ".jpg", ".jpeg", ".bmp", ".gif", ".tif", ".tiff", ".ico"];

    /// <summary>
    ///     拡張子に対応するシェルの種類アイコンを取得し、キャッシュします。
    /// </summary>
    /// <param name="extension">ファイルの拡張子。</param>
    /// <returns>種類アイコン。取得できない場合は null。</returns>
    public static ImageSource GetTypeIcon(string extension)
    {
        lock (TypeIcons)
        {
            if (TypeIcons.TryGetValue(extension ?? "", out ImageSource cached)) return cached;

            var info = new ShellFileInfo();
            SHGetFileInfo("file" + extension, 0x80, ref info, (uint)Marshal.SizeOf(typeof(ShellFileInfo)),
                0x100 | 0x10);
            if (info.Icon != IntPtr.Zero)
                try
                {
                    var bitmap = Imaging.CreateBitmapSourceFromHIcon(
                        info.Icon, Int32Rect.Empty, BitmapSizeOptions.FromWidthAndHeight(48, 48));
                    bitmap.Freeze();
                    cached = bitmap;
                }
                finally
                {
                    DestroyIcon(info.Icon);
                }

            if (TypeIcons.Count >= 512) TypeIcons.Clear();
            TypeIcons[extension ?? ""] = cached;
            return cached;
        }
    }

    /// <summary>
    ///     ローカルにある画像ファイルは縮小プレビューを、それ以外は種類アイコンを取得します。
    /// </summary>
    /// <param name="path">ファイルのパス。</param>
    /// <returns>表示用のアイコン。</returns>
    public static ImageSource GetIcon(string path)
    {
        var extension = Path.GetExtension(path);
        if (Array.Exists(ImageExtensions, item => string.Equals(item, extension, StringComparison.OrdinalIgnoreCase))
            && IsLocallyAvailable(path))
            try
            {
                var preview = new BitmapImage();
                preview.BeginInit();
                preview.CacheOption = BitmapCacheOption.OnLoad;
                preview.CreateOptions = BitmapCreateOptions.IgnoreColorProfile;
                preview.DecodePixelWidth = 128;
                preview.UriSource = new Uri(path, UriKind.Absolute);
                preview.EndInit();
                preview.Freeze();
                return preview;
            }
            catch
            {
                //
            }

        return GetTypeIcon(extension);
    }

    /// <summary>
    ///     ファイルがオフラインやクラウドのみの状態でなく、読み込んでもダウンロードが発生しないかどうかを判定します。
    /// </summary>
    /// <param name="path">判定するファイルのパス。</param>
    /// <returns>ローカルで利用できる場合は true。</returns>
    private static bool IsLocallyAvailable(string path)
    {
        try
        {
            var attributes = File.GetAttributes(path);
            if ((attributes & FileAttributes.Offline) != 0) return false;
            const FileAttributes recallOnDataAccess = (FileAttributes)0x00400000;
            const FileAttributes recallOnOpen = (FileAttributes)0x00040000;
            if ((attributes & recallOnDataAccess) != 0) return false;
            if ((attributes & recallOnOpen) != 0) return false;
            return true;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    ///     Win32 API の SHGetFileInfo を呼び出し、ファイルのアイコンなどの情報を取得します。
    /// </summary>
    /// <param name="path">ファイルのパスまたは名前。</param>
    /// <param name="attributes">ファイル属性。</param>
    /// <param name="info">取得した情報。</param>
    /// <param name="size">構造体のサイズ。</param>
    /// <param name="flags">取得する情報のフラグ。</param>
    /// <returns>処理結果。</returns>
    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr SHGetFileInfo(string path, uint attributes, ref ShellFileInfo info, uint size,
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
        public IntPtr Icon;
        public int Index;
        public uint Attributes;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)]
        public string DisplayName;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 80)]
        public string TypeName;
    }
}