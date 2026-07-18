using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Threading;

namespace Lattice.App.Views;

/// <summary>
/// Keeps a progress-fill <see cref="Border"/>'s (cosmetic, 200 ms) width transition from firing on
/// cell recycling. The Tasks/Transfers grids are virtualized, so Avalonia reuses a fill Border for
/// a different row on sort/filter/scroll; with the transition attached to the bound <c>Width</c>, a
/// recycled bar would animate from the PREVIOUS row's fraction to the new one — briefly showing
/// another item's progress after every reorder (Codex P2, PR #103).
///
/// <para>Set <c>ProgressFillBehavior.SnapOnRebind="True"</c> on the fill Border. On every
/// <see cref="StyledElement.DataContextChanged"/> — i.e. exactly a recycle to a new row — the
/// behavior drops the border's <c>Transitions</c> for the current dispatcher turn and restores them
/// after it. A transition is a value override, so removing it reveals the property's base (target)
/// value: any width the bound <c>Width</c> pushes during the turn snaps, and any in-flight animation
/// is cancelled to its target. Restoring on the next turn re-arms the transition so the following
/// in-place progress tick animates again.</para>
///
/// <para>Order-independent and value-independent: whether the <c>Width</c> binding pushes before or
/// after this handler runs, the turn ends transition-free; and it fires even when a recycled row's
/// fraction equals the previous one's (when the <c>Width</c> binding would not change at all), so a
/// same-fraction rebind still cancels a lingering animation (Codex P2 round 2).</para>
/// </summary>
public static class ProgressFillBehavior
{
    public static readonly AttachedProperty<bool> SnapOnRebindProperty =
        AvaloniaProperty.RegisterAttached<Border, bool>("SnapOnRebind", typeof(ProgressFillBehavior));

    public static void SetSnapOnRebind(Border border, bool value) => border.SetValue(SnapOnRebindProperty, value);

    public static bool GetSnapOnRebind(Border border) => border.GetValue(SnapOnRebindProperty);

    static ProgressFillBehavior() =>
        SnapOnRebindProperty.Changed.AddClassHandler<Border>((border, e) =>
        {
            border.DataContextChanged -= OnDataContextChanged; // idempotent across re-sets
            if (e.GetNewValue<bool>())
                border.DataContextChanged += OnDataContextChanged;
        });

    private static void OnDataContextChanged(object? sender, EventArgs e)
    {
        // Already suppressed for this turn (a burst of rebinds while scrolling) — the pending
        // restore will re-arm; don't stack a second one or lose the original transitions.
        if (sender is not Border border || border.Transitions is not { } transitions)
            return;

        border.Transitions = null;
        Dispatcher.UIThread.Post(() => border.Transitions = transitions);
    }
}
