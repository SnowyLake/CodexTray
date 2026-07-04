using System.Windows.Input;

namespace CodexMonitor.App;

internal sealed class RelayCommand : ICommand
{
    private readonly Action<object?> m_Execute;
    private readonly Predicate<object?>? m_CanExecute;

    public event EventHandler? CanExecuteChanged;

    /// <summary>
    /// Creates a command with an execute callback.
    /// </summary>
    public RelayCommand(Action<object?> execute, Predicate<object?>? canExecute = null)
    {
        m_Execute = execute;
        m_CanExecute = canExecute;
    }

    /// <summary>
    /// Returns whether the command can execute.
    /// </summary>
    public bool CanExecute(object? parameter)
    {
        return m_CanExecute?.Invoke(parameter) ?? true;
    }

    /// <summary>
    /// Runs the command callback.
    /// </summary>
    public void Execute(object? parameter)
    {
        m_Execute(parameter);
    }

    /// <summary>
    /// Raises the command availability change event.
    /// </summary>
    public void RaiseCanExecuteChanged()
    {
        CanExecuteChanged?.Invoke(this, EventArgs.Empty);
    }
}
