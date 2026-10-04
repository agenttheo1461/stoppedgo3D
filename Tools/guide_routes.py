"""Route-asset data for the build guide: final node lists (136, N136, 236, 29, 240) and helpers."""
import math, sys, os
sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
import guide_data as G
A = G.A

def N(x, z, c=0): return (x, z, c)
def quadpts(ts):
    P0, P1, P2 = (2500, -2000), (3000, -2000), (3500, -1750)
    return [(round((1-t)**2*P0[0] + 2*(1-t)*t*P1[0] + t*t*P2[0]), round((1-t)**2*P0[1] + 2*(1-t)*t*P1[1] + t*t*P2[1])) for t in ts]
SUNSET_CURVE = quadpts([0.56, 0.78, 1.0])                     # the curved -200th St into the Sunset Point loop
ARC = G.MRBD_NEW
def arc_down(zmax, dx=6):
    return [(x + dx, z) for x, z in ARC if z < zmax - 1]
# ---------------------------------------------------------------------------------------------------------
MERID = G.STOPREC
mp_x = MERID["mA_P"]["x"]; mp2_x = MERID["mA2_P"]["x"]
def meridian_leave(row_z, p_x, exit_dir):
    """First nodes of a route that LEAVES Meridian from the pickup bay: west along its row, down the moved arc, then onto Leaf Blvd."""
    pts = [(p_x, row_z)] + arc_down(row_z)
    last = pts[-1]
    tail = (last[0] + 4, 2995) if exit_dir == "east" else (last[0] + 4, 3003)
    return [N(x, z) for x, z in pts] + [N(*tail)]

NODES_136_OUT = [N(-3698, -652), N(-3698, -112), N(-3328, -112), N(-3328, 358), N(968, 358), N(968, 538), N(998, 538), N(998, 358), N(2985, 358), N(2985, 0, 1),
    N(3014, -130, 1), N(2997, -261, 1), N(2960, -391, 1), N(2963, -522, 1), N(3001, -652, 1), N(3013, -783, 1), N(2981, -913, 1), N(2955, -1043, 1), N(2977, -1174, 1), N(3012, -1304, 1),
    N(3006, -1435, 1), N(2968, -1565, 1), N(2960, -1696, 1), N(2995, -1826, 1), N(3017, -1957, 1)] + [N(x, z, 1) for x, z in SUNSET_CURVE[:-1]] + [N(*SUNSET_CURVE[-1], 0), N(3498, -1998), N(3150, -1998)]
NODES_136_IN = [N(3150, -1998), N(3035, -1998), N(3035, -1998, 1), N(3025, -1957, 1), N(3003, -1826, 1), N(2968, -1696, 1), N(2976, -1565, 1), N(3014, -1435, 1), N(3022, -1304, 1), N(2987, -1174, 1),
    N(2965, -1043, 1), N(2991, -913, 1), N(3023, -783, 1), N(3011, -652, 1), N(2973, -522, 1), N(2970, -391, 1), N(3006, -261, 1), N(3024, -130, 1), N(2995, 0, 1), N(2995, 362),
    N(998, 362), N(998, 538), N(966, 538), N(966, 362), N(-3332, 362), N(-3332, -108), N(-3702, -108), N(-3702, -612), N(-3484, -612), N(-3484, -652), N(-3670, -652)]
NODES_236_OUT = [N(-2930, -2608), N(-2774, -2608), N(-2774, -2698), N(-2965, -2698), N(-2965, -698), N(-3028, -698), N(-3028, -608), N(-3328, -608), N(-3328, 358),
                 N(968, 358), N(968, 538), N(998, 538), N(998, 358), N(2985, 358), N(2985, 0, 1)] + NODES_136_OUT[10:]
NODES_236_IN = [N(3230, -1998)] + NODES_136_IN[1:24] + [N(-3332, 362), N(-3332, -612), N(-3032, -612), N(-3032, -702), N(-2975, -702), N(-2975, -2608), N(-2930, -2608)]
NODES_29_OUT = [N(-2890, -2608), N(-2774, -2608), N(-2774, -2698), N(-2965, -2698), N(-2965, -698), N(-3028, -698), N(-3028, -108), N(-3328, -108), N(-3328, 1998), N(-2158, 1998), N(-2158, 2216), N(-2302, 2216), N(-2302, 2100)]
NODES_29_IN = [N(-2302, 2100), N(-2302, 2002), N(-3332, 2002), N(-3332, -112), N(-3032, -112), N(-3032, -702), N(-2975, -702), N(-2975, -2608), N(-2890, -2608)]

# ── stop lists (codes) ───────────────────────────────────────────────────────
C = G.code_of
S136_OUT = [C("cr_4"), "s0100", C("w333_2"), C("w333_19"), "s0167", "s0165", "s0163", "s0161", "s0159", "s0064", "s0066", "s0026", "s0028", "s0029", "s0031", "s0033",
            C("e125"), C("e175"), C("e199"), C("e225"), C("e275"), C("e299"), "s0572", "s0571", "s0570", "s0569", "s0568", "s0567", "s0566", "s0565", "s0563"]
S136_IN = ["s0564", "s0565", "s0566", "s0567", "s0568", "s0569", "s0570", "s0571", "s0572", C("e299"), C("e275"), C("e225"), C("e199"), C("e175"), C("e125"), "s0033", "s0032", "s0031", "s0029", "s0028", "s0026", "s0066", "s0064",
           "s0159", "s0161", "s0163", "s0165", "s0167", C("w333_19"), C("w333_2"), "s0100", C("cr_3")]
NIGHT_SKIP_OUT = {"s0163", "s0159", "s0066", "s0029", "s0031", C("e125"), C("e225"), "s0571", "s0569", "s0567", "s0565", "s0100"}
SN_OUT = [c for c in S136_OUT if c not in NIGHT_SKIP_OUT]
SN_IN = [c for c in S136_IN if c not in NIGHT_SKIP_OUT and c != "s0032"]
S236_OUT = ["s0332", "s0584", "s0590", C("w333_19"), "s0064", "s0028", "s0033", C("e199"), C("e299"), "s0568", "s0566", C("sun_2")]
S236_IN = [C("sun_2"), "s0566", "s0568", C("e299"), C("e199"), "s0033", "s0028", "s0064", C("w333_19"), "s0590", "s0584", "s0332"]
S29_OUT = [C("up_2"), "s0578", "s0580", "s0582", "s0584", "s0586", "s0588", "s0590", "s0101", C("w333_2"), C("w333_19"), "s0168", "s0170", "s0172", "s0174", "s0176", "s0178", "s0179", "s0180", "s0181", C("nw_2")]
S29_IN = list(reversed(S29_OUT))

def poly(nodes): return [(x, z) for x, z, c in nodes]
def plen(pts): return sum(math.dist(a, b) for a, b in zip(pts, pts[1:]))
def proj_along(pts, p):
    best = (1e18, 0); cum = 0
    for a, b in zip(pts, pts[1:]):
        dx, dz = b[0]-a[0], b[1]-a[1]; L2 = dx*dx + dz*dz; L = math.sqrt(L2)
        t = 0 if L2 == 0 else max(0, min(1, ((p[0]-a[0])*dx + (p[1]-a[1])*dz)/L2))
        d = math.dist(p, (a[0]+dx*t, a[1]+dz*t))
        if d < best[0]: best = (d, cum + t*L)
        cum += L
    return best[1]
def stop_xy(code):
    for k, s in G.STOPREC.items():
        if s["code"] == code: return (s["x"], s["z"])
    s = G.STOPS.get(code)
    return (s["x"], s["z"]) if s else (0, 0)
def minutes_table(nodes, codes, total_min):
    pts = poly(nodes); total = plen(pts) or 1
    pars = [proj_along(pts, stop_xy(c)) for c in codes]
    for i in range(1, len(pars)): pars[i] = max(pars[i], pars[i-1])            # keep the order
    out = []
    for i, (c, p) in enumerate(zip(codes, pars)):
        m = round(total_min * p/total, 1)
        tp = i == 0 or i == len(codes) - 1 or i % 5 == 0
        out.append((c, m, tp))
    return out
def name_of(code):
    for k, s in G.STOPREC.items():
        if s["code"] == code: return s["name"]
    return G.STOPS[code]["name"] if code in G.STOPS else code
if __name__ == "__main__":
    for nm, L in (("136 OUT", NODES_136_OUT), ("136 IN", NODES_136_IN), ("236 OUT", NODES_236_OUT), ("236 IN", NODES_236_IN), ("29 OUT", NODES_29_OUT), ("29 IN", NODES_29_IN)):
        print(nm, len(L), "nodes", round(plen(poly(L))/1000, 1), "km")
    print(len(S136_OUT), len(S136_IN), len(SN_OUT), len(SN_IN), len(S236_OUT), len(S29_OUT))
    print(SUNSET_CURVE)
