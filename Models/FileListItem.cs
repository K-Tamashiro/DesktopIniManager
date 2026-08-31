namespace DesktopIniManager.Models
{
    internal sealed class FileListItem
    {
        public FileListItem(string path, string[] searchKeys = null)
        {
            Path = path;
            Icon = Services.FileIconService.GetIcon(path);
            string name = System.IO.Path.GetFileName(path);
            IsSearchMatch = searchKeys != null && System.Array.Exists(searchKeys,
                key => name.IndexOf(key, System.StringComparison.CurrentCultureIgnoreCase) >= 0);
        }
        public string Path { get; }
        public string Name => System.IO.Path.GetFileName(Path);
        public string Extension => System.IO.Path.GetExtension(Path);
        public System.Windows.Media.ImageSource Icon { get; }
        public bool IsSearchMatch { get; }
    }
}
