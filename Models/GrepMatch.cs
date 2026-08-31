namespace DesktopIniManager.Models
{
    internal sealed class GrepMatch
    {
        public string ScopeName { get; set; }
        public string FilePath { get; set; }
        public string RelativePath { get; set; }
        public string FileName => System.IO.Path.GetFileName(FilePath);
        public string GroupPath => string.IsNullOrEmpty(ScopeName) ? RelativePath : ScopeName + " — " + RelativePath;
        public int LineNumber { get; set; }
        public int ColumnNumber { get; set; }
        public string LineText { get; set; }
    }
}
