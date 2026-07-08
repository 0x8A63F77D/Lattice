#!/usr/bin/env bash
# HostMonitor Promela double-check: safety (assertions) + one pan run per LTL property.
# CI entry point (see .github/workflows/ci.yml model-check job). Requires: spin, gcc.
set -euo pipefail
cd "$(dirname "$0")/../verification"

command -v spin >/dev/null || { echo "spin not found"; exit 2; }
command -v gcc  >/dev/null || { echo "gcc not found";  exit 2; }
echo "spin: $(spin -V)"
echo "gcc:  $(gcc --version | head -1)"

run_pan () {
  local desc="$1"; shift
  echo "=== $desc ==="
  ./pan "$@" | tee pan.out
  # pan reports violations via 'errors: N' — fail on any nonzero error count,
  # and on unreached-assertion noise stay quiet (exit code alone is not enough:
  # pan exits 0 even when errors are found).
  grep -q "errors: 0" pan.out || { echo "MODEL CHECK FAILED: $desc"; exit 1; }
  rm -f pan.out
}

# Generate + compile ONCE: every embedded `ltl NAME {}` block is compiled into
# pan and selected at RUN time with `./pan -N NAME`. (`spin -a -N x` would
# instead read a never-claim FILE named x — that is not what we want.)
spin -a HostMonitor.pml
gcc -O2 -o pan pan.c

# Safety: assertions + invalid end states (monitor/env are intentional end
# states — they carry end labels, so default end-state checking stays sound).
# -noclaim: keep the LTL claims out of the safety sweep entirely.
run_pan "safety (assertions, I1..I5)" -m100000 -noclaim

# Liveness: one exhaustive run per LTL property, weak fairness.
for P in L1 L2 L3; do
  run_pan "liveness $P (acceptance, weak fairness)" -a -f -m100000 -N "$P"
done

rm -f pan
echo "ALL MODEL CHECKS PASSED"
