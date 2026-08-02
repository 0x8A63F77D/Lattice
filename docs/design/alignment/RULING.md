# RULING #206 — chrome-mark vertical alignment

Status: FINAL — 2026-08-02
Scope: every chrome mark that sits beside a text label, all platforms (macOS, Windows, Linux), all shipped locales.
Authority: this file. The alignment lab branch is discarded; no experimental code merges.

---

## R1. Functional marks

Applies to any mark whose size is a platform idiom: 12 px status icons,
16 px play, 12 px chevron, 20 px checkbox.

    Philosophy (A) — optical centre on a fixed reference band, t = 0.

      bandTop       = baseline − capHeight
      bandBottom    = baseline
      centerY(mark) = baseline − capHeight / 2

`capHeight` is a FONT METRIC at the label's resolved size — never per-string
ink. The band is therefore invariant under text change, under locale change,
and under user data. A label seating multiple marks centres every mark on
that same band.

Face selection is DETERMINISTIC and CONTENT-BLIND, resolved per label
class:

  Localized chrome labels (script known from the UI locale): the metric
  face is the face the label's font stack resolves FOR THE LOCALE'S
  SCRIPT — under a CJK locale whose primary family carries no Han, that
  is the CJK fallback face actually rendering the text, not the
  invisible Latin primary. This is a locale-level decision made once per
  font configuration; string content still never participates. (This
  ruling's CJK accuracy figures were measured with the CJK face defining
  the band; reading a Latin primary there would centre marks on a band
  no visible glyph paints.)

  User-data labels (script unknown — project names): the metric face is
  the primary face of the label's font stack, regardless of content. A
  mixed-script or fallback-forcing name gets the same band as any other
  label at that site. (Content-dependent selection would re-introduce
  the per-string dependence this clause removes.)

The same rules select the face for R2's capHeight. The gate must
exercise the real default-plus-fallback configuration — a CJK locale
whose primary family lacks Han — so the localized rule is pinned against
the shipped font stack, not only against CJK-primary test pins.

GATE: |centerY(mark) − centerY(band)| <= 0.05 DIP, evaluated on arranged
layout geometry, at every registered site, in every registered UI font.

Registered UI fonts
  Latin, pinned in tests : Inter
  Latin, macOS runtime   : Helvetica
  CJK, macOS             : PingFang SC
  CJK, Windows           : Microsoft YaHei, Microsoft YaHei UI
                           (distinct families, distinct vertical metrics —
                           register both, do not treat as one)
  CJK, Linux             : Noto Sans SC

OPEN — blocks gate coverage, not the ruling:
  1. Latin runtime faces on Windows and Linux were never measured. The
     alignment lab covered macOS runtime only (Helvetica) plus the pinned
     test font (Inter). Register and measure whatever the Windows and Linux
     stacks actually resolve for Latin labels before claiming three-platform
     gate coverage. R1 is unaffected in form: t = 0 reads capHeight from
     whichever face resolves, so adding faces is mechanical.
  2. PingFang SC figures in this ruling come from a third-party webfont
     subset. Want one confirmation run on real macOS hardware.

## R2. Statistics legend swatch

The one mark whose size is open, and decorative rather than functional.

      swatchHeight = swatchWidth = capHeight(label font, resolved size)
      swatchTop    = baseline − capHeight
      swatchBottom = baseline
      cornerRadius = 2                        (= borderRadiusSmall)

Philosophies (A) and (B) coincide here by construction: swatch position is
independent of the label's descender content and of font choice. This is
what removes the legend from the dispute — project names are user data, and
the descender behaviour of "@" flips between the two Latin fonts.

GATE: swatch edges equal cap-band edges ± 0.05 DIP; radius == 2.

## R3. Digit-only cells

Deadlines and RAC keep the digit band. No change. Restated here only so the
gate's site registry is exhaustive.

## R4. Site exceptions

NONE. The legend needs no descender exception because its only mark is the
swatch, ruled by R2.

---

## Why t = 0, and what it costs

t = 0 (cap band) is the literal mechanisation of Fluent's "optically centre
the icon beside the label". Its cost is real and accepted: against labels
carrying descenders it seats the mark 1.31–1.55 DIP high (worst case
"Warning", Helvetica). The rejected alternative (d), t = 0.5, halves that to
0.82 DIP.

t = 0 was ruled anyway on three grounds:

1. **Script independence.** t = 0.5 compensates for descenders. Han
   characters have none, so the compensation is a pure loss against CJK
   labels. Measured across five CJK faces (PingFang SC, Microsoft YaHei,
   Microsoft YaHei UI, Noto Sans SC, Sarasa UI SC): t = 0 lands −0.50…0.00
   DIP, t = 0.5 lands +0.25…+0.75 DIP. t = 0 is the only rule that needs no
   script branch.
2. **Bilingual lists stay flush.** Icon height difference between Latin and
   CJK rows in one column: t = 0 → 0.00 DIP; t = 0.5 → 0.25 DIP; a
   script-dispatched rule → 0.50 DIP. Only t = 0 guarantees a flush column
   in a mixed-locale list.
3. **One constant, no new mechanism.** Both candidates are single-parameter
   fixed bands with identical gate shape. t = 0.5 buys 0.73 DIP on half the
   Latin repertoire and spends complexity and CJK accuracy for it.

Verified not to be a factor: the Fluent icon font mixed with the text fonts
introduces no hidden constant — glyph ink centre sits exactly on the 12 px
box centre, offset 0.00.

Also excluded, concurring with the lab: (c) saturates its cap in 11 of 14
sites and is inexpressible on two-mark buttons; (b) grows the event-log
badge ~1 px and merely moves the same error onto the other half of the
repertoire.

## Falsification

t = 0 is optimal for a repertoire in which descender-free and CJK labels
dominate, and for any UI that mixes scripts in one column.

If the shipped repertoire becomes predominantly Latin AND predominantly
descender-carrying, AND mixed-script columns are eliminated, re-run the
alignment lab: t = 0.5 then dominates, and R1 is re-ruled by flipping the
single constant — no other clause changes.

Re-measure trigger: any new UI font, any new locale whose script has ink
below the baseline in ordinary labels.
