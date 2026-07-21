using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.VisualTree;
using FluentAvalonia.UI.Controls;
using Lattice.App.Aggregation;
using Lattice.App.Infrastructure;
using Lattice.App.ViewModels;

namespace Lattice.App.Views;

public partial class ProjectsView : UserControl
{
    public ProjectsView()
    {
        InitializeComponent();
        // Row DataContexts are ProjectRow HOLDERS whose Data swaps in place on
        // value-change polls (the reconciler's Update op) — LoadingRow never
        // re-fires for that, so the hierarchy classes must track the holder.
        // RowClassBinder owns the whole subscription lifecycle
        // (load/re-apply/recycle/unload/detach-drain); only the styling applier
        // — which the design 2a row heights ride on via the class styles — is
        // view-local.
        _rowBinder = RowClassBinder<ProjectRow>.Attach(Grid, static (row, holder) =>
        {
            row.Classes.Set("projectParent", holder.Data.IsParent);
            row.Classes.Set("projectChild", !holder.Data.IsParent);
        });
        // Column-width persistence (#120): same single-copy machinery as TasksView.
        // The header-less chevron column carries no Tag, so it is not persisted.
        _widthPersistence = ColumnWidthPersistence.Attach(Grid, "projects");
        // M3 control ops: wire the confirmation-dialog seam when the VM attaches
        // (the FA dialog is constructed only here, never in VM code — headless VM
        // tests fake the seam at the VM boundary).
        DataContextChanged += (_, _) => WireConfirmationHandler();
        // Right-click must move selection to the row under the pointer BEFORE the
        // ContextFlyout opens, or the menu would act on a stale selection (tunnel:
        // the DataGrid marks the event handled on the bubble path).
        Grid.AddHandler(PointerPressedEvent, OnGridPointerPressed, RoutingStrategies.Tunnel);
        // A context request that did NOT originate on a row (grid background,
        // header, scrollbar) must not open the menu at all — the flyout is attached
        // to the whole DataGrid, so without this gate it would act on the OLD
        // selection (Codex R2 P2, PR #135). The keyboard menu key is suppressed too.
        Grid.AddHandler(ContextRequestedEvent, OnGridContextRequested, RoutingStrategies.Tunnel);
    }

    // Production wiring of the VM's dialog seam (design 2.5): the VM outlives its
    // views (the shell recreates a ProjectsView per navigation), so a handler
    // installed by an EARLIER view is stale — it resolves TopLevel off a detached
    // control and would silently decline every Confirm-class op (Codex P2, PR
    // #135). Handlers carry their owning view via ViewConfirmationHandler, and
    // wiring replaces any VIEW-installed handler (newest view wins — order-
    // independent) while a fake a test installed on the VM boundary is untouched.
    private void WireConfirmationHandler()
    {
        if (DataContext is not ProjectsViewModel vm)
            return;
        if (vm.ConfirmationHandler is null
            || vm.ConfirmationHandler.Target is ViewConfirmationHandler)
            vm.ConfirmationHandler = new ViewConfirmationHandler(this).ConfirmAsync;
    }

    // The marker type doubles as the closure: delegate.Target identifies a
    // view-installed handler regardless of WHICH view installed it.
    private sealed class ViewConfirmationHandler(ProjectsView owner)
    {
        public Task<bool> ConfirmAsync(ConfirmationRequest request) =>
            TopLevel.GetTopLevel(owner) is { } top
                ? ConfirmationDialog.ConfirmAsync(top, request)
                : Task.FromResult(false); // owner detached: fail safe, decline
    }

    private void OnGridPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (!e.GetCurrentPoint(Grid).Properties.IsRightButtonPressed)
            return;
        if ((e.Source as Visual)?.FindAncestorOfType<DataGridRow>(includeSelf: true) is { } row)
            Grid.SelectedItem = row.DataContext;
    }

    private void OnGridContextRequested(object? sender, ContextRequestedEventArgs e)
    {
        if ((e.Source as Visual)?.FindAncestorOfType<DataGridRow>(includeSelf: true) is null)
            e.Handled = true; // no row under the request: no target, no menu
    }

    // The failure bar holds the LAST op failure; a close-button click clears the
    // surface. Programmatic closes (the IsOpen binding flipping false on an
    // all-succeeded op) are not user dismissals, but clearing on them is harmless
    // — the surface is already closing — so no reason-gating is needed beyond the
    // partial bar's (which snapshots dismissal state; this one holds none).
    private void OnControlFailureClosed(FAInfoBar sender, FAInfoBarClosedEventArgs args)
    {
        if (args.Reason == FAInfoBarCloseReason.CloseButton && DataContext is ProjectsViewModel vm)
            vm.ControlFailure.Clear();
    }

    private readonly RowClassBinder<ProjectRow> _rowBinder;
    private readonly ColumnWidthPersistence _widthPersistence;

    /// <summary>Test seam (InternalsVisibleTo): live row-class subscriptions.
    /// The teardown-drain regression test pins this to 0 after detach.</summary>
    internal int RowSubscriptionCount => _rowBinder.Count;

    /// <summary>Test seam (InternalsVisibleTo): live column-width subscriptions.</summary>
    internal int ColumnWidthSubscriptionCount => _widthPersistence.SubscriptionCount;

    // Only a close-button click is a dismissal episode (design § partial-bar
    // dismissal semantics: dismiss snapshots the CURRENT unreachable id-set).
    // FAInfoBar also raises Closed with Reason=Programmatic whenever the IsOpen
    // binding flips false — scope switch to a single host, outage recovery — and
    // snapshotting there would suppress the warning on return to All hosts even
    // though the user never dismissed it.
    private void OnPartialBarClosed(FAInfoBar sender, FAInfoBarClosedEventArgs args)
    {
        if (args.Reason == FAInfoBarCloseReason.CloseButton && DataContext is ProjectsViewModel vm)
            vm.DismissPartialCommand.Execute(null);
    }

    // The DataGrid's built-in sort is FLAT; on this hierarchical grid it would scatter child
    // (per-host) rows away from their parent (and, if left unhandled, install a path sort). Cancel
    // it unconditionally and route to the VM, which swaps RowsView's single custom sort description
    // (design: only the parent aggregate sorts, children follow). Each sortable column's
    // SortMemberPath is the F# column token; the token→column mapping lives ONLY in F#
    // (ProjectRows.tryColumnOfToken — compile-time total over the DU), so this router has no case
    // list to fall out of sync. The chevron column has no SortMemberPath ⇒ None ⇒ no-op, never a
    // flat sort.
    private void OnGridSorting(object? sender, DataGridColumnEventArgs e)
    {
        e.Handled = true;
        if (DataContext is not ProjectsViewModel vm)
            return;
        var column = ProjectRows.tryColumnOfToken(e.Column.SortMemberPath);
        if (column is not null)
            vm.ToggleSort(column.Value);
    }
}
