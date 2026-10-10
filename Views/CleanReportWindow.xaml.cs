using System.Windows;
using DesktopIniManager.Properties;
using DesktopIniManager.ViewModels;

namespace DesktopIniManager.Views;

internal partial class CleanReportWindow : Window
{
    public CleanReportWindow()
    {
        InitializeComponent();
        CloseButtonIcon.Source = DifferencerStatusIcons.GetCustomIcon(34);
    }

    internal static void Show(Window owner, string summary, string logPath, string log)
    {
        var report = new CleanReportWindow
        {
            Owner = owner,
            Title = "Solution Clean"
        };
        report.SummaryText.Text = summary;
        report.LogPathText.Text = string.Format(Strings.Differencer_LogLabel, logPath);
        report.LogBox.Text = log;
        report.LogBox.ScrollToHome();
        report.Show();
    }

    private void Close_Click(object sender, RoutedEventArgs e)
    {
        Close();
    }
}
