using System;
using System.Drawing;
using System.IO;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace DesktopIniManager.Services
{
    internal static class FileIconService
    {
        private static readonly Dictionary<string, ImageSource> TypeIcons = new Dictionary<string, ImageSource>(StringComparer.OrdinalIgnoreCase);
        // A comparison lists many directories at once. A type icon must not open each file.
        public static ImageSource GetTypeIcon(string extension)
        {
            lock (TypeIcons)
            {
                ImageSource cached;
                if (TypeIcons.TryGetValue(extension ?? "", out cached)) return cached;
                var info = new ShellFileInfo();
                SHGetFileInfo("file" + extension, 0x80, ref info, (uint)Marshal.SizeOf(typeof(ShellFileInfo)), 0x100 | 0x1 | 0x10);
                if (info.Icon != IntPtr.Zero)
                {
                    try { var bitmap = Imaging.CreateBitmapSourceFromHIcon(info.Icon, Int32Rect.Empty, BitmapSizeOptions.FromWidthAndHeight(24, 24)); bitmap.Freeze(); cached = bitmap; }
                    finally { DestroyIcon(info.Icon); }
                }
                if (TypeIcons.Count >= 512) TypeIcons.Clear();
                TypeIcons[extension ?? ""] = cached;
                return cached;
            }
        }
        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct ShellFileInfo
        {
            public IntPtr Icon;
            public int Index;
            public uint Attributes;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)] public string DisplayName;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 80)] public string TypeName;
        }
        [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
        private static extern IntPtr SHGetFileInfo(string path, uint attributes, ref ShellFileInfo info, uint size, uint flags);
        [DllImport("user32.dll")]
        private static extern bool DestroyIcon(IntPtr icon);
        private static readonly string[] ImageExtensions =
        { ".png", ".jpg", ".jpeg", ".bmp", ".gif", ".tif", ".tiff", ".ico" };

        public static ImageSource GetIcon(string path)
        {
            string extension = Path.GetExtension(path);
            if (Array.Exists(ImageExtensions, item => string.Equals(item, extension, StringComparison.OrdinalIgnoreCase)))
            {
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
                catch { }
            }
            try
            {
                using (Icon icon = Icon.ExtractAssociatedIcon(path))
                {
                    if (icon == null) return null;
                    BitmapSource source = Imaging.CreateBitmapSourceFromHIcon(icon.Handle, Int32Rect.Empty,
                        BitmapSizeOptions.FromWidthAndHeight(32, 32));
                    source.Freeze();
                    return source;
                }
            }
            catch { return null; }
        }
    }
}
