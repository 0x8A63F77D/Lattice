using System;
using System.Globalization;
using Avalonia.Data.Converters;

namespace Lattice.App.Views;

/// <summary>
/// Progress-bar fill width: Fraction (0..1, or null when unknown) → pixels on the
/// fixed 56px track used by the Tasks DataGrid's Progress cell. Null maps to 0 (no
/// fill) rather than throwing — an in-progress task with no reported fraction yet
/// still renders an empty track.
/// </summary>
public sealed class FractionToWidthConverter : IValueConverter
{
    public const double TrackWidth = 56;

    public static readonly FractionToWidthConverter Instance = new();

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is double f ? f * TrackWidth : 0.0;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}

/// <summary>
/// Compares an enum-valued binding against a ConverterParameter string by name
/// (e.g. TaskStateKind.Running vs "Running"). Backs the four-stacked-PathIcon
/// per-state-kind pattern used both by the host rail (ShellWindow.axaml) and the
/// Tasks DataGrid's State cell.
/// </summary>
public sealed class EnumMatchConverter : IValueConverter
{
    public static readonly EnumMatchConverter Instance = new();

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is not null && string.Equals(value.ToString(), parameter as string, StringComparison.Ordinal);

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}
