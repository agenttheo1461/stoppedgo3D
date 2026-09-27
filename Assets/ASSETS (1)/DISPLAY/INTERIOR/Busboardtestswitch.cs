using UnityEngine;

// [CHANGE] MonoBehaviour version -- drag this onto any GameObject in the
// scene (an empty "Debug" or "TestSwitches" object is fine) and tick the
// checkbox in the Inspector. The 5 board scripts still just check
// BusBoardTestSwitch.DisableAllBoards exactly like before -- that's kept as
// a plain static bool so none of their code needs to change, this
// MonoBehaviour just writes into it from the Inspector value.
//
// [DefaultExecutionOrder(-32000)] matters here: Unity doesn't guarantee
// which MonoBehaviour's Awake() runs first by default. Without forcing this
// one to run very early, a bus board could call its own Awake() (and read
// the static DisableAllBoards) BEFORE this component's Awake() ever sets it
// from the Inspector value -- the board would see the stale default false
// and build its RenderTexture anyway, silently defeating the whole test.
// This forces the switch to be live before anything else in the scene runs.
[DefaultExecutionOrder(-32000)]
public class BusBoardTestSwitch : MonoBehaviour
{
    [Tooltip("Isolation-test switch for the 'Textures climbing in the profiler' investigation. When ON, every interior board (LCD/Scroll/Destination/Driver/RouteNumber) skips its RenderTexture/housing build entirely in Awake(). Set this BEFORE entering Play Mode -- toggling it live mid-session does nothing for boards that already built themselves.")]
    public bool disableAllBoards = false;

    /// <summary>What the board scripts actually check -- unchanged call
    /// site (`BusBoardTestSwitch.DisableAllBoards`) from the static-class
    /// version, just backed by this component's Inspector value now.</summary>
    public static bool DisableAllBoards { get; private set; }

    private void Awake()
    {
        DisableAllBoards = disableAllBoards;
    }

    // Lets flipping the checkbox in the Inspector while NOT in Play Mode
    // (or between scene loads) update the static value immediately too,
    // rather than only ever reading it once at Awake.
    private void OnValidate()
    {
        DisableAllBoards = disableAllBoards;
    }
}