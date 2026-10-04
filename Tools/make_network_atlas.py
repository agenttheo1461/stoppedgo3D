#!/usr/bin/env python3
"""
Transit City network atlas  ->  one vector PDF (zoom as far as you like).

Pages
  1  Full network overview: every road (with its name), every stop, terminals as stars
  2  All routes together, in their own colours
  3+ One page per route: outbound (solid red) / inbound (dashed blue), variants, every stop in order
  .. Stop index: every stop with its code, road, and the routes that serve it

Reads (nothing in the project is changed):
  Assets/CityBackups/CityBackup_*.json      roads + stops   (newest file; export a fresh one for current data:
                                            select the CityManager object > CityDataExporter > Export City Data Backup)
  Assets/ASSETS (1)/ROUTES/CBT/*.asset      routes: path nodes, stops, variants, colours, destinations

Run:  python3 Tools/make_network_atlas.py            (writes "Network Maps/Transit_City_Network_Atlas.pdf")
"""
import glob, json, math, os, re, sys
from collections import defaultdict
from reportlab.pdfgen import canvas
from reportlab.lib.colors import Color, black, white
from reportlab.pdfbase import pdfmetrics
from reportlab.pdfbase.ttfonts import TTFont

ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
OUT  = os.path.join(ROOT, "Network Maps", "Transit_City_Network_Atlas.pdf")

# ── fonts (Arial Unicode has the arrows/stars; fall back to Helvetica) ───────────
FONT, FONT_B = "Helvetica", "Helvetica-Bold"
for cand in ["/System/Library/Fonts/Supplemental/Arial Unicode.ttf", "/Library/Fonts/Arial Unicode.ttf"]:
    if os.path.exists(cand):
        pdfmetrics.registerFont(TTFont("AU", cand)); FONT = FONT_B = "AU"; break
ARROW = "↔" if FONT == "AU" else "<->"
RARROW = "→" if FONT == "AU" else "->"

# ═══════════════════════════ geometry (ports of RoadSegment / route curves) ══════
def lerp(a, b, t): return (a[0] + (b[0]-a[0])*t, a[1] + (b[1]-a[1])*t)

def cubic(p0, p1, p2, p3, t):
    u = 1 - t
    return tuple(u*u*u*p0[i] + 3*u*u*t*p1[i] + 3*u*t*t*p2[i] + t*t*t*p3[i] for i in (0, 1))

def catmull(p0, p1, p2, p3, t):
    t2, t3 = t*t, t*t*t
    return tuple(0.5*((-t3 + 2*t2 - t)*p0[i] + (3*t3 - 5*t2 + 2)*p1[i] + (-3*t3 + 4*t2 + t)*p2[i] + (t3 - t2)*p3[i]) for i in (0, 1))

def eval_curve(pts, t):
    """Same rules as RoadSegment.EvaluatePosition: 2 straight, 3 quadratic, 4 cubic, 3k+1 chained cubic, else Catmull-Rom."""
    n = len(pts); t = min(max(t, 0.0), 1.0)
    if n == 1: return pts[0]
    if n == 2: return lerp(pts[0], pts[1], t)
    if n == 3:
        u = 1 - t
        return tuple(u*u*pts[0][i] + 2*u*t*pts[1][i] + t*t*pts[2][i] for i in (0, 1))
    if n == 4: return cubic(pts[0], pts[1], pts[2], pts[3], t)
    if (n - 1) % 3 == 0:
        segs = (n - 1)//3; sc = t*segs; s = min(int(sc), segs - 1); i = s*3
        return cubic(pts[i], pts[i+1], pts[i+2], pts[i+3], sc - s)
    sc = t*(n - 1); i = min(int(sc), n - 2); lt = sc - i
    return catmull(pts[max(i-1, 0)], pts[i], pts[min(i+1, n-1)], pts[min(i+2, n-1)], lt)

def polylen(pts): return sum(math.dist(pts[i], pts[i+1]) for i in range(len(pts) - 1))

def sample_curve(pts, spacing):
    if len(pts) <= 2: return list(pts)
    n = max(8, int(polylen(pts)/spacing))
    return [eval_curve(pts, i/n) for i in range(n + 1)]

def math_road_points(r):
    sx, sz = r["mathStart"]["x"], r["mathStart"]["z"]; ex, ez = r["mathEnd"]["x"], r["mathEnd"]["z"]
    dx, dz = ex - sx, ez - sz; L = math.hypot(dx, dz)
    if L < 0.001: return [(sx, sz), (ex, ez)]
    fx, fz = dx/L, dz/L; sidex, sidez = -fz, fx
    cnt = max(2, r.get("sampleCount", 24)); out = []
    for i in range(cnt):
        t = i/(cnt - 1); ang = t*r["frequency"]*math.pi*2 + r["phase"]; w = r["waveform"]
        if   w == "Sine":       f = math.sin(ang)
        elif w == "Cosine":     f = math.cos(ang)
        elif w == "Arc":        f = 4*t*(1 - t)
        elif w == "Polynomial": f = t*t*(3 - 2*t) - 0.5
        elif w == "Zigzag":
            a = ang/math.pi; f = (abs(a % 2 - 1) if False else (1 - abs((a % 2) - 1))) * 2 - 1
        else: f = 0
        lat = r["amplitude"]*f
        out.append((sx + fx*t*L + sidex*lat, sz + fz*t*L + sidez*lat))
    return out

# ═══════════════════════════ load city (roads, stops) ════════════════════════════
def load_city():
    files = sorted(glob.glob(os.path.join(ROOT, "Assets", "CityBackups", "CityBackup_*.json")))
    if not files: sys.exit("No CityBackup_*.json found in Assets/CityBackups")
    path = files[-1]; d = json.load(open(path))
    roads = {}
    for r in d["roads"]:
        ctrl = math_road_points(r) if r["curveMode"] == "MathEquation" else [(p["x"], p["z"]) for p in r["controlPoints"]]
        roads[r["roadCode"]] = {"name": r["roadName"], "code": r["roadCode"], "ctrl": ctrl, "width": r.get("roadWidth", 7.0),
                                "poly": sample_curve(ctrl, 20.0)}
    stops, unplaced = {}, []
    for s in d["stops"]:
        rd = roads.get(s["parentRoadCode"])
        if not rd: unplaced.append(s); continue
        x, z = eval_curve(rd["ctrl"], s["tValue"])
        stops[s["stopCode"]] = {"code": s["stopCode"], "name": s["stopName"], "x": x, "z": z, "terminal": bool(s["isTerminal"]),
                                "road": rd["name"], "roadCode": rd["code"]}
    return os.path.basename(path), d.get("exportedAt", "?"), roads, stops, unplaced

# ═══════════════════════════ load routes (Unity YAML, light parser) ═════════════
VEC = re.compile(r"position: \{x: ([-\d.eE+]+), y: [-\d.eE+]+, z: ([-\d.eE+]+)\}\s*\n\s*isCurve: (\d)")
def parse_nodes(block):
    return [((float(x), float(z)), c == "1") for x, z, c in VEC.findall(block)]

def parse_stop_codes(block):
    return re.findall(r"stopCode: (\S+)", block)

def grab(text, key, indent):
    """Text of a list field (up to the next field at the same indent)."""
    m = re.search(r"\n%s%s:\s*\n(.*?)(?=\n%s\w|\Z)" % (indent, key, indent), text, re.S)
    return m.group(1) if m else ""

def scalar(text, key, indent="  "):
    m = re.search(r"\n%s%s: ?(.*)" % (indent, key), text)
    return m.group(1).strip() if m else ""

def curve_polyline(nodes):
    """Straight lines between nodes, with each run of 'isCurve' nodes drawn as one smooth curve."""
    pts, i, n = [], 0, len(nodes)
    while i < n:
        p, c = nodes[i]
        if not c: pts.append(p); i += 1; continue
        j = i
        while j + 1 < n and nodes[j + 1][1]: j += 1
        run = [nodes[k][0] for k in range(i, j + 1)]
        pts.extend(sample_curve(run, 4.0) if len(run) > 2 else run)
        i = j + 1
    return pts

def load_routes():
    routes = []
    for f in glob.glob(os.path.join(ROOT, "Assets", "ASSETS (1)", "ROUTES", "**", "*.asset"), recursive=True):
        t = open(f, errors="ignore").read()
        rn = scalar(t, "routeNumber")
        if not rn or "outboundNodes:" not in t: continue
        col = re.search(r"routeColor: \{r: ([-\d.eE+]+), g: ([-\d.eE+]+), b: ([-\d.eE+]+)", t)
        color = tuple(min(1, max(0, float(v))) for v in col.groups()) if col else (0.2, 0.3, 0.8)
        vm = re.search(r"\n  variants:\s*\n(.*?)(?=\n  \w|\Z)", t, re.S)      # the variants block, wherever it sits in the file
        vt = vm.group(1) if vm else ""
        head = t[:vm.start()] + t[vm.end():] if vm else t                      # everything else = the mainline
        r = {"num": rn, "name": scalar(t, "routeName"), "color": color,
             "destOut": scalar(t, "destinationNameOutbound") or "Z", "destIn": scalar(t, "destinationNameInbound") or "A",
             "tA": scalar(t, "terminalACode"), "tZ": scalar(t, "terminalZCode"),
             "outNodes": parse_nodes(grab(head, "outboundNodes", "  ")), "inNodes": parse_nodes(grab(head, "inboundNodes", "  ")),
             "outStops": parse_stop_codes(grab(head, "outboundStops", "  ")), "inStops": parse_stop_codes(grab(head, "inboundStops", "  ")),
             "variants": []}
        for vb in re.split(r"\n  - variantLetter: ", "\n" + vt)[1:]:
            letter = vb.split("\n")[0].strip()
            vo = parse_nodes(grab("\n" + vb, "outboundNodesOverride", "    "))
            vi = parse_nodes(grab("\n" + vb, "inboundNodesOverride", "    "))
            vso = parse_stop_codes(grab("\n" + vb, "outboundStopsOverride", "    "))
            vsi = parse_stop_codes(grab("\n" + vb, "inboundStopsOverride", "    "))
            short = re.search(r"isShortTurn: 1", vb) is not None
            if vo or vi or vso or vsi:
                r["variants"].append({"letter": "~" if short else letter, "outNodes": vo, "inNodes": vi, "outStops": vso, "inStops": vsi})
        r["outPoly"] = curve_polyline(r["outNodes"]); r["inPoly"] = curve_polyline(r["inNodes"])
        routes.append(r)
    def key(r):
        m = re.match(r"\d+", r["num"]); return (int(m.group()) if m else 10**9, r["num"])
    routes.sort(key=key)
    return routes

# ═══════════════════════════ drawing helpers ═════════════════════════════════════
class View:
    """World (x, z) -> page coordinates for one map rectangle."""
    def __init__(self, bbox, rect):
        x0, z0, x1, z1 = bbox; rx, ry, rw, rh = rect
        self.s = min(rw/(x1 - x0), rh/(z1 - z0))
        self.ox = rx + (rw - (x1 - x0)*self.s)/2; self.oy = ry + (rh - (z1 - z0)*self.s)/2
        self.x0, self.z0, self.rect, self.bbox = x0, z0, rect, bbox
    def p(self, pt): return (self.ox + (pt[0] - self.x0)*self.s, self.oy + (pt[1] - self.z0)*self.s)

def bbox_of(points, pad_frac=0.06, min_span=500.0):
    xs = [p[0] for p in points]; zs = [p[1] for p in points]
    x0, x1, z0, z1 = min(xs), max(xs), min(zs), max(zs)
    sx, sz = max(x1 - x0, min_span), max(z1 - z0, min_span)
    cx, cz = (x0 + x1)/2, (z0 + z1)/2
    return (cx - sx*(0.5 + pad_frac), cz - sz*(0.5 + pad_frac), cx + sx*(0.5 + pad_frac), cz + sz*(0.5 + pad_frac))

def widen_to_aspect(bbox, lo=0.8, hi=1.7):
    """A long thin route gets some of its neighbourhood on both sides, so the page isn't a sliver."""
    x0, z0, x1, z1 = bbox; w, h = x1 - x0, z1 - z0
    if w/h < lo: grow = h*lo - w; x0 -= grow/2; x1 += grow/2
    elif w/h > hi: grow = w/hi - h; z0 -= grow/2; z1 += grow/2
    return (x0, z0, x1, z1)

def begin_clip(c, view):
    rx, ry, rw, rh = view.rect
    c.saveState(); p = c.beginPath(); p.rect(rx, ry, rw, rh); c.clipPath(p, stroke=0, fill=0)

def end_clip(c): c.restoreState()

def poly_path(c, view, pts):
    p = c.beginPath(); first = True
    for q in pts:
        x, y = view.p(q)
        if first: p.moveTo(x, y); first = False
        else: p.lineTo(x, y)
    return p

def draw_poly(c, view, pts, color, width, dash=None, alpha=1.0):
    if len(pts) < 2: return
    c.saveState(); c.setStrokeColor(Color(*color, alpha=alpha)); c.setLineWidth(width); c.setLineJoin(1); c.setLineCap(1)
    if dash: c.setDash(*dash)
    c.drawPath(poly_path(c, view, pts), stroke=1, fill=0); c.restoreState()

def draw_star(c, x, y, r, fill=(1, 0.82, 0.2), edge=(0.55, 0.1, 0.1), lw=1.0):
    pts = []
    for i in range(10):
        ang = math.pi/2 + i*math.pi/5; rr = r if i % 2 == 0 else r*0.42
        pts.append((x + rr*math.cos(ang), y + rr*math.sin(ang)))
    p = c.beginPath(); p.moveTo(*pts[0])
    for q in pts[1:]: p.lineTo(*q)
    p.close(); c.saveState(); c.setFillColor(Color(*fill)); c.setStrokeColor(Color(*edge)); c.setLineWidth(lw); c.drawPath(p, stroke=1, fill=1); c.restoreState()

def draw_text(c, x, y, s, size, font=None, color=(0, 0, 0), anchor="l", alpha=1.0):
    c.saveState(); c.setFillColor(Color(*color, alpha=alpha)); c.setFont(font or FONT, size)
    {"l": c.drawString, "r": c.drawRightString, "c": c.drawCentredString}[anchor](x, y, s); c.restoreState()

def draw_halo_text(c, x, y, s, size, font=None, color=(0, 0, 0), anchor="l"):
    for dx, dy in ((1, 1), (-1, 1), (1, -1), (-1, -1)):
        draw_text(c, x + dx*size*0.07, y + dy*size*0.07, s, size, font, (1, 1, 1), anchor)
    draw_text(c, x, y, s, size, font, color, anchor)

def grid_and_axes(c, view, step, label_size, frame=True):
    x0, z0, x1, z1 = view.bbox; rx, ry, rw, rh = view.rect
    c.saveState(); c.setStrokeColor(Color(0.9, 0.9, 0.92)); c.setLineWidth(0.4)
    v = math.ceil(x0/step)*step
    while v <= x1:
        px, _ = view.p((v, z0)); c.line(px, ry, px, ry + rh); draw_text(c, px, ry - label_size*1.3, f"{int(v)}", label_size, color=(0.3, 0.3, 0.3), anchor="c"); v += step
    v = math.ceil(z0/step)*step
    while v <= z1:
        _, py = view.p((x0, v)); c.line(rx, py, rx + rw, py); draw_text(c, rx - label_size*0.5, py - label_size*0.35, f"{int(v)}", label_size, color=(0.3, 0.3, 0.3), anchor="r"); v += step
    if frame: c.setStrokeColor(Color(0.2, 0.2, 0.2)); c.setLineWidth(0.8); c.rect(rx, ry, rw, rh)
    c.restoreState()

def pick_step(span):
    for st in (100, 200, 250, 500, 1000, 2000):
        if span/st <= 12: return st
    return 2000

def road_labels(c, view, roads, size, min_len_px=0):
    seen = set()
    for r in roads.values():
        pts = r["poly"]
        if len(pts) < 2: continue
        a, b = view.p(pts[0]), view.p(pts[-1])
        if math.dist(a, b) < min_len_px: continue
        mid = pts[len(pts)//2]; x, y = view.p(mid)
        rx, ry, rw, rh = view.rect
        if not (rx < x < rx + rw and ry < y < ry + rh): continue
        key = (round(x/ (size*4)), round(y/(size*1.4)))
        if key in seen: continue
        seen.add(key)
        draw_text(c, x, y + size*0.3, r["name"], size, color=(0.35, 0.35, 0.40), anchor="c", alpha=0.85)

def draw_roads(c, view, roads, color=(0.74, 0.75, 0.78), width=1.0, bbox=None):
    for r in roads.values():
        pts = r["poly"]
        if bbox:
            xs = [p[0] for p in pts]; zs = [p[1] for p in pts]
            if max(xs) < bbox[0] or min(xs) > bbox[2] or max(zs) < bbox[1] or min(zs) > bbox[3]: continue
        draw_poly(c, view, pts, color, max(width, r["width"]*view.s*0.85))

# ═══════════════════════════ pages ═══════════════════════════════════════════════
def page_size(bbox, map_w, side_w, margin, title_h, max_map_h):
    spanx, spanz = bbox[2] - bbox[0], bbox[3] - bbox[1]
    mw = map_w; mh = mw*spanz/spanx
    if mh > max_map_h: mh = max_map_h; mw = mh*spanx/spanz
    return mw, mh, mw + side_w + margin*3, mh + title_h + margin*2

def numbered_list(c, x, y_top, lines, size, line_h, col_w, max_h, color=(0, 0, 0)):
    """Writes lines top-down in as many columns as needed. Returns the y it ended at."""
    per_col = max(1, int(max_h/line_h)); y = y_top
    for i, s in enumerate(lines):
        col, row = divmod(i, per_col)
        draw_text(c, x + col*col_w, y_top - row*line_h, s, size, color=color)
    return y_top - min(len(lines), per_col)*line_h

def overview_page(c, roads, stops, all_points, routes):
    bbox = bbox_of(all_points, 0.03)
    MARGIN, SIDE, TITLE = 90, 1250, 150
    mw, mh, PW, PH = page_size(bbox, 2600, SIDE, MARGIN, TITLE, 3200)
    c.setPageSize((PW, PH))
    view = View(bbox, (MARGIN + 60, MARGIN, mw - 60, mh))
    draw_text(c, PW/2 - SIDE/2, PH - 100, "Transit City — Full Network Overview", 78, FONT_B, anchor="c")
    grid_and_axes(c, view, 1000, 22)
    draw_roads(c, view, roads)
    road_labels(c, view, roads, 15, min_len_px=120)
    for s in stops.values():
        x, y = view.p((s["x"], s["z"]))
        c.saveState(); c.setFillColor(Color(0.25, 0.35, 0.9, alpha=0.75)); c.circle(x, y, 2.2, stroke=0, fill=1); c.restoreState()
    terms = sorted([s for s in stops.values() if s["terminal"]], key=lambda s: s["name"].lower())
    for i, s in enumerate(terms, 1):
        x, y = view.p((s["x"], s["z"])); draw_star(c, x, y, 13)
        draw_halo_text(c, x + 14, y + 4, str(i), 17, FONT_B, (0.5, 0.05, 0.05))
    # legend
    lx = MARGIN + 60 + mw + 70; ly = PH - TITLE - 10
    draw_text(c, lx, ly, f"Terminals ({len(terms)})", 36, FONT_B)
    lines = [f"{i}. {s['name']}" for i, s in enumerate(terms, 1)]
    numbered_list(c, lx, ly - 52, lines, 22, 29, 600, 1100)
    ky = PH - TITLE - 1300
    draw_star(c, lx + 14, ky + 8, 13); draw_text(c, lx + 40, ky, "terminal  (number matches the list)", 24)
    c.saveState(); c.setFillColor(Color(0.25, 0.35, 0.9, alpha=0.75)); c.circle(lx + 14, ky - 40 + 8, 4, stroke=0, fill=1); c.restoreState()
    draw_text(c, lx + 40, ky - 40, f"bus stop  ({len(stops)} on the map)", 24)
    c.saveState(); c.setStrokeColor(Color(0.74, 0.75, 0.78)); c.setLineWidth(7); c.line(lx, ky - 80 + 8, lx + 28, ky - 80 + 8); c.restoreState()
    draw_text(c, lx + 40, ky - 80, f"road  ({len(roads)} roads, names in grey)", 24)
    c.showPage()

def all_routes_page(c, roads, stops, all_points, routes):
    bbox = bbox_of(all_points, 0.03)
    MARGIN, SIDE, TITLE = 90, 1000, 150
    mw, mh, PW, PH = page_size(bbox, 2600, SIDE, MARGIN, TITLE, 3200)
    c.setPageSize((PW, PH))
    view = View(bbox, (MARGIN + 60, MARGIN, mw - 60, mh))
    draw_text(c, PW/2 - SIDE/2, PH - 100, "All Routes", 78, FONT_B, anchor="c")
    grid_and_axes(c, view, 1000, 22)
    draw_roads(c, view, roads, color=(0.86, 0.87, 0.89), width=1.0)
    for r in routes:                                             # a thin dark edge so pale route colours still show on white
        for poly in (r["outPoly"], r["inPoly"]):
            draw_poly(c, view, poly, (0.25, 0.25, 0.3), 6.2, alpha=0.45)
    for r in routes:
        for poly in (r["outPoly"], r["inPoly"]):
            draw_poly(c, view, poly, r["color"], 4.2, alpha=0.95)
        for v in r["variants"]:
            for nodes in (v["outNodes"], v["inNodes"]):
                if nodes: draw_poly(c, view, curve_polyline(nodes), r["color"], 3.0, dash=(9, 6), alpha=0.7)
    for s in stops.values():
        x, y = view.p((s["x"], s["z"]))
        c.saveState(); c.setFillColor(Color(0.2, 0.2, 0.25, alpha=0.55)); c.circle(x, y, 1.8, stroke=0, fill=1); c.restoreState()
    for s in stops.values():
        if s["terminal"]:
            x, y = view.p((s["x"], s["z"])); draw_star(c, x, y, 11)
    # route number tags near the middle of each route
    for r in routes:
        poly = r["outPoly"] or r["inPoly"]
        if len(poly) < 2: continue
        mx, my = view.p(poly[len(poly)//2]); w = 14 + 11*len(r["num"])
        c.saveState(); c.setFillColor(Color(*r["color"])); c.setStrokeColor(white); c.setLineWidth(2)
        c.roundRect(mx - w/2, my - 12, w, 24, 6, stroke=1, fill=1); c.restoreState()
        lum = 0.299*r["color"][0] + 0.587*r["color"][1] + 0.114*r["color"][2]
        draw_text(c, mx, my - 6, r["num"], 17, FONT_B, (0, 0, 0) if lum > 0.6 else (1, 1, 1), "c")
    lx = MARGIN + 60 + mw + 70; ly = PH - TITLE - 10
    draw_text(c, lx, ly, f"Routes ({len(routes)})", 36, FONT_B)
    y = ly - 60
    for r in routes:
        c.saveState(); c.setFillColor(Color(*r["color"])); c.setStrokeColor(Color(0.3, 0.3, 0.3)); c.setLineWidth(1); c.roundRect(lx, y - 6, 70, 26, 5, stroke=1, fill=1); c.restoreState()
        lum = 0.299*r["color"][0] + 0.587*r["color"][1] + 0.114*r["color"][2]
        draw_text(c, lx + 35, y + 1, r["num"], 19, FONT_B, (0, 0, 0) if lum > 0.6 else (1, 1, 1), "c")
        draw_text(c, lx + 85, y + 1, f"{r['destIn']}  {ARROW}  {r['destOut']}", 21)
        y -= 36
    c.showPage()

def stop_label(stops, code): 
    s = stops.get(code); return s["name"] if s else f"({code} — not in the city backup)"

def main_road(route, roads_grid):
    """Name of the road the route spends most of its length on."""
    tally = defaultdict(float)
    for poly in (route["outPoly"], route["inPoly"]):
        for i in range(len(poly) - 1):
            a, b = poly[i], poly[i + 1]; L = math.dist(a, b)
            if L <= 0: continue
            steps = max(1, int(L/25.0))
            for k in range(steps):
                f = (k + 0.5)/steps; m = (a[0] + (b[0] - a[0])*f, a[1] + (b[1] - a[1])*f)
                best = None
                for dx in (-1, 0, 1):
                    for dz in (-1, 0, 1):
                        for nm, px, pz in roads_grid.get((int(m[0]//50) + dx, int(m[1]//50) + dz), ()):
                            d = (px - m[0])**2 + (pz - m[1])**2
                            if best is None or d < best[0]: best = (d, nm)
                if best and best[0] < 20**2: tally[best[1]] += L/steps
    return max(tally, key=tally.get) if tally else ""

def route_page(c, route, roads, stops, roads_grid, city_bbox):
    pts = [p for p in route["outPoly"] + route["inPoly"]]
    for v in route["variants"]:
        pts += [n[0] for n in v["outNodes"] + v["inNodes"]]
    for code in route["outStops"] + route["inStops"]:
        if code in stops: pts.append((stops[code]["x"], stops[code]["z"]))
    if not pts: return
    bbox = widen_to_aspect(bbox_of(pts, 0.12, 600.0))
    MARGIN, SIDE, TITLE = 70, 900, 130
    mw, mh, PW, PH = page_size(bbox, 1500, SIDE, MARGIN, TITLE, 1700)
    via = main_road(route, roads_grid)
    title = f"Route {route['num']}: {route['destIn']} {ARROW} {route['destOut']}" + (f"  (via {via})" if via else "")
    PW = max(PW, pdfmetrics.stringWidth(title, FONT_B, 44) + 2*MARGIN + 60)
    # The side column (legend, two stop lists, small city inset) may be taller than the map: make the page tall enough.
    iw = 420; ih = iw*(city_bbox[3] - city_bbox[1])/(city_bbox[2] - city_bbox[0])
    if ih > 480: ih = 480; iw = ih*(city_bbox[2] - city_bbox[0])/(city_bbox[3] - city_bbox[1])
    longest = max(len(route["outStops"]), len(route["inStops"]), 1)
    side_needed = 330 + len(route["variants"])*30 + longest*20 + 40 + ih + 40
    PH = max(PH, TITLE + side_needed + MARGIN)
    c.setPageSize((PW, PH))
    view = View(bbox, (MARGIN + 50, PH - TITLE - 60 - mh, mw - 50, mh))     # map hangs from the top, under the title
    draw_text(c, MARGIN + 50, PH - 80, title, 44, FONT_B)
    grid_and_axes(c, view, pick_step(max(bbox[2] - bbox[0], bbox[3] - bbox[1])), 14)
    begin_clip(c, view)                       # everything on the map stays inside the map rectangle
    draw_roads(c, view, roads, color=(0.80, 0.81, 0.84), width=1.0, bbox=bbox)
    road_labels(c, view, {k: v for k, v in roads.items()}, 11, min_len_px=80)

    OUT_C, IN_C = (0.88, 0.26, 0.22), (0.15, 0.40, 0.72)
    VCOL = [(0.10, 0.60, 0.35), (0.85, 0.50, 0.05), (0.55, 0.20, 0.70), (0.05, 0.60, 0.65)]
    draw_poly(c, view, route["outPoly"], OUT_C, 4.2)
    draw_poly(c, view, route["inPoly"], IN_C, 4.2, dash=(13, 8))
    for i, v in enumerate(route["variants"]):
        col = VCOL[i % len(VCOL)]
        if v["outNodes"]: draw_poly(c, view, curve_polyline(v["outNodes"]), col, 3.0)
        if v["inNodes"]:  draw_poly(c, view, curve_polyline(v["inNodes"]), col, 3.0, dash=(8, 6))

    # stops: red = outbound only, blue = inbound only, black = both
    out_set, in_set = set(route["outStops"]), set(route["inStops"])
    for code in sorted(out_set | in_set):
        s = stops.get(code)
        if not s: continue
        x, y = view.p((s["x"], s["z"]))
        edge = (0, 0, 0) if (code in out_set and code in in_set) else (OUT_C if code in out_set else IN_C)
        c.saveState(); c.setFillColor(white); c.setStrokeColor(Color(*edge)); c.setLineWidth(1.6); c.circle(x, y, 4.6, stroke=1, fill=1); c.restoreState()
    # terminals: stars with names
    shown, placed = set(), []
    for code in [route["tA"], route["tZ"]] + route["outStops"][:1] + route["outStops"][-1:] + route["inStops"][:1] + route["inStops"][-1:]:
        s = stops.get(code)
        if not s or code in shown: continue
        shown.add(code); x, y = view.p((s["x"], s["z"])); draw_star(c, x, y, 15)
        ly = y + 6
        for _ in range(6):                    # nudge a label down if it would sit on top of another one
            if any(abs(ly - py) < 20 and abs((x + 18) - px) < 260 for px, py in placed): ly -= 22
            else: break
        placed.append((x + 18, ly))
        draw_halo_text(c, x + 18, ly, s["name"], 17, FONT_B)
    end_clip(c)

    # side column: legend + stop lists
    sx = MARGIN + 50 + mw + 50; sy = PH - 100
    draw_text(c, sx, sy, "Legend", 28, FONT_B); sy -= 38
    def key(y, col, dash, text, width=5):
        c.saveState(); c.setStrokeColor(Color(*col)); c.setLineWidth(width)
        if dash: c.setDash(*dash)
        c.line(sx, y + 6, sx + 54, y + 6); c.restoreState(); draw_text(c, sx + 68, y, text, 19)
    key(sy, OUT_C, None, f"Outbound {RARROW} {route['destOut']}"); sy -= 30
    key(sy, IN_C, (13, 8), f"Inbound {RARROW} {route['destIn']}"); sy -= 30
    for i, v in enumerate(route["variants"]):
        col = VCOL[i % len(VCOL)]
        if v["outNodes"] or v["inNodes"]:
            key(sy, col, None, f"Variant {v['letter']}  (own path; dashed = inbound)", 3.5); sy -= 30
        else:
            draw_text(c, sx, sy, f"Variant {v['letter']}: own stops, mainline path", 17, color=(0.3, 0.3, 0.3)); sy -= 26
    draw_star(c, sx + 27, sy + 8, 13); draw_text(c, sx + 68, sy, "terminal / end of line", 19); sy -= 30
    c.saveState(); c.setFillColor(white); c.setStrokeColor(black); c.setLineWidth(1.6); c.circle(sx + 27, sy + 8, 4.6, stroke=1, fill=1); c.restoreState()
    draw_text(c, sx + 68, sy, "stop  (ring: red outbound only, blue inbound only, black both)", 17); sy -= 44

    # stop lists, two columns
    colw = (SIDE - 20)/2
    for ci, (title2, codes, colr) in enumerate((( f"Outbound stops ({len(route['outStops'])})", route["outStops"], OUT_C),
                                                 ( f"Inbound stops ({len(route['inStops'])})", route["inStops"], IN_C))):
        x = sx + ci*(colw + 20); y = sy
        draw_text(c, x, y, title2, 22, FONT_B, colr); y -= 28
        for n, code in enumerate(codes, 1):
            if y < MARGIN: draw_text(c, x, y, "…", 16); break
            name = stop_label(stops, code)
            maxc = int(colw/9.0)
            draw_text(c, x, y, f"{n:>2}. {name[:maxc]}", 15); y -= 20

    # inset: where this route sits in the city
    ix, iy = sx + SIDE - iw, MARGIN - 10
    c.saveState(); c.setFillColor(white); c.setStrokeColor(Color(0.3, 0.3, 0.3)); c.setLineWidth(1); c.rect(ix, iy, iw, ih, stroke=1, fill=1); c.restoreState()
    iv = View(city_bbox, (ix, iy, iw, ih))
    draw_roads(c, iv, roads, color=(0.8, 0.8, 0.83), width=0.6)
    draw_poly(c, iv, route["outPoly"], OUT_C, 2.2); draw_poly(c, iv, route["inPoly"], IN_C, 2.2)
    bx0, by0 = iv.p((bbox[0], bbox[1])); bx1, by1 = iv.p((bbox[2], bbox[3]))
    c.saveState(); c.setStrokeColor(Color(0.1, 0.1, 0.1)); c.setLineWidth(1.2); c.rect(bx0, by0, bx1 - bx0, by1 - by0); c.restoreState()
    draw_text(c, ix, iy + ih + 8, "Where this is in the city", 16, color=(0.3, 0.3, 0.3))
    c.showPage()

def stop_index_pages(c, stops, routes, roads, unplaced=()):
    serving = defaultdict(set)
    for r in routes:
        for code in r["outStops"] + r["inStops"]: serving[code].add(r["num"])
        for v in r["variants"]:
            for code in v["outStops"] + v["inStops"]: serving[code].add(r["num"] + (v["letter"] if v["letter"] != "~" else "~"))
    stops = dict(stops)
    for u in unplaced:
        stops.setdefault(u["stopCode"], {"code": u["stopCode"], "name": u["stopName"] + "   [no road in the backup, not on the maps]",
                                         "terminal": bool(u["isTerminal"]), "road": "-", "roadCode": "", "x": 0, "z": 0})
    allcodes = sorted(stops, key=lambda k: k)
    PW, PH = 1700, 2200; MARGIN = 70; colw = (PW - 2*MARGIN)/2; line_h = 17; per_col = int((PH - 2*MARGIN - 70)/line_h)
    per_page = per_col*2; pages = max(1, math.ceil(len(allcodes)/per_page))
    for p in range(pages):
        c.setPageSize((PW, PH))
        draw_text(c, MARGIN, PH - 60, f"Stop index  ({len(allcodes)} stops)  —  page {p + 1} of {pages}", 30, FONT_B)
        draw_text(c, MARGIN, PH - 86, "code · name · road · routes serving it   (★ = terminal)", 15, color=(0.35, 0.35, 0.35))
        for i, code in enumerate(allcodes[p*per_page:(p + 1)*per_page]):
            s = stops[code]; col, row = divmod(i, per_col)
            x = MARGIN + col*colw; y = PH - MARGIN - 70 - row*line_h
            rts = ",".join(sorted(serving.get(code, []), key=lambda z: (int(re.match(r'\d+', z).group()) if re.match(r'\d+', z) else 0, z)))
            txt = f"{code}  {'★ ' if s['terminal'] else ''}{s['name']}  ·  {s['road']}" + (f"  ·  routes {rts}" if rts else "")
            draw_text(c, x, y, txt[:int(colw/6.6)], 12, FONT_B if s["terminal"] else FONT)
        c.showPage()

# ═══════════════════════════ main ════════════════════════════════════════════════
def main():
    src, exported, roads, stops, unplaced = load_city()
    routes = load_routes()
    # Which stops do routes need that the backup doesn't have?
    missing = sorted({code for r in routes for code in r["outStops"] + r["inStops"] if code not in stops})
    all_points = [p for r in roads.values() for p in r["poly"]] + [(s["x"], s["z"]) for s in stops.values()]
    city_bbox = bbox_of(all_points, 0.03)

    # nearest-road lookup grid for "via ..."
    grid = defaultdict(list)
    for r in roads.values():
        for p in sample_curve(r["ctrl"], 10.0):
            grid[(int(p[0]//50), int(p[1]//50))].append((r["name"], p[0], p[1]))

    only = os.environ.get("ATLAS_ONLY", "")          # preview helper: overview | allroutes | route:34 | stops
    out = os.environ.get("ATLAS_OUT", OUT)
    os.makedirs(os.path.dirname(out), exist_ok=True)
    c = canvas.Canvas(out, pageCompression=1)
    c.setTitle("Transit City — Network Atlas"); c.setAuthor("Headway!")
    if only in ("", "overview"): overview_page(c, roads, stops, all_points, routes)
    if only in ("", "allroutes"): all_routes_page(c, roads, stops, all_points, routes)
    for r in routes:
        if only in ("",) or only == "route:" + r["num"]: route_page(c, r, roads, stops, grid, city_bbox)
    if only in ("", "stops"): stop_index_pages(c, stops, routes, roads, unplaced)
    c.save()
    print(f"wrote {out}")
    print(f"city data: {src} (exported {exported})  roads={len(roads)} stops={len(stops)}  routes={len(routes)}")
    if missing: print(f"WARNING: {len(missing)} route stop(s) not in that backup, left off the maps: {', '.join(missing[:20])}{'…' if len(missing) > 20 else ''}")

if __name__ == "__main__":
    main()
