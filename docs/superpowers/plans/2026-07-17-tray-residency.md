# Tray Residency (#92) Design + Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Closing the main window keeps Lattice running in the tray (macOS menu bar / Windows notification area) with background polling; reopening restores the live window with no reconnect churn. This is the substrate for the M4 notification surface.

**Architecture:** All lifecycle and cadence *decisions* are pure policy modules with exhaustive transition-table tests (`WindowClosePolicy` in Lattice.App, `PollingCadencePolicy` in Lattice.Core, per the failure-mode-locality razor). The *shell* around them is deliberately thin: a `TrayResidencyController` in Lattice.App that owns the TrayIcon, the hide/show/exit funnels, and the lifetime wiring. `Lattice.Core` gains one input (window visibility) on `HostMonitorManager`; `HostMonitor.cs` and `HostMachine` are **not touched**. A single-instance guard ships as its own pre-req PR.

**Tech Stack:** Avalonia 12.1.0 (`TrayIcon` + `NativeMenu`, `IClassicDesktopStyleApplicationLifetime`, `IActivatableLifetime`), CommunityToolkit.Mvvm, xUnit + Avalonia.Headless.

---

## Part 1 — Framework facts (verified 2026-07-17; every capability claim carries its source)

All claims below were verified against the avalonia-docs MCP server or the Avalonia **12.1.0** ref assemblies in the local NuGet cache (`~/.nuget/packages/avalonia/12.1.0/ref/net10.0/Avalonia.Controls.xml`, `~/.nuget/packages/avalonia.native/12.1.0/lib/net10.0/Avalonia.Native.xml`) — the same package version `Lattice.App.csproj:61` references. "Ref-XML" below means the shipped API documentation XML in those assemblies.

| # | Fact | Source |
|---|------|--------|
| F1 | `TrayIcon` is an Application-level control (persists regardless of open windows) with `Icon` (WindowIcon), `ToolTipText`, `IsVisible`, `Command` (fires on icon click), `Menu` (**must** be a `NativeMenu`, not `Menu`), plus a `Clicked` event and `Dispose()`. Declared via the `TrayIcon.Icons` attached property on `Application`. | docs.avaloniaui.net/controls/navigation/trayicon; ref-XML `T:Avalonia.Controls.TrayIcon` members incl. `E:…TrayIcon.Clicked`, `M:…TrayIcon.Dispose`, `F:…TrayIcon.IconsProperty` |
| F2 | Platform support: **Windows full, macOS full, Linux** only on desktops with `StatusNotifierItem`/`AppIndicator` (confirmed Ubuntu; GNOME may need an extension). | docs.avaloniaui.net/controls/navigation/trayicon §Platform support |
| F3 | Click semantics differ: **macOS click shows the menu; Windows right-click shows the menu, left-click fires `Command`.** | same page, §Practical notes |
| F4 | `Window.Closing`/`OnClosing(WindowClosingEventArgs)` intercepts close; `e.Cancel = true` prevents it. | docs.avaloniaui.net/docs/how-to/window-how-to §Preventing Window Close |
| F5 | `WindowClosingEventArgs` exposes `CloseReason` (`WindowCloseReason`: `Undefined`, `WindowClosing`, `OwnerWindowClosing`, `ApplicationShutdown`, `OSShutdown`) and `IsProgrammatic`. | ref-XML `T:Avalonia.Controls.WindowClosingEventArgs`, `T:Avalonia.Controls.WindowCloseReason` |
| F6 | `IClassicDesktopStyleApplicationLifetime.ShutdownMode` — default `OnLastWindowClose`; `OnExplicitShutdown` keeps the app alive until `Shutdown()`/`TryShutdown()` is called. | docs.avaloniaui.net/api/avalonia/controls/applicationlifetimes/iclassicdesktopstyleapplicationlifetime |
| F7 | `ShutdownRequested` event: raised on OS session end (Windows + macOS) **and on macOS by the Quit menu / Dock-Quit**; if not cancelled, the framework closes each window (raising `Closing` with reason `ApplicationShutdown`/`OSShutdown`) and shuts down. Windows cannot ultimately prevent OS shutdown. | same page, §ShutdownRequested Remarks |
| F8 | macOS auto-appends "Quit App Name" (⌘Q) to the native application menu — we never add our own Quit item there. | docs.avaloniaui.net/docs/platform-specific-guides/macos §Application menu |
| F9 | `MacOSPlatformOptions.ShowInDock` (default `true`) exists — but it is an **AppBuilder startup option** (`.With(new MacOSPlatformOptions {…})`), equivalent to `LSUIElement`. **No runtime activation-policy switching API exists in 12.1.0**: a case-insensitive scan of Avalonia.Native's ref-XML for `ActivationPolicy`/`accessory` returns nothing. | ref-XML `P:Avalonia.MacOSPlatformOptions.ShowInDock`; absence verified by grep over Avalonia.Native.xml (12.1.0) |
| F10 | `IActivatableLifetime.Activated` with `ActivationKind.Reopen` fires exactly in our scenario: *"on MacOS when all the windows are closed, application continues to run in the background and the user clicks the application's dock icon"*. Obtained via `Application.Current.TryGetFeature<IActivatableLifetime>()` (may be null on platforms without it — always null-guard). | ref-XML `T:…IActivatableLifetime`, `F:…ActivationKind.Reopen` |
| F11 | No single-instance API exists in Avalonia: docs search for single-instance/mutex/activation returns only window/dialog pages, and there is no such member in the ApplicationLifetimes namespace (ref-XML). Standard .NET/OS primitives (exclusive lock file + named pipe) are required. | negative result, avalonia-docs search + ref-XML scan |
| F12 | `NativeMenuItem` needs a `Click` handler or `Command` to be enabled; `NativeMenu` is required for tray menus on all platforms. | docs.avaloniaui.net/controls/menus/nativemenu |

Repo facts the design leans on:

- `HostMonitor.SetPollingInterval(int)` already exists and takes effect from the next wait ([HostMonitor.cs:195](../../../src/Lattice.Core/HostMonitor.cs)); `HostMonitorManager` already fans `RegistryChangeKind.IntervalChanged` out to all monitors ([HostMonitorManager.cs:140](../../../src/Lattice.Core/HostMonitorManager.cs)). Cadence switching is a *parameter change*, not new machinery.
- Monitors are **App-lifetime, not window-lifetime**: the manager is composed in `App.OnFrameworkInitializationCompleted` and disposed on `desktop.Exit` ([App.axaml.cs:20-43](../../../src/Lattice.App/App.axaml.cs)). Hiding the window therefore keeps connections alive with zero extra work — "no reconnect churn on restore" is by-construction.
- Headless tests load the real `App` (`tests/Lattice.App.Tests/TestAppBuilder.cs:12` does `Configure<Lattice.App.App>().UseHeadless(...)`), but the composition root only runs under `IClassicDesktopStyleApplicationLifetime`. **Consequence: the TrayIcon must be constructed in code inside that guard, NOT declared in App.axaml** — a XAML declaration would instantiate a platform tray icon in every headless test run on a platform backend that has no tray. This is the same reasoning as the existing guard comment (App.axaml.cs:18-19).
- Settings persistence patterns: polling cadence lives in `LatticeConfig`/`HostRegistry` (Core-owned, `SettingsViewModel.PollingIntervalSeconds` → `RegistryGuard.TryMutate`); UI preferences live in `UiState` via `UiStateStore.Update` (App-owned). Both are used below, each for its own kind of setting.
- Tray icon asset: `Assets/lattice.ico` is already an `AvaloniaResource` (`Lattice.App.csproj:72`) and is used by `ShellWindow.axaml:10`. Reused for the tray in v1; a dedicated monochrome template icon for the macOS menu bar is a visual-fidelity follow-up for the owner to judge on hardware.

---

## Part 2 — The three design questions, settled

Presentation per project discipline: product-language consequence, steelmanned alternative, what-would-prove-me-wrong.

### Q1. Hidden-state polling cadence — **DECIDED (owner, 2026-07-17): configurable, default relaxed with a modest 30 s floor; BOINC-style split is the M4 evolution path**

**Decision:** While the window is hidden, each host's effective polling interval becomes `max(configuredInterval, 30 s)`. A Settings toggle "Full-speed polling while in the tray" (default OFF) removes the floor. On every window restore, all monitors get an immediate `RequestRefresh()` burst fired *before* the window is shown. Honest semantics (the refresh is async — `RequestRefresh` wakes the poll loop, it does not block on the RPC): the first frame may briefly show the last-polled (≤30 s old) data, and fresh data lands one RPC round-trip later — sub-second on a LAN. The burst shrinks the user-visible staleness from "up to the next relaxed tick" to that round-trip.

**Evidence base (source study of the official BOINC Manager, [issue #92 comment](https://github.com/0x8A63F77D/Lattice/issues/92#issuecomment-5002876374); all citations pinned to `BOINC/boinc` master@`8372513`):**

- **The "full stop while hidden" steelman is SOURCE-REFUTED.** The official Manager never stops polling when hidden. Its hidden-state guard ([MainDocument.cpp:1050](https://github.com/BOINC/boinc/blob/83725138b71c2712b31fadc9eebe750727cae9a6/clientgui/MainDocument.cpp#L1050)) pauses only the per-visible-tab *detail* RPCs (`get_results`, `get_project_status`, transfers, statistics, disk); the cheap/urgent layer stays hot precisely to feed the tray: `get_cc_status` at 1 s, `get_messages` every ~1 s **unconditionally**, `get_notices` at 60 s. `get_messages` is deliberately never gated because the daemon's message ring buffer is finite — slow polling can silently drop messages ([MainDocument.cpp:970](https://github.com/BOINC/boinc/blob/83725138b71c2712b31fadc9eebe750727cae9a6/clientgui/MainDocument.cpp#L970)), which makes an aggressive floor a **correctness** risk on busy hosts, not just a latency cost. This is why the floor is 30 s (modest, shape (c) in the research), not 60.
- **Shape (b) — the BOINC-style light-status/heavy-detail split — is the OWNER-APPROVED M4 evolution path.** BOINC keeps a per-host cheap heartbeat (cc_status/messages/notices) at full speed while hidden and pauses only the heavy detail layer. Lattice's single combined snapshot poll cannot express that split today; adopting it means splitting the snapshot poll into a light status poll + heavy detail poll. **Trigger for doing that work:** M4 notification requirements needing sub-minute hidden-state liveness (BOINC targets ~1 s for tray state). **Warning recorded now:** that split changes `HostMonitor.cs` polling semantics, so the verification-sync contract (F# spec + Promela + probe inventory, CLAUDE.md) applies when that day comes.
- **Product consequence (this issue, shape (c)):** with the window closed, Lattice polls each host at most every 30 s instead of every 2–60 s. Laptop background cost stays near-zero; message-gap exposure on busy hosts is half of what a 60 s floor would risk; on reopen, fresh data arrives within about a second of the window appearing (refresh burst). The toggle exists for users who want the hidden heartbeat at full configured speed.
- **What would prove the floor wrong (concrete falsifiers from the source study):** (1) M4 landing a tray/notification signal that needs sub-minute liveness while hidden — that triggers the shape-(b) split rather than another floor tweak; (2) observed message gaps on a busy host whose floored interval outruns the daemon's buffer turnover.
- Deliberately **not** a numeric setting: one boolean keeps the Settings surface flat; the 30 s floor is a named constant (`PollingCadencePolicy.HiddenFloorSeconds`), trivially changeable if evidence says otherwise.

### Q2. macOS Dock presence while tray-resident — **SETTLED: regular app, Dock icon stays** (framework evidence, not taste)

The desirable behavior — hide the Dock icon while the window is closed, restore it on reopen — requires switching `NSApplication.activationPolicy` between `.regular` and `.accessory` at runtime. **Avalonia 12.1.0 does not expose this** (F9): the only knob is startup-time `ShowInDock`, which is whole-session and also implies losing the app's menu-bar/⌘Tab presence (`LSUIElement` semantics). A permanent accessory mode contradicts the issue's own requirement that the restored window behave like a normal app window (menu bar, ⌘Tab).

- **Product consequence:** while tray-resident on macOS, the Lattice Dock icon remains visible (like Slack/Discord, unlike Rectangle/Stats). Clicking it reopens the window (F10 wiring). The menu-bar status item coexists with the Dock icon.
- **Steelmanned alternative:** menu-bar-purist apps (Stats, Ice) hide from the Dock; a monitoring utility arguably belongs in that class, and a *startup-time* "menu-bar-only mode (restart required)" setting using `ShowInDock=false` is technically available today. Rejected for v1: it forfeits the normal-window experience the rest of M2 is built around, for a cosmetic gain, and adds a restart-required setting — the worst settings UX class. Not precluded later.
- **What would prove me wrong:** Avalonia exposing runtime activation-policy switching upstream (worth a periodic check of `MacOSPlatformOptions`' successors), or the owner explicitly preferring permanent menu-bar-only mode after living with the Dock icon.

### Q3. Single-instance behavior — **SETTLED: required, own pre-req PR** (PR A)

With close-to-tray ON by default, "launch while already resident" stops being an edge case and becomes the *primary* re-entry path for users who forget the tray icon exists. A second live instance is actively harmful today: two `HostMonitorManager`s double-poll every host, and two `UiStateStore`/`LatticeConfig` writers last-write-win each other's saves. Avalonia has no built-in support (F11), so:

- An **exclusive lock file** (`instance.lock` in the Lattice config directory, i.e. next to `config.json`) decides primary vs. secondary at process start, before Avalonia init: opened `FileMode.OpenOrCreate` + `FileShare.None` and held for the process lifetime. Per-user by construction (the config dir path is per-user), cross-session by mechanism, and self-cleaning (the OS releases the lock on process death — no stale-lock recovery code). A named `Mutex` was rejected on review: `Local\` is *logon-session*-scoped, so two Unix terminal sessions could each acquire it — exactly the raw-binary launch case this guard exists for. Unix note: .NET emulates `FileShare` between .NET processes via advisory `flock`, which suffices here — the guard only ever coordinates Lattice with Lattice.
- A `NamedPipeServerStream` (`lattice-activate-{SHA256(user name) hex-truncated}`) owned by the primary; a secondary connects, writes the single byte `A`, and exits with code 0 in well under a second. Primary receives → `Dispatcher.UIThread.Post(controller.ShowWindow)`.
- macOS note: a future bundled `.app` launched via Finder/Dock gets single-instancing from macOS itself, but `dotnet run` / raw binary launches (today's reality) do not — the guard is needed on every platform.
- **What would prove me wrong:** a legitimate multi-instance use case (e.g. two config profiles) appearing later — the guard would then gain an opt-out flag; the pipe protocol doesn't constrain that future.

---

## Part 3 — Pure policy modules (named at plan time, per project rule)

Both are single-copy invariant carriers; the shells may not re-derive any of this logic inline. They are small enough that their complete bodies are specified here (the transition tables ARE the spec; C# tasks below are otherwise contract-level).

### 3a. `WindowClosePolicy` — Lattice.App, peer of `TasksOverlayPolicy`/`PartialBarPolicy`

File: `src/Lattice.App/ViewModels/WindowClosePolicy.cs`

```csharp
using Avalonia.Controls;

namespace Lattice.App.ViewModels;

/// <summary>What the shell should do with a window-close attempt.</summary>
public enum CloseVerdict
{
    /// <summary>Cancel the close, hide the window, keep polling in the tray.</summary>
    HideToTray,
    /// <summary>Let the window close AND initiate application shutdown.</summary>
    ExitApplication,
    /// <summary>Let the window close; shutdown is already in progress or externally owned — do not initiate it again.</summary>
    AllowClose,
}

/// <summary>
/// Pure decision core for close-to-tray (issue #92). The ShellWindow.OnClosing
/// shell maps its event args through this single function; no close semantics
/// may live anywhere else.
/// </summary>
public static class WindowClosePolicy
{
    public static CloseVerdict Decide(
        WindowCloseReason reason, bool isProgrammatic, bool exitOnClose, bool exitRequested) =>
        reason switch
        {
            // The platform/framework is already tearing the app down (macOS ⌘Q /
            // Quit menu arrives as ApplicationShutdown per F7; OS logoff/shutdown
            // as OSShutdown). Never fight it, never double-initiate.
            WindowCloseReason.ApplicationShutdown or WindowCloseReason.OSShutdown
                => CloseVerdict.AllowClose,
            // Not applicable to a MainWindow; if it ever fires, closing is correct.
            WindowCloseReason.OwnerWindowClosing => CloseVerdict.AllowClose,
            // Tray "Exit" funnel: the controller sets exitRequested, then calls
            // Close(); the close must proceed and shutdown follows.
            WindowCloseReason.WindowClosing or WindowCloseReason.Undefined
                when exitRequested => CloseVerdict.AllowClose,
            // Programmatic Close() from our own code (not the exit funnel) means
            // the caller intends a real close (e.g. future multi-window teardown).
            WindowCloseReason.WindowClosing or WindowCloseReason.Undefined
                when isProgrammatic => CloseVerdict.AllowClose,
            // The user clicked the close button and opted out of tray residency.
            WindowCloseReason.WindowClosing or WindowCloseReason.Undefined
                when exitOnClose => CloseVerdict.ExitApplication,
            WindowCloseReason.WindowClosing or WindowCloseReason.Undefined
                => CloseVerdict.HideToTray,
        };
}
```

Transition table (the test IS this table, exhaustively — 5 reasons × 2³ flags = 40 rows, xUnit `[Theory]` over all of them; collapsed here by dominance order `reason > exitRequested > isProgrammatic > exitOnClose`):

| `reason` | `exitRequested` | `isProgrammatic` | `exitOnClose` | verdict |
|---|---|---|---|---|
| `ApplicationShutdown` | * | * | * | `AllowClose` |
| `OSShutdown` | * | * | * | `AllowClose` |
| `OwnerWindowClosing` | * | * | * | `AllowClose` |
| `WindowClosing`/`Undefined` | true | * | * | `AllowClose` |
| `WindowClosing`/`Undefined` | false | true | * | `AllowClose` |
| `WindowClosing`/`Undefined` | false | false | true | `ExitApplication` |
| `WindowClosing`/`Undefined` | false | false | false | `HideToTray` |

Notes: `WindowCloseReason` is a **framework** enum, so the switch names every current case and the repo's CS8524 convention applies (pragma + comment) for future framework-added members — this is the sanctioned exception, not a domain-DU wildcard. `Undefined` is grouped with `WindowClosing` deliberately: an unattributed close on the platforms that can't report a reason must behave like a user close, and the `exitRequested`/`isProgrammatic` guards still catch our own funnels.

### 3b. `PollingCadencePolicy` — Lattice.Core

File: `src/Lattice.Core/PollingCadencePolicy.cs`

```csharp
namespace Lattice.Core;

/// <summary>
/// Pure cadence decision for tray residency (issue #92): what interval should
/// monitors actually poll at, given the configured interval and UI visibility.
/// HostMonitorManager is the only caller; monitors never see visibility.
/// </summary>
public static class PollingCadencePolicy
{
    /// <summary>Relaxed floor while hidden: never poll faster than this (seconds).
    /// 30, not 60 — get_messages gap risk on busy hosts bounds the floor (Q1).</summary>
    public const int HiddenFloorSeconds = 30;

    public static int EffectiveIntervalSeconds(
        int configuredSeconds, bool windowVisible, bool fullSpeedHidden) =>
        windowVisible || fullSpeedHidden
            ? configuredSeconds
            : Math.Max(configuredSeconds, HiddenFloorSeconds);
}
```

Transition table (test = exact `[Theory]` rows; `AllowedPollingIntervals` = 2, 5, 10, 30, 60):

| `configured` | `windowVisible` | `fullSpeedHidden` | effective |
|---|---|---|---|
| 2/5/10/30/60 | true | * | configured (unchanged) |
| 2/5/10/30/60 | false | true | configured (unchanged) |
| 2, 5, 10 | false | false | **30** (floored) |
| 30 | false | false | 30 (at the floor — passes through) |
| 60 | false | false | 60 (above the floor — passes through) |

**Manager invariant (I-CAD):** at every instant after any of {monitor created, visibility changed, `IntervalChanged` raised}, every live monitor's active interval equals `EffectiveIntervalSeconds(registry.PollingIntervalSeconds, visible, fullSpeedHidden)`. Single recompute funnel `ApplyCadence()` inside `HostMonitorManager`; the three triggers may not call `SetPollingInterval` directly.

Why C# and not F# for these two: no recursion scheme, no DU folding, no data pipeline — a guard cascade and a `max`. The repo's established policy-module idiom for exactly this shape is C# static classes with transition-table tests (`PartialBarPolicy`, `TasksOverlayPolicy`, `ColumnVisibilityPolicy`), and `PollingCadencePolicy` in C# additionally stays inside Stryker.NET's supported scope (F# is excluded, CLAUDE.md §Mutation gates). No F# tasks exist in this plan; if an implementer discovers a genuinely stateful lifecycle machine while wiring (more than these two functions), STOP and escalate to the controller rather than growing the shells.

---

## Part 4 — Settings additions

| Setting | Where | Wire format & default trick | UI |
|---|---|---|---|
| Close-to-tray | `UiState` (App-owned UI lifecycle pref) | `bool ExitOnClose = false` — **named in the negative so the JSON-missing default (`false`) = close-to-tray ON**, matching the issue default; older `ui-state.json` files need no migration | Settings toggle, label (localized): "Closing the window keeps Lattice running in the tray" — displayed toggle state is the *inverse* of the stored bool (VM inverts; contract in Task C2) |
| Hidden-cadence mode | `LatticeConfig` (Core-owned polling policy, sibling of `pollingIntervalSeconds`) | `bool FullSpeedHiddenPolling = false` — same missing-key-safe naming trick: absent ⇒ `false` ⇒ relaxed default. Positional record component appended LAST with a default value so existing `config.json` files deserialize unchanged | Settings toggle: "Full-speed polling while in the tray" |

`HostRegistry` gains `SetFullSpeedHiddenPolling(bool)` which persists via the existing `Mutate(... , RegistryChangeKind.IntervalChanged, null)` path — **reusing `IntervalChanged`** ("cadence parameters changed") rather than adding an enum case; the manager's handler recomputes via `ApplyCadence()` either way. This dodges the repo-wide exhaustive-switch sweep a new `RegistryChangeKind` member would force, and no consumer distinguishes the two causes.

New localized strings (`src/Lattice.App/Localization/Strings.resx`): `TrayShowWindow`, `TrayExit`, `SettingsCloseToTrayLabel`, `SettingsCloseToTrayDescription`, `SettingsFullSpeedHiddenLabel`, `SettingsFullSpeedHiddenDescription`, `TrayToolTip` (= "Lattice").

---

## Part 5 — Tray surface design (the thin shell)

New file `src/Lattice.App/Infrastructure/TrayResidencyController.cs` — the ONLY place that touches TrayIcon/Window visibility/shutdown. Constructed in `App.OnFrameworkInitializationCompleted` **inside the desktop-lifetime guard** (headless rationale in Part 1), after the manager/shell exist.

Owns:
- **TrayIcon (code-constructed, F1):** `Icon` = `new WindowIcon(AssetLoader.Open(avares://Lattice/Assets/lattice.ico))` (the assembly name is `Lattice`, not `Lattice.App` — `Lattice.App.csproj:10` `<AssemblyName>`; matches every existing `avares://Lattice/...` URI in App.axaml), `ToolTipText`, `Command` = show-window (Windows left-click, F3), `Menu` = `NativeMenu` with `TrayShowWindow` item + separator + `TrayExit` item (F12: each gets a command). Registered via `TrayIcon.SetIcons(app, new TrayIcons { trayIcon })`; disposed in the existing `desktop.Exit` handler (before manager teardown).
  - *Menu is "Show window" + "Exit" — not the issue's literal "Show/Hide"*: a dynamic-header toggle item relies on undocumented runtime NativeMenu mutation behavior, and the Hide half is already served by the window close button. Deliberate simplification, owner can veto on hardware.
- **`ShowWindow()`:** `manager.SetWindowVisible(true)` and `manager.RequestRefreshAll()` FIRST (the refresh burst starts its RPC round-trips before the first frame renders, Q1), then `mainWindow.Show()`; if `WindowState == Minimized` reset to `Normal`; `Activate()`. Idempotent when already visible. The burst is fire-and-forget — showing is never gated on RPC completion; fresh snapshots land via the normal event path within ~one round-trip.
- **`HideToTray()`:** `mainWindow.Hide()` then `manager.SetWindowVisible(false)`.
- **`ExitApplication()`:** sets `ExitRequested = true` (read by the close policy shell), then `desktop.Shutdown()`.
- **Lifetime wiring:**
  - `desktop.ShutdownMode = ShutdownMode.OnExplicitShutdown` (F6) — the app's lifetime is owned by explicit exits (tray Exit, exit-on-close verdict, platform shutdown per F7), never implicitly by the last window.
  - `IActivatableLifetime` (null-guarded `TryGetFeature`, F10): on `Activated` with `ActivationKind.Reopen` → `ShowWindow()` (macOS Dock-icon click while hidden).
  - Single-instance activation pings (PR A's pipe) also land on `ShowWindow()`.

`ShellWindow.OnClosing` override (the policy shell — total code):

```csharp
protected override void OnClosing(WindowClosingEventArgs e)
{
    base.OnClosing(e);
    if (_tray is not { } tray)   // headless tests / no controller: default close behavior
        return;
    switch (WindowClosePolicy.Decide(e.CloseReason, e.IsProgrammatic,
                exitOnClose: tray.ExitOnClose, exitRequested: tray.ExitRequested))
    {
        case CloseVerdict.HideToTray:
            e.Cancel = true;
            tray.HideToTray();
            break;
        case CloseVerdict.ExitApplication:
            tray.NotifyExitingViaClose();   // suppresses re-entrant policy on the shutdown-driven close
            tray.ExitApplication();
            break;
        case CloseVerdict.AllowClose:
            break;
    }
}
```

(`ExitApplication` verdict path: `Shutdown()` re-closes the window with `CloseReason.ApplicationShutdown`, which the policy maps to `AllowClose` — the table already guarantees no loop; `NotifyExitingViaClose` is belt-and-braces ordering, contract in Task C3.)

`HostMonitorManager` additions (Core): `SetWindowVisible(bool)`, `RequestRefreshAll()`, private `ApplyCadence()` honoring invariant I-CAD (Part 3b), and `CreateMonitor` seeding new monitors with the *effective* (not raw) interval so hosts added while hidden start relaxed. **`src/Lattice.Core/HostMonitor.cs` is not touched; if an implementer finds they need to, STOP — that triggers the verification-sync rule (F# spec + Promela) and must go back to the controller.** Commit messages for PR B state this explicitly.

---

## Part 6 — PR breakdown

Small mergeable PRs, in dependency order. A and B are machine-gated (Codex + CI, autonomous merge per repo cadence). **C's merge gate is OWNER-ON-HARDWARE — no autonomous merge.**

| PR | Content | Merge gate |
|---|---|---|
| **A — single-instance guard** (pre-req, Q3) | `SingleInstanceGuard` + `Program.cs` wiring + tests | Codex + CI, autonomous |
| **B — Core cadence substrate** (Q1 plumbing) | `PollingCadencePolicy` + `LatticeConfig.FullSpeedHiddenPolling` + `HostRegistry` setter + manager `SetWindowVisible`/`RequestRefreshAll`/`ApplyCadence` + tests | Codex + CI, autonomous |
| **C — tray integration** (the visible feature) | `WindowClosePolicy` + `TrayResidencyController` + TrayIcon + `OnClosing` shell + `UiState.ExitOnClose` + Settings toggles + strings + `IActivatableLifetime` wiring | **Owner-on-hardware acceptance (visual/interactive PR)**; Codex + CI still run first |

Task routing: all tasks below are C# / XAML at **contract level**, Sonnet-routed. Every dispatch prompt carries the judgment-routing line: *"If any contract here is ambiguous, under-specified, or contradicts the code you find, STOP and escalate to the controller — do not improvise."* First failed fix ⇒ escalate to Opus per standing tripwire.

### PR A tasks

#### Task A1: `SingleInstanceGuard`

**Files:** Create `src/Lattice.App/Infrastructure/SingleInstanceGuard.cs`; Test `tests/Lattice.App.Tests/SingleInstanceGuardTests.cs`

**Invariant I-GUARD (structural, Codex R3):** the guard may prevent a launch ONLY when a live primary actually answered the activation ping. Every other failure — unreadable/read-only lock file, permission change, path-is-a-directory, missing config dir, pipe errors — degrades to launching WITHOUT the guard (fail-open, trace warning). A broken lock file must not brick a monitoring app; this is the same philosophy as `App.LoadRegistryWithFallback`. Double-launch risk in that degraded state exists only in already-broken environments and is the lesser harm.

Contract:
- `static AcquireResult TryAcquire(string lockPath, string pipeName)` where `AcquireResult` is `Acquired(SingleInstanceGuard)` / `Contended` / `Unavailable` (small nested DU-style record hierarchy or enum + out param — implementer's choice, exhaustive switch at the call site). Opens `lockPath` (`instance.lock` in the Lattice config dir; default derived from `LatticeConfig.DefaultPath`, parent dir created with the same user-only Unix mode as `LatticeConfig.Save`) with `FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None` and holds the handle. Classification: success → `Acquired`; `IOException` on open (the lock-contention shape on both platforms) → `Contended`; `UnauthorizedAccessException` and path-shape errors (`DirectoryNotFoundException`, path-is-a-directory) → `Unavailable` — these mean the guard cannot operate, NOT that another instance exists. `Contended` is deliberately only *probable* contention — the pipe round-trip in A2 is the liveness oracle that confirms or refutes it (I-GUARD). No stale-lock handling needed: the OS releases the lock when the holder dies (Q3 rationale — a named `Mutex` was rejected because `Local\` is logon-session-scoped and fails the multi-terminal Unix case).
- Instance members: `StartActivationListener(Action onActivate)` — background `NamedPipeServerStream` accept-loop, one connection at a time, reads 1 byte, invokes `onActivate` (caller marshals to UI thread), loops; swallows per-connection `IOException`s (a dying secondary must not kill the listener); `Dispose()` cancels the loop and closes/releases the lock file handle.
- `static bool SignalPrimary(string pipeName)` — connect with 2 s timeout, write byte `A`, return success. Never throws.
- Default pipe name: `lattice-activate-` + first 16 hex chars of SHA-256 of `Environment.UserName` (pipe namespace is machine-global on Windows, and .NET's Unix pipe endpoints live in a shared temp dir; per-user suffix prevents cross-user collisions/hijack ambiguity).
- Tests (red-first, no wall-clock settles, lock file in a temp dir): acquire→second acquire on the same path returns `Contended`; release→re-acquire returns `Acquired`; lock path pointing at a DIRECTORY returns `Unavailable` (not `Contended` — the I-GUARD classification test); `SignalPrimary` before any listener returns false; listener + signal round-trip observed via a `TaskCompletionSource` (settle on the observed call, per repo test canon). All same-process — `FileShare.None` and pipes both enforce in-proc too.

- [ ] Write failing tests → run (`dotnet test --filter SingleInstanceGuard`) → implement → green → commit `feat(app): single-instance guard (#92)`

#### Task A2: `Program.cs` wiring

**Files:** Modify `src/Lattice.App/Program.cs`, `src/Lattice.App/App.axaml.cs`

Contract: `Main` calls `TryAcquire` before `BuildAvaloniaApp()` and switches exhaustively on the result (I-GUARD is the spec):
- `Acquired` → normal primary launch; guard stored in a static handed to `App`, which (desktop-guard block only) starts the listener with a callback that (until PR C lands) posts `MainWindow.Show()`+`Activate()` via `Dispatcher.UIThread`; PR C swaps the callback body to `TrayResidencyController.ShowWindow`. Guard disposed in `desktop.Exit`.
- `Contended` → `SignalPrimary`; on **success** (a live primary answered) → return 0 — the ONLY path that ends a launch. On **failure** (no listener answered within the timeout: stale-looking lock, non-Lattice holder, broken pipe) → the contention was refuted by the liveness oracle → launch WITHOUT the guard, trace warning.
- `Unavailable` → launch WITHOUT the guard, trace warning. Never exit.
Headless tests never call `Program.Main` (they build via `TestAppBuilder`) — assert nothing breaks by running the full existing suite.

- [ ] Implement → full `dotnet test` green → commit `feat(app): activate existing instance on relaunch (#92)` → push, PR A, Codex review

### PR B tasks

#### Task B1: `PollingCadencePolicy` (full code in Part 3b)

**Files:** Create `src/Lattice.Core/PollingCadencePolicy.cs`; Test `tests/Lattice.Tests/PollingCadencePolicyTests.cs`

- [ ] Failing `[Theory]` = exact transition table (Part 3b, all rows incl. every allowed interval) → implement verbatim from Part 3b → green → commit `feat(core): polling cadence policy for hidden window (#92)`

#### Task B2: config field + registry setter

**Files:** Modify `src/Lattice.Core/LatticeConfig.cs`, `src/Lattice.Core/HostRegistry.cs`; Test existing config/registry test files

Contract: append `bool FullSpeedHiddenPolling = false` as the LAST positional component with default (Part 4 rationale — old JSON round-trips; add explicit test: deserialize a pre-#92 fixture string, assert `false`). `Load` normalization: nothing needed (bool can't be out-of-range). `HostRegistry.SetFullSpeedHiddenPolling(bool)` mirrors `SetPollingInterval` incl. no-op-on-equal guard, raises `IntervalChanged` (Part 4 rationale in a code comment). Round-trip save/load test.

- [ ] Red-first per contract → green → commit `feat(core): persist hidden-polling mode (#92)`

#### Task B3: manager cadence funnel

**Files:** Modify `src/Lattice.Core/HostMonitorManager.cs`; Test `tests/Lattice.Tests/HostMonitorManagerTests.cs` (existing fake-client fixtures)

Contract: private `_windowVisible = true`; `SetWindowVisible(bool)` + the `IntervalChanged` case both funnel into private `ApplyCadence()` (computes effective via policy, fans `SetPollingInterval` under `_gate`); `CreateMonitor` seeds with effective interval; `RequestRefreshAll()` forwards `RequestRefresh()` to all monitors. Invariant I-CAD verbatim in a doc comment. Tests: visibility flip changes the interval monitors receive (observable via existing fake/TimeProvider fixtures); host added while hidden starts at the 30 s floor; a 60 s-configured host stays at 60 while hidden (floor never *slows* an already-slow host); `IntervalChanged` while hidden stays floored; full-speed flag bypasses floor. **Do not touch HostMonitor.cs** (Part 5 escalation rule; commit message states "no HostMonitor semantics changed → no verification-artifact update needed").

- [ ] Red-first per contract → green → full suite → commit `feat(core): window-visibility cadence input on HostMonitorManager (#92)` → push, PR B, Codex review

### PR C tasks

#### Task C1: `WindowClosePolicy` (full code in Part 3a)

**Files:** Create `src/Lattice.App/ViewModels/WindowClosePolicy.cs`; Test `tests/Lattice.App.Tests/WindowClosePolicyTests.cs`

- [ ] Failing exhaustive 40-row `[Theory]` (Part 3a table) → implement verbatim from Part 3a → green → commit `feat(app): window close policy (#92)`

#### Task C2: `UiState.ExitOnClose` + Settings toggles + strings

**Files:** Modify `src/Lattice.App/Infrastructure/UiStateStore.cs` (`UiState` record: append `bool ExitOnClose = false`), `src/Lattice.App/ViewModels/SettingsViewModel.cs`, `src/Lattice.App/Views/SettingsView.axaml`, `src/Lattice.App/Localization/Strings.resx` (keys in Part 4); Tests: existing `UiStateStore`/Settings headless test files

Contract: VM property `CloseToTray` (inverted persistence — get `!state.ExitOnClose`, set via `UiStateStore.Update`, matching the Theme-toggle pattern in this VM); `FullSpeedHiddenPolling` property mirroring `PollingIntervalSeconds`'s `RegistryGuard.TryMutate` pattern. Two `ToggleSwitch` rows in SettingsView using the existing settings-row layout. Headless tests: toggle → persisted JSON asserts; pre-#92 ui-state.json fixture loads with `ExitOnClose == false`.

- [ ] Red-first → green → commit `feat(app): tray residency settings (#92)`

#### Task C3: `TrayResidencyController` + App wiring

**Files:** Create `src/Lattice.App/Infrastructure/TrayResidencyController.cs`; Modify `src/Lattice.App/App.axaml.cs`, `src/Lattice.App/Views/ShellWindow.axaml.cs`

Contract = Part 5 in full, plus: `ExitOnClose` read live from `UiStateStore` at decide time (not cached — respects the store's read-modify-write doctrine); `NotifyExitingViaClose()` sets `ExitRequested` so the shutdown-driven second `Closing` (reason `ApplicationShutdown`) short-circuits identically on platforms that report `Undefined`; controller handed to `ShellWindow` via property (`_tray`), not DataContext (it's not a VM). `OnClosing` override = the exact snippet in Part 5. TrayIcon menu commands = plain `RelayCommand`s on the controller. Dispose order in `desktop.Exit`: tray first, then existing teardown chain. Headless: controller never constructed (guard) — assert existing suite still green; add a headless test that `ShellWindow` without a controller closes normally (regression: `_tray is null` path).
Manual smoke on the dev Mac (implementer, pre-review): window closes → app stays in menu bar → menu Show restores instantly with live data → Exit quits cleanly (`ps` confirms).

- [ ] Implement per contract → full suite green → manual smoke → commit `feat(app): tray residency controller + close-to-tray (#92)`

#### Task C4: platform reopen paths

**Files:** Modify `src/Lattice.App/Infrastructure/TrayResidencyController.cs` (small), `src/Lattice.App/App.axaml.cs`

Contract: `IActivatableLifetime` wiring per Part 5 (F10; null-guarded — headless/Win/Linux may lack the feature); PR A's activation callback swapped to `controller.ShowWindow`. Commit `feat(app): dock/relaunch reopen paths (#92)` → push, PR C, Codex review → **stop at owner gate**.

---

## Part 7 — Verification split & owner-on-hardware acceptance (PR C merge gate)

Machine-gated (CI + Codex, Tier 0): both policy transition tables (exhaustive), cadence invariant tests, settings persistence + back-compat fixtures, single-instance round-trip, `_tray is null` close regression, full existing suite (guards the headless-boot risk from Part 1).

**Owner-on-hardware checklist (macOS dev machine; this list is the PR C merge gate — check each box in the PR):**

- [ ] Close window → app stays in menu bar, Dock icon remains (Q2 expected behavior), BOINC data still updating when reopened
- [ ] Menu-bar icon → menu shows "Show window"/"Exit"; Show restores window with prior size/position/view instantly (no reconnect flicker in the rail)
- [ ] Reopen after >30 s hidden: fresh data lands within ~1 s of the window appearing (refresh burst; a brief flash of last-polled data is acceptable — what must NOT happen is data sitting stale until the next relaxed tick)
- [ ] Dock icon click while hidden reopens the window (F10 path)
- [ ] ⌘Q (app menu Quit) fully exits from both visible and hidden states; process gone
- [ ] Settings: turn close-to-tray OFF → close button exits the app; turn back ON → residency returns
- [ ] Launch a second `dotnet run` while resident → existing window surfaces, second process exits
- [ ] Tray icon legibility in light + dark menu bar (visual-fidelity judgment; monochrome template icon follow-up if it reads poorly)

**Known platform gaps (explicit, not silent):** Windows and Linux tray legs ship code-complete but hardware-unverified (owner has macOS only — dev-environment reality). Close-interception is the Windows-critical path and is covered by the policy tests, but tray-icon visuals/left-click behavior on Windows (F3) and Linux appindicator presence (F2) need a hardware pass when available → file a follow-up issue at PR C merge time. Linux fallback risk is bounded: a missing tray icon means close-to-tray strands the app headless-invisible; mitigation contract (Task C3): if `TrayIcon` platform init fails or the platform reports no tray support, the controller falls back to exit-on-close semantics (never hide without a way back). This fallback is part of the C3 contract, not an afterthought.

## Self-review notes

- Issue scope → tasks: tray icon+menu (C3), close semantics+toggle (C1/C2/C3), restore-with-state (C3, by-construction argument Part 1), cadence (B1–B3, Q1), macOS Dock (Q2 settled + C4), single instance (Q3, A1/A2). Verification split honored (policies pure + tables; tray = owner gate).
- No `HostMonitor.cs`/`HostMachine` changes anywhere; verification-sync rule not triggered (stated in B3's commit contract).
- Type-consistency pass: `CloseVerdict`/`Decide` signature match between Part 3a and Part 5 snippet; `SetWindowVisible`/`RequestRefreshAll` names match across Parts 3b/5/B3/C3; `FullSpeedHiddenPolling` spelled identically in Parts 3b/4/B2/C2.
