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
    private static readonly string[] SlotBorderColors = ["#4DA3FF", "#F0C14A", "#B07CFF", "#FF6B6B", "#3DDC97"];
    private readonly string[] _displayedSlotStates = new string[5];
    private readonly SemaphoreSlim _folderIconWorkers = new(4);
    private readonly MainWindowPresentationService _presentation;

    /// <summary>
    ///     起動状態を指定せずにメインウィンドウを作成します。
    /// </summary>
    public MainWindow() : this(null)
    {
    }

    /// <summary>
    ///     起動状態を使ってメインウィンドウを初期化し、プレゼンテーション サービス、スロット ボタン、レイアウトの保存処理を設定します。
    /// </summary>
    /// <param name="startup">事前に読み込んだ起動状態。null の場合は設定から読み込みます。</param>
    internal MainWindow(StartupState startup)
    {
        ThemeService.Apply(startup?.Theme ?? SettingsService.LoadTheme());
        InitializeComponent();
        ApplyFileItemStyles();
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
            AppSlot.Release();
        };
    }

    internal MainWindowViewModel ViewModel => _presentation.ViewModel;

    /// <summary>
    ///     ファイル一覧とアイコン一覧に、直下以外と検索一致を強調するスタイルを適用します。
    /// </summary>
    private void ApplyFileItemStyles()
    {
        FileList.ItemContainerStyle = CreateFileItemStyle(
            (Style)FindResource("FileListItemStyle"),
            typeof(ListViewItem));

        FileIconList.ItemContainerStyle = CreateFileItemStyle(
            (Style)FindResource("FileIconItemStyle"),
            typeof(ListBoxItem));
    }

    /// <summary>
    ///     直下以外のファイルを淡色表示し、検索一致を選択色で強調するトリガー付きスタイルを作成します。
    /// </summary>
    /// <param name="baseStyle">継承元のスタイル。</param>
    /// <param name="targetType">スタイルの対象型。</param>
    /// <returns>作成したスタイル。</returns>
    private static Style CreateFileItemStyle(Style baseStyle, Type targetType)
    {
        var style = new Style(targetType, baseStyle);

        var directChildTrigger = new DataTrigger
        {
            Binding = new Binding(nameof(FileListItem.IsDirectChild)),
            Value = false
        };

        directChildTrigger.Setters.Add(
            new Setter(ForegroundProperty,
                Application.Current.FindResource("Muted")));

        style.Triggers.Add(directChildTrigger);

        var searchMatchTrigger = new DataTrigger
        {
            Binding = new Binding(nameof(FileListItem.IsSearchMatch)),
            Value = true
        };

        searchMatchTrigger.Setters.Add(
            new Setter(BackgroundProperty,
                Application.Current.FindResource("ThemeSelected")));

        style.Triggers.Add(searchMatchTrigger);

        return style;
    }

    /// <summary>
    ///     タイトルのクリックでバージョン情報ウィンドウを表示します。
    /// </summary>
    /// <param name="sender">イベントの送信元。</param>
    /// <param name="e">イベントデータ。</param>
    private void AboutTitle_Click(object sender, RoutedEventArgs e)
    {
        new AboutWindow { Owner = this, Icon = Icon }.ShowDialog();
    }

    /// <summary>
    ///     指定スロットが開いていればアクティブにし、空いていれば新しいプロセスとして起動します。
    /// </summary>
    /// <param name="slot">スロット番号 (1 から 5)。</param>
    private static void LaunchSlot(int slot)
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

    /// <summary>
    ///     各スロットの状態に合わせてスロット ボタンの枠色、背景、マーク表示を更新します。
    /// </summary>
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
            mark?.SetCurrentValue(OpacityProperty, state == "In Use" ? 0.55 : 0.18);
            folder?.SetCurrentValue(VisibilityProperty, state == "In Use" ? Visibility.Visible : Visibility.Collapsed);
            if (mark is not null && folder is not null) _displayedSlotStates[index] = state;
        }
    }

    /// <summary>
    ///     保存されたペイン幅の比率と列幅を復元します。
    /// </summary>
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

    /// <summary>
    ///     現在のペイン幅と列幅を設定に保存します。
    /// </summary>
    private void SaveListLayout()
    {
        if (FolderPaneColumn.ActualWidth <= 0 || FilePaneColumn.ActualWidth <= 0) return;
        var columns = new[] { FileIconColumn, FileNameColumn, FileTypeColumn };
        SettingsService.SaveMainListLayout(
        [
            FolderPaneColumn.ActualWidth, FilePaneColumn.ActualWidth,
            .. columns.Select(column => double.IsNaN(column.Width) ? column.ActualWidth : column.Width)
        ]);
    }

    /// <summary>
    ///     フォルダー アイコンが読み込まれたときにアイコンを更新します。
    /// </summary>
    /// <param name="sender">対象の Image。</param>
    /// <param name="e">イベントデータ。</param>
    private void FolderIcon_Loaded(object sender, RoutedEventArgs e)
    {
        _ = RefreshFolderIcon((Image)sender);
    }

    /// <summary>
    ///     フォルダー アイコンのデータ コンテキストが変わったときにアイコンを更新します。
    /// </summary>
    /// <param name="sender">対象の Image。</param>
    /// <param name="e">イベントデータ。</param>
    private void FolderIcon_DataContextChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (((Image)sender).IsLoaded)
            _ = RefreshFolderIcon((Image)sender);
    }

    /// <summary>
    ///     フォルダー アイコンのバインディング対象が更新されたときにアイコンを更新します。
    /// </summary>
    /// <param name="sender">対象の Image。</param>
    /// <param name="e">イベントデータ。</param>
    private void FolderIcon_TargetUpdated(object sender, DataTransferEventArgs e)
    {
        if (((Image)sender).IsLoaded)
            _ = RefreshFolderIcon((Image)sender);
    }

    /// <summary>
    ///     同時実行数を制限しながらフォルダーの状態アイコンをバックグラウンドで取得し、表示中の項目が変わっていなければ反映します。
    /// </summary>
    /// <param name="image">アイコンを表示する Image。</param>
    private async Task RefreshFolderIcon(Image image)
    {
        try
        {
            if (image.DataContext is not FolderMatch folder || folder.IsLazyPlaceholder ||
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
                if (icon is not null && image.IsLoaded && ReferenceEquals(image.DataContext, folder) &&
                    folder.Reason == reason && ReferenceEquals(folder.IconPreview, original))
                    folder.IconPreview = icon;
            }
            finally
            {
                _folderIconWorkers.Release();
            }
        }
        catch (Exception ex)
        {
            Debug.WriteLine(ex);
        }
    }
}