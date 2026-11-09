using DesktopIniManager.Properties;
using DesktopIniManager.ViewModels;
using System.Windows;

namespace DesktopIniManager.Views;

internal partial class CleanReportWindow
{
    /// <summary>
    ///     Solution Clean の結果ウィンドウを作成します。
    /// </summary>
    public CleanReportWindow()
    {
        InitializeComponent();
        CloseButtonIcon.Source = DifferencerStatusIcons.GetCustomIcon(34);
    }

    /// <summary>
    ///     Solution Clean の結果の概要とログを表示します。
    /// </summary>
    /// <param name="owner">オーナー ウィンドウ。</param>
    /// <param name="summary">結果の概要。</param>
    /// <param name="logPath">ログ ファイルのパス。</param>
    /// <param name="log">ログの内容。</param>
    internal static void Show(Window owner, string summary, string logPath, string log)
    {
        var report = new CleanReportWindow
        {
            Owner = owner,
            Title = StringOverlay.Get("Clean_ReportTitle")
        };
        report.SummaryText.Text = summary;
        report.LogPathText.Text = string.Format(StringOverlay.Get("Differencer_LogLabel"), logPath);
        report.LogBox.Text = log;
        report.LogBox.ScrollToHome();
        report.Show();
    }

    /// <summary>
    ///     ウィンドウを閉じます。
    /// </summary>
    /// <param name="sender">イベントの送信元。</param>
    /// <param name="e">イベントデータ。</param>
    private void Close_Click(object sender, RoutedEventArgs e)
    {
        Close();
    }
}