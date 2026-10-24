---
paths:
  - "src/Lattice.App/**"
  - "src/Lattice.App.Aggregation/**"
  - "tests/Lattice.App.Tests/**"
  - "tests/Lattice.Aggregation.Tests/**"
  - "tests/Lattice.VisualTests/**"
---

# Lattice App UI canon

Settled repo decisions for the App layer, each with the reason it exists. Deviating needs a
fresh ruling, not a quiet change. Framework behaviour behind them: `avalonia-framework-facts.md`.

## Visual work has two lanes

- Logic/rendering CORRECTNESS (e.g. "clicking the header ⇒ rendered row order == expected") has
  an objective criterion: pin it with a headless assertion on the rendered result.
- Visual FIDELITY (spacing, weight, colour, an added glyph) has no machine judge: do not iterate
  on it autonomously. Hand the owner ONE reviewable version; their eye is the gate. Do not add
  speculative visual extras mid-fix.
- NEW visual surfaces (a new pill, button surface or chrome — not reuse of an approved component
  family) go through the owner's design session before landing: build the machine-verified
  functional version, stage screenshots + a surface inventory, and hold.
- Headless tests verify only what you thought to assert; they miss framework defaults nobody
  questioned (e.g. `CanUserResizeColumns` defaults false), theme-overlay colours and interaction
  gaps. View PRs get an owner eyeball pass when asked.

## Decisions

- Loading indicators: stock indeterminate `ProgressBar`, `IsIndeterminate="{Binding IsLoading}"`,
  `Width=160 HorizontalAlignment=Center` (the Statistics look). Never `FAProgressRing` for a state
  that can stay visible (input starvation). Guard: `LoadingIndicatorActivityTests`.
- Menu dividers: raw `<Separator/>`. Guards: `MenuSeparatorGeometryTests`,
  `MenuSeparatorVisualTests`.
- Grid order is view-owned. Flat grids (Tasks, Transfers) use a `FromComparer` default sort +
  built-in header sort + `CollectionViewOrder.RefreshIfStale`. Projects is hierarchical: a custom
  `DataGridSortDescription` over a VM-owned `DataGridCollectionView`, direction applied inside the
  comparer to the group component only. Every grid-bound reconciler consumer must use
  `Reconcile.alignToExisting` so its diff never emits a `Move` (the grid ignores `Move`).
- Selectors bound to a rebuilt-on-poll collection: reconcile the options in place (`RowHolder`)
  and, where selection must survive scope round-trips, bind an identity key via `SelectedValue`.
  Never `Clear()` a list a selector is bound to just because it is hidden.
- Icon-beside-label alignment uses the shared `Theming/InkAlignmentStyles.axaml` classes and the
  `TextInkBox` collapse machinery; the design contract for the open optical-baseline work is
  `docs/design` (#206/#216). Never "restore the old look" — it was a per-font accident.
- Mica: nav + command-bar regions show the material; content is always opaque. Each data view
  wraps everything below its command bar in ONE opaque content region (per-element opacity leaks).
  The command-bar toggle overrides the dedicated `LatticeCommandBarSurfaceBrush`, never
  `LatticeSurfaceBrush` (overlays must stay opaque); do not retoken `LatticeCanvasBrush`.
  `MicaBackdropPolicy` is the tested decision; granted-Mica fidelity is owner-on-hardware only.
- Statistics palette: colours belong to series ON the chart, never to a project. The visible cap
  must never exceed the palette size (test-pinned); raising it past ten needs more official
  palette colours first.
- Pure decision logic lives in policy modules from birth (`*Policy` C# statics or F# in
  `Lattice.App.Aggregation`) with exhaustive transition-table tests; views and VMs stay thin shells.

## Running the app for a visual check

- `SampleHost` is `#if DEBUG`: demo commands must use the **Debug** output — a Release binary with
  the sample flag shows an EMPTY app (pinned by `SampleHostReleaseExclusionTests`).
- Flags: `LATTICE_SAMPLE_HOSTS=1` (three-host sample fleet), `LATTICE_SAMPLE_TICK=1` (animated
  progress), `LATTICE_SAMPLE_MANY_PROJECTS=1` (adds an 11-project host).
- Launch directly: `LATTICE_SAMPLE_HOSTS=1 dotnet src/Lattice.App/bin/Debug/net10.0/Lattice.dll`.
- Tray residency + the single-instance guard hand a second launch to the resident process, which
  ignores new env vars — quit via tray → Exit before relaunching with different flags.
