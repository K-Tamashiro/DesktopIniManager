using System.Linq;
namespace DesktopIniManager.Services;

internal static class DiffMedia
{
    internal const string BinaryMessage = "Binary and cache files are not supported in Diff View. Only text files, supported images, and videos can be displayed.";
    public static bool IsBinary(string path)
    {
        string ext = System.IO.Path.GetExtension(path).ToLowerInvariant();
        // Videos are handled separately; do not treat them as unsupported binary.
        if (IsVideo(path)) return false;
        return new[] { ".exe", ".dll", ".pdb", ".obj", ".lib", ".zip", ".7z", ".rar", ".gz", ".pdf", ".doc", ".docx", ".xls", ".xlsx", ".ppt", ".pptx", ".db", ".sqlite", ".mp3", ".wav", ".msi", ".bin", ".icl", ".resources", ".baml", ".cache" }.Contains(ext);
    }
    public static bool IsImage(string path)
    { return new[] { ".png", ".jpg", ".jpeg", ".bmp", ".gif", ".ico", ".tif", ".tiff", ".wdp", ".jxr" }.Contains(System.IO.Path.GetExtension(path).ToLowerInvariant()); }
    public static bool IsVideo(string path)
    { return new[] { ".mp4", ".avi", ".mkv", ".mov", ".wmv", ".webm", ".m4v", ".mpg", ".mpeg" }.Contains(System.IO.Path.GetExtension(path).ToLowerInvariant()); }
}