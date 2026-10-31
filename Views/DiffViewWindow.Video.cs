using DesktopIniManager.Properties;
using DesktopIniManager.Services;
using DesktopIniManager.ViewModels;
using System;
using System.Diagnostics;
using System.IO;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;

namespace DesktopIniManager.Views;

internal sealed partial class DiffViewWindow
{
    private readonly Stopwatch videoClock = new();
    private readonly double[] videoDelays = new double[2];
    private readonly TextBlock[] videoDurationLabels = new TextBlock[2];
    private readonly Stopwatch videoSeekThrottle = Stopwatch.StartNew();
    private readonly bool[] videoSidePlaying = new bool[2];

    private MediaElement leftMedia, rightMedia;
    private ScaleTransform leftVideoScale, rightVideoScale;
    private bool videoDragging;
    private bool videoHasStarted;
    private bool videoIsPlaying;
    private double videoOrigin, videoSpeed = 1;
    private Button videoPlayPause;
    private DispatcherTimer videoPositionTimer;
    private bool videoResumeAfterDrag;
    private Slider videoSeek;
    private bool videoSeeking;
    private TextBlock videoTimeLabel;

    private double VideoPosition => Math.Clamp(videoOrigin + videoClock.Elapsed.TotalSeconds * videoSpeed, 0,
        videoSeek?.Maximum ?? 0);

    private static string FormatVideoFileSize(long bytes)
    {
        const double unit = 1024.0;
        if (bytes >= unit * unit * unit) return $"{bytes / (unit * unit * unit):0.##} GB";
        if (bytes >= unit * unit) return $"{bytes / (unit * unit):0.##} MB";
        if (bytes >= unit) return $"{bytes / unit:0.##} KB";
        return $"{bytes} B";
    }

    private void ResetMediaController()
    {
        ResetAudio();
        gifFrameHost.HorizontalAlignment = HorizontalAlignment.Center;
        videoPositionTimer?.Stop();
        videoPositionTimer = null;
        videoIsPlaying = false;
        videoClock.Reset();
        videoOrigin = 0;
        videoSpeed = 1;
        videoDragging = false;
        videoResumeAfterDrag = false;
        videoHasStarted = false;
        Array.Clear(videoDelays, 0, 2);
        Array.Clear(videoSidePlaying, 0, 2);
        videoSeeking = false;
        foreach (var media in new[] { leftMedia, rightMedia })
        {
            if (media == null) continue;
            media.MediaOpened -= Video_MediaOpened;
            try
            {
                media.Close();
            }
            catch
            {
            }
        }

        leftMedia = rightMedia = null;
        leftVideoScale = rightVideoScale = null;
        videoSeek = null;
        videoPlayPause = null;
        videoTimeLabel = null;
        Array.Clear(videoDurationLabels, 0, videoDurationLabels.Length);
        if (gifFrameHost.Tag is KeyEventHandler frameKeyHandler)
            RemoveHandler(Keyboard.PreviewKeyDownEvent, frameKeyHandler);
        gifFrameHost.Tag = null;
        gifFrameHost.Children.Clear();
        gifFrameHost.Visibility = Visibility.Collapsed;
    }

    private Task RenderVideos()
    {
        body.Children.Clear();
        gifFrameHost.Children.Clear();
        gifFrameHost.Visibility = Visibility.Collapsed;
        body.ColumnDefinitions[1].Width = new GridLength(12);

        var leftPath = ViewModel.GetPath(true);
        var rightPath = ViewModel.GetPath(false);
        var leftMissing = string.IsNullOrEmpty(leftPath) || !File.Exists(leftPath);
        var rightMissing = string.IsNullOrEmpty(rightPath) || !File.Exists(rightPath);

        leftMedia = CreateVideoElement(leftPath, leftMissing, true);
        rightMedia = CreateVideoElement(rightPath, rightMissing, false);

        leftVideoScale = new ScaleTransform(1, 1);
        rightVideoScale = new ScaleTransform(1, 1);

        var leftContainer = new Grid { LayoutTransform = leftVideoScale };
        leftContainer.Children.Add(leftMedia);
        var rightContainer = new Grid { LayoutTransform = rightVideoScale };
        rightContainer.Children.Add(rightMedia);

        leftScroll = ThemedViewer(leftContainer, true, leftMissing);
        rightScroll = ThemedViewer(rightContainer, false, rightMissing);
        EnableImagePan(leftScroll);
        EnableImagePan(rightScroll);

        body.Children.Add(ImagePaneHost(leftScroll, true, leftMissing));
        var rightHost = ImagePaneHost(rightScroll, false, rightMissing);
        Grid.SetColumn(rightHost, 2);
        body.Children.Add(rightHost);

        // Shared zoom control (reuse image toolbar area, styled like images)
        var zoom = imageZoom = new Slider
        {
            Minimum = 0.25,
            Maximum = 4,
            Value = 1,
            Width = 120,
            ToolTip = "Shared zoom for Source and Target",
            VerticalAlignment = VerticalAlignment.Center,
            Style = TryFindResource("ZoomSlider") as Style
        };
        var zoomLabel = new TextBlock
        {
            Margin = new Thickness(8, 0, 0, 0),
            VerticalAlignment = VerticalAlignment.Center,
            FontWeight = FontWeights.SemiBold,
            Text = "100%"
        };
        zoomLabel.SetResourceReference(TextBlock.ForegroundProperty, "Ink");
        zoom.ValueChanged += (s, e) =>
        {
            leftVideoScale.ScaleX = leftVideoScale.ScaleY = e.NewValue;
            rightVideoScale.ScaleX = rightVideoScale.ScaleY = e.NewValue;
            zoomLabel.Text = string.Format("{0:0}%", e.NewValue * 100);
        };

        var wrapper = imageToolbar as Panel;
        if (wrapper != null)
        {
            wrapper.Children.Clear();
            var chrome = new Border
            {
                CornerRadius = new CornerRadius(18),
                Padding = new Thickness(10, 4, 6, 4),
                Margin = new Thickness(0, 0, 8, 8),
                Child = new StackPanel
                {
                    Orientation = Orientation.Horizontal,
                    VerticalAlignment = VerticalAlignment.Center
                }
            };
            chrome.SetResourceReference(Border.BackgroundProperty, "CardBackground");
            chrome.SetResourceReference(Border.BorderBrushProperty, "Line");
            chrome.BorderThickness = new Thickness(1);
            var chromeRow = (StackPanel)chrome.Child;
            var zoomTitle = new TextBlock
            {
                Text = StringOverlay.Get("Diff_Zoom"),
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(2, 0, 8, 0),
                FontWeight = FontWeights.SemiBold
            };
            zoomTitle.SetResourceReference(TextBlock.ForegroundProperty, "Muted");
            chromeRow.Children.Add(zoomTitle);
            chromeRow.Children.Add(ImageZoomStepButton("\uE738", "Zoom out",
                () => { zoom.Value = Math.Max(zoom.Minimum, zoom.Value / 1.25); }));
            chromeRow.Children.Add(zoom);
            chromeRow.Children.Add(ImageZoomStepButton("\uE710", "Zoom in",
                () => { zoom.Value = Math.Min(zoom.Maximum, zoom.Value * 1.25); }));
            chromeRow.Children.Add(zoomLabel);
            wrapper.Children.Add(chrome);
            wrapper.Children.Add(CreatePlaybackSpeeds("VideoPlaybackSpeed", speed =>
            {
                videoOrigin = VideoPosition;
                videoClock.Reset();
                if (videoIsPlaying) videoClock.Start();
                videoSpeed = speed;
                leftMedia.SpeedRatio = rightMedia.SpeedRatio = speed;
            }));
            AddIconActionButton(wrapper, DifferencerStatusIcons.GetCustomIcon(72), StringOverlay.Get("Diff_Fit"),
                () => FitVideosToViewport());
            AddIconActionButton(wrapper, DifferencerStatusIcons.GetCustomIcon(73), "100%", () => { zoom.Value = 1; });
            wrapper.Visibility = Visibility.Visible;
            imageControlRow.Visibility = Visibility.Visible;
        }

        // Central video controller (play/pause + seek) in gifFrameHost
        BuildVideoController();
        UpdateEncodingHeaders();

        // Fit after layout
        Dispatcher.BeginInvoke(new Action(() =>
        {
            body.UpdateLayout();
            FitVideosToViewport();
            leftMedia?.Pause();
            rightMedia?.Pause();
        }), DispatcherPriority.Loaded);

        return Task.CompletedTask;
    }

    private MediaElement CreateVideoElement(string path, bool missing, bool source)
    {
        var media = new MediaElement
        {
            LoadedBehavior = MediaState.Manual,
            UnloadedBehavior = MediaState.Manual,
            Stretch = Stretch.Uniform,
            IsMuted = false,
            Volume = SettingsService.LoadDiffAudioVolume(source),
            ScrubbingEnabled = true
        };
        if (!missing && !string.IsNullOrEmpty(path))
            media.Source = new Uri(path);
        media.MediaOpened += Video_MediaOpened;
        return media;
    }

    private void Video_MediaOpened(object sender, RoutedEventArgs e)
    {
        var side = ReferenceEquals(sender, leftMedia) ? 0 : 1;
        if (videoDurationLabels[side] != null && sender is MediaElement media)
            videoDurationLabels[side].Text = media.NaturalDuration.HasTimeSpan
                ? media.NaturalDuration.TimeSpan.ToString(@"hh\:mm\:ss")
                : "--:--:--";
        UpdateVideoSeekRange();
        ApplyVideoPosition(VideoPosition, true);
        FitVideosToViewport();
    }

    private void FitVideosToViewport()
    {
        if (imageZoom == null || leftScroll == null || rightScroll == null) return;
        double vw = leftMedia?.NaturalVideoWidth > 0 ? leftMedia.NaturalVideoWidth : 640;
        double vh = leftMedia?.NaturalVideoHeight > 0 ? leftMedia.NaturalVideoHeight : 360;
        if (rightMedia?.NaturalVideoWidth > 0)
        {
            vw = Math.Max(vw, rightMedia.NaturalVideoWidth);
            vh = Math.Max(vh, rightMedia.NaturalVideoHeight);
        }

        var viewW = Math.Min(leftScroll.ViewportWidth, rightScroll.ViewportWidth);
        var viewH = Math.Min(leftScroll.ViewportHeight, rightScroll.ViewportHeight);
        if (viewW <= 1 || viewH <= 1 || vw <= 0 || vh <= 0) return;
        var scale = Math.Min(viewW / vw, viewH / vh);
        scale = Math.Max(imageZoom.Minimum, Math.Min(imageZoom.Maximum, scale));
        imageZoom.Value = scale;
    }

    private void UpdateVideoSeekRange()
    {
        if (videoSeek == null) return;
        var leftDur = leftMedia?.NaturalDuration.HasTimeSpan == true
            ? leftMedia.NaturalDuration.TimeSpan
            : TimeSpan.Zero;
        var rightDur = rightMedia?.NaturalDuration.HasTimeSpan == true
            ? rightMedia.NaturalDuration.TimeSpan
            : TimeSpan.Zero;
        var maxDur = TimeSpan.FromSeconds(Math.Max(0, Math.Max(
            leftDur.TotalSeconds > 0 ? leftDur.TotalSeconds + videoDelays[0] : 0,
            rightDur.TotalSeconds > 0 ? rightDur.TotalSeconds + videoDelays[1] : 0)));
        videoSeeking = true;
        videoSeek.Maximum = maxDur.TotalSeconds;
        videoSeeking = false;
        videoSeek.IsEnabled = videoPlayPause.IsEnabled = maxDur > TimeSpan.Zero;
        UpdateVideoTimeLabel(TimeSpan.FromSeconds(VideoPosition));
    }

    private Grid CreateVideoVolumePanel(MediaElement media, bool source)
    {
        var panel = new Grid { Margin = new Thickness(source ? 0 : 10, 0, source ? 10 : 0, 0) };
        panel.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        panel.ColumnDefinitions.Add(new ColumnDefinition());
        panel.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var volume = new Slider
        {
            Minimum = 0,
            Maximum = 1,
            Value = media.Volume,
            VerticalAlignment = VerticalAlignment.Center,
            IsMoveToPointEnabled = true,
            ToolTip = StringOverlay.Get(source ? "Diff_SourceVolume" : "Diff_TargetVolume"),
            Style = TryFindResource("AudioVolumeSlider") as Style
        };
        volume.SetResourceReference(ForegroundProperty, source ? "SourceColor" : "TargetColor");
        AutomationProperties.SetName(volume, (string)volume.ToolTip);
        volume.ValueChanged += (_, e) =>
        {
            media.Volume = e.NewValue;
            SettingsService.SaveDiffAudioVolume(source, e.NewValue);
        };
        Grid.SetColumn(volume, 1);
        panel.Children.Add(volume);

        Button Icon(int number, string key, double value)
        {
            var button = new Button
            {
                Width = 36,
                Height = 36,
                Padding = new Thickness(2),
                ToolTip = StringOverlay.Get(key),
                Content = new Image { Source = DifferencerStatusIcons.GetCustomIcon(number), Width = 28, Height = 28 },
                Style = TryFindResource("IconButton") as Style
            };
            AutomationProperties.SetName(button, (string)button.ToolTip);
            button.Click += (_, _) => volume.Value = value;
            return button;
        }

        panel.Children.Add(Icon(142, "Diff_MuteVolume", 0));
        var maximum = Icon(141, "Diff_MaximumVolume", 1);
        Grid.SetColumn(maximum, 2);
        panel.Children.Add(maximum);

        void UpdateVolumeEnabled(bool enabled)
        {
            panel.IsEnabled = enabled;
            panel.Opacity = enabled ? 1.0 : 0.35;
        }

        UpdateVolumeEnabled(media.HasAudio);
        media.MediaOpened += (_, _) => UpdateVolumeEnabled(media.HasAudio);
        media.MediaFailed += (_, _) => UpdateVolumeEnabled(false);
        return panel;
    }

    private void BuildVideoController()
    {
        var playGlyph = new TextBlock
        {
            Text = "▶",
            FontSize = 18,
            FontWeight = FontWeights.Bold,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            IsHitTestVisible = false
        };
        playGlyph.SetResourceReference(TextBlock.ForegroundProperty, "Ink");

        videoPlayPause = new Button
        {
            Content = playGlyph,
            Width = 40,
            Height = 40,
            Margin = new Thickness(0, 0, 8, 0),
            ToolTip = "Play / Pause",
            Padding = new Thickness(0)
        };
        // Ensure content is visible even with app button styles
        var border = new FrameworkElementFactory(typeof(Border));
        border.SetValue(Border.CornerRadiusProperty, new CornerRadius(8));
        border.SetValue(Border.BorderThicknessProperty, new Thickness(1));
        border.SetResourceReference(Border.BackgroundProperty, "CardBackground");
        border.SetResourceReference(Border.BorderBrushProperty, "Line");
        var presenter = new FrameworkElementFactory(typeof(ContentPresenter));
        presenter.SetValue(ContentPresenter.ContentProperty, new TemplateBindingExtension(ContentProperty));
        presenter.SetValue(HorizontalAlignmentProperty, HorizontalAlignment.Center);
        presenter.SetValue(VerticalAlignmentProperty, VerticalAlignment.Center);
        border.AppendChild(presenter);
        videoPlayPause.Template = new ControlTemplate(typeof(Button)) { VisualTree = border };

        videoPlayPause.Click += (s, e) => ToggleVideoPlayback();

        videoSeek = new Slider
        {
            Minimum = 0,
            Maximum = 1,
            IsEnabled = false,
            VerticalAlignment = VerticalAlignment.Center,
            Style = TryFindResource("AudioSeekSlider") as Style
        };
        videoSeek.AddHandler(Thumb.DragStartedEvent, new DragStartedEventHandler((_, _) => BeginVideoSeekDrag()));
        videoSeek.AddHandler(Thumb.DragCompletedEvent,
            new DragCompletedEventHandler((_, _) => { EndVideoSeekDrag(); }));
        videoSeek.ValueChanged += VideoSeek_ValueChanged;

        videoTimeLabel = new TextBlock
        {
            Text = "0:00 / 0:00",
            FontSize = 12,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(10, 0, 0, 0),
            MinWidth = 80
        };
        videoTimeLabel.SetResourceReference(TextBlock.ForegroundProperty, "Ink");

        var controller = new Grid
        {
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 4, 0, 4)
        };
        controller.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        controller.ColumnDefinitions.Add(new ColumnDefinition());
        controller.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        Grid.SetColumn(videoSeek, 1);
        Grid.SetColumn(videoTimeLabel, 2);
        controller.Children.Add(videoPlayPause);
        controller.Children.Add(videoSeek);
        controller.Children.Add(videoTimeLabel);

        var frameBorder = new Border
        {
            CornerRadius = new CornerRadius(18),
            BorderThickness = new Thickness(1),
            Padding = new Thickness(12, 6, 12, 6),
            Margin = new Thickness(10, 0, 10, 0),
            Child = controller
        };
        frameBorder.SetResourceReference(Border.BackgroundProperty, "CardBackground");
        frameBorder.SetResourceReference(Border.BorderBrushProperty, "Line");

        var controls = new Grid { Margin = new Thickness(0, 8, 0, 0) };
        controls.ColumnDefinitions.Add(new ColumnDefinition());
        controls.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1.6, GridUnitType.Star) });
        controls.ColumnDefinitions.Add(new ColumnDefinition());
        controls.Children.Add(CreateVideoVolumePanel(leftMedia, true));
        Grid.SetColumn(frameBorder, 1);
        controls.Children.Add(frameBorder);
        var targetVolume = CreateVideoVolumePanel(rightMedia, false);
        Grid.SetColumn(targetVolume, 2);
        controls.Children.Add(targetVolume);
        gifFrameHost.HorizontalAlignment = HorizontalAlignment.Stretch;
        gifFrameHost.Children.Add(controls);
        gifFrameHost.Visibility = Visibility.Visible;

        // 左右キーでシーク、Spaceで再生／一時停止（初回表示時から有効）
        KeyEventHandler videoKeyHandler = (_, e) =>
        {
            if (gifFrameHost.Visibility != Visibility.Visible) return;
            if (e.OriginalSource is DependencyObject input && FindAncestor<TextBox>(input) != null) return;
            if (e.OriginalSource is DependencyObject focused &&
                FindAncestor<Slider>(focused) is Slider slider && slider != videoSeek) return;

            if (e.Key == Key.Space)
            {
                ToggleVideoPlayback();
                e.Handled = true;
                return;
            }

            if (videoSeek == null || !videoSeek.IsEnabled) return;
            var step = 1.0; // 1秒
            if (e.Key == Key.Left) step = -1.0;
            else if (e.Key == Key.Right) step = 1.0;
            else return;

            var next = Math.Max(videoSeek.Minimum, Math.Min(videoSeek.Maximum, videoSeek.Value + step));
            videoSeek.Value = next;
            e.Handled = true;
        };
        AddHandler(Keyboard.PreviewKeyDownEvent, videoKeyHandler, true);
        gifFrameHost.Tag = videoKeyHandler;

        videoPositionTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(50) };
        videoPositionTimer.Tick += (s, e) =>
        {
            if (!videoIsPlaying || videoSeeking || videoDragging || videoSeek == null) return;
            var pos = TimeSpan.FromSeconds(VideoPosition);
            ApplyVideoPosition(pos.TotalSeconds, false);
            if (pos.TotalSeconds >= 0 && pos.TotalSeconds <= videoSeek.Maximum)
            {
                videoSeeking = true;
                videoSeek.Value = pos.TotalSeconds;
                videoSeeking = false;
                UpdateVideoTimeLabel(pos);
            }

            if (pos.TotalSeconds >= videoSeek.Maximum) ToggleVideoPlayback();
        };
    }

    private void ToggleVideoPlayback()
    {
        if (leftMedia == null && rightMedia == null) return;
        if (!videoIsPlaying && videoSeek?.IsEnabled != true) return;
        if (videoIsPlaying)
        {
            videoClock.Stop();
            leftMedia?.Pause();
            rightMedia?.Pause();
            Array.Clear(videoSidePlaying, 0, videoSidePlaying.Length);
            videoIsPlaying = false;
            videoPositionTimer?.Stop();
            if (videoPlayPause?.Content is TextBlock tb) tb.Text = "▶";
        }
        else
        {
            if (VideoPosition >= videoSeek.Maximum) SeekVideo(0);
            videoIsPlaying = true;
            videoClock.Start();
            var startPosition = VideoPosition;
            ApplyVideoPosition(startPosition, true);
            if (!videoHasStarted)
            {
                videoHasStarted = true;
                // Initial Play can reset MediaElement.Position to zero.
                // Reapply the requested seek after the media engine starts.
                Dispatcher.BeginInvoke(new Action(() =>
                {
                    if (videoIsPlaying && videoSeek != null)
                        ApplyVideoPosition(VideoPosition, true);
                }), DispatcherPriority.Loaded);
            }

            videoPositionTimer?.Start();
            if (videoPlayPause?.Content is TextBlock tb) tb.Text = "⏸";
        }
    }

    // ドラッグ中は左右の再生と共通時計を停止し、シーク完了後に元の再生状態へ戻す。
    private void BeginVideoSeekDrag()
    {
        if (videoSeek == null || videoDragging) return;
        videoResumeAfterDrag = videoIsPlaying;
        videoOrigin = VideoPosition;
        videoClock.Reset();
        videoIsPlaying = false;
        videoPositionTimer?.Stop();
        leftMedia?.Pause();
        rightMedia?.Pause();
        Array.Clear(videoSidePlaying, 0, videoSidePlaying.Length);
        videoDragging = true;
        videoSeekThrottle.Restart();
    }

    private void EndVideoSeekDrag()
    {
        if (!videoDragging || videoSeek == null) return;
        videoDragging = false;
        SeekVideo(videoSeek.Value);
        var resume = videoResumeAfterDrag;
        videoResumeAfterDrag = false;
        if (resume) ToggleVideoPlayback();
    }

    private void VideoSeek_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (videoSeeking || videoSeek == null) return;
        if (videoDragging)
        {
            if (videoSeekThrottle.ElapsedMilliseconds < 90)
            {
                UpdateVideoTimeLabel(TimeSpan.FromSeconds(e.NewValue));
                return;
            }

            videoSeekThrottle.Restart();
            SeekVideo(e.NewValue);
            return;
        }

        // バークリックやキーボード操作も、再生中は一旦停止して位置を確定する。
        var resume = videoIsPlaying;
        if (resume)
        {
            videoOrigin = VideoPosition;
            videoClock.Reset();
            videoIsPlaying = false;
            videoPositionTimer?.Stop();
            leftMedia?.Pause();
            rightMedia?.Pause();
            Array.Clear(videoSidePlaying, 0, videoSidePlaying.Length);
        }

        SeekVideo(e.NewValue);
        if (resume) ToggleVideoPlayback();
    }

    private void SeekVideo(double seconds)
    {
        videoSeeking = true;
        try
        {
            videoOrigin = Math.Clamp(seconds, 0, videoSeek.Maximum);
            videoClock.Reset();
            if (videoIsPlaying) videoClock.Start();
            var pos = TimeSpan.FromSeconds(videoOrigin);
            ApplyVideoPosition(videoOrigin, true);
            videoSeek.Value = videoOrigin;
            UpdateVideoTimeLabel(pos);
        }
        finally
        {
            videoSeeking = false;
        }
    }

    private void UpdateVideoTimeLabel(TimeSpan pos)
    {
        if (videoTimeLabel == null || videoSeek == null) return;
        var total = TimeSpan.FromSeconds(videoSeek.Maximum);
        videoTimeLabel.Text = $"{pos:hh\\:mm\\:ss} / {total:hh\\:mm\\:ss}";
    }

    private void ApplyVideoPosition(double position, bool seek)
    {
        for (var side = 0; side < 2; side++)
        {
            var item = side == 0 ? leftMedia : rightMedia;
            if (item?.NaturalDuration.HasTimeSpan != true) continue;
            var local = position - videoDelays[side];
            var playing = videoIsPlaying && local >= 0 && local < item.NaturalDuration.TimeSpan.TotalSeconds;
            if (seek)
                item.Position = TimeSpan.FromSeconds(Math.Clamp(local, 0,
                    Math.Max(0, item.NaturalDuration.TimeSpan.TotalSeconds - 0.001)));
            if (playing == videoSidePlaying[side]) continue;
            if (playing) item.Play();
            else item.Pause();
            videoSidePlaying[side] = playing;
        }
    }

    private void ApplyVideoSidePosition(int side, double position)
    {
        var item = side == 0 ? leftMedia : rightMedia;
        if (item?.NaturalDuration.HasTimeSpan != true) return;
        var local = position - videoDelays[side];
        item.Position =
            TimeSpan.FromSeconds(Math.Clamp(local, 0, Math.Max(0, item.NaturalDuration.TimeSpan.TotalSeconds - 0.001)));
        var playing = videoIsPlaying && local >= 0 && local < item.NaturalDuration.TimeSpan.TotalSeconds;
        if (playing == videoSidePlaying[side]) return;
        if (playing) item.Play();
        else item.Pause();
        videoSidePlaying[side] = playing;
    }

    private FrameworkElement CreateVideoDelay(bool source)
    {
        var side = source ? 0 : 1;
        var picker = CreateDelayPicker(source, () => videoDelays[side], value =>
        {
            var position = VideoPosition;
            videoDelays[side] = value;
            UpdateVideoSeekRange();
            if (videoSeek != null)
            {
                var clamped = Math.Clamp(position, videoSeek.Minimum, videoSeek.Maximum);
                if (clamped != position) SeekVideo(clamped);
                else ApplyVideoSidePosition(side, clamped);
            }
        });
        picker.IsEnabled = File.Exists(ViewModel.GetPath(source));
        return picker;
    }
}