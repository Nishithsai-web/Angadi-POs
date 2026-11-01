"""v2-stock emulator steps: add a supplier, switch an item to batch tracking, enter a supplier bill with a batch,
sell the item, open the Expiring list and the purchases & stock report. Runs after the built-in steps (English UI,
seeded kirana catalogue). No run-as: passes on debug and release builds."""
import time

import e2e_android as e

SUPPLIER = "E2E Pharma"
HUB = "Purchases & stock"


def back():
    e.adb("shell", "input", "keyevent", "4")
    time.sleep(1.2)


def items_root():
    """The Items tab's own page (its '+ New item' button), whatever stock page was open before."""
    e.recover()
    e.goto_tab("Items")
    for _ in range(5):
        if e.find("+ New item", exact=True) is not None:
            return
        back()
    e.wait_for("+ New item", exact=True, timeout=20)


def open_hub():
    items_root()
    e.tap(HUB, exact=True, timeout=30)
    e.wait_for("New purchase", exact=True, timeout=30)


def ok_dialog(value=""):
    e.type_in_dialog(value)
    e.tap("OK", exact=True, timeout=20)


def add_supplier():
    open_hub()
    e.tap("Suppliers", exact=True, timeout=20)
    e.tap("Add supplier", exact=True, timeout=20)
    ok_dialog(SUPPLIER)                                       # name
    ok_dialog("9876500000")                                   # phone
    ok_dialog()                                               # GSTIN (blank)
    ok_dialog()                                               # notes (blank)
    e.wait_for(SUPPLIER, timeout=30)


def track_batches_on_sugar():
    items_root()
    e.type_into(0, "Sugar")
    e.hide_keyboard()
    e.tap("Sugar", exact=True, timeout=30, cls="android.widget.TextView")
    e.tap("Track batch & expiry", exact=True, scroll=True, timeout=40)
    e.hide_keyboard()
    e.tap("Save", exact=True, scroll=True, timeout=30)
    e.wait_for("+ New item", exact=True, timeout=30)


def purchase_with_batch():
    open_hub()
    e.tap("New purchase", exact=True, timeout=20)
    e.tap("Choose a supplier", timeout=30)
    e.tap(SUPPLIER, timeout=20)
    e.type_into(0, "E2E-1")                                   # supplier's bill no.
    e.hide_keyboard()
    e.tap("+ Add item", exact=True, scroll=True, timeout=30)
    ok_dialog("Sugar")                                        # search
    e.tap("Sugar", timeout=20)                                # "1. Sugar · kg" in the action sheet
    ok_dialog("10")                                           # quantity
    ok_dialog("40")                                           # cost before GST
    e.tap("GST 5%", exact=True, timeout=20)
    ok_dialog("E2EB1")                                        # batch no
    ok_dialog("12/30")                                        # expiry MM/YY
    e.wait_for("E2EB1", timeout=30)
    e.tap("Save purchase", exact=True, scroll=True, timeout=30)
    e.wait_for("New purchase", exact=True, timeout=40)        # back on the hub after the save


def sell_batch_item():
    e.recover()
    e.goto_tab("Bill")
    e.new_bill()
    e.add_item("Sugar", "Sugar")
    e.tap("Cash", exact=True)
    e.tap("Pay", exact=True, timeout=20)
    e.wait_for("Bill done", timeout=60)


def expiring_list():
    open_hub()
    e.tap("Expiring", exact=True, timeout=20)
    e.wait_for("Within 90 days", timeout=30)


def purchases_report():
    open_hub()
    e.tap("Purchases & stock report", exact=True, scroll=True, timeout=20)
    e.wait_for("Purchases this month by supplier", timeout=30)
    e.wait_for(SUPPLIER, timeout=30)
    e.wait_for("Gross margin this month (ESTIMATE)", scroll=True, timeout=30)
    items_root()


STEPS = [
    ("inventory-add-supplier", add_supplier),
    ("inventory-track-batches", track_batches_on_sugar),
    ("inventory-purchase-batch", purchase_with_batch),
    ("inventory-sell-batch-item", sell_batch_item),
    ("inventory-expiring-list", expiring_list),
    ("inventory-purchases-report", purchases_report),
]
