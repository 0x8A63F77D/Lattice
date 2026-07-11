using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Media;
using Avalonia.VisualTree;
using Lattice.App.Infrastructure;
using Lattice.App.Localization;
using Lattice.App.Tests.Fakes;
using Lattice.App.ViewModels;
using Lattice.App.Views;
using Lattice.Core;
using Lattice.Tests;
using Xunit;
using static Lattice.Tests.HeadlessLayout;

namespace Lattice.App.Tests.Headless;

public class TransfersViewTests
{
    private static (Window Window, TransfersView View, TransfersViewModel Vm, HostRegistry Registry,
        HostMonitorManager Manager, Dictionary<string, FakeGuiRpcClient> Fakes) MakeView(UiStateStore? uiState = null)
    {
        var path = Path.Combine(Path.GetTempPath(), $"lattice-test-{Guid.NewGuid():N}.json");
        var registry = new HostRegistry(new LatticeConfig(5, []), path);
        var fakes = new Dictionary<string, FakeGuiRpcClient>();
        var manager = new HostMonitorManager(registry, () => new RoutingGuiRpcClient(fakes), TimeProvider.System);
        var store = new HostStore(registry, manager, new ImmediateUiDispatcher());
        var clock = new ManualUiClock();
        uiState ??= new UiStateStore(Path.Combine(Path.GetTempPath(), $"lattice-test-{Guid.NewGuid():N}-ui.json"));
        var vm = new TransfersViewModel(store, clock, uiState);
        var view = new TransfersView { DataContext = vm };
        // 1280px matches ShellWindow's default width, same rationale as TasksViewTests.
        var window = new Window { Width = 1280, Height = 800, Content = view };
        return (window, view, vm, registry, manager, fakes);
    }

    private static HostConfig AddHost(
        HostRegistry registry, Dictionary<string, FakeGuiRpcClient> fakes, string address, FakeGuiRpcClient fake)
    {
        var host = TestData.MakeHostConfig(name: address, address: address);
        fakes[address] = fake;
        registry.AddHost(host);
        return host;
    }

    private static TransferRowViewModel MakeRow(
        TransferUiState uiState, string statusText, string name = "file.dat", bool isUpload = false, Guid? hostId = null) =>
        new(
            Key: new TransferRowKey(hostId ?? Guid.NewGuid(), "https://project.example/", name, isUpload),
            Name: name, Project: "p", DirectionText: isUpload ? Strings.TransfersUpload : Strings.TransfersDownload,
            ProgressText: "1.0 / 2.0 MB", Fraction: 0.5, SpeedText: "1.0 MB/s",
            UiState: uiState, StatusText: statusText, HostId: hostId ?? Guid.NewGuid(), Host: "host-a");

    // Pins the retrying-row tint (design 2b): the RowClassBinder applier sets
    // the "retrying" class from TransferUiState.Retrying, and the style in
    // TransfersView.axaml paints DataGridRow.retrying's Background from the
    // warning-tint token — stamped from DataGridInfraTests' resource-resolution
    // pattern (TryGetResource against the window's actual theme).
    [AvaloniaFact]
    public void Retrying_row_carries_the_retrying_class_and_warning_tint_background()
    {
        var (window, _, vm, _, _, _) = MakeView();
        window.Show();

        var retrying = MakeRow(TransferUiState.Retrying, "Retry in 00:30 (attempt 2)");
        var holder = new TransferRow(retrying.Key, retrying);
        vm.Rows.Add(holder);
        Layout(window);

        var row = window.GetVisualDescendants().OfType<DataGridRow>()
            .Single(r => ReferenceEquals(r.DataContext, holder));
        Assert.Contains("retrying", row.Classes);

        Assert.True(Application.Current!.TryGetResource(
            "LatticeWarningTintBrush", window.ActualThemeVariant, out var expected));
        var expectedColor = ((SolidColorBrush)expected!).Color;
        var actual = Assert.IsAssignableFrom<ISolidColorBrush>(row.Background);
        Assert.Equal(expectedColor, actual.Color);
        window.Close();
    }

    // Pins the row-class liveness fix (shared RowClassBinder, Task-7 regression
    // from the Tasks history): an in-place Data update does NOT re-run
    // LoadingRow (the row item's identity never changes), so classes must
    // instead track the holder's PropertyChanged. Also pins that DataGrid
    // selection survives the same in-place update, since both ride on holder
    // identity.
    [AvaloniaFact]
    public void Row_going_to_retrying_in_place_updates_the_row_class_and_keeps_selection()
    {
        var (window, view, vm, _, _, _) = MakeView();
        window.Show();

        var initial = MakeRow(TransferUiState.Active, Strings.TransfersStatusActive);
        var holder = new TransferRow(initial.Key, initial);
        vm.Rows.Add(holder);
        Layout(window);

        view.Grid.SelectedItem = holder;
        var dataGridRow = window.GetVisualDescendants().OfType<DataGridRow>()
            .Single(r => ReferenceEquals(r.DataContext, holder));
        Assert.DoesNotContain("retrying", dataGridRow.Classes);

        // Same holder, same Key — a keyed-reconcile Update op, not a
        // remove+insert: the row never leaves the grid, so LoadingRow never
        // re-fires for it.
        holder.Data = holder.Data with { UiState = TransferUiState.Retrying, StatusText = "Retry in 00:29 (attempt 2)" };
        Layout(window);

        Assert.Contains("retrying", dataGridRow.Classes);
        Assert.Same(holder, view.Grid.SelectedItem);
        window.Close();
    }

    // Design 2b common case: a connected, empty-of-transfers host renders the
    // icon + title + caption empty state with no action button (unlike
    // Tasks' single-host empty state, which has no button either, but this
    // pins the copy AND the absence of a button explicitly per the plan).
    [AvaloniaFact]
    public async Task Empty_overlay_shows_icon_title_caption_and_no_button()
    {
        var (window, view, vm, registry, manager, fakes) = MakeView();
        AddHost(registry, fakes, "host-a", new FakeGuiRpcClient());
        window.Show();
        manager.Start();

        await Wait.UntilAsync(() => !vm.IsLoading, "the first (empty) snapshot should land");
        Layout(window);

        Assert.True(vm.IsEmpty);
        Assert.Empty(vm.Rows);
        _ = window.GetVisualDescendants().OfType<TextBlock>()
            .Single(t => t.IsVisible && t.Text == Strings.TransfersEmpty);
        _ = window.GetVisualDescendants().OfType<TextBlock>()
            .Single(t => t.IsVisible && t.Text == Strings.TransfersEmptyCaption);
        _ = view.EmptyOverlay.GetVisualDescendants().OfType<PathIcon>().Single();
        Assert.Empty(view.EmptyOverlay.GetVisualDescendants().OfType<Button>());

        await manager.DisposeAsync();
        window.Close();
    }

    // Teardown-drain regression (shared RowClassBinder contract, pinned per
    // view per the binder's own contract-of-record in RowClassBinderTests):
    // navigating away discards the view through the ContentControl
    // DataTemplate WITHOUT changing Grid.ItemsSource, so the DataGrid never
    // fires UnloadingRow for its realized rows. Detach must drain them all.
    [AvaloniaFact]
    public void Detaching_the_view_drains_all_row_class_subscriptions()
    {
        var (window, view, vm, _, _, _) = MakeView();
        window.Show();

        var row = MakeRow(TransferUiState.Active, Strings.TransfersStatusActive);
        vm.Rows.Add(new TransferRow(row.Key, row));
        Layout(window);
        Assert.True(view.RowSubscriptionCount > 0, "a realized row should have subscribed");

        // Mimic the shell's navigation teardown: the view leaves the visual
        // tree while its ItemsSource stays untouched (no UnloadingRow fires).
        window.Content = null;
        Layout(window);

        Assert.Equal(0, view.RowSubscriptionCount);
        window.Close();
    }
}
