using System;
using System.Windows;
using System.Windows.Threading;
using System.Threading.Tasks;
using DesktopIniManager.Services;
using DesktopIniManager.Views;

namespace DesktopIniManager
{
    public partial class App : Application
    {
        protected override async void OnStartup(StartupEventArgs e)
        {
            DispatcherUnhandledException += OnDispatcherUnhandledException;
            base.OnStartup(e);
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
                splash.Report("Building the workspace…", 3);
                await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
                var main = new MainWindow(state);
                MainWindow = main;
                var rendered = new TaskCompletionSource<bool>();
                EventHandler onRendered = null;
                onRendered = (sender, args) => { main.ContentRendered -= onRendered; rendered.TrySetResult(true); };
                main.ContentRendered += onRendered;
                main.Show();
                await rendered.Task;
                ready = true;
                splash.Report("Ready", 4);
                splash.Close();
                ShutdownMode = ShutdownMode.OnMainWindowClose;
            }
            catch (Exception ex)
            {
                MessageBox.Show("DesktopIniManager could not start.\n\n" + ex.Message, "DesktopIniManager", MessageBoxButton.OK, MessageBoxImage.Error);
                ready = true;
                splash?.Close();
                Shutdown(1);
            }
        }

        private static void OnDispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
        {
            MessageBox.Show("An error occurred. The application can continue.\n\n" + e.Exception.Message,
                "desktop.ini Manager", MessageBoxButton.OK, MessageBoxImage.Error);
            e.Handled = true;
        }
    }
}
