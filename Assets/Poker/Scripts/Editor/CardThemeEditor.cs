using UnityEngine;
using UnityEditor;
using System.Linq;

[CustomEditor(typeof(CardTheme))]
public class CardThemeEditor : Editor
{
    public override void OnInspectorGUI()
    {
        CardTheme cardTheme = (CardTheme)target;
        
        EditorGUILayout.HelpBox("Drag PARENT textures (folder icon ►) and click 'Load Sprites' for each theme.", MessageType.Info);
        
        // Ensure array exists
        if (cardTheme.cardThemes == null)
        {
            cardTheme.cardThemes = new CardThemeData[0];
        }
        
        EditorGUILayout.Space();
        
        // Show each theme
        for (int i = 0; i < cardTheme.cardThemes.Length; i++)
        {
            EditorGUILayout.BeginVertical("box");
            
            // Header with theme name and reorder buttons
            EditorGUILayout.BeginHorizontal();

            string displayLabel = string.IsNullOrEmpty(cardTheme.cardThemes[i].themeName)
                ? $"Theme {i}"
                : $"{cardTheme.cardThemes[i].themeName}";
            EditorGUILayout.LabelField(displayLabel, EditorStyles.boldLabel);
            
            // Move up button
            GUI.enabled = i > 0;
            if (GUILayout.Button("▲", GUILayout.Width(30)))
            {
                MoveThemeUp(cardTheme, i);
                return;
            }
            
            // Move down button
            GUI.enabled = i < cardTheme.cardThemes.Length - 1;
            if (GUILayout.Button("▼", GUILayout.Width(30)))
            {
                MoveThemeDown(cardTheme, i);
                return;
            }
            GUI.enabled = true;
            
            EditorGUILayout.EndHorizontal();
            
            // Editable theme name
            string newName = EditorGUILayout.TextField("Theme Name", cardTheme.cardThemes[i].themeName);
            if (newName != cardTheme.cardThemes[i].themeName)
            {
                Undo.RecordObject(cardTheme, "Rename Theme");
                cardTheme.cardThemes[i].themeName = newName;
                EditorUtility.SetDirty(cardTheme);
            }
            
            // Texture field
            Texture2D newTexture = (Texture2D)EditorGUILayout.ObjectField(
                "Sprite Sheet", 
                cardTheme.cardThemes[i].spriteSheetTexture, 
                typeof(Texture2D), 
                false
            );
            
            if (newTexture != cardTheme.cardThemes[i].spriteSheetTexture)
            {
                Undo.RecordObject(cardTheme, "Change Sprite Sheet");
                cardTheme.cardThemes[i].spriteSheetTexture = newTexture;
                EditorUtility.SetDirty(cardTheme);
            }
            
            // Load button
            if (cardTheme.cardThemes[i].spriteSheetTexture != null)
            {
                if (GUILayout.Button("Load Sprites"))
                {
                    LoadSpritesForTheme(cardTheme, i);
                }
            }
            
            // Show sprite count
            if (cardTheme.cardThemes[i].sprites != null)
            {
                EditorGUILayout.LabelField($"Loaded: {cardTheme.cardThemes[i].sprites.Length} sprites");
            }
            
            // Remove button
            if (GUILayout.Button("Remove Theme"))
            {
                RemoveTheme(cardTheme, i);
                return;
            }
            
            EditorGUILayout.EndVertical();
            EditorGUILayout.Space();
        }
        
        // Add new theme button
        if (GUILayout.Button("+ Add New Theme", GUILayout.Height(30)))
        {
            AddNewTheme(cardTheme);
        }
        
        EditorGUILayout.Space();
        
        // Draw current theme index field
        SerializedProperty themeIndexProp = serializedObject.FindProperty("currentThemeIndex");
        EditorGUILayout.PropertyField(themeIndexProp, new GUIContent("Current Theme Index"));
        serializedObject.ApplyModifiedProperties();
    }
    
    private void LoadSpritesForTheme(CardTheme cardTheme, int themeIndex)
    {
        Texture2D texture = cardTheme.cardThemes[themeIndex].spriteSheetTexture;
        string path = AssetDatabase.GetAssetPath(texture);
        
        Object[] sprites = AssetDatabase.LoadAllAssetsAtPath(path)
            .Where(obj => obj is Sprite)
            .ToArray();
        
        if (sprites.Length == 0)
        {
            EditorUtility.DisplayDialog("Error", "No sprites found! Check texture import settings.", "OK");
            return;
        }
        
        Undo.RecordObject(cardTheme, "Load Sprites");
        cardTheme.cardThemes[themeIndex].sprites = sprites.Cast<Sprite>().ToArray();
        EditorUtility.SetDirty(cardTheme);
        
        Debug.Log($"[{LogTag.Theme}] Sprites loaded — themeIndex={themeIndex}, spriteCount={sprites.Length}", this);
    }
    
    private void AddNewTheme(CardTheme cardTheme)
    {
        Undo.RecordObject(cardTheme, "Add Theme");
        System.Array.Resize(ref cardTheme.cardThemes, cardTheme.cardThemes.Length + 1);
        cardTheme.cardThemes[cardTheme.cardThemes.Length - 1] = new CardThemeData();
        EditorUtility.SetDirty(cardTheme);
    }
    
    private void RemoveTheme(CardTheme cardTheme, int index)
    {
        Undo.RecordObject(cardTheme, "Remove Theme");
        var list = cardTheme.cardThemes.ToList();
        list.RemoveAt(index);
        cardTheme.cardThemes = list.ToArray();
        EditorUtility.SetDirty(cardTheme);
    }
    
    private void MoveThemeUp(CardTheme cardTheme, int index)
    {
        if (index <= 0) return;
        
        Undo.RecordObject(cardTheme, "Move Theme Up");
        CardThemeData temp = cardTheme.cardThemes[index];
        cardTheme.cardThemes[index] = cardTheme.cardThemes[index - 1];
        cardTheme.cardThemes[index - 1] = temp;
        EditorUtility.SetDirty(cardTheme);
    }
    
    private void MoveThemeDown(CardTheme cardTheme, int index)
    {
        if (index >= cardTheme.cardThemes.Length - 1) return;
        
        Undo.RecordObject(cardTheme, "Move Theme Down");
        CardThemeData temp = cardTheme.cardThemes[index];
        cardTheme.cardThemes[index] = cardTheme.cardThemes[index + 1];
        cardTheme.cardThemes[index + 1] = temp;
        EditorUtility.SetDirty(cardTheme);
    }
}
