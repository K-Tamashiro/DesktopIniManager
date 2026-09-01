using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media.Imaging;

namespace DesktopIniManager.Services
{
    internal static class FolderIconService
    {
        private static readonly object CacheLock = new object();
        private static readonly Dictionary<string, BitmapSource> IconCache =
            new Dictionary<string, BitmapSource>(StringComparer.OrdinalIgnoreCase);

        private static BitmapSource _defaultFolderIcon;

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

        public static void ClearCache()
        {
            lock (CacheLock)
            {
                IconCache.Clear();
                _defaultFolderIcon = null;
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
}
