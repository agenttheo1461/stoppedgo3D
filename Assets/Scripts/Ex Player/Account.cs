using UnityEngine;
using System;
using System.IO;
using System.Text;
using System.Security.Cryptography;
using System.Collections.Generic;

public class AccountSystem : MonoBehaviour
{
    public static AccountSystem Instance { get; private set; }
    [Header("Developer Access Verification")]
    [SerializeField] private List<string> devUsernames = new List<string> { "Ted", "Test123" };
    private string saveFilePath;
    private ProfileRegistryWrapper registry = new ProfileRegistryWrapper();
    public PlayerData CurrentPlayer { get; private set; }
    public bool IsLoggedIn => CurrentPlayer != null;

    private readonly byte[] cryptoKey = Encoding.UTF8.GetBytes("A6tg9p0X2zM7kLqw1vbnmP09OlKiJuYh"); 
    private readonly byte[] cryptoIV = Encoding.UTF8.GetBytes("7u8i9o0p1q2w3e4r"); 

    private void Awake()
    {
        if (Instance != null && Instance != this) { Destroy(gameObject); return; }
        Instance = this;
        DontDestroyOnLoad(gameObject);

        saveFilePath = Path.Combine(Application.persistentDataPath, "user_registry.dat");
        LoadRegistryFromDisk();
    }

    public bool RegisterNewAccount(string username, string password)
    {
        string normalizedUser = username.Trim();
        if (string.IsNullOrEmpty(normalizedUser) || string.IsNullOrEmpty(password)) return false;

        if (registry.RegisteredAccounts.Exists(p => p.Username.ToLower() == normalizedUser.ToLower()))
        {
            Debug.LogWarning("Username already exists.");
            return false;
        }

        string finalDisplayName = normalizedUser;
        if (IsUserDeveloper(normalizedUser))
        {
            finalDisplayName = $"[Dev] {normalizedUser}";
        }

        string encryptedPass = EncryptString(password);
        PlayerData newAccount = new PlayerData(normalizedUser, encryptedPass, finalDisplayName);
        
        registry.RegisteredAccounts.Add(newAccount);
        SaveRegistryToDisk();
        
        Debug.Log($"128-bit ID: {newAccount.PlayerID}");
        return true;
    }

    public bool AttemptLogin(string username, string password)
    {
        string normalizedUser = username.Trim();
        PlayerData account = registry.RegisteredAccounts.Find(p => p.Username.ToLower() == normalizedUser.ToLower());

        if (account == null)
        {
            Debug.LogWarning("err");
            return false;
        }

        string decryptedPass = DecryptString(account.EncryptedPassword);
        if (decryptedPass == password)
        {
            if (IsUserDeveloper(normalizedUser) && !account.DisplayName.Contains("[Dev]"))
            {
                account.DisplayName = $"[Dev] {normalizedUser}";
                SaveRegistryToDisk();
            }

            CurrentPlayer = account;
            Debug.Log($"{CurrentPlayer.DisplayName} (ID: {CurrentPlayer.PlayerID})");
            return true;
        }

        Debug.LogWarning("Incorrect password.");
        return false;
    }

    public void Logout()
    {
        if (CurrentPlayer != null)
        {
            Debug.Log($"{CurrentPlayer.DisplayName} logged out");
            SaveRegistryToDisk();
            CurrentPlayer = null;
        }
    }

    private bool IsUserDeveloper(string username)
    {
        return devUsernames.Exists(devName => devName.Equals(username, StringComparison.OrdinalIgnoreCase));
    }

    public void SaveRegistryToDisk()
    {
        try
        {
            string rawJson = JsonUtility.ToJson(registry, true);
            string encryptedJson = EncryptString(rawJson);
            File.WriteAllText(saveFilePath, encryptedJson);
        }
        catch (Exception e)
        {
            Debug.LogError($"Err with disk: {e.Message}");
        }
    }

    private void LoadRegistryFromDisk()
    {
        if (!File.Exists(saveFilePath))
        {
            registry = new ProfileRegistryWrapper();
            return;
        }
        try
        {
            string encryptedJson = File.ReadAllText(saveFilePath);
            string decryptedJson = DecryptString(encryptedJson);
            registry = JsonUtility.FromJson<ProfileRegistryWrapper>(decryptedJson);
        }
        catch (Exception)
        {
            Debug.LogError("new storage");
            registry = new ProfileRegistryWrapper();
        }
    }

    private string EncryptString(string plainText)
    {
        using (Aes aes = Aes.Create())
        {
            aes.Key = cryptoKey;
            aes.IV = cryptoIV;
            ICryptoTransform encryptor = aes.CreateEncryptor(aes.Key, aes.IV);
            using (MemoryStream ms = new MemoryStream())
            {
                using (CryptoStream cs = new CryptoStream(ms, encryptor, CryptoStreamMode.Write))
                {
                    using (StreamWriter sw = new StreamWriter(cs))
                    {
                        sw.Write(plainText);
                    }
                    return Convert.ToBase64String(ms.ToArray());
                }
            }
        }
    }

    private string DecryptString(string cipherText)
    {
        using (Aes aes = Aes.Create())
        {
            aes.Key = cryptoKey;
            aes.IV = cryptoIV;
            ICryptoTransform decryptor = aes.CreateDecryptor(aes.Key, aes.IV);
            using (MemoryStream ms = new MemoryStream(Convert.FromBase64String(cipherText)))
            {
                using (CryptoStream cs = new CryptoStream(ms, decryptor, CryptoStreamMode.Read))
                {
                    using (StreamReader sr = new StreamReader(cs))
                    {
                        return sr.ReadToEnd();
                    }
                }
            }
        }
    }
}