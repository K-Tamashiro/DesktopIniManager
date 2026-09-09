using DesktopIniManager.ViewModels;
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
        internal GrepWindowViewModel ViewModel { get; }
        private bool _resultGroupsExpanded = true;
        private readonly Dictionary<string, bool> _resultGroupStates = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);

        public GrepWindow(Func<IReadOnlyList<string>> scopeProvider, IReadOnlyList<string> initialScopes)
        {
            ViewModel = new GrepWindowViewModel(scopeProvider, Dispatcher, new UserDialogService(this));
            InitializeComponent();
            DataContext = ViewModel;
            ViewModel.DialogTitle = Title;
            ViewModel.SearchHistoryRequested += () => { QueryBox.CommitHistory(); ExtensionsText.CommitHistory(); };
            ViewModel.MatchScrollRequested += match => ResultsGrid.ScrollIntoView(match);
            ViewModel.ResultGroupsResetRequested += () => { _resultGroupsExpanded = true; _resultGroupStates.Clear(); };
            ICollectionView resultView = CollectionViewSource.GetDefaultView(ViewModel.Matches);
            resultView.GroupDescriptions.Add(new PropertyGroupDescription(nameof(GrepMatch.GroupPath)));
            VirtualizingPanel.SetIsVirtualizing(ResultsGrid, true);
            VirtualizingPanel.SetIsVirtualizingWhenGrouping(ResultsGrid, true);
            VirtualizingPanel.SetVirtualizationMode(ResultsGrid, VirtualizationMode.Recycling);
            ScrollViewer.SetCanContentScroll(ResultsGrid, true);
            string savedProfile = SettingsService.LoadGrepProfile();
            ViewModel.SelectedProfile = LanguageProfile.All.FirstOrDefault(profile =>
                string.Equals(profile.Name, savedProfile, StringComparison.OrdinalIgnoreCase))
                ?? LanguageProfile.All.First(profile => !profile.IsFree);
            ApplyGrepColumnWidths(SettingsService.LoadGrepColumnWidths());
            bool resetPresets = SeedEditorPresets();
            HookPathBox(EditorBox);
            HookPathBox(EditorArgumentsBox);
            EditorBox.TextChanged += EditorBox_TextChanged;
            EditorBox.HistoryItemApplied += EditorBox_TextChanged;
            if (resetPresets)
            {
                ViewModel.EditorPath = EditorPresets[0].Executable;
                ViewModel.EditorArguments = EditorPresets[0].Arguments;
                SettingsService.SaveEditor(EditorPresets[0].Executable, EditorPresets[0].Arguments);
            }
            else
            {
                ViewModel.EditorPath = SettingsService.LoadEditorPath();
                ViewModel.EditorArguments = SettingsService.LoadEditorArguments();
            }
            ViewModel.SetExplicitScopes(initialScopes);
            Loaded += (sender, args) =>
            {
                ApplyGrepColumnWidths(SettingsService.LoadGrepColumnWidths());
                Dispatcher.BeginInvoke(new Action(() => ApplyGrepColumnWidths(SettingsService.LoadGrepColumnWidths())), System.Windows.Threading.DispatcherPriority.Loaded);
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



        /// <summary>Applies the current global expansion state to a newly realized file group.</summary>
        private void ResultGroupExpander_Loaded(object sender, RoutedEventArgs e)
        {
            var expander = sender as Expander;
            if (expander == null) return;
            bool expanded;
            expander.IsExpanded = _resultGroupStates.TryGetValue(ResultGroupKey(expander), out expanded)
                ? expanded : _resultGroupsExpanded;
        }

        /// <summary>Remembers an individual file group's expanded state.</summary>
        private void ResultGroupExpander_Expanded(object sender, RoutedEventArgs e)
        {
            var expander = sender as Expander;
            if (expander != null) _resultGroupStates[ResultGroupKey(expander)] = true;
        }

        /// <summary>Remembers an individual file group's collapsed state.</summary>
        private void ResultGroupExpander_Collapsed(object sender, RoutedEventArgs e)
        {
            var expander = sender as Expander;
            if (expander != null) _resultGroupStates[ResultGroupKey(expander)] = false;
        }

        /// <summary>Returns the stable file-path key for a result group.</summary>
        private static string ResultGroupKey(Expander expander)
        { return Convert.ToString((expander.DataContext as CollectionViewGroup)?.Name) ?? string.Empty; }

        /// <summary>Expands every file group in the GREP result list.</summary>
        private void ExpandResultGroups_Click(object sender, RoutedEventArgs e)
        {
            SetResultGroupsExpanded(true);
        }

        /// <summary>Collapses every file group to its file-path row.</summary>
        private void CollapseResultGroups_Click(object sender, RoutedEventArgs e)
        {
            SetResultGroupsExpanded(false);
        }

        /// <summary>Updates realized groups and the default state for groups realized after scrolling.</summary>
        private void SetResultGroupsExpanded(bool expanded)
        {
            _resultGroupsExpanded = expanded;
            _resultGroupStates.Clear();
            foreach (Expander expander in FindVisualDescendants<Expander>(ResultsGrid)
                .Where(item => string.Equals(item.Name, "ResultGroupExpander", StringComparison.Ordinal)))
                expander.IsExpanded = expanded;
        }

        /// <summary>Enumerates visual descendants of the requested type.</summary>
        private static IEnumerable<T> FindVisualDescendants<T>(DependencyObject root) where T : DependencyObject
        {
            if (root == null) yield break;
            for (int index = 0; index < System.Windows.Media.VisualTreeHelper.GetChildrenCount(root); index++)
            {
                DependencyObject child = System.Windows.Media.VisualTreeHelper.GetChild(root, index);
                var match = child as T;
                if (match != null) yield return match;
                foreach (T descendant in FindVisualDescendants<T>(child)) yield return descendant;
            }
        }



        private void BrowseEditor_Click(object sender, RoutedEventArgs e)
        {
            var dialog = new OpenFileDialog { Filter = Strings.Grep_EditorFilter, CheckFileExists = true };
            if (dialog.ShowDialog(this) != true) return;
            ViewModel.EditorPath = dialog.FileName; EditorBox.CommitHistory();
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
            EditorPreset preset = MatchEditorPreset(ViewModel.EditorPath);
            if (preset == null) return;
            _applyingEditorArgs = true;
            try
            {
                ViewModel.EditorArguments = preset.Arguments;
                ShowTextEnd(EditorArgumentsBox);
            }
            finally { _applyingEditorArgs = false; }
        }



        private static EditorPreset MatchEditorPreset(string editor)
        {
            if (string.IsNullOrWhiteSpace(editor)) return null;
            string path = GrepWindowViewModel.ExpandEditorPath(editor);
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






        public void SetExplicitScopes(IReadOnlyList<string> scopes) => ViewModel.SetExplicitScopes(scopes);
        public void ReloadFromMainWindow() => ViewModel.ReloadFromMainWindow();
        private void ResultsGrid_MouseDoubleClick(object sender, MouseButtonEventArgs e) => ViewModel.OpenMatchCommand.Execute(null);
        private void Close_Click(object sender, RoutedEventArgs e) => Close();
        protected override void OnClosing(CancelEventArgs e)
        {
            ViewModel.Close();
            double[] widths = ResultsGrid.Columns.Select(column => column.ActualWidth).ToArray();
            if (widths.Length >= 4 && widths[0] >= 80 && widths[1] >= 120)
                SettingsService.SaveGrepColumnWidths(widths);
            base.OnClosing(e);
        }

        private void ApplyGrepColumnWidths(double[] widths)
        {
            ResultsGrid.RowHeaderWidth = 0;
            double[] defaults = { 200, 300, 80, 80, 550 };
            double[] mins = { 90, 160, 60, 60, 200 };
            bool saved = widths != null && widths.Length >= 5 &&
                widths[0] >= 90 && widths[1] >= 160 && widths[2] >= 50 && widths[3] >= 50 && widths[4] >= 200;
            for (int index = 0; index < ResultsGrid.Columns.Count && index < defaults.Length; index++)
            {
                ResultsGrid.Columns[index].MinWidth = mins[index];
                ResultsGrid.Columns[index].Width = new DataGridLength(saved ? widths[index] : defaults[index]);
            }
        }
    }
}
