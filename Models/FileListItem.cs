using System;
using System.Windows.Media;
using DesktopIniManager.Services;

namespace DesktopIniManager.Models;

internal sealed class FileListItem
{
    private ImageSource _icon;

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

    private static bool IsImmediateChild(string filePath, string folderPath)
    {
        if (string.IsNullOrWhiteSpace(filePath) || string.IsNullOrWhiteSpace(folderPath))
            return true;
        var parent = System.IO.Path.GetDirectoryName(filePath);
        return string.Equals(Normalize(parent), Normalize(folderPath), StringComparison.OrdinalIgnoreCase);
    }

    private static string Normalize(string path)
    {
        if (string.IsNullOrWhiteSpace(path)) return string.Empty;
        return path.TrimEnd(System.IO.Path.DirectorySeparatorChar, System.IO.Path.AltDirectorySeparatorChar);
    }
}