using UnityEngine;

public static class AudioUtil
{
    public static Transform GetListener(NPCBusController bus, bool isDriving)
    {
        // driving camera
        if (isDriving && BusSelectMenu.Instance != null)
        {
            var cam = BusSelectMenu.Instance.cameraFollowScript;
            if (cam != null) return cam.transform;
        }

        // fallback camera
        if (Camera.main != null)
            return Camera.main.transform;

        // last fallback
        return bus.transform;
    }
}