using DesktopIniManager.Models;
using DesktopIniManager.Services;
using Microsoft.Win32;
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
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Data;
using System.Windows.Threading;
using DesktopIniManager.Properties;

namespace DesktopIniManager.Views
{
    public partial class GrepWindow : Window
    {
        private readonly Func<IReadOnlyList<string>> _scopeProvider;
        private readonly ObservableCollection<string> _scopes = new ObservableCollection<string>();
        private readonly ObservableCollection<GrepMatch> _matches = new ObservableCollection<GrepMatch>();
        private CancellationTokenSource _searchCts;
        private ConcurrentQueue<GrepMatch> _pendingMatches = new ConcurrentQueue<GrepMatch>();
        private readonly DispatcherTimer _resultTimer;

        public GrepWindow(Func<IReadOnlyList<string>> scopeProvider, IReadOnlyList<string> initialScopes)
        {
            _scopeProvider = scopeProvider;
            InitializeComponent();
            AttachElevationToggle();
            ScopeList.ItemsSource = _scopes;
            ICollectionView resultView = CollectionViewSource.GetDefaultView(_matches);
            resultView.GroupDescriptions.Add(new PropertyGroupDescription(nameof(GrepMatch.GroupPath)));
            ResultsGrid.ItemsSource = resultView;
            _resultTimer = new DispatcherTimer(TimeSpan.FromMilliseconds(50), DispatcherPriority.Background,
                (sender, args) => DrainPendingMatches(20), Dispatcher);
            _resultTimer.Stop();
            VirtualizingPanel.SetIsVirtualizing(ResultsGrid, true);
            VirtualizingPanel.SetIsVirtualizingWhenGrouping(ResultsGrid, true);
            VirtualizingPanel.SetVirtualizationMode(ResultsGrid, VirtualizationMode.Recycling);
            ScrollViewer.SetCanContentScroll(ResultsGrid, true);
            ProfileBox.ItemsSource = LanguageProfile.All;
            string savedProfile = SettingsService.LoadGrepProfile();
            ProfileBox.SelectedItem = LanguageProfile.All.FirstOrDefault(profile =>
                string.Equals(profile.Name, savedProfile, StringComparison.OrdinalIgnoreCase))
                ?? LanguageProfile.All.First(profile => !profile.IsFree);
            double[] widths = SettingsService.LoadGrepColumnWidths();
            if (widths != null)
                for (int index = 0; index < widths.Length && index < ResultsGrid.Columns.Count; index++)
                    if (widths[index] >= 40 && widths[index] <= 4000)
                        ResultsGrid.Columns[index].Width = new DataGridLength(widths[index]);
            bool resetPresets = SeedEditorPresets();
            HookPathBox(EditorBox);
            HookPathBox(EditorArgumentsBox);
            EditorBox.TextChanged += EditorBox_TextChanged;
            EditorBox.HistoryItemApplied += EditorBox_TextChanged;
            if (resetPresets)
            {
                EditorBox.Text = EditorPresets[0].Executable;
                EditorArgumentsBox.Text = EditorPresets[0].Arguments;
                SettingsService.SaveEditor(EditorPresets[0].Executable, EditorPresets[0].Arguments);
            }
            else
            {
                EditorBox.Text = SettingsService.LoadEditorPath();
                EditorArgumentsBox.Text = SettingsService.LoadEditorArguments();
            }
            SetScopes(initialScopes);
            Loaded += (sender, args) =>
            {
                QueryBox.Focus();
                ShowTextEnd(EditorBox);
                ShowTextEnd(EditorArgumentsBox);
            };
        }

        private void AttachElevationToggle()
        {
            var header = TitleBar;
            if (header == null) return;

            while (header.ColumnDefinitions.Count < 3)
                header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            header.ColumnDefinitions[0].Width = new GridLength(1, GridUnitType.Star);
            header.ColumnDefinitions[1].Width = GridLength.Auto;
            header.ColumnDefinitions[2].Width = GridLength.Auto;

            var close = CloseButton ?? header.Children.OfType<Button>().FirstOrDefault();
            if (close != null)
            {
                Grid.SetColumn(close, 2);
                close.VerticalAlignment = VerticalAlignment.Center;
            }

            var toggle = ElevationService.Shared.CreateToggle(this, "grep");
            toggle.VerticalAlignment = VerticalAlignment.Center;
            toggle.Margin = new Thickness(0, 0, 4, 0);
            Grid.SetColumn(toggle, 1);
            header.Children.Add(toggle);
        }

        internal void CaptureElevation(ElevationResumeState session)
        {
            session.GrepScopes = _scopes.ToList();
            session.GrepQuery = QueryBox.Text;
            session.GrepProfile = (ProfileBox.SelectedItem as LanguageProfile)?.Name;
            session.GrepExtensions = ExtensionsText.Text;
            session.Regex = RegexBox.IsChecked == true;
            session.MatchCase = MatchCaseBox.IsChecked == true;
            session.WholeWord = WholeWordBox.IsChecked == true;
        }

        internal void RestoreElevation(ElevationResumeState session)
        {
            SetScopes(session.GrepScopes ?? new List<string>());
            QueryBox.Text = session.GrepQuery ?? string.Empty;

            var profile = LanguageProfile.All.FirstOrDefault(item =>
                string.Equals(item.Name, session.GrepProfile, StringComparison.OrdinalIgnoreCase));
            if (profile != null) ProfileBox.SelectedItem = profile;
            if (profile?.IsFree == true) ExtensionsText.Text = session.GrepExtensions ?? string.Empty;

            RegexBox.IsChecked = session.Regex;
            MatchCaseBox.IsChecked = session.MatchCase;
            WholeWordBox.IsChecked = session.WholeWord;
            QueryBox.Focus();
        }

        private void HookPathBox(TextBox box)
        {
            box.GotKeyboardFocus += (sender, args) => ShowTextEnd(box);
            box.LostKeyboardFocus += (sender, args) => ShowTextEnd(box);
            box.TextChanged += (sender, args) =>
            {
                if (!box.IsKeyboardFocusWithin) ShowTextEnd(box);
            };
        }

        public void SetExplicitScopes(IReadOnlyList<string> scopes)
        {
            if (_searchCts != null) { StatusText.Text = Strings.Grep_CancelBeforeChange; return; }
            SetScopes(scopes);
        }

        public void ReloadFromMainWindow()
        {
            if (_searchCts != null) { StatusText.Text = Strings.Grep_CancelBeforeChange; return; }
            SetScopes(_scopeProvider());
        }

        private void SetScopes(IEnumerable<string> paths)
        {
            string[] normalized = NormalizeScopes(paths).ToArray();
            _scopes.Clear();
            foreach (string path in normalized) _scopes.Add(path);
            ScopeCountText.Text = string.Format(_scopes.Count == 1 ? Strings.Grep_FolderSingular : Strings.Grep_FolderPlural, _scopes.Count);
            StatusText.Text = _scopes.Count == 0 ? Strings.Grep_NoFoldersSelected : Strings.Common_Ready;
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

        private async void Search_Click(object sender, RoutedEventArgs e)
        {
            if (_searchCts != null) return;
            QueryBox.CommitHistory(); ExtensionsText.CommitHistory();
            string query = QueryBox.Text;
            var profile = ProfileBox.SelectedItem as LanguageProfile;
            string[] scopes = _scopes.ToArray();
            if (scopes.Length == 0) { MessageBox.Show(Strings.Grep_SelectFolders, Title); return; }
            if (string.IsNullOrEmpty(query)) { MessageBox.Show(Strings.Grep_EnterText, Title); return; }
            if (profile == null) return;
            if (profile.IsFree)
            {
                string[] extensions = ParseExtensions(ExtensionsText.Text);
                if (extensions.Length == 0) { MessageBox.Show(Strings.Grep_EnterExtensions, Title); return; }
                SettingsService.SaveGrepFreeExtensions(ExtensionsText.Text.Trim());
                profile = new LanguageProfile(profile.Name, extensions);
            }
            bool useRegex = RegexBox.IsChecked == true;
            bool matchCase = MatchCaseBox.IsChecked == true;
            bool wholeWord = WholeWordBox.IsChecked == true;

            var cts = new CancellationTokenSource();
            _searchCts = cts;
            _matches.Clear();
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
                            StatusText.Text = string.Format(Strings.Grep_SearchingFiles, done.ToString("N0"), total.ToString("N0"));
                    })), cts.Token,
                    match => { if (!cts.IsCancellationRequested) pending.Enqueue(match); }));
                await DrainAllPendingMatchesAsync(cts.Token);
                cts.Token.ThrowIfCancellationRequested();
                StatusText.Text = result.SkippedCount == 0 ? string.Format(Strings.Grep_Result, result.Matches.Count.ToString("N0"), result.FileCount.ToString("N0")) : string.Format(Strings.Grep_ResultSkipped, result.Matches.Count.ToString("N0"), result.FileCount.ToString("N0"), result.SkippedCount);
            }
            catch (OperationCanceledException) { _pendingMatches = new ConcurrentQueue<GrepMatch>(); StatusText.Text = Strings.Grep_SearchCancelled; }
            catch (ArgumentException ex) { MessageBox.Show(string.Format(Strings.Grep_InvalidExpression, ErrorMessages.English(ex)), Title, MessageBoxButton.OK, MessageBoxImage.Warning); StatusText.Text = Strings.Grep_InvalidExpressionStatus; }
            catch (Exception ex) { MessageBox.Show(ErrorMessages.English(ex), Title, MessageBoxButton.OK, MessageBoxImage.Error); StatusText.Text = Strings.Grep_SearchFailed; }
            finally { _resultTimer.Stop(); if (ReferenceEquals(_searchCts, cts)) _searchCts = null; SetSearching(false); cts.Dispose(); }
        }

        private void DrainPendingMatches(int maximum)
        {
            int count = 0; GrepMatch match; GrepMatch last = null;
            while (count++ < maximum && _pendingMatches.TryDequeue(out match)) { _matches.Add(match); last = match; }
            if (last != null) ResultsGrid.ScrollIntoView(last);
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

        private static void ShowTextEnd(TextBox box)
        {
            if (box == null) return;
            box.Dispatcher.BeginInvoke(new Action(() =>
            {
                string text = box.Text ?? string.Empty;
                box.CaretIndex = text.Length;
                box.ScrollToHorizontalOffset(Math.Max(0, box.ExtentWidth - box.ViewportWidth));
            }), DispatcherPriority.Loaded);
        }

        private void SetSearching(bool searching)
        {
            SearchButton.IsEnabled = !searching; CancelButton.IsEnabled = searching; ProfileBox.IsEnabled = !searching;
            SearchProgress.Visibility = searching ? Visibility.Visible : Visibility.Collapsed;
            ElevationService.Shared.SetBusy(this, searching);
        }

        private void ResultsGrid_MouseDoubleClick(object sender, MouseButtonEventArgs e)
        {
            var match = ResultsGrid.SelectedItem as GrepMatch;
            if (match == null) return;
            try
            {
                SettingsService.SaveEditor(EditorBox.Text.Trim(), EditorArgumentsBox.Text);
                string arguments = (EditorArgumentsBox.Text ?? string.Empty)
                    .Replace("{file}", match.FilePath).Replace("{line}", match.LineNumber.ToString()).Replace("{column}", match.ColumnNumber.ToString());
                Process.Start(new ProcessStartInfo(ExpandEditorPath(EditorBox.Text), arguments) { UseShellExecute = true });
            }
            catch (Exception ex) { MessageBox.Show(string.Format(Strings.Grep_EditorFailed, ErrorMessages.English(ex)), Title, MessageBoxButton.OK, MessageBoxImage.Error); }
        }

        private void BrowseEditor_Click(object sender, RoutedEventArgs e)
        {
            var dialog = new OpenFileDialog { Filter = Strings.Grep_EditorFilter, CheckFileExists = true };
            if (dialog.ShowDialog(this) != true) return;
            EditorBox.Text = dialog.FileName; EditorBox.CommitHistory();
            ShowTextEnd(EditorBox);
        }

        private static readonly EditorPreset[] EditorPresets =
        {
            new EditorPreset(@"C:\Program Files\MIFES11\MIW.exe", @"/+{line}@{column} ""{file}"""),
            new EditorPreset(@"C:\Program Files\Hidemaru\Hidemaru.exe", @"/j{line},{column} ""{file}"""),
            new EditorPreset(@"%LOCALAPPDATA%\Programs\Mery\Mery.exe", @"/l {line} /cl {column} ""{file}"""),
            new EditorPreset("code", @"--goto ""{file}:{line}:{column}""")
        };

        private bool _applyingEditorArgs;

        private sealed class EditorPreset
        {
            internal EditorPreset(string executable, string arguments)
            {
                Executable = executable;
                Arguments = arguments;
            }
            internal string Executable { get; }
            internal string Arguments { get; }
            internal string FileName { get { return Path.GetFileName(Executable); } }
        }

        private bool SeedEditorPresets()
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

        private void EditorBox_TextChanged(object sender, EventArgs e)
        {
            if (_applyingEditorArgs) return;
            EditorPreset preset = MatchEditorPreset(EditorBox.Text);
            if (preset == null) return;
            _applyingEditorArgs = true;
            try
            {
                EditorArgumentsBox.Text = preset.Arguments;
                ShowTextEnd(EditorArgumentsBox);
            }
            finally { _applyingEditorArgs = false; }
        }

        private static string ExpandEditorPath(string editor)
        {
            if (string.IsNullOrWhiteSpace(editor)) return editor;
            return Environment.ExpandEnvironmentVariables(editor.Trim().Trim('"'));
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

        private void ProfileBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            var previous = e.RemovedItems.Count > 0 ? e.RemovedItems[0] as LanguageProfile : null;
            if (previous != null && previous.IsFree) SettingsService.SaveGrepFreeExtensions(ExtensionsText.Text.Trim());
            var selected = ProfileBox.SelectedItem as LanguageProfile;
            bool free = selected != null && selected.IsFree;
            ExtensionsText.IsReadOnly = !free;
            ExtensionsText.Text = free ? SettingsService.LoadGrepFreeExtensions() : selected?.ExtensionText ?? string.Empty;
        }

        private static string[] ParseExtensions(string text)
        {
            return (text ?? string.Empty).Split(new[] { ' ', '\t', ',', ';' }, StringSplitOptions.RemoveEmptyEntries)
                .Select(value => value.Trim())
                .Where(value => value.Length > 0)
                .Select(value => value == "(none)" ? string.Empty : value.StartsWith(".", StringComparison.Ordinal) ? value : "." + value)
                .Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        }
        private void ReloadScopes_Click(object sender, RoutedEventArgs e) => ReloadFromMainWindow();
        private void Cancel_Click(object sender, RoutedEventArgs e)
        {
            if (_searchCts == null) return;
            _searchCts.Cancel();
            _resultTimer.Stop();
            _pendingMatches = new ConcurrentQueue<GrepMatch>();
            CancelButton.IsEnabled = false;
            StatusText.Text = Strings.Grep_Cancelling;
        }
        private void Close_Click(object sender, RoutedEventArgs e) => Close();
        protected override void OnClosing(CancelEventArgs e)
        {
            Cancel_Click(this, new RoutedEventArgs());
            _resultTimer.Stop();
            SettingsService.SaveEditor(EditorBox.Text.Trim(), EditorArgumentsBox.Text);
            SettingsService.SaveGrepProfile((ProfileBox.SelectedItem as LanguageProfile)?.Name);
            if ((ProfileBox.SelectedItem as LanguageProfile)?.IsFree == true) SettingsService.SaveGrepFreeExtensions(ExtensionsText.Text.Trim());
            SettingsService.SaveGrepColumnWidths(ResultsGrid.Columns.Select(column => column.ActualWidth).ToArray());
            base.OnClosing(e);
        }
    }
}
