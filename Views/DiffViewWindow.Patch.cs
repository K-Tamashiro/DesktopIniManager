using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using DesktopIniManager.Properties;
using DesktopIniManager.Services;
using DesktopIniManager.ViewModels;
using Microsoft.Win32;

namespace DesktopIniManager.Views;

internal sealed partial class DiffViewWindow
{
    private Canvas _patchMoveButtons;
    private TextBox _patchRootText, _patchCommitText;
    private Expander _patchCommitExpander;
    private ComboBox _patchSourceBox;
    private bool _selectingPatch;

    /// <summary>
    ///     パッチ表示のときだけパッチ用のコントロール (パッチ ファイルとリポジトリの選択欄など) を表示し、初回は作成します。
    /// </summary>
    private void UpdatePatchControls()
    {
        PatchControls.Visibility = ViewModel.IsPatch ? Visibility.Visible : Visibility.Collapsed;
        if (!ViewModel.IsPatch) return;
        if (_patchSourceBox == null)
        {
            var selectors = new Grid();
            selectors.ColumnDefinitions.Add(new ColumnDefinition());
            selectors.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(34) });
            selectors.ColumnDefinitions.Add(new ColumnDefinition());
            for (var side = 0; side < 2; side++)
            {
                var panel = new DockPanel();
                // Patch file: icon 159. Target side selects the repository folder (icon 75);
                // the target file is the repository path plus the patch's relative path.
                var label = side == 0 ? "Patch…" : "Repository…";
                var icon = new Image
                {
                    Source = DifferencerStatusIcons.GetCustomIcon(side == 0 ? 159 : 75),
                    Stretch = Stretch.Uniform,
                    VerticalAlignment = VerticalAlignment.Center
                };
                RenderOptions.SetBitmapScalingMode(icon, BitmapScalingMode.HighQuality);
                var browse = new Button
                {
                    Content = icon,
                    Style = TryFindResource("IconButton") as Style,
                    Background = Brushes.Transparent,
                    ToolTip = side == 0 ? "Open patch file" : "Select repository folder",
                    VerticalAlignment = VerticalAlignment.Center
                };
                System.Windows.Automation.AutomationProperties.SetName(browse, label);
                DockPanel.SetDock(browse, Dock.Left);
                panel.Children.Add(browse);
                if (side == 0)
                {
                    var combo = new ComboBox
                    { Style = (Style)FindResource("LanguageComboStyle"), Margin = new Thickness(6, 0, 0, 0) };
                    panel.Children.Add(combo);
                    _patchSourceBox = combo;
                    browse.Click += async (_, _) => await OpenPatchAsync();
                    combo.SelectionChanged += PatchSelectionChanged;
                }
                else
                {
                    _patchRootText = new TextBox
                    {
                        IsReadOnly = true,
                        Margin = new Thickness(6, 0, 0, 0),
                        Padding = new Thickness(8, 4, 8, 4),
                        VerticalContentAlignment = VerticalAlignment.Center,
                        TextWrapping = TextWrapping.NoWrap
                    };
                    _patchRootText.SetResourceReference(Control.BackgroundProperty, "CardBackground");
                    _patchRootText.SetResourceReference(Control.BorderBrushProperty, "Line");
                    _patchRootText.SetResourceReference(Control.ForegroundProperty, "Ink");
                    panel.Children.Add(_patchRootText);
                    browse.Click += async (_, _) => await BrowsePatchRepositoryAsync();
                }

                Grid.SetColumn(panel, side * 2);
                selectors.Children.Add(panel);
            }

            PatchControls.Children.Add(selectors);
            // Read-only text box: same look as the former text, but selectable and copyable.
            _patchCommitText = new TextBox
            {
                IsReadOnly = true,
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(8),
                Padding = new Thickness(0),
                BorderThickness = new Thickness(0),
                Background = Brushes.Transparent,
                MaxHeight = 160,
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto
            };
            _patchCommitExpander = new Expander
            {
                Header = "Commit message",
                Content = _patchCommitText,
                // Restore the last state; it is saved on every change.
                IsExpanded = SettingsService.LoadDiffFlag("commit-expanded", true)
            };
            _patchCommitExpander.Expanded += (_, _) => SettingsService.SaveDiffFlag("commit-expanded", true);
            _patchCommitExpander.Collapsed += (_, _) => SettingsService.SaveDiffFlag("commit-expanded", false);
            // Follow the theme: the default black text is unreadable on the dark background.
            _patchCommitExpander.SetResourceReference(Control.ForegroundProperty, "Ink");
            _patchCommitText.SetResourceReference(Control.ForegroundProperty, "Ink");
            PatchControls.Children.Add(_patchCommitExpander);
        }

        _selectingPatch = true;
        try
        {
            var entries = ViewModel.Patch.Files;
            if (_patchSourceBox is { } sourceBox)
            {
                sourceBox.ItemsSource = entries;
                sourceBox.SelectedIndex = ViewModel.PatchIndex;
            }
            _patchRootText.Text = ViewModel.PatchRoot ?? "No target repository selected";
            _patchRootText.ToolTip = ViewModel.PatchRoot;
            _patchCommitText.Text = string.Join(Environment.NewLine,
                new[] { ViewModel.Patch.CommitTitle, ViewModel.Patch.Author, ViewModel.Patch.CommitMessage }
                    .Where(value => !string.IsNullOrWhiteSpace(value)));
            _patchCommitExpander.Visibility =
                _patchCommitText.Text.Length == 0 ? Visibility.Collapsed : Visibility.Visible;
        }
        finally
        {
            _selectingPatch = false;
        }
    }

    /// <summary>
    ///     パッチの適用先リポジトリを選ぶダイアログを表示し、選んだフォルダーを適用先にして表示を更新します。
    /// </summary>
    private async Task BrowsePatchRepositoryAsync()
    {
        if (_renderingContent || ViewModel.CurrentPatch == null) return;
        try
        {
            var root = NativeFolderPicker.Show(new WindowInteropHelper(this).Handle, ViewModel.PatchRoot,
                "Patch target repository");
            if (root == null) return;
            ViewModel.SelectPatchRoot(root);
            await LoadContent(false, false);
        }
        catch (Exception ex)
        {
            ViewModel.ReportError(ex);
        }
    }

    /// <summary>
    ///     パッチ内のファイルの選択が変わったときに、そのファイルを表示します。
    /// </summary>
    /// <param name="sender">選択欄のコンボ ボックス。</param>
    /// <param name="e">イベントデータ。</param>
    private async void PatchSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_selectingPatch || _renderingContent || sender is not ComboBox { SelectedIndex: >= 0 } combo) return;
        try
        {
            ViewModel.SelectPatchFile(combo.SelectedIndex);
            await LoadContent(false, false);
        }
        catch (Exception ex)
        {
            ViewModel.ReportError(ex);
        }
    }

    /// <summary>
    ///     未保存の変更を確認したうえで、パッチ ファイルを選んで開きます。
    /// </summary>
    private async Task OpenPatchAsync()
    {
        if (_renderingContent || !await ConfirmTextChangesAsync()) return;
        var dialog = new OpenFileDialog
        { Filter = "Patch / Diff|*.patch;*.diff;*.rej|All files|*.*", CheckFileExists = true };
        if (dialog.ShowDialog(this) != true) return;
        try
        {
            if (await ViewModel.DropFilesAsync(new[] { dialog.FileName }, true)) await LoadContent();
        }
        catch (Exception ex)
        {
            ViewModel.ReportError(ex);
        }
    }

    /// <summary>
    ///     パッチの適用先ファイルを選びます。新規ファイルの場合は保存先を指定します。
    /// </summary>
    /// <returns>適用先を設定した場合は true。</returns>
    private async Task<bool> BrowsePatchTargetAsync()
    {
        if (_renderingContent || ViewModel.CurrentPatch == null) return false;
        try
        {
            FileDialog dialog = ViewModel.CurrentPatch.Entry.IsNew
                ? new SaveFileDialog
                {
                    FileName = Path.GetFileName(ViewModel.CurrentPatch.Entry.RelativePath) ?? string.Empty,
                    OverwritePrompt = true
                }
                : new OpenFileDialog { CheckFileExists = true };
            if (dialog.ShowDialog(this) != true) return false;
            if (!ViewModel.SelectPatchTarget(dialog.FileName))
            {
                var root = NativeFolderPicker.Show(new WindowInteropHelper(this).Handle,
                    Path.GetDirectoryName(dialog.FileName),
                    "Select the repository root matching the patch relative path");
                if (root == null) return false;
                ViewModel.SelectPatchRoot(root);
            }

            await LoadContent(false, false);
            return true;
        }
        catch (Exception ex)
        {
            ViewModel.ReportError(ex);
            return false;
        }
    }

    /// <summary>
    ///     パッチ表示のペインに、ハンクの適用位置を上下に移動するボタン用のレイヤーを追加します。
    /// </summary>
    /// <param name="grid">ペインを含むグリッド。</param>
    /// <param name="pane">比較先のペイン。</param>
    private void AddPatchMoveButtons(Grid grid, RichTextBox pane)
    {
        // Always create the layer in patch mode: a target can be attached after the
        // panes exist, and RefreshPatchMoveButtons then adds the buttons.
        if (!ViewModel.IsPatch) return;
        _patchMoveButtons = new Canvas
        { Width = 48, ClipToBounds = true, HorizontalAlignment = HorizontalAlignment.Right };
        Grid.SetColumn(_patchMoveButtons, 1);
        grid.Children.Add(_patchMoveButtons);
        RefreshPatchMoveButtons();
        pane.AddHandler(ScrollViewer.ScrollChangedEvent,
            new ScrollChangedEventHandler((_, _) => PositionPatchMoveButtons()));
        grid.MouseMove += (_, e) =>
        {
            var point = e.GetPosition(pane);
            var offset = FindScroll(pane)?.VerticalOffset ?? 0;
            var row = RowAt(point.Y + offset);
            var selected = _hunkStart >= 0 && _hunkStart < Lines.Count ? Lines[_hunkStart].EditHunkId : -1;
            var hovering = point.X >= 0 && point.X <= pane.ActualWidth && row >= _hunkStart && row < _hunkEnd;
            foreach (var button in _patchMoveButtons.Children.OfType<Button>())
            {
                var (_, _, id, _) = ((int, int, int, bool))button.Tag;
                button.Visibility = hovering && id == selected ? Visibility.Visible : Visibility.Hidden;
            }
        };
        grid.MouseLeave += (_, _) =>
        {
            foreach (var button in _patchMoveButtons.Children.OfType<Button>()) button.Visibility = Visibility.Hidden;
        };
    }

    /// <summary>
    ///     移動できるハンクごとに、適用位置を上下に移動するボタンを作り直します。
    /// </summary>
    private void RefreshPatchMoveButtons()
    {
        if (_patchMoveButtons == null || !ViewModel.IsPatch) return;
        _patchMoveButtons.Children.Clear();
        if (ViewModel.CurrentPatch?.CanApply != true) return;
        var seen = new HashSet<int>();
        foreach (var row in Hunks)
        {
            var id = Lines[row].EditHunkId;
            if (id < 0 || !seen.Add(id)) continue;
            // Buttons sit on the run's first and last rows, like the yellow outline;
            // they move the whole patch hunk the run belongs to.
            var last = Lines.FindLastIndex(line => line.EditHunkId == id);
            foreach (var delta in new[] { -1, 1 })
            {
                var button = new Button
                {
                    Content = new Image
                    {
                        Source = DifferencerStatusIcons.GetCustomIcon(delta < 0 ? 157 : 158),
                        Stretch = Stretch.Uniform
                    },
                    Width = 22,
                    Height = 22,
                    Visibility = Visibility.Hidden,
                    Padding = new Thickness(0),
                    Tag = (delta < 0 ? row : last, delta, id, row == last),
                    ToolTip = delta < 0 ? "Move hunk up one line" : "Move hunk down one line",
                    IsEnabled = ViewModel.CurrentPatch.CanMove(ViewModel.CurrentPatch.HunkOfRun(id), delta)
                };
                button.Click += async (_, e) =>
                {
                    e.Handled = true;
                    if (_renderingContent) return;
                    ViewModel.MovePatchHunk(id, delta);
                    await RenderTextEditsAsync();
                };
                _patchMoveButtons.Children.Add(button);
            }
        }

        PositionPatchMoveButtons();
    }

    /// <summary>
    ///     スクロール位置に合わせて、ハンクの移動ボタンを配置します。
    /// </summary>
    private void PositionPatchMoveButtons()
    {
        if (_patchMoveButtons == null) return;
        var offset = _leftList == null ? 0 : FindScroll(_leftList)?.VerticalOffset ?? 0;
        foreach (var button in _patchMoveButtons.Children.OfType<Button>())
        {
            var (row, delta, _, single) = ((int, int, int, bool))button.Tag;
            Canvas.SetTop(button, RowTop(row) - offset + (delta > 0 && !single ? DiffLineHeight - 22 : 0));
            Canvas.SetLeft(button, single && delta < 0 ? 0 : 24);
        }
    }

    /// <summary>
    ///     現在のパッチ ファイルの適用結果を保存します。新規ファイルの場合はエンコーディングと改行コードを選ばせます。
    /// </summary>
    /// <returns>保存した場合は true。</returns>
    private async Task<bool> SavePatchAsync()
    {
        if (ViewModel.CurrentPatch.TargetPath == null && !await BrowsePatchTargetAsync()) return false;
        Encoding encoding = null;
        string ending = null;
        if (ViewModel.CurrentPatch.Original.OriginalBytes == null)
        {
            var dialog = new Window
            {
                Owner = this,
                Title = "New file format",
                SizeToContent = SizeToContent.WidthAndHeight,
                WindowStartupLocation = WindowStartupLocation.CenterOwner,
                ResizeMode = ResizeMode.NoResize
            };
            dialog.SetResourceReference(BackgroundProperty, "WindowBackground");
            dialog.SetResourceReference(ForegroundProperty, "Ink");
            var panel = new StackPanel { Margin = new Thickness(16), MinWidth = 280 };
            var encodings = new ComboBox
            {
                ItemsSource = new[] { "UTF-8", "UTF-8 BOM", "S-JIS", "UTF-16 LE" },
                SelectedIndex = 0,
                Style = (Style)FindResource("LanguageComboStyle")
            };
            var endings = new ComboBox
            {
                ItemsSource = new[] { "CRLF", "LF", "CR" },
                SelectedIndex = 0,
                Style = (Style)FindResource("LanguageComboStyle"),
                Margin = new Thickness(0, 8, 0, 8)
            };
            var save = new Button { Content = StringOverlay.Get("Diff_Save"), IsDefault = true };
            save.Click += (_, _) => dialog.DialogResult = true;
            panel.Children.Add(encodings);
            panel.Children.Add(endings);
            panel.Children.Add(save);
            dialog.Content = panel;
            if (dialog.ShowDialog() != true) return false;
            encoding = encodings.SelectedIndex switch
            {
                1 => new UTF8Encoding(true, true),
                2 => CodePagesEncodingProvider.Instance.GetEncoding(932),
                3 => new UnicodeEncoding(false, true, true),
                _ => new UTF8Encoding(false, true)
            };
            ending = endings.SelectedIndex switch { 1 => "\n", 2 => "\r", _ => "\r\n" };
        }

        try
        {
            ViewModel.SavePatchTarget(encoding, ending);
            await RenderTextEditsAsync();
            return true;
        }
        catch (Exception ex)
        {
            ViewModel.ReportError(ex);
            return false;
        }
    }

    /// <summary>
    ///     未保存のパッチ ファイルをすべて保存し、元の選択に戻します。
    /// </summary>
    /// <returns>すべて保存した場合は true。途中で取り消した場合は false。</returns>
    private async Task<bool> SaveAllPatchFilesAsync()
    {
        var original = ViewModel.PatchIndex;
        for (var i = 0; i < ViewModel.PatchFiles.Count; i++)
        {
            if (!ViewModel.PatchFiles[i].IsDirty) continue;
            ViewModel.SelectPatchFile(i);
            await LoadContent(false, false);
            if (!await SavePatchAsync()) return false;
        }

        ViewModel.SelectPatchFile(original);
        await LoadContent(false, false);
        return true;
    }
}