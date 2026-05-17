using UnityEngine;
using Unity.Netcode;

public class GoToTask : TaskBase
{
    private Vector3 targetPosition;
    private float completionRadius;

    public GoToTask(string id, string description, ulong playerId, Vector3 targetPos, float radius = 2f) 
        : base(id, description, playerId)
    {
        targetPosition = targetPos;
        completionRadius = radius;
    }

    public override void UpdateCheck()
    {
        if (IsCompleted) return;

        // Fetch this specific player's network connection data on the server
        if (NetworkManager.Singleton.ConnectedClients.TryGetValue(TargetPlayerId, out var client))
        {
            if (client.PlayerObject != null)
            {
                float distance = Vector3.Distance(client.PlayerObject.transform.position, targetPosition);
                if (distance <= completionRadius)
                {
                    CompleteTask();
                }
            }
        }
    }
}