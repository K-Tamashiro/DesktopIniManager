using System.Windows;
using DesktopIniManager.Services;
using DesktopIniManager.ViewModels;
using DesktopIniManager.Views;

namespace DesktopIniManager
{
    public partial class MainWindow : Window
    {
        private readonly MainWindowPresentationService presentation;
        internal MainWindowViewModel ViewModel => presentation.ViewModel;

        public MainWindow() : this(null) { }

        internal MainWindow(StartupState startup)
        {
            ThemeService.Apply(startup?.DarkMode ?? SettingsService.LoadDarkMode());
            InitializeComponent();
            presentation = new MainWindowPresentationService(this, startup);
        }
    }
}
