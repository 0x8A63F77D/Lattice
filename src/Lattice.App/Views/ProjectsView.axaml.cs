using Avalonia.Controls;
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
