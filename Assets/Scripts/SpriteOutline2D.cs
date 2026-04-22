using UnityEngine;

[ExecuteAlways]
[RequireComponent(typeof(SpriteRenderer))]
public class SpriteOutline2D : MonoBehaviour
{
    [Header("Outline Settings")]
    [SerializeField] private Color _outlineColor = Color.red;
    [SerializeField, Range(0f, 20f)] private float _outlineThickness = 6f;
    [SerializeField, Range(0f, 1f)] private float _outlineSoftness = 0.5f;
    [SerializeField] private bool _outlineEnabled = true;

    private SpriteRenderer _spriteRenderer;
    private MaterialPropertyBlock _mpb;

    private static readonly int OutlineColorID = Shader.PropertyToID("_OutlineColor");
    private static readonly int OutlineThicknessID = Shader.PropertyToID("_OutlineThickness");
    private static readonly int OutlineEnabledID = Shader.PropertyToID("_OutlineEnabled");
    private static readonly int OutlineSoftnessID = Shader.PropertyToID("_OutlineSoftness");

    private void Awake()
    {
        _spriteRenderer = GetComponent<SpriteRenderer>();
        _mpb = new MaterialPropertyBlock();
    }

    private void OnEnable()
    {
        UpdateOutline();
    }

    private void OnValidate()
    {
        if (_spriteRenderer == null)
            _spriteRenderer = GetComponent<SpriteRenderer>();
        if (_mpb == null)
            _mpb = new MaterialPropertyBlock();
        UpdateOutline();
    }

    public void SetOutlineColor(Color color)
    {
        _outlineColor = color;
        UpdateOutline();
    }

    public void SetOutlineThickness(float thickness)
    {
        _outlineThickness = thickness;
        UpdateOutline();
    }

    public void SetOutlineEnabled(bool enabled)
    {
        _outlineEnabled = enabled;
        UpdateOutline();
    }

    public void SetOutlineSoftness(float softness)
    {
        _outlineSoftness = softness;
        UpdateOutline();
    }

    private void UpdateOutline()
    {
        if (_spriteRenderer == null) return;

        _spriteRenderer.GetPropertyBlock(_mpb);
        _mpb.SetColor(OutlineColorID, _outlineColor);
        _mpb.SetFloat(OutlineThicknessID, _outlineThickness);
        _mpb.SetFloat(OutlineEnabledID, _outlineEnabled ? 1f : 0f);
        _mpb.SetFloat(OutlineSoftnessID, _outlineSoftness);
        _spriteRenderer.SetPropertyBlock(_mpb);
    }
}
