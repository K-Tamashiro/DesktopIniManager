using DesktopIniManager.Controls;
using DesktopIniManager.Properties;
using DesktopIniManager.Services;
using DesktopIniManager.ViewModels;
using System;
using System.Globalization;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;

namespace DesktopIniManager.Views;

internal sealed partial class DiffViewWindow
{
    private Border audioDifferenceBusy;
    private bool audioDifferenceEnabled;
    private Task<AudioDifferences> audioDifferenceTask;
    private CancellationTokenSource audioLoading;
    private AudioOverview audioOverview;
    private AudioDiffPlayer audioPlayer;
    private EventHandler audioRendering;
    private AudioTimeline audioTimeline;
    private DispatcherTimer audioTimer;

    private void ResetAudio()
    {
        audioLoading?.Cancel();
        audioLoading?.Dispose();
        audioLoading = null;
        audioDifferenceTask = null;
        audioDifferenceBusy = null;
        audioDifferenceEnabled = false;
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
            return (Source: ViewModel.GetPath(true), Target: ViewModel.GetPath(false), audioLoading.Token);
        }).Task.ConfigureAwait(false);
        var envelopes = await Task.Run(() =>
        {
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

    private async Task ApplyAudioDifferencesAsync()
    {
        if (audioTimeline == null || audioLoading == null || !audioDifferenceEnabled) return;
        var timeline = audioTimeline;
        var token = audioLoading.Token;
        string source = ViewModel.GetPath(true), target = ViewModel.GetPath(false);
        try
        {
            // Optional analysis must not hold LoadContent's rendering/drop lock.
            if (audioDifferenceTask == null)
            {
                if (audioDifferenceBusy != null) audioDifferenceBusy.Visibility = Visibility.Visible;
                audioDifferenceTask = Task.Run(() => AudioDifferences.Load(source, target, token), token);
            }

            var differences = await audioDifferenceTask;
            if (token.IsCancellationRequested || windowClosed || !ReferenceEquals(timeline, audioTimeline)) return;
            for (var side = 0; side < timeline.Waves.Length; side++)
                timeline.Waves[side].SetDifferences(differences, audioDifferenceEnabled, side == 0);
            audioOverview?.SetDifferences(differences, audioDifferenceEnabled);
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            if (!token.IsCancellationRequested && !windowClosed && ReferenceEquals(timeline, audioTimeline))
            {
                audioDifferenceTask = null;
                ViewModel.ReportError(ex);
            }
        }
        finally
        {
            if (ReferenceEquals(timeline, audioTimeline) && audioDifferenceBusy != null)
                audioDifferenceBusy.Visibility = Visibility.Collapsed;
        }
    }

    private void BuildAudioView(string source, string target, AudioEnvelope[] envelopes)
    {
        Dispatcher.VerifyAccess();
        CheckWindowOpen();
        var duration = Math.Max(envelopes[0]?.Duration ?? 0, envelopes[1]?.Duration ?? 0);
        audioPlayer = new AudioDiffPlayer(source, target, duration);
        var overview = audioOverview = new AudioOverview(envelopes, duration);
        overview.SetResourceReference(ForegroundProperty, "AudioWaveformInk");
        overview.SetResourceReference(BackgroundProperty, "CardBackground");
        audioOverviewHost.Children.Add(overview);
        audioOverviewHost.Visibility = Visibility.Visible;
        body.Children.Clear();
        var layout = audioTimeline = new AudioTimeline(envelopes, duration, new[] { source, target });
        layout.DelayChanged += (side, value) =>
        {
            try
            {
                audioPlayer.SetDelay(side, value);
                layout.SetDelay(side, value);
                layout.UpdatePosition(audioPlayer.Position, false);
            }
            catch (Exception ex)
            {
                ViewModel.ReportError(ex);
            }
        };
        var resumeAfterOffsetDrag = false;
        foreach (var wave in layout.Waves)
        {
            wave.CursorDragStarted += () =>
            {
                resumeAfterOffsetDrag = audioPlayer.Playing;
                audioPlayer.Pause();
            };
            wave.CursorDragCompleted += () =>
            {
                if (resumeAfterOffsetDrag) audioPlayer.Play();
            };
        }

        Grid.SetColumnSpan(layout, 3);
        body.Children.Add(layout);
        // Overlay the waveform while the first difference analysis is running.
        var busyContent = new StackPanel
        {
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center
        };
        var busyBar = new ProgressBar
        {
            Width = 160,
            Height = 5,
            IsIndeterminate = true
        };
        busyBar.SetResourceReference(BackgroundProperty, "HeaderBackground");
        busyBar.SetResourceReference(ForegroundProperty, "Accent");
        busyContent.Children.Add(busyBar);
        var busyText = new TextBlock
        {
            Text = "Analyzing audio differences...",
            Margin = new Thickness(0, 10, 0, 0),
            HorizontalAlignment = HorizontalAlignment.Center
        };
        busyText.SetResourceReference(TextBlock.ForegroundProperty, "Muted");
        busyContent.Children.Add(busyText);
        audioDifferenceBusy = new Border
        {
            Child = busyContent,
            Visibility = Visibility.Collapsed,
            IsHitTestVisible = true
        };
        audioDifferenceBusy.SetResourceReference(Border.BackgroundProperty, "OverlayScrim");
        Grid.SetColumnSpan(audioDifferenceBusy, 3);
        Panel.SetZIndex(audioDifferenceBusy, 10);
        body.Children.Add(audioDifferenceBusy);
        layout.ViewChanged += overview.SetViewport;
        overview.ViewportDragged += layout.SetViewStart;
        BuildAudioZoom(duration, envelopes);

        var controls = new Grid { Margin = new Thickness(0, 8, 0, 0) };
        controls.ColumnDefinitions.Add(new ColumnDefinition());
        controls.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1.6, GridUnitType.Star) });
        controls.ColumnDefinitions.Add(new ColumnDefinition());
        for (var side = 0; side < 2; side++)
        {
            var index = side;
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
            volume.SetResourceReference(ForegroundProperty, side == 0 ? "SourceColor" : "TargetColor");
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
                    Content = new Image
                    { Source = DifferencerStatusIcons.GetCustomIcon(number), Width = 28, Height = 28 },
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
        };
        var seek = new Slider
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
            duration = audioPlayer.Duration;
            seek.Maximum = Math.Max(0.001, duration);
            if (!dragging) seek.Value = position;
            updating = false;
            time.Text =
                $"{TimeSpan.FromSeconds(position):hh\\:mm\\:ss} / {TimeSpan.FromSeconds(duration):hh\\:mm\\:ss}";
            if (updateTimeline) layout.UpdatePosition(position, audioPlayer?.Playing == true);
            overview.SetTiming(duration, layout.Delays);
            overview.Update(position, audioPlayer?.GetPeak(0) ?? 0, audioPlayer?.GetPeak(1) ?? 0);
            play.Content = audioPlayer?.Playing == true ? "Ⅱ" : "▶";
        }

        void Seek(double position)
        {
            try
            {
                audioPlayer?.Seek(position);
                layout.UpdatePosition(position, true);
                Refresh(position);
            }
            catch (Exception ex)
            {
                ViewModel.ReportError(ex);
            }
        }

        play.Click += (_, _) =>
        {
            ToggleAudioPlayback();
            Refresh(audioPlayer?.Position ?? 0);
        };
        seek.AddHandler(Thumb.DragStartedEvent, new DragStartedEventHandler((_, _) => dragging = true));
        seek.AddHandler(Thumb.DragCompletedEvent, new DragCompletedEventHandler((_, _) =>
        {
            dragging = false;
            Seek(seek.Value);
        }));
        seek.ValueChanged += (_, e) =>
        {
            if (updating) return;
            if (dragging) Refresh(e.NewValue);
            else Seek(e.NewValue);
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
            try
            {
                audioPlayer?.SetRepeat(a, b);
                Refresh(audioPlayer?.Position ?? 0);
            }
            catch (Exception ex)
            {
                ViewModel.ReportError(ex);
            }
        };
        transport.Children.Add(play);
        Grid.SetColumn(seek, 1);
        transport.Children.Add(seek);
        Grid.SetColumn(time, 2);
        transport.Children.Add(time);
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
        Grid.SetColumn(transportFrame, 1);
        controls.Children.Add(transportFrame);
        gifFrameHost.HorizontalAlignment = HorizontalAlignment.Stretch;
        gifFrameHost.Children.Add(controls);
        gifFrameHost.Visibility = Visibility.Visible;
        // Match display refresh; the transport text and overview meters need fewer updates.
        var lastOverviewUpdate = double.NegativeInfinity;
        audioRendering = (_, e) =>
        {
            if (audioPlayer == null || dragging || !audioPlayer.Playing) return;
            var position = audioPlayer.Position;
            layout.UpdatePosition(position, true);
            var now = ((RenderingEventArgs)e).RenderingTime.TotalSeconds;
            if (now - lastOverviewUpdate < 1.0 / 30) return;
            lastOverviewUpdate = now;
            Refresh(position, false);
        };
        CompositionTarget.Rendering += audioRendering;
        audioTimer = new DispatcherTimer(DispatcherPriority.Background, Dispatcher)
        { Interval = TimeSpan.FromMilliseconds(100) };
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
            zoom.Maximum = Math.Max(100, duration / WaveformControl.MinimumViewSeconds * 100);
            if (zoom.Value > zoom.Maximum) zoom.Value = zoom.Maximum;
        }

        var label = new TextBlock
        { Text = "100%", Margin = new Thickness(8, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center };
        var row = new StackPanel { Orientation = Orientation.Horizontal };
        row.Children.Add(new TextBlock
        { Text = StringOverlay.Get("Diff_Zoom"), VerticalAlignment = VerticalAlignment.Center });
        row.Children.Add(ImageZoomStepButton("\uE738", "Zoom out",
            () => zoom.Value = Math.Max(zoom.Minimum, zoom.Value - 100)));
        row.Children.Add(zoom);
        row.Children.Add(ImageZoomStepButton("\uE710", "Zoom in",
            () => zoom.Value = Math.Min(zoom.Maximum, zoom.Value + 100)));
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
        var differenceIcon = new Image
        { Source = DifferencerStatusIcons.GetCustomIcon(143), Stretch = Stretch.Uniform };
        var differenceToggle = new ToggleButton
        {
            Content = differenceIcon,
            Style = TryFindResource("IconToggleButton") as Style,
            Margin = new Thickness(0, 0, 8, 8),
            ToolTip = "Highlight audio differences (decoded samples, before volume adjustment)",
            IsChecked = audioDifferenceEnabled
        };
        AutomationProperties.SetName(differenceToggle, "Highlight audio differences");

        void UpdateDifferenceMode()
        {
            audioDifferenceEnabled = differenceToggle.IsChecked == true;
            differenceIcon.Opacity = audioDifferenceEnabled ? 1 : 0.35;
            if (audioTimeline != null)
                foreach (var wave in audioTimeline.Waves)
                    wave.ShowDifferences = audioDifferenceEnabled;
            if (audioOverview != null) audioOverview.ShowDifferences = audioDifferenceEnabled;
        }

        differenceToggle.Checked += (_, _) =>
        {
            UpdateDifferenceMode();
            if (audioDifferenceTask == null) _ = ApplyAudioDifferencesAsync();
        };
        differenceToggle.Unchecked += (_, _) => UpdateDifferenceMode();
        differenceIcon.Opacity = audioDifferenceEnabled ? 1 : 0.35;
        imageToolbar.Children.Add(differenceToggle);
        var repeatIcon = new Image
        { Source = DifferencerStatusIcons.GetCustomIcon(144), Stretch = Stretch.Uniform, Opacity = 0.35 };
        var repeatToggle = new ToggleButton
        {
            Content = repeatIcon,
            Style = TryFindResource("IconToggleButton") as Style,
            Margin = new Thickness(0, 0, 8, 8),
            ToolTip = "リピート：波形マップをドラッグしてA–B範囲を選択",
            IsEnabled = duration > 0
        };
        AutomationProperties.SetName(repeatToggle, "A–Bリピート");

        void UpdateRepeat()
        {
            var enabled = repeatToggle.IsChecked == true;
            repeatIcon.Opacity = enabled ? 1 : 0.35;
            audioOverview?.SetRepeatEnabled(enabled);
        }

        repeatToggle.Checked += (_, _) => UpdateRepeat();
        repeatToggle.Unchecked += (_, _) => UpdateRepeat();
        imageToolbar.Children.Add(repeatToggle);
        // Exclusive speed selection: selected icon is opaque, others are dimmed.
        imageToolbar.Children.Add(CreatePlaybackSpeeds("AudioPlaybackSpeed", speed => audioPlayer?.SetSpeed(speed)));
        // Recalculate after layout, and whenever the visible waveform width changes.
        audioTimeline.ZoomChanged += value => zoom.Value = value * 100;
        audioTimeline!.Waves[0].SizeChanged += (_, _) => UpdateMaximum();
        UpdateMaximum();
        imageToolbar.Visibility = imageControlRow.Visibility = Visibility.Visible;
    }

    private StackPanel CreatePlaybackSpeeds(string group, Action<double> setSpeed)
    {
        var speedGroup = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Margin = new Thickness(0, 0, 0, 8),
            VerticalAlignment = VerticalAlignment.Top
        };
        double[] speeds = { 0.5, 0.75, 1.0, 1.5, 1.75, 2.0 };
        for (var i = 0; i < speeds.Length; i++)
        {
            var speed = speeds[i];
            var image = new Image
            {
                Source = DifferencerStatusIcons.GetCustomIcon(145 + i),
                Stretch = Stretch.Uniform,
                Opacity = i == 2 ? 1 : 0.35
            };
            var radio = new RadioButton
            {
                GroupName = group,
                Content = image,
                IsChecked = i == 2,
                ToolTip = $"再生速度 {speed:0.##}倍",
                Margin = new Thickness(0, 0, 2, 0),
                Style = (Style)FindResource("AudioSpeedRadioButton")
            };
            RenderOptions.SetBitmapScalingMode(image, BitmapScalingMode.HighQuality);
            AutomationProperties.SetName(radio, $"再生速度 {speed:0.##}倍");
            radio.Checked += (_, _) =>
            {
                foreach (var item in speedGroup.Children)
                    if (item is RadioButton button && button.Content is Image icon)
                        icon.Opacity = ReferenceEquals(button, radio) ? 1 : 0.35;
                try
                {
                    setSpeed(speed);
                }
                catch (Exception ex)
                {
                    ViewModel.ReportError(ex);
                }
            };
            speedGroup.Children.Add(radio);
        }

        return speedGroup;
    }

    internal static FrameworkElement CreateDelayPicker(bool source, Func<double> getValue, Action<double> setValue)
    {
        var row = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        var caption = new TextBlock
        {
            Text = "Delay",
            FontSize = 10,
            Margin = new Thickness(0, 0, 5, 0),
            VerticalAlignment = VerticalAlignment.Center
        };
        caption.SetResourceReference(TextBlock.ForegroundProperty, "Muted");
        row.Children.Add(caption);
        var input = new TextBox
        {
            Width = 56,
            Height = 20,
            FontSize = 11,
            Padding = new Thickness(0),
            BorderThickness = new Thickness(0),
            Background = Brushes.Transparent,
            TextAlignment = TextAlignment.Right,
            VerticalContentAlignment = VerticalAlignment.Center,
            ToolTip = "0.1秒単位。正の値は遅らせ、負の値は先に進めます。"
        };
        input.SetResourceReference(ForegroundProperty, "Ink");
        var textHost = new FrameworkElementFactory(typeof(ScrollViewer));
        textHost.Name = "PART_ContentHost";
        input.Template = new ControlTemplate(typeof(TextBox)) { VisualTree = textHost };
        var frame = new Border
        {
            Child = row,
            Height = 24,
            CornerRadius = new CornerRadius(12),
            BorderThickness = new Thickness(1),
            Padding = new Thickness(9, 0, 5, 0),
            Margin = new Thickness(8, 0, 0, 0),
            VerticalAlignment = VerticalAlignment.Center
        };
        frame.SetResourceReference(Border.BackgroundProperty, "CardBackground");
        frame.SetResourceReference(Border.BorderBrushProperty, "Line");
        AutomationProperties.SetName(input, (source ? "Source" : "Target") + " delay in seconds");

        void Refresh()
        {
            input.Text = getValue().ToString("0.0", CultureInfo.CurrentCulture);
        }

        void Commit()
        {
            if (double.TryParse(input.Text, out var value) && double.IsFinite(value))
                setValue(Math.Round(Math.Clamp(value, -86400, 86400), 1));
            Refresh();
        }

        input.LostKeyboardFocus += (_, _) => Commit();
        input.PreviewKeyDown += (_, e) =>
        {
            if (e.Key == Key.Enter)
            {
                Commit();
                e.Handled = true;
            }
            else if (e.Key == Key.Escape)
            {
                Refresh();
                e.Handled = true;
            }
            else if (e.Key == Key.Up || e.Key == Key.Down)
            {
                Commit();
                setValue(Math.Round(Math.Clamp(getValue() + (e.Key == Key.Up ? 0.1 : -0.1), -86400, 86400), 1));
                Refresh();
                e.Handled = true;
            }
        };
        frame.Tag = (Action)Refresh;
        Refresh();
        row.Children.Add(input);
        var arrows = new StackPanel
        { Margin = new Thickness(4, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center };
        foreach (var direction in new[] { 1, -1 })
        {
            var button = new RepeatButton
            {
                Content = direction > 0 ? "▴" : "▾",
                Width = 18,
                Height = 10,
                FontSize = 9,
                Padding = new Thickness(0),
                Focusable = false,
                Cursor = Cursors.Hand
            };
            button.SetResourceReference(ForegroundProperty, "Muted");
            var chrome = new FrameworkElementFactory(typeof(Border));
            chrome.Name = "Chrome";
            chrome.SetValue(Border.CornerRadiusProperty, new CornerRadius(4));
            chrome.SetValue(Border.BackgroundProperty, Brushes.Transparent);
            var glyph = new FrameworkElementFactory(typeof(ContentPresenter));
            glyph.SetValue(HorizontalAlignmentProperty, HorizontalAlignment.Center);
            glyph.SetValue(VerticalAlignmentProperty, VerticalAlignment.Center);
            chrome.AppendChild(glyph);
            var template = new ControlTemplate(typeof(RepeatButton)) { VisualTree = chrome };
            var hover = new Trigger { Property = IsMouseOverProperty, Value = true };
            hover.Setters.Add(new Setter(Border.BackgroundProperty, new DynamicResourceExtension("HeaderBackground"),
                "Chrome"));
            template.Triggers.Add(hover);
            button.Template = template;
            button.Click += (_, _) =>
            {
                Commit();
                setValue(Math.Round(Math.Clamp(getValue() + direction * 0.1, -86400, 86400), 1));
                Refresh();
            };
            arrows.Children.Add(button);
        }

        row.Children.Add(arrows);
        var unit = new TextBlock
        {
            Text = "s",
            FontSize = 10,
            Margin = new Thickness(3, 0, 0, 0),
            VerticalAlignment = VerticalAlignment.Center
        };
        unit.SetResourceReference(TextBlock.ForegroundProperty, "Muted");
        row.Children.Insert(2, unit);
        return frame;
    }
}

// Compact block waveforms with playback progress and post-volume output peaks.
internal sealed class AudioOverview : Control
{
    private const double UnplayedOpacity = 0.4;
    private readonly float[][] barPeaks = new float[2][];
    private readonly double[] delays = new double[2];

    private readonly AudioEnvelope[] envelopes;
    private readonly float[] levels = new float[2];
    private bool[][] differenceBars = new bool[2][];
    private int differenceBarsCount = -1;
    private AudioDifferences differences;
    private bool draggingViewport;
    private double duration;
    private double position;
    private double? repeatA, repeatB;
    private bool selectingRepeat;
    private bool showDifferences;
    private double viewStart, viewDuration;
    private double viewportDragX, viewportDragStart;
    private bool viewportMoved;

    internal AudioOverview(AudioEnvelope[] envelopes, double duration)
    {
        this.envelopes = envelopes;
        this.duration = duration;
        ClipToBounds = true;
        ToolTip = "Source / Target — click waveform to seek; output level at right";
    }

    internal bool RepeatEnabled { get; private set; }

    internal bool ShowDifferences
    {
        get => showDifferences;
        set
        {
            showDifferences = value;
            InvalidateVisual();
        }
    }

    internal event Action<double?, double?> RepeatChanged;

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

    private double RepeatTime(Point point)
    {
        return Math.Clamp(point.X / Math.Max(1, ActualWidth - 100), 0, 1) * duration;
    }

    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonDown(e);
        if (!RepeatEnabled && duration > 0 && viewDuration > 0 && viewDuration < duration)
        {
            var width = Math.Max(0, ActualWidth - 100);
            var x = e.GetPosition(this).X;
            var left = viewStart / duration * width;
            var right = (viewStart + viewDuration) / duration * width;
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
            var width = Math.Max(1, ActualWidth - 100);
            var pixels = e.GetPosition(this).X - viewportDragX;
            if (Math.Abs(pixels) >= SystemParameters.MinimumHorizontalDragDistance) viewportMoved = true;
            if (viewportMoved)
            {
                var delta = pixels / width * duration;
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
            var click = !viewportMoved;
            var clickTime = RepeatTime(e.GetPosition(this));
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
        var a = repeatA.Value / duration * width;
        var b = (repeatB ?? repeatA).Value / duration * width;
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

    internal void SetDifferences(AudioDifferences value, bool enabled)
    {
        differences = value;
        differenceBarsCount = -1;
        ShowDifferences = enabled;
    }

    private bool[] GetDifferenceBars(int side, int bars)
    {
        if (differenceBarsCount != bars)
        {
            differenceBarsCount = bars;
            differenceBars = new bool[2][];
        }

        if (differenceBars[side] != null) return differenceBars[side];
        var flags = new bool[bars];
        if (differences != null && envelopes[side] != null && envelopes[side].Duration > 0)
        {
            var channels = side == 0 ? differences.Channels : differences.TargetChannels;
            var seconds = envelopes[side].Duration;
            foreach (var ranges in channels)
                foreach (var range in ranges)
                {
                    var first = Math.Clamp((int)Math.Floor(range.Start / seconds * bars), 0, bars);
                    var last = Math.Clamp((int)Math.Ceiling(range.End / seconds * bars), 0, bars);
                    for (var i = first; i < last; i++) flags[i] = true;
                }
        }

        return differenceBars[side] = flags;
    }

    internal void SetTiming(double total, double[] offsets)
    {
        duration = total;
        Array.Copy(offsets, delays, 2);
    }

    private float[] GetBarPeaks(int side, int bars)
    {
        if (barPeaks[side]?.Length == bars) return barPeaks[side];
        var result = new float[bars];
        var envelope = envelopes[side];
        var bins = envelope.Data?.BinCount ?? envelope.Max[0].Length;
        if (bins == 0) return result;
        for (var bar = 0; bar < bars; bar++)
        {
            var first = Math.Min(bins - 1, (int)((long)bar * bins / bars));
            var last = Math.Min(bins, Math.Max(first + 1, (int)((long)(bar + 1) * bins / bars)));
            for (var ch = 0; ch < envelope.Channels; ch++)
                if (envelope.Data != null)
                {
                    envelope.Data.GetExtrema(ch, first, last, out var min, out var max);
                    result[bar] = Math.Max(result[bar], Math.Max(Math.Abs(min), max));
                }
                else
                {
                    for (var bin = first; bin < last; bin++)
                        result[bar] = Math.Max(result[bar],
                            Math.Max(Math.Abs(envelope.Min[ch][bin]), envelope.Max[ch][bin]));
                }
        }

        return barPeaks[side] = result;
    }

    internal void SetViewport(double start, double span)
    {
        viewStart = start;
        viewDuration = span;
        InvalidateVisual();
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
        for (var side = 0; side < 2; side++)
        {
            var color = TryFindResource(side == 0 ? "SourceColor" : "TargetColor") as Brush ?? Foreground;
            var envelope = envelopes[side];
            var baseline = rowHeight * (side + 1) - 3;
            if (envelope != null && envelope.Duration > 0 && duration > 0)
            {
                var bars = Math.Max(1, (int)(width * envelope.Duration / duration / 4));
                var peaks = GetBarPeaks(side, bars);
                var redBars = showDifferences ? GetDifferenceBars(side, bars) : null;
                for (var bar = 0; bar < bars; bar++)
                {
                    var peak = peaks[bar];
                    var height = Math.Max(1, Math.Clamp(peak, 0, 1) * (rowHeight - 6));
                    var barTime = (double)bar / bars * envelope.Duration + delays[side];
                    var barX = bar * 4 + delays[side] / duration * width;
                    if (barX < 0 || barX + 2 > width) continue;
                    var played = barTime < position;
                    if (!played) dc.PushOpacity(UnplayedOpacity);
                    dc.DrawRectangle(redBars != null && redBars[bar] ? Brushes.Red : color, null,
                        new Rect(barX, baseline - height, 2, height));
                    if (!played) dc.Pop();
                }
            }

            // dBFS scale: -60 dB to 0 dB, including saved volume/mute.
            var level = levels[side] > 0 ? Math.Clamp((20 * Math.Log10(levels[side]) + 60) / 60, 0, 1) : 0;
            for (var segment = 0; segment < 16; segment++)
            {
                dc.PushOpacity(segment < level * 16 ? 1 : 0.15);
                dc.DrawRectangle(segment >= 14 ? Brushes.OrangeRed : color, null,
                    new Rect(width + 10 + segment * 5, side * rowHeight + 7, 3, Math.Max(1, rowHeight - 14)));
                dc.Pop();
            }
        }

        if (duration > 0)
        {
            var x = Math.Clamp(position / duration, 0, 1) * width;
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
    internal readonly double[] Delays = new double[2];

    internal readonly AudioWaveform[] Waves = new AudioWaveform[2];
    private readonly FrameworkElement[] delayPickers = new FrameworkElement[2];
    private readonly ScrollBar scroll = new() { Orientation = Orientation.Horizontal, Height = 16, Minimum = 0 };
    private bool applyingView;
    private double currentPosition;

    internal AudioTimeline(AudioEnvelope[] envelopes, double duration, string[] paths)
    {
        RowDefinitions.Add(new RowDefinition());
        RowDefinitions.Add(new RowDefinition());
        // Reserve scrollbar space even at fit so zoom never changes waveform height.
        RowDefinitions.Add(new RowDefinition { Height = new GridLength(16) });
        for (var side = 0; side < 2; side++)
        {
            var index = side;
            var wave = Waves[side] = new AudioWaveform(envelopes[side], duration, paths[side]);

            wave.SetResourceReference(Control.ForegroundProperty, side == 0 ? "SourceColor" : "TargetColor");
            wave.SetResourceReference(Control.BackgroundProperty, "WindowBackground");
            var pane = new Grid();
            pane.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            pane.RowDefinitions.Add(new RowDefinition());
            var label = CreateHeader(envelopes[side], envelopes[1 - side], side == 0);
            label.FontSize = 11;
            var header = new DockPanel { Margin = new Thickness(0, 3, 8, 3) };
            var picker = DiffViewWindow.CreateDelayPicker(side == 0, () => Delays[index],
                value => DelayChanged?.Invoke(index, value));
            delayPickers[side] = picker;
            picker.IsEnabled = envelopes[side] != null;
            DockPanel.SetDock(picker, Dock.Right);
            header.Children.Add(picker);
            header.Children.Add(label);
            pane.Children.Add(header);
            SetRow(wave, 1);
            pane.Children.Add(wave);
            var frame = new Border
            {
                BorderThickness = new Thickness(3),
                CornerRadius = new CornerRadius(5),
                Margin = new Thickness(0, side == 0 ? 0 : 4, 0, 0),
                Child = pane
            };
            frame.SetResourceReference(Border.BorderBrushProperty, side == 0 ? "SourceColor" : "TargetColor");
            SetRow(frame, side);
            Children.Add(frame);
            wave.PositionClicked += seconds => SeekRequested?.Invoke(Math.Max(0, seconds + Delays[index]));
            wave.CursorDragged += seconds => DelayChanged?.Invoke(index,
                Math.Round(Math.Clamp(currentPosition - seconds, -86400, 86400), 1));
            wave.ViewChanged += () =>
            {
                if (applyingView || wave.ViewDuration <= 0) return;
                applyingView = true;
                try
                {
                    Zoom = Math.Max(1, duration / wave.ViewDuration);
                    UpdateRange();
                    scroll.Value = wave.ViewStart / Math.Max(duration, 0.001) * Viewport * Zoom;
                }
                finally
                {
                    applyingView = false;
                }

                ApplyView();
                ZoomChanged?.Invoke(Zoom);
            };
        }

        SetRow(scroll, 2);
        Children.Add(scroll);
        scroll.ValueChanged += (_, _) => ApplyView();
        Waves[0].SizeChanged += (_, e) =>
        {
            var fraction = e.PreviousSize.Width > 0 ? Offset / (e.PreviousSize.Width * Zoom) : 0;
            UpdateRange();
            scroll.Value = Math.Clamp(fraction * Viewport * Zoom, 0, LastOffset);
            ApplyView();
        };
    }

    internal double Zoom { get; private set; } = 1;

    internal double Offset => scroll.Value;
    internal double Viewport => Math.Max(1, Waves[0].ActualWidth);

    // Maximum is the final scroll Value; ViewportSize controls the thumb size.
    private double LastOffset => Math.Max(0, Viewport * (Zoom - 1));

    internal static TextBlock CreateHeader(AudioEnvelope current, AudioEnvelope other, bool source)
    {
        var label = new TextBlock
        {
            Margin = new Thickness(8, 3, 8, 3),
            FontWeight = FontWeights.SemiBold,
            TextWrapping = TextWrapping.Wrap
        };
        label.SetResourceReference(TextBlock.ForegroundProperty, "Ink");
        label.Inlines.Add(new Run(source ? "Source" : "Target"));
        if (current == null)
        {
            label.Inlines.Add(" — No file");
            return label;
        }

        void Add(string title, string value, bool different)
        {
            label.Inlines.Add("   " + title);
            var run = new Run(value);
            if (other != null && different) run.Foreground = Brushes.Red;
            label.Inlines.Add(run);
        }

        string Number(double value, string format)
        {
            return value.ToString(format, CultureInfo.InvariantCulture);
        }

        Add("", current.Channels + " ch", current.Channels != other?.Channels);
        Add("", TimeSpan.FromSeconds(current.Duration).ToString(@"hh\:mm\:ss\.fff"),
            Math.Round(current.Duration * 1000) != Math.Round((other?.Duration ?? 0) * 1000));
        Add("Sample Rate: ", Number(current.SampleRate / 1000.0, "0.0##") + " kHz",
            current.SampleRate != other?.SampleRate);
        Add("Bit Depth: ",
            current.BitDepth.HasValue ? current.BitDepth + (current.FloatingPoint ? " bit float" : " bit") : "N/A",
            current.BitDepth != other?.BitDepth || current.FloatingPoint != other?.FloatingPoint);
        Add("Bit Rate: ", current.BitRate.HasValue ? Number(current.BitRate.Value / 1000.0, "0.###") + " kbps" : "N/A",
            current.BitRate != other?.BitRate);
        label.ToolTip =
            "PCM bit rate = sample rate × bit depth × channels. MP3 shows average encoded bit rate; bit depth is not applicable.";
        return label;
    }

    internal event Action<double> SeekRequested;

    internal void SetDelay(int side, double seconds)
    {
        Delays[side] = seconds;
        (delayPickers[side]?.Tag as Action)?.Invoke();
    }

    internal event Action<int, double> DelayChanged;

    internal event Action<double> ZoomChanged;

    internal event Action<double, double> ViewChanged;

    private void UpdateRange()
    {
        scroll.Maximum = LastOffset;
        scroll.ViewportSize = Viewport;
        scroll.SmallChange = 48;
        scroll.LargeChange = Viewport * 0.8;
        scroll.IsEnabled = Zoom > 1;
        // Keep the reserved row height, but hide the scrollbar at 100%.
        scroll.Visibility = Zoom > 1 ? Visibility.Visible : Visibility.Hidden;
    }

    private void ApplyView()
    {
        if (applyingView) return;
        applyingView = true;
        try
        {
            var start = Waves[0].Duration * Offset / Math.Max(1, Viewport * Zoom);
            foreach (var wave in Waves) wave.SetViewRange(start, wave.Duration / Zoom);
        }
        finally
        {
            applyingView = false;
        }

        ViewChanged?.Invoke(Waves[0].ViewStart, Waves[0].ViewDuration);
    }

    internal void SetZoom(double value)
    {
        var newZoom = Math.Clamp(value, 1, Math.Max(1, Waves[0].Duration / WaveformControl.MinimumViewSeconds));
        if (Zoom == newZoom) return;

        // Keep the time currently at the viewport's left edge at X=0.
        var leftFraction = Offset / (Viewport * Zoom);
        Zoom = newZoom;
        UpdateRange();
        scroll.Value = Math.Clamp(leftFraction * Viewport * Zoom, 0, LastOffset);
        ApplyView();
    }

    internal void Fit()
    {
        SetZoom(1);
        scroll.Value = 0;
        foreach (var wave in Waves) wave.Fit();
    }

    internal void SetViewStart(double seconds)
    {
        var duration = Waves[0].Duration;
        if (duration <= 0 || Zoom <= 1) return;
        scroll.Value = Math.Clamp(seconds / duration * Viewport * Zoom, 0, LastOffset);
    }

    internal void ScrollBy(double pixels)
    {
        scroll.Value = Math.Clamp(Offset + pixels, 0, LastOffset);
    }

    internal void UpdatePosition(double seconds, bool follow)
    {
        currentPosition = seconds;
        for (var side = 0; side < Waves.Length; side++) Waves[side].Position = Math.Max(0, seconds - Delays[side]);
        if (!follow || Zoom <= 1 || Array.Exists(Waves, w => w.IsPanning)) return;
        var x = Waves[0].PixelAt(Math.Max(0, seconds - Delays[0]));
        // Scroll smoothly once the playhead reaches the right-hand safe area.
        if (x > Offset + Viewport * 0.85 || x < Offset)
            scroll.Value = Math.Clamp(x - Viewport * (x < Offset ? 0.15 : 0.85), 0, LastOffset);
    }
}

// DIM supplies annotations; the reusable control owns waveform rendering.
internal sealed class AudioWaveform : WaveformControl
{
    private double cursorPosition;
    private DrawingGroup differenceLayer;
    private AudioDifferences differences;
    private bool draggingCursor;

    private (WaveformData Data, double Start, double Span,
        double Amplitude, Size Size, AudioDifferences Differences, bool Source) layerKey;

    private bool source = true, showDifferences;

    internal AudioWaveform(AudioEnvelope envelope, double duration, string path)
    {
        SetData(envelope?.Data, duration);
        ToolTip = "Wheel: time zoom / Ctrl+wheel: amplitude / Drag: pan / Click: seek";
    }

    internal bool ShowDifferences
    {
        get => showDifferences;
        set
        {
            if (showDifferences == value) return;
            showDifferences = value;
            // Switching only changes the opacity of the retained red layer.
            if (differenceLayer != null) differenceLayer.Opacity = value ? 1 : 0;
            InvalidateVisual();
        }
    }

    internal double Position
    {
        set
        {
            cursorPosition = value;
            SetPosition(TimeSpan.FromSeconds(value));
        }
    }

    internal void SetDifferences(AudioDifferences value, bool enabled, bool source = true)
    {
        differences = value;
        this.source = source;
        differenceLayer = null;
        layerKey = default;
        ShowDifferences = enabled;
        InvalidateVisual();
    }

    internal void SetView(double zoom, double offset)
    {
        SetViewRange(Duration * offset / Math.Max(1, ActualWidth * zoom), Duration / zoom);
    }

    internal double TimeAt(double x)
    {
        return XToTime(x);
    }

    internal double PixelAt(double seconds)
    {
        return ViewDuration > 0 ? seconds / ViewDuration * ActualWidth : 0;
    }

    internal event Action CursorDragStarted;

    internal event Action CursorDragCompleted;

    internal event Action<double> CursorDragged;

    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        if (Math.Abs(e.GetPosition(this).X - TimeToX(cursorPosition)) <= 7 && CaptureMouse())
        {
            draggingCursor = true;
            CursorDragStarted?.Invoke();
            e.Handled = true;
            return;
        }

        base.OnMouseLeftButtonDown(e);
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        if (!draggingCursor)
        {
            base.OnMouseMove(e);
            return;
        }

        CursorDragged?.Invoke(XToTime(e.GetPosition(this).X));
        e.Handled = true;
    }

    protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e)
    {
        if (!draggingCursor)
        {
            base.OnMouseLeftButtonUp(e);
            return;
        }

        CursorDragged?.Invoke(XToTime(e.GetPosition(this).X));
        draggingCursor = false;
        ReleaseMouseCapture();
        CursorDragCompleted?.Invoke();
        e.Handled = true;
    }

    protected override void OnLostMouseCapture(MouseEventArgs e)
    {
        var wasDragging = draggingCursor;
        draggingCursor = false;
        base.OnLostMouseCapture(e);
        if (wasDragging) CursorDragCompleted?.Invoke();
    }

    protected override void DrawOverlay(DrawingContext dc)
    {
        if (!showDifferences || differences == null || Data == null ||
            ViewDuration <= 0 || ActualWidth <= 0 || ActualHeight <= 0) return;

        var key = (Data, ViewStart, ViewDuration, AmplitudeScale, RenderSize, differences, source);
        if (differenceLayer == null || layerKey != key)
        {
            layerKey = key;
            differenceLayer = BuildDifferenceLayer();
        }

        dc.DrawDrawing(differenceLayer);
    }

    private DrawingGroup BuildDifferenceLayer()
    {
        var layer = new DrawingGroup { Opacity = showDifferences ? 1 : 0 };
        var channels = source ? differences.Channels : differences.TargetChannels;
        var lane = ActualHeight / Data.Channels;
        var pixels = Math.Max(1, (int)Math.Ceiling(ActualWidth));
        using (var drawing = layer.Open())
        {
            for (var ch = 0; ch < Math.Min(channels.Length, Data.Channels); ch++)
            {
                // A pixel mask bounds work by viewport width, not recording duration
                // or the number of difference intervals.
                var mask = new bool[pixels];
                foreach (var range in channels[ch])
                {
                    if (range.End <= ViewStart) continue;
                    if (range.Start >= ViewStart + ViewDuration) break;
                    var first = Math.Clamp((int)Math.Floor(TimeToX(range.Start)), 0, pixels);
                    var last = Math.Clamp((int)Math.Ceiling(TimeToX(range.End)), 0, pixels);
                    for (var x = first; x < last; x++) mask[x] = true;
                }

                var clip = new StreamGeometry();
                var any = false;
                using (var context = clip.Open())
                {
                    for (var x = 0; x < pixels;)
                    {
                        if (!mask[x])
                        {
                            x++;
                            continue;
                        }

                        var first = x;
                        while (x < pixels && mask[x]) x++;
                        var right = Math.Min(ActualWidth, x);
                        if (right <= first) continue;
                        any = true;
                        context.BeginFigure(new Point(first, ch * lane), true, true);
                        context.LineTo(new Point(right, ch * lane), true, false);
                        context.LineTo(new Point(right, (ch + 1) * lane), true, false);
                        context.LineTo(new Point(first, (ch + 1) * lane), true, false);
                    }
                }

                if (!any) continue;
                clip.Freeze();
                drawing.PushClip(clip);
                // Reuse the normal waveform's cached geometry. Only the overlay
                // color changes; no waveform samples or geometry are recalculated.
                drawing.DrawGeometry(null, new Pen(Brushes.Red, 1), GetWaveformGeometry(ch));
                drawing.Pop();
            }
        }

        return layer;
    }
}