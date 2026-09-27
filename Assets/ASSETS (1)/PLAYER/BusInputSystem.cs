using UnityEngine;
public class AudioKickdownBridge : MonoBehaviour
{
    public BusAudioEngine audioEngine;

    void Update()
    {
        if (audioEngine != null)
        {
            // Set the state directly into your BusAudioEngine class
            audioEngine.kickdownKey = Input.GetKey(KeyBindings.Current.kickdown);
        }
    }
}