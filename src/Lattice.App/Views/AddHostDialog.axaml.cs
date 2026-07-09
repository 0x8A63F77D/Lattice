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
    }

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
}
