using Avalonia.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using Lattice.App.Aggregation;
using Lattice.App.Charting;
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
/// What a legend chip RENDERS (§4): the project's label and the palette slot its line holds.
/// Immutable and reconciled in place, so a poll that only re-labels a project swaps this record
/// on the existing chip instead of rebuilding the legend (#191).
/// <para><see cref="Slot"/> is <c>null</c> when the series is not on the chart: a hidden series
/// holds no colour at all (§2, issue #171), and the view draws its grey "not plotted" swatch for
/// the null <see cref="Swatch"/>. Holding the SLOT rather than the brush is what keeps this record
/// value-comparable — <see cref="StatisticsPalette.Brush"/> hands out a fresh instance per call, so
/// a brush field would compare unequal on every poll and churn the reconciler.</para>
/// </summary>
public sealed record StatisticsChipData(string Name, int Ordinal, int? Slot)
{
    /// <summary>The colour of the line this chip stands for, or <c>null</c> while it is hidden.</summary>
    public IBrush? Swatch => Slot is { } slot ? StatisticsPalette.Brush(slot) : null;
}

/// <summary>What an overflow-flyout row renders (§4): the project's label, its current RAC, and
/// whether its checkbox is still enabled under the ≤6 cap. Immutable, reconciled in place.</summary>
public sealed record StatisticsOverflowData(string Name, string RacText, bool CanCheck);

/// <summary>
/// A legend chip (§4): a colour swatch + project name whose checked state is the series'
/// visibility. The chip notifies the ViewModel through <see cref="Toggled"/> so it can enforce
/// the ≤6 cap and rebuild the chart.
/// <para>A <see cref="RowHolder{TKey,TRow}"/> keyed by master URL (#191): the legend and the
/// overflow flyout are reconciled in place like every other bound collection in the app, so a
/// late-filled project name or a live RAC reorder swaps <see cref="RowHolder{TKey,TRow}.Data"/>
/// (or moves the row) instead of raising a Reset that destroys every container — including the
/// checkbox the user may be clicking in the open flyout. <see cref="IsVisible"/> stays ON the
/// holder rather than in the data: it is the two-way binding target, and the control must be
/// snapped back when the cap refuses a check, which a value-equal record swap could not do.</para>
/// </summary>
public sealed partial class StatisticsLegendChip(string masterUrl, StatisticsChipData data)
    : RowHolder<string, StatisticsChipData>(masterUrl, data)
{
    /// <summary>The project this chip stands for — the holder's immutable key.</summary>
    public string MasterUrl => Key;

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
/// series; <see cref="StatisticsOverflowData.CanCheck"/> disables the remaining unchecked rows
/// once six are shown. Reconciled in place for the same reason as the chips (#191) — and with a
/// stronger claim: this flyout is open, under the pointer, while its rows re-rank on live RAC.
/// </summary>
public sealed partial class StatisticsOverflowItem(string masterUrl, StatisticsOverflowData data)
    : RowHolder<string, StatisticsOverflowData>(masterUrl, data)
{
    /// <summary>The project this row toggles — the holder's immutable key.</summary>
    public string MasterUrl => Key;

    /// <summary>Two-way bound to the row checkbox.</summary>
    [ObservableProperty]
    private bool _isVisible;

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
