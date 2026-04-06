using CMBuyerStudio.Application.Abstractions;
using CMBuyerStudio.Desktop.Commands;
using CMBuyerStudio.Domain.Search;
using CMBuyerStudio.Domain.WantedCards;
using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Linq;
using System.Threading.Tasks;
using System.Windows.Input;

namespace CMBuyerStudio.Desktop.ViewModels;

public sealed class SearchViewModel : ViewModelBase
{
    private readonly ICardSearchService _cardSearchService;
    private readonly IWantedCardsService _wantedCardsService;
    private readonly WantedCardsViewModel _wantedCardsViewModel;

    private string _searchText = string.Empty;
    private int _selectedQuantity = 1;
    private bool _isSearching;
    private bool _isSaving;

    public ObservableCollection<SearchCardResult> Results { get; } = new();

    public string SearchText
    {
        get => _searchText;
        set
        {
            if (SetProperty(ref _searchText, value))
            {
                OnPropertyChanged(nameof(CanSearch));
            }
        }
    }

    public int SelectedQuantity
    {
        get => _selectedQuantity;
        set
        {
            var normalized = value < 1 ? 1 : value;

            if (SetProperty(ref _selectedQuantity, normalized))
            {
                OnPropertyChanged(nameof(CanSaveSelection));
            }
        }
    }

    public bool IsSearching
    {
        get => _isSearching;
        set
        {
            if (SetProperty(ref _isSearching, value))
            {
                OnPropertyChanged(nameof(CanSearch));
            }
        }
    }

    public bool IsSaving
    {
        get => _isSaving;
        set
        {
            if (SetProperty(ref _isSaving, value))
            {
                OnPropertyChanged(nameof(CanSaveSelection));
            }
        }
    }

    public int ResultsCount => Results.Count;

    public int SelectedVariantsCount => Results.Count(x => x.IsSelected);

    public bool HasResults => ResultsCount > 0;

    public bool CanSearch => !IsSearching && !string.IsNullOrWhiteSpace(SearchText);

    public bool CanSaveSelection => !IsSaving && SelectedVariantsCount > 0 && SelectedQuantity > 0;

    public ICommand SearchCommand { get; }
    public ICommand SaveSelectionCommand { get; }
    public ICommand SelectAllCommand { get; }

    public SearchViewModel(
        ICardSearchService cardSearchService,
        IWantedCardsService wantedCardsService,
        WantedCardsViewModel wantedCardsViewModel)
    {
        _cardSearchService = cardSearchService;
        _wantedCardsService = wantedCardsService;
        _wantedCardsViewModel = wantedCardsViewModel;

        SearchCommand = new RelayCommand(async _ => await SearchAsync());
        SaveSelectionCommand = new RelayCommand(async _ => await SaveSelectionAsync());
        SelectAllCommand = new RelayCommand(_ => SelectAll());

        Results.CollectionChanged += OnResultsCollectionChanged;
    }

    private void OnResultsCollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (e.OldItems != null)
        {
            foreach (SearchCardResult variant in e.OldItems)
            {
                DetachVariant(variant);
            }
        }

        if (e.NewItems != null)
        {
            foreach (SearchCardResult variant in e.NewItems)
            {
                AttachVariant(variant);
            }
        }

        RefreshResultsState();
        RefreshSelectionState();
    }

    private void AttachVariant(SearchCardResult variant)
    {
        variant.PropertyChanged += OnVariantPropertyChanged;
    }

    private void DetachVariant(SearchCardResult variant)
    {
        variant.PropertyChanged -= OnVariantPropertyChanged;
    }

    private void OnVariantPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(SearchCardResult.IsSelected))
        {
            RefreshSelectionState();
        }
    }

    private void RefreshResultsState()
    {
        OnPropertyChanged(nameof(ResultsCount));
        OnPropertyChanged(nameof(HasResults));
    }

    private void RefreshSelectionState()
    {
        OnPropertyChanged(nameof(SelectedVariantsCount));
        OnPropertyChanged(nameof(CanSaveSelection));
    }

    private async Task SearchAsync()
    {
        if (!CanSearch)
            return;

        IsSearching = true;

        try
        {
            Results.Clear();

            var results = await _cardSearchService.SearchAsync(SearchText);

            foreach (var result in results)
            {
                Results.Add(result);
            }

            RefreshResultsState();
            RefreshSelectionState();
        }
        finally
        {
            IsSearching = false;
        }
    }

    private void SelectAll()
    {
        foreach (var variant in Results)
        {
            variant.IsSelected = true;
        }

        RefreshSelectionState();
    }

    private async Task SaveSelectionAsync()
    {
        if (!CanSaveSelection)
            return;

        IsSaving = true;

        try
        {
            var searchText = SearchText?.Trim();

            var selectedVariants = Results
                .Where(v => v.IsSelected)
                .Select(v => new WantedCardVariant
                {
                    SetName = v.SetName,
                    ProductUrl = v.ProductUrl,
                    Price = v.Price
                })
                .ToList();

            if (selectedVariants.Count == 0)
                return;

            var wantedGroup = new WantedCardGroup
            {
                CardName = searchText ?? string.Empty,
                DesiredQuantity = SelectedQuantity,
                Variants = new ObservableCollection<WantedCardVariant>(selectedVariants)
            };

            await _wantedCardsService.AddOrMergeAsync(wantedGroup);
            await _wantedCardsViewModel.ReloadAsync();

            foreach (var variant in Results)
            {
                variant.IsSelected = false;
            }

            RefreshSelectionState();
        }
        finally
        {
            IsSaving = false;
        }
    }
}