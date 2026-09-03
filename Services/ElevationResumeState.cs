using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Xml.Serialization;

namespace DesktopIniManager.Services
{
    public sealed class ElevationResumeState
    {
        public string TargetWindow { get; set; }
        public string MainAction { get; set; }
        public string MainRoot { get; set; }
        public string MainQuery { get; set; }
        public string Source { get; set; }
        public string Target { get; set; }
        public bool CompareDates { get; set; } = true;
        public List<string> GrepScopes { get; set; } = new List<string>();
        public string GrepQuery { get; set; }
        public string GrepProfile { get; set; }
        public string GrepExtensions { get; set; }
        public bool Regex { get; set; }
        public bool MatchCase { get; set; }
        public bool WholeWord { get; set; }
        private static string DirectoryPath => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "DesktopIniManager");
        internal string Save()
        {
            Directory.CreateDirectory(DirectoryPath);
            string path = Path.Combine(DirectoryPath, "elevation-" + Guid.NewGuid().ToString("N") + ".xml");
            using (var stream = File.Create(path)) new XmlSerializer(typeof(ElevationResumeState)).Serialize(stream, this);
            return path;
        }
        internal static ElevationResumeState Load(string[] arguments)
        {
            int index = Array.IndexOf(arguments, "--resume-session");
            if (index < 0 || index + 1 >= arguments.Length) return null;
            string path = Path.GetFullPath(arguments[index + 1]);
            if (!string.Equals(Path.GetDirectoryName(path), DirectoryPath, StringComparison.OrdinalIgnoreCase)
                || !Path.GetFileName(path).StartsWith("elevation-", StringComparison.Ordinal) || Path.GetExtension(path) != ".xml")
                throw new IOException("Invalid elevation session path.");
            try
            {
                using (var stream = File.OpenRead(path)) return (ElevationResumeState)new XmlSerializer(typeof(ElevationResumeState)).Deserialize(stream);
            }
            finally { File.Delete(path); }
        }
    }
}
