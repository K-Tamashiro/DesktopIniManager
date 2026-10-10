using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Windows.Media;
using DesktopIniManager.ViewModels;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media.Imaging;

namespace DesktopIniManager.Services;

internal static class FolderIconService
{
    private static readonly object CacheLock = new object();
    private static readonly Dictionary<string, BitmapSource> IconCache =
        new Dictionary<string, BitmapSource>(StringComparer.OrdinalIgnoreCase);

    private static BitmapSource _defaultFolderIcon;

    // Called on a worker for visible folders only. Never cache emptiness by path:
    // files may have been added or removed since the last display.
    public static ImageSource GetStateIcon(string path, string reason = null)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(path) || !Directory.Exists(path)) return null;
            bool network = IsNetworkOrCloud(path);
            // The main screen's analysis is authoritative, including when desktop.ini
            // or child folders exist. Do not reinterpret its Empty folder result.
            if (string.Equals(reason, "Empty folder", StringComparison.OrdinalIgnoreCase))
                return DifferencerStatusIcons.GetCustomIcon(network ? 107 : 105);
            string ini = Path.Combine(path, "desktop.ini");
            if (File.Exists(ini) && File.ReadLines(ini).Any(line =>
                line.TrimStart().StartsWith("IconResource=", StringComparison.OrdinalIgnoreCase) ||
                line.TrimStart().StartsWith("IconFile=", StringComparison.OrdinalIgnoreCase)))
                return GetFolderIcon(path);

            // Match AnalyzeDevelopment when analysis has not run: desktop.ini and
            // ordinary child folders do not count; a .git directory is a repository.
            bool empty = !Directory.EnumerateFiles(path).Any(file =>
                !string.Equals(Path.GetFileName(file), "desktop.ini", StringComparison.OrdinalIgnoreCase)) &&
                !Directory.EnumerateDirectories(path).Any(folder =>
                    string.Equals(Path.GetFileName(folder), ".git", StringComparison.OrdinalIgnoreCase));
            return DifferencerStatusIcons.GetCustomIcon(network ? (empty ? 107 : 106) : (empty ? 105 : 104));
        }
        catch { return null; } // Unreadable folders must not be presented as empty.
    }

    private static bool IsNetworkOrCloud(string path)
    {
        string fullPath = Path.GetFullPath(path);
        string root = Path.GetPathRoot(fullPath);
        if (fullPath.StartsWith(@"\\", StringComparison.Ordinal) ||
            (!string.IsNullOrEmpty(root) && new DriveInfo(root).DriveType == DriveType.Network)) return true;
        foreach (string variable in new[] { "OneDrive", "OneDriveConsumer", "OneDriveCommercial" })
        {
            string cloudRoot = Environment.GetEnvironmentVariable(variable);
            if (string.IsNullOrWhiteSpace(cloudRoot)) continue;
            cloudRoot = Path.GetFullPath(cloudRoot).TrimEnd(Path.DirectorySeparatorChar);
            if (string.Equals(fullPath.TrimEnd(Path.DirectorySeparatorChar), cloudRoot, StringComparison.OrdinalIgnoreCase) ||
                fullPath.StartsWith(cloudRoot + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)) return true;
        }
        // Cloud Files providers expose recall/pinned flags on placeholders and their ancestors.
        for (var folder = new DirectoryInfo(fullPath); folder != null; folder = folder.Parent)
            if (((int)folder.Attributes & (0x00040000 | 0x00080000 | 0x00100000 | 0x00400000)) != 0) return true;
        return false;
    }

    public static BitmapSource GetFolderIcon(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
            return GetDefaultFolderIcon();

        lock (CacheLock)
        {
            if (IconCache.TryGetValue(path, out BitmapSource cached))
                return cached;
        }

        BitmapSource icon = GetIcon(path, false);
        if (icon == null)
            return GetDefaultFolderIcon();

        lock (CacheLock)
        {
            if (!IconCache.ContainsKey(path))
                IconCache[path] = icon;

            return IconCache[path];
        }
    }

    public static BitmapSource GetDefaultFolderIcon()
    {
        lock (CacheLock)
        {
            if (_defaultFolderIcon != null)
                return _defaultFolderIcon;
        }

        BitmapSource icon = GetIcon("folder", true);

        lock (CacheLock)
        {
            if (_defaultFolderIcon == null)
                _defaultFolderIcon = icon;

            return _defaultFolderIcon;
        }
    }

    public static void Invalidate(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
            return;

        lock (CacheLock)
        {
            IconCache.Remove(path);
        }
    }

    private static BitmapSource GetIcon(string path, bool useFileAttributes)
    {
        ShellFileInfo info = new ShellFileInfo();
        uint flags = 0x000000100 | 0x000000000; // SHGFI_ICON | SHGFI_LARGEICON
        if (useFileAttributes) flags |= 0x000000010; // SHGFI_USEFILEATTRIBUTES

        IntPtr result = SHGetFileInfo(
            path,
            0x10,
            ref info,
            (uint)Marshal.SizeOf(info),
            flags); // FILE_ATTRIBUTE_DIRECTORY

        if (result == IntPtr.Zero || info.IconHandle == IntPtr.Zero)
            return null;

        try
        {
            BitmapSource source = Imaging.CreateBitmapSourceFromHIcon(
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

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr SHGetFileInfo(
        string path,
        uint attributes,
        ref ShellFileInfo info,
        uint size,
        uint flags);

    [DllImport("user32.dll")]
    private static extern bool DestroyIcon(IntPtr icon);
}
