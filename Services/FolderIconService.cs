using System;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media.Imaging;

namespace DesktopIniManager.Services
{
    internal static class FolderIconService
    {
        public static BitmapSource GetFolderIcon(string path) => GetIcon(path, false);
        public static BitmapSource GetDefaultFolderIcon() => GetIcon("folder", true);

        private static BitmapSource GetIcon(string path, bool useFileAttributes)
        {
            ShellFileInfo info = new ShellFileInfo();
            uint flags = 0x000000100 | 0x000000000; // SHGFI_ICON | SHGFI_LARGEICON
            if (useFileAttributes) flags |= 0x000000010; // SHGFI_USEFILEATTRIBUTES
            IntPtr result = SHGetFileInfo(path, 0x10, ref info, (uint)Marshal.SizeOf(info), flags); // FILE_ATTRIBUTE_DIRECTORY
            if (result == IntPtr.Zero || info.IconHandle == IntPtr.Zero) return null;
            try
            {
                BitmapSource source = Imaging.CreateBitmapSourceFromHIcon(info.IconHandle, Int32Rect.Empty, BitmapSizeOptions.FromWidthAndHeight(32, 32));
                source.Freeze();
                return source;
            }
            finally { DestroyIcon(info.IconHandle); }
        }

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct ShellFileInfo
        {
            public IntPtr IconHandle; public int IconIndex; public uint Attributes;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)] public string DisplayName;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 80)] public string TypeName;
        }

        [DllImport("shell32.dll", CharSet = CharSet.Unicode)] private static extern IntPtr SHGetFileInfo(string path, uint attributes, ref ShellFileInfo info, uint size, uint flags);
        [DllImport("user32.dll")] private static extern bool DestroyIcon(IntPtr icon);
    }
}
