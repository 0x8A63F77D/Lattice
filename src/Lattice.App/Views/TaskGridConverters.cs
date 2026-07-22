using System;
using System.Globalization;
using Avalonia.Data.Converters;
using Lattice.App.Infrastructure;
using Lattice.App.Localization;

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
/// AppTheme -> localized label (Light/Dark/System) for the Settings theme
/// ComboBox item template (design 2d/1f).
/// </summary>
public sealed class ThemeLabelConverter : IValueConverter
{
    public static readonly ThemeLabelConverter Instance = new();

    // A non-AppTheme binding value (null / UnsetValue during template init) degrades to an empty
    // label OUTSIDE the switch, so the AppTheme switch itself stays exhaustive with no `_` arm.
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is AppTheme theme ? Label(theme) : string.Empty;

#pragma warning disable CS8524 // No `_` arm on purpose: CS8509 (a new NAMED AppTheme left unhandled)
    // must stay a build error so this label mapping is revisited. CS8524 is the residual "unnamed enum
    // value" case — an out-of-range cast like (AppTheme)999, unreachable for a well-formed value — and
    // is suppressed here; a `_` arm would silence CS8509 too and defeat the guard. Same pattern as
    // RailTierProjection.
    private static string Label(AppTheme theme) => theme switch
    {
        AppTheme.Light => Strings.ThemeLight,
        AppTheme.Dark => Strings.ThemeDark,
        AppTheme.System => Strings.ThemeSystem,
    };
#pragma warning restore CS8524

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}

/// <summary>Settings language-picker label: maps <see cref="AppLanguage"/> to its display
/// label. System is localized ("System default"); English and Chinese show their endonyms
/// ("English" / "中文"), which are identical in every locale so the picker reads the same
/// regardless of the current UI language (#147). Mirrors <see cref="ThemeLabelConverter"/>.</summary>
public sealed class LanguageLabelConverter : IValueConverter
{
    public static readonly LanguageLabelConverter Instance = new();

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is AppLanguage language ? Label(language) : string.Empty;

#pragma warning disable CS8524 // No `_` arm on purpose: CS8509 (a new NAMED AppLanguage left
    // unhandled) must stay a build error so this label mapping is revisited. CS8524 is the
    // residual "unnamed enum value" case (an out-of-range cast, unreachable for a well-formed
    // value) and is suppressed here. Same pattern as ThemeLabelConverter.
    private static string Label(AppLanguage language) => language switch
    {
        AppLanguage.System => Strings.LanguageSystemDefault,
        AppLanguage.English => Strings.LanguageEnglish,
        AppLanguage.Chinese => Strings.LanguageChinese,
    };
#pragma warning restore CS8524

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}

/// <summary>
/// Named funcs consumed via <c>{x:Static v:TaskGridConverters.Member}</c> in XAML.
/// Despite the file/class name (historically Tasks-DataGrid-only), this file also holds a
/// small number of app-wide converters that aren't Tasks-DataGrid-specific: <see cref="ChevronAngle"/>
/// below (shared with the host rail's group-header chevron in ShellWindow.axaml) and
/// <see cref="ThemeLabelConverter"/> above (used by the Settings theme picker) — neither is
/// Tasks-scoped like <see cref="FractionToWidthConverter"/> and <see cref="EnumMatchConverter"/>.
/// </summary>
public static class TaskGridConverters
{
    /// <summary>Expanded flag → chevron rotation: 90° (pointing down) when expanded, 0°
    /// (pointing right) when collapsed. Backs the rail group-header disclosure chevron.</summary>
    public static readonly IValueConverter ChevronAngle =
        new FuncValueConverter<bool, double>(expanded => expanded ? 90.0 : 0.0);
}
