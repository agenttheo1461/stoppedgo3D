using UnityEngine;
using System.Collections;
public class PaxVisual : MonoBehaviour
{
    private Transform _t;
    private Renderer  _renderer;
    private MaterialPropertyBlock _mpb;

    private void Awake()
    {
        _t = transform;
        _renderer = GetComponent<Renderer>();
        _mpb = new MaterialPropertyBlock();
    }

    public void SetColor(Color c)
    {
        if (_renderer == null) return;
        _mpb.SetColor("_Color", c);
        _renderer.SetPropertyBlock(_mpb);
    }

    /// <summary>Pushes a PaxAgent's simulated position/facing onto this visual's transform.</summary>
    public void SyncTransform(PaxAgent a, Vector3 lastMoveDir)
    {
        _t.position = a.position + Vector3.up * 0.5f; // half cube height off ground
        if (lastMoveDir.sqrMagnitude > 0.0001f)
            _t.rotation = Quaternion.Slerp(_t.rotation,
                Quaternion.LookRotation(lastMoveDir.normalized, Vector3.up),
                Time.deltaTime * 8f);
    }

public void SnapTo(Vector3 pos)
{
    gameObject.SetActive(true);
    _t.position = pos + Vector3.up * 0.5f;

    // DIAGNOSTIC LOG
    if (!gameObject.activeInHierarchy)
    {
        if (transform.parent != null && !transform.parent.gameObject.activeInHierarchy)
        {
            Debug.LogError($"[PAX ERROR] {gameObject.name} is inactive because its PARENT ({transform.parent.name}) is inactive!");
        }
        else
        {
            Debug.LogError($"[PAX ERROR] {gameObject.name} is inactive, but its parent is active. Something else is turning it off this frame!");
        }
    }
}
}
