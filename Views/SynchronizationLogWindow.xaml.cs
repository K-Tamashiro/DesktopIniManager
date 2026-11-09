using DesktopIniManager.Properties;
using DesktopIniManager.Services;
using DesktopIniManager.ViewModels;
using System;
using System.Windows;

namespace DesktopIniManager.Views;

internal partial class SynchronizationLogWindow : ISynchronizationLog
{
    /// <summary>
    ///     同期/ZIP のログ ウィンドウを作成します。
    /// </summary>
    public SynchronizationLogWindow()
    {
        InitializeComponent();
        CloseButtonIcon.Source = DifferencerStatusIcons.GetCustomIcon(34);
    }

    /// <summary>
    ///     同期/ZIP のログ ウィンドウを表示し、方向と比較ルートを記録します。
    /// </summary>
    /// <param name="owner">オーナー ウィンドウ。</param>
    /// <param name="direction">処理の方向。</param>
    /// <param name="snapshot">比較スナップショット。</param>
    internal SynchronizationLogWindow(Window owner, string direction, DiffSnapshot snapshot) : this()
    {
        Owner = owner;
        Title = StringOverlay.Get("Sync_LogTitle");
        SummaryText.Text = string.Format(StringOverlay.Get("Sync_Processing"), direction);
        Show();
        AppendLine(direction);
        AppendLine(string.Format(StringOverlay.Get("Sync_LogSource"), snapshot.SourceRoot));
        AppendLine(string.Format(StringOverlay.Get("Sync_LogTarget"), snapshot.TargetRoot));
        AppendLine(new string('-', 80));
        Activate();
    }

    /// <summary>
    ///     ログに 1 行追加します。「Log: 」で始まる行はログ ファイルのパスとして表示します。
    /// </summary>
    /// <param name="line">追加する行。</param>
    public void AppendLine(string line)
    {
        if (!IsVisible) return;
        // 言語ファイルの書式から接頭辞を取り出し、ログ ファイル行を判定する。
        var logPrefix = StringOverlay.Get("Differencer_LogPath").Split("{0}")[0];
        if (line != null && logPrefix.Length > 0 && line.StartsWith(logPrefix, StringComparison.Ordinal))
            LogPathText.Text = line;
        Log.AppendText(line + Environment.NewLine);
        Log.ScrollToEnd();
    }

    /// <summary>
    ///     処理の完了と、成功・失敗・ロックの件数を表示します。
    /// </summary>
    /// <param name="succeeded">成功した件数。</param>
    /// <param name="failed">失敗した件数。</param>
    /// <param name="locked">ロックされていた件数。</param>
    public void Complete(int succeeded, int failed, int locked)
    {
        AppendLine(new string('-', 80));
        AppendLine(string.Format(StringOverlay.Get("Sync_LogComplete"), succeeded, failed, locked));
        SummaryText.Text = string.Format(StringOverlay.Get("Sync_Summary"), succeeded, failed, locked);
        Title = StringOverlay.Get("Sync_LogResultTitle");
    }

    /// <summary>
    ///     ウィンドウが表示中であればアクティブにします。
    /// </summary>
    void ISynchronizationLog.Activate()
    {
        if (IsVisible) Activate();
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