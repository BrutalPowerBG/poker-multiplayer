using System;
using System.Collections;
using System.Collections.Generic;
using TMPro;
using Unity.Services.Lobbies.Models;
using UnityEngine;
using UnityEngine.UI;

public class LobbyListUI : MonoBehaviour {


    public static LobbyListUI Instance { get; private set; }



    [SerializeField] private Transform lobbySingleTemplate;
    [SerializeField] private Transform container;
    [SerializeField] private Button refreshButton;
    [SerializeField] private Button createLobbyButton;

    [Header("Join by Code")]
    [SerializeField] private TMP_InputField joinCodeInputField;
    [SerializeField] private Button joinByCodeButton;
    [SerializeField] private TextMeshProUGUI joinCodeErrorText;


    private void Awake() {
        Instance = this;

        lobbySingleTemplate.gameObject.SetActive(false);

        refreshButton.onClick.AddListener(RefreshButtonClick);
        createLobbyButton.onClick.AddListener(CreateLobbyButtonClick);

        if (joinByCodeButton != null) {
            joinByCodeButton.onClick.AddListener(JoinByCodeButtonClick);
        }

        if (joinCodeErrorText != null) {
            joinCodeErrorText.gameObject.SetActive(false);
        }
    }

    private bool subscribedToEvents = false;

    private void Start() {
        SubscribeToEvents();
    }

    /// <summary>
    /// Called every time the panel is activated. Ensures event subscriptions
    /// are in place and resets transient UI state (error messages, etc.).
    /// </summary>
    private void OnEnable() {
        SubscribeToEvents();
        ClearJoinCodeError();

        if (LobbyManager.Instance != null)
            LobbyManager.Instance.RefreshLobbyList();
    }

    private void SubscribeToEvents() {
        if (subscribedToEvents || LobbyManager.Instance == null) return;

        LobbyManager.Instance.OnLobbyListChanged += LobbyManager_OnLobbyListChanged;
        LobbyManager.Instance.OnJoinedLobby += LobbyManager_OnJoinedLobby;
        LobbyManager.Instance.OnLeftLobby += LobbyManager_OnLeftLobby;
        LobbyManager.Instance.OnKickedFromLobby += LobbyManager_OnKickedFromLobby;
        LobbyManager.Instance.OnJoinLobbyFailed += LobbyManager_OnJoinLobbyFailed;
        subscribedToEvents = true;
    }

    private void OnDestroy() {
        if (subscribedToEvents && LobbyManager.Instance != null) {
            LobbyManager.Instance.OnLobbyListChanged -= LobbyManager_OnLobbyListChanged;
            LobbyManager.Instance.OnJoinedLobby -= LobbyManager_OnJoinedLobby;
            LobbyManager.Instance.OnLeftLobby -= LobbyManager_OnLeftLobby;
            LobbyManager.Instance.OnKickedFromLobby -= LobbyManager_OnKickedFromLobby;
            LobbyManager.Instance.OnJoinLobbyFailed -= LobbyManager_OnJoinLobbyFailed;
            subscribedToEvents = false;
        }
    }

    private void LobbyManager_OnKickedFromLobby(object sender, LobbyManager.LobbyEventArgs e) {
        // Screen transition handled by LobbyScreenManager
    }

    private void LobbyManager_OnJoinLobbyFailed(object sender, string errorMessage) {
        ShowJoinCodeError(errorMessage);
    }

    private void LobbyManager_OnLeftLobby(object sender, EventArgs e) {
        // Screen transition handled by LobbyScreenManager
    }

    private void LobbyManager_OnJoinedLobby(object sender, LobbyManager.LobbyEventArgs e) {
        // Clear the join code input on successful join
        if (joinCodeInputField != null) joinCodeInputField.text = "";
        ClearJoinCodeError();
        // Screen transition handled by LobbyScreenManager
    }

    private void LobbyManager_OnLobbyListChanged(object sender, LobbyManager.OnLobbyListChangedEventArgs e) {
        UpdateLobbyList(e.lobbyList);
    }

    private void UpdateLobbyList(List<Lobby> lobbyList) {
        foreach (Transform child in container) {
            if (child == lobbySingleTemplate) continue;

            Destroy(child.gameObject);
        }

        foreach (Lobby lobby in lobbyList) {
            Transform lobbySingleTransform = Instantiate(lobbySingleTemplate, container);
            lobbySingleTransform.gameObject.SetActive(true);
            LobbyListSingleUI lobbyListSingleUI = lobbySingleTransform.GetComponent<LobbyListSingleUI>();
            lobbyListSingleUI.UpdateLobby(lobby);
        }
    }

    private void Update() {
        // Allow pressing Enter to join by code when the input field is focused
        if (joinCodeInputField != null && joinCodeInputField.isFocused) {
            if (Input.GetKeyDown(KeyCode.Return) || Input.GetKeyDown(KeyCode.KeypadEnter)) {
                JoinByCodeButtonClick();
            }
        }
    }

    private void JoinByCodeButtonClick() {
        if (joinCodeInputField == null) return;

        string code = joinCodeInputField.text.Trim();
        if (string.IsNullOrEmpty(code)) {
            ShowJoinCodeError("Please enter a lobby code.");
            return;
        }

        ClearJoinCodeError();
        LobbyManager.Instance.JoinLobbyByCode(code);
    }

    public void ShowJoinCodeError(string message) {
        if (joinCodeErrorText != null) {
            joinCodeErrorText.text = message;
            joinCodeErrorText.gameObject.SetActive(true);
        }
    }

    private void ClearJoinCodeError() {
        if (joinCodeErrorText != null) {
            joinCodeErrorText.gameObject.SetActive(false);
        }
    }

    private void RefreshButtonClick() {
        LobbyManager.Instance.RefreshLobbyList();
    }

    private void CreateLobbyButtonClick() {
        LobbyCreateUI.Instance.Show(); // Show() internally transitions via LobbyScreenManager
    }

}