using Lattice.App.Localization;
using Lattice.App.Tests.Fakes;
using Lattice.App.ViewModels;
using Lattice.Core;
using Xunit;

namespace Lattice.App.Tests;

/// <summary>
/// M3 PR I gates for the attach dialog's view model: it renders the
/// AttachMachine flow (phase text, verbatim failure, success) that the
/// AttachFlowRunner drives, gates on a picked Connected host (DI-3), toggles
/// credentials per the machine's DU, and cancels via the runner's token —
/// all against a SCRIPTED runner seam (the real runner + lane are proven Core-
/// side in #131). No wall-clock: the seam is gated with a TaskCompletionSource
/// and progress is marshalled through an inline dispatcher.
/// </summary>
public class AttachProjectViewModelTests
{
    private static readonly AttachHostOption HostA = new(Guid.NewGuid(), "host-a");
    private static readonly AttachHostOption HostB = new(Guid.NewGuid(), "host-b");

    // A scripted runner seam: records every call and returns a controllable task.
    private sealed class ScriptedRunner
    {
        public readonly List<(Guid Host, AttachMachine.AttachRequest Request)> Calls = [];
        public IProgress<AttachMachine.Stage>? Progress;
        public CancellationToken Token;
        public TaskCompletionSource<AttachFlowResult> Gate = new();

        public Task<AttachFlowResult> RunAsync(
            Guid hostId, AttachMachine.AttachRequest request,
            IProgress<AttachMachine.Stage>? progress, CancellationToken ct)
        {
            Calls.Add((hostId, request));
            Progress = progress;
            Token = ct;
            return Gate.Task;
        }
    }

    private static AttachProjectViewModel MakeVm(
        ScriptedRunner runner,
        IReadOnlyList<AttachHostOption>? hosts = null,
        Guid? lockedHostId = null) =>
        new(runner.RunAsync, hosts ?? [HostA], lockedHostId, new ImmediateUiDispatcher());

    // Fills the email/password path with a picked host so CanSubmit is true.
    private static void FillEmailPath(AttachProjectViewModel vm, AttachHostOption? host = null)
    {
        vm.SelectedHost = host ?? vm.Hosts[0];
        vm.ProjectUrl = "https://einstein.example/";
        vm.Email = "user@example.com";
        vm.Password = "pw";
    }

    // --- host picker (DI-3) ---

    [Fact]
    public void Scoped_host_locks_the_picker_to_that_host()
    {
        var vm = MakeVm(new ScriptedRunner(), hosts: [HostA], lockedHostId: HostA.Id);

        Assert.True(vm.IsHostPickerLocked);
        Assert.Same(HostA, vm.SelectedHost);
        Assert.Equal([HostA], vm.Hosts);
    }

    [Fact]
    public void All_hosts_scope_leaves_the_picker_unlocked_and_unselected()
    {
        var vm = MakeVm(new ScriptedRunner(), hosts: [HostA, HostB], lockedHostId: null);

        Assert.False(vm.IsHostPickerLocked);
        Assert.Null(vm.SelectedHost);
        Assert.Equal([HostA, HostB], vm.Hosts);
    }

    [Fact]
    public void Cannot_submit_until_a_host_is_picked()
    {
        var vm = MakeVm(new ScriptedRunner(), hosts: [HostA, HostB]);
        vm.ProjectUrl = "https://einstein.example/";
        vm.Email = "user@example.com";
        vm.Password = "pw";

        Assert.False(vm.AttachCommand.CanExecute(null));

        vm.SelectedHost = HostB;
        Assert.True(vm.AttachCommand.CanExecute(null));
    }

    [Fact]
    public void Cannot_submit_without_a_project_url()
    {
        var vm = MakeVm(new ScriptedRunner());
        vm.SelectedHost = vm.Hosts[0];
        vm.Email = "user@example.com";
        vm.Password = "pw";

        Assert.False(vm.AttachCommand.CanExecute(null));
    }

    // --- credentials toggle (AttachMachine.Credentials DU) ---

    [Fact]
    public void Email_path_requires_both_email_and_password()
    {
        var vm = MakeVm(new ScriptedRunner());
        vm.SelectedHost = vm.Hosts[0];
        vm.ProjectUrl = "https://einstein.example/";

        vm.Email = "user@example.com";
        Assert.False(vm.AttachCommand.CanExecute(null)); // no password yet

        vm.Password = "pw";
        Assert.True(vm.AttachCommand.CanExecute(null));
    }

    [Fact]
    public void Account_key_path_requires_only_the_key()
    {
        var vm = MakeVm(new ScriptedRunner());
        vm.SelectedHost = vm.Hosts[0];
        vm.ProjectUrl = "https://einstein.example/";
        vm.UseAccountKey = true;

        Assert.False(vm.AttachCommand.CanExecute(null)); // no key yet

        vm.AccountKey = "deadbeef";
        Assert.True(vm.AttachCommand.CanExecute(null));
    }

    [Fact]
    public async Task Email_path_sends_email_password_credentials_verbatim()
    {
        var runner = new ScriptedRunner();
        var vm = MakeVm(runner, lockedHostId: HostA.Id, hosts: [HostA]);
        vm.ProjectUrl = "  https://einstein.example/  ";
        vm.Email = "  User@Example.com ";
        vm.Password = "s3cret";

        var run = vm.AttachCommand.ExecuteAsync(null);
        runner.Gate.SetResult(new(AttachFlowOutcome.Attached, [], null));
        await run;

        var (host, request) = Assert.Single(runner.Calls);
        Assert.Equal(HostA.Id, host);
        Assert.Equal("https://einstein.example/", request.ProjectUrl);
        var creds = Assert.IsType<AttachMachine.Credentials.EmailPassword>(request.Credentials);
        Assert.Equal("User@Example.com", creds.email);   // trimmed, NOT lowercased (the GuiRpc layer lowercases)
        Assert.Equal("s3cret", creds.password);           // straight through, no mangling
    }

    [Fact]
    public async Task Account_key_path_sends_authenticator_credentials()
    {
        var runner = new ScriptedRunner();
        var vm = MakeVm(runner, lockedHostId: HostA.Id, hosts: [HostA]);
        vm.ProjectUrl = "https://einstein.example/";
        vm.UseAccountKey = true;
        vm.AccountKey = "  key123  ";

        var run = vm.AttachCommand.ExecuteAsync(null);
        runner.Gate.SetResult(new(AttachFlowOutcome.Attached, [], null));
        await run;

        var (_, request) = Assert.Single(runner.Calls);
        var creds = Assert.IsType<AttachMachine.Credentials.AuthenticatorKey>(request.Credentials);
        Assert.Equal("key123", creds.key);
    }

    // --- phase progression (Stage reports → text) ---

    [Fact]
    public async Task Phase_reports_drive_the_progress_text()
    {
        var runner = new ScriptedRunner();
        var vm = MakeVm(runner, lockedHostId: HostA.Id, hosts: [HostA]);
        FillEmailPath(vm);

        var run = vm.AttachCommand.ExecuteAsync(null);
        Assert.True(vm.IsBusy);
        Assert.True(vm.IsInProgress);

        runner.Progress!.Report(AttachMachine.Stage.LookupStage);
        Assert.Equal(Strings.AttachProgressLookup, vm.ProgressText);

        runner.Progress!.Report(AttachMachine.Stage.AttachStage);
        Assert.Equal(Strings.AttachProgressAttach, vm.ProgressText);

        runner.Gate.SetResult(new(AttachFlowOutcome.Attached, [], null));
        await run;
        Assert.False(vm.IsInProgress);
    }

    // --- success ---

    [Fact]
    public async Task Successful_attach_closes_the_dialog_and_keeps_the_daemon_messages()
    {
        var runner = new ScriptedRunner();
        var vm = MakeVm(runner, lockedHostId: HostA.Id, hosts: [HostA]);
        FillEmailPath(vm);
        var closed = 0;
        vm.CloseRequested += (_, _) => closed++;

        var run = vm.AttachCommand.ExecuteAsync(null);
        runner.Gate.SetResult(new(AttachFlowOutcome.Attached, ["Welcome to Einstein@Home"], null));
        await run;

        Assert.Equal(1, closed);
        Assert.Equal(AttachFlowOutcome.Attached, vm.LastOutcome);
        Assert.Contains("Welcome to Einstein@Home", vm.SuccessMessages);
        Assert.False(vm.HasFailure);
        Assert.False(vm.IsBusy);
    }

    // --- failure: verbatim daemon/project text, never branched on ---

    [Fact]
    public async Task Lookup_failure_shows_the_project_error_text_verbatim()
    {
        var runner = new ScriptedRunner();
        var vm = MakeVm(runner, lockedHostId: HostA.Id, hosts: [HostA]);
        FillEmailPath(vm);
        var closed = 0;
        vm.CloseRequested += (_, _) => closed++;

        var run = vm.AttachCommand.ExecuteAsync(null);
        runner.Gate.SetResult(new(AttachFlowOutcome.LookupFailed, [], "No account found with that email address."));
        await run;

        Assert.True(vm.HasFailure);
        Assert.Contains("No account found with that email address.", vm.FailureText);
        Assert.Equal(0, closed);       // stays open on failure
        Assert.False(vm.IsBusy);
        Assert.False(vm.IsInProgress);
    }

    [Fact]
    public async Task Attach_rejection_shows_the_daemon_messages_verbatim()
    {
        var runner = new ScriptedRunner();
        var vm = MakeVm(runner, lockedHostId: HostA.Id, hosts: [HostA]);
        FillEmailPath(vm);

        var run = vm.AttachCommand.ExecuteAsync(null);
        runner.Gate.SetResult(new(AttachFlowOutcome.AttachFailed,
            ["Already attached to project"], "The daemon rejected the attach (error -1)."));
        await run;

        Assert.True(vm.HasFailure);
        Assert.Contains("Already attached to project", vm.FailureText);
    }

    [Fact]
    public async Task Fault_shows_the_transport_error_text()
    {
        var runner = new ScriptedRunner();
        var vm = MakeVm(runner, lockedHostId: HostA.Id, hosts: [HostA]);
        FillEmailPath(vm);

        var run = vm.AttachCommand.ExecuteAsync(null);
        runner.Gate.SetResult(new(AttachFlowOutcome.Faulted, [], "Connection reset by peer"));
        await run;

        Assert.True(vm.HasFailure);
        Assert.Contains("Connection reset by peer", vm.FailureText);
    }

    // --- cancellation: the token propagates, and a canceled flow leaves clean state ---

    [Fact]
    public async Task Cancel_propagates_the_token_and_leaves_no_stuck_state()
    {
        var runner = new ScriptedRunner();
        // The runner completes only when its token is canceled — the shape of the
        // real flow, whose lane releases on the same token (proven in #131).
        var vm = MakeVm(runner, lockedHostId: HostA.Id, hosts: [HostA]);
        FillEmailPath(vm);
        var closed = 0;
        vm.CloseRequested += (_, _) => closed++;

        var run = vm.AttachCommand.ExecuteAsync(null);
        runner.Token.Register(() => runner.Gate.TrySetResult(new(AttachFlowOutcome.Canceled, [], null)));

        vm.Cancel();
        await run;

        Assert.True(runner.Token.IsCancellationRequested);
        Assert.False(vm.IsBusy);
        Assert.False(vm.IsInProgress);
        Assert.False(vm.HasFailure);
        Assert.Equal(0, closed);
    }

    [Fact]
    public async Task Failed_attempt_can_be_retried_in_the_same_session()
    {
        var runner = new ScriptedRunner();
        var vm = MakeVm(runner, lockedHostId: HostA.Id, hosts: [HostA]);
        FillEmailPath(vm);

        // First attempt fails: dialog stays open, still submittable.
        var run1 = vm.AttachCommand.ExecuteAsync(null);
        runner.Gate.SetResult(new(AttachFlowOutcome.LookupFailed, [], "No such account."));
        await run1;
        Assert.True(vm.HasFailure);
        Assert.True(vm.AttachCommand.CanExecute(null));

        // Retry with a fresh gate succeeds — the source is reused cleanly and the
        // stale failure clears.
        runner.Gate = new TaskCompletionSource<AttachFlowResult>();
        var closed = 0;
        vm.CloseRequested += (_, _) => closed++;
        var run2 = vm.AttachCommand.ExecuteAsync(null);
        Assert.False(vm.HasFailure);   // cleared at the start of the new attempt
        runner.Gate.SetResult(new(AttachFlowOutcome.Attached, [], null));
        await run2;

        Assert.Equal(1, closed);
        Assert.Equal(2, runner.Calls.Count);
    }

    // --- no double submit ---

    [Fact]
    public void Command_is_disabled_while_a_flow_is_in_flight()
    {
        var runner = new ScriptedRunner();
        var vm = MakeVm(runner, lockedHostId: HostA.Id, hosts: [HostA]);
        FillEmailPath(vm);

        Assert.True(vm.AttachCommand.CanExecute(null));
        _ = vm.AttachCommand.ExecuteAsync(null);   // enters the gated seam, stays busy
        Assert.False(vm.AttachCommand.CanExecute(null));

        runner.Gate.SetResult(new(AttachFlowOutcome.Attached, [], null));
    }
}
