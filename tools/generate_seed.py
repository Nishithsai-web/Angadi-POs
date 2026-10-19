# Generates src/Angadi.Infrastructure/Seed/*.json and fails on alias collisions between items.
# Run: python3 tools/generate_seed.py   (edit the lists below, never the JSON by hand)
import json,sys,unicodedata
K="Kilogram";P="Piece";PK="Pack";L="Litre";S="Strip"
# te, en, aliases, category, unit, price ₹, gst%, quick
kirana=[
("బియ్యం","Rice (Sona Masoori)",["biyyam","rice","సోనా మసూరి","సోనామసూరి"],"ధాన్యాలు",K,62,0,1),
("గోధుమ పిండి","Wheat atta",["atta","ఆటా","గోధుమపిండి","godhuma pindi"],"పిండ్లు",K,55,5,1),
("మైదా","Maida",["maida","మైదా పిండి"],"పిండ్లు",K,50,5,0),
("బొంబాయి రవ్వ","Rava (sooji)",["రవ్వ","ఉప్మా రవ్వ","sooji","rava","suji"],"పిండ్లు",K,55,5,0),
("కంది పప్పు","Toor dal",["కందిపప్పు","తూర్ దాల్","toor dal","kandi pappu"],"పప్పులు",K,160,0,1),
("పెసర పప్పు","Moong dal",["పెసరపప్పు","moong dal","pesara pappu"],"పప్పులు",K,130,0,0),
("మినప పప్పు","Urad dal",["మినపప్పు","మినప గుళ్ళు","urad dal","minapa pappu"],"పప్పులు",K,140,0,0),
("శనగ పప్పు","Chana dal",["శనగపప్పు","chana dal","senaga pappu"],"పప్పులు",K,95,0,0),
("పుట్నాల పప్పు","Roasted gram",["పుట్నాలు","putnalu","roasted gram"],"పప్పులు",K,120,5,0),
("చక్కెర","Sugar",["పంచదార","sugar","chakkera","panchadara"],"ధాన్యాలు",K,46,5,1),
("బెల్లం","Jaggery",["bellam","jaggery"],"ధాన్యాలు",K,70,0,0),
("ఉప్పు","Salt (1 kg)",["salt","uppu","ఉప్పు ప్యాకెట్"],"ధాన్యాలు",PK,28,0,1),
("పల్లి నూనె","Groundnut oil",["పల్లీ నూనె","వేరుశనగ నూనె","groundnut oil","palli nune"],"నూనెలు",L,190,5,0),
("సన్ ఫ్లవర్ నూనె","Sunflower oil",["నూనె","సన్‌ఫ్లవర్ ఆయిల్","sunflower oil","oil"],"నూనెలు",L,155,5,1),
("పామాయిల్","Palm oil",["పామ్ ఆయిల్","palm oil"],"నూనెలు",L,120,5,0),
("నెయ్యి","Ghee (500 ml)",["neyyi","ghee"],"పాల ఉత్పత్తులు",P,330,5,0),
("పాలు","Milk (500 ml)",["పాల ప్యాకెట్","milk","paalu"],"పాల ఉత్పత్తులు",PK,30,0,1),
("పెరుగు","Curd (500 g)",["perugu","curd","పెరుగు ప్యాకెట్"],"పాల ఉత్పత్తులు",PK,35,5,1),
("వెన్న","Butter (100 g)",["butter","venna"],"పాల ఉత్పత్తులు",P,60,5,0),
("పనీర్","Paneer (200 g)",["paneer","పన్నీర్"],"పాల ఉత్పత్తులు",P,90,0,0),
("గుడ్లు","Eggs",["కోడిగుడ్లు","గుడ్డు","ఎగ్స్","eggs","egg"],"పాల ఉత్పత్తులు",P,7,0,1),
("టీ పొడి","Tea powder (250 g)",["చాయ్ పొడి","టీపొడి","tea","tea powder"],"పానీయాలు",PK,140,5,1),
("కాఫీ పొడి","Coffee powder (100 g)",["కాఫీపొడి","coffee","coffee powder"],"పానీయాలు",PK,120,5,0),
("ఉల్లిపాయలు","Onions",["ఉల్లిగడ్డలు","ఉల్లిపాయ","onion","onions","ullipayalu"],"కూరగాయలు",K,35,0,1),
("టమాటాలు","Tomatoes",["టమాటా","టమాట","tomato","tomatoes"],"కూరగాయలు",K,30,0,1),
("బంగాళాదుంపలు","Potatoes",["ఆలుగడ్డలు","ఆలు","బంగాళదుంప","potato","potatoes","aloo"],"కూరగాయలు",K,35,0,1),
("పచ్చిమిర్చి","Green chillies",["మిర్చి","పచ్చి మిరపకాయలు","green chilli","mirchi"],"కూరగాయలు",K,80,0,1),
("అల్లం","Ginger",["allam","ginger"],"కూరగాయలు",K,120,0,0),
("వెల్లుల్లి","Garlic",["ఎల్లిపాయ","వెల్లుల్లిపాయలు","garlic","vellulli"],"కూరగాయలు",K,200,0,0),
("నిమ్మకాయలు","Lemons",["నిమ్మకాయ","lemon","lemons","nimmakaya"],"కూరగాయలు",P,5,0,0),
("కరివేపాకు","Curry leaves",["karivepaku","curry leaves"],"కూరగాయలు",PK,10,0,1),
("కొత్తిమీర","Coriander leaves",["kothimeera","coriander"],"కూరగాయలు",PK,10,0,1),
("కారం","Chilli powder",["కారం పొడి","మిరప్పొడి","chilli powder","karam"],"మసాలాలు",K,320,5,0),
("పసుపు","Turmeric powder",["పసుపు పొడి","turmeric","pasupu"],"మసాలాలు",K,280,5,0),
("ధనియాల పొడి","Coriander powder",["ధనియాలు","dhaniya powder","dhaniyalu"],"మసాలాలు",K,240,5,0),
("జీలకర్ర","Cumin seeds",["జీరా","jeera","cumin","jeelakarra"],"మసాలాలు",K,400,5,0),
("ఆవాలు","Mustard seeds",["avalu","mustard"],"మసాలాలు",K,120,5,0),
("మెంతులు","Fenugreek seeds",["menthulu","methi","fenugreek"],"మసాలాలు",K,160,5,0),
("మిరియాలు","Black pepper",["miriyalu","pepper","black pepper"],"మసాలాలు",K,800,5,0),
("యాలకులు","Cardamom (10 g)",["ఏలకులు","ఇలాచీ","elaichi","cardamom"],"మసాలాలు",PK,40,5,0),
("లవంగాలు","Cloves (10 g)",["lavangalu","cloves"],"మసాలాలు",PK,25,5,0),
("చింతపండు","Tamarind",["chintapandu","tamarind","imli"],"మసాలాలు",K,180,5,0),
("గరం మసాలా","Garam masala (50 g)",["garam masala","మసాలా"],"మసాలాలు",PK,45,5,0),
("ఇడ్లీ రవ్వ","Idli rava",["idli rava","ఇడ్లీరవ్వ"],"పిండ్లు",K,60,5,0),
("అటుకులు","Poha",["పోహా","poha","atukulu"],"ధాన్యాలు",K,70,5,0),
("సేమియా","Vermicelli (200 g)",["semiya","vermicelli","సేమ్యా"],"ధాన్యాలు",PK,30,5,0),
("బిస్కెట్లు","Biscuits",["బిస్కెట్","బిస్కట్లు","biscuit","biscuits"],"స్నాక్స్",PK,10,5,1),
("బ్రెడ్","Bread",["bread","పావ్","రొట్టె"],"స్నాక్స్",PK,45,0,1),
("రస్క్","Rusk",["rusk","రస్కులు"],"స్నాక్స్",PK,40,5,0),
("చిప్స్","Chips",["chips","వేఫర్స్","wafers"],"స్నాక్స్",PK,20,5,1),
("మిక్చర్","Namkeen mixture (200 g)",["మిక్స్చర్","నమ్కీన్","mixture","namkeen"],"స్నాక్స్",PK,50,5,0),
("చాక్లెట్","Chocolate",["చాక్లెట్లు","chocolate"],"స్నాక్స్",P,10,5,1),
("కూల్ డ్రింక్","Soft drink (750 ml)",["కూల్‌డ్రింక్","సాఫ్ట్ డ్రింక్","cool drink","soft drink"],"పానీయాలు",P,45,40,1),
("వాటర్ బాటిల్","Water bottle (1 L)",["మంచినీళ్ళు","నీళ్ళ బాటిల్","water","water bottle"],"పానీయాలు",P,20,5,1),
("స్నానం సబ్బు","Bath soap",["సబ్బు","బాత్ సోప్","soap","bath soap"],"సౌందర్య",P,40,5,1),
("బట్టల సబ్బు","Washing bar",["వాషింగ్ సోప్","washing soap","battala sabbu"],"శుభ్రత",P,30,18,0),
("డిటర్జెంట్ పౌడర్","Detergent powder (1 kg)",["సర్ఫ్","వాషింగ్ పౌడర్","detergent","washing powder"],"శుభ్రత",PK,110,18,0),
("టూత్ పేస్ట్","Toothpaste (100 g)",["పేస్ట్","toothpaste","paste"],"సౌందర్య",P,55,5,1),
("టూత్ బ్రష్","Toothbrush",["బ్రష్","brush","toothbrush"],"సౌందర్య",P,25,5,0),
("షాంపూ","Shampoo sachet",["షాంపూ ప్యాకెట్","shampoo"],"సౌందర్య",P,2,5,1),
("కొబ్బరి నూనె","Coconut oil (200 ml)",["తల నూనె","కొబ్బరినూనె","coconut oil","hair oil"],"సౌందర్య",P,80,5,0),
("అగ్గిపెట్టె","Matchbox",["అగ్గిపుల్లలు","matchbox","agipette"],"పూజ",P,2,5,1),
("అగరబత్తి","Agarbatti",["అగర్‌బత్తి","అగరబత్తీలు","agarbatti","incense"],"పూజ",PK,30,5,0),
("కొవ్వొత్తి","Candle",["కొవ్వొత్తులు","candle","candles"],"పూజ",P,5,5,0),
("అరటిపండ్లు","Bananas",["అరటిపండు","అరటి పండ్లు","banana","bananas"],"పండ్లు",P,5,0,0),
("కొబ్బరికాయ","Coconut",["కొబ్బరి కాయ","coconut","kobbarikaya"],"పండ్లు",P,30,0,0),
("పల్లీలు","Groundnuts",["వేరుశనగలు","పల్లీ","groundnut","peanuts","palleelu"],"ధాన్యాలు",K,140,0,0),
("జీడిపప్పు","Cashew",["జీడి పప్పు","కాజు","cashew","kaju"],"డ్రై ఫ్రూట్స్",K,900,5,0),
("కిస్మిస్","Raisins",["ఎండు ద్రాక్ష","ఎండుద్రాక్ష","raisins","kismis"],"డ్రై ఫ్రూట్స్",K,400,5,0),
("సగ్గుబియ్యం","Sabudana",["సగ్గు బియ్యం","sabudana","saggubiyyam"],"ధాన్యాలు",K,90,5,0),
("రాగి పిండి","Ragi flour",["రాగిపిండి","రాగులు","ragi","ragi flour"],"పిండ్లు",K,60,5,0),
("జొన్నలు","Jowar",["జొన్న","jowar","jonnalu"],"ధాన్యాలు",K,50,0,0),
("బొబ్బర్లు","Black-eyed beans",["అలసందలు","bobbarlu","lobia"],"పప్పులు",K,110,0,0),
("శనగలు","Chickpeas",["కాబూలీ శనగలు","chana","chickpeas","senagalu"],"పప్పులు",K,90,0,0),
("నూడుల్స్","Noodles",["noodles","మ్యాగీ"],"స్నాక్స్",PK,15,5,1),
("జామ్","Jam",["jam","ఫ్రూట్ జామ్"],"స్నాక్స్",P,120,5,0),
("ఆవకాయ","Mango pickle (500 g)",["పచ్చడి","ఆవకాయ పచ్చడి","pickle","avakaya"],"స్నాక్స్",P,150,5,0),
("అప్పడాలు","Papad",["అప్పడం","papad","appadalu"],"స్నాక్స్",PK,40,5,0),
("టాయిలెట్ క్లీనర్","Toilet cleaner",["toilet cleaner","బాత్రూమ్ క్లీనర్"],"శుభ్రత",P,95,18,0),
("దోమల కాయిల్","Mosquito coil",["మస్కిటో కాయిల్","mosquito coil","coil"],"శుభ్రత",PK,40,18,0),
]
pharm=[
("పారాసెటమాల్ 650","Paracetamol 650 mg",["డోలో","dolo","dolo 650","paracetamol","జ్వరం మాత్రలు"],"మాత్రలు",S,30,5,1),
("పారాసెటమాల్ 500","Paracetamol 500 mg",["క్రోసిన్","crocin","paracetamol 500"],"మాత్రలు",S,20,5,0),
("సెటిరిజిన్","Cetirizine 10 mg",["cetirizine","సెట్జిన్","అలర్జీ మాత్ర"],"మాత్రలు",S,20,5,1),
("అజిత్రోమైసిన్ 500","Azithromycin 500 mg",["azithromycin","అజిత్రాల్","azithral"],"మాత్రలు",S,110,5,0),
("అమోక్సిసిలిన్ 500","Amoxicillin 500 mg",["amoxicillin","మోక్స్","mox"],"మాత్రలు",S,90,5,0),
("పాంటోప్రజోల్ 40","Pantoprazole 40 mg",["pantoprazole","ప్యాన్ 40","pan 40","గ్యాస్ మాత్ర"],"మాత్రలు",S,110,5,1),
("ఒమెప్రజోల్ 20","Omeprazole 20 mg",["omeprazole","ఓమెజ్","omez"],"మాత్రలు",S,60,5,0),
("ఐబుప్రోఫెన్ 400","Ibuprofen 400 mg",["ibuprofen","బ్రూఫెన్","brufen","నొప్పి మాత్ర"],"మాత్రలు",S,25,5,0),
("డైక్లోఫెనాక్ 50","Diclofenac 50 mg",["diclofenac","వోవెరాన్","voveran"],"మాత్రలు",S,20,5,0),
("మెట్‌ఫార్మిన్ 500","Metformin 500 mg",["metformin","షుగర్ మాత్ర","గ్లైసిఫేజ్"],"మాత్రలు",S,30,5,0),
("అమ్లోడిపిన్ 5","Amlodipine 5 mg",["amlodipine","బీపీ మాత్ర","bp tablet"],"మాత్రలు",S,35,5,0),
("టెల్మిసార్టన్ 40","Telmisartan 40 mg",["telmisartan","టెల్మా","telma"],"మాత్రలు",S,80,5,0),
("అటోర్వాస్టాటిన్ 10","Atorvastatin 10 mg",["atorvastatin","కొలెస్ట్రాల్ మాత్ర"],"మాత్రలు",S,90,5,0),
("ఓఆర్ఎస్","ORS sachet",["ors","ఓ ఆర్ ఎస్","ఎలక్ట్రాల్","electral"],"సాషేలు",P,22,5,1),
("దగ్గు సిరప్","Cough syrup (100 ml)",["కఫ్ సిరప్","cough syrup","దగ్గు మందు"],"సిరప్‌లు",P,110,5,1),
("యాంటాసిడ్ సిరప్","Antacid syrup (170 ml)",["డైజీన్","digene","antacid","గ్యాస్ సిరప్"],"సిరప్‌లు",P,120,5,0),
("విటమిన్ సి","Vitamin C tablets",["vitamin c","లిమ్సీ","limcee"],"విటమిన్లు",S,30,5,0),
("బి కాంప్లెక్స్","B-complex",["b complex","బీకాంప్లెక్స్","becosules"],"విటమిన్లు",S,35,5,0),
("కాల్షియం","Calcium + D3",["calcium","కాల్షియం మాత్రలు","shelcal"],"విటమిన్లు",S,100,5,0),
("ఐరన్ మాత్రలు","Iron + folic acid",["iron","ఫోలిక్ యాసిడ్","iron tablets"],"విటమిన్లు",S,40,5,0),
("నొప్పి ఆయింట్‌మెంట్","Pain relief ointment (30 g)",["ఆయింట్‌మెంట్","pain balm","బామ్","balm"],"క్రీములు",P,95,5,0),
("యాంటీసెప్టిక్ లిక్విడ్","Antiseptic liquid (100 ml)",["డెట్టాల్","dettol","antiseptic"],"ప్రథమ చికిత్స",P,60,5,0),
("బ్యాండ్ ఎయిడ్","Adhesive bandages",["band aid","బ్యాండేజ్ స్ట్రిప్స్","plaster"],"ప్రథమ చికిత్స",PK,40,5,1),
("దూది","Cotton roll (50 g)",["కాటన్","cotton","duudi"],"ప్రథమ చికిత్స",P,40,5,0),
("గాజ్ బ్యాండేజ్","Gauze bandage roll",["బ్యాండేజ్","gauze","bandage"],"ప్రథమ చికిత్స",P,30,5,0),
("థర్మామీటర్","Digital thermometer",["thermometer","జ్వరం మీటర్"],"పరికరాలు",P,180,5,0),
("మాస్క్","Face mask",["mask","మాస్కులు"],"పరికరాలు",P,10,5,1),
("శానిటైజర్","Hand sanitizer (100 ml)",["sanitizer","sanitiser"],"శుభ్రత",P,50,18,0),
("శానిటరీ ప్యాడ్స్","Sanitary pads",["ప్యాడ్స్","నాప్కిన్స్","pads","sanitary napkins","విస్పర్"],"స్త్రీ సంరక్షణ",PK,40,0,1),
("డైపర్లు","Baby diapers",["డైపర్","diapers","pampers"],"శిశు సంరక్షణ",PK,350,5,0),
("కంటి చుక్కలు","Eye drops",["కళ్ళ చుక్కలు","eye drops","ఐ డ్రాప్స్"],"చుక్కలు",P,60,5,0),
("చెవి చుక్కలు","Ear drops",["ear drops","ఇయర్ డ్రాప్స్"],"చుక్కలు",P,70,5,0),
("ఫంగల్ క్రీమ్","Antifungal cream",["antifungal","దురద క్రీమ్","candid"],"క్రీములు",P,110,5,0),
("గ్లూకోజ్","Glucose powder (500 g)",["glucose","గ్లూకోజ్ పొడి","glucon d"],"సాషేలు",P,110,5,0),
("ఇన్సులిన్ సిరంజి","Insulin syringe",["syringe","సిరంజి","insulin syringe"],"పరికరాలు",P,12,5,0),
("డొంపెరిడోన్ 10","Domperidone 10 mg",["domperidone","వాంతుల మాత్ర","domstal"],"మాత్రలు",S,35,5,0),
("లోపరమైడ్ 2","Loperamide 2 mg",["loperamide","విరేచనాల మాత్ర","loperamide tablet"],"మాత్రలు",S,20,5,0),
("మల్టీవిటమిన్","Multivitamin",["multivitamin","మల్టీ విటమిన్","zincovit"],"విటమిన్లు",S,90,5,0),
("జలుబు మాత్రలు","Cold relief tablets",["జలుబు మాత్ర","cold tablet","sinarest","సైనారెస్ట్"],"మాత్రలు",S,45,5,1),
("ఆవిరి క్యాప్సూల్స్","Steam inhalation capsules",["స్టీమ్ క్యాప్సూల్స్","steam capsules","ఆవిరి మందు"],"క్యాప్సూల్స్",S,30,5,0),
]
assert len(kirana)==80,len(kirana); assert len(pharm)==40,len(pharm)
def key(s):
    s=unicodedata.normalize("NFC",s).replace("‌","").replace("‍","").lower().replace(" ","")
    # Must mirror TeluguText.MatchKey in C#.
    if s.endswith("మ్"): s=s[:-2]+"ం"
    if len(s)>4 and s.endswith("లు"): s=s[:-2]
    elif len(s)>4 and s.endswith("ల"): s=s[:-1]
    elif len(s)>3 and all(ord(c)<=0x24f for c in s) and s.endswith("s") and not s.endswith("ss"): s=s[:-1]
    return s
seen={};bad=0
for te,en,al,*_ in kirana+pharm:
    for n in set(key(x) for x in [te,en]+al):
        if n in seen and seen[n]!=te: print("COLLISION",n,seen[n],te); bad+=1
        seen[n]=te
def out(rows,opening,low,stripOpen,stripLow):
    r=[]
    for te,en,al,cat,u,price,gst,q in rows:
        o,l=(stripOpen,stripLow) if u==S else (opening,low)
        r.append(dict(te=te,en=en,aliases=al,category=cat,unit=u,pricePaise=price*100,gstBasisPoints=gst*100,openingStockMilli=o*1000,lowStockMilli=l*1000,quickSale=bool(q)))
    return r
hdr={"_comment":"Sample items for first-run seeding (FR-04). Prices are typical MRP and must be edited to the shop's own. GST slabs follow GST 2.0 (22 Sep 2025) for the usual packaged form; loose staples are nil. Shopkeepers should confirm slabs with their accountant."}
import os
SEED=os.path.join(os.path.dirname(os.path.abspath(__file__)),"..","src","Angadi.Infrastructure","Seed")
files={"kirana.json":{**hdr,"items":out(kirana,20,5,30,10)},"pharmacy.json":{**hdr,"items":out(pharm,20,5,30,10)}}
if "--check" in sys.argv:
    # CI: the committed JSON must equal what this script generates (nobody hand-edits the JSON).
    stale=[n for n,d in files.items() if json.load(open(os.path.join(SEED,n),encoding="utf-8"))!=d]
    if stale: print("seed JSON out of date, run python3 tools/generate_seed.py:",stale); sys.exit(1)
else:
    for n,d in files.items(): json.dump(d,open(os.path.join(SEED,n),"w",encoding="utf-8"),ensure_ascii=False,indent=1)
sys.exit(1) if bad else None; print("collisions:",bad,"| quick-sale:",sum(r[-1] for r in kirana+pharm))
