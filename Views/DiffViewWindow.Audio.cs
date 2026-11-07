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
    private Border _audioDifferenceBusy;
    private bool _audioDifferenceEnabled;
    private Task<AudioDifferences> _audioDifferenceTask;
    private CancellationTokenSource _audioLoading;
    private AudioOverview _audioOverview;
    private AudioDiffPlayer _audioPlayer;
    private EventHandler _audioRendering;
    private AudioTimeline _audioTimeline;
    private DispatcherTimer _audioTimer;

    /// <summary>
    ///     音声の読み込みと差分解析を取り消し、プレーヤーと表示をリセットします。
    /// </summary>
    private void ResetAudio()
    {
        _audioLoading?.Cancel();
        _audioLoading?.Dispose();
        _audioLoading = null;
        _audioDifferenceTask = null;
        _audioDifferenceBusy = null;
        _audioDifferenceEnabled = false;
        _audioTimer?.Stop();
        _audioTimer = null;
        if (_audioRendering != null) CompositionTarget.Rendering -= _audioRendering;
        _audioRendering = null;
        _audioTimeline = null;
        _audioOverview = null;
        _audioPlayer?.Dispose();
        _audioPlayer = null;
        AudioOverviewHost.Children.Clear();
        AudioOverviewHost.Visibility = Visibility.Collapsed;
    }

    /// <summary>
    ///     音声の再生と一時停止を切り替えます。
    /// </summary>
    private void ToggleAudioPlayback()
    {
        if (_audioPlayer == null) return;

        try
        {
            if (_audioPlayer.Playing)
                _audioPlayer.Pause();
            else
                _audioPlayer.Play();
        }
        catch (Exception ex)
        {
            ViewModel.ReportError(ex);
        }
    }

    /// <summary>
    ///     両側の音声の波形をバックグラウンドで読み込み、音声 DIFF の表示を作成します。
    /// </summary>
    private async Task RenderAudio()
    {
        // Do not depend on the caller having a DispatcherSynchronizationContext.
        var request = await Dispatcher.InvokeAsync(() =>
        {
            CheckWindowOpen();
            ResetAudio();
            _audioLoading = new CancellationTokenSource();
            return (Source: ViewModel.GetPath(true), Target: ViewModel.GetPath(false), _audioLoading.Token);
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

    /// <summary>
    ///     音声の差分区間をバックグラウンドで解析し (結果はキャッシュ)、波形と概要表示に反映します。
    /// </summary>
    private async Task ApplyAudioDifferencesAsync()
    {
        if (_audioTimeline == null || _audioLoading == null || !_audioDifferenceEnabled) return;
        var timeline = _audioTimeline;
        var token = _audioLoading.Token;
        string source = ViewModel.GetPath(true), target = ViewModel.GetPath(false);
        try
        {
            // Optional analysis must not hold LoadContent's rendering/drop lock.
            if (_audioDifferenceTask == null)
            {
                if (_audioDifferenceBusy != null) _audioDifferenceBusy.Visibility = Visibility.Visible;
                _audioDifferenceTask = Task.Run(() => AudioDifferences.Load(source, target, token), token);
            }

            var differences = await _audioDifferenceTask;
            if (token.IsCancellationRequested || _windowClosed || !ReferenceEquals(timeline, _audioTimeline)) return;
            for (var side = 0; side < timeline.Waves.Length; side++)
                timeline.Waves[side].SetDifferences(differences, _audioDifferenceEnabled, side == 0);
            _audioOverview?.SetDifferences(differences, _audioDifferenceEnabled);
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            if (!token.IsCancellationRequested && !_windowClosed && ReferenceEquals(timeline, _audioTimeline))
            {
                _audioDifferenceTask = null;
                ViewModel.ReportError(ex);
            }
        }
        finally
        {
            if (ReferenceEquals(timeline, _audioTimeline) && _audioDifferenceBusy != null)
                _audioDifferenceBusy.Visibility = Visibility.Collapsed;
        }
    }

    /// <summary>
    ///     音声プレーヤー、概要表示、波形タイムライン、再生操作を作成して、音声 DIFF の画面を組み立てます。
    /// </summary>
    /// <param name="source">比較元の音声ファイル。</param>
    /// <param name="target">比較先の音声ファイル。</param>
    /// <param name="envelopes">両側の波形情報。</param>
    private void BuildAudioView(string source, string target, AudioEnvelope[] envelopes)
    {
        Dispatcher.VerifyAccess();
        CheckWindowOpen();
        var duration = Math.Max(envelopes[0]?.Duration ?? 0, envelopes[1]?.Duration ?? 0);
        _audioPlayer = new AudioDiffPlayer(source, target, duration);
        var overview = _audioOverview = new AudioOverview(envelopes, duration);
        overview.SetResourceReference(ForegroundProperty, "AudioWaveformInk");
        overview.SetResourceReference(BackgroundProperty, "CardBackground");
        AudioOverviewHost.Children.Add(overview);
        AudioOverviewHost.Visibility = Visibility.Visible;
        Body.Children.Clear();
        var layout = _audioTimeline = new AudioTimeline(envelopes, duration);
        layout.DelayChanged += (side, value) =>
        {
            try
            {
                _audioPlayer.SetDelay(side, value);
                layout.SetDelay(side, value);
                layout.UpdatePosition(_audioPlayer.Position, false);
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
                resumeAfterOffsetDrag = _audioPlayer.Playing;
                _audioPlayer.Pause();
            };
            wave.CursorDragCompleted += () =>
            {
                if (resumeAfterOffsetDrag) _audioPlayer.Play();
            };
        }

        Grid.SetColumnSpan(layout, 3);
        Body.Children.Add(layout);
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
        _audioDifferenceBusy = new Border
        {
            Child = busyContent,
            Visibility = Visibility.Collapsed,
            IsHitTestVisible = true
        };
        _audioDifferenceBusy.SetResourceReference(Border.BackgroundProperty, "OverlayScrim");
        Grid.SetColumnSpan(_audioDifferenceBusy, 3);
        Panel.SetZIndex(_audioDifferenceBusy, 10);
        Body.Children.Add(_audioDifferenceBusy);
        layout.ViewChanged += overview.SetViewport;
        overview.ViewportDragged += layout.SetViewStart;
        BuildAudioZoom(duration);

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
            _audioPlayer.SetVolume(index, volume.Value);
            volume.ValueChanged += (_, e) =>
            {
                _audioPlayer?.SetVolume(index, e.NewValue);
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
            duration = _audioPlayer.Duration;
            seek.Maximum = Math.Max(0.001, duration);
            if (!dragging) seek.Value = position;
            updating = false;
            time.Text =
                $"{TimeSpan.FromSeconds(position):hh\\:mm\\:ss} / {TimeSpan.FromSeconds(duration):hh\\:mm\\:ss}";
            if (updateTimeline) layout.UpdatePosition(position, _audioPlayer?.Playing == true);
            overview.SetTiming(duration, layout.Delays);
            overview.Update(position, _audioPlayer?.GetPeak(0) ?? 0, _audioPlayer?.GetPeak(1) ?? 0);
            play.Content = _audioPlayer?.Playing == true ? "Ⅱ" : "▶";
        }

        void Seek(double position)
        {
            try
            {
                _audioPlayer?.Seek(position);
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
            Refresh(_audioPlayer?.Position ?? 0);
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
                _audioPlayer?.SetRepeat(a, b);
                Refresh(_audioPlayer?.Position ?? 0);
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
        GifFrameHost.HorizontalAlignment = HorizontalAlignment.Stretch;
        GifFrameHost.Children.Add(controls);
        GifFrameHost.Visibility = Visibility.Visible;
        // Match display refresh; the transport text and overview meters need fewer updates.
        var lastOverviewUpdate = double.NegativeInfinity;
        _audioRendering = (_, e) =>
        {
            if (_audioPlayer == null || dragging || !_audioPlayer.Playing) return;
            var position = _audioPlayer.Position;
            layout.UpdatePosition(position, true);
            var now = ((RenderingEventArgs)e).RenderingTime.TotalSeconds;
            if (now - lastOverviewUpdate < 1.0 / 30) return;
            lastOverviewUpdate = now;
            Refresh(position, false);
        };
        CompositionTarget.Rendering += _audioRendering;
        _audioTimer = new DispatcherTimer(DispatcherPriority.Background, Dispatcher)
        { Interval = TimeSpan.FromMilliseconds(100) };
        _audioTimer.Tick += (_, _) =>
        {
            if (_audioPlayer == null) return;
            if (!dragging && !_audioPlayer.Playing) Refresh(_audioPlayer.Position);
            var error = Interlocked.Exchange(ref _audioPlayer.PlaybackError, null);
            if (error != null) ViewModel.ReportError(error);
        };
        _audioTimer.Start();
        Refresh(0);
    }

    /// <summary>
    ///     波形の時間方向のズーム スライダーを作成します。
    /// </summary>
    /// <param name="duration">再生全体の長さ (秒)。</param>
    private void BuildAudioZoom(double duration)
    {
        var zoom = _imageZoom = new Slider
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
        ImageToolbar.Children.Clear();
        ImageToolbar.Children.Add(chrome);
        zoom.ValueChanged += (_, e) =>
        {
            _audioTimeline?.SetZoom(e.NewValue / 100.0);
            label.Text = $"{e.NewValue:0}%";
        };
        AddIconActionButton(ImageToolbar, DifferencerStatusIcons.GetCustomIcon(72), StringOverlay.Get("Diff_Fit"), () =>
        {
            zoom.Value = 100;
            _audioTimeline?.Fit();
        });
        var differenceIcon = new Image
        { Source = DifferencerStatusIcons.GetCustomIcon(143), Stretch = Stretch.Uniform };
        var differenceToggle = new ToggleButton
        {
            Content = differenceIcon,
            Style = TryFindResource("IconToggleButton") as Style,
            Margin = new Thickness(0, 0, 8, 8),
            ToolTip = "Highlight audio differences (decoded samples, before volume adjustment)",
            IsChecked = _audioDifferenceEnabled
        };
        AutomationProperties.SetName(differenceToggle, "Highlight audio differences");

        void UpdateDifferenceMode()
        {
            _audioDifferenceEnabled = differenceToggle.IsChecked == true;
            differenceIcon.Opacity = _audioDifferenceEnabled ? 1 : 0.35;
            if (_audioTimeline != null)
                foreach (var wave in _audioTimeline.Waves)
                    wave.ShowDifferences = _audioDifferenceEnabled;
            if (_audioOverview != null) _audioOverview.ShowDifferences = _audioDifferenceEnabled;
        }

        differenceToggle.Checked += (_, _) =>
        {
            UpdateDifferenceMode();
            if (_audioDifferenceTask == null) _ = ApplyAudioDifferencesAsync();
        };
        differenceToggle.Unchecked += (_, _) => UpdateDifferenceMode();
        differenceIcon.Opacity = _audioDifferenceEnabled ? 1 : 0.35;
        ImageToolbar.Children.Add(differenceToggle);
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
            _audioOverview?.SetRepeatEnabled(enabled);
        }

        repeatToggle.Checked += (_, _) => UpdateRepeat();
        repeatToggle.Unchecked += (_, _) => UpdateRepeat();
        ImageToolbar.Children.Add(repeatToggle);
        // Exclusive speed selection: selected icon is opaque, others are dimmed.
        ImageToolbar.Children.Add(CreatePlaybackSpeeds("AudioPlaybackSpeed", speed => _audioPlayer?.SetSpeed(speed)));
        // Recalculate after layout, and whenever the visible waveform width changes.
        _audioTimeline.ZoomChanged += value => zoom.Value = value * 100;
        _audioTimeline!.Waves[0].SizeChanged += (_, _) => UpdateMaximum();
        UpdateMaximum();
        ImageToolbar.Visibility = ImageControlRow.Visibility = Visibility.Visible;
    }

    /// <summary>
    ///     再生速度 (0.5x から 2.0x) を選ぶボタン群を作成します。
    /// </summary>
    /// <param name="group">ラジオ ボタンのグループ名。</param>
    /// <param name="setSpeed">選んだ速度を適用する処理。</param>
    /// <returns>作成したパネル。</returns>
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

    /// <summary>
    ///     遅延 (秒) を調整する入力欄と増減ボタンを作成します。
    /// </summary>
    /// <param name="source">比較元の場合は true。</param>
    /// <param name="getValue">現在の遅延を取得する処理。</param>
    /// <param name="setValue">遅延を設定する処理。</param>
    /// <returns>作成した入力欄。</returns>
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
    private readonly float[][] _barPeaks = new float[2][];
    private readonly double[] _delays = new double[2];

    private readonly AudioEnvelope[] _envelopes;
    private readonly float[] _levels = new float[2];
    private bool[][] _differenceBars = new bool[2][];
    private int _differenceBarsCount = -1;
    private AudioDifferences _differences;
    private bool _draggingViewport;
    private double _duration;
    private double _position;
    private double? _repeatA, _repeatB;
    private bool _selectingRepeat;
    private bool _showDifferences;
    private double _viewStart, _viewDuration;
    private double _viewportDragX, _viewportDragStart;
    private bool _viewportMoved;

    /// <summary>
    ///     音声全体の概要表示を作成します。
    /// </summary>
    /// <param name="envelopes">両側の波形情報。</param>
    /// <param name="duration">再生全体の長さ (秒)。</param>
    internal AudioOverview(AudioEnvelope[] envelopes, double duration)
    {
        _envelopes = envelopes;
        _duration = duration;
        ClipToBounds = true;
        ToolTip = "Source / Target — click waveform to seek; output level at right";
    }

    internal bool RepeatEnabled { get; private set; }

    internal bool ShowDifferences
    {
        get => _showDifferences;
        set
        {
            _showDifferences = value;
            InvalidateVisual();
        }
    }

    internal event Action<double?, double?> RepeatChanged;

    internal event Action<double> ViewportDragged;

    internal event Action<double> ViewportClicked;

    /// <summary>
    ///     A-B リピートの区間指定モードを切り替え、指定中の区間をリセットします。
    /// </summary>
    /// <param name="enabled">区間指定モードにする場合は true。</param>
    internal void SetRepeatEnabled(bool enabled)
    {
        RepeatEnabled = enabled;
        _selectingRepeat = false;
        _draggingViewport = false;
        _viewportMoved = false;
        if (IsMouseCaptured) ReleaseMouseCapture();
        _repeatA = _repeatB = null;
        Cursor = enabled ? Cursors.Cross : Cursors.Arrow;
        RepeatChanged?.Invoke(null, null);
        InvalidateVisual();
    }

    /// <summary>
    ///     概要表示上の位置を時刻に変換します。
    /// </summary>
    /// <param name="point">概要表示上の位置。</param>
    /// <returns>時刻 (秒)。</returns>
    private double RepeatTime(Point point)
    {
        return Math.Clamp(point.X / Math.Max(1, ActualWidth - 100), 0, 1) * _duration;
    }

    /// <summary>
    ///     表示範囲の枠のドラッグ、またはリピート区間の指定を開始します。
    /// </summary>
    /// <param name="e">マウス イベントのデータ。</param>
    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonDown(e);
        if (!RepeatEnabled && _duration > 0 && _viewDuration > 0 && _viewDuration < _duration)
        {
            var width = Math.Max(0, ActualWidth - 100);
            var x = e.GetPosition(this).X;
            var left = _viewStart / _duration * width;
            var right = (_viewStart + _viewDuration) / _duration * width;
            if (x >= left && x <= right && CaptureMouse())
            {
                _draggingViewport = true;
                _viewportMoved = false;
                _viewportDragX = x;
                _viewportDragStart = _viewStart;
                Cursor = Cursors.SizeWE;
                e.Handled = true;
                return;
            }
        }

        if (!RepeatEnabled || _duration <= 0 || e.GetPosition(this).X > ActualWidth - 100) return;
        if (!CaptureMouse()) return;
        _selectingRepeat = true;
        _repeatA = RepeatTime(e.GetPosition(this));
        _repeatB = null;
        RepeatChanged?.Invoke(null, null);
        InvalidateVisual();
        e.Handled = true;
    }

    /// <summary>
    ///     表示範囲の枠のドラッグ中は枠を移動し、リピート区間の指定中は区間を更新します。
    /// </summary>
    /// <param name="e">マウス イベントのデータ。</param>
    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        if (_draggingViewport)
        {
            var width = Math.Max(1, ActualWidth - 100);
            var pixels = e.GetPosition(this).X - _viewportDragX;
            if (Math.Abs(pixels) >= SystemParameters.MinimumHorizontalDragDistance) _viewportMoved = true;
            if (_viewportMoved)
            {
                var delta = pixels / width * _duration;
                ViewportDragged?.Invoke(Math.Clamp(_viewportDragStart + delta, 0,
                    Math.Max(0, _duration - _viewDuration)));
            }

            e.Handled = true;
            return;
        }

        if (!_selectingRepeat) return;
        _repeatB = RepeatTime(e.GetPosition(this));
        InvalidateVisual();
        e.Handled = true;
    }

    /// <summary>
    ///     枠のドラッグを終了します。ドラッグせずに離した場合はクリック位置の時刻を通知し、リピート区間の指定中は区間を確定して通知します。
    /// </summary>
    /// <param name="e">マウス イベントのデータ。</param>
    protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonUp(e);
        if (_draggingViewport)
        {
            var click = !_viewportMoved;
            var clickTime = RepeatTime(e.GetPosition(this));
            _draggingViewport = false;
            _viewportMoved = false;
            ReleaseMouseCapture();
            Cursor = Cursors.Arrow;
            if (click) ViewportClicked?.Invoke(clickTime);
            e.Handled = true;
            return;
        }

        if (!_selectingRepeat) return;
        _repeatB = RepeatTime(e.GetPosition(this));
        _selectingRepeat = false;
        ReleaseMouseCapture();
        RepeatChanged?.Invoke(_repeatA, _repeatB);
        InvalidateVisual();
        e.Handled = true;
    }

    /// <summary>
    ///     マウスのキャプチャを失ったときにドラッグ状態を解除します。
    /// </summary>
    /// <param name="e">マウス イベントのデータ。</param>
    protected override void OnLostMouseCapture(MouseEventArgs e)
    {
        base.OnLostMouseCapture(e);
        if (_draggingViewport)
        {
            _draggingViewport = false;
            _viewportMoved = false;
            Cursor = Cursors.Arrow;
            return;
        }

        if (!_selectingRepeat) return;
        _selectingRepeat = false;
        _repeatA = _repeatB = null;
        InvalidateVisual();
    }

    /// <summary>
    ///     リピート区間を半透明の帯で描画します。
    /// </summary>
    /// <param name="dc">描画コンテキスト。</param>
    /// <param name="width">波形の描画幅。</param>
    private void DrawRepeat(DrawingContext dc, double width)
    {
        if (!RepeatEnabled || !_repeatA.HasValue || _duration <= 0) return;
        var a = _repeatA.Value / _duration * width;
        var b = (_repeatB ?? _repeatA).Value / _duration * width;
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
        if (_repeatB.HasValue) Marker(b);
    }

    /// <summary>
    ///     差分区間を設定し、表示するかどうかを切り替えます。
    /// </summary>
    /// <param name="value">差分区間。</param>
    /// <param name="enabled">差分を表示する場合は true。</param>
    internal void SetDifferences(AudioDifferences value, bool enabled)
    {
        _differences = value;
        _differenceBarsCount = -1;
        ShowDifferences = enabled;
    }

    /// <summary>
    ///     概要表示のバーごとに差分を含むかどうかを求め、キャッシュします。
    /// </summary>
    /// <param name="side">0 が比較元、1 が比較先。</param>
    /// <param name="bars">バーの数。</param>
    /// <returns>バーごとの差分の有無。</returns>
    private bool[] GetDifferenceBars(int side, int bars)
    {
        if (_differenceBarsCount != bars)
        {
            _differenceBarsCount = bars;
            _differenceBars = new bool[2][];
        }

        if (_differenceBars[side] != null) return _differenceBars[side];
        var flags = new bool[bars];
        if (_differences != null && _envelopes[side] != null && _envelopes[side].Duration > 0)
        {
            var channels = side == 0 ? _differences.Channels : _differences.TargetChannels;
            var seconds = _envelopes[side].Duration;
            foreach (var ranges in channels)
                foreach (var range in ranges)
                {
                    var first = Math.Clamp((int)Math.Floor(range.Start / seconds * bars), 0, bars);
                    var last = Math.Clamp((int)Math.Ceiling(range.End / seconds * bars), 0, bars);
                    for (var i = first; i < last; i++) flags[i] = true;
                }
        }

        return _differenceBars[side] = flags;
    }

    /// <summary>
    ///     再生全体の長さと両側の遅延を設定します。
    /// </summary>
    /// <param name="total">再生全体の長さ (秒)。</param>
    /// <param name="offsets">両側の遅延 (秒)。</param>
    internal void SetTiming(double total, double[] offsets)
    {
        _duration = total;
        Array.Copy(offsets, _delays, 2);
    }

    /// <summary>
    ///     概要表示のバーごとのピーク値を波形データから求め、キャッシュします。
    /// </summary>
    /// <param name="side">0 が比較元、1 が比較先。</param>
    /// <param name="bars">バーの数。</param>
    /// <returns>バーごとのピーク値。</returns>
    private float[] GetBarPeaks(int side, int bars)
    {
        if (_barPeaks[side]?.Length == bars) return _barPeaks[side];
        var result = new float[bars];
        var envelope = _envelopes[side];
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

        return _barPeaks[side] = result;
    }

    /// <summary>
    ///     波形タイムラインで表示中の範囲を設定し、再描画します。
    /// </summary>
    /// <param name="start">表示範囲の開始 (秒)。</param>
    /// <param name="span">表示範囲の長さ (秒)。</param>
    internal void SetViewport(double start, double span)
    {
        _viewStart = start;
        _viewDuration = span;
        InvalidateVisual();
    }

    /// <summary>
    ///     再生位置と出力レベルを更新して再描画します。
    /// </summary>
    /// <param name="seconds">再生位置 (秒)。</param>
    /// <param name="source">比較元の出力レベル。</param>
    /// <param name="target">比較先の出力レベル。</param>
    internal void Update(double seconds, float source, float target)
    {
        _position = seconds;
        _levels[0] = source;
        _levels[1] = target;
        InvalidateVisual();
    }

    /// <summary>
    ///     両側の波形の概要、差分、表示範囲、リピート区間、再生位置、出力レベルを描画します。
    /// </summary>
    /// <param name="dc">描画コンテキスト。</param>
    protected override void OnRender(DrawingContext dc)
    {
        base.OnRender(dc);
        dc.DrawRoundedRectangle(Background, null, new Rect(RenderSize), 5, 5);
        double width = Math.Max(0, ActualWidth - 100), rowHeight = ActualHeight / 2;
        for (var side = 0; side < 2; side++)
        {
            var color = TryFindResource(side == 0 ? "SourceColor" : "TargetColor") as Brush ?? Foreground;
            var envelope = _envelopes[side];
            var baseline = rowHeight * (side + 1) - 3;
            if (envelope != null && envelope.Duration > 0 && _duration > 0)
            {
                var bars = Math.Max(1, (int)(width * envelope.Duration / _duration / 4));
                var peaks = GetBarPeaks(side, bars);
                var redBars = _showDifferences ? GetDifferenceBars(side, bars) : null;
                for (var bar = 0; bar < bars; bar++)
                {
                    var peak = peaks[bar];
                    var height = Math.Max(1, Math.Clamp(peak, 0, 1) * (rowHeight - 6));
                    var barTime = (double)bar / bars * envelope.Duration + _delays[side];
                    var barX = bar * 4 + _delays[side] / _duration * width;
                    if (barX < 0 || barX + 2 > width) continue;
                    var played = barTime < _position;
                    if (!played) dc.PushOpacity(UnplayedOpacity);
                    dc.DrawRectangle(redBars != null && redBars[bar] ? Brushes.Red : color, null,
                        new Rect(barX, baseline - height, 2, height));
                    if (!played) dc.Pop();
                }
            }

            // dBFS scale: -60 dB to 0 dB, including saved volume/mute.
            var level = _levels[side] > 0 ? Math.Clamp((20 * Math.Log10(_levels[side]) + 60) / 60, 0, 1) : 0;
            for (var segment = 0; segment < 16; segment++)
            {
                dc.PushOpacity(segment < level * 16 ? 1 : 0.15);
                dc.DrawRectangle(segment >= 14 ? Brushes.OrangeRed : color, null,
                    new Rect(width + 10 + segment * 5, side * rowHeight + 7, 3, Math.Max(1, rowHeight - 14)));
                dc.Pop();
            }
        }

        if (_duration > 0)
        {
            var x = Math.Clamp(_position / _duration, 0, 1) * width;
            dc.DrawLine(new Pen(Brushes.OrangeRed, 1), new Point(x, 0), new Point(x, ActualHeight));
        }

        if (_duration > 0 && _viewDuration > 0 && _viewDuration < _duration)
            dc.DrawRectangle(null, new Pen(Foreground, 1), new Rect(_viewStart / _duration * width, 1,
                _viewDuration / _duration * width, Math.Max(0, ActualHeight - 2)));
        DrawRepeat(dc, width);
    }
}

// Both panes share one horizontal time axis; only visible pixels are rendered.
internal sealed class AudioTimeline : Grid
{
    internal readonly double[] Delays = new double[2];

    internal readonly AudioWaveform[] Waves = new AudioWaveform[2];
    private readonly FrameworkElement[] _delayPickers = new FrameworkElement[2];
    private readonly ScrollBar _scroll = new() { Orientation = Orientation.Horizontal, Height = 16, Minimum = 0 };
    private bool _applyingView;
    private double _currentPosition;

    /// <summary>
    ///     両側の波形と共通の横スクロール バーを並べた波形タイムラインを作成します。
    /// </summary>
    /// <param name="envelopes">両側の波形情報。</param>
    /// <param name="duration">再生全体の長さ (秒)。</param>
    internal AudioTimeline(AudioEnvelope[] envelopes, double duration)
    {
        RowDefinitions.Add(new RowDefinition());
        RowDefinitions.Add(new RowDefinition());
        // Reserve scrollbar space even at fit so zoom never changes waveform height.
        RowDefinitions.Add(new RowDefinition { Height = new GridLength(16) });
        for (var side = 0; side < 2; side++)
        {
            var index = side;
            var wave = Waves[side] = new AudioWaveform(envelopes[side], duration);

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
            _delayPickers[side] = picker;
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
                Math.Round(Math.Clamp(_currentPosition - seconds, -86400, 86400), 1));
            wave.ViewChanged += () =>
            {
                if (_applyingView || wave.ViewDuration <= 0) return;
                _applyingView = true;
                try
                {
                    Zoom = Math.Max(1, duration / wave.ViewDuration);
                    UpdateRange();
                    _scroll.Value = wave.ViewStart / Math.Max(duration, 0.001) * Viewport * Zoom;
                }
                finally
                {
                    _applyingView = false;
                }

                ApplyView();
                ZoomChanged?.Invoke(Zoom);
            };
        }

        SetRow(_scroll, 2);
        Children.Add(_scroll);
        _scroll.ValueChanged += (_, _) => ApplyView();
        Waves[0].SizeChanged += (_, e) =>
        {
            var fraction = e.PreviousSize.Width > 0 ? Offset / (e.PreviousSize.Width * Zoom) : 0;
            UpdateRange();
            _scroll.Value = Math.Clamp(fraction * Viewport * Zoom, 0, LastOffset);
            ApplyView();
        };
    }

    internal double Zoom { get; private set; } = 1;

    internal double Offset => _scroll.Value;
    internal double Viewport => Math.Max(1, Waves[0].ActualWidth);

    // Maximum is the final scroll Value; ViewportSize controls the thumb size.
    private double LastOffset => Math.Max(0, Viewport * (Zoom - 1));

    /// <summary>
    ///     サンプリングレート、ビット深度、ビットレートを表示するヘッダーを作成します。相手と異なる値は赤で表示します。
    /// </summary>
    /// <param name="current">表示する側の波形情報。</param>
    /// <param name="other">相手側の波形情報。</param>
    /// <param name="source">比較元の場合は true。</param>
    /// <returns>作成したヘッダー。</returns>
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
            (long)Math.Round(current.Duration * 1000) !=
            (long)Math.Round((other?.Duration ?? 0) * 1000));
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

    /// <summary>
    ///     指定側の遅延を設定し、遅延の入力欄に反映します。
    /// </summary>
    /// <param name="side">0 が比較元、1 が比較先。</param>
    /// <param name="seconds">遅延 (秒)。</param>
    internal void SetDelay(int side, double seconds)
    {
        Delays[side] = seconds;
        (_delayPickers[side]?.Tag as Action)?.Invoke();
    }

    internal event Action<int, double> DelayChanged;

    internal event Action<double> ZoomChanged;

    internal event Action<double, double> ViewChanged;

    /// <summary>
    ///     横スクロール バーの範囲を、ズーム倍率と表示幅に合わせて更新します。
    /// </summary>
    private void UpdateRange()
    {
        _scroll.Maximum = LastOffset;
        _scroll.ViewportSize = Viewport;
        _scroll.SmallChange = 48;
        _scroll.LargeChange = Viewport * 0.8;
        _scroll.IsEnabled = Zoom > 1;
        // Keep the reserved row height, but hide the scrollbar at 100%.
        _scroll.Visibility = Zoom > 1 ? Visibility.Visible : Visibility.Hidden;
    }

    /// <summary>
    ///     スクロール位置とズーム倍率を両側の波形の表示範囲に反映します。
    /// </summary>
    private void ApplyView()
    {
        if (_applyingView) return;
        _applyingView = true;
        try
        {
            var start = Waves[0].Duration * Offset / Math.Max(1, Viewport * Zoom);
            foreach (var wave in Waves) wave.SetViewRange(start, wave.Duration / Zoom);
        }
        finally
        {
            _applyingView = false;
        }

        ViewChanged?.Invoke(Waves[0].ViewStart, Waves[0].ViewDuration);
    }

    /// <summary>
    ///     時間方向のズーム倍率を設定します。表示範囲の左端の時刻は変えません。
    /// </summary>
    /// <param name="value">ズーム倍率。</param>
    internal void SetZoom(double value)
    {
        var newZoom = Math.Clamp(value, 1, Math.Max(1, Waves[0].Duration / WaveformControl.MinimumViewSeconds));
        if (Math.Abs(Zoom - newZoom) < 1e-9) return;

        // Keep the time currently at the viewport's left edge at X=0.
        var leftFraction = Offset / (Viewport * Zoom);
        Zoom = newZoom;
        UpdateRange();
        _scroll.Value = Math.Clamp(leftFraction * Viewport * Zoom, 0, LastOffset);
        ApplyView();
    }

    /// <summary>
    ///     ズームを解除して全体を表示します。
    /// </summary>
    internal void Fit()
    {
        SetZoom(1);
        _scroll.Value = 0;
        foreach (var wave in Waves) wave.Fit();
    }

    /// <summary>
    ///     表示範囲の開始時刻を指定してスクロールします。
    /// </summary>
    /// <param name="seconds">表示範囲の開始 (秒)。</param>
    internal void SetViewStart(double seconds)
    {
        var duration = Waves[0].Duration;
        if (duration <= 0 || Zoom <= 1) return;
        _scroll.Value = Math.Clamp(seconds / duration * Viewport * Zoom, 0, LastOffset);
    }

    /// <summary>
    ///     指定したピクセル数だけ横にスクロールします。
    /// </summary>
    /// <param name="pixels">スクロール量 (ピクセル)。</param>
    internal void ScrollBy(double pixels)
    {
        _scroll.Value = Math.Clamp(Offset + pixels, 0, LastOffset);
    }

    /// <summary>
    ///     両側の波形の再生位置を遅延を考慮して更新し、必要に応じて再生位置が見えるようスクロールします。
    /// </summary>
    /// <param name="seconds">再生位置 (秒)。</param>
    /// <param name="follow">再生位置に追従してスクロールする場合は true。</param>
    internal void UpdatePosition(double seconds, bool follow)
    {
        _currentPosition = seconds;
        for (var side = 0; side < Waves.Length; side++) Waves[side].Position = Math.Max(0, seconds - Delays[side]);
        if (!follow || Zoom <= 1 || Array.Exists(Waves, w => w.IsPanning)) return;
        var x = Waves[0].PixelAt(Math.Max(0, seconds - Delays[0]));
        // Scroll smoothly once the playhead reaches the right-hand safe area.
        if (x > Offset + Viewport * 0.85 || x < Offset)
            _scroll.Value = Math.Clamp(x - Viewport * (x < Offset ? 0.15 : 0.85), 0, LastOffset);
    }
}

// DIM supplies annotations; the reusable control owns waveform rendering.
internal sealed class AudioWaveform : WaveformControl
{
    private double _cursorPosition;
    private DrawingGroup _differenceLayer;
    private AudioDifferences _differences;
    private bool _draggingCursor;

    private (WaveformData Data, double Start, double Span,
        double Amplitude, Size Size, AudioDifferences Differences, bool Source) _layerKey;

    private bool _source = true, _showDifferences;

    /// <summary>
    ///     音声 DIFF 用の波形表示を作成します。
    /// </summary>
    /// <param name="envelope">波形情報。</param>
    /// <param name="duration">タイムライン全体の長さ (秒)。</param>
    internal AudioWaveform(AudioEnvelope envelope, double duration)
    {
        SetData(envelope?.Data, duration);
        ToolTip = "Wheel: time zoom / Ctrl+wheel: amplitude / Drag: pan / Click: seek";
    }

    internal bool ShowDifferences
    {
        get => _showDifferences;
        set
        {
            if (_showDifferences == value) return;
            _showDifferences = value;
            // Switching only changes the opacity of the retained red layer.
            if (_differenceLayer != null) _differenceLayer.Opacity = value ? 1 : 0;
            InvalidateVisual();
        }
    }

    internal double Position
    {
        set
        {
            _cursorPosition = value;
            SetPosition(TimeSpan.FromSeconds(value));
        }
    }

    /// <summary>
    ///     差分区間を設定し、表示するかどうかを切り替えます。
    /// </summary>
    /// <param name="value">差分区間。</param>
    /// <param name="enabled">差分を表示する場合は true。</param>
    /// <param name="source">比較元側の差分を表示する場合は true。</param>
    internal void SetDifferences(AudioDifferences value, bool enabled, bool source = true)
    {
        _differences = value;
        _source = source;
        _differenceLayer = null;
        _layerKey = default;
        ShowDifferences = enabled;
        InvalidateVisual();
    }

    /// <summary>
    ///     ズーム倍率とスクロール位置から表示範囲を設定します。
    /// </summary>
    /// <param name="zoom">ズーム倍率。</param>
    /// <param name="offset">スクロール位置 (ピクセル)。</param>
    internal void SetView(double zoom, double offset)
    {
        SetViewRange(Duration * offset / Math.Max(1, ActualWidth * zoom), Duration / zoom);
    }

    /// <summary>
    ///     表示上の X 座標を時刻に変換します。
    /// </summary>
    /// <param name="x">X 座標。</param>
    /// <returns>時刻 (秒)。</returns>
    internal double TimeAt(double x)
    {
        return XToTime(x);
    }

    /// <summary>
    ///     時刻を、タイムライン全体での X 座標に変換します。
    /// </summary>
    /// <param name="seconds">時刻 (秒)。</param>
    /// <returns>X 座標。</returns>
    internal double PixelAt(double seconds)
    {
        return ViewDuration > 0 ? seconds / ViewDuration * ActualWidth : 0;
    }

    internal event Action CursorDragStarted;

    internal event Action CursorDragCompleted;

    internal event Action<double> CursorDragged;

    /// <summary>
    ///     再生位置のカーソル付近を押した場合は、遅延調整のためのカーソルのドラッグを開始します。
    /// </summary>
    /// <param name="e">マウス イベントのデータ。</param>
    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        if (Math.Abs(e.GetPosition(this).X - TimeToX(_cursorPosition)) <= 7 && CaptureMouse())
        {
            _draggingCursor = true;
            CursorDragStarted?.Invoke();
            e.Handled = true;
            return;
        }

        base.OnMouseLeftButtonDown(e);
    }

    /// <summary>
    ///     カーソルのドラッグ中は、カーソル位置の時刻を通知します。
    /// </summary>
    /// <param name="e">マウス イベントのデータ。</param>
    protected override void OnMouseMove(MouseEventArgs e)
    {
        if (!_draggingCursor)
        {
            base.OnMouseMove(e);
            return;
        }

        CursorDragged?.Invoke(XToTime(e.GetPosition(this).X));
        e.Handled = true;
    }

    /// <summary>
    ///     カーソルのドラッグを終了し、最終位置と完了を通知します。
    /// </summary>
    /// <param name="e">マウス イベントのデータ。</param>
    protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e)
    {
        if (!_draggingCursor)
        {
            base.OnMouseLeftButtonUp(e);
            return;
        }

        CursorDragged?.Invoke(XToTime(e.GetPosition(this).X));
        _draggingCursor = false;
        ReleaseMouseCapture();
        CursorDragCompleted?.Invoke();
        e.Handled = true;
    }

    /// <summary>
    ///     マウスのキャプチャを失ったときにカーソルのドラッグを終了し、完了を通知します。
    /// </summary>
    /// <param name="e">マウス イベントのデータ。</param>
    protected override void OnLostMouseCapture(MouseEventArgs e)
    {
        var wasDragging = _draggingCursor;
        _draggingCursor = false;
        base.OnLostMouseCapture(e);
        if (wasDragging) CursorDragCompleted?.Invoke();
    }

    /// <summary>
    ///     差分区間を赤で強調したレイヤーを、表示条件が変わったときだけ作り直して描画します。
    /// </summary>
    /// <param name="dc">描画コンテキスト。</param>
    protected override void DrawOverlay(DrawingContext dc)
    {
        if (!_showDifferences || _differences == null || Data == null ||
            ViewDuration <= 0 || ActualWidth <= 0 || ActualHeight <= 0) return;

        var key = (Data, ViewStart, ViewDuration, AmplitudeScale, RenderSize, _differences, _source);
        if (_differenceLayer == null || _layerKey != key)
        {
            _layerKey = key;
            _differenceLayer = BuildDifferenceLayer();
        }

        dc.DrawDrawing(_differenceLayer);
    }

    /// <summary>
    ///     差分区間の波形を赤で描いたレイヤーを作成します。
    /// </summary>
    /// <returns>作成したレイヤー。</returns>
    private DrawingGroup BuildDifferenceLayer()
    {
        var layer = new DrawingGroup { Opacity = _showDifferences ? 1 : 0 };
        var channels = _source ? _differences.Channels : _differences.TargetChannels;
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