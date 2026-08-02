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

`Lattice 206 Alignment Ruling Draft.dc.html` is a **design reference created in
HTML** — a measurement and evidence page used to reach the ruling, not
production code and not a component to port. It renders live specimens under
each candidate rule and measures them in the browser. Open it to see why the
ruling is what it is, or to re-run the comparison on another platform's fonts.
Do not copy code from it.

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

The gate must cover all fifteen. Names are as they appear in the alignment lab's
`measurements.tsv` (`View[Mark+Label]`).

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
| legend / swatch | `StatisticsView[Panel+Einstein@Home]` | R2 |
| legend / swatch | `StatisticsView[Panel+LHC@home]` | R2 |

The two `TasksView[...+Computing]` sites are the two-mark case: chevron and play
share one label and therefore one band.

## The gate

```
R1  |centerY(mark) − centerY(band)| <= 0.05 DIP
R2  swatch edges == cap-band edges ± 0.05 DIP
R2  cornerRadius == 2
```

- Assert on **arranged layout geometry**, not on rendered pixels — pixel
  snapping is platform noise and will make the gate flaky at this tolerance.
- Run every registered site × every registered UI font.
- The registry is data, not code paths: adding a font or a site must not require
  touching R1/R2 logic.

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
   resolve for Latin labels before claiming three-platform gate coverage. R1 is
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
- `Lattice 206 Alignment Ruling Draft.dc.html` — evidence page (design
  reference, not production code). Open in a browser; it measures the live
  fonts on whatever machine opens it.
- `measurements.tsv` — the alignment lab's raw data: 2 fonts × 4 candidates ×
  15 sites, arranged and rendered-pixel values.
- `shipped-band-verdict.tsv` — per-site verdict for the shipped band.
- `support.js`, `_ds/` — runtime and Fluent token/icon CSS the evidence page
  loads. Present only so it opens offline; not part of the deliverable. The icon
  CSS pulls the Fluent System Icons webfont from a CDN, so the evidence page
  needs network access to render glyphs.

## How to verify an implementation against this bundle

1. Implement R1/R2 from the formulas above.
2. Add the gate over the fifteen registered sites × registered fonts.
3. Cross-check a handful of sites against `measurements.tsv`: filter
   `candidate == "a"` and compare your arranged `vsCap` against the column of
   the same name. Ruled geometry means `vsCap == 0` by construction; `vsWordInk`
   is the optical error and is expected to be non-zero.
4. `shipped-band-verdict.tsv` gives the per-site top/bottom verdict for the
   shipped band and is the quickest regression reference.
