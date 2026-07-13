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
/// (e.g. TaskStateKind.Running vs "Running"). Backs the Tasks DataGrid's State
/// cell: four stacked PathIcons, exactly one visible per state kind. (The host
/// rail shares only the visual stacked-icon shape — it binds five exclusive
/// bools and does not use EnumMatchConverter. It does use ChevronAngle below.)
/// </summary>
public sealed class EnumMatchConverter : IValueConverter
{
    public static readonly EnumMatchConverter Instance = new();

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is not null && string.Equals(value.ToString(), parameter as string, StringComparison.Ordinal);

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}

/// <summary>
/// Named funcs consumed via <c>{x:Static v:TaskGridConverters.Member}</c> in XAML.
/// Despite the file/class name (historically Tasks-DataGrid-only), <see cref="ChevronAngle"/>
/// is shared with the host rail's group-header chevron in ShellWindow.axaml — it is not
/// Tasks-scoped like <see cref="FractionToWidthConverter"/> and <see cref="EnumMatchConverter"/> above.
/// </summary>
public static class TaskGridConverters
{
    /// <summary>Expanded flag → chevron rotation: 90° (pointing down) when expanded, 0°
    /// (pointing right) when collapsed. Backs the rail group-header disclosure chevron.</summary>
    public static readonly IValueConverter ChevronAngle =
        new FuncValueConverter<bool, double>(expanded => expanded ? 90.0 : 0.0);
}
