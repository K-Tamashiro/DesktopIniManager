namespace DesktopIniManager.Models
{
    internal sealed class GrepMatch
    {
        public string ScopeName { get; set; }
        public string FilePath { get; set; }
        public string RelativePath { get; set; }
        public int LineNumber { get; set; }
        public int ColumnNumber { get; set; }
        public string LineText { get; set; }
    }
}
