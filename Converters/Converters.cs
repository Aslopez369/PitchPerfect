using System.Globalization;
using System.Windows;
using System.Windows.Data;
using System.Windows.Media;
using PitchPerfect.Models;

namespace PitchPerfect.Converters;

/// <summary>
/// Converts a boolean to Visibility (true = Visible, false = Collapsed).
/// </summary>
[ValueConversion(typeof(bool), typeof(Visibility))]
public class BoolToVisibilityConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        return (value is bool b && b) ? Visibility.Visible : Visibility.Collapsed;
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
    {
        return value is Visibility v && v == Visibility.Visible;
    }
}

/// <summary>
/// Converts a boolean to Visibility (true = Collapsed, false = Visible) — inverse.
/// </summary>
[ValueConversion(typeof(bool), typeof(Visibility))]
public class InverseBoolToVisibilityConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        return (value is bool b && b) ? Visibility.Collapsed : Visibility.Visible;
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
    {
        return value is Visibility v && v == Visibility.Collapsed;
    }
}

/// <summary>
/// Converts a ProcessingMode enum value to Visibility based on a ConverterParameter string.
/// Usage: ConverterParameter=Global or ConverterParameter=PerApp
/// </summary>
[ValueConversion(typeof(ProcessingMode), typeof(Visibility))]
public class ModeToVisibilityConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        if (value is ProcessingMode mode && parameter is string param)
        {
            if (Enum.TryParse<ProcessingMode>(param, out var targetMode))
            {
                return mode == targetMode ? Visibility.Visible : Visibility.Collapsed;
            }
        }
        return Visibility.Collapsed;
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
    {
        throw new NotImplementedException();
    }
}

/// <summary>
/// Converts a boolean to a color: true = green, false = gray.
/// </summary>
[ValueConversion(typeof(bool), typeof(SolidColorBrush))]
public class BoolToColorConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        return (value is bool b && b)
            ? new SolidColorBrush(System.Windows.Media.Color.FromRgb(0x38, 0x8E, 0x3C))
            : new SolidColorBrush(System.Windows.Media.Color.FromRgb(0xD3, 0x2F, 0x2F));
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
    {
        throw new NotImplementedException();
    }
}

/// <summary>
/// Converts a boolean processing state to a color: true = green, false = gray.
/// </summary>
[ValueConversion(typeof(bool), typeof(SolidColorBrush))]
public class BoolToProcessingColorConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        return (value is bool b && b)
            ? new SolidColorBrush(System.Windows.Media.Color.FromRgb(0x4C, 0xAF, 0x50))
            : new SolidColorBrush(System.Windows.Media.Color.FromRgb(0xBD, 0xBD, 0xBD));
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
    {
        throw new NotImplementedException();
    }
}

/// <summary>
/// Converts a boolean processing state to text: true = "Processing...", false = "Idle".
/// </summary>
[ValueConversion(typeof(bool), typeof(string))]
public class BoolToProcessingTextConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        return (value is bool b && b) ? "正在处理中..." : "未激活";
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
    {
        throw new NotImplementedException();
    }
}
