using UnityEngine;

public class BusAdBoard : MonoBehaviour
{
    // [ADD] Set once per physical ad plane in the bus prefab -- this is what
    // makes type-safety automatic. A King-type board will only ever pull
    // kingAdMaterial off whatever AdData it's given, a Mini-type board only
    // ever pulls miniAdMaterial, etc. There's no path left where a caller
    // can hand this board a wrong-shaped material by mistake.
    [Tooltip("Which physical ad slot this specific plane is. Determines which material gets pulled off an AdData campaign -- set once per board in the prefab, never changes at runtime.")]
    public AdBoardType boardType = AdBoardType.King;

    [SerializeField] private Renderer adRenderer;

    // [ADD] Source of truth for whether this board currently has real ad art
    // showing. NPCBusController's idle/active toggle reads this instead of
    // blanket-forcing the renderer back on whenever the bus comes off idle
    // -- otherwise a deliberate SetAd(null) gets silently undone the next
    // time this bus is dispatched from the depot pool.
    public bool HasAdAssigned { get; private set; }

    public void SetAd(AdData ad)
    {
        // [ADD] Diagnostic log -- confirms whether SetAd is even being
        // called for this board, and with what. If you never see this line
        // in the console for a given bus, that board isn't going through
        // any of the registration paths (BusSpawner's normal dispatch,
        // BusManager.RegisterBus, or PlayerHandoff.SetPlayerBus) at all --
        // remove once you've confirmed things are wiring up correctly.
        Debug.Log($"[BusAdBoard] SetAd on {gameObject.name} (type={boardType}) -- campaign={(ad != null ? ad.adName : "null")}");

        Material mat = ad != null ? ad.GetMaterialFor(boardType) : null;

        // If this campaign doesn't have art for THIS board's format, treat
        // it exactly like "no ad" -- shut the plane off rather than ever
        // falling back to a differently-shaped material.
        if (mat == null)
        {
            HasAdAssigned = false;
            adRenderer.enabled = false;
            return;
        }

        HasAdAssigned = true;
        adRenderer.enabled = true;
        // [FIX — Material leak] Was adRenderer.material = mat, which clones
        // `mat` into a brand-new Material instance the first time it's
        // touched, and clones it AGAIN on every subsequent call (ad
        // rotation, redispatch, possession -- anything that calls SetAd
        // again). Nothing here modifies the material per-instance, and
        // `mat` is already a shared campaign asset from AdData -- there was
        // never a reason to instance it. Unity never auto-destroys the
        // previous clone when you reassign .material; it just becomes an
        // orphaned native object that leaks for the rest of the session,
        // one more instance every time this runs, on every ad board, on
        // every bus. .sharedMaterial assigns the actual shared asset
        // directly -- no clone, no leak, and it's also what actually
        // allows Unity to batch/GPU-instance multiple boards showing the
        // same ad, which .material was silently preventing anyway.
        adRenderer.sharedMaterial = mat;
    }
}