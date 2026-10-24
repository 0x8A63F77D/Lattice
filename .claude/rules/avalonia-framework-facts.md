---
paths:
  - "src/Lattice.App/**"
  - "tests/Lattice.App.Tests/**"
  - "tests/Lattice.VisualTests/**"
  - "tests/Lattice.TestSupport/**"
---

# Avalonia / FluentAvalonia framework facts

Measured or source-verified against Avalonia 12.1 and FluentAvalonia 3.0.x while shipping
Lattice. They are facts about the framework, not Lattice decisions (see `app-ui-canon.md` for
those). Re-verify after a major framework bump. Still consult the Avalonia docs MCP for any API
question — these record only what the docs do not say or say wrongly.

## Build

- `.axaml` edits are not reliably picked up by incremental builds: XamlIl compiles XAML into the
  assembly and mtime logic can keep the previous version. After ANY `.axaml` edit — above all
  `sed`/`mv` mutation testing — clean `obj/` and `bin/` of the affected projects before trusting a
  test result. Restored `.bak` files carry stale mtimes too.

## Layout and text

- A `StackPanel` child defaults to `VerticalAlignment=Stretch`. `Center` on the PANEL centres the
  panel, not its children. Any technique that reshapes a TextBlock's box (negative-margin collapse
  onto a glyph band) is a no-op unless that TextBlock is explicitly `Center`.
- `UseLayoutRounding` applies per Layoutable to its own margin/arrange rect and inherits — switch
  it off on the holding PANEL, or a sibling icon still snaps (0.5 px at 1×).
- `PathIcon` stretches geometry uniformly and centres it: ink centre = box centre.
- `FormattedText.BuildGeometry().Bounds` is the only exact ink measure. `FontMetrics` has no cap
  height; `GlyphTypeface.TryGetGlyphMetrics` returns a zeroed ink box. Cap band = ink of `"H"`.
- Hinting snaps cap top and baseline to the device grid independently, so an ink centre can sit a
  full device pixel off — tolerances below that are fitting noise.
- `FormattedText` falls back per character: a band string the label's font does not cover is
  measured from the FALLBACK face (bounds and height). Tell: an identical margin across two
  metrically different fonts. Mixed-script pairs (Latin user data under a CJK UI) land on no band.
  A culture sweep that relabels into the culture's script cannot see this — hold the label's
  script fixed and vary only the UI culture.
- An app-level style class (`TextBlock.myClass` in `Application.Styles`) does reach a
  ControlTemplate's children without `/template/`.

## Controls

- FA `FANavigationView` supports adaptive `PaneDisplayMode="Auto"`; `ExpandedModeThresholdWidth`
  + `CompactModeThresholdWidth="0"` gives a clean two-state rail. It measures its own width.
  Hand-driving `IsPaneOpen` from code-behind is a false green: headless passes, the real Skia
  render loop reverts it.
- `window.Width` below `MinWidth` is silently clamped; size before `Show()` (a shown window
  re-measures to its client size).
- FA 3.0.x NavigationView pane is already transparent (`SplitView.PaneBackground` alpha 0); the
  `NavigationViewDefaultPaneBackground` key is a no-op on it. Headless coerces
  `ActualTransparencyLevel` away from Mica, so granted-Mica pixels are untestable in CI.
- Mutating `Window.Resources` raises `ResourcesChanged` even for an unchanged value; a resource
  write inside a resource-observable callback must be idempotent (`ContainsKey` guard) or it
  recurses into a UI-thread stack overflow.
- `Border.BoxShadow` renders only with `BackgroundSizing=CenterBorder` (upstream #15602; FA
  themes set `InnerBorderEdge` widely). `FAInfoBar` clips to bounds (`ClipToBounds=False` needed)
  and a closed bar renders nothing. Property-level shadow assertions are false-green — probe
  rendered pixels.
- `FAProgressRing`'s composition animation ignores visibility (`IsActive` is the only off-switch)
  and starves ALL pointer input to ~1 s app-wide while running. `InputStarvationTimeout` tuning
  does not mitigate it (measured). Stock indeterminate `ProgressBar` does not starve.
- Stock `ComboBox` opens on pointer RELEASE by upstream design (#3428, #9707).
- A raw `<Separator/>` works in `MenuFlyout`. `<MenuItem Header="-"/>` is a real MenuItem, so
  MenuItem styles (e.g. `MinHeight`) inflate the divider slot.
- A `ContextFlyout` on a whole DataGrid fires on chrome (header, background, scrollbar) and acts
  on the old selection — gate it on a `DataGridRow` ancestor of `e.Source` and move selection on
  right-press.
- Command-driven disablement surfaces as `IsEffectivelyEnabled`, not `IsEnabled`.
- `FAContentDialog` danger button: style the presenter
  (`Button.danger /template/ ContentPresenter#PART_ContentPresenter`); control-level Background is
  reverted by FA's pointer-over presenter swap. Enum is `FAInfoBarSeverity` in FA 3.x.

## DataGrid (Avalonia.Controls.DataGrid 12.1)

- `DataGridCollectionView` has no `Move` case: `ObservableCollection.Move` is silently ignored and
  rendered rows keep stale order.
- Sort arrows light by matching a SortDescription's `PropertyPath` to the column's
  `SortMemberPath`; null path ⇒ no arrow. `DataGridSortDescription` is subclassable; descending in
  the built-in path negates the WHOLE comparison (incompatible with hierarchical "children never
  flip"). No live shaping: item property changes never re-sort.
- Binding a non-generic `DataGridCollectionView` needs explicit
  `{CompiledBinding Data.X, DataType=vm:Row}` on text columns.

## MVVM selection identity

- Selectors resolve selection against item INSTANCES. Rebuilding the bound collection with
  value-equal copies makes the control null its own `SelectedItem` without writing back — invisible
  to ViewModel-only tests; assert the control in a headless view test.
- `[ObservableProperty]` setters swallow value-equal writes via `EqualityComparer<T>.Default`, so
  "compare differently at the call site" (`ReferenceEquals`) is never a repair. Repairs: select by a
  value-type key through `SelectedValue`, and/or reconcile the options collection in place.

## Platform

- macOS tray: template tinting via `MacOSProperties.IsTemplateIcon` (→ `NSImage setTemplate:`);
  `TrayIcon.Impl` is created in the constructor, so set it after `Icon`. The tray path is
  SINGLE-bitmap (no @1x/@2x resolution) — wire the 36 px master.
- Non-Skia headless stubs `Bitmap` decode to 1×1; read PNG IHDR bytes or decode with SkiaSharp.
  Never trust a supplied raster from its dimensions — decode and probe pixel content.
- macOS login item: an unprivileged app cannot read the System Settings Login Items toggle
  (`launchctl print-disabled` still says enabled; the BTM database is root-only; `SMAppService`
  reports it as not-found). Writing `~/Library/LaunchAgents/<label>.plist` with `RunAtLoad` (never
  `KeepAlive`) is the whole registration.
- To start window-less, leave `desktop.MainWindow` unassigned (with `OnExplicitShutdown`).
- Embedded font: inline `avares://<AssemblyName>/Assets/Fonts#<Family>` needs no registration
  (assembly name is `Lattice`, not the project name); a broken URI throws in Skia. Asking the font
  manager "is this Inter?" is false-green when the harness default is Inter — assert
  `FontFamily.Key` and the file's own name table. OFL text needs a separate `Content` copy.
