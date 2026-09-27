using System.Collections.Generic;
using UnityEngine;

// ═══════════════════════════════════════════════════════════════════════════════
//  NOTIFICATION TOAST
//
//  The universal in-game notification channel -- works identically on every
//  platform, no permissions, no new dependencies. Anything that wants to tell
//  the player something (shift-start countdowns, "your starred bus is
//  available", etc.) calls NotificationToast.Show(...) and it just appears as
//  a sliding banner, stacked with any others, auto-dismissing after a few
//  seconds. This is the fallback every platform gets even where a real OS
//  push notification isn't possible (PC, WebGL, or simply while the game is
//  in the foreground and a push would be redundant) -- see
//  PlatformNotifications.cs for the OS-level side on Android/iOS/macOS.
// ═══════════════════════════════════════════════════════════════════════════════
public class NotificationToast : MonoBehaviour
{
    public static NotificationToast Instance { get; private set; }

    private class Toast
    {
        public string title;
        public string body;
        public Texture2D icon;
        public float shownAt;
        public float duration;
    }

    private const float SlideInDuration = 0.25f;
    private const float FadeOutDuration = 0.4f;
    private const int MaxVisible = 4;

    private readonly List<Toast> _toasts = new List<Toast>();

    private bool _stylesReady;
    private GUIStyle _lblTitle, _lblBody;

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
    private static void AutoCreate()
    {
        if (Instance != null) return;
        new GameObject("NotificationToast").AddComponent<NotificationToast>();
    }

    private void Awake()
    {
        if (Instance != null && Instance != this) { Destroy(gameObject); return; }
        Instance = this;
        DontDestroyOnLoad(gameObject);
    }

    private void OnDestroy() { if (Instance == this) Instance = null; }

    public static void Show(string title, string body, Texture2D icon = null, float duration = 6f)
    {
        if (Instance == null) return;
        Instance._toasts.Add(new Toast { title = title, body = body, icon = icon, shownAt = Time.realtimeSinceStartup, duration = duration });
        if (Instance._toasts.Count > MaxVisible + 4) Instance._toasts.RemoveAt(0); // hard backstop against pile-up
    }

    private void EnsureStyles()
    {
        if (_stylesReady) return; _stylesReady = true;
        _lblTitle = MDT_UITheme.MakeLabel(13, FontStyle.Bold, TextAnchor.MiddleLeft, MDT_UITheme.TextCyan);
        _lblBody  = MDT_UITheme.MakeLabel(12, FontStyle.Normal, TextAnchor.UpperLeft, MDT_UITheme.TextPrimary);
    }

    private void OnGUI()
    {
        if (_toasts.Count == 0) return;
        EnsureStyles();

        float now = Time.realtimeSinceStartup;
        float panelW = 320f, y = 16f;
        int shown = 0;

        for (int i = _toasts.Count - 1; i >= 0 && shown < MaxVisible; i--)
        {
            var t = _toasts[i];
            float age = now - t.shownAt;
            if (age > t.duration + FadeOutDuration) { _toasts.RemoveAt(i); continue; }

            float slideT = Mathf.Clamp01(age / SlideInDuration);
            float ease = 1f - Mathf.Pow(1f - slideT, 3f);
            float xOffset = (1f - ease) * (panelW + 20f);

            float alpha = 1f;
            float fadeStart = t.duration;
            if (age > fadeStart) alpha = 1f - Mathf.Clamp01((age - fadeStart) / FadeOutDuration);

            bool hasIcon = t.icon != null;
            float panelH = hasIcon ? 72f : 52f;
            var rect = new Rect(Screen.width - panelW - 16f + xOffset, y, panelW, panelH);

            var prevColor = GUI.color;
            GUI.color = new Color(1, 1, 1, alpha);
            MDT_UITheme.DrawSoftShadow(rect, 12f);
            MDT_UITheme.DrawRoundedRectBordered(rect, 10f, MDT_UITheme.BGDeep, MDT_UITheme.BorderAccent, 1);

            float textX = rect.x + 12f;
            float textW = rect.width - 24f;
            if (hasIcon)
            {
                float iconSize = 48f;
                GUI.DrawTexture(new Rect(rect.x + 10f, rect.y + 12f, iconSize, iconSize), t.icon, ScaleMode.ScaleToFit);
                textX = rect.x + 10f + iconSize + 10f;
                textW = rect.width - (iconSize + 32f);
            }

            GUI.Label(new Rect(textX, rect.y + 10f, textW, 18f), t.title, _lblTitle);
            GUI.Label(new Rect(textX, rect.y + 28f, textW, rect.height - 34f), t.body, _lblBody);
            GUI.color = prevColor;

            y += panelH + 10f;
            shown++;
        }
    }
}
