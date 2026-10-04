# TRANSIT CITY: MANUAL BUILD GUIDE

Everything the plan PDFs show, turned into things you type. Nothing here is automated: you type the numbers into the Unity Inspector yourself, one small step at a time.

**Files this guide matches:** `Network_Redesign_Plan.pdf` (the pictures) and this file (the typing). If the two ever disagree, trust the PDF picture for *what you want* and this file for *the exact numbers*.

**Data it was built from:** city backup `CityBackup_2026-09-27_18-33-33.json`, 22 route assets in `ROUTES/CBT/`, the fleet roster `DEPOTS/FleetRoster.asset`.


## How to use this guide

1. Work **top to bottom**. Each phase finishes with a **STOP: save and test** box. Do not start the next phase until the test passes.
2. Every step has a tick box. Tick it as you go (copy this file into your notes app if you want live ticks).
3. **Never delete a stop or a road to 'remove' it.** Unity stores the CityManager lists as prefab overrides keyed by list position, so deleting an element shifts everything. Retire stops by renaming them `placeholder` and clearing their road (instructions in Phase 3).
4. When a step says **type**, the value is in a code box or table. When it says **copy**, it names the route asset and the exact node numbers to copy from.
5. Coordinates are always **X, Y, Z**. **Y is always 0.** X runs east (+) / west (−). Z runs north (+) / south (−). The plan PDFs use the same X and Z.


## Table of contents

- **Phase 0**: Prepare and back up
- **Phase 1**: Fleet roster (the new 2900 Series (Artic) Express-Max buses and the 2800 normal artics)
- **Phase 2**: Roads (CityManager > Road Definitions)
- **Phase 3**: Stops (CityManager > Stop Definitions)
- **Phase 4**: Route assets, route by route (all 22)
- **Phase 5**: The big routes in full: 136, **Variant N (136N)**, **236 Express-Max**, 29, 240
- **Phase 6**: Registration lists, depots, fleet limits
- **Phase 7**: Terminal-by-terminal checklist (what each terminal needs, in one place)
- **Phase 8**: Testing in Play Mode
- **Appendix**: stop master table, how tValue works, troubleshooting


## Phase 0: Prepare and back up

**Step 0.1** Commit everything you have to git first. In a terminal in the project folder:

```bash
git add -A && git commit -m "Before network redesign"
```

**Step 0.2** In the Unity scene, select the object with the CityManager and run **CityDataExporter > Export City Data Backup** (right-click the component header). That writes a new timestamped file in `Assets/CityBackups/`.

**Step 0.3** Duplicate the route assets you are about to edit so you can roll back. In the Project window go to `Assets/ASSETS (1)/ROUTES/CBT/`, select all 22 `.asset` files, press **Ctrl+D** (Cmd+D on Mac), then move the copies into a new folder **outside** the ROUTES folder, e.g. `Assets/_BACKUP_ROUTES` (my atlas scripts read everything under ROUTES, so a backup left in there would be counted as extra routes). The backup copies must also never be added to the CityManager or BusScheduler route lists.

**Step 0.4** Stop Play Mode before editing. Edits made in Play Mode are lost when you stop.

**Step 0.5** The one-time editor setting from earlier: `Tools > Play Mode > Hold recompiles until I stop playing` is ticked. Leave it ticked.

**Where things live (so you can find them fast)**

| What | Where in Unity |
|---|---|
| Roads | Select the CityManager object > Inspector > **Road Network** > `roadDefinitions` (a list). Each element is a `RoadSegmentDefinition`. |
| Stops | Same object > **Bus Stops** > `stopDefinitions` (a list). Each element is a `BusStopData`. |
| Routes (assets) | Project window: `Assets/ASSETS (1)/ROUTES/CBT/<number>.asset` (type: BusRouteData). |
| Route lists | CityManager > **Routes** (`routes` array) **and** the BusScheduler object > `managedRoutes` array. A new route (236) must be added to **both**. |
| Fleet | Project window: `Assets/ASSETS (1)/DEPOTS/FleetRoster.asset` > `series` list. |
| Depots | `Assets/ASSETS (1)/DEPOTS/<depot>.asset` > **Routes Served** (`servedRouteNumbers`). |

**Tools you can use (optional):** `Tools > Transit > Road Drawer` (click roads on the ground), `Tools > City Building > Bus Stop Maker`, and the in-game Route Maker (press **U** in Play Mode). This guide is for typing by hand, so you only need the Inspector.


## Phase 1: Fleet roster

The roster is a list of *series*. A series is a block of fleet numbers with one bus type. **Do this phase first** so route assets can name the new series.

**How the game decides which route may use a bus.** Every route asset has `allowedFleetSeries`, a list of *hundred-blocks* (typing `2400` allows fleet numbers 2400–2499). I checked all 22 routes: every one lists its blocks explicitly, and **none lists 2800 or 2900**. That is the trick: give the Express-Max buses block **2900**, the normal artics block **2800**, and only the routes you add them to can ever use them.

(The existing **2700 Series** is the XDE40, fleet 2701–2750. That is why your new artics cannot be 'the 2700s'. Block 2900 keeps them clear. If you still want them called 2700 something, tell me and I will rewrite this; it costs you the clean reservation.)

|  | Express-Max (25 buses) | Normal artics (10 buses) |
|---|---|---|
| seriesName | `2900 Series (Artic)` | `2800 Series (Artic)` |
| startFleetNumber | `2901`  (2901–2925) | `2801`  (2801–2810) |
| busCount | `25` | `10` |
| busType | `XHE60` (placeholder prefab until your new model exists) | `XHE60` |
| isArticulated | tick (`1`) | tick (`1`) |
| modelYear | `2028` (your choice) | `2027` |
| depotAllocations | split the 25 between the depots that will run 236 and 240 (suggestion: the depots closest to University Park and Parkview) | spread as you like |

**Step 1.1** Open `FleetRoster.asset`. At the bottom of `series`, **copy** the element `2500 Series (Artic)` (right-click the element name > Duplicate Array Element) twice.
**Step 1.2** Edit the two new elements with the values in the table above. Leave every other field as the 2500 Series (Artic) had it (engine, transmission, fuel, wheel covers, etc.).
**Step 1.3** In each `depotAllocations` row make sure the `busCount` values add up to the series `busCount` (25 and 10).

> **Reservation rule, written once.** Max buses (block 2900) run only on routes whose `allowedFleetSeries` contains `2900`: that is 236 and 240 and nothing else. The 10 normal artics (block 2800) run only on routes whose list contains `2800`: you add that in Phase 4/5 (136 first).

**STOP: save and test.** Press Play. In the main menu fleet list confirm the two new series exist and the counts are 25 and 10. Stop Play.


## Phase 2: Roads (CityManager > Road Definitions)

**How a road is typed.** In `roadDefinitions` each element has: `roadName`, `roadCode`, `roadWidth`, `isOneWay`, `reverseFlow`, `hasSidewalks`, `curveMode` (leave as **ControlPoints**), and `controlPoints` (a list of Vector3).
- **2 points = a straight road.** Every new road below is 2 points (straight). A road with 3 points is a curve (quadratic), 4 points a cubic curve. **Do not type 4 points for a rectangle**: it becomes a bulging curve. Use separate straight roads.
- Roads that cross an existing road make a junction on their own (`canHaveIntersection` is on by default). Tick nothing extra.
- Control point order = the direction traffic runs on a one-way road, and the stops you place sit on the **right-hand side** of that direction.
- **New roads you add go at the END of the list.** Never insert in the middle.


### 2A. Roads to EDIT (they already exist)

The road list is a reorderable list with one **foldout arrow per road** (the title shows the road name). Click arrows until you find the `roadCode` in the heading below, then edit it. There is no search box, so work down the list once and tick off the edits.

- [ ] **Meridian Blvd** (`roadCode MRBD`): MOVE the whole arc 400 m west: subtract 400 from the X of every control point (z stays the same).
  The arc has 24 control points. Take them in order (index 0 first). **Type the NEW column over the old one**:

| idx | old X | Z (unchanged) | **new X** |
|---|---|---|---|
| 0 | 0 | 3150 | -400 |
| 1 | -12 | 3149 | -412 |
| 2 | -23 | 3147 | -423 |
| 3 | -34 | 3145 | -434 |
| 4 | -44 | 3142 | -444 |
| 5 | -54 | 3139 | -454 |
| 6 | -64 | 3135 | -464 |
| 7 | -73 | 3131 | -473 |
| 8 | -81 | 3127 | -481 |
| 9 | -89 | 3122 | -489 |
| 10 | -96 | 3116 | -496 |
| 11 | -103 | 3110 | -503 |
| 12 | -110 | 3103 | -510 |
| 13 | -116 | 3096 | -516 |
| 14 | -122 | 3089 | -522 |
| 15 | -127 | 3081 | -527 |
| 16 | -131 | 3073 | -531 |
| 17 | -135 | 3064 | -535 |
| 18 | -139 | 3054 | -539 |
| 19 | -142 | 3044 | -542 |
| 20 | -145 | 3034 | -545 |
| 21 | -147 | 3023 | -547 |
| 22 | -149 | 3012 | -549 |
| 23 | -150 | 3000 | -550 |

- [ ] **303rd St** (`roadCode N303ST`): Extend west: change control point 1 (the second one) to the new X.
|  | controlPoints (X, Y, Z) |
|---|---|
| old | (0, 0, 3030) ; (-146, 0, 3030) |
| **new** | (0, 0, 3030) ; (-546, 0, 3030) |

- [ ] **306th St** (`roadCode N306ST`): Extend west: change control point 1 to the new X.
|  | controlPoints (X, Y, Z) |
|---|---|
| old | (0, 0, 3060) ; (-137, 0, 3060) |
| **new** | (0, 0, 3060) ; (-537, 0, 3060) |

- [ ] **Berrelingway connector** (`roadCode BRWYX`): Extend east to the new 56th Av: change control point 0 (the first one) to X=560.
|  | controlPoints (X, Y, Z) |
|---|---|
| old | (494, 0, 2900) ; (400, 0, 2900) |
| **new** | (560, 0, 2900) ; (400, 0, 2900) |

- [ ] **203rd St** (`roadCode N203ST`): Widen: roadWidth 5 -> 7.


### 2B. Roads to REMOVE

A road cannot be deleted from the list without shifting it, so you **neutralise** it instead (the roads below are only ever replaced by new ones):

| roadCode | name | what to do |
|---|---|---|
| `ARBD` | Art Blvd (curve at University Park) | Select it. Set `roadWidth` to `0.1`, `canHaveIntersection` OFF, `hasSidewalks` OFF, `hideCenterLine` ON, and move **both** control points far away to `(0, 0, -20000)` and `(0, 0, -20010)` so it is out of the city. (If you prefer, delete the element: you will need to re-check the override list order; the neutralise method is safer.) |
| `MRSQD` | Meridian Square Dropoff (D-shaped loop) | Select it. Set `roadWidth` to `0.1`, `canHaveIntersection` OFF, `hasSidewalks` OFF, `hideCenterLine` ON, and move **both** control points far away to `(0, 0, -20000)` and `(0, 0, -20010)` so it is out of the city. (If you prefer, delete the element: you will need to re-check the override list order; the neutralise method is safer.) |
| `W375AVE` | -375th Av (Crestbury stub) | Select it. Set `roadWidth` to `0.1`, `canHaveIntersection` OFF, `hasSidewalks` OFF, `hideCenterLine` ON, and move **both** control points far away to `(0, 0, -20000)` and `(0, 0, -20010)` so it is out of the city. (If you prefer, delete the element: you will need to re-check the override list order; the neutralise method is safer.) |



### 2C. NEW roads to ADD

For each row: click the small **+ at the bottom-right of the road list**, click the new element's foldout arrow, then type the values. Leave anything not mentioned at its default (Unity gives `roadWidth 7`, `canHaveIntersection` on, `hasSidewalks` on).

#### PARKVIEW
- [ ] **-117th St**   (380 m)
| field | type this |
|---|---|
| roadName | -117th St |
| roadCode | `S117STX` |
| roadWidth | `7` |
| controlPoints | point 0: `(-380, 0, -1170)` ; point 1: `(0, 0, -1170)` |
| extras | none |

- [ ] **-125th St**   (200 m)
| field | type this |
|---|---|
| roadName | -125th St |
| roadCode | `S125STX` |
| roadWidth | `7` |
| controlPoints | point 0: `(-200, 0, -1250)` ; point 1: `(0, 0, -1250)` |
| extras | none |

- [ ] **Midway**   (80 m)
| field | type this |
|---|---|
| roadName | Midway |
| roadCode | `MIDWAYX` |
| roadWidth | `7` |
| controlPoints | point 0: `(0, 0, -1170)` ; point 1: `(0, 0, -1250)` |
| extras | none |

#### MERIDIAN SQUARE (main)
- [ ] **309th St**   (521 m)
| field | type this |
|---|---|
| roadName | 309th St |
| roadCode | `N309STX` |
| roadWidth | `7` |
| controlPoints | point 0: `(0, 0, 3090)` ; point 1: `(-521, 0, 3090)` |
| extras | none |

#### MERIDIAN SQUARE B
- [ ] **350th St**   (160 m)
| field | type this |
|---|---|
| roadName | 350th St |
| roadCode | `N350STX` |
| roadWidth | `9` |
| controlPoints | point 0: `(-700, 0, 3500)` ; point 1: `(-540, 0, 3500)` |
| extras | `isOneWay` **ON** (traffic runs from point 0 to point 1) |

- [ ] **-54th Av**   (500 m)
| field | type this |
|---|---|
| roadName | -54th Av |
| roadCode | `W54AVEX` |
| roadWidth | `9` |
| controlPoints | point 0: `(-540, 0, 3400)` ; point 1: `(-540, 0, 3900)` |
| extras | `isOneWay` **ON** (traffic runs from point 0 to point 1) |

- [ ] **390th St**   (160 m)
| field | type this |
|---|---|
| roadName | 390th St |
| roadCode | `N390STX` |
| roadWidth | `9` |
| controlPoints | point 0: `(-540, 0, 3900)` ; point 1: `(-700, 0, 3900)` |
| extras | `isOneWay` **ON** (traffic runs from point 0 to point 1) |

- [ ] **-62nd Av**   (500 m)
| field | type this |
|---|---|
| roadName | -62nd Av |
| roadCode | `W62AVEX` |
| roadWidth | `7` |
| controlPoints | point 0: `(-620, 0, 3400)` ; point 1: `(-620, 0, 3900)` |
| extras | none |

#### BERRELINGWAY NORTH
- [ ] **56th Av**   (210 m)
| field | type this |
|---|---|
| roadName | 56th Av |
| roadCode | `E56AVEX` |
| roadWidth | `7` |
| controlPoints | point 0: `(560, 0, 2790)` ; point 1: `(560, 0, 3000)` |
| extras | none |

- [ ] **279th St**   (160 m)
| field | type this |
|---|---|
| roadName | 279th St |
| roadCode | `N279STX` |
| roadWidth | `7` |
| controlPoints | point 0: `(400, 0, 2790)` ; point 1: `(560, 0, 2790)` |
| extras | none |

#### SOUTH PIER
- [ ] **-193rd St**   (290 m)
| field | type this |
|---|---|
| roadName | -193rd St |
| roadCode | `S193STX` |
| roadWidth | `7` |
| controlPoints | point 0: `(1700, 0, -1935)` ; point 1: `(1990, 0, -1935)` |
| extras | none |

#### NORTH BEACH
- [ ] **280th Av**   (400 m)
| field | type this |
|---|---|
| roadName | 280th Av |
| roadCode | `E280AVEX` |
| roadWidth | `7` |
| controlPoints | point 0: `(2800, 0, 3000)` ; point 1: `(2800, 0, 3400)` |
| extras | none |

- [ ] **320th St**   (150 m)
| field | type this |
|---|---|
| roadName | 320th St (Beach Mall) |
| roadCode | `N320STX` |
| roadWidth | `14` |
| controlPoints | point 0: `(2950, 0, 3200)` ; point 1: `(2800, 0, 3200)` |
| extras | `isOneWay` **ON** (traffic runs from point 0 to point 1) |

#### CRESTBURY
- [ ] **-348th Av**   (90 m)
| field | type this |
|---|---|
| roadName | -348th Av |
| roadCode | `W348AVEX` |
| roadWidth | `7` |
| controlPoints | point 0: `(-3480, 0, -700)` ; point 1: `(-3480, 0, -610)` |
| extras | none |

- [ ] **-65th St**   (220 m)
| field | type this |
|---|---|
| roadName | -65th St (Terminal Mall) |
| roadCode | `S65STX` |
| roadWidth | `7` |
| controlPoints | point 0: `(-3480, 0, -655)` ; point 1: `(-3700, 0, -655)` |
| extras | `isOneWay` **ON** (traffic runs from point 0 to point 1) |

#### SOUTHSIDE
- [ ] **83rd Av**   (200 m)
| field | type this |
|---|---|
| roadName | 83rd Av |
| roadCode | `E83AVEX` |
| roadWidth | `7` |
| controlPoints | point 0: `(830, 0, -2500)` ; point 1: `(830, 0, -2700)` |
| extras | none |

#### UNIVERSITY PARK
- [ ] **-297th Av**   (195 m)
| field | type this |
|---|---|
| roadName | -297th Av |
| roadCode | `W297AVEX` |
| roadWidth | `14` |
| controlPoints | point 0: `(-2970, 0, -2505)` ; point 1: `(-2970, 0, -2700)` |
| extras | none |

- [ ] **-250th St**   (200 m)
| field | type this |
|---|---|
| roadName | -250th St |
| roadCode | `S250STX` |
| roadWidth | `7` |
| controlPoints | point 0: `(-2970, 0, -2505)` ; point 1: `(-2770, 0, -2505)` |
| extras | none |

- [ ] **-277th Av**   (195 m)
| field | type this |
|---|---|
| roadName | -277th Av |
| roadCode | `W277AVEX` |
| roadWidth | `7` |
| controlPoints | point 0: `(-2770, 0, -2505)` ; point 1: `(-2770, 0, -2700)` |
| extras | none |

- [ ] **-260th St**   (200 m)
| field | type this |
|---|---|
| roadName | -260th St (Campus Drive) |
| roadCode | `S260STX` |
| roadWidth | `14` |
| controlPoints | point 0: `(-2970, 0, -2605)` ; point 1: `(-2770, 0, -2605)` |
| extras | `isOneWay` **ON** (traffic runs from point 0 to point 1) |

#### 36TH ST EAST EXTENSION
- [ ] **36th St**   (1000 m)
| field | type this |
|---|---|
| roadName | 36th St |
| roadCode | `N36STX` |
| roadWidth | `7` |
| controlPoints | point 0: `(1990, 0, 360)` ; point 1: `(2990, 0, 360)` |
| extras | none |

**Total new road: 6.0 km.**

> **Does the junction work?** After you add each road, press Play for a few seconds and look at where it crosses another road. You should see the procedural junction appear. If you see a gap or an overlap, nudge the end control point by a few metres.

**STOP: save and test.** Play. Drive or fly the camera along each new road. Fix anything that looks broken before you place stops on it.


## Phase 3: Stops (CityManager > Stop Definitions)

A stop has: `stopCode`, `stopName`, `parentRoadCode`, `tValue` (0–1 along that road), `hasShelter`, `isTerminal`, `isLayover`, `isAccessible`.
**tValue** is how far along the road the stop sits: 0 = at control point 0, 1 = at the last control point, 0.5 = halfway. For a straight 2-point road it is just the fraction of the length. The stop itself is drawn about half a road width to the **right** of the road line (in the control-point direction), so a stop looks like it is on the kerb. All values below were computed from the plan coordinates.

**Rules from your naming convention:** terminals are ALL CAPS place names with `[P]` (pickup) and `[D]` (drop-off); numbered spare bays are `[2]`, `[3]` and so on; street stops are `W 333 AV & N 19 ST` style.
**New stops go at the END of `stopDefinitions`** (click the small **+ at the bottom-right of the stop list**, open the new element with its foldout arrow), in the order below. New codes continue from `s0595`, so they are `s0596` onwards.


### 3A. NEW stops to add (39)

| code | stopName | parentRoadCode | tValue | isTerminal | isLayover | hasShelter |
|---|---|---|---|---|---|---|
| `s0596` | PARKVIEW PARKING GARAGE [D] | `S125ST` (-125th St) | 0.8593 | yes | no | yes |
| `s0597` | LEAF BLVD & W 20 AV | `LFBD` (LEAF BLVD) | 0.3807 | no | no | yes |
| `s0598` | LEAF BLVD & W 20 AV | `LFBD` (LEAF BLVD) | 0.3807 | no | no | yes |
| `s0599` | MERIDIAN SQUARE C [LAYOVER] | `N309STX` (309th St) | 0.48 | no | yes | yes |
| `s0600` | MERIDIAN SQUARE B [P2] | `W54AVEX` (-54th Av) | 0.68 | yes | yes | yes |
| `s0601` | BERRELINGWAY NORTH [D] | `E40AVE` (40th Av) | 0.9907 | yes | no | yes |
| `s0602` | BERRELINGWAY NORTH STATION [2] | `BRWYX` (BRWYEXTRA) | 0.094 | yes | no | yes |
| `s0603` | SOUTH PIER [D1] | `R068` (-190th St) | 0.7933 | yes | no | yes |
| `s0604` | SOUTH PIER [D3] | `R068` (-190th St) | 0.3107 | yes | no | yes |
| `s0605` | SOUTH PIER [P2] | `S193STX` (-193rd St) | 0.552 | yes | no | yes |
| `s0606` | SOUTH PIER [P3] | `S193STX` (-193rd St) | 0.7933 | yes | no | yes |
| `s0607` | SOUTH PIER [45 D] | `S200ST` (-200th St) | 0.7 | yes | no | yes |
| `s0608` | SOUTH PIER [45 2] | `S200ST` (-200th St) | 0.8 | yes | no | yes |
| `s0609` | SOUTH PIER [45 P] | `S200ST` (-200th St) | 0.9 | yes | no | yes |
| `s0610` | NORTH BEACH [3] | `N320STX` (320th St) | 0.8333 | yes | yes | yes |
| `s0611` | NORTH BEACH [4] | `N320STX` (320th St) | 0.1 | yes | yes | yes |
| `s0612` | NORTH BEACH [295 D] | `E295AV` (295th Av) | 0.75 | yes | no | yes |
| `s0613` | CRESTBURY [3] | `S65STX` (-65th St) | 0.8633 | yes | no | yes |
| `s0614` | CRESTBURY [4] | `W370AVE` (-370th Av) | 0.0833 | yes | no | yes |
| `s0615` | S 61 ST & W 364 AV | `S61ST` (-61st St) | 0.256 | no | no | yes |
| `s0616` | NW POINT [2] | `W230AVE` (-230th Av) | 0.4547 | yes | no | yes |
| `s0617` | NW POINT [3] | `W230AVE` (-230th Av) | 0.2273 | yes | yes | yes |
| `s0618` | SOUTHSIDE [2] | `E93AVE` (93rd Av) | 0.3807 | yes | no | yes |
| `s0619` | UNIVERSITY PARK [2] | `S260STX` (-260th St) | 0.4 | yes | no | yes |
| `s0620` | UNIVERSITY PARK [3] | `S260STX` (-260th St) | 0.6 | yes | yes | yes |
| `s0621` | UNIVERSITY PARK [4] | `S260STX` (-260th St) | 0.8 | yes | no | yes |
| `s0622` | 0TH AV / 200TH ST [3] | `N203ST` (203rd St) | 0.458 | yes | no | yes |
| `s0623` | 0TH AV / 200TH ST [4] | `N203ST` (203rd St) | 0.5347 | yes | yes | yes |
| `s0624` | VALLEY FIELDS [2] | `W750AV` (-750th Av) | 0.7867 | yes | no | yes |
| `s0625` | SUNSET POINT [2] | `S200ST2` (-200th St) | 0.5567 | yes | no | yes |
| `s0626` | SUNSET POINT [3] | `S200ST2` (-200th St) | 0.392 | yes | no | yes |
| `s0627` | W 333 AV & N 2 ST | `W333AVE` (-333rd Av) | 0.3507 | no | no | yes |
| `s0628` | W 333 AV & N 19 ST | `W333AVE` (-333rd Av) | 0.4067 | no | no | yes |
| `s0629` | N 36 ST & E 125 AV | `N36ST3` (36th St) | 0.2527 | no | no | yes |
| `s0630` | N 36 ST & E 175 AV | `N36ST3` (36th St) | 0.7573 | no | no | yes |
| `s0631` | N 36 ST & E 199 AV | `N36STX` (36th St) | 0.02 | no | no | yes |
| `s0632` | N 36 ST & E 225 AV | `N36STX` (36th St) | 0.26 | no | no | yes |
| `s0633` | N 36 ST & E 275 AV | `N36STX` (36th St) | 0.76 | no | no | yes |
| `s0634` | N 36 ST & E 299 AV | `N36STX` (36th St) | 0.98 | no | no | yes |

Leave `isAccessible` ticked (default).


### 3B. MOVED stops (they keep their code, so no route list needs touching)

Find each stop by its `stopCode`. Change only the fields shown. Where a new name is shown, type it as well.

| stopCode | current name | new name | new parentRoadCode | new tValue |
|---|---|---|---|---|
| `s0034` | Parkview Terminal | PARKVIEW LINK [E] | `S117STX` (-117th St) | 0.2367 |
| `s0406` | PARKVIEW B [D] | (keep) | `S105ST` (-105th St) | 0.108 |
| `s0407` | PARKVIEW B [P] | (keep) | `S105ST` (-105th St) | 0.0453 |
| `s0135` | Meridian Square [PICKUP] | (keep) | `N303ST` (303rd St) | 0.89 |
| `s0245` | Meridian Square A2 [PICKUP] | (keep) | `N306ST` (306th St) | 0.888 |
| `s0131` | Meridian Square [DROPOFF] | (keep) | `N303ST` (303rd St) | 0.0547 |
| `s0244` | Meridian Square A2 [DROPOFF] | (keep) | `N306ST` (306th St) | 0.056 |
| `s0478` | MERIDIAN SQUARE B [D] | (keep) | `W54AVEX` (-54th Av) | 0.4 |
| `s0479` | MERIDIAN SQUARE B [P] | (keep) | `W54AVEX` (-54th Av) | 0.8 |
| `s0089` | Berrelingway North Station | (keep) | `BRWYX` (BRWYEXTRA) | 0.5 |
| `s0246` | SOUTH PIER [D] | (keep) | `R068` (-190th St) | 0.5173 |
| `s0247` | SOUTH PIER [P] | (keep) | `S193STX` (-193rd St) | 0.3107 |
| `s0512` | NORTH BEACH [P] | (keep) | `N320STX` (320th St) | 0.5667 |
| `s0511` | NORTH BEACH [D] | (keep) | `N320STX` (320th St) | 0.3 |
| `s0092` | Crestbury Terminal | (keep) | `S65STX` (-65th St) | 0.3633 |
| `s0099` | Crestbury Terminal [DROPOFF] | (keep) | `S65STX` (-65th St) | 0.6367 |
| `s0184` | NW POINT | (keep) | `W230AVE` (-230th Av) | 0.682 |
| `s0292` | SOUTHSIDE [P] | (keep) | `E93AVE` (93rd Av) | 0.6667 |
| `s0291` | SOUTHSIDE [D] | (keep) | `E93AVE` (93rd Av) | 0.1907 |
| `s0332` | UNIVERSITY PARK | UNIVERSITY PARK [1] | `S260STX` (-260th St) | 0.2 |
| `s0374` | 0TH AV / 200TH ST [D] | (keep) | `N203ST` (203rd St) | 0.328 |
| `s0375` | 0TH AV / 200TH ST [P] | (keep) | `N203ST` (203rd St) | 0.3667 |
| `s0476` | VALLEY FIELDS [D] | (keep) | `W750AV` (-750th Av) | 0.82 |
| `s0477` | VALLEY FIELDS [P] | (keep) | `W750AV` (-750th Av) | 0.7533 |
| `s0564` | SUNSET POINT [P] | (keep) | `S200ST2` (-200th St) | 0.7213 |
| `s0563` | SUNSET POINT [D] | (keep) | `S200ST2` (-200th St) | 0.27 |


### 3C. RETIRED stops (set to placeholder; do not delete)

Every stop below is served ONLY by Route 136 or Route 29, and both are rewritten in full in Phase 5, so **you never have to hunt through other routes** to remove them: just leave them out of the new lists. For each stop in this list: set `stopName` to `placeholder`, set `parentRoadCode` to empty, `tValue` 0, untick `hasShelter`/`isTerminal`. Then remove its code from **every route's stop lists** (Phase 4 tells you which routes).

**Cut because you asked for every 2nd stop on the corridor to go (22):**

| stopCode | stopName | road | routes that listed it |
|---|---|---|---|
| `s0579` | W 297 AV & S 2210 BLOCK | -297th Av | 29 |
| `s0581` | W 297 AV & S 1989 BLOCK | -297th Av | 29 |
| `s0583` | W 297 AV & S 1731 BLOCK | -297th Av | 29 |
| `s0585` | W 297 AV & S 1474 BLOCK | -297th Av | 29 |
| `s0587` | W 297 AV & S 1216 BLOCK | -297th Av | 29 |
| `s0589` | W 297 AV & S 958 BLOCK | -297th Av | 29 |
| `s0169` | W 333 AV & N 55 ST | -333rd Av | 136 |
| `s0171` | W 333 AV & N 88 ST | -333rd Av | 136 |
| `s0173` | W 333 AV & N 1166 BLOCK | -333rd Av | 136 |
| `s0175` | W 333 AV & N 1512 BLOCK | -333rd Av | 136 |
| `s0177` | W 333 AV & N 1883 BLOCK | -333rd Av | 136 |
| `s0166` | NW BOOLEAN WAY | BOOLEAN WAY | 136 |
| `s0164` | NW BOOLEAN WAY | BOOLEAN WAY | 136 |
| `s0162` | NW BOOLEAN WAY | BOOLEAN WAY | 136 |
| `s0160` | NW BOOLEAN WAY | BOOLEAN WAY | 136 |
| `s0158` | NW BOOLEAN WAY | BOOLEAN WAY | 136 |
| `s0157` | NW BOOLEAN WAY | BOOLEAN WAY | 136 |
| `s0156` | NW BOOLEAN WAY | BOOLEAN WAY | 136 |
| `s0065` | N 36 ST & W 76 AV | 36th St | 136 |
| `s0025` | N 36 ST & W 195 AV | 36th St | 136 |
| `s0027` | N 36 ST & W 120 AV | 36th St | 136 |
| `s0030` | N 36 ST & E 62 AV | 36th St | 136 |

**Cut because Route 29 no longer goes to Parkview (2):**

| stopCode | why |
|---|---|
| `s0404` | PARKVIEW A [D] (only 29 used it) |
| `s0405` | PARKVIEW A [P] (only 29 used it) |

**STOP: save and test.** Play. Open the map and check: every new/moved stop sits on its road and on the right kerb; no stop is floating in a field. Fix tValues up or down by 0.01 until each is right.


## Phase 4: Route assets, route by route

**How a route is typed.** Open the asset (`ROUTES/CBT/<number>.asset`). The parts you will touch:

- `outboundNodes` / `inboundNodes`: the path. Each node is a Vector3 `position` and an `isCurve` tick. **Outbound goes terminal A to terminal Z. Inbound goes Z to A.**
- `outboundStops` / `inboundStops`: a list of `stopCode`, `minutesFromStart` and `isTimepoint`.
- `terminalACode` / `terminalZCode` and `destinationNameOutbound` / `destinationNameInbound`.
- `variants`: a list; each variant can override nodes, stops, schedule and vehicles. `overrideRoute` must be ON for a variant's node/stop lists to count.

**The rule for terminals (important, you will see it in every section).** A bus does not teleport between the end of one trip and the start of the next. It carries on forward. So:
1. The **arriving** trip's last node must be **at or past** its last stop in the direction of travel (otherwise the bus never reaches that stop).
2. The **departing** trip's first node must be **at or before** its first stop in the direction of travel.
3. The departing bay must be **further along the same loop** than the arriving bay. That is why the plan puts D before P on every terminal.

**Reading the edit tables.** `#n` means the node number in the Inspector list (it starts at 0). 'Delete #a–#b' means remove those elements. Always re-count the numbers after a delete.


### Route summary: what each of the 22 routes needs

| route | what changes | where |
|---|---|---|
| 1 | PARKVIEW: leaving Parkview Commons (outbound); MERIDIAN SQUARE (main): arrive at A and leave from the moved pickup bay | Phase 4 |
| 201 | PARKVIEW: leaving Parkview Commons (outbound); MERIDIAN SQUARE (main): arrive at A and leave from the moved pickup bay | Phase 4 |
| 7 | SOUTHSIDE: P moved back 55 m off the junction; UNIVERSITY PARK: bay [4] on Campus Drive | Phase 4 |
| 10 | SOUTHSIDE: P moved back 55 m off the junction; 0TH AV / 200TH ST: nothing to type | Phase 4 |
| 21 | SOUTH PIER: bays on the -190th St / Platform Road loop; CRESTBURY: the shared Terminal Mall (-65th St, westbound) | Phase 4 |
| 25 | NW POINT: bay on 230th Av | Phase 4 |
| 29 | **rewritten in full** (Phase 5) | 5D / 5A (+ 5B night variant for 136) |
| 34 | MERIDIAN SQUARE: Variant A passes along Leaf Blvd; NORTH BEACH: 34 / 34A reversed to run the Beach Mall westbound | Phase 4 |
| 45 | SOUTH PIER: 45 gets its own bays on -200th St; NORTH BEACH: Beach Mall; NORTH BEACH: arrive and leave on the Beach Mall (westbound) | Phase 4 |
| 73 | PARKVIEW WEST: arriving at the B bays; VALLEY FIELDS: stops closer together | Phase 4 |
| 85 | MERIDIAN SQUARE B: one counter-clockwise lap, no hairpins; VALLEY FIELDS: stops closer together | Phase 4 |
| 87 | PARKVIEW WEST: arriving at the B bays; MERIDIAN SQUARE (main): arrive at A and leave from the moved pickup bay | Phase 4 |
| 101 | MERIDIAN SQUARE A2: leave from the moved A2 pickup bay | Phase 4 |
| 109 | 0TH AV / 200TH ST: pickup moves west of -10th Av | Phase 4 |
| 114 | BERRELINGWAY NORTH: drop-off on 40th Av; NORTH BEACH: no more U-turn | Phase 4 |
| 116 | MERIDIAN SQUARE: new Leaf Blvd through stops; BERRELINGWAY NORTH: nothing to do; CRESTBURY: the shared Terminal Mall | Phase 4 |
| 136 | **rewritten in full** (Phase 5) | 5D / 5A (+ 5B night variant for 136) |
| 140 | PARKVIEW: inbound now comes down the new Midway stretch; BERRELINGWAY NORTH: one lap, ends at the Station bay on the connector | Phase 4 |
| 199 | MERIDIAN SQUARE A2: arrive at A2 and leave from the moved pickup bay; SOUTH PIER: own bays P `s0605` and D `s0603` | Phase 4 |
| 216 | MERIDIAN SQUARE: new Leaf Blvd through stops; BERRELINGWAY NORTH: nothing to do; CRESTBURY: the shared Terminal Mall | Phase 4 |
| 240 | PARKVIEW: inbound now comes down the new Midway stretch; BERRELINGWAY NORTH: one lap, ends at the Station bay on the connector; Express-Max conversion (5E) | Phase 4 + 5E |
| 299 | MERIDIAN SQUARE A2: arrive at A2 and leave from the moved pickup bay; SOUTH PIER: own bays P `s0606` and D `s0604` | Phase 4 |
| 236 | **new route** | 5C |

**Suggested order inside Phase 4:** do the terminal you are testing first (all its routes), then the next terminal. Within a terminal: roads and stops are already done, so just edit the routes listed in Phase 7 for that terminal.


### Route 1

**PARKVIEW: leaving Parkview Commons (outbound)**

- [ ] Old outbound start: #0 (-502, -1180) #1 (-502, -1252) #2 (-378, -1252) #3 (-378, -1055) #4 (2, -1055) #5 (2, 537) (the long lap round the block).
- [ ] **Delete old #0 to #4 (five nodes)** and **type these four in their place** (so the bus goes south, east along 125th, then north up Midway):
| # | X | Y | Z |
|---|---|---|---|
| 0 | -502 | 0 | -1200 |
| 1 | -502 | 0 | -1252 |
| 2 | 2 | 0 | -1252 |
| 3 | 2 | 0 | -1055 |

- [ ] The old #5 `(2, 537)` is now node #4 and carries on up Midway unchanged. Check there is no duplicate `(2, -1055)`.

**MERIDIAN SQUARE (main): arrive at A and leave from the moved pickup bay**

- [ ] Meridian's moved bays: **A drop-off `s0131` at X=-30**, **A pickup `s0135` at X=-486**, **A2 drop-off `s0244` at X=-30**, **A2 pickup `s0245` at X=-477**.
- [ ] **Outbound (arrival):** last node #8 `(-20, 3032)`: **change to `(-34, 0, 3032)`** so the bus reaches the drop-off at X=-30.
- [ ] **Inbound (departure):** old nodes #0–#2 are `(-120, 3032) (-147, 3032) (-152, 2995)` (the old short arc). **Delete #0, #1, #2** and insert these nodes at the start, in order, then keep the old node #3 `(-5, 2994)` and everything after it:
| # | X | Y | Z |
|---|---|---|---|
| 0 | -486 | 0 | 3032 |
| 1 | -541 | 0 | 3023 |
| 2 | -543 | 0 | 3012 |
| 3 | -544 | 0 | 3000 |
| 4 | -540 | 0 | 2995 |



### Route 201

**PARKVIEW: leaving Parkview Commons (outbound)**

- [ ] Exactly the same edit as Route 1: old nodes #0–#4 are `(-502,-1180) (-502,-1252) (-378,-1252) (-378,-1055) (2,-1055)`. **Delete those five and type these four**:
| # | X | Y | Z |
|---|---|---|---|
| 0 | -502 | 0 | -1200 |
| 1 | -502 | 0 | -1252 |
| 2 | 2 | 0 | -1252 |
| 3 | 2 | 0 | -1055 |

- [ ] Old #5 `(2, 537)` follows unchanged.

**MERIDIAN SQUARE (main): arrive at A and leave from the moved pickup bay**

- [ ] Meridian's moved bays: **A drop-off `s0131` at X=-30**, **A pickup `s0135` at X=-486**, **A2 drop-off `s0244` at X=-30**, **A2 pickup `s0245` at X=-477**.
- [ ] **Outbound (arrival):** last node #16 `(-20, 3032)`: **change to `(-34, 0, 3032)`** so the bus reaches the drop-off at X=-30.
- [ ] **Inbound (departure):** old nodes #0–#2 are `(-120, 3032) (-147, 3032) (-152, 2995)` (the old short arc). **Delete #0, #1, #2** and insert these nodes at the start, in order, then keep the old node #3 `(-5, 2994)` and everything after it:
| # | X | Y | Z |
|---|---|---|---|
| 0 | -486 | 0 | 3032 |
| 1 | -541 | 0 | 3023 |
| 2 | -543 | 0 | 3012 |
| 3 | -544 | 0 | 3000 |
| 4 | -540 | 0 | 2995 |



### Route 7

**SOUTHSIDE: P moved back 55 m off the junction**

- [ ] `s0292` (P) is now at z=-2640 and `s0291` (D) at z=-2540. Outbound node #0 `(920, -2660)` → **`(934, 0, -2640)`**. Node #1 `(929,-2698)` stays: the bus turns west onto -270th St there.
- [ ] Inbound: no change (it already ends at z=-2600, before P).

**UNIVERSITY PARK: bay [4] on Campus Drive**

- [ ] `terminalZCode` = `s0621` (UNIVERSITY PARK [4]). In the outbound list replace the last stop `s0332` with `s0621`; in the inbound list replace the first stop `s0332` with `s0621`.
- [ ] **Outbound (arrival):** old last node #2 `(-2910, -2698)`. **Delete #2** and **append**: `(-2965, 0, -2698)`, `(-2965, 0, -2608)`, `(-2810, 0, -2608)`.
- [ ] **Inbound (departure):** old #0–#8 are the loop around the old block. **Delete #0–#8** (9 nodes) and **insert at the start**: `(-2810, 0, -2608)`, `(-2774, 0, -2608)`, `(-2774, 0, -2712)`. Keep the old #9 `(1002, -2712)` and the rest.


### Route 10

**SOUTHSIDE: P moved back 55 m off the junction**

- [ ] `s0292` (P) is now at z=-2640 and `s0291` (D) at z=-2540. Outbound node #0 `(937, -2660)` → **`(934, 0, -2640)`**. Leave the rest.
- [ ] Inbound: no change (it already ends at z=-2600, before P).

**0TH AV / 200TH ST: nothing to type**

- [ ] Route 10 already turns round on -10th Av. Only the stop positions changed (Phase 3). **No node, no stop-list edits.**


### Route 21

**SOUTH PIER: bays on the -190th St / Platform Road loop**

- [ ] Old outbound start: `(1698,-1960) (1698,-2000) (1992,-2000)`. **Delete #0–#2**, insert these two at the start (21 loads at P1 `s0247` on the new -193rd St, then turns north onto 199th Av):
| # | X | Y | Z |
|---|---|---|---|
| 0 | 1790 | 0 | -1937 |
| 1 | 1992 | 0 | -1937 |

- [ ] Old inbound end: `(1988,-1900) (1900,-1905)` (#16, #17). **Change #16 to `(1988, 0, -1905)` and #17 to `(1850, 0, -1905)`** (the drop-off `s0246` moved to X=1850).

**CRESTBURY: the shared Terminal Mall (-65th St, westbound)**

- [ ] Old outbound end `(-3702,-110) (-3702,-608) (-3726,-608) (-3726,-630)` (#14–#17). **Delete #15, #16, #17** and **append**:
| # | X | Y | Z |
|---|---|---|---|
| 15 | -3702 | 0 | -612 |
| 16 | -3484 | 0 | -612 |
| 17 | -3484 | 0 | -652 |
| 18 | -3560 | 0 | -652 |

- [ ] Old inbound start `(-3730,-600) (-3726,-702) (-3698,-702) (-3698,-112)` (#0–#3). **Delete #0, #1, #2** and **insert at the start**: `(-3560, 0, -652)`, `(-3698, 0, -652)`. Keep old #3 `(-3698, -112)`.
- [ ] Stops: none (`s0092` just moved to X=-3560).


### Route 25

**NW POINT: bay on 230th Av**

- [ ] `s0184` moved to z=2150. **Outbound:** node #0 `(-2305, 2120)` → **`(-2302, 0, 2150)`**; node #1 `(-2300, 1998)` → **`(-2302, 0, 1998)`**.
- [ ] **Inbound:** node #12 `(-2300, 2220)` → **`(-2302, 0, 2218)`**; node #13 `(-2300, 2090)` → **`(-2302, 0, 2150)`**.
- [ ] Stops: no change.


### Route 29

> **Route 29 is rewritten or converted in full in Phase 5. Do that section instead of anything here.**


### Route 34

**MERIDIAN SQUARE: Variant A passes along Leaf Blvd**

- [ ] **Passing buses get the new Leaf Blvd through stops:** `LEAF BLVD & W 20 AV` eastbound is `s0597` (X=-200), westbound is `s0598`.
- [ ] Only **variant A** passes Meridian. In `variants > A`: **outboundStopsOverride** insert `s0597`, **inboundStopsOverride** insert `s0598`, in the same place as route 116 (between the stops just before and after x=-200 on Leaf Blvd). Give each a `minutesFromStart` between its neighbours (use the average).

**NORTH BEACH: 34 / 34A reversed to run the Beach Mall westbound**

- [ ] 34 now loads on the **same side and direction as 45 and 114**: east along 340th, **south down 295th Av**, **west along the Beach Mall**, north up 280th Av, back west on 340th.
- [ ] Mainline **inbound** end: old `(-7502,3398) (2988,3398) (2988,3200)` (#2–#4). **Change #3 to `(2948, 0, 3398)`**, **delete #4**, then **append**: `(2948, 0, 3205)` and `(2905, 0, 3205)` (end at the new D bay `s0511` at X=2905).
- [ ] Mainline **outbound** start: old `(2952,3050) (2952,3402) (-7698,3402)` (#0–#2). **Delete #0 and #1**, **insert at the start**: `(2865, 0, 3205)`, `(2802, 0, 3205)`, `(2802, 0, 3402)`. Keep old #2.
- [ ] **Variant A**: do the **same two edits** inside `variants > A`: `inboundNodesOverride` (old end `(2988,3398) (2988,3200)`, #7–#8: change #7 to `(2948,0,3398)`, delete #8, append the two nodes) and `outboundNodesOverride` (old start `(2952,3050) (2952,3402) (-702,3402)`: delete #0–#1 and insert the three nodes).
- [ ] **Stops:** the new drop-off on 295th, `s0612` (z=3300), goes **in the inbound list immediately before `s0511`** (and the same inside variant A's inbound stops).

**Where exactly do `s0597` (eastbound) and `s0598` (westbound) go?**

- Outbound (eastbound on Leaf Blvd): insert `s0597` between `s0128` (LEAF BLVD & W 45 AV) and `s0129` (LEAF BLVD & W 19 AV).
- Inbound (westbound on Leaf Blvd): insert `s0598` between `s0129` (LEAF BLVD & W 19 AV) and `s0128` (LEAF BLVD & W 45 AV).


### Route 45

**SOUTH PIER: 45 gets its own bays on -200th St; NORTH BEACH: Beach Mall**

- [ ] Old outbound start `(1698,-1940) (1698,-2002)`. **Delete #0–#1** and insert these five at the start:
| # | X | Y | Z |
|---|---|---|---|
| 0 | 1780 | 0 | -1998 |
| 1 | 1702 | 0 | -1998 |
| 2 | 1702 | 0 | -1937 |
| 3 | 1988 | 0 | -1937 |
| 4 | 1988 | 0 | -2002 |

- [ ] Old inbound end `(2500,-1999) (1992,-1998) (1992,-1900) (1900,-1900)` (#22–#25). **Delete #23, #24, #25** and add `(1940, 0, -1998)` as the new last node.
- [ ] **Terminals and stops:** `terminalACode` = `s0609`. Outbound first stop `s0247` → `s0609`. Inbound last stop `s0246` → `s0607`.
- [ ] **Sunset Point:** no change. 45 keeps `s0562 s0563 s0564` (the straight is now also the bays for 136 and 236).

**NORTH BEACH: arrive and leave on the Beach Mall (westbound)**

- [ ] Old outbound end: `(2995,3002) (2952,3002) (2952,3100)` (#27–#29). **Delete #29**, then **append**: `(2952, 0, 3205)` and `(2865, 0, 3205)`. Stops: `s0512` stays last (it moved to X=2865).
- [ ] Old inbound start: `(2952,3090) (2952,3398) (2985,3398)` (#0–#2). **Delete #0 and #1**, **insert at the start**: `(2865, 0, 3205)`, `(2802, 0, 3205)`, `(2802, 0, 3398)`. Keep old #2 `(2985, 3398)`.


### Route 73

**PARKVIEW WEST: arriving at the B bays**

- [ ] Parkview B [D] (`s0406`) moves west to X=-790. The inbound route must reach it:
- [ ] Inbound last node is #8 `(-760, -1048)`. **Change X to `-790`**: `(-790, 0, -1048)`.
- [ ] The outbound first node is already at X ≈ -860 (`s0407`), so no change there.

**VALLEY FIELDS: stops closer together**

- [ ] `s0476` (D) is now at z=-1230, `s0477` (P) at z=-1130, both on -750th Av.
- [ ] **Outbound:** last node #8 `(-7498, -1250)` → **`(-7498, 0, -1225)`** (so the bus reaches D).
- [ ] **Inbound:** first node #0 `(-7498, -1100)` → **`(-7498, 0, -1140)`** (so the bus starts before P).
- [ ] Stops: no list change.


### Route 85

**MERIDIAN SQUARE B: one counter-clockwise lap, no hairpins**

- [ ] Old inbound end: `(-702, 4098) (-702, 3500) (-672, 3580)` (hairpin). Old outbound start: `(-672, 3840) (-698, 3838) (-698, 4102)`.
- [ ] **Inbound: delete #6, #7, #8** (the last three nodes) and **type these four** (south on -70th Av, east along the south side, north up the east side to D):
| # | X | Y | Z |
|---|---|---|---|
| 6 | -702 | 0 | 4098 |
| 7 | -702 | 0 | 3498 |
| 8 | -536 | 0 | 3498 |
| 9 | -536 | 0 | 3604 |

- [ ] **Outbound: delete #0, #1, #2** and **type these four at the start**:
| # | X | Y | Z |
|---|---|---|---|
| 0 | -536 | 0 | 3796 |
| 1 | -536 | 0 | 3902 |
| 2 | -698 | 0 | 3902 |
| 3 | -698 | 0 | 4102 |

- [ ] Stops: no list change. `s0478` (D, z=3600) and `s0479` (P, z=3800) are already last/first and now sit on the east kerb of the same side, D first and P further on, so the next trip starts ahead of where this one ended.

**VALLEY FIELDS: stops closer together**

- [ ] `s0476` (D) is now at z=-1230, `s0477` (P) at z=-1130, both on -750th Av.
- [ ] **Outbound:** last node #6 `(-7498, -1250)` → **`(-7498, 0, -1225)`** (so the bus reaches D).
- [ ] **Inbound:** first node #0 `(-7498, -1100)` → **`(-7498, 0, -1140)`** (so the bus starts before P).
- [ ] Stops: no list change.


### Route 87

**PARKVIEW WEST: arriving at the B bays**

- [ ] Parkview B [D] (`s0406`) moves west to X=-790. The inbound route must reach it:
- [ ] Inbound last node is #9 `(-755, -1048)`. **Change it to** `(-790, 0, -1048)`.
- [ ] Variant **A** inbound (`variants > A > inboundNodesOverride`): last node #17 `(-765, -1048)`. **Change it to** `(-790, 0, -1048)`.
- [ ] The outbound first node is already at X ≈ -860 (`s0407`), so no change there.

**MERIDIAN SQUARE (main): arrive at A and leave from the moved pickup bay**

- [ ] Meridian's moved bays: **A drop-off `s0131` at X=-30**, **A pickup `s0135` at X=-486**, **A2 drop-off `s0244` at X=-30**, **A2 pickup `s0245` at X=-477**.
- [ ] **Outbound:** last node #11 `(-20, 3032)` → **`(-34, 0, 3032)`**. **Variant A outbound** (`variants > A > outboundNodesOverride`): last node → **`(-34, 0, 3032)`**.
- [ ] **Inbound:** old #0–#2 are `(-110, 3032) (-147, 3032) (-152, 3005)`. **Delete #0–#2**, insert at the start (Route 87 turns WEST onto Leaf Blvd, so the last new node is at z=3003):
| # | X | Y | Z |
|---|---|---|---|
| 0 | -486 | 0 | 3032 |
| 1 | -541 | 0 | 3023 |
| 2 | -543 | 0 | 3012 |
| 3 | -544 | 0 | 3000 |
| 4 | -540 | 0 | 3003 |

- [ ] Keep the old #3 `(-702, 3005)` and everything after it. **Do the same for variant A inbound**: delete its #0–#2 `(-110,3032) (-147,3032) (-151,3005)` and insert the same list, keep its old #3 `(-698, 3005)`.


### Route 101

**MERIDIAN SQUARE A2: leave from the moved A2 pickup bay**

- [ ] Meridian's moved bays: **A drop-off `s0131` at X=-30**, **A pickup `s0135` at X=-486**, **A2 drop-off `s0244` at X=-30**, **A2 pickup `s0245` at X=-477**.
- [ ] **Outbound (leaving):** old #0–#3 are `(-110,3062) (-139,3062) (-147,3032) (-151,3002)`. **Delete #0–#3**, insert (Route 101 goes WEST on Leaf Blvd):
| # | X | Y | Z |
|---|---|---|---|
| 0 | -477 | 0 | 3062 |
| 1 | -533 | 0 | 3054 |
| 2 | -536 | 0 | 3044 |
| 3 | -539 | 0 | 3034 |
| 4 | -541 | 0 | 3023 |
| 5 | -543 | 0 | 3012 |
| 6 | -544 | 0 | 3000 |
| 7 | -540 | 0 | 3003 |

- [ ] Keep the old #4 `(-698, 3002)` and the rest.
- [ ] **Inbound (arrival):** last node #9 `(-20, 3062)` → **`(-34, 0, 3062)`**.


### Route 109

**0TH AV / 200TH ST: pickup moves west of -10th Av**

- [ ] `s0374` (D) nudged to X=-30, `s0375` (P) to X=-80, new `[3]` `s0622` at X=-200.
- [ ] Outbound: no change.
- [ ] **Inbound:** node #0 `(-25, 2030)` → **`(-200, 0, 2036)`**; node #1 `(-70, 2032)` → **`(-240, 0, 2034)`**.
- [ ] Stops: in the inbound list replace the first stop `s0375` with `s0622`. `terminalZCode` stays `s0374`.


### Route 114

**BERRELINGWAY NORTH: drop-off on 40th Av**

- [ ] No node change: 114 already comes up 40th Av, east on Leaf, south on 49th Av.
- [ ] Stops, **inbound**: the new `s0601` (BERRELINGWAY NORTH [D], on 40th Av at z=2960): insert `s0601` **between** `s0151` (E 40 AV & N 299 ST) **and** `s0153` (Berrelingway North Local).

**NORTH BEACH: no more U-turn**

- [ ] Old outbound end: `(2995,3002) (2952,3002) (2952,3110)` (#12–#14). **Delete #14**, **append**: `(2952, 0, 3205)` and `(2865, 0, 3205)`.
- [ ] Old inbound start: `(2948,3080) (2952,3010) (2952,3398) (2985,3398)` (#0–#3). **Delete #0, #1, #2** (this is the U-turn), **insert at the start**: `(2865, 0, 3205)`, `(2802, 0, 3205)`, `(2802, 0, 3398)`. Keep old #3 `(2985, 3398)`.


### Route 116

**MERIDIAN SQUARE: new Leaf Blvd through stops**

- [ ] **Passing buses get the new Leaf Blvd through stops:** `LEAF BLVD & W 20 AV` eastbound is `s0597` (X=-200), westbound is `s0598`.
- [ ] No node change. **Outbound** (eastbound) stop list: insert `s0597` between the stop just west of Meridian and the one just east. **Inbound** (westbound): insert `s0598` likewise. See the neighbour table below this route's steps.

**BERRELINGWAY NORTH: nothing to do**

- [ ] 116 still arrives along Leaf Blvd and loads at the Local stop. No node or stop edits.

**CRESTBURY: the shared Terminal Mall**

- [ ] Old outbound start `(-3725, -650) (-3725, -702) (-2158, -702)`. **Delete #0 and #1** and **insert at the start**: `(-3670, 0, -652)`, `(-3702, 0, -652)`, `(-3702, 0, -702)`. Keep old #2 `(-2158, -702)`.
- [ ] Old inbound end `(-3698,-698) (-3698,-608) (-3726,-608) (-3726,-630)` (#6–#9). **Delete those four** and **append**: `(-3478, 0, -698)`, `(-3478, 0, -652)`, `(-3620, 0, -652)`.
- [ ] **Terminals and stops:** `terminalACode` = `s0613`. Outbound first stop `s0092` → `s0613`. Inbound last stop: replace the old last stop with `s0099` (`CRESTBURY TERMINAL [DROPOFF]`, now on the Mall at X=-3620).

**Where exactly do `s0597` (eastbound) and `s0598` (westbound) go?**

- Outbound (eastbound on Leaf Blvd): insert `s0597` between `s0128` (LEAF BLVD & W 45 AV) and `s0129` (LEAF BLVD & W 19 AV).
- Inbound (westbound on Leaf Blvd): insert `s0598` between `s0129` (LEAF BLVD & W 19 AV) and `s0128` (LEAF BLVD & W 45 AV).


### Route 136

> **Route 136 is rewritten or converted in full in Phase 5. Do that section instead of anything here.**


### Route 140

**PARKVIEW: inbound now comes down the new Midway stretch**

- [ ] **Outbound: no node change.** The old nodes already go straight along z=-1172 from the garage to x=402. That straight line was the grass; the new -117th St road now sits under it.
- [ ] **Inbound, change 2 nodes** (old list `(398,-1168) (-202,-1168) (-202,-1248) (-378,-1248) (-378,-1200)` is nodes #7–#11):
- [ ] Node #8: `(-202, -1168)` → **`(-2, 0, -1168)`**.
- [ ] Node #9: `(-202, -1248)` → **`(-2, 0, -1248)`**.
- [ ] Node #11 stays `(-378, -1200)`.
- [ ] **Stops, inbound:** the new Garage drop-off `s0596` sits on 125th just before the end. In the inbound stop list insert `s0596` **between** `s0152` (S 117 ST & Bay Block) **and** `s0154` (PARKVIEW PARKING GARAGE [E]).

**BERRELINGWAY NORTH: one lap, ends at the Station bay on the connector**

- [ ] Old outbound end: `(402, 2995) (488, 2995) (488, 2970)` (nodes #3–#5). Old inbound start (#0–#6): `(488, 2901) (488, 2900) (402, 2900) (402, 2995) (488, 2995) (488, 2900) (398, 2900)` (the double lap).
- [ ] **Outbound: delete the last 3 nodes** `(402, 2995) (488, 2995) (488, 2970)` and **append**:
| # | X | Y | Z |
|---|---|---|---|
| 3 | 402 | 0 | 2898 |
| 4 | 485 | 0 | 2898 |

- [ ] **Inbound: delete #0–#6** and **insert at the start**:
| # | X | Y | Z |
|---|---|---|---|
| 0 | 485 | 0 | 2898 |
| 1 | 556 | 0 | 2898 |
| 2 | 556 | 0 | 2793 |
| 3 | 398 | 0 | 2793 |

- [ ] Keep the old inbound #7 `(398, -1168)` and everything after it.
- [ ] **Terminal and stops:** `terminalZCode` = `s0089` (was `s0153`). Outbound last stop: replace `s0153` with `s0089`. Inbound first stop: replace `s0153` with `s0089`. Station [2] `s0602` is a spare bay, not in any list.


### Route 199

**MERIDIAN SQUARE A2: arrive at A2 and leave from the moved pickup bay**

- [ ] Meridian's moved bays: **A drop-off `s0131` at X=-30**, **A pickup `s0135` at X=-486**, **A2 drop-off `s0244` at X=-30**, **A2 pickup `s0245` at X=-477**.
- [ ] **Outbound (arrival):** last node #6 `(-20, 3062)` → **`(-34, 0, 3062)`**.
- [ ] **Inbound (departure):** old #0–#3 are `(-100, 3062) (-139,3062) (-147,3032) (-151,3002)`. **Delete #0–#3**, insert:
| # | X | Y | Z |
|---|---|---|---|
| 0 | -477 | 0 | 3062 |
| 1 | -533 | 0 | 3054 |
| 2 | -536 | 0 | 3044 |
| 3 | -539 | 0 | 3034 |
| 4 | -541 | 0 | 3023 |
| 5 | -543 | 0 | 3012 |
| 6 | -544 | 0 | 3000 |
| 7 | -540 | 0 | 3003 |

- [ ] Keep the old #4 `(-698, 3002)` and everything after it.

**SOUTH PIER: own bays P `s0605` and D `s0603`**

- [ ] Old outbound start `(1698, -1940) (1698, -2000) (1992, -2000)`. **Delete #0–#2** and insert: `(0) (1860, 0, -1937)` then `(1) (1992, 0, -1937)`.
- [ ] Old inbound end `(1988, -1900) (1900, -1900)` (the last two nodes). **Change them to** `(1988, 0, -1905)` and `(1930, 0, -1905)`.
- [ ] **Terminals and stops:** `terminalACode` = `s0605`. Outbound **first stop** `s0247` → `s0605`. Inbound **last stop** `s0246` → `s0603`.


### Route 216

**MERIDIAN SQUARE: new Leaf Blvd through stops**

- [ ] **Passing buses get the new Leaf Blvd through stops:** `LEAF BLVD & W 20 AV` eastbound is `s0597` (X=-200), westbound is `s0598`.
- [ ] No node change. **Outbound**: insert `s0597` in the same place as route 116. **Inbound**: insert `s0598` in the same place as route 116.

**BERRELINGWAY NORTH: nothing to do**

- [ ] Same as 116. No edits.

**CRESTBURY: the shared Terminal Mall**

- [ ] Old outbound start `(-3725, -660) (-3725, -702) (-2158, -702)`. **Delete #0 and #1** and **insert at the start**: `(-3670, 0, -652)`, `(-3702, 0, -652)`, `(-3702, 0, -702)`. Keep old #2 `(-2158, -702)`.
- [ ] Old inbound end `(-3698,-698) (-3698,-608) (-3726,-608) (-3726,-630)` (#10–#13). **Delete those four** and **append**: `(-3478, 0, -698)`, `(-3478, 0, -652)`, `(-3620, 0, -652)`.
- [ ] **Terminals and stops:** `terminalACode` = `s0613`. Outbound first stop `s0092` → `s0613`. Inbound last stop: replace the old last stop with `s0099` (`CRESTBURY TERMINAL [DROPOFF]`, now on the Mall at X=-3620).

**Where exactly do `s0597` (eastbound) and `s0598` (westbound) go?**

- Outbound (eastbound on Leaf Blvd): insert `s0597` between `s0124` (LEAF BLVD & W 185 AV) and `s0129` (LEAF BLVD & W 19 AV).
- Inbound (westbound on Leaf Blvd): insert `s0598` between `s0129` (LEAF BLVD & W 19 AV) and `s0124` (LEAF BLVD & W 185 AV).


### Route 240

**PARKVIEW: inbound now comes down the new Midway stretch**

- [ ] Same as Route 140. Outbound nodes: no change. **Inbound** nodes #8 `(-202,-1168)` → `(-2, 0, -1168)` and #9 `(-202,-1248)` → `(-2, 0, -1248)`.
- [ ] Stops inbound: in the inbound stop list insert `s0596` **between** `s0152` (S 117 ST & Bay Block) **and** `s0154` (PARKVIEW PARKING GARAGE [E]).

**BERRELINGWAY NORTH: one lap, ends at the Station bay on the connector**

- [ ] Old outbound end: `(442, 2995) (488, 2995) (488, 2970)` (nodes #3–#5). Old inbound start (#0–#6): `(488, 2910) (488, 2900) (442, 2900) (442, 2995) (488, 2995) (488, 2900) (438, 2900)` (the double lap).
- [ ] **Outbound: delete the last 3 nodes** `(442, 2995) (488, 2995) (488, 2970)` and **append**:
| # | X | Y | Z |
|---|---|---|---|
| 3 | 442 | 0 | 2898 |
| 4 | 485 | 0 | 2898 |

- [ ] **Inbound: delete #0–#6** and **insert at the start**:
| # | X | Y | Z |
|---|---|---|---|
| 0 | 485 | 0 | 2898 |
| 1 | 556 | 0 | 2898 |
| 2 | 556 | 0 | 2793 |
| 3 | 438 | 0 | 2793 |

- [ ] Keep the old inbound #7 `(438, -1168)` and everything after it.
- [ ] **Terminal and stops:** `terminalZCode` = `s0089` (was `s0153`). Outbound last stop: replace `s0153` with `s0089`. Inbound first stop: replace `s0153` with `s0089`. Station [2] `s0602` is a spare bay, not in any list.


### Route 299

**MERIDIAN SQUARE A2: arrive at A2 and leave from the moved pickup bay**

- [ ] Meridian's moved bays: **A drop-off `s0131` at X=-30**, **A pickup `s0135` at X=-486**, **A2 drop-off `s0244` at X=-30**, **A2 pickup `s0245` at X=-477**.
- [ ] **Outbound (arrival):** last node #6 `(-20, 3062)` → **`(-34, 0, 3062)`**.
- [ ] **Inbound (departure):** old #0–#3 are `(-90, 3062) (-139,3062) (-147,3032) (-151,3002)`. **Delete #0–#3**, insert:
| # | X | Y | Z |
|---|---|---|---|
| 0 | -477 | 0 | 3062 |
| 1 | -533 | 0 | 3054 |
| 2 | -536 | 0 | 3044 |
| 3 | -539 | 0 | 3034 |
| 4 | -541 | 0 | 3023 |
| 5 | -543 | 0 | 3012 |
| 6 | -544 | 0 | 3000 |
| 7 | -540 | 0 | 3003 |

- [ ] Keep the old #4 `(-698, 3002)` and everything after it.

**SOUTH PIER: own bays P `s0606` and D `s0604`**

- [ ] Old outbound start `(1698, -1950) (1698, -2000) (1992, -2000)`. **Delete #0–#2** and insert: `(0) (1930, 0, -1937)` then `(1) (1992, 0, -1937)`.
- [ ] Old inbound end `(1988, -1900) (1900, -1900)` (the last two nodes). **Change them to** `(1988, 0, -1905)` and `(1790, 0, -1905)`.
- [ ] **Terminals and stops:** `terminalACode` = `s0606`. Outbound **first stop** `s0247` → `s0606`. Inbound **last stop** `s0246` → `s0604`.


## Phase 5: The big routes in full


### 5A. Route 136: Crestbury to Sunset Point (the full-length local)

**What it is now:** the old route was NW Point to the 1000 Exchange (38 stops). The new route runs **Crestbury to Sunset Point**, about 11.5 km one way, 31 stops, model trip about 49 minutes.

**Copy sources (open these assets side by side, they save you typing):**
- The **Boolean Way / 36th St stretch** (`(-3332, 358)` to `(968, 358)`) is already in the **current 136** outbound nodes (#3 to #4) and inbound nodes (#4 to #3). Keep it.
- The **299th Av southbound stretch** `(2985, 0)` to `(3012, -1304)`: copy from **Route 45 inbound nodes #4–#14** (they are the southbound lane).
- The **299th Av stretch from z=-1304 to z=-2000**: copy from **Route 45 outbound nodes #10–#14**, *reversed*, and **subtract 8 from every X** (45's outbound is the other lane).
- The **Sunset Point loop** (curve, 350th Av, straight): Route 45 outbound nodes #4–#8 drive the same loop. Easiest: type the table below (its curve nodes sit on the real curved -200th St), or copy 45's #4–#8 and then change the last node to `(3150, -1998)`.
- The **Crestbury end** copies **Route 21's** end: 21 outbound nodes #14–#17 and inbound #0–#3 are the old stub approach, replaced by the new Mall nodes in the table below.

#### 136: fields to type

| field | value |
|---|---|
| routeNumber | `136` (unchanged) |
| routeName | `Crestbury - Sunset Point` |
| routeColor | keep (or pick a new one) |
| terminalACode | `s0614` (CRESTBURY [4]) |
| terminalZCode | `s0563` (SUNSET POINT [D]) |
| destinationNameOutbound | `Sunset Point` |
| destinationNameInbound | `Crestbury` |
| routeQualifierOutbound / Inbound | blank |
| articulatedPolicy | `Preferred`  (was Allowed: now it picks the 10 normal artics first) |
| allowedFleetSeries | **keep the existing list** (1100, 1400, 1500, 1600, 1900, 2000, 2200, 2400, 2500, 2700) **and add `2800`** |
| maxBusesAllowed | `8`  (15-min peak needs 8) |
| oneWayTripMinutes | `50` |
| operatingStartMinutes / EndMinutes | `240` / `1440`  (4:00 to 24:00; Variant N covers 0:00–4:00) |
| nightFleetSeries | leave as it is (1000, 1700, 1900). It already covers the 4:00–5:00 hour of the mainline. |

**Schedule:** right-click the asset's title bar > **Templates > Apply Non-24hr (4am-12am)**. Then in `scheduleWindows` set (suggested; change to taste):

| label | start | end | headwayFromA | headwayFromZ | Trip % |
|---|---|---|---|---|---|
| Early | 240 | 390 | 30 | 30 | 90 |
| AM Peak | 390 | 540 | 15 | 15 | 105 |
| Midday | 540 | 930 | 20 | 20 | 100 |
| PM Peak | 930 | 1110 | 15 | 15 | 105 |
| Evening | 1110 | 1440 | 30 | 30 | 90 |

#### 136 outbound nodes (Crestbury to Sunset Point): delete the whole old list and type these 30

| # | X | Y | Z | isCurve |
|---|---|---|---|---|
| 0 | -3698 | 0 | -652 |  |
| 1 | -3698 | 0 | -112 |  |
| 2 | -3328 | 0 | -112 |  |
| 3 | -3328 | 0 | 358 |  |
| 4 | 968 | 0 | 358 |  |
| 5 | 968 | 0 | 538 |  |
| 6 | 998 | 0 | 538 |  |
| 7 | 998 | 0 | 358 |  |
| 8 | 2985 | 0 | 358 |  |
| 9 | 2985 | 0 | 0 | tick |
| 10 | 3014 | 0 | -130 | tick |
| 11 | 2997 | 0 | -261 | tick |
| 12 | 2960 | 0 | -391 | tick |
| 13 | 2963 | 0 | -522 | tick |
| 14 | 3001 | 0 | -652 | tick |
| 15 | 3013 | 0 | -783 | tick |
| 16 | 2981 | 0 | -913 | tick |
| 17 | 2955 | 0 | -1043 | tick |
| 18 | 2977 | 0 | -1174 | tick |
| 19 | 3012 | 0 | -1304 | tick |
| 20 | 3006 | 0 | -1435 | tick |
| 21 | 2968 | 0 | -1565 | tick |
| 22 | 2960 | 0 | -1696 | tick |
| 23 | 2995 | 0 | -1826 | tick |
| 24 | 3017 | 0 | -1957 | tick |
| 25 | 3060 | 0 | -1922 | tick |
| 26 | 3280 | 0 | -1848 | tick |
| 27 | 3500 | 0 | -1750 |  |
| 28 | 3498 | 0 | -1998 |  |
| 29 | 3150 | 0 | -1998 |  |

#### 136 inbound nodes (Sunset Point to Crestbury): delete the whole old list and type these 31

| # | X | Y | Z | isCurve |
|---|---|---|---|---|
| 0 | 3150 | 0 | -1998 |  |
| 1 | 3035 | 0 | -1998 |  |
| 2 | 3035 | 0 | -1998 | tick |
| 3 | 3025 | 0 | -1957 | tick |
| 4 | 3003 | 0 | -1826 | tick |
| 5 | 2968 | 0 | -1696 | tick |
| 6 | 2976 | 0 | -1565 | tick |
| 7 | 3014 | 0 | -1435 | tick |
| 8 | 3022 | 0 | -1304 | tick |
| 9 | 2987 | 0 | -1174 | tick |
| 10 | 2965 | 0 | -1043 | tick |
| 11 | 2991 | 0 | -913 | tick |
| 12 | 3023 | 0 | -783 | tick |
| 13 | 3011 | 0 | -652 | tick |
| 14 | 2973 | 0 | -522 | tick |
| 15 | 2970 | 0 | -391 | tick |
| 16 | 3006 | 0 | -261 | tick |
| 17 | 3024 | 0 | -130 | tick |
| 18 | 2995 | 0 | 0 | tick |
| 19 | 2995 | 0 | 362 |  |
| 20 | 998 | 0 | 362 |  |
| 21 | 998 | 0 | 538 |  |
| 22 | 966 | 0 | 538 |  |
| 23 | 966 | 0 | 362 |  |
| 24 | -3332 | 0 | 362 |  |
| 25 | -3332 | 0 | -108 |  |
| 26 | -3702 | 0 | -108 |  |
| 27 | -3702 | 0 | -612 |  |
| 28 | -3484 | 0 | -612 |  |
| 29 | -3484 | 0 | -652 |  |
| 30 | -3670 | 0 | -652 |  |

#### 136 outbound stops

| # | stopCode | stopName | minutesFromStart | isTimepoint |
|---|---|---|---|---|
| 0 | `s0614` | CRESTBURY [4] | 0.0 | yes |
| 1 | `s0100` | S 11 ST & W 338 AV | 3.7 |  |
| 2 | `s0627` | W 333 AV & N 2 ST | 4.5 |  |
| 3 | `s0628` | W 333 AV & N 19 ST | 5.3 |  |
| 4 | `s0167` | NW BOOLEAN WAY | 6.2 |  |
| 5 | `s0165` | NW BOOLEAN WAY | 7.4 | yes |
| 6 | `s0163` | NW BOOLEAN WAY | 9.4 |  |
| 7 | `s0161` | NW BOOLEAN WAY | 11.7 |  |
| 8 | `s0159` | NW BOOLEAN WAY | 14.1 |  |
| 9 | `s0064` | Civic Center Terminal | 16.4 |  |
| 10 | `s0066` | N 36 ST & W 65 AV | 17.6 | yes |
| 11 | `s0026` | N 36 ST & W 145 AV | 19.1 |  |
| 12 | `s0028` | N 36 ST & N 0 AV | 21.2 |  |
| 13 | `s0029` | N 36 ST & E 45 AV | 22.1 |  |
| 14 | `s0031` | N 36 ST & AIRPORT LANE | 23.4 |  |
| 15 | `s0033` | 1000 Exchange | 25.1 | yes |
| 16 | `s0629` | N 36 ST & E 125 AV | 27.4 |  |
| 17 | `s0630` | N 36 ST & E 175 AV | 29.6 |  |
| 18 | `s0631` | N 36 ST & E 199 AV | 30.7 |  |
| 19 | `s0632` | N 36 ST & E 225 AV | 31.7 |  |
| 20 | `s0633` | N 36 ST & E 275 AV | 33.9 | yes |
| 21 | `s0634` | N 36 ST & E 299 AV | 34.8 |  |
| 22 | `s0572` | E 299 AV & N 31 ST | 35.1 |  |
| 23 | `s0571` | E 299 AV & S 5 ST | 36.7 |  |
| 24 | `s0570` | E 299 AV & S 28 ST | 37.7 |  |
| 25 | `s0569` | E 299 AV & S 60 ST | 39.1 | yes |
| 26 | `s0568` | E 299 AV & S 97 ST | 40.7 |  |
| 27 | `s0567` | E 299 AV & S 136 ST | 42.5 |  |
| 28 | `s0566` | E 299 AV & S 179 ST | 44.4 |  |
| 29 | `s0565` | E 299 AV & S 196 ST | 45.1 |  |
| 30 | `s0563` | SUNSET POINT [D] | 49.1 | yes |

#### 136 inbound stops

| # | stopCode | stopName | minutesFromStart | isTimepoint |
|---|---|---|---|---|
| 0 | `s0564` | SUNSET POINT [P] | 0.0 | yes |
| 1 | `s0565` | E 299 AV & S 196 ST | 0.7 |  |
| 2 | `s0566` | E 299 AV & S 179 ST | 1.5 |  |
| 3 | `s0567` | E 299 AV & S 136 ST | 3.5 |  |
| 4 | `s0568` | E 299 AV & S 97 ST | 5.3 |  |
| 5 | `s0569` | E 299 AV & S 60 ST | 7.0 | yes |
| 6 | `s0570` | E 299 AV & S 28 ST | 8.5 |  |
| 7 | `s0571` | E 299 AV & S 5 ST | 9.6 |  |
| 8 | `s0572` | E 299 AV & N 31 ST | 11.2 |  |
| 9 | `s0634` | N 36 ST & E 299 AV | 11.6 |  |
| 10 | `s0633` | N 36 ST & E 275 AV | 12.6 | yes |
| 11 | `s0632` | N 36 ST & E 225 AV | 14.8 |  |
| 12 | `s0631` | N 36 ST & E 199 AV | 15.9 |  |
| 13 | `s0630` | N 36 ST & E 175 AV | 17.1 |  |
| 14 | `s0629` | N 36 ST & E 125 AV | 19.4 |  |
| 15 | `s0033` | 1000 Exchange | 21.8 | yes |
| 16 | `s0032` | AIRPORT STATION | 22.3 |  |
| 17 | `s0031` | N 36 ST & AIRPORT LANE | 23.6 |  |
| 18 | `s0029` | N 36 ST & E 45 AV | 24.9 |  |
| 19 | `s0028` | N 36 ST & N 0 AV | 25.9 |  |
| 20 | `s0026` | N 36 ST & W 145 AV | 28.1 | yes |
| 21 | `s0066` | N 36 ST & W 65 AV | 29.7 |  |
| 22 | `s0064` | Civic Center Terminal | 30.9 |  |
| 23 | `s0159` | NW BOOLEAN WAY | 33.3 |  |
| 24 | `s0161` | NW BOOLEAN WAY | 35.8 |  |
| 25 | `s0163` | NW BOOLEAN WAY | 38.2 | yes |
| 26 | `s0165` | NW BOOLEAN WAY | 40.4 |  |
| 27 | `s0167` | NW BOOLEAN WAY | 41.7 |  |
| 28 | `s0628` | W 333 AV & N 19 ST | 42.6 |  |
| 29 | `s0627` | W 333 AV & N 2 ST | 43.4 |  |
| 30 | `s0100` | S 11 ST & W 338 AV | 44.2 | yes |
| 31 | `s0613` | CRESTBURY [3] | 50.0 | yes |

> The old `s0032` (AIRPORT STATION) stays in the **inbound** list only, as before.


### 5B. Variant N (shows on boards as 136N): the night pattern for 136

**What the game does with a variant letter.** The route number and the letter are simply glued together on the boards: variant letter `N` on route 136 shows as **`136N`**. (That is how `34A` and `87A` already work.) If you want it to read **`N136`** with the N *first*, you would have to make a separate route asset with `routeNumber = N136`; I recommend the variant (below) because buses, stops and the timetable stay linked to 136. Tell me if you want the separate-asset method written out instead.

**Goal:** from 0:00 to 4:00 the 136 runs every 60 minutes as a thinned pattern. Mainline 136 covers 4:00–24:00.

**Step N.1** Open `ROUTES/CBT/136.asset`. In **Variant Configurations**, right-click the `variants` header > **Add Short Turn Variant** is NOT what you want. Instead press **+** at the bottom of the `variants` list. A new element appears.
**Step N.2** Type into the new element:

| field | value |
|---|---|
| variantLetter | `N` |
| isShortTurn | OFF |
| overrideRoute | **ON** (needed for the stop lists below to count) |
| outboundNodesOverride / inboundNodesOverride | **leave EMPTY**. Empty means 'use the mainline path'. Do not copy the nodes. |
| terminalACodeOverride / terminalZCodeOverride | leave blank (same terminals) |
| destinationNameOutboundOverride / InboundOverride | leave blank (or `Sunset Point (Night)` / `Crestbury (Night)` if you want that on the board) |
| overrideSchedule | **ON** |
| operatingStartMinutes | `0` |
| operatingEndMinutes | `240` |
| headwayFromAMinutes / headwayFromZMinutes | `60` / `60` |
| oneWayTripMinutes | `42`  (night roads are emptier; 49 min x 0.85) |
| scheduleWindows | press + once: label `Night`, windowStartMinutes `0`, windowEndMinutes `240`, headwayFromA `60`, headwayFromZ `60`, tripTimeMultiplierPercent `85`, departureOffsetMinutes `0` |
| overrideVehicleRestrictions | **ON** |
| articulatedPolicyOverride | `Prohibited`  (the 10 normal artics rest at night) |
| allowedFleetSeriesOverride | `1000` and `1900` (136's own night list is 1000, 1700, 1900; 1700 is an artic block so it is left out here) |

**Step N.3** Type the night stop lists (these skip the stops the mainline stops at). **outboundStopsOverride**:

| # | stopCode | stopName | minutesFromStart | isTimepoint |
|---|---|---|---|---|
| 0 | `s0614` | CRESTBURY [4] | 0.0 | yes |
| 1 | `s0627` | W 333 AV & N 2 ST | 3.8 |  |
| 2 | `s0628` | W 333 AV & N 19 ST | 4.4 |  |
| 3 | `s0167` | NW BOOLEAN WAY | 5.2 |  |
| 4 | `s0165` | NW BOOLEAN WAY | 6.2 |  |
| 5 | `s0161` | NW BOOLEAN WAY | 9.8 | yes |
| 6 | `s0064` | Civic Center Terminal | 13.8 |  |
| 7 | `s0026` | N 36 ST & W 145 AV | 16.0 |  |
| 8 | `s0028` | N 36 ST & N 0 AV | 17.8 |  |
| 9 | `s0033` | 1000 Exchange | 21.1 |  |
| 10 | `s0630` | N 36 ST & E 175 AV | 24.8 | yes |
| 11 | `s0631` | N 36 ST & E 199 AV | 25.8 |  |
| 12 | `s0633` | N 36 ST & E 275 AV | 28.5 |  |
| 13 | `s0634` | N 36 ST & E 299 AV | 29.3 |  |
| 14 | `s0572` | E 299 AV & N 31 ST | 29.5 |  |
| 15 | `s0570` | E 299 AV & S 28 ST | 31.7 | yes |
| 16 | `s0568` | E 299 AV & S 97 ST | 34.2 |  |
| 17 | `s0566` | E 299 AV & S 179 ST | 37.3 |  |
| 18 | `s0563` | SUNSET POINT [D] | 41.2 | yes |

**inboundStopsOverride**:

| # | stopCode | stopName | minutesFromStart | isTimepoint |
|---|---|---|---|---|
| 0 | `s0564` | SUNSET POINT [P] | 0.0 | yes |
| 1 | `s0566` | E 299 AV & S 179 ST | 1.3 |  |
| 2 | `s0568` | E 299 AV & S 97 ST | 4.5 |  |
| 3 | `s0570` | E 299 AV & S 28 ST | 7.1 |  |
| 4 | `s0572` | E 299 AV & N 31 ST | 9.4 |  |
| 5 | `s0634` | N 36 ST & E 299 AV | 9.7 | yes |
| 6 | `s0633` | N 36 ST & E 275 AV | 10.6 |  |
| 7 | `s0631` | N 36 ST & E 199 AV | 13.4 |  |
| 8 | `s0630` | N 36 ST & E 175 AV | 14.4 |  |
| 9 | `s0033` | 1000 Exchange | 18.3 |  |
| 10 | `s0028` | N 36 ST & N 0 AV | 21.7 | yes |
| 11 | `s0026` | N 36 ST & W 145 AV | 23.6 |  |
| 12 | `s0064` | Civic Center Terminal | 25.9 |  |
| 13 | `s0161` | NW BOOLEAN WAY | 30.1 |  |
| 14 | `s0165` | NW BOOLEAN WAY | 33.9 |  |
| 15 | `s0167` | NW BOOLEAN WAY | 35.0 | yes |
| 16 | `s0628` | W 333 AV & N 19 ST | 35.8 |  |
| 17 | `s0627` | W 333 AV & N 2 ST | 36.5 |  |
| 18 | `s0613` | CRESTBURY [3] | 42.0 | yes |

Skipped at night: `s0029`, `s0031`, `s0066`, `s0100`, `s0159`, `s0163`, `s0565`, `s0567`, `s0569`, `s0571`, `s0629`, `s0632`. (Change this list freely: add or remove a stop code and keep the order.)

**Step N.4** The mainline must not also run 0:00–4:00. On the **mainline** set `operatingStartMinutes = 240` (done above). Check **Show Route Fleet Requirements** (right-click the asset) and confirm 136 and 136N do not overlap in time.
**Step N.5** Test: set the sim clock to 00:30 and open the dispatch list. You should see **136N** departures every 60 minutes and no plain 136.


### 5C. Route 236 Express-Max: University Park to Sunset Point (limited stop)

**Create the asset.** Select `136.asset` **after** you have finished 136 and 136N. Press **Ctrl+D**. Rename the copy `236.asset`. Open it and delete the `variants` entry (236 has no night pattern). Then type:

| field | value |
|---|---|
| routeId | `236`  (unique; never change later) |
| routeNumber | `236` |
| routeName | `University Park - Sunset Point (Express-Max)` |
| routeColor | orange, e.g. R 0.95  G 0.50  B 0.00 |
| articulatedPolicy | `Mandatory` |
| miniBusPolicy | `Prohibited` |
| allowedFleetSeries | **replace the whole copied list with just `2900`** (only the Express-Max buses) |
| nightFleetSeries | **empty** (236 does not run 0:00–4:00; an empty night list means normal rules apply, so the 4:00–5:00 hour still works) |
| maxBusesAllowed | `7`  (6 for a 20-minute peak + 1 spare) |
| terminalACode | `s0332` (UNIVERSITY PARK [1]) |
| terminalZCode | `s0625` (SUNSET POINT [2]) |
| destinationNameOutbound | `Sunset Point` |
| destinationNameInbound | `University Park` |
| routeQualifierOutbound / Inbound | `MAX` / `MAX` |
| oneWayTripMinutes | `45` |
| operatingStartMinutes / EndMinutes | `240` / `1440` |

**Schedule:** Templates > **Apply Non-24hr (4am-12am)**, then edit `scheduleWindows`: 

| label | start | end | headwayFromA | headwayFromZ | Trip % |
|---|---|---|---|---|---|
| Early | 240 | 390 | 30 | 30 | 90 |
| AM Peak | 390 | 600 | 20 | 20 | 105 |
| Midday | 600 | 930 | 30 | 30 | 100 |
| PM Peak | 930 | 1170 | 20 | 20 | 105 |
| Evening | 1170 | 1440 | 30 | 30 | 90 |

**Outbound nodes (University Park to Sunset Point).** *Copy source:* nodes #13 to the end (`(2985, 358)` and everything after: 36th St east, 299th Av south, the Sunset loop) are **identical to Route 136 outbound nodes #8 to #29**. Type #0–#12 yourself, then copy 136's #8–#29 after them:

| # | X | Y | Z | isCurve |
|---|---|---|---|---|
| 0 | -2930 | 0 | -2608 |  |
| 1 | -2774 | 0 | -2608 |  |
| 2 | -2774 | 0 | -2698 |  |
| 3 | -2965 | 0 | -2698 |  |
| 4 | -2965 | 0 | -698 |  |
| 5 | -3028 | 0 | -698 |  |
| 6 | -3028 | 0 | -608 |  |
| 7 | -3328 | 0 | -608 |  |
| 8 | -3328 | 0 | 358 |  |
| 9 | 968 | 0 | 358 |  |
| 10 | 968 | 0 | 538 |  |
| 11 | 998 | 0 | 538 |  |
| 12 | 998 | 0 | 358 |  |
| 13 | 2985 | 0 | 358 |  |
| 14 | 2985 | 0 | 0 | tick |
| 15 | 3014 | 0 | -130 | tick |
| 16 | 2997 | 0 | -261 | tick |
| 17 | 2960 | 0 | -391 | tick |
| 18 | 2963 | 0 | -522 | tick |
| 19 | 3001 | 0 | -652 | tick |
| 20 | 3013 | 0 | -783 | tick |
| 21 | 2981 | 0 | -913 | tick |
| 22 | 2955 | 0 | -1043 | tick |
| 23 | 2977 | 0 | -1174 | tick |
| 24 | 3012 | 0 | -1304 | tick |
| 25 | 3006 | 0 | -1435 | tick |
| 26 | 2968 | 0 | -1565 | tick |
| 27 | 2960 | 0 | -1696 | tick |
| 28 | 2995 | 0 | -1826 | tick |
| 29 | 3017 | 0 | -1957 | tick |
| 30 | 3060 | 0 | -1922 | tick |
| 31 | 3280 | 0 | -1848 | tick |
| 32 | 3500 | 0 | -1750 |  |
| 33 | 3498 | 0 | -1998 |  |
| 34 | 3150 | 0 | -1998 |  |

**Inbound nodes (Sunset Point to University Park):**

| # | X | Y | Z | isCurve |
|---|---|---|---|---|
| 0 | 3230 | 0 | -1998 |  |
| 1 | 3035 | 0 | -1998 |  |
| 2 | 3035 | 0 | -1998 | tick |
| 3 | 3025 | 0 | -1957 | tick |
| 4 | 3003 | 0 | -1826 | tick |
| 5 | 2968 | 0 | -1696 | tick |
| 6 | 2976 | 0 | -1565 | tick |
| 7 | 3014 | 0 | -1435 | tick |
| 8 | 3022 | 0 | -1304 | tick |
| 9 | 2987 | 0 | -1174 | tick |
| 10 | 2965 | 0 | -1043 | tick |
| 11 | 2991 | 0 | -913 | tick |
| 12 | 3023 | 0 | -783 | tick |
| 13 | 3011 | 0 | -652 | tick |
| 14 | 2973 | 0 | -522 | tick |
| 15 | 2970 | 0 | -391 | tick |
| 16 | 3006 | 0 | -261 | tick |
| 17 | 3024 | 0 | -130 | tick |
| 18 | 2995 | 0 | 0 | tick |
| 19 | 2995 | 0 | 362 |  |
| 20 | 998 | 0 | 362 |  |
| 21 | 998 | 0 | 538 |  |
| 22 | 966 | 0 | 538 |  |
| 23 | 966 | 0 | 362 |  |
| 24 | -3332 | 0 | 362 |  |
| 25 | -3332 | 0 | -612 |  |
| 26 | -3032 | 0 | -612 |  |
| 27 | -3032 | 0 | -702 |  |
| 28 | -2975 | 0 | -702 |  |
| 29 | -2975 | 0 | -2608 |  |
| 30 | -2930 | 0 | -2608 |  |

**Outbound stops (12):**

| # | stopCode | stopName | minutesFromStart | isTimepoint |
|---|---|---|---|---|
| 0 | `s0332` | UNIVERSITY PARK [1] | 0.0 | yes |
| 1 | `s0584` | W 297 AV & S 1603 BLOCK | 4.9 |  |
| 2 | `s0590` | W 297 AV & S 829 BLOCK | 7.4 |  |
| 3 | `s0628` | W 333 AV & N 19 ST | 11.8 |  |
| 4 | `s0064` | Civic Center Terminal | 20.1 |  |
| 5 | `s0028` | N 36 ST & N 0 AV | 23.7 | yes |
| 6 | `s0033` | 1000 Exchange | 26.6 |  |
| 7 | `s0631` | N 36 ST & E 199 AV | 30.7 |  |
| 8 | `s0634` | N 36 ST & E 299 AV | 33.8 |  |
| 9 | `s0568` | E 299 AV & S 97 ST | 38.1 |  |
| 10 | `s0566` | E 299 AV & S 179 ST | 40.8 | yes |
| 11 | `s0625` | SUNSET POINT [2] | 44.7 | yes |

**Inbound stops (12):**

| # | stopCode | stopName | minutesFromStart | isTimepoint |
|---|---|---|---|---|
| 0 | `s0625` | SUNSET POINT [2] | 0.0 | yes |
| 1 | `s0566` | E 299 AV & S 179 ST | 1.5 |  |
| 2 | `s0568` | E 299 AV & S 97 ST | 4.4 |  |
| 3 | `s0634` | N 36 ST & E 299 AV | 9.3 |  |
| 4 | `s0631` | N 36 ST & E 199 AV | 12.7 |  |
| 5 | `s0033` | 1000 Exchange | 17.3 | yes |
| 6 | `s0028` | N 36 ST & N 0 AV | 20.5 |  |
| 7 | `s0064` | Civic Center Terminal | 24.4 |  |
| 8 | `s0628` | W 333 AV & N 19 ST | 33.6 |  |
| 9 | `s0590` | W 297 AV & S 829 BLOCK | 38.5 |  |
| 10 | `s0584` | W 297 AV & S 1603 BLOCK | 41.3 | yes |
| 11 | `s0332` | UNIVERSITY PARK [1] | 45.0 | yes |

**Register 236** (see Phase 6): add it to CityManager > Routes, BusScheduler > managedRoutes, and the Routes Served list of the depots that will hold the Max buses.


### 5D. Route 29: University Park to NW Point

**What changed:** 29 used to run Parkview A to University Park. It now runs **University Park to NW Point**. **Do not run at night**: set `operatingStartMinutes = 300` (5:00), `operatingEndMinutes = 1440`, and leave `nightFleetSeries` empty.

**Copy sources:** the **-297th Av northbound stretch** is in the **current 29 inbound** nodes (#7–#9). The **NW leg** (-333rd Av north, 200th St, -216th Av, 222nd St) is the **old 136**: outbound nodes #1–#3 and inbound #4–#9 describe exactly that loop. You can copy them, then retype the lane offsets from the tables below.

| field | value |
|---|---|
| terminalACode | `s0619` (UNIVERSITY PARK [2]) |
| terminalZCode | `s0616` (NW POINT [2]) |
| destinationNameOutbound | `NW Point` |
| destinationNameInbound | `University Park` |
| articulatedPolicy | `Allowed` |
| oneWayTripMinutes | `30` |
| operatingStartMinutes / EndMinutes | `300` / `1440` |
| maxBusesAllowed | `5` |

**Outbound nodes (University Park to NW Point), delete the old list and type:**

| # | X | Y | Z | isCurve |
|---|---|---|---|---|
| 0 | -2890 | 0 | -2608 |  |
| 1 | -2774 | 0 | -2608 |  |
| 2 | -2774 | 0 | -2698 |  |
| 3 | -2965 | 0 | -2698 |  |
| 4 | -2965 | 0 | -698 |  |
| 5 | -3028 | 0 | -698 |  |
| 6 | -3028 | 0 | -108 |  |
| 7 | -3328 | 0 | -108 |  |
| 8 | -3328 | 0 | 1998 |  |
| 9 | -2158 | 0 | 1998 |  |
| 10 | -2158 | 0 | 2216 |  |
| 11 | -2302 | 0 | 2216 |  |
| 12 | -2302 | 0 | 2100 |  |

**Inbound nodes (NW Point to University Park):**

| # | X | Y | Z | isCurve |
|---|---|---|---|---|
| 0 | -2302 | 0 | 2100 |  |
| 1 | -2302 | 0 | 2002 |  |
| 2 | -3332 | 0 | 2002 |  |
| 3 | -3332 | 0 | -112 |  |
| 4 | -3032 | 0 | -112 |  |
| 5 | -3032 | 0 | -702 |  |
| 6 | -2975 | 0 | -702 |  |
| 7 | -2975 | 0 | -2608 |  |
| 8 | -2890 | 0 | -2608 |  |

**Outbound stops (21):**

| # | stopCode | stopName | minutesFromStart | isTimepoint |
|---|---|---|---|---|
| 0 | `s0619` | UNIVERSITY PARK [2] | 0.0 | yes |
| 1 | `s0578` | W 297 AV & S 2376 BLOCK | 3.0 |  |
| 2 | `s0580` | W 297 AV & S 2118 BLOCK | 4.1 |  |
| 3 | `s0582` | W 297 AV & S 1860 BLOCK | 5.2 |  |
| 4 | `s0584` | W 297 AV & S 1603 BLOCK | 6.3 |  |
| 5 | `s0586` | W 297 AV & S 1345 BLOCK | 7.4 | yes |
| 6 | `s0588` | W 297 AV & S 1087 BLOCK | 8.5 |  |
| 7 | `s0590` | W 297 AV & S 829 BLOCK | 9.6 |  |
| 8 | `s0101` | S 11 ST & W 307 AV | 13.1 |  |
| 9 | `s0627` | W 333 AV & N 2 ST | 14.7 |  |
| 10 | `s0628` | W 333 AV & N 19 ST | 15.4 | yes |
| 11 | `s0168` | W 333 AV & N 44 ST | 16.5 |  |
| 12 | `s0170` | W 333 AV & N 70 ST | 17.5 |  |
| 13 | `s0172` | W 333 AV & N 108 ST | 19.2 |  |
| 14 | `s0174` | W 333 AV & N 1327 BLOCK | 20.2 |  |
| 15 | `s0176` | W 333 AV & N 1697 BLOCK | 21.8 | yes |
| 16 | `s0178` | N 200 ST & W 311 AV | 24.0 |  |
| 17 | `s0179` | N 200 ST & W 288 AV | 24.9 |  |
| 18 | `s0180` | N 200 ST & W 260 AV | 26.1 |  |
| 19 | `s0181` | N 200 ST & W 236 AV | 27.1 |  |
| 20 | `s0616` | NW POINT [2] | 30.0 | yes |

**Inbound stops (21):**

| # | stopCode | stopName | minutesFromStart | isTimepoint |
|---|---|---|---|---|
| 0 | `s0616` | NW POINT [2] | 0.0 | yes |
| 1 | `s0181` | N 200 ST & W 236 AV | 0.7 |  |
| 2 | `s0180` | N 200 ST & W 260 AV | 1.9 |  |
| 3 | `s0179` | N 200 ST & W 288 AV | 3.3 |  |
| 4 | `s0178` | N 200 ST & W 311 AV | 4.4 |  |
| 5 | `s0176` | W 333 AV & N 1697 BLOCK | 7.0 | yes |
| 6 | `s0174` | W 333 AV & N 1327 BLOCK | 8.8 |  |
| 7 | `s0172` | W 333 AV & N 108 ST | 10.0 |  |
| 8 | `s0170` | W 333 AV & N 70 ST | 11.8 |  |
| 9 | `s0168` | W 333 AV & N 44 ST | 13.1 |  |
| 10 | `s0628` | W 333 AV & N 19 ST | 14.3 | yes |
| 11 | `s0627` | W 333 AV & N 2 ST | 15.1 |  |
| 12 | `s0101` | S 11 ST & W 307 AV | 17.0 |  |
| 13 | `s0590` | W 297 AV & S 829 BLOCK | 21.0 |  |
| 14 | `s0588` | W 297 AV & S 1087 BLOCK | 22.2 |  |
| 15 | `s0586` | W 297 AV & S 1345 BLOCK | 23.5 | yes |
| 16 | `s0584` | W 297 AV & S 1603 BLOCK | 24.7 |  |
| 17 | `s0582` | W 297 AV & S 1860 BLOCK | 26.0 |  |
| 18 | `s0580` | W 297 AV & S 2118 BLOCK | 27.2 |  |
| 19 | `s0578` | W 297 AV & S 2376 BLOCK | 28.5 |  |
| 20 | `s0619` | UNIVERSITY PARK [2] | 30.0 | yes |

Note the stops 29 **no longer serves** because it left Parkview and the -70th St corridor: `s0405`, `s0069`*, `s0093`, `s0094`, `s0095`, `s0096`*, `s0097`* (the starred ones are still served by other routes; `s0093`, `s0094`, `s0095` become unserved).


### 5E. Route 240: convert to Express-Max (route stays the same)

240 keeps its Parkview Garage to Berrelingway North path (see Route 240's Parkview and Berrelingway edits in Phase 4, **do those first**). Then change only:

| field | value |
|---|---|
| articulatedPolicy | `Mandatory` (it already is) |
| miniBusPolicy | `Prohibited` |
| allowedFleetSeries | **replace** the current list (1600, 2200, 2400, 2700) with just `2900` |
| routeQualifierOutbound / Inbound | `MAX` / `MAX` |
| maxBusesAllowed | `6` |
| scheduleWindows | Peak headway 10–15 min; your choice. (One-way trip is about 19 min, so a 10-min peak needs about 6 buses.) |
| nightFleetSeries | leave empty if it does not run 0:00–4:00 |

If the asset was disabled ('revived'), also check it is in **both** route lists (Phase 6).


## Phase 6: Registration lists, depots and fleet limits

- [ ] **Give the 10 normal artics (block 2800) to their routes.** In each of these assets add `2800` to `allowedFleetSeries` (keep the rest): **136** (done in 5A), **34, 73, 1, 201, 45, 199, 109, 10**. Pick other routes if you like: only routes whose `articulatedPolicy` is Allowed (1) or Preferred (2) can use artics (21 and 85 are Prohibited).
- [ ] **CityManager > Routes** (`routes`): add `236.asset` at the end. (Check `240.asset` is in the list.)
- [ ] **BusScheduler > managedRoutes**: add `236.asset` at the end. **Do not add a route twice**: the game de-duplicates, but keep the list clean.
- [ ] **Depots**: open every depot asset that will hold Express-Max buses (the depots you allocated the 25 buses to). In `servedRouteNumbers` add `236` and `240`. Also add `136` to any depot holding the normal artics.
- [ ] **Route 29** was served by some depots with `29` in `servedRouteNumbers`: that stays. Nothing to change.

**STOP: save and test.** Press Play. Open the route list in the main menu. Check 236 appears, shows `MAX`, and lists 6–7 buses at the 20-minute peak. Check 29 shows University Park to NW Point.


## Phase 7: Terminal-by-terminal checklist

Everything a terminal needs, gathered so you can finish one terminal at a time and test it. For each: roads (Phase 2), stops (Phase 3), routes (Phase 4).


### PARKVIEW

*29 left; Parkview A stops s0404/s0405 retired.*

- [ ] **Roads:** `S117STX` -117th St, `S125STX` -125th St, `MIDWAYX` Midway.
- [ ] **Stops:** `s0596` PARKVIEW PARKING GARAGE [D] (new), `s0034` PARKVIEW LINK [E] (move), `s0406` PARKVIEW B [D] (move), `s0407` PARKVIEW B [P] (move).
- [ ] **Routes to edit:** [1](#route-1), [201](#route-201), [73](#route-73), [87](#route-87), [140](#route-140), [240](#route-240).
- [ ] **Decor (optional, from the PDF):** canopies at each bay, shelters on `hasShelter` stops, benches, trees on the islands, route-map boards.


### MERIDIAN SQUARE (main)

*arc moved west 400 m; rows 303rd/306th/309th.*

- [ ] **Roads:** `N309STX` 309th St.
- [ ] **Stops:** `s0135` MERIDIAN SQUARE [PICKUP] (move), `s0245` MERIDIAN SQUARE A2 [PICKUP] (move), `s0131` MERIDIAN SQUARE [DROPOFF] (move), `s0244` MERIDIAN SQUARE A2 [DROPOFF] (move), `s0597` LEAF BLVD & W 20 AV (new), `s0598` LEAF BLVD & W 20 AV (new), `s0599` MERIDIAN SQUARE C [LAYOVER] (new).
- [ ] **Routes to edit:** [1](#route-1), [201](#route-201), [87](#route-87), [101](#route-101), [199](#route-199), [299](#route-299), [116](#route-116), [216](#route-216), [34](#route-34).
- [ ] **Decor (optional, from the PDF):** canopies at each bay, shelters on `hasShelter` stops, benches, trees on the islands, route-map boards.


### MERIDIAN SQUARE B

*rectangular lot, one counter-clockwise lap.*

- [ ] **Roads:** `N350STX` 350th St, `W54AVEX` -54th Av, `N390STX` 390th St, `W62AVEX` -62nd Av.
- [ ] **Stops:** `s0478` MERIDIAN SQUARE B [D] (move), `s0479` MERIDIAN SQUARE B [P] (move), `s0600` MERIDIAN SQUARE B [P2] (new).
- [ ] **Routes to edit:** [85](#route-85).
- [ ] **Decor (optional, from the PDF):** canopies at each bay, shelters on `hasShelter` stops, benches, trees on the islands, route-map boards.


### BERRELINGWAY NORTH

*only 114, 140 and 240 change.*

- [ ] **Roads:** `E56AVEX` 56th Av, `N279STX` 279th St.
- [ ] **Stops:** `s0089` BERRELINGWAY NORTH STATION (move), `s0601` BERRELINGWAY NORTH [D] (new), `s0602` BERRELINGWAY NORTH STATION [2] (new).
- [ ] **Routes to edit:** [114](#route-114), [116](#route-116), [216](#route-216), [140](#route-140), [240](#route-240).
- [ ] **Decor (optional, from the PDF):** canopies at each bay, shelters on `hasShelter` stops, benches, trees on the islands, route-map boards.


### SOUTH PIER

*-193rd St platform road, separate bays per route.*

- [ ] **Roads:** `S193STX` -193rd St.
- [ ] **Stops:** `s0603` SOUTH PIER [D1] (new), `s0246` SOUTH PIER [D] (move), `s0604` SOUTH PIER [D3] (new), `s0247` SOUTH PIER [P] (move), `s0605` SOUTH PIER [P2] (new), `s0606` SOUTH PIER [P3] (new), `s0607` SOUTH PIER [45 D] (new), `s0608` SOUTH PIER [45 2] (new), `s0609` SOUTH PIER [45 P] (new).
- [ ] **Routes to edit:** [21](#route-21), [45](#route-45), [199](#route-199), [299](#route-299).
- [ ] **Decor (optional, from the PDF):** canopies at each bay, shelters on `hasShelter` stops, benches, trees on the islands, route-map boards.


### NORTH BEACH

*Beach Mall westbound; 34 reversed; 114 U-turn removed.*

- [ ] **Roads:** `E280AVEX` 280th Av, `N320STX` 320th St.
- [ ] **Stops:** `s0512` NORTH BEACH [P] (move), `s0511` NORTH BEACH [D] (move), `s0610` NORTH BEACH [3] (new), `s0611` NORTH BEACH [4] (new), `s0612` NORTH BEACH [295 D] (new).
- [ ] **Routes to edit:** [34](#route-34), [45](#route-45), [114](#route-114).
- [ ] **Decor (optional, from the PDF):** canopies at each bay, shelters on `hasShelter` stops, benches, trees on the islands, route-map boards.


### CRESTBURY

*one shared Mall; 136 now starts here.*

- [ ] **Roads:** `W348AVEX` -348th Av, `S65STX` -65th St.
- [ ] **Stops:** `s0092` CRESTBURY TERMINAL (move), `s0099` CRESTBURY TERMINAL [DROPOFF] (move), `s0613` CRESTBURY [3] (new), `s0614` CRESTBURY [4] (new), `s0615` S 61 ST & W 364 AV (new).
- [ ] **Routes to edit:** [21](#route-21), [116](#route-116), [216](#route-216), [136](#route-136).
- [ ] **Decor (optional, from the PDF):** canopies at each bay, shelters on `hasShelter` stops, benches, trees on the islands, route-map boards.


### NW POINT

*no new road; -216th Av is the east side.*

- [ ] **Roads:** no new road.
- [ ] **Stops:** `s0184` NW POINT (move), `s0616` NW POINT [2] (new), `s0617` NW POINT [3] (new).
- [ ] **Routes to edit:** [25](#route-25), [29](#route-29).
- [ ] **Decor (optional, from the PDF):** canopies at each bay, shelters on `hasShelter` stops, benches, trees on the islands, route-map boards.


### SOUTHSIDE

*P moved back off the junction.*

- [ ] **Roads:** `E83AVEX` 83rd Av.
- [ ] **Stops:** `s0292` SOUTHSIDE [P] (move), `s0618` SOUTHSIDE [2] (new), `s0291` SOUTHSIDE [D] (move).
- [ ] **Routes to edit:** [7](#route-7), [10](#route-10).
- [ ] **Decor (optional, from the PDF):** canopies at each bay, shelters on `hasShelter` stops, benches, trees on the islands, route-map boards.


### UNIVERSITY PARK

*Art Blvd curve gone; hub Campus Drive.*

- [ ] **Roads:** `W297AVEX` -297th Av, `S250STX` -250th St, `W277AVEX` -277th Av, `S260STX` -260th St.
- [ ] **Stops:** `s0332` UNIVERSITY PARK [1] (move), `s0619` UNIVERSITY PARK [2] (new), `s0620` UNIVERSITY PARK [3] (new), `s0621` UNIVERSITY PARK [4] (new).
- [ ] **Routes to edit:** [7](#route-7), [29](#route-29), [236](#route-236).
- [ ] **Decor (optional, from the PDF):** canopies at each bay, shelters on `hasShelter` stops, benches, trees on the islands, route-map boards.


### 0TH AV / 200TH ST

*203rd St widened to 7 m; 109 pickup west of -10th Av.*

- [ ] **Roads:** no new road.
- [ ] **Stops:** `s0374` 0TH AV / 200TH ST [D] (move), `s0375` 0TH AV / 200TH ST [P] (move), `s0622` 0TH AV / 200TH ST [3] (new), `s0623` 0TH AV / 200TH ST [4] (new).
- [ ] **Routes to edit:** [10](#route-10), [109](#route-109).
- [ ] **Decor (optional, from the PDF):** canopies at each bay, shelters on `hasShelter` stops, benches, trees on the islands, route-map boards.


### VALLEY FIELDS

*stops 50 m closer together.*

- [ ] **Roads:** no new road.
- [ ] **Stops:** `s0476` VALLEY FIELDS [D] (move), `s0624` VALLEY FIELDS [2] (new), `s0477` VALLEY FIELDS [P] (move).
- [ ] **Routes to edit:** [73](#route-73), [85](#route-85).
- [ ] **Decor (optional, from the PDF):** canopies at each bay, shelters on `hasShelter` stops, benches, trees on the islands, route-map boards.


### SUNSET POINT

*no new road; loop already exists.*

- [ ] **Roads:** no new road.
- [ ] **Stops:** `s0564` SUNSET POINT [P] (move), `s0625` SUNSET POINT [2] (new), `s0626` SUNSET POINT [3] (new), `s0563` SUNSET POINT [D] (move).
- [ ] **Routes to edit:** [136](#route-136), [236](#route-236), [45](#route-45).
- [ ] **Decor (optional, from the PDF):** canopies at each bay, shelters on `hasShelter` stops, benches, trees on the islands, route-map boards.


### 36TH ST EAST EXTENSION

*1 km new road + six new stops.*

- [ ] **Roads:** `N36STX` 36th St.
- [ ] **Stops:** `s0629` N 36 ST & E 125 AV (new), `s0630` N 36 ST & E 175 AV (new), `s0631` N 36 ST & E 199 AV (new), `s0632` N 36 ST & E 225 AV (new), `s0633` N 36 ST & E 275 AV (new), `s0634` N 36 ST & E 299 AV (new), `s0627` W 333 AV & N 2 ST (new), `s0628` W 333 AV & N 19 ST (new).
- [ ] **Routes to edit:** [136](#route-136), [236](#route-236).
- [ ] **Decor (optional, from the PDF):** canopies at each bay, shelters on `hasShelter` stops, benches, trees on the islands, route-map boards.


## Phase 8: Testing in Play Mode

Do each test with the sim clock set to a time the route runs.

| Test | Expected |
|---|---|
| Fly to each new road | Surface looks right; junctions form; no road ends in the grass. |
| Parkview: watch a 140 and 240 outbound | They drive along the new -117th St to Midway; **no grass**. |
| Parkview: watch a 1 and a 201 leave Commons | South, east along 125th, north on Midway. **No lap round the block.** |
| Meridian: watch a 199 and a 1 | They turn into their own row, stop at the drop-off, reach the moved pickup, leave down the arc. |
| North Beach: watch a 114 | **No U-turn.** It goes up 295th Av, west along the Mall, up 280th Av. |
| North Beach: watch a 34 | East on 340th, south on 295th, west along the Mall (same kerb as 45), north on 280th Av. |
| University Park: watch a 29 | Comes down -297th Av, **turns onto Campus Drive legally**, leaves by -277th Av and -270th St. |
| Route 136 / 136N | 136 all day from 4:00. At 00:30 only **136N** runs (every 60 min, thinned stops). |
| Route 236 | Orange, `MAX`, limited stops, only buses from the 2900 Series (Artic). |
| Tracker / arrivals | Open a new stop and check the arrivals list shows the right routes (the stop index rebuilds when you edit an asset). |


## Appendix A: how tValue is worked out (so you can fix one by hand)

Pick the road. Take the point on the road centre line closest to where you want the stop. tValue = (distance along the road to that point) / (total road length). For a straight 2-point road that is just `(pointX - startX) / (endX - startX)` for an east–west road, or the same with Z for a north–south road. Example: `-117th St` runs from X=-380 to X=0. A stop at X=-290 has tValue = (-290 - -380) / 380 = **0.2368**.

If the stop appears on the **wrong side** of the road, the road's point order is the other way round for your direction of travel. New roads were ordered so stops land on the correct kerb. For old roads you cannot change, accept the side or add a twin stop on the other side.


## Appendix B: master list of every stop that changes

| code | kind | name | road | tValue | plan position (X, Y, Z) |
|---|---|---|---|---|---|
| `s0034` | MOVE | PARKVIEW LINK [E] | `S117STX` | 0.2367 | (-290, 0, -1165) |
| `s0089` | MOVE | BERRELINGWAY NORTH STATION | `BRWYX` | 0.5 | (480, 0, 2906) |
| `s0092` | MOVE | CRESTBURY TERMINAL | `S65STX` | 0.3633 | (-3560, 0, -651) |
| `s0099` | MOVE | CRESTBURY TERMINAL [DROPOFF] | `S65STX` | 0.6367 | (-3620, 0, -651) |
| `s0131` | MOVE | MERIDIAN SQUARE [DROPOFF] | `N303ST` | 0.0547 | (-30, 0, 3032) |
| `s0135` | MOVE | MERIDIAN SQUARE [PICKUP] | `N303ST` | 0.89 | (-486, 0, 3032) |
| `s0184` | MOVE | NW POINT | `W230AVE` | 0.682 | (-2306, 0, 2150) |
| `s0244` | MOVE | MERIDIAN SQUARE A2 [DROPOFF] | `N306ST` | 0.056 | (-30, 0, 3062) |
| `s0245` | MOVE | MERIDIAN SQUARE A2 [PICKUP] | `N306ST` | 0.888 | (-477, 0, 3062) |
| `s0246` | MOVE | SOUTH PIER [D] | `R068` | 0.5173 | (1850, 0, -1907) |
| `s0247` | MOVE | SOUTH PIER [P] | `S193STX` | 0.3107 | (1790, 0, -1942) |
| `s0291` | MOVE | SOUTHSIDE [D] | `E93AVE` | 0.1907 | (930, 0, -2540) |
| `s0292` | MOVE | SOUTHSIDE [P] | `E93AVE` | 0.6667 | (930, 0, -2640) |
| `s0332` | MOVE | UNIVERSITY PARK [1] | `S260STX` | 0.2 | (-2930, 0, -2613) |
| `s0374` | MOVE | 0TH AV / 200TH ST [D] | `N203ST` | 0.328 | (-30, 0, 2036) |
| `s0375` | MOVE | 0TH AV / 200TH ST [P] | `N203ST` | 0.3667 | (-80, 0, 2036) |
| `s0406` | MOVE | PARKVIEW B [D] | `S105ST` | 0.108 | (-790, 0, -1050) |
| `s0407` | MOVE | PARKVIEW B [P] | `S105ST` | 0.0453 | (-860, 0, -1050) |
| `s0476` | MOVE | VALLEY FIELDS [D] | `W750AV` | 0.82 | (-7494, 0, -1230) |
| `s0477` | MOVE | VALLEY FIELDS [P] | `W750AV` | 0.7533 | (-7494, 0, -1130) |
| `s0478` | MOVE | MERIDIAN SQUARE B [D] | `W54AVEX` | 0.4 | (-532, 0, 3600) |
| `s0479` | MOVE | MERIDIAN SQUARE B [P] | `W54AVEX` | 0.8 | (-532, 0, 3800) |
| `s0511` | MOVE | NORTH BEACH [D] | `N320STX` | 0.3 | (2905, 0, 3213) |
| `s0512` | MOVE | NORTH BEACH [P] | `N320STX` | 0.5667 | (2865, 0, 3213) |
| `s0563` | MOVE | SUNSET POINT [D] | `S200ST2` | 0.27 | (3369, 0, -1991) |
| `s0564` | MOVE | SUNSET POINT [P] | `S200ST2` | 0.7213 | (3150, 0, -1991) |
| `s0596` | NEW | PARKVIEW PARKING GARAGE [D] | `S125ST` | 0.8593 | (-300, 0, -1250) |
| `s0597` | NEW | LEAF BLVD & W 20 AV | `LFBD` | 0.3807 | (-200, 0, 3008) |
| `s0598` | NEW | LEAF BLVD & W 20 AV | `LFBD` | 0.3807 | (-200, 0, 2992) |
| `s0599` | NEW | MERIDIAN SQUARE C [LAYOVER] | `N309STX` | 0.48 | (-250, 0, 3092) |
| `s0600` | NEW | MERIDIAN SQUARE B [P2] | `W54AVEX` | 0.68 | (-532, 0, 3740) |
| `s0601` | NEW | BERRELINGWAY NORTH [D] | `E40AVE` | 0.9907 | (400, 0, 2960) |
| `s0602` | NEW | BERRELINGWAY NORTH STATION [2] | `BRWYX` | 0.094 | (545, 0, 2906) |
| `s0603` | NEW | SOUTH PIER [D1] | `R068` | 0.7933 | (1930, 0, -1907) |
| `s0604` | NEW | SOUTH PIER [D3] | `R068` | 0.3107 | (1790, 0, -1907) |
| `s0605` | NEW | SOUTH PIER [P2] | `S193STX` | 0.552 | (1860, 0, -1942) |
| `s0606` | NEW | SOUTH PIER [P3] | `S193STX` | 0.7933 | (1930, 0, -1942) |
| `s0607` | NEW | SOUTH PIER [45 D] | `S200ST` | 0.7 | (1940, 0, -1993) |
| `s0608` | NEW | SOUTH PIER [45 2] | `S200ST` | 0.8 | (1860, 0, -1993) |
| `s0609` | NEW | SOUTH PIER [45 P] | `S200ST` | 0.9 | (1780, 0, -1993) |
| `s0610` | NEW | NORTH BEACH [3] | `N320STX` | 0.8333 | (2825, 0, 3213) |
| `s0611` | NEW | NORTH BEACH [4] | `N320STX` | 0.1 | (2935, 0, 3213) |
| `s0612` | NEW | NORTH BEACH [295 D] | `E295AV` | 0.75 | (2950, 0, 3300) |
| `s0613` | NEW | CRESTBURY [3] | `S65STX` | 0.8633 | (-3670, 0, -651) |
| `s0614` | NEW | CRESTBURY [4] | `W370AVE` | 0.0833 | (-3700, 0, -651) |
| `s0615` | NEW | S 61 ST & W 364 AV | `S61ST` | 0.256 | (-3640, 0, -616) |
| `s0616` | NEW | NW POINT [2] | `W230AVE` | 0.4547 | (-2306, 0, 2100) |
| `s0617` | NEW | NW POINT [3] | `W230AVE` | 0.2273 | (-2306, 0, 2050) |
| `s0618` | NEW | SOUTHSIDE [2] | `E93AVE` | 0.3807 | (930, 0, -2580) |
| `s0619` | NEW | UNIVERSITY PARK [2] | `S260STX` | 0.4 | (-2890, 0, -2613) |
| `s0620` | NEW | UNIVERSITY PARK [3] | `S260STX` | 0.6 | (-2850, 0, -2613) |
| `s0621` | NEW | UNIVERSITY PARK [4] | `S260STX` | 0.8 | (-2810, 0, -2613) |
| `s0622` | NEW | 0TH AV / 200TH ST [3] | `N203ST` | 0.458 | (-200, 0, 2036) |
| `s0623` | NEW | 0TH AV / 200TH ST [4] | `N203ST` | 0.5347 | (-300, 0, 2036) |
| `s0624` | NEW | VALLEY FIELDS [2] | `W750AV` | 0.7867 | (-7494, 0, -1180) |
| `s0625` | NEW | SUNSET POINT [2] | `S200ST2` | 0.5567 | (3230, 0, -1991) |
| `s0626` | NEW | SUNSET POINT [3] | `S200ST2` | 0.392 | (3310, 0, -1991) |
| `s0627` | NEW | W 333 AV & N 2 ST | `W333AVE` | 0.3507 | (-3330, 0, 20) |
| `s0628` | NEW | W 333 AV & N 19 ST | `W333AVE` | 0.4067 | (-3330, 0, 190) |
| `s0629` | NEW | N 36 ST & E 125 AV | `N36ST3` | 0.2527 | (1250, 0, 360) |
| `s0630` | NEW | N 36 ST & E 175 AV | `N36ST3` | 0.7573 | (1750, 0, 360) |
| `s0631` | NEW | N 36 ST & E 199 AV | `N36STX` | 0.02 | (2010, 0, 360) |
| `s0632` | NEW | N 36 ST & E 225 AV | `N36STX` | 0.26 | (2250, 0, 360) |
| `s0633` | NEW | N 36 ST & E 275 AV | `N36STX` | 0.76 | (2750, 0, 360) |
| `s0634` | NEW | N 36 ST & E 299 AV | `N36STX` | 0.98 | (2970, 0, 360) |


## Appendix C: troubleshooting

| Problem | Fix |
|---|---|
| A bus never stops at a moved stop | The arriving trip's last node is before the stop. Move that node forward (rule at the top of Phase 4). |
| A bus skips its first stop | The departing trip's first node is after the stop. Move that node back. |
| Bus clips a kerb at a new corner | Move the corner node 2 m toward the road centre. |
| New road has no junction where it crosses | Open the road, make sure `canHaveIntersection` is ticked, and that the end control point actually reaches the other road's centre line. |
| A stop floats in a field | Wrong `parentRoadCode`. Re-check the road in the table; the road code is case-sensitive. |
| Variant 136N does not show | `overrideRoute` must be ticked, `operatingStartMinutes/EndMinutes` set, and `overrideSchedule` ticked. |
| 236 never gets a bus | Check the depot `servedRouteNumbers` contains `236`, `allowedFleetSeries` contains `2900`, articulatedPolicy is Mandatory, and the 2900 Series (Artic) exists in the roster. |
| Edits vanish after Play | You edited while in Play Mode. Stop Play and redo. |


*End of guide.*