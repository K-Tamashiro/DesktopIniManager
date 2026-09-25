using System;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Collections.Generic;
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

        internal static bool Confirm(Window owner, string direction, DiffFile[] files, DiffFolderSync[] folders, bool toTarget, string sourceRoot, string targetRoot)
        {
            string zipFileName;
            string zipFolder;
            return Confirm(owner, direction, files, folders, toTarget, sourceRoot, targetRoot, false, out zipFileName, out zipFolder);
        }

        internal static bool Confirm(Window owner, string direction, DiffFile[] files, DiffFolderSync[] folders, bool toTarget, string sourceRoot, string targetRoot, bool zipMode, out string zipFileName, out string zipFolder)
        {
            zipFileName = null;
            zipFolder = null;
            var dialog = new SynchronizeConfirmWindow { Owner = owner };
            dialog.Title = StringOverlay.Get(zipMode ? "Differencer_ZipTitle" : "Differencer_SyncTitle");
            dialog.HeadingText.Text = StringOverlay.Get(zipMode ? "Differencer_ZipSelectedTitle" : "Differencer_SyncSelectedTitle");
            string heading = zipMode
                ? StringOverlay.Get(toTarget ? "Differencer_ZipSourceToZip" : "Differencer_ZipTargetToZip")
                : StringOverlay.Get(toTarget ? "Differencer_SourceToTarget" : "Differencer_TargetToSource");
            string unit = StringOverlay.Get(files.Length == 1 ? "Differencer_File" : "Differencer_Files");
            folders = folders ?? Array.Empty<DiffFolderSync>();
            dialog.SummaryText.Text = heading + "   •   " + files.Length + " " + unit;
            if (folders.Length > 0)
                dialog.SummaryText.Text += "   •   " + folders.Length + (folders.Length == 1 ? " folder" : " folders");
            dialog.SourceLabelText.Text = StringOverlay.Get("Differencer_Source");
            dialog.TargetLabelText.Text = StringOverlay.Get("Differencer_Target");
            dialog.SourcePathText.Text = sourceRoot;
            dialog.TargetPathText.Text = targetRoot;
            if (zipMode)
            {
                int packFiles = files.Count(file => (toTarget ? file.Source : file.Target) != null);
                int packFolders = folders.Count(folder => toTarget ? folder.SourceExists : folder.TargetExists);
                var zipOperations = new List<string>
                {
                    StringOverlay.Get("Differencer_ZipTitle") + "  " + packFiles
                };
                if (packFolders > 0)
                    zipOperations.Add(StringOverlay.Get("Differencer_ZipFolders") + "  " + packFolders);
                dialog.OperationsList.ItemsSource = zipOperations;
                dialog.WarningText.Text = StringOverlay.Get("Differencer_ZipWarning");
                dialog.DirectionLeftIcon.Source = DifferencerStatusIcons.GetCustomIcon(toTarget ? 76 : 92);
                dialog.DirectionRightIcon.Source = DifferencerStatusIcons.GetCustomIcon(toTarget ? 93 : 65);
                dialog.ZipOptionsPanel.Visibility = Visibility.Visible;
                dialog.ZipFileNameLabel.Text = StringOverlay.Get("Differencer_ZipFileName");
                dialog.ZipPathLabel.Text = StringOverlay.Get("Differencer_ZipPath");
                dialog.ZipFileNameBox.Text = CreateDefaultZipFileName(toTarget ? sourceRoot : targetRoot);
                dialog.ZipPathBox.Text = FirstHistoryOrDefault(dialog.ZipPathBox, Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory));
                dialog.BrowseZipPathButton.Content = IconContent(DifferencerStatusIcons.GetCustomIcon(61));
                dialog.BrowseZipPathButton.ToolTip = StringOverlay.Get("Differencer_ZipBrowsePath");
                dialog.SyncButton.Content = ActionGlyph("\uE74E", StringOverlay.Get("Differencer_ZipStart"));
                dialog.SyncButton.Background = (Brush)dialog.FindResource(toTarget ? "SourceColor" : "TargetColor");
                dialog.SyncButton.Foreground = toTarget
                    ? (Brush)new BrushConverter().ConvertFromString("#20252B")
                    : Brushes.White;
            }
            else
            {
                var operations = files
                    .GroupBy(file => DeveloperDifferencerService.Operation(file, toTarget))
                    .Select(group => new { Operation = group.Key, Count = group.Count() })
                    .Concat(folders
                        .Where(folder => folder.SourceExists != folder.TargetExists)
                        .GroupBy(folder => FolderOperation(folder, toTarget))
                        .Select(group => new { Operation = group.Key, Count = group.Count() }))
                    .Where(item => !string.IsNullOrEmpty(item.Operation))
                    .GroupBy(item => item.Operation)
                    .Select(group => LocalizedOperation(group.Key) + "  " + group.Sum(item => item.Count))
                    .ToList();
                dialog.OperationsList.ItemsSource = operations;
                dialog.WarningText.Text = StringOverlay.Get("Differencer_OverwriteWarning");
                dialog.DirectionLeftIcon.Source = DifferencerStatusIcons.GetCustomIcon(toTarget ? 76 : 74);
                dialog.DirectionRightIcon.Source = DifferencerStatusIcons.GetCustomIcon(toTarget ? 75 : 65);
                dialog.ZipOptionsPanel.Visibility = Visibility.Collapsed;
                dialog.SyncButton.Content = ActionGlyph(toTarget ? "\uE74B" : "\uE74A", StringOverlay.Get("Differencer_Synchronize"));
                dialog.SyncButton.Background = (Brush)dialog.FindResource(toTarget ? "SourceColor" : "TargetColor");
                dialog.SyncButton.Foreground = toTarget
                    ? (Brush)new BrushConverter().ConvertFromString("#20252B")
                    : Brushes.White;
            }
            dialog.CancelButton.Content = IconContent(DifferencerStatusIcons.GetCustomIcon(25));
            dialog.CancelButton.ToolTip = StringOverlay.Get("Common_Cancel");
            if (dialog.ShowDialog() != true)
                return false;
            if (!zipMode)
                return true;
            zipFileName = (dialog.ZipFileNameBox.Text ?? "").Trim();
            zipFolder = (dialog.ZipPathBox.Text ?? "").Trim();
            if (string.IsNullOrWhiteSpace(zipFileName) || zipFileName.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
            {
                MessageBox.Show(owner, StringOverlay.Get("Differencer_ZipInvalidName"), StringOverlay.Get("Differencer_ZipTitle"), MessageBoxButton.OK, MessageBoxImage.Warning);
                return false;
            }
            if (string.IsNullOrWhiteSpace(zipFolder) || zipFolder.IndexOfAny(Path.GetInvalidPathChars()) >= 0)
            {
                MessageBox.Show(owner, StringOverlay.Get("Differencer_ZipInvalidPath"), StringOverlay.Get("Differencer_ZipTitle"), MessageBoxButton.OK, MessageBoxImage.Warning);
                return false;
            }
            dialog.ZipFileNameBox.CommitHistory();
            dialog.ZipPathBox.CommitHistory();
            return true;
        }

        private static string CreateDefaultZipFileName(string root)
        {
            string name = Path.GetFileName((root ?? string.Empty).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
            if (string.IsNullOrWhiteSpace(name)) name = "root";
            foreach (char invalid in Path.GetInvalidFileNameChars())
                name = name.Replace(invalid, '_');
            return name + "_" + DateTime.Now.ToString("yyyyMMdd_HHmmss") + ".zip";
        }

        private static string FirstHistoryOrDefault(HistoryTextBox box, string fallback)
        {
            if (box == null) return fallback;
            try
            {
                var store = new InputHistoryStore(Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "DesktopIniManager", "input-history"));
                var entries = store.Load(box.HistoryKey);
                if (entries != null && entries.Count > 0 && !string.IsNullOrWhiteSpace(entries[0]))
                    return entries[0];
            }
            catch { }
            return fallback;
        }

        private static string FolderOperation(DiffFolderSync folder, bool toTarget)
        {
            bool fromExists = toTarget ? folder.SourceExists : folder.TargetExists;
            bool toExists = toTarget ? folder.TargetExists : folder.SourceExists;
            if (fromExists && !toExists) return "CreateDir";
            if (!fromExists && toExists) return "DeleteDir";
            return null;
        }

        private static string LocalizedOperation(string operation)
        {
            if (operation == "Delete") return StringOverlay.Get("Differencer_Delete");
            if (operation == "Copy") return StringOverlay.Get("Differencer_Copy");
            if (operation == "CreateDir") return "Create folder";
            if (operation == "DeleteDir") return "Delete folder";
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

        private void BrowseZipPath_Click(object sender, RoutedEventArgs e)
        {
            string initial = string.IsNullOrWhiteSpace(ZipPathBox.Text)
                ? Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory)
                : ZipPathBox.Text;
            string path = NativeFolderPicker.Show(new WindowInteropHelper(this).Handle, initial, StringOverlay.Get("Differencer_ZipPath"));
            if (!string.IsNullOrEmpty(path))
                ZipPathBox.Text = path;
        }

        private void Cancel_Click(object sender, RoutedEventArgs e) => DialogResult = false;
        private void Sync_Click(object sender, RoutedEventArgs e) => DialogResult = true;
    }
}
