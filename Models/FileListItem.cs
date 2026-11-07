using DesktopIniManager.Services;
using System;
using System.Windows.Media;

namespace DesktopIniManager.Models;

internal sealed class FileListItem
{
    private ImageSource _icon;

    /// <summary>
    ///     ファイル パスからファイル一覧の項目を作成し、検索一致と選択フォルダー直下かどうかを判定します。
    /// </summary>
    /// <param name="path">ファイルのフル パス。</param>
    /// <param name="searchKeys">ファイル名の一致判定に使う検索キー。</param>
    /// <param name="selectedFolder">選択中のフォルダー パス。</param>
    public FileListItem(string path, string[] searchKeys = null, string selectedFolder = null)
    {
        Path = path;
        Name = System.IO.Path.GetFileName(path);
        Extension = System.IO.Path.GetExtension(path);
        IsSearchMatch = searchKeys != null && Array.Exists(searchKeys,
            key => FastFolderSearchService.FileNameMatches(Name, key));
        IsDirectChild = IsImmediateChild(path, selectedFolder);
    }

    public string Path { get; }
    public string Name { get; }
    public string Extension { get; }
    public bool IsSearchMatch { get; }
    public bool IsDirectChild { get; }

    public ImageSource Icon
    {
        get
        {
            if (_icon != null) return _icon;
            try
            {
                _icon = FileIconService.GetIcon(Path);
            }
            catch (Exception)
            {
                return null;
            }

            return _icon;
        }
    }

    /// <summary>
    ///     ファイルが指定フォルダーの直下にあるかどうかを判定します。
    /// </summary>
    /// <param name="filePath">判定するファイルのパス。</param>
    /// <param name="folderPath">基準となるフォルダーのパス。</param>
    /// <returns>直下にある場合、またはどちらかのパスが空の場合は true。</returns>
    private static bool IsImmediateChild(string filePath, string folderPath)
    {
        if (string.IsNullOrWhiteSpace(filePath) || string.IsNullOrWhiteSpace(folderPath))
            return true;
        var parent = System.IO.Path.GetDirectoryName(filePath);
        return string.Equals(Normalize(parent), Normalize(folderPath), StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    ///     パス末尾のディレクトリ区切り文字を取り除きます。
    /// </summary>
    /// <param name="path">正規化するパス。</param>
    /// <returns>正規化したパス。空の場合は空文字列。</returns>
    private static string Normalize(string path)
    {
        if (string.IsNullOrWhiteSpace(path)) return string.Empty;
        return path.TrimEnd(System.IO.Path.DirectorySeparatorChar, System.IO.Path.AltDirectorySeparatorChar);
    }
}