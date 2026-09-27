using System.Collections.Generic;
using UnityEngine;

// ═══════════════════════════════════════════════════════════════════════════════
//  AI CAR CONTROLLER  —  background traffic. Follows RoadGraph-derived
//  waypoints toward a destination node; on arrival, asks TrafficManager for a
//  new far-away destination and keeps going — perpetual traffic, not one-shot
//  commuters that idle once they get somewhere.
//
//  Movement is kinematic (Transform-driven), not Rigidbody-physics — same
//  "cosmetic, not physics" reasoning as most of this project's simpler
//  components: with dozens of cars on screen at once, a hand-rolled forward-
//  move + look-rotation is far cheaper and more predictable per car than
//  solving physics steering for all of them. A (kinematic) Collider is still
//  required so buses/other cars can detect this one via SphereCast/Overlap.
//
//  BUS AWARENESS: a forward SphereCast against busMask (set this to whatever
//  layer bus root colliders live on) slows/stops the car well before it
//  actually reaches a bus — buses get real right-of-way, not just "another
//  obstacle to nudge through."
// ═══════════════════════════════════════════════════════════════════════════════
[RequireComponent(typeof(Collider))]
public class AICarController : MonoBehaviour
{
    [Header("Driving Feel")]
    public float maxSpeed           = 12f;  // m/s (~43 km/h — neighborhood traffic pace)
    public float acceleration       = 4f;
    public float braking            = 8f;
    [Tooltip("How fast the car's heading catches up to its target heading. Higher = snappier turns.")]
    public float turnSpeed          = 6f;
    public float waypointArriveDist = 4f;

    [Header("Bus/Traffic Awareness")]
    [Tooltip("Layer(s) bus root colliders live on. A car slows/stops for anything on this mask ahead of it.")]
    public LayerMask busMask = ~0;
    [Tooltip("Also treat other AI cars as obstacles to avoid pile-ups.")]
    public bool avoidOtherCars = true;
    public float lookAheadDistance = 14f;
    public float lookAheadRadius   = 1.6f;
    [Tooltip("Full stop once an obstacle is this close, regardless of speed.")]
    public float stopDistance      = 5f;

    [Header("Destination Arrival")]
    public float destinationArriveDist = 8f;

    private RoadGraph _graph;
    private TrafficManager _manager;
    private List<Vector3> _waypoints = new();
    private int _waypointIndex;
    private Vector3 _destination;

    private float _currentSpeed;
    private static readonly Collider[] _obstacleBuf = new Collider[8];

    /// <summary>Called by TrafficManager right after Instantiate.</summary>
    public void Initialize(RoadGraph graph, Vector3 destination, TrafficManager manager)
    {
        _graph   = graph;
        _manager = manager;
        SetNewDestination(destination);

        // Face the first waypoint immediately instead of Slerping from
        // whatever rotation Instantiate happened to leave it at — avoids a
        // visible snap-spin on the first frame after spawning.
        if (_waypoints.Count > 0)
        {
            Vector3 dir = _waypoints[0] - transform.position;
            dir.y = 0f;
            if (dir.sqrMagnitude > 0.01f)
                transform.rotation = Quaternion.LookRotation(dir.normalized, Vector3.up);
        }
    }

    private void SetNewDestination(Vector3 destination)
    {
        _destination = destination;
        _waypoints = RoadGraphPathfinder.FindWaypoints(_graph, transform.position, destination);
        _waypointIndex = 0;

        if (_waypoints.Count == 0)
        {
            // No path found — most likely spawned off-graph or the pick was
            // unreachable. Don't sit dead forever; just roll a fresh random
            // destination instead of leaving a stuck car in the world.
            RequestNewDestination();
        }
    }

    private void RequestNewDestination()
    {
        if (_manager == null || _graph == null) return;
        var node = _manager.PickFarNode(transform.position);
        if (node != null) SetNewDestination(node.position);
    }

    private void Update()
    {
        if (_waypoints == null || _waypoints.Count == 0) return;

        float dt = Time.deltaTime;

        // ── Finished the whole route? ────────────────────────────────────────
        if (_waypointIndex >= _waypoints.Count)
        {
            if (Vector3.Distance(transform.position, _destination) <= destinationArriveDist)
            {
                RequestNewDestination();
                return;
            }
            // Ran out of waypoints without quite reaching the destination
            // (culled short by the waypoint spacing) — just aim at it directly
            // for these last few metres.
        }

        Vector3 target = _waypointIndex < _waypoints.Count ? _waypoints[_waypointIndex] : _destination;

        // ── Advance to the next waypoint once close enough ──────────────────
        Vector3 toTarget = target - transform.position;
        toTarget.y = 0f;
        if (toTarget.magnitude <= waypointArriveDist && _waypointIndex < _waypoints.Count)
        {
            _waypointIndex++;
            if (_waypointIndex < _waypoints.Count) target = _waypoints[_waypointIndex];
        }

        // ── Steering: rotate toward target heading ───────────────────────────
        if (toTarget.sqrMagnitude > 0.01f)
        {
            Quaternion targetRot = Quaternion.LookRotation(toTarget.normalized, Vector3.up);
            transform.rotation = Quaternion.Slerp(transform.rotation, targetRot, turnSpeed * dt);
        }

        // ── Obstacle awareness: buses (and optionally other cars) ahead ──────
        float obstacleFactor = SenseObstacleAhead();

        // ── Speed: accelerate toward maxSpeed, scaled down by obstacleFactor ─
        float wantSpeed = maxSpeed * obstacleFactor;
        _currentSpeed = wantSpeed < _currentSpeed
            ? Mathf.MoveTowards(_currentSpeed, wantSpeed, braking * dt)
            : Mathf.MoveTowards(_currentSpeed, wantSpeed, acceleration * dt);

        transform.position += transform.forward * _currentSpeed * dt;
    }

    /// <summary>Returns a 0..1 speed multiplier for what's ahead — 1 = clear
    /// road, 0 = something (almost always a bus) close enough to require a
    /// full stop. SphereCast rather than Raycast so a bus doesn't need to be
    /// dead-center in the car's path to be noticed and yielded to.</summary>
    private float SenseObstacleAhead()
    {
        Vector3 origin = transform.position + Vector3.up * 1f;

        if (Physics.SphereCast(origin, lookAheadRadius, transform.forward, out RaycastHit hit,
                                lookAheadDistance, busMask, QueryTriggerInteraction.Ignore))
        {
            float dist = hit.distance;
            if (dist <= stopDistance) return 0f;
            return Mathf.InverseLerp(stopDistance, lookAheadDistance, dist);
        }

        if (avoidOtherCars)
        {
            int n = Physics.OverlapSphereNonAlloc(
                origin + transform.forward * (lookAheadDistance * 0.5f),
                lookAheadRadius * 1.5f, _obstacleBuf, ~0, QueryTriggerInteraction.Ignore);

            for (int i = 0; i < n; i++)
            {
                if (_obstacleBuf[i] == null) continue;
                if (_obstacleBuf[i].transform.root == transform.root) continue; // skip self

                var otherCar = _obstacleBuf[i].GetComponentInParent<AICarController>();
                if (otherCar == null) continue;

                Vector3 toOther = otherCar.transform.position - transform.position;
                toOther.y = 0f;
                if (Vector3.Dot(toOther.normalized, transform.forward) < 0.5f) continue; // not ahead of us

                float dist = toOther.magnitude;
                if (dist <= stopDistance) return 0f;
                if (dist <= lookAheadDistance)
                    return Mathf.Min(1f, Mathf.InverseLerp(stopDistance, lookAheadDistance, dist));
            }
        }

        return 1f;
    }
}
