using UnityEngine;

// ─────────────────────────────────────────────────────────────────────────────
//  BUS CAMERA PROFILE
//
//  Drop this on a bus prefab to tune the camera specifically for that bus
//  type -- an 60' artic wants a further orbit distance and a different FPV
//  seat position than a 40' rigid, and this is where that lives instead of
//  CameraFollow25D having one global set of numbers for every bus in the
//  fleet.
//
//  Each section has its own "override" toggle and is independent: you can
//  override just orbit distance for one bus type and leave follow/FPV on
//  CameraFollow25D's defaults, or override everything. Anything left off
//  falls straight through to whatever's set on the camera object itself --
//  this component is purely additive, buses without one behave exactly as
//  before.
// ─────────────────────────────────────────────────────────────────────────────
public class BusCameraProfile : MonoBehaviour
{
    [Header("Front Anchor")]
    [Tooltip("The transform the camera (orbit + FPV/X mode) should track as this bus's front. Drag the actual front-section child here -- doesn't need to be named anything in particular, doesn't need to be a direct child. Leave empty to fall back to CameraFollow25D's name-search/root behavior.")]
    public Transform frontSection;

    [Header("Follow Mode")]
    public bool    overrideFollow = false;
    public Vector3 followOffset   = new Vector3(0f, 15f, -10f);
    public float   followSmoothing = 8f;

    [Header("Orbit Mode (F)")]
    public bool  overrideOrbit       = false;
    public float orbitDistance       = 12f;
    public float orbitHeight         = 6f;
    public float orbitSensitivity    = 3f;
    public float orbitSmoothing      = 8f;

    [Header("First Person (X)")]
    public bool    overrideFirstPerson  = false;
    public Vector3 firstPersonOffset    = new Vector3(0f, 2f, 0.5f);
    public bool    apply180Fix          = true;
    public float   fpLookSensitivity    = 3f;
}