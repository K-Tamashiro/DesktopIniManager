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

namespace DesktopIniManager.Views
{
    public partial class GrepWindow : Window
    {
        private readonly Func<IReadOnlyList<string>> _scopeProvider;
        private readonly ObservableCollection<string> _scopes = new ObservableCollection<string>();
        private readonly ObservableCollection<GrepMatch> _matches = new ObservableCollection<GrepMatch>();
        private CancellationTokenSource _searchCts;
        private readonly ConcurrentQueue<GrepMatch> _pendingMatches = new ConcurrentQueue<GrepMatch>();
        private readonly DispatcherTimer _resultTimer;

        public GrepWindow(Func<IReadOnlyList<string>> scopeProvider, IReadOnlyList<string> initialScopes)
        {
            _scopeProvider = scopeProvider;
            InitializeComponent();
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
            EditorBox.Text = SettingsService.LoadEditorPath();
            EditorArgumentsBox.Text = SettingsService.LoadEditorArguments();
            HookPathBox(EditorBox);
            HookPathBox(EditorArgumentsBox);
            SetScopes(initialScopes);
            Loaded += (sender, args) =>
            {
                QueryBox.Focus();
                ShowTextEnd(EditorBox);
                ShowTextEnd(EditorArgumentsBox);
            };
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
            if (_searchCts != null) { StatusText.Text = "Cancel the current search before changing scopes"; return; }
            SetScopes(scopes);
        }

        public void ReloadFromMainWindow()
        {
            if (_searchCts != null) { StatusText.Text = "Cancel the current search before changing scopes"; return; }
            SetScopes(_scopeProvider());
        }

        private void SetScopes(IEnumerable<string> paths)
        {
            string[] normalized = NormalizeScopes(paths).ToArray();
            _scopes.Clear();
            foreach (string path in normalized) _scopes.Add(path);
            ScopeCountText.Text = _scopes.Count + (_scopes.Count == 1 ? " folder" : " folders");
            StatusText.Text = _scopes.Count == 0 ? "No folders selected" : "Ready";
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
            string query = QueryBox.Text;
            var profile = ProfileBox.SelectedItem as LanguageProfile;
            string[] scopes = _scopes.ToArray();
            if (scopes.Length == 0) { MessageBox.Show("Select one or more project folders in DesktopIniManager.", Title); return; }
            if (string.IsNullOrEmpty(query)) { MessageBox.Show("Enter search text.", Title); return; }
            if (profile == null) return;
            if (profile.IsFree)
            {
                string[] extensions = ParseExtensions(ExtensionsText.Text);
                if (extensions.Length == 0) { MessageBox.Show("Enter one or more file extensions for the Free profile.", Title); return; }
                SettingsService.SaveGrepFreeExtensions(ExtensionsText.Text.Trim());
                profile = new LanguageProfile(profile.Name, extensions);
            }
            bool useRegex = RegexBox.IsChecked == true;
            bool matchCase = MatchCaseBox.IsChecked == true;
            bool wholeWord = WholeWordBox.IsChecked == true;

            var cts = new CancellationTokenSource();
            _searchCts = cts;
            _matches.Clear();
            while (_pendingMatches.TryDequeue(out GrepMatch ignored)) { }
            _resultTimer.Start();
            SetSearching(true);
            try
            {
                GrepSearchResult result = await Task.Run(() => new CodeGrepService().Search(scopes, profile, query,
                    useRegex, matchCase, wholeWord,
                    (done, total) => Dispatcher.BeginInvoke(new Action(() => StatusText.Text = "Searching " + done.ToString("N0") + " / " + total.ToString("N0") + " files…")), cts.Token,
                    match => _pendingMatches.Enqueue(match)));
                await DrainAllPendingMatchesAsync();
                StatusText.Text = result.Matches.Count.ToString("N0") + " matches in " + result.FileCount.ToString("N0") + " files" + (result.SkippedCount == 0 ? string.Empty : " · " + result.SkippedCount + " skipped");
            }
            catch (OperationCanceledException) { await DrainAllPendingMatchesAsync(); StatusText.Text = "Search cancelled"; }
            catch (ArgumentException ex) { MessageBox.Show("The search expression is invalid.\n\n" + ex.Message, Title, MessageBoxButton.OK, MessageBoxImage.Warning); StatusText.Text = "Invalid expression"; }
            catch (Exception ex) { MessageBox.Show(ex.Message, Title, MessageBoxButton.OK, MessageBoxImage.Error); StatusText.Text = "Search failed"; }
            finally { _resultTimer.Stop(); if (ReferenceEquals(_searchCts, cts)) _searchCts = null; SetSearching(false); cts.Dispose(); }
        }

        private void DrainPendingMatches(int maximum)
        {
            int count = 0; GrepMatch match; GrepMatch last = null;
            while (count++ < maximum && _pendingMatches.TryDequeue(out match)) { _matches.Add(match); last = match; }
            if (last != null) ResultsGrid.ScrollIntoView(last);
        }

        private async Task DrainAllPendingMatchesAsync()
        {
            while (!_pendingMatches.IsEmpty)
            {
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
                Process.Start(new ProcessStartInfo(EditorBox.Text.Trim(), arguments) { UseShellExecute = true });
            }
            catch (Exception ex) { MessageBox.Show("Could not open the editor.\n\n" + ex.Message, Title, MessageBoxButton.OK, MessageBoxImage.Error); }
        }

        private void BrowseEditor_Click(object sender, RoutedEventArgs e)
        {
            var dialog = new OpenFileDialog { Filter = "Applications|*.exe|All files|*.*", CheckFileExists = true };
            if (dialog.ShowDialog(this) != true) return;
            EditorBox.Text = dialog.FileName;
            ShowTextEnd(EditorBox);
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
        private void Cancel_Click(object sender, RoutedEventArgs e) => _searchCts?.Cancel();
        private void Close_Click(object sender, RoutedEventArgs e) => Close();
        protected override void OnClosing(CancelEventArgs e)
        {
            _searchCts?.Cancel();
            _resultTimer.Stop();
            SettingsService.SaveEditor(EditorBox.Text.Trim(), EditorArgumentsBox.Text);
            SettingsService.SaveGrepProfile((ProfileBox.SelectedItem as LanguageProfile)?.Name);
            if ((ProfileBox.SelectedItem as LanguageProfile)?.IsFree == true) SettingsService.SaveGrepFreeExtensions(ExtensionsText.Text.Trim());
            SettingsService.SaveGrepColumnWidths(ResultsGrid.Columns.Select(column => column.ActualWidth).ToArray());
            base.OnClosing(e);
        }
    }
}
