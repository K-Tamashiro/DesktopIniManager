using DesktopIniManager.Properties;
using DesktopIniManager.Services;
using DesktopIniManager.Views;
using System;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Threading;

namespace DesktopIniManager;

public partial class App
{
    private bool _startupReady;

    /// <summary>
    ///     アプリケーション起動時にスロットを確保し、カルチャ設定・スプラッシュ表示・起動状態の読み込みを行ってメインウィンドウを表示します。
    ///     外部 DIFF 起動が要求された場合は単体の DIFF ビューアーを開きます。
    /// </summary>
    /// <param name="e">起動引数を含むイベントデータ。</param>
    protected override async void OnStartup(StartupEventArgs e)
    {
        var externalDiff = ExternalDiffLaunch.IsRequested(e.Args);
        // Standalone viewers use the default slot-1 settings without acquiring
        // a slot mutex or installing its front-request listener.
        if (!externalDiff && !AppSlot.TryAcquire(e.Args, out var slotFailure))
        {
            MessageBox.Show(slotFailure, StringOverlay.Get("App_Title"), MessageBoxButton.OK, MessageBoxImage.Information);
            Shutdown();
            return;
        }

        DispatcherUnhandledException += OnDispatcherUnhandledException;
        WindowActivationService.Install();
        var ui = StringOverlay.ResolveCulture();
        StringOverlay.Load(ui);
        Strings.Culture = ui;
        Thread.CurrentThread.CurrentUICulture = ui;
        Thread.CurrentThread.CurrentCulture = ui;
        base.OnStartup(e);
        if (externalDiff)
        {
            StartExternalDiff(e.Args);
            return;
        }

        ShutdownMode = ShutdownMode.OnExplicitShutdown;
        SplashWindow splash = null;
        try
        {
            splash = new SplashWindow();
            splash.Closed += (_, _) =>
            {
                if (!_startupReady) Shutdown();
            };
            splash.Show();
            await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
            var progress = new Progress<Tuple<string, int>>(step => splash.Report(step.Item1, step.Item2));
            var state = await Task.Run(() => StartupState.Load((message, completed) =>
                ((IProgress<Tuple<string, int>>)progress).Report(Tuple.Create(message, completed))));
            splash.Report(StringOverlay.Get("Splash_BuildingWorkspace"), 3);
            await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
            var main = new MainWindow(state);
            MainWindow = main;
            splash.SkipStartupAnalysisRequested += (_, _) => main.ViewModel.CancelSearch();
            if (!string.IsNullOrWhiteSpace(main.ViewModel.RootPath)
                && Directory.Exists(main.ViewModel.RootPath.Trim()))
            {
                splash.Report(StringOverlay.Get("Splash_BuildingWorkspace"), 3);
                splash.SetBusy(true);
                PropertyChangedEventHandler onStatus = (_, args) =>
                {
                    if (args.PropertyName == nameof(main.ViewModel.Status)
                        && !string.IsNullOrWhiteSpace(main.ViewModel.Status))
                        splash.Report(main.ViewModel.Status, 3);
                };
                main.ViewModel.PropertyChanged += onStatus;
                try
                {
                    await main.ViewModel.PrepareTreesAtStartupAsync();
                }
                finally
                {
                    main.ViewModel.PropertyChanged -= onStatus;
                    splash.SetBusy(false);
                }
            }

            var rendered = new TaskCompletionSource<bool>();
            EventHandler onRendered = null;
            onRendered = (_, _) =>
            {
                main.ContentRendered -= onRendered;
                rendered.TrySetResult(true);
            };
            main.ContentRendered += onRendered;
            main.Show();
            await rendered.Task;
            _startupReady = true;
            splash.Report(StringOverlay.Get("Splash_Ready"), 4);
            splash.Close();
            WindowActivationService.BringToFront(main);
            ShutdownMode = ShutdownMode.OnMainWindowClose;
        }
        catch (Exception ex)
        {
            LogException(ex);
            MessageBox.Show(string.Format(StringOverlay.Get("App_StartFailed"), ErrorMessages.English(ex)), StringOverlay.Get("App_ProductName"),
                MessageBoxButton.OK, MessageBoxImage.Error);
            _startupReady = true;
            splash?.Close();
            Shutdown(1);
        }
    }

    /// <summary>
    ///     コマンドライン引数から外部 DIFF 起動情報を解析し、単体の DIFF ビューアーウィンドウをメインウィンドウとして表示します。
    /// </summary>
    /// <param name="args">比較元と比較先のパスを含むコマンドライン引数。</param>
    private void StartExternalDiff(string[] args)
    {
        try
        {
            var launch = ExternalDiffLaunch.Parse(args);
            ThemeService.Apply(SettingsService.LoadTheme());
            var snapshot = launch.CreateSnapshot();
            var viewer = new DiffViewWindow(snapshot, snapshot.Files[0], launch.SourcePath, launch.TargetPath)
            {
                Width = 1200,
                Height = 800,
                WindowStartupLocation = WindowStartupLocation.CenterScreen
            };
            MainWindow = viewer;
            ShutdownMode = ShutdownMode.OnMainWindowClose;
            viewer.Show();
            WindowActivationService.BringToFront(viewer);
        }
        catch (Exception ex)
        {
            LogException(ex);
            MessageBox.Show(ErrorMessages.English(ex) + "\n\n" + ExternalDiffLaunch.Usage,
                StringOverlay.Get("App_DiffViewTitle"), MessageBoxButton.OK, MessageBoxImage.Error);
            Shutdown(1);
        }
    }

    /// <summary>
    ///     UI スレッドで捕捉されなかった例外をログに記録し、メッセージを表示して処理済みにします。
    /// </summary>
    /// <param name="sender">イベントの送信元。</param>
    /// <param name="e">未処理例外のイベントデータ。</param>
    private static void OnDispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        LogException(e.Exception);
        MessageBox.Show(string.Format(StringOverlay.Get("App_Unhandled"), ErrorMessages.English(e.Exception)),
            StringOverlay.Get("App_Title"), MessageBoxButton.OK, MessageBoxImage.Error);
        e.Handled = true;
    }

    /// <summary>
    ///     例外をトレース出力し、アプリケーションのスロットフォルダーにある last-error.log に書き込みます。
    /// </summary>
    /// <param name="exception">記録する例外。</param>
    private static void LogException(Exception exception)
    {
        Trace.TraceError("{0}", exception);
        try
        {
            Directory.CreateDirectory(AppSlot.Root);
            File.WriteAllText(Path.Combine(AppSlot.Root, "last-error.log"),
                $"{DateTimeOffset.Now:O}{Environment.NewLine}{Environment.ProcessPath}{Environment.NewLine}{exception}");
        }
        catch (Exception logError) when (logError is IOException or UnauthorizedAccessException)
        {
            Trace.TraceError("Could not write the exception log: {0}", logError);
        }
    }
}