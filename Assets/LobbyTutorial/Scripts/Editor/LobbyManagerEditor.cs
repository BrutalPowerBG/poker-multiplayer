using UnityEditor;
using UnityEngine;

[CustomEditor(typeof(LobbyManager))]
public class LobbyManagerEditor : Editor {
    public override void OnInspectorGUI() {
        DrawDefaultInspector();

        var lobbyManager = (LobbyManager)target;

        GUILayout.Space(10);
        if (GUILayout.Button("Late Join / Force Start (Debug)")) {
            lobbyManager.LateJoinGame();
        }
    }
}
