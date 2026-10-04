#!/usr/bin/env python3
"""Terminal-area redesign booklet: BEFORE / AFTER maps, route entry arrows, new roads, bays, decor spots.
Run: python3 Tools/make_terminal_plans.py   ->  "Network Maps/Terminal_Area_Redesigns.pdf"
Env: TERM_ONLY=<key> and TERM_OUT=<file> render one area (preview)."""
import math, os, sys
sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
from terminal_plans_lib import *
from reportlab.pdfgen import canvas
from reportlab.lib.colors import Color

OUT = os.path.join(A.ROOT, "Network Maps", "Terminal_Area_Redesigns.pdf")
def quad(p0, p1, p2, n=14): return [((1-t)**2*p0[0]+2*(1-t)*t*p1[0]+t*t*p2[0], (1-t)**2*p0[1]+2*(1-t)*t*p1[1]+t*t*p2[1]) for t in [i/n for i in range(n+1)]]

DECOR = {  # kind: (letter, colour, name)
 "shelter": ("S", (0.15, 0.45, 0.85), "Bus shelter"), "canopy": ("C", (0.1, 0.6, 0.75), "Platform canopy"),
 "trees": ("T", (0.2, 0.62, 0.25), "Trees / planting"), "bench": ("B", (0.55, 0.4, 0.2), "Benches"),
 "kiosk": ("K", (0.85, 0.5, 0.1), "Kiosk / cafe"), "plaza": ("P", (0.55, 0.3, 0.75), "Plaza / paved square"),
 "fountain": ("F", (0.2, 0.55, 0.9), "Fountain / water feature"), "bike": ("R", (0.3, 0.3, 0.3), "Bike racks"),
 "lamp": ("L", (0.9, 0.75, 0.1), "Lamp posts"), "sign": ("i", (0.8, 0.2, 0.2), "Sign / route map board"),
 "clock": ("O", (0.4, 0.4, 0.6), "Clock tower / landmark"), "mural": ("M", (0.85, 0.3, 0.55), "Mural wall / art"),
 "bollard": ("b", (0.35, 0.35, 0.35), "Bollards / kerb separation"), "garden": ("G", (0.4, 0.7, 0.3), "Garden strip"),
 "lot": ("p", (0.5, 0.5, 0.55), "Car / drop-off bay"),
}


def spread(kind, x0, z0, x1, z1, n, label=""):
    out = []
    for i in range(n):
        t = 0.5 if n == 1 else i/(n-1)
        out.append((x0 + (x1-x0)*t, z0 + (z1-z0)*t, kind, label if i == 0 else ""))
    return out

def arc_x_at(arc, z):
    for (xa, za), (xb, zb) in zip(arc, arc[1:]):
        if (za - z)*(zb - z) <= 0 and za != zb: return xa + (xb - xa)*(z - za)/(zb - za)
    return arc[-1][0]

def build_meridian(a, roads):
    arc = [(x - 400, z) for x, z in roads["MRBD"]["ctrl"]]            # the whole arc, 400 m west
    ax = lambda z: arc_x_at(arc, z)
    rows = [(3030, -146, "extend 303rd St"), (3060, -137, "extend 306th St"), (3090, 0, "309th St")]
    a["new_roads"] = [(arc, 7, "MOVED  Meridian Blvd (whole arc 400 m west)")] + [([(x0, z), (ax(z), z)], 7, lab) for z, x0, lab in rows]
    def down(zmax, off): return [(x + off, z) for x, z in arc if z < zmax]
    a["after_paths"] = {
      "1/201/87": ([[(-2, 2950), (-2, 3032), (ax(3032) + 6, 3032)] + down(3032, 6) + [(4, 3004), (4, 2950)]], "row A: 303rd"),
      "199/299/101": ([[(2, 2950), (2, 3062), (ax(3062) + 10, 3062)] + down(3062, 10) + [(8, 2998), (8, 2950)]], "row A2: 306th"),
      "116/216/34": ([[(-470, 3000), (-30, 3000)], [(-30, 2994), (-470, 2994)]], "through bays on Leaf Blvd")}
    a["bay_rows"] = [(-30, 3037, -140, 3037, 4, "A  drop-off (1, 201, 87)"), (-250, 3037, ax(3030) + 25, 3037, 7, "A  pickup + layover"),
                     (-30, 3067, -140, 3067, 4, "A2 drop-off (199, 299, 101)"), (-250, 3067, ax(3060) + 25, 3067, 7, "A2 pickup + layover"),
                     (-60, 3096, ax(3090) + 25, 3096, 14, "row C: layover + extra bays"),
                     (-330, 3012, -60, 3012, 8, "through bays 116 / 216 / 34A (eastbound)"), (-60, 2988, -330, 2988, 8, "through bays (westbound)")]
    a["lots"] = [(ax(3125) + 40, 3102, -70, 3146, "layover lot (north of row C)")]
    a["new_stops"] = [(-30, 3032, "A [D] stays", "KEEP", None), (ax(3030) + 60, 3032, "A [P] moved west", "MOVE", "s0135"),
                      (-30, 3062, "A2 [D] stays", "KEEP", None), (ax(3060) + 60, 3062, "A2 [P] moved west", "MOVE", "s0245"),
                      (-200, 3008, "Leaf [E] 116/216/34A", "NEW", None), (-200, 2992, "Leaf [W] 116/216/34A", "NEW", None),
                      (-250, 3092, "C [D] (row C)", "NEW", None)]
    mid = (ax(3045) + 30 - 60)/2
    a["decor"] = (spread("canopy", -80, 3048, ax(3045) + 40, 3048, 5, "island canopies") + spread("bench", -100, 3050, ax(3045) + 60, 3050, 5, "benches") +
                  spread("trees", -60, 3078, ax(3075) + 40, 3078, 6, "tree line") + [(mid, 3048, "plaza", "central plaza"), (mid, 3050, "fountain", ""), (-20, 3048, "sign", "route map"),
                  (ax(3070) + 10, 3075, "clock", "clock tower (arc)"), (ax(3010) + 20, 2985, "bike", "bike hub"), (80, 3075, "kiosk", "station cafe")] +
                  spread("lamp", -60, 3120, ax(3120) + 60, 3120, 5, "lamps"))

AREAS = [
 dict(key="parkview", title="PARKVIEW", sub="Routes 1, 201, 73, 87, 140, 240  (29 now goes to NW Point)",
  bbox=(-960, -1345, 125, -925), routes=["1", "201", "73", "87", "140", "240"],
  problems=[
   (-190, -1170, "-117th St stops dead at Midway and at -20th Av. Routes 140 and 240 drive ~380 m over grass (no road from -38th Av to Midway)."),
   (-440, -1150, "Routes 1 and 201 leave Commons south, east, north past the garage, then east on 105th: a ~600 m lap to reach Midway 150 m away."),
   (-390, -1210, "Garage [E] and Bay Block stops are 20 m apart on the twin 38th/40th Av stubs: no layover, buses queue on the through lane."),
   (-380, -980, "Stop s0034 'Parkview Terminal' is on no route at all."),
   (-251, -1050, "Both [DROPOFF] stops sit in the live lane of 105th St, so a dwelling bus blocks Midway traffic."),
   (-810, -1075, "Parkview A (110th St) and B (105th St) are two separate bay roads 50 m apart. Once 29 leaves for NW Point only 73 and 87 are left, and they do not need two roads.")],
  new_roads=[([(-380, -1170), (0, -1170)], 7, "-117th St (extended west to -38th Av)"),
             ([(-200, -1250), (0, -1250)], 7, "-125th St (extended east to Midway)"),
             ([(0, -1170), (0, -1250)], 7, "Midway (extended south to 125th)")],
  remove_roads=[],
  after_paths={"1/201": ([[(-502, -1200), (-502, -1250), (-2, -1250), (-2, -1170), (-2, -1055), (-2, -900)],
                          [(-2, -900), (-2, -1045), (-502, -1045), (-502, -1120)]], "straight, then directly onto 0th Av"),
               "140": ([[(-380, -1230), (-378, -1172), (2, -1172), (402, -1172)],
                        [(398, -1168), (-2, -1168), (-2, -1248), (-378, -1248), (-378, -1215)]],
                       "140  OUT: -117th link east.  IN: down new Midway stretch, west on 125th"),
               "240": ([[(-380, -1240), (-378, -1176), (2, -1176), (442, -1176)],
                        [(438, -1164), (-4, -1164), (-4, -1246), (-376, -1246), (-376, -1215)]],
                       "240  same split: OUT on the link, IN down Midway then 125th (no more -20th Av)"),
               "73/87": ([[(-403, -1046), (-908, -1046), (-908, -702)]], "both load on 105th west, then north on -91st Av")},
  bays=[],
  bay_rows=[(-500, -1200, -500, -1130, 3, "Commons P (1, 201)"), (-500, -1100, -500, -1070, 2, "Commons D"), (-380, -1228, -380, -1195, 2, "Garage bays (140, 240)"),
            (-350, -1176, -230, -1176, 4, "layover on the link"), (-480, -1244, -230, -1244, 8, "bays on 125th (140/240 inbound)"),
            (-770, -1056, -890, -1056, 4, "WEST bays (73, 87): one road"), (-720, -1095, -900, -1095, 6, "layover on 110th")],
  lots=[(-370, -1243, -210, -1179, "layover yard")],
  new_stops=[(-300, -1250, "Parkview Garage [D] 140/240 inbound", "NEW", None), (-290, -1165, "Parkview Link [E] (replaces dead s0034)", "MOVE", "s0034"),
             (-790, -1050, "Parkview West [D] 73/87", "MOVE", "s0406"), (-860, -1050, "Parkview West [P] 73/87", "MOVE", "s0407"),
             (-760, -1100, "A [D] removed (29 left, merged into West)", "REMOVE", "s0404"), (-860, -1100, "A [P] removed (29 left)", "REMOVE", "s0405")],
  changes=["Build three short pieces of road: -117th St link (380 m), -125th St extension (200 m) and a Midway south extension (80 m). Each links into the existing grid at two points or more. Total 660 m.",
           "Route 1 and 201: leave Commons P south, left onto 125th, straight east 500 m, left directly onto 0th Av. Two turns, no lap of the block.",
           "Routes 140 AND 240 OUTBOUND: out of the garage, north to the -117th link, straight east to Midway and on (140 turns north at 40th Av, 240 at Berrelingway). INBOUND (to Parkview): both arrive on 117th, turn DOWN the new Midway stretch, then west along 125th into the garage bays. 240 no longer comes in by -20th Av; edit the inbound nodes of BOTH route assets.",
           "Parkview West: Route 29 leaves (it now runs University Park <-> NW Point), so 73 and 87 are the only two routes: they share ONE road, the 105th St bays, then north on -91st Av. 110th St stays as layover and as a second way in and out (it joins -71st Av and -91st Av).",
           "Stops: s0406/s0407 move to the shared West bays; s0404/s0405 (Parkview A, only route 29 used them) are removed; s0034 becomes the Link stop; a new Garage [D] stop on 125th.",
           "Parkview Commons becomes the 'front door': canopy platforms on 50th Av, courtyard inside the block, route-map board at each bay."],
  rules=["140/240 inbound uses the new Midway stretch; outbound never uses 125th.",
         "Route 1/201 may not use 105th or 38th Av to leave Parkview.",
         "West bays: 73 and 87 only, one-way westbound on 105th; layover buses wait on 110th only."],
  decor=[(-450, -1150, "plaza", "Commons courtyard"), (-440, -1120, "trees", "tree line"), (-470, -1175, "bench", "benches"), (-440, -1200, "kiosk", "cafe kiosk"),
         (-500, -1075, "canopy", "D canopy"), (-500, -1225, "canopy", "P canopy"), (-390, -1190, "shelter", "garage shelter"), (-300, -1215, "bike", "bike rack"),
         (-110, -1235, "lamp", "lamps on 125th"), (-100, -1185, "lamp", ""), (-250, -1160, "sign", "route map"), (-290, -1170, "garden", "garden strip"),
         (-830, -1063, "canopy", "West canopy"), (-790, -1063, "shelter", "West shelter"), (-860, -1110, "trees", "")]),

 dict(key="meridian", title="MERIDIAN SQUARE (main station)", sub="Routes 1, 87, 101, 199, 201, 299 terminate; 116, 216, 34A pass",
  bbox=(-640, 2875, 215, 3195), routes=["1", "201", "87", "101", "199", "299", "116", "216", "34"], build=build_meridian,
  problems=[
   (-150, 3010, "The Meridian Blvd arc closes the station only 150 m from 0th Av. The stubs are 146 m long: room for two bays each."),
   (-70, 3045, "Pickup and dropoff share the same short stub, so a dwelling bus blocks the next one; nowhere for a layover."),
   (-120, 2995, "Routes 116, 216 and 34A pass along Leaf Blvd with no stop beside the station, so passengers walk across the arc."),
   (-8, 3015, "Every bus arrives northbound on 0th Av and turns left across traffic into 303rd or 306th with no turn pocket."),
   (-60, 3120, "The land between 306th St and Nan Rd is empty: no layover, no third row.")],
  new_roads=[], remove_roads=["MRBD"], after_paths={}, bays=[], bay_rows=[], lots=[], new_stops=[],
  changes=["Keep Meridian Blvd but move the WHOLE arc 400 m west. It still meets Nan Rd (z=3150) at the top and Leaf Blvd (z=3000) at the bottom, so it links to the existing grid at two points.",
           "Only three rows: 303rd, 306th and 309th St, extended west to the moved arc. 303rd and 306th carry the routes, 309th is the layover row; the empty land north of it becomes a layover lot.",
           "Passing buses (116, 216, 34A) get eight kerb bays each way on Leaf Blvd along the south edge of the station, plus new through stops there.",
           "The two stops P (s0135, s0245) move west to the new bays; new stops on Leaf Blvd for the passing buses.",
           "Each island between rows is 23 m wide: canopies, benches, planting, route boards; the arc end becomes the clock tower.",
           "Protected left-turn pocket on 0th Av into the rows; exit stays right onto 0th Av south."],
  rules=["Routes enter by the east end of their row, drop off, wait at the west end, leave south down the arc and east on Leaf Blvd.",
         "116/216/34A never enter the rows; they stop at the Leaf Blvd through bays.",
         "Row C (309th) is staging; a bus leaves it only when its pickup bay is free."],
  decor=[]),

 dict(key="meridianb", title="MERIDIAN SQUARE B (backlot)", sub="Route 85 now; room for more",
  bbox=(-790, 3370, -440, 3960), routes=["85"],
  problems=[
   (-690, 3520, "Route 85 arrives southbound and makes a 143° hairpin at the south end to reach D, and another to reach P: it uses the loop both ways."),
   (-672, 3575, "D and P are 270 m apart at opposite ends of a curved road; two stops on a bend, not a terminal."),
   (-690, 3700, "The curved D shape has no layover, no sheltered platforms and nowhere to park, and it only connects to -70th Av.")],
  new_roads=[([(-700, 3500), (-540, 3500), (-540, 3900), (-700, 3900)], 9, "350th / 390th St + -54th Av (ring, joins -70th Av)"), ([(-620, 3500), (-620, 3900)], 7, "-62nd Av"),
             ([(-540, 3500), (-540, 3400)], 7, "-54th Av (extended south to 340th St)"), ([(-620, 3500), (-620, 3400)], 7, "-62nd Av link to 340th St")],
  remove_roads=["MRSQD"],
  after_paths={"85": ([[(-702, 4098), (-702, 3502), (-540, 3502), (-540, 3900), (-698, 3900), (-698, 4102)]], "one counter-clockwise lap: D then P on the east side, no hairpins")},
  bays=[],
  bay_rows=[(-532, 3540, -532, 3700, 6, "D alight (east kerb)"), (-532, 3720, -532, 3860, 6, "P pickup + layover (east kerb)"),
            (-626, 3560, -626, 3840, 10, "layover (west of spine)"), (-614, 3560, -614, 3840, 10, "layover (east of spine)")],
  lots=[(-692, 3510, -628, 3890, "parking-lot layover"), (-612, 3510, -548, 3890, "parking-lot layover")],
  new_stops=[(-532, 3600, "B [D] moved to the east kerb", "MOVE", "s0478"), (-532, 3800, "B [P] moved to the east kerb, further on", "MOVE", "s0479"), (-532, 3740, "B [P2] spare bay", "NEW", None)],
  changes=["Replace the D-shaped curve by a rectangular ring (160 x 400 m) with a spine: a proper bus parking lot.",
           "Link it to the world: the ring joins -70th Av on its west side and two new 100 m links join the ring and the spine to 340th St, so buses (and later more routes) can come in from the south too.",
           "Route 85: south on -70th Av, east along the south side, north up the east side past D then P (D first, P further on, so the next trip starts ahead of where the last one ended), west along the north side and back out north. One lap, no reversing.",
           "The two lots either side of the spine (about 20 bays) give the main station somewhere to push layover.",
           "Stops: D and P both move to the east side (D at z=3600, P at z=3800), plus a spare bay stop between them."],
  rules=["85 enters at the south-west corner and leaves at the north-west corner, counter-clockwise only (the south links to 340th St are for extra routes).", "Spine is bus-only; no standing on the ring."],
  decor=spread("lamp", -540, 3540, -540, 3860, 6, "light poles") + spread("trees", -586, 3530, -586, 3870, 8, "tree islands") +
        spread("shelter", -552, 3560, -552, 3700, 3, "D shelters") + spread("canopy", -630, 3600, -630, 3800, 3, "P canopies") +
        [(-580, 3700, "sign", "wayfinding"), (-660, 3700, "bike", "bike racks")]),

 dict(key="berrel", title="BERRELINGWAY NORTH", sub="Routes 114, 116, 140, 216, 240",
  bbox=(300, 2765, 600, 3040), routes=["114", "116", "140", "216", "240"],
  problems=[
   (440, 2921, "Stop 'Berrelingway North Station' sits on Berrelingway at x=440, but every route line uses 40th Av at x=400: the stop is 40 m from where buses drive."),
   (445, 2900, "Routes 140 and 240 inbound drive the block twice (two laps) to turn round."),
   (490, 2975, "One platform (49th Av) for five routes, and the 116/216 line ends 45 m short of it."),
   (400, 3000, "The whole 'terminal' is a 90 x 100 m block of lanes: no layover, no room.")],
  new_roads=[([(560, 2790), (560, 3000)], 7, "56th Av (joins Leaf Blvd)"), ([(400, 2790), (560, 2790)], 7, "279th St (joins 40th Av)"), ([(494, 2900), (560, 2900)], 7, "extend connector to East Road")],
  remove_roads=[],
  after_paths={"140/240": ([[(402, 2700), (402, 2900), (558, 2900), (558, 2795), (402, 2795), (402, 2700)]], "Station bays, lot, back south: one lap"),
               "114": ([[(402, 2002), (402, 2995), (488, 2995), (488, 2970)]], "114 unchanged: 40th Av north, Leaf east, 49th Av south")},
  bays=[],
  bay_rows=[(484, 2985, 484, 2925, 3, "Local bays (114, 116, 216)"), (440, 2906, 550, 2906, 5, "Station bays (140, 240)"), (410, 2796, 550, 2796, 7, "layover")],
  lots=[(410, 2798, 552, 2888, "layover lot")],
  new_stops=[(480, 2906, "Station [D/P] moved onto the connector", "MOVE", "s0089"), (400, 2960, "North [D] 40th Av (114 drop-off)", "NEW", None),
             (545, 2906, "Station [2] 140/240", "NEW", None), (484, 2955, "Local stays (49th Av)", "KEEP", None)],
  changes=["The terminal becomes a rectangle built mostly from existing roads: Leaf Blvd (north), 40th Av (west) and Berrelingway (down the middle) already exist; add East Road (x=560) and South Road (z=2790). East Road joins Leaf Blvd, South Road joins 40th Av.",
           "Extend the connector east to East Road: it is the Station platform road. 140 and 240 board there, then go on to the layover lot on the south side.",
           "114, 116 and 216 keep 49th Av as their Local platform; 116/216 still arrive along Leaf Blvd, 114 up 40th Av. Only the 140 and 240 laps change.",
           "Stops: the Station stop moves onto the connector, a second Station stop and a North drop-off on 40th Av are added."],
  rules=["140/240: in on 40th Av, east on the connector (Station bays), south on East Road into the lot, west on South Road, out on 40th Av.",
         "114: in on 40th Av north, east on Leaf, south on 49th Av (Local). 116/216: unchanged."],
  decor=spread("canopy", 450, 2912, 540, 2912, 3, "Station canopies") + spread("trees", 570, 2800, 570, 2990, 5, "tree row") +
        [(520, 2945, "plaza", "station court"), (520, 2945, "fountain", ""), (470, 2945, "shelter", "Local shelter"), (445, 2935, "bench", "benches"),
         (520, 2995, "sign", "route board"), (480, 2840, "lamp", "lamps"), (540, 2840, "lamp", ""), (420, 2840, "bike", "bike racks")]),

 dict(key="southpier", title="SOUTH PIER", sub="Routes 21, 45, 199, 299",
  bbox=(1640, -2075, 2060, -1850), routes=["21", "45", "199", "299"],
  problems=[
   (1990, -2000, "Routes 21, 199 and 299 turn left from -200th St onto 199th Av northbound across southbound traffic with no pocket."),
   (1700, -1950, "All four routes share one pickup bay on 170th Av, so a layover for one blocks the next."),
   (1850, -1950, "The 290 x 100 m rectangle is empty inside: no platform, no layover, no parking.")],
  new_roads=[([(1700, -1935), (1990, -1935)], 7, "-193rd St (extended east)")],
  remove_roads=[],
  after_paths={"21/199/299": ([[(1992, -1860), (1992, -1902), (1702, -1902), (1702, -1937), (1988, -1937), (1988, -1860)]], "D on -190th, P on Platform Road"),
               "45": ([[(2060, -1999), (1702, -1999), (1702, -1937), (1986, -1937), (1986, -1997), (2060, -2001)]], "45: its own bays on -200th, round the loop, back east")},
  bays=[],
  bay_rows=[(1940, -1907, 1760, -1907, 7, "D drop-off (all four routes)"), (1760, -1942, 1940, -1942, 7, "P pickup row"), (1960, -1993, 1740, -1993, 7, "45 bays on -200th St (westbound kerb)")],
  lots=[(1715, -1986, 1980, -1946, "layover lot")],
  new_stops=[(1930, -1907, "South Pier D1", "NEW", None), (1850, -1907, "South Pier D2 (moved s0246)", "MOVE", "s0246"), (1790, -1907, "South Pier D3", "NEW", None),
             (1790, -1942, "South Pier P1 (moved s0247)", "MOVE", "s0247"), (1860, -1942, "South Pier P2", "NEW", None), (1930, -1942, "South Pier P3", "NEW", None),
             (1940, -1993, "South Pier [45] D  (45 section)", "NEW", None), (1860, -1993, "South Pier [45] 2", "NEW", None), (1780, -1993, "South Pier [45] P", "NEW", None)],
  changes=["The rectangle already uses four existing roads (-200th St, 170th Av, -190th St, 199th Av). Platform Road across the middle joins 170th Av and 199th Av, splitting it into a drop-off island on -190th St and a pickup island on Platform Road; the south part becomes the layover lot.",
           "21, 199 and 299 drop on -190th St, pick up on Platform Road and exit on 199th Av. 45 stays on -200th St: west along its own bays, up 170th Av, east on Platform Road, south on 199th Av and back east.",
           "Stops: three drop-off stops on -190th St and three pickup stops on Platform Road for 21, 199 and 299; Route 45 gets its own three stops on its section of -200th St (it comes in along -200th, loads there, then loops round and goes back east).",
           "Protected left-turn pocket on -200th St into 199th Av."],
  rules=["Enter from 199th Av, west on -190th (drop), 170th Av south, east on Platform Road (pick up), exit via 199th Av.",
         "Layover lot is reached from the west end only."],
  decor=spread("canopy", 1930, -1921, 1780, -1921, 4, "island canopies") + spread("trees", 1950, -1921, 1760, -1921, 5, "tree row") +
        [(1850, -1921, "plaza", "pier plaza"), (1850, -1921, "fountain", ""), (1990, -1920, "sign", "ferry / route map"),
         (1700, -1921, "clock", "pier clock"), (1930, -1972, "lamp", "lamps"), (1770, -1972, "lamp", ""), (1850, -1972, "bike", "bike racks")]),

 dict(key="northbeach", title="NORTH BEACH", sub="Routes 34, 45, 114",
  bbox=(2760, 2985, 3040, 3430), routes=["34", "45", "114"],
  problems=[
   (2950, 3040, "Route 114 inbound drives south from the stop and then reverses north: a 173° U-turn."),
   (2970, 3304, "D (34) and P (45, 114) are 200 m apart on two parallel roads; transfers walk across the beach."),
   (2920, 3200, "295th Av and 299th Av are only 40 m apart: no room for platforms or a layover."),
   (2990, 3000, "45 and 114 arrive via Leaf Blvd and turn left onto 295th with no pocket.")],
  new_roads=[([(2800, 3000), (2800, 3400)], 7, "280th Av (joins Leaf Blvd and 340th St)"), ([(2800, 3200), (2950, 3200)], 14, "320th St (Beach Mall)")],
  remove_roads=[],
  after_paths={"45/114": ([[(2995, 2138), (2995, 3002), (2952, 3002), (2952, 3205), (2802, 3205), (2802, 3398), (2985, 3398), (2985, 2900)]], "295th north, WEST along the Mall, up 280th Av, east on 340th"),
               "34": ([[(2000, 3398), (2948, 3398), (2948, 3209), (2804, 3209), (2804, 3402), (2000, 3402)]], "REVERSED: down 295th, WEST along the Mall (same side as 45), up 280th Av, back west")},
  bays=[],
  bay_rows=[(2940, 3213, 2812, 3213, 8, "ONE side of the Mall (north kerb): 34, 45 and 114 all load here"), (2940, 3187, 2812, 3187, 8, "layover (south kerb)"),
            (2806, 3050, 2806, 3170, 6, "layover (280th Av south)")],
  lots=[(2812, 3010, 2940, 3180, "layover lot")],
  new_stops=[(2865, 3213, "Beach Mall [P] 34/45/114 (moved s0512)", "MOVE", "s0512"), (2905, 3213, "Beach Mall [D] 34 (moved s0511)", "MOVE", "s0511"),
             (2825, 3213, "Beach Mall [3] spare", "NEW", None), (2935, 3213, "Beach Mall [4] spare", "NEW", None), (2950, 3300, "295th [D] 34 drop", "NEW", None)],
  changes=["Use roads that exist: Leaf Blvd (south), 340th St (north) and 295th Av (east) are three sides of a 150 x 400 m rectangle. The only new road is 280th Av, which joins Leaf Blvd and 340th St, plus the Beach Mall across the middle.",
           "Route 34/34A is REVERSED through the terminal: it comes east on 340th, turns down 295th Av, and runs WEST along the Mall, so it and 45/114 load on the same kerb, same direction. Then 280th Av north and back west on 340th St.",
           "The Mall is one-way westbound (14 m, two lanes): all three routes use the north kerb, the south kerb is layover.",
           "114 no longer U-turns. Stops: s0512 and s0511 move onto the Mall's north kerb, two more stops, and a 295th Av drop-off for 34.",
           "The south half is the layover lot."],
  rules=["Beach Mall is westbound only; 34 loads on the same kerb as 45/114.", "34 enters via 295th Av southbound, 45/114 via 295th Av northbound; both turn onto the Mall.", "Protected left-turn pocket from Leaf Blvd onto 295th."],
  decor=spread("trees", 2830, 3150, 2930, 3150, 4, "palms") + [(2875, 3225, "plaza", "kerb plaza"), (2875, 3226, "fountain", "water feature"), (2905, 3240, "kiosk", "beach cafe")] +
        spread("canopy", 2830, 3220, 2930, 3220, 4, "Mall canopies") + spread("bench", 2900, 3030, 2900, 3160, 3, "benches") +
        [(2800, 3290, "mural", "beach mural"), (2985, 3200, "bike", "bike racks"), (2925, 3330, "shelter", "shelter")] + spread("lamp", 2825, 3030, 2825, 3380, 4, "lamps")),

 dict(key="crestbury", title="CRESTBURY", sub="Routes 21, 116, 216 and 136",
  bbox=(-3900, -785, -3430, -535), routes=["21", "116", "216", "136"],
  problems=[
   (-3725, -682, "Three routes share one stop on a 90 m stub that opens right onto -70th St: dwell blocks the junction."),
   (-3642, -700, "The dropoff stop is in the live lane of -70th St."),
   (-3600, -655, "21 comes from the north, 116/216 from the east: they have no shared platform, so transfers mean walking round the block.")],
  new_roads=[([(-3480, -700), (-3480, -610)], 7, "-348th Av (joins 61st and 70th St)"), ([(-3700, -655), (-3480, -655)], 7, "-65th St (Terminal Mall)")],
  remove_roads=["W375AVE"],
  after_paths={"21/136": ([[(-3330, -110), (-3702, -110), (-3702, -612), (-3482, -612), (-3482, -652), (-3698, -652), (-3698, -112)]], "21 and 136: in on 61st, ONE shared Mall, out north on 370th (136 turns east on -11th St)"),
               "116/216": ([[(-3300, -698), (-3482, -698), (-3482, -658), (-3698, -658), (-3698, -698), (-3300, -702)]], "70th, East Road, same Mall, out via 70th")},
  bays=[],
  bay_rows=[(-3500, -651, -3680, -651, 8, "ONE Mall: 21, 116, 216 and 136 all load here (north kerb)"), (-3500, -662, -3680, -662, 8, "layover (south kerb)")],
  lots=[(-3690, -690, -3490, -666, "layover / parking"), (-3690, -644, -3490, -618, "plaza island")],
  new_stops=[(-3560, -651, "Crestbury [1] (moved s0092)", "MOVE", "s0092"), (-3620, -651, "Crestbury [2] (moved s0099)", "MOVE", "s0099"), (-3670, -651, "Crestbury [3]", "NEW", None),
             (-3640, -616, "61st [D] 21 / 136", "NEW", None), (-3700, -651, "Crestbury [4] 136 (full-length local to Sunset Point)", "NEW", None)],
  changes=["All four routes load on ONE road, the Terminal Mall at z=-655, so every transfer is across a platform. 21 and 136 come from the north (136 along -11th St and -370th Av), 116/216 from the east, they just meet here. 136 is the long local that runs the whole way to Sunset Point.",
           "The block is built on existing roads: 61st St and 70th St are its long sides, 370th Av its west side. New East Road joins 61st and 70th St, the Terminal Mall joins 370th Av and East Road: every new piece links at two points.",
           "Retire the 375th Av stub: its bays move to the Mall.",
           "21 and 136: south on 370th, east on 61st, south on East Road, west along the Mall, north on 370th. 116/216: 70th St, up East Road, west along the Mall, 370th and 70th out."],
  rules=["Mall is one-way westbound; three bays in a row.", "21 and 136 never use 70th St; 116/216 never use 61st."],
  decor=spread("canopy", -3680, -636, -3520, -636, 4, "platform canopies") + spread("trees", -3680, -628, -3520, -628, 5, "plaza trees") +
        [(-3600, -636, "plaza", "town square"), (-3600, -636, "fountain", ""), (-3700, -636, "sign", "route board"), (-3560, -678, "bike", "bike racks"),
         (-3640, -636, "bench", "benches"), (-3800, -600, "garden", "Crestbury green")] + spread("lamp", -3680, -705, -3520, -705, 3, "lamps")),

 dict(key="nwpoint", title="NW POINT", sub="Routes 25 and 29  (136 moved to University Park)",
  bbox=(-2440, 1975, -2090, 2245), routes=["25", "29"],
  problems=[
   (-2300, 2110, "One stop in the middle of a through street. Route 25 stays and Route 29 arrives, so two routes need two real bays."),
   (-2302, 2120, "Route 25's line starts 10 m diagonally off the road at the stop."),
   (-2220, 2110, "A 220 m stub with nothing beside it: no bays, no layover.")],
  new_roads=[], remove_roads=[],
  after_paths={"25": ([[(-2158, 2218), (-2302, 2218), (-2302, 2002), (-900, 2002)]], "230th Av bays, out east"),
               "29": ([[(-3318, 1998), (-2162, 1998), (-2162, 2214), (-2306, 2214), (-2306, 2006), (-3318, 2006)]], "29: east on 200th, up -216th Av, west on 222nd, down 230th Av bays, back west")},
  bays=[],
  bay_rows=[(-2306, 2040, -2306, 2190, 7, "ONE road: 25 and 29 share 230th Av"), (-2170, 2040, -2170, 2190, 6, "layover on -216th Av (west kerb)")],
  lots=[(-2285, 2030, -2185, 2210, "layover lot")],
  new_stops=[(-2306, 2150, "NW Point [1] 25 (moved s0184)", "MOVE", "s0184"), (-2306, 2100, "NW Point [2] 29", "NEW", None), (-2306, 2050, "NW Point [3] spare", "NEW", None)],
  changes=["136 moved to University Park; NW Point now has route 25 and the re-routed 29 (University Park <-> NW Point via the new -290th Av).",
           "No new road: -216th Av already runs along the east side (140 m from 230th Av), so 230th Av, 200th St, 222nd St and -216th Av form an existing rectangle. The space inside is the layover lot.",
           "Two routes, one road: both load on 230th Av southbound. 25 comes in along 222nd and leaves east on 200th; 29 comes in from the west along 200th, loops east, up -216th Av and west along 222nd to reach the same southbound bays, then leaves west on 200th.",
           "Stops: s0184 moves to the first bay, a second stop for 29 and a spare."],
  rules=["Both routes load southbound on 230th Av; 25 leaves east, 29 leaves west.", "29 enters the stub from 222nd St, never from 200th St."],
  decor=spread("canopy", -2300, 2060, -2300, 2180, 3, "platform canopy") + [(-2260, 2120, "plaza", "plaza"), (-2260, 2120, "fountain", ""),
         (-2325, 2215, "garden", "lookout garden")] + spread("trees", -2180, 2040, -2180, 2200, 4, "tree row") + spread("lamp", -2260, 2030, -2260, 2200, 3, "lamps") + [(-2260, 2060, "sign", "route board")]),

 dict(key="southside", title="SOUTHSIDE", sub="Routes 7, 10",
  bbox=(780, -2735, 1060, -2475), routes=["7", "10"],
  problems=[
   (928, -2650, "The route lines break: inbound stops at z=-2600, outbound starts at z=-2660, a 60 m gap."),
   (930, -2696, "Pickup P sits exactly on the junction with -270th/-271st: a waiting bus blocks the junction."),
   (860, -2600, "There is empty land beside the stops: no layover, no bays.")],
  new_roads=[([(830, -2500), (830, -2700)], 7, "83rd Av (joins -250th and -270th St)")],
  remove_roads=[],
  after_paths={"7": ([[(928, -2498), (928, -2696), (830, -2698), (780, -2698)]], "west on -270th"), "10": ([[(928, -2498), (928, -2708), (1002, -2710)]], "east on -271st")},
  bays=[],
  bay_rows=[(936, -2525, 936, -2680, 8, "ONE road: 7 and 10 share 93rd Av"), (846, -2520, 846, -2690, 8, "layover row"), (906, -2520, 906, -2690, 8, "layover row")],
  lots=[(838, -2692, 922, -2508, "parking-lot layover")],
  new_stops=[(930, -2640, "Southside [P] (moved s0292, off the junction)", "MOVE", "s0292"), (930, -2580, "Southside [2]", "NEW", None), (930, -2540, "Southside [D] (s0291 shifted)", "MOVE", "s0291")],
  changes=["Two routes, one road: 93rd Av gets eight bays, shared by 7 and 10. It already links -250th St and -270th St at both ends.",
           "West Road (x=830) links the same two streets; the lot between it and 93rd Av is the layover.",
           "Move P back 55 m from the junction, add a middle stop, and make both route lines continuous from D to P."],
  rules=["Enter from -250th St, load on 93rd Av, 7 leaves west on -270th, 10 east on -271st."],
  decor=spread("lamp", 860, -2520, 860, -2690, 5, "lamps") + spread("trees", 875, -2530, 875, -2680, 5, "tree islands") + spread("shelter", 922, -2540, 922, -2660, 2, "shelters") +
        [(975, -2600, "garden", "Southside Green"), (975, -2560, "trees", ""), (975, -2640, "trees", ""), (915, -2600, "sign", "route board"), (960, -2600, "bench", "benches")]),

 dict(key="univpark", title="UNIVERSITY PARK", sub="Routes 7, 29 and 236 (Express-Max)",
  bbox=(-3170, -2775, -2690, -2465), routes=["7", "29", "236"],
  problems=[
   (-2900, -2640, "-297th Av ends in the Art Blvd curve, which sweeps east. A bus coming down 297th cannot turn west into the terminal without leaving the road: 29 drives ~290 m over the grass to get there."),
   (-2985, -2700, "The only stop is a street stop on -270th St and Route 7's line ends 75 m short of it."),
   (-3035, -2600, "The block west of 297th Av has road on all four sides but nothing uses it."),
   (-2840, -2560, "The 200 x 200 m land east of 297th Av is empty: that is where a real campus transit centre fits, with room for the new 236 Express-Max buses.")],
  new_roads=[([(-2970, -2505), (-2970, -2700)], 14, "-297th Av (extended south to -270th St)"),
             ([(-2970, -2505), (-2770, -2505)], 7, "-250th St (extended east)"),
             ([(-2770, -2505), (-2770, -2700)], 7, "-277th Av (joins -250th and -270th St)"),
             ([(-2970, -2605), (-2770, -2605)], 14, "-260th St (Campus Drive)")],
  remove_roads=["ARBD"],
  after_paths={"29/236": ([[(-2972, -2400), (-2972, -2608), (-2772, -2608), (-2772, -2698), (-2968, -2698), (-2968, -2400)]], "29 and 236: down -297th Av, left onto Campus Drive, right on -277th, west on -270th, north on 297th"),
               "7": ([[(-2690, -2698), (-2968, -2698), (-2968, -2604), (-2772, -2604), (-2772, -2708), (-2690, -2708)]], "7: west on -270th, north on 297th, right onto Campus Drive, out east on -271st")},
  bays=[],
  bay_rows=[(-2950, -2613, -2790, -2613, 8, "ONE Campus Drive kerb (south side): [1] 236, [2][3] 29, [4] 7"), (-2955, -2511, -2790, -2511, 8, "layover on -250th St"), (-2955, -2590, -2790, -2590, 8, "layover (Campus Drive north kerb)"),
            (-3106, -2520, -3106, -2690, 8, "layover in the old block (-310th Av)")],
  lots=[(-2960, -2580, -2780, -2520, "layover lot (236 Express-Max artics park here)"), (-2960, -2695, -2780, -2620, "campus plaza")],
  new_stops=[(-2930, -2613, "University Park [1] 236 (moved s0332)", "MOVE", "s0332"), (-2890, -2613, "University Park [2] 29", "NEW", None), (-2850, -2613, "University Park [3] 29 / spare", "NEW", None),
             (-2810, -2613, "University Park [4] 7", "NEW", None)],
  changes=["Redo the whole terminal: delete the Art Blvd curve and replace it with straight, legal roads. -297th Av is extended south in a straight line to -270th St, -250th St is extended east, and -277th Av closes a 200 x 200 m hub on the empty land east of 297th. The south side is the existing -270th St.",
           "A 14 m Campus Drive (-260th St) runs across the hub: one road, one kerb, one direction (eastbound). Bay [1] is the 236 Express-Max, [2] and [3] are 29, [4] is 7: every transfer is a few steps. The campus plaza fills the south half, like a university transit centre.",
           "29 and 236 both come down -297th Av and turn onto Campus Drive; 7 comes from the east along -270th, goes up 297th and turns right onto Campus Drive. All three leave by -277th Av and -270th St; 29 and 236 go back north on 297th ext.",
           "136 no longer ends here (it ends at Crestbury). University Park is now 7, 29 and the 236.",
           "The north half is layover (the 236 Express-Max artics park here), the old block west of 297th is spare layover.",
           "Stops: s0332 moves onto Campus Drive as bay [1]; [2], [3], [4] are new."],
  rules=["Campus Drive is eastbound only. 7 enters from the south end of 297th, 29 and 236 from the north end.", "No turning across Campus Drive: every turn into or out of it is a right turn except the entry from 297th (protected pocket).", "236 never uses bays [2]-[4]; 29 and 7 never use bay [1]."],
  decor=spread("canopy", -2950, -2625, -2790, -2625, 5, "Campus Drive canopies") + spread("trees", -2955, -2660, -2790, -2660, 5, "plaza trees") +
        [(-2870, -2665, "plaza", "campus plaza"), (-2870, -2668, "fountain", ""), (-2870, -2640, "bench", "benches"), (-2810, -2540, "bike", "bike hub"),
         (-2940, -2540, "kiosk", "campus cafe"), (-2950, -2700, "sign", "campus map"), (-2780, -2600, "clock", "clock tower")] + spread("lamp", -2760, -2520, -2760, -2690, 4, "lamps") +
        spread("trees", -2985, -2520, -2985, -2690, 5, "297th tree line")),

 dict(key="zeroth", title="0TH AV / 200TH ST", sub="Routes 10, 109 (1 and 201 pass by)",
  bbox=(-450, 1975, 70, 2175), routes=["10", "109", "201"],
  problems=[
   (-50, 2030, "203rd St is only 5 m wide, the narrowest road around, with both bays squeezed on 53 m of it."),
   (0, 2015, "109 turns left from eastbound 200th St onto 0th Av north, across traffic, then left again into 203rd."),
   (-100, 2030, "Buses waiting have nowhere to go: -10th Av crosses the bays."),
   (-200, 2090, "There is empty land north of 203rd and a 970 m road (-10th Av) going nowhere beside the stops.")],
  new_roads=[],
  remove_roads=[],
  after_paths={"10": ([[(2, 2002), (2, 2032), (-98, 2032), (-98, 2002), (2, 1998), (998, 1998)]], "west on 203rd, down -10th Av, east on 200th"),
               "109": ([[(2, 1998), (2, 2032), (-440, 2032)]], "203rd St through bays, on west")},
  bays=[],
  bay_rows=[(-40, 2036, -380, 2036, 12, "ONE road: 10 and 109 share 203rd St"), (-106, 2050, -106, 2160, 6, "layover on -10th Av north"), (-94, 2050, -94, 2160, 6, "")],
  lots=[],
  new_stops=[(-30, 2036, "0th/200th [D] (s0374 nudged)", "MOVE", "s0374"), (-80, 2036, "0th/200th [P] (s0375 nudged)", "MOVE", "s0375"), (-200, 2036, "0th/200th [3] 109 pickup", "NEW", None), (-300, 2036, "0th/200th [4] spare", "NEW", None)],
  changes=["Two routes, one road: 203rd St (widened to 7 m) gets twelve bays between Midway and the new link, shared by 10 and 109.",
           "No new road: -10th Av already joins 203rd St to 200th St 100 m west of Midway, so route 10 turns round on it (west on 203rd, south on -10th Av, east on 200th) and never crosses Midway twice. 109 keeps going west on 203rd, where its pickup bays are.",
           "Layover moves onto -10th Av north, an existing road that goes nowhere: waiting buses leave the bays.",
           "Stops: s0375 moves west, two new stops down 203rd St.",
           "Protected left-turn pocket on 200th St into 0th Av north."],
  rules=["10 turns on -10th Av; 109 does not.", "Layover only on -10th Av north."],
  decor=spread("canopy", -60, 2048, -380, 2048, 5, "platform canopies") + spread("trees", -120, 2080, -380, 2080, 5, "tree row") +
        [(-220, 2060, "plaza", "plaza"), (-30, 2050, "sign", "route board"), (-440, 2045, "bike", "bike racks")] + spread("lamp", -60, 2022, -380, 2022, 4, "lamps")),

 dict(key="valley", title="VALLEY FIELDS", sub="Routes 73, 85",
  bbox=(-7650, -1400, -7300, -980), routes=["73", "85"],
  problems=[(-7500, -1180, "D (z=-1271) and P (z=-1088) are 180 m apart on the through road."),
            (-7440, -1180, "The -740th Av return leg is quiet but unused."),
            (-7450, -1180, "The loop already has road on all four sides but only two lonely stops on it.")],
  new_roads=[], remove_roads=[],
  after_paths={"73/85": ([[(-7498, -1364), (-7498, -1250), (-7498, -1004), (-7402, -1004), (-7402, -1372), (-7498, -1368)]], "load on -750th, layover on -740th")},
  bays=[],
  bay_rows=[(-7494, -1260, -7494, -1090, 8, "ONE road: 73 and 85 share -750th Av"), (-7406, -1360, -7406, -1010, 14, "layover on -740th Av")],
  lots=[],
  new_stops=[(-7494, -1230, "Valley Fields [D] (moved s0476)", "MOVE", "s0476"), (-7494, -1180, "Valley Fields [2]", "NEW", None), (-7494, -1130, "Valley Fields [P] (moved s0477)", "MOVE", "s0477")],
  changes=["No new road: -750th Av, the top and bottom streets and -740th Av already form a closed loop that links to the grid.",
           "Two routes, one road: -750th Av holds all bays; -740th Av is the layover leg.",
           "Stops move 50 m together and a middle stop is added, so the two routes load in the same place."],
  rules=["Load on -750th Av northbound; wait on -740th Av."],
  decor=spread("canopy", -7494, -1260, -7494, -1090, 3, "canopies") + spread("trees", -7425, -1030, -7425, -1350, 6, "tree row") + [(-7450, -1030, "garden", "gateway park"), (-7520, -1180, "sign", "route board")] +
        spread("lamp", -7400, -1030, -7400, -1350, 4, "lamps")),
 dict(key="sunset", title="SUNSET POINT", sub="Routes 136 and 236 end here; 45 loops through",
  bbox=(2940, -2110, 3640, -1700), routes=["136", "236", "45"],
  problems=[
   (3250, -2000, "Sunset Point has two stops 250 m apart on one straight, used only by 45. There is no bay layout and nowhere to wait."),
   (3400, -1880, "The land inside the loop (between the curved -200th St and the straight) is empty."),
   (2990, -1900, "136 and 236 will arrive here from 299th Av and need to load and layover without blocking 45.")],
  new_roads=[], remove_roads=[],
  after_paths={"136/236": ([[(2990, -1800), (2990, -1992)] + [p for p in quad((2500, -2000), (3000, -2000), (3500, -1750), 40) if p[0] >= 3010] + [(3498, -1998), (3035, -1998), (3025, -1957), (3003, -1826)]], "136 and 236: down 299th Av, round the existing loop, bays on the straight, back up 299th Av")},
  bays=[],
  bay_rows=[(3390, -1991, 3120, -1991, 8, "ONE road: 136, 236 and 45 load on the straight (north kerb)"), (3390, -2009, 3120, -2009, 8, "layover (south kerb)")],
  lots=[(3050, -1985, 3480, -1790, "layover lot (inside the loop)")],
  new_stops=[(3150, -1991, "Sunset Point [1] 136 (moved s0564)", "MOVE", "s0564"), (3230, -1991, "Sunset Point [2] 236", "NEW", None), (3310, -1991, "Sunset Point [3] 45", "NEW", None), (3369, -1991, "Sunset Point [4] (s0563 stays)", "KEEP", None)],
  changes=["No new road: the loop already exists (the curved -200th St, 350th Av and the straight -200th St). Route 45 drives it today.",
           "Buses arrive from 299th Av, go round the loop and load on the straight; bays [1] is 136, [2] is the 236 Express-Max, [3] is 45, [4] spare.",
           "The land inside the loop becomes the layover lot; a promenade and plaza at the west end gives Sunset Point a face.",
           "Stops: s0564 moves to bay [1], [2] and [3] are new, s0563 stays."],
  rules=["Loop is one-way: east on the curve, south on 350th Av, west on the straight (bays), north on 299th Av.", "136 and 236 never use the south kerb."],
  decor=spread("canopy", 3140, -1982, 3380, -1982, 4, "platform canopies") + spread("trees", 3100, -1930, 3470, -1930, 5, "palms") + [(3250, -1960, "plaza", "sunset plaza"), (3250, -1962, "fountain", ""), (3120, -1975, "sign", "route map"),
         (3440, -1900, "kiosk", "snack kiosk")] + spread("lamp", 3100, -2025, 3480, -2025, 4, "lamps") + [(3040, -1950, "bike", "bike racks")]),

 dict(key="ext36", title="36TH ST EAST EXTENSION", sub="Routes 136 and 236 (and 299th Av stops for 45)",
  bbox=(1860, 60, 3120, 640), routes=["136", "236"],
  problems=[
   (1990, 360, "36th St dead-ends at 199th Av. 136 and 236 have no road to reach 299th Av and the south-east."),
   (2400, 360, "There are no stops on 36th St east of the Exchange at all (the last stop is Airport Lane)."),
   (2990, 360, "299th Av already runs from z=-3000 to 3404 as a 14 m arterial, but nothing joins it from 36th St.")],
  new_roads=[([(1990, 360), (2990, 360)], 7, "36th St (extended east to 299th Av, 1 km)")],
  remove_roads=[],
  after_paths={"136/236": ([[(1860, 358), (2988, 358), (2988, 100)]], "36th St east to 299th Av, then south down 299th Av")},
  bays=[], bay_rows=[],
  lots=[],
  new_stops=[(2010, 360, "N 36 ST & E 199 AV (136, 236; transfer to 21/199/299)", "NEW", None), (2250, 360, "N 36 ST & E 225 AV", "NEW", None), (2500, 360, "N 36 ST & E 250 AV", "NEW", None),
             (2750, 360, "N 36 ST & E 275 AV", "NEW", None), (2970, 360, "N 36 ST & E 299 AV (136, 236)", "NEW", None)],
  changes=["Extend 36th St 1 km east from 199th Av to 299th Av (a T junction). It crosses no existing road.",
           "136 and 236 run east along it, then south down 299th Av, which already carries route 45's eight stops (s0565 to s0572), to Sunset Point.",
           "Eight new 36th St stops for the local 136 (three more west of this view at E 125, 150 and 175 Av); the 236 Express-Max stops only at E 199 Av and E 299 Av.",
           "Both routes get a transfer to 21/199/299 at 199th Av and to 45 on 299th Av."],
  rules=["236 is limited stop: it stops only at the two marked transfer stops on this stretch."],
  decor=spread("lamp", 2050, 372, 2950, 372, 6, "lamps") + spread("shelter", 2250, 348, 2750, 348, 3, "shelters") + [(2010, 335, "sign", "transfer board"), (2970, 335, "sign", "transfer board")])
]

# ── drawing ──────────────────────────────────────────────────────────────────────

def stop_changes(c, view, stops, area, bbox):
    A.begin_clip(c, view)
    cols = {"NEW": (0.0, 0.6, 0.2), "MOVE": (0.0, 0.45, 0.8), "KEEP": (0.4, 0.4, 0.4), "REMOVE": (0.8, 0.1, 0.1)}
    for x, z, label, kind, old in area.get("new_stops", []):
        col = cols[kind]
        if old and old in stops and kind in ("MOVE", "REMOVE"):
            ox, oz = view.p((stops[old]["x"], stops[old]["z"]))
            c.saveState(); c.setStrokeColor(Color(0.8, 0.1, 0.1)); c.setLineWidth(1.6); c.line(ox - 5, oz - 5, ox + 5, oz + 5); c.line(ox - 5, oz + 5, ox + 5, oz - 5); c.restoreState()
            if kind == "MOVE":
                nx, nz = view.p((x, z)); c.saveState(); c.setStrokeColor(Color(*col, alpha=0.6)); c.setLineWidth(0.8); c.setDash(3, 2); c.line(ox, oz, nx, nz); c.restoreState()
        if kind == "REMOVE": continue
        px, py = view.p((x, z))
        A.draw_star(c, px, py, 7, fill=(1, 0.95, 0.5) if kind != "KEEP" else (0.92, 0.92, 0.92), edge=col, lw=1.6)
        A.draw_halo_text(c, px + 8, py - 2, ("NEW  " if kind == "NEW" else "MOVED  " if kind == "MOVE" else "") + label, 5.9, A.FONT_B, col)
    A.end_clip(c)

def entry_arrows(c, view, bbox, routes, nums, paths=None):
    """Big labelled arrows where each route enters/leaves the map window."""
    seen = set()
    for rt in routes:
        if rt["num"] not in nums: continue
        for kind, poly in (("out", rt["outPoly"]), ("in", rt["inPoly"])):
            pts = resample(poly, 6.0)
            prev_in = None
            for i, p in enumerate(pts):
                now_in = inbox(p, bbox)
                if prev_in is not None and now_in != prev_in and i > 3 and i < len(pts) - 3:
                    q = pts[i - 3] if now_in else pts[i + 3]
                    key = (rt["num"], round(p[0]/40), round(p[1]/40))
                    if key not in seen:
                        seen.add(key); big_arrow(c, view, p, pts[i + 3] if now_in else pts[i - 3], rt, entering=now_in, flip=not now_in)
                prev_in = now_in

_LBL = {}
def big_arrow(c, view, p, q, rt, entering, flip):
    # arrow at p pointing along travel: entering => toward q (inside); leaving => away from the inside
    a, b = view.p(p), view.p(q)
    ang = math.atan2(b[1]-a[1], b[0]-a[0]) + (math.pi if flip else 0)
    L = 22
    tip = (a[0] + L*math.cos(ang)*0.5, a[1] + L*math.sin(ang)*0.5)
    tail = (a[0] - L*math.cos(ang)*0.5, a[1] - L*math.sin(ang)*0.5)
    col = rt["color"]
    c.saveState(); c.setStrokeColor(Color(1, 1, 1)); c.setLineWidth(6.5); c.line(*tail, *tip)
    c.setStrokeColor(Color(*col)); c.setLineWidth(4); c.line(*tail, *tip)
    hp = c.beginPath(); hp.moveTo(tip[0] + 8*math.cos(ang), tip[1] + 8*math.sin(ang))
    hp.lineTo(tip[0] + 6*math.cos(ang + 2.2), tip[1] + 6*math.sin(ang + 2.2)); hp.lineTo(tip[0] + 6*math.cos(ang - 2.2), tip[1] + 6*math.sin(ang - 2.2)); hp.close()
    c.setFillColor(Color(*col)); c.setStrokeColor(Color(1, 1, 1)); c.setLineWidth(1.2); c.drawPath(hp, stroke=1, fill=1)
    k = (round(a[0]/30), round(a[1]/30)); n = _LBL.get(k, 0); _LBL[k] = n + 1
    lx, ly = a[0] - (11 + 8*n)*math.sin(ang), a[1] + (11 + 8*n)*math.cos(ang)
    A.draw_halo_text(c, lx, ly - 2, ("IN " if entering else "OUT ") + rt["num"], 6.5, A.FONT_B, (0.1, 0.1, 0.1), "c")
    c.restoreState()


def remove_marker(c, view, roads, codes):
    for code in codes:
        r = roads.get(code)
        if not r: continue
        pts = dense(r["ctrl"], 6.0)
        A.draw_poly(c, view, pts, (0.85, 0.15, 0.15), 1.5, dash=(6, 4))
        mid = pts[len(pts)//2]; x, y = view.p(mid); A.draw_halo_text(c, x + 4, y + 6, "REMOVE: " + r["name"], 6.4, A.FONT_B, (0.75, 0.1, 0.1))

def bay_row(c, view, x0, z0, x1, z1, n, label):
    a, b = view.p((x0, z0)), view.p((x1, z1)); ang = math.atan2(b[1]-a[1], b[0]-a[0])
    c.saveState(); c.setFillColor(Color(1, 0.55, 0.1, alpha=0.92)); c.setStrokeColor(Color(0.4, 0.2, 0, 1)); c.setLineWidth(0.5)
    for i in range(n):
        t = 0.5 if n == 1 else i/(n-1); px, py = a[0] + (b[0]-a[0])*t, a[1] + (b[1]-a[1])*t
        c.saveState(); c.translate(px, py); c.rotate(math.degrees(ang)); c.rect(-4.2, -1.8, 8.4, 3.6, stroke=1, fill=1); c.restoreState()
    c.restoreState()
    if label: A.draw_halo_text(c, a[0] + 2, a[1] + 6, label, 5.8, A.FONT_B, (0.55, 0.22, 0.0))

def lot_area(c, view, x0, z0, x1, z1, label):
    a, b = view.p((x0, z0)), view.p((x1, z1))
    c.saveState(); c.setFillColor(Color(0.55, 0.55, 0.6, alpha=0.18)); c.setStrokeColor(Color(0.45, 0.45, 0.5, alpha=0.8)); c.setLineWidth(0.7); c.setDash(3, 2)
    c.rect(min(a[0], b[0]), min(a[1], b[1]), abs(b[0]-a[0]), abs(b[1]-a[1]), stroke=1, fill=1)
    c.setDash(); c.setStrokeColor(Color(0.55, 0.55, 0.6, alpha=0.45)); c.setLineWidth(0.5)
    xl, xr, yb, yt = min(a[0], b[0]), max(a[0], b[0]), min(a[1], b[1]), max(a[1], b[1])
    k = xl - (yt - yb)
    while k < xr:
        x1_, y1_ = max(xl, k), yb + max(0, xl - k); x2_, y2_ = min(xr, k + (yt - yb)), yb + min(yt - yb, (min(xr, k + (yt - yb)) - k))
        if x2_ > x1_: c.line(x1_, y1_, x2_, y2_)
        k += 7
    c.restoreState()
    A.draw_halo_text(c, (a[0]+b[0])/2, (a[1]+b[1])/2 - 2, label, 6.4, A.FONT_B, (0.35, 0.35, 0.4), "c")

def decor_marker(c, view, x, z, kind, label):
    letter, col, _ = DECOR[kind]; px, py = view.p((x, z))
    c.saveState(); c.setFillColor(Color(*col)); c.setStrokeColor(Color(1, 1, 1)); c.setLineWidth(1.2); c.circle(px, py, 5.6, stroke=1, fill=1)
    A.draw_text(c, px, py - 2.3, letter, 6.6, A.FONT_B, (1, 1, 1), "c")
    if label: A.draw_halo_text(c, px + 8, py - 2, label, 5.8, A.FONT, (0.15, 0.15, 0.2))
    c.restoreState()

def bay_marker(c, view, x, z, label):
    px, py = view.p((x, z))
    c.saveState(); c.setFillColor(Color(1, 0.55, 0.1, alpha=0.9)); c.setStrokeColor(Color(0.4, 0.2, 0, alpha=1)); c.setLineWidth(0.9)
    c.roundRect(px - 5, py - 3, 10, 6, 1.5, stroke=1, fill=1)
    A.draw_halo_text(c, px + 8, py + 3.5, label, 6.0, A.FONT_B, (0.5, 0.2, 0.0))
    c.restoreState()

def numbered(c, view, x, z, n):
    px, py = view.p((x, z))
    c.saveState(); c.setFillColor(Color(0.85, 0.1, 0.1)); c.setStrokeColor(Color(1, 1, 1)); c.setLineWidth(1.2); c.circle(px, py, 8, stroke=1, fill=1)
    A.draw_text(c, px, py - 3, str(n), 9, A.FONT_B, (1, 1, 1), "c"); c.restoreState()

def wrap(c, text, font, size, width):
    words, lines, cur = text.split(), [], ""
    for w in words:
        t = (cur + " " + w).strip()
        if c.stringWidth(t, font, size) <= width: cur = t
        else: lines.append(cur); cur = w
    if cur: lines.append(cur)
    return lines

def text_block(c, x, y, items, width, size=8.2, numbered_items=False, bullet="•", color=(0.1, 0.1, 0.1), numcol=None):
    lh = size*1.32
    for i, t in enumerate(items):
        pre = f"{i+1}. " if numbered_items else bullet + " "
        lines = wrap(c, t, A.FONT, size, width - 14)
        if numbered_items:
            c.saveState(); c.setFillColor(Color(0.85, 0.1, 0.1)); c.circle(x + 5, y + size*0.3, 5.5, stroke=0, fill=1)
            A.draw_text(c, x + 5, y - 0.4, str(i + 1), size - 1.5, A.FONT_B, (1, 1, 1), "c"); c.restoreState()
        else: A.draw_text(c, x, y, pre, size, color=color)
        for ln in lines:
            A.draw_text(c, x + 14, y, ln, size, color=color); y -= lh
        y -= 2
    return y

def draw_map(c, rect, area, roads, stops, routes, mode):
    _LBL.clear()
    bbox = area["bbox"]; view = A.View(bbox, rect)
    paths = area["after_paths"] if mode == "after" else {}
    changed = set(n for k in paths for n in k.split("/"))
    base_filter = [r["num"] for r in routes if r["num"] in area["routes"]]
    if mode == "after": base_filter = [n for n in base_filter if n not in changed]
    draw_area(c, view, bbox, roads, stops, routes, route_filter=set(base_filter), dim_routes=(mode == "after"),
              extra_roads=[(pts, w) for pts, w, _ in area["new_roads"]] if mode == "after" else ())
    # changed route paths (AFTER)
    if mode == "after":
        A.begin_clip(c, view)
        for key, (polys, note) in paths.items():
            rt = next((r for r in routes if r["num"] == key.split("/")[0]), None)
            col = rt["color"] if rt else (0.1, 0.5, 0.2)
            for poly in polys:
                A.draw_poly(c, view, poly, (1, 1, 1), 6.0, alpha=0.9); A.draw_poly(c, view, poly, col, 3.6)
                poly_arrows(c, view, resample(poly, 5.0), bbox, col, every_m=55, size=8)
        for pts, w, label in area["new_roads"]:
            mid = pts[len(pts)//2] if len(pts) > 2 else ((pts[0][0] + pts[1][0])/2, (pts[0][1] + pts[1][1])/2)
            x, y = view.p(mid); A.draw_halo_text(c, x, y + 6, label, 5.6, A.FONT, (0.15, 0.45, 0.25), "c")
        A.end_clip(c)
    A.begin_clip(c, view)
    draw_stops_clip = {s["code"] for s in stops.values() if inbox((s["x"], s["z"]), bbox)}
    A.end_clip(c)
    draw_stops(c, view, bbox, stops, labels=(mode == "before"))
    A.begin_clip(c, view)
    if mode == "before":
        for i, (x, z, _) in enumerate(area["problems"]): numbered(c, view, x, z, i + 1)
        entry_arrows(c, view, bbox, routes, area["routes"])
    else:
        remove_marker(c, view, roads, area.get("remove_roads", []))
        for lx0, lz0, lx1, lz1, lab in area.get("lots", []): lot_area(c, view, lx0, lz0, lx1, lz1, lab)
        for x, z, label in area["bays"]: bay_marker(c, view, x, z, label)
        for r_ in area.get("bay_rows", []): bay_row(c, view, *r_)
        for x, z, kind, label in area["decor"]: decor_marker(c, view, x, z, kind, label)
        stop_changes(c, view, stops, area, bbox)
        for ki, (key, (polys, note)) in enumerate(paths.items()):
            rt = next((r for r in routes if r["num"] == key.split("/")[0]), None)
            if rt:
                p0 = polys[0][0]; px, py = view.p(p0)
                A.draw_halo_text(c, px + 6, py - 10 - 8*ki, f"R{key}: {note}", 6.2, A.FONT_B, (0.3, 0.3, 0.3))
        # entry arrows for unchanged routes still apply
        entry_arrows(c, view, bbox, [r for r in routes if r["num"] not in changed], area["routes"])
        for key, (polys, note) in paths.items():
            tmp = [dict(r, outPoly=polys[0], inPoly=polys[-1]) for r in routes if r["num"] == key.split("/")[0]]
            entry_arrows(c, view, bbox, tmp, area["routes"])
    A.end_clip(c)
    c.saveState(); c.setStrokeColor(Color(0.2, 0.2, 0.2)); c.setLineWidth(1); c.rect(*rect); c.restoreState()
    A.draw_text(c, rect[0] + 6, rect[1] + rect[3] - 14, "BEFORE (as built now)" if mode == "before" else "AFTER (proposed)", 11, A.FONT_B,
                (0.7, 0.1, 0.1) if mode == "before" else (0.0, 0.45, 0.15))
    # scale bar
    L = 100 if (bbox[2] - bbox[0]) < 700 else 200; x0 = rect[0] + 8; y0 = rect[1] + 10
    c.saveState(); c.setStrokeColor(Color(0, 0, 0)); c.setLineWidth(2); c.line(x0, y0, x0 + L*view.s, y0); c.restoreState()
    A.draw_text(c, x0, y0 + 4, f"{L} m", 6.5)

def text_h(c, items, width, size=8.2):
    lh = size*1.32
    return sum(len(wrap(c, t, A.FONT, size, width - 14))*lh + 2 for t in items) + 70

def area_page(c, area, roads, stops, routes):
    bx = area["bbox"]; asp = (bx[2] - bx[0])/(bx[3] - bx[1])
    M = 36; TITLE = 70
    if asp > 1.45:                                    # wide area: stack BEFORE over AFTER
        MW = 1500; MH = MW/asp; MH = min(MH, 760); MW = MH*asp
        W = max(MW + 2*M, 1500); colw = (W - 3*M)/2
        TEXT_H = max(text_h(c, [t for _, _, t in area["problems"]], colw), text_h(c, area["changes"], colw) + text_h(c, area.get("rules", []), colw)) + 40; H = TITLE + 2*MH + 3*M + TEXT_H
        c.setPageSize((W, H))
        draw_map(c, (M, H - TITLE - MH - M, MW, MH), area, roads, stops, routes, "before")
        draw_map(c, (M, H - TITLE - 2*MH - 2*M, MW, MH), area, roads, stops, routes, "after")
        ytxt = H - TITLE - 2*MH - 3*M - 6; colw = (W - 3*M)/2
    else:                                              # side by side
        MH = 760; MW = MH*asp
        if MW > 880: MW = 880; MH = MW/asp
        W = max(2*MW + 3*M, 1150); colw = (W - 3*M)/2
        TEXT_H = max(text_h(c, [t for _, _, t in area["problems"]], colw), text_h(c, area["changes"], colw) + text_h(c, area.get("rules", []), colw)) + 40; H = TITLE + MH + 3*M + TEXT_H
        c.setPageSize((W, H))
        draw_map(c, (M, H - TITLE - MH - M, MW, MH), area, roads, stops, routes, "before")
        draw_map(c, (W - M - MW, H - TITLE - MH - M, MW, MH), area, roads, stops, routes, "after")
        ytxt = H - TITLE - MH - 2*M - 6; colw = (W - 3*M)/2
    A.draw_text(c, M, H - 32, area["title"], 26, A.FONT_B); A.draw_text(c, M, H - 54, area["sub"] + "   ·   data: city backup + route assets", 11, color=(0.35, 0.35, 0.4))
    # legend (top right)
    lx = W - M - 520; ly = H - 24
    A.draw_text(c, lx, ly, "Legend", 8, A.FONT_B)
    items = [("red circle = problem", None), ("dashed green centre line = proposed road (drawn like any other road)", None), ("orange box = bay", None), ("star: green = new stop, blue = moved, red X = old/removed", None), ("coloured arrow = route in / out", None)]
    A.draw_text(c, lx, ly - 11, "  ·  ".join(t for t, _ in items), 7, color=(0.25, 0.25, 0.3))
    A.draw_text(c, lx, ly - 22, "   ".join(f"{v[0]} {v[2]}" for v in list(DECOR.values())[:8]), 6.2, color=(0.3, 0.3, 0.35))
    A.draw_text(c, lx, ly - 31, "   ".join(f"{v[0]} {v[2]}" for v in list(DECOR.values())[8:]), 6.2, color=(0.3, 0.3, 0.35))
    A.draw_text(c, M, ytxt, "WHAT'S WRONG NOW", 10, A.FONT_B, (0.7, 0.1, 0.1))
    text_block(c, M, ytxt - 16, [t for _, _, t in area["problems"]], colw, numbered_items=True)
    A.draw_text(c, M*2 + colw, ytxt, "WHAT CHANGES  (and where the decor goes)", 10, A.FONT_B, (0.0, 0.45, 0.15))
    y = text_block(c, M*2 + colw, ytxt - 16, area["changes"], colw)
    if area.get("rules"):
        A.draw_text(c, M*2 + colw, y - 6, "TURNING / ROUTE RULES", 10, A.FONT_B, (0.1, 0.25, 0.6))
        y = text_block(c, M*2 + colw, y - 22, area["rules"], colw, color=(0.1, 0.1, 0.25))
    if area["decor"]:
        names = {}
        for _, _, k, lab in area["decor"]: names.setdefault(k, 0); names[k] += 1
        dl = "Decor spots: " + ", ".join(f"{DECOR[k][2]} x{n}" for k, n in names.items()) + "."
        text_block(c, M*2 + colw, y - 4, [dl], colw, size=7.4, bullet="•", color=(0.3, 0.3, 0.35))
    c.showPage()

def build_page(c):
    W, H = 1500, 1200; c.setPageSize((W, H)); M = 40
    A.draw_text(c, M, H - 50, "BUILD LIST: ROADS TO ADD, REMOVE AND REROUTE", 24, A.FONT_B)
    A.draw_text(c, M, H - 72, "Lengths measured from the plan coordinates (7 m wide unless noted). No new routes: only the originals change.", 10.5, color=(0.35, 0.35, 0.4))
    y = H - 110; total = 0
    for a in AREAS:
        items = []
        for pts, w, label in a["new_roads"]:
            L = sum(math.dist(p, q) for p, q in zip(pts, pts[1:])); total += L; items.append(f"+ {label.replace('NEW  ', '').replace('extend ', 'extend ')}: {L:.0f} m")
        for code in a.get("remove_roads", []): items.append(f"- remove road {code}")
        if a["after_paths"]: items.append("~ reroute: " + ", ".join(f"R{k}" for k in a["after_paths"]))
        ns = a.get("new_stops", [])
        n_new = sum(1 for q in ns if q[3] == "NEW"); n_mv = sum(1 for q in ns if q[3] == "MOVE"); n_rm = sum(1 for q in ns if q[3] == "REMOVE")
        if ns: items.append(f"stops: {n_new} new, {n_mv} moved, {n_rm} removed")
        if not a["new_roads"]: items.append("(no new road needed: uses roads that already exist)")
        A.draw_text(c, M, y, a["title"], 11, A.FONT_B); y -= 14
        for it in items:
            A.draw_text(c, M + 14, y, it, 9); y -= 12
        y -= 8
    A.draw_text(c, M, y - 6, f"Total new road: {total:.0f} m", 13, A.FONT_B)
    c.showPage()

def main():
    src, exp, roads, stops, unplaced = A.load_city(); routes = A.load_routes()
    only = os.environ.get("TERM_ONLY", ""); out = os.environ.get("TERM_OUT", OUT)
    os.makedirs(os.path.dirname(out), exist_ok=True)
    c = canvas.Canvas(out, pageCompression=1); c.setTitle("Transit City - Terminal Area Redesigns")
    for a in AREAS:
        if a.get("build"): a["build"](a, roads)
    for a in AREAS:
        if not only or only == a["key"]: area_page(c, a, roads, stops, routes)
    if not only: build_page(c)
    c.save(); print("wrote", out, "from", src)
    if not only:
        import make_route_136_29 as rr
        comb = os.path.join(A.ROOT, "Network Maps", "Network_Redesign_Plan.pdf")
        c2 = canvas.Canvas(comb, pageCompression=1); c2.setTitle("Transit City - Network Redesign Plan")
        rr.page1(c2); rr.page2(c2)
        for a in AREAS: area_page(c2, a, roads, stops, routes)
        build_page(c2); c2.save(); print("wrote", comb)

if __name__ == "__main__": main()
