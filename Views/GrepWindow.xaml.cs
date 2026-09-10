using DesktopIniManager.Models;
using DesktopIniManager.ViewModels;
using DesktopIniManager.Services;
using Microsoft.Win32;
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using DesktopIniManager.Properties;

namespace DesktopIniManager.Views
{
    public partial class GrepWindow : Window
    {
        internal GrepWindowViewModel ViewModel { get; }
        private bool _resultGroupsExpanded = true;
        private bool _applyingGroupExpansion;
        private readonly Dictionary<string, bool> _resultGroupStates = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);

        public GrepWindow(Func<IReadOnlyList<string>> scopeProvider, IReadOnlyList<string> initialScopes)
        {
            ViewModel = new GrepWindowViewModel(scopeProvider, Dispatcher, new UserDialogService(this));
            InitializeComponent();
            DataContext = ViewModel;
            ViewModel.DialogTitle = Title;
            ViewModel.SearchHistoryRequested += () => { QueryBox.CommitHistory(); ExtensionsText.CommitHistory(); };
            ViewModel.MatchScrollRequested += ScrollToMatch;
            ViewModel.ResultGroupsResetRequested += () => { _resultGroupsExpanded = true; _resultGroupStates.Clear(); };
            ViewModel.BrowseEditorRequested += BrowseEditor;
            ViewModel.SaveResultsRequested += SaveResults;
            ViewModel.CloseRequested += Close;
            ViewModel.ResultGroupsExpansionRequested += SetResultGroupsExpanded;
            ViewModel.Initialize(initialScopes);
            ApplyGrepColumnWidths(ViewModel.ColumnWidths);
            HookPathBox(EditorBox);
            HookPathBox(EditorArgumentsBox);
            // Re-selecting the same history item must also restore its preset arguments.
            EditorBox.HistoryItemApplied += (sender, args) => ViewModel.ApplyEditorPresetCommand.Execute(null);
            Loaded += (sender, args) =>
            {
                ApplyGrepColumnWidths(ViewModel.ColumnWidths);
                Dispatcher.BeginInvoke(new Action(() => ApplyGrepColumnWidths(ViewModel.ColumnWidths)), DispatcherPriority.Loaded);
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
            ApplyGroupExpansion(sender as Expander);
        }

        private void ResultGroupExpander_DataContextChanged(object sender, DependencyPropertyChangedEventArgs e)
        {
            ApplyGroupExpansion(sender as Expander);
        }

        private void ApplyGroupExpansion(Expander expander)
        {
            if (expander == null) return;
            string key = ResultGroupKey(expander);
            bool expanded = !string.IsNullOrEmpty(key) && _resultGroupStates.TryGetValue(key, out bool stored)
                ? stored : _resultGroupsExpanded;
            if (expander.IsExpanded == expanded) return;
            _applyingGroupExpansion = true;
            try { expander.IsExpanded = expanded; }
            finally { _applyingGroupExpansion = false; }
        }

        /// <summary>Remembers an individual file group's expanded state.</summary>
        private void ResultGroupExpander_Expanded(object sender, RoutedEventArgs e)
        {
            if (_applyingGroupExpansion) return;
            string key = ResultGroupKey(sender as Expander);
            if (!string.IsNullOrEmpty(key)) _resultGroupStates[key] = true;
        }

        /// <summary>Remembers an individual file group's collapsed state.</summary>
        private void ResultGroupExpander_Collapsed(object sender, RoutedEventArgs e)
        {
            if (_applyingGroupExpansion) return;
            string key = ResultGroupKey(sender as Expander);
            if (!string.IsNullOrEmpty(key)) _resultGroupStates[key] = false;
        }

        /// <summary>Returns the stable file-path key for a result group.</summary>
        private static string ResultGroupKey(Expander expander)
        { return Convert.ToString((expander.DataContext as CollectionViewGroup)?.Name) ?? string.Empty; }

        /// <summary>Updates realized groups and the default state for groups realized after scrolling.</summary>
        private void SetResultGroupsExpanded(bool expanded)
        {
            _resultGroupsExpanded = expanded;
            _resultGroupStates.Clear();
            RecordGroupStates(ResultsGrid.Items.Groups, expanded);
            _applyingGroupExpansion = true;
            try
            {
                foreach (Expander expander in FindVisualDescendants<Expander>(ResultsGrid)
                    .Where(item => string.Equals(item.Name, "ResultGroupExpander", StringComparison.Ordinal)))
                    expander.IsExpanded = expanded;
            }
            finally { _applyingGroupExpansion = false; }
        }

        private void ScrollToMatch(GrepMatch match)
        {
            if (match == null) return;
            Dispatcher.BeginInvoke(new Action(() =>
            {
                try { ResultsGrid.ScrollIntoView(match); }
                catch { }
                ScrollViewer viewer = FindVisualDescendants<ScrollViewer>(ResultsGrid).FirstOrDefault();
                viewer?.ScrollToEnd();
            }), DispatcherPriority.Background);
        }

        private void RecordGroupStates(System.Collections.IEnumerable groups, bool expanded)
        {
            if (groups == null) return;
            foreach (object item in groups)
            {
                var group = item as CollectionViewGroup;
                if (group == null) continue;
                _resultGroupStates[Convert.ToString(group.Name) ?? string.Empty] = expanded;
                if (!group.IsBottomLevel) RecordGroupStates(group.Items, expanded);
            }
        }

        private void GroupHeader_Click(object sender, MouseButtonEventArgs e)
        {
            if (e.OriginalSource is Button) return;
            var expander = FindAncestor<Expander>(sender as DependencyObject);
            if (expander == null) return;
            expander.IsExpanded = !expander.IsExpanded;
            e.Handled = true;
        }

        private void RemoveGroup_Click(object sender, RoutedEventArgs e)
        {
            e.Handled = true;
            string key = GroupKeyFromSender(sender as DependencyObject);
            if (!string.IsNullOrEmpty(key)) ViewModel.RemoveGroup(key);
        }

        private void CopyGroupPath_Click(object sender, RoutedEventArgs e)
        {
            string key = GroupKeyFromSender(sender as DependencyObject);
            if (!string.IsNullOrEmpty(key)) ViewModel.CopyGroupPath(key);
        }

        private void OpenGroupFolder_Click(object sender, RoutedEventArgs e)
        {
            string key = GroupKeyFromSender(sender as DependencyObject);
            if (!string.IsNullOrEmpty(key)) ViewModel.OpenGroupFolder(key);
        }

        private static string GroupKeyFromSender(DependencyObject source)
        {
            var menuItem = source as MenuItem;
            var menu = menuItem?.Parent as ContextMenu;
            DependencyObject start = menu?.PlacementTarget as DependencyObject ?? source;
            var expander = FindAncestor<Expander>(start);
            if (expander == null && menuItem != null)
                expander = FindAncestor<Expander>(menuItem);
            return expander == null ? string.Empty : ResultGroupKey(expander);
        }

        private static T FindAncestor<T>(DependencyObject current) where T : DependencyObject
        {
            while (current != null)
            {
                var match = current as T;
                if (match != null) return match;
                current = System.Windows.Media.VisualTreeHelper.GetParent(current);
            }
            return null;
        }

        private void SaveResults()
        {
            var dialog = new SaveFileDialog
            {
                Filter = Strings.Grep_SaveFilter,
                FileName = "grep-results.txt",
                AddExtension = true,
                DefaultExt = "txt"
            };
            if (dialog.ShowDialog(this) != true) return;
            try { ViewModel.SaveResultsTo(dialog.FileName); }
            catch (Exception ex)
            {
                MessageBox.Show(this, ErrorMessages.English(ex), Title, MessageBoxButton.OK, MessageBoxImage.Error);
            }
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

        private void BrowseEditor()
        {
            var dialog = new OpenFileDialog { Filter = Strings.Grep_EditorFilter, CheckFileExists = true };
            if (dialog.ShowDialog(this) != true) return;
            ViewModel.EditorPath = dialog.FileName; EditorBox.CommitHistory();
            ShowTextEnd(EditorBox);
        }

        public void SetExplicitScopes(IReadOnlyList<string> scopes) => ViewModel.SetExplicitScopes(scopes);
        public void ReloadFromMainWindow() => ViewModel.ReloadFromMainWindow();
        protected override void OnClosing(CancelEventArgs e)
        {
            ViewModel.Close();
            double[] widths = ResultsGrid.Columns.Select(column => column.ActualWidth).ToArray();
            ViewModel.SaveColumnWidths(widths);
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

        private void MatchLine_Loaded(object sender, RoutedEventArgs e)
        {
            ApplyMatchHighlight(sender as TextBlock);
        }

        private void MatchLine_DataContextChanged(object sender, DependencyPropertyChangedEventArgs e)
        {
            ApplyMatchHighlight(sender as TextBlock);
        }

        private static void ApplyMatchHighlight(TextBlock block)
        {
            if (block == null) return;
            var match = block.DataContext as GrepMatch;
            string text = match?.LineText ?? string.Empty;
            string needle = match?.Highlight;
            block.Inlines.Clear();
            if (string.IsNullOrEmpty(text) || string.IsNullOrEmpty(needle))
            {
                block.Text = text;
                return;
            }
            int start = 0;
            while (start < text.Length)
            {
                int index = text.IndexOf(needle, start, StringComparison.CurrentCultureIgnoreCase);
                if (index < 0)
                {
                    block.Inlines.Add(new Run(text.Substring(start)));
                    break;
                }
                if (index > start) block.Inlines.Add(new Run(text.Substring(start, index - start)));
                block.Inlines.Add(new Run(text.Substring(index, needle.Length))
                {
                    Foreground = Brushes.Red,
                    FontWeight = FontWeights.SemiBold
                });
                start = index + needle.Length;
            }
        }
    }
}
