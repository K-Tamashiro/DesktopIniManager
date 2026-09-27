using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using DesktopIniManager.Models;
using DesktopIniManager.Services;

namespace DesktopIniManager.ViewModels
{
    internal static class ResultHistoryStore
    {
        internal const int Limit = 20;
        internal static string DirectoryPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "DesktopIniManager", "result-history");
        private static readonly JsonSerializerOptions Options = new JsonSerializerOptions { IncludeFields = true, IgnoreReadOnlyProperties = true };
        internal static T Load<T>(string name) where T : new()
        {
            string path = Path.Combine(DirectoryPath, name + ".json");
            return File.Exists(path) ? JsonSerializer.Deserialize<T>(File.ReadAllText(path), Options) ?? new T() : new T();
        }
        internal static void Save<T>(string name, T value)
        {
            Directory.CreateDirectory(DirectoryPath);
            string path = Path.Combine(DirectoryPath, name + ".json");
            string temp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                File.WriteAllText(temp, JsonSerializer.Serialize(value, Options));
                File.Move(temp, path, true);
            }
            finally { if (File.Exists(temp)) File.Delete(temp); }
        }
    }

    internal sealed class GrepHistoryTab : ObservableObject
    {
        public GrepHistoryTab() { }
        private string title = Properties.StringOverlay.Get("History_NewSearch");
        public string Title { get => title; set => SetProperty(ref title, value); }
        public string Query { get; set; } = "";
        public string Profile { get; set; }
        public string Extensions { get; set; } = "";
        public bool UseRegex { get; set; }
        public bool MatchCase { get; set; }
        public bool WholeWord { get; set; }
        public string Filter { get; set; } = "";
        public string Status { get; set; } = "Ready";
        public List<string> Scopes { get; set; } = new List<string>();
        public List<string> EnabledScopes { get; set; } = new List<string>();
        public List<GrepMatch> Matches { get; set; } = new List<GrepMatch>();
    }

    internal sealed class ComparisonHistoryTab
    {
        public ComparisonHistoryTab() { }
        public string Title { get; set; }
        public DateTime CreatedAt { get; set; }
        public DiffSnapshot Snapshot { get; set; }
        public string SelectedFolder { get; set; } = "";
        public List<string> Expanded { get; set; } = new List<string>();
        public DiffKind KindMask { get; set; } = DiffKind.Differences;
        public bool ShowObj { get; set; }
        public bool ShowBin { get; set; }
        public string Status { get; set; }
        public List<HistoryFolderState> FolderStates { get; set; } = new List<HistoryFolderState>();
        public string Roots => "Source: " + Snapshot?.SourceRoot + "   Target: " + Snapshot?.TargetRoot;
    }
    internal sealed class HistoryFolderState
    {
        public HistoryFolderState() { }
        public string Path { get; set; }
        public bool SourceExists { get; set; }
        public bool TargetExists { get; set; }
        public bool SourceEmpty { get; set; }
        public bool TargetEmpty { get; set; }
    }
    internal sealed class HistoryFile<T>
    {
        public HistoryFile() { }
        public List<T> Tabs { get; set; } = new List<T>();
        public int SelectedIndex { get; set; }
    }
}
