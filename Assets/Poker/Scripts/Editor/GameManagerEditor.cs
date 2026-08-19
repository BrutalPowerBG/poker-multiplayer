using UnityEngine;
using UnityEditor;
using Unity.Netcode;

[CustomEditor(typeof(GameManager))]
public class GameManagerEditor : Editor
{
    private bool showPlayersList = true;
    
    public override void OnInspectorGUI()
    {
        DrawDefaultInspector();
        
        EditorGUILayout.Space();
        EditorGUILayout.LabelField("Game Controls", EditorStyles.boldLabel);
        
        GameManager gameManager = (GameManager)target;
        
        // Start Game Button
        GUI.enabled = Application.isPlaying;
        if (GUILayout.Button("Start Game", GUILayout.Height(40)))
        {
            gameManager.StartGameServerRpc();
            EditorUtility.SetDirty(gameManager);
        }
        GUI.enabled = true;
        
        // Player Actions Section
        EditorGUILayout.Space();
        EditorGUILayout.LabelField("Player Actions", EditorStyles.boldLabel);
        
        GUI.enabled = Application.isPlaying && gameManager.hasLocalPlayer;
        if (GUILayout.Button("Place Bet (2.65) - Local Player", GUILayout.Height(40)))
        {
            gameManager.Bet(2.65f);
            EditorUtility.SetDirty(gameManager);
        }
        GUI.enabled = true;
        
        if (!Application.isPlaying)
        {
            EditorGUILayout.HelpBox("Game controls and player actions are only available in Play Mode.", MessageType.Info);
        }
        else if (!gameManager.hasLocalPlayer)
        {
            EditorGUILayout.HelpBox("No local player found. Please sit at a table to use player actions.", MessageType.Warning);
        }
        else
        {
            EditorGUILayout.HelpBox($"Local player at slot {gameManager.localPlayer.realSlotIndex} with stack ${gameManager.localPlayer.stack:F2}", MessageType.Info);
        }
        
        // Table Actions Section
        EditorGUILayout.Space();
        EditorGUILayout.LabelField("Table Actions", EditorStyles.boldLabel);
        
        GUI.enabled = Application.isPlaying;
        if (GUILayout.Button("Collect Bets to Pot", GUILayout.Height(40)))
        {
            gameManager.CollectBetsToTablePot();
            EditorUtility.SetDirty(gameManager);
        }
        GUI.enabled = true;
        
        if (!Application.isPlaying)
        {
            EditorGUILayout.HelpBox("Table actions are only available in Play Mode.", MessageType.Info);
        }
        else
        {
            EditorGUILayout.HelpBox($"Collects all bets from player endpoints and animates them to the pot. Current pot: ${gameManager.potOnTable.Value:F2}", MessageType.Info);
        }
        
        // Players Overview Section
        EditorGUILayout.Space();
        showPlayersList = EditorGUILayout.Foldout(showPlayersList, "Players Overview", true, EditorStyles.foldoutHeader);
        
        if (showPlayersList && Application.isPlaying)
        {
            EditorGUI.indentLevel++;
            
            if (gameManager.players != null && gameManager.players.Count > 0)
            {
                EditorGUILayout.BeginVertical(EditorStyles.helpBox);
                EditorGUILayout.LabelField($"Total Players: {gameManager.players.Count}", EditorStyles.boldLabel);
                EditorGUILayout.Space(5);
                
                for (int i = 0; i < gameManager.players.Count; i++)
                {
                    var player = gameManager.players[i];
                    
                    // Check if this player is the local player
                    bool isLocalPlayer = gameManager.hasLocalPlayer && 
                                        player.realSlotIndex == gameManager.localPlayer.realSlotIndex;
                    
                    EditorGUILayout.BeginVertical(GUI.skin.box);
                    
                    // Player header with index
                    string headerText = $"Player {i}";
                    if (isLocalPlayer)
                    {
                        headerText += " (Local Player)";
                    }
                    EditorGUILayout.LabelField(headerText, EditorStyles.boldLabel);
                    
                    EditorGUI.indentLevel++;
                    
                    // Player details
                    EditorGUILayout.LabelField("Name:", player.playerName.ToString());
                    EditorGUILayout.LabelField("Real Slot Index:", player.realSlotIndex.ToString());
                    EditorGUILayout.LabelField("Stack:", $"${player.stack:F2}");
                    EditorGUILayout.LabelField("Icon Index:", player.iconIndex.ToString());
                    
                    // Display cards if they've been dealt
                    EditorGUILayout.Space(3);
                    EditorGUILayout.LabelField("Cards:", EditorStyles.boldLabel);
                    EditorGUI.indentLevel++;
                    
                    // If this is the local player on a client, show cards from localPlayer variable
                    // because NetworkList is server-authoritative and client modifications don't persist
                    Card displayCard1 = isLocalPlayer ? gameManager.localPlayer.card1 : player.card1;
                    Card displayCard2 = isLocalPlayer ? gameManager.localPlayer.card2 : player.card2;
                    
                    string card1Text = GetCardDisplayText(displayCard1);
                    string card2Text = GetCardDisplayText(displayCard2);
                    
                    EditorGUILayout.LabelField("Card 1:", card1Text);
                    EditorGUILayout.LabelField("Card 2:", card2Text);
                    
                    EditorGUI.indentLevel--;
                    
                    EditorGUI.indentLevel--;
                    
                    EditorGUILayout.EndVertical();
                    
                    if (i < gameManager.players.Count - 1)
                    {
                        EditorGUILayout.Space(5);
                    }
                }
                
                EditorGUILayout.EndVertical();
            }
            else
            {
                EditorGUILayout.HelpBox("No players in the game.", MessageType.Info);
            }
            
            EditorGUI.indentLevel--;
        }
        else if (showPlayersList && !Application.isPlaying)
        {
            EditorGUI.indentLevel++;
            EditorGUILayout.HelpBox("Players list is only available in Play Mode.", MessageType.Info);
            EditorGUI.indentLevel--;
        }
        
        // Game State Info
        EditorGUILayout.Space();
        EditorGUILayout.LabelField("Game State", EditorStyles.boldLabel);
        
        if (Application.isPlaying)
        {
            EditorGUILayout.BeginVertical(EditorStyles.helpBox);
            EditorGUILayout.LabelField("Players Count:", gameManager.playersCount.Value.ToString());
            EditorGUILayout.LabelField("Max Players:", gameManager.maxPlayersCount.ToString());
            EditorGUILayout.LabelField("Game Started:", gameManager.isGameStarted.Value ? "Yes" : "No");
            EditorGUILayout.LabelField("Is Server:", gameManager.IsServer ? "Yes" : "No");
            EditorGUILayout.LabelField("Is Client:", gameManager.IsClient ? "Yes" : "No");
            
            if (gameManager.localPlayer.playerName.Length > 0)
            {
                EditorGUILayout.Space(5);
                EditorGUILayout.LabelField("Local Player:", EditorStyles.boldLabel);
                EditorGUI.indentLevel++;
                EditorGUILayout.LabelField("Name:", gameManager.localPlayer.playerName.ToString());
                EditorGUILayout.LabelField("Slot:", gameManager.localPlayer.realSlotIndex.ToString());
                EditorGUILayout.LabelField("Stack:", $"${gameManager.localPlayer.stack:F2}");
                
                // Display local player's cards
                EditorGUILayout.Space(3);
                EditorGUILayout.LabelField("Cards:");
                EditorGUI.indentLevel++;
                EditorGUILayout.LabelField("Card 1:", GetCardDisplayText(gameManager.localPlayer.card1));
                EditorGUILayout.LabelField("Card 2:", GetCardDisplayText(gameManager.localPlayer.card2));
                EditorGUI.indentLevel--;
                
                EditorGUI.indentLevel--;
            }
            
            EditorGUILayout.EndVertical();
        }
        else
        {
            EditorGUILayout.HelpBox("Game state info is only available in Play Mode.", MessageType.Info);
        }
        
        // Force repaint during play mode to keep inspector updated
        if (Application.isPlaying)
        {
            Repaint();
        }
    }
    
    /// <summary>
    /// Helper method to format card display text with emojis for suits
    /// </summary>
    private string GetCardDisplayText(Card card)
    {
        // Check for None state (not dealt, folded, etc.)
        if (card.rank == Card.Rank.None || card.suit == Card.Suit.None)
        {
            return "∅ (No Card)";
        }
        
        // Check for Hidden state (face-down)
        if (card.rank == Card.Rank.Hidden && card.suit == Card.Suit.Hidden)
        {
            return "🂠 (Hidden)";
        }
        
        // Get suit emoji
        string suitEmoji = card.suit switch
        {
            Card.Suit.Hearts => "♥️",
            Card.Suit.Diamonds => "♦️",
            Card.Suit.Clubs => "♣️",
            Card.Suit.Spades => "♠️",
            _ => "?"
        };
        
        // Get rank text
        string rankText = card.rank switch
        {
            Card.Rank.Ace => "Ace",
            Card.Rank.King => "King",
            Card.Rank.Queen => "Queen",
            Card.Rank.Jack => "Jack",
            Card.Rank.Ten => "10",
            Card.Rank.Nine => "9",
            Card.Rank.Eight => "8",
            Card.Rank.Seven => "7",
            Card.Rank.Six => "6",
            Card.Rank.Five => "5",
            Card.Rank.Four => "4",
            Card.Rank.Three => "3",
            Card.Rank.Two => "2",
            _ => card.rank.ToString()
        };
        
        return $"{rankText} {suitEmoji} ({card.suit})";
    }
}
