using UnityEngine;

public class NPCPerformanceTest : MonoBehaviour
{
    [Header("Debug")]

    private bool npcsEnabled = true;

    void Update()
    {
        if (MainMenu.BlocksInput) return; // [FIX Bug 43] don't allow debug actions behind the main menu
        bool ctrl  = KeyBindings.DebugModifierHeld;
        bool shift = Input.GetKey(KeyCode.LeftShift) || Input.GetKey(KeyCode.RightShift);
        // Gated behind Ctrl+Shift so a stray press can't disable every NPC.
        if (ctrl && shift && Input.GetKeyDown(KeyBindings.Current.debugNpcTest))
        {
            npcsEnabled = !npcsEnabled;

            int count = 0;

            foreach (var npc in FindObjectsByType<NPCBusController>(FindObjectsSortMode.None))
            {
                npc.enabled = npcsEnabled;
                count++;
            }

            Debug.Log(
                $"NPC Controllers {(npcsEnabled ? "ENABLED" : "DISABLED")} | Count: {count}"
            );
        }
    }
}