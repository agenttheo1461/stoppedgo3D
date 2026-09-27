using UnityEngine;

// ═══════════════════════════════════════════════════════════════════════════════
//  BusDestinationBoard  v3 -- in-house dot-matrix, no TMP
//
//  Same 3-row layout and public API as v2 (SetRoute / SetBlank /
//  SetOutOfService), but now builds three DotMatrixText rows (real
//  extruded-dot meshes, see DotMatrixText.cs / DotMatrixGlyphs.cs) instead
//  of TextMeshProUGUI + Canvas. No font asset, no TMP dependency, no
//  Canvas/CanvasScaler -- these are plain world-space mesh children
//  positioned directly in metres under this transform.
//
//  ROWS
//  ────
//  Row 1 (left):  GIANT route number.
//  Row 2 (right of number): destination name -- pitch (dot size) shrinks
//                 as the string gets longer, so long names still fit
//                 instead of overflowing, same idea as TMP auto-sizing.
//  Row 3 (small, under destination): qualifier line ("VIA EVERGREEN",
//                 "LIMITED", "MAX"), pulled from BusRouteData's
//                 routeQualifierOutbound/Inbound fields.
//
//  SETUP (one-time)
//  ────────────────
//  1. Create one Material using shader Custom/DotMatrixDot.
//  2. Assign it to `dotMaterial` below -- that single material can be
//     shared across the whole fleet; per-row color comes from vertex
//     color (dotColor on each row), not the material.
//  3. Add this component to a child GO positioned/rotated where the
//     front blind box should sit.
//  4. Call board.SetRoute("10", "City Centre", "Via Evergreen") whenever
//     route/destination changes, or tick autoReadFromController to have
//     it poll the sibling NPCBusController automatically.
// ═══════════════════════════════════════════════════════════════════════════════

public class BusDestinationBoard : MonoBehaviour
{
    [Header("Shared Material")]
    [Tooltip("Material using Custom/DotMatrixDot. Share one instance across the whole fleet.")]
    public Material dotMaterial;

    [Header("Board Size (metres)")]
    public float boardWidth  = 1.4f;
    public float boardHeight = 0.42f;

    [Header("Colours")]
    [Tooltip("There's no background panel -- this dot color IS the board's look, so it defaults to amber LED.")]
    public Color textColor = new Color(1.00f, 0.55f, 0.00f, 1f);

    [Header("Typography (row heights, metres)")]
    [Tooltip("Row height for the giant route number.")]
    [Range(0.05f, 0.50f)] public float routeRowHeight = 0.22f;
    [Tooltip("Destination row height at full size (short strings).")]
    [Range(0.02f, 0.30f)] public float destRowHeightMax = 0.16f;
    [Tooltip("Destination row height floor (long strings shrink toward this).")]
    [Range(0.01f, 0.20f)] public float destRowHeightMin = 0.05f;
    [Tooltip("Row height for the small qualifier line.")]
    [Range(0.01f, 0.12f)] public float qualifierRowHeight = 0.06f;
    [Tooltip("Destination string length at/above which it's shrunk all the way to destRowHeightMin.")]
    public int destLongStringChars = 14;

    [Header("MAX Badge")]
    [Tooltip("If the destination text contains 'MAX' anywhere, split it out into its own filled badge (solid panel in textColor, punched-out/hollow letters) followed by the rest of the destination.")]
    public bool autoMaxBadge = true;
    [Tooltip("Gap between the MAX badge and the rest of the destination text, in metres.")]
    public float maxBadgeGap = 0.02f;

    [Header("Dot Geometry")]
    [Tooltip("Dot diameter as a fraction of dot pitch.")]
    [Range(0.3f, 1f)] public float dotSize = 0.8f;
    [Tooltip("Physical dot extrusion depth in metres. <=0 means 'auto' -- each row derives its own thickness as 3x its dot pitch, so dots are genuinely 3 pixels thick regardless of row size.")]
    public float dotThickness = -1f;

    // ── Auto-polling ──────────────────────────────────────────────────────────
    [Header("Auto-Read from Controller")]
    public bool  autoReadFromController = true;
    public float updateIntervalSeconds  = 1f;

    // ── Runtime ───────────────────────────────────────────────────────────────
    private DotMatrixText _routeRow;
    private DotMatrixText _destRow;
    private DotMatrixText _qualifierRow;
    private DotMatrixText _maxBadgeRow;

    private NPCBusController _controller;
    private float            _pollTimer;

    private string _lastRoute     = null;
    private string _lastDest      = null;
    private string _lastQualifier = null;

    // ═════════════════════════════════════════════════════════════════════════
    //  LIFECYCLE
    // ═════════════════════════════════════════════════════════════════════════
    private void Awake()
    {
        BuildRows();

        if (autoReadFromController)
            _controller = GetComponentInParent<NPCBusController>();
    }

    private void Update()
    {
        if (!autoReadFromController || _controller == null) return;

        _pollTimer -= Time.deltaTime;
        if (_pollTimer > 0f) return;
        _pollTimer = updateIntervalSeconds;

        PollController();
    }

    // ═════════════════════════════════════════════════════════════════════════
    //  PUBLIC API
    // ═════════════════════════════════════════════════════════════════════════

    public void SetRoute(string routeNumber, string destination, string qualifier = "")
    {
        if (routeNumber == _lastRoute && destination == _lastDest && qualifier == _lastQualifier) return;
        _lastRoute     = routeNumber;
        _lastDest      = destination;
        _lastQualifier = qualifier;

        string routeText = string.IsNullOrEmpty(routeNumber) ? "" : routeNumber.ToUpper();
        string destText  = string.IsNullOrEmpty(destination) ? "" : destination.ToUpper();
        string qualText  = string.IsNullOrEmpty(qualifier)   ? "" : qualifier.ToUpper();

        _routeRow.SetText(routeText);

        // "MAX" pulls out into its own inline badge (solid panel,
        // punched-out letters) placed before the destination text --
        // checked in BOTH the destination string ("MAX AIRPORT") and the
        // qualifier field (routeQualifierOutbound/Inbound == "MAX", the
        // more common real-world place express/limited-stop service
        // actually gets flagged from BusRouteData).
        string destRemainder = destText;
        string qualForRow = qualText;
        bool showMaxBadge = false;

        if (autoMaxBadge)
        {
            int idx = destText.IndexOf("MAX", System.StringComparison.Ordinal);
            if (idx >= 0)
            {
                showMaxBadge = true;
                destRemainder = (destText.Substring(0, idx) + destText.Substring(idx + 3)).Trim();
                while (destRemainder.Contains("  ")) destRemainder = destRemainder.Replace("  ", " ");
            }

            if (qualText.Trim() == "MAX")
            {
                showMaxBadge = true;
                // The qualifier line's entire job here was just to say
                // "MAX" -- that's now covered by the inline badge, so
                // don't ALSO render it a second time as its own row
                // underneath (that read as a redundant duplicate "MAX").
                qualForRow = "";
            }
        }

        _maxBadgeRow.invertedBadge = showMaxBadge;
        _maxBadgeRow.SetText(showMaxBadge ? "MAX" : "");

        // Shrink the destination row's available width by however much
        // the badge just ate, and do it BEFORE SetText below -- ShrinkAndWrap
        // needs the correct maxWidth at the moment it evaluates the string,
        // not one frame later, or a long name would wrap/shrink against
        // the wrong (badge-less) width for one update.
        _destRow.maxWidth = ComputeDestMaxWidth();

        // Overflow (shrink/wrap) is now handled inside DotMatrixText itself
        // via maxWidth + ShrinkAndWrap (set up once in CreateRow below) --
        // no more manual char-count-based targetHeight lerp here, since
        // that couldn't wrap, only shrink, which is why long destinations
        // used to just get crushed onto one line instead of splitting.
        _destRow.SetText(destRemainder);

        // Any OTHER qualifier text (e.g. "VIA EVERGREEN", "LIMITED")
        // still renders on its own row as before, badge-styled only if
        // it's literally "MAX" on its own -- which, per above, has
        // already been cleared to "" by this point, so this condition is
        // effectively dead for MAX specifically now and only fires for
        // some other value someone deliberately wants badge-styled.
        _qualifierRow.invertedBadge = qualForRow.Trim() == "MAX";
        _qualifierRow.SetText(qualForRow);

        // When there's no qualifier row content, let the destination row
        // reclaim that vertical band instead of leaving a dead gap under
        // it. Note this checks qualForRow, not the raw qualifier param --
        // a MAX-only qualifier that got folded into the badge shouldn't
        // still reserve empty space for a row that's now blank.
        bool hasQualifier = !string.IsNullOrEmpty(qualForRow);
        PositionDestBlock(hasQualifier);
    }

    public void SetBlank()
    {
        if (_lastRoute == "" && _lastDest == "" && _lastQualifier == "") return;
        _lastRoute = _lastDest = _lastQualifier = "";

        _routeRow.SetText("");
        _destRow.SetText("");
        _qualifierRow.invertedBadge = false;
        _qualifierRow.SetText("");
        _maxBadgeRow.invertedBadge = false;
        _maxBadgeRow.SetText("");
    }

    public void SetOutOfService()
    {
        const string OOS = "NOT IN SERVICE";
        if (_lastRoute == "" && _lastDest == OOS) return;
        _lastRoute     = "";
        _lastDest      = OOS;
        _lastQualifier = "";

        _routeRow.SetText("");
        _destRow.targetHeight = destRowHeightMax;
        _destRow.maxWidth = ComputeDestMaxWidth();
        _destRow.SetText(OOS);
        _qualifierRow.invertedBadge = false;
        _qualifierRow.SetText("");
        _maxBadgeRow.invertedBadge = false;
        _maxBadgeRow.SetText("");
        PositionDestBlock(false);
    }

    // ═════════════════════════════════════════════════════════════════════════
    //  INTERNAL — POLL CONTROLLER
    // ═════════════════════════════════════════════════════════════════════════
    private void PollController()
    {
        if (!_controller.running || _controller.State == NPCBusController.BusState.Idle)
        {
            SetBlank();
            return;
        }

        if (_controller.State == NPCBusController.BusState.DeadRunning ||
            _controller.State == NPCBusController.BusState.ExpressDeadRun)
        {
            SetOutOfService();
            return;
        }

        BusRouteData route = _controller.CurrentRoute;
        if (route == null) { SetOutOfService(); return; }

        string routeNum = route.routeNumber ?? "";

        // Plain route number (no '~') and the destination of the variant the bus is running (a short turn shows
        // its own turnback destination); falls back to the terminal code when there's no destination text.
        string dest = route.GetDestinationName(_controller.IsOutbound, _controller.CurrentVariant);
        if (string.IsNullOrEmpty(dest)) dest = (_controller.IsOutbound ? route.terminalZCode : route.terminalACode) ?? "";
        string qualifier = (_controller.IsOutbound ? route.routeQualifierOutbound : route.routeQualifierInbound) ?? "";

        SetRoute(routeNum, dest, qualifier);
    }

    // ═════════════════════════════════════════════════════════════════════════
    //  INTERNAL — ROW CONSTRUCTION / LAYOUT
    // ═════════════════════════════════════════════════════════════════════════
    private void BuildRows()
    {
        // Route number -- giant, pinned to the left, full board height.
        _routeRow = CreateRow("RouteRow", routeRowHeight, DotMatrixText.HAlign.Left, DotMatrixText.VAlign.Middle);
        _routeRow.transform.localPosition = new Vector3(0f, boardHeight * 0.5f, 0f);

        // Destination -- right of the route number, auto-shrinks with
        // string length. Both rows share the same left edge (destColumnX)
        // and VAlign.Top so stacking them is a simple vertical offset --
        // no more "destination pinned to the ceiling, qualifier pinned to
        // the floor" independent anchoring, which is what made the
        // qualifier read like a stray superscript instead of a second
        // line that belongs to the same block.
        _destRow = CreateRow("DestRow", destRowHeightMax, DotMatrixText.HAlign.Left, DotMatrixText.VAlign.Top);
        _destRow.autoScaleToHeight = true;

        // The destination column starts at destColumnX (30% of board
        // width, see PositionDestBlock) and runs to the board's right
        // edge, minus a small margin so text doesn't touch the bezel.
        // This is the actual overflow bug fix: previously nothing set
        // maxWidth/overflowMode here at all, so DotMatrixText's wrap
        // system never engaged -- the board just crushed long strings
        // onto one line via the old manual char-count shrink instead.
        _destRow.maxWidth           = boardWidth - (boardWidth * 0.30f) - 0.04f;
        _destRow.overflowMode       = DotMatrixText.OverflowMode.ShrinkAndWrap;
        _destRow.wrapWidthMultiplier = 1.25f;
        _destRow.minShrinkFraction  = destRowHeightMin / destRowHeightMax; // still respects the configured floor

        // Qualifier -- small line directly under the destination, same
        // left edge, same alignment scheme, just scaled down.
        _qualifierRow = CreateRow("QualifierRow", qualifierRowHeight, DotMatrixText.HAlign.Left, DotMatrixText.VAlign.Top);

        // MAX badge -- filled panel + hollow letters, sits inline before
        // the destination text whenever "MAX" appears anywhere in it.
        // Matches destRowHeightMax so it visually pairs with the
        // destination row's first line, not some arbitrary size.
        _maxBadgeRow = CreateRow("MaxBadgeRow", destRowHeightMax, DotMatrixText.HAlign.Left, DotMatrixText.VAlign.Middle);
        // Note: DotMatrixDot.shader ignores vertex-color alpha (it writes
        // its own AA-derived alpha for AlphaToMask), so a transparent
        // badgeTextColor wouldn't actually punch a hole -- black is what
        // reads as "hollow" here, since it matches the dark board/bezel
        // behind it. This is DotMatrixText's default already; left
        // explicit so it's obvious this is intentional, not an oversight.
        _maxBadgeRow.badgeTextColor = Color.black;

        PositionDestBlock(false);

        SetBlank();
    }

    [Tooltip("Vertical gap between the destination line and the qualifier line, as a fraction of the destination row's current (possibly shrunk) height.")]
    [Range(0f, 1f)] public float destQualifierGapRatio = 0.35f;

    /// <summary>
    /// Available width for the destination row's own text, after
    /// reserving space for the MAX badge (if currently showing) plus the
    /// gap and the board's right-edge margin.
    /// </summary>
    private float ComputeDestMaxWidth()
    {
        float destColumnX = boardWidth * 0.30f;
        float destTextX = destColumnX + BadgeFootprint();
        return Mathf.Max(0.05f, boardWidth - destTextX - 0.04f);
    }

    /// <summary>Badge's actual rendered footprint (panel width incl. padding + trailing gap), or 0 if not showing.</summary>
    private float BadgeFootprint()
    {
        if (!_maxBadgeRow.invertedBadge) return 0f;
        float badgePitch = _maxBadgeRow.targetHeight / DotMatrixGlyphs.GlyphHeight;
        float badgePad   = badgePitch * _maxBadgeRow.badgePaddingDots;
        return _maxBadgeRow.CurrentWidth + badgePad * 2f + maxBadgeGap;
    }

    private void PositionDestBlock(bool hasQualifier)
    {
        float destColumnX = boardWidth * 0.30f;
        float badgeFootprint = BadgeFootprint();
        float destTextX = destColumnX + badgeFootprint;

        // destH is the destination row's ACTUAL rendered height, which is
        // now up to 2 lines tall when ShrinkAndWrap has wrapped a long
        // name -- using targetHeight here would leave the qualifier
        // overlapping the destination's second line, since targetHeight
        // is just the per-line nominal height, not the block height.
        float destH = _destRow.CurrentHeight > 0f ? _destRow.CurrentHeight : _destRow.targetHeight;
        float gap   = hasQualifier ? _destRow.targetHeight * destQualifierGapRatio : 0f;
        float qualH = hasQualifier ? qualifierRowHeight : 0f;

        float blockHeight = destH + gap + qualH;
        float blockTop    = (boardHeight + blockHeight) * 0.5f; // top-anchored rows, so top edge = centerline + half block

        // The badge is vertically centered against the destination row's
        // FIRST line specifically (not the whole possibly-2-line block),
        // so it reads as sitting next to "MAX" the word, not floating
        // next to the block's vertical midpoint.
        float destFirstLineH = Mathf.Min(destH, _destRow.targetHeight);
        _maxBadgeRow.transform.localPosition =
            new Vector3(destColumnX, blockTop - destFirstLineH * 0.5f - _maxBadgeRow.targetHeight * 0.5f, 0f);

        _destRow.transform.localPosition = new Vector3(destTextX, blockTop, 0f);
        _qualifierRow.transform.localPosition = new Vector3(destColumnX, blockTop - destH - gap, 0f);
    }

    private DotMatrixText CreateRow(string name, float rowHeight, DotMatrixText.HAlign hAlign, DotMatrixText.VAlign vAlign)
    {
        var go = new GameObject(name);
        go.transform.SetParent(transform, false);

        var row = go.AddComponent<DotMatrixText>();
        row.SetMaterial(dotMaterial);
        row.dotColor          = textColor;
        row.dotSize           = dotSize;
        row.dotThickness       = dotThickness;
        row.autoScaleToHeight  = true;
        row.targetHeight       = rowHeight;
        row.horizontalAlign    = hAlign;
        row.verticalAlign      = vAlign;

        return row;
    }
}