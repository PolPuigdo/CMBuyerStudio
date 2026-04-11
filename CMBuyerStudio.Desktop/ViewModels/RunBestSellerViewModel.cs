using CMBuyerStudio.Application.Abstractions;
using CMBuyerStudio.Application.RunAnalysis;
using CMBuyerStudio.Desktop.Commands;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Input;

namespace CMBuyerStudio.Desktop.ViewModels;

public sealed class RunBestSellerViewModel : ViewModelBase
{
    private readonly IRunAnalysisService _runService;
    private CancellationTokenSource? _cts;
    private string? _euReportPath;
    private string? _localReportPath;

    public ICommand RunCommand { get; }
    public ICommand CancelCommand { get; }
    public ICommand OpenEuReportCommand { get; }
    public ICommand OpenLocalReportCommand { get; }

    public ObservableCollection<RunStepViewModel> Steps { get; } = new();

    public RunBestSellerViewModel(IRunAnalysisService runService)
    {
        _runService = runService;

        RunCommand = new RelayCommand(async _ => await RunAsync());
        CancelCommand = new RelayCommand(_ => Cancel());
        OpenEuReportCommand = new RelayCommand(_ => OpenReport(_euReportPath));
        OpenLocalReportCommand = new RelayCommand(_ => OpenReport(_localReportPath));

        InitializeSteps();
        ReportsStatusText = "No reports generated yet.";
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

    private string _reportsStatusText = "";
    public string ReportsStatusText
    {
        get => _reportsStatusText;
        set => SetProperty(ref _reportsStatusText, value);
    }

    public bool CanOpenEuReport => HasExistingReport(_euReportPath);

    public bool CanOpenLocalReport => HasExistingReport(_localReportPath);

    #endregion

    private async Task RunAsync()
    {
        _cts = new CancellationTokenSource();
        ResetReportState();

        CanRun = false;
        CanCancel = true;
        ReportsStatusText = "Generating reports...";

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

            case CalculationProfileSnapshotEvent profileSnapshot:
                DetailText = profileSnapshot.Summary;
                break;

            case CalculationProfileCompletedEvent profileCompleted:
                DetailText = profileCompleted.Summary;
                break;

            case CalculationFinishedEvent calc:
                SetStep($"{calc.Scope} Calculation", StepStatus.Completed);
                break;

            case ReportGeneratedEvent generatedReport:
                SetStep("Reports", StepStatus.Running);
                RegisterGeneratedReport(generatedReport);
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

    private static bool HasExistingReport(string? path)
        => !string.IsNullOrWhiteSpace(path) && File.Exists(path);

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
