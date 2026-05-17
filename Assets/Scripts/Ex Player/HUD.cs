using UnityEngine;
using System.Collections.Generic;

public class UniversalGameHUD : MonoBehaviour
{
    [Header("UI Visual Customization")]
    [SerializeField] private Color panelBackgroundColor = new Color(0.01f, 0.01f, 0.01f, 1f);
    [SerializeField] private Color accentColor = new Color(0.1f, 0.1f, 0.1f, 0.7f);
    [SerializeField] private Color taskTextColor = new Color(1.0f, 0.75f, 0.0f, 1.0f);
    [Range(0, 20)] [SerializeField] private int cornerRadius = 12;

    private GUIStyle headerStyle;
    private GUIStyle bodyStyle;
    private GUIStyle taskStyle;
    
    private Texture2D panelBackgroundTex;
    private Texture2D lineTex; 

    private void Start()
    {
        panelBackgroundTex = CreateRoundedTexture(280, 95, cornerRadius, panelBackgroundColor);
        lineTex = CreateSolidTexture(2, 2, accentColor);
    }

    private void OnGUI()
    {
        InitializeGUIStyles();
        
        string displayName = "debug15628";
        string playerLevel = "NaN";
        
        if (AccountSystem.Instance != null && AccountSystem.Instance.IsLoggedIn)
        {
            displayName = AccountSystem.Instance.CurrentPlayer.DisplayName;
            playerLevel = AccountSystem.Instance.CurrentPlayer.PlayerLevel.ToString();
        }
        else
        {
            return; 
        }

        string currentObjective = "";
        if (NetworkTaskManager.Instance != null)
        {
            currentObjective = NetworkTaskManager.Instance.GetCurrentLocalTaskDescription();
        }

        Rect hudPanelRect = new Rect(20, 20, 280, 95);
        
        GUIStyle panelStyle = new GUIStyle();
        panelStyle.normal.background = panelBackgroundTex;
        
        GUILayout.BeginArea(hudPanelRect, panelStyle);
        
        GUILayout.BeginArea(new Rect(12, 10, 256, 75));
        GUILayout.BeginVertical();
        
        GUILayout.BeginHorizontal();
        
        if (displayName.Contains("[Dev]"))
        {
            headerStyle.normal.textColor = Color.cyan;
        }
        else
        {
            headerStyle.normal.textColor = Color.white;
        }

        GUILayoutExtension.MakeRowLabel(displayName, headerStyle);
        
        GUILayout.FlexibleSpace();
        GUILayout.Label($"LVL {playerLevel}", bodyStyle);
        GUILayout.EndHorizontal();

        GUILayout.Space(6);

        GUILayout.Label("Task:", bodyStyle);
        GUILayout.Space(2);
        GUILayout.Label(currentObjective, taskStyle);

        GUILayout.EndVertical();
        GUILayout.EndArea(); 
        GUILayout.EndArea(); 
    }

    private void InitializeGUIStyles()
    {
        if (headerStyle != null) return;

        headerStyle = new GUIStyle(GUI.skin.label);
        headerStyle.fontSize = 12;
        headerStyle.fontStyle = FontStyle.Bold;

        bodyStyle = new GUIStyle(GUI.skin.label);
        bodyStyle.fontSize = 9;
        bodyStyle.fontStyle = FontStyle.Bold;
        bodyStyle.normal.textColor = new Color(0.65f, 0.67f, 0.72f, 1.0f);

        taskStyle = new GUIStyle(GUI.skin.label);
        taskStyle.fontSize = 10;
        taskStyle.fontStyle = FontStyle.Bold;
        taskStyle.normal.textColor = taskTextColor;
        taskStyle.wordWrap = true;
    }

    private void DrawHorizontalLine()
    {
        Rect lineRect = GUILayoutUtility.GetRect(10, 1);
        GUI.DrawTexture(lineRect, lineTex);
    }

    private Texture2D CreateSolidTexture(int width, int height, Color col)
    {
        Color[] pix = new Color[width * height];
        for (int i = 0; i < pix.Length; ++i) pix[i] = col;
        Texture2D result = new Texture2D(width, height);
        result.SetPixels(pix);
        result.Apply();
        return result;
    }

    private Texture2D CreateRoundedTexture(int width, int height, int radius, Color col)
    {
        Texture2D tex = new Texture2D(width, height, TextureFormat.RGBA32, false);
        Color[] pix = new Color[width * height];

        for (int y = 0; y < height; y++)
        {
            for (int x = 0; x < width; x++)
            {
                pix[y * width + x] = col;

                bool isLeft = x < radius;
                bool isRight = x >= width - radius;
                bool isBottom = y < radius;
                bool isTop = y >= height - radius;

                if (isLeft || isRight || isBottom || isTop)
                {
                    float cx = isLeft ? radius : (isRight ? width - radius - 1 : x);
                    float cy = isBottom ? radius : (isTop ? height - radius - 1 : y);

                    float dx = x - cx;
                    float dy = y - cy;
                    float distanceSq = dx * dx + dy * dy;
                    float radiusSq = radius * radius;

                    if (distanceSq > radiusSq)
                    {
                        pix[y * width + x] = Color.clear;
                    }
                    else if (distanceSq > (radius - 1) * (radius - 1))
                    {
                        float dist = Mathf.Sqrt(distanceSq);
                        float edgeAlpha = radius - dist;
                        pix[y * width + x] = new Color(col.r, col.g, col.b, col.a * edgeAlpha);
                    }
                }
            }
        }

        tex.SetPixels(pix);
        tex.Apply();
        return tex;
    }

    private void OnDestroy()
    {
        if (panelBackgroundTex != null) Destroy(panelBackgroundTex);
        if (lineTex != null) Destroy(lineTex);
    }
}

public static class GUILayoutExtension
{
    public static void MakeRowLabel(string txt, GUIStyle style)
    {
        Vector2 size = style.CalcSize(new GUIContent(txt));
        GUILayout.Label(txt, style, GUILayout.Width(size.x + 5));
    }
}