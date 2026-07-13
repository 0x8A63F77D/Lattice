using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Avalonia.VisualTree;
using FluentAvalonia.UI.Controls;
using Lattice.App.Localization;
using Lattice.App.ViewModels;
using Lattice.App.Views;
using Lattice.Core;
using Lattice.Tests;
using Xunit;
using static Lattice.Tests.HeadlessLayout;

namespace Lattice.App.Tests.Headless;

public class EditHostDialogTests
{
    [AvaloniaFact]
    public async Task Auth_failed_edit_dialog_shows_error_and_focuses_password()
    {
        var path = Path.Combine(Path.GetTempPath(), $"lattice-test-{Guid.NewGuid():N}.json");
        var registry = new HostRegistry(new LatticeConfig(5, []), path);
        var cfg = TestData.MakeHostConfig(name: "mini-01");
        registry.AddHost(cfg);
        var vm = AddHostViewModel.ForEdit(registry, () => new FakeGuiRpcClient(), cfg, authError: true);
        var dialog = new AddHostDialog { DataContext = vm };
        var window = new Window { Width = 600, Height = 500 };
        window.Show();
        Layout(window);
        _ = dialog.ShowAsync(window);
        Layout(window);

        Assert.Equal(Strings.EditHostDialogTitle, dialog.Title);
        var pwd = window.GetVisualDescendants().OfType<TextBox>().Single(t => t.Name == "PasswordBox");
        Assert.Contains("danger", pwd.Classes);
        Assert.True(pwd.IsFocused);
        // Secondary "Test connection" button is present in edit mode.
        Assert.Equal(Strings.SettingsTestConnectionButton, dialog.SecondaryButtonText);
        dialog.Hide();
        window.Close();
    }

    [AvaloniaFact]
    public void Test_connection_click_leaves_the_dialog_open_and_shows_the_result_inline()
    {
        var path = Path.Combine(Path.GetTempPath(), $"lattice-test-{Guid.NewGuid():N}.json");
        var registry = new HostRegistry(new LatticeConfig(5, []), path);
        var cfg = TestData.MakeHostConfig(name: "mini-01");
        registry.AddHost(cfg);
        var vm = AddHostViewModel.ForEdit(registry, () => new FakeGuiRpcClient(), cfg, authError: false);
        var dialog = new AddHostDialog { DataContext = vm };
        var window = new Window { Width = 600, Height = 500 };
        window.Show();
        Layout(window);
        var showTask = dialog.ShowAsync(window);
        Layout(window);

        // Drive the REAL secondary-button path: the template's SecondaryButton click
        // routes through FAContentDialog.OnButtonClick -> OnSecondaryButtonClick ->
        // our OnSecondaryClick handler, whose FADeferral sets args.Cancel = true so
        // FAContentDialog's own deferral callback skips HideCore().
        var secondary = window.GetVisualDescendants().OfType<Button>()
            .Single(b => b.Name == "SecondaryButton");
        Assert.True(secondary.IsEnabled);
        secondary.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        // FakeGuiRpcClient's default hooks complete synchronously, so the whole
        // TestConnectionAsync chain resolves without a real async hop, and both
        // FADeferral layers (ours + FAContentDialog's own OnButtonClick deferral)
        // unwind within the same RaiseEvent call. RunJobs drains anything the
        // dispatcher queued (bindings) - no wall-clock wait needed.
        Dispatcher.UIThread.RunJobs();

        // FAContentDialog.FinalCloseDialog flips IsHitTestVisible to false
        // SYNCHRONOUSLY, before its internal `await Task.Delay(200)` - this is the
        // earliest observable "the dialog decided to close" signal, and it fires
        // deterministically without waiting on that real delay. If OnSecondaryClick
        // let the deferral through uncancelled, this would already be false here.
        Assert.True(dialog.IsHitTestVisible);
        Assert.False(showTask.IsCompleted);
        Assert.Single(window.GetVisualDescendants().OfType<FAContentDialog>());
        Assert.Equal(string.Format(Strings.SettingsTestConnectionSuccess, 8, 2, 0), vm.TestResultText);
        Assert.False(vm.Succeeded);

        dialog.Hide();
        window.Close();
    }
}
