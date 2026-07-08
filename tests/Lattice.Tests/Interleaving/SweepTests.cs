using Lattice.Boinc.GuiRpc;
using Lattice.Core;
using Microsoft.Extensions.Time.Testing;
using Xunit;

namespace Lattice.Tests.Interleaving;

/// <summary>The environment actions swept against every interleaving point.</summary>
public enum EnvAction { UpdateConfig, Dispose, RequestRefresh }

public class SweepTests
{
    [Fact]
    public async Task Probe_seam_freezes_and_releases_at_designated_point()
    {
        var fake = new FakeGuiRpcClient();
        var controller = new ProbeController();
        await using var monitor = new HostMonitor(TestData.MakeHostConfig(), () => fake, new FakeTimeProvider(), 5);

        monitor.InterleaveProbe = controller.Probe;
        controller.FreezeAt(InterleavePoints.BeforeAcceptGuard);
        monitor.Start();

        await controller.WaitForAsync(InterleavePoints.BeforeAcceptGuard);
        Assert.Equal(HostConnectionState.FetchingState, monitor.Status.State);

        controller.Release();
        await Wait.UntilAsync(() => monitor.Status.State == HostConnectionState.Connected);
    }

    [Fact]
    public async Task FailedAttemptDoesNotPollute_DaemonVersion()
    {
        // First connection is accepted with daemon version 8.0 and ticks fine; then a
        // later poll tick fails, forcing Retrying (event #1). The reconnect attempt
        // reaches exchange_versions returning 9.9 but FAILS at get_state — never
        // accepted. The SECOND Retrying event must still carry 8.0 (the last ACCEPTED
        // version), not 9.9 (the unaccepted attempt's version).
        bool failResults = false;
        int stateCalls = 0;
        var fake = new FakeGuiRpcClient
        {
            // One counter drives both: connection #1 sees 8.0 and a good get_state;
            // connection #2 sees 9.9 and a throwing get_state.
            OnExchangeVersions = () => Task.FromResult(stateCalls == 0
                ? new VersionInfo(8, 0, 0)
                : new VersionInfo(9, 9, 0)),
            OnGetState = () => ++stateCalls == 1
                ? Task.FromResult(FakeGuiRpcClient.EmptyState)
                : throw new BoincConnectionException("get_state boom"),
            OnGetResults = _ => failResults
                ? throw new BoincConnectionException("results boom")
                : Task.FromResult<IReadOnlyList<Result>>([]),
        };

        List<ConnectionStatus> statusEvents = [];
        var time = new FakeTimeProvider();
        await using var monitor = new HostMonitor(TestData.MakeHostConfig(), () => fake, time, 5);
        monitor.StatusChanged += (_, s) => { lock (statusEvents) statusEvents.Add(s); };
        monitor.Start();

        // Connection #1 accepted (first tick included) with daemon version 8.0.
        await Wait.UntilAsync(() => monitor.Status.State == HostConnectionState.Connected);

        // Break the next poll tick: a post-Connected failure publishes Retrying #1.
        failResults = true;
        monitor.RequestRefresh();
        await Wait.UntilAsync(() => monitor.Status.State == HostConnectionState.Retrying);

        // Skip the backoff. The reconnect attempt fetches 9.9 from exchange_versions
        // but dies at get_state, publishing Retrying #2 for the unaccepted attempt.
        monitor.RequestRefresh();
        await Wait.UntilAsync(() =>
        {
            lock (statusEvents)
                return statusEvents.Count(s => s.State == HostConnectionState.Retrying) >= 2;
        });

        ConnectionStatus secondRetrying;
        lock (statusEvents)
            secondRetrying = statusEvents.Where(s => s.State == HostConnectionState.Retrying).ElementAt(1);
        Assert.Equal(new VersionInfo(8, 0, 0), secondRetrying.DaemonVersion);
    }

    [Fact]
    public async Task AbortedAttemptNeverDestroysMessageLog()
    {
        // Regression test for the pre-fix message-log clear-on-reconnect defect.
        // RunAttemptAsync used to run `_messages.Clear()` immediately after get_state
        // and BEFORE the BeforeAcceptGuard config check — destroying the user-visible
        // log inside a reconnect attempt that had not yet been accepted (and could
        // still be aborted by a config change). The fix keeps the retained log intact
        // until a new connection's first tick atomically replaces it.
        //
        // Freeze a reconnect attempt exactly at BeforeAcceptGuard (post get_state,
        // pre-accept — where the old code had already cleared) and assert the log is
        // still intact there. Then abort the frozen attempt with a second config change
        // and assert the log survives the abort and every later reconnect.
        var fake = new FakeGuiRpcClient
        {
            OnGetMessages = seqno => Task.FromResult<IReadOnlyList<Message>>(
                seqno == 0 ? [TestData.MakeMessage(1), TestData.MakeMessage(2)] : []),
            OnGetResults = _ => Task.FromResult<IReadOnlyList<Result>>([]),
            OnGetFileTransfers = () => Task.FromResult<IReadOnlyList<FileTransfer>>([]),
        };
        var controller = new ProbeController();
        var time = new FakeTimeProvider();
        HostConfig config = TestData.MakeHostConfig();
        await using var monitor = new HostMonitor(config, () => fake, time, 5);
        monitor.InterleaveProbe = controller.Probe;
        monitor.Start();

        // Connection #1: the first tick publishes the log [1,2]. BeforeAcceptGuard is
        // armed only AFTER this, so connection #1's own pass through that point is never
        // frozen — and steady-state polling never revisits BeforeAcceptGuard, so arming
        // here is race-free: the next (and only) pass is the reconnect attempt below.
        await Wait.UntilAsync(() => monitor.Snapshot is not null);
        Assert.Equal(2, monitor.Messages.Count);
        Assert.Equal(1, monitor.Messages[0].Seqno);
        Assert.Equal(2, monitor.Messages[1].Seqno);

        // Force a reconnect and catch the reconnect attempt at BeforeAcceptGuard.
        controller.FreezeAt(InterleavePoints.BeforeAcceptGuard);
        monitor.UpdateConfig(config with { Address = "newhost" });
        await controller.WaitForAsync(InterleavePoints.BeforeAcceptGuard);

        // The try/finally guarantees the frozen loop is always released, so a failing
        // assertion below surfaces as a clean test failure rather than a disposal hang.
        try
        {
            // LOAD-BEARING: post get_state, pre-accept, inside the reconnect attempt —
            // exactly where the pre-fix code had already run _messages.Clear(). The
            // retained log must still be intact. (Pre-fix: Count == 0 → assertion fails.)
            Assert.Equal(2, monitor.Messages.Count);
            Assert.Equal(1, monitor.Messages[0].Seqno);
            Assert.Equal(2, monitor.Messages[1].Seqno);

            // Abort THIS frozen attempt with a second config change: the accept guard
            // just past the freeze sees _configChanged and returns ConfigChanged, so this
            // attempt never reaches Connected or a tick.
            monitor.UpdateConfig(config with { Address = "newerhost" });
        }
        finally
        {
            controller.Release();
        }

        // The log survives the abort and the subsequent reconnect: it is only ever
        // atomically replaced by a new connection's first tick (with the same [1,2] the
        // daemon serves), never cleared to empty.
        await Wait.UntilAsync(() => monitor.Status.State == HostConnectionState.Connected);
        await Wait.UntilAsync(() => monitor.Snapshot is not null);
        Assert.Equal(2, monitor.Messages.Count);
        Assert.Equal(1, monitor.Messages[0].Seqno);
        Assert.Equal(2, monitor.Messages[1].Seqno);
    }

    [Fact]
    public async Task FirstTickReplacesLogAtomically()
    {
        // The retained log must survive a reconnect all the way until the new
        // connection's first tick REPLACES it — the pre-fix code instead cleared it
        // early, inside RunAttemptAsync, before the tick ran. Freeze the reconnect's
        // first tick at TickBeforeMsgPublish (get_messages has returned, log not yet
        // mutated) and assert the OLD log is still visible there.
        //
        // The restarted daemon's seqno counter has reset, so it serves a single message
        // with seqno 1 again: replace-then-count and (pre-fix) clear-then-append both
        // yield Count == 1 afterwards, so the distinguishing evidence is the surviving
        // OLD log observed in the pre-publish window while frozen.
        bool failStatus = false;
        var fake = new FakeGuiRpcClient
        {
            OnGetMessages = seqno => Task.FromResult<IReadOnlyList<Message>>(
                seqno == 0 ? [TestData.MakeMessage(1), TestData.MakeMessage(2)] : []),
            OnGetResults = _ => Task.FromResult<IReadOnlyList<Result>>([]),
            OnGetFileTransfers = () => Task.FromResult<IReadOnlyList<FileTransfer>>([]),
            OnGetCcStatus = () => failStatus
                ? throw new BoincConnectionException("daemon restarted")
                : Task.FromResult(FakeGuiRpcClient.DefaultStatus),
        };
        var controller = new ProbeController();
        var time = new FakeTimeProvider();
        HostConfig config = TestData.MakeHostConfig();
        var messagesAdded = new List<MessagesAddedEventArgs>();

        await using var monitor = new HostMonitor(config, () => fake, time, 5);
        monitor.MessagesAdded += (_, e) => { lock (messagesAdded) messagesAdded.Add(e); };
        monitor.InterleaveProbe = controller.Probe;
        monitor.Start();

        // Connection #1: log [1,2]. Its own first tick already passed
        // TickBeforeMsgPublish before this await returns, so nothing is armed yet.
        await Wait.UntilAsync(() => monitor.Snapshot is not null);
        Assert.Equal(2, monitor.Messages.Count);
        Assert.Equal(1, monitor.Messages[0].Seqno);
        Assert.Equal(2, monitor.Messages[1].Seqno);

        // Break the connection: the next tick throws, the host drops to Retrying and
        // parks in the (never-advanced) backoff wait — no attempt is in flight there.
        failStatus = true;
        monitor.RequestRefresh();
        await Wait.UntilAsync(() => monitor.Status.State == HostConnectionState.Retrying);

        // Parked in Retrying: safe to reconfigure the fake for the "restarted daemon"
        // whose seqno counter reset (one message, seqno 1) and to arm the reconnect's
        // tick freeze. Steady-state ticks — which would also pass TickBeforeMsgPublish —
        // cannot occur while parked, so arming here catches only the reconnect's first
        // tick.
        failStatus = false;
        fake.OnGetMessages = seqno => Task.FromResult<IReadOnlyList<Message>>(
            seqno == 0 ? [TestData.MakeMessage(1)] : []);
        controller.FreezeAt(InterleavePoints.TickBeforeMsgPublish);

        // Skip the backoff; the reconnect runs to its first tick and freezes just before
        // the log mutation.
        monitor.RequestRefresh();
        await controller.WaitForAsync(InterleavePoints.TickBeforeMsgPublish);

        // The try/finally guarantees the frozen loop is always released, so a failing
        // assertion below surfaces as a clean test failure rather than a disposal hang.
        try
        {
            // LOAD-BEARING: get_messages(0) has returned the restarted daemon's [1], but
            // the log has not been touched yet. The retained OLD log [1,2] must still be
            // visible. (Pre-fix: cleared inside RunAttemptAsync → empty → assertion fails.)
            Assert.Equal(2, monitor.Messages.Count);
            Assert.Equal(1, monitor.Messages[0].Seqno);
            Assert.Equal(2, monitor.Messages[1].Seqno);
        }
        finally
        {
            controller.Release();
        }

        // Let the first tick complete: the log is atomically replaced with the daemon's
        // current buffer — exactly the single seqno-1 message, not [1,2] with a seqno-1
        // message appended.
        await Wait.UntilAsync(() => monitor.Messages.Count == 1);

        Assert.Single(monitor.Messages);
        Assert.Equal(1, monitor.Messages[0].Seqno);

        // The reconnect's first tick raised MessagesAdded with exactly that single
        // message (connection #1's event carried [1,2] and does not match).
        lock (messagesAdded)
            Assert.Single(messagesAdded, e => e.Messages.Count == 1 && e.Messages[0].Seqno == 1);
    }

    // -------------------------------------------------------------------------
    // Sweep matrix: every interleaving point × every environment action
    // (15 × 3 = 45 cases). Per-case assertions are grouped by lettered property
    // so a failure names what was violated:
    //   A1 — per-point stale-publish doctrine after UpdateConfig (AssertA1Doctrine)
    //   A2 — Retrying/AuthFailed/Disconnected publishes happen only after the old
    //        client's disposal (AssertA2TerminalStatusesFollowClientDisposal)
    //   A3 — UpdateConfig leads to a connect against the NEW config in bounded time
    //   A5 — Dispose completes, loop task non-faulted, final status Disconnected
    //   RequestRefresh — the loop makes progress and nothing faults (except the
    //        deliberate BeforeParkWait special case: parked stays parked, L3b)
    // -------------------------------------------------------------------------

    public static IEnumerable<object[]> SweepCases() =>
        from point in InterleavePoints.All
        from action in new[] { EnvAction.UpdateConfig, EnvAction.Dispose, EnvAction.RequestRefresh }
        select new object[] { point, action };

    /// <summary>One recorded publish: kind is "status" / "snapshot" / "messages";
    /// Gen is the config generation stamped into the payload (null when unstamped).</summary>
    private sealed record RecordedEvent(
        string Kind, int? Gen, HostConnectionState? State, int Attempt, bool AllClientsDisposed);

    private static int? GenFromTag(string tag) => tag switch
    {
        "gen1" => 1,
        "gen2" => 2,
        _ => null,
    };

    [Theory]
    [MemberData(nameof(SweepCases))]
    public async Task SweepPointTimesAction(string point, EnvAction action)
    {
        // ---------------------------------------------------------------- Arrange
        // Two config generations, distinguished by port. Every publish is stamped
        // with the generation of the connection that produced it: StatusChanged via
        // DaemonVersion (8, gen, 0), SnapshotUpdated via HostName ("gen{g}"),
        // MessagesAdded via the message Body ("gen{g}").
        Guid hostId = Guid.NewGuid();
        HostConfig config1 = TestData.MakeHostConfig(id: hostId, name: "gen1", port: 31417);
        HostConfig config2 = TestData.MakeHostConfig(id: hostId, name: "gen2", port: 31418);
        int currentGen = 1;

        // ScriptFor(point): the dispatcher points are only reachable through a FAILED
        // attempt, so gen1 refuses the TCP connect (FinallyEnter / AfterCtsDispose /
        // BeforeRetryPublish) or refuses the password (BeforeParkWait). All other
        // points ride the happy path. The preamble applies to ALL actions at those
        // points; only the A1 doctrine column is UpdateConfig-specific.
        bool failingConnectPreamble = point is InterleavePoints.FinallyEnter
            or InterleavePoints.AfterCtsDispose
            or InterleavePoints.BeforeRetryPublish;
        bool refusedPasswordPreamble = point == InterleavePoints.BeforeParkWait;

        object clientsGate = new();
        List<(int Gen, FakeGuiRpcClient Fake)> clients = [];

        FakeGuiRpcClient MakeClient(int gen)
        {
            var fake = new FakeGuiRpcClient
            {
                OnConnect = (_, _) =>
                {
                    if (gen == 1 && failingConnectPreamble)
                        throw new BoincConnectionException("connect refused");
                    // A connect racing a config change (client built for a generation
                    // that is no longer current) never completes on its own: only its
                    // connection token — already canceled by UpdateConfig, which is
                    // what made the generation stale — can abort it.
                    return Volatile.Read(ref currentGen) == gen
                        ? Task.CompletedTask
                        : new TaskCompletionSource().Task;
                },
                OnAuthorize = _ => Task.FromResult(!(gen == 1 && refusedPasswordPreamble)),
                OnExchangeVersions = () => Task.FromResult(new VersionInfo(8, gen, 0)),
                OnGetMessages = seqno => Task.FromResult<IReadOnlyList<Message>>(
                    seqno == 0
                        ? [TestData.MakeMessage(1, $"gen{gen}"), TestData.MakeMessage(2, $"gen{gen}")]
                        : []),
            };
            lock (clientsGate) clients.Add((gen, fake));
            return fake;
        }

        bool AllClientsDisposed()
        { lock (clientsGate) return clients.All(c => c.Fake.Disposed); }
        int TickCount()
        { lock (clientsGate) return clients.Sum(c => c.Fake.Calls.Count(call => call == "get_cc_status")); }
        int ConnectCount(int gen)
        {
            lock (clientsGate)
                return clients.Where(c => c.Gen == gen)
                              .Sum(c => c.Fake.Calls.Count(call => call.StartsWith("connect:")));
        }

        object recordedGate = new();
        List<RecordedEvent> recorded = [];
        void Record(string kind, int? gen, HostConnectionState? state = null, int attempt = 0)
        {
            // A2 evidence is captured at publish time, on the loop thread: were all
            // clients created so far already disposed when this event fired?
            bool allDisposed = AllClientsDisposed();
            lock (recordedGate) recorded.Add(new RecordedEvent(kind, gen, state, attempt, allDisposed));
        }

        var controller = new ProbeController();
        var time = new FakeTimeProvider();
        await using var monitor = new HostMonitor(
            config1, () => MakeClient(Volatile.Read(ref currentGen)), time, 5);
        monitor.StatusChanged += (_, s) => Record("status", s.DaemonVersion?.Minor, s.State, s.Attempt);
        monitor.SnapshotUpdated += (_, snap) => Record("snapshot", GenFromTag(snap.HostName));
        monitor.MessagesAdded += (_, e) => Record("messages", GenFromTag(e.Messages[0].Body));
        monitor.InterleaveProbe = controller.Probe;

        // -------------------------------------------------------------------- Act
        // Freeze choice: every point is armed BEFORE Start() and therefore freezes on
        // its FIRST visit — attempt #1 for the attempt.* points, the first connection's
        // first tick for the tick.* points, the first poll wait for the poll.* points,
        // and the scripted failing/refused attempt for the dispatcher points. The
        // doctrine is per-point, not per-visit, so the first visit is the sweep target.
        controller.FreezeAt(point);
        monitor.Start();
        if (point == InterleavePoints.PollAfterWait)
            monitor.RequestRefresh(); // completes the first poll wait so the loop reaches the point

        int marker = 0;
        Task? disposeTask = null;
        try
        {
            await controller.WaitForAsync(point);

            // Retained-log doctrine: at every point before the first tick's message
            // publish the log is untouched.
            if (point is InterleavePoints.BeforeSnapshot or InterleavePoints.AfterSnapshot
                or InterleavePoints.BeforeAcceptGuard or InterleavePoints.BeforeConnectedPublish
                or InterleavePoints.TickBeforeMsgGuard or InterleavePoints.TickBeforeMsgPublish)
                Assert.Empty(monitor.Messages);

            lock (recordedGate) marker = recorded.Count;

            switch (action)
            {
                case EnvAction.UpdateConfig:
                    Volatile.Write(ref currentGen, 2);
                    monitor.UpdateConfig(config2);
                    break;
                case EnvAction.Dispose:
                    // DisposeAsync awaits the (frozen) loop: start it now, await after release.
                    disposeTask = monitor.DisposeAsync().AsTask();
                    break;
                case EnvAction.RequestRefresh:
                    monitor.RequestRefresh();
                    break;
            }
        }
        finally
        {
            // A failing assertion above must never leave the loop frozen (disposal
            // would hang); release unconditionally.
            controller.Release();
        }

        // ------------------------------------------------------------ Quiescence
        List<RecordedEvent> EventsAfterMarker()
        { lock (recordedGate) return [.. recorded.Skip(marker)]; }

        void AssertA3NewGenerationConnected()
        {
            lock (clientsGate)
                Assert.Contains(clients, c => c.Fake.Calls.Contains($"connect:{config2.Address}:{config2.Port}"));
        }

        async Task AssertA5DisposeOutcomeAsync()
        {
            await disposeTask!.WaitAsync(TimeSpan.FromSeconds(10));
            Assert.True(monitor._loop.IsCompleted, "the loop task must complete (A5)");
            Assert.False(monitor._loop.IsFaulted, "the loop task must not fault (A5)");
            Assert.Equal(HostConnectionState.Disconnected, monitor.Status.State);
            lock (clientsGate)
                Assert.All(clients, c => Assert.True(c.Fake.Disposed, "every client must be disposed (A5)"));
        }

        void AssertA2TerminalStatusesFollowClientDisposal()
        {
            List<RecordedEvent> all;
            lock (recordedGate) all = [.. recorded];
            foreach (RecordedEvent e in all.Where(e => e.State
                         is HostConnectionState.Retrying or HostConnectionState.AuthFailed
                         or HostConnectionState.Disconnected))
                Assert.True(e.AllClientsDisposed,
                    $"a {e.State} publish must follow the old client's disposal (A2)");
        }

        async Task AssertRefreshOutcomeAsync()
        {
            if (refusedPasswordPreamble)
            {
                // SPECIAL CASE (L3b contract): AuthFailed parking deliberately ignores
                // refresh wakes — the wake is consumed WITHOUT releasing the park, so
                // "loop makes progress" is intentionally NOT asserted here. Parked
                // stays parked (bounded settle: fake-time advances + scheduler yields
                // give a wrong un-park every chance to manifest as a second connect)...
                for (int i = 0; i < 25; i++)
                {
                    time.Advance(TimeSpan.FromMinutes(1));
                    await Task.Yield();
                }
                Assert.Equal(HostConnectionState.AuthFailed, monitor.Status.State);
                Assert.Equal(1, ConnectCount(1));
                // ...and the loop is parked, not dead: a config change still revives it.
                Volatile.Write(ref currentGen, 2);
                monitor.UpdateConfig(config2);
                await Wait.UntilAsync(
                    () => monitor.Status is { State: HostConnectionState.Connected, DaemonVersion.Minor: 2 },
                    "a config change must revive the parked loop");
                Assert.Equal(1, ConnectCount(1)); // the refresh never re-attempted the old generation
                return;
            }
            if (failingConnectPreamble)
            {
                // No connection exists at these points; progress = the wake skips the
                // remaining backoff and another (still-failing) attempt runs promptly.
                await Wait.UntilAsync(() => ConnectCount(1) >= 2,
                    "the wake must trigger another connect attempt");
                await Wait.UntilAsync(
                    () => monitor.Status is { State: HostConnectionState.Retrying, Attempt: 2 },
                    "the second failure must publish Retrying(attempt 2)");
                Assert.False(monitor._loop.IsFaulted);
                return;
            }
            // Connected path: the wake is consumed by the next poll wait — exactly one
            // tick beyond what releasing the freeze alone produces. PollAfterWait's
            // release re-enters the tick loop once on its own (tick #2), so only a
            // third tick proves the action's wake was consumed there.
            int expectedTicks = point == InterleavePoints.PollAfterWait ? 3 : 2;
            await Wait.UntilAsync(() => TickCount() >= expectedTicks,
                "the wake must produce the next tick");
            Assert.Equal(HostConnectionState.Connected, monitor.Status.State);
            Assert.False(monitor._loop.IsFaulted);
        }

        switch (action)
        {
            case EnvAction.UpdateConfig:
                // A3 + self-healing arm: post-quiescence, status/snapshot/log all
                // reflect the NEW config regardless of any benign in-gap publish.
                await Wait.UntilAsync(
                    () => monitor.Status is { State: HostConnectionState.Connected, DaemonVersion.Minor: 2 },
                    "the monitor must reconnect on the new generation (A3)");
                await Wait.UntilAsync(() => monitor.Snapshot?.HostName == "gen2",
                    "the snapshot must reflect the new config");
                await Wait.UntilAsync(
                    () => monitor.Messages.Count == 2 && monitor.Messages.All(m => m.Body == "gen2"),
                    "the new connection's first tick must replace the log");
                AssertA3NewGenerationConnected();
                AssertA1Doctrine(point, EventsAfterMarker());
                break;

            case EnvAction.Dispose:
                await AssertA5DisposeOutcomeAsync();
                break;

            case EnvAction.RequestRefresh:
                await AssertRefreshOutcomeAsync();
                break;
        }

        AssertA2TerminalStatusesFollowClientDisposal();
    }

    // A1 doctrine table (UpdateConfig only): what may / may not be published with the
    // OLD generation AFTER the config change landed at the frozen point. One case arm
    // per doctrine row; convergence on the new generation is asserted by the caller.
    private static void AssertA1Doctrine(string point, List<RecordedEvent> after)
    {
        bool Gen1(string kind) => after.Any(e => e.Kind == kind && e.Gen == 1);
        bool Gen1Connected() => after.Any(e =>
            e is { Kind: "status", State: HostConnectionState.Connected, Gen: 1 });

        switch (point)
        {
            // New config picked up by this or the next attempt: no old-generation
            // publish of any kind.
            case InterleavePoints.BeforeSnapshot:
            case InterleavePoints.AfterSnapshot:
            // The accept guard catches the change: no Connected for the old
            // generation; log intactness was asserted at the freeze.
            case InterleavePoints.BeforeAcceptGuard:
                Assert.False(Gen1Connected(), "no Connected for the old generation (A1)");
                Assert.False(Gen1("snapshot"), "no old-generation snapshot (A1)");
                Assert.False(Gen1("messages"), "no old-generation messages (A1)");
                break;

            // Benign in-gap window: Connected(old gen) MAY be published — the guard
            // already passed — but the tick guards stop any old-generation
            // snapshot/messages from following.
            case InterleavePoints.BeforeConnectedPublish:
                Assert.False(Gen1("snapshot"), "no old-generation snapshot (A1)");
                Assert.False(Gen1("messages"), "no old-generation messages (A1)");
                break;

            // The message guard catches: neither the message nor the snapshot publish
            // of this tick can happen with the old generation.
            case InterleavePoints.TickBeforeMsgGuard:
                Assert.False(Gen1("messages"), "the message guard must catch the change (A1)");
                Assert.False(Gen1("snapshot"), "the snapshot guard must catch the change (A1)");
                break;

            // The snapshot guard catches: no snapshot publish. (This tick's messages
            // were published BEFORE the freeze — before the change landed — which is
            // why only the snapshot half is asserted here.)
            case InterleavePoints.TickBeforeSnapGuard:
                Assert.False(Gen1("snapshot"), "the snapshot guard must catch the change (A1)");
                break;

            // In-gap points: an old-generation publish MAY occur (its guard already
            // passed); correctness is the post-quiescence convergence — the new
            // generation's connect + first-tick log replace — asserted by the caller.
            case InterleavePoints.TickBeforeMsgPublish:
            case InterleavePoints.TickBeforeBuild:
            case InterleavePoints.TickBeforeSnapPublish:
                break;

            // PollAsync returns via the flag: no further old-generation publishes.
            case InterleavePoints.PollBeforeWait:
            case InterleavePoints.PollAfterWait:
                Assert.False(Gen1("snapshot"), "no old-generation snapshot after the flag (A1)");
                Assert.False(Gen1("messages"), "no old-generation messages after the flag (A1)");
                break;

            // Teardown proceeds; the old generation never got past connect here, so
            // nothing old-generation-stamped can exist at all.
            case InterleavePoints.FinallyEnter:
            case InterleavePoints.AfterCtsDispose:
                Assert.False(Gen1Connected(), "no Connected for the old generation (A1)");
                Assert.False(Gen1("snapshot"), "no old-generation snapshot (A1)");
                Assert.False(Gen1("messages"), "no old-generation messages (A1)");
                break;

            // The Retrying publish carries the OLD attempt's vintage — attempt 1, the
            // failed connect it describes — and is expected to appear after the freeze;
            // the reconnect then starts fresh (attempt reset is implied by convergence).
            case InterleavePoints.BeforeRetryPublish:
                Assert.Contains(after, e => e is
                    { Kind: "status", State: HostConnectionState.Retrying, Attempt: 1 });
                break;

            // AuthFailed was published BEFORE the freeze (it describes the refused
            // attempt); after the config change the loop un-parks and must never
            // republish AuthFailed.
            case InterleavePoints.BeforeParkWait:
                Assert.DoesNotContain(after, e => e is
                    { Kind: "status", State: HostConnectionState.AuthFailed });
                break;

            default:
                Assert.Fail($"Unmapped interleave point: {point}");
                break;
        }
    }
}
