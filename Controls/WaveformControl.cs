using System;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace DesktopIniManager.Controls;

/// <summary>Standalone waveform view. Wheel: time zoom; Ctrl+wheel: amplitude; drag: pan; click: seek.</summary>
public class WaveformControl : Control
{
    public const double MinimumViewSeconds = 0.5;
    private WaveformData data;
    private Geometry[] cachedGeometry = Array.Empty<Geometry>();
    private (WaveformData Data, double Start, double Span, double Amplitude, Size Size) geometryKey;
    private readonly DrawingGroup cursorDrawing = new();
    private readonly TranslateTransform cursorTransform = new();
    private double start, span, timelineDuration, amplitude = 1, position;
    private Point down;
    private double downStart;
    private bool pressed, dragged;
    private int loadVersion;
    private double highlightStart, highlightDuration;
    public bool InteractionEnabled { get; set; } = true;
    public event Action<double> PositionClicked;
    public event Action ViewChanged;
    public WaveformData Data => data;
    public double Duration => Math.Max(timelineDuration, data?.Duration ?? 0);
    public double ViewStart => start;
    public double ViewDuration => span;
    public double AmplitudeScale => amplitude;
    public bool IsPanning => pressed && dragged;

    public WaveformControl()
    {
        ClipToBounds = true;
        Background = Brushes.Transparent;
        Foreground = Brushes.SteelBlue;
        cursorDrawing.Transform = cursorTransform;
        SizeChanged += (_, _) => { UpdateCursor(); InvalidateVisual(); };
    }

    public void Load(string path) => SetData(WaveformData.Load(path));
    public async Task LoadAsync(string path, CancellationToken cancellation = default)
    {
        VerifyAccess();
        int version = ++loadVersion;
        var result = await Task.Run(() => WaveformData.Load(path, cancellation), cancellation).ConfigureAwait(false);
        await Dispatcher.InvokeAsync(() =>
        {
            cancellation.ThrowIfCancellationRequested();
            if (version == loadVersion) SetData(result);
        }).Task.ConfigureAwait(false);
    }

    public void SetData(WaveformData value, double totalDuration = 0)
    {
        VerifyAccess();
        if (!double.IsFinite(totalDuration) || totalDuration < 0) throw new ArgumentOutOfRangeException(nameof(totalDuration));
        loadVersion++;
        data = value; timelineDuration = totalDuration;
        cachedGeometry = Array.Empty<Geometry>();
        geometryKey = default;
        position = 0; highlightStart = highlightDuration = 0;
        Fit();
    }

    /// <summary>Marks a main view's visible interval when this control is used as an overview.</summary>
    public void HighlightViewRange(double seconds, double visibleSeconds)
    {
        if (!double.IsFinite(seconds) || !double.IsFinite(visibleSeconds)) throw new ArgumentOutOfRangeException();
        highlightStart = Math.Clamp(seconds, 0, Duration);
        highlightDuration = Math.Clamp(visibleSeconds, 0, Duration - highlightStart);
        InvalidateVisual();
    }
    public void Fit() { amplitude = 1; SetViewRange(0, Duration); InvalidateVisual(); }
    public void SetViewRange(double seconds, double visibleSeconds)
    {
        VerifyAccess();
        if (!double.IsFinite(seconds) || !double.IsFinite(visibleSeconds)) throw new ArgumentOutOfRangeException();
        double width = Duration > 0 ? Math.Clamp(visibleSeconds, Math.Min(MinimumViewSeconds, Duration), Duration) : 0;
        double left = Math.Clamp(seconds, 0, Math.Max(0, Duration - width));
        if (start == left && span == width) { UpdateCursor(); return; }
        start = left; span = width;
        UpdateCursor(); InvalidateVisual(); ViewChanged?.Invoke();
    }
    public void ZoomX(double factor, double anchor = 0)
    {
        if (!double.IsFinite(factor) || factor <= 0 || !double.IsFinite(anchor)) throw new ArgumentOutOfRangeException();
        anchor = Math.Clamp(anchor, 0, 1);
        double width = Math.Clamp(span / factor, Math.Min(MinimumViewSeconds, Duration), Duration);
        SetViewRange(start + (span - width) * anchor, width);
    }
    public void ZoomY(double factor)
    {
        if (!double.IsFinite(factor) || factor <= 0) throw new ArgumentOutOfRangeException(nameof(factor));
        amplitude = Math.Clamp(amplitude * factor, 0.1, 32); InvalidateVisual();
    }
    public double TimeToX(double seconds) => span > 0 ? (seconds - start) / span * ActualWidth : 0;
    public double XToTime(double x) => Math.Clamp(start + x / Math.Max(1, ActualWidth) * span, 0, Duration);
    public double AmplitudeToY(double value, int channel = 0)
    {
        double lane = ActualHeight / Math.Max(1, data?.Channels ?? 1);
        return lane * (channel + 0.5) - value * amplitude * lane * 0.45;
    }
    public double YToAmplitude(double y, int channel = 0)
    {
        double lane = ActualHeight / Math.Max(1, data?.Channels ?? 1);
        return lane > 0 ? (lane * (channel + 0.5) - y) / (amplitude * lane * 0.45) : 0;
    }
    public void SetPosition(TimeSpan value)
    {
        position = Math.Clamp(value.TotalSeconds, 0, Duration); UpdateCursor();
    }
    private void UpdateCursor()
    {
        double x = TimeToX(position);
        cursorTransform.X = x;
        cursorDrawing.Opacity = span > 0 && x >= 0 && x <= ActualWidth ? 1 : 0;
    }
    protected override void OnRender(DrawingContext dc)
    {
        base.OnRender(dc);
        dc.DrawRectangle(Background, null, new Rect(RenderSize));
        if (span > 0 && ActualWidth > 0 && ActualHeight > 0 && data is { BinCount: > 0, Channels: > 0 })
        {
            double lane = ActualHeight / data.Channels;
            for (int ch = 0; ch < data.Channels; ch++)
            {
                dc.PushClip(new RectangleGeometry(new Rect(0, ch * lane, ActualWidth, lane)));
                dc.DrawLine(new Pen(Foreground, 0.3), new Point(0, lane * (ch + 0.5)), new Point(ActualWidth, lane * (ch + 0.5)));
                DrawWaveform(dc, ch);
                dc.Pop();
            }
            DrawOverlay(dc);
            if (highlightDuration > 0)
                dc.DrawRectangle(null, new Pen(Brushes.Goldenrod, 1), new Rect(TimeToX(highlightStart), 1,
                    highlightDuration / span * ActualWidth, Math.Max(0, ActualHeight - 2)));
        }
        using (var cursor = cursorDrawing.Open())
            cursor.DrawLine(new Pen(Brushes.Red, 2), new Point(0, 0), new Point(0, ActualHeight));
        UpdateCursor(); dc.DrawDrawing(cursorDrawing);
    }
    // Normal and difference-colored passes share the same immutable screen geometry.
    protected void DrawWaveform(DrawingContext dc, int channel, Brush stroke = null)
    {
        var geometry = GetWaveformGeometry(channel);
        if (geometry.IsEmpty()) return;
        double right = Math.Clamp(TimeToX(data.Duration), 0, ActualWidth);
        double lane = ActualHeight / data.Channels;
        dc.PushClip(new RectangleGeometry(new Rect(0, channel * lane, right, lane)));
        dc.DrawGeometry(null, new Pen(stroke ?? Foreground, 1), geometry);
        dc.Pop();
    }

    internal Geometry GetWaveformGeometry(int channel)
    {
        if (data == null || data.BinCount == 0 || span <= 0 || ActualWidth <= 0 || ActualHeight <= 0 || start >= data.Duration)
            return Geometry.Empty;
        var key = (data, start, span, amplitude, RenderSize);
        if (geometryKey != key || cachedGeometry.Length != data.Channels)
        {
            geometryKey = key;
            cachedGeometry = new Geometry[data.Channels];
        }
        if (cachedGeometry[channel] != null) return cachedGeometry[channel];
        int first = Math.Max(0, (int)Math.Floor(start * data.BinsPerSecond) - 1);
        int last = Math.Min(data.BinCount - 1, (int)Math.Ceiling(Math.Min(start + span, data.Duration) * data.BinsPerSecond));
        double pixelsPerBin = ActualWidth / span / data.BinsPerSecond;
        double BinX(int bin) => TimeToX(((double)bin / data.BinsPerSecond +
            Math.Min((bin + 1.0) / data.BinsPerSecond, data.Duration)) * 0.5);
        var geometry = new StreamGeometry();
        using (var context = geometry.Open())
        {
            if (pixelsPerBin >= 1)
            {
                // Keep the existing detailed trace and all per-bin extrema.
                context.BeginFigure(new Point(BinX(first),
                    AmplitudeToY(((double)data.Minimum(channel, first) + data.Maximum(channel, first)) * 0.5, channel)), false, false);
                for (int bin = first + 1; bin <= last; bin++)
                {
                    double mid = ((double)data.Minimum(channel, bin) + data.Maximum(channel, bin)) * 0.5;
                    context.LineTo(new Point(BinX(bin), AmplitudeToY(mid, channel)), true, false);
                }
                if (first == last)
                {
                    double mid = ((double)data.Minimum(channel, first) + data.Maximum(channel, first)) * 0.5;
                    context.BeginFigure(new Point(TimeToX((double)first / data.BinsPerSecond), AmplitudeToY(mid, channel)), false, false);
                    context.LineTo(new Point(TimeToX(Math.Min((first + 1.0) / data.BinsPerSecond, data.Duration)), AmplitudeToY(mid, channel)), true, false);
                }
                for (int bin = first; bin <= last; bin++)
                {
                    double min = data.Minimum(channel, bin), max = data.Maximum(channel, bin);
                    if (max <= min) continue;
                    context.BeginFigure(new Point(BinX(bin), AmplitudeToY(max, channel)), false, false);
                    context.LineTo(new Point(BinX(bin), AmplitudeToY(min, channel)), true, false);
                }
            }
            else
            {
                int columns = (int)Math.Ceiling(Math.Min(ActualWidth, TimeToX(data.Duration)));
                for (int pixel = 0; pixel < columns; pixel++)
                {
                    int from = Math.Max(0, (int)Math.Floor((start + pixel * span / ActualWidth) * data.BinsPerSecond));
                    int to = Math.Min(data.BinCount, (int)Math.Ceiling(Math.Min(data.Duration,
                        start + (pixel + 1) * span / ActualWidth) * data.BinsPerSecond));
                    if (from >= to) continue;
                    data.GetExtrema(channel, from, to, out float min, out float max);
                    double x = pixel + 0.5;
                    context.BeginFigure(new Point(x, AmplitudeToY(max, channel)), false, false);
                    context.LineTo(new Point(x, AmplitudeToY(min, channel)), true, false);
                }
            }
        }
        geometry.Freeze();
        return cachedGeometry[channel] = geometry;
    }

    protected virtual void DrawOverlay(DrawingContext dc) { }
    protected override void OnMouseWheel(MouseWheelEventArgs e)
    {
        base.OnMouseWheel(e);
        if (!InteractionEnabled) return;
        double factor = Math.Pow(1.2, e.Delta / 120.0);
        if ((Keyboard.Modifiers & ModifierKeys.Control) != 0) ZoomY(factor);
        else ZoomX(factor, 0);
        e.Handled = true;
    }
    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonDown(e);
        if (!InteractionEnabled) return;
        if (!CaptureMouse()) return;
        down = e.GetPosition(this); downStart = start; pressed = true; dragged = false; e.Handled = true;
    }
    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        if (!pressed) return;
        double delta = e.GetPosition(this).X - down.X;
        if (Math.Abs(delta) >= SystemParameters.MinimumHorizontalDragDistance) dragged = true;
        if (dragged) SetViewRange(downStart - delta / Math.Max(1, ActualWidth) * span, span);
    }
    protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonUp(e);
        if (!pressed) return;
        bool click = !dragged; pressed = false; ReleaseMouseCapture();
        if (click) PositionClicked?.Invoke(XToTime(e.GetPosition(this).X));
        e.Handled = true;
    }
    protected override void OnLostMouseCapture(MouseEventArgs e) { base.OnLostMouseCapture(e); pressed = dragged = false; }
}
