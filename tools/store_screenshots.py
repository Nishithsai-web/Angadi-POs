#!/usr/bin/env python3
"""
Google Play phone screenshots, taken from the real app on the CI emulator (e2e job, debug build, after the E2E test).

Starts the app from scratch (data cleared) with a believable shop, then captures the key screens in English and in
Telugu: bill with items, UPI QR, receipt (bill detail), customer account with dues, reports, stock, and the language
picker. Clean frames: clock fixed at 10:00, full battery, no notification icons, keyboard closed, toasts gone.

Size: the emulator screen is set to 1080x1920 (9:16) while capturing. Play rejects phone screenshots whose long side is
more than twice the short side, so the emulator's native 1080x2400 (2.22:1) cannot be uploaded as-is.

Output: e2e-out/store-screenshots/{en,te}/NN-name.png (+ _ui/ dumps for debugging). The e2e job's publish step pushes
them to the ci-e2e branch. Never fails the job: a screen that could not be captured is reported as a ::warning::.
Usage (emulator already running, app already installed by tools/e2e_android.py): python3 tools/store_screenshots.py
"""
import glob
import json
import os
import re
import sys
import time

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
import e2e_android as e2e  # noqa: E402  (adb + uiautomator helpers; owned by the E2E test, used read-only here)

PKG = e2e.PKG
ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
OUT = os.path.join(e2e.OUT, "store-screenshots")
WIDTH, HEIGHT, DENSITY = 1080, 1920, 360
SHOP = {"name": "Sri Lakshmi Kirana", "address": "Main Road, Guntur", "phone": "9000012345",
        "upi": "srilakshmi@okaxis", "gstin": "37ABCDE1234F1Z5"}
CUSTOMER = ("Ravi Kumar", "9876543210")
CAPTURED, MISSED = [], []


def load_strings():
    """The app's own UI text, so the Telugu run taps the same labels the app shows."""
    out = {}
    for lang in ("en", "te"):
        d = {}
        for f in sorted(glob.glob(os.path.join(ROOT, "src", "Angadi.Application", "Resources", f"strings*.{lang}.json"))):
            with open(f, encoding="utf-8") as fh:
                d.update(json.load(fh))
        out[lang] = d
    return out


STRINGS = load_strings()
LANG = ["en"]


def t(key):
    return STRINGS[LANG[0]][key]


def adb(*args, **kw):
    return e2e.adb(*args, check=False, **kw)


def keyboard_shown():
    return "mInputShown=true" in adb("shell", "dumpsys", "input_method")


def clean_frame():
    """No keyboard, no toast, no system dialog, then let the last animation settle."""
    for _ in range(3):
        if not keyboard_shown():
            break
        e2e.hide_keyboard()
    e2e.dismiss_system_popups()
    demo_status_bar()   # again: SystemUI drops demo mode when it re-lays out after the screen-size change
    time.sleep(3.5)   # a toast lasts at most 3.5 s


def save(name):
    clean_frame()
    folder = os.path.join(OUT, LANG[0])
    os.makedirs(os.path.join(OUT, "_ui"), exist_ok=True)
    os.makedirs(folder, exist_ok=True)
    png = e2e.subprocess.run(["adb", "exec-out", "screencap", "-p"], capture_output=True, timeout=60).stdout
    path = os.path.join(folder, name + ".png")
    with open(path, "wb") as f:
        f.write(png)
    with open(os.path.join(OUT, "_ui", f"{LANG[0]}-{name}.xml"), "w", encoding="utf-8") as f:
        f.write(e2e.dump())
    CAPTURED.append(f"{LANG[0]}/{name}")
    print(f"    saved {LANG[0]}/{name}.png", flush=True)


def shoot(name, fn):
    print(f"--- {LANG[0]} {name}", flush=True)
    try:
        fn()
        save(name)
    except Exception as ex:  # noqa: BLE001 — one missing screen must not cost the others
        MISSED.append(f"{LANG[0]}/{name}")
        print(f"::warning::store screenshot {LANG[0]}/{name} not captured: {str(ex)[:600]}".replace("\n", " "), flush=True)
        try:
            os.makedirs(os.path.join(OUT, "_ui"), exist_ok=True)
            with open(os.path.join(OUT, "_ui", f"{LANG[0]}-{name}-FAILED.png"), "wb") as f:
                f.write(e2e.subprocess.run(["adb", "exec-out", "screencap", "-p"], capture_output=True, timeout=60).stdout)
        except Exception:  # noqa: BLE001
            pass
        recover()


def on_tab_bar():
    return e2e.find(t("nav.items"), exact=True) is not None and e2e.find(t("nav.customers"), exact=True) is not None


def recover():
    """Back out of dialogs to a screen with the tab bar, in the current language. e2e_android.recover() only knows the
    English tab names, and in Telugu it would press Back until the app closed."""
    for _ in range(5):
        # A pushed page (receipt, bill detail, customer) keeps the tab bar but shows "Navigate up": pop it, since
        # tapping the current tab does not.
        if on_tab_bar() and e2e.find("Navigate up", exact=True) is None:
            return
        adb("shell", "input", "keyevent", "4")
        time.sleep(1.2)
    focus = [l for l in adb("shell", "dumpsys", "window").splitlines() if "mCurrentFocus" in l]
    if not any(PKG in l for l in focus):
        adb("shell", "monkey", "-p", PKG, "-c", "android.intent.category.LAUNCHER", "1")
        time.sleep(5)


# ------------------------------------------------------------------ device setup

def demo_status_bar():
    adb("shell", "settings", "put", "global", "sysui_demo_allowed", "1")

    def demo(command, *extras):
        adb("shell", "am", "broadcast", "-a", "com.android.systemui.demo", "-e", "command", command, *extras)

    demo("enter")
    demo("clock", "-e", "hhmm", "1000")
    demo("battery", "-e", "level", "100", "-e", "plugged", "false")
    demo("network", "-e", "wifi", "show", "-e", "level", "4")
    demo("network", "-e", "mobile", "show", "-e", "datatype", "none", "-e", "level", "4")
    demo("notifications", "-e", "visible", "false")


def fresh_app():
    adb("shell", "am", "force-stop", PKG)
    adb("shell", "pm", "clear", PKG)
    adb("shell", "wm", "size", f"{WIDTH}x{HEIGHT}")
    adb("shell", "wm", "density", str(DENSITY))   # 480 x 853 dp: a common phone, so the bill keeps room for its lines
    time.sleep(8)       # SystemUI re-lays out after the size change; demo mode set before that is lost
    demo_status_bar()
    adb("shell", "monkey", "-p", PKG, "-c", "android.intent.category.LAUNCHER", "1")
    time.sleep(8)
    e2e.dismiss_system_popups()


def restore_device():
    adb("shell", "wm", "size", "reset")
    adb("shell", "wm", "density", "reset")
    adb("shell", "am", "broadcast", "-a", "com.android.systemui.demo", "-e", "command", "exit")


# ------------------------------------------------------------------ the shop's data

def language_picker():
    # The first-run action sheet (both scripts), not the top bar's "English" switch behind it.
    e2e.wait_for("Choose language", timeout=120)


def setup_shop():
    e2e.tap("English", exact=True, timeout=30)
    e2e.wait_for("Welcome to Angadi", timeout=60)
    for index, value in ((0, SHOP["name"]), (1, SHOP["address"]), (2, SHOP["phone"]), (3, SHOP["upi"]), (4, SHOP["gstin"])):
        e2e.type_into(index, value)
        e2e.hide_keyboard()
    e2e.tap("Start", exact=True, scroll=True, timeout=60)
    e2e.wait_for(t("tender.cash"), exact=True, timeout=90)


def ensure_bill_screen():
    recover()
    e2e.goto_tab(t("nav.pos"))
    e2e.wait_for(t("tender.cash"), exact=True, timeout=30)


def new_bill():
    ensure_bill_screen()
    e2e.tap(t("pos.newbill"), exact=True, scroll=True, timeout=30)
    time.sleep(1)
    if e2e.find(t("common.yes"), exact=True) is not None:
        e2e.tap(t("common.yes"), exact=True)
    e2e.wait_for(t("tender.cash"), exact=True, timeout=30)


def bounds(node):
    m = re.match(r"\[(\d+),(\d+)\]\[(\d+),(\d+)\]", node.get("bounds", ""))
    return tuple(map(int, m.groups())) if m else (0, 0, 0, 0)


def price_tiles(below_quick_caption):
    """Visible item tiles (their '₹.. / unit' caption), in reading order. Quick-sale tiles sit below the 'Quick sale'
    caption; search results sit above it. Works in both languages."""
    all_nodes = e2e.nodes()
    caption = next((n for n in all_nodes if (n.get("text") or "") == t("pos.quicksale")), None)
    cy = bounds(caption)[1] if caption is not None else 0
    tiles = []
    for n in all_nodes:
        text = n.get("text") or ""
        x1, y1, x2, y2 = bounds(n)
        if "₹" in text and "/" in text and y2 > y1 + 10 and ((y1 > cy) == below_quick_caption):
            tiles.append((y1, x1, n))
    return [n for _, _, n in sorted(tiles, key=lambda v: (v[0], v[1]))]


def tap_node(node):
    x, y = e2e.center(node)
    adb("shell", "input", "tap", str(x), str(y))
    time.sleep(1.2)


def add_quick(index):
    tiles = price_tiles(below_quick_caption=True)
    if len(tiles) <= index:
        raise AssertionError(f"quick-sale tile {index} not on screen; {len(tiles)} visible")
    tap_node(tiles[index])


def add_searched(term):
    e2e.type_into(0, term)                      # "Search items" is the first field on the bill screen
    time.sleep(2)
    results = price_tiles(below_quick_caption=False)
    if not results:
        raise AssertionError(f"no search result for '{term}'")
    tap_node(results[0])
    e2e.hide_keyboard()


def udhaar_for_customer():
    """A credit bill for a named customer, so the customer screen has dues to show."""
    new_bill()
    add_searched("Sugar")
    add_searched("Toor")
    add_quick(0)
    e2e.tap(t("pos.customer.add"), timeout=30)
    e2e.type_in_dialog(CUSTOMER[0])
    e2e.tap(t("common.ok"), exact=True, timeout=20)
    e2e.tap(t("customers.add"), timeout=30)
    e2e.type_in_dialog(CUSTOMER[1])
    e2e.tap(t("common.ok"), exact=True, timeout=20)
    e2e.wait_for(CUSTOMER[0], timeout=30)
    e2e.tap(t("tender.udhaar"), exact=True)
    e2e.wait_for(t("receipt.title"), timeout=60)


def cash_bill(*terms):
    new_bill()
    for term in terms:
        add_searched(term)
    e2e.tap(t("tender.cash"), exact=True)
    e2e.tap(t("tender.pay"), exact=True, timeout=20)
    e2e.wait_for(t("receipt.title"), timeout=60)


# ------------------------------------------------------------------ the screens

def bill_with_items():
    new_bill()
    for term in ("Rice", "Milk"):
        try:
            add_searched(term)                   # typed in English; the Telugu UI may not match it, tiles still do
        except AssertionError as ex:
            print(f"    {ex}", flush=True)
            e2e.hide_keyboard()
    add_quick(0)
    add_quick(1)
    add_quick(2)
    e2e.hide_keyboard()


def upi_qr():
    e2e.tap(t("tender.upi"), exact=True)
    e2e.wait_for(t("pos.upi.paid"), timeout=30)
    time.sleep(1.5)                              # the QR bitmap is drawn after the page appears


def receipt():
    """Finish the UPI payment, then open the bill: items, GST split, total, share."""
    e2e.tap(t("pos.upi.paid"), timeout=30)
    e2e.type_in_dialog("424242424242")
    e2e.tap(t("common.ok"), exact=True, timeout=20)
    e2e.wait_for(t("receipt.title"), timeout=60)
    recover()
    e2e.goto_tab(t("nav.bills"))
    latest, number = None, -1
    deadline = time.time() + 30
    while latest is None and time.time() < deadline:
        for n in e2e.nodes():
            m = re.search(r"-(\d{6})", n.get("text") or "")
            if m and int(m.group(1)) > number:
                latest, number = n, int(m.group(1))
        if latest is None:
            time.sleep(1)
    if latest is None:
        raise AssertionError("no bill in the Bills list")
    tap_node(latest)
    e2e.wait_for(t("bills.reshare"), timeout=30, scroll=False)


def scroll_to_top(text, top=330):
    n = e2e.wait_for(text, timeout=30)
    y = bounds(n)[1]
    if y > top + 40:
        adb("shell", "input", "swipe", "540", str(y), "540", str(top), "900")   # slow: no fling
        time.sleep(1.5)


def customer_dues():
    recover()
    e2e.goto_tab(t("nav.customers"))
    e2e.tap(CUSTOMER[0], timeout=30)
    scroll_to_top(t("customers.balance"))


def reports():
    recover()
    e2e.goto_tab(t("nav.reports"))
    e2e.wait_for("₹", timeout=60)
    time.sleep(2)


def stock():
    recover()
    e2e.goto_tab(t("nav.items"))
    time.sleep(2)


def switch_to_telugu():
    ensure_bill_screen()
    e2e.tap("తెలుగు", exact=True, timeout=30)
    LANG[0] = "te"
    e2e.wait_for(t("tender.cash"), exact=True, timeout=60)


def screens():
    for name, fn in [
        ("01-bill", bill_with_items),
        ("02-upi-qr", upi_qr),
        ("03-receipt", receipt),
        ("04-customer-dues", customer_dues),
        ("05-reports", reports),
        ("06-stock", stock),
    ]:
        shoot(name, fn)


def main():
    os.makedirs(OUT, exist_ok=True)
    try:
        fresh_app()
        for lang in ("en", "te"):          # the picker is bilingual: the same frame serves both listings
            LANG[0] = lang
            shoot("07-language", language_picker)
        LANG[0] = "en"
        setup_shop()
        cash_bill("Rice", "Eggs")          # some sales for the reports
        udhaar_for_customer()
        screens()
        switch_to_telugu()
        screens()
    except Exception as ex:  # noqa: BLE001
        print(f"::warning::store screenshots stopped early: {str(ex)[:800]}".replace("\n", " "), flush=True)
    finally:
        restore_device()
    print(f"store screenshots: {len(CAPTURED)} captured, missed: {', '.join(MISSED) or 'none'}", flush=True)


if __name__ == "__main__":
    main()
