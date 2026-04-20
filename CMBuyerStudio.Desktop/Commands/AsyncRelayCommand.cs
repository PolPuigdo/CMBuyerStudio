using System.Windows.Input;
using CMBuyerStudio.Desktop.ErrorHandling;

namespace CMBuyerStudio.Desktop.Commands;

public sealed class AsyncRelayCommand : ICommand
{
    private readonly Func<object?, Task> _executeAsync;
    private readonly Func<object?, bool>? _canExecute;
    private readonly IExceptionHandlingService _exceptionHandlingService;
    private readonly string _source;
    private int _isExecuting;

    public AsyncRelayCommand(
        Func<object?, Task> executeAsync,
        Func<object?, bool>? canExecute,
        IExceptionHandlingService exceptionHandlingService,
        string source)
    {
        _executeAsync = executeAsync;
        _canExecute = canExecute;
        _exceptionHandlingService = exceptionHandlingService;
        _source = string.IsNullOrWhiteSpace(source) ? nameof(AsyncRelayCommand) : source.Trim();
    }

    public event EventHandler? CanExecuteChanged;

    public bool CanExecute(object? parameter)
    {
        if (Interlocked.CompareExchange(ref _isExecuting, 0, 0) != 0)
        {
            return false;
        }

        return _canExecute?.Invoke(parameter) ?? true;
    }

    public async void Execute(object? parameter)
    {
        if (!CanExecute(parameter))
        {
            return;
        }

        Interlocked.Exchange(ref _isExecuting, 1);
        RaiseCanExecuteChanged();

        try
        {
            await _executeAsync(parameter);
        }
        catch (OperationCanceledException)
        {
            // Cancellation is expected for user-initiated cancel flows.
        }
        catch (Exception exception)
        {
            _exceptionHandlingService.Handle(exception, _source);
        }
        finally
        {
            Interlocked.Exchange(ref _isExecuting, 0);
            RaiseCanExecuteChanged();
        }
    }

    public void RaiseCanExecuteChanged()
    {
        CanExecuteChanged?.Invoke(this, EventArgs.Empty);
    }
}
