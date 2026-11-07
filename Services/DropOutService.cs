using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using System.Windows;

namespace DesktopIniManager.Services;

internal static class DropOutService
{
    /// <summary>
    ///     ポインターの移動量がシステムのドラッグ開始距離に達したかどうかを判定します。
    /// </summary>
    /// <param name="origin">ドラッグ開始時の位置。</param>
    /// <param name="current">現在の位置。</param>
    /// <returns>ドラッグを開始すべき距離を移動した場合は true。</returns>
    internal static bool MovedEnough(Point origin, Point current)
    {
        return Math.Abs(current.X - origin.X) >= SystemParameters.MinimumHorizontalDragDistance
               || Math.Abs(current.Y - origin.Y) >= SystemParameters.MinimumVerticalDragDistance;
    }

    /// <summary>
    ///     存在するファイルとフォルダーだけを対象に、Explorer などへのコピー ドラッグを開始します。
    /// </summary>
    /// <param name="source">ドラッグ元の要素。</param>
    /// <param name="paths">ドラッグするパス。</param>
    internal static void DragExisting(DependencyObject source, IReadOnlyList<string> paths)
    {
        var existing = Existing(paths);
        if (existing.Length == 0 || source == null) return;
        DragDrop.DoDragDrop(source, new DataObject(DataFormats.FileDrop, existing), DragDropEffects.Copy);
    }

    /// <summary>
    ///     フォルダー直下のファイルを一時フォルダーへコピーする処理を、UI スレッド外で実行します。
    /// </summary>
    /// <param name="folderPath">コピー元のフォルダー。</param>
    /// <returns>作成した一時フォルダーのパスを返すタスク。</returns>
    internal static Task<string> StageFolderWithImmediateFilesAsync(string folderPath)
    {
        // Directory enumeration and file copies may hit a cold local disk or NAS.
        // Keep all staging I/O off the WPF UI thread.
        return Task.Run(() => StageFolderWithImmediateFiles(folderPath));
    }

    /// <summary>
    ///     同名の一時フォルダーを作成し、元フォルダー直下のファイルだけをコピーします。
    /// </summary>
    /// <param name="folderPath">コピー元のフォルダー。</param>
    /// <returns>作成した一時フォルダーのパス。元フォルダーが存在しない場合は null。</returns>
    internal static string StageFolderWithImmediateFiles(string folderPath)
    {
        if (string.IsNullOrWhiteSpace(folderPath) || !Directory.Exists(folderPath))
            return null;

        var name = Path.GetFileName(folderPath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
        if (string.IsNullOrWhiteSpace(name))
            name = "folder";

        var stage = Path.Combine(Path.GetTempPath(), "DesktopIniManager-dropout", Guid.NewGuid().ToString("N"), name);
        Directory.CreateDirectory(stage);

        try
        {
            foreach (var file in Directory.EnumerateFiles(folderPath))
            {
                var dest = Path.Combine(stage, Path.GetFileName(file));
                File.Copy(file, dest, true);
            }
        }
        catch (UnauthorizedAccessException)
        {
        }
        catch (IOException)
        {
        }

        return stage;
    }

    /// <summary>
    ///     指定された相対パスのフォルダーとファイルを、構造を保ったまま一時フォルダーへコピーします。
    /// </summary>
    /// <param name="root">コピー元のルート フォルダー。</param>
    /// <param name="relativeFiles">コピーするファイルの相対パス。</param>
    /// <param name="relativeFolders">作成するフォルダーの相対パス。</param>
    /// <returns>作成した一時フォルダーのパス。コピー対象がない場合は null。</returns>
    internal static string StageStructuredCopy(string root, IEnumerable<string> relativeFiles,
        IEnumerable<string> relativeFolders)
    {
        if (string.IsNullOrWhiteSpace(root) || !Directory.Exists(root))
            return null;

        var label = Path.GetFileName(root.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
        if (string.IsNullOrWhiteSpace(label))
            label = "copy";

        var stage = Path.Combine(Path.GetTempPath(), "DesktopIniManager-dropout", Guid.NewGuid().ToString("N"), label);
        Directory.CreateDirectory(stage);
        var any = false;

        if (relativeFolders != null)
            foreach (var relative in relativeFolders)
            {
                if (string.IsNullOrWhiteSpace(relative)) continue;
                Directory.CreateDirectory(Path.Combine(stage, relative));
                any = true;
            }

        if (relativeFiles != null)
            foreach (var relative in relativeFiles)
            {
                if (string.IsNullOrWhiteSpace(relative)) continue;
                var source = Path.Combine(root, relative);
                if (!File.Exists(source)) continue;
                var dest = Path.Combine(stage, relative);
                var destFolder = Path.GetDirectoryName(dest);
                if (!string.IsNullOrEmpty(destFolder))
                    Directory.CreateDirectory(destFolder);
                File.Copy(source, dest, true);
                any = true;
            }

        return any ? stage : null;
    }

    /// <summary>
    ///     パスを絶対パスに変換し、重複を除いて実在するものだけを抽出します。
    /// </summary>
    /// <param name="paths">対象のパス。</param>
    /// <returns>実在するパスの配列。</returns>
    private static string[] Existing(IReadOnlyList<string> paths)
    {
        if (paths == null || paths.Count == 0)
            return Array.Empty<string>();

        var result = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var path in paths)
        {
            if (string.IsNullOrWhiteSpace(path)) continue;
            try
            {
                var full = Path.GetFullPath(path);
                if (!seen.Add(full)) continue;
                if (File.Exists(full) || Directory.Exists(full))
                    result.Add(full);
            }
            catch
            {
                // Ignore invalid paths
            }
        }

        return result.ToArray();
    }
}