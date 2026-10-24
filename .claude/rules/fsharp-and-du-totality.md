---
paths:
  - "**/*.fs"
  - "**/*.fsproj"
  - "src/Lattice.App/**/*.cs"
  - "src/Lattice.Core/**/*.cs"
---

# F# idiom and DU-totality review notes

Supplements the F# style canon in CLAUDE.md.

## Where F# goes

- Use F# wherever the domain plays to functional strengths: pure transformation pipelines, state
  machines, chart data aggregation, property-test generators. Keep UI, INPC, string formatting and
  XAML-facing types in C#; GuiRpc parsing stays C# (published package surface). State the language
  choice and why when proposing a design.
- Default verification tier for F# pipelines: correctness by construction + FsCheck properties.
  Promela is reserved for temporal/concurrent semantics.
- Plan-doc F# is born functional: with transcription-tier implementers the plan snippet IS the
  product (#38 root cause).

## Totality claims need an actual match

- `if/elif/else` over scalars, lengths or booleans is idiomatic here — do not flag it.
- The real smell: code that CLAIMS or RELIES on DU totality without a `match` over that DU's cases.
  Counting or comparing by `= Case` is invisible to the exhaustiveness checker; CONSTRUCTING cases
  from booleans is not matching them. Before enshrining a totality claim, locate the exhaustive
  `match` and probe it (add a case, confirm FS0025 under warnings-as-errors).
- An if/elif ladder enumerating a domain type's case combinations wants to be a tuple match.
- C# consumers of an F# DU get no exhaustiveness (`Is*` chains and `Tag` switches are unchecked).
  Gate them with a reflection-driven totality test: enumerate `FSharpType.GetUnionCases`, build each
  with `FSharpValue.MakeUnion`, map every one — never a hand-listed set. Add a bijection test for
  the reverse direction.
- When two representations of one concept are passed as two arguments, delete one and derive it,
  so a mismatch becomes a compile error rather than something a test must catch.
- Byte-identical refactors: a temporary FsCheck differential property against a verbatim legacy
  copy, run green, then deleted (not committed).

## HostMachine vocabulary (`src/Lattice.Core.Machine/HostMachine.fs`)

- Any change to `HostMachine.Phase` must extend the I6 phase→lifecycle projection table in the same
  commit (both encodings + the doc table, `verification/README.md` rule 4). Prefer domain-shaped
  names (connection states); justify operational names (named after pending I/O) in the commit
  message. Surface framing/vocabulary smells proactively.
