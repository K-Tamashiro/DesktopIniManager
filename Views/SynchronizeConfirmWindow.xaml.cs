using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using DesktopIniManager.Services;

namespace DesktopIniManager.Views
{
    internal partial class SynchronizeConfirmWindow : Window
    {
        public SynchronizeConfirmWindow()
        {
            InitializeComponent();
        }

        internal static bool Confirm(Window owner, string direction, DiffFile[] files, bool toTarget, string sourceRoot, string targetRoot)
        {
            var dialog = new SynchronizeConfirmWindow { Owner = owner };
            dialog.SummaryText.Text = direction + "   •   " + files.Length + (files.Length == 1 ? " file" : " files");
            dialog.SourcePathText.Text = sourceRoot;
            dialog.TargetPathText.Text = targetRoot;
            dialog.OperationsList.ItemsSource = files
                .GroupBy(file => DeveloperDifferencerService.Operation(file, toTarget))
                .Select(group => group.Key + "  " + group.Count())
                .ToList();
            dialog.CancelButton.Content = ActionGlyph("\uE711", "Cancel");
            dialog.SyncButton.Content = ActionGlyph(toTarget ? "\uE74B" : "\uE74A", "Synchronize");
            dialog.SyncButton.Background = (Brush)dialog.FindResource(toTarget ? "SourceColor" : "TargetColor");
            dialog.SyncButton.Foreground = toTarget
                ? (Brush)new BrushConverter().ConvertFromString("#20252B")
                : Brushes.White;
            return dialog.ShowDialog() == true;
        }

        private static StackPanel ActionGlyph(string glyph, string label)
        {
            var content = new StackPanel { Orientation = Orientation.Horizontal };
            content.Children.Add(new TextBlock
            {
                Text = glyph,
                FontFamily = new FontFamily("Segoe MDL2 Assets"),
                FontSize = 15,
                VerticalAlignment = VerticalAlignment.Center
            });
            content.Children.Add(new TextBlock { Text = label, Margin = new Thickness(6, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center });
            return content;
        }

        private void Cancel_Click(object sender, RoutedEventArgs e) => DialogResult = false;
        private void Sync_Click(object sender, RoutedEventArgs e) => DialogResult = true;
    }
}
