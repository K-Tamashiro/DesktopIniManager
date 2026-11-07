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
    private readonly Stopwatch _videoClock = new();
    private readonly double[] _videoDelays = new double[2];
    private readonly TextBlock[] _videoDurationLabels = new TextBlock[2];
    private readonly Stopwatch _videoSeekThrottle = Stopwatch.StartNew();
    private readonly bool[] _videoSidePlaying = new bool[2];

    private MediaElement _leftMedia, _rightMedia;
    private ScaleTransform _leftVideoScale, _rightVideoScale;
    private bool _videoDragging;
    private bool _videoHasStarted;
    private bool _videoIsPlaying;
    private double _videoOrigin, _videoSpeed = 1;
    private Button _videoPlayPause;
    private DispatcherTimer _videoPositionTimer;
    private bool _videoResumeAfterDrag;
    private Slider _videoSeek;
    private bool _videoSeeking;
    private TextBlock _videoTimeLabel;

    private double VideoPosition => Math.Clamp(_videoOrigin + _videoClock.Elapsed.TotalSeconds * _videoSpeed, 0,
        _videoSeek?.Maximum ?? 0);

    /// <summary>
    ///     ファイル サイズを B、KB、MB、GB の単位で表示用の文字列にします。
    /// </summary>
    /// <param name="bytes">ファイル サイズ (バイト)。</param>
    /// <returns>表示用の文字列。</returns>
    private static string FormatVideoFileSize(long bytes)
    {
        const double unit = 1024.0;
        if (bytes >= unit * unit * unit) return $"{bytes / (unit * unit * unit):0.##} GB";
        if (bytes >= unit * unit) return $"{bytes / (unit * unit):0.##} MB";
        return bytes >= unit
            ? $"{bytes / unit:0.##} KB"
            : $"{bytes:0} B";
    }

    /// <summary>
    ///     動画と音声の再生を停止し、メディア操作の状態をリセットします。
    /// </summary>
    private void ResetMediaController()
    {
        ResetAudio();
        GifFrameHost.HorizontalAlignment = HorizontalAlignment.Center;
        _videoPositionTimer?.Stop();
        _videoPositionTimer = null;
        _videoIsPlaying = false;
        _videoClock.Reset();
        _videoOrigin = 0;
        _videoSpeed = 1;
        _videoDragging = false;
        _videoResumeAfterDrag = false;
        _videoHasStarted = false;
        Array.Clear(_videoDelays, 0, 2);
        Array.Clear(_videoSidePlaying, 0, 2);
        _videoSeeking = false;
        foreach (var media in new[] { _leftMedia, _rightMedia })
        {
            if (media == null) continue;
            media.MediaOpened -= Video_MediaOpened;
            try
            {
                media.Close();
            }
            catch
            {
                // Ignore exceptions when closing media, as it may already be disposed or in an invalid state.
            }
        }

        _leftMedia = _rightMedia = null;
        _leftVideoScale = _rightVideoScale = null;
        _videoSeek = null;
        _videoPlayPause = null;
        _videoTimeLabel = null;
        Array.Clear(_videoDurationLabels, 0, _videoDurationLabels.Length);
        if (GifFrameHost.Tag is KeyEventHandler frameKeyHandler)
            RemoveHandler(Keyboard.PreviewKeyDownEvent, frameKeyHandler);
        GifFrameHost.Tag = null;
        GifFrameHost.Children.Clear();
        GifFrameHost.Visibility = Visibility.Collapsed;
    }

    /// <summary>
    ///     比較元と比較先の動画を並べて表示し、共通の再生操作を作成します。
    /// </summary>
    private Task RenderVideos()
    {
        Body.Children.Clear();
        GifFrameHost.Children.Clear();
        GifFrameHost.Visibility = Visibility.Collapsed;
        Body.ColumnDefinitions[1].Width = new GridLength(12);

        var leftPath = ViewModel.GetPath(true);
        var rightPath = ViewModel.GetPath(false);
        var leftMissing = string.IsNullOrEmpty(leftPath) || !File.Exists(leftPath);
        var rightMissing = string.IsNullOrEmpty(rightPath) || !File.Exists(rightPath);

        _leftMedia = CreateVideoElement(leftPath, leftMissing, true);
        _rightMedia = CreateVideoElement(rightPath, rightMissing, false);

        _leftVideoScale = new ScaleTransform(1, 1);
        _rightVideoScale = new ScaleTransform(1, 1);

        var leftContainer = new Grid { LayoutTransform = _leftVideoScale };
        leftContainer.Children.Add(_leftMedia);
        var rightContainer = new Grid { LayoutTransform = _rightVideoScale };
        rightContainer.Children.Add(_rightMedia);

        _leftScroll = ThemedViewer(leftContainer, true, leftMissing);
        _rightScroll = ThemedViewer(rightContainer, false, rightMissing);
        EnableImagePan(_leftScroll);
        EnableImagePan(_rightScroll);

        Body.Children.Add(ImagePaneHost(_leftScroll, true, leftMissing));
        var rightHost = ImagePaneHost(_rightScroll, false, rightMissing);
        Grid.SetColumn(rightHost, 2);
        Body.Children.Add(rightHost);

        // Shared zoom control (reuse image toolbar area, styled like images)
        var zoom = _imageZoom = new Slider
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
        zoom.ValueChanged += (_, e) =>
        {
            _leftVideoScale.ScaleX = _leftVideoScale.ScaleY = e.NewValue;
            _rightVideoScale.ScaleX = _rightVideoScale.ScaleY = e.NewValue;
            zoomLabel.Text = $"{e.NewValue * 100:0}%";
        };

        if (ImageToolbar is Panel wrapper)
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
                _videoOrigin = VideoPosition;
                _videoClock.Reset();
                if (_videoIsPlaying) _videoClock.Start();
                _videoSpeed = speed;
                _leftMedia.SpeedRatio = _rightMedia.SpeedRatio = speed;
            }));
            AddIconActionButton(wrapper, DifferencerStatusIcons.GetCustomIcon(72),
                StringOverlay.Get("Diff_Fit"), FitVideosToViewport);
            AddIconActionButton(wrapper, DifferencerStatusIcons.GetCustomIcon(73), "100%", () => { zoom.Value = 1; });
            wrapper.Visibility = Visibility.Visible;
            ImageControlRow.Visibility = Visibility.Visible;
        }

        // Central video controller (play/pause + seek) in gifFrameHost
        BuildVideoController();
        UpdateEncodingHeaders();

        // Fit after layout
        Dispatcher.BeginInvoke(new Action(() =>
        {
            Body.UpdateLayout();
            FitVideosToViewport();
            _leftMedia?.Pause();
            _rightMedia?.Pause();
        }), DispatcherPriority.Loaded);

        return Task.CompletedTask;
    }

    /// <summary>
    ///     動画を再生する MediaElement を作成します。
    /// </summary>
    /// <param name="path">動画ファイルのパス。</param>
    /// <param name="missing">ファイルがない場合は true。</param>
    /// <param name="source">比較元の場合は true。</param>
    /// <returns>作成した MediaElement。</returns>
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

    /// <summary>
    ///     動画を開いたときに長さを表示し、シーク範囲と再生位置を更新します。
    /// </summary>
    /// <param name="sender">開いた MediaElement。</param>
    /// <param name="e">イベントデータ。</param>
    private void Video_MediaOpened(object sender, RoutedEventArgs e)
    {
        var side = ReferenceEquals(sender, _leftMedia) ? 0 : 1;
        if (_videoDurationLabels[side] != null && sender is MediaElement media)
            _videoDurationLabels[side].Text = media.NaturalDuration.HasTimeSpan
                ? media.NaturalDuration.TimeSpan.ToString(@"hh\:mm\:ss")
                : "--:--:--";
        UpdateVideoSeekRange();
        ApplyVideoPosition(VideoPosition, true);
        FitVideosToViewport();
    }

    /// <summary>
    ///     大きい方の動画のサイズに合わせて、表示領域に収まるズーム倍率を設定します。
    /// </summary>
    private void FitVideosToViewport()
    {
        if (_imageZoom == null || _leftScroll == null || _rightScroll == null) return;
        double vw = _leftMedia?.NaturalVideoWidth > 0 ? _leftMedia.NaturalVideoWidth : 640;
        double vh = _leftMedia?.NaturalVideoHeight > 0 ? _leftMedia.NaturalVideoHeight : 360;
        if (_rightMedia?.NaturalVideoWidth > 0)
        {
            vw = Math.Max(vw, _rightMedia.NaturalVideoWidth);
            vh = Math.Max(vh, _rightMedia.NaturalVideoHeight);
        }

        var viewW = Math.Min(_leftScroll.ViewportWidth, _rightScroll.ViewportWidth);
        var viewH = Math.Min(_leftScroll.ViewportHeight, _rightScroll.ViewportHeight);
        if (viewW <= 1 || viewH <= 1 || vw <= 0 || vh <= 0) return;
        var scale = Math.Min(viewW / vw, viewH / vh);
        scale = Math.Max(_imageZoom.Minimum, Math.Min(_imageZoom.Maximum, scale));
        _imageZoom.Value = scale;
    }

    /// <summary>
    ///     両側の動画の長さと遅延から、シーク バーの範囲を更新します。
    /// </summary>
    private void UpdateVideoSeekRange()
    {
        if (_videoSeek == null) return;
        var leftDur = _leftMedia?.NaturalDuration.HasTimeSpan == true
            ? _leftMedia.NaturalDuration.TimeSpan
            : TimeSpan.Zero;
        var rightDur = _rightMedia?.NaturalDuration.HasTimeSpan == true
            ? _rightMedia.NaturalDuration.TimeSpan
            : TimeSpan.Zero;
        var maxDur = TimeSpan.FromSeconds(Math.Max(0, Math.Max(
            leftDur.TotalSeconds > 0 ? leftDur.TotalSeconds + _videoDelays[0] : 0,
            rightDur.TotalSeconds > 0 ? rightDur.TotalSeconds + _videoDelays[1] : 0)));
        _videoSeeking = true;
        _videoSeek.Maximum = maxDur.TotalSeconds;
        _videoSeeking = false;
        _videoSeek.IsEnabled = _videoPlayPause.IsEnabled = maxDur > TimeSpan.Zero;
        UpdateVideoTimeLabel(TimeSpan.FromSeconds(VideoPosition));
    }

    /// <summary>
    ///     動画の音量スライダーとミュート ボタンを作成します。音声トラックがない場合は無効にします。
    /// </summary>
    /// <param name="media">対象の MediaElement。</param>
    /// <param name="source">比較元の場合は true。</param>
    /// <returns>作成したパネル。</returns>
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

    /// <summary>
    ///     再生/一時停止、シーク バー、時刻表示、再生速度などの動画の操作部を作成します。
    /// </summary>
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

        _videoPlayPause = new Button
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
        _videoPlayPause.Template = new ControlTemplate(typeof(Button)) { VisualTree = border };

        _videoPlayPause.Click += (_, _) => ToggleVideoPlayback();

        _videoSeek = new Slider
        {
            Minimum = 0,
            Maximum = 1,
            IsEnabled = false,
            VerticalAlignment = VerticalAlignment.Center,
            Style = TryFindResource("AudioSeekSlider") as Style
        };
        _videoSeek.AddHandler(Thumb.DragStartedEvent, new DragStartedEventHandler((_, _) => BeginVideoSeekDrag()));
        _videoSeek.AddHandler(Thumb.DragCompletedEvent,
            new DragCompletedEventHandler((_, _) => { EndVideoSeekDrag(); }));
        _videoSeek.ValueChanged += VideoSeek_ValueChanged;

        _videoTimeLabel = new TextBlock
        {
            Text = "0:00 / 0:00",
            FontSize = 12,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(10, 0, 0, 0),
            MinWidth = 80
        };
        _videoTimeLabel.SetResourceReference(TextBlock.ForegroundProperty, "Ink");

        var controller = new Grid
        {
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 4, 0, 4)
        };
        controller.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        controller.ColumnDefinitions.Add(new ColumnDefinition());
        controller.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        Grid.SetColumn(_videoSeek, 1);
        Grid.SetColumn(_videoTimeLabel, 2);
        controller.Children.Add(_videoPlayPause);
        controller.Children.Add(_videoSeek);
        controller.Children.Add(_videoTimeLabel);

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
        controls.Children.Add(CreateVideoVolumePanel(_leftMedia, true));
        Grid.SetColumn(frameBorder, 1);
        controls.Children.Add(frameBorder);
        var targetVolume = CreateVideoVolumePanel(_rightMedia, false);
        Grid.SetColumn(targetVolume, 2);
        controls.Children.Add(targetVolume);
        GifFrameHost.HorizontalAlignment = HorizontalAlignment.Stretch;
        GifFrameHost.Children.Add(controls);
        GifFrameHost.Visibility = Visibility.Visible;

        // 左右キーでシーク、Spaceで再生／一時停止（初回表示時から有効）
        KeyEventHandler videoKeyHandler = (_, e) =>
        {
            if (GifFrameHost.Visibility != Visibility.Visible) return;
            if (e.OriginalSource is DependencyObject input && FindAncestor<TextBox>(input) != null) return;
            if (e.OriginalSource is DependencyObject focused &&
                FindAncestor<Slider>(focused) is { } slider && slider != _videoSeek) return;

            if (e.Key == Key.Space)
            {
                ToggleVideoPlayback();
                e.Handled = true;
                return;
            }

            if (_videoSeek == null || !_videoSeek.IsEnabled) return;
            double step;
            if (e.Key == Key.Left) step = -1.0;
            else if (e.Key == Key.Right) step = 1.0;
            else return;

            var next = Math.Max(_videoSeek.Minimum, Math.Min(_videoSeek.Maximum, _videoSeek.Value + step));
            _videoSeek.Value = next;
            e.Handled = true;
        };
        AddHandler(Keyboard.PreviewKeyDownEvent, videoKeyHandler, true);
        GifFrameHost.Tag = videoKeyHandler;

        _videoPositionTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(50) };
        _videoPositionTimer.Tick += (_, _) =>
        {
            if (!_videoIsPlaying || _videoSeeking || _videoDragging || _videoSeek == null) return;
            var pos = TimeSpan.FromSeconds(VideoPosition);
            ApplyVideoPosition(pos.TotalSeconds, false);
            if (pos.TotalSeconds >= 0 && pos.TotalSeconds <= _videoSeek.Maximum)
            {
                _videoSeeking = true;
                _videoSeek.Value = pos.TotalSeconds;
                _videoSeeking = false;
                UpdateVideoTimeLabel(pos);
            }

            if (pos.TotalSeconds >= _videoSeek.Maximum) ToggleVideoPlayback();
        };
    }

    /// <summary>
    ///     両側の動画の再生と一時停止を切り替えます。
    /// </summary>
    private void ToggleVideoPlayback()
    {
        if (_leftMedia == null && _rightMedia == null) return;
        if (!_videoIsPlaying && _videoSeek?.IsEnabled != true) return;
        if (_videoIsPlaying)
        {
            _videoClock.Stop();
            _leftMedia?.Pause();
            _rightMedia?.Pause();
            Array.Clear(_videoSidePlaying, 0, _videoSidePlaying.Length);
            _videoIsPlaying = false;
            _videoPositionTimer?.Stop();
            if (_videoPlayPause?.Content is TextBlock tb) tb.Text = "▶";
        }
        else
        {
            if (VideoPosition >= _videoSeek.Maximum) SeekVideo(0);
            _videoIsPlaying = true;
            _videoClock.Start();
            var startPosition = VideoPosition;
            ApplyVideoPosition(startPosition, true);
            if (!_videoHasStarted)
            {
                _videoHasStarted = true;
                // Initial Play can reset MediaElement.Position to zero.
                // Reapply the requested seek after the media engine starts.
                Dispatcher.BeginInvoke(new Action(() =>
                {
                    if (_videoIsPlaying && _videoSeek != null)
                        ApplyVideoPosition(VideoPosition, true);
                }), DispatcherPriority.Loaded);
            }

            _videoPositionTimer?.Start();
            if (_videoPlayPause?.Content is TextBlock tb) tb.Text = "⏸";
        }
    }

    // ドラッグ中は左右の再生と共通時計を停止し、シーク完了後に元の再生状態へ戻す。
    /// <summary>
    ///     シーク バーのドラッグ開始時に、両側の再生と共通の時計を停止します。
    /// </summary>
    private void BeginVideoSeekDrag()
    {
        if (_videoSeek == null || _videoDragging) return;
        _videoResumeAfterDrag = _videoIsPlaying;
        _videoOrigin = VideoPosition;
        _videoClock.Reset();
        _videoIsPlaying = false;
        _videoPositionTimer?.Stop();
        _leftMedia?.Pause();
        _rightMedia?.Pause();
        Array.Clear(_videoSidePlaying, 0, _videoSidePlaying.Length);
        _videoDragging = true;
        _videoSeekThrottle.Restart();
    }

    /// <summary>
    ///     シーク バーのドラッグ終了時に選んだ位置へ移動し、ドラッグ前に再生中だった場合は再開します。
    /// </summary>
    private void EndVideoSeekDrag()
    {
        if (!_videoDragging || _videoSeek == null) return;
        _videoDragging = false;
        SeekVideo(_videoSeek.Value);
        var resume = _videoResumeAfterDrag;
        _videoResumeAfterDrag = false;
        if (resume) ToggleVideoPlayback();
    }

    /// <summary>
    ///     シーク バーの値が変わったときに再生位置を移動します。ドラッグ中は移動の頻度を抑えます。
    /// </summary>
    /// <param name="sender">イベントの送信元。</param>
    /// <param name="e">値変更のイベントデータ。</param>
    private void VideoSeek_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_videoSeeking || _videoSeek == null) return;
        if (_videoDragging)
        {
            if (_videoSeekThrottle.ElapsedMilliseconds < 90)
            {
                UpdateVideoTimeLabel(TimeSpan.FromSeconds(e.NewValue));
                return;
            }

            _videoSeekThrottle.Restart();
            SeekVideo(e.NewValue);
            return;
        }

        // バークリックやキーボード操作も、再生中は一旦停止して位置を確定する。
        var resume = _videoIsPlaying;
        if (resume)
        {
            _videoOrigin = VideoPosition;
            _videoClock.Reset();
            _videoIsPlaying = false;
            _videoPositionTimer?.Stop();
            _leftMedia?.Pause();
            _rightMedia?.Pause();
            Array.Clear(_videoSidePlaying, 0, _videoSidePlaying.Length);
        }

        SeekVideo(e.NewValue);
        if (resume) ToggleVideoPlayback();
    }

    /// <summary>
    ///     両側の動画を指定した位置に移動します。
    /// </summary>
    /// <param name="seconds">移動先の位置 (秒)。</param>
    private void SeekVideo(double seconds)
    {
        _videoSeeking = true;
        try
        {
            _videoOrigin = Math.Clamp(seconds, 0, _videoSeek.Maximum);
            _videoClock.Reset();
            if (_videoIsPlaying) _videoClock.Start();
            var pos = TimeSpan.FromSeconds(_videoOrigin);
            ApplyVideoPosition(_videoOrigin, true);
            _videoSeek.Value = _videoOrigin;
            UpdateVideoTimeLabel(pos);
        }
        finally
        {
            _videoSeeking = false;
        }
    }

    /// <summary>
    ///     「現在位置 / 全体の長さ」の時刻表示を更新します。
    /// </summary>
    /// <param name="pos">現在位置。</param>
    private void UpdateVideoTimeLabel(TimeSpan pos)
    {
        if (_videoTimeLabel == null || _videoSeek == null) return;
        var total = TimeSpan.FromSeconds(_videoSeek.Maximum);
        _videoTimeLabel.Text = $"{pos:hh\\:mm\\:ss} / {total:hh\\:mm\\:ss}";
    }

    /// <summary>
    ///     共通の再生位置を両側の動画に反映し、遅延を考慮して各側の再生・停止を切り替えます。
    /// </summary>
    /// <param name="position">共通の再生位置 (秒)。</param>
    /// <param name="seek">各側の位置を移動する場合は true。</param>
    private void ApplyVideoPosition(double position, bool seek)
    {
        for (var side = 0; side < 2; side++)
        {
            var item = side == 0 ? _leftMedia : _rightMedia;
            if (item?.NaturalDuration.HasTimeSpan != true) continue;
            var local = position - _videoDelays[side];
            var playing = _videoIsPlaying && local >= 0 && local < item.NaturalDuration.TimeSpan.TotalSeconds;
            if (seek)
                item.Position = TimeSpan.FromSeconds(Math.Clamp(local, 0,
                    Math.Max(0, item.NaturalDuration.TimeSpan.TotalSeconds - 0.001)));
            if (playing == _videoSidePlaying[side]) continue;
            if (playing) item.Play();
            else item.Pause();
            _videoSidePlaying[side] = playing;
        }
    }

    /// <summary>
    ///     指定側の動画を、遅延を考慮した位置に移動し、再生範囲内かどうかで再生・停止を切り替えます。
    /// </summary>
    /// <param name="side">0 が比較元、1 が比較先。</param>
    /// <param name="position">共通の再生位置 (秒)。</param>
    private void ApplyVideoSidePosition(int side, double position)
    {
        var item = side == 0 ? _leftMedia : _rightMedia;
        if (item?.NaturalDuration.HasTimeSpan != true) return;
        var local = position - _videoDelays[side];
        item.Position =
            TimeSpan.FromSeconds(Math.Clamp(local, 0, Math.Max(0, item.NaturalDuration.TimeSpan.TotalSeconds - 0.001)));
        var playing = _videoIsPlaying && local >= 0 && local < item.NaturalDuration.TimeSpan.TotalSeconds;
        if (playing == _videoSidePlaying[side]) return;
        if (playing) item.Play();
        else item.Pause();
        _videoSidePlaying[side] = playing;
    }

    /// <summary>
    ///     指定側の動画の遅延を調整する入力欄を作成します。
    /// </summary>
    /// <param name="source">比較元の場合は true。</param>
    /// <returns>作成した入力欄。</returns>
    private FrameworkElement CreateVideoDelay(bool source)
    {
        var side = source ? 0 : 1;
        var picker = CreateDelayPicker(source, () => _videoDelays[side], value =>
        {
            var position = VideoPosition;
            _videoDelays[side] = value;
            UpdateVideoSeekRange();
            if (_videoSeek == null) return;

            var clamped = Math.Clamp(position, _videoSeek.Minimum, _videoSeek.Maximum);
            if (Math.Abs(clamped - position) > double.Epsilon)
                SeekVideo(clamped);
            else
                ApplyVideoSidePosition(side, clamped);
        });
        picker.IsEnabled = File.Exists(ViewModel.GetPath(source));
        return picker;
    }
}