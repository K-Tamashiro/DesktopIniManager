using System;
using System.Globalization;
using System.IO;
using System.Threading;
using System.Windows;
using System.Windows.Threading;
using System.Threading.Tasks;
using DesktopIniManager.Services;
using DesktopIniManager.Views;
using DesktopIniManager.Properties;

namespace DesktopIniManager;

public partial class App : Application
{
    protected override async void OnStartup(StartupEventArgs e)
    {
        bool externalDiff = ExternalDiffLaunch.IsRequested(e.Args);
        // Standalone viewers use the default slot-1 settings without acquiring
        // a slot mutex or installing its front-request listener.
        if (!externalDiff && !AppSlot.TryAcquire(e.Args, out string slotFailure))
        {
            MessageBox.Show(slotFailure, "desktop.ini Manager", MessageBoxButton.OK, MessageBoxImage.Information);
            Shutdown();
            return;
        }
        DispatcherUnhandledException += OnDispatcherUnhandledException;
        WindowActivationService.Install();
        CultureInfo ui = StringOverlay.ResolveCulture();
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
        bool ready = false;
        try
        {
            splash = new SplashWindow();
            splash.Closed += (sender, args) => { if (!ready) Shutdown(); };
            splash.Show();
            await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
            var progress = new Progress<Tuple<string, int>>(step => splash.Report(step.Item1, step.Item2));
            var state = await Task.Run(() => StartupState.Load((message, completed) =>
                ((IProgress<Tuple<string, int>>)progress).Report(Tuple.Create(message, completed))));
            splash.Report(Strings.Splash_BuildingWorkspace, 3);
            await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
            var main = new MainWindow(state);
            MainWindow = main;
            splash.SkipStartupAnalysisRequested += (sender, args) => main.ViewModel.CancelSearch();
            if (!string.IsNullOrWhiteSpace(main.ViewModel.RootPath)
                && Directory.Exists(main.ViewModel.RootPath.Trim()))
            {
                splash.Report(Strings.Splash_BuildingWorkspace, 3);
                splash.SetBusy(true);
                System.ComponentModel.PropertyChangedEventHandler onStatus = (sender, args) =>
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
            onRendered = (sender, args) => { main.ContentRendered -= onRendered; rendered.TrySetResult(true); };
            main.ContentRendered += onRendered;
            main.Show();
            await rendered.Task;
            ready = true;
            splash.Report(Strings.Splash_Ready, 4);
            splash.Close();
            WindowActivationService.BringToFront(main);
            ShutdownMode = ShutdownMode.OnMainWindowClose;
        }
        catch (Exception ex)
        {
            MessageBox.Show(string.Format(Strings.App_StartFailed, ErrorMessages.English(ex)), Strings.App_ProductName, MessageBoxButton.OK, MessageBoxImage.Error);
            ready = true;
            splash?.Close();
            Shutdown(1);
        }
    }

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
            MessageBox.Show(ErrorMessages.English(ex) + "\n\n" + ExternalDiffLaunch.Usage,
                "DIM DIFF VIEW", MessageBoxButton.OK, MessageBoxImage.Error);
            Shutdown(1);
        }
    }

    private static void OnDispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        MessageBox.Show(string.Format(Strings.App_Unhandled, ErrorMessages.English(e.Exception)),
            Strings.App_Title, MessageBoxButton.OK, MessageBoxImage.Error);
        e.Handled = true;
    }
}
