using DesktopIniManager.ViewModels;
using DesktopIniManager.Services;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using DesktopIniManager.Properties;

namespace DesktopIniManager.Views
{
    public partial class DeveloperDifferencerWindow
    {
        private static StackPanel ActionContent(string glyph, string label)
        {
            var content = new StackPanel { Orientation = Orientation.Horizontal };
            content.Children.Add(new TextBlock { Text = glyph, FontFamily = new System.Windows.Media.FontFamily("Segoe MDL2 Assets"), FontSize = 15, VerticalAlignment = VerticalAlignment.Center });
            content.Children.Add(new TextBlock { Text = label, Margin = new Thickness(6, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center });
            return content;
        }
        private SolutionCleanSelection ChooseCleanSolutions(IReadOnlyList<string> solutions, string source)
        {
                var dialog = new Window
                {
                    Owner = this,
                    Title = Strings.Differencer_CleanSolutionsTitle,
                    Width = 760,
                    Height = 480,
                    WindowStartupLocation = WindowStartupLocation.CenterOwner
                };
                dialog.SetResourceReference(BackgroundProperty, "WindowBackground");
                dialog.SetResourceReference(ForegroundProperty, "Ink");
                var panel = new DockPanel { Margin = new Thickness(16) };
                var header = new TextBlock
                {
                    Text = Strings.Differencer_CleanDialogHint,
                    TextWrapping = TextWrapping.Wrap,
                    Margin = new Thickness(0, 0, 0, 12)
                };
                DockPanel.SetDock(header, Dock.Top); panel.Children.Add(header);
                var footer = new StackPanel { Margin = new Thickness(0, 12, 0, 0) };
                footer.Children.Add(new TextBlock { Text = Strings.Differencer_ConfigurationsLabel });
                var configuration = new HistoryTextBox { HistoryKey = "Differencer-CleanConfigurations", Text = "Debug;Release", Margin = new Thickness(0, 6, 0, 12) };
                footer.Children.Add(configuration);
                var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
                var cancel = new Button { Content = ActionContent("\uE711", Strings.Common_Cancel), Style = (Style)FindResource("DifferencerActionButton"), IsCancel = true, Margin = new Thickness(0, 0, 8, 0) };
                cancel.SetResourceReference(BackgroundProperty, "Secondary");
                var run = new Button { Content = ActionContent("\uE75C", Strings.Common_Clean), Style = (Style)FindResource("DifferencerActionButton"), Background = (System.Windows.Media.Brush)FindResource("CleanColor"), IsDefault = true };
                buttons.Children.Add(cancel); buttons.Children.Add(run); footer.Children.Add(buttons);
                DockPanel.SetDock(footer, Dock.Bottom); panel.Children.Add(footer);
                var choices = solutions.Select(path => new CheckBox
                {
                    Content = path,
                    IsChecked = !SolutionCleanService.ContainsRunningApplication(path),
                    IsEnabled = !SolutionCleanService.ContainsRunningApplication(path),
                    ToolTip = SolutionCleanService.ContainsRunningApplication(path) ? Strings.Differencer_DimLockedTip : path,
                    Margin = new Thickness(0, 6, 0, 6)
                }).ToList();
                var list = new StackPanel();
                string sourceRoot = DeveloperDifferencerService.Root(source);
                foreach (var choice in choices)
                {
                    bool isSource = ((string)choice.Content).StartsWith(sourceRoot, StringComparison.OrdinalIgnoreCase);
                    var row = new StackPanel();
                    row.Children.Add(new TextBlock { Text = isSource ? Strings.Differencer_SourceLeft : Strings.Differencer_TargetRight, FontWeight = FontWeights.SemiBold });
                    row.Children.Add(choice);
                    var frame = new Border
                    {
                        Child = row,
                        BorderBrush = (System.Windows.Media.Brush)FindResource(isSource ? "SourceColor" : "TargetColor"),
                        BorderThickness = new Thickness(4, 1, 1, 1),
                        CornerRadius = new CornerRadius(4),
                        Padding = new Thickness(10, 6, 10, 2),
                        Margin = new Thickness(0, 0, 0, 8)
                    };
                    frame.SetResourceReference(BackgroundProperty, "CardBackground");
                    list.Children.Add(frame);
                }
                panel.Children.Add(new ScrollViewer { Content = list, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Auto });
                dialog.Content = panel;
                string[] configurations = null;
                run.Click += (s, args) =>
                {
                    configurations = configuration.Text.Split(';').Select(c => c.Trim()).Where(c => c.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
                    if (!choices.Any(c => c.IsChecked == true) || configurations.Length == 0 ||
                        configurations.Any(c => c.Any(ch => !char.IsLetterOrDigit(ch) && ch != ' ' && ch != '_' && ch != '-')))
                    { MessageBox.Show(dialog, Strings.Differencer_SelectSolutions); return; }
                    dialog.DialogResult = true;
                };
                if (dialog.ShowDialog() != true) return null;
                return new SolutionCleanSelection { Solutions = choices.Where(c => c.IsChecked == true).Select(c => (string)c.Content).ToArray(), Configurations = configurations };
        }
        private void ShowCleanReport(string summary, string logPath, string log)
        {
                var report = new Window
                {
                    Owner = this,
                    Title = summary,
                    Width = 850,
                    Height = 480,
                    WindowStartupLocation = WindowStartupLocation.CenterOwner,
                    Content = new TextBox
                    {
                        IsReadOnly = true,
                        Text = string.Format(Strings.Differencer_LogLabel, logPath) + Environment.NewLine + log,
                        VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                        HorizontalScrollBarVisibility = ScrollBarVisibility.Auto
                    }
                };
                report.SetResourceReference(BackgroundProperty, "WindowBackground");
                report.SetResourceReference(ForegroundProperty, "Ink"); report.Show();
        }
    }
}
