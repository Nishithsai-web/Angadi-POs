"""v2 several shops on one phone (agent v2-shops). Run by tools/e2e_android.py after its own steps.

The E2E phone is on the free tier: Settings -> Shops lists the one shop, and "+ Add a shop" is refused with the
Pro message (one subscription covers all shops). Works on debug and release (no run-as)."""
import time

import e2e_android as e


def second_shop_needs_pro():
    e.goto_tab("Settings")
    e.tap("Shops", exact=True, timeout=30)
    e.wait_for("Your shops", timeout=30)
    e.wait_for("(open now)", timeout=20)              # the one shop, marked as the open one
    e.shot("shops-list")
    e.tap("+ Add a shop", exact=True, timeout=20)
    e.tap("Yes", exact=True, timeout=20)              # "Add a new shop?" confirmation (no owner PIN on the E2E phone)
    e.wait_for("A second shop needs Angadi Pro", timeout=30)
    e.wait_for("covers all your shops", timeout=5)
    e.shot("shops-needs-pro")
    e.tap("Cancel", exact=True, timeout=20)
    time.sleep(1)
    e.wait_for("Your shops", timeout=20)
    if e.find("New shop") is not None:
        raise AssertionError("a second shop was created on the free tier")
    e.adb("shell", "input", "keyevent", "4")          # back to Settings
    time.sleep(2)
    e.relaunch_if_backgrounded()
    e.goto_tab("Bill")                                # still on the same shop


STEPS = [
    ("shops-second-needs-pro", second_shop_needs_pro),
]
