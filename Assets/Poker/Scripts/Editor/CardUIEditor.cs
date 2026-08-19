using UnityEngine;
using UnityEditor;

[CustomEditor(typeof(CardUI))]
public class CardUIEditor : Editor
{
    public override void OnInspectorGUI()
    {
        DrawDefaultInspector();
        
        EditorGUILayout.Space();
        
        CardUI cardUI = (CardUI)target;
        
        if (GUILayout.Button("Initialize Card", GUILayout.Height(40)))
        {
            cardUI.Init();
            EditorUtility.SetDirty(cardUI);
        }
        
        EditorGUILayout.Space();
        
        EditorGUILayout.BeginHorizontal();
        if (GUILayout.Button("Flip (Instant)", GUILayout.Height(40)))
        {
            cardUI.Flip(instant: true);
            EditorUtility.SetDirty(cardUI);
        }
        if (GUILayout.Button("Flip (Animated)", GUILayout.Height(40)))
        {
            cardUI.Flip(instant: false);
            EditorUtility.SetDirty(cardUI);
        }
        EditorGUILayout.EndHorizontal();
        
        EditorGUILayout.HelpBox("Initialize creates the card (with 90°→0° animation). Flip toggles between front and back (with flip animation).", MessageType.Info);
    }
}
