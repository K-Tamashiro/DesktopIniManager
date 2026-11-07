using System;
using System.Threading.Tasks;
using System.Windows.Input;

namespace DesktopIniManager.ViewModels;

public sealed class ParameterCommand : ICommand
{
    private readonly Func<bool> _canExecute;
    private readonly Action<object> _execute;

    /// <summary>
    ///     パラメーター付きで実行するコマンドを作成します。
    /// </summary>
    /// <param name="execute">実行する処理。</param>
    /// <param name="canExecute">実行できるかどうかを判定する処理。null の場合は常に実行できます。</param>
    public ParameterCommand(Action<object> execute, Func<bool> canExecute = null)
    {
        _execute = execute;
        _canExecute = canExecute;
    }

    /// <summary>
    ///     コマンドを実行できるかどうかを判定します。
    /// </summary>
    /// <param name="parameter">コマンドのパラメーター (使用しません)。</param>
    /// <returns>実行できる場合は true。</returns>
    public bool CanExecute(object parameter)
    {
        return _canExecute?.Invoke() ?? true;
    }

    /// <summary>
    ///     実行できる場合にコマンドを実行します。
    /// </summary>
    /// <param name="parameter">処理に渡すパラメーター。</param>
    public void Execute(object parameter)
    {
        if (CanExecute(parameter)) _execute(parameter);
    }

    public event EventHandler CanExecuteChanged;

    /// <summary>
    ///     実行可否が変わったことを通知します。
    /// </summary>
    public void NotifyCanExecuteChanged()
    {
        CanExecuteChanged?.Invoke(this, EventArgs.Empty);
    }
}

public sealed class RelayCommand : ICommand
{
    private readonly Func<bool> _canExecute;
    private readonly Action _execute;

    /// <summary>
    ///     パラメーターなしで実行するコマンドを作成します。
    /// </summary>
    /// <param name="execute">実行する処理。</param>
    /// <param name="canExecute">実行できるかどうかを判定する処理。null の場合は常に実行できます。</param>
    public RelayCommand(Action execute, Func<bool> canExecute = null)
    {
        _execute = execute;
        _canExecute = canExecute;
    }

    /// <summary>
    ///     コマンドを実行できるかどうかを判定します。
    /// </summary>
    /// <param name="parameter">コマンドのパラメーター (使用しません)。</param>
    /// <returns>実行できる場合は true。</returns>
    public bool CanExecute(object parameter)
    {
        return _canExecute?.Invoke() ?? true;
    }

    /// <summary>
    ///     実行できる場合にコマンドを実行します。
    /// </summary>
    /// <param name="parameter">コマンドのパラメーター (使用しません)。</param>
    public void Execute(object parameter)
    {
        if (CanExecute(parameter)) _execute();
    }

    public event EventHandler CanExecuteChanged;

    /// <summary>
    ///     実行可否が変わったことを通知します。
    /// </summary>
    public void NotifyCanExecuteChanged()
    {
        CanExecuteChanged?.Invoke(this, EventArgs.Empty);
    }
}

public sealed class AsyncRelayCommand : ICommand
{
    private readonly Func<bool> _canExecute;
    private readonly Func<Task> _execute;
    private readonly Action<Exception> _onError;
    private bool _running;

    /// <summary>
    ///     非同期処理を実行するコマンドを作成します。
    /// </summary>
    /// <param name="execute">実行する非同期処理。</param>
    /// <param name="onError">処理で発生した例外を受け取る処理。</param>
    /// <param name="canExecute">実行できるかどうかを判定する処理。null の場合は常に実行できます。</param>
    public AsyncRelayCommand(Func<Task> execute, Action<Exception> onError, Func<bool> canExecute = null)
    {
        _execute = execute;
        _onError = onError;
        _canExecute = canExecute;
    }

    /// <summary>
    ///     実行中でなく、実行条件を満たしているかどうかを判定します。
    /// </summary>
    /// <param name="parameter">コマンドのパラメーター (使用しません)。</param>
    /// <returns>実行できる場合は true。</returns>
    public bool CanExecute(object parameter)
    {
        return !_running && (_canExecute?.Invoke() ?? true);
    }

    /// <summary>
    ///     コマンドを非同期に実行します。
    /// </summary>
    /// <param name="parameter">コマンドのパラメーター (使用しません)。</param>
    public async void Execute(object parameter)
    {
        await ExecuteAsync();
    }

    public event EventHandler CanExecuteChanged;

    /// <summary>
    ///     実行中は再実行できない状態にして処理を実行し、例外はエラー処理に渡します。
    /// </summary>
    public async Task ExecuteAsync()
    {
        if (!CanExecute(null)) return;
        _running = true;
        NotifyCanExecuteChanged();
        try
        {
            await _execute();
        }
        catch (Exception ex)
        {
            _onError(ex);
        }
        finally
        {
            _running = false;
            NotifyCanExecuteChanged();
        }
    }

    /// <summary>
    ///     実行可否が変わったことを通知します。
    /// </summary>
    public void NotifyCanExecuteChanged()
    {
        CanExecuteChanged?.Invoke(this, EventArgs.Empty);
    }
}