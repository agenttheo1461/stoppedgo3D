using System.Collections;
using System.Collections.Generic;
using UnityEngine;

// ═══════════════════════════════════════════════════════════════════════════════
//  BUS BOARDING SEQUENCER
//
//  One static utility that both NPCBusController and PlayerHandoff call into.
//  Handles walking a batch of PaxAgents single-file from the curb queue into
//  the bus door (boarding) or from the door out to the curb then away
//  (alighting). Each pax takes perPaxBoardSeconds to fully step through the
//  door, offset by paxFileGap from the pax ahead of them — so N pax take
//  roughly (N-1)*gap + perPaxBoardSeconds, not N*perPaxBoardSeconds, since
//  they overlap in the line like a real queue.
//
//  Call BeginBoarding/BeginAlighting, then either:
//    · yield on the IEnumerator from a coroutine (blocks until done), or
//    · poll handle.IsDone / handle.ElapsedSeconds from your own loop (StopDwell
//      already runs as a coroutine, so the natural usage is to run this nested
//      via `yield return StartCoroutine(...)`).
// ═══════════════════════════════════════════════════════════════════════════════
public class BoardingHandle
{
    public bool  IsDone;
    public float ElapsedSeconds;
    public int   PaxProcessed;
}

public static class BusBoardingSequencer
{
    /// <summary>
    /// Drives `count` pax from the stop's waiting queue into the bus through doorWorldPos.
    /// Returns the handle immediately; caller should `yield return` the coroutine itself
    /// (use RunOn) to actually wait for completion, or just check handle.IsDone.
    /// </summary>
    public static IEnumerator RunBoarding(
        MonoBehaviour host, string stopCode, Vector3 doorWorldPos, Vector3 busForward,
        int count, int busID, bool isPlayerBus, BoardingHandle handle)
    {
        handle.IsDone = false;
        handle.ElapsedSeconds = 0f;
        handle.PaxProcessed = 0;

        var sim = PaxSimManager.Instance;
        if (sim == null || count <= 0) { handle.IsDone = true; yield break; }

        List<PaxAgent> claimed = sim.ClaimBoardingPax(stopCode, count);

        // If the stop's visual/sim queue doesn't have enough waiting pax to match
        // the gameplay pax counters (e.g. before the sim has had time to populate
        // this stop), synthesize the remainder so dwell timing still feels right.
        int shortfall = count - claimed.Count;
        if (shortfall > 0)
        {
            for (int i = 0; i < shortfall; i++)
            {
                claimed.Add(new PaxAgent
                {
                    id = -1,
                    position = doorWorldPos - busForward * (2f + i * 0.8f),
                    state = PaxState.BoardingBus,
                    walkSpeed = 1.35f,
                });
            }
        }

        float perPax = sim.perPaxBoardSeconds;
        float gap    = sim.paxFileGap;

        // Stagger start times so pax form a single-file line rather than all
        // arriving at the door simultaneously.
        var startDelays = new float[claimed.Count];
        for (int i = 0; i < claimed.Count; i++) startDelays[i] = i * gap;

        float t = 0f;
        var inFlight = new bool[claimed.Count];
        int finishedCount = 0;

        while (finishedCount < claimed.Count)
        {
            t += Time.deltaTime;
            handle.ElapsedSeconds = t;

            for (int i = 0; i < claimed.Count; i++)
            {
                if (inFlight[i] || t < startDelays[i]) continue;
                inFlight[i] = true;

                var a = claimed[i];
                if (a.id < 0) { handle.PaxProcessed++; finishedCount++; continue; } // synthetic — no visual to animate

                Vector3 startPos = a.position;
                Vector3 endPos   = doorWorldPos;
                host.StartCoroutine(WalkPaxToPoint(a, startPos, endPos, perPax, onArrive: () =>
                {
                    a.state = PaxState.OnBus;
                    a.boardedBusID = busID;
                    a.isOnPlayerBus = isPlayerBus;
                    sim.ReleaseVisualPublic(a);
                    handle.PaxProcessed++;
                    finishedCount++;
                }));
            }
            yield return null;
        }

        handle.IsDone = true;
    }

    /// <summary>
    /// Spawns `count` alighting pax at the door and walks them out to the curb,
    /// then releases them into the sidewalk network to walk away and despawn.
    /// </summary>
    public static IEnumerator RunAlighting(
        MonoBehaviour host, string stopCode, Vector3 doorWorldPos, Vector3 curbWorldPos,
        int count, BoardingHandle handle)
    {
        handle.IsDone = false;
        handle.ElapsedSeconds = 0f;
        handle.PaxProcessed = 0;

        var sim = PaxSimManager.Instance;
        if (sim == null || count <= 0) { handle.IsDone = true; yield break; }

        List<PaxAgent> alighting = sim.SpawnAlightingPax(stopCode, doorWorldPos, count);

        float perPax = sim.perPaxBoardSeconds;
        float gap    = sim.paxFileGap;

        var startDelays = new float[alighting.Count];
        for (int i = 0; i < alighting.Count; i++) startDelays[i] = i * gap;

        float t = 0f;
        var inFlight = new bool[alighting.Count];
        int finishedCount = 0;

        while (finishedCount < alighting.Count)
        {
            t += Time.deltaTime;
            handle.ElapsedSeconds = t;

            for (int i = 0; i < alighting.Count; i++)
            {
                if (inFlight[i] || t < startDelays[i]) continue;
                inFlight[i] = true;

                var a = alighting[i];
                Vector3 target = curbWorldPos + Random.insideUnitSphere * 1.2f;
                target.y = curbWorldPos.y;

                host.StartCoroutine(WalkPaxToPoint(a, doorWorldPos, target, perPax, onArrive: () =>
                {
                    sim.ReleaseAfterAlighting(a, target);
                    handle.PaxProcessed++;
                    finishedCount++;
                }));
            }
            yield return null;
        }

        handle.IsDone = true;
    }

    private static IEnumerator WalkPaxToPoint(PaxAgent a, Vector3 from, Vector3 to, float duration, System.Action onArrive)
    {
        a.position = from;
        float t = 0f;
        Vector3 dir = (to - from); dir.y = 0f;

        while (t < duration)
        {
            t += Time.deltaTime;
            float frac = Mathf.Clamp01(t / duration);
            a.position = Vector3.Lerp(from, to, frac);
            if (a.visual != null) a.visual.SyncTransform(a, dir);
            yield return null;
        }

        a.position = to;
        onArrive?.Invoke();
    }
}