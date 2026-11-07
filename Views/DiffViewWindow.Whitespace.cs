using DesktopIniManager.ViewModels;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;
using System.Windows.Threading;

namespace DesktopIniManager.Views;

internal sealed partial class DiffViewWindow
{
    /// <summary>
    ///     両側のペインの空白・改行マーカーを再描画します。
    /// </summary>
    private void RefreshWhitespaceMarkers()
    {
        foreach (var pane in new[] { _leftList, _rightList })
            if (pane?.Parent is Grid grid)
                foreach (var overlay in grid.Children.OfType<WhitespaceOverlay>())
                    overlay.RefreshMarkers();
    }

    // Cache character offsets once. No display markers are inserted into the document.
    private sealed class WhitespaceRow
    {
        internal readonly string Ending;
        internal readonly int Length;
        internal readonly List<(int Offset, string Glyph, bool Changed)> Marks = [];
        internal bool HasChangedMarks;
        private readonly Paragraph _paragraph;
        private readonly Run[] _runs;
        private readonly int[] _starts;
        internal bool IsEndOfFile;

        // changes: the row's inline-difference mask in display columns (tabs
        // expanded), or null. Changed whitespace is invisible as red text, so it
        // is drawn as a red marker here instead, whether or not spaces are shown.
        /// <summary>
        ///     段落と元の行から、空白・改行マーカーを描画するための行情報を作成します。変更された空白は、表示設定に関係なく赤いマーカーで描画します。
        /// </summary>
        /// <param name="paragraph">行を表示している段落。</param>
        /// <param name="original">元の行のテキスト。</param>
        /// <param name="ending">行の改行コード。</param>
        /// <param name="changes">表示上の桁ごとの変更フラグ (タブ展開後)。null の場合は変更なし。</param>
        internal WhitespaceRow(Paragraph paragraph, string original, string ending, bool[] changes = null)
        {
            _paragraph = paragraph;
            _runs = [.. paragraph.Inlines.OfType<Run>()];
            _starts = new int[_runs.Length];
            var offset = 0;
            for (var i = 0; i < _runs.Length; i++)
            {
                _starts[i] = offset;
                offset += _runs[i].Text.Length;
            }

            if (original == null) return; // A folded summary or missing side.
            // Run lengths already include the display expansion of tabs. Only
            // scan indentation; body whitespace must remain ordinary text.
            Length = offset;
            offset = 0;
            var indentation = true;
            bool Changed(int column) => changes != null && column < changes.Length && changes[column];
            foreach (var c in original)
            {
                if (c is not ('\t' or '\u0020' or '\u3000'))
                {
                    indentation = false;
                    // Body text has no markers; stop once no changed column can follow.
                    if (changes == null) break;
                    offset++;
                    continue;
                }

                var width = c == '\t' ? 4 : 1;
                for (var n = 0; n < width; n++)
                {
                    var column = offset + n;
                    var changed = Changed(column);
                    if (!indentation && !changed) continue;
                    Marks.Add((column, c == '\t' && n == 0 ? "\u003E" : c == '\u3000' ? "\u25AF" : "\uFF65",
                        changed));
                    HasChangedMarks |= changed;
                }

                offset += width;
            }

            Ending = ending switch { "\r\n" => "\u21B2", "\n" => "\u2193", "\r" => "\u240D", _ => null };
        }

        /// <summary>
        ///     行内の文字位置に対応するテキスト位置を取得します。
        /// </summary>
        /// <param name="offset">行内の文字位置。</param>
        /// <returns>テキスト位置。</returns>
        internal TextPointer Position(int offset)
        {
            if (_runs.Length == 0) return _paragraph.ContentStart.GetInsertionPosition(LogicalDirection.Forward);
            var index = Array.BinarySearch(_starts, offset);
            if (index < 0) index = Math.Max(0, ~index - 1);
            return _runs[index].ContentStart
                .GetPositionAtOffset(Math.Min(offset - _starts[index], _runs[index].Text.Length));
        }
    }

    private sealed class WhitespaceOverlay : FrameworkElement
    {
        private readonly Dictionary<string, FormattedText> _glyphs = new();
        private sealed record CachedRow(DrawingGroup Drawing, double HorizontalOffset, double VerticalOffset,
            int MarkCount, int EofCount);

        private readonly Dictionary<int, CachedRow> _cachedRows = new();
        private readonly DiffViewModel _model;
        private readonly RichTextBox _pane;
        private double DiffLineHeight => _pane.Document.LineHeight;
        private double _glyphDpi;
        private bool _lightBackground;
        private bool _refreshPending;
        private IReadOnlyList<WhitespaceRow> _rows = [];
        private bool _hasChangedMarks;

        /// <summary>
        ///     ペインの上に空白・改行マーカーを描画するオーバーレイを作成し、スクロールやサイズ変更で再描画するよう設定します。
        /// </summary>
        /// <param name="pane">対象のペイン。</param>
        /// <param name="model">DIFF ビューの ViewModel。</param>
        internal WhitespaceOverlay(RichTextBox pane, DiffViewModel model)
        {
            _pane = pane;
            _model = model;
            IsHitTestVisible = false;
            ClipToBounds = true;
            pane.AddHandler(ScrollViewer.ScrollChangedEvent,
                new ScrollChangedEventHandler((_, _) => RefreshAfterLayout()), true);
            pane.SizeChanged += (_, _) =>
            {
                _cachedRows.Clear();
                RefreshAfterLayout();
            };
            Loaded += (_, _) => RefreshAfterLayout();
        }

        private int LastVisibleRowCount { get; set; }
        private int LastDrawnMarkCount { get; set; }
        private int LastDrawnEofCount { get; set; }

        /// <summary>
        ///     キャッシュを破棄してマーカーを再描画します。
        /// </summary>
        internal void RefreshMarkers()
        {
            _cachedRows.Clear();
            InvalidateVisual();
        }

        /// <summary>
        ///     フォントが変わったときに、記号とキャッシュを破棄して再描画します。
        /// </summary>
        internal void RefreshFont()
        {
            _glyphs.Clear();
            _cachedRows.Clear();
            RefreshAfterLayout();
        }

        /// <summary>
        ///     描画対象の行を設定し、再描画します。
        /// </summary>
        /// <param name="value">行情報の一覧。</param>
        internal void SetRows(IReadOnlyList<WhitespaceRow> value)
        {
            _rows = value;
            _hasChangedMarks = value.Any(row => row.HasChangedMarks);
            _cachedRows.Clear();
            RefreshAfterLayout();
        }

        /// <summary>
        ///     RichTextBox のレイアウトが完了した後に、1 回だけ再描画するようスケジュールします。
        /// </summary>
        private void RefreshAfterLayout()
        {
            if (_refreshPending) return;
            _refreshPending = true;
            Dispatcher.BeginInvoke(DispatcherPriority.Loaded, new Action(() =>
            {
                _refreshPending = false;
                if (!IsLoaded) return;
                // RichTextBox lays out its FlowDocument separately. Drawing before
                // that pass leaves an empty overlay whose text positions are invalid.
                _pane.UpdateLayout();
                InvalidateVisual();
            }));
        }

        /// <summary>
        ///     表示範囲の行について、空白・タブ・改行・EOF のマーカーと、変更された空白のマーカーを描画します。
        /// </summary>
        /// <param name="drawing">描画コンテキスト。</param>
        protected override void OnRender(DrawingContext drawing)
        {
            base.OnRender(drawing);
            LastVisibleRowCount = LastDrawnMarkCount = LastDrawnEofCount = 0;
            if ((!_model.ShowSpaces && !_model.ShowNewlines && !_hasChangedMarks) || _rows.Count == 0) return;
            var scroll = FindScroll(_pane);
            if (scroll == null || scroll.ViewportHeight <= 0) return;
            var dpi = VisualTreeHelper.GetDpi(this).PixelsPerDip;
            var light = GetWindow(_pane) is DiffViewWindow window && !window.IsDarkBackground();
            if (Math.Abs(_glyphDpi - dpi) > 0.0001 || _lightBackground != light)
            {
                _glyphs.Clear();
                _cachedRows.Clear();
                _glyphDpi = dpi;
                _lightBackground = light;
            }

            // Unwrapped rows; compact one-line folds are shorter, so use the window's row geometry.
            var owner = GetWindow(_pane) as DiffViewWindow;
            var first = Math.Max(0, (owner?.RowAt(scroll.VerticalOffset) ??
                                     (int)(scroll.VerticalOffset / DiffLineHeight)) - 1);
            var last = Math.Min(_rows.Count, (owner?.RowAt(scroll.VerticalOffset + scroll.ViewportHeight) ??
                                              first + (int)Math.Ceiling(scroll.ViewportHeight / DiffLineHeight)) + 3);
            var origin = scroll.TranslatePoint(new Point(), this);
            drawing.PushClip(
                new RectangleGeometry(new Rect(origin, new Size(scroll.ViewportWidth, scroll.ViewportHeight))));
            for (var i = first; i < last; i++)
            {
                LastVisibleRowCount++;
                if (!_cachedRows.TryGetValue(i, out var cached))
                {
                    var row = _rows[i];
                    var position = row.Position(0);
                    if (position is not { HasValidLayout: true }) continue;

                    var marksBefore = LastDrawnMarkCount;
                    var eofBefore = LastDrawnEofCount;
                    var group = new DrawingGroup();
                    using (var context = group.Open())
                    {
                        foreach (var mark in row.Marks)
                            if (mark.Changed)
                                DrawChangedMark(context, row, mark.Offset, mark.Glyph, dpi);
                            else if (_model.ShowSpaces)
                                DrawMark(context, row, mark.Offset, mark.Glyph, false, dpi);
                        if (_model.ShowNewlines && row.Ending != null)
                            DrawMark(context, row, row.Length, row.Ending, true, dpi);
                        if (_model.ShowNewlines && row.IsEndOfFile)
                            DrawMark(context, row, row.Length, "[EOF]", true, dpi);
                    }

                    cached = new CachedRow(group, scroll.HorizontalOffset, scroll.VerticalOffset,
                        LastDrawnMarkCount - marksBefore, LastDrawnEofCount - eofBefore);
                    _cachedRows.Add(i, cached);
                    LastDrawnMarkCount = marksBefore;
                    LastDrawnEofCount = eofBefore;
                }

                drawing.PushTransform(new TranslateTransform(
                    cached.HorizontalOffset - scroll.HorizontalOffset,
                    cached.VerticalOffset - scroll.VerticalOffset));
                drawing.DrawDrawing(cached.Drawing);
                drawing.Pop();
                LastDrawnMarkCount += cached.MarkCount;
                LastDrawnEofCount += cached.EofCount;
            }

            drawing.Pop();
        }

        private static readonly Brush ChangedSpaceFill = Freeze(new SolidColorBrush(Color.FromArgb(96, 255, 0, 0)));

        /// <summary>
        ///     ブラシをフリーズして返します。
        /// </summary>
        /// <param name="brush">対象のブラシ。</param>
        /// <returns>フリーズしたブラシ。</returns>
        private static Brush Freeze(Brush brush)
        {
            brush.Freeze();
            return brush;
        }

        // Inline difference on whitespace: a translucent red cell plus a red marker glyph.
        /// <summary>
        ///     変更された空白の位置に、半透明の赤い背景と赤いマーカーを描画します。
        /// </summary>
        /// <param name="drawing">描画コンテキスト。</param>
        /// <param name="row">行情報。</param>
        /// <param name="offset">行内の文字位置。</param>
        /// <param name="glyph">マーカーの記号。</param>
        /// <param name="dpi">表示倍率。</param>
        private void DrawChangedMark(DrawingContext drawing, WhitespaceRow row, int offset, string glyph, double dpi)
        {
            var position = row.Position(offset);
            if (position is not { HasValidLayout: true }) return;
            var rect = position.GetCharacterRect(LogicalDirection.Forward);
            if (rect.IsEmpty) return;
            var next = row.Position(offset + 1);
            var width = next is { HasValidLayout: true } ? next.GetCharacterRect(LogicalDirection.Backward).X - rect.X : 0;
            if (width <= 0) width = _pane.FontSize / 2;
            var point = _pane.TranslatePoint(rect.TopLeft, this);
            drawing.DrawRectangle(ChangedSpaceFill, null, new Rect(point.X, point.Y, width, rect.Height));
            var key = "c" + glyph;
            if (!_glyphs.TryGetValue(key, out var text))
            {
                text = new FormattedText(glyph, CultureInfo.CurrentUICulture, FlowDirection.LeftToRight,
                    new Typeface(_pane.FontFamily, FontStyles.Normal, FontWeights.Bold, FontStretches.Normal),
                    _pane.FontSize, Brushes.Red, dpi);
                _glyphs.Add(key, text);
            }

            drawing.DrawText(text, new Point(point.X, point.Y + (rect.Height - text.Height)));
            LastDrawnMarkCount++;
        }

        /// <summary>
        ///     空白・改行・EOF のマーカーを文字の位置に描画します。
        /// </summary>
        /// <param name="drawing">描画コンテキスト。</param>
        /// <param name="row">行情報。</param>
        /// <param name="offset">行内の文字位置。</param>
        /// <param name="glyph">マーカーの記号。</param>
        /// <param name="newline">改行マーカーの場合は true。</param>
        /// <param name="dpi">表示倍率。</param>
        private void DrawMark(DrawingContext drawing, WhitespaceRow row, int offset, string glyph, bool newline,
            double dpi)
        {
            var position = row.Position(offset);
            if (position is not { HasValidLayout: true }) return;
            var rect = position.GetCharacterRect(LogicalDirection.Forward);
            if (rect.IsEmpty) return;
            var point = _pane.TranslatePoint(rect.TopLeft, this);
            if (glyph == "[EOF]")
            {
                var icon = DifferencerStatusIcons.GetCustomIcon(155);
                if (icon != null)
                {
                    const double size = 20;
                    drawing.DrawImage(icon, new Rect(point.X, point.Y + (rect.Height - size) / 2, size, size));
                    LastDrawnMarkCount++;
                    LastDrawnEofCount++;
                    return;
                }
            }

            Brush foreground = !newline ? Brushes.Gray :
                glyph == "\u2193" ? Brushes.RoyalBlue :
                glyph == "\u21B2" && _lightBackground ? Brushes.DarkRed : Brushes.Gold;
            var key = (newline ? "n" : "s") + glyph;
            if (!_glyphs.TryGetValue(key, out var text))
            {
                text = new FormattedText(glyph, CultureInfo.CurrentUICulture, FlowDirection.LeftToRight,
                    new Typeface(_pane.FontFamily, FontStyles.Normal, newline ? FontWeights.Bold : FontWeights.Normal,
                        FontStretches.Normal),
                    _pane.FontSize, foreground, dpi);
                _glyphs.Add(key, text);
            }

            drawing.DrawText(text, new Point(point.X, point.Y + (rect.Height - text.Height)));
            LastDrawnMarkCount++;
            if (glyph == "[EOF]") LastDrawnEofCount++;
        }
    }
}
