module Lattice.App.Aggregation.ControlConfirmationTests

open System
open Xunit
open FsCheck.Xunit
open Lattice.App.Aggregation

// --- ConfirmationPolicy.classify: exhaustive transition table (design Part 3 / DI-1 / DI-2).
//     Every ControlIntent construction path appears below, with the hostCount boundary (1 vs 2)
//     pinned for the reversible project ops. ---

[<Fact>]
let ``task abort classifies Confirm Destructive`` () =
    Assert.Equal(Confirm Destructive, ConfirmationPolicy.classify (OfTask TaskAbort))

[<Fact>]
let ``task suspend and resume classify Instant`` () =
    Assert.Equal(Instant, ConfirmationPolicy.classify (OfTask TaskSuspend))
    Assert.Equal(Instant, ConfirmationPolicy.classify (OfTask TaskResume))

[<Fact>]
let ``project detach classifies Confirm Destructive at every host count`` () =
    Assert.Equal(Confirm Destructive, ConfirmationPolicy.classify (OfProject(ProjectDetach, 1)))
    Assert.Equal(Confirm Destructive, ConfirmationPolicy.classify (OfProject(ProjectDetach, 2)))
    Assert.Equal(Confirm Destructive, ConfirmationPolicy.classify (OfProject(ProjectDetach, 8)))

[<Fact>]
let ``reversible project ops on one host classify Instant`` () =
    for op in [ ProjectSuspend; ProjectResume; ProjectUpdate ] do
        Assert.Equal(Instant, ConfirmationPolicy.classify (OfProject(op, 1)))

[<Fact>]
let ``reversible project ops on two hosts classify Confirm Caution`` () =
    for op in [ ProjectSuspend; ProjectResume; ProjectUpdate ] do
        Assert.Equal(Confirm Caution, ConfirmationPolicy.classify (OfProject(op, 2)))

[<Fact>]
let ``reversible project ops on a degenerate zero host count classify Instant`` () =
    // hostCount <= 1 is the single-host side of the DI-2 boundary; 0 cannot arise
    // from a real parent row but the mapping is total, so pin it.
    for op in [ ProjectSuspend; ProjectResume; ProjectUpdate ] do
        Assert.Equal(Instant, ConfirmationPolicy.classify (OfProject(op, 0)))

[<Fact>]
let ``every mode intent classifies Instant`` () =
    Assert.Equal(Instant, ConfirmationPolicy.classify (OfMode(SetPermanent(CpuLane, ModeAlways))))
    Assert.Equal(Instant, ConfirmationPolicy.classify (OfMode(SetPermanent(GpuLane, ModeAuto))))
    Assert.Equal(Instant, ConfirmationPolicy.classify (OfMode(SetPermanent(NetworkLane, ModeNever))))
    Assert.Equal(Instant, ConfirmationPolicy.classify (OfMode(Snooze(TimeSpan.FromMinutes 15.0))))
    Assert.Equal(Instant, ConfirmationPolicy.classify (OfMode(CancelTemporary CpuLane)))

// FsCheck: the DI-1 invariant — the two work-destroying intents are never Instant,
// regardless of how the intent was constructed (guards future refactors of classify).
[<Property>]
let ``abort and detach never classify Instant`` (hostCount: int) =
    ConfirmationPolicy.classify (OfTask TaskAbort) <> Instant
    && ConfirmationPolicy.classify (OfProject(ProjectDetach, hostCount)) <> Instant

// --- RunModePolicy.toWireArgs: full table (design 1.4 wire semantics) ---

[<Fact>]
let ``SetPermanent maps each perm mode to its wire mode with zero duration`` () =
    Assert.Equal((CpuLane, RunModePolicy.WireAlways, TimeSpan.Zero),
                 RunModePolicy.toWireArgs (SetPermanent(CpuLane, ModeAlways)))
    Assert.Equal((GpuLane, RunModePolicy.WireAuto, TimeSpan.Zero),
                 RunModePolicy.toWireArgs (SetPermanent(GpuLane, ModeAuto)))
    Assert.Equal((NetworkLane, RunModePolicy.WireNever, TimeSpan.Zero),
                 RunModePolicy.toWireArgs (SetPermanent(NetworkLane, ModeNever)))

[<Fact>]
let ``SetPermanent passes every lane through unchanged`` () =
    for lane in [ CpuLane; GpuLane; NetworkLane ] do
        let mappedLane, _, _ = RunModePolicy.toWireArgs (SetPermanent(lane, ModeAlways))
        Assert.Equal(lane, mappedLane)

[<Fact>]
let ``Snooze maps to a temporary CPU-lane Never`` () =
    let duration = TimeSpan.FromHours 1.0
    Assert.Equal((CpuLane, RunModePolicy.WireNever, duration),
                 RunModePolicy.toWireArgs (Snooze duration))

[<Fact>]
let ``CancelTemporary maps to a permanent restore on its lane`` () =
    for lane in [ CpuLane; GpuLane; NetworkLane ] do
        Assert.Equal((lane, RunModePolicy.WireRestore, TimeSpan.Zero),
                     RunModePolicy.toWireArgs (CancelTemporary lane))

// --- RunModePolicy.snoozeUntil: the "Snoozed until" derivation (design 1.4) ---

[<Fact>]
let ``snoozeUntil is None at zero and negative delays`` () =
    let now = DateTimeOffset(2026, 7, 19, 12, 0, 0, TimeSpan.Zero)
    Assert.Equal(None, RunModePolicy.snoozeUntil now true 0.0)
    Assert.Equal(None, RunModePolicy.snoozeUntil now true -1.0)

[<Fact>]
let ``snoozeUntil adds a positive delay to now when the CPU mode is Never`` () =
    let now = DateTimeOffset(2026, 7, 19, 12, 0, 0, TimeSpan.Zero)
    Assert.Equal(Some(now.AddSeconds 900.0), RunModePolicy.snoozeUntil now true 900.0)

[<Fact>]
let ``snoozeUntil is None for a positive-delay override that is not Never`` () =
    // A temporary Always/Auto override (another client) carries a positive delay too,
    // but it is not a snooze — computing is not paused.
    let now = DateTimeOffset(2026, 7, 19, 12, 0, 0, TimeSpan.Zero)
    Assert.Equal(None, RunModePolicy.snoozeUntil now false 900.0)
