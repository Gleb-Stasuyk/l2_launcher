using System.Windows.Input;

namespace L2Launcher;

public sealed class RelayCommand : ICommand
{
    private readonly Func<Task> _run;
    private readonly Func<bool> _can;
    public RelayCommand(Func<Task> run, Func<bool>? can = null) { _run = run; _can = can ?? (() => true); }
    public event EventHandler? CanExecuteChanged { add => CommandManager.RequerySuggested += value; remove => CommandManager.RequerySuggested -= value; }
    public bool CanExecute(object? p) => _can();
    public async void Execute(object? p)
    {
        try { await _run(); }
        catch (Exception ex) { App.WriteCrash(ex); }
    }
}
