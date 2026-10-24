---
paths:
  - "tests/Lattice.Tests/**"
  - "src/Lattice.Core/SnapshotBuilder.cs"
  - "src/Lattice.Core/HostControlService.cs"
  - "src/Lattice.Core/ControlOpResult.cs"
  - ".github/workflows/mutation-tier1.yml"
  - ".github/workflows/mutation-tier2.yml"
  - ".config/dotnet-tools.json"
  - "**/*.fsproj"
---

# Mutation testing and test-framework facts

## Stryker.NET (4.16) behaviour

- Must run with `--test-runner mtp`: the default VSTest runner cannot execute xunit v3 tests and
  the score collapses to ~0% with every mutant "survived" (stryker-net#3117). The failure is loud.
- `// Stryker disable once <Mutator>` binds to the mutated NODE by trivia attachment, not by line.
  It silently fails on continuation-line mutants (an outer `.ConfigureAwait` of a multi-line
  call, a mutant inside a multi-line `&&` operand). Always re-run and confirm the survivor dropped.
- Block `// Stryker disable <Mutator>` … `restore` suppresses EVERY mutant in the range regardless
  of the named mutator — it can mask killed mutants. Confirm the killed count is unchanged.
- The Tier-1 gate is incremental (`--since:origin/main`) and scores per changed file: a file can
  pass the combined nightly yet false-block a future single-file PR. Keep each file's isolated
  score above `break`.
- If an equivalent mutant shares a line with a killed one, isolate it with a behaviour-preserving
  reformat and a `disable once`, rather than a block disable.
- Do not extract production code into new assemblies only so Stryker can reach it (refactoring to
  feed the metric tool). Restart trigger: a real policy-module bug escaping.

## xunit v3 and FsCheck

- The whole suite is on xunit v3. xunit v2→v3 is package-only when a project has no
  `Xunit.Abstractions` / `ITestOutputHelper` / `IAsyncLifetime`; no test project needs
  `<OutputType>Exe</OutputType>`.
- FsCheck for xunit v3 is the separate package id `FsCheck.Xunit.v3`. FsCheck 3 moved `Gen`/`Arb`/
  `Prop` and the `gen` builder to `FsCheck.FSharp`; `Arb.generate<'T>` became
  `ArbMap.defaults |> ArbMap.generate<'T>`.
- After any property-engine swap, break one property per project on purpose and require
  "Falsifiable, after N tests" — a dead generator looks exactly like a green suite.
