using System;
using System.Threading.Tasks;
using System.Windows.Input;

namespace DesktopIniManager.ViewModels;

public sealed class ParameterCommand : ICommand
{
    private readonly Func<bool> _canExecute;
    private readonly Action<object> _execute;

    public ParameterCommand(Action<object> execute, Func<bool> canExecute = null)
    {
        _execute = execute;
        _canExecute = canExecute;
    }

    public bool CanExecute(object parameter)
    {
        return _canExecute?.Invoke() ?? true;
    }

    public void Execute(object parameter)
    {
        if (CanExecute(parameter)) _execute(parameter);
    }

    public event EventHandler CanExecuteChanged;

    public void NotifyCanExecuteChanged()
    {
        CanExecuteChanged?.Invoke(this, EventArgs.Empty);
    }
}

public sealed class RelayCommand : ICommand
{
    private readonly Func<bool> _canExecute;
    private readonly Action _execute;

    public RelayCommand(Action execute, Func<bool> canExecute = null)
    {
        _execute = execute;
        _canExecute = canExecute;
    }

    public bool CanExecute(object parameter)
    {
        return _canExecute?.Invoke() ?? true;
    }

    public void Execute(object parameter)
    {
        if (CanExecute(parameter)) _execute();
    }

    public event EventHandler CanExecuteChanged;

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

    public AsyncRelayCommand(Func<Task> execute, Action<Exception> onError, Func<bool> canExecute = null)
    {
        _execute = execute;
        _onError = onError;
        _canExecute = canExecute;
    }

    public bool CanExecute(object parameter)
    {
        return !_running && (_canExecute?.Invoke() ?? true);
    }

    public async void Execute(object parameter)
    {
        await ExecuteAsync();
    }

    public event EventHandler CanExecuteChanged;

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

    public void NotifyCanExecuteChanged()
    {
        CanExecuteChanged?.Invoke(this, EventArgs.Empty);
    }
}