using DesktopIniManager.Models;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Xml.Serialization;

namespace DesktopIniManager.Services;

public sealed class FolderTreeState
{
    public string Root { get; set; }
    public int View { get; set; }
    public List<byte[]> Icons { get; set; } = new();
    public List<FolderTreeNodeState> Physical { get; set; } = new();
    public List<FolderTreeNodeState> Solution { get; set; } = new();
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
    public List<FolderTreeNodeState> Children { get; set; } = new();
}

internal static class FolderTreeStateService
{
    internal static string StatePath { get; set; } = Path.Combine(AppSlot.Directory, "folder-trees.xml");

    internal static List<FolderTreeNodeState> Capture(IEnumerable<FolderMatch> roots, FolderMatch current,
        List<byte[]> icons = null, Dictionary<ImageSource, int> iconIds = null)
    {
        if (icons != null && iconIds == null) iconIds = new Dictionary<ImageSource, int>();
        // Capture the underlying trees, never the filtered or search view.
        return roots.Select(node => new FolderTreeNodeState
        {
            Path = node.Path,
            DisplayName = node.DisplayName,
            Reason = node.Reason,
            Actionable = node.IsActionable,
            Expanded = node.IsExpanded,
            Hidden = node.IsHidden,
            Current = ReferenceEquals(node, current),
            Icon = CaptureIcon(node.IconPreview, icons, iconIds),
            Children = Capture(node.Children, current, icons, iconIds)
        }).ToList();
    }

    // C#
    private static int CaptureIcon(ImageSource image, List<byte[]> icons, Dictionary<ImageSource, int> ids)
    {
        if (icons == null || !(image is BitmapSource bitmap)) return -1;
        if (ids.TryGetValue(image, out var index)) return index;

        BitmapSource frozenCopy = null;
        var dispatcher = bitmap?.Dispatcher
                         ?? Application.Current?.Dispatcher;

        var createAndFreeze = () =>
        {
            var temp = new WriteableBitmap(bitmap);
            temp.Freeze();
            frozenCopy = temp;
        };

        if (dispatcher != null && !dispatcher.CheckAccess())
            dispatcher.Invoke(createAndFreeze);
        else
            createAndFreeze();

        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(frozenCopy));
        using (var stream = new MemoryStream())
        {
            encoder.Save(stream);
            var data = stream.ToArray();
            index = icons.Count;
            icons.Add(data);
            ids.Add(image, index);
            return index;
        }
    }

    internal static List<ImageSource> RestoreIcons(IEnumerable<byte[]> icons)
    {
        var result = new List<ImageSource>();
        foreach (var bytes in icons)
            using (var stream = new MemoryStream(bytes))
            {
                var image = BitmapFrame.Create(stream, BitmapCreateOptions.None, BitmapCacheOption.OnLoad);
                image.Freeze();
                result.Add(image);
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
                Path = saved.Path,
                DisplayName = saved.DisplayName,
                Reason = saved.Reason,
                IsActionable = saved.Actionable,
                IsExpanded = saved.Expanded,
                IsHidden = saved.Hidden,
                IsCurrent = saved.Current,
                Parent = parent,
                IconPreview = icons != null && saved.Icon >= 0 && saved.Icon < icons.Count
                    ? icons[saved.Icon]
                    : FolderIconService.GetDefaultFolderIcon()
            };
            foreach (var child in Restore(saved.Children, node, icons)) node.Children.Add(child);
            result.Add(node);
        }

        return result;
    }

    internal static void Clear()
    {
        try
        {
            if (File.Exists(StatePath)) File.Delete(StatePath);
        }
        catch
        {
        }
    }

    internal static FolderTreeState Load()
    {
        if (!File.Exists(StatePath)) return null;
        using (var stream = File.OpenRead(StatePath))
        {
            return (FolderTreeState)new XmlSerializer(typeof(FolderTreeState)).Deserialize(stream);
        }
    }

    internal static void Save(FolderTreeState state)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(StatePath));
        var temporary = StatePath + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            using (var stream = File.Create(temporary))
            {
                new XmlSerializer(typeof(FolderTreeState)).Serialize(stream, state);
            }

            if (File.Exists(StatePath)) File.Replace(temporary, StatePath, null);
            else File.Move(temporary, StatePath);
        }
        finally
        {
            if (File.Exists(temporary)) File.Delete(temporary);
        }
    }
}