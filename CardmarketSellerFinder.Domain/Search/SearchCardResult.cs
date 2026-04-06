using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace CMBuyerStudio.Domain.Search;

public class SearchCardResult : INotifyPropertyChanged
{
    private string _cardName = string.Empty;
    private string _setName = string.Empty;
    private string _productUrl = string.Empty;
    private decimal? _price;
    private string _imageUrl = string.Empty;
    private string _imagePath = string.Empty;
     public SearchCardResult() { }
    private bool _isSelected;

    public string CardName
    {
        get => _cardName;
        set => SetField(ref _cardName, value);
    }

    public string SetName
    {
        get => _setName;
        set => SetField(ref _setName, value);
    }

    public string ProductUrl
    {
        get => _productUrl;
        set => SetField(ref _productUrl, value);
    }

    public decimal? Price
    {
        get => _price;
        set => SetField(ref _price, value);
    }

    public string ImageUrl
    {
        get => _imageUrl;
        set => SetField(ref _imageUrl, value);
    }

    public string ImagePath
    {
        get => _imagePath;
        set => SetField(ref _imagePath, value);
    }

    public bool IsSelected
    {
        get => _isSelected;
        set => SetField(ref _isSelected, value);
    }

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