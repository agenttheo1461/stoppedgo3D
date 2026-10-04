using UnityEngine;

// ═══════════════════════════════════════════════════════════════════════════════
//  POSSESSION VISIBILITY ENFORCER
//
//  A blunt, standalone safety net -- drag this onto any ONE persistent scene
//  GameObject (the same one NetworkGameBridge lives on works fine) and it needs
//  no wiring, no Inspector fields, nothing else. Every frame, for every busID
//  NetworkGameBridge says is currently possessed (local or remote), it force-
//  enables that bus's renderers/audio/colliders DIRECTLY on the real Unity
//  components -- it does not go through NPCBusController's own cull state
//  (_renderersEnabled/_audioCulled) at all, so it can't be defeated by any
//  stale-cache bug in that system the way the actual bug this session was
//  (SetDepotComponentsActive desyncing _renderersEnabled from the real
//  Renderer.enabled values -- see NPCBusController.cs's own fix comment).
//  This exists as insurance ON TOP of that root-cause fix, not instead of it --
//  if the root-cause fix is complete, this simply does nothing every frame
//  (Renderer.enabled = true when it's already true is a no-op). If anything
//  else ever manages to hide a possessed bus again, this drags it back within
//  one frame regardless of which system caused it.
// ═══════════════════════════════════════════════════════════════════════════════
public class PossessionVisibilityEnforcer : MonoBehaviour
{
    private void Update()
    {
        if (NetworkGameBridge.Instance == null || BusManager.Instance == null) return;

        foreach (var busID in NetworkGameBridge.Instance.GetPossessedBusIDs())
        {
            var rec = BusManager.Instance.GetRecord(busID);
            if (rec?.controller == null) continue;

            var go = rec.controller.gameObject;
            if (!go.activeSelf) go.SetActive(true);

            // Articulated buses' rear section (trailerPivot) is a SEPARATE GameObject that can be
            // deactivated outright (SetActive(false), not just Renderer.enabled) -- a Renderer
            // under an inactive parent stays invisible even with its own .enabled = true, so this
            // needs its own explicit check; the loop below alone doesn't cover it.
            if (rec.controller.trailerPivot != null && !rec.controller.trailerPivot.gameObject.activeSelf)
                rec.controller.trailerPivot.gameObject.SetActive(true);

            var renderers = go.GetComponentsInChildren<Renderer>(true);
            for (int i = 0; i < renderers.Length; i++)
                if (renderers[i] != null && !renderers[i].enabled) renderers[i].enabled = true;

            var audioSources = go.GetComponentsInChildren<AudioSource>(true);
            for (int i = 0; i < audioSources.Length; i++)
                if (audioSources[i] != null && !audioSources[i].enabled) audioSources[i].enabled = true;

            var colliders = go.GetComponentsInChildren<Collider>(true);
            for (int i = 0; i < colliders.Length; i++)
                if (colliders[i] != null && !colliders[i].enabled) colliders[i].enabled = true;
        }
    }
}
