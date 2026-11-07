using System.Windows;

namespace DesktopIniManager.Services;

internal interface IUserDialogService
{
    /// <summary>
    ///     メッセージ ボックスを表示します。
    /// </summary>
    /// <param name="message">表示するメッセージ。</param>
    /// <param name="title">ウィンドウのタイトル。</param>
    /// <param name="buttons">表示するボタン。</param>
    /// <param name="image">表示するアイコン。</param>
    /// <returns>利用者が選択した結果。</returns>
    MessageBoxResult Show(string message, string title,
        MessageBoxButton buttons = MessageBoxButton.OK, MessageBoxImage image = MessageBoxImage.None);
}

internal sealed class UserDialogService : IUserDialogService
{
    private readonly Window _owner;

    /// <summary>
    ///     オーナー ウィンドウを指定してダイアログ サービスを作成します。
    /// </summary>
    /// <param name="owner">メッセージ ボックスのオーナー ウィンドウ。</param>
    public UserDialogService(Window owner)
    {
        _owner = owner;
    }

    /// <summary>
    ///     オーナー ウィンドウが表示中であればその子として、そうでなければ単独でメッセージ ボックスを表示します。
    /// </summary>
    /// <param name="message">表示するメッセージ。</param>
    /// <param name="title">ウィンドウのタイトル。</param>
    /// <param name="buttons">表示するボタン。</param>
    /// <param name="image">表示するアイコン。</param>
    /// <returns>利用者が選択した結果。</returns>
    public MessageBoxResult Show(string message, string title,
        MessageBoxButton buttons = MessageBoxButton.OK, MessageBoxImage image = MessageBoxImage.None)
    {
        return _owner.IsVisible
            ? MessageBox.Show(_owner, message, title, buttons, image)
            : MessageBox.Show(message, title, buttons, image);
    }
}