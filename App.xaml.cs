using System;
using System.Windows;
using System.Windows.Threading;

namespace DesktopIniManager
{
    public partial class App : Application
    {
        protected override void OnStartup(StartupEventArgs e)
        {
            DispatcherUnhandledException += OnDispatcherUnhandledException;
            base.OnStartup(e);
        }

        private static void OnDispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
        {
            MessageBox.Show("An error occurred. The application can continue.\n\n" + e.Exception.Message,
                "desktop.ini Manager", MessageBoxButton.OK, MessageBoxImage.Error);
            e.Handled = true;
        }
    }
}
