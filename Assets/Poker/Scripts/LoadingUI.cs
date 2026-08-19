using System.Collections;
using System.Collections.Generic;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

// ═══════════════════════════════════════════════════════════════════
//  Loading UI
//
//  Attach to any persistent GameObject in the scene.
//
//  Setup:
//    • "loadingBackground"  — the root GameObject to show/hide.
//    • "loadingFillImage"   — an Image with Image Type = Filled.
//      The fill amount pulses between 0.5 and 1 while loading.
//
//  Usage:
//    LoadingUI.Instance.StartLoading();
//    LoadingUI.Instance.StopLoading();
// ═══════════════════════════════════════════════════════════════════

public class LoadingUI : MonoBehaviour
{
    public static LoadingUI Instance { get; private set; }

    [Header("References")]
    [Tooltip("Root GameObject that contains the entire loading overlay.")]
    [SerializeField] private GameObject loadingBackground;

    [Tooltip("Image with Image Type = Filled. Fill amount pulses 0.5 ↔ 1 while loading.")]
    [SerializeField] private Image loadingFillImage;

    [Header("Animation")]
    [Tooltip("How long (seconds) one pulse cycle takes (0.5 → 1 → 0.5).")]
    [SerializeField] private float pulseDuration = 1.2f;

    [SerializeField] private TextMeshProUGUI loadingText;

    [Tooltip("Seconds between each dot being appended.")]
    [SerializeField] private float dotInterval = 0.4f;

    [Header("Sprite Animation")]
    [Tooltip("Sliced sprites from the sprite sheet (Sprite Mode: Multiple). Indices 0-3.")]
    [SerializeField] private Sprite[] frameSprites;

    [Tooltip("Images whose sprite is updated on each pulse iteration.")]
    [SerializeField] private List<Image> animatedImages;

    private int currentFrame;
    private Coroutine pulseCoroutine;
    private Coroutine dotsCoroutine;
    private string baseLoadingText = "";

    // ══════════════════════════════════════════════════════════════
    //  Unity lifecycle
    // ══════════════════════════════════════════════════════════════

    private void Awake()
    {
        if (Instance != null && Instance != this)
        {
            Destroy(gameObject);
            return;
        }
        Instance = this;
        DontDestroyOnLoad(gameObject);

        // Ensure hidden on start
        if (loadingBackground != null)
            loadingBackground.SetActive(false);

        if (loadingFillImage != null)
            loadingFillImage.fillAmount = 0.5f;
    }

    // ══════════════════════════════════════════════════════════════
    //  Public API
    // ══════════════════════════════════════════════════════════════

    /// <summary>
    /// Shows the loading overlay and starts the fill-pulse animation.
    /// Safe to call multiple times — won't start a duplicate coroutine.
    /// </summary>
    /// <summary>
    /// Shows the loading overlay, starts the fill-pulse, and animates dots on the text.
    /// The text parameter overrides the Inspector-assigned loadingText reference.
    /// Call the no-argument overload to use whatever is assigned in the Inspector.
    /// </summary>
    public void StartLoading(TextMeshProUGUI text)
    {
        loadingText = text;
        StartLoading();
    }

    /// <summary>
    /// Shows the loading overlay and starts the fill-pulse animation.
    /// Uses the loadingText reference assigned in the Inspector (if any).
    /// </summary>
    public void StartLoading()
    {
        if (loadingBackground != null)
            loadingBackground.SetActive(true);

        if (loadingFillImage != null)
            loadingFillImage.fillAmount = 0.5f;

        currentFrame = 0;
        ApplyFrame(currentFrame);

        if (pulseCoroutine != null)
            StopCoroutine(pulseCoroutine);
        pulseCoroutine = StartCoroutine(PulseRoutine());

        // Capture the label's current text as the base (strips any leftover dots)
        if (loadingText != null)
            baseLoadingText = StripDots(loadingText.text);

        if (dotsCoroutine != null)
            StopCoroutine(dotsCoroutine);
        dotsCoroutine = StartCoroutine(DotsRoutine());
    }

    /// <summary>
    /// Snaps fill to 1, stops the animation, and hides the loading overlay.
    /// </summary>
    public void StopLoading()
    {
        if (pulseCoroutine != null)
        {
            StopCoroutine(pulseCoroutine);
            pulseCoroutine = null;
        }

        if (dotsCoroutine != null)
        {
            StopCoroutine(dotsCoroutine);
            dotsCoroutine = null;
        }

        // Restore the text to the clean base (no trailing dots)
        if (loadingText != null)
            loadingText.text = baseLoadingText;

        if (loadingFillImage != null)
            loadingFillImage.fillAmount = 1f;

        if (loadingBackground != null)
            loadingBackground.SetActive(false);
    }

    // ══════════════════════════════════════════════════════════════
    //  Animation
    // ══════════════════════════════════════════════════════════════

    private IEnumerator PulseRoutine()
    {
        float halfCycle = pulseDuration * 0.5f;

        while (true)
        {
            // 0.5 → 1
            yield return StartCoroutine(AnimateFill(0.5f, 1f, halfCycle));

            // 1 → 0.5
            yield return StartCoroutine(AnimateFill(1f, 0.5f, halfCycle));

            // Advance sprite frame (0 → 1 → 2 → 3 → 0 → …) at the end of each full cycle
            if (frameSprites != null && frameSprites.Length > 0)
            {
                currentFrame = (currentFrame + 1) % Mathf.Min(frameSprites.Length, 4);
                ApplyFrame(currentFrame);
            }
        }
    }

    private IEnumerator DotsRoutine()
    {
        int dotCount = 0;
        var wait = new WaitForSecondsRealtime(dotInterval);

        while (true)
        {
            dotCount = (dotCount % 3) + 1; // cycles 1 → 2 → 3 → 1 → …

            if (loadingText != null)
                loadingText.text = baseLoadingText + new string('.', dotCount);

            yield return wait;
        }
    }

    /// <summary>Removes trailing dots from a string.</summary>
    private static string StripDots(string s)
    {
        if (string.IsNullOrEmpty(s)) return s;
        return s.TrimEnd('.');
    }

    /// <summary>Sets the sprite on all animatedImages to frameSprites[index].</summary>
    private void ApplyFrame(int index)
    {
        if (frameSprites == null || index < 0 || index >= frameSprites.Length) return;
        if (animatedImages == null) return;

        Sprite s = frameSprites[index];
        for (int i = 0; i < animatedImages.Count; i++)
        {
            if (animatedImages[i] != null)
                animatedImages[i].sprite = s;
        }
    }

    private IEnumerator AnimateFill(float from, float to, float duration)
    {
        float elapsed = 0f;
        while (elapsed < duration)
        {
            elapsed += Time.unscaledDeltaTime; // unscaled so it works during Time.timeScale = 0
            float t = Mathf.SmoothStep(0f, 1f, elapsed / duration);
            if (loadingFillImage != null)
                loadingFillImage.fillAmount = Mathf.Lerp(from, to, t);
            yield return null;
        }

        if (loadingFillImage != null)
            loadingFillImage.fillAmount = to;
    }
}
