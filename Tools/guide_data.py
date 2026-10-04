"""Data layer for the manual build guide: new roads, road edits, stops (with parent road + tValue), all computed from the plan."""
import math, re, sys, os
sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
import make_terminal_plans as T
A = T.A
src, exp, ROADS, STOPS, UNPLACED = A.load_city(); ROUTES = A.load_routes(); R = {r["num"]: r for r in ROUTES}
for a in T.AREAS:
    if a.get("build"): a["build"](a, ROADS)
AREA = {a["key"]: a for a in T.AREAS}
def serves(code):
    return sorted({r["num"] for r in ROUTES for c in r["outStops"] + r["inStops"] + [x for v in r["variants"] for x in v["outStops"] + v["inStops"]] if c == code})

# ── road helpers ──────────────────────────────────────────────────────────────
def curve_pts(ctrl, n=1500): return [A.eval_curve(ctrl, i/n) for i in range(n + 1)]
def project(ctrl, p):
    """(t, distance, signed side) of the closest point on a road to p; side>0 = LEFT of +t (x east, z north)."""
    best = (1e18, 0.0, 0.0); n = 1500
    pts = curve_pts(ctrl, n)
    for i, q in enumerate(pts):
        d = math.dist(p, q)
        if d < best[0]:
            a = pts[max(i - 1, 0)]; b = pts[min(i + 1, n)]
            tx, tz = b[0] - a[0], b[1] - a[1]; L = math.hypot(tx, tz) or 1
            cross = (tx*(p[1] - q[1]) - tz*(p[0] - q[0]))/L          # >0: p is left of the direction of travel in an x-east, z-north plane
            best = (d, i/n, cross)
    return best[1], best[0], best[2]
def road_code_for(name, taken):
    m = re.match(r"(-?)(\d+)(?:st|nd|rd|th) (Av|St)", name)
    if m:
        neg, num, kind = m.groups()
        base = (("S" if neg else "N") if kind == "St" else ("W" if neg else "E")) + num + ("ST" if kind == "St" else "AVE")
    else:
        base = re.sub(r"[^A-Z0-9]", "", name.upper())[:8]
    code = base + "X"; k = 2
    while code in taken: code = base + f"X{k}"; k += 1
    taken.add(code); return code

# ── NEW ROADS (from the plan pages) ───────────────────────────────────────────
ONEWAY = {"Campus Drive": True, "Terminal Mall": True, "Beach Mall": True}      # one-way roads and their wanted traffic direction = plan order, except where listed below
REVERSE_FLOW_WANTED = {"Terminal Mall": True, "Beach Mall": True}                # the plan draws them west->east but the buses go east->west
taken = set(ROADS.keys()); NEWROADS = []
for key, a in AREA.items():
    for pts, width, label in a["new_roads"]:
        name = label.split(" (")[0].replace("NEW  ", "").replace("MOVED  ", "").replace("extend ", "").strip()
        alias = {"-260th St": "Campus Drive", "-65th St": "Terminal Mall", "320th St": "Beach Mall"}
        disp = name
        ow = None
        if name in alias.values() or (key == "univpark" and "260th" in name): ow = "Campus Drive"
        if key == "crestbury" and "65th" in name: ow = "Terminal Mall"
        if key == "northbeach" and "320th" in name: ow = "Beach Mall"
        if name.startswith("Meridian Blvd"):
            continue            # handled as an edit of the existing road MRBD
        if name.startswith("309th St"): name = "309th St"
        if name.startswith("extend 30"): continue
        NEWROADS.append(dict(area=key, label=label, name=name, code=road_code_for(name, taken), width=width, pts=[(round(x), round(z)) for x, z in pts], oneway=bool(ow), ow_name=ow))
# rows 303rd / 306th are edits of existing roads, not new roads
NEWROADS = [r for r in NEWROADS if not r["label"].startswith("extend ")]
for r in NEWROADS:
    if r["ow_name"] in ("Terminal Mall", "Beach Mall"): r["pts"] = r["pts"]        # keep drawn order; reverseFlow handles the direction
# Meridian B: a 4-point road is a bezier curve, so the rectangle must be separate STRAIGHT roads
NEWROADS = [r for r in NEWROADS if r["area"] != "meridianb"]
_t = set(ROADS.keys()) | {r["code"] for r in NEWROADS}
for nm, pts, w, ow in (("350th St", [(-700, 3500), (-540, 3500)], 9, True), ("-54th Av", [(-540, 3400), (-540, 3900)], 9, True),
                       ("390th St", [(-540, 3900), (-700, 3900)], 9, True), ("-62nd Av", [(-620, 3400), (-620, 3900)], 7, False)):
    NEWROADS.append(dict(area="meridianb", label=nm, name=nm, code=road_code_for(nm, _t), width=w, pts=pts, oneway=ow, ow_name=None))
for r in NEWROADS:       # one-way malls run in the direction of the control points, and the stops sit on the right side of that direction
    if r["ow_name"] == "Terminal Mall": r["pts"] = [(-3480, -655), (-3700, -655)]
    if r["ow_name"] == "Beach Mall": r["pts"] = [(2950, 3200), (2800, 3200)]
ALLROADS = {c: dict(ROADS[c]) for c in ROADS}
for r in NEWROADS: ALLROADS[r["code"]] = {"name": r["name"], "code": r["code"], "ctrl": [(float(x), float(z)) for x, z in r["pts"]], "width": r["width"]}
# Meridian Blvd moved west 400 m (24 control points)
MRBD_OLD = [(round(x), round(z)) for x, z in ROADS["MRBD"]["ctrl"]]
MRBD_NEW = [(x - 400, z) for x, z in MRBD_OLD]
ALLROADS["MRBD"] = dict(ALLROADS["MRBD"], ctrl=[(float(x), float(z)) for x, z in MRBD_NEW])
def arc_x(z): return A.eval_curve  # placeholder (not used)
ROAD_EDITS = []   # (code, name, what, old, new)
def row_end(z):
    return T.arc_x_at([(x, zz) for x, zz in MRBD_NEW], z)
ROAD_EDITS.append(("MRBD", "Meridian Blvd", "MOVE the whole arc 400 m west: subtract 400 from the X of every control point (z stays the same).", MRBD_OLD, MRBD_NEW))
ROAD_EDITS.append(("N303ST", "303rd St", "Extend west: change control point 1 (the second one) to the new X.", [(0, 3030), (-146, 3030)], [(0, 3030), (round(row_end(3030)), 3030)]))
ROAD_EDITS.append(("N306ST", "306th St", "Extend west: change control point 1 to the new X.", [(0, 3060), (-137, 3060)], [(0, 3060), (round(row_end(3060)), 3060)]))
ROAD_EDITS.append(("BRWYX", "Berrelingway connector", "Extend east to the new 56th Av: change control point 0 (the first one) to X=560.", [(494, 2900), (400, 2900)], [(560, 2900), (400, 2900)]))
ROAD_EDITS.append(("N203ST", "203rd St", "Widen: roadWidth 5 -> 7.", None, None))
ALLROADS["N203ST"] = dict(ALLROADS["N203ST"], width=7.0)
for _c, _n in (("N303ST", 1), ("N306ST", 1), ("BRWYX", 0)):
    _old = ROAD_EDITS[[e[0] for e in ROAD_EDITS].index(_c)][4]
    ALLROADS[_c] = dict(ALLROADS[_c], ctrl=[(float(x), float(z)) for x, z in _old])
ROAD_REMOVE = [("ARBD", "Art Blvd (curve at University Park)"), ("MRSQD", "Meridian Square Dropoff (D-shaped loop)"), ("W375AVE", "-375th Av (Crestbury stub)")]
for r in NEWROADS:
    if r["name"] == "309th St":
        r["pts"] = [(0, 3090), (round(row_end(3090)), 3090)]; ALLROADS[r["code"]]["ctrl"] = [(float(x), float(z)) for x, z in r["pts"]]

# ── STOP REGISTRY ─────────────────────────────────────────────────────────────
# (key, final name, x, z, kind, old code, routes out, routes in, terminal?, layover?)
def ax(z): return row_end(z)
REG = [
 # Parkview
 ("pv_garageD", "PARKVIEW PARKING GARAGE [D]", -300, -1250, "NEW", None, [], ["140", "240"], True, False),
 ("pv_link", "PARKVIEW LINK [E]", -290, -1165, "MOVE", "s0034", [], [], False, True),
 ("pv_westD", "PARKVIEW B [D]", -790, -1050, "MOVE", "s0406", [], [], True, False),
 ("pv_westP", "PARKVIEW B [P]", -860, -1050, "MOVE", "s0407", [], [], True, False),
 # Meridian A
 ("mA_P", "MERIDIAN SQUARE [PICKUP]", round(ax(3030)) + 60, 3032, "MOVE", "s0135", [], [], True, False),
 ("mA2_P", "MERIDIAN SQUARE A2 [PICKUP]", round(ax(3060)) + 60, 3062, "MOVE", "s0245", [], [], True, False),
 ("mA_D", "MERIDIAN SQUARE [DROPOFF]", -30, 3032, "MOVE", "s0131", [], [], True, False),
 ("mA2_D", "MERIDIAN SQUARE A2 [DROPOFF]", -30, 3062, "MOVE", "s0244", [], [], True, False),
 ("leafE", "LEAF BLVD & W 20 AV", -200, 3008, "NEW", None, ["116", "216", "34A"], [], False, False),
 ("leafW", "LEAF BLVD & W 20 AV", -200, 2992, "NEW", None, [], ["116", "216", "34A"], False, False),
 ("mC_D", "MERIDIAN SQUARE C [LAYOVER]", -250, 3092, "NEW", None, [], [], False, True),
 # Meridian B
 ("mB_D", "MERIDIAN SQUARE B [D]", -532, 3600, "MOVE", "s0478", [], [], True, False),
 ("mB_P", "MERIDIAN SQUARE B [P]", -532, 3800, "MOVE", "s0479", [], [], True, False),
 ("mB_P2", "MERIDIAN SQUARE B [P2]", -532, 3740, "NEW", None, [], [], True, True),
 # Berrelingway North
 ("br_station", "BERRELINGWAY NORTH STATION", 480, 2906, "MOVE", "s0089", [], [], True, False),
 ("br_D", "BERRELINGWAY NORTH [D]", 400, 2960, "NEW", None, [], ["114"], True, False),
 ("br_station2", "BERRELINGWAY NORTH STATION [2]", 545, 2906, "NEW", None, ["140", "240"], [], True, False),
 # South Pier
 ("sp_D1", "SOUTH PIER [D1]", 1930, -1907, "NEW", None, [], ["21", "199", "299"], True, False),
 ("sp_D2", "SOUTH PIER [D]", 1850, -1907, "MOVE", "s0246", [], [], True, False),
 ("sp_D3", "SOUTH PIER [D3]", 1790, -1907, "NEW", None, [], [], True, False),
 ("sp_P1", "SOUTH PIER [P]", 1790, -1942, "MOVE", "s0247", [], [], True, False),
 ("sp_P2", "SOUTH PIER [P2]", 1860, -1942, "NEW", None, [], [], True, False),
 ("sp_P3", "SOUTH PIER [P3]", 1930, -1942, "NEW", None, [], [], True, False),
 ("sp_45D", "SOUTH PIER [45 D]", 1940, -1993, "NEW", None, [], ["45"], True, False),
 ("sp_452", "SOUTH PIER [45 2]", 1860, -1993, "NEW", None, [], [], True, False),
 ("sp_45P", "SOUTH PIER [45 P]", 1780, -1993, "NEW", None, ["45"], [], True, False),
 # North Beach
 ("nb_1", "NORTH BEACH [P]", 2865, 3213, "MOVE", "s0512", [], [], True, False),
 ("nb_2", "NORTH BEACH [D]", 2905, 3213, "MOVE", "s0511", [], [], True, False),
 ("nb_3", "NORTH BEACH [3]", 2825, 3213, "NEW", None, [], [], True, True),
 ("nb_4", "NORTH BEACH [4]", 2935, 3213, "NEW", None, [], [], True, True),
 ("nb_295", "NORTH BEACH [295 D]", 2950, 3300, "NEW", None, [], ["34"], True, False),
 # Crestbury
 ("cr_1", "CRESTBURY TERMINAL", -3560, -651, "MOVE", "s0092", [], [], True, False),
 ("cr_2", "CRESTBURY TERMINAL [DROPOFF]", -3620, -651, "MOVE", "s0099", [], [], True, False),
 ("cr_3", "CRESTBURY [3]", -3670, -651, "NEW", None, [], [], True, False),
 ("cr_4", "CRESTBURY [4]", -3700, -651, "NEW", None, ["136"], ["136"], True, False),
 ("cr_61", "S 61 ST & W 364 AV", -3640, -616, "NEW", None, [], [], False, False),
 # NW Point
 ("nw_1", "NW POINT", -2306, 2150, "MOVE", "s0184", [], [], True, False),
 ("nw_2", "NW POINT [2]", -2306, 2100, "NEW", None, [], [], True, False),
 ("nw_3", "NW POINT [3]", -2306, 2050, "NEW", None, [], [], True, True),
 # Southside
 ("ss_P", "SOUTHSIDE [P]", 930, -2640, "MOVE", "s0292", [], [], True, False),
 ("ss_2", "SOUTHSIDE [2]", 930, -2580, "NEW", None, [], [], True, False),
 ("ss_D", "SOUTHSIDE [D]", 930, -2540, "MOVE", "s0291", [], [], True, False),
 # University Park
 ("up_1", "UNIVERSITY PARK [1]", -2930, -2613, "MOVE", "s0332", [], [], True, False),
 ("up_2", "UNIVERSITY PARK [2]", -2890, -2613, "NEW", None, [], [], True, False),
 ("up_3", "UNIVERSITY PARK [3]", -2850, -2613, "NEW", None, [], [], True, True),
 ("up_4", "UNIVERSITY PARK [4]", -2810, -2613, "NEW", None, [], [], True, False),
 # 0th Av / 200th St
 ("z_D", "0TH AV / 200TH ST [D]", -30, 2036, "MOVE", "s0374", [], [], True, False),
 ("z_P", "0TH AV / 200TH ST [P]", -80, 2036, "MOVE", "s0375", [], [], True, False),
 ("z_3", "0TH AV / 200TH ST [3]", -200, 2036, "NEW", None, [], [], True, False),
 ("z_4", "0TH AV / 200TH ST [4]", -300, 2036, "NEW", None, [], [], True, True),
 # Valley Fields
 ("vf_D", "VALLEY FIELDS [D]", -7494, -1230, "MOVE", "s0476", [], [], True, False),
 ("vf_2", "VALLEY FIELDS [2]", -7494, -1180, "NEW", None, [], [], True, False),
 ("vf_P", "VALLEY FIELDS [P]", -7494, -1130, "MOVE", "s0477", [], [], True, False),
 # Sunset Point
 ("sun_1", "SUNSET POINT [P]", 3150, -1991, "MOVE", "s0564", [], [], True, False),
 ("sun_2", "SUNSET POINT [2]", 3230, -1991, "NEW", None, [], [], True, False),
 ("sun_3", "SUNSET POINT [3]", 3310, -1991, "NEW", None, [], [], True, False),
 ("sun_D", "SUNSET POINT [D]", 3369, -1991, "MOVE", "s0563", [], [], True, False),
 # 333rd Av / 36th St extension
 ("w333_2", "W 333 AV & N 2 ST", -3330, 20, "NEW", None, [], [], False, False),
 ("w333_19", "W 333 AV & N 19 ST", -3330, 190, "NEW", None, [], [], False, False),
 ("e125", "N 36 ST & E 125 AV", 1250, 360, "NEW", None, [], [], False, False),
 ("e175", "N 36 ST & E 175 AV", 1750, 360, "NEW", None, [], [], False, False),
 ("e199", "N 36 ST & E 199 AV", 2010, 360, "NEW", None, [], [], False, False),
 ("e225", "N 36 ST & E 225 AV", 2250, 360, "NEW", None, [], [], False, False),
 ("e275", "N 36 ST & E 275 AV", 2750, 360, "NEW", None, [], [], False, False),
 ("e299", "N 36 ST & E 299 AV", 2970, 360, "NEW", None, [], [], False, False),
]
maxcode = max(int(c[1:]) for c in list(STOPS) + [s["stopCode"] for s in UNPLACED] if re.match(r"s\d+$", c))
NEWCODE = {}; nxt = maxcode + 1
for k, name, x, z, kind, old, *_ in REG:
    if kind == "NEW": NEWCODE[k] = f"s{nxt:04d}"; nxt += 1
def code_of(k):
    for kk, name, x, z, kind, old, *_ in REG:
        if kk == k: return NEWCODE.get(k) or old
STOPREC = {}
for k, name, x, z, kind, old, ro, ri, term, lay in REG:
    best = None
    for c, r in ALLROADS.items():
        if len(r["ctrl"]) < 2: continue
        t, d, side = project(r["ctrl"], (x, z))
        if best is None or d < best[1]: best = (c, d, t, side)
    code, d, t, side = best
    STOPREC[k] = dict(key=k, name=name, x=x, z=z, kind=kind, old=old, code=NEWCODE.get(k) or old, road=code, roadname=ALLROADS[code]["name"], t=round(t, 4), dist=round(d), side="LEFT" if side > 0 else "RIGHT", term=term, lay=lay, routes_out=ro, routes_in=ri)
# stops to retire (set stopName 'placeholder' and clear parentRoadCode; remove from every route list)
RETIRE = [("s0404", "PARKVIEW A [D] (only 29 used it)"), ("s0405", "PARKVIEW A [P] (only 29 used it)")]
CROSSED = ["s0579", "s0581", "s0583", "s0585", "s0587", "s0589", "s0169", "s0171", "s0173", "s0175", "s0177", "s0166", "s0164", "s0162", "s0160", "s0158", "s0157", "s0156", "s0065", "s0025", "s0027", "s0030"]
if __name__ == "__main__":
    print("max code", maxcode, "| new roads:", len(NEWROADS), "| stops in registry:", len(REG), "new codes:", len(NEWCODE))
    for r in NEWROADS: print(r["area"], r["code"], r["name"], r["pts"], r["width"], "ONEWAY" if r["oneway"] else "")
    print()
    for k, s in STOPREC.items():
        print(f"{s['code']:>6} {s['kind']:4} {s['name'][:36]:36} on {s['road']:9} t={s['t']:<7} d={s['dist']:>3}m side={s['side']}")
