using System;
using System.IO;
using System.Linq;

namespace DesktopIniManager.Services;

internal sealed class ExternalDiffLaunch
{
    internal const string Usage = "Usage: DesktopIniManager.exe -diff [<Source> <Target>]";
    internal string SourcePath { get; private set; }
    internal string TargetPath { get; private set; }

    internal static bool IsRequested(string[] args) => args != null &&
        args.Any(arg => string.Equals(arg, "-diff", StringComparison.OrdinalIgnoreCase));

    internal static ExternalDiffLaunch Parse(string[] args)
    {
        if (args?.Length == 1 && string.Equals(args[0], "-diff", StringComparison.OrdinalIgnoreCase))
            return new ExternalDiffLaunch { SourcePath = "", TargetPath = "" };
        if (args == null || args.Length != 3 ||
            !string.Equals(args[0], "-diff", StringComparison.OrdinalIgnoreCase) ||
            string.IsNullOrWhiteSpace(args[1]) || string.IsNullOrWhiteSpace(args[2]))
            throw new ArgumentException(Usage);
        var launch = new ExternalDiffLaunch
        {
            SourcePath = Path.GetFullPath(args[1]),
            TargetPath = Path.GetFullPath(args[2])
        };
        foreach (string path in new[] { launch.SourcePath, launch.TargetPath })
            if (Directory.Exists(path)) throw new ArgumentException("Comparison paths must be files.");
        if (!File.Exists(launch.SourcePath) && !File.Exists(launch.TargetPath))
            throw new FileNotFoundException("Neither comparison file exists.");
        return launch;
    }

    internal DiffSnapshot CreateSnapshot()
    {
        var snapshot = new DiffSnapshot
        {
            SourceRoot = string.IsNullOrEmpty(SourcePath) ? "" : Path.GetDirectoryName(SourcePath),
            TargetRoot = string.IsNullOrEmpty(TargetPath) ? "" : Path.GetDirectoryName(TargetPath)
        };
        snapshot.Files.Add(new DiffFile
        {
            RelativePath = Path.GetFileName(File.Exists(SourcePath) ? SourcePath : TargetPath),
            Source = DiffStamp.Read(SourcePath),
            Target = DiffStamp.Read(TargetPath)
        });
        return snapshot;
    }
}
