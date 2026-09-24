using System.Windows.Input;

namespace SupportCaseManager.AiAssistant.App.ViewModels;

public sealed class AsyncRelayCommand : ICommand
{
    private readonly Func<Task> executeAsync;
    private readonly Func<bool>? canExecute;
    private readonly Func<bool, Task>? executionObserved;
    private bool isExecuting;

    public AsyncRelayCommand(
        Func<Task> executeAsync,
        Func<bool>? canExecute = null,
        Func<bool, Task>? executionObserved = null)
    {
        this.executeAsync = executeAsync ?? throw new ArgumentNullException(nameof(executeAsync));
        this.canExecute = canExecute;
        this.executionObserved = executionObserved;
    }

    public event EventHandler? CanExecuteChanged;

    public bool CanExecute(object? parameter)
    {
        return !isExecuting && (canExecute?.Invoke() ?? true);
    }

    public async void Execute(object? parameter)
    {
        var allowed = CanExecute(parameter);
        if (!allowed)
        {
            if (executionObserved is not null)
            {
                await executionObserved(false);
            }
            return;
        }

        try
        {
            isExecuting = true;
            RaiseCanExecuteChanged();
            if (executionObserved is not null)
            {
                await executionObserved(true);
            }
            await executeAsync();
        }
        finally
        {
            isExecuting = false;
            RaiseCanExecuteChanged();
        }
    }

    public void RaiseCanExecuteChanged()
    {
        CanExecuteChanged?.Invoke(this, EventArgs.Empty);
    }
}
