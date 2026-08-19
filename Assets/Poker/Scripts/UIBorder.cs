using UnityEngine;
using UnityEngine.UI;
#if UNITY_EDITOR
using UnityEditor;
#endif

/// <summary>
/// Attach to any UI Image to give it a border.
/// The Image on this object becomes the border, and an auto-created inner child
/// (inset by borderWidth) becomes the content fill.
/// All settings are live in the Inspector.
/// </summary>
[ExecuteAlways]
[RequireComponent(typeof(Image))]
public class UIBorder : MonoBehaviour
{
    private const string INNER_CHILD_NAME = "_UIBorder_Inner";

    [Header("Border")]
    [SerializeField] private Color borderColor = Color.white;
    [SerializeField, Range(0f, 50f)] private float borderWidth = 2f;

    [Header("Fill")]
    [SerializeField] private Color fillColor = new Color(0.2f, 0.2f, 0.2f, 1f);

    [Header("Border Animation")]
    [SerializeField] private bool animateBorder = true;
    [SerializeField, Range(0.01f, 0.5f)] private float brightnessRange = 0.35f;
    [SerializeField, Range(0.1f, 5f)] private float animationSpeed = 0.25f;
    [SerializeField] private bool randomizeStart = false;

    [HideInInspector]
    [SerializeField] private bool initialized = false;

    private Image borderImage;
    private Image innerImage;
    private float animationTimeOffset;

    /// <summary>
    /// Called by Unity when the component is first added or reset.
    /// Captures the Image's current color as the fill color before Apply can overwrite it.
    /// </summary>
    private void Reset()
    {
        Image img = GetComponent<Image>();
        if (img != null)
        {
            fillColor = img.color;
        }
        initialized = true;
        EnsureInnerChild();
        Apply();
    }

    private void OnEnable()
    {
        // Skip on first add — Reset() hasn't run yet so the image color hasn't been captured
        if (!initialized) return;

        animationTimeOffset = randomizeStart ? Random.Range(0f, 100f) : 0f;

        EnsureInnerChild();
        Apply();
    }

    private void OnValidate()
    {
#if UNITY_EDITOR
        EditorApplication.delayCall += () =>
        {
            if (this != null && gameObject != null)
            {
                EnsureInnerChild();
                Apply();
            }
        };
#endif
    }

    private void Update()
    {
#if UNITY_EDITOR
        // Keep in sync while selecting in editor (handles undo, sprite changes, etc.)
        if (!Application.isPlaying && initialized)
        {
            EnsureInnerChild();
            Apply();
        }
#endif

        if (Application.isPlaying && animateBorder && initialized)
        {
            ApplyAnimatedBorder();
        }
    }

    private void ApplyAnimatedBorder()
    {
        if (borderImage == null) return;

        // Convert border color to HSV
        Color.RGBToHSV(borderColor, out float h, out float s, out float v);

        // Oscillate the brightness (V) using a sine wave with per-instance offset
        float offset = Mathf.Sin((Time.time + animationTimeOffset) * animationSpeed * Mathf.PI * 2f) * brightnessRange;
        float animatedV = Mathf.Clamp01(v + offset);

        borderImage.color = Color.HSVToRGB(h, s, animatedV);
        borderImage.color = new Color(borderImage.color.r, borderImage.color.g, borderImage.color.b, borderColor.a);
    }

    private void EnsureInnerChild()
    {
        borderImage = GetComponent<Image>();
        if (borderImage == null) return;

        Transform innerChild = transform.Find(INNER_CHILD_NAME);

        if (innerChild != null)
        {
            innerImage = innerChild.GetComponent<Image>();
            if (innerImage == null)
                innerImage = innerChild.gameObject.AddComponent<Image>();
        }
        else
        {
            GameObject innerGO = new GameObject(INNER_CHILD_NAME);
            innerGO.transform.SetParent(transform, false);
            innerGO.transform.SetAsFirstSibling();

            innerImage = innerGO.AddComponent<Image>();
            innerImage.raycastTarget = false;
        }

        // Keep sprite/type/material/aspect in sync with parent
        innerImage.sprite = borderImage.sprite;
        innerImage.type = borderImage.type;
        innerImage.material = borderImage.material;
        innerImage.preserveAspect = borderImage.preserveAspect;
    }

    private void Apply()
    {
        if (borderImage == null || innerImage == null) return;

        // Colors
        borderImage.color = borderColor;
        innerImage.color = fillColor;

        // Inset the child by borderWidth on all sides
        RectTransform innerRect = innerImage.rectTransform;
        innerRect.anchorMin = Vector2.zero;
        innerRect.anchorMax = Vector2.one;
        innerRect.offsetMin = new Vector2(borderWidth, borderWidth);
        innerRect.offsetMax = new Vector2(-borderWidth, -borderWidth);
    }

    private void OnDestroy()
    {
        // Restore the original fill color on the parent image
        Image parentImage = GetComponent<Image>();
        if (parentImage != null)
        {
            parentImage.color = fillColor;
        }

        // Clean up the inner child when the component is removed
        Transform innerChild = transform.Find(INNER_CHILD_NAME);
        if (innerChild != null)
        {
            if (Application.isPlaying)
                Destroy(innerChild.gameObject);
            else
                DestroyImmediate(innerChild.gameObject);
        }
    }

    /// <summary>
    /// Call at runtime to update the border after changing properties via code.
    /// </summary>
    public void SetBorder(Color border, Color fill, float width)
    {
        borderColor = border;
        fillColor = fill;
        borderWidth = width;
        EnsureInnerChild();
        Apply();
    }
}
