# Handoff: Lattice #206 — chrome-mark vertical alignment

## Overview

Every chrome mark that sits beside a text label in Lattice (status icons, play
button, chevron, checkbox, and the statistics legend swatch) is vertically
aligned by an ad-hoc rule per site. Issue #206 asked for one ruling that covers
all of them. This package is that ruling plus everything needed to implement and
gate it.

**This is not a UI redesign.** No colours, copy, spacing or component structure
change. What changes is a single geometric rule for where a mark's vertical
centre sits relative to its label, applied uniformly, plus a new automated gate
that asserts it.

The ruling is `RULING.md` in this bundle. It is the contract; the numbers below
are its supporting evidence. If the two ever disagree, `RULING.md` wins.

## About the design files

`evidence/alignment-evidence.html` is a **design reference created in HTML** — a
measurement and evidence page used to reach the ruling, not production code and
not a component to port. It renders live specimens under each candidate rule and
measures them in the browser. Open it to see why the ruling is what it is. For
the open items below its reach is uneven: the PingFang SC real-hardware
confirmation IS within it (the page carries named CJK probes and detects local
faces), but the Windows/Linux Latin item is NOT — the page's Latin probe
hard-codes the macOS stack (`Helvetica, 'Helvetica Neue', Arial`), so on other
platforms it silently measures an unidentified browser fallback, not the face
Avalonia resolves. The instrument for open item 1 is the implementation gate
itself, reading the cap-height metric of the face Avalonia actually resolves —
which the limitation note below makes authoritative anyway. Do not copy code
from it.

**Known measurement limitation.** The page reads capHeight as the ink bounds of
an `H` glyph (`measureText('H').actualBoundingBoxAscent`) because canvas
TextMetrics exposes no cap-height font metric. That proxy was applied uniformly
to every candidate and every face, so the *comparative* evidence behind the
ruling stands; but a face whose `H` ink differs from its declared cap height —
hinting alone can move ink a device pixel — will show *absolute* band figures
that differ from the normative metric. R1's `capHeight` is the **font metric**,
full stop: the implementation gate must read it from the resolved face (e.g.
Skia's `SKFontMetrics.CapHeight`), never from glyph ink. When using this page
to evidence a new face, treat its figures as proxy comparisons and confirm the
metric value in the implementation's own font stack.

The implementation target is the existing Lattice codebase and its existing
layout primitives. Nothing here should introduce a new styling mechanism.

## Fidelity

**High-fidelity, and numerically normative.** The geometry is exact and the gate
tolerance is ±0.05 DIP. Implement the formulas verbatim rather than
approximating them by eye or by nudging offsets until a screenshot looks right.

---

## The ruling, in implementation terms

### R1 — functional marks

Applies to any mark whose size is a platform idiom: 12 px status icons, 16 px
play, 12 px chevron, 20 px checkbox.

```
bandTop       = baseline − capHeight
bandBottom    = baseline
centerY(mark) = baseline − capHeight / 2
```

- `capHeight` is a **font metric read from the label's resolved face at its
  resolved size**. It is never measured from the label's actual glyphs. Two
  labels in the same face and size get the same band even if one has descenders
  and the other does not.
- **Face selection is deterministic and content-blind**, resolved per label
  class (normative text in `RULING.md`):
  - *Localized chrome labels* — the anchor is the script of the **resolved
    resource set** (the language whose strings actually loaded), NOT the OS
    locale: under System preference on an unsupported CJK locale (ja-JP,
    ko-KR, zh-TW) the culture stays CJK while neutral-English resources load,
    so the labels are Latin and so is the band — the shipped word-band
    mechanism already anchors on a resource-set string for exactly this
    reason. The metric face is the face the font stack resolves for that
    script: under a CJK-resolved resource set whose primary family lacks Han,
    the CJK fallback face actually rendering the text, not the invisible
    Latin primary. Per-resource-set, per-font-configuration; the live label's
    content still never participates.
  - *User-data labels* (project names) — script unknown, so the metric face is
    the **primary face** of the label's font stack regardless of content; a
    mixed-script or fallback-forcing name gets the same band as every other
    label at that site.
  The gate must include a fallback-forcing, mixed-script label sample at the
  user-data sites (legend swatch, overflow checkbox), AND must run the
  localized-label sites under both shipped configurations: the real
  default-plus-fallback configuration (a CJK-resolved resource set whose
  primary family lacks Han) and the unsupported-CJK/System configuration
  (CJK OS culture, English resource fallback → Latin band) — rather than
  only CJK-primary test pins.
- The mark's box is *not* resized to the band. Only its centre is constrained. A
  12 px icon beside a 12 px label overhangs the band top and bottom — that is
  correct and intended.
- A label seating more than one mark centres **every** mark on that same band.
- t = 0 in the lab's parameterisation. If the ruling is ever revisited, the only
  value that changes is this constant (see Falsification in `RULING.md`).

### R2 — statistics legend swatch

The one mark whose size is open, and decorative rather than functional.

```
swatchHeight = swatchWidth = capHeight(label font, resolved size)
swatchTop    = baseline − capHeight
swatchBottom = baseline
cornerRadius = 2                      // = borderRadiusSmall
```

The swatch box **is** the band. Today it is a fixed 12 px square with radius 3;
it becomes a cap-height square with radius 2 (≈8.6 px at a 12 px Helvetica
label). Radius 2 is the Fluent `borderRadiusSmall` token and is also the
current 3 px scaled proportionally to the new size (3 × 8.6/12 ≈ 2.15).

### R3 — digit-only cells

Deadlines and RAC cells keep the digit band. **No code change.** Listed only so
the gate's site registry is exhaustive.

### R4 — site exceptions

None. The legend needs no descender exception because its only mark is the
swatch, ruled by R2.

---

## Registered sites

The gate must cover all eighteen. Names follow the alignment lab's
`measurements.tsv` convention (`View[Mark+Label]`).

| Group | Site | Rule |
|---|---|---|
| pills | `EventLogView[IconCheckmarkFilled+Error]` | R1 |
| pills | `EventLogView[IconCheckmarkFilled+Info]` | R1 |
| pills | `EventLogView[IconCheckmarkFilled+Warning]` | R1 |
| freshness | `ProjectsView[IconWarningFilled+Updated 4 m ago]` | R1 |
| freshness | `TasksView[IconWarningFilled+Updated 4 m ago]` | R1 |
| freshness | `TasksView[IconWarningFilled+3 deadlines at risk]` | R1 |
| freshness | `TransfersView[IconWarningFilled+Updated 4 m ago]` | R1 |
| computing | `TasksView[IconChevronDownRegular+Computing]` | R1 |
| computing | `TasksView[IconPlaySettingsRegular+Computing]` | R1 |
| cells | `TasksView[Running]` | R1 |
| cells | `ProjectsView[Active]` | R1 |
| cells | `TransfersView[Active]` | R1 |
| cells | `TasksView[07-11 00:00]` | R3 (digit band, unchanged) |
| snooze pill | `ShellWindow[IconPauseRegular+SnoozeTime]` | R3 (digit band, unchanged) |
| legend / swatch | `StatisticsView[Panel+Einstein@Home]` | R2 |
| legend / swatch | `StatisticsView[Panel+LHC@home]` | R2 |
| overflow | `StatisticsView[OverflowCheckBox+ProjectName]` | R1 |
| rail group header | `ShellWindow[IconChevronRightRegular+GroupHeader]` | R1 |

The two `TasksView[...+Computing]` sites are the two-mark case: chevron and play
share one label and therefore one band.

The overflow, rail-group-header and snooze-pill rows were added after the
alignment lab ran (Codex review rounds on the landing PR each flagged a shipped
site the lab missed), so they have **no rows in `measurements.tsv`** —
`measurements.tsv` is historical evidence for the lab-measured sites only. The
snooze pill (`ShellWindow.axaml`, 10 px pause mark beside the pinned-Inter time
label) is a second digit-band construction alongside the Deadline cell — R3,
already aligned, no code change; registered so the gate exercises it.

- `StatisticsView[OverflowCheckBox+ProjectName]` is the "+N more" flyout row
  (`Views/StatisticsView.axaml`): a 20 px checkbox beside a user-data project
  name — the checkbox class R1 itself names. The label is user data; R1 applies
  unchanged (capHeight of the label's resolved face — no per-site exception,
  per R4).
- `ShellWindow[IconChevronRightRegular+GroupHeader]` is the rail's collapsible
  group header (`Views/ShellWindow.axaml`): a Lattice-authored DataTemplate
  seating a 12 px chevron beside the 11 px group-header label — inside the
  scope boundary (our XAML positions it; it shares the label's line box). The
  chevron's expand rotation is a RenderTransform and does not move its layout
  centre, so R1 gates it like any other mark.

One registry row covers one unique construction. The shared status-bar
template (`Theming/ControlStyles.axaml`) is **already represented**: its
warning section is exactly the `TasksView[IconWarningFilled+3 deadlines at
risk]` entry (`StatusBarControl.WarningText`), and the template's other
instances across views are the same construction — do not add per-view rows
for it, and do not count it missing.

**Scope boundary.** The ruling's "every chrome mark that sits beside a text
label" governs marks whose position Lattice's own XAML determines — the class
above. Marks inside third-party control templates (NavigationView item icons,
FluentAvalonia-internal chrome) follow the framework's own alignment and are out
of scope; so are icons not sharing a line box with a label (icon-only buttons,
the first-run hero icon stacked above its caption). Adding a NEW in-class site
without registering it here is the regression this table exists to prevent.

## The gate

```
R1  |centerY(mark) − centerY(band)| <= 0.05 DIP
R2  swatch edges == cap-band edges ± 0.05 DIP
R2  cornerRadius == 2
```

- Assert on **arranged layout geometry**, not on rendered pixels — pixel
  snapping is platform noise and will make the gate flaky at this tolerance.
- Run every registered site × every registered UI font.
- **The table above is a point-in-time census, not the gate's source of
  truth.** Three review rounds each surfaced a shipped in-class site the
  hand-maintained table had missed; a hand table cannot hold this invariant.
  Every in-class site already wears the alignment mechanism itself (the band
  style class / collapse converter today, the R1/R2 primitives after
  implementation), so the gate must **enumerate its sites by that structural
  marker** across the app's views. A site that adopts the mechanism is gated
  automatically; editing this table is never a precondition for coverage. The
  census keeps two roles: the implementation-time conversion checklist, and
  the record reviewers diff against.
- **A second, independent census guards against unmarked sites.** Marker
  enumeration alone cannot catch an in-class site that never adopts the
  mechanism — so the gate pairs it with an independent discovery pass: walk
  the instantiated views' Lattice-authored templates for mark-beside-label
  candidates (an icon / checkbox / swatch primitive sharing a line container
  with a text element), and assert every candidate either wears the mechanism
  or appears on an explicit, adjudicated exclusion list. The two enumerations
  check each other: geometry is gated over the marker set; mechanism adoption
  is gated over the discovered set. Note this assertion is EXPECTED to fail
  for the overflow checkbox and rail group header until the implementation
  converts them — that failure is the census doing its job, not noise.
- The registry stays data, not code paths: adding a font or a site must not
  require touching R1/R2 logic.

### Registered UI fonts

| Role | Face |
|---|---|
| Latin, pinned in tests | Inter |
| Latin, macOS runtime | Helvetica |
| CJK, macOS | PingFang SC |
| CJK, Windows | Microsoft YaHei **and** Microsoft YaHei UI |
| CJK, Linux | Noto Sans SC |

Microsoft YaHei and Microsoft YaHei UI are **distinct families with distinct
vertical metrics**. Register both; do not collapse them.

### Open — blocks gate coverage, not the ruling

1. **Latin runtime faces on Windows and Linux were never measured.** The
   alignment lab covered macOS runtime (Helvetica) plus the pinned test font
   (Inter). Register and measure whatever the Windows and Linux stacks actually
   resolve for Latin labels before claiming three-platform gate coverage —
   using the implementation gate's own metric readout on those platforms, not
   the evidence page (its Latin probe is macOS-specific; see above). R1 is
   unaffected in form — `t = 0` reads `capHeight` from whichever face resolves,
   so adding faces is mechanical.
2. **PingFang SC figures come from a third-party webfont subset.** One
   confirmation run on real macOS hardware is wanted.

---

## What this costs, so it is not mistaken for a regression

R1 keeps today's rule (the lab's candidate (a)). Against Latin labels carrying
descenders it seats the mark high — worst case 1.55 DIP on "Warning" in
Helvetica. The rejected alternative (candidate (d), t = 0.5) halves that to 0.82
DIP but loses on CJK and on mixed-script columns.

Worst optical error by candidate, Helvetica, DIP (negative = mark sits high):

| Label | (a) ruled | (d) rejected |
|---|---|---|
| Warning (descender) | −1.55 | −0.82 |
| Running (descender) | −1.33 | −0.70 |
| Updated 4 m ago (descender) | −1.31 | −0.69 |
| Computing (descender) | −1.31 | −0.63 |
| Info (no descender) | −0.06 | +0.67 |
| Error (no descender) | −0.13 | +0.60 |
| Active (no descender) | −0.11 | +0.51 |
| **worst in repertoire** | **1.55** | **0.82** |

Ruled (a) anyway on three grounds:

1. **Script independence.** t = 0.5 compensates for descenders; Han characters
   have none, so it is a pure loss against CJK labels. Measured across five CJK
   faces: (a) lands −0.50…0.00 DIP, (d) lands +0.25…+0.75 DIP.
2. **Bilingual columns stay flush.** Icon height difference between Latin and
   CJK rows in one column: (a) 0.00 DIP, (d) 0.25 DIP, script-dispatched rule
   0.50 DIP. Only (a) guarantees a flush mixed-locale list.
3. **One constant, no new mechanism.**

Verified not to be a factor: the Fluent icon font mixed with the text faces
introduces no hidden constant — glyph ink centre sits exactly on the 12 px box
centre, offset 0.00.

Also excluded, concurring with the lab: (c) saturates its cap in 11 of 14 sites
and is inexpressible on two-mark buttons; (b) grows the event-log badge ~1 px and
merely moves the same error onto the other half of the repertoire.

## Design tokens touched

| Token | Value | Where |
|---|---|---|
| `borderRadiusSmall` | 2 | R2 swatch corner radius (was a hard-coded 3) |

No colour, spacing, or type token changes. The swatch's fill colour, the icon
colours, and all label typography are unchanged.

## Assets

None. All marks are existing Fluent System Icons glyphs already in the codebase;
the swatch is a plain filled rectangle.

## Files in this bundle

- `RULING.md` — the contract. Normative.
- `README.md` — this file.
- `measurements.tsv` — the alignment lab's raw data: 2 fonts × 4 candidates ×
  the 15 lab-measured sites, arranged and rendered-pixel values. The three
  post-lab registry sites (overflow checkbox, rail group header, snooze pill)
  have no rows here.
- `shipped-band-verdict.tsv` — shipped-band verdict for the 11 lab-measured
  mark-bearing sites (see the verification notes below for what it omits).
- `evidence/alignment-evidence.html` — evidence page (design reference, not
  production code). Open in a browser; it measures the live fonts on whatever
  machine opens it.
- `evidence/support.js`, `evidence/_ds/` — runtime and token/icon CSS the
  evidence page loads. Present only so it opens from the repo; not part of the
  deliverable. The icon CSS pulls the Fluent System Icons webfont from a CDN,
  so the evidence page needs network access to render glyphs.

## How to verify an implementation against this bundle

1. Implement R1/R2 from the formulas above.
2. Add the gate over the registered sites × registered fonts — enumerated by
   the structural marker, cross-checked against the eighteen-site census above.
3. Cross-check a handful of sites against `measurements.tsv`: filter
   `candidate == "a"` and compare your arranged `vsCap` against the column of
   the same name. Ruled geometry means `vsCap == 0` by construction; `vsWordInk`
   is the optical error and is expected to be non-zero. The three post-lab
   registry sites (overflow checkbox, rail group header, snooze pill) have no
   rows to cross-check against — the gate itself is their evidence.
4. `shipped-band-verdict.tsv` gives the per-site top/bottom verdict for the
   shipped band — the quickest regression reference for the 11 sites it covers.
   It omits the four grid-cell sites (`TasksView[Running]`,
   `ProjectsView[Active]`, `TransfersView[Active]`, and the R3 digit cell
   `TasksView[07-11 00:00]`): the digit cell is unchanged by this ruling so no
   verdict applies, and the three status cells were lab-measured (they are in
   `measurements.tsv`) but got no verdict rows. Re-run
   `evidence/alignment-evidence.html` if a verdict for those cells is needed.
