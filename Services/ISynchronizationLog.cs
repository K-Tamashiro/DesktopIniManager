namespace DesktopIniManager.Services;

internal interface ISynchronizationLog
{
    /// <summary>
    ///     同期ログに 1 行追加します。
    /// </summary>
    /// <param name="line">追加する行。</param>
    void AppendLine(string line);

    /// <summary>
    ///     同期処理の完了と結果の件数を表示します。
    /// </summary>
    /// <param name="succeeded">成功した件数。</param>
    /// <param name="failed">失敗した件数。</param>
    /// <param name="locked">ロックされていた件数。</param>
    void Complete(int succeeded, int failed, int locked);

    /// <summary>
    ///     ログ ウィンドウをアクティブにします。
    /// </summary>
    void Activate();
}