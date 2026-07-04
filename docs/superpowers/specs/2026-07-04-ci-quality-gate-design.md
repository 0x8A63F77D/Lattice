# CI and Quality Gate Design (pre-M2)

Date: 2026-07-04
Status: approved

## Goal

Before M2 development starts, set up continuous integration and merge gating for the GitHub repository `0x8A63F77D/Lattice`: every change reaching `main` must pass a full build (zero warnings) and the entire test suite.

## Decision record

| Question | Decision | Rationale |
|---|---|---|
| Scope | CI + gate only, no release pipeline | M2 is UI work with no packaging needs in the near term; add NuGet publishing when an actual release calls for it (YAGNI) |
| Gate strictness | `main` accepts PR merges only + required check, no admin bypass | The workflow already goes through PRs (including Codex Review); escape hatch = temporarily disabling the ruleset |
| Runner platform | `ubuntu-latest` only, matrix-shaped for future expansion | The protocol library is pure managed code; local development already covers Windows, CI covers the Linux side; widen the matrix when M2 introduces platform-specific code |
| Checks | Release build with `-warnaserror` + full test suite | Locks in the current zero-warning state; formatting checks / coverage thresholds deferred until they earn their keep |
| Protection mechanism | Repository ruleset (not legacy branch protection) | GitHub's current recommended form, richer configuration, free for public repos |

## Section 1: CI workflow

File: `.github/workflows/ci.yml`

- **Triggers**: `pull_request` (targeting `main`) + `push` (`main` branch). Running again on `main` after merge guards against the merge result differing from the PR head.
- **Job `build-test`** with `strategy.matrix.os: [ubuntu-latest]`. The single-entry matrix is deliberate: expanding platforms in M2 is a one-line change, and the job display name stays a stable `build-test` so the required check does not need reconfiguring.
- **Steps**:
  1. `actions/checkout`
  2. `actions/setup-dotnet`, SDK version `10.0.x`
  3. `dotnet build Lattice.sln -c Release -warnaserror`
  4. `dotnet test Lattice.sln -c Release --no-build`
- **Concurrency**: `concurrency: ci-${{ github.ref }}` with `cancel-in-progress: true` — pushing a new commit to the same branch cancels the stale run.
- Build and test in Release configuration, matching the future packaging configuration. `-warnaserror` applies in CI only; local builds are unaffected.

**Implementation note (observed during rollout):** even with an explicit job `name`, GitHub appends the matrix value to the check run name, so the actual required-check context is `build-test (ubuntu-latest)`. When the matrix widens, each leg becomes its own context and the ruleset's required checks must be updated in the same change.

## Section 2: Branch protection (ruleset)

One repository ruleset on `main`:

- Merges via PR only (`pull_request` rule with 0 required approvals — requiring approvals would deadlock a single-maintainer repo)
- Required status check: `build-test (ubuntu-latest)`
- Force pushes and branch deletion forbidden
- No bypass actors (admins are bound too)

Escape hatch: in an emergency, temporarily switch the ruleset to disabled under Settings → Rules and restore afterwards. It is an explicit action that leaves an audit trail.

## Section 3: Rollout order

1. Commit the CI workflow on a branch off `main`, open a dedicated PR, merge it into `main`
2. Enable the ruleset only after that merge (in the reverse order, the CI PR would be stuck behind a required check that does not exist yet)
3. Trigger CI on any PR opened before the workflow existed (empty commit or re-run) so it passes the gate before merging

## Test strategy

The gate itself gets verified once, for real:

- Push a temporary branch with a compiler warning, open a PR → expect CI red (`-warnaserror` works)
- Push a temporary branch with a failing test, open a PR → expect CI red
- Verify `main` rejects direct pushes (git push refused by the ruleset)
- Verify the merge button is disabled while CI is red
- Delete the temporary branches afterwards

## Definition of done

- [x] `ci.yml` merged into `main`, triggered by both PRs and `main` pushes
- [x] Ruleset active: no direct pushes or force pushes to `main`; PRs must have `build-test (ubuntu-latest)` green to merge
- [x] Gate behavior verified per the test strategy
- [x] Pre-existing PRs pass the gate (PR #1 was merged before the workflow landed; the gate applies from M2 onward)
