using System;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace DesktopIniManager.Services
{
    internal static class LanguageIconService
    {
        internal static ImageSource[] LoadFlagIcons()
        {
            var flags = new ImageSource[4];
            string path = PrepareFlagLibrary(FindFlagLibrary());
            if (path == null) return flags;
            for (int index = 0; index < flags.Length; index++)
            {
                flags[index] = ExtractFlagIcon(path, index);
                if (flags[index] == null) flags[index] = ExtractFlagIconFromResources(path, index);
            }
            return flags;
        }
        private static string FindFlagLibrary()
        {
            string baseDir = AppDomain.CurrentDomain.BaseDirectory;
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
        private static string PrepareFlagLibrary(string path)
        {
            if (string.IsNullOrEmpty(path) || !File.Exists(path)) return null;
            UnblockInternetZone(path);
            try
            {
                string cacheDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "DesktopIniManager");
                Directory.CreateDirectory(cacheDir);
                string cache = Path.Combine(cacheDir, "Flag.icl");
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
        private static void UnblockInternetZone(string path)
        {
            try { File.Delete(path + ":Zone.Identifier"); }
            catch { }
        }
        private static ImageSource ExtractFlagIcon(string path, int index)
        {
            var large = new IntPtr[1];
            var small = new IntPtr[1];
            ExtractIconEx(path, index, large, small, 1);
            IntPtr handle = large[0] != IntPtr.Zero ? large[0] : small[0];
            if (handle == IntPtr.Zero) return null;
            try
            {
                var bitmap = Imaging.CreateBitmapSourceFromHIcon(handle, Int32Rect.Empty, BitmapSizeOptions.FromWidthAndHeight(16, 16));
                bitmap.Freeze();
                return bitmap;
            }
            catch { return null; }
            finally
            {
                if (large[0] != IntPtr.Zero) DestroyIcon(large[0]);
                if (small[0] != IntPtr.Zero) DestroyIcon(small[0]);
            }
        }
        private static ImageSource ExtractFlagIconFromResources(string path, int index)
        {
            try
            {
                var group = IconResourceReader.Read(path).FirstOrDefault(item => item.ShellIndex == index);
                return group != null ? group.Preview : null;
            }
            catch { return null; }
        }
        [System.Runtime.InteropServices.DllImport("shell32.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode)]
        private static extern uint ExtractIconEx(string file, int index, IntPtr[] large, IntPtr[] small, uint count);

        [System.Runtime.InteropServices.DllImport("user32.dll")]
        private static extern bool DestroyIcon(IntPtr icon);

    }
}
