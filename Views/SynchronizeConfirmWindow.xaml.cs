using DesktopIniManager.Properties;
using DesktopIniManager.Services;
using DesktopIniManager.ViewModels;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;

namespace DesktopIniManager.Views;

internal partial class SynchronizeConfirmWindow
{
    /// <summary>
    ///     同期確認ダイアログを作成します。
    /// </summary>
    public SynchronizeConfirmWindow()
    {
        InitializeComponent();
    }

    /// <summary>
    ///     同期するファイルとフォルダーの一覧を表示し、実行するかどうかを確認します。
    /// </summary>
    /// <param name="owner">オーナー ウィンドウ。</param>
    /// <param name="direction">同期の方向の表示文字列。</param>
    /// <param name="files">同期するファイル。</param>
    /// <param name="folders">同期するフォルダー。</param>
    /// <param name="toTarget">比較元から比較先へ同期する場合は true。</param>
    /// <param name="sourceRoot">比較元のルート。</param>
    /// <param name="targetRoot">比較先のルート。</param>
    /// <returns>実行する場合は true。</returns>
    internal static bool Confirm(Window owner, string direction, DiffFile[] files, DiffFolderSync[] folders,
        bool toTarget, string sourceRoot, string targetRoot)
    {
        return Confirm(owner, direction, files, folders, toTarget, sourceRoot, targetRoot, false, out _, out _);
    }

    /// <summary>
    ///     同期または ZIP 出力の対象と操作の一覧を表示し、実行するかどうかを確認します。ZIP の場合は出力先とファイル名も指定させます。
    /// </summary>
    /// <param name="owner">オーナー ウィンドウ。</param>
    /// <param name="direction">処理の方向の表示文字列。</param>
    /// <param name="files">対象のファイル。</param>
    /// <param name="folders">対象のフォルダー。</param>
    /// <param name="toTarget">比較元から比較先への処理の場合は true。</param>
    /// <param name="sourceRoot">比較元のルート。</param>
    /// <param name="targetRoot">比較先のルート。</param>
    /// <param name="zipMode">ZIP 出力の場合は true。</param>
    /// <param name="zipFileName">指定された ZIP ファイル名。</param>
    /// <param name="zipFolder">指定された ZIP の出力先フォルダー。</param>
    /// <returns>実行する場合は true。</returns>
    internal static bool Confirm(Window owner, string direction, DiffFile[] files, DiffFolderSync[] folders,
        bool toTarget, string sourceRoot, string targetRoot, bool zipMode, out string zipFileName, out string zipFolder)
    {
        zipFileName = null;
        zipFolder = null;
        var dialog = new SynchronizeConfirmWindow { Owner = owner };
        dialog.Title = StringOverlay.Get(zipMode ? "Differencer_ZipTitle" : "Differencer_SyncTitle");
        dialog.HeadingText.Text =
            StringOverlay.Get(zipMode ? "Differencer_ZipSelectedTitle" : "Differencer_SyncSelectedTitle");
        var heading = zipMode
            ? StringOverlay.Get(toTarget ? "Differencer_ZipSourceToZip" : "Differencer_ZipTargetToZip")
            : StringOverlay.Get(toTarget ? "Differencer_SourceToTarget" : "Differencer_TargetToSource");
        var unit = StringOverlay.Get(files.Length == 1 ? "Differencer_File" : "Differencer_Files");
        folders = folders ?? Array.Empty<DiffFolderSync>();
        dialog.SummaryText.Text = heading + "   \u2022   " + files.Length + " " + unit;
        if (folders.Length > 0)
            dialog.SummaryText.Text += "   \u2022   " + string.Format(StringOverlay.Get(folders.Length == 1 ? "Sync_FolderSingular" : "Sync_FolderPlural"), folders.Length);
        dialog.SourceLabelText.Text = StringOverlay.Get("Differencer_Source");
        dialog.TargetLabelText.Text = StringOverlay.Get("Differencer_Target");
        dialog.SourcePathText.Text = sourceRoot;
        dialog.TargetPathText.Text = targetRoot;
        if (zipMode)
        {
            var packFiles = files.Count(file => (toTarget ? file.Source : file.Target) != null);
            var packFolders = folders.Count(folder => toTarget ? folder.SourceExists : folder.TargetExists);
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
            dialog.ZipPathBox.Text = FirstHistoryOrDefault(dialog.ZipPathBox,
                Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory));
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
            dialog.SyncButton.Content =
                ActionGlyph(toTarget ? "\uE74B" : "\uE74A", StringOverlay.Get("Differencer_Synchronize"));
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
            MessageBox.Show(owner, StringOverlay.Get("Differencer_ZipInvalidName"),
                StringOverlay.Get("Differencer_ZipTitle"), MessageBoxButton.OK, MessageBoxImage.Warning);
            return false;
        }

        if (string.IsNullOrWhiteSpace(zipFolder) || zipFolder.IndexOfAny(Path.GetInvalidPathChars()) >= 0)
        {
            MessageBox.Show(owner, StringOverlay.Get("Differencer_ZipInvalidPath"),
                StringOverlay.Get("Differencer_ZipTitle"), MessageBoxButton.OK, MessageBoxImage.Warning);
            return false;
        }

        dialog.ZipFileNameBox.CommitHistory();
        dialog.ZipPathBox.CommitHistory();
        return true;
    }

    /// <summary>
    ///     ルート フォルダー名と現在の日時から、既定の ZIP ファイル名を作成します。
    /// </summary>
    /// <param name="root">ルート フォルダー。</param>
    /// <returns>ZIP ファイル名。</returns>
    private static string CreateDefaultZipFileName(string root)
    {
        var name = Path.GetFileName((root ?? string.Empty).TrimEnd(Path.DirectorySeparatorChar,
            Path.AltDirectorySeparatorChar));
        if (string.IsNullOrWhiteSpace(name)) name = "root";
        foreach (var invalid in Path.GetInvalidFileNameChars())
            name = name.Replace(invalid, '_');
        return name + "_" + DateTime.Now.ToString("yyyyMMdd_HHmmss") + ".zip";
    }

    /// <summary>
    ///     入力欄の履歴の先頭の値を取得します。
    /// </summary>
    /// <param name="box">履歴付きの入力欄。</param>
    /// <param name="fallback">履歴がない場合の値。</param>
    /// <returns>履歴の先頭の値、または既定値。</returns>
    private static string FirstHistoryOrDefault(HistoryTextBox box, string fallback)
    {
        if (box == null) return fallback;
        try
        {
            var store = new InputHistoryStore(Path.Combine(AppSlot.Directory, "input-history"));
            var entries = store.Load(box.HistoryKey);
            if (entries != null && entries.Count > 0 && !string.IsNullOrWhiteSpace(entries[0]))
                return entries[0];
        }
        catch (Exception ex)
        {
            System.Diagnostics.Trace.TraceWarning("Unable to load ZIP path history: {0}", ex);
        }

        return fallback;
    }

    /// <summary>
    ///     フォルダーの同期で行う操作 (作成または削除) を判定します。
    /// </summary>
    /// <param name="folder">同期するフォルダー。</param>
    /// <param name="toTarget">比較元から比較先へ同期する場合は true。</param>
    /// <returns>"CreateDir"、"DeleteDir"、または操作がない場合は null。</returns>
    private static string FolderOperation(DiffFolderSync folder, bool toTarget)
    {
        var fromExists = toTarget ? folder.SourceExists : folder.TargetExists;
        var toExists = toTarget ? folder.TargetExists : folder.SourceExists;
        if (fromExists && !toExists) return "CreateDir";
        if (!fromExists && toExists) return "DeleteDir";
        return null;
    }

    /// <summary>
    ///     操作名を表示用の文字列に変換します。
    /// </summary>
    /// <param name="operation">操作名。</param>
    /// <returns>表示用の文字列。</returns>
    private static string LocalizedOperation(string operation)
    {
        if (operation == "Delete") return StringOverlay.Get("Differencer_Delete");
        if (operation == "Copy") return StringOverlay.Get("Differencer_Copy");
        if (operation == "CreateDir") return StringOverlay.Get("Sync_CreateFolder");
        if (operation == "DeleteDir") return StringOverlay.Get("Sync_DeleteFolder");
        return StringOverlay.Get("Differencer_Overwrite");
    }

    /// <summary>
    ///     アイコンを表示する Image を作成します。
    /// </summary>
    /// <param name="icon">表示するアイコン。</param>
    /// <returns>作成した Image。</returns>
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

    /// <summary>
    ///     Segoe MDL2 Assets の記号とラベルを並べた表示を作成します。
    /// </summary>
    /// <param name="glyph">表示する記号。</param>
    /// <param name="label">ラベル。</param>
    /// <returns>作成したパネル。</returns>
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
        content.Children.Add(new TextBlock
        { Text = label, Margin = new Thickness(6, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center });
        return content;
    }

    /// <summary>
    ///     ZIP の出力先フォルダーを選ぶダイアログを表示します。
    /// </summary>
    /// <param name="sender">イベントの送信元。</param>
    /// <param name="e">イベントデータ。</param>
    private void BrowseZipPath_Click(object sender, RoutedEventArgs e)
    {
        var initial = string.IsNullOrWhiteSpace(ZipPathBox.Text)
            ? Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory)
            : ZipPathBox.Text;
        var path = NativeFolderPicker.Show(new WindowInteropHelper(this).Handle, initial,
            StringOverlay.Get("Differencer_ZipPath"));
        if (!string.IsNullOrEmpty(path))
            ZipPathBox.Text = path;
    }

    /// <summary>
    ///     ダイアログを取り消します。
    /// </summary>
    /// <param name="sender">イベントの送信元。</param>
    /// <param name="e">イベントデータ。</param>
    private void Cancel_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
    }

    /// <summary>
    ///     実行を確定してダイアログを閉じます。
    /// </summary>
    /// <param name="sender">イベントの送信元。</param>
    /// <param name="e">イベントデータ。</param>
    private void Sync_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = true;
    }
}