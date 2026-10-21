"""First-run Back block (agent F2, A2-08). WIPES the app's data, so it must run after every other step module
(the 'zz' name sorts it last); only the final log and freeze checks follow it."""
import time

import e2e_android as e


def first_run_back_is_blocked():
    """With no shop profile the counter can't bill: Back on shop setup must not leave it (Android 16 predictive back
    included)."""
    e.adb("shell", "pm", "clear", e.PKG)
    time.sleep(2)
    e.launch()
    e.tap("English", exact=True, timeout=120)
    e.wait_for("Welcome to Angadi", timeout=60)
    e.adb("shell", "input", "keyevent", "4")
    time.sleep(3)
    e.alive()
    e.wait_for("Welcome to Angadi", timeout=15)


STEPS = [
    ("first-run-back-blocked", first_run_back_is_blocked),
]
