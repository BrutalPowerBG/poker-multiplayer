using System;
using UnityEngine;

/// <summary>
/// Central audio settings: place on a root GameObject in your first-loaded scene (or bootstrap).
/// Persists across scenes via DontDestroyOnLoad. Menus call SetMasterVolume / SetAudioEnabled.
/// </summary>
public class SoundManager : MonoBehaviour
{
    public static SoundManager Instance { get; private set; }

    private const string PREF_MASTER_VOLUME = "SoundManager_MasterVolume";
    private const string PREF_AUDIO_ENABLED = "SoundManager_AudioEnabled";

    [SerializeField, Range(0f, 1f)] private float defaultMasterVolume = 1f;
    [SerializeField] private bool defaultAudioEnabled = true;

    [SerializeField, Range(0f, 1f)] private float masterVolume = 1f;
    [SerializeField] private bool audioEnabled = true;

    /// <summary>0–1 linear master level when audio is enabled.</summary>
    public float MasterVolume
    {
        get => masterVolume;
        set
        {
            masterVolume = Mathf.Clamp01(value);
            SaveAndApply();
        }
    }

    /// <summary>When false, output is silent (menus can show this as "muted").</summary>
    public bool AudioEnabled
    {
        get => audioEnabled;
        set
        {
            audioEnabled = value;
            SaveAndApply();
        }
    }

    /// <summary>Volume actually applied to AudioListener (0 if disabled).</summary>
    public float EffectiveVolume => audioEnabled ? masterVolume : 0f;

    public event Action SettingsChanged;

    void Awake()
    {
        if (Instance != null && Instance != this)
        {
            Destroy(gameObject);
            return;
        }

        Instance = this;
        DontDestroyOnLoad(gameObject);
        LoadSettings();
        ApplyToListener();
    }

    void OnDestroy()
    {
        if (Instance == this)
            Instance = null;
    }

    public void SetMasterVolume(float volume)
    {
        MasterVolume = volume;
    }

    public void SetAudioEnabled(bool enabled)
    {
        AudioEnabled = enabled;
    }

    public void ToggleAudioEnabled()
    {
        AudioEnabled = !audioEnabled;
    }

    /// <summary>Re-read prefs and push to the listener (e.g. after external PlayerPrefs changes).</summary>
    public void ReloadFromStorage()
    {
        LoadSettings();
        ApplyToListener();
        SettingsChanged?.Invoke();
    }

    void LoadSettings()
    {
        if (PlayerPrefs.HasKey(PREF_MASTER_VOLUME))
            masterVolume = Mathf.Clamp01(PlayerPrefs.GetFloat(PREF_MASTER_VOLUME));
        else
            masterVolume = Mathf.Clamp01(defaultMasterVolume);

        if (PlayerPrefs.HasKey(PREF_AUDIO_ENABLED))
            audioEnabled = PlayerPrefs.GetInt(PREF_AUDIO_ENABLED) != 0;
        else
            audioEnabled = defaultAudioEnabled;
    }

    void SaveAndApply()
    {
        PlayerPrefs.SetFloat(PREF_MASTER_VOLUME, masterVolume);
        PlayerPrefs.SetInt(PREF_AUDIO_ENABLED, audioEnabled ? 1 : 0);
        PlayerPrefs.Save();
        ApplyToListener();
        SettingsChanged?.Invoke();
    }

    void ApplyToListener()
    {
        AudioListener.volume = EffectiveVolume;
    }

    /// <summary>Creates a SoundManager in play mode if none exists (optional bootstrap).</summary>
    public static SoundManager GetOrCreate()
    {
        if (Instance != null)
            return Instance;

        var go = new GameObject("SoundManager");
        return go.AddComponent<SoundManager>();
    }
}
