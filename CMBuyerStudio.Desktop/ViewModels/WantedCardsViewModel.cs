using CMBuyerStudio.Desktop.Commands;
using CMBuyerStudio.Desktop.Models;
using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.Linq;
using System.Windows.Input;

namespace CMBuyerStudio.Desktop.ViewModels;

public class WantedCardsViewModel : ViewModelBase
{
    public ObservableCollection<WantedCardGroup> Groups { get; set; } = new();

    public int TotalGroups => Groups.Count;

    public ICommand RemoveVariantCommand { get; }
    public ICommand DeleteGroupCommand { get; }
    public ICommand ClearAllCommand { get; }

    public WantedCardsViewModel()
    {
        Groups.CollectionChanged += OnGroupsCollectionChanged;

        RemoveVariantCommand = new RelayCommand(p => RemoveVariant(p));
        DeleteGroupCommand = new RelayCommand(p => DeleteGroup(p));
        ClearAllCommand = new RelayCommand(_ => ClearAll());

        // MOCK inicial para probar binding real
        Groups.Add(new WantedCardGroup
        {
            CardName = "Falkenrath Aristocrat",
            DesiredQuantity = 2,
            Variants =
            {
                new WantedCardVariant { SetName = "Modern Masters 2017", Price = 0.05m },
                new WantedCardVariant { SetName = "Dark Ascension I AM TESTING", Price = 0.95m },
                new WantedCardVariant { SetName = "Double Masters", Price = 0.15m }
            }
        });

        Groups.Add(new WantedCardGroup
        {
            CardName = "Lightning Bolt",
            DesiredQuantity = 4,
            Variants =
            {
                new WantedCardVariant { SetName = "Magic 2010", Price = 1.20m },
                new WantedCardVariant { SetName = "Magic 2011", Price = 1.35m }
            }
        });
    }

    private void OnGroupsCollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        OnPropertyChanged(nameof(TotalGroups));
    }

    private void RemoveVariant(object? parameter)
    {
        if (parameter is WantedCardVariant variant)
        {
            var group = Groups.FirstOrDefault(g => g.Variants.Contains(variant));

            if (group == null)
                return;

            group.Variants.Remove(variant);

            if (group.Variants.Count == 0)
            {
                DeleteGroup(group);
            }
        }
    }

    private void DeleteGroup(object? parameter)
    {
        if (parameter is WantedCardGroup group)
        {
            Groups.Remove(group);
        }
    }

    private void ClearAll()
    {
        Groups.Clear();
    }
}