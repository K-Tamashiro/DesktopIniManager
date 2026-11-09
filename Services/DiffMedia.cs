using DesktopIniManager.Properties;
using System.IO;
using System.Linq;

namespace DesktopIniManager.Services;

internal static class DiffMedia
{
    internal static string BinaryMessage =>
        StringOverlay.Get("Diff_BinaryNotSupported");

    /// <summary>
    ///     DIFF ビューで表示できないバイナリ ファイルやキャッシュ ファイルかどうかを拡張子で判定します。動画と音声は対象外です。
    /// </summary>
    /// <param name="path">判定するファイルのパス。</param>
    /// <returns>表示できないバイナリ ファイルの場合は true。</returns>
    public static bool IsBinary(string path)
    {
        var ext = Path.GetExtension(path).ToLowerInvariant();
        // Videos are handled separately; do not treat them as unsupported binary.
        if (IsVideo(path) || IsAudio(path)) return false;
        return new[]
        {
            ".exe", ".dll", ".pdb", ".obj", ".lib", ".zip", ".7z", ".rar", ".gz", ".pdf", ".doc", ".docx", ".xls",
            ".xlsx", ".ppt", ".pptx", ".db", ".sqlite", ".mp3", ".wav", ".msi", ".bin", ".icl", ".resources", ".baml",
            ".cache",
            // VB6 のバイナリ リソース（フォーム .frx、ユーザー コントロール .ctx、デザイナー .dsx、
            // ActiveX ドキュメント .dox、プロパティ ページ .pgx）とコンポーネント。編集しないので比較対象から外す。
            ".frx", ".ctx", ".dsx", ".dox", ".pgx", ".ocx", ".oca", ".tlb", ".res",
            // Android（ネイティブ ライブラリ、アーカイブ、パッケージ、DEX、クラス、署名キー）。
            ".so", ".a", ".o", ".jar", ".aar", ".apk", ".aab", ".dex", ".class", ".jks", ".keystore",
            // C++ のビルド生成物（プリコンパイル済みヘッダー、インクリメンタル リンク、エクスポート、デバッグ情報）。
            ".pch", ".ilk", ".exp", ".idb", ".ipch"
        }.Contains(ext);
    }

    /// <summary>
    ///     画像として比較できるファイルかどうかを拡張子で判定します。
    /// </summary>
    /// <param name="path">判定するファイルのパス。</param>
    /// <returns>対応する画像形式の場合は true。</returns>
    public static bool IsImage(string path)
    {
        return new[] { ".png", ".jpg", ".jpeg", ".bmp", ".gif", ".ico", ".tif", ".tiff", ".wdp", ".jxr" }.Contains(
            Path.GetExtension(path).ToLowerInvariant());
    }

    /// <summary>
    ///     音声として比較できるファイル (MP3/WAV) かどうかを拡張子で判定します。
    /// </summary>
    /// <param name="path">判定するファイルのパス。</param>
    /// <returns>対応する音声形式の場合は true。</returns>
    public static bool IsAudio(string path)
    {
        return new[] { ".mp3", ".wav", ".wave" }.Contains(Path.GetExtension(path).ToLowerInvariant());
    }

    /// <summary>
    ///     動画として比較できるファイルかどうかを拡張子で判定します。
    /// </summary>
    /// <param name="path">判定するファイルのパス。</param>
    /// <returns>対応する動画形式の場合は true。</returns>
    public static bool IsVideo(string path)
    {
        return new[] { ".mp4", ".avi", ".mkv", ".mov", ".wmv", ".webm", ".m4v", ".mpg", ".mpeg" }.Contains(
            Path.GetExtension(path).ToLowerInvariant());
    }
}