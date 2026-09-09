using System;
using System.Windows;
using DesktopIniManager.Properties;

namespace DesktopIniManager.Views
{
    internal partial class CleanReportWindow : Window
    {
        public CleanReportWindow()
        {
            InitializeComponent();
        }

        internal static void Show(Window owner, string summary, string logPath, string log)
        {
            var report = new CleanReportWindow
            {
                Owner = owner,
                Title = summary
            };
            report.LogBox.Text = string.Format(Strings.Differencer_LogLabel, logPath) + Environment.NewLine + log;
            report.Show();
        }
    }
}
