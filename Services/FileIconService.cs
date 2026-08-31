using System;
using System.Drawing;
using System.IO;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace DesktopIniManager.Services
{
    internal static class FileIconService
    {
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
