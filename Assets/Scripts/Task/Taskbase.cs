using UnityEngine;
using System;

[Serializable]
public abstract class TaskBase
{
    public string TaskID { get; protected set; }
    public string TaskDescription { get; protected set; }
    public bool IsCompleted { get; protected set; }
    public ulong TargetPlayerId { get; protected set; }

    public event Action OnTaskCompleted;

    public TaskBase(string id, string description, ulong playerId)
    {
        TaskID = id;
        TaskDescription = description;
        TargetPlayerId = playerId;
        IsCompleted = false;
    }

    public abstract void UpdateCheck();

    protected void CompleteTask()
    {
        if (IsCompleted) return;
        IsCompleted = true;
        OnTaskCompleted?.Invoke();
    }
}