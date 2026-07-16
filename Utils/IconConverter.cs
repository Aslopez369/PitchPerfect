using System;
using System.Globalization;
using System.Windows.Data;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace PitchPerfect.Utils;

/// <summary>
/// Converts an executable file path to an ImageSource containing the application's icon.
/// Used in the per-app session list to display each application's icon.
/// </summary>
public sealed class IconConverter : IValueConverter
{
    /// <summary>
    /// Converts an executable path string to an ImageSource.
    /// </summary>
    /// <param name="value">The executable file path.</param>
    /// <param name="targetType">The target type (ImageSource).</param>
    /// <param name="parameter">Unused.</param>
    /// <param name="culture">Unused.</param>
    /// <returns>An ImageSource with the extracted icon, or null.</returns>
    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is not string path || string.IsNullOrWhiteSpace(path))
        {
            return null;
        }

        try
        {
            using var icon = System.Drawing.Icon.ExtractAssociatedIcon(path);
            if (icon == null) return null;

            var bitmap = Imaging.CreateBitmapSourceFromHIcon(
                icon.Handle,
                System.Windows.Int32Rect.Empty,
                BitmapSizeOptions.FromWidthAndHeight(24, 24));

            // Freeze the bitmap to make it cross-thread accessible
            bitmap.Freeze();
            return bitmap;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// ConvertBack is not supported.
    /// </summary>
    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        throw new NotSupportedException("IconConverter does not support ConvertBack.");
    }
}
