using System.Linq;
namespace DesktopIniManager.Services
{
    internal static class DiffMedia
    {
        internal const string BinaryMessage = "Binary and cache files are not supported in Diff View. Only text files and supported images can be displayed.";
        public static bool IsBinary(string path)
        { return new[] { ".exe", ".dll", ".pdb", ".obj", ".lib", ".zip", ".7z", ".rar", ".gz", ".pdf", ".doc", ".docx", ".xls", ".xlsx", ".ppt", ".pptx", ".db", ".sqlite", ".mp3", ".mp4", ".wav", ".msi", ".bin", ".icl", ".resources", ".baml", ".cache" }.Contains(System.IO.Path.GetExtension(path).ToLowerInvariant()); }
        public static bool IsImage(string path)
        { return new[] { ".png", ".jpg", ".jpeg", ".bmp", ".gif", ".ico", ".tif", ".tiff", ".wdp", ".jxr" }.Contains(System.IO.Path.GetExtension(path).ToLowerInvariant()); }
    }

}
