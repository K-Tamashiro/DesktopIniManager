using DesktopIniManager.Models;
using DesktopIniManager.Services;
using DesktopIniManager.ViewModels;
using DesktopIniManager.Views;
using DesktopIniManager.Views.Presenter;
using System;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Media;
using System.Windows.Shapes;
using System.Windows.Threading;

namespace DesktopIniManager;

public partial class MainWindow
{
    private static readonly string[] SlotBorderColors = { "#4DA3FF", "#F0C14A", "#B07CFF", "#FF6B6B", "#3DDC97" };
    private readonly string[] _displayedSlotStates = new string[5];
    private readonly SemaphoreSlim _folderIconWorkers = new(4);
    private readonly MainWindowPresentationService _presentation;

    public MainWindow() : this(null)
    {
    }

    internal MainWindow(StartupState startup)
    {
        ThemeService.Apply(startup?.Theme ?? SettingsService.LoadTheme());
        InitializeComponent();
        ClearQueryButton.Click += (_, _) =>
            Dispatcher.BeginInvoke(new Action(() => QueryBox.Focus()), DispatcherPriority.Input);
        ClearFolderFilterButton.Click += (_, _) =>
            Dispatcher.BeginInvoke(new Action(() => FolderFilterBox.Focus()), DispatcherPriority.Input);
        _presentation = new MainWindowPresentationService(this, startup);
        RestoreListLayout();
        AppSlot.FrontRequested += () =>
            Dispatcher.BeginInvoke(new Action(() => WindowActivationService.BringToFront(this, true)));
        SlotButton1.Click += (_, _) => LaunchSlot(1);
        SlotButton2.Click += (_, _) => LaunchSlot(2);
        SlotButton3.Click += (_, _) => LaunchSlot(3);
        SlotButton4.Click += (_, _) => LaunchSlot(4);
        SlotButton5.Click += (_, _) => LaunchSlot(5);
        Loaded += (_, _) => RefreshSlotButtons();
        Activated += (_, _) => RefreshSlotButtons();
        var slotTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        slotTimer.Tick += (_, _) => RefreshSlotButtons();
        slotTimer.Start();
        Closed += (_, _) =>
        {
            slotTimer.Stop();
            SaveListLayout();
        };
    }

    internal MainWindowViewModel ViewModel => _presentation.ViewModel;

    private void AboutTitle_Click(object sender, RoutedEventArgs e)
    {
        new AboutWindow { Owner = this, Icon = Icon }.ShowDialog();
    }

    private void LaunchSlot(int slot)
    {
        var state = AppSlot.StateText(slot);
        if (state == "In Use") return;
        if (state == "Open")
        {
            AppSlot.Activate(slot);
            return;
        }

        var exe = Environment.ProcessPath;
        if (exe is null) return;

        Process.Start(new ProcessStartInfo(exe, "-d" + slot)
        {
            UseShellExecute = false
        });
    }

    private void RefreshSlotButtons()
    {
        var buttons = new[] { SlotButton1, SlotButton2, SlotButton3, SlotButton4, SlotButton5 };
        for (var index = 0; index < buttons.Length; index++)
        {
            if (buttons[index] == null) continue;
            var state = AppSlot.StateText(index + 1);
            if (_displayedSlotStates[index] == state) continue;
            buttons[index].Tag = state;
            var color = (Color?)ColorConverter.ConvertFromString(SlotBorderColors[index]) ?? Colors.Transparent;
            buttons[index].BorderBrush = new SolidColorBrush(color);
            if (state == "In Use")
            {
                var tint = (Color?)ColorConverter.ConvertFromString(SlotBorderColors[index]) ?? Colors.Transparent;
                tint.A = 64;
                buttons[index].Background = new SolidColorBrush(tint);
            }
            else
            {
                buttons[index].ClearValue(BackgroundProperty);
            }

            buttons[index].ApplyTemplate();
            var mark = buttons[index].Template.FindName("SlotMark", buttons[index]) as Border;
            var folder = buttons[index].Template.FindName("SlotFolder", buttons[index]) as Path;
            if (mark != null) mark.Opacity = state == "In Use" ? 0.55 : 0.18;
            if (folder != null) folder.Visibility = state == "In Use" ? Visibility.Visible : Visibility.Collapsed;
            if (mark != null && folder != null) _displayedSlotStates[index] = state;
        }
    }

    private void RestoreListLayout()
    {
        var values = SettingsService.LoadMainListLayout();
        var columns = new[] { FileIconColumn, FileNameColumn, FileTypeColumn };
        if (values == null || values.Length != columns.Length + 2) return;
        // Preserve the split ratio when the window is resized or moved to another display.
        if (values[0] > 0 && values[1] > 0)
        {
            FolderPaneColumn.Width = new GridLength(values[0], GridUnitType.Star);
            FilePaneColumn.Width = new GridLength(values[1], GridUnitType.Star);
        }

        for (var i = 0; i < columns.Length; i++)
            columns[i].Width = values[i + 2];
    }

    private void SaveListLayout()
    {
        if (FolderPaneColumn.ActualWidth <= 0 || FilePaneColumn.ActualWidth <= 0) return;
        var columns = new[] { FileIconColumn, FileNameColumn, FileTypeColumn };
        SettingsService.SaveMainListLayout(new[] { FolderPaneColumn.ActualWidth, FilePaneColumn.ActualWidth }
            .Concat(columns.Select(column => double.IsNaN(column.Width) ? column.ActualWidth : column.Width))
            .ToArray());
    }

    private void FolderIcon_Loaded(object sender, RoutedEventArgs e)
    {
        RefreshFolderIcon((Image)sender);
    }

    private void FolderIcon_DataContextChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (((Image)sender).IsLoaded) RefreshFolderIcon((Image)sender);
    }

    private void FolderIcon_TargetUpdated(object sender, DataTransferEventArgs e)
    {
        if (((Image)sender).IsLoaded) RefreshFolderIcon((Image)sender);
    }

    private async void RefreshFolderIcon(Image image)
    {
        if (!(image.DataContext is FolderMatch folder) || folder.IsLazyPlaceholder ||
            !folder.IsActionable || !string.IsNullOrEmpty(folder.SolutionFile) ||
            (folder.Reason ?? "").StartsWith("Solution", StringComparison.OrdinalIgnoreCase)) return;
        var original = folder.IconPreview;
        var reason = folder.Reason;
        await _folderIconWorkers.WaitAsync();
        try
        {
            if (!image.IsLoaded || !ReferenceEquals(image.DataContext, folder)) return;
            var icon = await Task.Run(() => FolderIconService.GetStateIcon(folder.Path, reason));
            // A user may have applied an icon or changed selection while the directory was read.
            if (icon != null && image.IsLoaded && ReferenceEquals(image.DataContext, folder) &&
                folder.Reason == reason && ReferenceEquals(folder.IconPreview, original)) folder.IconPreview = icon;
        }
        finally
        {
            _folderIconWorkers.Release();
        }
    }
}