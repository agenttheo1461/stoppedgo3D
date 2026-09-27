using UnityEngine;

// [ADD] Which physical ad plane a material is meant for. Each has a fixed
// real-world aspect ratio -- stretching one type's art onto another type's
// board is exactly the bug this whole typed system exists to prevent.
public enum AdBoardType
{
    King,   // side king ad, 3.5 x 1.7
    Back,   // rear ad, 1 x 1
    Mini,   // interior/short-strip ad, 3.5 x 0.625
}

[CreateAssetMenu(menuName = "Transit/Ad Data")]
public class AdData : ScriptableObject
{
    public string adName;

    // [ADD] One AdData asset now represents a full campaign -- the same
    // advertiser's creative across every board format it's bought, not just
    // a single material. A campaign doesn't have to buy every format; any
    // slot left null just means "this advertiser doesn't run that size,"
    // and BusAdBoard.SetAd already treats a null material as no-ad (shuts
    // the plane off) rather than falling back to a wrong-shaped material.
    [Header("King Ad Material (3.5 x 1.7)")]
    [Tooltip("Side ad plane, standard 3.5:1.7 board.")]
    public Material kingAdMaterial;

    [Header("Back Ad Material (1 x 1)")]
    [Tooltip("Rear-mounted ad plane, square 1:1 board.")]
    public Material backAdMaterial;

    [Header("Mini Ad Material (3.5 x 0.625)")]
    [Tooltip("Interior/short-strip ad plane, wide 3.5:0.625 board.")]
    public Material miniAdMaterial;

    [Header("Future Wrap Materials")]
    [Tooltip("The material that will be applied to the whole bus body later.")]
    public Material wrapMaterial;

    /// <summary>The single lookup point for "what material goes on a board
    /// of this type." BusAdBoard calls this instead of ever being handed a
    /// raw Material directly -- that's what makes it structurally
    /// impossible for a caller to hand a board the wrong-aspect creative.</summary>
    public Material GetMaterialFor(AdBoardType type) => type switch
    {
        AdBoardType.King => kingAdMaterial,
        AdBoardType.Back => backAdMaterial,
        AdBoardType.Mini => miniAdMaterial,
        _ => null,
    };

    // [ADD] Whichever script randomly assigns a campaign to a board needs to
    // filter by this BEFORE picking -- not just call GetMaterialFor and
    // accept null. That's the actual source of the gray-board bug: a
    // campaign that only bought King gets rolled for a Mini board, returns
    // null, and the board correctly (but unhelpfully) shuts itself off.
    public bool HasMaterialFor(AdBoardType type) => GetMaterialFor(type) != null;

    /// <summary>True if this campaign bought at least one board format.
    /// Mostly useful for catching a totally-empty AdData asset in a picker
    /// or a validation pass, rather than letting it silently roll as a
    /// "valid" campaign that never lights up any board.</summary>
    public bool HasAnyMaterial => kingAdMaterial != null || backAdMaterial != null || miniAdMaterial != null;
}