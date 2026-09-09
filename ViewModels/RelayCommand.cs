using System;
using System.Threading.Tasks;
using System.Windows.Input;

namespace DesktopIniManager.ViewModels
{
    public sealed class ParameterCommand : ICommand
    {
        private readonly Action<object> execute;
        private readonly Func<bool> canExecute;
        public ParameterCommand(Action<object> execute, Func<bool> canExecute = null)
        { this.execute = execute; this.canExecute = canExecute; }
        public bool CanExecute(object parameter) => canExecute?.Invoke() ?? true;
        public void Execute(object parameter) { if (CanExecute(parameter)) execute(parameter); }
        public event EventHandler CanExecuteChanged;
        public void NotifyCanExecuteChanged() => CanExecuteChanged?.Invoke(this, EventArgs.Empty);
    }

    public sealed class RelayCommand : ICommand
    {
        private readonly Action execute;
        private readonly Func<bool> canExecute;
        public RelayCommand(Action execute, Func<bool> canExecute = null)
        { this.execute = execute; this.canExecute = canExecute; }
        public bool CanExecute(object parameter) => canExecute?.Invoke() ?? true;
        public void Execute(object parameter) { if (CanExecute(parameter)) execute(); }
        public event EventHandler CanExecuteChanged;
        public void NotifyCanExecuteChanged() => CanExecuteChanged?.Invoke(this, EventArgs.Empty);
    }

    public sealed class AsyncRelayCommand : ICommand
    {
        private readonly Func<Task> execute;
        private readonly Func<bool> canExecute;
        private readonly Action<Exception> onError;
        private bool running;
        public AsyncRelayCommand(Func<Task> execute, Action<Exception> onError, Func<bool> canExecute = null)
        { this.execute = execute; this.onError = onError; this.canExecute = canExecute; }
        public bool CanExecute(object parameter) => !running && (canExecute?.Invoke() ?? true);
        public async void Execute(object parameter) => await ExecuteAsync();
        public async Task ExecuteAsync()
        {
            if (!CanExecute(null)) return;
            running = true;
            NotifyCanExecuteChanged();
            try { await execute(); }
            catch (Exception ex) { onError(ex); }
            finally { running = false; NotifyCanExecuteChanged(); }
        }
        public event EventHandler CanExecuteChanged;
        public void NotifyCanExecuteChanged() => CanExecuteChanged?.Invoke(this, EventArgs.Empty);
    }
}
