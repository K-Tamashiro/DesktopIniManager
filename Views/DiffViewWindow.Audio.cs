using System;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Input;
using System.Windows.Threading;
using DesktopIniManager.Services;
using NAudio.Wave;
using System.IO;
using DesktopIniManager.ViewModels;
using DesktopIniManager.Properties;

namespace DesktopIniManager.Views;

internal sealed partial class DiffViewWindow
{
    private AudioDiffPlayer audioPlayer;
    private CancellationTokenSource audioLoading;
    private DispatcherTimer audioTimer;
    private EventHandler audioRendering;
    private AudioTimeline audioTimeline;
    private AudioOverview audioOverview;
    private bool audioDifferenceEnabled;
    private Task<AudioDifferences> audioDifferenceTask;
    private void ResetAudio()
    {
        audioLoading?.Cancel();
        audioLoading?.Dispose();
        audioLoading = null;
        audioDifferenceTask = null;
        audioTimer?.Stop();
        audioTimer = null;
        if (audioRendering != null) CompositionTarget.Rendering -= audioRendering;
        audioRendering = null;
        audioTimeline = null;
        audioOverview = null;
        audioPlayer?.Dispose();
        audioPlayer = null;
        audioOverviewHost.Children.Clear();
        audioOverviewHost.Visibility = Visibility.Collapsed;
    }
    private void ToggleAudioPlayback()
    {
        if (audioPlayer == null) return;

        try
        {
            if (audioPlayer.Playing)
                audioPlayer.Pause();
            else
                audioPlayer.Play();
        }
        catch (Exception ex)
        {
            ViewModel.ReportError(ex);
        }
    }
    private async Task RenderAudio()
    {
        // Do not depend on the caller having a DispatcherSynchronizationContext.
        var request = await Dispatcher.InvokeAsync(() =>
        {
            CheckWindowOpen();
            ResetAudio();
            audioLoading = new CancellationTokenSource();
            return (Source: ViewModel.GetPath(true), Target: ViewModel.GetPath(false), Token: audioLoading.Token);
        }).Task.ConfigureAwait(false);
        var envelopes = await Task.Run(() =>
        {
            // Read duration from the audio header before decoding either file.
            ValidateAudioDuration(request.Source, request.Token);
            ValidateAudioDuration(request.Target, request.Token);
            return new[]
            {
                AudioEnvelope.Load(request.Source, request.Token),
                AudioEnvelope.Load(request.Target, request.Token)
            };
        }, request.Token).ConfigureAwait(false);
        await Dispatcher.InvokeAsync(() =>
        {
            request.Token.ThrowIfCancellationRequested();
            BuildAudioView(request.Source, request.Target, envelopes);
        }).Task.ConfigureAwait(false);
    }

    private static void ValidateAudioDuration(string path, CancellationToken cancellation)
    {
        cancellation.ThrowIfCancellationRequested();
        if (string.IsNullOrEmpty(path) || !File.Exists(path)) return;
        using var reader = new AudioFileReader(path);
        if (reader.TotalTime > TimeSpan.FromHours(1))
            throw new InvalidDataException($"1時間を超える音声ファイルは読み込めません: {Path.GetFileName(path)} ({reader.TotalTime:hh\\:mm\\:ss})");
    }

    private async Task ApplyAudioDifferencesAsync()
    {
        if (audioTimeline == null || audioLoading == null || !audioDifferenceEnabled) return;
        var timeline = audioTimeline;
        var token = audioLoading.Token;
        string source = ViewModel.GetPath(true), target = ViewModel.GetPath(false);
        try
        {
            // Optional analysis must not hold LoadContent's rendering/drop lock.
            audioDifferenceTask ??= Task.Run(() => AudioDifferences.Load(source, target, token), token);
            var differences = await audioDifferenceTask;
            if (token.IsCancellationRequested || windowClosed || !ReferenceEquals(timeline, audioTimeline)) return;
            for (int side = 0; side < timeline.Waves.Length; side++)
                timeline.Waves[side].SetDifferences(differences, audioDifferenceEnabled, side == 0);
            audioOverview?.SetDifferences(differences, audioDifferenceEnabled);
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            if (!token.IsCancellationRequested && !windowClosed && ReferenceEquals(timeline, audioTimeline))
            {
                audioDifferenceTask = null;
                ViewModel.ReportError(ex);
            }
        }
    }

    private void BuildAudioView(string source, string target, AudioEnvelope[] envelopes)
    {
        Dispatcher.VerifyAccess();
        CheckWindowOpen();
        double duration = Math.Max(envelopes[0]?.Duration ?? 0, envelopes[1]?.Duration ?? 0);
        audioPlayer = new AudioDiffPlayer(source, target, duration);
        var overview = audioOverview = new AudioOverview(envelopes, duration);
        overview.SetResourceReference(Control.ForegroundProperty, "AudioWaveformInk");
        overview.SetResourceReference(Control.BackgroundProperty, "CardBackground");
        audioOverviewHost.Children.Add(overview);
        audioOverviewHost.Visibility = Visibility.Visible;
        body.Children.Clear();
        var layout = audioTimeline = new AudioTimeline(envelopes, duration, new[] { source, target });
        Grid.SetColumnSpan(layout, 3);
        body.Children.Add(layout);
        layout.ViewChanged += overview.SetViewport;
        overview.ViewportDragged += layout.SetViewStart;
        BuildAudioZoom(duration, envelopes);

        var controls = new Grid { Margin = new Thickness(0, 8, 0, 0) };
        controls.ColumnDefinitions.Add(new ColumnDefinition());
        controls.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1.6, GridUnitType.Star) });
        controls.ColumnDefinitions.Add(new ColumnDefinition());
        for (int side = 0; side < 2; side++)
        {
            int index = side;
            var volumePanel = new Grid { Margin = new Thickness(side == 0 ? 0 : 10, 0, side == 0 ? 10 : 0, 0) };
            volumePanel.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            volumePanel.ColumnDefinitions.Add(new ColumnDefinition());
            volumePanel.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            var volume = new Slider
            {
                Minimum = 0,
                Maximum = 1,
                Value = SettingsService.LoadDiffAudioVolume(side == 0),
                VerticalAlignment = VerticalAlignment.Center,
                ToolTip = side == 0 ? "Source volume" : "Target volume",
                IsMoveToPointEnabled = true
            };
            volume.Style = TryFindResource("AudioVolumeSlider") as Style;
            volume.SetResourceReference(Control.ForegroundProperty, side == 0 ? "SourceColor" : "TargetColor");
            audioPlayer.SetVolume(index, volume.Value);
            volume.ValueChanged += (_, e) =>
            {
                audioPlayer?.SetVolume(index, e.NewValue);
                SettingsService.SaveDiffAudioVolume(index == 0, e.NewValue);
            };
            Grid.SetColumn(volume, 1);
            volumePanel.Children.Add(volume);
            Button Icon(int number, string hint, double value)
            {
                var button = new Button
                {
                    Width = 36,
                    Height = 36,
                    Padding = new Thickness(2),
                    ToolTip = hint,
                    Content = new Image { Source = DifferencerStatusIcons.GetCustomIcon(number), Width = 28, Height = 28 },
                    Style = TryFindResource("IconButton") as Style
                };
                button.Click += (_, _) => volume.Value = value;
                return button;
            }
            volumePanel.Children.Add(Icon(142, "Mute (0%)", 0));
            var maximum = Icon(141, "Maximum (100%)", 1);
            Grid.SetColumn(maximum, 2);
            volumePanel.Children.Add(maximum);
            Grid.SetColumn(volumePanel, side == 0 ? 0 : 2);
            controls.Children.Add(volumePanel);
        }
        var transport = new Grid();
        transport.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        transport.ColumnDefinitions.Add(new ColumnDefinition());
        transport.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var play = new Button
        {
            Content = "▶",
            ToolTip = "Play / Pause",
            IsEnabled = duration > 0,
            Style = (Style)FindResource("AudioPlayButton")
        }; var seek = new Slider
        {
            Minimum = 0,
            Maximum = Math.Max(duration, 0.001),
            IsEnabled = duration > 0,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(8, 0, 8, 0),
            IsMoveToPointEnabled = true,
            ToolTip = "Seek"
        };
        seek.Style = TryFindResource("AudioSeekSlider") as Style;
        var time = new TextBlock { VerticalAlignment = VerticalAlignment.Center, FontSize = 11 };
        time.SetResourceReference(TextBlock.ForegroundProperty, "Ink");
        bool updating = false, dragging = false;
        void Refresh(double position, bool updateTimeline = true)
        {
            updating = true;
            if (!dragging) seek.Value = position;
            updating = false;
            time.Text = $"{TimeSpan.FromSeconds(position):hh\\:mm\\:ss} / {TimeSpan.FromSeconds(duration):hh\\:mm\\:ss}";
            if (updateTimeline) layout.UpdatePosition(position, audioPlayer?.Playing == true);
            overview.Update(position, audioPlayer?.GetPeak(0) ?? 0, audioPlayer?.GetPeak(1) ?? 0);
            play.Content = audioPlayer?.Playing == true ? "Ⅱ" : "▶";
        }
        void Seek(double position)
        {
            try { audioPlayer?.Seek(position); layout.UpdatePosition(position, true); Refresh(position); }
            catch (Exception ex) { ViewModel.ReportError(ex); }
        }
        play.Click += (_, _) =>
        {
            ToggleAudioPlayback();
            Refresh(audioPlayer?.Position ?? 0);
        };
        seek.AddHandler(Thumb.DragStartedEvent, new DragStartedEventHandler((_, _) => dragging = true));
        seek.AddHandler(Thumb.DragCompletedEvent, new DragCompletedEventHandler((_, _) => { dragging = false; Seek(seek.Value); }));
        seek.ValueChanged += (_, e) =>
        {
            if (updating) return;
            if (dragging) Refresh(e.NewValue); else Seek(e.NewValue);
        };
        layout.SeekRequested += Seek;
        overview.ViewportClicked += Seek;
        overview.MouseLeftButtonDown += (_, e) =>
        {
            if (overview.RepeatEnabled) return;
            Seek(Math.Clamp(e.GetPosition(overview).X / Math.Max(1, overview.ActualWidth - 100), 0, 1) * duration);
            e.Handled = true;
        };
        overview.RepeatChanged += (a, b) =>
        {
            try { audioPlayer?.SetRepeat(a, b); Refresh(audioPlayer?.Position ?? 0); }
            catch (Exception ex) { ViewModel.ReportError(ex); }
        };
        transport.Children.Add(play);
        Grid.SetColumn(seek, 1); transport.Children.Add(seek);
        Grid.SetColumn(time, 2); transport.Children.Add(time);
        var transportFrame = new Border
        {
            CornerRadius = new CornerRadius(10),
            BorderThickness = new Thickness(1),
            Padding = new Thickness(10, 6, 14, 6),
            Margin = new Thickness(10, 0, 10, 0),
            Child = transport
        };
        transportFrame.SetResourceReference(Border.BackgroundProperty, "CardBackground");
        transportFrame.SetResourceReference(Border.BorderBrushProperty, "Line");
        Grid.SetColumn(transportFrame, 1); controls.Children.Add(transportFrame);
        gifFrameHost.HorizontalAlignment = HorizontalAlignment.Stretch;
        gifFrameHost.Children.Add(controls);
        gifFrameHost.Visibility = Visibility.Visible;
        // Match display refresh; the transport text and overview meters need fewer updates.
        double lastOverviewUpdate = double.NegativeInfinity;
        audioRendering = (_, e) =>
        {
            if (audioPlayer == null || dragging || !audioPlayer.Playing) return;
            double position = audioPlayer.Position;
            layout.UpdatePosition(position, true);
            double now = ((RenderingEventArgs)e).RenderingTime.TotalSeconds;
            if (now - lastOverviewUpdate < 1.0 / 30) return;
            lastOverviewUpdate = now;
            Refresh(position, false);
        };
        CompositionTarget.Rendering += audioRendering;
        audioTimer = new DispatcherTimer(DispatcherPriority.Background, Dispatcher) { Interval = TimeSpan.FromMilliseconds(100) };
        audioTimer.Tick += (_, _) =>
        {
            if (audioPlayer == null) return;
            if (!dragging && !audioPlayer.Playing) Refresh(audioPlayer.Position);
            var error = Interlocked.Exchange(ref audioPlayer.PlaybackError, null);
            if (error != null) ViewModel.ReportError(error);
        };
        audioTimer.Start();
        Refresh(0);
    }

    private void BuildAudioZoom(double duration, AudioEnvelope[] envelopes)
    {
        var zoom = imageZoom = new Slider
        {
            Minimum = 100,
            Maximum = 100,
            Value = 100,
            SmallChange = 100,
            LargeChange = 100,
            TickFrequency = 100,
            IsSnapToTickEnabled = false,
            Width = 150,
            VerticalAlignment = VerticalAlignment.Center,
            Style = TryFindResource("ZoomSlider") as Style,
            ToolTip = "Horizontal waveform zoom"
        };
        void UpdateMaximum()
        {
            zoom.Maximum = Math.Max(100, duration / 0.5 * 100);
            if (zoom.Value > zoom.Maximum) zoom.Value = zoom.Maximum;
        }

        var label = new TextBlock { Text = "100%", Width = 94, Margin = new Thickness(8, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center };
        var row = new StackPanel { Orientation = Orientation.Horizontal };
        row.Children.Add(new TextBlock { Text = StringOverlay.Get("Diff_Zoom"), VerticalAlignment = VerticalAlignment.Center });
        row.Children.Add(ImageZoomStepButton("\uE738", "Zoom out", () => zoom.Value = Math.Max(zoom.Minimum, zoom.Value - 100)));
        row.Children.Add(zoom);
        row.Children.Add(ImageZoomStepButton("\uE710", "Zoom in", () => zoom.Value = Math.Min(zoom.Maximum, zoom.Value + 100)));
        row.Children.Add(label);
        var chrome = new Border
        {
            Child = row,
            CornerRadius = new CornerRadius(18),
            BorderThickness = new Thickness(1),
            Padding = new Thickness(10, 4, 12, 4),
            Margin = new Thickness(0, 0, 8, 8)
        };
        chrome.SetResourceReference(Border.BackgroundProperty, "CardBackground");
        chrome.SetResourceReference(Border.BorderBrushProperty, "Line");
        imageToolbar.Children.Clear();
        imageToolbar.Children.Add(chrome);
        zoom.ValueChanged += (_, e) =>
        {
            audioTimeline?.SetZoom(e.NewValue / 100.0);
            label.Text = $"{e.NewValue:0}%";
        };
        AddIconActionButton(imageToolbar, DifferencerStatusIcons.GetCustomIcon(72), StringOverlay.Get("Diff_Fit"), () =>
        {
            zoom.Value = 100;
            audioTimeline?.Fit();
        });
        var differenceIcon = new Image { Source = DifferencerStatusIcons.GetCustomIcon(143), Stretch = Stretch.Uniform };
        var differenceToggle = new ToggleButton
        {
            Content = differenceIcon,
            Style = TryFindResource("IconToggleButton") as Style,
            Margin = new Thickness(0, 0, 8, 8),
            ToolTip = "Highlight audio differences (decoded samples, before volume adjustment)",
            IsChecked = audioDifferenceEnabled
        };
        System.Windows.Automation.AutomationProperties.SetName(differenceToggle, "Highlight audio differences");
        void UpdateDifferenceMode()
        {
            audioDifferenceEnabled = differenceToggle.IsChecked == true;
            differenceIcon.Opacity = audioDifferenceEnabled ? 1 : 0.35;
            if (audioTimeline != null)
                foreach (var wave in audioTimeline.Waves) wave.ShowDifferences = audioDifferenceEnabled;
            if (audioOverview != null) audioOverview.ShowDifferences = audioDifferenceEnabled;
            if (audioDifferenceEnabled) _ = ApplyAudioDifferencesAsync();
        }
        differenceToggle.Checked += (_, _) => UpdateDifferenceMode();
        differenceToggle.Unchecked += (_, _) => UpdateDifferenceMode();
        UpdateDifferenceMode();
        imageToolbar.Children.Add(differenceToggle);
        var repeatIcon = new Image { Source = DifferencerStatusIcons.GetCustomIcon(144), Stretch = Stretch.Uniform, Opacity = 0.35 };
        var repeatToggle = new ToggleButton
        {
            Content = repeatIcon,
            Style = TryFindResource("IconToggleButton") as Style,
            Margin = new Thickness(0, 0, 8, 8),
            ToolTip = "リピート：波形マップをドラッグしてA–B範囲を選択",
            IsEnabled = duration > 0
        };
        System.Windows.Automation.AutomationProperties.SetName(repeatToggle, "A–Bリピート");
        void UpdateRepeat()
        {
            bool enabled = repeatToggle.IsChecked == true;
            repeatIcon.Opacity = enabled ? 1 : 0.35;
            audioOverview?.SetRepeatEnabled(enabled);
        }
        repeatToggle.Checked += (_, _) => UpdateRepeat();
        repeatToggle.Unchecked += (_, _) => UpdateRepeat();
        imageToolbar.Children.Add(repeatToggle);
        // Exclusive speed selection: selected icon is opaque, others are dimmed.
        var speedGroup = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(6, 0, 0, 8) };
        double[] speeds = { 0.5, 0.75, 1.0, 1.5, 1.75, 2.0 };
        for (int i = 0; i < speeds.Length; i++)
        {
            double speed = speeds[i];
            var image = new Image
            {
                Source = DifferencerStatusIcons.GetCustomIcon(145 + i),
                Width = 36, Height = 36,
                Stretch = Stretch.Uniform,
                Opacity = i == 2 ? 1 : 0.35
            };
            var radio = new RadioButton
            {
                GroupName = "AudioPlaybackSpeed",
                Content = image,
                IsChecked = i == 2,
                ToolTip = $"再生速度 {speed:0.##}倍",
                Margin = new Thickness(0, 0, 3, 0),
                Style = TryFindResource("AudioSpeedRadioButton") as Style
            };
            // No radio style resource is required: hide the native indicator.
            if (radio.Style == null)
            {
                var template = new ControlTemplate(typeof(RadioButton));
                var border = new FrameworkElementFactory(typeof(Border));
                border.SetValue(Border.BackgroundProperty, Brushes.Transparent);
                var presenter = new FrameworkElementFactory(typeof(ContentPresenter));
                presenter.SetValue(ContentPresenter.ContentSourceProperty, "Content");
                border.AppendChild(presenter);
                template.VisualTree = border;
                radio.Template = template;
            }
            radio.Checked += (_, _) =>
            {
                foreach (var item in speedGroup.Children)
                    if (item is RadioButton button && button.Content is Image icon)
                        icon.Opacity = ReferenceEquals(button, radio) ? 1 : 0.35;
                try { audioPlayer?.SetSpeed(speed); }
                catch (Exception ex) { ViewModel.ReportError(ex); }
            };
            speedGroup.Children.Add(radio);
        }
        imageToolbar.Children.Add(speedGroup);
        // Recalculate after layout, and whenever the visible waveform width changes.
        audioTimeline.ZoomChanged += value => zoom.Value = value * 100;
        audioTimeline!.Waves[0].SizeChanged += (_, _) => UpdateMaximum();
        UpdateMaximum();
        imageToolbar.Visibility = imageControlRow.Visibility = Visibility.Visible;
    }
}

// Compact block waveforms with playback progress and post-volume output peaks.
internal sealed class AudioOverview : Control
{
    internal bool RepeatEnabled { get; private set; }
    internal event Action<double?, double?> RepeatChanged;
    private double? repeatA, repeatB;
    private bool selectingRepeat;
    private bool draggingViewport;
    private bool viewportMoved;
    private double viewportDragX, viewportDragStart;
    internal event Action<double> ViewportDragged;
    internal event Action<double> ViewportClicked;
    internal void SetRepeatEnabled(bool enabled)
    {
        RepeatEnabled = enabled;
        selectingRepeat = false;
        draggingViewport = false;
        viewportMoved = false;
        if (IsMouseCaptured) ReleaseMouseCapture();
        repeatA = repeatB = null;
        Cursor = enabled ? Cursors.Cross : Cursors.Arrow;
        RepeatChanged?.Invoke(null, null);
        InvalidateVisual();
    }
    private double RepeatTime(Point point) =>
        Math.Clamp(point.X / Math.Max(1, ActualWidth - 100), 0, 1) * duration;
    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonDown(e);
        if (!RepeatEnabled && duration > 0 && viewDuration > 0 && viewDuration < duration)
        {
            double width = Math.Max(0, ActualWidth - 100);
            double x = e.GetPosition(this).X;
            double left = viewStart / duration * width;
            double right = (viewStart + viewDuration) / duration * width;
            if (x >= left && x <= right && CaptureMouse())
            {
                draggingViewport = true;
                viewportMoved = false;
                viewportDragX = x;
                viewportDragStart = viewStart;
                Cursor = Cursors.SizeWE;
                e.Handled = true;
                return;
            }
        }
        if (!RepeatEnabled || duration <= 0 || e.GetPosition(this).X > ActualWidth - 100) return;
        if (!CaptureMouse()) return;
        selectingRepeat = true;
        repeatA = RepeatTime(e.GetPosition(this));
        repeatB = null;
        RepeatChanged?.Invoke(null, null);
        InvalidateVisual();
        e.Handled = true;
    }
    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        if (draggingViewport)
        {
            double width = Math.Max(1, ActualWidth - 100);
            double pixels = e.GetPosition(this).X - viewportDragX;
            if (Math.Abs(pixels) >= SystemParameters.MinimumHorizontalDragDistance) viewportMoved = true;
            if (viewportMoved)
            {
                double delta = pixels / width * duration;
                ViewportDragged?.Invoke(Math.Clamp(viewportDragStart + delta, 0, Math.Max(0, duration - viewDuration)));
            }
            e.Handled = true;
            return;
        }
        if (!selectingRepeat) return;
        repeatB = RepeatTime(e.GetPosition(this));
        InvalidateVisual();
        e.Handled = true;
    }
    protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonUp(e);
        if (draggingViewport)
        {
            bool click = !viewportMoved;
            double clickTime = RepeatTime(e.GetPosition(this));
            draggingViewport = false;
            viewportMoved = false;
            ReleaseMouseCapture();
            Cursor = Cursors.Arrow;
            if (click) ViewportClicked?.Invoke(clickTime);
            e.Handled = true;
            return;
        }
        if (!selectingRepeat) return;
        repeatB = RepeatTime(e.GetPosition(this));
        selectingRepeat = false;
        ReleaseMouseCapture();
        RepeatChanged?.Invoke(repeatA, repeatB);
        InvalidateVisual();
        e.Handled = true;
    }
    protected override void OnLostMouseCapture(MouseEventArgs e)
    {
        base.OnLostMouseCapture(e);
        if (draggingViewport)
        {
            draggingViewport = false;
            viewportMoved = false;
            Cursor = Cursors.Arrow;
            return;
        }
        if (!selectingRepeat) return;
        selectingRepeat = false;
        repeatA = repeatB = null;
        InvalidateVisual();
    }
    private void DrawRepeat(DrawingContext dc, double width)
    {
        if (!RepeatEnabled || !repeatA.HasValue || duration <= 0) return;
        double a = repeatA.Value / duration * width;
        double b = (repeatB ?? repeatA).Value / duration * width;
        dc.PushOpacity(0.18);
        dc.DrawRectangle(Brushes.Yellow, null, new Rect(Math.Min(a, b), 0, Math.Abs(b - a), ActualHeight));
        dc.Pop();
        void Marker(double x)
        {
            x = Math.Clamp(x, 1, Math.Max(1, width - 1));
            dc.DrawLine(new Pen(Brushes.Yellow, 1.5), new Point(x, 0), new Point(x, ActualHeight));
            var triangles = new StreamGeometry();
            using (var context = triangles.Open())
            {
                context.BeginFigure(new Point(x - 4, 0), true, true);
                context.LineTo(new Point(x + 4, 0), true, false);
                context.LineTo(new Point(x, 5), true, false);
                context.BeginFigure(new Point(x - 4, ActualHeight), true, true);
                context.LineTo(new Point(x + 4, ActualHeight), true, false);
                context.LineTo(new Point(x, ActualHeight - 5), true, false);
            }
            dc.DrawGeometry(Brushes.Yellow, null, triangles);
        }
        Marker(a);
        if (repeatB.HasValue) Marker(b);
    }
    private AudioDifferences differences;
    private bool showDifferences;
    internal bool ShowDifferences
    {
        get => showDifferences;
        set { showDifferences = value; InvalidateVisual(); }
    }

    internal void SetDifferences(AudioDifferences value, bool enabled)
    {
        differences = value;
        ShowDifferences = enabled;
    }

    private bool IsDifferenceAt(int side, double from, double to)
    {
        if (!showDifferences || differences == null) return false;
        var channels = side == 0 ? differences.Channels : differences.TargetChannels;
        foreach (var ranges in channels)
            foreach (var range in ranges)
                if (range.Start < to && range.End > from) return true;
        return false;
    }

    private void DrawDifferenceJoin(DrawingContext dc, int side, double width, double rowHeight)
    {
        if (!showDifferences || differences == null || duration <= 0) return;
        double? join = side == 0 ? differences.SourceJoin : differences.TargetJoin;
        if (!join.HasValue) return;
        double x = Math.Clamp(join.Value / duration, 0, 1) * width;
        double top = side * rowHeight + 3, bottom = (side + 1) * rowHeight - 3;
        dc.DrawLine(new Pen(Brushes.Red, 2) { DashStyle = DashStyles.Dash },
            new Point(x, top), new Point(x, bottom));
    }

    private readonly AudioEnvelope[] envelopes;
    private readonly double duration;
    private double position;
    private readonly float[] levels = new float[2];
    private readonly float[][] barPeaks = new float[2][];
    private float[] GetBarPeaks(int side, int bars)
    {
        if (barPeaks[side]?.Length == bars) return barPeaks[side];
        var result = new float[bars];
        var envelope = envelopes[side];
        int bins = envelope.Data?.BinCount ?? envelope.Max[0].Length;
        if (bins == 0) return result;
        for (int bar = 0; bar < bars; bar++)
        {
            int first = Math.Min(bins - 1, bar * bins / bars);
            int last = Math.Min(bins, Math.Max(first + 1, (bar + 1) * bins / bars));
            for (int ch = 0; ch < envelope.Channels; ch++)
                for (int bin = first; bin < last; bin++)
                    result[bar] = Math.Max(result[bar], Math.Max(Math.Abs(envelope.Data?.Minimum(ch, bin) ?? envelope.Min[ch][bin]), envelope.Data?.Maximum(ch, bin) ?? envelope.Max[ch][bin]));
        }
        return barPeaks[side] = result;
    }
    private double viewStart, viewDuration;
    internal void SetViewport(double start, double span)
    {
        viewStart = start; viewDuration = span; InvalidateVisual();
    }
    private const double UnplayedOpacity = 0.4;

    internal AudioOverview(AudioEnvelope[] envelopes, double duration)
    {
        this.envelopes = envelopes;
        this.duration = duration;
        ClipToBounds = true;
        ToolTip = "Source / Target — click waveform to seek; output level at right";
    }

    internal void Update(double seconds, float source, float target)
    {
        position = seconds;
        levels[0] = source;
        levels[1] = target;
        InvalidateVisual();
    }

    protected override void OnRender(DrawingContext dc)
    {
        base.OnRender(dc);
        dc.DrawRoundedRectangle(Background, null, new Rect(RenderSize), 5, 5);
        double width = Math.Max(0, ActualWidth - 100), rowHeight = ActualHeight / 2;
        for (int side = 0; side < 2; side++)
        {
            var color = TryFindResource(side == 0 ? "SourceColor" : "TargetColor") as Brush ?? Foreground;
            var envelope = envelopes[side];
            double baseline = rowHeight * (side + 1) - 3;
            if (envelope != null && envelope.Duration > 0 && duration > 0)
            {
                int bars = Math.Max(1, (int)(width * envelope.Duration / duration / 4));
                var peaks = GetBarPeaks(side, bars);
                for (int bar = 0; bar < bars; bar++)
                {
                    float peak = peaks[bar];
                    double height = Math.Max(1, Math.Clamp(peak, 0, 1) * (rowHeight - 6));
                    bool played = (double)bar / bars * envelope.Duration < position;
                    if (!played) dc.PushOpacity(UnplayedOpacity);
                    double barStart = (double)bar / bars * envelope.Duration;
                    double barEnd = (double)(bar + 1) / bars * envelope.Duration;
                    dc.DrawRectangle(IsDifferenceAt(side, barStart, barEnd) ? Brushes.Red : color, null,
                        new Rect(bar * 4, baseline - height, 2, height));
                    if (!played) dc.Pop();
                }
            }
            DrawDifferenceJoin(dc, side, width, rowHeight);
            // dBFS scale: -60 dB to 0 dB, including saved volume/mute.
            double level = levels[side] > 0 ? Math.Clamp((20 * Math.Log10(levels[side]) + 60) / 60, 0, 1) : 0;
            for (int segment = 0; segment < 16; segment++)
            {
                dc.PushOpacity(segment < level * 16 ? 1 : 0.15);
                dc.DrawRectangle(segment >= 14 ? Brushes.OrangeRed : color, null,
                    new Rect(width + 10 + segment * 5, side * rowHeight + 7, 3, Math.Max(1, rowHeight - 14)));
                dc.Pop();
            }
        }
        if (duration > 0)
        {
            double x = Math.Clamp(position / duration, 0, 1) * width;
            dc.DrawLine(new Pen(Brushes.OrangeRed, 1), new Point(x, 0), new Point(x, ActualHeight));
        }
        if (duration > 0 && viewDuration > 0 && viewDuration < duration)
            dc.DrawRectangle(null, new Pen(Foreground, 1), new Rect(viewStart / duration * width, 1,
                viewDuration / duration * width, Math.Max(0, ActualHeight - 2)));
        DrawRepeat(dc, width);
    }
}

// Both panes share one horizontal time axis; only visible pixels are rendered.
internal sealed class AudioTimeline : Grid
{
    internal static TextBlock CreateHeader(AudioEnvelope current, AudioEnvelope other, bool source)
    {
        var label = new TextBlock
        {
            Margin = new Thickness(8, 3, 8, 3),
            FontWeight = FontWeights.SemiBold,
            TextWrapping = TextWrapping.Wrap
        };
        label.SetResourceReference(TextBlock.ForegroundProperty, "Ink");
        label.Inlines.Add(new System.Windows.Documents.Run(source ? "Source" : "Target"));
        if (current == null) { label.Inlines.Add(" — No file"); return label; }
        void Add(string title, string value, bool different)
        {
            label.Inlines.Add("   " + title);
            var run = new System.Windows.Documents.Run(value);
            if (other != null && different) run.Foreground = Brushes.Red;
            label.Inlines.Add(run);
        }
        string Number(double value, string format) => value.ToString(format, System.Globalization.CultureInfo.InvariantCulture);
        Add("", current.Channels + " ch", current.Channels != other?.Channels);
        Add("", TimeSpan.FromSeconds(current.Duration).ToString(@"hh\:mm\:ss\.fff"),
            Math.Round(current.Duration * 1000) != Math.Round((other?.Duration ?? 0) * 1000));
        Add("Sample Rate: ", Number(current.SampleRate / 1000.0, "0.0##") + " kHz", current.SampleRate != other?.SampleRate);
        Add("Bit Depth: ", current.BitDepth.HasValue ? current.BitDepth + (current.FloatingPoint ? " bit float" : " bit") : "N/A",
            current.BitDepth != other?.BitDepth || current.FloatingPoint != other?.FloatingPoint);
        Add("Bit Rate: ", current.BitRate.HasValue ? Number(current.BitRate.Value / 1000.0, "0.###") + " kbps" : "N/A",
            current.BitRate != other?.BitRate);
        label.ToolTip = "PCM bit rate = sample rate × bit depth × channels. MP3 shows average encoded bit rate; bit depth is not applicable.";
        return label;
    }

    internal readonly AudioWaveform[] Waves = new AudioWaveform[2];
    private readonly ScrollBar scroll = new ScrollBar { Orientation = Orientation.Horizontal, Height = 16, Minimum = 0 };
    private double zoom = 1;
    internal double Zoom => zoom;
    internal double Offset => scroll.Value;
    internal double Viewport => Math.Max(1, Waves[0].ActualWidth);
    internal event Action<double> SeekRequested;
    internal event Action<double> ZoomChanged;
    internal event Action<double, double> ViewChanged;
    private bool applyingView;

    internal AudioTimeline(AudioEnvelope[] envelopes, double duration, string[] paths)
    {
        RowDefinitions.Add(new RowDefinition());
        RowDefinitions.Add(new RowDefinition());
        // Reserve scrollbar space even at fit so zoom never changes waveform height.
        RowDefinitions.Add(new RowDefinition { Height = new GridLength(16) });
        for (int side = 0; side < 2; side++)
        {
            var wave = Waves[side] = new AudioWaveform(envelopes[side], duration, paths[side]);

            wave.SetResourceReference(Control.ForegroundProperty, side == 0 ? "SourceColor" : "TargetColor");
            wave.SetResourceReference(Control.BackgroundProperty, "WindowBackground");
            var pane = new Grid();
            pane.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            pane.RowDefinitions.Add(new RowDefinition());
            var label = CreateHeader(envelopes[side], envelopes[1 - side], side == 0);
            pane.Children.Add(label);
            SetRow(wave, 1); pane.Children.Add(wave);
            var frame = new Border
            {
                BorderThickness = new Thickness(3),
                CornerRadius = new CornerRadius(5),
                Margin = new Thickness(0, side == 0 ? 0 : 4, 0, 0),
                Child = pane
            };
            frame.SetResourceReference(Border.BorderBrushProperty, side == 0 ? "SourceColor" : "TargetColor");
            SetRow(frame, side); Children.Add(frame);
            wave.PositionClicked += seconds => SeekRequested?.Invoke(seconds);
            wave.ViewChanged += () =>
            {
                if (applyingView || wave.ViewDuration <= 0) return;
                applyingView = true;
                try
                {
                    zoom = Math.Max(1, duration / wave.ViewDuration);
                    UpdateRange();
                    scroll.Value = wave.ViewStart / Math.Max(duration, 0.001) * Viewport * zoom;
                }
                finally { applyingView = false; }
                ApplyView(); ZoomChanged?.Invoke(zoom);
            };
        }
        SetRow(scroll, 2); Children.Add(scroll);
        scroll.ValueChanged += (_, _) => ApplyView();
        Waves[0].SizeChanged += (_, e) =>
        {
            double fraction = e.PreviousSize.Width > 0 ? Offset / (e.PreviousSize.Width * zoom) : 0;
            UpdateRange();
            scroll.Value = Math.Clamp(fraction * Viewport * zoom, 0, LastOffset);
            ApplyView();
        };
    }

    // Maximum is the final scroll Value; ViewportSize controls the thumb size.
    private double LastOffset => Math.Max(0, Viewport * (zoom - 1));

    private void UpdateRange()
    {
        scroll.Maximum = LastOffset;
        scroll.ViewportSize = Viewport;
        scroll.SmallChange = 48;
        scroll.LargeChange = Viewport * 0.8;
        scroll.IsEnabled = zoom > 1;
        // Keep the reserved row height, but hide the scrollbar at 100%.
        scroll.Visibility = zoom > 1 ? Visibility.Visible : Visibility.Hidden;
    }
    private void ApplyView()
    {
        if (applyingView) return;
        applyingView = true;
        try
        {
            double start = Waves[0].Duration * Offset / Math.Max(1, Viewport * zoom);
            foreach (var wave in Waves) wave.SetViewRange(start, wave.Duration / zoom);
        }
        finally { applyingView = false; }
        ViewChanged?.Invoke(Waves[0].ViewStart, Waves[0].ViewDuration);
    }
    internal void SetZoom(double value)
    {
        double newZoom = Math.Clamp(value, 1, Math.Max(1, Waves[0].Duration / 0.5));
        if (zoom == newZoom) return;

        // Keep the time currently at the viewport's left edge at X=0.
        double leftFraction = Offset / (Viewport * zoom);
        zoom = newZoom;
        UpdateRange();
        scroll.Value = Math.Clamp(leftFraction * Viewport * zoom, 0, LastOffset);
        ApplyView();
    }
    internal void Fit() { SetZoom(1); scroll.Value = 0; foreach (var wave in Waves) wave.Fit(); }
    internal void SetViewStart(double seconds)
    {
        double duration = Waves[0].Duration;
        if (duration <= 0 || zoom <= 1) return;
        scroll.Value = Math.Clamp(seconds / duration * Viewport * zoom, 0, LastOffset);
    }
    internal void ScrollBy(double pixels) => scroll.Value = Math.Clamp(Offset + pixels, 0, LastOffset);
    internal void UpdatePosition(double seconds, bool follow)
    {
        foreach (var wave in Waves) wave.Position = seconds;
        if (!follow || zoom <= 1 || Array.Exists(Waves, w => w.IsPanning)) return;
        double x = Waves[0].PixelAt(seconds);
        // Scroll smoothly once the playhead reaches the right-hand safe area.
        if (x > Offset + Viewport * 0.85 || x < Offset)
            scroll.Value = Math.Clamp(x - Viewport * (x < Offset ? 0.15 : 0.85), 0, LastOffset);
    }
}

// DIM supplies annotations; the reusable control owns waveform rendering.
internal sealed class AudioWaveform : DesktopIniManager.Controls.WaveformControl
{
    private AudioDifferences differences;
    private bool source = true, showDifferences;
    internal bool ShowDifferences
    {
        get => showDifferences;
        set { showDifferences = value; InvalidateVisual(); }
    }
    internal void SetDifferences(AudioDifferences value, bool enabled, bool source = true)
    {
        differences = value; this.source = source; ShowDifferences = enabled;
    }
    internal AudioWaveform(AudioEnvelope envelope, double duration, string path)
    {
        SetData(envelope?.Data, duration);
        ToolTip = "Wheel: time zoom / Ctrl+wheel: amplitude / Drag: pan / Click: seek";
    }
    internal void SetView(double zoom, double offset) =>
        SetViewRange(Duration * offset / Math.Max(1, ActualWidth * zoom), Duration / zoom);
    internal double TimeAt(double x) => XToTime(x);
    internal double PixelAt(double seconds) => ViewDuration > 0 ? seconds / ViewDuration * ActualWidth : 0;
    internal double Position { set => SetPosition(TimeSpan.FromSeconds(value)); }
    protected override void DrawOverlay(DrawingContext dc)
    {
        if (!showDifferences || differences == null || Data == null) return;
        var channels = source ? differences.Channels : differences.TargetChannels;
        double lane = ActualHeight / Data.Channels;
        for (int ch = 0; ch < Math.Min(channels.Length, Data.Channels); ch++)
        {
            // Combine visible difference intervals into one clip. Repainting the
            // entire waveform for each interval made dense differences quadratic.
            var clip = new GeometryGroup();
            foreach (var range in channels[ch])
            {
                if (range.End <= ViewStart || range.Start >= ViewStart + ViewDuration) continue;
                double left = Math.Max(0, TimeToX(range.Start));
                double right = Math.Min(ActualWidth, TimeToX(range.End));
                if (right <= left) continue;
                clip.Children.Add(new RectangleGeometry(new Rect(left, ch * lane, right - left, lane)));
            }
            if (clip.Children.Count == 0) continue;
            clip.Freeze();
            dc.PushClip(clip);
            DrawWaveform(dc, ch, Brushes.Red);
            dc.Pop();
        }
        double? join = source ? differences.SourceJoin : differences.TargetJoin;
        if (join.HasValue)
        {
            double x = TimeToX(join.Value);
            dc.DrawLine(new Pen(Brushes.Red, 2) { DashStyle = DashStyles.Dash }, new Point(x, 0), new Point(x, ActualHeight));
        }
    }
}
