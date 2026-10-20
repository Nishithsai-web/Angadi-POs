#!/usr/bin/env bash
# Waits for the CI run of the current HEAD, then prints each job's failed steps and error annotations.
sha=${SHA:-$(git rev-parse --short=7 HEAD)}
for i in $(seq 1 150); do
  r=$(gh api "repos/Nishithsai-web/Angadi-POs/actions/runs?branch=$(git rev-parse --abbrev-ref HEAD)&per_page=5" --jq ".workflow_runs[] | select(.head_sha[:7]==\"$sha\") | \"\(.id) \(.status) \(.conclusion)\"" | head -1)
  case "$r" in *completed*) break;; esac; sleep 20
done
echo "run: $r"; id=${r%% *}
for j in $(gh api repos/Nishithsai-web/Angadi-POs/actions/runs/$id/jobs --jq '.jobs[].id'); do
  gh api repos/Nishithsai-web/Angadi-POs/actions/jobs/$j --jq '"== \(.name): \(.conclusion) " + ([.steps[] | select(.conclusion=="failure") | .name] | join(","))'
  gh api "repos/Nishithsai-web/Angadi-POs/check-runs/$j/annotations?per_page=100" --jq '.[] | select(.annotation_level=="failure" and (.message|startswith("Process completed")|not)) | .message' \
    | sed -E 's|/home/runner/work/[^/]+/[^/]+/||g; s/ \[\/home[^]]*\]//g' | awk '!seen[$0]++' | head -${1:-80}
done
