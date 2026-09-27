using UnityEngine;

public class BusLightController : MonoBehaviour
{
    public Light[] headlights;
    public Light[] brakeLights;

    public bool headlightsOn;
    public bool isBraking;

    void Update()
    {
        foreach (var l in headlights)
            if (l) l.enabled = headlightsOn;

        foreach (var l in brakeLights)
            if (l) l.enabled = isBraking;
    }
}