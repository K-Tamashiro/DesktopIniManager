using DesktopIniManager.ViewModels;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;

namespace DesktopIniManager.Views;

internal sealed partial class DiffViewWindow
{
    private void MarkerDisplay_Click(object sender, RoutedEventArgs e)
    {
        ViewModel.ShowSpaces = showSpacesBox.IsChecked == true;
        ViewModel.ShowNewlines = showNewlinesBox.IsChecked == true;
        foreach (var pane in new[] { leftList, rightList })
            if (pane?.Parent is Grid grid)
                foreach (var overlay in grid.Children.OfType<WhitespaceOverlay>())
                    overlay.InvalidateVisual();
    }

    // Cache character offsets once. No display markers are inserted into the document.
    private sealed class WhitespaceRow
    {
        internal readonly string Ending;
        internal readonly int Length;
        internal readonly List<(int Offset, string Glyph)> Marks = new();
        internal readonly Paragraph Paragraph;
        private readonly Run[] runs;
        private readonly int[] starts;

        internal WhitespaceRow(Paragraph paragraph, string original, string ending)
        {
            Paragraph = paragraph;
            runs = paragraph.Inlines.OfType<Run>().ToArray();
            starts = new int[runs.Length];
            var offset = 0;
            for (var i = 0; i < runs.Length; i++)
            {
                starts[i] = offset;
                offset += runs[i].Text.Length;
            }

            if (original == null) return; // A folded summary or missing side.
            offset = 0;
            foreach (var c in original)
            {
                if (c == '\t')
                {
                    Marks.Add((offset, "\u003E"));
                    for (var n = 1; n < 4; n++) Marks.Add((offset + n, "\uFF65"));
                    offset += 4;
                    continue;
                }

                if (c == '\u0020') Marks.Add((offset, "\uFF65"));
                else if (c == '\u3000') Marks.Add((offset, "\u25AF"));
                offset++;
            }

            Length = offset;
            Ending = ending switch { "\r\n" => "\u21B2", "\n" => "\u2193", "\r" => "\u240D", _ => null };
        }

        internal TextPointer Position(int offset)
        {
            if (runs.Length == 0) return Paragraph.ContentStart.GetInsertionPosition(LogicalDirection.Forward);
            var index = Array.BinarySearch(starts, offset);
            if (index < 0) index = Math.Max(0, ~index - 1);
            return runs[index].ContentStart
                .GetPositionAtOffset(Math.Min(offset - starts[index], runs[index].Text.Length));
        }
    }

    private sealed class WhitespaceOverlay : FrameworkElement
    {
        private readonly Dictionary<string, FormattedText> glyphs = new();
        private readonly DiffViewModel model;
        private readonly RichTextBox pane;
        private double glyphDpi;
        private IReadOnlyList<WhitespaceRow> rows = Array.Empty<WhitespaceRow>();

        internal WhitespaceOverlay(RichTextBox pane, DiffViewModel model)
        {
            this.pane = pane;
            this.model = model;
            IsHitTestVisible = false;
            ClipToBounds = true;
            pane.AddHandler(ScrollViewer.ScrollChangedEvent,
                new ScrollChangedEventHandler((s, e) => InvalidateVisual()), true);
            pane.SizeChanged += (s, e) => InvalidateVisual();
            Loaded += (s, e) => InvalidateVisual();
        }

        internal int LastVisibleRowCount { get; private set; }
        internal int LastDrawnMarkCount { get; private set; }

        internal void SetRows(IReadOnlyList<WhitespaceRow> value)
        {
            rows = value;
            InvalidateVisual();
        }

        protected override void OnRender(DrawingContext drawing)
        {
            base.OnRender(drawing);
            LastVisibleRowCount = LastDrawnMarkCount = 0;
            if ((!model.ShowSpaces && !model.ShowNewlines) || rows.Count == 0) return;
            var scroll = FindScroll(pane);
            if (scroll == null || scroll.ViewportHeight <= 0) return;
            var dpi = VisualTreeHelper.GetDpi(this).PixelsPerDip;
            if (glyphDpi != dpi)
            {
                glyphs.Clear();
                glyphDpi = dpi;
            }

            // The comparison uses fixed-height, unwrapped rows.
            var first = Math.Max(0, (int)(scroll.VerticalOffset / DiffLineHeight) - 1);
            var last = Math.Min(rows.Count, first + (int)Math.Ceiling(scroll.ViewportHeight / DiffLineHeight) + 3);
            var origin = scroll.TranslatePoint(new Point(), this);
            drawing.PushClip(
                new RectangleGeometry(new Rect(origin, new Size(scroll.ViewportWidth, scroll.ViewportHeight))));
            for (var i = first; i < last; i++)
            {
                LastVisibleRowCount++;
                var row = rows[i];
                if (model.ShowSpaces)
                    foreach (var mark in row.Marks)
                        DrawMark(drawing, row, mark.Offset, mark.Glyph, false, dpi);
                if (model.ShowNewlines && row.Ending != null)
                    DrawMark(drawing, row, row.Length, row.Ending, true, dpi);
            }

            drawing.Pop();
        }

        private void DrawMark(DrawingContext drawing, WhitespaceRow row, int offset, string glyph, bool newline,
            double dpi)
        {
            var position = row.Position(offset);
            if (position == null || !position.HasValidLayout) return;
            var rect = position.GetCharacterRect(LogicalDirection.Forward);
            if (rect.IsEmpty) return;
            var point = pane.TranslatePoint(rect.TopLeft, this);
            if (point.X < -30 || point.X > ActualWidth || point.Y < -DiffLineHeight || point.Y > ActualHeight) return;
            var light = GetWindow(pane) is DiffViewWindow window && !window.IsDarkBackground();
            Brush foreground = !newline ? Brushes.Gray :
                glyph == "\u2193" ? Brushes.RoyalBlue :
                glyph == "\u21B2" && light ? Brushes.DarkRed : Brushes.Gold;
            var key = (newline ? "n" : "s") + glyph + (light ? "light" : "dark");
            if (!glyphs.TryGetValue(key, out var text))
            {
                text = new FormattedText(glyph, CultureInfo.CurrentUICulture, FlowDirection.LeftToRight,
                    new Typeface(pane.FontFamily, FontStyles.Normal, newline ? FontWeights.Bold : FontWeights.Normal,
                        FontStretches.Normal),
                    pane.FontSize, foreground, dpi);
                glyphs.Add(key, text);
            }

            drawing.DrawText(text, new Point(point.X, point.Y + (rect.Height - text.Height)));
            LastDrawnMarkCount++;
        }
    }
}