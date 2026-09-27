using System;
using System.Collections.Generic;

/// <summary>
/// v3.0 — Persists per-block completed/missed flags, not just an index.
///
/// Why: DailyShiftGenerator is now a deterministic pure function of
/// (shiftDayNumber, busID, preferredRoute) — see DailyShiftGenerator.cs.
/// That means the calendar itself never needs to be saved; anyone can
/// regenerate byte-identical blocks from those three numbers at any time.
/// What DOES need saving is progress: which blocks in that calendar were
/// completed or missed, and which one the bus was on. blockStatuses is
/// indexed identically to the freshly-regenerated list (index-for-index,
/// guaranteed stable because generation is pure), so "block 2 was missed"
/// stays true even across an app relaunch, a day rollover mid-save, or a
/// long real-world gap while the app was closed.
/// </summary>
[Serializable]
public class BlockStatus
{
    public bool completed;
    public bool missed;
}

[Serializable]
public class ShiftAssignment
{
    public int busID;
    public int fleetNumber;
    public int shiftDayNumber;          // which GameDayNumber this calendar belongs to (regen input, not stored data)
    public string preferredRouteAtGen;  // regen input — must match or the calendar won't reproduce identically
    public int currentBlockIndex;
    public List<BlockStatus> blockStatuses = new(); // parallel to the regenerated block list
}