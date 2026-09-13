using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using DesktopIniManager.Properties;
using DesktopIniManager.Services;
using DesktopIniManager.ViewModels;

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
            dialog.Title = StringOverlay.Get("Differencer_SyncTitle");
            dialog.HeadingText.Text = StringOverlay.Get("Differencer_SyncSelectedTitle");
            string heading = StringOverlay.Get(toTarget ? "Differencer_SourceToTarget" : "Differencer_TargetToSource");
            string unit = StringOverlay.Get(files.Length == 1 ? "Differencer_File" : "Differencer_Files");
            dialog.SummaryText.Text = heading + "   •   " + files.Length + " " + unit;
            dialog.SourceLabelText.Text = StringOverlay.Get("Differencer_Source");
            dialog.TargetLabelText.Text = StringOverlay.Get("Differencer_Target");
            dialog.SourcePathText.Text = sourceRoot;
            dialog.TargetPathText.Text = targetRoot;
            dialog.OperationsList.ItemsSource = files
                .GroupBy(file => DeveloperDifferencerService.Operation(file, toTarget))
                .Select(group => LocalizedOperation(group.Key) + "  " + group.Count())
                .ToList();
            dialog.WarningText.Text = StringOverlay.Get("Differencer_OverwriteWarning");
            dialog.DirectionLeftIcon.Source = DifferencerStatusIcons.GetCustomIcon(toTarget ? 76 : 74);
            dialog.DirectionRightIcon.Source = DifferencerStatusIcons.GetCustomIcon(toTarget ? 75 : 65);
            dialog.CancelButton.Content = IconContent(DifferencerStatusIcons.GetCustomIcon(25));
            dialog.CancelButton.ToolTip = StringOverlay.Get("Common_Cancel");
            dialog.SyncButton.Content = ActionGlyph(toTarget ? "\uE74B" : "\uE74A", StringOverlay.Get("Differencer_Synchronize"));
            dialog.SyncButton.Background = (Brush)dialog.FindResource(toTarget ? "SourceColor" : "TargetColor");
            dialog.SyncButton.Foreground = toTarget
                ? (Brush)new BrushConverter().ConvertFromString("#20252B")
                : Brushes.White;
            return dialog.ShowDialog() == true;
        }

        private static string LocalizedOperation(string operation)
        {
            if (operation == "Delete") return StringOverlay.Get("Differencer_Delete");
            if (operation == "Copy") return StringOverlay.Get("Differencer_Copy");
            return StringOverlay.Get("Differencer_Overwrite");
        }

        private static Image IconContent(ImageSource icon)
        {
            var image = new Image
            {
                Source = icon,
                Stretch = Stretch.Uniform,
                VerticalAlignment = VerticalAlignment.Center
            };
            RenderOptions.SetBitmapScalingMode(image, BitmapScalingMode.HighQuality);
            return image;
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
