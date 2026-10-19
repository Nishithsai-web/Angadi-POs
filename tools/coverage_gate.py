#!/usr/bin/env python3
"""
NFR-10 coverage gate for CI.

  * Angadi.Domain + Angadi.Application line coverage >= 70%
  * BillCalculator (money math) line coverage >= 95%

Usage: python3 tools/coverage_gate.py <TestResults dir>
Reads every coverage.cobertura.xml under the directory (coverlet output).
"""
import glob
import os
import sys
import xml.etree.ElementTree as ET

LAYER_MIN = 0.70
MONEY_MIN = 0.95
LAYERS = ("Angadi.Domain", "Angadi.Application")
MONEY_CLASS = "Angadi.Domain.Billing.BillCalculator"


def main(root: str) -> int:
    files = glob.glob(os.path.join(root, "**", "coverage.cobertura.xml"), recursive=True)
    if not files:
        print(f"coverage gate: no coverage.cobertura.xml under {root}")
        return 1

    # line hits keyed by (file, line) so multiple reports merge without double counting
    layer_lines: dict[tuple[str, int], bool] = {}
    money_lines: dict[tuple[str, int], bool] = {}
    per_package: dict[str, dict[tuple[str, int], bool]] = {}

    for path in files:
        tree = ET.parse(path)
        for package in tree.getroot().iter("package"):
            pname = package.get("name", "")
            for cls in package.iter("class"):
                cname = cls.get("name", "")
                fname = cls.get("filename", "")
                for line in cls.iter("line"):
                    key = (fname, int(line.get("number", "0")))
                    hit = int(line.get("hits", "0")) > 0
                    per_package.setdefault(pname, {})
                    per_package[pname][key] = per_package[pname].get(key, False) or hit
                    if pname in LAYERS:
                        layer_lines[key] = layer_lines.get(key, False) or hit
                    if cname == MONEY_CLASS or cname.startswith(MONEY_CLASS + "/"):
                        money_lines[key] = money_lines.get(key, False) or hit

    def rate(lines: dict) -> float:
        return sum(lines.values()) / len(lines) if lines else 0.0

    print("Line coverage by assembly:")
    for name in sorted(per_package):
        lines = per_package[name]
        print(f"  {name:28} {rate(lines):6.1%}  ({sum(lines.values())}/{len(lines)})")

    ok = True
    layer = rate(layer_lines)
    print(f"\nDomain + Application: {layer:.1%} (min {LAYER_MIN:.0%})")
    if layer < LAYER_MIN:
        ok = False
        print("  FAIL: below the NFR-10 threshold")

    if not money_lines:
        ok = False
        print(f"FAIL: {MONEY_CLASS} not found in coverage")
    else:
        money = rate(money_lines)
        print(f"BillCalculator:       {money:.1%} (min {MONEY_MIN:.0%})")
        if money < MONEY_MIN:
            ok = False
            print("  FAIL: money math must be >= 95% covered")

    return 0 if ok else 1


if __name__ == "__main__":
    sys.exit(main(sys.argv[1] if len(sys.argv) > 1 else "TestResults"))
