using System.Collections.ObjectModel;

namespace CMBuyerStudio.Desktop.Models;

public class WantedCardGroup
{
    public string CardName { get; set; } = string.Empty;
    public int DesiredQuantity { get; set; }
    public ObservableCollection<WantedCardVariant> Variants { get; set; } = new();
}