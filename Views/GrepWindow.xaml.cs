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
            ViewModel.BrowseEditorRequested += BrowseEditor;
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
    }
}
