using CMBuyerStudio.Application.Abstractions;
using CMBuyerStudio.Application.Models;
using CMBuyerStudio.Desktop.Commands;
using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Windows.Input;

namespace CMBuyerStudio.Desktop.ViewModels;

public sealed class SettingsViewModel : ViewModelBase
{
    private readonly IAppSettingsService _appSettingsService;
    private readonly RelayCommand _saveCommand;
    private readonly Dictionary<string, double> _additionalShippingCountries = new(StringComparer.OrdinalIgnoreCase);

    private bool _isDirty;
    private bool _isSaving;
    private bool _isLoading;
    private bool _showPassword;
    private bool _suspendDirtyTracking;

    private int _cacheTtlHours = 24;
    private double _shippingDefaultCost = 3.0;
    private string _cardmarketUsername = string.Empty;
    private string _cardmarketPassword = string.Empty;
    private int _selectedMinConditionId = 2;
    private string _statusMessage = "Loading settings...";
    private string _validationMessage = string.Empty;

    private bool _currentHeadless;
    private int _currentMaxConcurrentWorkers = 10;
    private string _currentUrlProxyChecker = string.Empty;

    public SettingsViewModel(IAppSettingsService appSettingsService)
    {
        _appSettingsService = appSettingsService;

        SellerCountryOptions = new ObservableCollection<SelectableOptionViewModel>(
            SettingsCatalog.SellerCountries.Select(item => new SelectableOptionViewModel(item.Id, item.Name)));
        LanguageOptions = new ObservableCollection<SelectableOptionViewModel>(
            SettingsCatalog.Languages.Select(item => new SelectableOptionViewModel(item.Id, item.Name)));
        MinConditionOptions = new ObservableCollection<SettingsOptionCatalogItem>(SettingsCatalog.MinConditions);

        ShippingCosts = new ObservableCollection<ShippingCostEntryViewModel>();
        Proxies = new ObservableCollection<ProxyEntryViewModel>();

        _saveCommand = new RelayCommand(async _ => await SaveAsync(), _ => CanSave);
        SaveCommand = _saveCommand;
        AddProxyCommand = new RelayCommand(_ => AddProxy());
        RemoveProxyCommand = new RelayCommand(RemoveProxy);

        HookCollectionEvents();
        HookSelectableOptions(SellerCountryOptions);
        HookSelectableOptions(LanguageOptions);

        _ = LoadAsync();
    }

    public ObservableCollection<ShippingCostEntryViewModel> ShippingCosts { get; }
    public ObservableCollection<SelectableOptionViewModel> SellerCountryOptions { get; }
    public ObservableCollection<SelectableOptionViewModel> LanguageOptions { get; }
    public ObservableCollection<SettingsOptionCatalogItem> MinConditionOptions { get; }
    public ObservableCollection<ProxyEntryViewModel> Proxies { get; }

    public ICommand SaveCommand { get; }
    public ICommand AddProxyCommand { get; }
    public ICommand RemoveProxyCommand { get; }

    public bool IsDirty
    {
        get => _isDirty;
        private set
        {
            if (SetProperty(ref _isDirty, value))
            {
                RefreshSaveState();
            }
        }
    }

    public bool IsSaving
    {
        get => _isSaving;
        private set
        {
            if (SetProperty(ref _isSaving, value))
            {
                RefreshSaveState();
            }
        }
    }

    public bool IsLoading
    {
        get => _isLoading;
        private set
        {
            if (SetProperty(ref _isLoading, value))
            {
                RefreshSaveState();
            }
        }
    }

    public bool CanSave => !IsSaving && !IsLoading && IsDirty;

    public bool ShowPassword
    {
        get => _showPassword;
        set => SetProperty(ref _showPassword, value);
    }

    public int CacheTtlHours
    {
        get => _cacheTtlHours;
        set => SetEditableProperty(ref _cacheTtlHours, value);
    }

    public double ShippingDefaultCost
    {
        get => _shippingDefaultCost;
        set => SetEditableProperty(ref _shippingDefaultCost, value);
    }

    public string CardmarketUsername
    {
        get => _cardmarketUsername;
        set => SetEditableProperty(ref _cardmarketUsername, value);
    }

    public string CardmarketPassword
    {
        get => _cardmarketPassword;
        set => SetEditableProperty(ref _cardmarketPassword, value);
    }

    public int SelectedMinConditionId
    {
        get => _selectedMinConditionId;
        set => SetEditableProperty(ref _selectedMinConditionId, value);
    }

    public string StatusMessage
    {
        get => _statusMessage;
        private set => SetProperty(ref _statusMessage, value);
    }

    public string ValidationMessage
    {
        get => _validationMessage;
        private set
        {
            if (SetProperty(ref _validationMessage, value))
            {
                OnPropertyChanged(nameof(HasValidationMessage));
            }
        }
    }

    public bool HasValidationMessage => !string.IsNullOrWhiteSpace(ValidationMessage);

    private async Task LoadAsync()
    {
        IsLoading = true;
        ValidationMessage = string.Empty;
        StatusMessage = "Loading settings...";

        try
        {
            var snapshot = await _appSettingsService.GetCurrentAsync();

            _suspendDirtyTracking = true;
            try
            {
                _currentHeadless = snapshot.Scraping.Headless;
                _currentMaxConcurrentWorkers = Math.Max(1, snapshot.Scraping.MaxConcurrentWorkers);
                _currentUrlProxyChecker = snapshot.Scraping.UrlProxyChecker;

                CacheTtlHours = Math.Max(1, snapshot.Cache.TtlHours);
                ShippingDefaultCost = Math.Max(0, snapshot.ShippingCosts.Default);
                CardmarketUsername = snapshot.Scraping.CardmarketUsername;
                CardmarketPassword = snapshot.Scraping.CardmarketPassword;
                SelectedMinConditionId = SettingsCatalog.MinConditions.Any(item => item.Id == snapshot.Scraping.MinCondition)
                    ? snapshot.Scraping.MinCondition
                    : 2;

                ApplyCsvSelection(SellerCountryOptions, snapshot.Scraping.SellerCountry);
                ApplyCsvSelection(LanguageOptions, snapshot.Scraping.Languages);
                LoadShippingCosts(snapshot.ShippingCosts);
                LoadProxies(snapshot.Scraping.Proxies);
            }
            finally
            {
                _suspendDirtyTracking = false;
            }

            ValidationMessage = string.Empty;
            StatusMessage = "Settings loaded.";
            IsDirty = false;
        }
        catch (Exception ex)
        {
            ValidationMessage = ex.Message;
            StatusMessage = "Could not load settings.";
            IsDirty = false;
        }
        finally
        {
            IsLoading = false;
        }
    }

    private async Task SaveAsync()
    {
        if (!CanSave)
        {
            return;
        }

        var validationError = Validate();
        if (!string.IsNullOrWhiteSpace(validationError))
        {
            ValidationMessage = validationError;
            StatusMessage = "Fix validation errors before saving.";
            return;
        }

        IsSaving = true;
        ValidationMessage = string.Empty;
        StatusMessage = "Saving settings...";

        try
        {
            var snapshot = BuildSnapshotForSave();
            await _appSettingsService.SaveAsync(snapshot);

            IsDirty = false;
            StatusMessage = "Settings saved.";
        }
        catch (Exception ex)
        {
            ValidationMessage = ex.Message;
            StatusMessage = "Could not save settings.";
        }
        finally
        {
            IsSaving = false;
        }
    }

    private AppSettingsSnapshot BuildSnapshotForSave()
    {
        var shippingCountries = new Dictionary<string, double>(_additionalShippingCountries, StringComparer.OrdinalIgnoreCase);

        foreach (var shipping in ShippingCosts)
        {
            shippingCountries[shipping.Country] = shipping.Cost;
        }

        return new AppSettingsSnapshot
        {
            Cache = new CacheSettingsSnapshot
            {
                TtlHours = CacheTtlHours
            },
            ShippingCosts = new ShippingCostsSettingsSnapshot
            {
                Default = ShippingDefaultCost,
                Countries = shippingCountries
            },
            Scraping = new ScrapingSettingsSnapshot
            {
                Headless = _currentHeadless,
                MaxConcurrentWorkers = _currentMaxConcurrentWorkers,
                UrlProxyChecker = _currentUrlProxyChecker,
                CardmarketUsername = CardmarketUsername.Trim(),
                CardmarketPassword = CardmarketPassword,
                SellerCountry = BuildCsv(SellerCountryOptions),
                Languages = BuildCsv(LanguageOptions),
                MinCondition = SelectedMinConditionId,
                Proxies = Proxies.Select(proxy => new ProxySettingsSnapshot
                {
                    Server = proxy.Server.Trim(),
                    Username = string.IsNullOrWhiteSpace(proxy.Username) ? string.Empty : proxy.Username.Trim(),
                    Password = string.IsNullOrWhiteSpace(proxy.Password) ? string.Empty : proxy.Password
                }).ToList()
            }
        };
    }

    private string Validate()
    {
        if (CacheTtlHours < 1)
        {
            return "Cache TTL must be at least 1 hour.";
        }

        if (ShippingDefaultCost < 0)
        {
            return "Shipping default cost must be >= 0.";
        }

        foreach (var shipping in ShippingCosts)
        {
            if (shipping.Cost < 0)
            {
                return $"Shipping cost for {shipping.Country} must be >= 0.";
            }
        }

        if (!SellerCountryOptions.Any(option => option.IsSelected))
        {
            return "Select at least one seller country.";
        }

        if (!LanguageOptions.Any(option => option.IsSelected))
        {
            return "Select at least one language.";
        }

        if (!MinConditionOptions.Any(option => option.Id == SelectedMinConditionId))
        {
            return "Select a valid minimum condition.";
        }

        foreach (var proxy in Proxies)
        {
            if (string.IsNullOrWhiteSpace(proxy.Server))
            {
                return "Each proxy requires Server (url+port).";
            }
        }

        return string.Empty;
    }

    private void LoadShippingCosts(ShippingCostsSettingsSnapshot shipping)
    {
        _additionalShippingCountries.Clear();

        foreach (var pair in shipping.Countries)
        {
            if (!SettingsCatalog.ShippingCountries.Contains(pair.Key, StringComparer.OrdinalIgnoreCase))
            {
                _additionalShippingCountries[pair.Key] = pair.Value;
            }
        }

        ShippingCosts.Clear();

        foreach (var country in SettingsCatalog.ShippingCountries)
        {
            var value = shipping.Countries.TryGetValue(country, out var configuredValue)
                ? configuredValue
                : shipping.Default;
            ShippingCosts.Add(new ShippingCostEntryViewModel(country, value));
        }
    }

    private void LoadProxies(IReadOnlyList<ProxySettingsSnapshot> proxies)
    {
        Proxies.Clear();

        foreach (var proxy in proxies)
        {
            Proxies.Add(new ProxyEntryViewModel
            {
                Server = proxy.Server,
                Username = proxy.Username ?? string.Empty,
                Password = proxy.Password ?? string.Empty
            });
        }
    }

    private static void ApplyCsvSelection(IEnumerable<SelectableOptionViewModel> options, string csv)
    {
        var selectedIds = ParseCsvIds(csv);

        foreach (var option in options)
        {
            option.IsSelected = selectedIds.Contains(option.Id);
        }
    }

    private static HashSet<int> ParseCsvIds(string csv)
    {
        var result = new HashSet<int>();

        if (string.IsNullOrWhiteSpace(csv))
        {
            return result;
        }

        var parts = csv.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        foreach (var part in parts)
        {
            if (int.TryParse(part, NumberStyles.Integer, CultureInfo.InvariantCulture, out var value))
            {
                result.Add(value);
            }
        }

        return result;
    }

    private static string BuildCsv(IEnumerable<SelectableOptionViewModel> options)
    {
        return string.Join(
            ",",
            options
                .Where(option => option.IsSelected)
                .Select(option => option.Id.ToString(CultureInfo.InvariantCulture)));
    }

    private void AddProxy()
    {
        Proxies.Add(new ProxyEntryViewModel());
    }

    private void RemoveProxy(object? parameter)
    {
        if (parameter is not ProxyEntryViewModel proxy)
        {
            return;
        }

        Proxies.Remove(proxy);
    }

    private bool SetEditableProperty<T>(ref T field, T value, [CallerMemberName] string? propertyName = null)
    {
        if (!SetProperty(ref field, value, propertyName))
        {
            return false;
        }

        MarkDirty();
        return true;
    }

    private void HookCollectionEvents()
    {
        ShippingCosts.CollectionChanged += OnCollectionChanged;
        Proxies.CollectionChanged += OnCollectionChanged;
    }

    private void HookSelectableOptions(IEnumerable<SelectableOptionViewModel> options)
    {
        foreach (var option in options)
        {
            option.PropertyChanged += OnEntryPropertyChanged;
        }
    }

    private void OnCollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (e.OldItems is not null)
        {
            foreach (var item in e.OldItems.OfType<INotifyPropertyChanged>())
            {
                item.PropertyChanged -= OnEntryPropertyChanged;
            }
        }

        if (e.NewItems is not null)
        {
            foreach (var item in e.NewItems.OfType<INotifyPropertyChanged>())
            {
                item.PropertyChanged += OnEntryPropertyChanged;
            }
        }

        MarkDirty();
    }

    private void OnEntryPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        MarkDirty();
    }

    private void MarkDirty()
    {
        if (_suspendDirtyTracking)
        {
            return;
        }

        IsDirty = true;
    }

    private void RefreshSaveState()
    {
        OnPropertyChanged(nameof(CanSave));
        _saveCommand.RaiseCanExecuteChanged();
    }
}
