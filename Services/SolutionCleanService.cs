using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;

namespace DesktopIniManager.Services
{
    internal static class SolutionCleanService
    {
        internal static bool ContainsRunningApplication(string solution, string applicationDirectory = null)
        {
            string root = Path.GetFullPath(Path.GetDirectoryName(solution)).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
            string application = Path.GetFullPath(applicationDirectory ?? AppDomain.CurrentDomain.BaseDirectory).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
            return application.StartsWith(root, StringComparison.OrdinalIgnoreCase);
        }

        internal static List<string> FindSolutions(params string[] roots)
        {
            var result = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (string input in roots)
            {
                string root = MftDifferencerService.Root(input);
                var pending = new Stack<string>(); pending.Push(root);
                while (pending.Count > 0)
                {
                    string folder = pending.Pop();
                    foreach (string file in Directory.EnumerateFiles(folder))
                        if ((Path.GetExtension(file).Equals(".sln", StringComparison.OrdinalIgnoreCase) || Path.GetExtension(file).Equals(".slnx", StringComparison.OrdinalIgnoreCase)) &&
                            (File.GetAttributes(file) & FileAttributes.ReparsePoint) == 0)
                            result.Add(file);
                    foreach (string child in Directory.EnumerateDirectories(folder))
                        if (!MftDifferencerService.Protected(child) && !new[] { "bin", "obj" }.Contains(Path.GetFileName(child), StringComparer.OrdinalIgnoreCase) &&
                            (File.GetAttributes(child) & FileAttributes.ReparsePoint) == 0)
                            pending.Push(child);
                }
            }
            return result.OrderBy(p => p, StringComparer.OrdinalIgnoreCase).ToList();
        }

        internal static string FindMSBuild()
        {
            string vswhere = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "Microsoft Visual Studio", "Installer", "vswhere.exe");
            if (!File.Exists(vswhere)) throw new IOException("Visual Studio Installer / vswhere.exe was not found. Install Visual Studio or Build Tools with MSBuild.");
            string output;
            int exit = Run(vswhere, "-latest -products * -requires Microsoft.Component.MSBuild -find MSBuild\\**\\Bin\\MSBuild.exe", Path.GetDirectoryName(vswhere), out output);
            string path = output.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries).FirstOrDefault(File.Exists);
            if (exit != 0 || path == null) throw new IOException("MSBuild was not found. Install Visual Studio or Build Tools with MSBuild.");
            return path;
        }

        internal static int Clean(string msbuild, string solution, string configuration, out string output)
        {
            if (ContainsRunningApplication(solution))
                throw new IOException("Cannot clean the solution containing the running DIM application. Start DIM from a separate release folder outside this solution, then clean again.");
            if (string.IsNullOrWhiteSpace(configuration) || configuration.Any(c => !char.IsLetterOrDigit(c) && c != ' ' && c != '_' && c != '-'))
                throw new ArgumentException("Use configuration names separated by semicolons (for example Debug;Release).");
            MftDifferencerService.SafePath(Path.GetDirectoryName(solution) + Path.DirectorySeparatorChar, Path.GetFileName(solution));
            return Run(msbuild, "\"" + solution + "\" /t:Clean /p:Configuration=\"" + configuration + "\" /nologo /v:minimal /nr:false", Path.GetDirectoryName(solution), out output);
        }

        private static int Run(string executable, string arguments, string directory, out string output)
        {
            var log = new StringBuilder();
            using (var process = new Process { StartInfo = new ProcessStartInfo(executable, arguments) {
                WorkingDirectory = directory, UseShellExecute = false, CreateNoWindow = true,
                RedirectStandardOutput = true, RedirectStandardError = true } })
            {
                DataReceivedEventHandler append = (s, e) => { if (e.Data != null) lock (log) log.AppendLine(e.Data); };
                process.OutputDataReceived += append; process.ErrorDataReceived += append;
                process.Start(); process.BeginOutputReadLine(); process.BeginErrorReadLine(); process.WaitForExit();
                output = log.ToString(); return process.ExitCode;
            }
        }
    }
}
