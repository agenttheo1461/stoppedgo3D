#if UNITY_EDITOR
using UnityEditor;
using UnityEngine;

// ═══════════════════════════════════════════════════════════════════════════════
//  PLAY MODE COMPILE GUARD
//
//  When a script is saved while the game is playing, Unity can swap the code in
//  mid-session ("Recompile And Continue Playing"). That wipes every static field and
//  every private field that isn't serialized — which here is nearly everything the
//  game runs on (the scheduler's timetable and bus assignments, the bus manager's
//  records, the Route Manager's locks…). The session then limps on half-broken.
//
//  This sets Unity's "Script Changes While Playing" to "Recompile After Finished
//  Playing": edits wait, the running session stays exactly as it is, and the code
//  recompiles the moment you stop playing (or press Ctrl/Cmd+R to compile on demand).
//
//  It is applied every time the editor loads. Turn it off from the menu below if
//  you'd rather have Unity's own setting back.
// ═══════════════════════════════════════════════════════════════════════════════
[InitializeOnLoad]
public static class PlayModeCompileGuard
{
    private const string UnityPrefKey = "ScriptCompilationDuringPlay"; // 0 = recompile and continue, 1 = after finished playing, 2 = stop playing and recompile
    private const string OurSwitchKey = "Headway.PlayModeCompileGuard.Off";
    private const string MenuPath     = "Tools/Play Mode/Hold recompiles until I stop playing";

    static PlayModeCompileGuard()
    {
        if (EditorPrefs.GetBool(OurSwitchKey, false)) return;
        if (EditorPrefs.GetInt(UnityPrefKey, 0) != 1) EditorPrefs.SetInt(UnityPrefKey, 1);
    }

    [MenuItem(MenuPath)]
    private static void Toggle()
    {
        bool nowOff = !EditorPrefs.GetBool(OurSwitchKey, false);
        EditorPrefs.SetBool(OurSwitchKey, nowOff);
        EditorPrefs.SetInt(UnityPrefKey, nowOff ? 0 : 1);
        Debug.Log(nowOff
            ? "[PlayModeCompileGuard] Off. Unity will recompile while the game is playing (state is lost)."
            : "[PlayModeCompileGuard] On. Code changes wait until you stop playing.");
    }

    [MenuItem(MenuPath, true)]
    private static bool ToggleChecked()
    {
        Menu.SetChecked(MenuPath, !EditorPrefs.GetBool(OurSwitchKey, false));
        return true;
    }
}
#endif
