using System;
using System.Collections.Generic;

[Serializable]
public class PlayerData
{
    public string Username;
    public string EncryptedPassword;
    public string PlayerID;
    public string DisplayName;
    public int PlayerLevel;
    public int ElevatedPoints;
    public List<string> CompletedTaskIDs; 
    public PlayerData(string username, string encryptedPassword, string displayName)
    {
        Username = username;
        EncryptedPassword = encryptedPassword;
        PlayerID = Guid.NewGuid().ToString();
        DisplayName = displayName;
        PlayerLevel = 0;
        ElevatedPoints = 0;
        CompletedTaskIDs = new List<string>();
    }
}
[Serializable]
public class ProfileRegistryWrapper
{
    public List<PlayerData> RegisteredAccounts = new List<PlayerData>();
}