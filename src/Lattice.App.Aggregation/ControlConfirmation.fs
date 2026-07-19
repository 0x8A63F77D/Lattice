namespace Lattice.App.Aggregation

open System

/// M3 control operations on one task (always exactly one host + result).
type TaskOp =
    | TaskSuspend
    | TaskResume
    | TaskAbort

/// M3 control operations on a project attachment.
type ProjectOp =
    | ProjectSuspend
    | ProjectResume
    | ProjectUpdate
    | ProjectDetach

/// Run-mode lanes. App.Aggregation stays free of GuiRpc types (module rule);
/// the C# adapter maps these to Lattice.Boinc.GuiRpc enums with an exhaustive
/// switch (CS8524 discipline).
type ModeLane =
    | CpuLane
    | GpuLane
    | NetworkLane

/// Permanent modes a user can select directly (restore is CancelTemporary).
type PermMode =
    | ModeAlways
    | ModeAuto
    | ModeNever

/// What the user asked for on one host's run-mode surface (DI-4).
type ModeIntent =
    | SetPermanent of ModeLane * PermMode
    | Snooze of duration: TimeSpan          // CPU lane, temporary Never (design 1.4)
    | CancelTemporary of ModeLane           // wire: restore

/// One user-initiated control intent carrying its blast radius.
type ControlIntent =
    | OfTask of TaskOp
    | OfProject of ProjectOp * hostCount: int   // parent row spans hostCount hosts
    | OfMode of ModeIntent                      // single host by construction (DI-4)

type ConfirmSeverity =
    | Caution        // reversible but multi-host: the dialog is the blast-radius receipt
    | Destructive    // permanently loses computed work

type ConfirmationClass =
    | Instant
    | Confirm of ConfirmSeverity

/// Total mapping intent -> confirmation class (design Part 3 / DI-1 / DI-2).
/// The dialog CONTENT (strings, host enumeration) is the view layer's job;
/// this module decides only the class.
module ConfirmationPolicy =

    let classify (intent: ControlIntent) : ConfirmationClass =
        match intent with
        | OfTask TaskAbort -> Confirm Destructive
        | OfTask (TaskSuspend | TaskResume) -> Instant
        | OfProject (ProjectDetach, _) -> Confirm Destructive
        | OfProject ((ProjectSuspend | ProjectResume | ProjectUpdate), hostCount) ->
            if hostCount > 1 then Confirm Caution else Instant
        | OfMode (SetPermanent _ | Snooze _ | CancelTemporary _) -> Instant

/// Pins the snooze/restore wire semantics (design 1.4) in one tested place.
module RunModePolicy =

    /// Wire-level mode values (RUN_MODE_* in BOINC's lib/common_defs.h).
    type WireMode =
        | WireAlways
        | WireAuto
        | WireNever
        | WireRestore

    /// Total mapping intent -> (lane, mode, duration) RPC arguments.
    /// duration Zero = permanent; positive = temporary override (snooze).
    let toWireArgs (intent: ModeIntent) : ModeLane * WireMode * TimeSpan =
        match intent with
        | SetPermanent (lane, ModeAlways) -> lane, WireAlways, TimeSpan.Zero
        | SetPermanent (lane, ModeAuto) -> lane, WireAuto, TimeSpan.Zero
        | SetPermanent (lane, ModeNever) -> lane, WireNever, TimeSpan.Zero
        | Snooze duration -> CpuLane, WireNever, duration
        | CancelTemporary lane -> lane, WireRestore, TimeSpan.Zero

    /// "Snoozed until" derivation from cc_status's *_mode_delay (design 1.4):
    /// Some deadline while a temporary override is active, else None.
    let temporaryUntil (now: DateTimeOffset) (modeDelaySeconds: float) : DateTimeOffset option =
        if modeDelaySeconds > 0.0 then Some (now.AddSeconds modeDelaySeconds) else None
