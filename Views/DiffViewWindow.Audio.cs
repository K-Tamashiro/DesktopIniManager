using System;
using System.Threading;
using System.Threading.Tasks;
using NAudio.Wave;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Input;
using System.Windows.Threading;
using DesktopIniManager.Services;
using DesktopIniManager.ViewModels;
using DesktopIniManager.Properties;

namespace DesktopIniManager.Views;

internal sealed partial class DiffViewWindow
{
    private AudioDiffPlayer audioPlayer;
    private CancellationTokenSource audioLoading;
    private DispatcherTimer audioTimer;
    private AudioTimeline audioTimeline;
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
        audioTimeline = null;
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
        var envelopes = await Task.Run(() => new[]
        {
            AudioEnvelope.Load(request.Source, request.Token),
            AudioEnvelope.Load(request.Target, request.Token)
        }, request.Token).ConfigureAwait(false);
        await Dispatcher.InvokeAsync(() =>
        {
            request.Token.ThrowIfCancellationRequested();
            BuildAudioView(request.Source, request.Target, envelopes);
        }).Task.ConfigureAwait(false);
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
        var overview = new AudioOverview(envelopes, duration);
        overview.SetResourceReference(Control.ForegroundProperty, "AudioWaveformInk");
        overview.SetResourceReference(Control.BackgroundProperty, "CardBackground");
        audioOverviewHost.Children.Add(overview);
        audioOverviewHost.Visibility = Visibility.Visible;
        body.Children.Clear();
        var layout = audioTimeline = new AudioTimeline(envelopes, duration, new[] { source, target });
        Grid.SetColumnSpan(layout, 3);
        body.Children.Add(layout);
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
        void Refresh(double position)
        {
            updating = true;
            if (!dragging) seek.Value = position;
            updating = false;
            time.Text = $"{TimeSpan.FromSeconds(position):hh\\:mm\\:ss} / {TimeSpan.FromSeconds(duration):hh\\:mm\\:ss}";
            layout.UpdatePosition(position, audioPlayer?.Playing == true);
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
        overview.MouseLeftButtonDown += (_, e) =>
        {
            Seek(Math.Clamp(e.GetPosition(overview).X / Math.Max(1, overview.ActualWidth - 100), 0, 1) * duration);
            e.Handled = true;
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
        audioTimer = new DispatcherTimer(DispatcherPriority.Background, Dispatcher) { Interval = TimeSpan.FromMilliseconds(33) };
        audioTimer.Tick += (_, _) =>
        {
            if (audioPlayer == null) return;
            if (!dragging) Refresh(audioPlayer.Position);
            var error = Interlocked.Exchange(ref audioPlayer.PlaybackError, null);
            if (error != null) ViewModel.ReportError(error);
        };
        audioTimer.Start();
        Refresh(0);
    }

    private void BuildAudioZoom(double duration, AudioEnvelope[] envelopes)
    {
        // At maximum zoom, render approximately ten horizontal pixels per source sample.
        // Use the longest duration and highest sample rate across Source and Target.
        int sampleRate = Math.Max(envelopes[0]?.SampleRate ?? 0, envelopes[1]?.SampleRate ?? 0);
        const double pixelsPerSampleAtMaximum = 10.0;
        var zoom = imageZoom = new Slider
        {
            Minimum = 100,
            Maximum = 100,
            Value = 100,
            SmallChange = 100,
            LargeChange = 100,
            TickFrequency = 100,
            IsSnapToTickEnabled = true,
            Width = 150,
            VerticalAlignment = VerticalAlignment.Center,
            Style = TryFindResource("ZoomSlider") as Style,
            ToolTip = "Horizontal waveform zoom"
        };
        void UpdateMaximum()
        {
            double width = audioTimeline?.Viewport ?? 1;
            double maximumZoom = sampleRate > 0 && duration > 0
                ? Math.Max(1, Math.Ceiling(duration * sampleRate * pixelsPerSampleAtMaximum / width))
                : 1;
            zoom.Maximum = Math.Max(100, Math.Ceiling(maximumZoom) * 100);
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
            if (audioDifferenceEnabled) _ = ApplyAudioDifferencesAsync();
        }
        differenceToggle.Checked += (_, _) => UpdateDifferenceMode();
        differenceToggle.Unchecked += (_, _) => UpdateDifferenceMode();
        UpdateDifferenceMode();
        imageToolbar.Children.Add(differenceToggle);
        // Recalculate after layout, and whenever the visible waveform width changes.
        audioTimeline!.Waves[0].SizeChanged += (_, _) => UpdateMaximum();
        UpdateMaximum();
        imageToolbar.Visibility = imageControlRow.Visibility = Visibility.Visible;
    }
}

// Compact block waveforms with playback progress and post-volume output peaks.
internal sealed class AudioOverview : Control
{
    private readonly AudioEnvelope[] envelopes;
    private readonly double duration;
    private double position;
    private readonly float[] levels = new float[2];
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
                int bins = envelope.Max[0].Length;
                for (int bar = 0; bar < bars; bar++)
                {
                    float peak = 0;
                    int first = Math.Min(bins - 1, bar * bins / bars);
                    int last = Math.Min(bins, Math.Max(first + 1, (bar + 1) * bins / bars));
                    for (int ch = 0; ch < envelope.Channels; ch++)
                        for (int bin = first; bin < last; bin++)
                            peak = Math.Max(peak, Math.Max(Math.Abs(envelope.Min[ch][bin]), envelope.Max[ch][bin]));
                    double height = Math.Max(1, Math.Clamp(peak, 0, 1) * (rowHeight - 6));
                    bool played = (double)bar / bars * envelope.Duration < position;
                    if (!played) dc.PushOpacity(UnplayedOpacity);
                    dc.DrawRectangle(color, null, new Rect(bar * 4, baseline - height, 2, height));
                    if (!played) dc.Pop();
                }
            }
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
    }
}

// Both panes share one horizontal time axis; only visible pixels are rendered.
internal sealed class AudioTimeline : Grid
{
    internal readonly AudioWaveform[] Waves = new AudioWaveform[2];
    private readonly ScrollBar scroll = new ScrollBar { Orientation = Orientation.Horizontal, Height = 16, Minimum = 0 };
    private double zoom = 1;
    internal double Zoom => zoom;
    internal double Offset => scroll.Value;
    internal double Viewport => Math.Max(1, Waves[0].ActualWidth);
    internal event Action<double> SeekRequested;
    private double seekPosition;
    private bool isPanning;
    private readonly ProgressBar waveformWaiting = new ProgressBar
    {
        IsIndeterminate = true,
        Height = 8,
        VerticalAlignment = VerticalAlignment.Top,
        Visibility = Visibility.Collapsed,
        IsHitTestVisible = false
    };

    private void UpdateWaiting()
    {
        waveformWaiting.Visibility = Array.Exists(Waves, w => w?.IsSampling == true)
            ? Visibility.Visible : Visibility.Collapsed;
    }

    internal AudioTimeline(AudioEnvelope[] envelopes, double duration, string[] paths)
    {
        RowDefinitions.Add(new RowDefinition());
        RowDefinitions.Add(new RowDefinition());
        // Reserve scrollbar space even at fit so zoom never changes waveform height.
        RowDefinitions.Add(new RowDefinition { Height = new GridLength(16) });
        for (int side = 0; side < 2; side++)
        {
            var wave = Waves[side] = new AudioWaveform(envelopes[side], duration, paths[side]);
            wave.SamplingChanged += UpdateWaiting;
            wave.SetResourceReference(Control.ForegroundProperty, side == 0 ? "SourceColor" : "TargetColor");
            wave.SetResourceReference(Control.BackgroundProperty, "WindowBackground");
            var pane = new Grid();
            pane.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            pane.RowDefinitions.Add(new RowDefinition());
            var label = new TextBlock
            {
                Margin = new Thickness(8, 3, 8, 3),
                FontWeight = FontWeights.SemiBold,
                Text = (side == 0 ? "Source" : "Target") + (envelopes[side] == null ? " — No file" :
                    $"   {envelopes[side].Channels} ch   {TimeSpan.FromSeconds(envelopes[side].Duration):hh\\:mm\\:ss\\.fff}")
            };
            label.SetResourceReference(TextBlock.ForegroundProperty, "Ink");
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
            // A click must seek even when WPF mouse capture is lost between down and up.
            // Dragging pans the view only; seeking is performed by a click.
            Point dragStart = default;
            double dragOffset = 0;
            bool dragging = false;
            bool pressed = false;
            wave.PreviewMouseLeftButtonDown += (_, e) =>
            {
                dragStart = e.GetPosition(this);
                dragOffset = Offset;
                dragging = false;
                pressed = true;
                wave.CaptureMouse();
                e.Handled = true;
            };
            wave.PreviewMouseMove += (_, e) =>
            {
                if (!pressed || e.LeftButton != MouseButtonState.Pressed) return;
                double delta = e.GetPosition(this).X - dragStart.X;
                if (!dragging && Math.Abs(delta) < SystemParameters.MinimumHorizontalDragDistance) return;
                dragging = true;
                isPanning = true;
                // Move the same scroll value used by the scrollbar itself.
                scroll.Value = Math.Clamp(dragOffset - delta, scroll.Minimum, LastOffset);
                e.Handled = true;
            };
            wave.AddHandler(UIElement.PreviewMouseLeftButtonUpEvent, new MouseButtonEventHandler((_, e) =>
            {
                if (!pressed) return;
                Point release = e.GetPosition(wave);
                bool wasDragging = dragging;
                pressed = false;
                dragging = false;
                isPanning = false;
                if (wave.IsMouseCaptured) wave.ReleaseMouseCapture();
                if (!wasDragging && release.X >= 0 && release.X <= wave.ActualWidth &&
                    release.Y >= 0 && release.Y <= wave.ActualHeight)
                    SeekRequested?.Invoke(wave.TimeAt(release.X));
                e.Handled = true;
            }), true);
        }
        SetRow(scroll, 2); Children.Add(scroll);
        SetRow(waveformWaiting, 2); Children.Add(waveformWaiting);
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
        foreach (var wave in Waves) wave.SetView(zoom, Offset);
    }
    internal void SetZoom(double value)
    {
        double newZoom = Math.Max(1, value);
        if (zoom == newZoom) return;

        // Keep the time currently at the viewport's left edge at X=0.
        double leftFraction = Offset / (Viewport * zoom);
        zoom = newZoom;
        UpdateRange();
        scroll.Value = Math.Clamp(leftFraction * Viewport * zoom, 0, LastOffset);
        ApplyView();
    }
    internal void Fit() { SetZoom(1); scroll.Value = 0; }
    internal void ScrollBy(double pixels) => scroll.Value = Math.Clamp(Offset + pixels, 0, LastOffset);
    internal void UpdatePosition(double seconds, bool follow)
    {
        seekPosition = seconds;

        foreach (var wave in Waves) wave.Position = seconds;
        if (!follow || zoom <= 1 || isPanning) return;
        double x = Waves[0].PixelAt(seconds);
        // Scroll smoothly once the playhead reaches the right-hand safe area.
        if (x > Offset + Viewport * 0.85 || x < Offset)
            scroll.Value = Math.Clamp(x - Viewport * (x < Offset ? 0.15 : 0.85), 0, LastOffset);
    }
}

// One connected waveform at every scale. Only the sampling density changes.
internal sealed class AudioWaveform : Control
{
    private AudioDifferences differences;
    private bool differenceSource = true;
    private bool showDifferences;
    internal bool ShowDifferences
    {
        get => showDifferences;
        set { showDifferences = value; InvalidateVisual(); }
    }
    internal void SetDifferences(AudioDifferences value, bool enabled, bool source = true)
    {
        differences = value;
        differenceSource = source;
        ShowDifferences = enabled;
    }

    private void DrawDifferences(DrawingContext dc)
    {
        if (!showDifferences || differences == null || geometry == null) return;
        var channels = differenceSource ? differences.Channels : differences.TargetChannels;
        double? join = differenceSource ? differences.SourceJoin : differences.TargetJoin;
        if (join.HasValue)
        {
            double x = PixelAt(join.Value);
            var joinPen = new Pen(Brushes.Red, 2) { DashStyle = DashStyles.Dash };
            dc.DrawLine(joinPen, new Point(x, 0), new Point(x, ActualHeight));
        }
        var clip = new StreamGeometry();
        using (var context = clip.Open())
        {
            double lane = ActualHeight / envelope.Channels;
            for (int ch = 0; ch < Math.Min(envelope.Channels, channels.Length); ch++)
            {
                var ranges = channels[ch];
                double startTime = TimeAt(0), endTime = TimeAt(ActualWidth);
                int low = 0, high = ranges.Count;
                while (low < high)
                {
                    int mid = low + (high - low) / 2;
                    if (ranges[mid].End <= startTime) low = mid + 1; else high = mid;
                }
                for (int i = low; i < ranges.Count && ranges[i].Start < endTime; i++)
                {
                    double left = Math.Max(offset, PixelAt(ranges[i].Start));
                    double right = Math.Min(offset + ActualWidth, Math.Max(left + 1, PixelAt(ranges[i].End)));
                    context.BeginFigure(new Point(left, ch * lane), true, true);
                    context.LineTo(new Point(right, ch * lane), true, false);
                    context.LineTo(new Point(right, (ch + 1) * lane), true, false);
                    context.LineTo(new Point(left, (ch + 1) * lane), true, false);
                }
            }
        }
        clip.FillRule = FillRule.Nonzero;
        dc.PushClip(clip);
        dc.PushOpacity(0.25);
        dc.DrawGeometry(null, new Pen(Brushes.Red, 5), geometry);
        dc.Pop();
        dc.DrawGeometry(null, new Pen(Brushes.Red, 1.5), geometry);
        dc.Pop();
    }
    // Maximum allowed vertical deviation (screen pixels) when two extrema collapse
    // into a single representative point. Tune this without changing the renderer.
    private const double WaveformDetailThreshold = 1.0;
    private const double WaveformThresholdScale = 99999.0;
    private static double EffectiveThreshold => WaveformDetailThreshold * WaveformThresholdScale;

    private readonly AudioEnvelope envelope;
    private readonly double duration;
    private readonly string path;
    private double position;
    private double zoom = 1, offset;
    private int requestVersion;
    private bool loading;
    internal bool IsSampling => loading;
    internal event Action SamplingChanged;
    private void SetSampling(bool value)
    {
        if (loading == value) return;
        loading = value;
        SamplingChanged?.Invoke();
    }
    private Point[][] sampledPoints;
    private Geometry geometry;
    private double cachedStart = double.NaN, cachedEnd = double.NaN;

    internal AudioWaveform(AudioEnvelope envelope, double duration, string path)
    {
        this.envelope = envelope;
        this.duration = duration;
        this.path = path;
        ClipToBounds = true;
        SizeChanged += (_, _) => { ResetSampling(); QueueSampling(); };
        Unloaded += (_, _) => { requestVersion++; SetSampling(false); sampledPoints = null; };
    }

    private void ResetSampling()
    {
        requestVersion++;
        sampledPoints = null;
        geometry = null;
        cachedStart = cachedEnd = double.NaN;
        InvalidateVisual();
    }

    internal void SetView(double scale, double horizontalOffset)
    {
        if (zoom == scale && offset == horizontalOffset) return;
        bool zoomChanged = zoom != scale;
        zoom = scale;
        offset = horizontalOffset;
        if (zoomChanged) ResetSampling();
        else
        {
            // Preview geometry covers only the previous viewport; rebuild when panning.
            if (sampledPoints == null || offset < cachedStart || offset + ActualWidth > cachedEnd)
                geometry = null;
            InvalidateVisual();
        }
        QueueSampling();
    }

    internal double TimeAt(double x) => duration * Math.Clamp((x + offset) / Math.Max(1, ActualWidth * zoom), 0, 1);
    internal double PixelAt(double seconds) => duration > 0 ? Math.Clamp(seconds / duration, 0, 1) * ActualWidth * zoom : 0;
    internal double Position { set { position = value; InvalidateVisual(); } }

    private bool samplingQueued;
    private void QueueSampling()
    {
        if (samplingQueued || envelope == null || duration <= 0 || zoom <= 1) return;
        // The overview already contains the information visible at coarse scales.
        // Avoid decoding minutes of PCM merely to draw a few screen pixels.
        if (envelope.SampleRate * duration / Math.Max(1, ActualWidth * zoom) > 128) return;
        samplingQueued = true;
        Dispatcher.BeginInvoke(new Action(() =>
        {
            samplingQueued = false;
            if (!IsLoaded || zoom <= 1 || ActualWidth < 1) return;
            if (envelope.SampleRate * duration / (ActualWidth * zoom) > 128) return;
            double visibleEnd = Math.Min(ActualWidth * zoom, offset + ActualWidth);
            if (!loading && (sampledPoints == null || offset < cachedStart || visibleEnd > cachedEnd))
                RequestSamples();
        }), DispatcherPriority.Background);
    }

    // Keep extrema in chronological order. All output is rendered with the same
    // connected-line geometry, irrespective of zoom or source duration.
    private sealed class PixelBucket
    {
        internal bool HasValue;
        internal long MinIndex, MaxIndex;
        internal float Min, Max;
        internal void Add(float value, long index)
        {
            if (!float.IsFinite(value)) return;
            if (!HasValue)
            {
                HasValue = true;
                Min = Max = value;
                MinIndex = MaxIndex = index;
                return;
            }
            if (value < Min) { Min = value; MinIndex = index; }
            if (value > Max) { Max = value; MaxIndex = index; }
        }
    }

    private static Point[] BuildChannelPoints(PixelBucket[] buckets, double pixelsPerFrame,
        long firstFrame, double center, double amplitude, double tolerance)
    {
        var points = new System.Collections.Generic.List<Point>(buckets.Length * 2);
        foreach (var bucket in buckets)
        {
            if (!bucket.HasValue) continue;
            var min = new Point((firstFrame + bucket.MinIndex) * pixelsPerFrame,
                center - bucket.Min * amplitude);
            var max = new Point((firstFrame + bucket.MaxIndex) * pixelsPerFrame,
                center - bucket.Max * amplitude);
            if (bucket.MinIndex <= bucket.MaxIndex)
            {
                points.Add(min);
                if (Math.Abs(max.Y - min.Y) > tolerance && bucket.MinIndex != bucket.MaxIndex) points.Add(max);
            }
            else
            {
                points.Add(max);
                if (Math.Abs(max.Y - min.Y) > tolerance && bucket.MinIndex != bucket.MaxIndex) points.Add(min);
            }
        }
        return points.ToArray();
    }

    private async void RequestSamples()
    {
        if (loading || envelope == null || string.IsNullOrEmpty(path) || ActualWidth < 1 || duration <= 0) return;
        SetSampling(true);
        int version = ++requestVersion;
        double width = ActualWidth;
        double height = ActualHeight;
        double scale = zoom;
        // Cache one viewport either side of the visible area.
        double left = Math.Max(0, offset - width);
        double right = Math.Min(width * scale, offset + width * 2);
        int columns = Math.Max(1, (int)Math.Ceiling(right - left));
        bool completed = false;
        try
        {
            var points = await Task.Run(() =>
            {
                using var reader = new AudioFileReader(path);
                int channels = reader.WaveFormat.Channels;
                int rate = reader.WaveFormat.SampleRate;
                double pixelsPerFrame = width * scale / (duration * rate);
                long totalFrames = reader.Length / reader.WaveFormat.BlockAlign;
                long firstFrame = Math.Clamp((long)Math.Floor(left / pixelsPerFrame) - 2, 0, totalFrames);
                long endFrame = Math.Clamp((long)Math.Ceiling(right / pixelsPerFrame) + 2, firstFrame, totalFrames);
                var buckets = new PixelBucket[channels][];
                for (int ch = 0; ch < channels; ch++)
                {
                    buckets[ch] = new PixelBucket[columns];
                    for (int i = 0; i < columns; i++) buckets[ch][i] = new PixelBucket();
                }
                reader.Position = firstFrame * reader.WaveFormat.BlockAlign;
                var buffer = new float[4096 * channels];
                long frame = firstFrame;
                while (frame < endFrame)
                {
                    if (version != System.Threading.Volatile.Read(ref requestVersion)) return null;
                    int framesToRead = (int)Math.Min(4096, endFrame - frame);
                    int read = reader.Read(buffer, 0, framesToRead * channels);
                    if (read <= 0) break;
                    int framesRead = read / channels;
                    for (int i = 0; i < framesRead; i++)
                    {
                        long index = frame + i;
                        int column = (int)Math.Floor(index * pixelsPerFrame - left);
                        if (column < 0 || column >= columns) continue;
                        for (int ch = 0; ch < channels; ch++)
                            buckets[ch][column].Add(buffer[i * channels + ch], index - firstFrame);
                    }
                    frame += framesRead;
                }
                var result = new Point[channels][];
                double laneHeight = height / channels;
                for (int ch = 0; ch < channels; ch++)
                    result[ch] = BuildChannelPoints(buckets[ch], pixelsPerFrame, firstFrame,
                        laneHeight * (ch + 0.5), laneHeight * 0.44, EffectiveThreshold);
                return result;
            });
            if (version != requestVersion || points == null) return;
            sampledPoints = points;
            cachedStart = left;
            cachedEnd = right;
            completed = true;
            geometry = null;
            InvalidateVisual();
        }
        catch (Exception)
        {
            // Preserve the overview while decoding is unavailable.
        }
        finally
        {
            SetSampling(false);
            if (version != requestVersion || completed) QueueSampling();
        }
    }

    // Immediate preview from the precomputed envelope. The same connected-line
    // renderer is used; the async PCM sampling replaces only the point density.
    private Point[][] BuildPreview()
    {
        if (envelope == null || duration <= 0) return null;
        int channels = envelope.Channels;
        var result = new Point[channels][];
        double fullWidth = ActualWidth * zoom;
        double height = ActualHeight / channels;
        int bins = envelope.Min[0].Length;
        double binWidth = fullWidth * envelope.Duration / (duration * bins);
        int start = Math.Clamp((int)Math.Floor(offset / Math.Max(binWidth, 1e-10)) - 1, 0, bins);
        int end = Math.Clamp((int)Math.Ceiling((offset + ActualWidth) / Math.Max(binWidth, 1e-10)) + 1, start, bins);
        for (int ch = 0; ch < channels; ch++)
        {
            var points = new System.Collections.Generic.List<Point>();
            // Reduce envelope bins by display columns, preserving both extrema.
            // Skipping every Nth bin loses peaks and makes the 100% view sparse.
            int columns = Math.Max(1, (int)Math.Ceiling(ActualWidth));
            int previousColumn = -1;
            int minBin = -1, maxBin = -1;
            float minValue = 0, maxValue = 0;
            void FlushColumn()
            {
                if (minBin < 0) return;
                double center = height * (ch + 0.5);
                double amplitude = height * 0.44;
                var low = new Point(minBin * binWidth, center - minValue * amplitude);
                var high = new Point(maxBin * binWidth, center - maxValue * amplitude);
                if (minBin <= maxBin) { points.Add(low); points.Add(high); }
                else { points.Add(high); points.Add(low); }
            }
            for (int i = start; i < end; i++)
            {
                int column = Math.Clamp((int)Math.Floor(i * binWidth - offset), -1, columns);
                if (column != previousColumn)
                {
                    FlushColumn();
                    previousColumn = column;
                    minBin = maxBin = i;
                    minValue = envelope.Min[ch][i];
                    maxValue = envelope.Max[ch][i];
                }
                else
                {
                    if (envelope.Min[ch][i] < minValue)
                    {
                        minValue = envelope.Min[ch][i];
                        minBin = i;
                    }
                    if (envelope.Max[ch][i] > maxValue)
                    {
                        maxValue = envelope.Max[ch][i];
                        maxBin = i;
                    }
                }
            }
            FlushColumn();
            result[ch] = points.ToArray();
        }
        return result;
    }

    private Geometry BuildGeometry()
    {
        var shape = new StreamGeometry();
        var points = sampledPoints ?? BuildPreview();
        using (var context = shape.Open())
        {
            if (points != null)
                foreach (var channel in points)
                {
                    if (channel.Length == 0) continue;
                    context.BeginFigure(channel[0], false, false);
                    for (int i = 1; i < channel.Length; i++) context.LineTo(channel[i], true, false);
                }
        }
        shape.Freeze();
        return shape;
    }

    protected override void OnRender(DrawingContext dc)
    {
        base.OnRender(dc);
        dc.DrawRectangle(Background, null, new Rect(RenderSize));
        if (envelope != null && duration > 0 && ActualHeight > 0 && ActualWidth > 0)
        {
            // Rendering never opens or decodes a file. Sampling is queued by view changes.
            geometry ??= BuildGeometry();
            dc.PushTransform(new TranslateTransform(-offset, 0));
            var pen = new Pen(Foreground, 1);
            // Unplayed waveform is dim; played region uses the normal Source/Target color.
            dc.PushOpacity(0.4);
            dc.DrawGeometry(null, pen, geometry);
            dc.Pop();
            double playedWidth = Math.Clamp(PixelAt(position), 0, ActualWidth * zoom);
            if (playedWidth > 0)
            {
                dc.PushClip(new RectangleGeometry(new Rect(0, 0, playedWidth, ActualHeight)));
                dc.DrawGeometry(null, pen, geometry);
                dc.Pop();
            }
            DrawDifferences(dc);
            dc.Pop();
        }
        double cursor = PixelAt(position) - offset;
        if (cursor < 0 || cursor > ActualWidth) return;
        cursor = Math.Clamp(cursor, 1, Math.Max(1, ActualWidth - 1));
        dc.DrawLine(new Pen(Brushes.Red, 2), new Point(cursor, 0), new Point(cursor, ActualHeight));
    }
}
