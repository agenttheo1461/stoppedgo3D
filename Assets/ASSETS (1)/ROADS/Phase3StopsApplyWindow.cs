using System.Collections.Generic;
using System.Linq;
using UnityEngine;
#if UNITY_EDITOR
using UnityEditor;
using UnityEditor.SceneManagement;

// ═══════════════════════════════════════════════════════════════════════════════
//  NETWORK REDESIGN -- PHASE 3 (STOPS)  (Tools > City Building > Network Redesign)
//
//  Applies every stop edit in Phase 3 of the manual build guide straight to
//  CityManager.stopDefinitions, through normal C#/Undo -- not a hand-edit of
//  the scene YAML. stopDefinitions is a plain serialized List<BusStopData> on
//  a MonoBehaviour; mutating it through code and Undo.RecordObject + SetDirty
//  is exactly what the Inspector itself does one field at a time, just all 90
//  edits in one click instead of hundreds of typed fields.
//
//  Three groups, applied in this order (matches the guide's own order):
//   1. 3A -- 39 NEW stops (s0596-s0634), appended at the end in order.
//   2. 3B -- 26 MOVED stops, found by existing stopCode, parentRoadCode/
//      tValue overwritten (and name too, where the guide gives a new one).
//   3. 3C -- 24 RETIRED stops, found by stopCode, set to the guide's exact
//      "placeholder" shape (name, road cleared, tValue 0, shelter/terminal
//      off) rather than deleted -- deleting would shift every later scene
//      override by list position, which the guide explicitly warns against.
//
//  Idempotent: a NEW stop already present by code is skipped (not
//  duplicated) if you click Apply twice; a MOVE/RETIRE just reapplies the
//  same target values either way. A stopCode that doesn't exist yet for a
//  MOVE/RETIRE is logged, never silently skipped.
//
//  Does NOT touch roads (Phase 2) or routes (Phase 4+) -- stops only, as
//  asked. Run Phase 2 first if you haven't: stops on an unresolved
//  parentRoadCode just won't have anywhere to resolve a position from yet.
// ═══════════════════════════════════════════════════════════════════════════════
public class Phase3StopsApplyWindow : EditorWindow
{
    [MenuItem("Tools/City Building/Network Redesign/Apply Phase 3 (Stops)")]
    public static void ShowWindow()
    {
        var win = GetWindow<Phase3StopsApplyWindow>("Phase 3: Stops");
        win.Show();
    }

    private CityManager _city;
    private Vector2 _scroll;
    private readonly List<string> _log = new();

    private void OnEnable()
    {
        if (_city == null) _city = FindFirstObjectByType<CityManager>();
    }

    private void OnGUI()
    {
        _city = (CityManager)EditorGUILayout.ObjectField("City Manager", _city, typeof(CityManager), true);
        EditorGUILayout.HelpBox(
            "Applies every Phase 3 stop edit from the Network Redesign Plan: 39 new stops, " +
            "26 moved stops, 24 retired stops. Safe to click more than once -- a new stop " +
            "already present by code is skipped rather than duplicated.",
            MessageType.Info);

        GUI.enabled = _city != null;
        if (GUILayout.Button("Apply Phase 3 (Stops)", GUILayout.Height(32)))
            Apply();
        GUI.enabled = true;

        if (_log.Count > 0)
        {
            EditorGUILayout.Space(8);
            _scroll = EditorGUILayout.BeginScrollView(_scroll, GUILayout.Height(380));
            foreach (var line in _log)
                EditorGUILayout.LabelField(line, EditorStyles.wordWrappedLabel);
            EditorGUILayout.EndScrollView();
        }
    }

    private void Apply()
    {
        _log.Clear();
        if (_city == null) { _log.Add("No CityManager assigned."); return; }

        Undo.RecordObject(_city, "Apply Phase 3 Stops");

        var byCode = _city.stopDefinitions
            .Where(s => s != null && !string.IsNullOrEmpty(s.stopCode))
            .ToDictionary(s => s.stopCode, s => s);

        int added = 0, moved = 0, retired = 0, skippedExisting = 0, missing = 0;

        // ── 3A: new stops ──
        foreach (var n in NewStops)
        {
            if (byCode.ContainsKey(n.code))
            {
                _log.Add($"NEW {n.code}: already exists -- skipped.");
                skippedExisting++;
                continue;
            }
            var stop = new BusStopData
            {
                stopCode       = n.code,
                stopName       = n.name,
                parentRoadCode = n.road,
                tValue         = n.t,
                isTerminal     = n.isTerminal,
                isLayover      = n.isLayover,
                hasShelter     = n.hasShelter,
                isAccessible   = true,
            };
            _city.stopDefinitions.Add(stop);
            byCode[n.code] = stop;
            _log.Add($"NEW {n.code}: added \"{n.name}\" on {n.road} @ {n.t:0.####}.");
            added++;
        }

        if (byCode.TryGetValue("s0597", out var leaf1) && byCode.TryGetValue("s0598", out var leaf2)
            && leaf1.parentRoadCode == leaf2.parentRoadCode && Mathf.Approximately(leaf1.tValue, leaf2.tValue))
        {
            _log.Add("NOTE: s0597 and s0598 share the exact same road+tValue, so GetWorldPosition() places " +
                     "them at the identical spot. Tools/guide_data.py's own stop registry computes s0597 as " +
                     "the LEFT side of Leaf Blvd and s0598 as the RIGHT side (eastbound vs westbound) -- that " +
                     "distinction never reaches the printed guide's numbers because BusStopData has no side/" +
                     "direction field to hold it. Nudge one stop's tValue by a hair if you want them visibly apart.");
        }

        // ── 3B: moved stops ──
        foreach (var m in MovedStops)
        {
            if (!byCode.TryGetValue(m.code, out var stop))
            {
                _log.Add($"MOVE {m.code}: NOT FOUND -- check the code.");
                missing++;
                continue;
            }
            if (m.newName != null) stop.stopName = m.newName;
            stop.parentRoadCode = m.road;
            stop.tValue = m.t;
            _log.Add($"MOVE {m.code}: now on {m.road} @ {m.t:0.####}" +
                     (m.newName != null ? $", renamed to \"{m.newName}\"." : "."));
            moved++;
        }

        // ── 3C: retired stops ──
        foreach (var code in RetiredStops)
        {
            if (!byCode.TryGetValue(code, out var stop))
            {
                _log.Add($"RETIRE {code}: NOT FOUND -- check the code.");
                missing++;
                continue;
            }
            stop.stopName       = "placeholder";
            stop.parentRoadCode = "";
            stop.tValue          = 0f;
            stop.hasShelter      = false;
            stop.isTerminal      = false;
            _log.Add($"RETIRE {code}: cleared.");
            retired++;
        }

        EditorUtility.SetDirty(_city);
        EditorSceneManager.MarkSceneDirty(_city.gameObject.scene);

        _log.Insert(0,
            $"Done: {added} added, {moved} moved, {retired} retired, {skippedExisting} already-existing " +
            $"skipped, {missing} codes not found. stopDefinitions now has {_city.stopDefinitions.Count} stops. " +
            "Save the scene (Ctrl/Cmd+S) once this looks right.");
    }

    private struct NewStopDef
    {
        public string code, name, road;
        public float  t;
        public bool   isTerminal, isLayover, hasShelter;
    }

    private struct MoveStopDef
    {
        public string code, newName, road;
        public float  t;
    }

    // ── 3A: 39 new stops, in the order they go into stopDefinitions ──
    private static readonly NewStopDef[] NewStops =
    {
        new NewStopDef { code = "s0596", name = "PARKVIEW PARKING GARAGE [D]",       road = "S125ST",  t = 0.8593f, isTerminal = true,  isLayover = false, hasShelter = true },
        new NewStopDef { code = "s0597", name = "LEAF BLVD & W 20 AV",               road = "LFBD",    t = 0.3807f, isTerminal = false, isLayover = false, hasShelter = true },
        new NewStopDef { code = "s0598", name = "LEAF BLVD & W 20 AV",               road = "LFBD",    t = 0.3807f, isTerminal = false, isLayover = false, hasShelter = true },
        new NewStopDef { code = "s0599", name = "MERIDIAN SQUARE C [LAYOVER]",       road = "N309STX", t = 0.48f,   isTerminal = false, isLayover = true,  hasShelter = true },
        new NewStopDef { code = "s0600", name = "MERIDIAN SQUARE B [P2]",            road = "W54AVEX", t = 0.68f,   isTerminal = true,  isLayover = true,  hasShelter = true },
        new NewStopDef { code = "s0601", name = "BERRELINGWAY NORTH [D]",            road = "E40AVE",  t = 0.9907f, isTerminal = true,  isLayover = false, hasShelter = true },
        new NewStopDef { code = "s0602", name = "BERRELINGWAY NORTH STATION [2]",    road = "BRWYX",   t = 0.094f,  isTerminal = true,  isLayover = false, hasShelter = true },
        new NewStopDef { code = "s0603", name = "SOUTH PIER [D1]",                   road = "R068",    t = 0.7933f, isTerminal = true,  isLayover = false, hasShelter = true },
        new NewStopDef { code = "s0604", name = "SOUTH PIER [D3]",                   road = "R068",    t = 0.3107f, isTerminal = true,  isLayover = false, hasShelter = true },
        new NewStopDef { code = "s0605", name = "SOUTH PIER [P2]",                   road = "S193STX", t = 0.552f,  isTerminal = true,  isLayover = false, hasShelter = true },
        new NewStopDef { code = "s0606", name = "SOUTH PIER [P3]",                   road = "S193STX", t = 0.7933f, isTerminal = true,  isLayover = false, hasShelter = true },
        new NewStopDef { code = "s0607", name = "SOUTH PIER [45 D]",                 road = "S200ST",  t = 0.7f,    isTerminal = true,  isLayover = false, hasShelter = true },
        new NewStopDef { code = "s0608", name = "SOUTH PIER [45 2]",                 road = "S200ST",  t = 0.8f,    isTerminal = true,  isLayover = false, hasShelter = true },
        new NewStopDef { code = "s0609", name = "SOUTH PIER [45 P]",                 road = "S200ST",  t = 0.9f,    isTerminal = true,  isLayover = false, hasShelter = true },
        new NewStopDef { code = "s0610", name = "NORTH BEACH [3]",                   road = "N320STX", t = 0.8333f, isTerminal = true,  isLayover = true,  hasShelter = true },
        new NewStopDef { code = "s0611", name = "NORTH BEACH [4]",                   road = "N320STX", t = 0.1f,    isTerminal = true,  isLayover = true,  hasShelter = true },
        new NewStopDef { code = "s0612", name = "NORTH BEACH [295 D]",               road = "E295AV",  t = 0.75f,   isTerminal = true,  isLayover = false, hasShelter = true },
        new NewStopDef { code = "s0613", name = "CRESTBURY [3]",                     road = "S65STX",  t = 0.8633f, isTerminal = true,  isLayover = false, hasShelter = true },
        new NewStopDef { code = "s0614", name = "CRESTBURY [4]",                     road = "W370AVE", t = 0.0833f, isTerminal = true,  isLayover = false, hasShelter = true },
        new NewStopDef { code = "s0615", name = "S 61 ST & W 364 AV",                road = "S61ST",   t = 0.256f,  isTerminal = false, isLayover = false, hasShelter = true },
        new NewStopDef { code = "s0616", name = "NW POINT [2]",                      road = "W230AVE", t = 0.4547f, isTerminal = true,  isLayover = false, hasShelter = true },
        new NewStopDef { code = "s0617", name = "NW POINT [3]",                      road = "W230AVE", t = 0.2273f, isTerminal = true,  isLayover = true,  hasShelter = true },
        new NewStopDef { code = "s0618", name = "SOUTHSIDE [2]",                     road = "E93AVE",  t = 0.3807f, isTerminal = true,  isLayover = false, hasShelter = true },
        new NewStopDef { code = "s0619", name = "UNIVERSITY PARK [2]",               road = "S260STX", t = 0.4f,    isTerminal = true,  isLayover = false, hasShelter = true },
        new NewStopDef { code = "s0620", name = "UNIVERSITY PARK [3]",               road = "S260STX", t = 0.6f,    isTerminal = true,  isLayover = true,  hasShelter = true },
        new NewStopDef { code = "s0621", name = "UNIVERSITY PARK [4]",               road = "S260STX", t = 0.8f,    isTerminal = true,  isLayover = false, hasShelter = true },
        new NewStopDef { code = "s0622", name = "0TH AV / 200TH ST [3]",             road = "N203ST",  t = 0.458f,  isTerminal = true,  isLayover = false, hasShelter = true },
        new NewStopDef { code = "s0623", name = "0TH AV / 200TH ST [4]",             road = "N203ST",  t = 0.5347f, isTerminal = true,  isLayover = true,  hasShelter = true },
        new NewStopDef { code = "s0624", name = "VALLEY FIELDS [2]",                 road = "W750AV",  t = 0.7867f, isTerminal = true,  isLayover = false, hasShelter = true },
        new NewStopDef { code = "s0625", name = "SUNSET POINT [2]",                  road = "S200ST2", t = 0.5567f, isTerminal = true,  isLayover = false, hasShelter = true },
        new NewStopDef { code = "s0626", name = "SUNSET POINT [3]",                  road = "S200ST2", t = 0.392f,  isTerminal = true,  isLayover = false, hasShelter = true },
        new NewStopDef { code = "s0627", name = "W 333 AV & N 2 ST",                 road = "W333AVE", t = 0.3507f, isTerminal = false, isLayover = false, hasShelter = true },
        new NewStopDef { code = "s0628", name = "W 333 AV & N 19 ST",                road = "W333AVE", t = 0.4067f, isTerminal = false, isLayover = false, hasShelter = true },
        new NewStopDef { code = "s0629", name = "N 36 ST & E 125 AV",                road = "N36ST3",  t = 0.2527f, isTerminal = false, isLayover = false, hasShelter = true },
        new NewStopDef { code = "s0630", name = "N 36 ST & E 175 AV",                road = "N36ST3",  t = 0.7573f, isTerminal = false, isLayover = false, hasShelter = true },
        new NewStopDef { code = "s0631", name = "N 36 ST & E 199 AV",                road = "N36STX",  t = 0.02f,   isTerminal = false, isLayover = false, hasShelter = true },
        new NewStopDef { code = "s0632", name = "N 36 ST & E 225 AV",                road = "N36STX",  t = 0.26f,   isTerminal = false, isLayover = false, hasShelter = true },
        new NewStopDef { code = "s0633", name = "N 36 ST & E 275 AV",                road = "N36STX",  t = 0.76f,   isTerminal = false, isLayover = false, hasShelter = true },
        new NewStopDef { code = "s0634", name = "N 36 ST & E 299 AV",                road = "N36STX",  t = 0.98f,   isTerminal = false, isLayover = false, hasShelter = true },
    };

    // ── 3B: 26 moved stops (newName == null means "keep the existing name") ──
    private static readonly MoveStopDef[] MovedStops =
    {
        new MoveStopDef { code = "s0034", newName = "PARKVIEW LINK [E]",     road = "S117STX", t = 0.2367f },
        new MoveStopDef { code = "s0406", newName = null,                    road = "S105ST",  t = 0.108f  },
        new MoveStopDef { code = "s0407", newName = null,                    road = "S105ST",  t = 0.0453f },
        new MoveStopDef { code = "s0135", newName = null,                    road = "N303ST",  t = 0.89f   },
        new MoveStopDef { code = "s0245", newName = null,                    road = "N306ST",  t = 0.888f  },
        new MoveStopDef { code = "s0131", newName = null,                    road = "N303ST",  t = 0.0547f },
        new MoveStopDef { code = "s0244", newName = null,                    road = "N306ST",  t = 0.056f  },
        new MoveStopDef { code = "s0478", newName = null,                    road = "W54AVEX", t = 0.4f    },
        new MoveStopDef { code = "s0479", newName = null,                    road = "W54AVEX", t = 0.8f    },
        new MoveStopDef { code = "s0089", newName = null,                    road = "BRWYX",   t = 0.5f    },
        new MoveStopDef { code = "s0246", newName = null,                    road = "R068",    t = 0.5173f },
        new MoveStopDef { code = "s0247", newName = null,                    road = "S193STX", t = 0.3107f },
        new MoveStopDef { code = "s0512", newName = null,                    road = "N320STX", t = 0.5667f },
        new MoveStopDef { code = "s0511", newName = null,                    road = "N320STX", t = 0.3f    },
        new MoveStopDef { code = "s0092", newName = null,                    road = "S65STX",  t = 0.3633f },
        new MoveStopDef { code = "s0099", newName = null,                    road = "S65STX",  t = 0.6367f },
        new MoveStopDef { code = "s0184", newName = null,                    road = "W230AVE", t = 0.682f  },
        new MoveStopDef { code = "s0292", newName = null,                    road = "E93AVE",  t = 0.6667f },
        new MoveStopDef { code = "s0291", newName = null,                    road = "E93AVE",  t = 0.1907f },
        new MoveStopDef { code = "s0332", newName = "UNIVERSITY PARK [1]",   road = "S260STX", t = 0.2f    },
        new MoveStopDef { code = "s0374", newName = null,                    road = "N203ST",  t = 0.328f  },
        new MoveStopDef { code = "s0375", newName = null,                    road = "N203ST",  t = 0.3667f },
        new MoveStopDef { code = "s0476", newName = null,                    road = "W750AV",  t = 0.82f   },
        new MoveStopDef { code = "s0477", newName = null,                    road = "W750AV",  t = 0.7533f },
        new MoveStopDef { code = "s0564", newName = null,                    road = "S200ST2", t = 0.7213f },
        new MoveStopDef { code = "s0563", newName = null,                    road = "S200ST2", t = 0.27f   },
    };

    // ── 3C: 24 retired stops (kept, never deleted -- see header) ──
    private static readonly string[] RetiredStops =
    {
        // -297th Av, every 2nd stop (Route 29)
        "s0579", "s0581", "s0583", "s0585", "s0587", "s0589",
        // -333rd Av, every 2nd stop (Route 136)
        "s0169", "s0171", "s0173", "s0175", "s0177",
        // BOOLEAN WAY (Route 136)
        "s0166", "s0164", "s0162", "s0160", "s0158", "s0157", "s0156",
        // 36th St, every 2nd stop (Route 136)
        "s0065", "s0025", "s0027", "s0030",
        // Parkview A, dropped because Route 29 no longer goes to Parkview
        "s0404", "s0405",
    };
}
#endif
