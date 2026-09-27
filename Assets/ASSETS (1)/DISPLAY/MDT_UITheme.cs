using UnityEngine;
using System.Collections.Generic;

// ═══════════════════════════════════════════════════════════════════════════════
//  MDT_UITheme  —  Shared design tokens & primitive drawing helpers
//
//  Single source of truth for all MDT UI panels (LiveMap, UIController, etc.).
//  Every color, radius, and font size lives here. Each panel calls
//  MDT_UITheme.Draw* helpers rather than reproducing the same GL primitives.
// ═══════════════════════════════════════════════════════════════════════════════
public static class MDT_UITheme
{
    // ── Palette ───────────────────────────────────────────────────────────────
    // Modern black/gray theme — neutral, soft, low-chroma. "Highlights" (selection,
    // accents, hover states) are all lighter GRAYS rather than a saturated hue, so
    // the whole UI reads as one cohesive monochrome surface, with soft drop shadows
    // providing depth instead of color. Status/semantic colors (LEDs, level bars,
    // route badges) intentionally stay saturated since those convey functional
    // meaning, not decoration.

    // Backgrounds
    public static readonly Color BGDeep      = new Color(0.075f, 0.075f, 0.080f, 0.98f);  // main panel
    public static readonly Color BGMid       = new Color(0.100f, 0.100f, 0.106f, 1.00f);  // map / list area
    public static readonly Color BGHeader    = new Color(0.050f, 0.050f, 0.054f, 1.00f);  // header bar
    public static readonly Color BGFooter    = new Color(0.050f, 0.050f, 0.054f, 1.00f);  // footer / legend
    public static readonly Color BGRowEven   = new Color(0.108f, 0.108f, 0.114f, 1.00f);
    public static readonly Color BGRowOdd    = new Color(0.090f, 0.090f, 0.096f, 1.00f);
    public static readonly Color BGRowHover  = new Color(0.190f, 0.190f, 0.200f, 1.00f);
    public static readonly Color BGPill      = new Color(0.130f, 0.130f, 0.138f, 1.00f);
    public static readonly Color BGPillSel   = new Color(0.400f, 0.400f, 0.415f, 1.00f);  // lighter-gray highlight
    public static readonly Color BGDirSel    = new Color(0.340f, 0.340f, 0.355f, 1.00f);  // lighter-gray highlight
    public static readonly Color BGButton    = new Color(0.145f, 0.145f, 0.154f, 1.00f);
    public static readonly Color BGTooltip   = new Color(0.060f, 0.060f, 0.065f, 0.97f);

    // Borders & chrome
    public static readonly Color BevelHi     = new Color(1.0f, 1.0f, 1.0f, 0.10f);  // top/left highlight
    public static readonly Color BevelShadow = new Color(0.0f, 0.0f, 0.0f, 0.40f);  // bottom/right shadow
    public static readonly Color BorderAccent= new Color(0.62f, 0.62f, 0.65f, 0.35f); // outer accent ring — lighter gray
    public static readonly Color Divider     = new Color(0.20f, 0.20f, 0.21f, 1.00f);
    public static readonly Color Grid        = new Color(0.30f, 0.30f, 0.32f, 0.22f);

    // Semantic text
    public static readonly Color TextPrimary = new Color(0.94f, 0.94f, 0.95f, 0.96f);
    public static readonly Color TextSecond  = new Color(0.68f, 0.68f, 0.70f, 0.90f);
    public static readonly Color TextDim     = new Color(0.48f, 0.48f, 0.50f, 0.85f);
    public static readonly Color TextCyan    = new Color(0.80f, 0.80f, 0.83f, 1.00f);  // lighter-gray highlight text
    public static readonly Color TextAmber   = new Color(0.98f, 0.75f, 0.20f, 1.00f);
    public static readonly Color TextGreen   = new Color(0.22f, 0.92f, 0.50f, 1.00f);
    public static readonly Color TextRed     = new Color(1.00f, 0.32f, 0.32f, 1.00f);
    public static readonly Color TextWhite   = Color.white;

    // Status LED colors
    public static readonly Color LEDGreen    = new Color(0.20f, 1.00f, 0.40f, 1.00f);
    public static readonly Color LEDAmber    = new Color(1.00f, 0.72f, 0.10f, 1.00f);
    public static readonly Color LEDRed      = new Color(1.00f, 0.28f, 0.28f, 1.00f);
    public static readonly Color LEDOff      = new Color(0.25f, 0.30f, 0.38f, 0.80f);

    // Stop dot colors
    public static readonly Color StopDot     = new Color(0.50f, 0.70f, 0.95f, 0.90f);
    public static readonly Color TermDot     = new Color(1.00f, 0.88f, 0.22f, 1.00f);
    public static readonly Color TermGlow    = new Color(1.00f, 0.85f, 0.10f, 0.40f);

    // Bus chip
    public static readonly Color ChipPlayer  = new Color(1.00f, 0.95f, 0.20f, 1.00f); // yellow border
    public static readonly Color ChipShadow  = new Color(0.00f, 0.00f, 0.00f, 0.40f);

    // ── Mobile touch-target scaling (items 15/16) ─────────────────────────────
    // Shared with every IMGUI panel in the project -- MobileTouchHUD already
    // had its own private copy of this exact formula (Screen.height/1080,
    // clamped 0.9-2.4); DriverConsole/BusDashboardHUD/MDT_LiveMap had no
    // scaling at all, drawing the same fixed pixel Rects on a phone as on a
    // desktop monitor. Centralized here so all four panels apply the same
    // scale and the same minimum-touch-target floor consistently instead of
    // each hand-rolling (or not hand-rolling) their own.
    public const float MinUIScale = 0.9f;
    public const float MaxUIScale = 2.4f;
    public static float UIScale => Mathf.Clamp(Screen.height / 1080f, MinUIScale, MaxUIScale);

    // Apple/Android guidance settles around 44pt; using 44 raw pixels at the
    // 1080p reference height (same reference UIScale is computed against)
    // and letting it scale with everything else keeps it proportionate
    // rather than a fixed screen-space floor that'd be the wrong size on a
    // different resolution.
    public const float MinTouchTargetBase = 44f;

    /// <summary>Scales a base (1080p-reference) size by UIScale, then floors
    /// it at MinTouchTargetBase*UIScale -- for anything meant to be tapped.
    /// Non-interactive elements (labels, dividers) should just multiply by
    /// UIScale directly instead of calling this, since they have no minimum
    /// tap-target requirement.</summary>
    public static float ScaledTouchSize(float baseSize) => Mathf.Max(baseSize * UIScale, MinTouchTargetBase * UIScale);

    // ── Rounded-rect tuning ────────────────────────────────────────────────────
    public const float RadiusPanel  = 16f;
    public const float RadiusRow    = 8f;
    public const float RadiusPill   = 8f;
    public const float RadiusChip   = 6f;
    public const float RadiusButton = 8f;
    private const int  Supersample = 4; // [BUMPED 3->4] More AA samples per output pixel
    // (9 -> 16 sub-samples) for smoother rounded-corner curves -- the visible
    // "pixels" on rounded edges was an antialiasing-quality issue, not a
    // resolution one. Output texture dimensions are still exactly w×h either
    // way (the supersampled buffer only exists transiently during
    // generation, then gets box-downsampled and thrown away) -- so this adds
    // zero bytes to the texture cache and doesn't reopen the RAM issue just
    // fixed. Cost is purely a one-time ~1.8x more pixel math per cache MISS
    // (not per draw call -- cached rects don't regenerate), which is
    // negligible at UI-element sizes.

    // ── Texture cache (shared across all panels) ──────────────────────────────
    // [FIX — the actual RAM leak] This was an UNBOUNDED Dictionary<Color,
    // Texture2D> with no eviction at all, unlike _roundedCache below which
    // already had an LRU cap. Any caller building a GUIStyle via SetBg()
    // with a COMPUTED color (an animated tint, an HSV shift, a per-bus
    // color, a hover/active blend done in the caller rather than the fixed
    // Lerp calls in MakeButton) mints a brand-new exact-float Color key
    // every single call. Every one of those became a permanent 1x1
    // Texture2D that never got freed -- and each Texture2D carries real
    // Unity per-object/native overhead well beyond its 4 bytes of pixel
    // data, so tens of thousands of these (easily reached over a play
    // session with any per-frame or per-instance color variation) is
    // exactly the kind of stable, plateaued few-hundred-MB-to-1GB growth
    // this was doing. Bounded with the same LRU pattern _roundedCache
    // already uses below -- smaller cap since these are simple solid
    // colors, not full rounded-rect renders, so there's no real cost to
    // evicting and regenerating one on the rare cache-miss.
    private static readonly Dictionary<Color, Texture2D> _texCache = new Dictionary<Color, Texture2D>(64);
    private static readonly LinkedList<Color> _texCacheLRU = new LinkedList<Color>();
    private const int TexCacheMaxSize = 128;
    
    // Self-cleaning LRU cache with explicit clearing support and safe capacity
    private static readonly Dictionary<long, Texture2D> _roundedCache = new Dictionary<long, Texture2D>(64);
    private static readonly LinkedList<long> _roundedCacheLRU = new LinkedList<long>();
    private const int SafeMaxCacheSize = 512;
    
    // Keys currently baked into a live GUIStyle's background — these must
    // never be destroyed by LRU eviction while pinned, since eviction has no
    // way to know a GUIStyle elsewhere is still holding the reference.
    // Deliberately NOT a full unpin/ref-count system: pins are only ever
    // added (never removed), which is safe and bounded as long as
    // SetBgRounded is called a small, fixed number of times (built-once
    // static button skins) rather than per-frame/per-instance with computed
    // colors. If a caller needs dynamic/computed-color buttons, it should
    // draw with DrawRoundedRectBordered() each frame (see SlotButton in
    // BusDashboardHUD) instead of baking a GUIStyle -- that path already
    // goes through the ordinary evictable cache correctly, since nothing
    // holds the texture reference past the current draw call.
    private static readonly HashSet<long> _pinnedRoundedKeys = new HashSet<long>();

    private static Texture2D _white;

    public static Texture2D White
    {
        get
        {
            if (_white == null) { _white = new Texture2D(1,1); _white.SetPixel(0,0,Color.white); _white.Apply(); }
            return _white;
        }
    }

    /// <summary>
    /// Completely clears and destroys all cached textures to free up memory.
    /// Call this when turning off the engine or unloading the scene.
    /// </summary>
    public static void ClearCache()
    {
        foreach (var tex in _texCache.Values)
        {
            if (tex != null) Object.Destroy(tex);
        }
        _texCache.Clear();
        _texCacheLRU.Clear();

        foreach (var tex in _roundedCache.Values)
        {
            if (tex != null) Object.Destroy(tex);
        }
        _roundedCache.Clear();
        _roundedCacheLRU.Clear();
        _pinnedRoundedKeys.Clear();
        _sliceStyles.Clear(); // they hold the textures just destroyed above

        if (_white != null)
        {
            Object.Destroy(_white);
            _white = null;
        }
    }

    public static Texture2D Tex(Color c)
    {
        if (_texCache.TryGetValue(c, out var t) && t != null)
        {
            _texCacheLRU.Remove(c);
            _texCacheLRU.AddLast(c);
            return t;
        }

        if (_texCache.Count >= TexCacheMaxSize)
        {
            var oldestNode = _texCacheLRU.First;
            if (oldestNode != null)
            {
                Color oldestKey = oldestNode.Value;
                if (_texCache.TryGetValue(oldestKey, out var oldTex))
                {
                    if (oldTex != null) Object.Destroy(oldTex);
                    _texCache.Remove(oldestKey);
                }
                _texCacheLRU.RemoveFirst();
            }
        }

        t = new Texture2D(1, 1);
        t.SetPixel(0, 0, c);
        t.Apply();
        _texCache[c] = t;
        _texCacheLRU.AddLast(c);
        return t;
    }

    // ── Primitive drawing ─────────────────────────────────────────────────────
    public static void DrawRect(Rect r, Color col)
    {
        GUI.color = col;
        GUI.DrawTexture(r, White);
        GUI.color = Color.white;
    }

    public static void DrawLine(Vector2 a, Vector2 b, Color col, float thickness)
    {
        Vector2 d = b - a;
        float angle = Mathf.Atan2(d.y, d.x) * Mathf.Rad2Deg;
        float len   = d.magnitude;
        if (len < 0.5f) return;

        var saved = GUI.matrix;
        GUIUtility.RotateAroundPivot(angle, a);
        GUI.color = col;
        GUI.DrawTexture(new Rect(a.x, a.y - thickness * 0.5f, len, thickness), White);
        GUI.color  = Color.white;
        GUI.matrix = saved;
    }

    // ── Rounded-rect generation ──────────────────────────────────────────────
    private static long RoundedKey(int w, int h, int radius, Color fill, Color border, int borderW)
    {
        unchecked
        {
            long key = 17;
            key = key * 31 + w;
            key = key * 31 + h;
            key = key * 31 + radius;
            key = key * 31 + borderW;
            key = key * 31 + fill.GetHashCode();
            key = key * 31 + border.GetHashCode();
            return key;
        }
    }

    private static bool InsideRoundedRect(float x, float y, float w, float h, float r)
    {
        float dx = Mathf.Max(Mathf.Abs(x - w * 0.5f) - (w * 0.5f - r), 0f);
        float dy = Mathf.Max(Mathf.Abs(y - h * 0.5f) - (h * 0.5f - r), 0f);
        return (dx * dx + dy * dy) <= r * r;
    }

    /// <summary>
    /// Builds (or fetches from cache) a supersampled-and-downsampled rounded-rect
    /// texture with dimension snapping to stabilize cache reuse.
    /// </summary>
    public static Texture2D RoundedRect(int w, int h, float radius, Color fill, Color border = default, int borderW = 0, bool pin = false)
    {
        // Snap dimensions to nearest 4 pixels to prevent cache churn from minor layout shifts
        w = Mathf.Max(Mathf.RoundToInt(w / 4f) * 4, 4);
        h = Mathf.Max(Mathf.RoundToInt(h / 4f) * 4, 4);
        radius = Mathf.Clamp(radius, 0f, Mathf.Min(w, h) * 0.5f);

        long key = RoundedKey(w, h, Mathf.RoundToInt(radius * 4f), fill, border, borderW);
        
        if (_roundedCache.TryGetValue(key, out var cached) && cached != null)
        {
            _roundedCacheLRU.Remove(key);
            _roundedCacheLRU.AddLast(key);
            if (pin) _pinnedRoundedKeys.Add(key);
            return cached;
        }

        // Evict oldest UNPINNED entry if cache is full. Pinned entries are
        // skipped over rather than destroyed -- see _pinnedRoundedKeys.
        if (_roundedCache.Count >= SafeMaxCacheSize)
        {
            var node = _roundedCacheLRU.First;
            while (node != null && _pinnedRoundedKeys.Contains(node.Value))
                node = node.Next;

            if (node != null)
            {
                long oldestKey = node.Value;
                if (_roundedCache.TryGetValue(oldestKey, out var oldTex))
                {
                    if (oldTex != null) Object.Destroy(oldTex);
                    _roundedCache.Remove(oldestKey);
                }
                _roundedCacheLRU.Remove(node);
            }
            // If every entry is pinned, the cache is allowed to grow past
            // SafeMaxCacheSize rather than corrupt a live GUIStyle -- this
            // should only happen if something is pinning far more distinct
            // button skins than the app actually has, which is itself a
            // sign a caller should switch to the per-frame draw pattern.
        }

        var tex = RenderRoundedRect(w, h, radius, fill, border, borderW);

        _roundedCache[key] = tex;
        _roundedCacheLRU.AddLast(key);
        if (pin) _pinnedRoundedKeys.Add(key);
        return tex;
    }

    /// <summary>
    /// Pure pixel-generation for a supersampled-and-downsampled rounded rect.
    /// No cache bookkeeping -- shared by the cached (RoundedRect) and
    /// uncached (RoundedRectUncached) entry points so the two don't drift.
    /// </summary>
    private static Texture2D RenderRoundedRect(int w, int h, float radius, Color fill, Color border, int borderW)
    {
        int ss = Supersample;
        int sw = w * ss, sh = h * ss;
        float sr = radius * ss;

        var px = new Color32[sw * sh];
        Color32 fillC   = fill;
        Color32 borderC = border;
        bool hasBorder  = borderW > 0 && border.a > 0f;
        float sBorderW  = borderW * ss;

        for (int y = 0; y < sh; y++)
        {
            for (int x = 0; x < sw; x++)
            {
                bool inOuter = InsideRoundedRect(x + 0.5f, y + 0.5f, sw, sh, sr);
                if (!inOuter) { px[y * sw + x] = new Color32(0, 0, 0, 0); continue; }

                if (hasBorder)
                {
                    bool inInner = InsideRoundedRect(x + 0.5f, y + 0.5f, sw, sh, sr - sBorderW)
                                   && x + 0.5f >= sBorderW && x + 0.5f <= sw - sBorderW
                                   && y + 0.5f >= sBorderW && y + 0.5f <= sh - sBorderW;
                    px[y * sw + x] = inInner ? fillC : borderC;
                }
                else
                {
                    px[y * sw + x] = fillC;
                }
            }
        }

        var outPx = new Color32[w * h];
        for (int y = 0; y < h; y++)
        {
            for (int x = 0; x < w; x++)
            {
                int rA = 0, gA = 0, bA = 0, aA = 0;
                for (int sy = 0; sy < ss; sy++)
                {
                    for (int sx = 0; sx < ss; sx++)
                    {
                        var c = px[(y * ss + sy) * sw + (x * ss + sx)];
                        rA += c.r; gA += c.g; bA += c.b; aA += c.a;
                    }
                }
                int n = ss * ss;
                outPx[y * w + x] = new Color32((byte)(rA / n), (byte)(gA / n), (byte)(bA / n), (byte)(aA / n));
            }
        }

        var tex = new Texture2D(w, h, TextureFormat.RGBA32, false);
        tex.filterMode = FilterMode.Bilinear;
        tex.wrapMode   = TextureWrapMode.Clamp;
        tex.SetPixels32(outPx);
        tex.Apply();
        return tex;
    }

    // Large rounded rects (the timetable panel, its soft shadows, the grid inset) used to be rendered as ONE
    // texture the size of the rect at 4x supersampling -- e.g. a 1800x950 panel is a ~27M-pixel CPU render,
    // done a dozen times on first open (each shadow layer is its own size/alpha). That was the multi-second
    // stall. Big rects now draw as a 9-slice of a tiny cached texture: the corners come from the small
    // texture, the flat edges/interior are stretched, and the result is visually the same for a uniform
    // fill/border. Small rects (buttons, chips, cards) keep the exact per-size texture.
    private static readonly Dictionary<long, GUIStyle> _sliceStyles = new Dictionary<long, GUIStyle>(32);
    private const int SliceStyleCap = 64;

    private static bool TryDrawSliced(Rect r, float radius, Color fill, Color border, int borderW)
    {
        int cr   = Mathf.Max(1, Mathf.CeilToInt(radius));
        int size = Mathf.Max(Mathf.RoundToInt((cr * 2 + 8) / 4f) * 4, 4); // RoundedRect snaps to multiples of 4
        if (r.width <= size + 4f || r.height <= size + 4f) return false;   // small enough that the exact texture is cheap

        long key = RoundedKey(size, size, Mathf.RoundToInt(radius * 4f), fill, border, borderW);
        if (!_sliceStyles.TryGetValue(key, out var st) || st == null || st.normal.background == null)
        {
            // Bounded: a caller animating the colour of a big rect would otherwise mint a style per frame.
            if (_sliceStyles.Count >= SliceStyleCap) _sliceStyles.Clear();
            st = new GUIStyle { border = new RectOffset(cr, cr, cr, cr) };
            st.normal.background = RoundedRect(size, size, radius, fill, border, borderW);
            _sliceStyles[key] = st;
        }
        if (Event.current != null && Event.current.type == EventType.Repaint)
            st.Draw(r, GUIContent.none, false, false, false, false);
        return true;
    }

    public static void DrawRoundedRect(Rect r, float radius, Color col)
    {
        if (r.width < 1f || r.height < 1f) return;
        GUI.color = Color.white;
        if (TryDrawSliced(r, radius, col, default, 0)) return;
        var tex = RoundedRect(Mathf.RoundToInt(r.width), Mathf.RoundToInt(r.height), radius, col);
        GUI.color = Color.white;
        GUI.DrawTexture(r, tex);
    }

    public static void DrawRoundedRectBordered(Rect r, float radius, Color fill, Color border, int borderW = 1)
    {
        if (r.width < 1f || r.height < 1f) return;
        GUI.color = Color.white;
        if (TryDrawSliced(r, radius, fill, border, borderW)) return;
        var tex = RoundedRect(Mathf.RoundToInt(r.width), Mathf.RoundToInt(r.height), radius, fill, border, borderW);
        GUI.color = Color.white;
        GUI.DrawTexture(r, tex);
    }

    // ── Soft shadow ───────────────────────────────────────────────────────────
    public static void DrawSoftShadow(Rect r, float radius, float offsetY = 4f, float spread = 10f, int layers = 5, float baseAlpha = 0.09f)
    {
        for (int i = layers; i >= 1; i--)
        {
            float t    = i / (float)layers;
            float grow = spread * t;
            var sr = new Rect(r.x - grow * 0.5f, r.y - grow * 0.5f + offsetY, r.width + grow, r.height + grow);
            DrawRoundedRect(sr, radius + grow * 0.5f, new Color(0, 0, 0, baseAlpha * (1f - t * 0.35f) / layers * 2.2f));
        }
    }

    // ── Panel chrome ──────────────────────────────────────────────────────────
    public static void DrawPanel(Rect r)
    {
        float radius = RadiusPanel;
        DrawSoftShadow(r, radius, 6f, 22f, 6, 0.10f);
        DrawRoundedRectBordered(new Rect(r.x - 1, r.y - 1, r.width + 2, r.height + 2),
                                 radius + 1f, BGDeep, BorderAccent, 1);
        DrawRoundedRect(new Rect(r.x + radius * 0.4f, r.y, r.width - radius * 0.8f, 2), 1f, BevelHi);
        DrawRoundedRect(new Rect(r.x + radius * 0.4f, r.yMax - 2, r.width - radius * 0.8f, 2), 1f, BevelShadow);
    }

    public static void DrawInset(Rect r, Color bg)
    {
        DrawSoftShadow(r, RadiusRow, 3f, 10f, 4, 0.07f);
        DrawRoundedRectBordered(r, RadiusRow, bg, new Color(1, 1, 1, 0.05f), 1);
        DrawRect(new Rect(r.x + RadiusRow * 0.5f, r.y, r.width - RadiusRow, 3), new Color(0, 0, 0, 0.22f));
        DrawRect(new Rect(r.x + RadiusRow * 0.5f, r.yMax - 1, r.width - RadiusRow, 1), new Color(1, 1, 1, 0.04f));
    }

    // ── Header bar ────────────────────────────────────────────────────────────
    public static void DrawHeader(Rect r)
    {
        DrawRoundedRect(r, RadiusPanel * 0.6f, BGHeader);
        DrawRect(new Rect(r.xMax - r.width * 0.4f, r.y, r.width * 0.4f, r.height),
                 new Color(1f, 1f, 1f, 0.03f));
        DrawRect(new Rect(r.x, r.yMax - 1, r.width, 1), new Color(0.75f, 0.75f, 0.78f, 0.28f));
    }

    // ── LED indicator ─────────────────────────────────────────────────────────
    public static void DrawLED(Vector2 centre, float radius, Color col)
    {
        float glowSize = radius * 4f;
        DrawRoundedRect(new Rect(centre.x - radius * 2, centre.y - radius * 2, glowSize, glowSize),
                         glowSize * 0.5f, col * new Color(1,1,1,0.18f));
        float coreSize = radius * 2f;
        DrawRoundedRect(new Rect(centre.x - radius, centre.y - radius, coreSize, coreSize),
                         coreSize * 0.5f, col);
    }

    // ── Divider ───────────────────────────────────────────────────────────────
    public static void DrawDivider(float x, float y, float w)
    {
        DrawRect(new Rect(x, y, w, 1), Divider);
        DrawRect(new Rect(x, y + 1, w, 1), new Color(1,1,1,0.03f));
    }

    // ── Route badge pill ──────────────────────────────────────────────────────
    public static void DrawRouteBadge(Rect r, string text, Color routeColor, GUIStyle labelStyle)
    {
        Color bg = Color.Lerp(routeColor, Color.black, 0.45f);
        bg.a = 1f;
        Color border = Color.Lerp(routeColor, Color.white, 0.3f);
        DrawRoundedRectBordered(new Rect(r.x - 1, r.y - 1, r.width + 2, r.height + 2), RadiusPill, bg, border, 1);
        GUI.Label(r, text, labelStyle);
    }

    // ── Tooltip box ───────────────────────────────────────────────────────────
    public static void DrawTooltip(Vector2 pos, string text, GUIStyle style)
    {
        Vector2 size = style.CalcSize(new GUIContent(text));
        size.x = Mathf.Max(size.x + 16, 90);
        size.y += 10;

        float tx = pos.x + 14;
        float ty = pos.y - size.y * 0.5f;
        tx = Mathf.Min(tx, Screen.width  - size.x - 4);
        ty = Mathf.Max(ty, 4);
        ty = Mathf.Min(ty, Screen.height - size.y - 4);

        var r = new Rect(tx, ty, size.x, size.y);
        DrawRoundedRect(new Rect(r.x + 2, r.y + 2, r.width, r.height), RadiusRow, new Color(0,0,0,0.5f));
        DrawRoundedRectBordered(new Rect(r.x - 1, r.y - 1, r.width + 2, r.height + 2),
                                 RadiusRow + 1f, BGTooltip, new Color(0.65f, 0.65f, 0.68f, 0.45f), 1);
        GUI.Label(r, text, style);
    }

    // ── Scale bar (for the live map) ──────────────────────────────────────────
    public static void DrawScaleBar(Rect area, float worldUnitsVisible, GUIStyle labelStyle)
    {
        float[] niceVals = { 50, 100, 200, 500, 1000 };
        float px = area.width;

        float chosen = niceVals[0];
        foreach (float v in niceVals)
        {
            float frac = (v / worldUnitsVisible) * px;
            if (frac < px * 0.28f) chosen = v;
        }

        float barPx  = (chosen / worldUnitsVisible) * px;
        float bx     = area.x + 10;
        float by     = area.yMax - 18;

        DrawRect(new Rect(bx, by + 5, barPx, 2), new Color(1,1,1,0.45f));
        DrawRect(new Rect(bx,           by + 2, 1, 8), new Color(1,1,1,0.45f));
        DrawRect(new Rect(bx + barPx,   by + 2, 1, 8), new Color(1,1,1,0.45f));

        string label = chosen >= 1000 ? $"{chosen/1000:F0}km" : $"{chosen:F0}m";
        GUI.Label(new Rect(bx, by - 10, barPx, 14), label, labelStyle);
    }

    // ── Level bar (fuel / maintenance gauges) ─────────────────────────────────
    public static void DrawLevelBar(Rect r, float fraction, Color fillColor, string label, GUIStyle labelStyle)
    {
        fraction = Mathf.Clamp01(fraction);
        float radius = r.height * 0.5f;

        DrawRoundedRect(new Rect(r.x, r.y + 1, r.width, r.height), radius, new Color(0, 0, 0, 0.35f));
        DrawRoundedRectBordered(r, radius, new Color(0.045f, 0.065f, 0.10f, 1f), new Color(1, 1, 1, 0.06f), 1);

        float innerW = Mathf.Max(0f, r.width - 4f);
        float fillW  = Mathf.Max(r.height - 4f, innerW * fraction);
        if (fraction > 0.01f)
        {
            var fillRect = new Rect(r.x + 2, r.y + 2, fillW, r.height - 4f);
            DrawRoundedRect(fillRect, fillRect.height * 0.5f, fillColor);
            DrawRoundedRect(new Rect(fillRect.x, fillRect.y, fillRect.width, fillRect.height * 0.45f),
                             fillRect.height * 0.225f, new Color(1, 1, 1, 0.12f));
        }

        if (!string.IsNullOrEmpty(label))
            GUI.Label(r, label, labelStyle);
    }

    public static Color LevelColor(float fraction)
    {
        if (fraction > 0.5f) return TextGreen;
        if (fraction > 0.2f) return TextAmber;
        return TextRed;
    }

    // ── GUIStyle factories ────────────────────────────────────────────────────
    public static GUIStyle MakeLabel(int size, FontStyle fs, TextAnchor anchor, Color col)
    {
        var s = new GUIStyle(GUI.skin.label)
        {
            fontSize  = size,
            fontStyle = fs,
            alignment = anchor,
        };
        s.normal.textColor = col;
        return s;
    }

    public static GUIStyle MakeButton(Color bg, Color text, int size, FontStyle fs = FontStyle.Bold)
    {
        var s = new GUIStyle(GUI.skin.button)
        {
            fontSize  = size,
            fontStyle = fs,
            border    = new RectOffset(10,10,10,10),
            alignment = TextAnchor.MiddleCenter,
        };
        s.normal.textColor  = text;
        s.hover.textColor   = TextWhite;
        s.active.textColor  = TextPrimary;
        Color hoverBg  = Color.Lerp(bg, Color.white, 0.14f);
        Color activeBg = Color.Lerp(bg, Color.black, 0.18f);
        SetBgRounded(s, bg, hoverBg, activeBg, 48, 28, RadiusButton);
        return s;
    }

    public static void SetBg(GUIStyle s, Color normal, Color hover, Color active)
    {
        s.normal.background  = Tex(normal);
        s.hover.background   = Tex(hover);
        s.active.background  = Tex(active);
        s.focused.background = Tex(normal);
    }

    public static void SetBgRounded(GUIStyle s, Color normal, Color hover, Color active, int w, int h, float radius)
    {
        // [FIX] Bake through the shared cache with pin:true rather than a
        // fully uncached texture. Pinning keeps the RAM-bounding benefit of
        // the shared cache (repeated identical colors across multiple
        // buttons still dedupe to one texture) while guaranteeing eviction
        // never destroys a texture a live GUIStyle is still pointing at --
        // that was the actual cause of buttons rendering as flat boxes after
        // ~15s. A plain uncached texture per call would fix the box bug too,
        // but only safely if this is called a small fixed number of times;
        // if some caller ever starts building styles per-frame or per
        // instance with computed colors, uncached would leak unbounded.
        // Pinning degrades gracefully in that scenario instead -- see the
        // comment on _pinnedRoundedKeys.
        s.normal.background  = RoundedRect(w, h, radius, normal, pin: true);
        s.hover.background   = RoundedRect(w, h, radius, hover,  pin: true);
        s.active.background  = RoundedRect(w, h, radius, active, pin: true);
        s.focused.background = RoundedRect(w, h, radius, normal, pin: true);
    }

    /// <summary>
    /// Same rendering as RoundedRect(), but bypasses the shared cache
    /// entirely (no dedup, no eviction, no pin bookkeeping). Only use this
    /// for genuine one-offs you won't call again with the same
    /// size/color -- for anything baked into a GUIStyle, use RoundedRect
    /// with pin:true via SetBgRounded instead.
    /// </summary>
    public static Texture2D RoundedRectUncached(int w, int h, float radius, Color fill, Color border = default, int borderW = 0)
    {
        w = Mathf.Max(Mathf.RoundToInt(w / 4f) * 4, 4);
        h = Mathf.Max(Mathf.RoundToInt(h / 4f) * 4, 4);
        radius = Mathf.Clamp(radius, 0f, Mathf.Min(w, h) * 0.5f);
        return RenderRoundedRect(w, h, radius, fill, border, borderW);
    }
}