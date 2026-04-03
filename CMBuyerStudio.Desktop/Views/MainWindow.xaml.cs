using System.Windows;
using CMBuyerStudio.Desktop.ViewModels;

namespace CMBuyerStudio.Desktop.Views;

public partial class MainWindow : Window
{
    public MainWindow(MainWindowViewModel viewModel)
    {
        InitializeComponent();
        DataContext = viewModel;
    }
}