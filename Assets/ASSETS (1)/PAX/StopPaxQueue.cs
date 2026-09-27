using System.Collections.Generic;
using UnityEngine;

// ═══════════════════════════════════════════════════════════════════════════════
//  STOP PAX QUEUE
//
//  Owns the list of PaxAgents currently waiting at one stop. Pax arrive via
//  PaxSimManager.EnsureQueued() once they finish walking to the stop, and they
//  leave the queue only when claimed for boarding by a bus's boarding sequence.
//
//  This is intentionally dumb/FIFO — first to arrive boards first. Queue
//  position is tracked so the visual layer (PaxVisualPool) can line pax up
//  along the curb near the stop rather than stacking them on one point.
// ═══════════════════════════════════════════════════════════════════════════════
public class StopPaxQueue
{
    public string stopCode;
    private readonly List<PaxAgent> _waiting = new List<PaxAgent>();

    public StopPaxQueue(string code) { stopCode = code; }

    public int WaitingCount => _waiting.Count;
    public IReadOnlyList<PaxAgent> Waiting => _waiting;

    public void EnsureWaiting(PaxAgent a)
    {
        if (!_waiting.Contains(a)) _waiting.Add(a);
    }

    /// <summary>Removes and returns up to `count` pax from the front of the queue,
    /// marking them BoardingBus. Caller (BusBoardingSequencer) drives their walk-in.</summary>
    public List<PaxAgent> ClaimForBoarding(int count)
    {
        var claimed = new List<PaxAgent>();
        int n = Mathf.Min(count, _waiting.Count);
        for (int i = 0; i < n; i++)
        {
            var a = _waiting[0];
            _waiting.RemoveAt(0);
            a.state = PaxState.BoardingBus;
            claimed.Add(a);
        }
        return claimed;
    }

    /// <summary>Returns a queue-position offset (metres back from the head) for lining
    /// pax up along the curb while they wait — purely cosmetic spacing.</summary>
    public float GetQueueOffset(PaxAgent a)
    {
        int idx = _waiting.IndexOf(a);
        return idx < 0 ? 0f : idx * 0.85f;
    }

    public void Remove(PaxAgent a) => _waiting.Remove(a);
}