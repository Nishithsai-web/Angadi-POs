#!/usr/bin/env bash
# Downloads the OFL-licensed Noto fonts the app and the receipt PDF need (P2, FR-17) and copies them to
# every place that uses them. Run once after cloning; CI runs it before building.
set -euo pipefail
cd "$(dirname "$0")/.."
base="https://raw.githubusercontent.com/notofonts/notofonts.github.io/main/fonts"
declare -A files=(
  [NotoSansTelugu-Regular.ttf]="$base/NotoSansTelugu/hinted/ttf/NotoSansTelugu-Regular.ttf"
  [NotoSansTelugu-Bold.ttf]="$base/NotoSansTelugu/hinted/ttf/NotoSansTelugu-Bold.ttf"
  [NotoSans-Regular.ttf]="$base/NotoSans/hinted/ttf/NotoSans-Regular.ttf"
  [NotoSans-Bold.ttf]="$base/NotoSans/hinted/ttf/NotoSans-Bold.ttf"
)
targets=(src/Angadi.Maui/Resources/Fonts src/Angadi.Maui/Resources/Raw/fonts tests/Angadi.Tests/Fonts)
# Fonts are committed to the repo (vendor-fonts workflow); only download when any copy is missing.
missing=0
for t in "${targets[@]}"; do for name in "${!files[@]}"; do [ -s "$t/$name" ] || missing=1; done; done
if [ "$missing" = 0 ]; then echo "fonts already present"; exit 0; fi
tmp=$(mktemp -d)
for name in "${!files[@]}"; do
  curl -fsSL --retry 3 -o "$tmp/$name" "${files[$name]}"
  # A real TTF starts with 0x00010000 or 'true'; reject HTML/JSON error pages.
  head -c 4 "$tmp/$name" | od -An -tx1 | grep -Eq "00 01 00 00|74 72 75 65" || { echo "bad font download: $name"; exit 1; }
done
for t in "${targets[@]}"; do mkdir -p "$t"; cp "$tmp"/*.ttf "$t"/; done
echo "fonts installed: ${!files[*]}"
