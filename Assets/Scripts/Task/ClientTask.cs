using UnityEngine;
using Unity.Netcode;
using System.Collections.Generic;

public class NetworkTaskManager : NetworkBehaviour
{
    public static NetworkTaskManager Instance { get; private set; }

    private class PlayerProgressTrack
    {
        public List<TaskBase> Queue = new List<TaskBase>();
        public TaskBase ActiveTask;
    }

    private Dictionary<ulong, PlayerProgressTrack> playerTracks = new Dictionary<ulong, PlayerProgressTrack>();
    private string localClientUIDescription = "tasks...";

    private void Awake()
    {
        if (Instance != null && Instance != this) { Destroy(gameObject); return; }
        Instance = this;
    }

    public override void OnNetworkSpawn()
    {
        if (!IsServer) return;

        NetworkManager.Singleton.OnClientConnectedCallback += InitializePlayerTrack;
        NetworkManager.Singleton.OnClientDisconnectCallback += CleanupPlayerTrack;

        foreach (var clientId in NetworkManager.Singleton.ConnectedClientsIds)
        {
            InitializePlayerTrack(clientId);
        }
    }

    private void InitializePlayerTrack(ulong clientId)
    {
        if (!IsServer || playerTracks.ContainsKey(clientId)) return;

        PlayerProgressTrack newTrack = new PlayerProgressTrack();
        playerTracks.Add(clientId, newTrack);

        newTrack.Queue.Add(new GoToTask("task_goto_cube", "Go to coords 3, 4, 6.", clientId, new Vector3(3f, 4f, 6f), 2.5f));
        
        EvaluateNextPlayerTask(clientId);
    }

    private void CleanupPlayerTrack(ulong clientId)
    {
        if (IsServer && playerTracks.ContainsKey(clientId))
        {
            playerTracks.Remove(clientId);
        }
    }

    private void Update()
    {
        if (!IsServer) return;

        foreach (var track in playerTracks.Values)
        {
            if (track.ActiveTask != null && !track.ActiveTask.IsCompleted)
            {
                track.ActiveTask.UpdateCheck();
            }
        }
    }

    private void EvaluateNextPlayerTask(ulong clientId)
    {
        if (!playerTracks.TryGetValue(clientId, out var track)) return;

        if (track.Queue.Count > 0)
        {
            track.ActiveTask = track.Queue[0];
            track.Queue.RemoveAt(0);

            track.ActiveTask.OnTaskCompleted += () => HandleTaskCompletion(clientId);
            
            UpdateTaskUIClientRpc(track.ActiveTask.TaskDescription, new ClientRpcParams { Send = new ClientRpcSendParams { TargetClientIds = new[] { clientId } } });
        }
        else
        {
            track.ActiveTask = null;
            UpdateTaskUIClientRpc("🎉 All sandbox operations complete!", new ClientRpcParams { Send = new ClientRpcSendParams { TargetClientIds = new[] { clientId } } });
        }
    }

    private void HandleTaskCompletion(ulong clientId)
    {
        EvaluateNextPlayerTask(clientId);
    }

    [ClientRpc]
    private void UpdateTaskUIClientRpc(string description, ClientRpcParams rpcParams = default)
    {
        localClientUIDescription = description;
    }
    public string GetCurrentLocalTaskDescription()
    {
        return localClientUIDescription;
    }

    public override void OnDestroy()
    {
        if (NetworkManager.Singleton != null)
        {
            NetworkManager.Singleton.OnClientConnectedCallback -= InitializePlayerTrack;
            NetworkManager.Singleton.OnClientDisconnectCallback -= CleanupPlayerTrack;
        }
        base.OnDestroy();
    }
}