using DesktopIniManager.Models;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Xml.Serialization;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace DesktopIniManager.Services
{
    public sealed class FolderTreeState
    {
        public string Root { get; set; }
        public int View { get; set; }
        public List<byte[]> Icons { get; set; } = new List<byte[]>();
        public List<FolderTreeNodeState> Physical { get; set; } = new List<FolderTreeNodeState>();
        public List<FolderTreeNodeState> Solution { get; set; } = new List<FolderTreeNodeState>();
    }

    public sealed class FolderTreeNodeState
    {
        public string Path { get; set; }
        public string DisplayName { get; set; }
        public string Reason { get; set; }
        public bool Actionable { get; set; }
        public bool Expanded { get; set; }
        public bool Hidden { get; set; }
        public bool Current { get; set; }
        public int Icon { get; set; } = -1;
        public List<FolderTreeNodeState> Children { get; set; } = new List<FolderTreeNodeState>();
    }

    internal static class FolderTreeStateService
    {
        internal static string StatePath = System.IO.Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "DesktopIniManager", "folder-trees.xml");

        internal static List<FolderTreeNodeState> Capture(IEnumerable<FolderMatch> roots, FolderMatch current,
            List<byte[]> icons = null, Dictionary<ImageSource, int> iconIds = null)
        {
            if (icons != null && iconIds == null) iconIds = new Dictionary<ImageSource, int>();
            // Capture the underlying trees, never the filtered or search view.
            return roots.Select(node => new FolderTreeNodeState
            {
                Path = node.Path, DisplayName = node.DisplayName, Reason = node.Reason,
                Actionable = node.IsActionable, Expanded = node.IsExpanded, Hidden = node.IsHidden,
                Current = ReferenceEquals(node, current), Icon = CaptureIcon(node.IconPreview, icons, iconIds),
                Children = Capture(node.Children, current, icons, iconIds)
            }).ToList();
        }

        private static int CaptureIcon(ImageSource image, List<byte[]> icons, Dictionary<ImageSource, int> ids)
        {
            if (icons == null || !(image is BitmapSource bitmap)) return -1;
            int index;
            if (ids.TryGetValue(image, out index)) return index;
            var encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(bitmap));
            using (var stream = new MemoryStream())
            {
                encoder.Save(stream);
                index = icons.Count; icons.Add(stream.ToArray()); ids.Add(image, index);
                return index;
            }
        }

        internal static List<ImageSource> RestoreIcons(IEnumerable<byte[]> icons)
        {
            var result = new List<ImageSource>();
            foreach (var bytes in icons)
            {
                using (var stream = new MemoryStream(bytes))
                {
                    var image = BitmapFrame.Create(stream, BitmapCreateOptions.None, BitmapCacheOption.OnLoad);
                    image.Freeze(); result.Add(image);
                }
            }
            return result;
        }

        internal static List<FolderMatch> Restore(IEnumerable<FolderTreeNodeState> nodes, FolderMatch parent = null,
            List<ImageSource> icons = null)
        {
            var result = new List<FolderMatch>();
            foreach (var saved in nodes)
            {
                var node = new FolderMatch
                {
                    Path = saved.Path, DisplayName = saved.DisplayName, Reason = saved.Reason,
                    IsActionable = saved.Actionable, IsExpanded = saved.Expanded, IsHidden = saved.Hidden,
                    IsCurrent = saved.Current, Parent = parent,
                    IconPreview = icons != null && saved.Icon >= 0 && saved.Icon < icons.Count
                        ? icons[saved.Icon] : FolderIconService.GetDefaultFolderIcon()
                };
                foreach (var child in Restore(saved.Children, node, icons)) node.Children.Add(child);
                result.Add(node);
            }
            return result;
        }

        internal static FolderTreeState Load()
        {
            if (!File.Exists(StatePath)) return null;
            using (var stream = File.OpenRead(StatePath))
                return (FolderTreeState)new XmlSerializer(typeof(FolderTreeState)).Deserialize(stream);
        }

        internal static void Save(FolderTreeState state)
        {
            Directory.CreateDirectory(System.IO.Path.GetDirectoryName(StatePath));
            string temporary = StatePath + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                using (var stream = File.Create(temporary))
                    new XmlSerializer(typeof(FolderTreeState)).Serialize(stream, state);
                if (File.Exists(StatePath)) File.Replace(temporary, StatePath, null);
                else File.Move(temporary, StatePath);
            }
            finally { if (File.Exists(temporary)) File.Delete(temporary); }
        }
    }
}
