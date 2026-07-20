using System;
using System.IO;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Lattice.App.Infrastructure;
using Lattice.App.ViewModels;
using Lattice.App.Views;
using Xunit;

namespace Lattice.App.Tests.Headless;

/// <summary>
/// End-to-end column-width persistence (issue #120) on the real data views:
/// a resize writes the settled width to the shared UiStateStore, a fresh mount
/// restores it, garbage in a hand-edited config is ignored without breaking
/// layout, and the two views' same-named columns never cross-wire.
/// </summary>
public class ColumnWidthPersistenceTests
{
    // Tasks Project column: index 0, XAML default 108, MinWidth 108.
    private const int ProjectColumn = 0;
    private const double DefaultProjectWidth = 108;

    [AvaloniaFact]
    public async Task Resized_tasks_column_persists_the_settled_width()
    {
        var fx = new HostGraphFixture();
        var vm = new TasksViewModel(fx.Store, fx.Clock, fx.UiState, fx.Density, fx.Control);
        var view = new TasksView { DataContext = vm };
        var window = fx.Host(view);
        window.Show();
        fx.Layout();

        // Drag the Project column wider — the end-state of a user resize; direct
        // Width assignment is the seam the existing scrollbar-overflow test uses.
        view.Grid.Columns[ProjectColumn].Width = new Avalonia.Controls.DataGridLength(250);

        await fx.SettleAsync(() =>
            fx.UiState.Load().ColumnWidths.TryGetValue("tasks/Project", out var w) && Math.Abs(w - 250) < 0.5);

        Assert.Equal(250, fx.UiState.Load().ColumnWidths["tasks/Project"], precision: 0);
        await fx.DisposeAsync();
    }

    [AvaloniaFact]
    public async Task Fresh_mount_restores_a_persisted_width()
    {
        var fx = new HostGraphFixture();
        // Seed a persisted width as if a previous session had saved it.
        fx.UiState.Save(UiState.Default with { ColumnWidths = new() { ["tasks/Project"] = 260 } });

        var vm = new TasksViewModel(fx.Store, fx.Clock, fx.UiState, fx.Density, fx.Control);
        var view = new TasksView { DataContext = vm };
        var window = fx.Host(view);
        window.Show();
        fx.Layout();

        Assert.Equal(260, view.Grid.Columns[ProjectColumn].Width.Value, precision: 0);
        Assert.False(view.Grid.Columns[ProjectColumn].Width.IsStar);
        await fx.DisposeAsync();
    }

    [AvaloniaFact]
    public async Task Garbage_persisted_width_is_ignored_and_a_valid_sibling_still_restores()
    {
        var fx = new HostGraphFixture();
        // Hand-edited config: Project is nonsense (negative), Task is a good value.
        fx.UiState.Save(UiState.Default with
        {
            ColumnWidths = new() { ["tasks/Project"] = -50, ["tasks/Task"] = 300 },
        });

        var vm = new TasksViewModel(fx.Store, fx.Clock, fx.UiState, fx.Density, fx.Control);
        var view = new TasksView { DataContext = vm };
        var window = fx.Host(view);
        window.Show();
        fx.Layout();

        // Garbage ignored → Project sits at its XAML default; valid sibling applied.
        Assert.Equal(DefaultProjectWidth, view.Grid.Columns[ProjectColumn].Width.Value, precision: 0);
        Assert.Equal(300, view.Grid.Columns[2].Width.Value, precision: 0); // Task column
        await fx.DisposeAsync();
    }

    [AvaloniaFact]
    public async Task Transfers_project_width_does_not_cross_wire_the_tasks_project_column()
    {
        var fx = new HostGraphFixture();
        var vm = new TransfersViewModel(fx.Store, fx.Clock, fx.Density);
        var view = new TransfersView { DataContext = vm };
        var window = fx.Host(view);
        window.Show();
        fx.Layout();

        // Transfers Project is column index 1 (index 0 is File).
        view.Grid.Columns[1].Width = new Avalonia.Controls.DataGridLength(230);

        await fx.SettleAsync(() =>
            fx.UiState.Load().ColumnWidths.TryGetValue("transfers/Project", out var w) && Math.Abs(w - 230) < 0.5);

        var widths = fx.UiState.Load().ColumnWidths;
        Assert.Equal(230, widths["transfers/Project"], precision: 0);
        // The identically-named Tasks column must be untouched — keys are view-namespaced.
        Assert.False(widths.ContainsKey("tasks/Project"));
        await fx.DisposeAsync();
    }

    [AvaloniaFact]
    public async Task Detaching_the_view_drains_the_column_width_subscriptions()
    {
        var fx = new HostGraphFixture();
        var vm = new TasksViewModel(fx.Store, fx.Clock, fx.UiState, fx.Density, fx.Control);
        var view = new TasksView { DataContext = vm };
        var window = fx.Host(view);
        window.Show();
        fx.Layout();
        Assert.True(view.ColumnWidthSubscriptionCount > 0, "columns should be subscribed while attached");

        window.Content = null;
        fx.Layout();

        Assert.Equal(0, view.ColumnWidthSubscriptionCount);
        await fx.DisposeAsync();
    }
}
