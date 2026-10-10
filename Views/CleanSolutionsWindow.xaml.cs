using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using DesktopIniManager.Properties;
using DesktopIniManager.Services;
using DesktopIniManager.ViewModels;

namespace DesktopIniManager.Views;

internal sealed class CleanSolutionItem
{
    public string Path { get; set; }
    public bool IsSource { get; set; }
    public bool IsChecked { get; set; }
    public bool IsEnabled { get; set; }
    public string Tip { get; set; }
    public string SideLabel { get; set; }
}

internal partial class CleanSolutionsWindow : Window
{
    public CleanSolutionsWindow()
    {
        InitializeComponent();
        CancelButtonIcon.Source = DifferencerStatusIcons.GetCustomIcon(26);
        RunButtonIcon.Source = DifferencerStatusIcons.GetCustomIcon(49);
    }

    internal static SolutionCleanSelection Choose(Window owner, IReadOnlyList<string> solutions, string source)
    {
        var dialog = new CleanSolutionsWindow { Owner = owner };
        string sourceRoot = DeveloperDifferencerService.Root(source);
        dialog.SolutionList.ItemsSource = solutions.Select(path =>
        {
            bool locked = SolutionCleanService.ContainsRunningApplication(path);
            bool isSource = path.StartsWith(sourceRoot, StringComparison.OrdinalIgnoreCase);
            return new CleanSolutionItem
            {
                Path = path,
                IsSource = isSource,
                IsChecked = !locked,
                IsEnabled = !locked,
                Tip = locked ? Strings.Differencer_DimLockedTip : path,
                SideLabel = isSource ? Strings.Differencer_SourceLeft : Strings.Differencer_TargetRight
            };
        }).ToList();
        if (dialog.ShowDialog() != true) return null;
        return dialog.Result;
    }

    private SolutionCleanSelection Result { get; set; }

    private void Cancel_Click(object sender, RoutedEventArgs e) => DialogResult = false;

    private void Run_Click(object sender, RoutedEventArgs e)
    {
        var items = (SolutionList.ItemsSource as IEnumerable<CleanSolutionItem>) ?? Array.Empty<CleanSolutionItem>();
        string[] configurations = ConfigurationBox.Text.Split(';')
            .Select(item => item.Trim())
            .Where(item => item.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        string[] selected = items.Where(item => item.IsChecked).Select(item => item.Path).ToArray();
        if (selected.Length == 0 || configurations.Length == 0 ||
            configurations.Any(item => item.Any(ch => !char.IsLetterOrDigit(ch) && ch != ' ' && ch != '_' && ch != '-')))
        {
            MessageBox.Show(this, Strings.Differencer_SelectSolutions);
            return;
        }
        Result = new SolutionCleanSelection { Solutions = selected, Configurations = configurations };
        DialogResult = true;
    }
}
