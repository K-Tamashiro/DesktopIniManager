using System;
using System.Windows;
using DesktopIniManager.Properties;

namespace DesktopIniManager.Views
{
    internal partial class SplashWindow : Window
    {
        internal SplashWindow()
        {
            InitializeComponent();
            Width = Math.Min(940, SystemParameters.WorkArea.Width * 0.9);
            Height = Width / 2;
            if (Height > SystemParameters.WorkArea.Height * 0.9)
            {
                Height = SystemParameters.WorkArea.Height * 0.9;
                Width = Height * 2;
            }
            copyright.Text = string.Format(Strings.Splash_Copyright, DateTime.Now.Year);
        }

        internal void Report(string message, int completed)
        {
            if (stage.Text != message) previous.Text = stage.Text;
            stage.Text = message;
            if (!progress.IsIndeterminate)
                progress.Value = completed;
        }

        internal void SetBusy(bool busy)
        {
            progress.IsIndeterminate = busy;
            if (!busy)
                progress.Value = Math.Min(progress.Maximum, Math.Max(progress.Value, 3));
        }
    }
}
