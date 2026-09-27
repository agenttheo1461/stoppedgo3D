#if UNITY_EDITOR
using System.Collections.Generic;
using System.IO;
using UnityEditor;
using UnityEngine;

/// <summary>
/// Bus prefab renders with a TRANSPARENT background, front view turned slightly to the bus's right.
///
///  Tools > Bus > Render Prefab Previews (front-right)
///      every prefab in BUS MODELS -> 1024x1024 PNG in &lt;project&gt;/BusPreviews/ (outside Assets).
///  Tools > Bus > Generate Main Menu Bus Icons (from FleetRoster)
///      every series in the FleetRoster asset -> Assets/Resources/SeriesIcons/&lt;seriesName&gt;.png (512x512).
///      The main menu shows these next to the selected bus (falls back to the old Resources/SeriesIcons/&lt;busType&gt;.png).
///
/// Transparency: each bus is rendered twice, on black and on white; alpha = 1 - (white - black), colour = black / alpha.
/// That gives clean anti-aliased edges (no fringe of background colour). The camera is fitted to the bus's own visible
/// outline, so a 60 ft artic fills the frame just like a 40 ft bus.
/// Buses face -Z (front = -Z, right side = -X). If a picture comes out from the back or the left, change ViewDir.
/// </summary>
public static class BusPreviewRenderer
{
    private const string Folder = "Assets/ASSETS (1)/BUS MODELS";
    private const string IconFolder = "Assets/Resources/SeriesIcons";
    private static readonly Vector3 ViewDir = new Vector3(-0.34f, 0.21f, -0.94f);   // bus centre -> camera

    // ───────────────────────────── menu items ─────────────────────────────
    [MenuItem("Tools/Bus/Render Prefab Previews (front-right)")]
    private static void RenderAll()
    {
        string outDir = Path.Combine(Directory.GetParent(Application.dataPath).FullName, "BusPreviews");
        Directory.CreateDirectory(outDir);
        var guids = AssetDatabase.FindAssets("t:Prefab", new[] { Folder });
        var pru = MakePru();
        int done = 0, skipped = 0;
        try
        {
            for (int gi = 0; gi < guids.Length; gi++)
            {
                string path = AssetDatabase.GUIDToAssetPath(guids[gi]);
                string name = Path.GetFileNameWithoutExtension(path);
                if (path.Contains("(backup")) { skipped++; continue; }
                EditorUtility.DisplayProgressBar("Rendering bus previews", name, gi / (float)guids.Length);
                var prefab = AssetDatabase.LoadAssetAtPath<GameObject>(path);
                var tex = prefab != null ? RenderPrefabTransparent(pru, prefab, 1024) : null;
                if (tex == null) { skipped++; continue; }
                File.WriteAllBytes(Path.Combine(outDir, name + ".png"), tex.EncodeToPNG());
                Object.DestroyImmediate(tex);
                done++;
            }
        }
        finally { EditorUtility.ClearProgressBar(); pru.Cleanup(); }
        Debug.Log($"[BusPreview] Rendered {done} prefab(s) (transparent), skipped {skipped}. Saved to {outDir}");
        EditorUtility.RevealInFinder(outDir);
    }

    [MenuItem("Tools/Bus/Generate Main Menu Bus Icons (from FleetRoster)")]
    private static void GenerateMenuIcons()
    {
        var rosterGuids = AssetDatabase.FindAssets("t:FleetRosterData");
        if (rosterGuids.Length == 0) { Debug.LogError("[BusIcons] No FleetRosterData asset found."); return; }
        var roster = AssetDatabase.LoadAssetAtPath<FleetRosterData>(AssetDatabase.GUIDToAssetPath(rosterGuids[0]));
        Directory.CreateDirectory(Path.Combine(Directory.GetParent(Application.dataPath).FullName, IconFolder));

        var cache = new Dictionary<GameObject, byte[]>();   // one render per distinct prefab, reused across series that share it
        var pru = MakePru();
        int written = 0;
        var paths = new List<string>();
        try
        {
            for (int i = 0; i < roster.series.Count; i++)
            {
                var def = roster.series[i];
                if (def == null || def.prefab == null) continue;
                EditorUtility.DisplayProgressBar("Generating bus icons", def.seriesName, i / (float)roster.series.Count);
                if (!cache.TryGetValue(def.prefab, out var png))
                {
                    var tex = RenderPrefabTransparent(pru, def.prefab, 512);
                    if (tex == null) continue;
                    png = tex.EncodeToPNG();
                    Object.DestroyImmediate(tex);
                    cache[def.prefab] = png;
                }
                string safe = string.Concat(def.seriesName.Split(Path.GetInvalidFileNameChars()));
                string assetPath = $"{IconFolder}/{safe}.png";
                File.WriteAllBytes(Path.Combine(Directory.GetParent(Application.dataPath).FullName, assetPath), png);
                paths.Add(assetPath);
                written++;
            }
        }
        finally { EditorUtility.ClearProgressBar(); pru.Cleanup(); }

        AssetDatabase.Refresh();
        foreach (var p in paths)
        {
            var ti = AssetImporter.GetAtPath(p) as TextureImporter;
            if (ti == null) continue;
            ti.textureType = TextureImporterType.Default;
            ti.alphaIsTransparency = true;
            ti.mipmapEnabled = false;
            ti.npotScale = TextureImporterNPOTScale.None;
            ti.SaveAndReimport();
        }
        Debug.Log($"[BusIcons] Wrote {written} series icon(s) ({cache.Count} distinct bus prefabs) to {IconFolder}.");
    }

    // ───────────────────────────── rendering ─────────────────────────────
    private static PreviewRenderUtility MakePru()
    {
        var pru = new PreviewRenderUtility();
        pru.cameraFieldOfView = 28f;
        pru.camera.clearFlags = CameraClearFlags.SolidColor;
        pru.ambientColor = new Color(0.55f, 0.55f, 0.6f, 1f);
        pru.lights[0].intensity = 1.3f;
        pru.lights[0].transform.rotation = Quaternion.Euler(40f, 35f, 0f);
        pru.lights[1].intensity = 0.7f;
        return pru;
    }

    /// <summary>Instantiates the prefab, fits the camera to its visible outline, renders it on transparent, destroys it.</summary>
    private static Texture2D RenderPrefabTransparent(PreviewRenderUtility pru, GameObject prefab, int size)
    {
        var inst = Object.Instantiate(prefab);
        try
        {
            var rends = inst.GetComponentsInChildren<Renderer>(true);
            if (rends.Length == 0) return null;
            pru.AddSingleGO(inst);

            var corners = new List<Vector3>();
            foreach (var r in rends)
            {
                Bounds rb = r.bounds;
                for (int c = 0; c < 8; c++)
                    corners.Add(rb.center + Vector3.Scale(rb.extents, new Vector3((c & 1) == 0 ? -1 : 1, (c & 2) == 0 ? -1 : 1, (c & 4) == 0 ? -1 : 1)));
            }
            Bounds b = new Bounds(corners[0], Vector3.zero);
            foreach (var c in corners) b.Encapsulate(c);

            Vector3 fwd = -ViewDir.normalized;
            Vector3 right = Vector3.Cross(Vector3.up, fwd).normalized;
            Vector3 up = Vector3.Cross(fwd, right).normalized;

            float minX = float.MaxValue, maxX = float.MinValue, minY = float.MaxValue, maxY = float.MinValue;
            foreach (var c in corners)
            {
                Vector3 rel = c - b.center;
                float x = Vector3.Dot(rel, right), y = Vector3.Dot(rel, up);
                minX = Mathf.Min(minX, x); maxX = Mathf.Max(maxX, x);
                minY = Mathf.Min(minY, y); maxY = Mathf.Max(maxY, y);
            }
            Vector3 target = b.center + right * ((minX + maxX) * 0.5f) + up * ((minY + maxY) * 0.5f);

            float tanHalf = Mathf.Tan(pru.cameraFieldOfView * 0.5f * Mathf.Deg2Rad);
            float dist = 0f;
            foreach (var c in corners)
            {
                Vector3 rel = c - target;
                float need = Mathf.Max(Mathf.Abs(Vector3.Dot(rel, right)), Mathf.Abs(Vector3.Dot(rel, up))) / tanHalf - Vector3.Dot(rel, fwd);
                dist = Mathf.Max(dist, need);
            }
            dist *= 1.08f;

            var cam = pru.camera;
            cam.nearClipPlane = 0.1f;
            cam.farClipPlane = dist * 4f + b.size.magnitude;
            cam.transform.position = target - fwd * dist;
            cam.transform.LookAt(target);

            // Pass 1: transparent render, measure the visible outline, then re-centre and zoom to fill ~94% of the frame.
            Texture2D tex = RenderTransparent(pru, size);
            if (TryGetSilhouette(tex, out int minPx, out int maxPx, out int minPy, out int maxPy))
            {
                float cxPx = (minPx + maxPx) * 0.5f, cyPx = (minPy + maxPy) * 0.5f;
                float sizePx = Mathf.Max(maxPx - minPx, maxPy - minPy);
                float worldPerPx = 2f * dist * tanHalf / size;
                target += right * ((cxPx - size * 0.5f) * worldPerPx) + up * ((cyPx - size * 0.5f) * worldPerPx);
                dist *= Mathf.Clamp(sizePx / (0.94f * size), 0.2f, 2f);
                cam.transform.position = target - fwd * dist;
                cam.transform.LookAt(target);
                Object.DestroyImmediate(tex);
                tex = RenderTransparent(pru, size);
            }
            return tex;
        }
        finally { if (inst != null) Object.DestroyImmediate(inst); }
    }

    private static Texture2D RenderOnBackground(PreviewRenderUtility pru, int size, Color bg)
    {
        pru.camera.backgroundColor = bg;
        pru.BeginStaticPreview(new Rect(0, 0, size, size));
        pru.Render(true);
        return pru.EndStaticPreview();
    }

    /// <summary>Render on black and on white, then solve for alpha and un-premultiplied colour.</summary>
    private static Texture2D RenderTransparent(PreviewRenderUtility pru, int size)
    {
        var black = RenderOnBackground(pru, size, new Color(0, 0, 0, 1));
        var white = RenderOnBackground(pru, size, new Color(1, 1, 1, 1));
        var bp = black.GetPixels();
        var wp = white.GetPixels();
        Object.DestroyImmediate(black);
        Object.DestroyImmediate(white);
        var outp = new Color[bp.Length];
        for (int i = 0; i < bp.Length; i++)
        {
            float a = Mathf.Clamp01(1f - ((wp[i].r - bp[i].r) + (wp[i].g - bp[i].g) + (wp[i].b - bp[i].b)) / 3f);
            outp[i] = a > 0.004f
                ? new Color(Mathf.Clamp01(bp[i].r / a), Mathf.Clamp01(bp[i].g / a), Mathf.Clamp01(bp[i].b / a), a)
                : new Color(0, 0, 0, 0);
        }
        var tex = new Texture2D(size, size, TextureFormat.RGBA32, false);
        tex.SetPixels(outp);
        tex.Apply();
        return tex;
    }

    /// <summary>Bounding box (pixels, y up) of everything with alpha above a small threshold.</summary>
    private static bool TryGetSilhouette(Texture2D tex, out int minX, out int maxX, out int minY, out int maxY)
    {
        minX = minY = int.MaxValue; maxX = maxY = int.MinValue;
        var px = tex.GetPixels32();
        for (int y = 0; y < tex.height; y++)
            for (int x = 0; x < tex.width; x++)
                if (px[y * tex.width + x].a > 12)
                {
                    if (x < minX) minX = x; if (x > maxX) maxX = x;
                    if (y < minY) minY = y; if (y > maxY) maxY = y;
                }
        return maxX > minX && maxY > minY;
    }
}
#endif
