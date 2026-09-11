using System;
using System.Windows;
using DesktopIniManager.Services;

namespace DesktopIniManager.Views
{
    internal partial class SynchronizationLogWindow : Window, ISynchronizationLog
    {
        public SynchronizationLogWindow()
        {
            InitializeComponent();
        }

        internal SynchronizationLogWindow(Window owner, string direction, DiffSnapshot snapshot) : this()
        {
            Owner = owner;
            Title = "Synchronizing — " + direction;
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
            log.AppendText(line + Environment.NewLine);
            log.ScrollToEnd();
        }

        public void Complete(int succeeded, int failed, int locked)
        {
            AppendLine(new string('-', 80));
            AppendLine("Complete  OK " + succeeded + " / FAIL " + failed + " / LOCKED " + locked);
            Title = "Sync result — OK " + succeeded + " / FAIL " + failed + " / LOCKED " + locked;
        }

        void ISynchronizationLog.Activate()
        {
            if (IsVisible) Activate();
        }
    }
}
