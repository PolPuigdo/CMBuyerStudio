using CMBuyerStudio.Application.Abstractions;
using CMBuyerStudio.Application.RunAnalysis;
using CMBuyerStudio.Desktop.Commands;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Input;

namespace CMBuyerStudio.Desktop.ViewModels;

public sealed class RunBestSellerViewModel : ViewModelBase
{
    private readonly IRunAnalysisService _runService;
    private readonly WantedCardsViewModel _wantedCardsViewModel;
    private CancellationTokenSource? _cts;
    private string? _euReportPath;
    private string? _localReportPath;

    public ICommand RunCommand { get; }
    public ICommand CancelCommand { get; }
    public ICommand OpenEuReportCommand { get; }
    public ICommand OpenLocalReportCommand { get; }

    public ObservableCollection<RunStepViewModel> Steps { get; } = new();

    public RunBestSellerViewModel(
        IRunAnalysisService runService,
        WantedCardsViewModel wantedCardsViewModel)
    {
        _runService = runService;
        _wantedCardsViewModel = wantedCardsViewModel;

        RunCommand = new RelayCommand(async _ => await RunAsync());
        CancelCommand = new RelayCommand(_ => Cancel());
        OpenEuReportCommand = new RelayCommand(_ => OpenReport(_euReportPath));
        OpenLocalReportCommand = new RelayCommand(_ => OpenReport(_localReportPath));

        InitializeSteps();
        ReportsStatusText = "No reports generated yet.";

        _wantedCardsViewModel.PropertyChanged += OnWantedCardsViewModelPropertyChanged;
        TotalWantedCards = _wantedCardsViewModel.TotalGroups;
    }

    #region Properties

    private int _progressValue;
    public int ProgressValue
    {
        get => _progressValue;
        set
        {
            if (SetProperty(ref _progressValue, value))
            {
                OnPropertyChanged(nameof(ProgressText));
            }
        }
    }

    private int _progressMaximum;
    public int ProgressMaximum
    {
        get => _progressMaximum;
        set
        {
            if (SetProperty(ref _progressMaximum, value))
            {
                OnPropertyChanged(nameof(ProgressText));
            }
        }
    }

    public string ProgressText => $"{CalculateProgressPercentage(ProgressValue, ProgressMaximum)}%";

    private string _statusText = "";
    public string StatusText
    {
        get => _statusText;
        set => SetProperty(ref _statusText, value);
    }

    private string _detailText = "";
    public string DetailText
    {
        get => _detailText;
        set => SetProperty(ref _detailText, value);
    }

    private bool _canRun = true;
    public bool CanRun
    {
        get => _canRun;
        set => SetProperty(ref _canRun, value);
    }

    public string RunButtonText => "Calculate Best Sellers";

    private bool _canCancel;
    public bool CanCancel
    {
        get => _canCancel;
        set => SetProperty(ref _canCancel, value);
    }

    private string _reportsStatusText = "";
    public string ReportsStatusText
    {
        get => _reportsStatusText;
        set => SetProperty(ref _reportsStatusText, value);
    }

    private int _totalWantedCards;
    public int TotalWantedCards
    {
        get => _totalWantedCards;
        private set
        {
            if (SetProperty(ref _totalWantedCards, value))
            {
                OnPropertyChanged(nameof(TotalWantedCardsText));
            }
        }
    }

    public string TotalWantedCardsText
        => TotalWantedCards == 1 ? "1 card" : $"{TotalWantedCards} cards";

    private string _euTotalText = "Pending";
    public string EuTotalText
    {
        get => _euTotalText;
        private set => SetProperty(ref _euTotalText, value);
    }

    private string _localTotalText = "Pending";
    public string LocalTotalText
    {
        get => _localTotalText;
        private set => SetProperty(ref _localTotalText, value);
    }

    public bool CanOpenEuReport => HasExistingReport(_euReportPath);

    public bool CanOpenLocalReport => HasExistingReport(_localReportPath);

    #endregion

    private async Task RunAsync()
    {
        _cts = new CancellationTokenSource();
        ResetReportState();
        ResetCalculationTotals();

        CanRun = false;
        CanCancel = true;
        ReportsStatusText = "Generating reports...";

        var progress = new Progress<RunProgressEvent>(HandleProgress);

        try
        {
            await _runService.RunAsync(progress, _cts.Token);
        }
        finally
        {
            CanRun = true;
            CanCancel = false;
        }
    }

    private void Cancel()
    {
        _cts?.Cancel();
    }

    private void InitializeSteps()
    {
        Steps.Add(new RunStepViewModel("Scraping", "Scraping card offers"));
        Steps.Add(new RunStepViewModel("EU Calculation", "Optimizing EU sellers"));
        Steps.Add(new RunStepViewModel("Local Calculation", "Optimizing local sellers"));
        Steps.Add(new RunStepViewModel("Reports", "Generating HTML reports"));
    }

    private void HandleProgress(RunProgressEvent e)
    {
        switch (e)
        {
            case RunStartedEvent started:
                ProgressMaximum = 100;
                ProgressValue = started.Progress;
                StatusText = "Getting Target Cards...";
                break;

            case RecoverCacheStartEvent rcEvent:
                ProgressValue = rcEvent.Progress;
                StatusText = "Recovering Cache...";
                break;

            case RecoverCacheCompletedEvent rcEvent:
                ProgressValue = rcEvent.Progress;
                break;

            case ProxyCheckStartEvent scraping:
                StatusText = $"Checking Available Connection Proxies...";
                ProgressValue = scraping.Progress;
                break;

            case CardScrapingStartedEvent scraping:
                StatusText = $"Scraping...";
                ProgressValue = scraping.Progress;
                SetStep("Scraping", StepStatus.Running);
                break;

            case CardScrapedEvent scraping:
                ProgressValue = scraping.Progress;
                SetStep("Scraping", StepStatus.Completed);
                break;

            case BuildPhasesStartEvent:
                StatusText = $"Building Phases...";
                break;

            case PurgeStartEvent purge:
                StatusText = $"Purging Sellers...";
                ProgressValue = purge.Progress;
                break;

            case EUCalculationStartEvent euCalc:
                StatusText = $"Calculation Best Seller (Europe)...";
                ProgressValue = euCalc.Progress;
                SetStep("EU Calculation", StepStatus.Running);
                break;

            case EUCalculationCompleteEvent euCalc:
                ProgressValue = euCalc.Progress;
                EuTotalText = FormatEuroPrice(euCalc.TotalPrice);
                SetStep("EU Calculation", StepStatus.Completed);
                break;

            case LocalCalculationStartEvent localCalc:
                StatusText = $"Calculation Best Seller (Local)...";
                ProgressValue = localCalc.Progress;
                SetStep("Local Calculation", StepStatus.Running);
                break;

            case LocalCalculationCompleteEvent euCalc:
                ProgressValue = euCalc.Progress;
                LocalTotalText = FormatEuroPrice(euCalc.TotalPrice);
                SetStep("Local Calculation", StepStatus.Completed);
                break;

            case ReportStartEvent report:
                ProgressValue = report.Progress;
                StatusText = "Generating Reports...";
                SetStep("Reports", StepStatus.Running);
                break;

            case ReportGeneratedEvent generatedReport:
                RegisterGeneratedReport(generatedReport);
                ProgressValue = generatedReport.Progress;
                StatusText = "Done!";
                if (generatedReport.Progress == 100)
                {
                    SetStep("Reports", StepStatus.Completed);
                }
                break;
        }
    }

    private void SetStep(string title, StepStatus status)
    {
        var step = Steps.FirstOrDefault(s => s.Title == title);
        if (step != null)
        {
            step.Status = status;
        }
    }

    private void OnWantedCardsViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (string.IsNullOrEmpty(e.PropertyName) || e.PropertyName == nameof(WantedCardsViewModel.TotalGroups))
        {
            TotalWantedCards = _wantedCardsViewModel.TotalGroups;
        }
    }

    private void RegisterGeneratedReport(ReportGeneratedEvent generatedReport)
    {
        if (string.Equals(generatedReport.Scope, "EU", StringComparison.OrdinalIgnoreCase))
        {
            _euReportPath = generatedReport.Path;
            OnPropertyChanged(nameof(CanOpenEuReport));
        }
        else if (string.Equals(generatedReport.Scope, "Local", StringComparison.OrdinalIgnoreCase))
        {
            _localReportPath = generatedReport.Path;
            OnPropertyChanged(nameof(CanOpenLocalReport));
        }

        var generatedScopes = new List<string>();
        if (CanOpenEuReport)
        {
            generatedScopes.Add("EU");
        }

        if (CanOpenLocalReport)
        {
            generatedScopes.Add("Local");
        }

        ReportsStatusText = generatedScopes.Count switch
        {
            0 => "Generating reports...",
            1 => $"{generatedScopes[0]} report generated.",
            _ => "EU and Local reports generated."
        };
    }

    private void ResetReportState()
    {
        _euReportPath = null;
        _localReportPath = null;
        OnPropertyChanged(nameof(CanOpenEuReport));
        OnPropertyChanged(nameof(CanOpenLocalReport));
    }

    private void ResetCalculationTotals()
    {
        EuTotalText = "Pending";
        LocalTotalText = "Pending";
    }

    private static bool HasExistingReport(string? path)
        => !string.IsNullOrWhiteSpace(path) && File.Exists(path);

    private static int CalculateProgressPercentage(int currentValue, int maximumValue)
    {
        if (maximumValue <= 0)
        {
            return 0;
        }

        var percentage = (int)Math.Round((double)currentValue * 100 / maximumValue, MidpointRounding.AwayFromZero);
        return Math.Max(0, Math.Min(100, percentage));
    }

    private static string FormatEuroPrice(decimal value)
    {
        var culture = CultureInfo.GetCultureInfo("es-ES");
        return string.Format(culture, "{0:N2} EUR", value);
    }

    private static void OpenReport(string? reportPath)
    {
        if (!HasExistingReport(reportPath))
        {
            return;
        }

        Process.Start(new ProcessStartInfo
        {
            FileName = reportPath,
            UseShellExecute = true
        });
    }
}
