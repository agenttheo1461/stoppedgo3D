"""Shared drawing for the terminal-area plans (BEFORE / AFTER maps). Uses the atlas script's loaders."""
import math, os, sys
sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
import make_network_atlas as A
from reportlab.lib.colors import Color

def dense(ctrl, step=6.0):
    n = max(2, int(A.polylen(ctrl)/step))
    return [A.eval_curve(ctrl, i/n) for i in range(n + 1)]

def inbox(p, b, pad=0): return b[0]-pad <= p[0] <= b[2]+pad and b[1]-pad <= p[1] <= b[3]+pad

def draw_arrow(c, view, a, b, color, size=7.0, alpha=1.0):
    pa, pb = view.p(a), view.p(b)
    ang = math.atan2(pb[1]-pa[1], pb[0]-pa[0]); mx, my = (pa[0]+pb[0])/2, (pa[1]+pb[1])/2
    pts = [(mx + size*math.cos(ang), my + size*math.sin(ang)),
           (mx + size*0.7*math.cos(ang+2.5), my + size*0.7*math.sin(ang+2.5)),
           (mx + size*0.7*math.cos(ang-2.5), my + size*0.7*math.sin(ang-2.5))]
    p = c.beginPath(); p.moveTo(*pts[0]); p.lineTo(*pts[1]); p.lineTo(*pts[2]); p.close()
    c.saveState(); c.setFillColor(Color(*color, alpha=alpha)); c.setStrokeColor(Color(1, 1, 1, alpha=0.9)); c.setLineWidth(0.5)
    c.drawPath(p, stroke=1, fill=1); c.restoreState()

def poly_arrows(c, view, poly, bbox, color, every_m=70.0, size=7.0):
    acc = every_m*0.5
    for a, b in zip(poly, poly[1:]):
        L = math.dist(a, b); acc += L
        if acc >= every_m and inbox(a, bbox) and inbox(b, bbox):
            draw_arrow(c, view, a, b, color, size); acc = 0

def draw_area(c, view, bbox, roads, stops, routes, route_filter=None, mode="before", show_names=True, stop_codes=True,
              extra_roads=(), closed_roads=(), dim_routes=False):
    A.begin_clip(c, view)
    c.setFillColor(Color(0.97, 0.975, 0.98)); c.rect(*view.rect, stroke=0, fill=1)
    # roads
    for r in roads.values():
        pts = dense(r["ctrl"], 8.0)
        if not any(inbox(p, bbox, 60) for p in pts): continue
        A.draw_poly(c, view, pts, (0.62, 0.64, 0.68), max(1.6, r["width"]*view.s*1.0))
    for r in roads.values():
        pts = dense(r["ctrl"], 8.0)
        if not any(inbox(p, bbox, 60) for p in pts): continue
        A.draw_poly(c, view, pts, (0.86, 0.87, 0.89), max(0.9, r["width"]*view.s*0.7))
    for pts, width in closed_roads:
        A.draw_poly(c, view, pts, (0.9, 0.2, 0.2), max(2.0, width*view.s), dash=(5, 4), alpha=0.9)
    for pts, width in extra_roads:
        # drawn exactly like the existing roads (casing + fill); only a thin dashed green centre line marks it as proposed
        A.draw_poly(c, view, pts, (0.62, 0.64, 0.68), max(1.6, width*view.s*1.0))
        A.draw_poly(c, view, pts, (0.86, 0.87, 0.89), max(0.9, width*view.s*0.7))
        A.draw_poly(c, view, pts, (0.1, 0.6, 0.3), 0.9, dash=(3, 3), alpha=0.9)
    # road names
    if show_names:
        seen = set()
        for r in roads.values():
            pts = dense(r["ctrl"], 8.0)
            vis = [p for p in pts if inbox(p, bbox)]
            if len(vis) < 4: continue
            m = vis[len(vis)//2]; x, y = view.p(m)
            k = (round(x/50), round(y/14))
            if k in seen: continue
            seen.add(k); A.draw_text(c, x, y + 2.2, r["name"], 6.2, color=(0.33, 0.35, 0.42), anchor="c", alpha=0.9)
    # routes
    for rt in routes:
        if route_filter and rt["num"] not in route_filter: continue
        for poly, dash in ((rt["outPoly"], None), (rt["inPoly"], (4, 2))):
            pts = [p for p in poly]
            A.draw_poly(c, view, pts, rt["color"], 2.4, dash=dash, alpha=0.4 if dim_routes else 0.9)
            poly_arrows(c, view, A.resample(pts, 5.0) if hasattr(A, "resample") else pts, bbox, rt["color"])
    A.end_clip(c)

def resample(poly, step=5.0):
    out = [poly[0]]
    for a, b in zip(poly, poly[1:]):
        L = math.dist(a, b)
        if L == 0: continue
        n = max(1, int(L//step))
        for i in range(1, n+1): out.append((a[0]+(b[0]-a[0])*i/n, a[1]+(b[1]-a[1])*i/n))
    return out
A.resample = resample

def draw_stops(c, view, bbox, stops, labels=True, only=None):
    A.begin_clip(c, view)
    for s in stops.values():
        if not inbox((s["x"], s["z"]), bbox): continue
        if only and s["code"] not in only: continue
        x, y = view.p((s["x"], s["z"]))
        if s["terminal"]: A.draw_star(c, x, y, 6.5)
        else:
            c.saveState(); c.setFillColor(Color(0.1, 0.35, 0.85)); c.setStrokeColor(Color(1, 1, 1)); c.setLineWidth(0.7); c.circle(x, y, 2.6, stroke=1, fill=1); c.restoreState()
        if labels: A.draw_halo_text(c, x + 5, y + 4, f"{s['code']} {s['name'][:26]}", 5.4, color=(0.05, 0.1, 0.4))
    A.end_clip(c)
