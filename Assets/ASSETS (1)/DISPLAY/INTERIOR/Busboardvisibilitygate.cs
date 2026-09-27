using UnityEngine;

// ═══════════════════════════════════════════════════════════════════════════════
//  BusBoardVisibilityGate
//
//  Every interior board (ScrollBoard, LCDBoard, DestinationSign,
//  RouteNumberSign) was doing its full Update() data refresh AND full
//  OnGUI() RenderTexture redraw every frame, for every board, on every bus
//  in the fleet -- completely regardless of whether the player is anywhere
//  near that bus or could even see the board at all. At fleet scale (dozens
//  of buses × ~4 boards each, several deadheading buses' worth on a fresh
//  highway corridor) that's a lot of RenderTexture writes, GL matrix work,
//  and GUI.Label draws happening purely off-screen.
//
//  This gate attaches to the board's QUAD (the actual rendered mesh --
//  OnBecameVisible/OnBecameInvisible only fire on the GameObject that
//  actually holds the Renderer, so it can't live on the parent board script
//  directly). It rides Unity's existing per-frame renderer culling, which
//  is already running regardless -- reading IsVisible here costs nothing
//  extra. A secondary distance check against Camera.main is included as a
//  backup for scenes with a wide-view/minimap camera that could otherwise
//  make far-off boards register as "visible" even though nobody's actually
//  close enough to read them.
//
//  USAGE: each board's BuildHousing() creates the quad, then does:
//      _visGate = quad.AddComponent<BusBoardVisibilityGate>();
//      _visGate.maxDistance = 40f; // tune per board size if you want
//  Then in both Update() and OnGUI(), bail early with:
//      if (_visGate != null && !_visGate.ShouldRender) return;
// ═══════════════════════════════════════════════════════════════════════════════
public class BusBoardVisibilityGate : MonoBehaviour
{
    [Tooltip("Distance beyond which the board is treated as not worth rendering even if OnBecameVisible fired (e.g. a wide overview/minimap camera). Set to 0 to disable the distance check and rely on renderer visibility alone.")]
    public float maxDistance = 40f;

    /// <summary>True while this board's quad is actually inside some
    /// camera's frustum and not occluded -- set by Unity itself via the
    /// OnBecameVisible/Invisible callbacks below, not polled.</summary>
    public bool IsRendererVisible { get; private set; } = true; // starts true so a freshly-spawned, immediately-visible bus doesn't sit dark for one frame waiting on the callback

    /// <summary>What board scripts should actually check. Combines renderer
    /// visibility with the distance backstop.</summary>
    public bool ShouldRender
    {
        get
        {
            if (!IsRendererVisible) return false;
            if (maxDistance <= 0f) return true;
            var cam = Camera.main;
            if (cam == null) return true; // no main camera to measure against -- fail open rather than going permanently dark
            float sqrDist = (transform.position - cam.transform.position).sqrMagnitude;
            return sqrDist <= maxDistance * maxDistance;
        }
    }

    private void OnBecameVisible()   => IsRendererVisible = true;
    private void OnBecameInvisible() => IsRendererVisible = false;
}