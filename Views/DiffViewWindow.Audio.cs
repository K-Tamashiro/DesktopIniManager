using System;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Threading;
using DesktopIniManager.Services;
using DesktopIniManager.ViewModels;

namespace DesktopIniManager.Views;

internal sealed partial class DiffViewWindow
{
    private AudioDiffPlayer audioPlayer;
    private CancellationTokenSource audioLoading;
    private DispatcherTimer audioTimer;

    private void ResetAudio()
    {
        audioLoading?.Cancel();
        audioLoading?.Dispose();
        audioLoading = null;
        audioTimer?.Stop();
        audioTimer = null;
        audioPlayer?.Dispose();
        audioPlayer = null;
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

    private void BuildAudioView(string source, string target, AudioEnvelope[] envelopes)
    {
        Dispatcher.VerifyAccess();
        CheckWindowOpen();
        double duration = Math.Max(envelopes[0]?.Duration ?? 0, envelopes[1]?.Duration ?? 0);
        audioPlayer = new AudioDiffPlayer(source, target, duration);
        body.Children.Clear();
        var layout = new Grid();
        layout.RowDefinitions.Add(new RowDefinition());
        layout.RowDefinitions.Add(new RowDefinition());
        Grid.SetColumnSpan(layout, 3);
        body.Children.Add(layout);
        var waves = new AudioWaveform[2];
        for (int side = 0; side < 2; side++)
        {
            var waveform = waves[side] = new AudioWaveform(envelopes[side], duration);
            waveform.SetResourceReference(Control.ForegroundProperty, "AudioWaveformInk");
            waveform.SetResourceReference(Control.BackgroundProperty, "WindowBackground");
            var pane = new Grid();
            pane.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            pane.RowDefinitions.Add(new RowDefinition());
            var label = new TextBlock
            {
                Margin = new Thickness(8, 3, 8, 3),
                Text = (side == 0 ? "Source" : "Target") + (envelopes[side] == null ? " — No file" :
                    $"   {envelopes[side].Channels} ch   {TimeSpan.FromSeconds(envelopes[side].Duration):hh\\:mm\\:ss\\.fff}"),
                FontWeight = FontWeights.SemiBold
            };
            label.SetResourceReference(TextBlock.ForegroundProperty, "Ink");
            pane.Children.Add(label);
            Grid.SetRow(waveform, 1);
            pane.Children.Add(waveform);
            var frame = new Border
            {
                BorderThickness = new Thickness(3),
                CornerRadius = new CornerRadius(5),
                Margin = new Thickness(0, side == 0 ? 0 : 4, 0, 0),
                Child = pane
            };
            frame.SetResourceReference(Border.BorderBrushProperty, side == 0 ? "SourceColor" : "TargetColor");
            Grid.SetRow(frame, side);
            layout.Children.Add(frame);
        }

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
                Value = 1,
                VerticalAlignment = VerticalAlignment.Center,
                ToolTip = side == 0 ? "Source volume" : "Target volume",
                IsMoveToPointEnabled = true
            };
            volume.Style = TryFindResource("AudioVolumeSlider") as Style;
            volume.SetResourceReference(Control.ForegroundProperty, side == 0 ? "SourceColor" : "TargetColor");
            volume.ValueChanged += (_, e) => audioPlayer?.SetVolume(index, e.NewValue);
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
            foreach (var wave in waves) wave.Position = position;
            play.Content = audioPlayer?.Playing == true ? "Ⅱ" : "▶";
        }
        void Seek(double position)
        {
            try { audioPlayer?.Seek(position); Refresh(position); }
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
        foreach (var wave in waves)
            wave.MouseLeftButtonDown += (_, e) => { Seek(Math.Clamp(e.GetPosition(wave).X / Math.Max(1, wave.ActualWidth), 0, 1) * duration); e.Handled = true; };
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
}

// Geometry is cached on resize; playback only redraws the cursor.
internal sealed class AudioWaveform : Control
{
    private readonly AudioEnvelope envelope;
    private readonly double duration;
    private Geometry geometry;
    private double position;
    internal AudioWaveform(AudioEnvelope envelope, double duration)
    {
        this.envelope = envelope;
        this.duration = duration;
        ClipToBounds = true;
        SizeChanged += (_, _) => { geometry = null; InvalidateVisual(); };
    }
    internal double Position { set { position = value; InvalidateVisual(); } }
    protected override void OnRender(DrawingContext dc)
    {
        base.OnRender(dc);
        dc.DrawRectangle(Background, null, new Rect(RenderSize));
        if (envelope != null && duration > 0 && ActualHeight > 0 && ActualWidth > 0)
        {
            if (geometry == null)
            {
                var shape = new StreamGeometry();
                using (var context = shape.Open())
                {
                    double width = ActualWidth * envelope.Duration / duration;
                    int pixels = Math.Max(1, (int)Math.Ceiling(width));
                    int bins = envelope.Min[0].Length;
                    for (int ch = 0; ch < envelope.Channels; ch++)
                    {
                        double height = ActualHeight / envelope.Channels, center = height * (ch + 0.5);
                        for (int x = 0; x < pixels; x++)
                        {
                            int first = Math.Min(bins - 1, x * bins / pixels);
                            int last = Math.Min(bins, Math.Max(first + 1, (x + 1) * bins / pixels));
                            float min = 0, max = 0;
                            for (int b = first; b < last; b++) { min = Math.Min(min, envelope.Min[ch][b]); max = Math.Max(max, envelope.Max[ch][b]); }
                            context.BeginFigure(new Point(x, center - Math.Clamp(max, -1, 1) * height * 0.44), false, false);
                            context.LineTo(new Point(x, center - Math.Clamp(min, -1, 1) * height * 0.44 + 0.25), true, false);
                        }
                    }
                }
                shape.Freeze(); geometry = shape;
            }
            dc.DrawGeometry(null, new Pen(Foreground, 1), geometry);
        }
        double cursor = duration > 0 ? Math.Clamp(position / duration, 0, 1) * Math.Max(0, ActualWidth - 2) + 1 : 1;
        dc.DrawLine(new Pen(Brushes.Red, 2), new Point(cursor, 0), new Point(cursor, ActualHeight));
    }
}
