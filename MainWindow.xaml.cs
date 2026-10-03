using System;
using System.Windows;
using System.Windows.Threading;
using System.Windows.Media;
using System.Linq;
using System.Windows.Controls;
using System.Threading;
using System.Threading.Tasks;
using DesktopIniManager.Models;
using DesktopIniManager.Services;
using DesktopIniManager.ViewModels;
using DesktopIniManager.Views;

namespace DesktopIniManager
{
    public partial class MainWindow : Window
    {
        private readonly MainWindowPresentationService presentation;
        private readonly SemaphoreSlim folderIconWorkers = new SemaphoreSlim(4);
        internal MainWindowViewModel ViewModel => presentation.ViewModel;

        public MainWindow() : this(null) { }

        internal MainWindow(StartupState startup)
        {
            ThemeService.Apply(startup?.Theme ?? SettingsService.LoadTheme());
            InitializeComponent();
            presentation = new MainWindowPresentationService(this, startup);
            RestoreListLayout();
            AppSlot.FrontRequested += () => Dispatcher.BeginInvoke(new Action(() => WindowActivationService.BringToFront(this, true)));
            SlotButton1.Click += (sender, args) => LaunchSlot(1);
            SlotButton2.Click += (sender, args) => LaunchSlot(2);
            SlotButton3.Click += (sender, args) => LaunchSlot(3);
            SlotButton4.Click += (sender, args) => LaunchSlot(4);
            SlotButton5.Click += (sender, args) => LaunchSlot(5);
            Loaded += (sender, args) => RefreshSlotButtons();
            Activated += (sender, args) => RefreshSlotButtons();
            var slotTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
            slotTimer.Tick += (sender, args) => RefreshSlotButtons();
            slotTimer.Start();
            Closed += (sender, args) => { slotTimer.Stop(); SaveListLayout(); };
        }

        private void LaunchSlot(int slot)
        {
            string state = AppSlot.StateText(slot);
            if (state == "In Use") return;
            if (state == "Open")
            {
                AppSlot.Activate(slot);
                return;
            }
            string exe = System.Diagnostics.Process.GetCurrentProcess().MainModule.FileName;
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(exe, "-d" + slot.ToString()) { UseShellExecute = false });
        }

        private static readonly string[] SlotBorderColors = { "#4DA3FF", "#F0C14A", "#B07CFF", "#FF6B6B", "#3DDC97" };
        private readonly string[] displayedSlotStates = new string[5];

        private void RefreshSlotButtons()
        {
            var buttons = new[] { SlotButton1, SlotButton2, SlotButton3, SlotButton4, SlotButton5 };
            for (int index = 0; index < buttons.Length; index++)
            {
                if (buttons[index] == null) continue;
                string state = AppSlot.StateText(index + 1);
                if (displayedSlotStates[index] == state) continue;
                buttons[index].Tag = state;
                buttons[index].BorderBrush = new SolidColorBrush((Color)ColorConverter.ConvertFromString(SlotBorderColors[index]));
                buttons[index].BorderThickness = new Thickness(1.5);
                if (state == "In Use")
                {
                    Color tint = (Color)ColorConverter.ConvertFromString(SlotBorderColors[index]);
                    tint.A = 64;
                    buttons[index].Background = new SolidColorBrush(tint);
                }
                else
                    buttons[index].ClearValue(Control.BackgroundProperty);
                buttons[index].ApplyTemplate();
                var mark = buttons[index].Template.FindName("SlotMark", buttons[index]) as Border;
                var folder = buttons[index].Template.FindName("SlotFolder", buttons[index]) as System.Windows.Shapes.Path;
                if (mark != null) mark.Opacity = state == "In Use" ? 0.55 : 0.18;
                if (folder != null) folder.Visibility = state == "In Use" ? Visibility.Visible : Visibility.Collapsed;
                if (mark != null && folder != null) displayedSlotStates[index] = state;
            }
        }

        private void RestoreListLayout()
        {
            double[] values = SettingsService.LoadMainListLayout();
            var columns = new[] { FileIconColumn, FileNameColumn, FileTypeColumn };
            if (values == null || values.Length != columns.Length + 2) return;
            // Preserve the split ratio when the window is resized or moved to another display.
            if (values[0] > 0 && values[1] > 0)
            {
                FolderPaneColumn.Width = new GridLength(values[0], GridUnitType.Star);
                FilePaneColumn.Width = new GridLength(values[1], GridUnitType.Star);
            }
            for (int i = 0; i < columns.Length; i++)
                columns[i].Width = values[i + 2];
        }

        private void SaveListLayout()
        {
            if (FolderPaneColumn.ActualWidth <= 0 || FilePaneColumn.ActualWidth <= 0) return;
            var columns = new[] { FileIconColumn, FileNameColumn, FileTypeColumn };
            SettingsService.SaveMainListLayout(new[] { FolderPaneColumn.ActualWidth, FilePaneColumn.ActualWidth }
                .Concat(columns.Select(column => double.IsNaN(column.Width) ? column.ActualWidth : column.Width)).ToArray());
        }

        private void FolderIcon_Loaded(object sender, RoutedEventArgs e) => RefreshFolderIcon((Image)sender);

        private void FolderIcon_DataContextChanged(object sender, DependencyPropertyChangedEventArgs e)
        {
            if (((Image)sender).IsLoaded) RefreshFolderIcon((Image)sender);
        }

        private void FolderIcon_TargetUpdated(object sender, System.Windows.Data.DataTransferEventArgs e)
        {
            if (((Image)sender).IsLoaded) RefreshFolderIcon((Image)sender);
        }

        private async void RefreshFolderIcon(Image image)
        {
            if (!(image.DataContext is FolderMatch folder) || folder.IsLazyPlaceholder ||
                !folder.IsActionable || !string.IsNullOrEmpty(folder.SolutionFile) ||
                (folder.Reason ?? "").StartsWith("Solution", System.StringComparison.OrdinalIgnoreCase)) return;
            var original = folder.IconPreview;
            string reason = folder.Reason;
            await folderIconWorkers.WaitAsync();
            try
            {
                if (!image.IsLoaded || !ReferenceEquals(image.DataContext, folder)) return;
                var icon = await Task.Run(() => FolderIconService.GetStateIcon(folder.Path, reason));
                // A user may have applied an icon or changed selection while the directory was read.
                if (icon != null && image.IsLoaded && ReferenceEquals(image.DataContext, folder) &&
                    folder.Reason == reason && ReferenceEquals(folder.IconPreview, original)) folder.IconPreview = icon;
            }
            finally { folderIconWorkers.Release(); }
        }
    }
}
