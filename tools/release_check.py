#!/usr/bin/env python3
"""
Release-readiness check: run by .github/workflows/release.yml before the signed bundle is built.

Fails (with one clear line per problem) when something the Play listing or the shopkeeper depends on is not filled in:
  - docs/PRIVACY_POLICY.md still has a placeholder like [Developer name] or the "Owner action" note
  - Support:WhatsAppNumber in src/Angadi.Maui/Resources/Raw/appsettings.json is empty or not a phone number
  - the version is malformed (tag/input 1.2.3; csproj ApplicationDisplayVersion 1.2.3, ApplicationVersion an integer)
  - the subscription IDs / prices in appsettings differ from the ones created in Play (store/listing/IN_APP_PRODUCTS.md)
  - a store listing text (store/listing/{en,te}/*.txt) is missing, empty or over Play's length limit

Usage: python3 tools/release_check.py [--version 1.2.3] [--root .]
"""
import argparse
import json
import os
import re
import sys

SEMVER = re.compile(r"^(0|[1-9]\d*)\.(0|[1-9]\d*)\.(0|[1-9]\d*)$")
EXPECTED_BILLING = {
    "MonthlyProductId": "angadi_pro_monthly",
    "YearlyProductId": "angadi_pro_yearly",
    "MonthlyPriceRupees": 200,
    "YearlyPriceRupees": 1999,
}


def check_privacy(root, problems):
    path = os.path.join(root, "docs", "PRIVACY_POLICY.md")
    if not os.path.isfile(path):
        problems.append("docs/PRIVACY_POLICY.md is missing: Play needs a public privacy policy URL")
        return
    with open(path, encoding="utf-8") as f:
        lines = f.read().splitlines()
    for no, line in enumerate(lines, 1):
        # A placeholder is [something] that is not a Markdown link ([text](url)).
        for m in re.finditer(r"\[([^\]\n]*)\](?!\()", line):
            problems.append(f"docs/PRIVACY_POLICY.md:{no}: placeholder '[{m.group(1)}]' is not filled in")
        if "Owner action" in line:
            problems.append(f"docs/PRIVACY_POLICY.md:{no}: remove the 'Owner action' note once the fields are filled in")


def check_appsettings(root, problems):
    path = os.path.join(root, "src", "Angadi.Maui", "Resources", "Raw", "appsettings.json")
    try:
        with open(path, encoding="utf-8") as f:
            cfg = json.load(f)
    except (OSError, ValueError) as ex:
        problems.append(f"appsettings.json cannot be read: {ex}")
        return
    number = str(cfg.get("Support", {}).get("WhatsAppNumber", "")).strip()
    digits = re.sub(r"[\s+-]", "", number)
    if not number:
        problems.append("appsettings.json Support:WhatsAppNumber is empty: shopkeepers' feedback would have nowhere to go")
    elif not re.fullmatch(r"(91)?[6-9]\d{9}", digits):
        problems.append(f"appsettings.json Support:WhatsAppNumber '{number}' is not a 10-digit Indian mobile number")
    billing = cfg.get("Billing", {})
    for key, want in EXPECTED_BILLING.items():
        if billing.get(key) != want:
            problems.append(f"appsettings.json Billing:{key} is '{billing.get(key)}', Play has '{want}' "
                            "(store/listing/IN_APP_PRODUCTS.md)")


LISTING_LIMITS = {"title.txt": 30, "short_description.txt": 80, "full_description.txt": 4000}


def check_listing(root, problems):
    for lang in ("en", "te"):
        for name, limit in LISTING_LIMITS.items():
            rel = f"store/listing/{lang}/{name}"
            try:
                with open(os.path.join(root, rel), encoding="utf-8") as f:
                    text = f.read().strip()
            except OSError:
                problems.append(f"{rel} is missing")
                continue
            if not text:
                problems.append(f"{rel} is empty")
            elif len(text) > limit:
                problems.append(f"{rel} has {len(text)} characters; Play allows {limit}")


def check_versions(root, version, problems):
    if version is not None and not SEMVER.match(version):
        problems.append(f"release version '{version}' must look like 1.2.3 (tag v1.2.3)")
    path = os.path.join(root, "src", "Angadi.Maui", "Angadi.Maui.csproj")
    try:
        with open(path, encoding="utf-8") as f:
            csproj = f.read()
    except OSError as ex:
        problems.append(f"Angadi.Maui.csproj cannot be read: {ex}")
        return
    display = re.search(r"<ApplicationDisplayVersion>([^<]*)</ApplicationDisplayVersion>", csproj)
    code = re.search(r"<ApplicationVersion>([^<]*)</ApplicationVersion>", csproj)
    if not display or not SEMVER.match(display.group(1).strip()):
        problems.append(f"Angadi.Maui.csproj ApplicationDisplayVersion '{display.group(1) if display else ''}' must look like 1.2.3")
    if not code or not re.fullmatch(r"[1-9]\d{0,8}", code.group(1).strip()):
        problems.append(f"Angadi.Maui.csproj ApplicationVersion '{code.group(1) if code else ''}' must be a positive whole number")
    if not re.search(r"<ApplicationId>com\.angadi\.pos</ApplicationId>", csproj):
        problems.append("Angadi.Maui.csproj ApplicationId must stay com.angadi.pos (Play identifies the app by it)")


def main(argv=None):
    ap = argparse.ArgumentParser(description=__doc__.splitlines()[1])
    ap.add_argument("--version", help="the version being released, e.g. 1.2.3")
    ap.add_argument("--root", default=os.path.dirname(os.path.dirname(os.path.abspath(__file__))))
    args = ap.parse_args(argv)
    problems = []
    check_privacy(args.root, problems)
    check_appsettings(args.root, problems)
    check_versions(args.root, args.version, problems)
    check_listing(args.root, problems)
    if problems:
        for p in problems:
            print(f"::error::release check: {p}")
        print(f"\nNot ready to release: {len(problems)} problem(s). See docs/RELEASE.md section C.")
        return 1
    print("Release check passed: privacy policy filled in, support number set, versions, product IDs and listing text valid.")
    return 0


if __name__ == "__main__":
    sys.exit(main())
