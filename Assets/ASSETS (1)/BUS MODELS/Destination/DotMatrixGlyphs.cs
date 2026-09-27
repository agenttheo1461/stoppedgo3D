using System.Collections.Generic;

// ═══════════════════════════════════════════════════════════════════════════════
//  DotMatrixGlyphs
//
//  A fully hand-drawn 5-column x 7-row bitmap font, built from scratch for
//  Headway's in-house dot-matrix boards. Not traced or copied from any
//  existing product, font file, or real transit sign asset -- just a plain
//  5x7 dot grid per character, which is the standard resolution class for
//  this style of display (same *category* of grid used by countless LED
//  signs in general, but the specific pixel patterns below are original).
//
//  FORMAT
//  ──────
//  Each glyph is 7 strings, top row first, 5 characters each.
//  '#' = lit dot, '.' = unlit dot.
//
//  Coverage: A-Z, 0-9, space, and a small punctuation set: - . & / '
//  That's everything a destination/route/qualifier board actually needs.
//  If something's missing, add a new 7-row entry below -- nothing else
//  in the pipeline needs to change.
//
//  EDITING
//  ───────
//  If a letter reads badly at bus-board size, just edit its dot pattern
//  directly below. Each row is a plain string, so this is a straight
//  text edit, no external tool needed.
// ═══════════════════════════════════════════════════════════════════════════════

public static class DotMatrixGlyphs
{
    // Every glyph below is still hand-drawn at 5x7 with 1px strokes (easy
    // to eyeball-edit as plain text) -- StrokeScale then nearest-neighbor
    // blows each of those single dots up into an NxN block before anything
    // else ever sees it. A vertical leg that was 1 column wide in the raw
    // pattern (e.g. 'H') comes out StrokeScale columns wide in the pattern
    // everyone actually uses, same for horizontal bars, corners, diagonals
    // -- the whole glyph gets uniformly bolder instead of just one stroke
    // getting special-cased.

    public const int StrokeScale = 3;

    // Raw hand-drawn source, 5 wide x 7 tall, 1px strokes. Edit THESE if a
    // letter reads badly -- the scaled-up version used everywhere else is
    // derived from this automatically at class load.
    public const int RawGlyphWidth  = 5;
    public const int RawGlyphHeight = 7;

    // Scaled dimensions actually exposed/used by DotMatrixText.
    public static readonly int GlyphWidth  = RawGlyphWidth  * StrokeScale;
    public static readonly int GlyphHeight = RawGlyphHeight * StrokeScale;

    private static readonly Dictionary<char, string[]> _rawGlyphs = new Dictionary<char, string[]>
    {
        [' '] = new[]
        {
            ".....",
            ".....",
            ".....",
            ".....",
            ".....",
            ".....",
            ".....",
        },

        ['A'] = new[]
        {
            "..#..",
            ".#.#.",
            "#...#",
            "#...#",
            "#####",
            "#...#",
            "#...#",
        },
        ['B'] = new[]
        {
            "####.",
            "#...#",
            "#...#",
            "####.",
            "#...#",
            "#...#",
            "####.",
        },
        ['C'] = new[]
        {
            ".####",
            "#....",
            "#....",
            "#....",
            "#....",
            "#....",
            ".####",
        },
        ['D'] = new[]
        {
            "####.",
            "#...#",
            "#...#",
            "#...#",
            "#...#",
            "#...#",
            "####.",
        },
        ['E'] = new[]
        {
            "#####",
            "#....",
            "#....",
            "####.",
            "#....",
            "#....",
            "#####",
        },
        ['F'] = new[]
        {
            "#####",
            "#....",
            "#....",
            "####.",
            "#....",
            "#....",
            "#....",
        },
        ['G'] = new[]
        {
            ".####",
            "#....",
            "#....",
            "#.###",
            "#...#",
            "#...#",
            ".####",
        },
        ['H'] = new[]
        {
            "#...#",
            "#...#",
            "#...#",
            "#####",
            "#...#",
            "#...#",
            "#...#",
        },
        ['I'] = new[]
        {
            "#####",
            "..#..",
            "..#..",
            "..#..",
            "..#..",
            "..#..",
            "#####",
        },
        ['J'] = new[]
        {
            "..###",
            "...#.",
            "...#.",
            "...#.",
            "#..#.",
            "#..#.",
            ".##..",
        },
        ['K'] = new[]
        {
            "#...#",
            "#..#.",
            "#.#..",
            "##...",
            "#.#..",
            "#..#.",
            "#...#",
        },
        ['L'] = new[]
        {
            "#....",
            "#....",
            "#....",
            "#....",
            "#....",
            "#....",
            "#####",
        },
        ['M'] = new[]
        {
            "#...#",
            "##.##",
            "#.#.#",
            "#.#.#",
            "#...#",
            "#...#",
            "#...#",
        },
        ['N'] = new[]
        {
            "#...#",
            "##..#",
            "#.#.#",
            "#.#.#",
            "#..##",
            "#...#",
            "#...#",
        },
        ['O'] = new[]
        {
            ".###.",
            "#...#",
            "#...#",
            "#...#",
            "#...#",
            "#...#",
            ".###.",
        },
        ['P'] = new[]
        {
            "####.",
            "#...#",
            "#...#",
            "####.",
            "#....",
            "#....",
            "#....",
        },
        ['Q'] = new[]
        {
            ".###.",
            "#...#",
            "#...#",
            "#...#",
            "#.#.#",
            "#..#.",
            ".##.#",
        },
        ['R'] = new[]
        {
            "####.",
            "#...#",
            "#...#",
            "####.",
            "#.#..",
            "#..#.",
            "#...#",
        },
        ['S'] = new[]
        {
            ".####",
            "#....",
            "#....",
            ".###.",
            "....#",
            "....#",
            "####.",
        },
        ['T'] = new[]
        {
            "#####",
            "..#..",
            "..#..",
            "..#..",
            "..#..",
            "..#..",
            "..#..",
        },
        ['U'] = new[]
        {
            "#...#",
            "#...#",
            "#...#",
            "#...#",
            "#...#",
            "#...#",
            ".###.",
        },
        ['V'] = new[]
        {
            "#...#",
            "#...#",
            "#...#",
            "#...#",
            ".#.#.",
            ".#.#.",
            "..#..",
        },
        ['W'] = new[]
        {
            "#...#",
            "#...#",
            "#...#",
            "#.#.#",
            "#.#.#",
            "##.##",
            "#...#",
        },
        ['X'] = new[]
        {
            "#...#",
            ".#.#.",
            "..#..",
            "..#..",
            "..#..",
            ".#.#.",
            "#...#",
        },
        ['Y'] = new[]
        {
            "#...#",
            ".#.#.",
            "..#..",
            "..#..",
            "..#..",
            "..#..",
            "..#..",
        },
        ['Z'] = new[]
        {
            "#####",
            "....#",
            "...#.",
            "..#..",
            ".#...",
            "#....",
            "#####",
        },

        ['0'] = new[]
        {
            ".###.",
            "#...#",
            "#..##",
            "#.#.#",
            "##..#",
            "#...#",
            ".###.",
        },
        ['1'] = new[]
        {
            "..#..",
            ".##..",
            "..#..",
            "..#..",
            "..#..",
            "..#..",
            ".###.",
        },
        ['2'] = new[]
        {
            ".###.",
            "#...#",
            "....#",
            "...#.",
            "..#..",
            ".#...",
            "#####",
        },
        ['3'] = new[]
        {
            "####.",
            "....#",
            "....#",
            "..##.",
            "....#",
            "....#",
            "####.",
        },
        ['4'] = new[]
        {
            "...#.",
            "..##.",
            ".#.#.",
            "#..#.",
            "#####",
            "...#.",
            "...#.",
        },
        ['5'] = new[]
        {
            "#####",
            "#....",
            "####.",
            "....#",
            "....#",
            "#...#",
            ".###.",
        },
        ['6'] = new[]
        {
            "..##.",
            ".#...",
            "#....",
            "####.",
            "#...#",
            "#...#",
            ".###.",
        },
        ['7'] = new[]
        {
            "#####",
            "....#",
            "...#.",
            "..#..",
            ".#...",
            ".#...",
            ".#...",
        },
        ['8'] = new[]
        {
            ".###.",
            "#...#",
            "#...#",
            ".###.",
            "#...#",
            "#...#",
            ".###.",
        },
        ['9'] = new[]
        {
            ".###.",
            "#...#",
            "#...#",
            ".####",
            "....#",
            "...#.",
            ".##..",
        },

        ['-'] = new[]
        {
            ".....",
            ".....",
            ".....",
            "#####",
            ".....",
            ".....",
            ".....",
        },
        ['.'] = new[]
        {
            ".....",
            ".....",
            ".....",
            ".....",
            ".....",
            ".##..",
            ".##..",
        },
        ['&'] = new[]
        {
            ".##..",
            "#..#.",
            "#.#..",
            ".#...",
            "#.#.#",
            "#..#.",
            ".##.#",
        },
        ['/'] = new[]
        {
            "....#",
            "...#.",
            "...#.",
            "..#..",
            ".#...",
            ".#...",
            "#....",
        },
        ['\''] = new[]
        {
            ".##..",
            ".##..",
            "..#..",
            ".....",
            ".....",
            ".....",
            ".....",
        },
    };

    // Built once at class load: every raw 5x7 pattern above, nearest-
    // neighbor blown up by StrokeScale into the GlyphWidth x GlyphHeight
    // pattern actually used for rendering.
    private static readonly Dictionary<char, string[]> _glyphs = BuildScaledGlyphs();

    private static Dictionary<char, string[]> BuildScaledGlyphs()
    {
        var scaled = new Dictionary<char, string[]>();
        foreach (var kv in _rawGlyphs)
            scaled[kv.Key] = ScalePattern(kv.Value, StrokeScale);
        return scaled;
    }

    private static string[] ScalePattern(string[] raw, int scale)
    {
        var outRows = new string[RawGlyphHeight * scale];
        for (int rawRow = 0; rawRow < RawGlyphHeight; rawRow++)
        {
            var sb = new System.Text.StringBuilder(RawGlyphWidth * scale);
            for (int rawCol = 0; rawCol < RawGlyphWidth; rawCol++)
            {
                char pixel = raw[rawRow][rawCol];
                for (int i = 0; i < scale; i++) sb.Append(pixel);
            }
            string scaledRow = sb.ToString();
            for (int i = 0; i < scale; i++)
                outRows[rawRow * scale + i] = scaledRow;
        }
        return outRows;
    }

    /// <summary>
    /// Returns the stroke-scaled dot pattern for a character (GlyphWidth x
    /// GlyphHeight). Unknown characters fall back to a blank (space) glyph
    /// so a missing symbol never throws or corrupts the board layout --
    /// it just leaves a gap.
    /// </summary>
    public static string[] GetPattern(char c)
    {
        char upper = char.ToUpperInvariant(c);
        if (_glyphs.TryGetValue(upper, out var pattern))
            return pattern;

        return _glyphs[' '];
    }

    public static bool IsSupported(char c) => _rawGlyphs.ContainsKey(char.ToUpperInvariant(c));
}