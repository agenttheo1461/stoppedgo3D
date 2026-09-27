using UnityEngine;

public class BikeRackController : MonoBehaviour
{
    [SerializeField] private GameObject bikeRackObject;

    public void SetBikeRack(bool enabled)
    {
        if (bikeRackObject != null)
            bikeRackObject.SetActive(enabled);
    }
}