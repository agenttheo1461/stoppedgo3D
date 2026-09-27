using System.Collections.Generic;
using UnityEngine;

// ═══════════════════════════════════════════════════════════════════════════════
//  PAX VISUAL POOL
//
//  Object pool for cube visuals — PaxSimManager only ever acquires/releases
//  through here, never instantiates/destroys directly, so we don't pay GC/
//  instantiate cost as pax cross the visibility radius repeatedly.
//
//  NOTE: relies on the PaxVisual component, defined in its own PaxVisual.cs.
// ═══════════════════════════════════════════════════════════════════════════════
public class PaxVisualPool : MonoBehaviour
{
    public static PaxVisualPool Instance { get; private set; }

    [Header("Prefab")]
    [Tooltip("Leave null to auto-generate a simple coloured cube primitive.")]
    public GameObject paxPrefab;
    public Vector3 paxScale = new Vector3(0.5f, 1.0f, 0.5f);

    [Header("Pool")]
    public int prewarmCount = 120;

    [Header("Colour Variety")]
    public Color[] paxColors = new[]
    {
        new Color(0.85f, 0.30f, 0.25f),
        new Color(0.25f, 0.45f, 0.85f),
        new Color(0.30f, 0.75f, 0.35f),
        new Color(0.85f, 0.75f, 0.20f),
        new Color(0.55f, 0.30f, 0.75f),
        new Color(0.90f, 0.55f, 0.20f),
        new Color(0.20f, 0.75f, 0.75f),
    };

    private readonly Stack<PaxVisual> _free = new Stack<PaxVisual>();
    private Transform _poolRoot;
    private System.Random _rng = new System.Random();

    private void Awake()
    {
        if (Instance != null && Instance != this) { Destroy(gameObject); return; }
        Instance = this;

        _poolRoot = new GameObject("PaxVisualPool_Root").transform;
        _poolRoot.SetParent(transform, false);

        for (int i = 0; i < prewarmCount; i++)
            _free.Push(CreateNew());
    }

    private PaxVisual CreateNew()
    {
        GameObject go;
        if (paxPrefab != null)
        {
            go = Instantiate(paxPrefab, _poolRoot);
        }
        else
        {
            go = GameObject.CreatePrimitive(PrimitiveType.Cube);
            go.transform.SetParent(_poolRoot, false);
            var col = go.GetComponent<Collider>();
            if (col != null) Destroy(col);
        }

        go.transform.localScale = paxScale;
        go.name = "PaxVisual";
        go.SetActive(false);

        var pv = go.GetComponent<PaxVisual>() ?? go.AddComponent<PaxVisual>();
        return pv;
    }

    public PaxVisual Acquire(PaxAgent agent)
    {
        PaxVisual pv = _free.Count > 0 ? _free.Pop() : CreateNew();
        pv.gameObject.SetActive(true);
        pv.SnapTo(agent.position);
        pv.SetColor(paxColors[_rng.Next(paxColors.Length)]);

pv.gameObject.SetActive(true); 
    return pv;
    }

    public void Release(PaxVisual pv)
    {
        if (pv == null) return;
        pv.gameObject.SetActive(false);
        _free.Push(pv);
    }
}