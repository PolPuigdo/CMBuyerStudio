using System;
using System.Globalization;
using System.IO;
using System.Windows.Data;
using System.Windows.Media.Imaging;

namespace CMBuyerStudio.Desktop.Converters;

public sealed class ImagePathToCardImageSourceConverter : IValueConverter
{
    private static readonly Uri FallbackUri = new("pack://application:,,,/Assets/Images/cardImageNotAvailable.png", UriKind.Absolute);

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        var imagePath = value as string;

        var uri = !string.IsNullOrWhiteSpace(imagePath) && File.Exists(imagePath)
            ? new Uri(imagePath, UriKind.Absolute)
            : FallbackUri;

        return new BitmapImage(uri);
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => Binding.DoNothing;
}
