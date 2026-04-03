using CMBuyerStudio.Desktop.ViewModels;
using System.Windows.Controls;

namespace CMBuyerStudio.Desktop.Views;

public partial class WantedCardsView : UserControl
{
    public WantedCardsView()
    {
        InitializeComponent();
        DataContext = new WantedCardsViewModel();
    }
}