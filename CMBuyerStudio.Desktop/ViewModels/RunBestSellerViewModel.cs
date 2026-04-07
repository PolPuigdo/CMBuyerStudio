using CMBuyerStudio.Application.Abstractions;
using CMBuyerStudio.Application.RunAnalysis;
using CMBuyerStudio.Desktop.Commands;
using System.Collections.ObjectModel;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Input;

namespace CMBuyerStudio.Desktop.ViewModels;

public sealed class RunBestSellerViewModel : ViewModelBase
{
    private readonly IRunAnalysisService _runService;
    private CancellationTokenSource? _cts;

    public ICommand RunCommand { get; }
    public ICommand CancelCommand { get; }

    public ObservableCollection<RunStepViewModel> Steps { get; } = new();

    public RunBestSellerViewModel(IRunAnalysisService runService)
    {
        _runService = runService;

        RunCommand = new RelayCommand(async _ => await RunAsync());
        CancelCommand = new RelayCommand(_ => Cancel());

        InitializeSteps();
    }

    #region Properties

    private int _progressValue;
    public int ProgressValue
    {
        get => _progressValue;
        set => SetProperty(ref _progressValue, value);
    }

    private int _progressMaximum;
    public int ProgressMaximum
    {
        get => _progressMaximum;
        set => SetProperty(ref _progressMaximum, value);
    }

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

    private bool _canCancel;
    public bool CanCancel
    {
        get => _canCancel;
        set => SetProperty(ref _canCancel, value);
    }

    #endregion

    private async Task RunAsync()
    {
        _cts = new CancellationTokenSource();

        CanRun = false;
        CanCancel = true;

        var progress = new Progress<RunProgressEvent>(HandleProgress);

        await _runService.RunAsync(progress, _cts.Token);

        CanRun = true;
        CanCancel = false;
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
                ProgressMaximum = started.TotalCards;
                ProgressValue = 0;
                StatusText = "Starting...";
                break;

            case CardScrapingStartedEvent scraping:
                StatusText = $"Scraping {scraping.CardName} ({scraping.Current}/{scraping.Total})";
                ProgressValue = scraping.Current;
                SetStep("Scraping", StepStatus.Running);
                break;

            case CalculationStartedEvent calc:
                StatusText = $"Calculating {calc.Scope}...";
                SetStep($"{calc.Scope} Calculation", StepStatus.Running);
                break;

            case CalculationFinishedEvent calc:
                SetStep($"{calc.Scope} Calculation", StepStatus.Completed);
                break;

            case ReportGeneratedEvent:
                SetStep("Reports", StepStatus.Running);
                break;

            case RunCompletedEvent:
                StatusText = "Completed!";
                SetStep("Reports", StepStatus.Completed);
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
}