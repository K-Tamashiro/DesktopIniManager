using FastVolumeIndex;
using System;
using System.Diagnostics;
using System.Text;

namespace FastVolumeIndex.Cli
{
    internal static class Program
    {
        private static int Main(string[] args)
        {
            Console.OutputEncoding = new UTF8Encoding(false);
            string root; bool includeFiles;
            if (!TryParse(args, out root, out includeFiles)) { PrintUsage(); return 1; }
            try
            {
                Console.WriteLine("Reading NTFS MFT for " + root + " ...");
                NtfsVolumeIndex mft = NtfsVolumeIndex.Create(root);
                var timer = Stopwatch.StartNew();
                VolumePathIndex index = VolumePathIndex.Build(mft, root);
                timer.Stop();
                Console.WriteLine("Indexed " + mft.EntryCount.ToString("N0") + " MFT entries and built " +
                    index.Nodes.Count.ToString("N0") + " normalized paths in " +
                    (mft.EnumerationTime + timer.Elapsed).TotalMilliseconds.ToString("N0") + " ms.");
                Console.WriteLine(index.RootPath);
                PrintTree(index.Root, string.Empty, includeFiles);
                return 0;
            }
            catch (Exception ex) { Console.Error.WriteLine("MFT search failed: " + ex.Message); return 2; }
        }
        private static bool TryParse(string[] args, out string root, out bool includeFiles)
        {
            root = Environment.CurrentDirectory;
            includeFiles = false;
            if (args.Length == 0) return true;
            if (args.Length == 1)
            {
                if (IsHelp(args[0])) return false;
                if (IsFilesOption(args[0])) { includeFiles = true; return true; }
                root = args[0];
                return true;
            }
            if (args.Length == 2)
            {
                if (IsFilesOption(args[0])) { includeFiles = true; root = args[1]; return true; }
                if (IsFilesOption(args[1])) { includeFiles = true; root = args[0]; return true; }
            }
            return false;
        }

        private static bool IsFilesOption(string value) => string.Equals(value, "/f", StringComparison.OrdinalIgnoreCase);
        private static bool IsHelp(string value) => value == "/?" || value == "-h" || value == "--help";
        private static void PrintTree(VolumePathNode parent, string prefix, bool includeFiles)
        {
            int count = parent.Directories.Count + (includeFiles ? parent.Files.Count : 0); int position = 0;
            foreach (VolumePathNode child in parent.Directories)
            {
                bool last = ++position == count;
                Console.WriteLine(prefix + (last ? "└─ " : "├─ ") + child.Name);
                PrintTree(child, prefix + (last ? "   " : "│  "), includeFiles);
            }
            if (!includeFiles) return;
            foreach (VolumePathNode file in parent.Files)
            { bool last = ++position == count; Console.WriteLine(prefix + (last ? "└─ " : "├─ ") + file.Name); }
        }
        private static void PrintUsage()
        {
            Console.WriteLine("mftree - fast NTFS directory tree\n");
            Console.WriteLine("Usage:\n  mftree                    Show folders under the current directory\n  mftree /f                 Show folders and files under the current directory\n  mftree <folder-path>      Show folders under a path\n  mftree <folder-path> /f   Show folders and files under a path\n");
            Console.WriteLine("Run as administrator. Local NTFS volumes only.");
        }
    }
}
