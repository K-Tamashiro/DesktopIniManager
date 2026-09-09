using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using DesktopIniManager.Services;

namespace DesktopIniManager.Views
{
    internal sealed class SynchronizationLogWindow : Window, ISynchronizationLog
    {
        private readonly TextBox log = new TextBox
        {
            IsReadOnly = true, TextWrapping = TextWrapping.NoWrap,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            FontFamily = new FontFamily("Consolas"), FontSize = 13
        };
        internal SynchronizationLogWindow(Window owner, string direction, DiffSnapshot snapshot)
        {
            Owner = owner; Title = "Synchronizing — " + direction;
            Width = 850; Height = 480; Content = log;
            WindowStartupLocation = WindowStartupLocation.CenterOwner;
            Show();
            AppendLine(direction); AppendLine("Source: " + snapshot.SourceRoot);
            AppendLine("Target: " + snapshot.TargetRoot); AppendLine(new string('-', 80));
            Activate();
        }
        public void AppendLine(string line)
        {
            if (!IsVisible) return;
            log.AppendText(line + Environment.NewLine); log.ScrollToEnd();
        }
        public void Complete(int succeeded, int failed, int locked)
        {
            AppendLine(new string('-', 80));
            AppendLine("Complete  OK " + succeeded + " / FAIL " + failed + " / LOCKED " + locked);
            Title = "Sync result — OK " + succeeded + " / FAIL " + failed + " / LOCKED " + locked;
        }
        void ISynchronizationLog.Activate() { if (IsVisible) Activate(); }
    }
}
