using System;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace DesktopIniManager.Services;

internal static class LanguageIconService
{
    /// <summary>
    ///     Flag.icl から言語選択用の国旗アイコンを 4 つ読み込みます。
    /// </summary>
    /// <returns>国旗アイコンの配列。読み込めなかった要素は null。</returns>
    internal static ImageSource[] LoadFlagIcons()
    {
        var flags = new ImageSource[4];
        var path = PrepareFlagLibrary(FindFlagLibrary());
        if (path == null) return flags;
        for (var index = 0; index < flags.Length; index++)
        {
            flags[index] = ExtractFlagIcon(path, index);
            if (flags[index] == null) flags[index] = ExtractFlagIconFromResources(path, index);
        }

        return flags;
    }

    /// <summary>
    ///     国旗アイコン ライブラリ (Flag.icl) を候補のフォルダーから探します。
    /// </summary>
    /// <returns>見つかったファイルのパス。見つからない場合は null。</returns>
    private static string FindFlagLibrary()
    {
        var baseDir = AppDomain.CurrentDomain.BaseDirectory;
        string[] candidates =
        {
            Path.Combine(baseDir, "Assets", "Flag.icl"),
            Path.Combine(baseDir, "Assets", "flag.icl"),
            Path.GetFullPath(Path.Combine(baseDir, "..", "..", "Assets", "Flag.icl")),
            Path.GetFullPath(Path.Combine(baseDir, "..", "..", "Assets", "flag.icl")),
            Path.GetFullPath(Path.Combine(Environment.CurrentDirectory, "Assets", "Flag.icl"))
        };
        return candidates.FirstOrDefault(File.Exists);
    }

    /// <summary>
    ///     国旗アイコン ライブラリのブロックを解除し、ローカル アプリケーション データ フォルダーにコピーして使用します。
    /// </summary>
    /// <param name="path">元のライブラリのパス。</param>
    /// <returns>使用するライブラリのパス。ライブラリがない場合は null。</returns>
    private static string PrepareFlagLibrary(string path)
    {
        if (string.IsNullOrEmpty(path) || !File.Exists(path)) return null;
        UnblockInternetZone(path);
        try
        {
            var cacheDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "DesktopIniManager");
            Directory.CreateDirectory(cacheDir);
            var cache = Path.Combine(cacheDir, "Flag.icl");
            if (!File.Exists(cache) || File.GetLastWriteTimeUtc(path) > File.GetLastWriteTimeUtc(cache))
                File.Copy(path, cache, true);
            UnblockInternetZone(cache);
            return File.Exists(cache) ? cache : path;
        }
        catch
        {
            return path;
        }
    }

    /// <summary>
    ///     ファイルの Zone.Identifier 代替データ ストリームを削除し、インターネットからのブロックを解除します。
    /// </summary>
    /// <param name="path">対象のファイル。</param>
    private static void UnblockInternetZone(string path)
    {
        try
        {
            File.Delete(path + ":Zone.Identifier");
        }
        catch
        {
            // Ignore errors when trying to delete the alternate data stream
        }
    }

    /// <summary>
    ///     ExtractIconEx で指定インデックスのアイコンを取り出し、16×16 の画像に変換します。
    /// </summary>
    /// <param name="path">アイコン ライブラリのパス。</param>
    /// <param name="index">アイコンのインデックス。</param>
    /// <returns>取り出した画像。失敗した場合は null。</returns>
    private static ImageSource ExtractFlagIcon(string path, int index)
    {
        var large = new IntPtr[1];
        var small = new IntPtr[1];
        ExtractIconEx(path, index, large, small, 1);
        var handle = large[0] != IntPtr.Zero ? large[0] : small[0];
        if (handle == IntPtr.Zero) return null;
        try
        {
            var bitmap = Imaging.CreateBitmapSourceFromHIcon(handle, Int32Rect.Empty,
                BitmapSizeOptions.FromWidthAndHeight(16, 16));
            bitmap.Freeze();
            return bitmap;
        }
        catch
        {
            return null;
        }
        finally
        {
            if (large[0] != IntPtr.Zero) DestroyIcon(large[0]);
            if (small[0] != IntPtr.Zero) DestroyIcon(small[0]);
        }
    }

    /// <summary>
    ///     リソースを直接読み込んで、指定インデックスのアイコンを取り出します。
    /// </summary>
    /// <param name="path">アイコン ライブラリのパス。</param>
    /// <param name="index">アイコンのシェル インデックス。</param>
    /// <returns>取り出した画像。失敗した場合は null。</returns>
    private static ImageSource ExtractFlagIconFromResources(string path, int index)
    {
        try
        {
            var group = IconResourceReader.Read(path).FirstOrDefault(item => item.ShellIndex == index);
            return group != null ? group.Preview : null;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    ///     Win32 API の ExtractIconEx を呼び出し、ファイルからアイコンを取り出します。
    /// </summary>
    /// <param name="file">アイコンを含むファイル。</param>
    /// <param name="index">取り出す最初のアイコンのインデックス。</param>
    /// <param name="large">大きいアイコンのハンドルを受け取る配列。</param>
    /// <param name="small">小さいアイコンのハンドルを受け取る配列。</param>
    /// <param name="count">取り出すアイコン数。</param>
    /// <returns>取り出したアイコン数。</returns>
    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern uint ExtractIconEx(string file, int index, IntPtr[] large, IntPtr[] small, uint count);

    /// <summary>
    ///     Win32 API の DestroyIcon を呼び出し、アイコン ハンドルを破棄します。
    /// </summary>
    /// <param name="icon">破棄するアイコン ハンドル。</param>
    /// <returns>成功した場合は true。</returns>
    [DllImport("user32.dll")]
    private static extern bool DestroyIcon(IntPtr icon);
}