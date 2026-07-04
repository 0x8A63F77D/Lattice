# CI and Quality Gate Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Set up GitHub Actions CI and a merge gate for `main` on `0x8A63F77D/Lattice`: every change must go through a PR, build in Release with zero warnings, and pass all tests before reaching `main`.

**Architecture:** One workflow (`ci.yml`, single job `build-test`, matrix-shaped with only ubuntu-latest) + one repository ruleset (PR-only, required check, no force-push/deletion, no bypass). Land CI first, enable the ruleset second, then re-trigger any pre-existing PRs.

**Tech Stack:** GitHub Actions (actions/checkout, actions/setup-dotnet), gh CLI (rulesets REST API), .NET 10 SDK.

**Spec:** `docs/superpowers/specs/2026-07-04-ci-quality-gate-design.md`

**Status: executed 2026-07-04.** All tasks complete. Deviations from the original plan discovered during execution:
- `main` had no code before PR #1 merged (the whole solution lived on the M1 branch), so PR #1 was merged first and this plan's branch rebased onto it; Task 5's "re-trigger PR #1" became moot.
- With a matrix, GitHub appends the matrix value to the check name even when the job `name` is explicit — the required-check context is `build-test (ubuntu-latest)`, not `build-test`.

## Global Constraints

- Every new shell must first run: `export PATH="/c/Program Files/dotnet:/c/Program Files/GitHub CLI:$PATH" HTTPS_PROXY="http://192.168.1.192:10090" HTTP_PROXY="http://192.168.1.192:10090"` (Git Bash; both gh and git push need the proxy)
- Local dotnet output is localized Chinese (the success line is `已通过!`); pipes mask exit codes — judge success by the actual result line, not the exit code after `| tail`
- Working directory: `D:/0x8A63F77D/Documents/GitHub/Lattice`; branch `ci-quality-gate` (cut from `origin/main`, already carrying the spec commit)
- The job display name is fixed to `build-test` (the required check matches on name); widening the matrix in M2 requires renaming and updating the ruleset in the same change — a known future action
- Conventional commit messages; never merge the temporary verification branches into anything

---

### Task 1: CI workflow file + CI PR

**Files:**
- Create: `.github/workflows/ci.yml`

**Interfaces:**
- Produces: GitHub check context `build-test (ubuntu-latest)` (referenced by Task 3's ruleset; observed by Tasks 4/5)

- [x] **Step 1: Write the workflow file**

`.github/workflows/ci.yml` (note the job `name` is a fixed string with no matrix interpolation):

```yaml
name: CI

on:
  pull_request:
    branches: [main]
  push:
    branches: [main]

concurrency:
  group: ci-${{ github.ref }}
  cancel-in-progress: true

jobs:
  build-test:
    name: build-test
    strategy:
      matrix:
        os: [ubuntu-latest]
    runs-on: ${{ matrix.os }}
    steps:
      - uses: actions/checkout@v5
      - uses: actions/setup-dotnet@v4
        with:
          dotnet-version: 10.0.x
      - run: dotnet build Lattice.sln -c Release -warnaserror
      - run: dotnet test Lattice.sln -c Release --no-build
```

- [x] **Step 2: Run the same commands locally to confirm the current code is green under this configuration**

```bash
cd "D:/0x8A63F77D/Documents/GitHub/Lattice"
export PATH="/c/Program Files/dotnet:$PATH"
dotnet build Lattice.sln -c Release -warnaserror
dotnet test Lattice.sln -c Release --no-build
```

Expected: build with zero warnings/errors; test line shows `已通过! - 失败: 0`.

- [x] **Step 3: Commit and push**

```bash
git add .github/workflows/ci.yml
git commit -m "ci: add build-test workflow (Release, warnings as errors)"
git push -u origin ci-quality-gate
```

- [x] **Step 4: Open the PR and wait for CI**

```bash
gh pr create --base main --head ci-quality-gate \
  --title "ci: add CI workflow and quality gate spec" \
  --body "Adds .github/workflows/ci.yml (Release build with -warnaserror + full test suite on ubuntu-latest) per docs/superpowers/specs/2026-07-04-ci-quality-gate-design.md. Ruleset for main will be enabled after this merges."
gh pr checks --watch
```

Expected: the `build-test (ubuntu-latest)` check appears and passes. On failure, read logs (`gh run view --log-failed`), fix, re-push until green.

- [x] **Step 5: No extra commit needed** (this task's deliverable is the PR with its green check)

### Task 2: Merge the CI PR

**Files:** none (pure GitHub operation)

**Interfaces:**
- Consumes: Task 1's PR (check green)
- Produces: `ci.yml` on `main` (Task 3's ruleset depends on the check existing on `main`)

- [x] **Step 1: Merge once the check is green (rebase keeps the docs and ci commits separate)**

```bash
gh pr merge <pr-number> --rebase --delete-branch
git checkout main && git pull
git log --oneline -3
```

Expected: `ci:` and `docs:` commits on top of `main`; the local branch deleted by gh.

**Process note:** wait for Codex Review's result before merging, not just CI — see the wait-for-codex-review lesson from this rollout.

- [x] **Step 2: Confirm the push-triggered CI run on main is green**

```bash
gh run list --branch main --limit 1
```

Expected: latest run `completed success` (may take ~2-3 minutes; follow with `gh run watch <id>`).

### Task 3: Enable the ruleset on main

**Files:** none (GitHub configuration via REST API)

**Interfaces:**
- Consumes: check context `build-test (ubuntu-latest)` (defined by Task 1)
- Produces: the merge gate on `main` (verified by Tasks 4/5)

- [x] **Step 1: Create the ruleset**

```bash
gh api repos/0x8A63F77D/Lattice/rulesets --method POST --input - <<'EOF'
{
  "name": "protect-main",
  "target": "branch",
  "enforcement": "active",
  "conditions": { "ref_name": { "include": ["~DEFAULT_BRANCH"], "exclude": [] } },
  "rules": [
    { "type": "deletion" },
    { "type": "non_fast_forward" },
    { "type": "pull_request", "parameters": {
        "required_approving_review_count": 0,
        "dismiss_stale_reviews_on_push": false,
        "require_code_owner_review": false,
        "require_last_push_approval": false,
        "required_review_thread_resolution": false,
        "allowed_merge_methods": ["merge", "squash", "rebase"]
    } },
    { "type": "required_status_checks", "parameters": {
        "strict_required_status_checks_policy": false,
        "do_not_enforce_on_create": false,
        "required_status_checks": [ { "context": "build-test (ubuntu-latest)" } ]
    } }
  ],
  "bypass_actors": []
}
EOF
```

Expected: JSON response with `"enforcement": "active"` and a ruleset `id`. If the API rejects a parameter (the rulesets schema evolves), adjust field names against the official docs; the target configuration is fixed: PR-only + required check + no deletion + no force push + no bypass.

- [x] **Step 2: Verify direct pushes are rejected**

```bash
git checkout main
git commit --allow-empty -m "test: should be rejected"
git push 2>&1 | tail -5
git reset --hard origin/main
```

Expected: push refused with a ruleset/pull-request-required message (GH013 or similar). **Confirm the rejection before resetting**; if the push somehow lands, the ruleset is not active — push a revert immediately and revisit Step 1.

### Task 4: Verify the gate blocks red PRs

**Files:**
- Create (temporary, deleted after verification): breaking changes on branch `tmp/gate-check`

**Interfaces:**
- Consumes: Task 3's ruleset + Task 1's workflow

- [x] **Step 1: Push a branch with a compiler warning**

```bash
git checkout -b tmp/gate-check origin/main
cat >> src/Lattice.Boinc.GuiRpc/XmlSanitizer.cs <<'EOF'

internal static class GateCheckWarning
{
    private static int _unused; // CS0169: intentionally unused to trip -warnaserror
}
EOF
git add -A && git commit -m "test: intentional warning (gate check, do not merge)"
git push -u origin tmp/gate-check
gh pr create --base main --head tmp/gate-check --title "test: gate check (do not merge)" --body "Verifies -warnaserror blocks merge. Will be closed."
gh pr checks --watch
```

Expected: `build-test (ubuntu-latest)` **fails** (CS0169 escalated to an error by `-warnaserror`).

- [x] **Step 2: Confirm the merge button is disabled**

```bash
gh pr view --json mergeStateStatus --jq .mergeStateStatus
```

Expected: `BLOCKED`.

- [x] **Step 3: Verify again with a failing test**

```bash
git checkout tmp/gate-check
git reset --hard origin/main
cat > tests/Lattice.Tests/GateCheckTests.cs <<'EOF'
using Xunit;

namespace Lattice.Tests;

public class GateCheckTests
{
    [Fact]
    public void Intentionally_fails_to_verify_the_gate() => Assert.True(false);
}
EOF
git add -A && git commit -m "test: intentional failing test (gate check, do not merge)"
git push --force-with-lease
gh pr checks --watch
```

Expected: `build-test (ubuntu-latest)` fails (build passes, tests fail); `mergeStateStatus` still `BLOCKED`.

- [x] **Step 4: Clean up**

```bash
gh pr close tmp/gate-check --delete-branch
git checkout main
git branch -D tmp/gate-check 2>/dev/null || true
```

Expected: temporary PR closed, remote and local temporary branches deleted.

### Task 5: Pre-existing PRs pass the new gate

**Files:** none

**Interfaces:**
- Consumes: Task 3's ruleset

- [x] **Step 1: Re-trigger CI on any PR opened before the workflow existed**

Became moot during execution: PR #1 merged before the workflow landed (see Status note above). For future reference, an empty commit triggers `pull_request: synchronize`:

```bash
git checkout <pr-branch>
git commit --allow-empty -m "ci: trigger checks under new quality gate"
git push
gh pr checks <pr-number> --watch
```

- [x] **Step 2: Confirm mergeability**

```bash
gh pr view <pr-number> --json mergeStateStatus --jq .mergeStateStatus
```

Expected: `CLEAN` (or another mergeable state).

- [x] **Step 3: Update project-status memory**

Record in `lattice-project-status.md`: CI + ruleset active (date, check context `build-test (ubuntu-latest)`, and the M2 matrix-expansion caveat).
