using System.Collections.Generic;
using UnityEngine;

// ═══════════════════════════════════════════════════════════════════════════════
//  DotMatrixText
//
//  Builds a procedural mesh of lit-dot geometry from a string, using the
//  5x7 patterns in DotMatrixGlyphs. No TextMeshPro, no texture atlas -- each
//  lit dot is real extruded geometry (a short box, not a flat plane), so the
//  board reads as individual raised/domed LEDs rather than a printed decal.
//
//  PERFORMANCE
//  ───────────
//  The mesh is only rebuilt when the text (or color/pitch) actually changes
//  -- SetText() early-outs on no-op calls. For a fleet of NPC buses whose
//  boards mostly sit static between stops, this keeps cost near zero outside
//  of the occasional route/destination change.
//
//  DEPTH
//  ─────
//  Each dot is extruded dotThickness metres deep (front face + 4 side walls
//  + a dim back cap), not just a flat quad -- by default this is set to
//  3x the dot pitch, so the dots genuinely read as "3 pixels thick" boxes
//  with real parallax/shading as the camera moves, instead of a flat
//  billboard image of dots.
//
//  The front face carries full dot-color and gets circle-masked by
//  DotMatrixDot.shader (UV 0..1 across the quad). Side walls and the back
//  cap use a constant centred UV (0.5, 0.5) so the shader's circular clip
//  never touches them (distance-from-centre = 0) -- they just render as
//  solid, slightly dimmed panels, which is what sells the extrusion as a
//  rounded LED lens rather than a flat sticker.
// ═══════════════════════════════════════════════════════════════════════════════

[RequireComponent(typeof(MeshFilter))]
[RequireComponent(typeof(MeshRenderer))]
public class DotMatrixText : MonoBehaviour
{
    public enum HAlign { Left, Center, Right }
    public enum VAlign { Top, Middle, Bottom }

    [Header("Text")]
    [TextArea] public string text = "";

    [Header("Dot Grid")]
    [Tooltip("Distance in metres between adjacent dot centres.")]
    public float dotPitch = 0.02f;
    [Tooltip("Dot diameter as a fraction of dotPitch.")]
    [Range(0.3f, 1f)] public float dotSize = 0.8f;
    [Tooltip("Physical extrusion depth of each dot, in metres. Defaults to 3x dotPitch so dots are genuinely 3 'pixels' thick, not flat quads.")]
    public float dotThickness = -1f; // -1 = auto (3x pitch), set explicitly to override
    [Tooltip("Extra blank dot-columns inserted between characters.")]
    public int charSpacing = 1;

    [Header("Color")]
    public Color dotColor = new Color(1.00f, 0.55f, 0.00f, 1f);
    [Tooltip("Side-wall / back-cap brightness multiplier, sells the extrusion depth.")]
    [Range(0.2f, 1f)] public float sideShade = 0.55f;

    [Header("Inverted Badge (e.g. 'MAX')")]
    [Tooltip("When on, draws a solid filled panel behind the text in dotColor, and renders the glyph dots themselves in badgeTextColor instead -- a filled block with contrasting punched-out letters, instead of individual lit dots on a dark background.")]
    public bool invertedBadge = false;
    [Tooltip("Letter color used when invertedBadge is on. Defaults to black for max contrast against dotColor's filled panel.")]
    public Color badgeTextColor = Color.black;
    [Tooltip("Padding around the text, in dot-pitch units, added to the filled panel on all sides so the letters don't touch the panel edge.")]
    public float badgePaddingDots = 1f;

    [Header("Auto Scale")]
    [Tooltip("If true, dotPitch is derived each rebuild from targetHeight instead of used directly.")]
    public bool autoScaleToHeight = false;
    [Tooltip("Desired total row height in metres (used only when autoScaleToHeight is on).")]
    public float targetHeight = 0.2f;

    public enum OverflowMode { ShrinkToFit, WrapSecondLine, ShrinkAndWrap }

    [Header("Overflow (text longer than the board)")]
    [Tooltip("Max width in metres before overflow kicks in. 0 = unlimited (text can run off the board).")]
    public float maxWidth = 0f;
    [Tooltip("ShrinkToFit: uniformly shrinks dot pitch so the whole string fits on one line.\n" +
             "WrapSecondLine: keeps base dot size and word-wraps onto a second line below.\n" +
             "ShrinkAndWrap: wraps to two lines first at full size; only shrinks further if the " +
             "wrapped block still exceeds wrapWidthMultiplier x maxWidth.")]
    public OverflowMode overflowMode = OverflowMode.ShrinkToFit;
    [Tooltip("Gap between line 1 and line 2, in dot-pitch units. Used by WrapSecondLine and ShrinkAndWrap.")]
    public float lineGapDots = 1f;
    [Tooltip("Floor on how far shrinking is allowed to shrink dotPitch, as a fraction of the base pitch. Prevents text from shrinking into unreadable dust on very long strings.")]
    [Range(0.2f, 1f)] public float minShrinkFraction = 0.5f;
    [Tooltip("ShrinkAndWrap only: the wrapped two-line block is allowed to be up to this multiple of maxWidth wide before shrinking kicks in on top of the wrap. 1.25 = wrapped lines can run 25% over maxWidth before anything shrinks.")]
    public float wrapWidthMultiplier = 1.25f;

    [Header("Alignment (relative to this transform's local origin)")]
    public HAlign horizontalAlign = HAlign.Left;
    public VAlign verticalAlign   = VAlign.Middle;

    public float CurrentWidth  { get; private set; }
    public float CurrentHeight { get; private set; }

    private MeshFilter   _mf;
    private MeshRenderer _mr;
    private Mesh         _mesh;

    // Cache to skip redundant rebuilds
    private string _lastText;
    private Color  _lastColor;
    private float  _lastPitch;
    private float  _lastThickness;
    private bool   _lastInverted;
    private Color  _lastBadgeTextColor;
    private float  _lastMaxWidth;
    private OverflowMode _lastOverflowMode;
    private float  _lastWrapWidthMultiplier;

    private void Awake()
    {
        EnsureComponents();
        Rebuild(force: true);
    }

    /// <summary>Assign the shared dot-matrix material (Custom/DotMatrixDot).</summary>
    public void SetMaterial(Material mat)
    {
        EnsureComponents();
        _mr.sharedMaterial = mat;
    }

    /// <summary>Update displayed text. No-ops (skips mesh rebuild) if nothing changed.</summary>
    public void SetText(string newText)
    {
        text = newText ?? "";
        Rebuild(force: false);
    }

    public void SetColor(Color c)
    {
        dotColor = c;
        Rebuild(force: false);
    }

    private void EnsureComponents()
    {
        if (_mf == null) _mf = GetComponent<MeshFilter>();
        if (_mr == null) _mr = GetComponent<MeshRenderer>();
        if (_mesh == null)
        {
            _mesh = new Mesh { name = "DotMatrixText_Mesh" };
            _mf.sharedMesh = _mesh;
        }
        _mr.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
        _mr.receiveShadows    = false;
    }

    private void Rebuild(bool force)
    {
        EnsureComponents();

        float pitch = dotPitch;
        if (autoScaleToHeight)
            pitch = targetHeight / DotMatrixGlyphs.GlyphHeight;

        float thickness = dotThickness; // resolved (auto or fixed) inside BuildMesh, after overflow shrink

        if (!force &&
            text == _lastText &&
            dotColor == _lastColor &&
            Mathf.Approximately(pitch, _lastPitch) &&
            Mathf.Approximately(thickness, _lastThickness) &&
            invertedBadge == _lastInverted &&
            (!invertedBadge || badgeTextColor == _lastBadgeTextColor) &&
            Mathf.Approximately(maxWidth, _lastMaxWidth) &&
            overflowMode == _lastOverflowMode &&
            Mathf.Approximately(wrapWidthMultiplier, _lastWrapWidthMultiplier))
        {
            return; // nothing actually changed -- skip the rebuild
        }

        BuildMesh(pitch, thickness);

        _lastText           = text;
        _lastColor          = dotColor;
        _lastPitch          = pitch;
        _lastThickness      = thickness;
        _lastInverted       = invertedBadge;
        _lastBadgeTextColor = badgeTextColor;
        _lastMaxWidth       = maxWidth;
        _lastOverflowMode   = overflowMode;
        _lastWrapWidthMultiplier = wrapWidthMultiplier;
    }

    private void BuildMesh(float pitch, float rawThickness)
    {
        var verts = new List<Vector3>();
        var tris  = new List<int>();
        var uvs   = new List<Vector2>();
        var cols  = new List<Color>();

        string s = text ?? "";
        int glyphW = DotMatrixGlyphs.GlyphWidth;
        int glyphH = DotMatrixGlyphs.GlyphHeight;

        // ── Overflow resolution ────────────────────────────────────────
        List<string> lines = new List<string> { s };
        float finalPitch = pitch;

        if (maxWidth > 0f && s.Length > 0)
        {
            float baseWidth = ColsFor(s, glyphW, charSpacing) * pitch;

            if (baseWidth > maxWidth)
            {
                if (overflowMode == OverflowMode.ShrinkToFit)
                {
                    float shrink = Mathf.Max(maxWidth / baseWidth, minShrinkFraction);
                    finalPitch = pitch * shrink;
                }
                else if (overflowMode == OverflowMode.WrapSecondLine)
                {
                    lines = WrapToTwoLines(s, glyphW, charSpacing, pitch, maxWidth);
                    int widestCols = 0;
                    foreach (var line in lines)
                        widestCols = Mathf.Max(widestCols, ColsFor(line, glyphW, charSpacing));
                    float widestWidth = widestCols * pitch;
                    if (widestWidth > maxWidth)
                        finalPitch = pitch * Mathf.Max(maxWidth / widestWidth, minShrinkFraction);
                }
                else // ShrinkAndWrap
                {
                    // Wrap first at full size, no shrink -- wrapping alone
                    // solves most overflow without touching pitch at all.
                    lines = WrapToTwoLines(s, glyphW, charSpacing, pitch, maxWidth);
                    int widestCols = 0;
                    foreach (var line in lines)
                        widestCols = Mathf.Max(widestCols, ColsFor(line, glyphW, charSpacing));
                    float widestWidth = widestCols * pitch;

                    // Only shrink on top of the wrap if the wrapped block
                    // still runs past the allowed cap (maxWidth * multiplier).
                    float cap = maxWidth * wrapWidthMultiplier;
                    if (widestWidth > cap)
                        finalPitch = pitch * Mathf.Max(cap / widestWidth, minShrinkFraction);
                }
            }
        }

        pitch = finalPitch;
        float thickness = rawThickness > 0f ? rawThickness : pitch * 3f;

        int widestLineCols = 0;
        foreach (var line in lines)
            widestLineCols = Mathf.Max(widestLineCols, ColsFor(line, glyphW, charSpacing));

        float lineGap = lineGapDots * pitch;
        float rowHeight = glyphH * pitch;

        CurrentWidth  = widestLineCols * pitch;
        CurrentHeight = lines.Count * rowHeight + Mathf.Max(0, lines.Count - 1) * lineGap;

        float originX = horizontalAlign switch
        {
            HAlign.Center => -CurrentWidth * 0.5f,
            HAlign.Right  => -CurrentWidth,
            _             => 0f,
        };
        float originYBlock = verticalAlign switch
        {
            VAlign.Middle => -CurrentHeight * 0.5f,
            VAlign.Top    => -CurrentHeight,
            _             => 0f,
        };

        float radius = pitch * dotSize * 0.5f;
        Color letterFront = invertedBadge ? badgeTextColor : dotColor;
        Color side = letterFront * sideShade;
        side.a = letterFront.a;

        for (int li = 0; li < lines.Count; li++)
        {
            string line = lines[li];
            if (line.Length == 0) continue;

            int lineCols = ColsFor(line, glyphW, charSpacing);
            float lineOriginXAbs = originX + (CurrentWidth - lineCols * pitch) *
                (horizontalAlign == HAlign.Center ? 0.5f : (horizontalAlign == HAlign.Right ? 1f : 0f));
            float lineOriginY = originYBlock + (lines.Count - 1 - li) * (rowHeight + lineGap);

            if (invertedBadge)
            {
                float pad = pitch * badgePaddingDots;
                Color panelColor = dotColor;
                Color panelSide  = panelColor * sideShade;
                panelSide.a = panelColor.a;

                float panelThickness = thickness * 0.6f;
                float panelZ = -thickness * 0.25f;

                AddPanel(verts, tris, uvs, cols,
                    lineOriginXAbs - pad, lineOriginY - pad,
                    lineOriginXAbs + lineCols * pitch + pad, lineOriginY + rowHeight + pad,
                    panelZ, panelThickness, panelColor, panelSide);
            }

            int col = 0;
            foreach (char c in line)
            {
                string[] pattern = DotMatrixGlyphs.GetPattern(c);

                for (int row = 0; row < glyphH; row++)
                {
                    string rowStr = pattern[row];
                    for (int gc = 0; gc < glyphW; gc++)
                    {
                        if (rowStr[gc] != '#') continue;

                        float cx = lineOriginXAbs + (col + gc) * pitch + pitch * 0.5f;
                        float cy = lineOriginY + (glyphH - 1 - row) * pitch + pitch * 0.5f;

                        AddDot(verts, tris, uvs, cols, cx, cy, radius, thickness, letterFront, side);
                    }
                }

                col += glyphW + charSpacing;
            }
        }

        _mesh.Clear();
        _mesh.indexFormat = verts.Count > 65000
            ? UnityEngine.Rendering.IndexFormat.UInt32
            : UnityEngine.Rendering.IndexFormat.UInt16;
        _mesh.SetVertices(verts);
        _mesh.SetUVs(0, uvs);
        _mesh.SetColors(cols);
        _mesh.SetTriangles(tris, 0);
        _mesh.RecalculateNormals();
        _mesh.RecalculateBounds();
    }

    /// <summary>Dot-columns a string occupies, including inter-character spacing but not trailing spacing.</summary>
    private static int ColsFor(string s, int glyphW, int charSpacing)
        => s.Length > 0 ? s.Length * glyphW + (s.Length - 1) * charSpacing : 0;

    /// <summary>
    /// Greedy word-wrap into exactly two lines at the given pitch/maxWidth,
    /// breaking on spaces. If a single word is wider than maxWidth on its
    /// own, it is left unbroken on its line (BuildMesh falls back to a
    /// uniform shrink in that case).
    /// </summary>
    private static List<string> WrapToTwoLines(string s, int glyphW, int charSpacing, float pitch, float maxWidth)
    {
        string[] words = s.Split(' ');
        var line1 = new List<string>();
        int line1Cols = 0;
        int i = 0;

        for (; i < words.Length; i++)
        {
            string word = words[i];
            int wordCols = ColsFor(word, glyphW, charSpacing);
            int addedCols = line1.Count > 0 ? line1Cols + (glyphW + charSpacing) + wordCols : wordCols;

            if (line1.Count > 0 && addedCols * pitch > maxWidth)
                break;

            line1.Add(word);
            line1Cols = addedCols;
        }

        if (line1.Count == 0 && i < words.Length)
        {
            line1.Add(words[i]);
            i++;
        }

        string remaining = string.Join(" ", words, i, words.Length - i);
        return new List<string> { string.Join(" ", line1), remaining };
    }


    /// <summary>
    /// Builds one physically-thick LED dot: a circle-masked front face at
    /// z = 0, a dim back cap at z = -thickness, and four dim side walls
    /// connecting them -- so the dot is a real 3-pixel-deep box, not a
    /// flat plane.
    /// </summary>
    /// <summary>
    /// Public so other components -- e.g. a full-grid LED background behind
    /// a display, not just glyph text -- can build individual dots with the
    /// exact same geometry (front face circle-masked, real extruded depth,
    /// dim side walls) without duplicating this code.
    /// </summary>
    public static void AddDot(
        List<Vector3> verts, List<int> tris, List<Vector2> uvs, List<Color> cols,
        float cx, float cy, float r, float thickness, Color frontColor, Color sideColor)
    {
        // Front face (z=0) -- UV spans 0..1 so the shader can circle-mask it.
        int fBase = verts.Count;
        verts.Add(new Vector3(cx - r, cy - r, 0f));
        verts.Add(new Vector3(cx + r, cy - r, 0f));
        verts.Add(new Vector3(cx + r, cy + r, 0f));
        verts.Add(new Vector3(cx - r, cy + r, 0f));
        uvs.Add(new Vector2(0f, 0f));
        uvs.Add(new Vector2(1f, 0f));
        uvs.Add(new Vector2(1f, 1f));
        uvs.Add(new Vector2(0f, 1f));
        for (int i = 0; i < 4; i++) cols.Add(frontColor);
        tris.Add(fBase + 0); tris.Add(fBase + 2); tris.Add(fBase + 1);
        tris.Add(fBase + 0); tris.Add(fBase + 3); tris.Add(fBase + 2);

        float bz = -thickness;

        // Back cap (z = -thickness) -- constant centred UV so the shader
        // never clips it (distance from centre = 0 => never discarded).
        int bBase = verts.Count;
        verts.Add(new Vector3(cx - r, cy - r, bz));
        verts.Add(new Vector3(cx + r, cy - r, bz));
        verts.Add(new Vector3(cx + r, cy + r, bz));
        verts.Add(new Vector3(cx - r, cy + r, bz));
        for (int i = 0; i < 4; i++) { uvs.Add(new Vector2(0.5f, 0.5f)); cols.Add(sideColor); }
        // Reversed winding vs. the front face since it faces -Z.
        tris.Add(bBase + 0); tris.Add(bBase + 1); tris.Add(bBase + 2);
        tris.Add(bBase + 0); tris.Add(bBase + 2); tris.Add(bBase + 3);

        // Four side walls connecting the front perimeter to the back
        // perimeter -- this is what gives the dot real physical depth
        // instead of it just being a flat sprite.
        AddSideWall(verts, tris, uvs, cols,
            new Vector3(cx - r, cy - r, 0f), new Vector3(cx + r, cy - r, 0f),
            new Vector3(cx + r, cy - r, bz), new Vector3(cx - r, cy - r, bz),
            sideColor); // bottom

        AddSideWall(verts, tris, uvs, cols,
            new Vector3(cx + r, cy - r, 0f), new Vector3(cx + r, cy + r, 0f),
            new Vector3(cx + r, cy + r, bz), new Vector3(cx + r, cy - r, bz),
            sideColor); // right

        AddSideWall(verts, tris, uvs, cols,
            new Vector3(cx + r, cy + r, 0f), new Vector3(cx - r, cy + r, 0f),
            new Vector3(cx - r, cy + r, bz), new Vector3(cx + r, cy + r, bz),
            sideColor); // top

        AddSideWall(verts, tris, uvs, cols,
            new Vector3(cx - r, cy + r, 0f), new Vector3(cx - r, cy - r, 0f),
            new Vector3(cx - r, cy - r, bz), new Vector3(cx - r, cy + r, bz),
            sideColor); // left
    }

    /// <summary>
    /// Builds one flat-ish extruded rectangular panel spanning (x0,y0) to
    /// (x1,y1) at depth z, used as the filled background behind an
    /// invertedBadge row. Front/back faces use constant centred UV (same
    /// trick as AddDot's side walls) so the shader's circular dot-mask
    /// never clips it -- it renders as a solid rect, not a circle.
    /// </summary>
    private static void AddPanel(
        List<Vector3> verts, List<int> tris, List<Vector2> uvs, List<Color> cols,
        float x0, float y0, float x1, float y1, float z, float thickness,
        Color frontColor, Color sideColor)
    {
        int fBase = verts.Count;
        verts.Add(new Vector3(x0, y0, z));
        verts.Add(new Vector3(x1, y0, z));
        verts.Add(new Vector3(x1, y1, z));
        verts.Add(new Vector3(x0, y1, z));
        for (int i = 0; i < 4; i++) { uvs.Add(new Vector2(0.5f, 0.5f)); cols.Add(frontColor); }
        tris.Add(fBase + 0); tris.Add(fBase + 2); tris.Add(fBase + 1);
        tris.Add(fBase + 0); tris.Add(fBase + 3); tris.Add(fBase + 2);

        float bz = z - thickness;
        int bBase = verts.Count;
        verts.Add(new Vector3(x0, y0, bz));
        verts.Add(new Vector3(x1, y0, bz));
        verts.Add(new Vector3(x1, y1, bz));
        verts.Add(new Vector3(x0, y1, bz));
        for (int i = 0; i < 4; i++) { uvs.Add(new Vector2(0.5f, 0.5f)); cols.Add(sideColor); }
        tris.Add(bBase + 0); tris.Add(bBase + 1); tris.Add(bBase + 2);
        tris.Add(bBase + 0); tris.Add(bBase + 2); tris.Add(bBase + 3);

        AddSideWall(verts, tris, uvs, cols,
            new Vector3(x0, y0, z), new Vector3(x1, y0, z),
            new Vector3(x1, y0, bz), new Vector3(x0, y0, bz), sideColor); // bottom
        AddSideWall(verts, tris, uvs, cols,
            new Vector3(x1, y0, z), new Vector3(x1, y1, z),
            new Vector3(x1, y1, bz), new Vector3(x1, y0, bz), sideColor); // right
        AddSideWall(verts, tris, uvs, cols,
            new Vector3(x1, y1, z), new Vector3(x0, y1, z),
            new Vector3(x0, y1, bz), new Vector3(x1, y1, bz), sideColor); // top
        AddSideWall(verts, tris, uvs, cols,
            new Vector3(x0, y1, z), new Vector3(x0, y0, z),
            new Vector3(x0, y0, bz), new Vector3(x0, y1, bz), sideColor); // left
    }

    private static void AddSideWall(
        List<Vector3> verts, List<int> tris, List<Vector2> uvs, List<Color> cols,
        Vector3 a, Vector3 b, Vector3 c, Vector3 d, Color color)
    {
        int baseIdx = verts.Count;
        verts.Add(a); verts.Add(b); verts.Add(c); verts.Add(d);
        for (int i = 0; i < 4; i++) { uvs.Add(new Vector2(0.5f, 0.5f)); cols.Add(color); }
        tris.Add(baseIdx + 0); tris.Add(baseIdx + 1); tris.Add(baseIdx + 2);
        tris.Add(baseIdx + 0); tris.Add(baseIdx + 2); tris.Add(baseIdx + 3);
    }
}