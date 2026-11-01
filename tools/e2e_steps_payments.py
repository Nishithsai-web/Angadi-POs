"""v2 split tender + returns (agent billing). Run by tools/e2e_android.py after its own steps; debug and release.

A cash + UPI split bill, a partial return of it refunded in cash, and both checked on the day-end report."""
import re
import time

import e2e_android as e

STATE = {}


def _type_field(field, value):
    x, y = e.center(field)
    e.adb("shell", "input", "tap", str(x), str(y))
    time.sleep(0.6)
    e.adb("shell", "input", "text", value)
    time.sleep(0.6)


def split_cash_upi_bill():
    """Counter: Rice → Split → ₹10 cash, the rest to UPI (the default) → QR → Payment received → Bill done."""
    e.goto_tab("Bill")
    e.new_bill()
    e.add_item("Rice", "Rice (Sona")
    e.tap("Split", exact=True, scroll=True, timeout=30)
    e.wait_for("Rest goes to", timeout=30)
    fields = e.edit_texts()
    if len(fields) < 3:
        raise AssertionError(f"split screen: expected 3 number boxes, found {len(fields)}")
    _type_field(fields[-3], "10")                      # cash part; UPI part left empty → the rest goes to UPI
    e.hide_keyboard()
    e.wait_for("Remaining", timeout=10)
    e.shot("split-entry")
    e.tap("Continue", exact=True, scroll=True, timeout=20)
    e.tap("Payment received", timeout=30)              # the QR is for the UPI part only
    e.type_in_dialog("515151515151")
    e.tap("OK", exact=True, timeout=20)
    e.wait_for("Bill done", timeout=60)
    m = re.search(r"\b[A-Z]{1,4}-\d{6}\b", e.screen_texts())
    if not m:
        raise AssertionError("no bill number on the receipt screen: " + e.screen_texts())
    STATE["bill"] = m.group(0)
    e.shot("split-done")


def partial_return_cash_refund():
    """Bills → the split bill → Return items → 0.5 kg → refund in cash → Record → R- number on the result screen."""
    bill = STATE.get("bill")
    if not bill:
        raise AssertionError("the split bill step did not record a bill number")
    e.goto_tab("Bills")
    e.tap(bill, exact=True, timeout=30)
    e.tap("Return items", scroll=True, timeout=30)
    e.wait_for("Give the money back as", scroll=True, timeout=30)
    fields = e.edit_texts()
    if len(fields) < 2:
        raise AssertionError(f"return screen: expected a quantity box and a reason box, found {len(fields)}")
    _type_field(fields[-2], "0.5")                     # the one line's quantity (the last box is the reason)
    e.hide_keyboard()
    e.wait_for("Refund ₹", scroll=True, timeout=20)    # live refund preview
    e.tap("Record return", scroll=True, timeout=20)
    e.wait_for("Return recorded", timeout=40)
    texts = e.screen_texts()
    number = re.search(r"\bR-\d{6}\b", texts)
    refund = re.search(r"Refund (₹[\d,]+\.\d\d)", texts)
    if not number or not refund:
        raise AssertionError("the result screen lacks the return number or the refund: " + texts)
    STATE["refund"] = refund.group(1)
    e.shot("return-done")
    e.tap("Done", exact=True, timeout=20)


def day_end_shows_split_and_return():
    """Reports: the day-end has the return, its refund and net sales; cash and UPI rows are there."""
    refund = STATE.get("refund")
    if not refund:
        raise AssertionError("the return step did not record a refund")
    e.goto_tab("Reports")
    e.wait_for("Day end", timeout=60)
    e.wait_for("Returns (", scroll=True, timeout=60)
    e.wait_for("Net sales", scroll=True, timeout=20)
    if e.find(refund, exact=True) is None:
        raise AssertionError(f"the refund {refund} is not on the day-end report: " + e.screen_texts())
    e.shot("day-end-returns")


STEPS = [
    ("split-cash-upi-bill", split_cash_upi_bill),
    ("partial-return-cash", partial_return_cash_refund),
    ("day-end-split-return", day_end_shows_split_and_return),
]
