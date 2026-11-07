using System.Collections.Generic;
using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace DesktopIniManager.ViewModels;

public abstract class ObservableObject : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler PropertyChanged;

    /// <summary>
    ///     値が変わった場合にフィールドを更新し、プロパティの変更を通知します。
    /// </summary>
    /// <typeparam name="T">プロパティの型。</typeparam>
    /// <param name="storage">更新するフィールド。</param>
    /// <param name="value">新しい値。</param>
    /// <param name="name">プロパティ名。省略時は呼び出し元のメンバー名。</param>
    /// <returns>値が変わった場合は true。</returns>
    protected bool SetProperty<T>(ref T storage, T value, [CallerMemberName] string name = null)
    {
        if (EqualityComparer<T>.Default.Equals(storage, value)) return false;
        storage = value;
        OnPropertyChanged(name);
        return true;
    }

    /// <summary>
    ///     プロパティの変更を通知します。
    /// </summary>
    /// <param name="name">プロパティ名。省略時は呼び出し元のメンバー名。</param>
    protected void OnPropertyChanged([CallerMemberName] string name = null)
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }
}