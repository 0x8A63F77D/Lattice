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
  gcc -O2 -o pan pan.c
  ./pan "$@" | tee pan.out
  # pan reports violations via 'errors: N' — fail on any nonzero error count,
  # and on unreached-assertion noise stay quiet (exit code alone is not enough:
  # pan exits 0 even when errors are found).
  grep -q "errors: 0" pan.out || { echo "MODEL CHECK FAILED: $desc"; exit 1; }
  rm -f pan pan.out
}

# Safety: assertions + invalid end states (monitor/env are intentional end
# states — they carry end labels, so default end-state checking stays sound).
spin -a HostMonitor.pml
run_pan "safety (assertions, I1..I5)" -m100000

# Liveness: one exhaustive run per LTL property, weak fairness.
for P in L1 L2 L3; do
  spin -a -N "$P" HostMonitor.pml
  run_pan "liveness $P (acceptance, weak fairness)" -a -f -m100000
done

echo "ALL MODEL CHECKS PASSED"
