using System.ComponentModel;
using System.Windows;
using System.Windows.Input;

namespace CMBuyerStudio.Desktop.Views;

public partial class ConfirmDialogWindow : Window
{
    private bool _hasExplicitResult;

    public ConfirmDialogWindow(string title, string message)
    {
        InitializeComponent();

        TitleTextBlock.Text = string.IsNullOrWhiteSpace(title) ? "Confirm" : title.Trim();
        MessageTextBlock.Text = message ?? string.Empty;

        Loaded += OnLoaded;
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        CancelButton.Focus();
    }

    private void OnConfirmButtonClick(object sender, RoutedEventArgs e)
    {
        CloseWithResult(true);
    }

    private void OnCancelButtonClick(object sender, RoutedEventArgs e)
    {
        CloseWithResult(false);
    }

    private void OnCloseButtonClick(object sender, RoutedEventArgs e)
    {
        CloseWithResult(false);
    }

    private void OnWindowKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            e.Handled = true;
            CloseWithResult(false);
        }
    }

    private void OnWindowDeactivated(object? sender, EventArgs e)
    {
        if (_hasExplicitResult || !IsLoaded)
        {
            return;
        }

        CloseWithResult(false);
    }

    private void OnWindowClosing(object? sender, CancelEventArgs e)
    {
        if (_hasExplicitResult)
        {
            return;
        }

        _hasExplicitResult = true;
        DialogResult = false;
    }

    private void CloseWithResult(bool result)
    {
        if (_hasExplicitResult)
        {
            return;
        }

        _hasExplicitResult = true;
        DialogResult = result;
    }
}
