using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace CMBuyerStudio.Domain.WantedCards;

public class WantedCardGroup : INotifyPropertyChanged
{
    private string _cardName = string.Empty;
    private int _desiredQuantity;

    public string CardName
    {
        get => _cardName;
        set => SetField(ref _cardName, value);
    }

    public int DesiredQuantity
    {
        get => _desiredQuantity;
        set => SetField(ref _desiredQuantity, value);
    }

    public ObservableCollection<WantedCardVariant> Variants { get; set; } = new();

    public event PropertyChangedEventHandler? PropertyChanged;

    private void OnPropertyChanged([CallerMemberName] string? propertyName = null)
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }

    private bool SetField<T>(ref T field, T value, [CallerMemberName] string? propertyName = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value))
            return false;

        field = value;
        OnPropertyChanged(propertyName);
        return true;
    }
}