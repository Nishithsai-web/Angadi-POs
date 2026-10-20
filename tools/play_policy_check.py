#!/usr/bin/env python3
"""
Google Play policy gate for a built APK (CI). Fails the build when the app would be rejected or would need extra
Play Console declarations nobody planned for:

- targetSdk below what Play requires for new apps and updates (API 36 from 31 Aug 2026);
- a release build marked debuggable;
- a permission pulled in by a library that Play treats as sensitive (each needs a policy declaration or changes the
  Data safety form): advertising ID, all-package visibility, SMS/call log, location, broad storage, installs.

Usage: python3 tools/play_policy_check.py <apk> [--release]
"""
import glob
import os
import re
import subprocess
import sys

MIN_TARGET_SDK = 36
SENSITIVE = {
    "com.google.android.gms.permission.AD_ID": "advertising ID: Data safety must declare it; Angadi shows no ads",
    "android.permission.QUERY_ALL_PACKAGES": "needs a Play declaration; Angadi lists the apps it opens in <queries>",
    "android.permission.READ_SMS": "SMS permissions are restricted to default SMS apps",
    "android.permission.SEND_SMS": "SMS permissions are restricted to default SMS apps",
    "android.permission.READ_CALL_LOG": "call log permissions are restricted",
    "android.permission.ACCESS_FINE_LOCATION": "location is not used",
    "android.permission.ACCESS_COARSE_LOCATION": "location is not used",
    "android.permission.ACCESS_BACKGROUND_LOCATION": "location is not used",
    "android.permission.MANAGE_EXTERNAL_STORAGE": "all-files access needs a Play declaration",
    "android.permission.REQUEST_INSTALL_PACKAGES": "installing packages needs a Play declaration",
    "android.permission.READ_MEDIA_IMAGES": "photo access needs a Play declaration; the QR upload uses the system picker",
    "android.permission.READ_MEDIA_VIDEO": "video access needs a Play declaration",
}


def aapt2():
    root = os.environ.get("ANDROID_SDK_ROOT") or os.environ.get("ANDROID_HOME") or ""
    tools = sorted(glob.glob(os.path.join(root, "build-tools", "*", "aapt2")))
    if not tools:
        sys.exit("::error::aapt2 not found under $ANDROID_SDK_ROOT/build-tools")
    return tools[-1]


def main():
    apk = sys.argv[1]
    release = "--release" in sys.argv
    out = subprocess.run([aapt2(), "dump", "badging", apk], capture_output=True, text=True, check=True).stdout
    problems = []

    target = re.search(r"targetSdkVersion:'(\d+)'", out)
    minsdk = re.search(r"(?:minSdkVersion|sdkVersion):'(\d+)'", out)
    print(f"targetSdk={target.group(1) if target else '?'} minSdk={minsdk.group(1) if minsdk else '?'}")
    if not target or int(target.group(1)) < MIN_TARGET_SDK:
        problems.append(f"targetSdk must be >= {MIN_TARGET_SDK} for Google Play (found {target.group(1) if target else 'none'})")

    debuggable = "application-debuggable" in out
    print(f"debuggable={debuggable}")
    if release and debuggable:
        problems.append("the release build is debuggable: Play rejects it")

    permissions = sorted(set(re.findall(r"uses-permission: name='([^']+)'", out)))
    print("permissions:\n  " + "\n  ".join(permissions))
    for p in permissions:
        if p in SENSITIVE:
            problems.append(f"sensitive permission {p}: {SENSITIVE[p]}")

    for p in problems:
        print(f"::error::Play policy: {p}")
    sys.exit(1 if problems else 0)


if __name__ == "__main__":
    main()
