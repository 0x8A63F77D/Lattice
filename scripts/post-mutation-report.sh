#!/usr/bin/env bash
# Posts (or updates) the Tier 2 nightly mutation-score comment on the tracking
# issue (issue #77 design). Expects a completed Stryker run with Markdown and
# Json reporters enabled, and `gh` authenticated via GH_TOKEN.
#
# Usage: post-mutation-report.sh <issue-number> [reports-root]
set -euo pipefail

issue="$1"
reports_root="${2:-tests/Lattice.Tests/StrykerOutput}"
marker='<!-- lattice-mutation-tier2 -->'

md_report=$(find "$reports_root" -name mutation-report.md 2>/dev/null | sort | tail -1)
json_report=$(find "$reports_root" -name mutation-report.json 2>/dev/null | sort | tail -1)
if [ -z "$md_report" ] || [ -z "$json_report" ]; then
  echo "No Stryker reports found under $reports_root" >&2
  exit 1
fi

survivors=$(jq -r '
  .files | to_entries[] | (.key | sub(".*/src/"; "src/")) as $f
  | .value.mutants[] | select(.status == "Survived" or .status == "NoCoverage")
  | "- `\($f):\(.location.start.line)` \(.mutatorName) [\(.status)]"' "$json_report")

body=$(printf '%s\n## Tier 2 nightly mutation audit\n\nRun: %s (updated %s)\n\n%s\n\n### Surviving mutants\n\n%s\n' \
  "$marker" \
  "${GITHUB_SERVER_URL:-https://github.com}/${GITHUB_REPOSITORY}/actions/runs/${GITHUB_RUN_ID:-local}" \
  "$(date -u +%Y-%m-%dT%H:%M:%SZ)" \
  "$(cat "$md_report")" \
  "${survivors:-_none_}")

comment_id=$(gh api "repos/${GITHUB_REPOSITORY}/issues/${issue}/comments" --paginate \
  --jq ".[] | select(.body | startswith(\"$marker\")) | .id" | head -1)

if [ -n "$comment_id" ]; then
  gh api -X PATCH "repos/${GITHUB_REPOSITORY}/issues/comments/${comment_id}" -f body="$body" > /dev/null
  echo "Updated comment $comment_id on issue #$issue"
else
  gh api -X POST "repos/${GITHUB_REPOSITORY}/issues/${issue}/comments" -f body="$body" > /dev/null
  echo "Created new comment on issue #$issue"
fi
