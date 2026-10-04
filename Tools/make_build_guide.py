#!/usr/bin/env python3
"""Writes 'Network Maps/BUILD_GUIDE.md': the manual, type-it-yourself build guide for the whole network redesign."""
import os, sys, math
sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
import guide_data as G
import guide_routes as GR
A = G.A
OUT = os.path.join(A.ROOT, "Network Maps", "BUILD_GUIDE.md")
L = []
def w(*lines):
    for t in lines: L.append(t)
def h(n, t): w("", "#" * n + " " + t, "")
def table(head, rows):
    w("| " + " | ".join(head) + " |", "|" + "|".join("---" for _ in head) + "|")
    for r in rows: w("| " + " | ".join(str(x) for x in r) + " |")
    w("")
def v3(x, z): return f"({x}, 0, {z})"
def nodes_table(nodes, start=0, note_col=False):
    rows = [(start + i, n[0], 0, n[1], "tick" if n[2] else "") for i, n in enumerate(nodes)]
    table(["#", "X", "Y", "Z", "isCurve"], rows)
STR = lambda c: f"`{c}`"
def sname(c): return GR.name_of(c)
R = G.R
def old_nodes(num, outbound=True, variant=None):
    r = R[num]
    if variant:
        v = next(v for v in r["variants"] if v["letter"] == variant); return v["outNodes" if outbound else "inNodes"]
    return r["outNodes" if outbound else "inNodes"]
def nodeline(nodes, idx): p, c = nodes[idx]; return f"#{idx} ({round(p[0])}, {round(p[1])})"
def neighbours_text(route, outbound, new_xy, newcode, variant=None):
    r = R[route]; lst = (r["variants"][0]["outStops" if outbound else "inStops"] if variant else r["outStops" if outbound else "inStops"])
    nodes = old_nodes(route, outbound, variant); pts = [(p[0], p[1]) for p, c in nodes]
    pars = [GR.proj_along(pts, GR.stop_xy(c)) for c in lst]; p = GR.proj_along(pts, new_xy); i = 0
    while i < len(lst) and pars[i] < p: i += 1
    a_ = lst[i-1] if i > 0 else None; b_ = lst[i] if i < len(lst) else None
    if a_ and b_: return f"insert `{newcode}` **between** `{a_}` ({GR.name_of(a_)}) **and** `{b_}` ({GR.name_of(b_)})"
    return f"insert `{newcode}` " + ("**at the start** of the list, before `" + b_ + "`" if a_ is None else "**at the end** of the list, after `" + a_ + "`")
def fmt_codes(codes): return ", ".join(f"`{c}`" for c in codes)

# ═════════════════════════════════════════════════════════════════════════════════════════════════
w("# TRANSIT CITY: MANUAL BUILD GUIDE", "",
  "Everything the plan PDFs show, turned into things you type. Nothing here is automated: you type the numbers into the Unity Inspector yourself, one small step at a time.",
  "", "**Files this guide matches:** `Network_Redesign_Plan.pdf` (the pictures) and this file (the typing). If the two ever disagree, trust the PDF picture for *what you want* and this file for *the exact numbers*.", "",
  "**Data it was built from:** city backup `" + G.src + "`, 22 route assets in `ROUTES/CBT/`, the fleet roster `DEPOTS/FleetRoster.asset`.", "")
h(2, "How to use this guide")
w("1. Work **top to bottom**. Each phase finishes with a **STOP: save and test** box. Do not start the next phase until the test passes.",
  "2. Every step has a tick box. Tick it as you go (copy this file into your notes app if you want live ticks).",
  "3. **Never delete a stop or a road to 'remove' it.** Unity stores the CityManager lists as prefab overrides keyed by list position, so deleting an element shifts everything. Retire stops by renaming them `placeholder` and clearing their road (instructions in Phase 3).",
  "4. When a step says **type**, the value is in a code box or table. When it says **copy**, it names the route asset and the exact node numbers to copy from.",
  "5. Coordinates are always **X, Y, Z**. **Y is always 0.** X runs east (+) / west (−). Z runs north (+) / south (−). The plan PDFs use the same X and Z.", "")
h(2, "Table of contents")
w("- **Phase 0**: Prepare and back up", "- **Phase 1**: Fleet roster (the new 2900 Series (Artic) Express-Max buses and the 2800 normal artics)", "- **Phase 2**: Roads (CityManager > Road Definitions)",
  "- **Phase 3**: Stops (CityManager > Stop Definitions)", "- **Phase 4**: Route assets, route by route (all 22)", "- **Phase 5**: The big routes in full: 136, **Variant N (136N)**, **236 Express-Max**, 29, 240",
  "- **Phase 6**: Registration lists, depots, fleet limits", "- **Phase 7**: Terminal-by-terminal checklist (what each terminal needs, in one place)", "- **Phase 8**: Testing in Play Mode", "- **Appendix**: stop master table, how tValue works, troubleshooting", "")

# ── PHASE 0 ───────────────────────────────────────────────────────────────────────────────────────
h(2, "Phase 0: Prepare and back up")
w("**Step 0.1** Commit everything you have to git first. In a terminal in the project folder:", "", "```bash", "git add -A && git commit -m \"Before network redesign\"", "```", "",
  "**Step 0.2** In the Unity scene, select the object with the CityManager and run **CityDataExporter > Export City Data Backup** (right-click the component header). That writes a new timestamped file in `Assets/CityBackups/`.", "",
  "**Step 0.3** Duplicate the route assets you are about to edit so you can roll back. In the Project window go to `Assets/ASSETS (1)/ROUTES/CBT/`, select all 22 `.asset` files, press **Ctrl+D** (Cmd+D on Mac), then move the copies into a new folder **outside** the ROUTES folder, e.g. `Assets/_BACKUP_ROUTES` (my atlas scripts read everything under ROUTES, so a backup left in there would be counted as extra routes). The backup copies must also never be added to the CityManager or BusScheduler route lists.", "",
  "**Step 0.4** Stop Play Mode before editing. Edits made in Play Mode are lost when you stop.", "",
  "**Step 0.5** The one-time editor setting from earlier: `Tools > Play Mode > Hold recompiles until I stop playing` is ticked. Leave it ticked.", "",
  "**Where things live (so you can find them fast)**", "")
table(["What", "Where in Unity"],
      [["Roads", "Select the CityManager object > Inspector > **Road Network** > `roadDefinitions` (a list). Each element is a `RoadSegmentDefinition`."],
       ["Stops", "Same object > **Bus Stops** > `stopDefinitions` (a list). Each element is a `BusStopData`."],
       ["Routes (assets)", "Project window: `Assets/ASSETS (1)/ROUTES/CBT/<number>.asset` (type: BusRouteData)."],
       ["Route lists", "CityManager > **Routes** (`routes` array) **and** the BusScheduler object > `managedRoutes` array. A new route (236) must be added to **both**."],
       ["Fleet", "Project window: `Assets/ASSETS (1)/DEPOTS/FleetRoster.asset` > `series` list."],
       ["Depots", "`Assets/ASSETS (1)/DEPOTS/<depot>.asset` > **Routes Served** (`servedRouteNumbers`)."]])
w("**Tools you can use (optional):** `Tools > Transit > Road Drawer` (click roads on the ground), `Tools > City Building > Bus Stop Maker`, and the in-game Route Maker (press **U** in Play Mode). This guide is for typing by hand, so you only need the Inspector.", "")

# ── PHASE 1 FLEET ─────────────────────────────────────────────────────────────────────────────────
h(2, "Phase 1: Fleet roster")
w("The roster is a list of *series*. A series is a block of fleet numbers with one bus type. **Do this phase first** so route assets can name the new series.", "",
  "**How the game decides which route may use a bus.** Every route asset has `allowedFleetSeries`, a list of *hundred-blocks* (typing `2400` allows fleet numbers 2400–2499). I checked all 22 routes: every one lists its blocks explicitly, and **none lists 2800 or 2900**. That is the trick: give the Express-Max buses block **2900**, the normal artics block **2800**, and only the routes you add them to can ever use them.", "",
  "(The existing **2700 Series** is the XDE40, fleet 2701–2750. That is why your new artics cannot be 'the 2700s'. Block 2900 keeps them clear. If you still want them called 2700 something, tell me and I will rewrite this; it costs you the clean reservation.)", "")
table(["", "Express-Max (25 buses)", "Normal artics (10 buses)"],
      [["seriesName", "`2900 Series (Artic)`", "`2800 Series (Artic)`"], ["startFleetNumber", "`2901`  (2901–2925)", "`2801`  (2801–2810)"],
       ["busCount", "`25`", "`10`"], ["busType", "`XHE60` (placeholder prefab until your new model exists)", "`XHE60`"], ["isArticulated", "tick (`1`)", "tick (`1`)"],
       ["modelYear", "`2028` (your choice)", "`2027`"], ["depotAllocations", "split the 25 between the depots that will run 236 and 240 (suggestion: the depots closest to University Park and Parkview)", "spread as you like"]])
w("**Step 1.1** Open `FleetRoster.asset`. At the bottom of `series`, **copy** the element `2500 Series (Artic)` (right-click the element name > Duplicate Array Element) twice.",
  "**Step 1.2** Edit the two new elements with the values in the table above. Leave every other field as the 2500 Series (Artic) had it (engine, transmission, fuel, wheel covers, etc.).",
  "**Step 1.3** In each `depotAllocations` row make sure the `busCount` values add up to the series `busCount` (25 and 10).", "",
  "> **Reservation rule, written once.** Max buses (block 2900) run only on routes whose `allowedFleetSeries` contains `2900`: that is 236 and 240 and nothing else. The 10 normal artics (block 2800) run only on routes whose list contains `2800`: you add that in Phase 4/5 (136 first).", "",
  "**STOP: save and test.** Press Play. In the main menu fleet list confirm the two new series exist and the counts are 25 and 10. Stop Play.", "")

# ── PHASE 2 ROADS ────────────────────────────────────────────────────────────────────────────────
h(2, "Phase 2: Roads (CityManager > Road Definitions)")
w("**How a road is typed.** In `roadDefinitions` each element has: `roadName`, `roadCode`, `roadWidth`, `isOneWay`, `reverseFlow`, `hasSidewalks`, `curveMode` (leave as **ControlPoints**), and `controlPoints` (a list of Vector3).",
  "- **2 points = a straight road.** Every new road below is 2 points (straight). A road with 3 points is a curve (quadratic), 4 points a cubic curve. **Do not type 4 points for a rectangle**: it becomes a bulging curve. Use separate straight roads.",
  "- Roads that cross an existing road make a junction on their own (`canHaveIntersection` is on by default). Tick nothing extra.",
  "- Control point order = the direction traffic runs on a one-way road, and the stops you place sit on the **right-hand side** of that direction.",
  "- **New roads you add go at the END of the list.** Never insert in the middle.", "")
h(3, "2A. Roads to EDIT (they already exist)")
w("The road list is a reorderable list with one **foldout arrow per road** (the title shows the road name). Click arrows until you find the `roadCode` in the heading below, then edit it. There is no search box, so work down the list once and tick off the edits.", "")
for code, name, what, old, new in G.ROAD_EDITS:
    w(f"- [ ] **{name}** (`roadCode {code}`): {what}")
    if old and new and code != "MRBD":
        table(["", "controlPoints (X, Y, Z)"], [["old", " ; ".join(v3(x, z) for x, z in old)], ["**new**", " ; ".join(v3(x, z) for x, z in new)]])
    if code == "MRBD":
        w("  The arc has 24 control points. Take them in order (index 0 first). **Type the NEW column over the old one**:", "")
        table(["idx", "old X", "Z (unchanged)", "**new X**"], [(i, o[0], o[1], n[0]) for i, (o, n) in enumerate(zip(old, new))])
w("")
h(3, "2B. Roads to REMOVE")
w("A road cannot be deleted from the list without shifting it, so you **neutralise** it instead (the roads below are only ever replaced by new ones):", "")
table(["roadCode", "name", "what to do"],
      [[f"`{c}`", n, "Select it. Set `roadWidth` to `0.1`, `canHaveIntersection` OFF, `hasSidewalks` OFF, `hideCenterLine` ON, and move **both** control points far away to `(0, 0, -20000)` and `(0, 0, -20010)` so it is out of the city. (If you prefer, delete the element: you will need to re-check the override list order; the neutralise method is safer.)"] for c, n in G.ROAD_REMOVE])
w("")
h(3, "2C. NEW roads to ADD")
w("For each row: click the small **+ at the bottom-right of the road list**, click the new element's foldout arrow, then type the values. Leave anything not mentioned at its default (Unity gives `roadWidth 7`, `canHaveIntersection` on, `hasSidewalks` on).", "")
by_area = {}
for r in G.NEWROADS: by_area.setdefault(r["area"], []).append(r)
TITLES = {"parkview": "PARKVIEW", "meridian": "MERIDIAN SQUARE (main)", "meridianb": "MERIDIAN SQUARE B", "berrel": "BERRELINGWAY NORTH", "southpier": "SOUTH PIER", "northbeach": "NORTH BEACH", "crestbury": "CRESTBURY",
          "southside": "SOUTHSIDE", "univpark": "UNIVERSITY PARK", "ext36": "36TH ST EAST EXTENSION", "nwpoint": "NW POINT", "zeroth": "0TH AV / 200TH ST", "valley": "VALLEY FIELDS", "sunset": "SUNSET POINT"}
tot = 0
for key in ["parkview", "meridian", "meridianb", "berrel", "southpier", "northbeach", "crestbury", "southside", "univpark", "ext36"]:
    if key not in by_area: continue
    w(f"#### {TITLES[key]}")
    for r in by_area[key]:
        length = sum(math.dist(p, q) for p, q in zip(r["pts"], r["pts"][1:])); tot += length
        w(f"- [ ] **{r['name']}**   ({length:.0f} m)")
        extra = []
        if r["oneway"]: extra.append("`isOneWay` **ON** (traffic runs from point 0 to point 1)")
        table(["field", "type this"], [["roadName", r["name"] + (f" ({r['ow_name']})" if r["ow_name"] else "")], ["roadCode", f"`{r['code']}`"], ["roadWidth", f"`{r['width']}`"],
                                       ["controlPoints", " ; ".join(f"point {i}: `{v3(x, z)}`" for i, (x, z) in enumerate(r["pts"]))], ["extras", "; ".join(extra) or "none"]])
w(f"**Total new road: {tot/1000:.1f} km.**", "")
w("> **Does the junction work?** After you add each road, press Play for a few seconds and look at where it crosses another road. You should see the procedural junction appear. If you see a gap or an overlap, nudge the end control point by a few metres.", "",
  "**STOP: save and test.** Play. Drive or fly the camera along each new road. Fix anything that looks broken before you place stops on it.", "")

# ── PHASE 3 STOPS ────────────────────────────────────────────────────────────────────────────────
h(2, "Phase 3: Stops (CityManager > Stop Definitions)")
w("A stop has: `stopCode`, `stopName`, `parentRoadCode`, `tValue` (0–1 along that road), `hasShelter`, `isTerminal`, `isLayover`, `isAccessible`.",
  "**tValue** is how far along the road the stop sits: 0 = at control point 0, 1 = at the last control point, 0.5 = halfway. For a straight 2-point road it is just the fraction of the length. The stop itself is drawn about half a road width to the **right** of the road line (in the control-point direction), so a stop looks like it is on the kerb. All values below were computed from the plan coordinates.", "",
  "**Rules from your naming convention:** terminals are ALL CAPS place names with `[P]` (pickup) and `[D]` (drop-off); numbered spare bays are `[2]`, `[3]` and so on; street stops are `W 333 AV & N 19 ST` style.",
  "**New stops go at the END of `stopDefinitions`** (click the small **+ at the bottom-right of the stop list**, open the new element with its foldout arrow), in the order below. New codes continue from `s0595`, so they are `s0596` onwards.", "")
h(3, "3A. NEW stops to add (39)")
newstops = [s for s in G.STOPREC.values() if s["kind"] == "NEW"]
table(["code", "stopName", "parentRoadCode", "tValue", "isTerminal", "isLayover", "hasShelter"],
      [(f"`{s['code']}`", s["name"], f"`{s['road']}` ({s['roadname']})", s["t"], "yes" if s["term"] else "no", "yes" if s["lay"] else "no", "yes") for s in sorted(newstops, key=lambda x: x["code"])])
w("Leave `isAccessible` ticked (default).", "")
h(3, "3B. MOVED stops (they keep their code, so no route list needs touching)")
w("Find each stop by its `stopCode`. Change only the fields shown. Where a new name is shown, type it as well.", "")
rows = []
for s in G.STOPREC.values():
    if s["kind"] != "MOVE": continue
    cur = G.STOPS.get(s["old"]); curname = cur["name"] if cur else "?"
    newname = "(keep)"
    if s["old"] == "s0034": newname = s["name"]
    if s["old"] == "s0332": newname = "UNIVERSITY PARK [1]"
    rows.append((f"`{s['old']}`", curname, newname, f"`{s['road']}` ({s['roadname']})", s["t"]))
table(["stopCode", "current name", "new name", "new parentRoadCode", "new tValue"], rows)
h(3, "3C. RETIRED stops (set to placeholder; do not delete)")
w("Every stop below is served ONLY by Route 136 or Route 29, and both are rewritten in full in Phase 5, so **you never have to hunt through other routes** to remove them: just leave them out of the new lists. For each stop in this list: set `stopName` to `placeholder`, set `parentRoadCode` to empty, `tValue` 0, untick `hasShelter`/`isTerminal`. Then remove its code from **every route's stop lists** (Phase 4 tells you which routes).", "",
  "**Cut because you asked for every 2nd stop on the corridor to go (22):**", "")
rows = [(f"`{c}`", G.STOPS[c]["name"], G.STOPS[c]["road"], ", ".join(G.serves(c))) for c in G.CROSSED]
table(["stopCode", "stopName", "road", "routes that listed it"], rows)
w("**Cut because Route 29 no longer goes to Parkview (2):**", "")
table(["stopCode", "why"], [[f"`{c}`", n] for c, n in G.RETIRE])
w("**STOP: save and test.** Play. Open the map and check: every new/moved stop sits on its road and on the right kerb; no stop is floating in a field. Fix tValues up or down by 0.01 until each is right.", "")

# ── PHASE 4: routes ──────────────────────────────────────────────────────────────────────────────
h(2, "Phase 4: Route assets, route by route")
w("**How a route is typed.** Open the asset (`ROUTES/CBT/<number>.asset`). The parts you will touch:", "",
  "- `outboundNodes` / `inboundNodes`: the path. Each node is a Vector3 `position` and an `isCurve` tick. **Outbound goes terminal A to terminal Z. Inbound goes Z to A.**",
  "- `outboundStops` / `inboundStops`: a list of `stopCode`, `minutesFromStart` and `isTimepoint`.",
  "- `terminalACode` / `terminalZCode` and `destinationNameOutbound` / `destinationNameInbound`.",
  "- `variants`: a list; each variant can override nodes, stops, schedule and vehicles. `overrideRoute` must be ON for a variant's node/stop lists to count.", "",
  "**The rule for terminals (important, you will see it in every section).** A bus does not teleport between the end of one trip and the start of the next. It carries on forward. So:",
  "1. The **arriving** trip's last node must be **at or past** its last stop in the direction of travel (otherwise the bus never reaches that stop).",
  "2. The **departing** trip's first node must be **at or before** its first stop in the direction of travel.",
  "3. The departing bay must be **further along the same loop** than the arriving bay. That is why the plan puts D before P on every terminal.", "",
  "**Reading the edit tables.** `#n` means the node number in the Inspector list (it starts at 0). 'Delete #a–#b' means remove those elements. Always re-count the numbers after a delete.", "")

ROUTE_STEPS = {}
def add(route, title, lines): ROUTE_STEPS.setdefault(route, []).append((title, lines))

# ---- Parkview ----
add("1", "PARKVIEW: leaving Parkview Commons (outbound)",
    ["Old outbound start: " + " ".join(nodeline(old_nodes("1", True), i) for i in range(0, 6)) + " (the long lap round the block).",
     "**Delete old #0 to #4 (five nodes)** and **type these four in their place** (so the bus goes south, east along 125th, then north up Midway):"])
table_p = [(0, -502, -1200), (1, -502, -1252), (2, 2, -1252), (3, 2, -1055)]
def tbl_xz(rows, head=("#", "X", "Y", "Z")): table(list(head), [(i, x, 0, z) for i, x, z in rows])
# Route 1/201 outbound
ROUTE_STEPS["1"][-1][1].append("RAW_TABLE:p1")
ROUTE_STEPS["1"][-1][1].append("The old #5 `(2, 537)` is now node #4 and carries on up Midway unchanged. Check there is no duplicate `(2, -1055)`.")
add("201", "PARKVIEW: leaving Parkview Commons (outbound)", ["Exactly the same edit as Route 1: old nodes #0–#4 are `(-502,-1180) (-502,-1252) (-378,-1252) (-378,-1055) (2,-1055)`. **Delete those five and type these four**:", "RAW_TABLE:p1", "Old #5 `(2, 537)` follows unchanged."])
for num in ("73", "87"):
    add(num, "PARKVIEW WEST: arriving at the B bays",
        ["Parkview B [D] (`s0406`) moves west to X=-790. The inbound route must reach it:"] +
        ([f"Inbound last node is #{len(old_nodes('73', False)) - 1} `(-760, -1048)`. **Change X to `-790`**: `(-790, 0, -1048)`."] if num == "73" else
         [f"Inbound last node is #{len(old_nodes('87', False)) - 1} `(-755, -1048)`. **Change it to** `(-790, 0, -1048)`.", f"Variant **A** inbound (`variants > A > inboundNodesOverride`): last node #{len(old_nodes('87', False, 'A')) - 1} `(-765, -1048)`. **Change it to** `(-790, 0, -1048)`."]) +
        ["The outbound first node is already at X ≈ -860 (`s0407`), so no change there."])
add("140", "PARKVIEW: inbound now comes down the new Midway stretch",
    ["**Outbound: no node change.** The old nodes already go straight along z=-1172 from the garage to x=402. That straight line was the grass; the new -117th St road now sits under it.",
     "**Inbound, change 2 nodes** (old list `(398,-1168) (-202,-1168) (-202,-1248) (-378,-1248) (-378,-1200)` is nodes #7–#11):",
     "Node #8: `(-202, -1168)` → **`(-2, 0, -1168)`**.", "Node #9: `(-202, -1248)` → **`(-2, 0, -1248)`**.", "Node #11 stays `(-378, -1200)`.",
     "**Stops, inbound:** the new Garage drop-off `s0596` sits on 125th just before the end. In the inbound stop list " + neighbours_text("140", False, (-300, -1250), "s0596") + "."])
add("240", "PARKVIEW: inbound now comes down the new Midway stretch",
    ["Same as Route 140. Outbound nodes: no change. **Inbound** nodes #8 `(-202,-1168)` → `(-2, 0, -1168)` and #9 `(-202,-1248)` → `(-2, 0, -1248)`.", "Stops inbound: in the inbound stop list " + neighbours_text("240", False, (-300, -1250), "s0596") + "."])
# ---- Meridian A ----
M1 = GR.meridian_leave(3032, int(GR.mp_x), "east"); M1w = GR.meridian_leave(3032, int(GR.mp_x), "west")
M2e = GR.meridian_leave(3062, int(GR.mp2_x), "east"); M2w = GR.meridian_leave(3062, int(GR.mp2_x), "west")
def meridian_note(): return f"Meridian's moved bays: **A drop-off `s0131` at X=-30**, **A pickup `s0135` at X={int(GR.mp_x)}**, **A2 drop-off `s0244` at X=-30**, **A2 pickup `s0245` at X={int(GR.mp2_x)}**."
for num, outi, ini, rowz, note in (("1", 8, (0, 1, 2), 3032, "east"), ("201", 16, (0, 1, 2), 3032, "east")):
    ov = old_nodes(num, True); iv = old_nodes(num, False)
    add(num, "MERIDIAN SQUARE (main): arrive at A and leave from the moved pickup bay",
        [meridian_note(), f"**Outbound (arrival):** last node #{outi} `(-20, 3032)`: **change to `(-34, 0, 3032)`** so the bus reaches the drop-off at X=-30.",
         f"**Inbound (departure):** old nodes #0–#2 are `{nodeline(iv, 0)[3:]} {nodeline(iv, 1)[3:]} {nodeline(iv, 2)[3:]}` (the old short arc). **Delete #0, #1, #2** and insert these nodes at the start, in order, then keep the old node #3 `(-5, 2994)` and everything after it:", "RAW_NODES:M1"])
for num, outlast, rowz, mk in (("87", None, 3032, "M1w"),):
    ov = old_nodes("87", True); iv = old_nodes("87", False)
    add("87", "MERIDIAN SQUARE (main): arrive at A and leave from the moved pickup bay",
        [meridian_note(), f"**Outbound:** last node #{len(ov) - 1} `(-20, 3032)` → **`(-34, 0, 3032)`**. **Variant A outbound** (`variants > A > outboundNodesOverride`): last node → **`(-34, 0, 3032)`**.",
         f"**Inbound:** old #0–#2 are `{nodeline(iv, 0)[3:]} {nodeline(iv, 1)[3:]} {nodeline(iv, 2)[3:]}`. **Delete #0–#2**, insert at the start (Route 87 turns WEST onto Leaf Blvd, so the last new node is at z=3003):", "RAW_NODES:M1w",
         "Keep the old #3 `(-702, 3005)` and everything after it. **Do the same for variant A inbound**: delete its #0–#2 `(-110,3032) (-147,3032) (-151,3005)` and insert the same list, keep its old #3 `(-698, 3005)`."])
add("101", "MERIDIAN SQUARE A2: leave from the moved A2 pickup bay",
    [meridian_note(), f"**Outbound (leaving):** old #0–#3 are `(-110,3062) (-139,3062) (-147,3032) (-151,3002)`. **Delete #0–#3**, insert (Route 101 goes WEST on Leaf Blvd):", "RAW_NODES:M2w", "Keep the old #4 `(-698, 3002)` and the rest.",
     "**Inbound (arrival):** last node #9 `(-20, 3062)` → **`(-34, 0, 3062)`**."])
for num in ("199", "299"):
    add(num, "MERIDIAN SQUARE A2: arrive at A2 and leave from the moved pickup bay",
        [meridian_note(), f"**Outbound (arrival):** last node #6 `(-20, 3062)` → **`(-34, 0, 3062)`**.", f"**Inbound (departure):** old #0–#3 are `{nodeline(old_nodes(num, False), 0)[3:]} (-139,3062) (-147,3032) (-151,3002)`. **Delete #0–#3**, insert:", "RAW_NODES:M2w", "Keep the old #4 `(-698, 3002)` and everything after it."])
LEAF_NOTE = "**Passing buses get the new Leaf Blvd through stops:** `LEAF BLVD & W 20 AV` eastbound is `s0597` (X=-200), westbound is `s0598`."
add("116", "MERIDIAN SQUARE: new Leaf Blvd through stops", [LEAF_NOTE, "No node change. **Outbound** (eastbound) stop list: insert `s0597` between the stop just west of Meridian and the one just east. **Inbound** (westbound): insert `s0598` likewise. See the neighbour table below this route's steps."])
add("216", "MERIDIAN SQUARE: new Leaf Blvd through stops", [LEAF_NOTE, "No node change. **Outbound**: insert `s0597` in the same place as route 116. **Inbound**: insert `s0598` in the same place as route 116."])
add("34", "MERIDIAN SQUARE: Variant A passes along Leaf Blvd", [LEAF_NOTE, "Only **variant A** passes Meridian. In `variants > A`: **outboundStopsOverride** insert `s0597`, **inboundStopsOverride** insert `s0598`, in the same place as route 116 (between the stops just before and after x=-200 on Leaf Blvd). Give each a `minutesFromStart` between its neighbours (use the average)."])
# ---- Meridian B ----
add("85", "MERIDIAN SQUARE B: one counter-clockwise lap, no hairpins",
    ["Old inbound end: `(-702, 4098) (-702, 3500) (-672, 3580)` (hairpin). Old outbound start: `(-672, 3840) (-698, 3838) (-698, 4102)`.",
     "**Inbound: delete #6, #7, #8** (the last three nodes) and **type these four** (south on -70th Av, east along the south side, north up the east side to D):", "RAW_TABLE:b85in",
     "**Outbound: delete #0, #1, #2** and **type these four at the start**:", "RAW_TABLE:b85out",
     "Stops: no list change. `s0478` (D, z=3600) and `s0479` (P, z=3800) are already last/first and now sit on the east kerb of the same side, D first and P further on, so the next trip starts ahead of where this one ended."])
# ---- Berrelingway North ----
add("114", "BERRELINGWAY NORTH: drop-off on 40th Av", ["No node change: 114 already comes up 40th Av, east on Leaf, south on 49th Av.", "Stops, **inbound**: the new `s0601` (BERRELINGWAY NORTH [D], on 40th Av at z=2960): " + neighbours_text("114", False, (400, 2960), "s0601") + "."])
add("116", "BERRELINGWAY NORTH: nothing to do", ["116 still arrives along Leaf Blvd and loads at the Local stop. No node or stop edits."])
add("216", "BERRELINGWAY NORTH: nothing to do", ["Same as 116. No edits."])
for num, x, ox in (("140", 402, 402), ("240", 442, 438)):
    ov, iv = old_nodes(num, True), old_nodes(num, False)
    add(num, "BERRELINGWAY NORTH: one lap, ends at the Station bay on the connector",
        [f"Old outbound end: `{' '.join(nodeline(ov, i)[3:] for i in range(len(ov) - 3, len(ov)))}` (nodes #{len(ov)-3}–#{len(ov)-1}). Old inbound start (#0–#6): `{' '.join(nodeline(iv, i)[3:] for i in range(0, 7))}` (the double lap).",
         f"**Outbound: delete the last 3 nodes** `({x}, 2995) (488, 2995) (488, 2970)` and **append**:", "RAW_TABLE:be_out" + num, f"**Inbound: delete #0–#6** and **insert at the start**:", "RAW_TABLE:be_in" + num, f"Keep the old inbound #7 `({ox - 4 if num == '140' else 438}, -1168)` and everything after it.",
         "**Terminal and stops:** `terminalZCode` = `s0089` (was `s0153`). Outbound last stop: replace `s0153` with `s0089`. Inbound first stop: replace `s0153` with `s0089`. Station [2] `s0602` is a spare bay, not in any list."])
# ---- South Pier ----
add("21", "SOUTH PIER: bays on the -190th St / Platform Road loop", ["Old outbound start: `(1698,-1960) (1698,-2000) (1992,-2000)`. **Delete #0–#2**, insert these two at the start (21 loads at P1 `s0247` on the new -193rd St, then turns north onto 199th Av):", "RAW_TABLE:sp21out",
    "Old inbound end: `(1988,-1900) (1900,-1905)` (#16, #17). **Change #16 to `(1988, 0, -1905)` and #17 to `(1850, 0, -1905)`** (the drop-off `s0246` moved to X=1850)."])
for num, p, d, px, dx in (("199", "s0605", "s0603", 1860, 1930), ("299", "s0606", "s0604", 1930, 1790)):
    ov, iv = old_nodes(num, True), old_nodes(num, False)
    add(num, f"SOUTH PIER: own bays P `{p}` and D `{d}`", [f"Old outbound start `{nodeline(ov, 0)[3:]} {nodeline(ov, 1)[3:]} {nodeline(ov, 2)[3:]}`. **Delete #0–#2** and insert: `(0) ({px}, 0, -1937)` then `(1) (1992, 0, -1937)`.",
        f"Old inbound end `({round(iv[-2][0][0])}, {round(iv[-2][0][1])}) ({round(iv[-1][0][0])}, {round(iv[-1][0][1])})` (the last two nodes). **Change them to** `({1988}, 0, -1905)` and `({dx}, 0, -1905)`.",
        f"**Terminals and stops:** `terminalACode` = `{p}`. Outbound **first stop** `s0247` → `{p}`. Inbound **last stop** `s0246` → `{d}`."])
add("45", "SOUTH PIER: 45 gets its own bays on -200th St; NORTH BEACH: Beach Mall", ["Old outbound start `(1698,-1940) (1698,-2002)`. **Delete #0–#1** and insert these five at the start:", "RAW_TABLE:sp45out",
    "Old inbound end `(2500,-1999) (1992,-1998) (1992,-1900) (1900,-1900)` (#22–#25). **Delete #23, #24, #25** and add `(1940, 0, -1998)` as the new last node.",
    "**Terminals and stops:** `terminalACode` = `s0609`. Outbound first stop `s0247` → `s0609`. Inbound last stop `s0246` → `s0607`.",
    "**Sunset Point:** no change. 45 keeps `s0562 s0563 s0564` (the straight is now also the bays for 136 and 236)."])
add("45", "NORTH BEACH: arrive and leave on the Beach Mall (westbound)",
    ["Old outbound end: `(2995,3002) (2952,3002) (2952,3100)` (#27–#29). **Delete #29**, then **append**: `(2952, 0, 3205)` and `(2865, 0, 3205)`. Stops: `s0512` stays last (it moved to X=2865).",
     "Old inbound start: `(2952,3090) (2952,3398) (2985,3398)` (#0–#2). **Delete #0 and #1**, **insert at the start**: `(2865, 0, 3205)`, `(2802, 0, 3205)`, `(2802, 0, 3398)`. Keep old #2 `(2985, 3398)`."])
add("114", "NORTH BEACH: no more U-turn",
    ["Old outbound end: `(2995,3002) (2952,3002) (2952,3110)` (#12–#14). **Delete #14**, **append**: `(2952, 0, 3205)` and `(2865, 0, 3205)`.",
     "Old inbound start: `(2948,3080) (2952,3010) (2952,3398) (2985,3398)` (#0–#3). **Delete #0, #1, #2** (this is the U-turn), **insert at the start**: `(2865, 0, 3205)`, `(2802, 0, 3205)`, `(2802, 0, 3398)`. Keep old #3 `(2985, 3398)`."])
add("34", "NORTH BEACH: 34 / 34A reversed to run the Beach Mall westbound",
    ["34 now loads on the **same side and direction as 45 and 114**: east along 340th, **south down 295th Av**, **west along the Beach Mall**, north up 280th Av, back west on 340th.",
     "Mainline **inbound** end: old `(-7502,3398) (2988,3398) (2988,3200)` (#2–#4). **Change #3 to `(2948, 0, 3398)`**, **delete #4**, then **append**: `(2948, 0, 3205)` and `(2905, 0, 3205)` (end at the new D bay `s0511` at X=2905).",
     "Mainline **outbound** start: old `(2952,3050) (2952,3402) (-7698,3402)` (#0–#2). **Delete #0 and #1**, **insert at the start**: `(2865, 0, 3205)`, `(2802, 0, 3205)`, `(2802, 0, 3402)`. Keep old #2.",
     "**Variant A**: do the **same two edits** inside `variants > A`: `inboundNodesOverride` (old end `(2988,3398) (2988,3200)`, #7–#8: change #7 to `(2948,0,3398)`, delete #8, append the two nodes) and `outboundNodesOverride` (old start `(2952,3050) (2952,3402) (-702,3402)`: delete #0–#1 and insert the three nodes).",
     "**Stops:** the new drop-off on 295th, `s0612` (z=3300), goes **in the inbound list immediately before `s0511`** (and the same inside variant A's inbound stops)."])
# ---- Crestbury ----
add("21", "CRESTBURY: the shared Terminal Mall (-65th St, westbound)", ["Old outbound end `(-3702,-110) (-3702,-608) (-3726,-608) (-3726,-630)` (#14–#17). **Delete #15, #16, #17** and **append**:", "RAW_TABLE:cr21out",
    "Old inbound start `(-3730,-600) (-3726,-702) (-3698,-702) (-3698,-112)` (#0–#3). **Delete #0, #1, #2** and **insert at the start**: `(-3560, 0, -652)`, `(-3698, 0, -652)`. Keep old #3 `(-3698, -112)`.", "Stops: none (`s0092` just moved to X=-3560)."])
for num in ("116", "216"):
    ov, iv = old_nodes(num, True), old_nodes(num, False)
    add(num, "CRESTBURY: the shared Terminal Mall",
        [f"Old outbound start `{nodeline(ov, 0)[3:]} {nodeline(ov, 1)[3:]} {nodeline(ov, 2)[3:]}`. **Delete #0 and #1** and **insert at the start**: `(-3670, 0, -652)`, `(-3702, 0, -652)`, `(-3702, 0, -702)`. Keep old #2 `(-2158, -702)`.",
         f"Old inbound end `(-3698,-698) (-3698,-608) (-3726,-608) (-3726,-630)` (#{len(iv)-4}–#{len(iv)-1}). **Delete those four** and **append**: `(-3478, 0, -698)`, `(-3478, 0, -652)`, `(-3620, 0, -652)`.",
         "**Terminals and stops:** `terminalACode` = `s0613`. Outbound first stop `s0092` → `s0613`. Inbound last stop: replace the old last stop with `s0099` (`CRESTBURY TERMINAL [DROPOFF]`, now on the Mall at X=-3620)."])
# ---- NW Point ----
add("25", "NW POINT: bay on 230th Av", ["`s0184` moved to z=2150. **Outbound:** node #0 `(-2305, 2120)` → **`(-2302, 0, 2150)`**; node #1 `(-2300, 1998)` → **`(-2302, 0, 1998)`**.",
    "**Inbound:** node #12 `(-2300, 2220)` → **`(-2302, 0, 2218)`**; node #13 `(-2300, 2090)` → **`(-2302, 0, 2150)`**.", "Stops: no change."])
# ---- Southside ----
for num in ("7", "10"):
    ov = old_nodes(num, True)
    add(num, "SOUTHSIDE: P moved back 55 m off the junction", [f"`s0292` (P) is now at z=-2640 and `s0291` (D) at z=-2540. Outbound node #0 `{nodeline(ov, 0)[3:]}` → **`(934, 0, -2640)`**." + (" Leave the rest." if num == "10" else " Node #1 `(929,-2698)` stays: the bus turns west onto -270th St there."), "Inbound: no change (it already ends at z=-2600, before P)."])
# ---- University Park ----
add("7", "UNIVERSITY PARK: bay [4] on Campus Drive", ["`terminalZCode` = `s0621` (UNIVERSITY PARK [4]). In the outbound list replace the last stop `s0332` with `s0621`; in the inbound list replace the first stop `s0332` with `s0621`.",
    "**Outbound (arrival):** old last node #2 `(-2910, -2698)`. **Delete #2** and **append**: `(-2965, 0, -2698)`, `(-2965, 0, -2608)`, `(-2810, 0, -2608)`.",
    "**Inbound (departure):** old #0–#8 are the loop around the old block. **Delete #0–#8** (9 nodes) and **insert at the start**: `(-2810, 0, -2608)`, `(-2774, 0, -2608)`, `(-2774, 0, -2712)`. Keep the old #9 `(1002, -2712)` and the rest."])
# ---- Zeroth ----
add("109", "0TH AV / 200TH ST: pickup moves west of -10th Av", ["`s0374` (D) nudged to X=-30, `s0375` (P) to X=-80, new `[3]` `s0622` at X=-200.", "Outbound: no change.", "**Inbound:** node #0 `(-25, 2030)` → **`(-200, 0, 2036)`**; node #1 `(-70, 2032)` → **`(-240, 0, 2034)`**.", "Stops: in the inbound list replace the first stop `s0375` with `s0622`. `terminalZCode` stays `s0374`."])
add("10", "0TH AV / 200TH ST: nothing to type", ["Route 10 already turns round on -10th Av. Only the stop positions changed (Phase 3). **No node, no stop-list edits.**"])
# ---- Valley ----
for num in ("73", "85"):
    ov, iv = old_nodes(num, True), old_nodes(num, False)
    add(num, "VALLEY FIELDS: stops closer together", ["`s0476` (D) is now at z=-1230, `s0477` (P) at z=-1130, both on -750th Av.", f"**Outbound:** last node #{len(ov) - 1} `(-7498, -1250)` → **`(-7498, 0, -1225)`** (so the bus reaches D).", "**Inbound:** first node #0 `(-7498, -1100)` → **`(-7498, 0, -1140)`** (so the bus starts before P).", "Stops: no list change."])

ORDER = ["1", "201", "7", "10", "21", "25", "29", "34", "45", "73", "85", "87", "101", "109", "114", "116", "136", "140", "199", "216", "240", "299"]
NODE_TABLES = {
 "p1": ("#", [(0, -502, -1200), (1, -502, -1252), (2, 2, -1252), (3, 2, -1055)]),
 "b85in": ("#", [(6, -702, 4098), (7, -702, 3498), (8, -536, 3498), (9, -536, 3604)]),
 "b85out": ("#", [(0, -536, 3796), (1, -536, 3902), (2, -698, 3902), (3, -698, 4102)]),
 "be_out140": ("#", [(3, 402, 2898), (4, 485, 2898)]), "be_out240": ("#", [(3, 442, 2898), (4, 485, 2898)]),
 "be_in140": ("#", [(0, 485, 2898), (1, 556, 2898), (2, 556, 2793), (3, 398, 2793)]), "be_in240": ("#", [(0, 485, 2898), (1, 556, 2898), (2, 556, 2793), (3, 438, 2793)]),
 "sp21out": ("#", [(0, 1790, -1937), (1, 1992, -1937)]),
 "sp45out": ("#", [(0, 1780, -1998), (1, 1702, -1998), (2, 1702, -1937), (3, 1988, -1937), (4, 1988, -2002)]),
 "cr21out": ("#", [(15, -3702, -612), (16, -3484, -612), (17, -3484, -652), (18, -3560, -652)]),
}
RAW = {"M1": M1, "M1w": M1w, "M2w": M2w, "M2e": M2e}
def emit_steps(num):
    items = ROUTE_STEPS.get(num, [])
    for title, lines in items:
        w(f"**{title}**", "")
        for ln in lines:
            if ln.startswith("RAW_TABLE:"):
                key = ln.split(":")[1]; head, rows = NODE_TABLES[key]; table(["#", "X", "Y", "Z"], [(i, x, 0, z) for i, x, z in rows])
            elif ln.startswith("RAW_NODES:"):
                nodes = RAW[ln.split(":")[1]]; table(["#", "X", "Y", "Z"], [(i, n[0], 0, n[1]) for i, n in enumerate(nodes)])
            else: w("- [ ] " + ln)
        w("")
SHORT = {"1": ("Parkview Commons", "Meridian Square"), "201": ("Parkview Commons", "Meridian Square")}
h(3, "Route summary: what each of the 22 routes needs")
_rows = []
for _n in ORDER:
    if _n in ("29", "136"): _rows.append((_n, "**rewritten in full** (Phase 5)", "5D / 5A (+ 5B night variant for 136)")); continue
    _t = [t for t, _ in ROUTE_STEPS.get(_n, [])]
    if _n == "240": _t.append("Express-Max conversion (5E)")
    _rows.append((_n, "; ".join(_t) if _t else "none", "Phase 4" + (" + 5E" if _n == "240" else "")))
_rows.append(("236", "**new route**", "5C"))
table(["route", "what changes", "where"], _rows)
w("**Suggested order inside Phase 4:** do the terminal you are testing first (all its routes), then the next terminal. Within a terminal: roads and stops are already done, so just edit the routes listed in Phase 7 for that terminal.", "")
for num in ORDER:
    h(3, f"Route {num}")
    if num in ("29", "136", "236"):
        w(f"> **Route {num} is rewritten or converted in full in Phase 5. Do that section instead of anything here.**", "")
        continue
    if num not in ROUTE_STEPS: w("No edits.", ""); continue
    emit_steps(num)
    if num in ("116", "216", "34"):
        r = R[num]
        w("**Where exactly do `s0597` (eastbound) and `s0598` (westbound) go?**", "")
        for tag, outb, newcode, xy in (("Outbound (eastbound on Leaf Blvd)", True, "s0597", (-200, 3008)), ("Inbound (westbound on Leaf Blvd)", False, "s0598", (-200, 2992))):
            lst = (r["variants"][0]["outStops"] if outb else r["variants"][0]["inStops"]) if num == "34" else (r["outStops"] if outb else r["inStops"])
            nodes = old_nodes(num, outb, "A") if num == "34" else old_nodes(num, outb)
            pts = [(p[0], p[1]) for p, c in nodes]
            pars = [GR.proj_along(pts, GR.stop_xy(c)) for c in lst]; p = GR.proj_along(pts, xy); i = 0
            while i < len(lst) and pars[i] < p: i += 1
            a_ = lst[i-1] if i > 0 else None; b_ = lst[i] if i < len(lst) else None
            where = f"between `{a_}` ({sname(a_)}) and `{b_}` ({sname(b_)})" if a_ and b_ else ("at the start of the list" if a_ is None else "at the end of the list")
            w(f"- {tag}: insert `{newcode}` {where}.")
        w("")

# ── PHASE 5 ──────────────────────────────────────────────────────────────────────────────────────
h(2, "Phase 5: The big routes in full")
h(3, "5A. Route 136: Crestbury to Sunset Point (the full-length local)")
w("**What it is now:** the old route was NW Point to the 1000 Exchange (38 stops). The new route runs **Crestbury to Sunset Point**, about 11.5 km one way, 31 stops, model trip about 49 minutes.", "",
  "**Copy sources (open these assets side by side, they save you typing):**",
  "- The **Boolean Way / 36th St stretch** (`(-3332, 358)` to `(968, 358)`) is already in the **current 136** outbound nodes (#3 to #4) and inbound nodes (#4 to #3). Keep it.",
  "- The **299th Av southbound stretch** `(2985, 0)` to `(3012, -1304)`: copy from **Route 45 inbound nodes #4–#14** (they are the southbound lane).",
  "- The **299th Av stretch from z=-1304 to z=-2000**: copy from **Route 45 outbound nodes #10–#14**, *reversed*, and **subtract 8 from every X** (45's outbound is the other lane).",
  "- The **Sunset Point loop** (curve, 350th Av, straight): Route 45 outbound nodes #4–#8 drive the same loop. Easiest: type the table below (its curve nodes sit on the real curved -200th St), or copy 45's #4–#8 and then change the last node to `(3150, -1998)`.",
  "- The **Crestbury end** copies **Route 21's** end: 21 outbound nodes #14–#17 and inbound #0–#3 are the old stub approach, replaced by the new Mall nodes in the table below.", "")
w("#### 136: fields to type", "")
table(["field", "value"],
      [["routeNumber", "`136` (unchanged)"], ["routeName", "`Crestbury - Sunset Point`"], ["routeColor", "keep (or pick a new one)"], ["terminalACode", f"`{GR.C('cr_4')}` (CRESTBURY [4])"],
       ["terminalZCode", "`s0563` (SUNSET POINT [D])"], ["destinationNameOutbound", "`Sunset Point`"], ["destinationNameInbound", "`Crestbury`"], ["routeQualifierOutbound / Inbound", "blank"],
       ["articulatedPolicy", "`Preferred`  (was Allowed: now it picks the 10 normal artics first)"], ["allowedFleetSeries", "**keep the existing list** (1100, 1400, 1500, 1600, 1900, 2000, 2200, 2400, 2500, 2700) **and add `2800`**"],
       ["maxBusesAllowed", "`8`  (15-min peak needs 8)"], ["oneWayTripMinutes", "`50`"], ["operatingStartMinutes / EndMinutes", "`240` / `1440`  (4:00 to 24:00; Variant N covers 0:00–4:00)"],
       ["nightFleetSeries", "leave as it is (1000, 1700, 1900). It already covers the 4:00–5:00 hour of the mainline."]])
w("**Schedule:** right-click the asset's title bar > **Templates > Apply Non-24hr (4am-12am)**. Then in `scheduleWindows` set (suggested; change to taste):", "")
table(["label", "start", "end", "headwayFromA", "headwayFromZ", "Trip %"],
      [["Early", 240, 390, 30, 30, 90], ["AM Peak", 390, 540, 15, 15, 105], ["Midday", 540, 930, 20, 20, 100], ["PM Peak", 930, 1110, 15, 15, 105], ["Evening", 1110, 1440, 30, 30, 90]])
w("#### 136 outbound nodes (Crestbury to Sunset Point): delete the whole old list and type these 30", "")
nodes_table(GR.NODES_136_OUT)
w("#### 136 inbound nodes (Sunset Point to Crestbury): delete the whole old list and type these 31", "")
nodes_table(GR.NODES_136_IN)
mt = GR.minutes_table(GR.NODES_136_OUT, GR.S136_OUT, 50)
w("#### 136 outbound stops", "")
table(["#", "stopCode", "stopName", "minutesFromStart", "isTimepoint"], [(i, f"`{c}`", sname(c), m, "yes" if tp else "") for i, (c, m, tp) in enumerate(mt)])
mt = GR.minutes_table(GR.NODES_136_IN, GR.S136_IN, 50)
w("#### 136 inbound stops", "")
table(["#", "stopCode", "stopName", "minutesFromStart", "isTimepoint"], [(i, f"`{c}`", sname(c), m, "yes" if tp else "") for i, (c, m, tp) in enumerate(mt)])
w("> The old `s0032` (AIRPORT STATION) stays in the **inbound** list only, as before.", "")

h(3, "5B. Variant N (shows on boards as 136N): the night pattern for 136")
w("**What the game does with a variant letter.** The route number and the letter are simply glued together on the boards: variant letter `N` on route 136 shows as **`136N`**. (That is how `34A` and `87A` already work.) If you want it to read **`N136`** with the N *first*, you would have to make a separate route asset with `routeNumber = N136`; I recommend the variant (below) because buses, stops and the timetable stay linked to 136. Tell me if you want the separate-asset method written out instead.", "",
  "**Goal:** from 0:00 to 4:00 the 136 runs every 60 minutes as a thinned pattern. Mainline 136 covers 4:00–24:00.", "",
  "**Step N.1** Open `ROUTES/CBT/136.asset`. In **Variant Configurations**, right-click the `variants` header > **Add Short Turn Variant** is NOT what you want. Instead press **+** at the bottom of the `variants` list. A new element appears.",
  "**Step N.2** Type into the new element:", "")
table(["field", "value"],
      [["variantLetter", "`N`"], ["isShortTurn", "OFF"], ["overrideRoute", "**ON** (needed for the stop lists below to count)"], ["outboundNodesOverride / inboundNodesOverride", "**leave EMPTY**. Empty means 'use the mainline path'. Do not copy the nodes."],
       ["terminalACodeOverride / terminalZCodeOverride", "leave blank (same terminals)"], ["destinationNameOutboundOverride / InboundOverride", "leave blank (or `Sunset Point (Night)` / `Crestbury (Night)` if you want that on the board)"],
       ["overrideSchedule", "**ON**"], ["operatingStartMinutes", "`0`"], ["operatingEndMinutes", "`240`"], ["headwayFromAMinutes / headwayFromZMinutes", "`60` / `60`"], ["oneWayTripMinutes", "`42`  (night roads are emptier; 49 min x 0.85)"],
       ["scheduleWindows", "press + once: label `Night`, windowStartMinutes `0`, windowEndMinutes `240`, headwayFromA `60`, headwayFromZ `60`, tripTimeMultiplierPercent `85`, departureOffsetMinutes `0`"],
       ["overrideVehicleRestrictions", "**ON**"], ["articulatedPolicyOverride", "`Prohibited`  (the 10 normal artics rest at night)"], ["allowedFleetSeriesOverride", "`1000` and `1900` (136's own night list is 1000, 1700, 1900; 1700 is an artic block so it is left out here)"]])
w("**Step N.3** Type the night stop lists (these skip the stops the mainline stops at). **outboundStopsOverride**:", "")
mt = GR.minutes_table(GR.NODES_136_OUT, GR.SN_OUT, 42)
table(["#", "stopCode", "stopName", "minutesFromStart", "isTimepoint"], [(i, f"`{c}`", sname(c), m, "yes" if tp else "") for i, (c, m, tp) in enumerate(mt)])
w("**inboundStopsOverride**:", "")
mt = GR.minutes_table(GR.NODES_136_IN, GR.SN_IN, 42)
table(["#", "stopCode", "stopName", "minutesFromStart", "isTimepoint"], [(i, f"`{c}`", sname(c), m, "yes" if tp else "") for i, (c, m, tp) in enumerate(mt)])
w("Skipped at night: " + fmt_codes(sorted(GR.NIGHT_SKIP_OUT)) + ". (Change this list freely: add or remove a stop code and keep the order.)", "",
  "**Step N.4** The mainline must not also run 0:00–4:00. On the **mainline** set `operatingStartMinutes = 240` (done above). Check **Show Route Fleet Requirements** (right-click the asset) and confirm 136 and 136N do not overlap in time.",
  "**Step N.5** Test: set the sim clock to 00:30 and open the dispatch list. You should see **136N** departures every 60 minutes and no plain 136.", "")

h(3, "5C. Route 236 Express-Max: University Park to Sunset Point (limited stop)")
w("**Create the asset.** Select `136.asset` **after** you have finished 136 and 136N. Press **Ctrl+D**. Rename the copy `236.asset`. Open it and delete the `variants` entry (236 has no night pattern). Then type:", "")
table(["field", "value"],
      [["routeId", "`236`  (unique; never change later)"], ["routeNumber", "`236`"], ["routeName", "`University Park - Sunset Point (Express-Max)`"], ["routeColor", "orange, e.g. R 0.95  G 0.50  B 0.00"],
       ["articulatedPolicy", "`Mandatory`"], ["miniBusPolicy", "`Prohibited`"], ["allowedFleetSeries", "**replace the whole copied list with just `2900`** (only the Express-Max buses)"], ["nightFleetSeries", "**empty** (236 does not run 0:00–4:00; an empty night list means normal rules apply, so the 4:00–5:00 hour still works)"],
       ["maxBusesAllowed", "`7`  (6 for a 20-minute peak + 1 spare)"], ["terminalACode", "`s0332` (UNIVERSITY PARK [1])"], ["terminalZCode", f"`{GR.C('sun_2')}` (SUNSET POINT [2])"],
       ["destinationNameOutbound", "`Sunset Point`"], ["destinationNameInbound", "`University Park`"], ["routeQualifierOutbound / Inbound", "`MAX` / `MAX`"], ["oneWayTripMinutes", "`45`"],
       ["operatingStartMinutes / EndMinutes", "`240` / `1440`"]])
w("**Schedule:** Templates > **Apply Non-24hr (4am-12am)**, then edit `scheduleWindows`: ", "")
table(["label", "start", "end", "headwayFromA", "headwayFromZ", "Trip %"], [["Early", 240, 390, 30, 30, 90], ["AM Peak", 390, 600, 20, 20, 105], ["Midday", 600, 930, 30, 30, 100], ["PM Peak", 930, 1170, 20, 20, 105], ["Evening", 1170, 1440, 30, 30, 90]])
w("**Outbound nodes (University Park to Sunset Point).** *Copy source:* nodes #13 to the end (`(2985, 358)` and everything after: 36th St east, 299th Av south, the Sunset loop) are **identical to Route 136 outbound nodes #8 to #29**. Type #0–#12 yourself, then copy 136's #8–#29 after them:", "")
nodes_table(GR.NODES_236_OUT)
w("**Inbound nodes (Sunset Point to University Park):**", "")
nodes_table(GR.NODES_236_IN)
w("**Outbound stops (12):**", "")
mt = GR.minutes_table(GR.NODES_236_OUT, GR.S236_OUT, 45); table(["#", "stopCode", "stopName", "minutesFromStart", "isTimepoint"], [(i, f"`{c}`", sname(c), m, "yes" if tp else "") for i, (c, m, tp) in enumerate(mt)])
w("**Inbound stops (12):**", "")
mt = GR.minutes_table(GR.NODES_236_IN, GR.S236_IN, 45); table(["#", "stopCode", "stopName", "minutesFromStart", "isTimepoint"], [(i, f"`{c}`", sname(c), m, "yes" if tp else "") for i, (c, m, tp) in enumerate(mt)])
w("**Register 236** (see Phase 6): add it to CityManager > Routes, BusScheduler > managedRoutes, and the Routes Served list of the depots that will hold the Max buses.", "")

h(3, "5D. Route 29: University Park to NW Point")
w("**What changed:** 29 used to run Parkview A to University Park. It now runs **University Park to NW Point**. **Do not run at night**: set `operatingStartMinutes = 300` (5:00), `operatingEndMinutes = 1440`, and leave `nightFleetSeries` empty.", "",
  "**Copy sources:** the **-297th Av northbound stretch** is in the **current 29 inbound** nodes (#7–#9). The **NW leg** (-333rd Av north, 200th St, -216th Av, 222nd St) is the **old 136**: outbound nodes #1–#3 and inbound #4–#9 describe exactly that loop. You can copy them, then retype the lane offsets from the tables below.", "")
table(["field", "value"],
      [["terminalACode", f"`{GR.C('up_2')}` (UNIVERSITY PARK [2])"], ["terminalZCode", f"`{GR.C('nw_2')}` (NW POINT [2])"], ["destinationNameOutbound", "`NW Point`"], ["destinationNameInbound", "`University Park`"],
       ["articulatedPolicy", "`Allowed`"], ["oneWayTripMinutes", "`30`"], ["operatingStartMinutes / EndMinutes", "`300` / `1440`"], ["maxBusesAllowed", "`5`"]])
w("**Outbound nodes (University Park to NW Point), delete the old list and type:**", ""); nodes_table(GR.NODES_29_OUT)
w("**Inbound nodes (NW Point to University Park):**", ""); nodes_table(GR.NODES_29_IN)
w("**Outbound stops (21):**", ""); mt = GR.minutes_table(GR.NODES_29_OUT, GR.S29_OUT, 30); table(["#", "stopCode", "stopName", "minutesFromStart", "isTimepoint"], [(i, f"`{c}`", sname(c), m, "yes" if tp else "") for i, (c, m, tp) in enumerate(mt)])
w("**Inbound stops (21):**", ""); mt = GR.minutes_table(GR.NODES_29_IN, GR.S29_IN, 30); table(["#", "stopCode", "stopName", "minutesFromStart", "isTimepoint"], [(i, f"`{c}`", sname(c), m, "yes" if tp else "") for i, (c, m, tp) in enumerate(mt)])
w("Note the stops 29 **no longer serves** because it left Parkview and the -70th St corridor: `s0405`, `s0069`*, `s0093`, `s0094`, `s0095`, `s0096`*, `s0097`* (the starred ones are still served by other routes; `s0093`, `s0094`, `s0095` become unserved).", "")

h(3, "5E. Route 240: convert to Express-Max (route stays the same)")
w("240 keeps its Parkview Garage to Berrelingway North path (see Route 240's Parkview and Berrelingway edits in Phase 4, **do those first**). Then change only:", "")
table(["field", "value"], [["articulatedPolicy", "`Mandatory` (it already is)"], ["miniBusPolicy", "`Prohibited`"], ["allowedFleetSeries", "**replace** the current list (1600, 2200, 2400, 2700) with just `2900`"], ["routeQualifierOutbound / Inbound", "`MAX` / `MAX`"],
                           ["maxBusesAllowed", "`6`"], ["scheduleWindows", "Peak headway 10–15 min; your choice. (One-way trip is about 19 min, so a 10-min peak needs about 6 buses.)"], ["nightFleetSeries", "leave empty if it does not run 0:00–4:00"]])
w("If the asset was disabled ('revived'), also check it is in **both** route lists (Phase 6).", "")

# ── PHASE 6 ──────────────────────────────────────────────────────────────────────────────────────
h(2, "Phase 6: Registration lists, depots and fleet limits")
w("- [ ] **Give the 10 normal artics (block 2800) to their routes.** In each of these assets add `2800` to `allowedFleetSeries` (keep the rest): **136** (done in 5A), **34, 73, 1, 201, 45, 199, 109, 10**. Pick other routes if you like: only routes whose `articulatedPolicy` is Allowed (1) or Preferred (2) can use artics (21 and 85 are Prohibited).", "- [ ] **CityManager > Routes** (`routes`): add `236.asset` at the end. (Check `240.asset` is in the list.)", "- [ ] **BusScheduler > managedRoutes**: add `236.asset` at the end. **Do not add a route twice**: the game de-duplicates, but keep the list clean.",
  "- [ ] **Depots**: open every depot asset that will hold Express-Max buses (the depots you allocated the 25 buses to). In `servedRouteNumbers` add `236` and `240`. Also add `136` to any depot holding the normal artics.",
  "- [ ] **Route 29** was served by some depots with `29` in `servedRouteNumbers`: that stays. Nothing to change.", "")
w("**STOP: save and test.** Press Play. Open the route list in the main menu. Check 236 appears, shows `MAX`, and lists 6–7 buses at the 20-minute peak. Check 29 shows University Park to NW Point.", "")

# ── PHASE 7 terminals ───────────────────────────────────────────────────────────────────────────
h(2, "Phase 7: Terminal-by-terminal checklist")
w("Everything a terminal needs, gathered so you can finish one terminal at a time and test it. For each: roads (Phase 2), stops (Phase 3), routes (Phase 4).", "")
TERM = [("PARKVIEW", "parkview", ["1", "201", "73", "87", "140", "240"], "29 left; Parkview A stops s0404/s0405 retired"),
        ("MERIDIAN SQUARE (main)", "meridian", ["1", "201", "87", "101", "199", "299", "116", "216", "34"], "arc moved west 400 m; rows 303rd/306th/309th"),
        ("MERIDIAN SQUARE B", "meridianb", ["85"], "rectangular lot, one counter-clockwise lap"),
        ("BERRELINGWAY NORTH", "berrel", ["114", "116", "216", "140", "240"], "only 114, 140 and 240 change"),
        ("SOUTH PIER", "southpier", ["21", "45", "199", "299"], "-193rd St platform road, separate bays per route"),
        ("NORTH BEACH", "northbeach", ["34", "45", "114"], "Beach Mall westbound; 34 reversed; 114 U-turn removed"),
        ("CRESTBURY", "crestbury", ["21", "116", "216", "136"], "one shared Mall; 136 now starts here"),
        ("NW POINT", "nwpoint", ["25", "29"], "no new road; -216th Av is the east side"),
        ("SOUTHSIDE", "southside", ["7", "10"], "P moved back off the junction"),
        ("UNIVERSITY PARK", "univpark", ["7", "29", "236"], "Art Blvd curve gone; hub Campus Drive"),
        ("0TH AV / 200TH ST", "zeroth", ["10", "109"], "203rd St widened to 7 m; 109 pickup west of -10th Av"),
        ("VALLEY FIELDS", "valley", ["73", "85"], "stops 50 m closer together"),
        ("SUNSET POINT", "sunset", ["136", "236", "45"], "no new road; loop already exists"),
        ("36TH ST EAST EXTENSION", "ext36", ["136", "236"], "1 km new road + six new stops")]
for name, key, routes, note in TERM:
    h(3, name)
    w(f"*{note}.*", "")
    nr = by_area.get(key, [])
    w("- [ ] **Roads:** " + (", ".join(f"`{r['code']}` {r['name']}" for r in nr) if nr else "no new road") + ".")
    sts = [s for s in G.STOPREC.values() if any(s["name"] and True for _ in [0])]
    key_stops = {"parkview": ["pv_garageD", "pv_link", "pv_westD", "pv_westP"], "meridian": ["mA_P", "mA2_P", "mA_D", "mA2_D", "leafE", "leafW", "mC_D"], "meridianb": ["mB_D", "mB_P", "mB_P2"],
                 "berrel": ["br_station", "br_D", "br_station2"], "southpier": ["sp_D1", "sp_D2", "sp_D3", "sp_P1", "sp_P2", "sp_P3", "sp_45D", "sp_452", "sp_45P"], "northbeach": ["nb_1", "nb_2", "nb_3", "nb_4", "nb_295"],
                 "crestbury": ["cr_1", "cr_2", "cr_3", "cr_4", "cr_61"], "nwpoint": ["nw_1", "nw_2", "nw_3"], "southside": ["ss_P", "ss_2", "ss_D"], "univpark": ["up_1", "up_2", "up_3", "up_4"],
                 "zeroth": ["z_D", "z_P", "z_3", "z_4"], "valley": ["vf_D", "vf_2", "vf_P"], "sunset": ["sun_1", "sun_2", "sun_3", "sun_D"], "ext36": ["e125", "e175", "e199", "e225", "e275", "e299", "w333_2", "w333_19"]}[key]
    w("- [ ] **Stops:** " + ", ".join(f"`{G.STOPREC[k]['code']}` {G.STOPREC[k]['name']} ({G.STOPREC[k]['kind'].lower()})" for k in key_stops) + ".")
    w("- [ ] **Routes to edit:** " + ", ".join(f"[{r}](#route-{r})" for r in routes) + ".")
    w("- [ ] **Decor (optional, from the PDF):** canopies at each bay, shelters on `hasShelter` stops, benches, trees on the islands, route-map boards.", "")

# ── PHASE 8 ──────────────────────────────────────────────────────────────────────────────────────
h(2, "Phase 8: Testing in Play Mode")
w("Do each test with the sim clock set to a time the route runs.", "")
table(["Test", "Expected"],
      [["Fly to each new road", "Surface looks right; junctions form; no road ends in the grass."],
       ["Parkview: watch a 140 and 240 outbound", "They drive along the new -117th St to Midway; **no grass**."],
       ["Parkview: watch a 1 and a 201 leave Commons", "South, east along 125th, north on Midway. **No lap round the block.**"],
       ["Meridian: watch a 199 and a 1", "They turn into their own row, stop at the drop-off, reach the moved pickup, leave down the arc."],
       ["North Beach: watch a 114", "**No U-turn.** It goes up 295th Av, west along the Mall, up 280th Av."],
       ["North Beach: watch a 34", "East on 340th, south on 295th, west along the Mall (same kerb as 45), north on 280th Av."],
       ["University Park: watch a 29", "Comes down -297th Av, **turns onto Campus Drive legally**, leaves by -277th Av and -270th St."],
       ["Route 136 / 136N", "136 all day from 4:00. At 00:30 only **136N** runs (every 60 min, thinned stops)."],
       ["Route 236", "Orange, `MAX`, limited stops, only buses from the 2900 Series (Artic)."],
       ["Tracker / arrivals", "Open a new stop and check the arrivals list shows the right routes (the stop index rebuilds when you edit an asset)."]])
h(2, "Appendix A: how tValue is worked out (so you can fix one by hand)")
w("Pick the road. Take the point on the road centre line closest to where you want the stop. tValue = (distance along the road to that point) / (total road length). For a straight 2-point road that is just `(pointX - startX) / (endX - startX)` for an east–west road, or the same with Z for a north–south road. Example: `-117th St` runs from X=-380 to X=0. A stop at X=-290 has tValue = (-290 - -380) / 380 = **0.2368**.", "",
  "If the stop appears on the **wrong side** of the road, the road's point order is the other way round for your direction of travel. New roads were ordered so stops land on the correct kerb. For old roads you cannot change, accept the side or add a twin stop on the other side.", "")
h(2, "Appendix B: master list of every stop that changes")
rows = []
for s in sorted(G.STOPREC.values(), key=lambda x: (x["kind"], x["code"])):
    rows.append((f"`{s['code']}`", s["kind"], s["name"], f"`{s['road']}`", s["t"], v3(s["x"], s["z"])))
table(["code", "kind", "name", "road", "tValue", "plan position (X, Y, Z)"], rows)
h(2, "Appendix C: troubleshooting")
table(["Problem", "Fix"],
      [["A bus never stops at a moved stop", "The arriving trip's last node is before the stop. Move that node forward (rule at the top of Phase 4)."],
       ["A bus skips its first stop", "The departing trip's first node is after the stop. Move that node back."],
       ["Bus clips a kerb at a new corner", "Move the corner node 2 m toward the road centre."],
       ["New road has no junction where it crosses", "Open the road, make sure `canHaveIntersection` is ticked, and that the end control point actually reaches the other road's centre line."],
       ["A stop floats in a field", "Wrong `parentRoadCode`. Re-check the road in the table; the road code is case-sensitive."],
       ["Variant 136N does not show", "`overrideRoute` must be ticked, `operatingStartMinutes/EndMinutes` set, and `overrideSchedule` ticked."],
       ["236 never gets a bus", "Check the depot `servedRouteNumbers` contains `236`, `allowedFleetSeries` contains `2900`, articulatedPolicy is Mandatory, and the 2900 Series (Artic) exists in the roster."],
       ["Edits vanish after Play", "You edited while in Play Mode. Stop Play and redo."]])
w("", "*End of guide.*")
open(OUT, "w", encoding="utf-8").write("\n".join(L))
print("wrote", OUT, len(L), "lines")
