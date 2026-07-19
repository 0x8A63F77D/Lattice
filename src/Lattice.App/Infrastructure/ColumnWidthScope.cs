using Avalonia;
using Avalonia.Controls;

namespace Lattice.App.Infrastructure;

/// <summary>
/// Carries the <see cref="UiStateStore"/> down the visual tree so the data
/// views — which the shell materializes through DataTemplates, with no
/// constructor seam to inject into — can reach it for column-width persistence
/// (issue #120). Set once on the hosting window at the composition root
/// (App.OnFrameworkInitializationCompleted) and, in tests, on the fixture
/// window; the inherited attached property then resolves on every descendant
/// grid at attach time.
///
/// This deliberately keeps a pure view concern (a DataGridColumn's pixel width)
/// out of the view models: ProjectsViewModel / EventLogViewModel persist
/// nothing else and gain no store dependency. A null value (no scope set) makes
/// <see cref="ColumnWidthPersistence"/> a safe no-op.
/// </summary>
public static class ColumnWidthScope
{
    /// <summary>Inherited so a single set on the top-level window reaches every
    /// data view nested under the page host.</summary>
    public static readonly AttachedProperty<UiStateStore?> StoreProperty =
        AvaloniaProperty.RegisterAttached<Control, UiStateStore?>(
            "Store", typeof(ColumnWidthScope), defaultValue: null, inherits: true);

    public static void SetStore(Control target, UiStateStore? value) =>
        target.SetValue(StoreProperty, value);

    public static UiStateStore? GetStore(Control target) =>
        target.GetValue(StoreProperty);
}
