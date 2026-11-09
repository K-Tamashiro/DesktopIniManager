using DesktopIniManager.Properties;
using System.Windows.Media.Imaging;

namespace DesktopIniManager.Models;

internal sealed class IconGroupResource
{
    public string ResourceName { get; set; }
    public ushort Language { get; set; }
    public int ShellIndex { get; set; }
    public int ImageCount { get; set; }
    public BitmapSource Preview { get; set; }
    public string DisplayName => string.Format(StringOverlay.Get("Icon_ResourceLabel"), ShellIndex, ResourceName);
    public string ImageCountText => string.Format(StringOverlay.Get("Icon_ImageCount"), ImageCount);
}