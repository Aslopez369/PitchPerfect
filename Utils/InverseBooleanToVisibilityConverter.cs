using System;
using System.Globalization;
using System.Windows;
using System.Windows.Data;

namespace PitchPerfect.Utils;

/// <summary>
/// Converts a boolean to Visibility, inverting the result.
/// True → Collapsed, False → Visible.
/// Used to show the VB-Cable warning when VB-Cable is NOT installed.
/// </summary>
public sealed class InverseBooleanToVisibilityConverter : IValueConverter
{
    /// <summary>
    /// Converts a boolean to an inverted Visibility value.
    /// </summary>
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is bool b)
        {
            return b ? Visibility.Collapsed : Visibility.Visible;
        }
        return Visibility.Visible;
    }

    /// <summary>
    /// Converts a Visibility value back to an inverted boolean.
    /// </summary>
    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is Visibility v)
        {
            return v == Visibility.Collapsed;
        }
        return false;
    }
}
