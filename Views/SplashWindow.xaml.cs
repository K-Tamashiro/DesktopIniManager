using DesktopIniManager.Properties;
using System;
using System.Windows;
using System.Windows.Input;

namespace DesktopIniManager.Views;

internal partial class SplashWindow
{
    internal event EventHandler SkipStartupAnalysisRequested;

    /// <summary>
    ///     作業領域に合わせた大きさでスプラッシュ ウィンドウを作成します。
    /// </summary>
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

    /// <summary>
    ///     Esc キーで、起動時の解析を省略するよう要求します。
    /// </summary>
    /// <param name="sender">イベントの送信元。</param>
    /// <param name="e">キー イベントのデータ。</param>
    private void OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Escape) return;

        e.Handled = true;
        SkipStartupAnalysisRequested?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>
    ///     起動の進捗メッセージと完了ステップ数を表示します。
    /// </summary>
    /// <param name="message">進捗メッセージ。</param>
    /// <param name="completed">完了したステップ数。</param>
    internal void Report(string message, int completed)
    {
        if (Stage.Text != message) Previous.Text = Stage.Text;
        Stage.Text = message;
        if (!Progress.IsIndeterminate)
            Progress.Value = completed;
    }

    /// <summary>
    ///     進捗バーの不確定表示を切り替えます。
    /// </summary>
    /// <param name="busy">処理時間が不明な処理中の場合は true。</param>
    internal void SetBusy(bool busy)
    {
        Progress.IsIndeterminate = busy;
        if (!busy)
            Progress.Value = Math.Min(Progress.Maximum, Math.Max(Progress.Value, 3));
    }
}