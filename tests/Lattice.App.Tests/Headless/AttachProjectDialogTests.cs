using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Avalonia.VisualTree;
using FluentAvalonia.UI.Controls;
using Lattice.App.Tests.Fakes;
using Lattice.App.ViewModels;
using Lattice.App.Views;
using Lattice.Core;
using Xunit;
using static Lattice.Tests.HeadlessLayout;

namespace Lattice.App.Tests.Headless;

/// <summary>
/// Headless smoke of the attach dialog's XAML and its button wiring — the cheap
/// insurance below the owner-eyeball visual gate: the compiled bindings + the
/// FAContentDialog StyleKeyOverride load without throwing, the credential toggle
/// swaps which fields render, and the primary button routes to the VM's command
/// (never closing the dialog itself). Flow rendering/verbatim-failure logic is
/// covered headless-free in AttachProjectViewModelTests.
/// </summary>
public class AttachProjectDialogTests
{
    private static readonly AttachHostOption HostA = new(Guid.NewGuid(), "host-a");

    private sealed class ScriptedRunner
    {
        public readonly List<Guid> Calls = [];
        public readonly TaskCompletionSource<AttachFlowResult> Gate = new();
        public CancellationToken Token;
        public Task<AttachFlowResult> RunAsync(
            Guid hostId, AttachMachine.AttachRequest request,
            IProgress<AttachMachine.Stage>? progress, CancellationToken ct)
        {
            Calls.Add(hostId);
            Token = ct;
            return Gate.Task;
        }
    }

    private static (Window Window, AttachProjectDialog Dialog, AttachProjectViewModel Vm, ScriptedRunner Runner) Open()
    {
        var runner = new ScriptedRunner();
        var vm = new AttachProjectViewModel(runner.RunAsync, [HostA], HostA.Id, new ImmediateUiDispatcher());
        var window = new Window { Width = 600, Height = 500 };
        window.Show();
        var dialog = new AttachProjectDialog { DataContext = vm };
        _ = dialog.ShowAsync(window);
        Dispatcher.UIThread.RunJobs();
        Layout(window);
        return (window, dialog, vm, runner);
    }

    [AvaloniaFact]
    public void Dialog_loads_and_shows()
    {
        var (window, _, _, _) = Open();
        Assert.Single(window.GetVisualDescendants().OfType<AttachProjectDialog>());
        window.Close();
    }

    [AvaloniaFact]
    public void Credential_toggle_swaps_the_visible_fields()
    {
        var (window, dialog, vm, _) = Open();
        var emailFields = dialog.GetVisualDescendants().OfType<StackPanel>().Single(p => p.Name == "EmailFields");
        var keyBox = dialog.GetVisualDescendants().OfType<TextBox>().Single(t => t.Name == "AccountKeyBox");

        // Default (email & password) path.
        Assert.True(emailFields.IsVisible);
        Assert.False(keyBox.IsVisible);

        vm.UseAccountKey = true;
        Dispatcher.UIThread.RunJobs();
        Layout(window);

        Assert.False(emailFields.IsVisible);
        Assert.True(keyBox.IsVisible);
        window.Close();
    }

    [AvaloniaFact]
    public void Primary_button_starts_the_flow_without_closing_the_dialog()
    {
        var (window, _, vm, runner) = Open();
        vm.ProjectUrl = "https://einstein.example/";
        vm.Email = "user@example.com";
        vm.Password = "pw";
        Dispatcher.UIThread.RunJobs();

        var primary = window.GetVisualDescendants().OfType<Button>().Single(b => b.Name == "PrimaryButton");
        primary.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Dispatcher.UIThread.RunJobs();

        Assert.Single(runner.Calls);                    // the flow started
        Assert.True(vm.IsBusy);
        // The button never closes the dialog itself — the VM drives the close on
        // success. A stray close would fire the dialog's Closed handler and abort
        // the flow just started (OnClosed → vm.Cancel), so the flow's token must
        // still be live here.
        Assert.Single(window.GetVisualDescendants().OfType<AttachProjectDialog>());
        Assert.False(runner.Token.IsCancellationRequested);

        runner.Gate.SetResult(new(AttachFlowOutcome.Attached, [], null));
        window.Close();
    }
}
