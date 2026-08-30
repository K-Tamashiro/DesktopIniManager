using System; using System.ComponentModel; using System.IO; using System.Runtime.InteropServices; using System.Text;
namespace DesktopIniManager.Services
{
    internal sealed class DesktopIniService
    {
        public void Apply(string folder, string resourcePath, int index, bool addToGitIgnore)
        {
            if (!Directory.Exists(folder)) throw new DirectoryNotFoundException(folder);
            if (!File.Exists(resourcePath)) throw new FileNotFoundException("The icon resource was not found.", resourcePath);
            string ini = Path.Combine(folder, "desktop.ini");
            string content = "[.ShellClassInfo]\r\nIconResource=" + resourcePath + "," + index + "\r\n";

            // Hidden/System/ReadOnly desktop.ini files cannot always be overwritten.
            // Temporarily remove only the attributes that block an update.
            if (File.Exists(ini))
            {
                FileAttributes existing = File.GetAttributes(ini);
                File.SetAttributes(ini, existing & ~(FileAttributes.Hidden | FileAttributes.System | FileAttributes.ReadOnly));
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
            NotifyExplorer(folder);
        }

        private static void EnsureGitIgnore(string folder)
        {
            string gitIgnore = Path.Combine(folder, ".gitignore");
            string content = File.Exists(gitIgnore) ? File.ReadAllText(gitIgnore) : string.Empty;
            string[] lines = content.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');
            foreach (string line in lines)
            {
                string rule = line.Trim();
                if (rule.StartsWith("#", StringComparison.Ordinal) || rule.StartsWith("!", StringComparison.Ordinal)) continue;
                rule = rule.Replace('\\', '/').TrimStart('/');
                if (string.Equals(rule, "desktop.ini", StringComparison.OrdinalIgnoreCase)
                    || rule.EndsWith("/desktop.ini", StringComparison.OrdinalIgnoreCase)) return;
            }

            string separator = content.Length == 0 || content.EndsWith("\n", StringComparison.Ordinal) || content.EndsWith("\r", StringComparison.Ordinal) ? string.Empty : Environment.NewLine;
            File.AppendAllText(gitIgnore, separator + "desktop.ini" + Environment.NewLine, new UTF8Encoding(false));
        }

        public void Remove(string folder)
        {
            if (!Directory.Exists(folder)) throw new DirectoryNotFoundException(folder);
            string ini = Path.Combine(folder, "desktop.ini");
            if (File.Exists(ini))
            {
                FileAttributes attributes = File.GetAttributes(ini);
                File.SetAttributes(ini, attributes & ~(FileAttributes.Hidden | FileAttributes.System | FileAttributes.ReadOnly));
                File.Delete(ini);
            }
            FileAttributes folderAttributes = File.GetAttributes(folder);
            File.SetAttributes(folder, folderAttributes & ~(FileAttributes.ReadOnly | FileAttributes.System));
            NotifyExplorer(folder);
        }

        private static void NotifyExplorer(string folder)
        {
            const uint ShcneUpdateItem = 0x00002000;
            const uint ShcneUpdateDir = 0x00001000;
            const uint ShcneAssocChanged = 0x08000000;
            const uint ShcnfPathW = 0x0005;
            const uint ShcnfFlush = 0x1000;
            const uint ShcnfIdList = 0x0000;

            SHChangeNotify(ShcneUpdateItem, ShcnfPathW | ShcnfFlush, folder, null);
            string parent = Path.GetDirectoryName(folder.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
            if (!string.IsNullOrEmpty(parent))
                SHChangeNotify(ShcneUpdateDir, ShcnfPathW | ShcnfFlush, parent, null);
            SHChangeNotify(ShcneAssocChanged, ShcnfIdList | ShcnfFlush, null, null);
        }

        [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
        private static extern void SHChangeNotify(uint eventId, uint flags, string item1, string item2);
    }
}
