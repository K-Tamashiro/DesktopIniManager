using System;
using System.Windows;
using DesktopIniManager.Services;
using DesktopIniManager.ViewModels;

namespace DesktopIniManager.Views
{
    internal partial class SynchronizationLogWindow : Window, ISynchronizationLog
    {
        public SynchronizationLogWindow()
        {
            InitializeComponent();
            CloseButtonIcon.Source = DifferencerStatusIcons.GetCustomIcon(34);
        }

        internal SynchronizationLogWindow(Window owner, string direction, DiffSnapshot snapshot) : this()
        {
            Owner = owner;
            Title = "Synchronization / ZIP";
            SummaryText.Text = direction + " — processing…";
            Show();
            AppendLine(direction);
            AppendLine("Source: " + snapshot.SourceRoot);
            AppendLine("Target: " + snapshot.TargetRoot);
            AppendLine(new string('-', 80));
            Activate();
        }

        public void AppendLine(string line)
        {
            if (!IsVisible) return;
            if (line != null && line.StartsWith("Log: ", StringComparison.Ordinal))
                LogPathText.Text = line;
            log.AppendText(line + Environment.NewLine);
            log.ScrollToEnd();
        }

        public void Complete(int succeeded, int failed, int locked)
        {
            AppendLine(new string('-', 80));
            AppendLine("Complete  OK " + succeeded + " / FAIL " + failed + " / LOCKED " + locked);
            SummaryText.Text = "Complete: " + succeeded + " succeeded / " + failed + " failed / " + locked + " locked";
            Title = "Synchronization / ZIP result";
        }

        void ISynchronizationLog.Activate()
        {
            if (IsVisible) Activate();
        }

        private void Close_Click(object sender, RoutedEventArgs e)
        {
            Close();
        }
    }
}
