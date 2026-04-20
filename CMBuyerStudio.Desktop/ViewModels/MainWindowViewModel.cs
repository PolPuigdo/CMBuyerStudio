using System.Threading;
using System.Threading.Tasks;
using System.Windows.Input;
using CMBuyerStudio.Desktop.Commands;
using CMBuyerStudio.Desktop.ErrorHandling;
using CMBuyerStudio.Desktop.Extensions;
using CMBuyerStudio.Desktop.Feedback;

namespace CMBuyerStudio.Desktop.ViewModels;

public sealed class MainWindowViewModel : ViewModelBase
{
    private readonly IUserFeedbackService _userFeedbackService;
    private readonly IExceptionHandlingService _exceptionHandlingService;
    private readonly SynchronizationContext? _synchronizationContext;

    private ViewModelBase _currentViewModel;
    private bool _isToastVisible;
    private string _toastMessage = string.Empty;
    private ToastNotificationKind _toastKind = ToastNotificationKind.Success;
    private int _toastVersion;

    public MainWindowViewModel(
        SearchViewModel searchViewModel,
        WantedCardsViewModel wantedCardsViewModel,
        RunBestSellerViewModel runBestSellerViewModel,
        SettingsViewModel settingsViewModel,
        LogsViewModel logsViewModel,
        IUserFeedbackService userFeedbackService,
        IExceptionHandlingService exceptionHandlingService)
    {
        _userFeedbackService = userFeedbackService;
        _exceptionHandlingService = exceptionHandlingService;
        _synchronizationContext = SynchronizationContext.Current;

        SearchViewModel = searchViewModel;
        WantedCardsViewModel = wantedCardsViewModel;
        RunBestSellerViewModel = runBestSellerViewModel;
        SettingsViewModel = settingsViewModel;
        LogsViewModel = logsViewModel;

        ShowSearchCommand = new RelayCommand(_ => CurrentViewModel = SearchViewModel);
        ShowWantedCardsCommand = new RelayCommand(_ => CurrentViewModel = WantedCardsViewModel);
        ShowRunBestSellerCommand = new RelayCommand(_ => CurrentViewModel = RunBestSellerViewModel);
        ShowSettingsCommand = new RelayCommand(_ => CurrentViewModel = SettingsViewModel);
        ShowLogsCommand = new RelayCommand(_ => CurrentViewModel = LogsViewModel);

        _currentViewModel = SearchViewModel;

        _userFeedbackService.ToastNotified += OnToastNotified;
    }

    public SearchViewModel SearchViewModel { get; }
    public WantedCardsViewModel WantedCardsViewModel { get; }
    public RunBestSellerViewModel RunBestSellerViewModel { get; }
    public SettingsViewModel SettingsViewModel { get; }
    public LogsViewModel LogsViewModel { get; }

    public ViewModelBase CurrentViewModel
    {
        get => _currentViewModel;
        set
        {
            if (ReferenceEquals(_currentViewModel, value))
                return;

            _currentViewModel = value;
            OnPropertyChanged();
        }
    }

    public bool IsToastVisible
    {
        get => _isToastVisible;
        private set => SetProperty(ref _isToastVisible, value);
    }

    public string ToastMessage
    {
        get => _toastMessage;
        private set => SetProperty(ref _toastMessage, value);
    }

    public ToastNotificationKind ToastKind
    {
        get => _toastKind;
        private set => SetProperty(ref _toastKind, value);
    }

    public ICommand ShowSearchCommand { get; }
    public ICommand ShowWantedCardsCommand { get; }
    public ICommand ShowRunBestSellerCommand { get; }
    public ICommand ShowSettingsCommand { get; }
    public ICommand ShowLogsCommand { get; }

    private void OnToastNotified(object? sender, ToastNotification toast)
    {
        if (string.IsNullOrWhiteSpace(toast.Message))
        {
            return;
        }

        RunOnUiThread(() =>
        {
            ToastMessage = toast.Message;
            ToastKind = toast.Kind;
            IsToastVisible = true;

            _toastVersion++;
            var currentToastVersion = _toastVersion;
            HideToastAfterDelayAsync(currentToastVersion, toast.DurationMs)
                .ForgetSafe(_exceptionHandlingService, "MainWindowViewModel.HideToastAfterDelay");
        });
    }

    private async Task HideToastAfterDelayAsync(int toastVersion, int durationMs)
    {
        var normalizedDuration = durationMs > 0 ? durationMs : 2500;
        await Task.Delay(normalizedDuration);

        RunOnUiThread(() =>
        {
            if (toastVersion != _toastVersion)
            {
                return;
            }

            IsToastVisible = false;
        });
    }

    private void RunOnUiThread(Action action)
    {
        if (_synchronizationContext is null || ReferenceEquals(SynchronizationContext.Current, _synchronizationContext))
        {
            action();
            return;
        }

        _synchronizationContext.Post(_ => action(), null);
    }
}
