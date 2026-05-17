using UnityEngine;

public class AccountManagerUI : MonoBehaviour
{
    private string inputUser = "";
    private string inputPass = "";
    private string statusMessage = "Log in or Sign up.";

    private void OnGUI()
    {
        if (AccountSystem.Instance != null && AccountSystem.Instance.IsLoggedIn)
        {
            return; 
        }

        GUILayout.BeginArea(new Rect(Screen.width / 2 - 150, Screen.height / 2 - 160, 300, 220), "test/login", GUI.skin.window);
        
        GUILayout.Space(10);
        GUILayout.Label("Username:");
        inputUser = GUILayout.TextField(inputUser, 24);

        GUILayout.Space(5);
        GUILayout.Label("Password:");
        inputPass = GUILayout.PasswordField(inputPass, '*', 32);

        GUILayout.Space(15);
        GUILayout.BeginHorizontal();
        
        if (GUILayout.Button("Log In"))
        {
            if (AccountSystem.Instance.AttemptLogin(inputUser, inputPass))
            {
                statusMessage = $"{AccountSystem.Instance.CurrentPlayer.DisplayName}";
                ServerMovement localPlayer = FindObjectOfType<ServerMovement>();
                if (localPlayer != null)
                {
                    localPlayer.InitializeNameplate(
                        AccountSystem.Instance.CurrentPlayer.DisplayName, 
                        AccountSystem.Instance.CurrentPlayer.PlayerLevel
                    );
                }
            }
            else
            {
                statusMessage = "Invalid.";
            }
        }

        if (GUILayout.Button("Register"))
        {
            if (AccountSystem.Instance.RegisterNewAccount(inputUser, inputPass))
            {
                statusMessage = "Login.";
            }
            else
            {
                statusMessage = "Invalid sign up";
            }
        }
        GUILayout.EndHorizontal();

        GUILayout.Space(15);
        GUILayout.Label($"Status: {statusMessage}");
        GUILayout.EndArea();
    }
}