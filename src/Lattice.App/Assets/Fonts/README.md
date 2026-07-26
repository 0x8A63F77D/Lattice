# Embedded fonts

## Inter-Regular.ttf

| | |
|---|---|
| Family | `Inter` (name-table ID 1), style Regular, weight 400 |
| Version | 3.019 (`Version 3.019;git-0a5106e0b`) |
| Copyright | Copyright 2020 The Inter Project Authors (https://github.com/rsms/inter) |
| License | SIL Open Font License 1.1 — [OFL.txt](OFL.txt), shipped alongside the app binary |
| Provenance | Byte-copied out of the `Avalonia.Fonts.Inter` 12.1.0 package asset `avares://Avalonia.Fonts.Inter/Assets/Inter-Regular.ttf` (sha256 `41ab0f707a2bfab8133ccdfcdab52282f5f79e5751f43a264805451c7bb95fb8`) |

**Why this file and not the NuGet package.** `tests/Lattice.VisualTests` pins Inter as its default
family through that same package, so the pixel gates already render Inter. Vendoring the identical
bytes means the shipped pin and the gate's font are the same outlines — the gate sees what users
see — while the app carries only the one face it uses instead of the package's six weights, and the
`avares://Lattice/...` URI is distinguishable from the harness default (which is what lets
`SnoozePillAlignmentTests.Pill_time_is_pinned_to_the_embedded_face` fail when the pin is removed).

**Where it is used.** The snooze pill's time digits ONLY (`ShellWindow.axaml`, `SnoozePillTime`) —
issue #181, a deliberately local pin. The app-wide font question (#152, CJK coverage) is open and
this file does not pre-empt it: Inter has no CJK glyphs, so widening its use needs that decision
first. Adding a weight here means adding the matching static face, not synthesising one.
