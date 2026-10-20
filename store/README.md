# Play Store listing assets

| File | Play Console field | Size |
|---|---|---|
| `play-icon-512.png` | App icon | 512 × 512 PNG |
| `feature-graphic-1024x500.png` | Feature graphic | 1024 × 500 PNG |
| `launcher-preview.png` | Not uploaded: how the icon looks under round, squircle and teardrop launcher masks | — |

**The mark:** a kirana shop's striped awning over **అ**, the first letter of **అంగడి** ("shop"). It says "shop" to
anyone, and "Telugu" to the people it is built for. Deep green and turmeric amber, the app's own colours.
The Telugu letter is a path traced from Noto Sans Telugu Bold (SIL Open Font Licence), so no font is needed to draw it.

**Regenerate** (repo root): `python3 tools/brand/make_logo.py` writes the app icon layers, splash and `store/play-icon.svg`.
Then render the PNGs with Chromium: `node tools/brand/render.js store/play-icon.svg store/play-icon-512.png 512 512`
and `node tools/brand/render.js tools/brand/feature.html store/feature-graphic-1024x500.png 1024 500`.

**Listing text**
- App name: `Angadi – Telugu Billing POS` (27/30)
- Short description: `Speak in Telugu, get the bill. UPI, udhaar, stock & GST receipts.` (66/80)
- Category: Business
- Contains ads: No · In-app purchases: subscriptions (`angadi_pro_monthly` ₹200/month, `angadi_pro_yearly` ₹1,999/year)
- Target audience: 18+ (business tool) · Data safety: no data collected or shared — everything is stored on the device (see `docs/PRIVACY_POLICY.md`)

**Full description (English)**

Angadi (అంగడి) is a billing POS built for kirana shops and small pharmacies in Andhra Pradesh and Telangana — Telugu first, not Telugu as an afterthought.

SPEAK, AND THE BILL IS READY
Say the items in Telugu — "బియ్యం రెండు కేజీలు, పప్పు అర కేజీ" — and Angadi builds the bill. No typing, no hunting through lists while a customer waits. The app learns the words your shop actually uses.

SCAN, TAP, DONE
Camera barcode scanning for packaged goods, favourites for your fastest movers, and a big, clear bill screen that works on the phone or tablet your shop already owns.

CASH, UPI, UDHAAR — ALL THREE
Take cash, show your UPI QR, or put it on the customer's udhaar (appu) account. Every payment is recorded, dues are tracked customer by customer, and you can send a polite WhatsApp reminder listing each unpaid bill with its date.

GST RECEIPTS WITHOUT THE HEADACHE
Every bill is GST-correct, with a receipt you can print or share on WhatsApp as a PDF. Day-end and GST reports are ready when you need them.

STOCK THAT WATCHES ITSELF
Stock updates with every bill. Low-stock and out-of-stock alerts, a stock ledger that proves every change, and free on-device insights: what to reorder, what is gathering dust, and a morning summary of yesterday.

YOUR DATA STAYS YOURS
Everything lives on your device — no Angadi server, no account, and billing works fully offline. Back up and restore whenever you change phones.

Free for your first 50 bills every month, then ₹200/month or ₹1,999/year.

**Full description (తెలుగు)**

అంగడి — ఆంధ్రప్రదేశ్, తెలంగాణలోని కిరాణా దుకాణాలు, చిన్న మందుల షాపుల కోసం తెలుగులోనే తయారైన బిల్లింగ్ యాప్.

చెప్పండి, బిల్లు సిద్ధం
"బియ్యం రెండు కేజీలు, పప్పు అర కేజీ" అని తెలుగులో చెబితే చాలు — బిల్లు తయారవుతుంది. కస్టమర్ ఎదురుచూస్తుండగా టైపు చేయాల్సిన పని లేదు. మీ దుకాణంలో వాడే పదాలను యాప్ నేర్చుకుంటుంది.

బార్‌కోడ్ స్కాన్, టచ్ — అంతే
ప్యాకెట్ వస్తువులకు కెమెరా బార్‌కోడ్ స్కాన్, తరచూ అమ్మే వస్తువులకు ఫేవరెట్స్, మీ దగ్గర ఉన్న ఫోన్ లేదా టాబ్లెట్‌పైనే పెద్ద, స్పష్టమైన బిల్లు స్క్రీన్.

నగదు, UPI, అప్పు — మూడూ
నగదు తీసుకోండి, UPI QR చూపించండి, లేదా కస్టమర్ అప్పు ఖాతాలో వేయండి. ప్రతి చెల్లింపూ నమోదవుతుంది; ఎవరు ఎంత బాకీ ఉన్నారో స్పష్టంగా తెలుస్తుంది; బాకీ బిల్లుల వివరాలతో WhatsAppలో మర్యాదగా గుర్తు చేయవచ్చు.

GST బిల్లులు, రిపోర్టులు
ప్రతి బిల్లు GST నియమాల ప్రకారం తయారవుతుంది; రసీదును ప్రింట్ చేయవచ్చు, PDFగా WhatsAppలో పంపవచ్చు. రోజు చివరి రిపోర్టు, GST రిపోర్టు సిద్ధంగా ఉంటాయి.

స్టాక్, హెచ్చరికలు
ప్రతి బిల్లుతో స్టాక్ సర్దుబాటు అవుతుంది. తక్కువ స్టాక్, అయిపోయిన స్టాక్ హెచ్చరికలు; ఏ వస్తువు మళ్లీ ఆర్డర్ చేయాలో, ఏది కదలకుండా పడి ఉందో సూచనలు; నిన్నటి అమ్మకాల ఉదయం సారాంశం.

మీ డేటా, మీ పరికరంలోనే
సర్వర్ లేదు, ఖాతా లేదు, ఇంటర్నెట్ లేకపోయినా బిల్లింగ్ పనిచేస్తుంది. ఫోన్ మార్చినప్పుడు బ్యాకప్‌తో అంతా తిరిగి వస్తుంది.

ప్రతి నెలా మొదటి 50 బిల్లులు ఉచితం. తర్వాత నెలకు ₹200 లేదా ఏడాదికి ₹1,999.

## Screenshots (RELEASE-3 checklist)

Play will not publish without phone screenshots, and the tablet listing — the devices this POS targets — needs 7″ and 10″ tablet screenshots. Capture these screens, in this order, Telugu UI, real seed data:

- [ ] **Phone** (min 2, up to 8; 1080×1920 or larger): 1) POS counter with a built bill, 2) voice billing result, 3) barcode scan-to-add, 4) customer udhaar ledger, 5) day-end report, 6) UPI QR / setup
- [ ] **7″ tablet**: the same six screens in the tablet layout
- [ ] **10″ tablet**: the same six screens in the tablet layout

**Where they come from:** the CI `e2e` job drives the real app on an emulator and publishes its step screenshots to the **`ci-e2e` branch** (and as the `e2e-results` artifact, `e2e-out/`) on every run — harvest the phone set from there. The e2e emulator profile is a phone (pixel_6), so the 7″/10″ sets still need a tablet emulator profile or a real-tablet pass before submission; tick the boxes only when the PNGs are actually in Play Console.
