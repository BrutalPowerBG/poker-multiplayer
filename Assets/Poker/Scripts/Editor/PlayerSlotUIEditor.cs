using UnityEngine;
using UnityEditor;

[CustomEditor(typeof(PlayerSlotUI))]
public class PlayerSlotUIEditor : Editor
{
    public override void OnInspectorGUI()
    {
        DrawDefaultInspector();
        
        EditorGUILayout.Space();
        
        PlayerSlotUI playerSlot = (PlayerSlotUI)target;
        
        EditorGUILayout.BeginHorizontal();
        
        if (GUILayout.Button("Start Timer", GUILayout.Height(40)))
        {
            playerSlot.StartTimer();
            EditorUtility.SetDirty(playerSlot);
        }
        
        if (GUILayout.Button("Pause Timer", GUILayout.Height(40)))
        {
            playerSlot.PauseTimer();
            EditorUtility.SetDirty(playerSlot);
        }
        
        EditorGUILayout.EndHorizontal();
        
        EditorGUILayout.BeginHorizontal();
        
        if (GUILayout.Button("Resume Timer", GUILayout.Height(40)))
        {
            playerSlot.ResumeTimer();
            EditorUtility.SetDirty(playerSlot);
        }
        
        if (GUILayout.Button("Stop Timer", GUILayout.Height(40)))
        {
            playerSlot.StopTimer();
            EditorUtility.SetDirty(playerSlot);
        }
        
        EditorGUILayout.EndHorizontal();
        
        EditorGUILayout.HelpBox("Start: Begin countdown | Pause: Freeze timer | Resume: Continue countdown | Stop: Reset and hide timer", MessageType.Info);
    }
}
