#!/usr/bin/env bash
# Prints compiler/test failures for a CI job from GitHub check annotations (raw logs are not reachable here).
# Usage: tools/ci_errors.sh <job-id>
gh api "repos/Nishithsai-web/Angadi-POs/check-runs/$1/annotations?per_page=100" \
  --jq '.[] | select(.annotation_level=="failure") | "\(.path):\(.start_line) \(.message)"' \
  | sed -E 's|/home/runner/work/[^/]+/[^/]+/||g' | grep -v '^\.github' | sort -u
