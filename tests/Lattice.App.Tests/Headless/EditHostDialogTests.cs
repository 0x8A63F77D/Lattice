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

    // Note on scope: FluentAvaloniaUI's FAContentDialog.OnButtonClick has its own
    // deferral-count gate (_hasDeferralActive) shared across all three buttons, which
    // already discards a literal second RaiseEvent(ClickEvent) on the SAME button while
    // our own FADeferral from the first click is still open - so a real double real-click
    // never reaches OnSecondaryClick a second time in the first place, and can't exercise
    // OnSecondaryClick's own guard. To test that guard directly and deterministically, this
    // seeds the "a test is already in flight" precondition by starting
    // TestConnectionCommand.ExecuteAsync directly (standing in for any caller other than
    // this dialog's own button - the scenario the CanExecute check in OnSecondaryClick is
    // defense-in-depth against), then drives one real click on the actual secondary button
    // into that state.
    [AvaloniaFact]
    public void Clicking_test_connection_while_already_running_does_not_start_a_second_run()
    {
        var path = Path.Combine(Path.GetTempPath(), $"lattice-test-{Guid.NewGuid():N}.json");
        var registry = new HostRegistry(new LatticeConfig(5, []), path);
        var cfg = TestData.MakeHostConfig(name: "mini-01");
        registry.AddHost(cfg);
        // A single shared fake so every ExecuteAsync attempt logs into the same
        // Calls list - a second concurrent run would show up as a second "connect:"
        // entry. OnConnect blocks on a gate we control, so the in-flight run stays
        // deterministically pending while we drive the click - no wall-clock waits.
        var connectGate = new TaskCompletionSource();
        var fake = new FakeGuiRpcClient { OnConnect = (_, _) => connectGate.Task };
        var vm = AddHostViewModel.ForEdit(registry, () => fake, cfg, authError: false);
        var dialog = new AddHostDialog { DataContext = vm };
        var window = new Window { Width = 600, Height = 500 };
        window.Show();
        Layout(window);
        var showTask = dialog.ShowAsync(window);
        Layout(window);

        // Seed "already in flight" directly on the command - not via the dialog's own
        // button, so FAContentDialog has no deferral of its own open yet and the click
        // below routes into OnSecondaryClick normally.
        Assert.True(vm.TestConnectionCommand.CanExecute(null));
        Task firstRun = vm.TestConnectionCommand.ExecuteAsync(null);
        Assert.True(vm.TestConnectionCommand.IsRunning);
        Assert.False(vm.TestConnectionCommand.CanExecute(null));
        Assert.Equal(1, fake.Calls.Count(c => c.StartsWith("connect:")));

        var secondary = window.GetVisualDescendants().OfType<Button>()
            .Single(b => b.Name == "SecondaryButton");
        Assert.True(secondary.IsEnabled);

        // Real click while TestConnectionCommand is already running: OnSecondaryClick's
        // CanExecute guard must skip ExecuteAsync entirely - no second "connect:" call -
        // and still complete its deferral so the dialog stays open (no hang).
        secondary.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Assert.Equal(1, fake.Calls.Count(c => c.StartsWith("connect:")));
        Assert.True(dialog.IsHitTestVisible);
        Assert.False(showTask.IsCompleted);

        // Let the one real run resolve and confirm everything settles cleanly.
        connectGate.SetResult();
        Dispatcher.UIThread.RunJobs();

        Assert.False(vm.TestConnectionCommand.IsRunning);
        Assert.True(firstRun.IsCompletedSuccessfully);
        Assert.Equal(1, fake.Calls.Count(c => c.StartsWith("connect:")));
        Assert.Equal(string.Format(Strings.SettingsTestConnectionSuccess, 8, 2, 0), vm.TestResultText);
        Assert.True(dialog.IsHitTestVisible);
        Assert.False(showTask.IsCompleted);

        dialog.Hide();
        window.Close();
    }
}
