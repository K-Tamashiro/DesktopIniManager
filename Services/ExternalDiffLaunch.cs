using DesktopIniManager.Properties;
using System;
using System.IO;
using System.Linq;

namespace DesktopIniManager.Services;

internal sealed class ExternalDiffLaunch
{
    internal static string Usage => StringOverlay.Get("App_DiffUsage");
    internal string SourcePath { get; private set; }
    internal string TargetPath { get; private set; }

    /// <summary>
    ///     コマンドライン引数に -diff が含まれているかどうかを判定します。
    /// </summary>
    /// <param name="args">コマンドライン引数。</param>
    /// <returns>外部 DIFF 起動が要求されている場合は true。</returns>
    internal static bool IsRequested(string[] args)
    {
        return args != null &&
               args.Any(arg => string.Equals(arg, "-diff", StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    ///     -diff [比較元 比較先] 形式の引数を解析し、比較するファイル パスを検証します。
    /// </summary>
    /// <param name="args">コマンドライン引数。</param>
    /// <returns>解析した起動情報。</returns>
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
        foreach (var path in new[] { launch.SourcePath, launch.TargetPath })
            if (Directory.Exists(path))
                throw new ArgumentException(StringOverlay.Get("Err_ComparisonPathsFiles")).Localized();
        if (!File.Exists(launch.SourcePath) && !File.Exists(launch.TargetPath))
            throw new FileNotFoundException(StringOverlay.Get("Err_ComparisonFilesMissing")).Localized();
        return launch;
    }

    /// <summary>
    ///     比較元と比較先のファイルを 1 件だけ含む DIFF スナップショットを作成します。
    /// </summary>
    /// <returns>作成した DIFF スナップショット。</returns>
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