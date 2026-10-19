#!/usr/bin/env python3
"""
Turns build errors in a log into ONE GitHub annotation (all lines joined), so they can be read
through the check-runs API even when raw job logs are not downloadable.
Usage: python3 tools/ci_report.py build.log "label"
"""
import re
import sys

path, label = sys.argv[1], sys.argv[2] if len(sys.argv) > 2 else "build"
pattern = re.compile(r"(error|Error)\s+[A-Z]{2,6}\d{2,5}\s*:|: error |error MSB|error NU|error XA|error XC|error APT")
seen, lines = set(), []
with open(path, encoding="utf-8", errors="replace") as f:
    for raw in f:
        line = re.sub(r"/home/runner/work/[^/]+/[^/]+/", "", raw.strip())
        line = re.sub(r"\s*\[[^\]]*\.csproj[^\]]*\]$", "", line)   # drop trailing [project] suffix
        if pattern.search(line) and line not in seen:
            seen.add(line)
            lines.append(line)

if lines:
    body = f"{label}: {len(lines)} error line(s)%0A" + "%0A".join(l.replace("%", "%25").replace("\r", "") for l in lines[:150])
    print(f"::error title={label} errors::{body}")
    print(f"{len(lines)} distinct error lines", file=sys.stderr)
else:
    print(f"{label}: no error lines found", file=sys.stderr)
