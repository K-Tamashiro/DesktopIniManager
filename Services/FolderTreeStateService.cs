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

    /// <summary>
    ///     フォルダー ツリーを、検索やフィルターの影響を受けない保存用の状態に再帰的に変換します。
    /// </summary>
    /// <param name="roots">変換するルート ノード。</param>
    /// <param name="current">現在選択中のノード。</param>
    /// <param name="icons">アイコン画像の保存先。null の場合はアイコンを保存しません。</param>
    /// <param name="iconIds">保存済みアイコンとインデックスの対応表。</param>
    /// <returns>保存用のノード状態の一覧。</returns>
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
    /// <summary>
    ///     アイコン画像を PNG に変換して保存先に追加し、そのインデックスを返します。保存済みの画像は再利用します。
    /// </summary>
    /// <param name="image">保存するアイコン画像。</param>
    /// <param name="icons">PNG データの保存先。</param>
    /// <param name="ids">保存済みアイコンとインデックスの対応表。</param>
    /// <returns>アイコンのインデックス。保存しない場合は -1。</returns>
    private static int CaptureIcon(ImageSource image, List<byte[]> icons, Dictionary<ImageSource, int> ids)
    {
        if (icons == null || !(image is BitmapSource bitmap)) return -1;
        if (ids.TryGetValue(image, out var index)) return index;

        BitmapSource frozenCopy = null;
        var dispatcher = bitmap.Dispatcher
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

    /// <summary>
    ///     保存された PNG データからフリーズ済みのアイコン画像を復元します。
    /// </summary>
    /// <param name="icons">PNG データの一覧。</param>
    /// <returns>復元したアイコン画像の一覧。</returns>
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

    /// <summary>
    ///     保存されたノード状態からフォルダー ツリーを再帰的に復元します。
    /// </summary>
    /// <param name="nodes">復元するノード状態。</param>
    /// <param name="parent">復元したノードの親。</param>
    /// <param name="icons">復元済みのアイコン画像。</param>
    /// <returns>復元したノードの一覧。</returns>
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

    /// <summary>
    ///     保存されたフォルダー ツリーの状態ファイルを削除します。
    /// </summary>
    internal static void Clear()
    {
        try
        {
            if (File.Exists(StatePath)) File.Delete(StatePath);
        }
        catch
        {
            // Ignore errors when trying to delete the state file
        }
    }

    /// <summary>
    ///     状態ファイルからフォルダー ツリーの状態を読み込みます。
    /// </summary>
    /// <returns>読み込んだ状態。ファイルがない場合は null。</returns>
    internal static FolderTreeState Load()
    {
        if (!File.Exists(StatePath)) return null;
        using (var stream = File.OpenRead(StatePath))
        {
            return (FolderTreeState)new XmlSerializer(typeof(FolderTreeState)).Deserialize(stream);
        }
    }

    /// <summary>
    ///     フォルダー ツリーの状態を一時ファイルに書き込んでから置き換え、状態ファイルを安全に保存します。
    /// </summary>
    /// <param name="state">保存する状態。</param>
    internal static void Save(FolderTreeState state)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(StatePath))
                                  ?? throw new IOException("The tree state path has no parent directory."));
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