using System.Windows.Media;

namespace DesktopIniManager.Models;

internal sealed class LanguageChoice
{
    /// <summary>
    ///     表示言語の選択肢を作成します。
    /// </summary>
    /// <param name="code">カルチャ コード。</param>
    /// <param name="flag">国旗アイコン。</param>
    /// <param name="name">表示名。</param>
    public LanguageChoice(string code, ImageSource flag, string name)
    {
        Code = code;
        Flag = flag;
        Name = name;
    }

    public string Code { get; }
    public ImageSource Flag { get; }
    public string Name { get; }
}