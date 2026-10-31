using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using System.Windows;

namespace DesktopIniManager.Services;

internal static class DropOutService
{
    internal static bool MovedEnough(Point origin, Point current)
    {
        return Math.Abs(current.X - origin.X) >= SystemParameters.MinimumHorizontalDragDistance
               || Math.Abs(current.Y - origin.Y) >= SystemParameters.MinimumVerticalDragDistance;
    }

    internal static void DragExisting(DependencyObject source, IReadOnlyList<string> paths)
    {
        var existing = Existing(paths);
        if (existing.Length == 0 || source == null) return;
        DragDrop.DoDragDrop(source, new DataObject(DataFormats.FileDrop, existing), DragDropEffects.Copy);
    }

    internal static Task<string> StageFolderWithImmediateFilesAsync(string folderPath)
    {
        // Directory enumeration and file copies may hit a cold local disk or NAS.
        // Keep all staging I/O off the WPF UI thread.
        return Task.Run(() => StageFolderWithImmediateFiles(folderPath));
    }

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
            }
        }

        return result.ToArray();
    }
}