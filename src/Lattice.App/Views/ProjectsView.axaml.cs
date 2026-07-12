using Avalonia.Controls;
using FluentAvalonia.UI.Controls;
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
    }

    private readonly RowClassBinder<ProjectRow> _rowBinder;

    /// <summary>Test seam (InternalsVisibleTo): live row-class subscriptions.
    /// The teardown-drain regression test pins this to 0 after detach.</summary>
    internal int RowSubscriptionCount => _rowBinder.Count;

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
}
