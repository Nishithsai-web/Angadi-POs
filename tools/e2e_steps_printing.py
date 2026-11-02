"""v2 Bluetooth receipt printing (agent printing). The emulator has no Bluetooth printer, so these steps check what a
shop sees without one: the printer settings screen works, the paper choice is saved, and Print says clearly that no
printer is set up (instead of failing silently). Real printing is verified on hardware (docs/RUNBOOK.md)."""
import time

import e2e_android as e

NO_PRINTER = "No receipt printer set up yet"


def printer_settings_screen():
    """Settings -> Receipt printer: status, paper widths, auto-print, test page; the paper choice sticks."""
    e.goto_tab("Settings")
    e.tap("Receipt printer", exact=True, scroll=True, timeout=30)
    e.wait_for("No printer chosen yet", timeout=30)
    e.wait_for("58 mm (small roll)", timeout=10)
    e.wait_for("80 mm (big roll)", timeout=10)
    e.wait_for("Print after every bill", timeout=10)
    e.shot("printer-settings")
    e.tap("80 mm (big roll)", timeout=10)
    e.wait_for("☑  80 mm", timeout=15)                  # saved and shown as the chosen paper
    e.tap("Print test page", scroll=True, timeout=20)
    e.wait_for(NO_PRINTER, timeout=30)                  # a dialog, not a silent failure
    e.shot("printer-test-no-printer")
    e.tap("OK", exact=True, timeout=10)
    time.sleep(1)
    e.wait_for("No printer chosen yet", scroll=True, timeout=15)
    e.adb("shell", "input", "keyevent", "4")            # back to Settings
    time.sleep(2)


def print_receipt_without_printer_says_so():
    """Receipt screen: Print with no printer chosen tells the shopkeeper where to set one up; the bill stays done."""
    e.goto_tab("Bill")
    e.new_bill()
    e.add_item("Rice", "Rice (Sona")
    e.tap("Cash", exact=True)
    e.tap("Pay", exact=True, timeout=20)
    e.wait_for("Bill done", timeout=60)
    e.tap("Print receipt", exact=True, scroll=True, timeout=20)
    e.wait_for(NO_PRINTER, timeout=30)
    e.shot("print-no-printer")
    e.tap("OK", exact=True, timeout=10)
    time.sleep(1)
    e.wait_for("Bill done", timeout=15)
    e.tap("New bill", exact=True, scroll=True, timeout=20)
    e.wait_for("Cash", exact=True, timeout=30)


STEPS = [
    ("printer-settings-screen", printer_settings_screen),
    ("print-without-printer", print_receipt_without_printer_says_so),
]
