using CMBuyerStudio.Application.Abstractions;
using CMBuyerStudio.Desktop.Commands;
using CMBuyerStudio.Domain.WantedCards;
using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Linq;
using System.Threading.Tasks;
using System.Windows.Input;

namespace CMBuyerStudio.Desktop.ViewModels;

public class WantedCardsViewModel : ViewModelBase
{
    private readonly IWantedCardsRepository _wantedCardsRepository;
    private bool _isInitializing;

    public ObservableCollection<WantedCardGroup> Groups { get; set; } = new();

    public int TotalGroups => Groups.Count;

    public ICommand RemoveVariantCommand { get; }
    public ICommand DeleteGroupCommand { get; }
    public ICommand ClearAllCommand { get; }

    public WantedCardsViewModel(IWantedCardsRepository wantedCardsRepository)
    {
        _wantedCardsRepository = wantedCardsRepository;

        RemoveVariantCommand = new RelayCommand(p => RemoveVariant(p));
        DeleteGroupCommand = new RelayCommand(p => DeleteGroup(p));
        ClearAllCommand = new RelayCommand(_ => ClearAll());

        Groups.CollectionChanged += OnGroupsCollectionChanged;
    }

    public async Task InitializeAsync()
    {
        _isInitializing = true;

        try
        {
            Groups.Clear();

            var groups = await _wantedCardsRepository.GetAllAsync();

            foreach (var group in groups)
            {
                Groups.Add(group);
            }

            OnPropertyChanged(nameof(TotalGroups));
        }
        finally
        {
            _isInitializing = false;
        }
    }

    private void OnGroupsCollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (e.OldItems != null)
        {
            foreach (WantedCardGroup group in e.OldItems)
            {
                DetachGroup(group);
            }
        }

        if (e.NewItems != null)
        {
            foreach (WantedCardGroup group in e.NewItems)
            {
                AttachGroup(group);
            }
        }

        OnPropertyChanged(nameof(TotalGroups));

        if (!_isInitializing)
        {
            _ = SaveAsync();
        }
    }

    private void AttachGroup(WantedCardGroup group)
    {
        group.PropertyChanged += OnGroupPropertyChanged;
        group.Variants.CollectionChanged += OnVariantsChanged;
    }

    private void DetachGroup(WantedCardGroup group)
    {
        group.PropertyChanged -= OnGroupPropertyChanged;
        group.Variants.CollectionChanged -= OnVariantsChanged;
    }

    private void OnGroupPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (_isInitializing)
            return;

        if (e.PropertyName == nameof(WantedCardGroup.DesiredQuantity))
        {
            _ = SaveAsync();
        }
    }

    private void OnVariantsChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (_isInitializing)
            return;

        _ = SaveAsync();
    }

    private async Task SaveAsync()
    {
        await _wantedCardsRepository.SaveAllAsync(Groups);
    }

    private void RemoveVariant(object? parameter)
    {
        if (parameter is not WantedCardVariant variant)
            return;

        var group = Groups.FirstOrDefault(g => g.Variants.Contains(variant));
        if (group == null)
            return;

        group.Variants.Remove(variant);

        if (group.Variants.Count == 0)
        {
            Groups.Remove(group);
        }
    }

    private void DeleteGroup(object? parameter)
    {
        if (parameter is not WantedCardGroup group)
            return;

        Groups.Remove(group);
    }

    private void ClearAll()
    {
        Groups.Clear();
    }

    //public void AddOrMergeGroup(WantedCardGroup incomingGroup)
    //{
    //    var existingGroup = Groups.FirstOrDefault(g => g.CardName == incomingGroup.CardName);

    //    if (existingGroup == null)
    //    {
    //        Groups.Add(incomingGroup);
    //        return;
    //    }

    //    existingGroup.DesiredQuantity += incomingGroup.DesiredQuantity;

    //    foreach (var variant in incomingGroup.Variants)
    //    {
    //        var exists = existingGroup.Variants.Any(v =>
    //            v.SetName == variant.SetName &&
    //            v.ProductUrl == variant.ProductUrl);

    //        if (!exists)
    //        {
    //            existingGroup.Variants.Add(variant);
    //        }
    //    }
    //}

    public async Task ReloadAsync()
    {
        await InitializeAsync();
    }
}