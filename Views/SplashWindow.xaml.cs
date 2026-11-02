using DesktopIniManager.Properties;
using System;
using System.Windows;
using System.Windows.Input;

namespace DesktopIniManager.Views;

internal partial class SplashWindow
{
    internal event EventHandler SkipStartupAnalysisRequested;

    internal SplashWindow()
    {
        InitializeComponent();
        PreviewKeyDown += OnPreviewKeyDown;
        Width = Math.Min(940, SystemParameters.WorkArea.Width * 0.9);
        Height = Width / 2;
        if (Height > SystemParameters.WorkArea.Height * 0.9)
        {
            Height = SystemParameters.WorkArea.Height * 0.9;
            Width = Height * 2;
        }
        Copyright.Text = string.Format(Strings.Splash_Copyright, DateTime.Now.Year);
    }

    private void OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Escape) return;

        e.Handled = true;
        SkipStartupAnalysisRequested?.Invoke(this, EventArgs.Empty);
    }

    internal void Report(string message, int completed)
    {
        if (Stage.Text != message) Previous.Text = Stage.Text;
        Stage.Text = message;
        if (!Progress.IsIndeterminate)
            Progress.Value = completed;
    }

    internal void SetBusy(bool busy)
    {
        Progress.IsIndeterminate = busy;
        if (!busy)
            Progress.Value = Math.Min(Progress.Maximum, Math.Max(Progress.Value, 3));
    }
}