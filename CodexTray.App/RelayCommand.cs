using System.Windows.Input;

namespace CodexTray.App;

internal sealed class RelayCommand : ICommand
{
    private readonly Action<object?> m_Execute;

    public event EventHandler? CanExecuteChanged
    {
        add { }
        remove { }
    }

    /// <summary>
    /// Creates a command with an execute callback.
    /// </summary>
    public RelayCommand(Action<object?> execute)
    {
        m_Execute = execute;
    }

    /// <summary>
    /// Returns whether the command can execute.
    /// </summary>
    public bool CanExecute(object? parameter)
    {
        return true;
    }

    /// <summary>
    /// Runs the command callback.
    /// </summary>
    public void Execute(object? parameter)
    {
        m_Execute(parameter);
    }

}
