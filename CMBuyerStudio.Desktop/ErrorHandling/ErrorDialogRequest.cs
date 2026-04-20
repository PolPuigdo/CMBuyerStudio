namespace CMBuyerStudio.Desktop.ErrorHandling;

public sealed record ErrorDialogRequest(
    string Title,
    string Summary,
    string Details,
    string CloseButtonText,
    string? LogPath,
    bool IsFatal);
