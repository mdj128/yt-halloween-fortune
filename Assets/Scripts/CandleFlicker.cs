using UnityEngine;

/// <summary>
/// Adds a subtle procedural flicker to a candle-style light.
/// </summary>
[RequireComponent(typeof(Light))]
public class CandleFlicker : MonoBehaviour
{
    [Header("Base Light")]
    [Tooltip("Average light intensity before flicker is applied.")]
    public float baseIntensity = 1.2f;

    [Tooltip("How much the intensity varies around the base value.")]
    public float flickerStrength = 0.35f;

    [Tooltip("Speed multiplier for the flicker noise.")]
    public float flickerSpeed = 12f;

    [Tooltip("Warm candle flame colour applied every frame.")]
    public Color baseColor = new Color(1f, 0.67f, 0.38f);

    private Light _light;
    private float _noiseOffset;

    private void Awake()
    {
        CacheLight();
        _noiseOffset = Random.Range(0f, 1000f);
    }

    private void OnValidate()
    {
        CacheLight();
        if (_light != null)
        {
            _light.color = baseColor;
        }
    }

    private void Update()
    {
        if (_light == null)
        {
            return;
        }

        float time = Time.time * flickerSpeed;
        float noise = Mathf.PerlinNoise(_noiseOffset, time) - 0.5f;
        float intensity = baseIntensity + noise * 2f * flickerStrength;

        _light.intensity = Mathf.Max(0f, intensity);
        _light.color = baseColor;
    }

    private void CacheLight()
    {
        if (_light == null)
        {
            _light = GetComponent<Light>();
        }
    }
}
