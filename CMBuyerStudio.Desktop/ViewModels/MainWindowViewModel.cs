using System.Windows.Input;
using CMBuyerStudio.Desktop.Commands;

namespace CMBuyerStudio.Desktop.ViewModels;

public sealed class MainWindowViewModel : ViewModelBase
{
    private ViewModelBase _currentViewModel;

    public MainWindowViewModel(
        SearchViewModel searchViewModel,
        WantedCardsViewModel wantedCardsViewModel,
        RunBestSellerViewModel runBestSellerViewModel,
        SettingsViewModel settingsViewModel,
        LogsViewModel logsViewModel)
    {
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

    public ICommand ShowSearchCommand { get; }
    public ICommand ShowWantedCardsCommand { get; }
    public ICommand ShowRunBestSellerCommand { get; }
    public ICommand ShowSettingsCommand { get; }
    public ICommand ShowLogsCommand { get; }
}