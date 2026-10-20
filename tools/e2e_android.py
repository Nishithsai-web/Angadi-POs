#!/usr/bin/env python3
"""
End-to-end test of the real Angadi app on an Android emulator (CI job `e2e`).

Drives the UI the way a shopkeeper would — first run, shop setup, cash / UPI / udhaar bills, an unknown barcode
attached to an item, reports, the language switch — using only adb + uiautomator. After every step it checks
the app is still alive; at the end it reads the app's own log file and fails on any logged error.

Output: screenshots + a UI dump per step in e2e-out/, GitHub annotations for every failure.
Usage: python3 tools/e2e_android.py path/to/app.apk
"""
import os
import re
import subprocess
import sys
import time
import xml.etree.ElementTree as ET

PKG = "com.angadi.pos"
OUT = os.path.join(os.environ.get("GITHUB_WORKSPACE", "."), "e2e-out")
STEP = [0]
FAILURES = []


def adb(*args, check=True, capture=True, timeout=120):
    r = subprocess.run(["adb", *args], capture_output=capture, text=True, timeout=timeout)
    if check and r.returncode != 0:
        raise RuntimeError(f"adb {' '.join(args)} failed: {r.stderr.strip()}")
    return r.stdout if capture else ""


def shot(name):
    STEP[0] += 1
    base = os.path.join(OUT, f"{STEP[0]:02d}-{name}")
    with open(base + ".png", "wb") as f:
        f.write(subprocess.run(["adb", "exec-out", "screencap", "-p"], capture_output=True, timeout=60).stdout)
    xml = dump()
    if xml:
        with open(base + ".xml", "w", encoding="utf-8") as f:
            f.write(xml)


def dump():
    for _ in range(5):
        out = subprocess.run(["adb", "exec-out", "uiautomator", "dump", "/dev/tty"], capture_output=True, timeout=60).stdout
        text = out.decode("utf-8", "replace")
        i = text.find("<?xml")
        j = text.rfind("</hierarchy>")
        if i >= 0 and j > i:
            return text[i:j + len("</hierarchy>")]
        time.sleep(1)
    return ""


def nodes():
    xml = dump()
    if not xml:
        return []
    try:
        root = ET.fromstring(xml)
    except ET.ParseError:
        return []
    return list(root.iter("node"))


def center(node):
    m = re.match(r"\[(\d+),(\d+)\]\[(\d+),(\d+)\]", node.get("bounds", ""))
    x1, y1, x2, y2 = map(int, m.groups())
    return (x1 + x2) // 2, (y1 + y2) // 2


def label(node):
    return (node.get("text") or "") + "|" + (node.get("content-desc") or "")


def find(pattern, exact=False, cls=None):
    rx = re.compile(("^" + re.escape(pattern) + "$") if exact else re.escape(pattern), re.I)
    for n in nodes():
        if cls and n.get("class") != cls:
            continue
        for value in (n.get("text") or "", n.get("content-desc") or ""):
            if value and rx.search(value):
                return n
    return None


SYSTEM_POPUPS = ("isn't responding", "isn’t responding", "keeps stopping", "has stopped")


APP_FREEZES = []


def dismiss_system_popups():
    """Android's own 'X isn't responding' dialogs (common right after an emulator boots) cover the app: tap Wait.
    If the dialog is about Angadi itself, that is a real freeze: it is recorded and fails the run at the end."""
    for _ in range(3):
        texts = screen_texts()
        if not any(p in texts for p in SYSTEM_POPUPS):
            return
        if "Angadi" in texts or "అంగడి" in texts:
            APP_FREEZES.append(texts[:300])
            print(f"::error::the app showed a system 'not responding' dialog: {texts[:300]}", flush=True)
        for button in ("Wait", "Close app", "OK"):
            n = find(button, exact=True)
            if n is not None:
                x, y = center(n)
                adb("shell", "input", "tap", str(x), str(y))
                time.sleep(1.5)
                break
        else:
            return


def wait_for(pattern, timeout=40, exact=False, scroll=False, cls=None):
    end = time.time() + timeout
    while time.time() < end:
        dismiss_system_popups()
        n = find(pattern, exact=exact, cls=cls)
        if n is not None:
            return n
        if scroll:
            swipe_up()
        time.sleep(1)
        alive()
    raise AssertionError(f"'{pattern}' did not appear within {timeout}s; on screen: {screen_texts()}")


def tap(pattern, timeout=40, exact=False, scroll=False, cls=None):
    n = wait_for(pattern, timeout, exact, scroll, cls)
    x, y = center(n)
    adb("shell", "input", "tap", str(x), str(y))
    time.sleep(1.2)
    return n


def screen_texts():
    seen = []
    for n in nodes():
        for value in (n.get("text") or "", n.get("content-desc") or ""):
            v = value.strip()
            if v and v not in seen:
                seen.append(v)
    return " | ".join(seen)[:700]


def edit_texts():
    return [n for n in nodes() if n.get("class") == "android.widget.EditText"]


def type_into(index, value):
    fields = edit_texts()
    if len(fields) <= index:
        raise AssertionError(f"expected at least {index + 1} text fields, found {len(fields)}")
    x, y = center(fields[index])
    adb("shell", "input", "tap", str(x), str(y))
    time.sleep(0.6)
    adb("shell", "input", "text", value.replace(" ", "%s"))
    time.sleep(0.6)


def type_in_dialog(value):
    """The focused field of a prompt dialog (DisplayPromptAsync)."""
    field = None
    for _ in range(20):
        fields = edit_texts()
        if fields:
            field = fields[-1]
            break
        time.sleep(1)
    if field is None:
        raise AssertionError("no text field in the dialog")
    x, y = center(field)
    adb("shell", "input", "tap", str(x), str(y))
    time.sleep(0.5)
    if value:
        adb("shell", "input", "text", value.replace(" ", "%s"))
    time.sleep(0.5)


def hide_keyboard():
    shown = "mInputShown=true" in adb("shell", "dumpsys", "input_method", check=False)
    if shown:
        adb("shell", "input", "keyevent", "111")   # ESCAPE closes the IME without navigating back
        time.sleep(0.8)


def swipe_up():
    adb("shell", "input", "swipe", "540", "1500", "540", "600", "300")
    time.sleep(0.8)


def alive():
    if not adb("shell", "pidof", PKG, check=False).strip():
        crash = adb("logcat", "-d", "-b", "crash", check=False)
        raise AssertionError("the app process died\n" + crash[-3000:])


def step(name, fn):
    print(f"--- {name}", flush=True)
    try:
        fn()
        alive()
        shot(name)
        print(f"    ok", flush=True)
    except Exception as ex:  # noqa: BLE001 — every failure is reported, then the run continues where it can
        shot(name + "-FAILED")
        msg = f"E2E step '{name}' failed: {ex}".replace("\n", " ")[:900]
        print(f"::error::{msg}", flush=True)
        FAILURES.append(name)
        if not adb("shell", "pidof", PKG, check=False).strip():
            raise SystemExit(1)
        recover()


def on_tab_bar():
    return find("Items", exact=True) is not None and find("Customers", exact=True) is not None


def relaunch_if_backgrounded():
    if find("అంగడి", exact=True) is not None and find("Cash", exact=True) is None and not on_tab_bar():
        adb("shell", "monkey", "-p", PKG, "-c", "android.intent.category.LAUNCHER", "1")
        time.sleep(4)


def recover():
    """Back out of dialogs and pushed pages to the tab bar, so one failure doesn't fail every later step.
    Never presses Back on a tab root (that would leave the app)."""
    for _ in range(4):
        if on_tab_bar():
            return
        adb("shell", "input", "keyevent", "4")
        time.sleep(1.2)
    relaunch_if_backgrounded()


def goto_tab(name):
    """Phones show 5 tabs; the 6th (and on narrow screens more) sit under 'More'."""
    if find(name, exact=True) is None and find("More", exact=True) is not None:
        tap("More", exact=True, timeout=10)
    tap(name, exact=True, timeout=30)


# ------------------------------------------------------------------ the shopkeeper's morning

def first_run():
    tap("English", exact=True, timeout=120)          # language sheet, shown before anything else
    wait_for("Welcome to Angadi", timeout=60)


def shop_setup():
    type_into(0, "E2E Kirana")                         # shop name
    hide_keyboard()
    type_into(3, "e2e@okaxis")                         # UPI ID
    hide_keyboard()
    tap("Start", exact=True, scroll=True, timeout=60)
    wait_for("Cash", exact=True, timeout=90)           # billing screen after the shell restarts


def add_item(search, pick):
    type_into(0, search)                               # "Search items" is the first field on the bill screen
    tap(pick, timeout=30, cls="android.widget.TextView")   # the result row, not the search box
    hide_keyboard()


def new_bill():
    tap("New bill", exact=True, scroll=True, timeout=30)
    time.sleep(1)
    if find("Yes", exact=True) is not None:            # "drop the unfinished bill?" on the bill screen
        tap("Yes", exact=True)
    wait_for("Cash", exact=True, timeout=30)


def cash_bill():
    add_item("Rice", "Rice (Sona")
    tap("Cash", exact=True)
    tap("Pay", exact=True, timeout=20)                 # prompt: leave the amount empty = exact cash
    wait_for("Bill done", timeout=60)


def upi_bill():
    new_bill()
    add_item("Milk", "Milk (500")
    tap("UPI", exact=True)
    tap("Payment received", timeout=30)
    type_in_dialog("424242424242")                     # UTR / reference
    tap("OK", exact=True, timeout=20)
    wait_for("Bill done", timeout=60)


def udhaar_bill():
    new_bill()
    add_item("Sugar", "Sugar")
    tap("+ Customer", timeout=30)
    type_in_dialog("Ravi")
    tap("OK", exact=True, timeout=20)
    tap("New customer", timeout=30)
    type_in_dialog("9876543210")
    tap("OK", exact=True, timeout=20)
    wait_for("Ravi", timeout=30)
    tap("Udhaar", exact=True)
    wait_for("Bill done", timeout=60)


def scan_unknown_barcode():
    new_bill()
    tap("Scan")
    for allow in ("While using the app", "Only this time", "Allow"):
        n = find(allow)
        if n is not None:
            x, y = center(n)
            adb("shell", "input", "tap", str(x), str(y))
            time.sleep(1.5)
            break
    tap("Type it instead", timeout=40)
    type_in_dialog("8901725133979")
    tap("OK", exact=True, timeout=20)
    tap("Put this barcode on an existing item", timeout=30)
    type_in_dialog("Tea")
    tap("OK", exact=True, timeout=20)
    tap("Tea powder", timeout=30, cls="android.widget.TextView")
    wait_for("Tea powder", timeout=30)                 # now on the bill
    tap("Cash", exact=True)
    tap("Pay", exact=True, timeout=20)
    wait_for("Bill done", timeout=60)


def scan_known_barcode():
    new_bill()
    tap("Scan")
    tap("Type it instead", timeout=40)
    type_in_dialog("8901725133979")
    tap("OK", exact=True, timeout=20)
    wait_for("Tea powder", timeout=30)                 # the same packet is now found directly


def empty_bill_says_so():
    new_bill()
    tap("Cash", exact=True)
    time.sleep(1)
    if find("Bill done"):
        raise AssertionError("an empty bill was finalized")


def reports():
    goto_tab("Reports")
    wait_for("Stock matches the ledger", scroll=True, timeout=60)


def customer_account():
    goto_tab("Customers")
    tap("Ravi", timeout=30)
    wait_for("Previous bills", scroll=True, timeout=40)


def language_switch():
    goto_tab("Bill")
    tap("తెలుగు", exact=True, timeout=30)               # top bar: switch the whole app
    wait_for("నగదు", timeout=60)                        # "Cash" in Telugu
    tap("English", exact=True, timeout=30)
    wait_for("Cash", exact=True, timeout=60)


def app_never_froze():
    anr = adb("shell", "dumpsys", "activity", "anr", check=False) + adb("logcat", "-d", "-s", "ActivityManager:E", check=False)
    hits = [l for l in anr.splitlines() if "ANR in " + PKG in l]
    for line in hits[:10]:
        print(f"::error::{line[:500]}")
    if hits:
        raise AssertionError("Angadi was not responding (ANR) during the run")


def app_log_has_no_errors():
    errors_tagged = [l for l in adb("logcat", "-d", "-s", "Angadi:E", check=False).splitlines() if " E Angadi" in l or "E/Angadi" in l]
    for line in errors_tagged[:20]:
        print(f"::error::logcat: {line[:500]}")
    if errors_tagged:
        raise AssertionError(f"{len(errors_tagged)} unhandled error(s) in logcat")
    # One string: adb joins arguments with spaces, so a separate 'sh -c' would run a bare 'cat' that waits forever.
    log = adb("shell", f"run-as {PKG} sh -c 'cat files/logs/*.log'", check=False, timeout=60)
    if not log.strip():
        print("::warning::app log not readable (non-debuggable build?)")
        return
    with open(os.path.join(OUT, "app.log"), "w", encoding="utf-8") as f:
        f.write(log)
    errors = [l for l in log.splitlines() if "[ERR]" in l or "[FTL]" in l]
    for line in errors[:20]:
        print(f"::error::app log: {line[:500]}")
    if errors:
        raise AssertionError(f"{len(errors)} error(s) in the app log")


def main():
    apk = sys.argv[1]
    os.makedirs(OUT, exist_ok=True)
    adb("wait-for-device")
    for _ in range(60):   # the runner says "booted" before the launcher settles; give it time
        if adb("shell", "getprop", "sys.boot_completed", check=False).strip() == "1":
            break
        time.sleep(2)
    # The test emulator's own launcher often ANRs right after boot and its dialog covers the app. Hide system
    # error dialogs (Angadi's own freezes are still caught: the process check, logcat errors, our ANR watch).
    adb("shell", "settings", "put", "global", "hide_error_dialogs", "1", check=False)
    adb("shell", "am", "force-stop", "com.google.android.apps.nexuslauncher", check=False)
    time.sleep(20)
    dismiss_system_popups()
    adb("uninstall", PKG, check=False)
    adb("install", "-r", "-g", apk, timeout=600)   # -g: grant runtime permissions (camera, mic)
    adb("logcat", "-c", check=False)
    adb("shell", "monkey", "-p", PKG, "-c", "android.intent.category.LAUNCHER", "1")
    time.sleep(8)
    dismiss_system_popups()

    for name, fn in [
        ("first-run-language", first_run),
        ("shop-setup", shop_setup),
        ("cash-bill", cash_bill),
        ("upi-bill", upi_bill),
        ("udhaar-bill-new-customer", udhaar_bill),
        ("scan-unknown-attach-to-item", scan_unknown_barcode),
        ("scan-known-barcode", scan_known_barcode),
        ("empty-bill-is-refused", empty_bill_says_so),
        ("reports-inventory-ledger", reports),
        ("customer-account", customer_account),
        ("language-switch", language_switch),
        ("app-log-clean", app_log_has_no_errors),
        ("no-app-freezes", app_never_froze),
    ]:
        step(name, fn)

    with open(os.path.join(OUT, "logcat.txt"), "w", encoding="utf-8") as f:
        f.write(adb("logcat", "-d", check=False))
    if APP_FREEZES:
        FAILURES.append("app-not-responding")
    if FAILURES:
        print(f"::error::E2E failed steps: {', '.join(FAILURES)}")
        sys.exit(1)
    print("E2E: all steps passed")


if __name__ == "__main__":
    main()
