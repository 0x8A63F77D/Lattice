# M2 Grid-Layer Fidelity (#57 + #55) Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Reconcile the four data views (Tasks, Projects, Transfers, Event log) to the finalized M2 DataGrid spec — full-height column dividers, header colors/bottom-rule, row-hover-under-tint, middle-ellipsized Task column, tabular figures, locked column widths — and give the Event log a real, drag-resizable column header row.

**Architecture:** Almost all of the fidelity lives in the shared `src/Lattice.App/Theming/DataGridStyles.axaml` (+ two new tokens in `Theming/Tokens.axaml`). Per-view `.axaml` files only opt specific columns into the shared machinery (gutter `noDivider` class, `numericCell` class, middle-ellipsis on the star column, `.tintClass:pointerover` re-assertions). The Event-log header row is a change to `EventLogView.axaml` + four localized strings. No C#/domain logic changes; **do not touch `src/Lattice.Core/HostMonitor.cs` or `HostMachine`.**

**Tech Stack:** Avalonia 12.1, `Avalonia.Controls.DataGrid` 12.1 (Fluent theme), FluentAvalonia, xUnit + `Avalonia.Headless.XUnit` for geometry/brush probes.

---

## Design facts established up-front (authoritative — do not re-derive)

Source of truth: `docs/design/m2/README.md` §"The DataGrid is the core pattern" + the greyed annotations in `docs/design/m2/Lattice M2 Spec.html`. The spec's own CSS (extracted from the HTML) is authoritative and reads:

```css
/* shared datagrid column dividers — full-height hairline via inset shadow + gap */
.dg > *:not([style*="absolute"]) { box-shadow: inset -1px 0 0 #EDEBE9; }  /* light divider */
.dg > *:last-child               { box-shadow: none; }                     /* none on last col */
.dgd > *:not([style*="absolute"]){ box-shadow: inset -1px 0 0 #333; }      /* dark divider = #333 */
.dg-p > *:first-child            { box-shadow: none; }   /* Projects: no divider on chevron (1st) col */
.dg-l > *:nth-child(4)           { box-shadow: none; }   /* Event log: no divider on severity (4th) col */
```

Resolved values (light / dark):
- **Column divider**: `#EDEBE9` / `#333333` → NEW token `LatticeGridDividerBrush`.
- **Row rule (horizontal)**: `#F0F0F0` / `#333333` → already `LatticeStrokeSubtleBrush` (keep).
- **Header bottom border**: `#E0E0E0` / `#3D3D3D` → already `LatticeStrokeBrush` (reuse).
- **Header text**: `#616161` / `#D6D6D6` → already `LatticeTextSecondaryBrush` (keep).
- **Row hover**: `#F5F5F5` / `#383838` → NEW token `LatticeRowHoverBrush`.
- **Selected tint**: `#EBF3FC` / `#123B5C` = `LatticeSelectedTintBrush`; **at-risk tint** `#FFF9F5` / `#3A2A1E` = `LatticeWarningTintBrush`; **error-row tint** = `LatticeDangerTintBrush` (all exist).

Avalonia DataGrid mechanics (verified against `Avalonia.Controls.DataGrid` @ commit `50da2ce`):
- The Fluent DataGrid `ControlTheme` defaults **`GridLinesVisibility="None"`** — so the app currently renders **no** row rules and **no** column dividers. Setting `GridLinesVisibility="All"` turns both on; `HorizontalGridLinesBrush` (row rule) and `VerticalGridLinesBrush` (divider) then take effect.
- The body divider is template part `Rectangle#PART_RightGridLine` (a `Grid.Column="1"`, `Width="1"` rectangle in the `DataGridCell` template). `DataGridCell.EnsureGridLine` sets its `.Fill` = `VerticalGridLinesBrush` and its `.IsVisible` **imperatively as a local value** (so a style can't override `IsVisible`) — but it never touches `Width`. **To suppress a column's divider, set `Width="0"` on that part via a cell style class** (not `IsVisible`).
- Last visible column's divider auto-collapses when the filler column is inactive. All four grids have a `*` (star) column that consumes remaining width → filler inactive → **last-column divider is hidden natively; no work needed.**
- Header dividers are `Rectangle#VerticalSeparator` in the `DataGridColumnHeader` template, `Fill="{TemplateBinding SeparatorBrush}"`, `IsVisible="{TemplateBinding AreSeparatorsVisible}"`. Setting `SeparatorBrush` on the header style recolors them. The **last** column header separator auto-collapses (`UpdateSeparatorVisibility`). Per-column suppression = set `AreSeparatorsVisible="False"` on that header via an `:nth-child` / `:nth-last-child` selector (there is no per-column header style-class API; `:nth-child` is confirmed supported).
- The header bottom rule is `Rectangle#PART_ColumnHeadersAndRowsSeparator` in the `DataGrid` template, `Fill="{DynamicResource DataGridGridLinesBrush}"` (a system brush) — **recolor to `#E0E0E0` via a `/template/` style.**
- **Middle-ellipsis** is a built-in: `TextTrimming="PrefixCharacterEllipsis"` keeps an 8-char leading prefix + `…` + tail (head…tail). Use it on the star/Task column; leave `CharacterEllipsis` (end) on other text columns.

Column widths were audited against the spec and **already match exactly** in all four views (Tasks 108/118/*/112/68/74/100/112/76; Projects 24/200/110/140/100/110/*; Transfers */140/80/190/90/210/80; Event log 128/84/140/20/*). The width work is **regression-locking geometry tests**, not value edits.

## File structure

- `src/Lattice.App/Theming/Tokens.axaml` — add 2 brush tokens (light+dark).
- `src/Lattice.App/Theming/DataGridStyles.axaml` — shared: gridlines on, divider brush, header bottom rule, header separator brush, `noDivider` + `numericCell` cell classes, row hover + kill header hover. Owns everything reusable.
- `src/Lattice.App/Views/TasksView.axaml` — star-column middle-ellipsis, `numericCell` on numeric text columns, `.atRisk:pointerover` tint keep.
- `src/Lattice.App/Views/ProjectsView.axaml` — chevron column `noDivider` + header-separator suppression, `numericCell` on numeric columns.
- `src/Lattice.App/Views/TransfersView.axaml` — `numericCell` on Speed, `.retrying:pointerover` tint keep.
- `src/Lattice.App/Views/EventLogView.axaml` — **#55**: header row + labels, severity-column `noDivider` + header-separator suppression, `.warning`/`.error` `:pointerover` tint keep.
- `src/Lattice.App/Localization/Strings.resx` — `EventLogColTime`, `EventLogColMessage` (Host/Project reuse `ColHost`/`ColProject`).
- Tests (new files + additions): `tests/Lattice.App.Tests/Headless/DataGridInfraTests.cs` (shared brush/gridline probes), per-view test files (`TasksViewTests.cs`, `ProjectsViewTests.cs`, `TransfersViewTests.cs`, `EventLogViewTests.cs`).

### Conventions for every task
- **TDD, red-first + mutation-falsified**: write the test, run it, watch it FAIL for the right reason; implement; run it GREEN; then mentally (or actually) mutate the production value and confirm the test would go red. A test that passes before the change is a false green — restructure it.
- Headless probes verify **brushes + geometry + property state**, not rendered pixels (per #50: headless can't catch color fidelity end-to-end — the owner visual-verifies at the end).
- Commit after each task with a Conventional English message.
- Build gate per task: `dotnet build -c Debug -warnaserror` green + the touched test project green. Final task adds Release.

---

## Task 1: New design tokens (divider + row hover)

**Files:**
- Modify: `src/Lattice.App/Theming/Tokens.axaml` (Light dict ends line 26, Dark dict ends line 47)
- Test: `tests/Lattice.App.Tests/Headless/ThemeResourceTests.cs`

- [ ] **Step 1: Write the failing test**

Append to `ThemeResourceTests.cs` (mirror the existing resource-resolution tests in that file — they resolve a key against `Application.Current.TryGetResource(key, variant, out _)` for both `ThemeVariant.Light` and `ThemeVariant.Dark`). Add:

```csharp
[AvaloniaTheory]
[InlineData("LatticeGridDividerBrush", "#FFEDEBE9", "#FF333333")]
[InlineData("LatticeRowHoverBrush",    "#FFF5F5F5", "#FF383838")]
public void New_grid_tokens_resolve_to_spec_colors(string key, string lightHex, string darkHex)
{
    AssertBrush(key, Avalonia.Styling.ThemeVariant.Light, lightHex);
    AssertBrush(key, Avalonia.Styling.ThemeVariant.Dark, darkHex);
}
```

If `ThemeResourceTests.cs` has no `AssertBrush` helper, add one:

```csharp
private static void AssertBrush(string key, Avalonia.Styling.ThemeVariant variant, string expectedHex)
{
    Assert.True(Application.Current!.TryGetResource(key, variant, out var res), $"{key} missing for {variant}");
    var brush = Assert.IsAssignableFrom<Avalonia.Media.ISolidColorBrush>(res);
    Assert.Equal(Avalonia.Media.Color.Parse(expectedHex), brush.Color);
}
```

- [ ] **Step 2: Run it to verify it fails**

Run: `dotnet test tests/Lattice.App.Tests -c Debug --filter "FullyQualifiedName~ThemeResourceTests.New_grid_tokens_resolve_to_spec_colors"`
Expected: FAIL — `LatticeGridDividerBrush missing for Light`.

- [ ] **Step 3: Add the tokens**

In the **Light** `ResourceDictionary` (after line 25, before `</ResourceDictionary>` at 26):

```xml
      <SolidColorBrush x:Key="LatticeGridDividerBrush" Color="#EDEBE9" />
      <SolidColorBrush x:Key="LatticeRowHoverBrush" Color="#F5F5F5" />
```

In the **Dark** `ResourceDictionary` (after line 46):

```xml
      <SolidColorBrush x:Key="LatticeGridDividerBrush" Color="#333333" />
      <SolidColorBrush x:Key="LatticeRowHoverBrush" Color="#383838" />
```

- [ ] **Step 4: Run it to verify it passes**

Run: same filter as Step 2. Expected: PASS (2 theories × light/dark).

- [ ] **Step 5: Commit**

```bash
git add src/Lattice.App/Theming/Tokens.axaml tests/Lattice.App.Tests/Headless/ThemeResourceTests.cs
git commit -m "feat(theming): add grid-divider and row-hover tokens (#57)"
```

---

## Task 2: Turn gridlines on — column dividers + header bottom rule (shared)

**Files:**
- Modify: `src/Lattice.App/Theming/DataGridStyles.axaml`
- Test: `tests/Lattice.App.Tests/Headless/DataGridInfraTests.cs`

- [ ] **Step 1: Write the failing tests**

Add to `DataGridInfraTests.cs` (uses its existing `MakeLatticeGrid()` + `ShowInWindow()` helpers; add `using Avalonia.Controls.Shapes;` — already present):

```csharp
[AvaloniaFact]
public void Lattice_grid_enables_gridlines_and_divider_brush_is_spec_color()
{
    var grid = MakeLatticeGrid();
    var window = ShowInWindow(grid);
    Layout(window);

    Assert.Equal(Avalonia.Controls.DataGridGridLinesVisibility.All, grid.GridLinesVisibility);

    Assert.True(Application.Current!.TryGetResource(
        "LatticeGridDividerBrush", window.ActualThemeVariant, out var divider));
    var vfill = Assert.IsAssignableFrom<ISolidColorBrush>(grid.VerticalGridLinesBrush);
    Assert.Equal(((ISolidColorBrush)divider!).Color, vfill.Color);

    // Row rule stays the subtle stroke, distinct from the divider.
    Assert.True(Application.Current!.TryGetResource(
        "LatticeStrokeSubtleBrush", window.ActualThemeVariant, out var rowRule));
    var hfill = Assert.IsAssignableFrom<ISolidColorBrush>(grid.HorizontalGridLinesBrush);
    Assert.Equal(((ISolidColorBrush)rowRule!).Color, hfill.Color);
    Assert.NotEqual(vfill.Color, hfill.Color);
    window.Close();
}

[AvaloniaFact]
public void Header_bottom_rule_paints_the_E0E0E0_stroke_not_the_system_gridline()
{
    var grid = MakeLatticeGrid();
    var window = ShowInWindow(grid);
    Layout(window);

    var rule = VisualTree.FindInVisualTree<Rectangle>(grid, r => r.Name == "PART_ColumnHeadersAndRowsSeparator");
    Assert.NotNull(rule);
    Assert.True(Application.Current!.TryGetResource(
        "LatticeStrokeBrush", window.ActualThemeVariant, out var stroke));
    var fill = Assert.IsAssignableFrom<ISolidColorBrush>(rule!.Fill);
    Assert.Equal(((ISolidColorBrush)stroke!).Color, fill.Color);
    window.Close();
}
```
(`DataGridInfraTests` currently has no `VisualTree` import; add `using` for the `Lattice.App.Tests.Headless.VisualTree` helper — same namespace, so no import needed — or inline a descendant search. `MakeLatticeGrid` builds a bare grid with no last-column star; that's fine for these probes since they read grid-level brushes + the header part, not per-cell divider visibility.)

- [ ] **Step 2: Run to verify they fail**

Run: `dotnet test tests/Lattice.App.Tests -c Debug --filter "FullyQualifiedName~DataGridInfraTests.Lattice_grid_enables_gridlines_and_divider_brush_is_spec_color|FullyQualifiedName~DataGridInfraTests.Header_bottom_rule_paints_the_E0E0E0"`
Expected: FAIL — `GridLinesVisibility` is `None`; `VerticalGridLinesBrush` is Transparent; header rule fill is the system brush.

- [ ] **Step 3: Implement in `DataGridStyles.axaml`**

In the `DataGrid.lattice` style (lines 57-69), replace the `VerticalGridLinesBrush` setter and add `GridLinesVisibility`:

```xml
  <Style Selector="DataGrid.lattice">
    <Setter Property="CanUserResizeColumns" Value="True" />
    <!-- Fluent DataGrid theme defaults GridLinesVisibility=None (no rules/dividers).
         Turn both on: horizontal = the #F0F0F0 row rule, vertical = the #EDEBE9 column
         divider. Last-column divider auto-collapses natively (every lattice grid has a
         star column, so the filler column is inactive). Icon/gutter dividers are killed
         per-cell via the .noDivider class (Task 3) — EnsureGridLine sets the part's
         IsVisible as a local value, so suppression is by Width=0, not IsVisible. -->
    <Setter Property="GridLinesVisibility" Value="All" />
    <Setter Property="RowHeight" Value="{DynamicResource LatticeRowHeight}" />
    <Setter Property="FontSize" Value="13" />
    <Setter Property="Foreground" Value="{DynamicResource LatticeTextPrimaryBrush}" />
    <Setter Property="HorizontalGridLinesBrush" Value="{DynamicResource LatticeStrokeSubtleBrush}" />
    <Setter Property="VerticalGridLinesBrush" Value="{DynamicResource LatticeGridDividerBrush}" />
    <Setter Property="Background" Value="{DynamicResource LatticeSurfaceBrush}" />
  </Style>
```

Add a `/template/` style to recolor the header bottom rule (place after the `DataGrid.lattice.compact` block, ~line 74):

```xml
  <!-- The header bottom rule is a template part painted from the system DataGridGridLinesBrush;
       repaint it to the Lattice stroke (#E0E0E0 / #3D3D3D) per spec's 32px-header bottom border. -->
  <Style Selector="DataGrid.lattice /template/ Rectangle#PART_ColumnHeadersAndRowsSeparator">
    <Setter Property="Fill" Value="{DynamicResource LatticeStrokeBrush}" />
  </Style>
```

- [ ] **Step 4: Run to verify they pass**

Run: same filter as Step 2. Expected: PASS. Then mutate `GridLinesVisibility` back to `None` locally and confirm the first test goes red; revert.

- [ ] **Step 5: Commit**

```bash
git add src/Lattice.App/Theming/DataGridStyles.axaml tests/Lattice.App.Tests/Headless/DataGridInfraTests.cs
git commit -m "feat(grid): render column dividers and spec header bottom rule (#57)"
```

---

## Task 3: `noDivider` gutter class + header separator brush + `numericCell` class (shared)

**Files:**
- Modify: `src/Lattice.App/Theming/DataGridStyles.axaml`
- Test: `tests/Lattice.App.Tests/Headless/DataGridInfraTests.cs`

- [ ] **Step 1: Write the failing tests**

The `noDivider` probe needs a real gutter column, so add a local grid builder to `DataGridInfraTests.cs` and two tests:

```csharp
// A lattice grid whose FIRST column is a gutter marked .noDivider, followed by two
// normal columns + a star column (so the filler is inactive and last-col logic matches
// the real views). Column 0's PART_RightGridLine must collapse to zero width.
private static DataGrid MakeGutterGrid()
{
    var grid = new DataGrid
    {
        ItemsSource = new[] { new Row(), new Row() },
        Columns =
        {
            new DataGridTextColumn { Header = "G", Binding = new Avalonia.Data.Binding("Value"),
                                     Width = new DataGridLength(24), CellStyleClasses = { "noDivider" } },
            new DataGridTextColumn { Header = "Name", Binding = new Avalonia.Data.Binding("Name"), Width = new DataGridLength(120) },
            new DataGridTextColumn { Header = "V2", Binding = new Avalonia.Data.Binding("Value"), Width = new DataGridLength(120) },
            new DataGridTextColumn { Header = "Star", Binding = new Avalonia.Data.Binding("Name"), Width = new DataGridLength(1, DataGridLengthUnitType.Star) },
        },
    };
    grid.Classes.Add("lattice");
    return grid;
}

[AvaloniaFact]
public void Gutter_cell_class_collapses_the_right_gridline_to_zero_width()
{
    var grid = MakeGutterGrid();
    var window = ShowInWindow(grid);
    Layout(window);

    // Cells of column 0 (the .noDivider gutter): their PART_RightGridLine has Width 0.
    var gutterCells = grid.GetVisualDescendants().OfType<DataGridCell>()
        .Where(c => c.Classes.Contains("noDivider")).ToList();
    Assert.NotEmpty(gutterCells);
    foreach (var cell in gutterCells)
    {
        var line = VisualTree.FindInVisualTree<Rectangle>(cell, r => r.Name == "PART_RightGridLine");
        Assert.NotNull(line);
        Assert.Equal(0d, line!.Width);
    }

    // A normal (non-gutter) non-last cell keeps a 1px divider.
    var normalCell = grid.GetVisualDescendants().OfType<DataGridCell>()
        .First(c => !c.Classes.Contains("noDivider")
                 && c.FindAncestorOfType<DataGridRow>() != null
                 && (c.Content as TextBlock)?.Text != null); // any body cell
    var normalLine = VisualTree.FindInVisualTree<Rectangle>(normalCell, r => r.Name == "PART_RightGridLine");
    Assert.NotNull(normalLine);
    Assert.NotEqual(0d, normalLine!.Width);
    window.Close();
}

[AvaloniaFact]
public void Header_separator_uses_the_divider_brush()
{
    var grid = MakeLatticeGrid();
    var window = ShowInWindow(grid);
    Layout(window);

    var header = grid.GetVisualDescendants().OfType<DataGridColumnHeader>()
        .First(h => (h.Content as string) == "Name");
    Assert.True(Application.Current!.TryGetResource(
        "LatticeGridDividerBrush", window.ActualThemeVariant, out var divider));
    var sep = Assert.IsAssignableFrom<ISolidColorBrush>(header.SeparatorBrush);
    Assert.Equal(((ISolidColorBrush)divider!).Color, sep.Color);

    // Header text is the #616161 / #D6D6D6 secondary token (spec header color).
    Assert.True(Application.Current!.TryGetResource(
        "LatticeTextSecondaryBrush", window.ActualThemeVariant, out var fg));
    var hfg = Assert.IsAssignableFrom<ISolidColorBrush>(header.Foreground);
    Assert.Equal(((ISolidColorBrush)fg!).Color, hfg.Color);
    Assert.Equal(11d, header.FontSize);
    Assert.Equal(Avalonia.Media.FontWeight.SemiBold, header.FontWeight);
    window.Close();
}

[AvaloniaFact]
public void Numeric_cell_class_applies_tabular_figures_to_its_textblock()
{
    var grid = MakeLatticeGrid();
    grid.Columns[1].CellStyleClasses.Add("numericCell");
    var window = ShowInWindow(grid);
    Layout(window);

    var cell = grid.GetVisualDescendants().OfType<DataGridCell>()
        .First(c => c.Classes.Contains("numericCell"));
    var tb = VisualTree.FindInVisualTree<TextBlock>(cell);
    Assert.NotNull(tb);
    Assert.NotNull(tb!.FontFeatures);
    Assert.Contains(tb.FontFeatures!, f => f.Tag == "tnum" && f.Value == 1);
    window.Close();
}
```
(Add `using Avalonia.Controls;` for `DataGridLengthUnitType`, `using Avalonia.VisualTree;` for `FindAncestorOfType`, and `using Avalonia.Media;` — check existing imports; `Avalonia.Media` is already imported.)

- [ ] **Step 2: Run to verify they fail**

Run: `dotnet test tests/Lattice.App.Tests -c Debug --filter "FullyQualifiedName~DataGridInfraTests.Gutter_cell_class_collapses|FullyQualifiedName~DataGridInfraTests.Header_separator_uses_the_divider|FullyQualifiedName~DataGridInfraTests.Numeric_cell_class"`
Expected: FAIL — no `noDivider`/`numericCell` styles; header `SeparatorBrush` is the system gridline.

- [ ] **Step 3: Implement in `DataGridStyles.axaml`**

Add `SeparatorBrush` to the existing `DataGridColumnHeader` style (lines 49-55):

```xml
  <Style Selector="DataGridColumnHeader">
    <Setter Property="Height" Value="{DynamicResource LatticeGridHeaderHeight}" />
    <Setter Property="FontSize" Value="11" />
    <Setter Property="FontWeight" Value="SemiBold" />
    <Setter Property="Foreground" Value="{DynamicResource LatticeTextSecondaryBrush}" />
    <Setter Property="Padding" Value="8,0" />
    <!-- Column-header vertical separators match the body divider color; last-col
         separator auto-collapses. Gutter headers opt out per-view via nth-child. -->
    <Setter Property="SeparatorBrush" Value="{DynamicResource LatticeGridDividerBrush}" />
  </Style>
```

Add the two cell classes (after the `DataGridCell` style, ~line 79):

```xml
  <!-- Gutter/icon columns (Projects chevron, Event-log severity) suppress their column
       divider. EnsureGridLine owns the part's IsVisible as a local value, so collapse the
       divider by zeroing its Width instead (the part is Grid.Column=1, so 0 width also
       reclaims the layout slot). -->
  <Style Selector="DataGridCell.noDivider /template/ Rectangle#PART_RightGridLine">
    <Setter Property="Width" Value="0" />
  </Style>

  <!-- Numeric text columns: tabular (fixed-width) figures on the generated cell TextBlock.
       Template columns that build their own TextBlock use Classes="numeric" directly;
       this class is for DataGridTextColumn cells (no direct handle on their TextBlock). -->
  <Style Selector="DataGridCell.numericCell TextBlock">
    <Setter Property="FontFeatures" Value="+tnum" />
  </Style>
```

- [ ] **Step 4: Run to verify they pass**

Run: same filter as Step 2. Expected: PASS. Mutate: change the `noDivider` setter `Width` to `1` → the gutter test goes red; revert.

- [ ] **Step 5: Commit**

```bash
git add src/Lattice.App/Theming/DataGridStyles.axaml tests/Lattice.App.Tests/Headless/DataGridInfraTests.cs
git commit -m "feat(grid): add noDivider gutter + numericCell classes and header separator brush (#57)"
```

---

## Task 4: Row hover under state tints + no header hover (shared)

**Files:**
- Modify: `src/Lattice.App/Theming/DataGridStyles.axaml`
- Test: `tests/Lattice.App.Tests/Headless/DataGridInfraTests.cs`

- [ ] **Step 1: Write the failing tests**

Hover is driven by moving the pointer over a row (mirrors the header-edge-drag technique already in this file). Add:

```csharp
private static DataGridRow BodyRow(DataGrid grid, int index) =>
    grid.GetVisualDescendants().OfType<DataGridRow>().Single(r => r.Index == index);

[AvaloniaFact]
public void Hovering_a_plain_row_paints_the_hover_brush()
{
    var grid = MakeLatticeGrid();
    var window = ShowInWindow(grid);
    Layout(window);

    var row = BodyRow(grid, 0);
    var mid = row.TranslatePoint(new Point(row.Bounds.Width / 2, row.Bounds.Height / 2), window)!.Value;
    window.MouseMove(mid, RawInputModifiers.None);
    Layout(window);

    Assert.Contains(":pointerover", row.Classes);
    Assert.True(Application.Current!.TryGetResource(
        "LatticeRowHoverBrush", window.ActualThemeVariant, out var hover));
    var bg = Assert.IsAssignableFrom<ISolidColorBrush>(row.Background);
    Assert.Equal(((ISolidColorBrush)hover!).Color, bg.Color);
    window.Close();
}

[AvaloniaFact]
public void Hovering_an_at_risk_row_keeps_its_warning_tint_hover_sits_under()
{
    var grid = MakeLatticeGrid();
    var window = ShowInWindow(grid);
    Layout(window);

    var row = BodyRow(grid, 0);
    row.Classes.Add("atRisk"); // simulate a tinted row (the real views add this in code)
    // Provide the tint rule locally so the test is self-contained (the real views own it):
    Layout(window);
    var mid = row.TranslatePoint(new Point(row.Bounds.Width / 2, row.Bounds.Height / 2), window)!.Value;
    window.MouseMove(mid, RawInputModifiers.None);
    Layout(window);

    // With the .atRisk:pointerover re-assertion, the row keeps the warning tint, NOT the hover.
    Assert.True(Application.Current!.TryGetResource(
        "LatticeWarningTintBrush", window.ActualThemeVariant, out var warn));
    Assert.True(Application.Current!.TryGetResource(
        "LatticeRowHoverBrush", window.ActualThemeVariant, out var hover));
    var bg = Assert.IsAssignableFrom<ISolidColorBrush>(row.Background);
    Assert.Equal(((ISolidColorBrush)warn!).Color, bg.Color);
    Assert.NotEqual(((ISolidColorBrush)hover!).Color, bg.Color);
    window.Close();
}

[AvaloniaFact]
public void Column_header_does_not_change_background_on_hover()
{
    var grid = MakeLatticeGrid();
    var window = ShowInWindow(grid);
    Layout(window);

    var header = grid.GetVisualDescendants().OfType<DataGridColumnHeader>()
        .First(h => (h.Content as string) == "Name");
    var root = VisualTree.FindInVisualTree<Grid>(header, g => g.Name == "PART_ColumnHeaderRoot");
    Assert.NotNull(root);
    var before = (root!.Background as ISolidColorBrush)?.Color;

    var pt = header.TranslatePoint(new Point(header.Bounds.Width / 2, header.Bounds.Height / 2), window)!.Value;
    window.MouseMove(pt, RawInputModifiers.None);
    Layout(window);

    var after = (root.Background as ISolidColorBrush)?.Color;
    Assert.Equal(before, after); // no hover tint swap
    window.Close();
}
```

> Note: the shared `DataGridStyles.axaml` must itself carry a base `DataGrid.lattice DataGridRow.atRisk` tint rule for the second test to be self-contained AND for the real Tasks/Transfers/EventLog tints to keep working under hover. Add the shared tint + hover-keep rules in the shared file (Step 3) so `.atRisk`/`.retrying`/`.warning`/`.error` all keep their tint on hover globally; the per-view files already declare the base (non-hover) tint but the **`:pointerover` re-assertion belongs in the shared file** so it applies uniformly. (Keeping per-view base tints is fine — Avalonia merges; the shared `:pointerover` rule has higher specificity than the shared plain hover rule and wins.)

- [ ] **Step 2: Run to verify they fail**

Run: `dotnet test tests/Lattice.App.Tests -c Debug --filter "FullyQualifiedName~DataGridInfraTests.Hovering_a_plain_row|FullyQualifiedName~DataGridInfraTests.Hovering_an_at_risk_row|FullyQualifiedName~DataGridInfraTests.Column_header_does_not_change_background"`
Expected: FAIL — no hover brush; header swaps to the theme hover background.

- [ ] **Step 3: Implement in `DataGridStyles.axaml`**

Add (after the cell classes from Task 3):

```xml
  <!-- Row hover: whole row → #F5F5F5 / #383838 with a 100ms fade. Sits UNDER the state
       tints — the .tint:pointerover rules below re-assert each tint so a hovered at-risk /
       retrying / warning / error row keeps its color. Selection paints on the row's
       BackgroundRectangle overlay (already above Background), so selected rows are unaffected. -->
  <Style Selector="DataGrid.lattice DataGridRow">
    <Setter Property="Transitions">
      <Transitions>
        <BrushTransition Property="Background" Duration="0:0:0.1" />
      </Transitions>
    </Setter>
  </Style>
  <Style Selector="DataGrid.lattice DataGridRow:pointerover">
    <Setter Property="Background" Value="{DynamicResource LatticeRowHoverBrush}" />
  </Style>

  <!-- Base state tints (shared so hover-keep is uniform). Per-view files may also declare
       these; identical values, so the merge is a no-op. -->
  <Style Selector="DataGrid.lattice DataGridRow.atRisk">
    <Setter Property="Background" Value="{DynamicResource LatticeWarningTintBrush}" />
  </Style>
  <Style Selector="DataGrid.lattice DataGridRow.retrying">
    <Setter Property="Background" Value="{DynamicResource LatticeWarningTintBrush}" />
  </Style>
  <Style Selector="DataGrid.lattice DataGridRow.warning">
    <Setter Property="Background" Value="{DynamicResource LatticeWarningTintBrush}" />
  </Style>
  <Style Selector="DataGrid.lattice DataGridRow.error">
    <Setter Property="Background" Value="{DynamicResource LatticeDangerTintBrush}" />
  </Style>
  <!-- Hover-keep: tint wins over the plain hover (class+pseudo out-specifies pseudo alone). -->
  <Style Selector="DataGrid.lattice DataGridRow.atRisk:pointerover">
    <Setter Property="Background" Value="{DynamicResource LatticeWarningTintBrush}" />
  </Style>
  <Style Selector="DataGrid.lattice DataGridRow.retrying:pointerover">
    <Setter Property="Background" Value="{DynamicResource LatticeWarningTintBrush}" />
  </Style>
  <Style Selector="DataGrid.lattice DataGridRow.warning:pointerover">
    <Setter Property="Background" Value="{DynamicResource LatticeWarningTintBrush}" />
  </Style>
  <Style Selector="DataGrid.lattice DataGridRow.error:pointerover">
    <Setter Property="Background" Value="{DynamicResource LatticeDangerTintBrush}" />
  </Style>

  <!-- Headers do not hover (spec): neutralize the Fluent theme's pointerover/pressed
       root-background swap; keep the header on the grid surface. -->
  <Style Selector="DataGridColumnHeader:pointerover /template/ Grid#PART_ColumnHeaderRoot">
    <Setter Property="Background" Value="Transparent" />
  </Style>
  <Style Selector="DataGridColumnHeader:pressed /template/ Grid#PART_ColumnHeaderRoot">
    <Setter Property="Background" Value="Transparent" />
  </Style>
```

> This centralizes the four row tints in the shared file. In Tasks 5–8, **remove** the now-duplicated base tint `Style` rules from the per-view `UserControl.Styles` (`DataGridRow.atRisk`, `.retrying`, `.warning`, `.error`) to avoid drift — the shared rules cover them. Keep per-view rules that do more than Background (e.g. `.suspended` foreground, `.atRiskText`, `.projectParent` heights).

- [ ] **Step 4: Run to verify they pass**

Run: same filter as Step 2. Expected: PASS. Mutate: delete the `.atRisk:pointerover` rule → the at-risk test goes red (row shows hover brush); revert.

- [ ] **Step 5: Commit**

```bash
git add src/Lattice.App/Theming/DataGridStyles.axaml tests/Lattice.App.Tests/Headless/DataGridInfraTests.cs
git commit -m "feat(grid): row hover under state tints; headers do not hover (#57)"
```

---

## Task 5: Tasks view — middle-ellipsis, numeric figures, width lock, tint dedup

**Files:**
- Modify: `src/Lattice.App/Views/TasksView.axaml`
- Test: `tests/Lattice.App.Tests/Headless/TasksViewTests.cs`

- [ ] **Step 1: Write the failing tests**

Add to `TasksViewTests.cs` (uses its `MakeView()` + the `TaskRowViewModel`/`TaskRow` add pattern at lines 113-119; add `using Avalonia.Media;`):

```csharp
[AvaloniaFact]
public void Task_column_middle_ellipsizes_and_other_text_columns_end_ellipsize()
{
    var (window, _, vm, _, _, _) = MakeView();
    window.Show();
    var row = new TaskRowViewModel(
        Project: "einstein", Application: "O3AS", Name: "h1_1234_long_task_name_0_1",
        Fraction: 0.2, PercentText: "20%", ElapsedText: "1m 00s", RemainingText: "5m 00s",
        DeadlineText: "07-11 00:00", Deadline: DateTimeOffset.UtcNow.AddHours(1),
        StateKind: TaskStateKind.Running, StateText: "Running",
        IsDeadlineAtRisk: false, IsSuspended: false, HostId: Guid.NewGuid(), Host: "host-a");
    vm.Rows.Add(new TaskRow(row.Key, row));
    Layout(window);

    var grid = window.GetVisualDescendants().OfType<DataGrid>().Single();
    // The Task cell's TextBlock (bound to the task Name) uses PrefixCharacterEllipsis.
    var taskTb = grid.GetVisualDescendants().OfType<TextBlock>()
        .Single(t => t.Text == "h1_1234_long_task_name_0_1");
    Assert.Same(TextTrimming.PrefixCharacterEllipsis, taskTb.TextTrimming);
    window.Close();
}

[AvaloniaFact]
public void Tasks_default_column_widths_match_the_spec()
{
    var (window, _, _, _, _, _) = MakeView();
    window.Show();
    Layout(window);
    var grid = window.GetVisualDescendants().OfType<DataGrid>().Single();
    double W(int i) => grid.Columns[i].Width.Value;
    Assert.Equal(108, W(0)); // Project
    Assert.Equal(118, W(1)); // Application
    Assert.True(grid.Columns[2].Width.IsStar); // Task
    Assert.Equal(112, W(3)); // Progress
    Assert.Equal(68,  W(4)); // Elapsed
    Assert.Equal(74,  W(5)); // Remaining
    Assert.Equal(100, W(6)); // Deadline
    Assert.Equal(112, W(7)); // State
    Assert.Equal(76,  W(8)); // Host
    window.Close();
}

[AvaloniaFact]
public void Elapsed_and_remaining_cells_use_tabular_figures()
{
    var (window, _, vm, _, _, _) = MakeView();
    window.Show();
    var row = new TaskRowViewModel(
        Project: "p", Application: "a", Name: "t", Fraction: 0.2, PercentText: "20%",
        ElapsedText: "1m 00s", RemainingText: "5m 00s", DeadlineText: "07-11 00:00",
        Deadline: DateTimeOffset.UtcNow.AddHours(1), StateKind: TaskStateKind.Running, StateText: "Running",
        IsDeadlineAtRisk: false, IsSuspended: false, HostId: Guid.NewGuid(), Host: "host-a");
    vm.Rows.Add(new TaskRow(row.Key, row));
    Layout(window);
    var grid = window.GetVisualDescendants().OfType<DataGrid>().Single();
    foreach (var text in new[] { "1m 00s", "5m 00s" })
    {
        var tb = grid.GetVisualDescendants().OfType<TextBlock>().Single(t => t.Text == text);
        Assert.NotNull(tb.FontFeatures);
        Assert.Contains(tb.FontFeatures!, f => f.Tag == "tnum" && f.Value == 1);
    }
    window.Close();
}
```

- [ ] **Step 2: Run to verify they fail**

Run: `dotnet test tests/Lattice.App.Tests -c Debug --filter "FullyQualifiedName~TasksViewTests.Task_column_middle_ellipsizes|FullyQualifiedName~TasksViewTests.Tasks_default_column_widths|FullyQualifiedName~TasksViewTests.Elapsed_and_remaining"`
Expected: FAIL — Task cell is `CharacterEllipsis`; Elapsed/Remaining lack `+tnum`. (Width test should PASS immediately — it's a regression lock; if it fails, a prior edit drifted the widths — fix the view, not the test.)

- [ ] **Step 3: Implement in `TasksView.axaml`**

Task column TextBlock (line 142-143) → middle ellipsis:

```xml
                <TextBlock Text="{Binding Data.Name}" VerticalAlignment="Center"
                           TextTrimming="PrefixCharacterEllipsis" ToolTip.Tip="{Binding Data.Name}" />
```

Elapsed + Remaining text columns (lines 161-164) → add `CellStyleClasses="numericCell"`:

```xml
          <DataGridTextColumn Header="{x:Static loc:Strings.ColElapsed}"
                               Binding="{Binding Data.ElapsedText}" Width="68" CellStyleClasses="numericCell" />
          <DataGridTextColumn Header="{x:Static loc:Strings.ColRemaining}"
                               Binding="{Binding Data.RemainingText}" Width="74" CellStyleClasses="numericCell" />
```

Deadline text (template column, line 172-173) → add `Classes="numeric"` to its TextBlock so the countdown is tabular:

```xml
                  <TextBlock Text="{Binding Data.DeadlineText}" FontSize="12" Classes="numeric"
                             Classes.atRiskText="{Binding Data.IsDeadlineAtRisk}" />
```

Remove the now-shared base tint rule from `UserControl.Styles` (lines 24-26) — delete the `DataGridRow.atRisk` `Style` block (the shared file now owns it). **Keep** `DataGridRow.suspended`, `Border.progressFill(.suspended)`, `TextBlock.atRiskText`.

- [ ] **Step 4: Run to verify they pass**

Run: same filter as Step 2 + `FullyQualifiedName~TasksViewTests` (full file, to prove the tint dedup didn't regress `At_risk_row_carries_the_atRisk_class`). Expected: PASS.

- [ ] **Step 5: Commit**

```bash
git add src/Lattice.App/Views/TasksView.axaml tests/Lattice.App.Tests/Headless/TasksViewTests.cs
git commit -m "feat(tasks): middle-ellipsis task name, tabular figures, width lock (#57)"
```

---

## Task 6: Projects view — chevron gutter divider off (body + header), numeric figures, width lock

**Files:**
- Modify: `src/Lattice.App/Views/ProjectsView.axaml`
- Test: `tests/Lattice.App.Tests/Headless/ProjectsViewTests.cs`

- [ ] **Step 1: Write the failing tests**

Add to `ProjectsViewTests.cs`. To realize cells, add one parent `ProjectRow` to `vm.Rows` using the exact `ProjectRowViewModel(...)` + `new ProjectRow(vm.Key, vm)` shape already used elsewhere in this test file (grep the file for `vm.Rows.Add` and copy the nearest example's constructor call verbatim — do not invent field names). The width test needs no rows. Tests:

```csharp
[AvaloniaFact]
public void Chevron_gutter_column_has_no_body_divider_and_no_header_separator()
{
    var (window, _, vm, _, _, _) = MakeView();
    window.Show();
    // ...populate one project parent row via the file's existing helper so cells realize...
    Layout(window);
    var grid = window.GetVisualDescendants().OfType<DataGrid>().Single();

    // Body: the first column's cells are marked noDivider → PART_RightGridLine width 0.
    var gutterCells = grid.GetVisualDescendants().OfType<DataGridCell>()
        .Where(c => c.Classes.Contains("noDivider")).ToList();
    Assert.NotEmpty(gutterCells);
    foreach (var c in gutterCells)
    {
        var line = VisualTree.FindInVisualTree<Rectangle>(c, r => r.Name == "PART_RightGridLine");
        Assert.Equal(0d, line!.Width);
    }

    // Header: the first column header's separator is off; a normal header keeps it.
    var headers = grid.GetVisualDescendants().OfType<DataGridColumnHeader>()
        .Where(h => h.OwningColumn != null)
        .OrderBy(h => h.OwningColumn!.DisplayIndex).ToList();
    Assert.False(headers[0].AreSeparatorsVisible);          // chevron gutter
    Assert.True(headers[1].AreSeparatorsVisible);           // Project column
    window.Close();
}

[AvaloniaFact]
public void Projects_default_column_widths_match_the_spec()
{
    var (window, _, _, _, _, _) = MakeView();
    window.Show();
    Layout(window);
    var grid = window.GetVisualDescendants().OfType<DataGrid>().Single();
    double W(int i) => grid.Columns[i].Width.Value;
    Assert.Equal(24,  W(0)); // chevron
    Assert.Equal(200, W(1)); // Project
    Assert.Equal(110, W(2)); // Hosts
    Assert.Equal(140, W(3)); // Resource share
    Assert.Equal(100, W(4)); // Avg credit
    Assert.Equal(110, W(5)); // Total credit
    Assert.True(grid.Columns[6].Width.IsStar); // Status
    window.Close();
}
```

- [ ] **Step 2: Run to verify they fail**

Run: `dotnet test tests/Lattice.App.Tests -c Debug --filter "FullyQualifiedName~ProjectsViewTests.Chevron_gutter_column|FullyQualifiedName~ProjectsViewTests.Projects_default_column_widths"`
Expected: FAIL — chevron cells lack `noDivider`; header[0].AreSeparatorsVisible is `true`. (Width test PASS as regression lock.)

- [ ] **Step 3: Implement in `ProjectsView.axaml`**

Chevron column (line 144) → mark its cells `noDivider`:

```xml
          <DataGridTemplateColumn Width="24" CellStyleClasses="noDivider">
```

Header separator suppression for the first (chevron) column — add to `UserControl.Styles`:

```xml
    <!-- Chevron gutter column: no header divider (spec .dg-p>*:first-child). The body
         divider is off via CellStyleClasses="noDivider"; the header separator has no
         per-column style-class hook, so target the first header positionally (chevron
         is always column 0). -->
    <Style Selector="DataGridColumnHeadersPresenter > DataGridColumnHeader:nth-child(1)">
      <Setter Property="AreSeparatorsVisible" Value="False" />
    </Style>
```

Numeric columns → add `CellStyleClasses="numericCell"` to Hosts (line 178-180), Avg credit (200-201), Total credit (202-203):

```xml
          <DataGridTextColumn Header="{x:Static loc:Strings.ProjectsColHosts}"
                               Binding="{Binding Data.HostsText}" Width="110" CellStyleClasses="numericCell"
                               IsVisible="{Binding IsAllHostsScope}" />
          ...
          <DataGridTextColumn Header="{x:Static loc:Strings.ProjectsColAvgCredit}"
                               Binding="{Binding Data.AvgCreditText}" Width="100" CellStyleClasses="numericCell" />
          <DataGridTextColumn Header="{x:Static loc:Strings.ProjectsColTotalCredit}"
                               Binding="{Binding Data.TotalCreditText}" Width="110" CellStyleClasses="numericCell" />
```

Projects has no row tints, so no tint dedup needed.

> If Step 4 reveals `:nth-child(1)` does not stick (e.g. `UpdateSeparatorVisibility` re-asserts), fall back to code-behind in `ProjectsView.axaml.cs`: on the grid's `LayoutUpdated` (once), find `DataGridColumnHeader` where `OwningColumn == Grid.Columns[0]` and set `AreSeparatorsVisible = false`. Prefer the declarative selector; only fall back if the test forces it.

- [ ] **Step 4: Run to verify they pass**

Run: same filter as Step 2, then full `FullyQualifiedName~ProjectsViewTests`. Expected: PASS.

- [ ] **Step 5: Commit**

```bash
git add src/Lattice.App/Views/ProjectsView.axaml tests/Lattice.App.Tests/Headless/ProjectsViewTests.cs
git commit -m "feat(projects): suppress chevron gutter divider, tabular figures, width lock (#57)"
```

---

## Task 7: Transfers view — numeric figures, width lock, tint dedup

**Files:**
- Modify: `src/Lattice.App/Views/TransfersView.axaml`
- Test: `tests/Lattice.App.Tests/Headless/TransfersViewTests.cs`

- [ ] **Step 1: Write the failing tests**

Add to `TransfersViewTests.cs`. For the Speed test, add one `TransferRow` whose `SpeedText` is `"1.2 MB/s"` by copying the nearest `vm.Rows.Add(new TransferRow(...))` example already in this file verbatim (do not invent the `TransferRowViewModel` field names). The width test needs no rows.

```csharp
[AvaloniaFact]
public void Transfers_default_column_widths_match_the_spec()
{
    var (window, _, _, _, _, _) = MakeView();
    window.Show();
    Layout(window);
    var grid = window.GetVisualDescendants().OfType<DataGrid>().Single();
    Assert.True(grid.Columns[0].Width.IsStar); // File
    double W(int i) => grid.Columns[i].Width.Value;
    Assert.Equal(140, W(1)); // Project
    Assert.Equal(80,  W(2)); // Direction
    Assert.Equal(190, W(3)); // Progress
    Assert.Equal(90,  W(4)); // Speed
    Assert.Equal(210, W(5)); // Status
    Assert.Equal(80,  W(6)); // Host
    window.Close();
}

[AvaloniaFact]
public void Speed_cell_uses_tabular_figures()
{
    var (window, _, vm, _, _, _) = MakeView();
    window.Show();
    // ...add one TransferRow whose SpeedText is e.g. "1.2 MB/s" via the file's helper...
    Layout(window);
    var grid = window.GetVisualDescendants().OfType<DataGrid>().Single();
    var tb = grid.GetVisualDescendants().OfType<TextBlock>().Single(t => t.Text == "1.2 MB/s");
    Assert.Contains(tb.FontFeatures!, f => f.Tag == "tnum" && f.Value == 1);
    window.Close();
}
```

- [ ] **Step 2: Run to verify they fail**

Run: `dotnet test tests/Lattice.App.Tests -c Debug --filter "FullyQualifiedName~TransfersViewTests.Transfers_default_column_widths|FullyQualifiedName~TransfersViewTests.Speed_cell_uses_tabular"`
Expected: Speed test FAIL (no `+tnum`); width test PASS (regression lock).

- [ ] **Step 3: Implement in `TransfersView.axaml`**

Speed column (line 116-117) → `CellStyleClasses="numericCell"`:

```xml
          <DataGridTextColumn Header="{x:Static loc:Strings.TransfersColSpeed}"
                               Binding="{Binding Data.SpeedText}" Width="90" CellStyleClasses="numericCell" />
```

Remove the now-shared base tint rule from `UserControl.Styles` (lines 17-19) — delete `DataGridRow.retrying`. **Keep** `Border.progressFill` and `TextBlock.retryingText`.

- [ ] **Step 4: Run to verify they pass**

Run: same filter, then full `FullyQualifiedName~TransfersViewTests` (proves the retrying-row tint still applies via the shared rule). Expected: PASS.

- [ ] **Step 5: Commit**

```bash
git add src/Lattice.App/Views/TransfersView.axaml tests/Lattice.App.Tests/Headless/TransfersViewTests.cs
git commit -m "feat(transfers): tabular figures on speed, width lock, shared tint (#57)"
```

---

## Task 8: Event log — add the column header row (#55) + gutter/tint fidelity

**Files:**
- Modify: `src/Lattice.App/Views/EventLogView.axaml`, `src/Lattice.App/Localization/Strings.resx`
- Test: `tests/Lattice.App.Tests/Headless/EventLogViewTests.cs`

- [ ] **Step 1: Write the failing tests**

Add to `EventLogViewTests.cs` (uses its `MakeView()`/`AddHost()`/`Raise()` + `Msg()` helpers):

```csharp
[AvaloniaFact]
public void Event_log_has_a_visible_column_header_row_with_spec_labels()
{
    var (window, _, _, registry, manager, fakes) = MakeView();
    var host = AddHost(registry, fakes, "host-a");
    window.Show();
    Raise(manager, host.Id, Msg(1, 1, "hello"));
    Layout(window);

    var grid = window.GetVisualDescendants().OfType<DataGrid>().Single();
    Assert.Equal(DataGridHeadersVisibility.Column, grid.HeadersVisibility);

    var labels = grid.GetVisualDescendants().OfType<DataGridColumnHeader>()
        .Where(h => h.IsVisible).Select(h => h.Content as string)
        .Where(s => !string.IsNullOrEmpty(s)).ToList();
    Assert.Contains(Strings.EventLogColTime, labels);
    Assert.Contains(Strings.ColHost, labels);
    Assert.Contains(Strings.ColProject, labels);
    Assert.Contains(Strings.EventLogColMessage, labels);
    window.Close();
}

[AvaloniaFact]
public void Event_log_columns_are_resizable_via_header_edge_drag()
{
    var (window, _, _, registry, manager, fakes) = MakeView();
    var host = AddHost(registry, fakes, "host-a");
    window.Show();
    Raise(manager, host.Id, Msg(1, 1, "hello"));
    Layout(window);

    var grid = window.GetVisualDescendants().OfType<DataGrid>().Single();
    Assert.True(grid.CanUserResizeColumns);
    var header = grid.GetVisualDescendants().OfType<DataGridColumnHeader>()
        .Single(h => (h.Content as string) == Strings.EventLogColTime);
    var col = header.OwningColumn!;
    var start = col.ActualWidth;
    var edge = header.TranslatePoint(new Point(header.Bounds.Width - 2, header.Bounds.Height / 2), window)!.Value;
    var target = edge.WithX(edge.X + 48);
    window.MouseMove(edge, RawInputModifiers.None);
    window.MouseDown(edge, MouseButton.Left, RawInputModifiers.None);
    window.MouseMove(target, RawInputModifiers.None);
    window.MouseUp(target, MouseButton.Left, RawInputModifiers.None);
    Layout(window);
    Assert.True(col.ActualWidth > start + 20);
    window.Close();
}

[AvaloniaFact]
public void Severity_gutter_column_has_no_divider_in_body_or_header()
{
    var (window, _, _, registry, manager, fakes) = MakeView();
    var host = AddHost(registry, fakes, "host-a");
    window.Show();
    Raise(manager, host.Id, Msg(1, 1, "hello"));
    Layout(window);
    var grid = window.GetVisualDescendants().OfType<DataGrid>().Single();

    // Body: the severity-icon cells (priorityIcon) collapse their divider.
    var iconCells = grid.GetVisualDescendants().OfType<DataGridCell>()
        .Where(c => c.Classes.Contains("priorityIcon")).ToList();
    Assert.NotEmpty(iconCells);
    foreach (var c in iconCells)
        Assert.Equal(0d, VisualTree.FindInVisualTree<Rectangle>(c, r => r.Name == "PART_RightGridLine")!.Width);

    // Header: the severity column header separator is off (it is nth-last-child(2):
    // ...Project, [severity], Message — robust to the Host column hiding).
    var iconHeader = grid.GetVisualDescendants().OfType<DataGridColumnHeader>()
        .Single(h => h.OwningColumn != null && h.OwningColumn.DisplayIndex == 3);
    Assert.False(iconHeader.AreSeparatorsVisible);
    window.Close();
}

[AvaloniaFact]
public void Event_log_default_column_widths_match_the_spec()
{
    var (window, _, _, _, _, _) = MakeView();
    window.Show();
    Layout(window);
    var grid = window.GetVisualDescendants().OfType<DataGrid>().Single();
    double W(int i) => grid.Columns[i].Width.Value;
    Assert.Equal(128, W(0)); // Time
    Assert.Equal(84,  W(1)); // Host
    Assert.Equal(140, W(2)); // Project
    Assert.Equal(20,  W(3)); // severity
    Assert.True(grid.Columns[4].Width.IsStar); // Message
    window.Close();
}
```

- [ ] **Step 2: Run to verify they fail**

Run: `dotnet test tests/Lattice.App.Tests -c Debug --filter "FullyQualifiedName~EventLogViewTests.Event_log_has_a_visible_column_header|FullyQualifiedName~EventLogViewTests.Event_log_columns_are_resizable|FullyQualifiedName~EventLogViewTests.Severity_gutter_column|FullyQualifiedName~EventLogViewTests.Event_log_default_column_widths"`
Expected: FAIL — `HeadersVisibility` is `None`; `EventLogColTime`/`EventLogColMessage` don't exist (compile error until Step 3 adds strings); severity header separator on.

- [ ] **Step 3: Implement**

Add strings to `Strings.resx` (sentence-case, matching the neighboring column entries):

```xml
  <data name="EventLogColTime" xml:space="preserve"><value>Time</value></data>
  <data name="EventLogColMessage" xml:space="preserve"><value>Message</value></data>
```

In `EventLogView.axaml`, on the `DataGrid` (line 150-153) remove `HeadersVisibility="None"` (theme default is `Column`):

```xml
      <DataGrid x:Name="Grid" x:FieldModifier="public" Classes="lattice eventlog"
                ItemsSource="{Binding Rows}" IsReadOnly="True"
                RowHeight="26"
                CanUserSortColumns="False" CanUserReorderColumns="False">
```

Add `Header` to each column and mark the severity gutter. Time (159), Host (167), Project (170), severity (175), Message (192):

```xml
          <DataGridTemplateColumn Width="128" Header="{x:Static loc:Strings.EventLogColTime}">
          ...
          <DataGridTextColumn Binding="{Binding Data.Host}" Width="84" Header="{x:Static loc:Strings.ColHost}"
                              FontSize="12" Foreground="{DynamicResource LatticeTextSecondaryBrush}"
                              IsVisible="{Binding IsAllHostsScope}" />
          <DataGridTextColumn Binding="{Binding Data.Project}" Width="140" Header="{x:Static loc:Strings.ColProject}"
                              FontSize="12" Foreground="{DynamicResource LatticeTextSecondaryBrush}" />
          <DataGridTemplateColumn Width="20" CellStyleClasses="priorityIcon noDivider">
          ...
          <DataGridTemplateColumn Width="*" Header="{x:Static loc:Strings.EventLogColMessage}">
```

Add the severity-header separator suppression + tint dedup to `UserControl.Styles`. The severity column is `nth-last-child(2)` (Message is always last; severity always immediately precedes it, regardless of the Host column hiding):

```xml
    <!-- Severity gutter column: no header divider (spec .dg-l>*:nth-child(4)). Target from
         the end so it survives the Host column hiding in single-host scope. -->
    <Style Selector="DataGridColumnHeadersPresenter > DataGridColumnHeader:nth-last-child(2)">
      <Setter Property="AreSeparatorsVisible" Value="False" />
    </Style>
```

Remove the now-shared base tint rules from `UserControl.Styles` (lines 63-68) — delete the `DataGridRow.warning` and `DataGridRow.error` `Style` blocks (shared file owns them). **Keep** the `ToggleButton.pill` styles, the `DataGrid.eventlog DataGridRow` height rule, and `DataGrid.eventlog DataGridCell.priorityIcon` padding rule.

> Same `:nth-last-child(2)` fallback note as Task 6: if it doesn't stick, use code-behind targeting `OwningColumn == Grid.Columns[3]`. Prefer declarative.

- [ ] **Step 4: Run to verify they pass**

Run: same filter as Step 2, then full `FullyQualifiedName~EventLogViewTests` (proves the header row didn't break the existing severity-class / follow-scroll / 26px-row / detach-drain tests, and that warning/error tints still apply via the shared rule). Expected: PASS.

- [ ] **Step 5: Commit**

```bash
git add src/Lattice.App/Views/EventLogView.axaml src/Lattice.App/Localization/Strings.resx tests/Lattice.App.Tests/Headless/EventLogViewTests.cs
git commit -m "feat(eventlog): add drag-resizable column header row (#55); gutter/tint fidelity (#57)"
```

---

## Task 9: Full-suite gate + Release build + localization sanity

**Files:** none new (verification + any small fix-ups).

- [ ] **Step 1: Localization completeness**

Run: `dotnet test tests/Lattice.App.Tests -c Debug --filter "FullyQualifiedName~LocalizationTests"`
Expected: PASS (confirms `EventLogColTime`/`EventLogColMessage` are wired into the generated `Strings` and any completeness assertion). If it flags the new keys, address per the test's contract.

- [ ] **Step 2: Full suite, Debug, warnings-as-errors**

Run: `dotnet build -c Debug -warnaserror` then `dotnet test -c Debug`
Expected: build clean; all tests green (existing + new).

- [ ] **Step 3: Release build**

Run: `dotnet build -c Release -warnaserror`
Expected: clean. (Release-only analyzer/nullable diffs surface here.)

- [ ] **Step 4: Self-review against the spec**

Re-read `docs/design/m2/README.md` §"The DataGrid is the core pattern" and confirm each bullet maps to a shipped change: header 32/600/11/#616161/#E0E0E0 ✓ (Tasks 2–3 + pre-existing), body 36/28 + 13px/#242424/#616161 ✓ (pre-existing tokens), dividers #EDEBE9 none-on-last/gutter ✓ (Tasks 2–3, 6, 8), hover #F5F5F5 under tints + dark #383838 + no header hover ✓ (Task 4), left-align + tabular ✓ (pre-existing align + Tasks 5/6/7), middle-vs-end ellipsis ✓ (Task 5), widths ✓ (locked in 5/6/7/8), Event-log header row ✓ (Task 8).

- [ ] **Step 5: Commit any fix-ups**

```bash
git add -A && git commit -m "test(grid): full-suite + release gate for M2 grid fidelity (#57, #55)"
```

---

## PR + review + MERGE GATE

- [ ] Push branch; open PR citing **#57** and **#55**. Body must include: the spec cards referenced (README §"DataGrid is the core pattern"; HTML cards `1c`/`1d`/`2a`/`2b`/`2c`); the exact values implemented (divider `#EDEBE9`/`#333`, header bottom `#E0E0E0`, hover `#F5F5F5`/`#383838`, `PrefixCharacterEllipsis` middle-ellipsis, the four width sets, Event-log header `Time 128 · Host 84 · Project 140 · severity 20 · Message 1fr`); note that `GridLinesVisibility` was `None` (dividers/row-rules were entirely absent before). List the human-verify gate below.
- [ ] Full **Codex loop** per CLAUDE.md: round-1 review is automatic (no manual `@codex review` trigger). A 👀/👍 counts only if the reactor is exactly `chatgpt-codex-connector[bot]` — verify via `gh api .../reactions` (anonymous counts are not proof); round-1 auto reactions land on the **PR body**, not a comment. Address findings; re-poll review threads ≥60 s after a review object posts; verdicts count only on the FINAL commit.
- [ ] ⚠️ **MERGE GATE — do NOT auto-merge.** Run to Codex-clean + CI-green, then **STOP** and hand back to the controller/owner to visually verify the running app: column dividers (present, quieter-than-header-rule, none on last/gutter columns), header text `#616161` + `#E0E0E0` bottom rule, row hover sitting UNDER at-risk/selected tints, Event-log header row + drag-resize, in BOTH light and dark themes. Per [[user-review-boundaries]] and the #50 precedent (headless cannot catch color fidelity).

## Report back to controller
PR number · final sha · Codex verdict + how the 👀/👍 was attributed (reactor login) · CI status · explicit **"awaiting owner visual verification"** handoff.
