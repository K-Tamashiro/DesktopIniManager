using DesktopIniManager.Properties;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;

namespace DesktopIniManager.Services;

internal sealed class DesktopIniService
{
    /// <summary>
    ///     フォルダーに desktop.ini を UTF-16 で作成してアイコンを設定し、必要に応じて .gitignore への追加と Explorer への通知を行います。
    /// </summary>
    /// <param name="folder">アイコンを設定するフォルダー。</param>
    /// <param name="resourcePath">アイコン リソースを含むファイルのパス。</param>
    /// <param name="index">リソース内のアイコン インデックス。</param>
    /// <param name="addToGitIgnore">.gitignore に desktop.ini を追加する場合は true。</param>
    /// <param name="notifyExplorer">Explorer に変更を通知する場合は true。</param>
    public void Apply(string folder, string resourcePath, int index, bool addToGitIgnore, bool notifyExplorer = true)
    {
        if (!Directory.Exists(folder)) throw new DirectoryNotFoundException(folder);
        if (!File.Exists(resourcePath))
            throw new FileNotFoundException(StringOverlay.Get("Err_IconResourceMissing"), resourcePath).Localized();
        var ini = Path.Combine(folder, "desktop.ini");
        var content = "[.ShellClassInfo]\r\nIconResource=" + resourcePath + "," + index + "\r\n";

        // Hidden/System/ReadOnly desktop.ini files cannot always be overwritten.
        // Temporarily remove only the attributes that block an update.
        if (File.Exists(ini))
        {
            var existing = File.GetAttributes(ini);
            File.SetAttributes(ini,
                existing & ~(FileAttributes.Hidden | FileAttributes.System | FileAttributes.ReadOnly));
        }

        // The Windows shell does not reliably decode non-ASCII IconResource paths
        // from UTF-8 desktop.ini files. UTF-16 LE is the native desktop.ini format.
        File.WriteAllText(ini, content, Encoding.Unicode);
        File.SetAttributes(ini, File.GetAttributes(ini) | FileAttributes.Hidden | FileAttributes.System);
        // Explorer recognizes a folder as customized when either the read-only or
        // system bit is set. Set both so the behavior is consistent across Windows
        // versions and folder locations.
        File.SetAttributes(folder, File.GetAttributes(folder) | FileAttributes.ReadOnly | FileAttributes.System);
        if (addToGitIgnore) EnsureGitIgnore(folder);
        if (notifyExplorer) NotifyExplorer(new[] { folder });
    }

    /// <summary>
    ///     フォルダーの .gitignore に desktop.ini の除外ルールがなければ追記します。
    /// </summary>
    /// <param name="folder">.gitignore を持つフォルダー。</param>
    private static void EnsureGitIgnore(string folder)
    {
        var gitIgnore = Path.Combine(folder, ".gitignore");
        var content = File.Exists(gitIgnore) ? File.ReadAllText(gitIgnore) : string.Empty;
        var lines = content.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');
        foreach (var line in lines)
        {
            var rule = line.Trim();
            if (rule.StartsWith("#", StringComparison.Ordinal) ||
                rule.StartsWith("!", StringComparison.Ordinal)) continue;
            rule = rule.Replace('\\', '/').TrimStart('/');
            if (string.Equals(rule, "desktop.ini", StringComparison.OrdinalIgnoreCase)
                || rule.EndsWith("/desktop.ini", StringComparison.OrdinalIgnoreCase)) return;
        }

        var separator =
            content.Length == 0 || content.EndsWith("\n", StringComparison.Ordinal) ||
            content.EndsWith("\r", StringComparison.Ordinal)
                ? string.Empty
                : Environment.NewLine;
        File.AppendAllText(gitIgnore, separator + "desktop.ini" + Environment.NewLine, new UTF8Encoding(false));
    }

    /// <summary>
    ///     desktop.ini を削除してフォルダーの読み取り専用・システム属性を解除し、カスタム アイコンをリセットします。
    /// </summary>
    /// <param name="folder">対象のフォルダー。</param>
    /// <param name="notifyExplorer">Explorer に変更を通知する場合は true。</param>
    public void Remove(string folder, bool notifyExplorer = true)
    {
        if (!Directory.Exists(folder)) throw new DirectoryNotFoundException(folder);
        var ini = Path.Combine(folder, "desktop.ini");
        if (File.Exists(ini))
        {
            var attributes = File.GetAttributes(ini);
            File.SetAttributes(ini,
                attributes & ~(FileAttributes.Hidden | FileAttributes.System | FileAttributes.ReadOnly));
            File.Delete(ini);
        }

        var folderAttributes = File.GetAttributes(folder);
        File.SetAttributes(folder, folderAttributes & ~(FileAttributes.ReadOnly | FileAttributes.System));
        if (notifyExplorer) NotifyExplorer(new[] { folder });
    }

    /// <summary>
    ///     変更したフォルダーとその親フォルダーの更新を Explorer に通知し、アイコンを再読み込みさせます。
    /// </summary>
    /// <param name="folders">変更したフォルダー。</param>
    public void NotifyExplorer(IEnumerable<string> folders)
    {
        if (folders == null) return;

        const uint shcneUpdateItem = 0x00002000;
        const uint shcneUpdateDir = 0x00001000;
        const uint shcneAssocChanged = 0x08000000;
        const uint shcnfPathW = 0x0005;
        const uint shcnfFlush = 0x1000;
        const uint shcnfIdList = 0x0000;

        var changed = folders
            .Where(folder => !string.IsNullOrWhiteSpace(folder))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

        if (changed.Length == 0) return;

        foreach (var folder in changed)
            SHChangeNotify(shcneUpdateItem, shcnfPathW, folder, null);

        foreach (var parent in changed
                     .Select(folder =>
                         Path.GetDirectoryName(folder.TrimEnd(Path.DirectorySeparatorChar,
                             Path.AltDirectorySeparatorChar)))
                     .Where(parent => !string.IsNullOrEmpty(parent))
                     .Distinct(StringComparer.OrdinalIgnoreCase))
            SHChangeNotify(shcneUpdateDir, shcnfPathW, parent, null);

        // Flush once after all changed folders have been queued.
        SHChangeNotify(shcneAssocChanged, shcnfIdList | shcnfFlush, null, null);
    }

    /// <summary>
    ///     Win32 API の SHChangeNotify を呼び出し、シェルに変更を通知します。
    /// </summary>
    /// <param name="eventId">通知するイベントの種類。</param>
    /// <param name="flags">引数の形式とフラッシュの指定。</param>
    /// <param name="item1">1 つ目の対象項目。</param>
    /// <param name="item2">2 つ目の対象項目。</param>
    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern void SHChangeNotify(uint eventId, uint flags, string item1, string item2);
}