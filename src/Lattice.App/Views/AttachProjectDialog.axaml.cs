using FluentAvalonia.UI.Controls;
using Lattice.App.ViewModels;

namespace Lattice.App.Views;

/// <summary>
/// The project-attach dialog (M3 PR I). The "Attach" primary button never
/// closes the dialog itself — it starts the flow and the dialog stays open,
/// rendering progress and (on failure) the verbatim error. The view model
/// drives the close: a successful attach raises
/// <see cref="AttachProjectViewModel.CloseRequested"/>, which the opener maps to
/// <see cref="FAContentDialog.Hide(FAContentDialogResult)"/>. Any close path
/// (the Cancel button, Escape, a success-driven Hide) aborts a running flow via
/// the view model's token — the daemon-side lookup dies harmlessly with the
/// connection (design 2.3).
/// </summary>
public partial class AttachProjectDialog : FAContentDialog
{
    public AttachProjectDialog()
    {
        InitializeComponent();
        PrimaryButtonClick += OnPrimaryClick;
        Closed += OnClosed;
    }

    // Subclassing changes the default StyleKey to AttachProjectDialog, so the
    // FAContentDialog control theme no longer resolves and OnApplyTemplate can't
    // find the PrimaryButton template part (KeyNotFoundException at first
    // measure). Pin the base type's theme. (Precedent: AddHostDialog.)
    protected override Type StyleKeyOverride => typeof(FAContentDialog);

    // Start the flow without letting the button close the dialog: the VM keeps it
    // open (progress / failure) and closes it itself on success (OnClosed below
    // then aborts nothing, the flow already settled). The CanExecute guard is the
    // re-entrancy gate — ExecuteAsync ignores it, so a stray second click while a
    // flow is in flight (IsBusy ⇒ CanExecute false) is skipped, never stacked
    // (precedent AddHostDialog.axaml.cs:50).
    private void OnPrimaryClick(FAContentDialog sender, FAContentDialogButtonClickEventArgs args)
    {
        args.Cancel = true;
        if (DataContext is AttachProjectViewModel vm && vm.AttachCommand.CanExecute(null))
            vm.AttachCommand.Execute(null);
    }

    // Any close path aborts a running flow through the VM's token (idempotent when
    // none is running). On the success path the flow has already settled, so this
    // Cancel is a no-op on a completed CTS.
    private void OnClosed(FAContentDialog sender, FAContentDialogClosedEventArgs args)
    {
        if (DataContext is AttachProjectViewModel vm)
            vm.Cancel();
    }
}
