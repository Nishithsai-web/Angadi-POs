"""Platform, data-safety and counter-UX checks (agent F2). Run by tools/e2e_android.py after its own steps."""
import time

import e2e_android as e


def upi_warns_and_survives_background():
    """A2-06: the fraud warning and the payee are on the UPI screen. The shopkeeper then switches to their UPI app to
    check the money (Home + relaunch here): coming back, 'Payment received' must still finish the bill."""
    e.goto_tab("Bill")
    e.new_bill()
    e.add_item("Milk", "Milk (500")
    e.tap("UPI", exact=True)
    e.wait_for("YOUR UPI app", timeout=30)
    e.wait_for("Paying to: e2e@okaxis", timeout=10)
    e.shot("upi-warning")
    e.adb("shell", "input", "keyevent", "3")           # Home: off to PhonePe / GPay to check
    time.sleep(4)
    e.adb("shell", "monkey", "-p", e.PKG, "-c", "android.intent.category.LAUNCHER", "1")
    time.sleep(4)
    e.tap("Payment received", timeout=30)
    e.type_in_dialog("434343434343")
    e.tap("OK", exact=True, timeout=20)
    e.wait_for("Bill done", timeout=60)


def scanner_back_returns_to_bill():
    """A2-08: system Back (predictive back on Android 16) closes the camera and leaves the bill screen usable."""
    # The previous step ends on the receipt page ("Bill done"), which is pushed on the Bill tab's stack — tapping
    # the Bill tab does not pop it. "New bill" is the shopkeeper's own way back to the counter.
    e.goto_tab("Bill")
    e.new_bill()
    e.tap("Scan")                                      # the button reads "▦  Scan"
    e.wait_for("Type it instead", timeout=40)
    e.adb("shell", "input", "keyevent", "4")
    time.sleep(2)
    e.wait_for("Cash", exact=True, timeout=30)
    if e.find("Type it instead") is not None:
        raise AssertionError("the scanner stayed open after Back")


def settings_shows_backup_status():
    """A2-03: Settings shows the data size and the last manual backup; a backup updates it."""
    e.goto_tab("Settings")
    e.wait_for("Data size:", scroll=True, timeout=30)
    e.wait_for("Last backup: never", timeout=10)
    e.tap("Back up data", exact=True, timeout=20)
    time.sleep(4)                                      # the share sheet opens; close it without sharing
    e.adb("shell", "input", "keyevent", "4")
    time.sleep(2)
    e.relaunch_if_backgrounded()
    e.wait_for("Data size:", scroll=True, timeout=30)
    if e.find("Last backup: never") is not None:
        raise AssertionError("the manual backup was not recorded")


STEPS = [
    ("upi-warning-and-background", upi_warns_and_survives_background),
    ("scanner-back", scanner_back_returns_to_bill),
    ("settings-backup-status", settings_shows_backup_status),
]
