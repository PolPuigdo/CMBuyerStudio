using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows.Media;

namespace CMBuyerStudio.Desktop.ViewModels;

public enum StepStatus
{
    Pending,
    Running,
    Completed
}

public sealed class RunStepViewModel : INotifyPropertyChanged
{
    private StepStatus _status;

    public string Title { get; }
    public string Description { get; }

    public StepStatus Status
    {
        get => _status;
        set
        {
            _status = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(StatusText));
            OnPropertyChanged(nameof(StatusForeground));
        }
    }

    public string StatusText => Status switch
    {
        StepStatus.Pending => "Pending",
        StepStatus.Running => "Running",
        StepStatus.Completed => "Completed",
        _ => ""
    };

    public Brush StatusForeground => Status switch
    {
        StepStatus.Pending => Brushes.Gray,
        StepStatus.Running => Brushes.DeepSkyBlue,
        StepStatus.Completed => Brushes.LimeGreen,
        _ => Brushes.White
    };

    public RunStepViewModel(string title, string description)
    {
        Title = title;
        Description = description;
        Status = StepStatus.Pending;
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    private void OnPropertyChanged([CallerMemberName] string? name = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}