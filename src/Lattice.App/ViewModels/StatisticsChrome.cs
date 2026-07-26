using Avalonia.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using Lattice.App.Aggregation;
using Lattice.App.Infrastructure;

namespace Lattice.App.ViewModels;

/// <summary>One metric-switcher segment (design contract §4). Wording is Manager parity.</summary>
public sealed record StatisticsMetricOption(string Label, CreditMetric Metric);

/// <summary>
/// The host picker's entry, shown only in the "All hosts" scope (§4).
/// <para>A <see cref="RowHolder{TKey,TRow}"/> and NOT a record, on purpose (issue #175): a
/// ComboBox resolves its selection against item INSTANCES, so an option's instance must live as
/// long as its host does. The reconciler swaps <c>Data</c> (the display name) in place on a
/// rename and inserts/removes only what a fleet change actually changed — where rebuilding the
/// list wholesale silently dropped the control's selection and left the picker blank. Key is the
/// host id; Data is the display name (bound directly, so an in-place rename re-renders).</para>
/// </summary>
public sealed class StatisticsHostOption(Guid hostId, string displayName)
    : RowHolder<Guid, string>(hostId, displayName)
{
    /// <summary>The host this entry selects — the holder's immutable key.</summary>
    public Guid HostId => Key;
}

/// <summary>
/// A legend chip (§4): a colour swatch + project name whose checked state is the series'
/// visibility. Colour is keyed by daemon ordinal (never recoloured on toggle). The chip
/// notifies the ViewModel through <see cref="Toggled"/> so it can enforce the ≤6 cap and
/// rebuild the chart.
/// </summary>
public sealed partial class StatisticsLegendChip : ObservableObject
{
    public StatisticsLegendChip(string masterUrl, string name, int ordinal, IBrush swatch, bool isVisible)
    {
        MasterUrl = masterUrl;
        Name = name;
        Ordinal = ordinal;
        Swatch = swatch;
        _isVisible = isVisible;
    }

    public string MasterUrl { get; }
    public string Name { get; }
    public int Ordinal { get; }
    public IBrush Swatch { get; }

    /// <summary>Two-way bound to the chip ToggleButton. The setter tells the ViewModel.</summary>
    [ObservableProperty]
    private bool _isVisible;

    private bool _suppress;

    /// <summary>Set by the ViewModel; invoked after a USER toggle to rebuild the chart.</summary>
    public Action<StatisticsLegendChip>? Toggled { get; set; }

    /// <summary>Sync visibility from the ViewModel without re-entering <see cref="Toggled"/>.</summary>
    public void SetVisibleSilently(bool value)
    {
        _suppress = true;
        IsVisible = value;
        _suppress = false;
    }

    partial void OnIsVisibleChanged(bool value)
    {
        if (!_suppress) Toggled?.Invoke(this);
    }
}

/// <summary>
/// An overflow-flyout row (§4): the projects beyond the ≤6 chip cap. Its checkbox adds the
/// series; <see cref="CanCheck"/> disables the remaining unchecked rows once six are shown.
/// </summary>
public sealed partial class StatisticsOverflowItem : ObservableObject
{
    public StatisticsOverflowItem(string masterUrl, string name, string racText, bool isVisible, bool canCheck)
    {
        MasterUrl = masterUrl;
        Name = name;
        _racText = racText;
        _isVisible = isVisible;
        _canCheck = canCheck;
    }

    public string MasterUrl { get; }
    public string Name { get; }

    /// <summary>Current RAC in the flyout's value slot (integer, group-separated).</summary>
    [ObservableProperty]
    private string _racText;

    /// <summary>Two-way bound to the row checkbox.</summary>
    [ObservableProperty]
    private bool _isVisible;

    /// <summary>False when six series are already shown and this row is unchecked (§4 cap).</summary>
    [ObservableProperty]
    private bool _canCheck;

    private bool _suppress;

    /// <summary>Set by the ViewModel; invoked after a USER toggle to rebuild the chart.</summary>
    public Action<StatisticsOverflowItem>? Toggled { get; set; }

    /// <summary>Sync visibility from the ViewModel without re-entering <see cref="Toggled"/>.</summary>
    public void SetVisibleSilently(bool value)
    {
        _suppress = true;
        IsVisible = value;
        _suppress = false;
    }

    partial void OnIsVisibleChanged(bool value)
    {
        if (!_suppress) Toggled?.Invoke(this);
    }
}
