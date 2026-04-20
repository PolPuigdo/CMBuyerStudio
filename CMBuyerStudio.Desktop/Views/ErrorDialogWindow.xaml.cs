using System.Windows;
using System.Windows.Input;

namespace CMBuyerStudio.Desktop.Views;

public partial class ErrorDialogWindow : Window
{
    private bool _isDetailsVisible;

    public ErrorDialogWindow(
        string title,
        string summary,
        string details,
        string? logPath,
        string closeButtonText)
    {
        InitializeComponent();

        TitleTextBlock.Text = string.IsNullOrWhiteSpace(title) ? "Unexpected error" : title.Trim();
        SummaryTextBlock.Text = summary ?? string.Empty;
        DetailsTextBox.Text = details ?? string.Empty;
        CloseActionButton.Content = string.IsNullOrWhiteSpace(closeButtonText) ? "Close" : closeButtonText.Trim();

        if (string.IsNullOrWhiteSpace(logPath))
        {
            LogPathTextBlock.Visibility = Visibility.Collapsed;
            LogPathTextBlock.Text = string.Empty;
        }
        else
        {
            LogPathTextBlock.Visibility = Visibility.Visible;
            LogPathTextBlock.Text = $"Log file: {logPath}";
        }

        if (string.IsNullOrWhiteSpace(details))
        {
            ToggleDetailsButton.Visibility = Visibility.Collapsed;
            CopyDetailsButton.Visibility = Visibility.Collapsed;
            DetailsHost.Visibility = Visibility.Collapsed;
        }

        Loaded += (_, _) => CloseActionButton.Focus();
    }

    private void OnToggleDetailsButtonClick(object sender, RoutedEventArgs e)
    {
        _isDetailsVisible = !_isDetailsVisible;
        DetailsHost.Visibility = _isDetailsVisible ? Visibility.Visible : Visibility.Collapsed;
        ToggleDetailsButton.Content = _isDetailsVisible ? "Hide details" : "Show details";
    }

    private void OnCopyDetailsButtonClick(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrWhiteSpace(DetailsTextBox.Text))
        {
            return;
        }

        try
        {
            Clipboard.SetText(DetailsTextBox.Text);
        }
        catch
        {
            // Ignore clipboard failures to avoid crashing the error dialog itself.
        }
    }

    private void OnCloseButtonClick(object sender, RoutedEventArgs e)
    {
        DialogResult = true;
    }

    private void OnWindowKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Escape)
        {
            return;
        }

        e.Handled = true;
        DialogResult = true;
    }
}
