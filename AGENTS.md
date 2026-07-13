# AGENTS.md — review-relevant canon for cross-tool agents

Lattice is a multi-host BOINC monitoring dashboard: a cross-platform desktop app built with
Avalonia and FluentAvalonia (Fluent 2). It is a BOINC GUI RPC *client* only — all real work is
done by the official `boinc` daemon on each host; Lattice connects over TCP and renders state.

## Solution boundaries (violations are review findings)

- `Lattice.Boinc.GuiRpc` — protocol layer, single-host semantics only. Knows nothing about
  multiple hosts, polling policy, or the app. Publishable as a standalone NuGet package.
- `Lattice.Core` — multi-host aggregation, polling cadence, reconnect/backoff, state diffing.
  Depends on GuiRpc, never the reverse. No UI dependencies, no direct socket code.
- `Lattice.Core.Machine` — pure F# decision core (`HostMachine.step`). No I/O, no deps.
- `Lattice.App` — Avalonia UI. No protocol logic; ViewModels consume Core observables/events.

## F# style canon — review-blocking gates

- Purity is judged at the function boundary (referential transparency; mutation never escaping),
  not by a token blacklist.
- Prefer semantically specific combinators (`map` / `filter` / `choose` / `collect` / `groupBy` /
  `mapFold`) over `fold`; `fold` is the last resort.
- No wildcard `| _ ->` on domain DUs outside predicate lambdas
  (`function X -> true | _ -> false` is the acceptable form). DU totality is why F# was chosen.
- `Option` for absence; `Result` with typed errors for expected failure. Convert exception-style
  .NET APIs at the boundary.
- Seq re-enumeration is a correctness trap, not a style point: materialize expensive sources at
  an explicit point before multi-pass use.
- Sanctioned imperative kernels: a perf-relevant algorithm kernel may use imperative collections
  only when the mutation is confined inside the function, the external interface is pure, and a
  short boundary comment states the exception.
- `mutable` / `<-` / `ResizeArray` / imperative loops in F# domain logic are a review-blocking
  finding unless covered by the sanctioned-kernel rule. Mutation-as-simulation in test oracles is
  acceptable when it aids clarity.

## C# enum switches

Switch expressions over domain enums must not have a `_` arm (it defeats the CS8509
new-named-case guard). Note: even with all named values covered, the compiler emits CS8524
(unnamed values), which fails under `-warnaserror`; the accepted fix is a local
`#pragma warning disable CS8524` around the switch, never a `_` arm.

## Verification-sync contract (hard workflow rule)

Any semantic change to `src/Lattice.Core/HostMonitor.cs` must update, in the SAME commit:

1. the F# executable spec (`tests/Lattice.Verification/`),
2. the Promela model (`verification/HostMonitor.pml`),
3. the shared-state inventory + probe-point list.

A commit touching HostMonitor semantics without these must state in its message why no model
change is needed.

## Test determinism

- Never wait on transient states or booleans that can go true early — settle on the expected
  text or a fake's observed calls. Wall-clock settles are banned.
- Test dispatchers must reproduce production ordering semantics: lock-only serialization is not
  `Dispatcher.UIThread.Post` — use the deferred-queue dispatcher for multi-monitor fixtures.
- Visual bugs: geometry/pixel probing (headless Skia) is the verification bar; a fix without
  end-state visual verification is not done.

## Mutation gates (Stryker.NET pilot, issue #77)

- Tier 0 (`dotnet test` per-PR) is unchanged. Tier 1 runs incremental Stryker on PRs touching the
  mutation scope — report-only during calibration. Tier 2 is a nightly full run posting the score
  to issue #77. Scope is pinned in `tests/Lattice.Tests/stryker-config.json`; never widen it
  repo-wide.
- Never add assertions solely to kill a surviving mutant or raise the mutation score — equivalent
  mutants exist, and survivor adjudication belongs to the controller session, not the implementer.

## Protocol layer (`Lattice.Boinc.GuiRpc`)

- Protocol code changes must come with fixture-based tests in the same commit (canned XML
  fixtures; no live daemon required).
- Never parse BOINC error message text — wording changes between versions. Branch only on
  structural tags (`<error>`, `<unauthorized/>`, `<success/>`).
- Self-closing tags must have NO space before the slash: send `<authorized/>`, never
  `<authorized />`. The daemon's parser is not a real XML parser; parse its output leniently.

---

CLAUDE.md is the authoritative full canon; this file is the review-relevant distillation.
If they conflict, CLAUDE.md wins.
