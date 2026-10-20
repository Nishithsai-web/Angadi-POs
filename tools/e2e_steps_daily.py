"""
T4 daily-counter steps for the emulator E2E (loaded by tools/e2e_android.py: STEPS run after its own steps).

What a shopkeeper does every day and the base run does not cover: a new item with price + opening stock, then
billed; editing a line's quantity; a bill discount; voiding a bill (stock returns, seen in Reports); a dues payment
(balance drops); a full cash bill in Telugu; the "New bill" confirmation; Android Back mid-bill.
Each step starts from wherever the last one left off (goto_tab / new_bill), and none uses run-as, so all of
them run on the debug AND the release build.
"""
import re
import time

import e2e_android as e

EDIT = "android.widget.EditText"


def known_bug(message):
    """A real app bug this step steps over: a clearly labelled warning (finding ID in the text), not a failure.
    Only the one assertion about the bug itself goes here, so anything else in the step still fails the run."""
    print(f"::warning::EXPECTED FAILURE (known app bug, reported as a finding): {message}", flush=True)
    e.shot("KNOWN-BUG")


def clear_focused_field(chars=16):
    """Prompts open pre-filled (quantity "1", discount "0", the dues balance): end of text, then delete it all."""
    e.adb("shell", "input", "keyevent", "KEYCODE_MOVE_END", *(["KEYCODE_DEL"] * chars))
    time.sleep(0.4)


def type_in_dialog(value, clear=False):
    field = None
    for _ in range(20):
        fields = e.edit_texts()
        if fields:
            field = fields[-1]
            break
        time.sleep(1)
    if field is None:
        raise AssertionError("no text field in the dialog")
    x, y = e.center(field)
    e.adb("shell", "input", "tap", str(x), str(y))
    time.sleep(0.5)
    if clear:
        clear_focused_field()
    if value:
        e.adb("shell", "input", "text", value.replace(" ", "%s"))
    time.sleep(0.5)


def type_into_clear(index, value):
    fields = e.edit_texts()
    if len(fields) <= index:
        raise AssertionError(f"expected at least {index + 1} text fields, found {len(fields)}")
    x, y = e.center(fields[index])
    e.adb("shell", "input", "tap", str(x), str(y))
    time.sleep(0.6)
    clear_focused_field(24)
    e.adb("shell", "input", "text", value.replace(" ", "%s"))
    time.sleep(0.6)


def swipe_down():
    e.adb("shell", "input", "swipe", "540", "600", "540", "1500", "300")
    time.sleep(0.8)


def type_into_field(hint, value):
    """An EMPTY text field found by its hint (Android reports an empty field's hint as its text); scrolls to it."""
    n = e.wait_for(hint, exact=True, cls=EDIT, scroll=True, timeout=30)
    x, y = e.center(n)
    e.adb("shell", "input", "tap", str(x), str(y))
    time.sleep(0.6)
    e.adb("shell", "input", "text", value.replace(" ", "%s"))
    time.sleep(0.6)
    e.hide_keyboard()


def text_after(caption):
    """The first non-empty text that follows a caption on screen ("Current dues" → "₹46.00")."""
    found = False
    for n in e.nodes():
        t = (n.get("text") or "").strip()
        if found and t:
            return t
        if t == caption:
            found = True
    return None


def paise(rupees_text):
    m = re.search(r"₹([\d,]+)\.(\d\d)", rupees_text or "")
    if not m:
        raise AssertionError(f"not an amount: {rupees_text!r}")
    return int(m.group(1).replace(",", "")) * 100 + int(m.group(2))


def rupees(p):
    return f"₹{p // 100:,}.{p % 100:02d}"   # amounts in this run stay under ₹1 lakh, so Western grouping = Indian


def in_foreground():
    """Is an Angadi window the one the user sees (not the launcher)?"""
    window = e.adb("shell", "dumpsys", "window", check=False)
    focus = re.search(r"mCurrentFocus=.*", window)
    if focus and e.PKG in focus.group(0):
        return True
    activities = e.adb("shell", "dumpsys", "activity", "activities", check=False)
    resumed = re.search(r"(topResumedActivity|mResumedActivity)[=:].*", activities)
    return bool(resumed and e.PKG in resumed.group(0))


def pay_cash_exact():
    e.tap("Cash", exact=True)
    e.tap("Pay", exact=True, timeout=20)                 # empty amount = exact cash
    e.wait_for("Bill done", timeout=60)


def bill_number_on_screen():
    n = e.wait_for("Bill no ", timeout=30)
    m = re.search(r"[A-Z0-9]+-\d{6}", n.get("text") or "")
    if not m:
        raise AssertionError(f"no bill number on the receipt: {n.get('text')!r}")
    return m.group(0)


def new_item_then_bill():
    """A new packet arrives: add it from the Items tab with a price and opening stock, sell one, stock drops by one."""
    e.goto_tab("Items")
    e.tap("+ New item", exact=True, timeout=30)
    type_into_field("English name", "E2E Biscuit")
    type_into_field("Price (incl. GST)", "25")
    type_into_field("Stock now", "10")                # opening stock (only shown for a new item)
    e.tap("Save", exact=True, scroll=True, timeout=30)
    e.wait_for("+ New item", exact=True, timeout=30)     # back on the Items list
    type_into_clear(0, "E2E")
    e.hide_keyboard()
    e.wait_for("10 pc", exact=True, timeout=30)           # the opening stock was recorded
    e.goto_tab("Bill")
    e.new_bill()
    e.add_item("E2E Bisc", "E2E Biscuit")
    e.wait_for("1 pc × ₹25.00", timeout=20)
    pay_cash_exact()
    e.wait_for("₹25.00", exact=True, timeout=20)
    e.goto_tab("Items")
    type_into_clear(0, "E2E")
    e.hide_keyboard()
    e.wait_for("9 pc", exact=True, timeout=30)            # sold one


def edit_line_quantity():
    """Customer says "make it three": tap the line, type the new quantity, the total follows."""
    e.goto_tab("Bill")
    e.new_bill()
    e.add_item("Rice", "Rice (Sona")
    e.tap("1 kg × ₹62.00", timeout=20)                   # the line itself opens the quantity prompt
    type_in_dialog("3", clear=True)
    e.tap("OK", exact=True, timeout=20)
    e.wait_for("3 kg × ₹62.00", timeout=20)
    e.wait_for("₹186.00", exact=True, timeout=20)
    e.new_bill()                                         # discard (confirmed)
    if e.find("kg × ₹") is not None:
        raise AssertionError("the discarded bill still shows its line")


def bill_discount():
    """A 50% bill discount: ₹62 of rice becomes ₹31, and the receipt says so."""
    e.goto_tab("Bill")
    e.new_bill()
    e.add_item("Rice", "Rice (Sona")
    e.wait_for("1 kg × ₹62.00", timeout=20)
    e.tap("Bill discount", exact=True, timeout=20)
    type_in_dialog("50", clear=True)
    e.tap("OK", exact=True, timeout=20)
    e.wait_for("₹31.00", exact=True, timeout=20)         # the total on the bill
    pay_cash_exact()
    e.wait_for("₹31.00", exact=True, timeout=20)         # and on the receipt


def void_bill_restores_stock():
    """Wrong bill: void it from Bills, the packet goes back on the shelf (Reports → stock movements)."""
    e.goto_tab("Bill")
    e.new_bill()
    e.add_item("Salt", "Salt (1")
    pay_cash_exact()
    number = bill_number_on_screen()
    print(f"    voiding {number}", flush=True)
    e.goto_tab("Bills")
    e.tap(number, exact=True, scroll=True, timeout=40)
    e.tap("Void bill", exact=True, scroll=True, timeout=30)
    type_in_dialog("wrong item")
    e.tap("OK", exact=True, timeout=20)
    e.wait_for(number + " · VOID", timeout=30)
    if e.find("Void bill", exact=True) is not None:
        raise AssertionError("a voided bill can still be voided")
    e.goto_tab("Reports")
    e.wait_for("Salt (1 kg) · Void — back to stock", scroll=True, timeout=60)


def dues_payment():
    """Ravi pays ₹20 of his udhaar: the balance drops by exactly that and the payment is in his dues history."""
    e.goto_tab("Customers")
    e.tap("Ravi", exact=True, timeout=30)
    e.wait_for("Current dues", exact=True, timeout=30)
    before = paise(text_after("Current dues"))
    if before < 2000:
        raise AssertionError(f"Ravi's dues are {rupees(before)}; the udhaar step should have left ₹46.00")
    e.tap("Record payment", exact=True, scroll=True, timeout=30)
    type_in_dialog("20", clear=True)                   # the prompt opens with the full balance filled in
    e.tap("OK", exact=True, timeout=20)
    end = time.time() + 30
    after = before
    while time.time() < end and after == before:
        time.sleep(1)
        text = text_after("Current dues")
        if text is None:
            swipe_down()
            continue
        after = paise(text)
    if after != before - 2000:
        raise AssertionError(f"dues went {rupees(before)} → {rupees(after)} after a ₹20.00 payment")
    e.wait_for("−₹20.00", exact=True, scroll=True, timeout=40)   # the payment row in the dues history


def telugu_cash_bill():
    """The whole counter in Telugu: labels, search, line, tender, receipt — then back to English."""
    e.goto_tab("Bill")
    e.new_bill()
    e.tap("తెలుగు", exact=True, timeout=30)
    try:
        e.wait_for("నగదు", exact=True, timeout=60)        # Cash
        for label in ("కొత్త బిల్లు", "బిల్లు తగ్గింపు", "ఉద్దెర", "చెప్పండి"):   # New bill, Bill discount, Udhaar, Speak
            if e.find(label) is None:
                raise AssertionError(f"Telugu label '{label}' missing; on screen: {e.screen_texts()}")
        e.wait_for("సరుకు వెతకండి", exact=True, cls=EDIT, timeout=20)   # search hint
        for english in ("Cash", "New bill", "Bill discount", "Udhaar"):
            if e.find(english, exact=True) is not None:
                raise AssertionError(f"English label '{english}' left on the Telugu screen")
        e.type_into(0, "Rice")
        e.tap("బియ్యం", exact=True, cls="android.widget.TextView", timeout=30)   # the tile shows the Telugu name
        e.hide_keyboard()
        e.wait_for("1 కేజీ × ₹62.00", timeout=20)
        e.tap("నగదు", exact=True)
        e.tap("చెల్లింపు", exact=True, timeout=20)        # Pay
        e.wait_for("బిల్లు పూర్తయింది", timeout=60)          # Bill done
        e.wait_for("బిల్లు నం", timeout=20)                 # Bill no
        e.wait_for("₹62.00", exact=True, timeout=20)
        e.shot("telugu-receipt")
        e.tap("కొత్త బిల్లు", exact=True, timeout=20)      # New bill → the counter
        e.wait_for("నగదు", exact=True, timeout=30)
    finally:
        n = e.find("English", exact=True)
        if n is not None:
            x, y = e.center(n)
            e.adb("shell", "input", "tap", str(x), str(y))
    e.wait_for("Cash", exact=True, timeout=60)


def new_bill_confirmation():
    """'New bill' with items on the bill asks first: No keeps the bill, Yes clears it."""
    e.goto_tab("Bill")
    e.new_bill()
    e.add_item("Sugar", "Sugar")
    e.wait_for("1 kg × ₹46.00", timeout=20)
    e.tap("New bill", exact=True, timeout=20)
    e.wait_for("Discard this bill and start a new one?", timeout=20)
    e.tap("No", exact=True, timeout=20)
    e.wait_for("1 kg × ₹46.00", timeout=20)              # kept
    e.tap("New bill", exact=True, timeout=20)
    e.wait_for("Discard this bill and start a new one?", timeout=20)
    e.tap("Yes", exact=True, timeout=20)
    e.wait_for("Tap the mic and say the items", timeout=20)
    if e.find("kg × ₹") is not None:
        raise AssertionError("'Yes' did not clear the bill")


def back_button_mid_bill():
    """Android Back on the counter with items on the bill must not leave the app; the bill must never be lost."""
    e.goto_tab("Bill")
    e.new_bill()
    e.add_item("Sugar", "Sugar")
    e.wait_for("1 kg × ₹46.00", timeout=20)
    e.hide_keyboard()                                    # Back would only close the keyboard otherwise
    e.adb("shell", "input", "keyevent", "4")
    time.sleep(3)
    stayed = in_foreground()
    e.shot("back-button-pressed")
    if not stayed:
        e.launch()
    e.wait_for("1 kg × ₹46.00", timeout=60)              # hard requirement: the bill is still there
    e.new_bill()
    if not stayed:
        known_bug("BUG T4-11: Back on the Bill tab with an unfinished bill sends Angadi to the background "
                       "(the bill itself survived)")


STEPS = [
    ("new-item-then-bill", new_item_then_bill),
    ("edit-line-quantity", edit_line_quantity),
    ("bill-discount", bill_discount),
    ("void-bill-restores-stock", void_bill_restores_stock),
    ("dues-payment-lowers-balance", dues_payment),
    ("telugu-cash-bill", telugu_cash_bill),
    ("new-bill-confirmation", new_bill_confirmation),
    ("back-button-mid-bill", back_button_mid_bill),
]
