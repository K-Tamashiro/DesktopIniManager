using DesktopIniManager.Services;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;

namespace DesktopIniManager.Views
{
    public partial class MftDifferencerWindow
    {
        private static StackPanel ActionContent(string glyph, string label)
        {
            var content = new StackPanel { Orientation = Orientation.Horizontal };
            content.Children.Add(new TextBlock { Text = glyph, FontFamily = new System.Windows.Media.FontFamily("Segoe MDL2 Assets"), FontSize = 15, VerticalAlignment = VerticalAlignment.Center });
            content.Children.Add(new TextBlock { Text = label, Margin = new Thickness(6, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center });
            return content;
        }

        private async void CleanSolutionClick(object sender, RoutedEventArgs e)
        {
            if (busy) return;
            SetBusy(true); CancelCompareButton.IsEnabled = false;
            try
            {
                string source = SourceBox.Text, target = TargetBox.Text;
                StatusText.Text = "Finding solutions…";
                var solutions = await Task.Run(() => SolutionCleanService.FindSolutions(source, target));
                if (solutions.Count == 0) { StatusText.Text = "No solutions found under Source or Target."; return; }
                var dialog = new Window { Owner = this, Title = "Clean solutions", Width = 760, Height = 480,
                    WindowStartupLocation = WindowStartupLocation.CenterOwner };
                dialog.SetResourceReference(BackgroundProperty, "WindowBackground");
                dialog.SetResourceReference(ForegroundProperty, "Ink");
                var panel = new DockPanel { Margin = new Thickness(16) };
                var header = new TextBlock { Text = "Select solutions to clean with MSBuild. The list will be compared again after cleaning.\nSolutions containing this running DIM are disabled. To clean them, start DIM from a separate release folder outside the solution.",
                    TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 12) };
                DockPanel.SetDock(header, Dock.Top); panel.Children.Add(header);
                var footer = new StackPanel { Margin = new Thickness(0, 12, 0, 0) };
                footer.Children.Add(new TextBlock { Text = "Configurations (semicolon separated; solution default platform)" });
                var configuration = new TextBox { Text = "Debug;Release", Margin = new Thickness(0, 6, 0, 12) };
                footer.Children.Add(configuration);
                var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
                var cancel = new Button { Content = ActionContent("\uE711", "Cancel"), Style = (Style)FindResource("MftActionButton"), IsCancel = true, Margin = new Thickness(0, 0, 8, 0) };
                cancel.SetResourceReference(BackgroundProperty, "Secondary");
                var run = new Button { Content = ActionContent("\uE75C", "Clean"), Style = (Style)FindResource("MftActionButton"), Background = (System.Windows.Media.Brush)FindResource("CleanColor"), IsDefault = true };
                buttons.Children.Add(cancel); buttons.Children.Add(run); footer.Children.Add(buttons);
                DockPanel.SetDock(footer, Dock.Bottom); panel.Children.Add(footer);
                var choices = solutions.Select(path => new CheckBox { Content = path,
                    IsChecked = !SolutionCleanService.ContainsRunningApplication(path),
                    IsEnabled = !SolutionCleanService.ContainsRunningApplication(path),
                    ToolTip = SolutionCleanService.ContainsRunningApplication(path) ? "Contains the running DIM; start DIM from a separate release folder to clean this solution." : path,
                    Margin = new Thickness(0, 6, 0, 6) }).ToList();
                var list = new StackPanel();
                string sourceRoot = MftDifferencerService.Root(source);
                foreach (var choice in choices)
                {
                    bool isSource = ((string)choice.Content).StartsWith(sourceRoot, StringComparison.OrdinalIgnoreCase);
                    var row = new StackPanel();
                    row.Children.Add(new TextBlock { Text = isSource ? "Source (left)" : "Target (right)", FontWeight = FontWeights.SemiBold });
                    row.Children.Add(choice);
                    var frame = new Border { Child = row, BorderBrush = (System.Windows.Media.Brush)FindResource(isSource ? "SourceColor" : "TargetColor"),
                        BorderThickness = new Thickness(4, 1, 1, 1), CornerRadius = new CornerRadius(4), Padding = new Thickness(10, 6, 10, 2), Margin = new Thickness(0, 0, 0, 8) };
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
                    { MessageBox.Show(dialog, "Select at least one solution and enter configuration names such as Debug;Release."); return; }
                    dialog.DialogResult = true;
                };
                if (dialog.ShowDialog() != true) { StatusText.Text = "Clean cancelled."; return; }
                string msbuild = await Task.Run(() => SolutionCleanService.FindMSBuild());
                ClearComparisonView();
                CompareProgress.Visibility = Visibility.Visible; CompareProgress.IsIndeterminate = true;
                var log = new StringBuilder(); int failures = 0, completed = 0;
                foreach (string solution in choices.Where(c => c.IsChecked == true).Select(c => (string)c.Content))
                    foreach (string config in configurations)
                    {
                        string label = solution + " [" + config + "]";
                        StatusText.Text = "Cleaning " + label;
                        SetFilePanelBusy(true, "Cleaning " + Path.GetFileName(solution) + " [" + config + "]…");
                        log.AppendLine(label);
                        try
                        {
                            int exit = await Task.Run(() => { string output; int code = SolutionCleanService.Clean(msbuild, solution, config, out output); log.AppendLine(output); return code; });
                            if (exit != 0) failures++;
                            log.AppendLine(exit == 0 ? "OK" : "FAIL (exit " + exit + ")");
                        }
                        catch (Exception ex) { failures++; log.AppendLine("FAIL " + ErrorMessages.English(ex)); }
                        completed++;
                    }
                string summary = "Clean complete: " + (completed - failures) + " succeeded / " + failures + " failed.";
                Directory.CreateDirectory(StateDirectory);
                string logPath = Path.Combine(StateDirectory, "solution-clean-" + DateTime.Now.ToString("yyyyMMdd-HHmmss-fff") + ".log");
                File.WriteAllText(logPath, log.ToString());
                await Compare();
                StatusText.Text = summary + " " + StatusText.Text;
                var report = new Window { Owner = this, Title = summary, Width = 850, Height = 480,
                    WindowStartupLocation = WindowStartupLocation.CenterOwner,
                    Content = new TextBox { IsReadOnly = true, Text = "Log: " + logPath + Environment.NewLine + log,
                        VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Auto } };
                report.SetResourceReference(BackgroundProperty, "WindowBackground");
                report.SetResourceReference(ForegroundProperty, "Ink"); report.Show();
            }
            catch (Exception ex) { StatusText.Text = "Clean failed: " + ErrorMessages.English(ex); ShowError(ex); }
            finally { CompareProgress.Visibility = Visibility.Collapsed; CompareProgress.IsIndeterminate = false; SetBusy(false); }
        }
    }
}
