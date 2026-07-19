using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;

namespace Lattice.App.Views;

/// <summary>
/// Opts a <see cref="ComboBox"/> into opening its dropdown on pointer PRESS instead of the
/// stock release-open (issue #95). Avalonia deliberately opens on release
/// (AvaloniaUI/Avalonia#3428 for touch-scroll correctness, #9707 for cross-control
/// consistency), which makes the perceived click→menu latency equal the user's entire
/// press-hold duration — typically 70–150 ms — on top of the ≤30 ms popup work. macOS-native
/// popup buttons and menus open on mouse-down, so against the platform baseline the whole
/// press reads as lag. Measured breakdown on the #95 thread.
///
/// <para>Set <c>ComboBoxPressOpenBehavior.OpenOnPress="True"</c> on the ComboBox. A
/// tunnel-phase <c>PointerPressed</c> handler opens the dropdown and marks the event handled,
/// which keeps the stock <c>:pressed</c>-then-toggle-on-release pipeline inert (the release
/// finds no <c>:pressed</c> state, so it cannot immediately toggle the dropdown back closed).
/// Presses while the dropdown is already open are left untouched: item clicks inside the
/// popup and the light-dismiss close path behave exactly as stock.</para>
/// </summary>
public static class ComboBoxPressOpenBehavior
{
    public static readonly AttachedProperty<bool> OpenOnPressProperty =
        AvaloniaProperty.RegisterAttached<ComboBox, bool>("OpenOnPress", typeof(ComboBoxPressOpenBehavior));

    public static void SetOpenOnPress(ComboBox box, bool value) => box.SetValue(OpenOnPressProperty, value);

    public static bool GetOpenOnPress(ComboBox box) => box.GetValue(OpenOnPressProperty);

    static ComboBoxPressOpenBehavior() =>
        OpenOnPressProperty.Changed.AddClassHandler<ComboBox>((box, e) =>
        {
            box.RemoveHandler(InputElement.PointerPressedEvent, OnPointerPressed); // idempotent across re-sets
            if (e.GetNewValue<bool>())
                box.AddHandler(InputElement.PointerPressedEvent, OnPointerPressed, RoutingStrategies.Tunnel);
        });

    private static void OnPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        // Editable combos reserve the press for text-caret placement; none exist in Lattice
        // today, but the behavior must stay safe to lift into a global style.
        if (sender is not ComboBox box || box.IsDropDownOpen || box.IsEditable)
            return;
        if (!e.GetCurrentPoint(box).Properties.IsLeftButtonPressed)
            return;
        box.SetCurrentValue(ComboBox.IsDropDownOpenProperty, true);
        e.Handled = true;
    }
}
