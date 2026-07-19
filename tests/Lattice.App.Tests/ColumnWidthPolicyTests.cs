using System.Collections.Generic;
using Lattice.App.ViewModels;
using Xunit;

namespace Lattice.App.Tests;

/// <summary>
/// Pure decision table for <see cref="ColumnWidthPolicy"/>: what counts as a
/// usable persisted width (garbage rejection), how a per-view+per-column key is
/// built, and which measured widths are worth writing back (never a spurious
/// default, never a no-op, never an invalid value).
/// </summary>
public class ColumnWidthPolicyTests
{
    [Theory]
    [InlineData(108.0, true)]
    [InlineData(20.0, true)]     // exactly the floor
    [InlineData(10000.0, true)]  // exactly the ceiling
    [InlineData(19.999, false)]  // below floor
    [InlineData(10000.001, false)] // above ceiling
    [InlineData(0.0, false)]
    [InlineData(-5.0, false)]    // hand-edited negative
    [InlineData(double.NaN, false)]
    [InlineData(double.PositiveInfinity, false)]
    [InlineData(double.NegativeInfinity, false)]
    public void IsValidWidth_rejects_garbage(double width, bool expected)
    {
        Assert.Equal(expected, ColumnWidthPolicy.IsValidWidth(width));
    }

    [Fact]
    public void Key_namespaces_the_column_by_view()
    {
        Assert.Equal("tasks/Project", ColumnWidthPolicy.Key("tasks", "Project"));
        // Same column tag in two views must not collide.
        Assert.NotEqual(
            ColumnWidthPolicy.Key("tasks", "Project"),
            ColumnWidthPolicy.Key("transfers", "Project"));
    }

    [Fact]
    public void TryGetRestoreWidth_returns_a_present_valid_width()
    {
        var persisted = new Dictionary<string, double> { ["tasks/Project"] = 200.0 };
        Assert.True(ColumnWidthPolicy.TryGetRestoreWidth(persisted, "tasks/Project", out var w));
        Assert.Equal(200.0, w);
    }

    [Fact]
    public void TryGetRestoreWidth_ignores_an_absent_key()
    {
        var persisted = new Dictionary<string, double>();
        Assert.False(ColumnWidthPolicy.TryGetRestoreWidth(persisted, "tasks/Project", out _));
    }

    [Theory]
    [InlineData(-5.0)]
    [InlineData(0.0)]
    [InlineData(double.NaN)]
    public void TryGetRestoreWidth_ignores_a_present_but_invalid_width(double bad)
    {
        var persisted = new Dictionary<string, double> { ["tasks/Project"] = bad };
        Assert.False(ColumnWidthPolicy.TryGetRestoreWidth(persisted, "tasks/Project", out _));
    }

    [Fact]
    public void ComputeWrites_is_empty_when_every_column_sits_at_its_default()
    {
        // Initial render: nothing persisted, every column at its XAML default.
        // No user resize happened, so nothing must be written (no default churn).
        var measured = new Dictionary<string, double> { ["tasks/Project"] = 108, ["tasks/Task"] = 230 };
        var defaults = new Dictionary<string, double> { ["tasks/Project"] = 108, ["tasks/Task"] = 230 };
        var persisted = new Dictionary<string, double>();

        var writes = ColumnWidthPolicy.ComputeWrites(measured, persisted, defaults);

        Assert.Empty(writes);
    }

    [Fact]
    public void ComputeWrites_captures_a_resize_away_from_the_default()
    {
        var measured = new Dictionary<string, double> { ["tasks/Project"] = 200, ["tasks/Task"] = 230 };
        var defaults = new Dictionary<string, double> { ["tasks/Project"] = 108, ["tasks/Task"] = 230 };
        var persisted = new Dictionary<string, double>();

        var writes = ColumnWidthPolicy.ComputeWrites(measured, persisted, defaults);

        Assert.Equal(new Dictionary<string, double> { ["tasks/Project"] = 200 }, writes);
    }

    [Fact]
    public void ComputeWrites_is_empty_when_measured_matches_what_is_already_persisted()
    {
        var measured = new Dictionary<string, double> { ["tasks/Project"] = 200 };
        var defaults = new Dictionary<string, double> { ["tasks/Project"] = 108 };
        var persisted = new Dictionary<string, double> { ["tasks/Project"] = 200 };

        Assert.Empty(ColumnWidthPolicy.ComputeWrites(measured, persisted, defaults));
    }

    [Fact]
    public void ComputeWrites_records_a_resize_back_to_the_default_over_a_persisted_value()
    {
        // Persisted 200, user drags back to the 108 default → the default must be
        // written explicitly (not left as the stale 200).
        var measured = new Dictionary<string, double> { ["tasks/Project"] = 108 };
        var defaults = new Dictionary<string, double> { ["tasks/Project"] = 108 };
        var persisted = new Dictionary<string, double> { ["tasks/Project"] = 200 };

        var writes = ColumnWidthPolicy.ComputeWrites(measured, persisted, defaults);

        Assert.Equal(new Dictionary<string, double> { ["tasks/Project"] = 108 }, writes);
    }

    [Fact]
    public void ComputeWrites_ignores_sub_epsilon_jitter()
    {
        var measured = new Dictionary<string, double> { ["tasks/Project"] = 200.3 };
        var defaults = new Dictionary<string, double> { ["tasks/Project"] = 108 };
        var persisted = new Dictionary<string, double> { ["tasks/Project"] = 200 };

        Assert.Empty(ColumnWidthPolicy.ComputeWrites(measured, persisted, defaults));
    }

    [Fact]
    public void ComputeWrites_never_writes_an_invalid_measured_width()
    {
        // A measured width that fails validation (e.g. a collapsed 0) must never
        // be persisted, even though it differs from the default.
        var measured = new Dictionary<string, double> { ["tasks/Project"] = 0 };
        var defaults = new Dictionary<string, double> { ["tasks/Project"] = 108 };
        var persisted = new Dictionary<string, double>();

        Assert.Empty(ColumnWidthPolicy.ComputeWrites(measured, persisted, defaults));
    }

    [Fact]
    public void ComputeWrites_writes_only_the_changed_columns()
    {
        var measured = new Dictionary<string, double>
        {
            ["tasks/Project"] = 200,  // changed
            ["tasks/Task"] = 230,     // default, untouched
            ["tasks/State"] = 150,    // changed
        };
        var defaults = new Dictionary<string, double>
        {
            ["tasks/Project"] = 108,
            ["tasks/Task"] = 230,
            ["tasks/State"] = 112,
        };
        var persisted = new Dictionary<string, double>();

        var writes = ColumnWidthPolicy.ComputeWrites(measured, persisted, defaults);

        Assert.Equal(
            new Dictionary<string, double> { ["tasks/Project"] = 200, ["tasks/State"] = 150 },
            writes);
    }
}
