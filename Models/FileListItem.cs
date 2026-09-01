namespace DesktopIniManager.Models
{
    internal sealed class FileListItem
    {
        private System.Windows.Media.ImageSource _icon;

        public FileListItem(string path, string[] searchKeys = null)
        {
            Path = path;
            Name = System.IO.Path.GetFileName(path);
            Extension = System.IO.Path.GetExtension(path);
            IsSearchMatch = searchKeys != null && System.Array.Exists(searchKeys,
                key => Name.IndexOf(key, System.StringComparison.CurrentCultureIgnoreCase) >= 0);
        }

        public string Path { get; }
        public string Name { get; }
        public string Extension { get; }

        public System.Windows.Media.ImageSource Icon =>
            _icon ?? (_icon = Services.FileIconService.GetIcon(Path));

        public bool IsSearchMatch { get; }
    }
}
