#!/usr/bin/env python3
"""Emits ONE GitHub annotation listing failed tests (name + first lines of the message) from TRX files.

Also: skipped tests (outcome NotExecuted, e.g. [Fact(Skip = "BUG ...")]) are counted as skipped, not failed, and any
test output line containing "[ci-report] " is surfaced as a notice annotation (S4-5: measurements such as the
YearOfDataTests timing table are otherwise only in the TRX artifact, which the agent sandbox cannot download)."""
import glob
import re
import sys
import xml.etree.ElementTree as ET

root = sys.argv[1] if len(sys.argv) > 1 else "TestResults"
ns = {"t": "http://microsoft.com/schemas/VisualStudio/TeamTest/2010"}
REPORT = "[ci-report] "
failed, total, passed, skipped, reports = [], 0, 0, 0, []
for path in glob.glob(f"{root}/**/*.trx", recursive=True):
    tree = ET.parse(path)
    for r in tree.getroot().iterfind(".//t:UnitTestResult", ns):
        total += 1
        outcome = r.get("outcome")
        out = r.find(".//t:Output/t:StdOut", ns)
        if out is not None and out.text and REPORT in out.text:
            reports.append((r.get("testName"), [l.split(REPORT, 1)[1].rstrip() for l in out.text.splitlines() if REPORT in l]))
        if outcome == "Passed":
            passed += 1
            continue
        if outcome == "NotExecuted":
            skipped += 1
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

summary = f"tests: {passed}/{total} passed, {skipped} skipped, {len(failed)} failed"
for name, lines in reports:
    body = "%0A".join(l.replace("%", "%25") for l in lines)
    print(f"::notice title=report {name.rsplit('.', 1)[-1]}::{body}")
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
