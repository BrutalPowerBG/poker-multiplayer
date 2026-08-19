using TMPro;
using UnityEngine;
using UnityEngine.UI;

/// <summary>
/// Attach to a GameObject that has a <see cref="Slider"/> and <see cref="Toggle"/> as children
/// (or assign references in the inspector). Updates <see cref="SoundManager"/> on user input.
/// </summary>
public class AudioSettingsUI : MonoBehaviour
{
    [SerializeField] private Slider volumeSlider;
    [SerializeField] private Toggle audioEnabledToggle;
    [Tooltip("Shows effective volume as a whole-number percent (0–100%). Leave empty to hide.")]
    [SerializeField] private TMP_Text volumePercentText;

    void Awake()
    {
        if (volumeSlider == null)
            volumeSlider = GetComponentInChildren<Slider>(true);
        if (audioEnabledToggle == null)
            audioEnabledToggle = GetComponentInChildren<Toggle>(true);
        if (volumePercentText == null)
            volumePercentText = GetComponentInChildren<TMP_Text>(true);
    }

    void OnEnable()
    {
        var sm = SoundManager.Instance ?? SoundManager.GetOrCreate();
        RefreshUIFromSoundManager(sm);

        if (volumeSlider != null)
            volumeSlider.onValueChanged.AddListener(OnVolumeSliderChanged);
        if (audioEnabledToggle != null)
            audioEnabledToggle.onValueChanged.AddListener(OnAudioEnabledToggleChanged);

        if (SoundManager.Instance != null)
            SoundManager.Instance.SettingsChanged += OnSoundManagerSettingsChanged;
    }

    void OnDisable()
    {
        if (volumeSlider != null)
            volumeSlider.onValueChanged.RemoveListener(OnVolumeSliderChanged);
        if (audioEnabledToggle != null)
            audioEnabledToggle.onValueChanged.RemoveListener(OnAudioEnabledToggleChanged);

        if (SoundManager.Instance != null)
            SoundManager.Instance.SettingsChanged -= OnSoundManagerSettingsChanged;
    }

    void OnSoundManagerSettingsChanged()
    {
        if (SoundManager.Instance != null)
            RefreshUIFromSoundManager(SoundManager.Instance);
    }

    void RefreshUIFromSoundManager(SoundManager sm)
    {
        if (volumeSlider != null)
        {
            float v = Mathf.Lerp(volumeSlider.minValue, volumeSlider.maxValue, sm.MasterVolume);
            volumeSlider.SetValueWithoutNotify(v);
        }

        if (audioEnabledToggle != null)
            audioEnabledToggle.SetIsOnWithoutNotify(sm.AudioEnabled);

        UpdateVolumePercentText(sm);
    }

    void UpdateVolumePercentText(SoundManager sm)
    {
        if (volumePercentText == null)
            return;
        int pct = Mathf.Clamp(Mathf.RoundToInt(sm.EffectiveVolume * 100f), 0, 100);
        volumePercentText.text = $"{pct}%";
    }

    void OnVolumeSliderChanged(float value)
    {
        if (volumeSlider == null || SoundManager.Instance == null)
            return;
        float normalized = Mathf.InverseLerp(volumeSlider.minValue, volumeSlider.maxValue, value);
        SoundManager.Instance.SetMasterVolume(normalized);
    }

    void OnAudioEnabledToggleChanged(bool enabled)
    {
        if (SoundManager.Instance != null)
            SoundManager.Instance.SetAudioEnabled(enabled);
    }
}
