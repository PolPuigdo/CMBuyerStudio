namespace CMBuyerStudio.Desktop.ViewModels;

public sealed class SelectableOptionViewModel : ViewModelBase
{
    private bool _isSelected;

    public SelectableOptionViewModel(int id, string name)
    {
        Id = id;
        Name = name;
    }

    public int Id { get; }

    public string Name { get; }

    public bool IsSelected
    {
        get => _isSelected;
        set => SetProperty(ref _isSelected, value);
    }
}

public sealed class ShippingCostEntryViewModel : ViewModelBase
{
    private double _cost;

    public ShippingCostEntryViewModel(string country, double cost)
    {
        Country = country;
        _cost = cost;
    }

    public string Country { get; }

    public double Cost
    {
        get => _cost;
        set => SetProperty(ref _cost, value);
    }
}

public sealed class ProxyEntryViewModel : ViewModelBase
{
    private string _server = string.Empty;
    private string _username = string.Empty;
    private string _password = string.Empty;

    public string Server
    {
        get => _server;
        set => SetProperty(ref _server, value);
    }

    public string Username
    {
        get => _username;
        set => SetProperty(ref _username, value);
    }

    public string Password
    {
        get => _password;
        set => SetProperty(ref _password, value);
    }
}
