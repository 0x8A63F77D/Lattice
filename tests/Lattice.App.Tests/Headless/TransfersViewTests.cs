using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Media;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Lattice.Boinc.GuiRpc;
using Lattice.App.Infrastructure;
using Lattice.App.Localization;
using Lattice.App.Tests.Fakes;
using Lattice.App.ViewModels;
using Lattice.App.Views;
using Lattice.Core;
using Lattice.Tests;
using Xunit;

namespace Lattice.App.Tests.Headless;

// Composition root, dispatcher discipline and settle rules all come from the
// shared HostGraphFixture — see its class doc. This suite adds only the view
// hosting: which view it builds, and the window it renders in.
public class TransfersViewTests
{
    private static (HostGraphFixture Fx, Window Window, TransfersView View, TransfersViewModel Vm) MakeView(
        UiStateStore? uiState = null)
    {
        var fx = new HostGraphFixture(uiState);
        var vm = new TransfersViewModel(fx.Store, fx.Clock, fx.Density);
        var view = new TransfersView { DataContext = vm };
        // The fixture's 1280px default width matches ShellWindow's, same
        // rationale as TasksViewTests (responsive breakpoints stay clear).
        var window = fx.Host(view);
        return (fx, window, view, vm);
    }

    // Store-backed view for facts whose polls mutate rows WHILE the grid renders
    // them: hosts are registered up front and the real store → VM pipeline is
    // driven by the started manager; the fixture's queue dispatcher delivers the
    // background poll results on the test (UI) thread at Layout/Settle drains.
    private static (HostGraphFixture Fx, Window Window, TransfersView View, TransfersViewModel Vm) MakeStoreView(
        params (string Address, FakeGuiRpcClient Fake)[] hosts)
    {
        var (fx, window, view, vm) = MakeView();
        foreach (var (address, fake) in hosts)
            fx.AddHost(address, fake);
        return (fx, window, view, vm);
    }

    // The displayed rows' holders in on-screen Y order (both rows here share one
    // file name, so identity — not text — is the probe), filtered to genuinely
    // visible rows per the recycled-ghost caveat TasksViewTests documents.
    private static TransferRow[] DisplayedHolders(TransfersView view) =>
        view.Grid.GetVisualDescendants().OfType<DataGridRow>()
            .Where(r => r.DataContext is TransferRow && r.IsVisible && r.Bounds.Height > 0)
            .OrderBy(r => r.Bounds.Y)
            .Select(r => (TransferRow)r.DataContext!)
            .ToArray();

    private static TransferRowViewModel MakeRow(
        TransferUiState uiState, string statusText, string name = "file.dat", bool isUpload = false, Guid? hostId = null)
    {
        // Single hostId source: Key.HostId and Data.HostId must agree — the
        // invariant TransferRowViewModel.From establishes in production.
        var id = hostId ?? Guid.NewGuid();
        return new(
            Key: new TransferRowKey(id, "https://project.example/", name, isUpload),
            Name: name, Project: "p", DirectionText: isUpload ? Strings.TransfersUpload : Strings.TransfersDownload,
            ProgressText: "1.0 / 2.0 MB", Fraction: 0.5, SpeedText: "1.0 MB/s",
            UiState: uiState, StatusText: statusText, HostId: id, Host: "host-a");
    }

    // Paired header-count/host-column pattern, mirrored from TasksViewTests
    // (Themed_render_shows_the_nine_column_headers / Host_column_hides_when_
    // scope_is_a_single_host): pins the full design-2b header wording (File ·
    // Project · Direction · Progress · Speed · Status · Host) — design says
    // "Status", not the Tasks grid's "State" (Codex round 2 P3 on PR #45) —
    // and that the Host column (bound to IsAllHostsScope in
    // TransfersView.axaml) is present by default and is the ONLY column the
    // scope switch hides.
    [AvaloniaFact]
    public void All_hosts_scope_shows_the_seven_design_headers_including_Status()
    {
        // The seventh header is Host, shown only under genuine multi-host
        // presentation (IsAllHostsScope). Post-ScopeMachine that keys on >1
        // registered host, not on the AllHosts scope alone, so this aggregate
        // render must register two hosts to earn the Host column.
        var (fx, window, _, _) = MakeView();
        fx.AddHost("host-a", new FakeGuiRpcClient());
        fx.AddHost("host-b", new FakeGuiRpcClient());
        window.Show();
        fx.Layout();

        var expected = new[]
        {
            Strings.TransfersColFile, Strings.ColProject, Strings.TransfersColDirection,
            Strings.ColProgress, Strings.TransfersColSpeed, Strings.TransfersColStatus, Strings.ColHost,
        };
        var headers = window.GetVisualDescendants().OfType<DataGridColumnHeader>()
            .Where(h => h.IsVisible)
            .Select(h => h.Content as string)
            .Where(text => text is not null)
            .ToList();

        Assert.Equal(expected.Length, headers.Count);
        foreach (var header in expected)
            Assert.Contains(header, headers);
        // Belt-and-braces against the shared-key regression: the Tasks grid's
        // "State" wording must not leak in via Strings.ColState.
        Assert.DoesNotContain(Strings.ColState, headers);
        fx.Dispose();
    }

    [AvaloniaFact]
    public void Host_column_hides_when_scope_is_a_single_host()
    {
        var (fx, window, _, vm) = MakeView();
        window.Show();
        fx.Layout();

        vm.Scope = new ScopeSelection(Guid.NewGuid());
        fx.Layout();

        var headers = window.GetVisualDescendants().OfType<DataGridColumnHeader>()
            .Where(h => h.IsVisible)
            .Select(h => h.Content as string)
            .Where(text => text is not null)
            .ToList();
        Assert.DoesNotContain(Strings.ColHost, headers);
        Assert.Equal(6, headers.Count);
        fx.Dispose();
    }

    // Pins the retrying-row tint (design 2b): the RowClassBinder applier sets
    // the "retrying" class from TransferUiState.Retrying, and the style in
    // TransfersView.axaml paints DataGridRow.retrying's Background from the
    // warning-tint token — stamped from DataGridInfraTests' resource-resolution
    // pattern (TryGetResource against the window's actual theme).
    [AvaloniaFact]
    public void Retrying_row_carries_the_retrying_class_and_warning_tint_background()
    {
        var (fx, window, _, vm) = MakeView();
        window.Show();

        var retrying = MakeRow(TransferUiState.Retrying, "Retry in 00:30 (attempt 2)");
        var holder = new TransferRow(retrying.Key, retrying);
        vm.Rows.Add(holder);
        fx.Layout();

        var row = window.GetVisualDescendants().OfType<DataGridRow>()
            .Single(r => ReferenceEquals(r.DataContext, holder));
        Assert.Contains("retrying", row.Classes);

        Assert.True(Application.Current!.TryGetResource(
            "LatticeWarningTintBrush", window.ActualThemeVariant, out var expected));
        var expectedColor = ((SolidColorBrush)expected!).Color;
        var actual = Assert.IsAssignableFrom<ISolidColorBrush>(row.Background);
        Assert.Equal(expectedColor, actual.Color);
        fx.Dispose();
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
        var (fx, window, view, vm) = MakeView();
        window.Show();

        var initial = MakeRow(TransferUiState.Active, Strings.TransfersStatusActive);
        var holder = new TransferRow(initial.Key, initial);
        vm.Rows.Add(holder);
        fx.Layout();

        view.Grid.SelectedItem = holder;
        var dataGridRow = window.GetVisualDescendants().OfType<DataGridRow>()
            .Single(r => ReferenceEquals(r.DataContext, holder));
        Assert.DoesNotContain("retrying", dataGridRow.Classes);

        // Same holder, same Key — a keyed-reconcile Update op, not a
        // remove+insert: the row never leaves the grid, so LoadingRow never
        // re-fires for it.
        holder.Data = holder.Data with { UiState = TransferUiState.Retrying, StatusText = "Retry in 00:29 (attempt 2)" };
        fx.Layout();

        Assert.Contains("retrying", dataGridRow.Classes);
        Assert.Same(holder, view.Grid.SelectedItem);
        fx.Dispose();
    }

    // Issue #86 headline (Transfers leg): the narrow real trigger for a survivor reorder here is
    // a tie on the (project, name) sort key — an upload and a download of the same file — whose
    // snapshot order flips between polls. The old VM-ordered source replayed that as the
    // Move→Remove+Insert of the moved holder, which cleared DataGrid selection; with display
    // order view-owned the source never reorders, the tie keeps its first-seen rendered order,
    // and the selected row stays selected.
    [AvaloniaFact]
    public async Task Selected_row_survives_a_poll_that_flips_tie_order()
    {
        const double mb = 1024.0 * 1024.0;
        IReadOnlyList<FileTransfer> transfers =
        [
            TestData.MakeTransfer(name: "file.dat", isUpload: true, nbytes: 100 * mb),
            TestData.MakeTransfer(name: "file.dat", isUpload: false, nbytes: 100 * mb),
        ];
        var fake = new FakeGuiRpcClient { OnGetFileTransfers = () => Task.FromResult(transfers) };
        var (fx, window, view, vm) = MakeStoreView(("host-a", fake));
        window.Show();
        fx.Start();
        await fx.SettleAsync(() => vm.Rows.Count == 2);
        fx.Layout();

        var upload = (TransferRow)vm.Rows.Single(r => r.Key.IsUpload);
        var download = (TransferRow)vm.Rows.Single(r => !r.Key.IsUpload);
        Assert.Equal(new[] { upload, download }, DisplayedHolders(view));
        // The download is the holder the old code's Move→Remove+Insert replay
        // removed (and re-inserted), clearing its selection.
        view.Grid.SelectedItem = download;

        // Same two transfers, flipped list order; the download's progress change
        // gives the poll an observable settle signal.
        transfers =
        [
            TestData.MakeTransfer(name: "file.dat", isUpload: false, nbytes: 100 * mb, bytesXferred: 25 * mb),
            TestData.MakeTransfer(name: "file.dat", isUpload: true, nbytes: 100 * mb),
        ];
        vm.RefreshCommand.Execute(null);
        await fx.SettleAsync(() => download.Data.Fraction == 0.25);
        fx.Layout();
        Dispatcher.UIThread.RunJobs();
        fx.Layout();

        Assert.Equal(new[] { upload, download }, DisplayedHolders(view));
        Assert.Same(download, view.Grid.SelectedItem);

        await fx.DisposeAsync();
    }

    // Design 2b common case: a connected, empty-of-transfers host renders the
    // icon + title + caption empty state with no action button (unlike
    // Tasks' single-host empty state, which has no button either, but this
    // pins the copy AND the absence of a button explicitly per the plan).
    [AvaloniaFact]
    public async Task Empty_overlay_shows_icon_title_caption_and_no_button()
    {
        var (fx, window, view, vm) = MakeView();
        fx.AddHost("host-a", new FakeGuiRpcClient());
        window.Show();
        fx.Start();

        await fx.SettleAsync(() => !vm.IsLoading);
        fx.Layout();

        Assert.True(vm.IsEmpty);
        Assert.Empty(vm.Rows);
        _ = window.GetVisualDescendants().OfType<TextBlock>()
            .Single(t => t.IsVisible && t.Text == Strings.TransfersEmpty);
        _ = window.GetVisualDescendants().OfType<TextBlock>()
            .Single(t => t.IsVisible && t.Text == Strings.TransfersEmptyCaption);
        _ = view.EmptyOverlay.GetVisualDescendants().OfType<PathIcon>().Single();
        Assert.Empty(view.EmptyOverlay.GetVisualDescendants().OfType<Button>());

        await fx.DisposeAsync();
    }

    // Teardown-drain regression (shared RowClassBinder contract, pinned per
    // view per the binder's own contract-of-record in RowClassBinderTests):
    // navigating away discards the view through the ContentControl
    // DataTemplate WITHOUT changing Grid.ItemsSource, so the DataGrid never
    // fires UnloadingRow for its realized rows. Detach must drain them all.
    [AvaloniaFact]
    public void Detaching_the_view_drains_all_row_class_subscriptions()
    {
        var (fx, window, view, vm) = MakeView();
        window.Show();

        var row = MakeRow(TransferUiState.Active, Strings.TransfersStatusActive);
        vm.Rows.Add(new TransferRow(row.Key, row));
        fx.Layout();
        Assert.True(view.RowSubscriptionCount > 0, "a realized row should have subscribed");

        // Mimic the shell's navigation teardown: the view leaves the visual
        // tree while its ItemsSource stays untouched (no UnloadingRow fires).
        window.Content = null;
        fx.Layout();

        Assert.Equal(0, view.RowSubscriptionCount);
        fx.Dispose();
    }

    // Regression lock (grid-fidelity plan #57 task 7): pins the design-2b
    // default column widths so a future edit can't silently drift them.
    // File is the sole star column; the rest are pinned to the plan's pixel
    // spec.
    [AvaloniaFact]
    public void Transfers_default_column_widths_match_the_spec()
    {
        var (fx, window, _, _) = MakeView();
        window.Show();
        fx.Layout();

        var grid = window.GetVisualDescendants().OfType<DataGrid>().Single();
        Assert.False(grid.Columns[0].Width.IsStar); // File fixed (not a star — Finding A)
        double W(int i) => grid.Columns[i].Width.Value;
        Assert.Equal(200, W(0)); // File
        Assert.Equal(140, W(1)); // Project
        Assert.Equal(80, W(2)); // Direction
        Assert.Equal(190, W(3)); // Progress
        Assert.Equal(90, W(4)); // Speed
        Assert.Equal(210, W(5)); // Status
        Assert.Equal(80, W(6)); // Host
        fx.Dispose();
    }

    // Pins tabular figures (+tnum) on the Speed cell, mirroring the Tasks
    // Elapsed/Remaining columns and Projects' credit columns (#57 task 7).
    // SpeedText is a plain field on TransferRowViewModel (not computed), so
    // MakeRow's literal "1.0 MB/s" is already the deterministic probe string.
    [AvaloniaFact]
    public void Speed_cell_uses_tabular_figures()
    {
        var (fx, window, _, vm) = MakeView();
        window.Show();

        var row = MakeRow(TransferUiState.Active, Strings.TransfersStatusActive);
        vm.Rows.Add(new TransferRow(row.Key, row));
        fx.Layout();

        var grid = window.GetVisualDescendants().OfType<DataGrid>().Single();
        var tb = grid.GetVisualDescendants().OfType<TextBlock>().Single(t => t.Text == "1.0 MB/s");
        Assert.NotNull(tb.FontFeatures);
        Assert.Contains(tb.FontFeatures!, f => f.Tag == "tnum" && f.Value == 1);
        fx.Dispose();
    }
}
