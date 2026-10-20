#!/usr/bin/env python3
"""Emits ONE GitHub annotation listing failed tests (name + first lines of the message) from TRX files."""
import glob
import re
import sys
import xml.etree.ElementTree as ET

root = sys.argv[1] if len(sys.argv) > 1 else "TestResults"
ns = {"t": "http://microsoft.com/schemas/VisualStudio/TeamTest/2010"}
failed, total, passed = [], 0, 0
for path in glob.glob(f"{root}/**/*.trx", recursive=True):
    tree = ET.parse(path)
    for r in tree.getroot().iterfind(".//t:UnitTestResult", ns):
        total += 1
        outcome = r.get("outcome")
        if outcome == "Passed":
            passed += 1
            continue
        msg = r.find(".//t:Message", ns)
        stack = r.find(".//t:StackTrace", ns)
        text = (msg.text or "") if msg is not None else outcome
        where = ""
        if stack is not None and stack.text:
            m = re.search(r"in .*?(tests/[^\s:]+:line \d+)", stack.text.replace("\\", "/"))
            where = f" @ {m.group(1)}" if m else ""
        text = " ".join(text.split())[:400]
        failed.append(f"{r.get('testName')}{where} :: {text}")

summary = f"tests: {passed}/{total} passed, {len(failed)} failed"
# No TRX results or no failures found but the step failed: surface the tail of the console log instead.
if total == 0 and len(sys.argv) > 2:
    try:
        tail = [l.rstrip() for l in open(sys.argv[2], encoding="utf-8", errors="replace")][-60:]
        keep = [l for l in tail if l.strip()]
        if total == 0 or any("crash" in l.lower() or "aborted" in l.lower() for l in keep):
            print("::error title=test log tail::" + "%0A".join(l.replace("%", "%25") for l in keep))
    except OSError:
        pass
print(summary, file=sys.stderr)
if failed:
    body = summary + "%0A" + "%0A".join(f.replace("%", "%25") for f in failed[:80])
    print(f"::error title=failed tests::{body}")
