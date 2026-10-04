#!/usr/bin/env python3
"""Routes 136 / 236 (Express-Max) / 29 on the proposed network + Express-Max fleet sheet.
Run: python3 Tools/make_route_136_29.py  ->  "Network Maps/Route_136_236_29.pdf" """
import math, os, sys
sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
from terminal_plans_lib import *
from reportlab.pdfgen import canvas
from reportlab.lib.colors import Color

OUT = os.path.join(A.ROOT, "Network Maps", "Route_136_236_29.pdf")
src, exp, roads, stops, unplaced = A.load_city(); routes = A.load_routes()
R = {r["num"]: r for r in routes}
RED, ORANGE, GREEN, BLUE = (0.85, 0.1, 0.1), (0.95, 0.5, 0.0), (0.05, 0.55, 0.3), (0.15, 0.35, 0.85)

def serves(code):
    return sorted({r["num"] for r in routes for c in r["outStops"] + r["inStops"] + [x for v in r["variants"] for x in v["outStops"] + v["inStops"]] if c == code})
def wrap(c, text, font, size, width):
    words, lines, cur = text.split(), [], ""
    for w in words:
        t = (cur + " " + w).strip()
        if c.stringWidth(t, font, size) <= width: cur = t
        else: lines.append(cur); cur = w
    if cur: lines.append(cur)
    return lines

# ── geometry ────────────────────────────────────────────────────────────────
HUB_EXIT = [(-2772, -2608), (-2772, -2698), (-2968, -2698), (-2968, -2508)]
CORR_OLD = [(-2970, -2505), (-2970, -700), (-3030, -700), (-3030, -610), (-3330, -610), (-3330, 358)]       # the old full 136 corridor
EXCH = [(968, 358), (968, 538), (998, 538), (998, 362)]                                                    # loop round the 1000 Exchange block
n45 = sorted([p for p, c in R["45"]["outNodes"] if p[0] > 2900 and -1960 <= p[1] <= 0], key=lambda p: -p[1])  # 299th Av, southbound
def _quad(p0, p1, p2, n=40): return [((1-t)**2*p0[0]+2*(1-t)*t*p1[0]+t*t*p2[0], (1-t)**2*p0[1]+2*(1-t)*t*p1[1]+t*t*p2[1]) for t in [i/n for i in range(n+1)]]
SUNSET = [p for p in _quad((2500, -2000), (3000, -2000), (3500, -1750)) if p[0] >= 3010] + [(3498, -1998), (3035, -1998)]
EAST = [(2988, 358)] + n45 + SUNSET
P136 = [(-3600, -657), (-3698, -657), (-3698, -112), (-3330, -112), (-3330, 358)] + EXCH + EAST
P236 = [(-2930, -2610)] + HUB_EXIT + CORR_OLD + EXCH + EAST
P29 = [(-2890, -2610)] + HUB_EXIT + [(-2970, -2505), (-2970, -700), (-3030, -700), (-3030, -110), (-3330, -110), (-3330, 2002), (-2162, 2002), (-2162, 2218), (-2302, 2218), (-2302, 2090)]
DROPPED = [(-3330, 358), (-3330, 2002)]

# ── stop sequences (label, x, z, kind, routes) ────────────────────────────────
def S(c, kind="KEEP", extra=""): s = stops[c]; return (f"{c}  {s['name']}{extra}", s["x"], s["z"], kind, ", ".join(serves(c)))
c297 = sorted([c for c in stops if c.startswith("s05") and 578 <= int(c[1:]) <= 590], key=lambda c: int(c[1:]))
ext36 = [("N 36 ST & E 125 AV", 1250), ("N 36 ST & E 150 AV", 1500), ("N 36 ST & E 175 AV", 1750), ("N 36 ST & E 199 AV", 2010), ("N 36 ST & E 225 AV", 2250), ("N 36 ST & E 250 AV", 2500), ("N 36 ST & E 275 AV", 2750), ("N 36 ST & E 299 AV", 2970)]
NIGHT_SKIP = set()
CROSS_BOOL = ["s0166", "s0164", "s0162", "s0160", "s0158", "s0157", "s0156"]   # every 2nd on Boolean Way (+ s0157 so no stop is closer than 150 m to Civic Center)
CROSS_36 = ["s0065", "s0025", "s0027", "s0030"]                                  # every 2nd on 36th St; kept: Civic Center, s0066, s0026, s0028 (236), s0029 (34), s0031, Exchange
CROSS_EXT = {"N 36 ST & E 150 AV", "N 36 ST & E 250 AV"}                          # new 36th St stops that are simply not created
CROSSED_36 = CROSS_BOOL + CROSS_36
seq136 = [("CRESTBURY [4]  (Mall)", -3600, -657, "NEW", "136, 21, 116, 216"), S("s0100"),
          ("NEW  W 333 AV & N 2 ST", -3330, 20, "NEW", "136, 29"), ("NEW  W 333 AV & N 19 ST", -3330, 190, "NEW", "136, 29, 236")]
for c in R["136"]["outStops"][R["136"]["outStops"].index("s0167"):]:
    if c == "s0033": break
    if c in CROSSED_36: continue
    seq136.append(S(c, "SKIP" if c in NIGHT_SKIP else "KEEP"))
seq136.append(S("s0033"))
for n, x in [e for e in ext36 if e[0] not in CROSS_EXT]: seq136.append((f"NEW  {n}", x, 360, "NEW", "136" + (", 236" if x in (2010, 2970) else "")))
for c in ["s0572", "s0571", "s0570", "s0569", "s0568", "s0567", "s0566", "s0565"]: seq136.append(S(c))
seq136.append(("SUNSET POINT [1]  (moved s0564)", 3150, -1991, "NEW", "136, 236, 45"))

seq236 = [("UNIVERSITY PARK [1]  (Campus Drive)", -2930, -2613, "NEW", "236, 29, 7"), S("s0584"), S("s0590"), ("NEW  W 333 AV & N 19 ST", -3330, 190, "NEW", "136, 29, 236"),
          S("s0064"), S("s0028"), S("s0033"), ("NEW  N 36 ST & E 199 AV", 2010, 360, "NEW", "136, 236"), ("NEW  N 36 ST & E 299 AV", 2970, 360, "NEW", "136, 236"),
          S("s0568"), S("s0566"), ("SUNSET POINT [2]", 3230, -1991, "NEW", "236, 136, 45")]

KEEP297 = [c for i, c in enumerate(c297) if i % 2 == 0]; REM297 = [c for i, c in enumerate(c297) if i % 2 == 1]
N333 = [f"s0{n}" for n in range(168, 178)]; KEEP333 = [c for i, c in enumerate(N333) if i % 2 == 0]; REM333 = [c for i, c in enumerate(N333) if i % 2 == 1]

seq29 = [("UNIVERSITY PARK [2]  (Campus Drive)", -2890, -2613, "NEW", "29")] + [S(c, "KEEP", "") for c in KEEP297] + [S("s0101", "KEEP", "  (route 21's stop)"),
         ("NEW  W 333 AV & N 2 ST", -3330, 20, "NEW", "136, 29"), ("NEW  W 333 AV & N 19 ST", -3330, 190, "NEW", "136, 29, 236")]
seq29 += [S(c, "RESC") for c in KEEP333] + [S(f"s0{n}", "RESC") for n in range(178, 182)] + [("NW POINT [2]  (230th Av bays)", -2306, 2100, "NEW", "25, 29")]
REMOVED = REM297 + REM333 + CROSSED_36
KM = {k: A.polylen(P)/1000 for k, P in (("136", P136), ("236", P236), ("29", P29))}

def stop_dot(c, view, x, z, col, r=2.8):
    px, py = view.p((x, z)); c.saveState(); c.setFillColor(Color(*col)); c.setStrokeColor(Color(1, 1, 1)); c.setLineWidth(0.6); c.circle(px, py, r, stroke=1, fill=1); c.restoreState()

def page1(c):
    W, H = 2500, 1500; c.setPageSize((W, H)); M = 36
    bbox = (-3900, -2790, 3700, 2290); MW = 1250; MH = MW*(bbox[3]-bbox[1])/(bbox[2]-bbox[0])
    if MH > H - 130: MH = H - 130; MW = MH*(bbox[2]-bbox[0])/(bbox[3]-bbox[1])
    rect = (M, H - 100 - MH, MW, MH); view = A.View(bbox, rect)
    A.draw_text(c, M, H - 40, "ROUTES 136, 236 EXPRESS-MAX AND 29  -  on the proposed network", 24, A.FONT_B)
    A.draw_text(c, M, H - 62, f"136: Crestbury -> Sunset Point, {KM['136']:.1f} km, {len(seq136)} stops (local, full length)   ·   236: University Park -> Sunset Point, {KM['236']:.1f} km, {len(seq236)} stops (limited)   ·   29: University Park -> NW Point, {KM['29']:.1f} km, {len(seq29)} stops", 10.5, color=(0.3, 0.3, 0.35))
    A.begin_clip(c, view); c.setFillColor(Color(0.97, 0.975, 0.98)); c.rect(*rect, stroke=0, fill=1)
    for r in roads.values():
        if r["code"] == "ARBD": continue
        A.draw_poly(c, view, dense(r["ctrl"], 25.0), (0.8, 0.81, 0.84), max(0.8, r["width"]*view.s*0.9))
    A.draw_poly(c, view, [(1990, 360), (2990, 360)], (0.8, 0.81, 0.84), 2.4); A.draw_poly(c, view, [(1990, 360), (2990, 360)], (0.1, 0.6, 0.3), 0.8, dash=(3, 3))
    for P, col, w in ((P29, GREEN, 4.2), (P136, RED, 4.2), (P236, ORANGE, 2.6)):
        A.draw_poly(c, view, resample(P, 8), (1, 1, 1), w + 3.2); A.draw_poly(c, view, resample(P, 8), col, w)
        poly_arrows(c, view, resample(P, 5), bbox, col, every_m=320, size=8)
    for seq, col in ((seq29, GREEN), (seq136, RED), (seq236, ORANGE)):
        for lab, x, z, kind, rts in seq:
            if kind == "NEW": A.draw_star(c, *view.p((x, z)), 5.4, fill=(1, 0.85, 0.3), edge=col, lw=1.2)
            elif kind == "RESC": stop_dot(c, view, x, z, GREEN, 3.2)
            elif kind == "SKIP": stop_dot(c, view, x, z, (0.5, 0.5, 0.5), 3.4)
            else: stop_dot(c, view, x, z, BLUE, 2.4)
    for cd in REMOVED:
        px, py = view.p((stops[cd]["x"], stops[cd]["z"])); c.saveState(); c.setStrokeColor(Color(0.85, 0.1, 0.1)); c.setLineWidth(1.8); c.line(px - 4.5, py - 4.5, px + 4.5, py + 4.5); c.line(px - 4.5, py + 4.5, px + 4.5, py - 4.5); c.restoreState()
    for nm, x, z, dx, dy in (("UNIVERSITY PARK", -2930, -2616, 10, 8), ("CRESTBURY", -3700, -682, 10, -14), ("1000 EXCHANGE", 968, 470, -40, 12), ("NW POINT", -2300, 2110, 10, 6), ("SUNSET POINT", 3150, -1991, -20, -16)):
        px, py = view.p((x, z)); A.draw_halo_text(c, px + dx, py + dy, nm, 9, A.FONT_B, (0.5, 0.0, 0.0))
    A.end_clip(c); c.setStrokeColor(Color(0.2, 0.2, 0.2)); c.rect(*rect, stroke=1, fill=0)
    lx, ly = M + 8, H - 100 - 20
    for i, (txt, col) in enumerate([("136 local", RED), ("236 Express-Max (limited stop)", ORANGE), ("29", GREEN)]):
        c.setStrokeColor(Color(*col)); c.setLineWidth(3.5); c.line(lx, ly - i*14, lx + 24, ly - i*14); A.draw_text(c, lx + 30, ly - i*14 - 3, txt, 8.5, A.FONT_B)
    for i, (txt, col) in enumerate([("existing stop kept", BLUE), ("rescued stop (was 136's, orphaned)", GREEN)]):
        c.saveState(); c.setFillColor(Color(*col)); c.circle(lx + 12, ly - 46 - i*14 + 3, 3.2, stroke=0, fill=1); c.restoreState(); A.draw_text(c, lx + 30, ly - 46 - i*14, txt, 8)
    A.draw_star(c, lx + 12, ly - 88 + 3, 5.4, fill=(1, 0.85, 0.3), edge=(0.6, 0.3, 0)); A.draw_text(c, lx + 30, ly - 88, "NEW stop", 8)
    c.saveState(); c.setStrokeColor(Color(0.85, 0.1, 0.1)); c.setLineWidth(1.8); c.line(lx + 8, ly - 105, lx + 16, ly - 97); c.line(lx + 8, ly - 97, lx + 16, ly - 105); c.restoreState(); A.draw_text(c, lx + 30, ly - 102, "stop crossed out (every 2nd on corridor, Boolean Way, 36th St)", 8)
    x0 = M*2 + MW; colw = (W - x0 - M)/3 - 8; y = H - 100
    def listing(xx, title, seq, col):
        A.draw_text(c, xx, y, title, 10.5, A.FONT_B, col); yy = y - 15
        for i, (lab, x, z, kind, rts) in enumerate(seq):
            colr = {"NEW": (0.75, 0.4, 0.0), "RESC": (0.05, 0.45, 0.15), "KEEP": (0.1, 0.1, 0.1), "SKIP": (0.45, 0.45, 0.45)}[kind]
            A.draw_text(c, xx, yy, f"{i+1:>2}. {lab[:38]}" + ("  [night: skip]" if kind == "SKIP" else ""), 6.9, color=colr); yy -= 11.2
    yy = y - 15 - 11.2*(len(seq29) + 3)
    A.draw_text(c, x0 + 2*(colw + 8), yy, f"CROSSED OUT: every 2nd stop on the -300 corridor, Boolean Way and 36th St ({len(REMOVED)})", 9.5, A.FONT_B, (0.8, 0.1, 0.1)); yy -= 14
    for cd in REMOVED: A.draw_text(c, x0 + 2*(colw + 8), yy, f"X  {cd}  {stops[cd]['name']}", 6.9, color=(0.7, 0.1, 0.1)); yy -= 11.2
    listing(x0, f"136 LOCAL  ({len(seq136)} stops)", seq136, RED); listing(x0 + colw + 8, f"236 EXPRESS-MAX  ({len(seq236)} stops)", seq236, ORANGE); listing(x0 + 2*(colw + 8), f"29  ({len(seq29)} stops)", seq29, GREEN)
    c.showPage()

# ── fleet sheet ───────────────────────────────────────────────────────────────
CRUISE_KMH = 24.0
def trip_min(km, nstops, dwell): return km/CRUISE_KMH*60 + nstops*dwell
def buses(T, headway, layover=8): return math.ceil((2*T + 2*layover)/headway)
T136, T236, T29 = trip_min(KM["136"], len(seq136), 0.55), trip_min(KM["236"], len(seq236), 0.6), trip_min(KM["29"], len(seq29), 0.55)
KM240 = A.polylen(R["240"]["outPoly"])/1000; T240 = trip_min(KM240, len(R["240"]["outStops"]), 0.6)

def page2(c):
    W, H = 1700, 1250; c.setPageSize((W, H)); M = 40
    A.draw_text(c, M, H - 46, "EXPRESS-MAX 236 AND 136: HOW MANY BUSES", 26, A.FONT_B)
    A.draw_text(c, M, H - 68, f"Trip time model: {CRUISE_KMH:.0f} km/h running speed + dwell per stop (0.55 min local, 0.6 min limited), 8 min layover each end. Round trip = 2 x trip + 2 x layover. Tune in the sim.", 10.5, color=(0.3, 0.3, 0.35))
    y = H - 110
    rows = [("Route", "Length", "Stops", "One-way trip", "Peak headway", "Buses (peak)", "+1 spare")]
    for name, km, n, T, hws in (("236 Express-Max", KM["236"], len(seq236), T236, (20, 15, 10)), ("240 Express-Max (route unchanged)", KM240, len(R["240"]["outStops"]), T240, (15, 10)),
                                ("136 local (normal artics)", KM["136"], len(seq136), T136, (20, 15, 12)), ("29", KM["29"], len(seq29), T29, (30, 20))):
        for hw in hws: rows.append((name, f"{km:.1f} km", str(n), f"{T:.0f} min", f"{hw} min", str(buses(T, hw)), str(buses(T, hw) + 1)))
    cx = [M, M + 330, M + 430, M + 520, M + 650, M + 780, M + 900]
    for i, row in enumerate(rows):
        for k, t in enumerate(row): A.draw_text(c, cx[k], y - i*17, t, 10, A.FONT_B if i == 0 else A.FONT, (0.05, 0.3, 0.1) if (i and k == 5) else (0, 0, 0))
    y -= len(rows)*17 + 24
    mx236_20, mx240_10 = buses(T236, 20) + 1, buses(T240, 10) + 1
    mx236_10, mx240_10b = buses(T236, 10) + 1, buses(T240, 10) + 1
    blocks = [
     ("EXPRESS-MAX FLEET (25 buses)", ORANGE, [f"236 at 20 min and 240 at 10 min uses {mx236_20} + {mx240_10} = {mx236_20 + mx240_10} buses with one spare each. That leaves {25 - mx236_20 - mx240_10} spare.",
        f"236 at 10 min and 240 at 10 min uses {mx236_10} + {mx240_10b} = {mx236_10 + mx240_10b}, so all 25 only make sense if you plan a 10-minute 236 or more Max routes later.",
        f"The 236 is {KM['236']:.1f} km ({T236:.0f} min), not 9.7 km, because it takes the old full 136 corridor, as you asked. At 20 minutes that is {buses(T236, 20)} buses, not 4."]),
     ("NORMAL ARTICS (10 buses, spread over routes like 136)", BLUE, [f"136 local needs {buses(T136, 20)} buses at 20 min or {buses(T136, 15)} at 15 min, so one route can eat most of the 10. Give 136 about 6 and spread the other 4.",
        "These run on routes whose allowedFleetSeries lists the artic series; the Express-Max routes list only the Max series."]),
     ("NIGHT 0:00-4:00", (0.3, 0.3, 0.45), [f"236 does not run at midnight: service span 4:00 to 24:00, so it never needs a night pool. The game's night window is 0:00 to 5:00, so its 4:00-5:00 hour still needs the Max series in nightFleetSeries (or start it at 5:00).",
        f"136 runs all night at a 60 min headway. With the corridor stops thinned (-297th Av, -333rd Av, Boolean Way, 36th St) the model trip is {T136:.0f} min, so it needs {buses(T136, 60)} buses at 60 min; a 70 min trip would need {buses(70, 60)}. If the night trip still has to drop, extra night-only skips on 299th Av (s0572-s0565) are the next place to cut."])]
    for title, col, lines in blocks:
        A.draw_text(c, M, y, title, 13, A.FONT_B, col); y -= 17
        for t in lines:
            ls = wrap(c, t, A.FONT, 10, W - 2*M - 24); A.draw_text(c, M + 4, y, "•", 10)
            for ln in ls: A.draw_text(c, M + 18, y, ln, 10); y -= 13.2
            y -= 3
        y -= 10
    c.showPage()

def main():
    os.makedirs(os.path.dirname(OUT), exist_ok=True)
    out = os.environ.get("R136_OUT", OUT); c = canvas.Canvas(out, pageCompression=1); c.setTitle("Routes 136, 236 and 29")
    page1(c); page2(c); c.save()
    print("wrote", out, "| km", {k: round(v, 1) for k, v in KM.items()}, "| stops 136/236/29", len(seq136), len(seq236), len(seq29), "| trips", round(T136), round(T236), round(T29), round(T240))

if __name__ == "__main__": main()
