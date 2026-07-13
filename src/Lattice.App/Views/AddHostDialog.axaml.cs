using Avalonia.Controls;
using FluentAvalonia.Core;
using FluentAvalonia.UI.Controls;
using Lattice.App.ViewModels;

namespace Lattice.App.Views;

public partial class AddHostDialog : FAContentDialog
{
    public AddHostDialog()
    {
        InitializeComponent();
        PrimaryButtonClick += OnPrimaryClick;
        SecondaryButtonClick += OnSecondaryClick;
        Opened += OnOpened;
    }

    // Subclassing changes the default StyleKey to AddHostDialog, so the
    // FAContentDialog control theme no longer resolves and OnApplyTemplate
    // can't find the PrimaryButton template part (KeyNotFoundException at
    // first measure). Pin the base type's theme.
    protected override Type StyleKeyOverride => typeof(FAContentDialog);

    // The primary button test-connects; on failure the dialog stays open and the
    // InfoBar renders the error. A deferral holds the close until AddAsync resolves.
    private async void OnPrimaryClick(FAContentDialog sender, FAContentDialogButtonClickEventArgs args)
    {
        if (DataContext is not AddHostViewModel vm)
            return;
        FADeferral deferral = args.GetDeferral();
        try
        {
            await vm.AddCommand.ExecuteAsync(null);
            args.Cancel = !vm.Succeeded; // stay open and show the InfoBar on failure
        }
        finally
        {
            deferral.Complete();
        }
    }

    // Auth-failed deep link (design 3b): the edit dialog opens with the password
    // field already flagged. Focus lands there so the user can retype immediately.
    private void OnOpened(FAContentDialog sender, EventArgs args)
    {
        if (DataContext is AddHostViewModel { HasPasswordError: true } && this.FindControl<TextBox>("PasswordBox") is { } pwd)
            pwd.Focus();
    }

    // Test connection: never closes the dialog; runs the test and shows the result inline.
    private async void OnSecondaryClick(FAContentDialog sender, FAContentDialogButtonClickEventArgs args)
    {
        if (DataContext is not AddHostViewModel vm) return;
        FADeferral deferral = args.GetDeferral();
        try { await vm.TestConnectionCommand.ExecuteAsync(null); args.Cancel = true; }
        finally { deferral.Complete(); }
    }
}
