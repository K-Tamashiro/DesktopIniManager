using System;
using System.IO;
using System.Linq;

namespace DesktopIniManager.Services
{
    internal sealed class ExternalDiffLaunch
    {
        internal const string Usage = "Usage: DesktopIniManager.exe -diff <Source> <Target>";
        internal string SourcePath { get; private set; }
        internal string TargetPath { get; private set; }

        internal static bool IsRequested(string[] args) => args != null &&
            args.Any(arg => string.Equals(arg, "-diff", StringComparison.OrdinalIgnoreCase));

        internal static ExternalDiffLaunch Parse(string[] args)
        {
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
                if (!File.Exists(path)) throw new FileNotFoundException("Comparison file does not exist.", path);
            return launch;
        }

        internal DiffSnapshot CreateSnapshot()
        {
            var snapshot = new DiffSnapshot
            {
                SourceRoot = Path.GetDirectoryName(SourcePath),
                TargetRoot = Path.GetDirectoryName(TargetPath)
            };
            snapshot.Files.Add(new DiffFile
            {
                RelativePath = Path.GetFileName(SourcePath),
                Source = DiffStamp.Read(SourcePath),
                Target = DiffStamp.Read(TargetPath)
            });
            return snapshot;
        }
    }
}
