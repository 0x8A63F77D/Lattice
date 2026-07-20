using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using FluentAvalonia.UI.Controls;
using Lattice.App.Aggregation;

namespace Lattice.App.Views;

/// <summary>
/// The sole payload between view models and the confirmation-dialog seam
/// (design 2.5). <paramref name="Body"/> arrives pre-formatted by the caller
/// (including DI-2's host enumeration); <paramref name="Severity"/> is the pure
/// <c>ConfirmationPolicy</c> output class that drives the dialog's styling —
/// the record carries no strings-building or policy logic of its own.
/// </summary>
public sealed record ConfirmationRequest(
    string Title, string Body, string PrimaryButtonText, ConfirmSeverity Severity);

/// <summary>
/// Generic confirmation dialog for control operations (M3): renders a
/// <see cref="ConfirmationRequest"/> and returns true only on the primary
/// button. Destructive severity styles the primary button with the danger
/// accent and makes the safe Close button the default (Enter never destroys
/// work); Caution keeps Primary as the default.
/// </summary>
public partial class ConfirmationDialog : FAContentDialog
{
    public ConfirmationDialog()
    {
        InitializeComponent();
    }

    // Subclassing changes the default StyleKey to ConfirmationDialog, so the
    // FAContentDialog control theme no longer resolves and OnApplyTemplate
    // can't find the PrimaryButton template part (KeyNotFoundException at
    // first measure). Pin the base type's theme. (Precedent: AddHostDialog.)
    protected override Type StyleKeyOverride => typeof(FAContentDialog);

    private ConfirmSeverity? Severity =>
        DataContext is ConfirmationRequest request ? request.Severity : null;

    // DefaultButton must be set before ShowAsync applies it; DataContext
    // assignment happens at construction, well before the dialog opens.
    protected override void OnDataContextChanged(EventArgs e)
    {
        base.OnDataContextChanged(e);
        if (Severity is { } severity)
            DefaultButton = severity.IsDestructive
                ? FAContentDialogButton.Close
                : FAContentDialogButton.Primary;
    }

    // The template (and its PrimaryButton part) exists only once the dialog is
    // shown; class the button here, the canonical part-access hook.
    protected override void OnApplyTemplate(TemplateAppliedEventArgs e)
    {
        base.OnApplyTemplate(e);
        if (Severity is { } severity && e.NameScope.Find<Button>("PrimaryButton") is { } primary)
            primary.Classes.Set("danger", severity.IsDestructive);
    }

    /// <summary>
    /// Shows a confirmation for <paramref name="request"/> over
    /// <paramref name="owner"/>; true only when the user pressed the primary
    /// (confirming) button. This is the production implementation behind the
    /// view models' dialog seam (<c>Func&lt;ConfirmationRequest, Task&lt;bool&gt;&gt;</c>).
    /// </summary>
    public static Task<bool> ConfirmAsync(TopLevel owner, ConfirmationRequest request) =>
        ConfirmAsync(owner, new ConfirmationDialog { DataContext = request });

    internal static async Task<bool> ConfirmAsync(TopLevel owner, ConfirmationDialog dialog) =>
        await dialog.ShowAsync(owner) == FAContentDialogResult.Primary;
}
