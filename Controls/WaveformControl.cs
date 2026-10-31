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
    private readonly DrawingGroup _cursorDrawing = new();
    private readonly TranslateTransform _cursorTransform = new();
    private Geometry[] _cachedGeometry = Array.Empty<Geometry>();
    private Point _down;
    private double _downStart;
    private (WaveformData Data, double Start, double Span, double Amplitude, Size Size) _geometryKey;
    private double _highlightStart, _highlightDuration;
    private int _loadVersion;
    private bool _pressed, _dragged;
    private double _timelineDuration, _position;

    public WaveformControl()
    {
        ClipToBounds = true;
        Background = Brushes.Transparent;
        Foreground = Brushes.SteelBlue;
        _cursorDrawing.Transform = _cursorTransform;
        SizeChanged += (_, _) =>
        {
            UpdateCursor();
            InvalidateVisual();
        };
    }

    public bool InteractionEnabled { get; set; } = true;
    public WaveformData Data { get; private set; }

    public double Duration => Math.Max(_timelineDuration, Data?.Duration ?? 0);
    public double ViewStart { get; private set; }

    public double ViewDuration { get; private set; }

    public double AmplitudeScale { get; private set; } = 1;

    public bool IsPanning => _pressed && _dragged;
    public event Action<double> PositionClicked;
    public event Action ViewChanged;

    public void Load(string path)
    {
        SetData(WaveformData.Load(path));
    }

    public async Task LoadAsync(string path, CancellationToken cancellation = default)
    {
        VerifyAccess();
        var version = ++_loadVersion;
        var result = await Task.Run(() => WaveformData.Load(path, cancellation), cancellation).ConfigureAwait(false);
        await Dispatcher.InvokeAsync(() =>
        {
            cancellation.ThrowIfCancellationRequested();
            if (version == _loadVersion) SetData(result);
        }).Task.ConfigureAwait(false);
    }

    public void SetData(WaveformData value, double totalDuration = 0)
    {
        VerifyAccess();
        if (!double.IsFinite(totalDuration) || totalDuration < 0)
            throw new ArgumentOutOfRangeException(nameof(totalDuration));
        _loadVersion++;
        Data = value;
        _timelineDuration = totalDuration;
            _cachedGeometry = Array.Empty<Geometry>();
        _geometryKey = default;
        _position = 0;
        _highlightStart = _highlightDuration = 0;
        Fit();
    }

    /// <summary>Marks a main view's visible interval when this control is used as an overview.</summary>
    public void HighlightViewRange(double seconds, double visibleSeconds)
    {
        if (!double.IsFinite(seconds) || !double.IsFinite(visibleSeconds)) throw new ArgumentOutOfRangeException();
        _highlightStart = Math.Clamp(seconds, 0, Duration);
        _highlightDuration = Math.Clamp(visibleSeconds, 0, Duration - _highlightStart);
        InvalidateVisual();
    }

    public void Fit()
    {
        AmplitudeScale = 1;
        SetViewRange(0, Duration);
        InvalidateVisual();
    }

    public void SetViewRange(double seconds, double visibleSeconds)
    {
        VerifyAccess();
        if (!double.IsFinite(seconds) || !double.IsFinite(visibleSeconds)) throw new ArgumentOutOfRangeException();
        var width = Duration > 0 ? Math.Clamp(visibleSeconds, Math.Min(MinimumViewSeconds, Duration), Duration) : 0;
        var left = Math.Clamp(seconds, 0, Math.Max(0, Duration - width));
        if (Math.Abs(ViewStart - left) < 1e-9 &&
            Math.Abs(ViewDuration - width) < 1e-9)
        {
            UpdateCursor();
            return;
        }

        ViewStart = left;
        ViewDuration = width;
        UpdateCursor();
        InvalidateVisual();
        ViewChanged?.Invoke();
    }

    public void ZoomX(double factor, double anchor = 0)
    {
        if (!double.IsFinite(factor) || factor <= 0 || !double.IsFinite(anchor))
            throw new ArgumentOutOfRangeException();
        anchor = Math.Clamp(anchor, 0, 1);
        var width = Math.Clamp(ViewDuration / factor, Math.Min(MinimumViewSeconds, Duration), Duration);
        SetViewRange(ViewStart + (ViewDuration - width) * anchor, width);
    }

    public void ZoomY(double factor)
    {
        if (!double.IsFinite(factor) || factor <= 0) throw new ArgumentOutOfRangeException(nameof(factor));
        AmplitudeScale = Math.Clamp(AmplitudeScale * factor, 0.1, 32);
        InvalidateVisual();
    }

    public double TimeToX(double seconds)
    {
        return ViewDuration > 0 ? (seconds - ViewStart) / ViewDuration * ActualWidth : 0;
    }

    public double XToTime(double x)
    {
        return Math.Clamp(ViewStart + x / Math.Max(1, ActualWidth) * ViewDuration, 0, Duration);
    }

    public double AmplitudeToY(double value, int channel = 0)
    {
        var lane = ActualHeight / Math.Max(1, Data?.Channels ?? 1);
        return lane * (channel + 0.5) - value * AmplitudeScale * lane * 0.45;
    }

    public double YToAmplitude(double y, int channel = 0)
    {
        var lane = ActualHeight / Math.Max(1, Data?.Channels ?? 1);
        return lane > 0 ? (lane * (channel + 0.5) - y) / (AmplitudeScale * lane * 0.45) : 0;
    }

    public void SetPosition(TimeSpan value)
    {
        _position = Math.Clamp(value.TotalSeconds, 0, Duration);
        UpdateCursor();
    }

    private void UpdateCursor()
    {
        var x = TimeToX(_position);
        _cursorTransform.X = x;
        _cursorDrawing.Opacity = ViewDuration > 0 && x >= 0 && x <= ActualWidth ? 1 : 0;
    }

    protected override void OnRender(DrawingContext dc)
    {
        base.OnRender(dc);
        dc.DrawRectangle(Background, null, new Rect(RenderSize));
        if (ViewDuration > 0 && ActualWidth > 0 && ActualHeight > 0 && Data is { BinCount: > 0, Channels: > 0 })
        {
            var lane = ActualHeight / Data.Channels;
            for (var ch = 0; ch < Data.Channels; ch++)
            {
                dc.PushClip(new RectangleGeometry(new Rect(0, ch * lane, ActualWidth, lane)));
                dc.DrawLine(new Pen(Foreground, 0.3), new Point(0, lane * (ch + 0.5)),
                    new Point(ActualWidth, lane * (ch + 0.5)));
                DrawWaveform(dc, ch);
                dc.Pop();
            }

            DrawOverlay(dc);
            if (_highlightDuration > 0)
                dc.DrawRectangle(null, new Pen(Brushes.Goldenrod, 1), new Rect(TimeToX(_highlightStart), 1,
                    _highlightDuration / ViewDuration * ActualWidth, Math.Max(0, ActualHeight - 2)));
        }

        using (var cursor = _cursorDrawing.Open())
        {
            cursor.DrawLine(new Pen(Brushes.Red, 2), new Point(0, 0), new Point(0, ActualHeight));
        }

        UpdateCursor();
        dc.DrawDrawing(_cursorDrawing);
    }

    // Normal and difference-colored passes share the same immutable screen geometry.
    protected void DrawWaveform(DrawingContext dc, int channel, Brush stroke = null)
    {
        var geometry = GetWaveformGeometry(channel);
        if (geometry.IsEmpty()) return;
        var right = Math.Clamp(TimeToX(Data.Duration), 0, ActualWidth);
        var lane = ActualHeight / Data.Channels;
        dc.PushClip(new RectangleGeometry(new Rect(0, channel * lane, right, lane)));
        dc.DrawGeometry(null, new Pen(stroke ?? Foreground, 1), geometry);
        dc.Pop();
    }

    internal Geometry GetWaveformGeometry(int channel)
    {
        if (Data == null || Data.BinCount == 0 || ViewDuration <= 0 || ActualWidth <= 0 || ActualHeight <= 0 ||
            ViewStart >= Data.Duration)
            return Geometry.Empty;
        var key = (Data, ViewStart, ViewDuration, AmplitudeScale, RenderSize);
        if (_geometryKey != key || _cachedGeometry.Length != Data.Channels)
        {
            _geometryKey = key;
            _cachedGeometry = new Geometry[Data.Channels];
        }

        if (_cachedGeometry[channel] != null) return _cachedGeometry[channel];
        var first = Math.Max(0, (int)Math.Floor(ViewStart * Data.BinsPerSecond) - 1);
        var last = Math.Min(Data.BinCount - 1,
            (int)Math.Ceiling(Math.Min(ViewStart + ViewDuration, Data.Duration) * Data.BinsPerSecond));
        var pixelsPerBin = ActualWidth / ViewDuration / Data.BinsPerSecond;

        double BinX(int bin)
        {
            return TimeToX(((double)bin / Data.BinsPerSecond +
                            Math.Min((bin + 1.0) / Data.BinsPerSecond, Data.Duration)) * 0.5);
        }

        var geometry = new StreamGeometry();
        using (var context = geometry.Open())
        {
            if (pixelsPerBin >= 1)
            {
                // Keep the existing detailed trace and all per-bin extrema.
                context.BeginFigure(new Point(BinX(first),
                        AmplitudeToY(((double)Data.Minimum(channel, first) + Data.Maximum(channel, first)) * 0.5,
                            channel)),
                    false, false);
                for (var bin = first + 1; bin <= last; bin++)
                {
                    var mid = ((double)Data.Minimum(channel, bin) + Data.Maximum(channel, bin)) * 0.5;
                    context.LineTo(new Point(BinX(bin), AmplitudeToY(mid, channel)), true, false);
                }

                if (first == last)
                {
                    var mid = ((double)Data.Minimum(channel, first) + Data.Maximum(channel, first)) * 0.5;
                    context.BeginFigure(
                        new Point(TimeToX((double)first / Data.BinsPerSecond), AmplitudeToY(mid, channel)), false,
                        false);
                    context.LineTo(
                        new Point(TimeToX(Math.Min((first + 1.0) / Data.BinsPerSecond, Data.Duration)),
                            AmplitudeToY(mid, channel)), true, false);
                }

                for (var bin = first; bin <= last; bin++)
                {
                    double min = Data.Minimum(channel, bin), max = Data.Maximum(channel, bin);
                    if (max <= min) continue;
                    context.BeginFigure(new Point(BinX(bin), AmplitudeToY(max, channel)), false, false);
                    context.LineTo(new Point(BinX(bin), AmplitudeToY(min, channel)), true, false);
                }
            }
            else
            {
                var columns = (int)Math.Ceiling(Math.Min(ActualWidth, TimeToX(Data.Duration)));
                for (var pixel = 0; pixel < columns; pixel++)
                {
                    var from = Math.Max(0,
                        (int)Math.Floor((ViewStart + pixel * ViewDuration / ActualWidth) * Data.BinsPerSecond));
                    var to = Math.Min(Data.BinCount, (int)Math.Ceiling(Math.Min(Data.Duration,
                        ViewStart + (pixel + 1) * ViewDuration / ActualWidth) * Data.BinsPerSecond));
                    if (from >= to) continue;
                    Data.GetExtrema(channel, from, to, out var min, out var max);
                    var x = pixel + 0.5;
                    context.BeginFigure(new Point(x, AmplitudeToY(max, channel)), false, false);
                    context.LineTo(new Point(x, AmplitudeToY(min, channel)), true, false);
                }
            }
        }

        geometry.Freeze();
        return _cachedGeometry[channel] = geometry;
    }

    protected virtual void DrawOverlay(DrawingContext dc)
    {
    }

    protected override void OnMouseWheel(MouseWheelEventArgs e)
    {
        base.OnMouseWheel(e);
        if (!InteractionEnabled) return;
        var factor = Math.Pow(1.2, e.Delta / 120.0);
        if ((Keyboard.Modifiers & ModifierKeys.Control) != 0) ZoomY(factor);
        else ZoomX(factor);
        e.Handled = true;
    }

    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonDown(e);
        if (!InteractionEnabled) return;
        if (!CaptureMouse()) return;
        _down = e.GetPosition(this);
        _downStart = ViewStart;
        _pressed = true;
        _dragged = false;
        e.Handled = true;
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        if (!_pressed) return;
        var delta = e.GetPosition(this).X - _down.X;
        if (Math.Abs(delta) >= SystemParameters.MinimumHorizontalDragDistance) _dragged = true;
        if (_dragged) SetViewRange(_downStart - delta / Math.Max(1, ActualWidth) * ViewDuration, ViewDuration);
    }

    protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonUp(e);
        if (!_pressed) return;
        var click = !_dragged;
        _pressed = false;
        ReleaseMouseCapture();
        if (click) PositionClicked?.Invoke(XToTime(e.GetPosition(this).X));
        e.Handled = true;
    }

    protected override void OnLostMouseCapture(MouseEventArgs e)
    {
        base.OnLostMouseCapture(e);
        _pressed = _dragged = false;
    }
}