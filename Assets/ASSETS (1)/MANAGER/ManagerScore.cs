using System.Collections.Generic;
using UnityEngine;

// ═══════════════════════════════════════════════════════════════════════════════
//  MANAGER SCORE — points and XP for dispatching well, added to the same level,
//  XP and points the main menu shows (PointsManager).
//
//    · a trip you placed finishes:           10 points, 25 XP
//        …and it was within 2 minutes:        +15 points, +40 XP
//        …within 5 minutes:                   +6 points, +15 XP
//    · you send a replacement for a broken bus: 30 points, 60 XP
//  Checked every couple of seconds, in Drive mode as well, so a plan you made
//  still pays out while you are driving.
// ═══════════════════════════════════════════════════════════════════════════════
public static class ManagerScore
{
    public static int SessionPoints { get; private set; }
    public static int SessionXP     { get; private set; }

    private static readonly HashSet<TimetableSlot> _scored = new HashSet<TimetableSlot>();
    private static float _next;

    public static event System.Action<string> Earned;

    public static void Award(int points, int xp, string why)
    {
        var pm = PointsManager.Instance;
        if (pm == null) return;
        bool level = pm.AwardManagerScore(points, xp);
        SessionPoints += points; SessionXP += xp;
        Earned?.Invoke($"+{points} points · {why}" + (level ? " · Level up!" : ""));
    }

    public static void Tick()
    {
        if (Time.unscaledTime < _next) return;
        _next = Time.unscaledTime + 2f;

        var list = ManagerLocks.FixedTrips;
        for (int i = list.Count - 1; i >= 0; i--)
        {
            var s = list[i];
            if (s.state != SlotState.Completed) continue;
            if (s.assignedBusID < 0 && s.actualDeparture < 0f) { ManagerLocks.UnlockSlot(s); continue; } // never ran
            if (_scored.Add(s))
            {
                float late = Mathf.Abs(s.latenessMinutes);
                int pts = 10, xp = 25; string why = $"route {s.routeNumber} trip finished";
                if (late <= 2f) { pts += 15; xp += 40; why += " on time"; }
                else if (late <= 5f) { pts += 6; xp += 15; why += " nearly on time"; }
                Award(pts, xp, why);
            }
            ManagerLocks.UnlockSlot(s); // done, so stop tracking it
        }
        if (_scored.Count > 500) _scored.Clear();
    }
}
