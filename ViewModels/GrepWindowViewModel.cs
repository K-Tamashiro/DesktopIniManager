using DesktopIniManager.Models;
using DesktopIniManager.Services;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Collections.Concurrent;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Data;
using System.Windows.Threading;
using DesktopIniManager.Properties;

namespace DesktopIniManager.ViewModels
{
    internal sealed class GrepWindowViewModel : ObservableObject
    {
        private readonly Func<IReadOnlyList<string>> _scopeProvider;
        private readonly ObservableCollection<GrepScopeItem> _scopes = new ObservableCollection<GrepScopeItem>();
        private readonly ObservableCollection<GrepMatch> _matches = new ObservableCollection<GrepMatch>();
        private CancellationTokenSource _searchCts;
        private ConcurrentQueue<GrepMatch> _pendingMatches = new ConcurrentQueue<GrepMatch>();
        private readonly DispatcherTimer _resultTimer;
        private string _query = string.Empty;
        public string Query
        {
            get => _query;
            set
            {
                if (SetProperty(ref _query, value))
                    ClearQueryCommand?.NotifyCanExecuteChanged();
            }
        }
        private string _extensions = string.Empty;
        public string Extensions { get => _extensions; set => SetProperty(ref _extensions, value); }
        private string _editorPath = string.Empty;
        public string EditorPath
        {
            get => _editorPath;
            set
            {
                if (SetProperty(ref _editorPath, value)) ApplyEditorPreset();
            }
        }
        private string _editorArguments = string.Empty;
        public string EditorArguments { get => _editorArguments; set => SetProperty(ref _editorArguments, value); }
        private bool? _useRegex = false;
        public bool? UseRegex { get => _useRegex; set => SetProperty(ref _useRegex, value); }
        private bool? _matchCase = false;
        public bool? MatchCase { get => _matchCase; set => SetProperty(ref _matchCase, value); }
        private bool? _wholeWord = false;
        public bool? WholeWord { get => _wholeWord; set => SetProperty(ref _wholeWord, value); }
        private string _status = Strings.Common_Ready;
        public string Status { get => _status; set => SetProperty(ref _status, value); }
        private string _scopeCountLabel = string.Empty;
        public string ScopeCountLabel { get => _scopeCountLabel; set => SetProperty(ref _scopeCountLabel, value); }
        private string _listFilter = string.Empty;
        public string ListFilter
        {
            get => _listFilter;
            set
            {
                if (SetProperty(ref _listFilter, value))
                    Results?.Refresh();
            }
        }
        private GrepMatch _selectedMatch = null;
        public GrepMatch SelectedMatch { get => _selectedMatch; set => SetProperty(ref _selectedMatch, value); }
        private bool _isSearching = false;
        public bool IsSearching { get => _isSearching; private set => SetProperty(ref _isSearching, value); }
        private bool _isCancelling = false;
        public bool IsCancelling { get => _isCancelling; private set => SetProperty(ref _isCancelling, value); }

        private readonly Dispatcher Dispatcher;
        private readonly IUserDialogService _dialogs;
        internal string DialogTitle { get; set; }
        public ObservableCollection<GrepScopeItem> Scopes => _scopes;
        public ObservableCollection<GrepMatch> Matches => _matches;
        public IEnumerable<LanguageProfile> Profiles => LanguageProfile.All;
        public bool CanEdit => !IsSearching;
        private LanguageProfile _selectedProfile;
        public LanguageProfile SelectedProfile
        {
            get => _selectedProfile;
            set
            {
                if (ReferenceEquals(_selectedProfile, value)) return;
                SearchHistoryRequested?.Invoke();
                if (_selectedProfile?.IsFree == true) SettingsService.SaveGrepFreeExtensions(Extensions.Trim());
                SetProperty(ref _selectedProfile, value);
                Extensions = value?.IsFree == true ? SettingsService.LoadGrepFreeExtensions() : value?.ExtensionText ?? string.Empty;
            }
        }
        public AsyncRelayCommand SearchCommand { get; }
        public RelayCommand CancelCommand { get; }
        public RelayCommand ClearQueryCommand { get; }
        public RelayCommand ClearResultsCommand { get; }
        public RelayCommand ReloadScopesCommand { get; }
        public RelayCommand OpenMatchCommand { get; }
        public RelayCommand BrowseEditorCommand { get; }
        public RelayCommand CloseCommand { get; }
        public RelayCommand ExpandResultGroupsCommand { get; }
        public RelayCommand CollapseResultGroupsCommand { get; }
        public RelayCommand OpenResultsInEditorCommand { get; }
        public RelayCommand SaveResultsCommand { get; }
        public RelayCommand ApplyEditorPresetCommand { get; }
        public event Action SaveResultsRequested;
        public ICollectionView Results { get; }
        public double[] ColumnWidths { get; private set; }
        public event Action BrowseEditorRequested;
        public event Action CloseRequested;
        public event Action<bool> ResultGroupsExpansionRequested;
        public event Action SearchHistoryRequested;
        public event Action ResultGroupsResetRequested;
        public event Action<GrepMatch> MatchScrollRequested;
        internal GrepWindowViewModel(Func<IReadOnlyList<string>> scopeProvider, Dispatcher dispatcher, IUserDialogService dialogs)
        {
            _scopeProvider = scopeProvider; Dispatcher = dispatcher; _dialogs = dialogs;
            SearchCommand = new AsyncRelayCommand(SearchAsync, ex => _dialogs.Show(ErrorMessages.English(ex), DialogTitle), () => !IsSearching);
            CancelCommand = new RelayCommand(Cancel, () => IsSearching && !IsCancelling);
            ClearQueryCommand = new RelayCommand(() => Query = string.Empty, () => !string.IsNullOrEmpty(Query));
            ClearResultsCommand = new RelayCommand(ClearResults, () => !IsSearching && _matches.Count > 0);
            ReloadScopesCommand = new RelayCommand(ReloadFromMainWindow, () => !IsSearching);
            OpenMatchCommand = new RelayCommand(OpenMatch);
            BrowseEditorCommand = new RelayCommand(() => BrowseEditorRequested?.Invoke());
            CloseCommand = new RelayCommand(() => CloseRequested?.Invoke());
            ExpandResultGroupsCommand = new RelayCommand(() => ResultGroupsExpansionRequested?.Invoke(true));
            CollapseResultGroupsCommand = new RelayCommand(() => ResultGroupsExpansionRequested?.Invoke(false));
            OpenResultsInEditorCommand = new RelayCommand(OpenResultsInEditor, () => VisibleMatchCount() > 0);
            SaveResultsCommand = new RelayCommand(() => SaveResultsRequested?.Invoke(), () => VisibleMatchCount() > 0);
            ApplyEditorPresetCommand = new RelayCommand(ApplyEditorPreset);
            Results = CollectionViewSource.GetDefaultView(_matches);
            Results.Filter = MatchListFilter;
            Results.GroupDescriptions.Add(new PropertyGroupDescription(nameof(GrepMatch.GroupPath)));
            _resultTimer = new DispatcherTimer(TimeSpan.FromMilliseconds(50), DispatcherPriority.Background,
                (sender, args) => DrainPendingMatches(20), Dispatcher);
            _resultTimer.Stop();
        }

        internal void Initialize(IReadOnlyList<string> initialScopes)
        {
            string savedProfile = SettingsService.LoadGrepProfile();
            SelectedProfile = LanguageProfile.All.FirstOrDefault(profile =>
                string.Equals(profile.Name, savedProfile, StringComparison.OrdinalIgnoreCase))
                ?? LanguageProfile.All.First(profile => !profile.IsFree);
            ColumnWidths = SettingsService.LoadGrepColumnWidths();
            if (SeedEditorPresets())
            {
                EditorPath = FirstEditorPreset.Executable;
                EditorArguments = FirstEditorPreset.Arguments;
                SettingsService.SaveEditor(EditorPath, EditorArguments);
            }
            else
            {
                EditorPath = SettingsService.LoadEditorPath();
                EditorArguments = SettingsService.LoadEditorArguments();
            }
            SetExplicitScopes(initialScopes);
        }

        internal void SaveColumnWidths(double[] widths)
        {
            if (widths.Length >= 4 && widths[0] >= 80 && widths[1] >= 120)
                SettingsService.SaveGrepColumnWidths(widths);
        }

        private void ApplyEditorPreset()
        {
            string arguments;
            if (TryApplyEditorPreset(EditorPath, out arguments)) EditorArguments = arguments;
        }
        internal void Close()
        {
            Cancel(); _resultTimer.Stop();
            SettingsService.SaveEditor(EditorPath.Trim(), EditorArguments);
            SettingsService.SaveGrepProfile(SelectedProfile?.Name);
            if (SelectedProfile?.IsFree == true) SettingsService.SaveGrepFreeExtensions(Extensions.Trim());
        }
        public void SetExplicitScopes(IReadOnlyList<string> scopes)
        {
            if (_searchCts != null) { Status = Strings.Grep_CancelBeforeChange; return; }
            SetScopes(scopes);
        }

        public void ReloadFromMainWindow()
        {
            if (_searchCts != null) { Status = Strings.Grep_CancelBeforeChange; return; }
            SetScopes(_scopeProvider());
        }

        private void SetScopes(IEnumerable<string> paths)
        {
            var previous = _scopes.ToDictionary(item => item.FolderPath, item => item.IsEnabled, StringComparer.OrdinalIgnoreCase);
            foreach (GrepScopeItem item in _scopes)
                item.PropertyChanged -= ScopeEnabledChanged;
            string[] normalized = NormalizeScopes(paths).ToArray();
            _scopes.Clear();
            foreach (string path in normalized)
            {
                bool enabled = !previous.TryGetValue(path, out bool was) || was;
                var item = new GrepScopeItem(path, enabled);
                item.PropertyChanged += ScopeEnabledChanged;
                _scopes.Add(item);
            }
            UpdateScopeCountLabel();
            Status = EnabledScopePaths().Length == 0 ? Strings.Grep_NoFoldersSelected : Strings.Common_Ready;
        }

        private void ScopeEnabledChanged(object sender, PropertyChangedEventArgs e)
        {
            if (e.PropertyName != nameof(GrepScopeItem.IsEnabled)) return;
            UpdateScopeCountLabel();
            if (!IsSearching)
                Status = EnabledScopePaths().Length == 0 ? Strings.Grep_NoFoldersSelected : Strings.Common_Ready;
        }

        private void UpdateScopeCountLabel()
        {
            int enabled = _scopes.Count(item => item.IsEnabled);
            int total = _scopes.Count;
            string format = total == 1 ? Strings.Grep_FolderSingular : Strings.Grep_FolderPlural;
            ScopeCountLabel = enabled == total
                ? string.Format(format, total)
                : string.Format(format, enabled) + " / " + total;
        }

        private string[] EnabledScopePaths()
        {
            return _scopes.Where(item => item.IsEnabled).Select(item => item.FolderPath).ToArray();
        }

        private void ClearResults()
        {
            ResultGroupsResetRequested?.Invoke();
            _matches.Clear();
            NotifyListCommands();
            Status = Strings.Common_Ready;
        }

        private bool MatchListFilter(object item)
        {
            var match = item as GrepMatch;
            if (match == null) return false;
            string filter = ListFilter ?? string.Empty;
            if (string.IsNullOrWhiteSpace(filter)) return true;
            string haystack = string.Join(" ", match.ScopeName, match.FileName, match.RelativePath, match.FilePath, match.GroupPath, match.LineText);
            var compare = System.Globalization.CultureInfo.CurrentCulture.CompareInfo;
            const System.Globalization.CompareOptions options =
                System.Globalization.CompareOptions.IgnoreCase
                | System.Globalization.CompareOptions.IgnoreWidth
                | System.Globalization.CompareOptions.IgnoreKanaType
                | System.Globalization.CompareOptions.IgnoreNonSpace;
            return filter.Split(new[] { ' ', '\t', '\u3000' }, StringSplitOptions.RemoveEmptyEntries)
                .All(token => compare.IndexOf(haystack, token, options) >= 0);
        }

        internal IEnumerable<GrepMatch> VisibleMatches()
        {
            return Results.Cast<GrepMatch>();
        }

        private int VisibleMatchCount()
        {
            return Results == null ? 0 : VisibleMatches().Count();
        }

        internal string FormatVisibleResults()
        {
            var builder = new System.Text.StringBuilder();
            string currentGroup = null;
            foreach (GrepMatch match in VisibleMatches())
            {
                if (!string.Equals(currentGroup, match.GroupPath, StringComparison.OrdinalIgnoreCase))
                {
                    if (builder.Length > 0) builder.AppendLine();
                    builder.AppendLine(match.FilePath);
                    currentGroup = match.GroupPath;
                }
                builder.Append("  ").Append(match.LineNumber).Append(':').Append(match.ColumnNumber)
                    .Append("  ").Append(match.LineText).AppendLine();
            }
            return builder.ToString();
        }

        private void OpenResultsInEditor()
        {
            string text = FormatVisibleResults();
            if (string.IsNullOrEmpty(text)) return;
            try { Clipboard.SetText(text); }
            catch { }
            try
            {
                string path = Path.Combine(Path.GetTempPath(), "DesktopIniManager-grep-results.txt");
                File.WriteAllText(path, text);
                string arguments = (EditorArguments ?? string.Empty)
                    .Replace("{file}", path).Replace("{line}", "1").Replace("{column}", "1");
                if (string.IsNullOrWhiteSpace(arguments)) arguments = "\"" + path + "\"";
                Process.Start(new ProcessStartInfo(ExpandEditorPath(EditorPath), arguments) { UseShellExecute = true });
            }
            catch (Exception ex)
            {
                _dialogs.Show(string.Format(Strings.Grep_EditorFailed, ErrorMessages.English(ex)), DialogTitle, MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        internal void SaveResultsTo(string path)
        {
            File.WriteAllText(path, FormatVisibleResults());
        }

        internal void RemoveGroup(string groupPath)
        {
            if (string.IsNullOrEmpty(groupPath)) return;
            for (int index = _matches.Count - 1; index >= 0; index--)
                if (string.Equals(_matches[index].GroupPath, groupPath, StringComparison.OrdinalIgnoreCase))
                    _matches.RemoveAt(index);
            NotifyListCommands();
        }

        internal string FilePathForGroup(string groupPath)
        {
            return _matches.FirstOrDefault(match => string.Equals(match.GroupPath, groupPath, StringComparison.OrdinalIgnoreCase))?.FilePath;
        }

        internal void CopyGroupPath(string groupPath)
        {
            string path = FilePathForGroup(groupPath);
            if (string.IsNullOrEmpty(path)) return;
            Clipboard.SetText(path);
        }

        internal void OpenGroupFolder(string groupPath)
        {
            string path = FilePathForGroup(groupPath);
            if (string.IsNullOrEmpty(path)) return;
            string folder = Path.GetDirectoryName(path);
            if (string.IsNullOrEmpty(folder) || !Directory.Exists(folder)) return;
            Process.Start(new ProcessStartInfo("explorer.exe", "\"" + folder + "\"") { UseShellExecute = true });
        }

        private void NotifyListCommands()
        {
            ClearResultsCommand?.NotifyCanExecuteChanged();
            OpenResultsInEditorCommand?.NotifyCanExecuteChanged();
            SaveResultsCommand?.NotifyCanExecuteChanged();
        }

        private static IEnumerable<string> NormalizeScopes(IEnumerable<string> paths)
        {
            var result = new List<string>();
            foreach (string path in (paths ?? Enumerable.Empty<string>()).Where(Directory.Exists)
                .Select(path => Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar)).Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(path => path.Length))
            {
                if (!result.Any(parent => IsAncestor(parent, path))) result.Add(path);
            }
            return result.OrderBy(path => path, StringComparer.CurrentCultureIgnoreCase);
        }

        private static bool IsAncestor(string parent, string child)
        {
            return child.StartsWith(parent.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
        }

        internal async Task SearchAsync()
        {
            if (_searchCts != null) return;
            SearchHistoryRequested?.Invoke();
            string query = Query;
            var profile = SelectedProfile as LanguageProfile;
            string[] scopes = EnabledScopePaths();
            if (_scopes.Count == 0) { _dialogs.Show(Strings.Grep_SelectFolders, DialogTitle); return; }
            if (scopes.Length == 0) { _dialogs.Show(Strings.Grep_NoFoldersSelected, DialogTitle); return; }
            if (string.IsNullOrEmpty(query)) { _dialogs.Show(Strings.Grep_EnterText, DialogTitle); return; }
            if (profile == null) return;
            string[] extensions = ParseExtensions(Extensions);
            if (extensions.Length == 0) { _dialogs.Show(Strings.Grep_EnterExtensions, DialogTitle); return; }
            if (profile.IsFree) SettingsService.SaveGrepFreeExtensions(Extensions.Trim());
            profile = new LanguageProfile(profile.Name, extensions);
            bool useRegex = UseRegex == true;
            bool matchCase = MatchCase == true;
            bool wholeWord = WholeWord == true;

            var cts = new CancellationTokenSource();
            _searchCts = cts;
            var pending = new ConcurrentQueue<GrepMatch>();
            _pendingMatches = pending;
            _resultTimer.Start();
            SetSearching(true);
            try
            {
                GrepSearchResult result = await Task.Run(() => new CodeGrepService().Search(scopes, profile, query,
                    useRegex, matchCase, wholeWord,
                    (done, total) => Dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(() =>
                    {
                        if (ReferenceEquals(_searchCts, cts) && !cts.IsCancellationRequested)
                            Status = string.Format(Strings.Grep_SearchingFiles, done.ToString("N0"), total.ToString("N0"));
                    })), cts.Token,
                    match => { if (!cts.IsCancellationRequested) pending.Enqueue(match); }));
                await DrainAllPendingMatchesAsync(cts.Token);
                cts.Token.ThrowIfCancellationRequested();
                Status = result.SkippedCount == 0 ? string.Format(Strings.Grep_Result, result.Matches.Count.ToString("N0"), result.FileCount.ToString("N0")) : string.Format(Strings.Grep_ResultSkipped, result.Matches.Count.ToString("N0"), result.FileCount.ToString("N0"), result.SkippedCount);
            }
            catch (OperationCanceledException) { _pendingMatches = new ConcurrentQueue<GrepMatch>(); Status = Strings.Grep_SearchCancelled; }
            catch (ArgumentException ex) { _dialogs.Show(string.Format(Strings.Grep_InvalidExpression, ErrorMessages.English(ex)), DialogTitle, MessageBoxButton.OK, MessageBoxImage.Warning); Status = Strings.Grep_InvalidExpressionStatus; }
            catch (Exception ex) { _dialogs.Show(ErrorMessages.English(ex), DialogTitle, MessageBoxButton.OK, MessageBoxImage.Error); Status = Strings.Grep_SearchFailed; }
            finally { _resultTimer.Stop(); if (ReferenceEquals(_searchCts, cts)) _searchCts = null; SetSearching(false); cts.Dispose(); }
        }

        private void DrainPendingMatches(int maximum)
        {
            int count = 0; GrepMatch match; GrepMatch last = null;
            while (count++ < maximum && _pendingMatches.TryDequeue(out match)) { _matches.Add(match); last = match; }
            if (last != null)
            {
                NotifyListCommands();
                MatchScrollRequested?.Invoke(last);
            }
        }

        private async Task DrainAllPendingMatchesAsync(CancellationToken token)
        {
            while (!_pendingMatches.IsEmpty)
            {
                token.ThrowIfCancellationRequested();
                DrainPendingMatches(30);
                await System.Windows.Threading.Dispatcher.Yield(DispatcherPriority.Background);
            }
        }

        private void SetSearching(bool value)
        {
            IsSearching = value; IsCancelling = false;
            OnPropertyChanged(nameof(CanEdit)); SearchCommand.NotifyCanExecuteChanged();
            CancelCommand.NotifyCanExecuteChanged(); ReloadScopesCommand.NotifyCanExecuteChanged();
            NotifyListCommands();
        }

        private static string[] ParseExtensions(string text)
        {
            return (text ?? string.Empty).Split(new[] { ' ', '\t', ',', ';' }, StringSplitOptions.RemoveEmptyEntries)
                .Select(value => value.Trim())
                .Where(value => value.Length > 0)
                .Select(value => value == "(none)" ? string.Empty : value.StartsWith(".", StringComparison.Ordinal) ? value : "." + value)
                .Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        }

        internal void Cancel()
        {
            if (_searchCts == null) return;
            _searchCts.Cancel();
            _resultTimer.Stop();
            _pendingMatches = new ConcurrentQueue<GrepMatch>();
            IsCancelling = true; CancelCommand.NotifyCanExecuteChanged();
            Status = Strings.Grep_Cancelling;
        }

        private void OpenMatch()
        {
            var match = SelectedMatch as GrepMatch;
            if (match == null) return;
            try
            {
                SettingsService.SaveEditor(EditorPath.Trim(), EditorArguments);
                string arguments = (EditorArguments ?? string.Empty)
                    .Replace("{file}", match.FilePath).Replace("{line}", match.LineNumber.ToString()).Replace("{column}", match.ColumnNumber.ToString());
                Process.Start(new ProcessStartInfo(ExpandEditorPath(EditorPath), arguments) { UseShellExecute = true });
            }
            catch (Exception ex) { _dialogs.Show(string.Format(Strings.Grep_EditorFailed, ErrorMessages.English(ex)), DialogTitle, MessageBoxButton.OK, MessageBoxImage.Error); }
        }

        internal static string ExpandEditorPath(string editor)
        {
            if (string.IsNullOrWhiteSpace(editor)) return editor;
            return Environment.ExpandEnvironmentVariables(editor.Trim().Trim('"'));
        }

        private static readonly EditorPreset[] EditorPresets =
        {
            new EditorPreset(@"C:\Program Files\MIFES11\MIW.exe", @"/+{line}@{column} ""{file}"""),
            new EditorPreset(@"C:\Program Files\Hidemaru\Hidemaru.exe", @"/j{line},{column} ""{file}"""),
            new EditorPreset(@"%LOCALAPPDATA%\Programs\Mery\Mery.exe", @"/l {line} /cl {column} ""{file}"""),
            new EditorPreset("code", @"--goto ""{file}:{line}:{column}""")
        };

        public EditorPreset FirstEditorPreset => EditorPresets[0];

        public bool SeedEditorPresets()
        {
            string settingsDirectory = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "DesktopIniManager");
            string markerPath = Path.Combine(settingsDirectory, "editor-presets-miw.txt");
            var store = new InputHistoryStore(Path.Combine(settingsDirectory, "input-history"));
            if (File.Exists(markerPath)) return false;
            store.Replace("Grep-Editor", EditorPresets.Select(preset => preset.Executable));
            store.Replace("Grep-EditorArguments", EditorPresets.Select(preset => preset.Arguments));
            try
            {
                Directory.CreateDirectory(settingsDirectory);
                File.WriteAllText(markerPath, "mifes");
            }
            catch { }
            return true;
        }

        public bool TryApplyEditorPreset(string editor, out string arguments)
        {
            arguments = null;
            EditorPreset preset = MatchEditorPreset(editor);
            if (preset == null) return false;
            arguments = preset.Arguments;
            return true;
        }

        private static EditorPreset MatchEditorPreset(string editor)
        {
            if (string.IsNullOrWhiteSpace(editor)) return null;
            string path = ExpandEditorPath(editor);
            string name = Path.GetFileName(path);
            foreach (EditorPreset preset in EditorPresets)
            {
                if (string.Equals(preset.Executable, path, StringComparison.OrdinalIgnoreCase)) return preset;
                if (!string.IsNullOrEmpty(name) && string.Equals(preset.FileName, name, StringComparison.OrdinalIgnoreCase)) return preset;
            }
            if (string.Equals(path, "code", StringComparison.OrdinalIgnoreCase))
                return EditorPresets[EditorPresets.Length - 1];
            if (string.Equals(name, "MIW.exe", StringComparison.OrdinalIgnoreCase)
                || string.Equals(name, "Mifes.exe", StringComparison.OrdinalIgnoreCase))
                return EditorPresets[0];
            return null;
        }

        public sealed class EditorPreset
        {
            internal EditorPreset(string executable, string arguments)
            {
                Executable = executable;
                Arguments = arguments;
            }
            public string Executable { get; }
            public string Arguments { get; }
            public string FileName => Path.GetFileName(Executable);
        }
    }

    internal sealed class GrepScopeItem : ObservableObject
    {
        public string FolderPath { get; }
        private bool isEnabled;
        public bool IsEnabled { get => isEnabled; set => SetProperty(ref isEnabled, value); }
        public GrepScopeItem(string path, bool enabled = true)
        {
            FolderPath = path;
            isEnabled = enabled;
        }
    }
}
