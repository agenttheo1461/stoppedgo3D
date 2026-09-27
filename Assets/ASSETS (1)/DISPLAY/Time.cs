// DayNightController.cs  — updated to drive skybox + toon shader globals
//
// Additions over original:
//   UpdateSunProperties() now calls three Shader.SetGlobalXxx each frame:
//
//     _SkyboxTimeOfDay  — 0..1 day fraction; NightCitySkybox uses this to blend
//                         between night and day sky, fade stars, drive city glow.
//
//     _SkyboxSunDir     — world-space unit vector pointing TOWARD the sun.
//                         Computed as -sunLight.transform.forward after rotation
//                         is applied.  NightCitySkybox places its sun disc here.
//
//     _SkyboxSunColor   — current sun light colour (matches sunColor gradient).
//                         NightCitySkybox tints the sun disc and halo with this
//                         so the disc goes orange at dawn/dusk automatically.
//
// No other behaviour is changed.

using UnityEngine;

public class DayNightController : MonoBehaviour
{
    [Header("References")]
    [SerializeField] private Light sunLight;

    [Header("Lighting Settings")]
    [SerializeField] private Gradient sunColor;
    [SerializeField] private AnimationCurve intensityCurve;

    [Header("Rotation Offsets")]
    [Tooltip("Adjust this so 12:00 PM (720 mins) means the sun is directly overhead.")]
    [SerializeField] private float angleOffset = -90f;

    private const float MINUTES_IN_DAY = 1440f; // 24 × 60

    private void Start()
    {
        if (sunLight == null)
            sunLight = GetComponent<Light>();

        // Force Unity to use the flat color calculation we drive via code
        RenderSettings.ambientMode = UnityEngine.Rendering.AmbientMode.Flat;
    }

[Header("Testing Override")]
    [Tooltip("Check this to ignore the game clock and use the slider below.")]
    [SerializeField] private bool useTestTime = false;
    
    [Tooltip("0 = Midnight, 0.5 = Noon, 0.75 = 6 PM")]
    [Range(0f, 1f)]
    [SerializeField] private float testTimeOfDay = 0.5f;

    private void Update()
    {
        float dayPercentage;

        if (useTestTime)
        {
            // Override active: grab the value from the inspector slider
            dayPercentage = testTimeOfDay;
        }
        else
        {
            // Normal gameplay: grab the value from the scheduler clock
            if (BusScheduler.Instance == null) return;
            float currentMinutes = BusScheduler.Instance.CurrentGameMinutes;
            dayPercentage  = (currentMinutes % MINUTES_IN_DAY) / MINUTES_IN_DAY;
        }

        UpdateSunRotation(dayPercentage);
        UpdateSunProperties(dayPercentage); 
    }

    private void UpdateSunRotation(float percentage)
    {
        float xRotation = (percentage * 360f) + angleOffset;
        transform.localRotation = Quaternion.Euler(xRotation, 30f, 0f);
    }

    private void UpdateSunProperties(float percentage)
    {
        if (sunLight == null) return;

        // Existing behaviour — colour + intensity from inspector curves.
        if (sunColor != null)
            sunLight.color = sunColor.Evaluate(percentage);

        if (intensityCurve != null)
            sunLight.intensity = intensityCurve.Evaluate(percentage);

        // Scale ambient bounce light with sun colour so interiors go dark at night.
        RenderSettings.ambientLight = sunLight.color * 0.5f;

        // ── Global shader properties (read by NightCitySkybox + ToonRampLit) ──

        // 0 = midnight, 0.25 = 6 AM, 0.5 = noon, 0.75 = 6 PM.
        // NightCitySkybox uses this to blend day/night gradients, fade stars,
        // city glow, and drive dawn/dusk warm tint.
        Shader.SetGlobalFloat("_SkyboxTimeOfDay", percentage);

        // Unit vector pointing from earth toward the sun.
        // sunLight.transform.forward is the direction light TRAVELS (downward).
        // Negating it gives the direction you'd look to see the sun — which is
        // what the skybox needs to place the disc.
        // This is only valid after UpdateSunRotation() has run this frame.
        Shader.SetGlobalVector("_SkyboxSunDir", -sunLight.transform.forward);

        // Current sun colour so the skybox can tint the disc/halo to match.
        // At noon this is white; at dawn/dusk it's orange; at night it's black
        // (intensity = 0 so the disc disappears, though the skybox also checks
        // whether sunDir is above the horizon independently).
        Shader.SetGlobalColor("_SkyboxSunColor", sunLight.color);
    }
}