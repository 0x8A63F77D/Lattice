using Avalonia.Controls;
using FluentAvalonia.UI.Controls;
using Lattice.App.Infrastructure;
using Lattice.App.ViewModels;
using Lattice.Core;

namespace Lattice.App.Views;

public partial class TransfersView : UserControl
{
    public TransfersView()
    {
        InitializeComponent();
        // Post-retrofit, row DataContexts are TransferRow HOLDERS whose Data
        // swaps in place on value-change polls (CollectionReconciler's Update
        // op) — LoadingRow never re-fires for that, so classes must track the
        // holder. RowClassBinder owns the whole subscription lifecycle
        // (load/re-apply/recycle/unload/detach-drain); only the styling
        // applier is view-local. Stamped from TasksView's attachment — same
        // shared binder, same one-shot-in-constructor contract.
        _rowBinder = RowClassBinder<TransferRow>.Attach(Grid, static (row, holder) =>
            row.Classes.Set("retrying", holder.Data.UiState == TransferUiState.Retrying));
    }

    // Only a close-button click is a dismissal episode — same rationale as
    // TasksView.OnPartialBarClosed (FAInfoBar also raises Closed with
    // Reason=Programmatic on scope-driven IsOpen flips, which must not count
    // as a user dismissal).
    private void OnPartialBarClosed(FAInfoBar sender, FAInfoBarClosedEventArgs args)
    {
        if (args.Reason == FAInfoBarCloseReason.CloseButton && DataContext is TransfersViewModel vm)
            vm.DismissPartialCommand.Execute(null);
    }

    private readonly RowClassBinder<TransferRow> _rowBinder;

    /// <summary>Test seam (InternalsVisibleTo): live row-class subscriptions.
    /// The teardown-drain regression test pins this to 0 after detach.</summary>
    internal int RowSubscriptionCount => _rowBinder.Count;
}
