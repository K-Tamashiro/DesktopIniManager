using System.Windows.Media.Imaging;

namespace DesktopIniManager.Models
{
    internal sealed class IconGroupResource
    {
        public string ResourceName { get; set; }
        public ushort Language { get; set; }
        public int ShellIndex { get; set; }
        public int ImageCount { get; set; }
        public BitmapSource Preview { get; set; }
        public string DisplayName => ShellIndex + "  (resource " + ResourceName + ")";
        public string ImageCountText => ImageCount + " images";
    }
}
