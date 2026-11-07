using DesktopIniManager.Services;
using System;
using System.Globalization;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;

namespace DesktopIniManager.Views;

internal sealed partial class DiffViewWindow
{
    private FontFamily _textFontFamily;
    private double _textFontSize;

    /// <summary>
    ///     保存されたフォント設定を読み込み、フォント名とサイズの選択欄を初期化します。
    /// </summary>
    private void InitializeTextFontControls()
    {
        var (family, size) = SettingsService.LoadDiffFont();
        var families = Fonts.SystemFontFamilies.Select(font => font.Source)
            .Append(family).Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(name => name, StringComparer.CurrentCultureIgnoreCase).ToArray();
        _textFontFamily = new FontFamily(family);
        _textFontSize = size;
        TextFontFamilyBox.ItemsSource = families;
        TextFontFamilyBox.SelectedItem = families.First(name =>
            string.Equals(name, family, StringComparison.OrdinalIgnoreCase));
        TextFontSizeBox.ItemsSource = new double[] { 8, 9, 10, 11, 12, 13, 14, 16, 18, 20, 24, 28, 32, 36, 48, 60, 72 }
            .Append(size).Distinct().OrderBy(value => value).ToArray();
        TextFontSizeBox.SelectedItem = size;
        TextFontFamilyBox.SelectionChanged += TextFontSelectionChanged;
        TextFontSizeBox.SelectionChanged += TextFontSelectionChanged;
    }

    /// <summary>
    ///     フォントが変更されたときに、先頭に表示している行を保ったまま表示を作り直し、設定を保存します。
    /// </summary>
    /// <param name="sender">イベントの送信元。</param>
    /// <param name="e">イベントデータ。</param>
    private void TextFontSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (TextFontFamilyBox.SelectedItem is not string family ||
            TextFontSizeBox.SelectedItem is not double size || _renderingContent) return;
        try
        {
            // Keep the same top row: measured with the old row geometry, placed with the new.
            var topRow = _leftList == null ? 0 : RowAt(FindScroll(_leftList)?.VerticalOffset ?? 0);
            _textFontFamily = new FontFamily(family);
            _textFontSize = size;
            _sharedTextWidth = MeasureSharedTextWidth();
            // Keep the same documents, edit history and marker options. Only their
            // typography changes; the top visible row stays at the same position.
            _scrolling = true;
            foreach (var pane in new[] { _leftList, _rightList })
            {
                if (pane == null) continue;
                var scroll = FindScroll(pane);
                var vertical = RowTop(topRow);
                var horizontal = scroll?.HorizontalOffset ?? 0;
                pane.FontFamily = _textFontFamily;
                pane.FontSize = _textFontSize;
                var document = pane.Document;
                document.LineHeight = DiffLineHeight;
                document.PageWidth = Math.Max(_sharedTextWidth + 24, Math.Max(200, pane.ActualWidth));
                document.PageHeight = Math.Max(Math.Max(DiffLineHeight, RowsHeight) + 24, 200);
                var row = 0;
                foreach (var paragraph in document.Blocks.OfType<Paragraph>())
                {
                    // Compact one-line folds keep their 3 DIP height and size.
                    if (!IsCompactRow(row))
                    {
                        paragraph.LineHeight = DiffLineHeight;
                        if (row < Lines.Count && Lines[row].FilteredLineCount > 0)
                            paragraph.FontSize = _textFontSize * 10 / 13;
                    }
                    row++;
                }

                if (pane.Tag is Canvas gutterViewport && gutterViewport.Children[0] is StackPanel gutter)
                {
                    FillGutter(gutter, pane == _leftList);
                    gutter.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
                    gutterViewport.Width = gutter.DesiredSize.Width;
                    PositionEditArrows(gutterViewport, vertical);
                }

                pane.UpdateLayout();
                scroll?.ScrollToVerticalOffset(vertical);
                scroll?.ScrollToHorizontalOffset(horizontal);
                if (pane.Parent is Grid grid)
                    foreach (var overlay in grid.Children.OfType<WhitespaceOverlay>()) overlay.RefreshFont();
            }

            DrawMap();
            UpdateHunkOverlay();
            PositionPatchMoveButtons();
            SettingsService.SaveDiffFont(family, size);
        }
        catch (Exception ex)
        {
            ViewModel.ReportError(ex);
        }
        finally
        {
            _scrolling = false;
        }
    }

    /// <summary>
    ///     左右のペインで共有するテキスト幅を、全行の描画幅の最大値から求めます。
    /// </summary>
    /// <returns>テキスト幅 (DIP)。</returns>
    private double MeasureSharedTextWidth()
    {
        if (Lines == null || Lines.Count == 0) return 0;
        var typeface = new Typeface(_textFontFamily, FontStyles.Normal, FontWeights.Normal, FontStretches.Normal);
        var dpi = VisualTreeHelper.GetDpi(this).PixelsPerDip;
        var width = 0.0;
        // Proportional fonts can make a shorter line wider than the longest line.
        foreach (var text in Lines.SelectMany(line => new[] { line.Left, line.Right }).Distinct())
        {
            if (text == null) continue;
            var formatted = new FormattedText(text.Replace("\t", "    ") + "  ", CultureInfo.CurrentCulture,
                FlowDirection.LeftToRight, typeface, _textFontSize, Brushes.Black, dpi);
            width = Math.Max(width, formatted.WidthIncludingTrailingWhitespace);
        }

        return width + 24;
    }
}
