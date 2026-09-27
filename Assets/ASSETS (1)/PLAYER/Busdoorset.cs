using System.Collections.Generic;
using UnityEngine;

// ═══════════════════════════════════════════════════════════════════════════════
//  BUS DOOR SET — groups the leaf/leaves that make up ONE doorway (front or
//  rear) so both panels of a two-leaf slide-glide door open/close together.
//  Single-leaf doorways just use a list with one entry.
//
//  This is the thing both player input and NPC dwell logic should actually
//  call — neither should touch BusDoorLeaf directly.
// ═══════════════════════════════════════════════════════════════════════════════
public class BusDoorSet : MonoBehaviour
{
    public enum DoorPosition { Front, Rear }
    public DoorPosition position = DoorPosition.Front;
    public List<BusDoorLeaf> leaves = new();

    public bool IsOpen => leaves.Count > 0 && leaves[0].State == BusDoorLeaf.DoorState.Open;
    public bool IsOpenOrOpening
    {
        get
        {
            foreach (var l in leaves) if (l != null && l.IsOpenOrOpening) return true;
            return false;
        }
    }

    /// <summary>True only when every leaf has FINISHED closing — not just told
    /// to close. A set with no leaves counts as fully closed (nothing to wait on).</summary>
    public bool IsFullyClosed
    {
        get
        {
            foreach (var l in leaves)
                if (l != null && l.State != BusDoorLeaf.DoorState.Closed) return false;
            return true;
        }
    }

    public void Open()
    {
        foreach (var l in leaves) if (l != null) l.Open();
    }

    public void Close()
    {
        foreach (var l in leaves) if (l != null) l.Close();
    }

    public void Toggle()
    {
        if (IsOpenOrOpening) Close();
        else Open();
    }

    public bool IsWedged => leaves.Count > 0 && leaves[0].State == BusDoorLeaf.DoorState.Wedged;

    /// <summary>Stuck-door fallback: forces every leaf to a halfway position
    /// instead of the normal open/closed poses. See BusDoorLeaf.Wedge.</summary>
    public void Wedge()
    {
        foreach (var l in leaves) if (l != null) l.Wedge();
    }

    public void SnapClosed()
    {
        foreach (var l in leaves) if (l != null) l.SnapClosed();
    }
}