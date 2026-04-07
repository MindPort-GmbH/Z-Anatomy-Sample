// StampClipSpehere.hlsl
// Top-level behavior:
// - Each stamp source GameObject provides transform.worldToLocalMatrix,
//   which reflects the source Transform (position, rotation, and scale).
// - For each pixel, the shader transforms its world position into the stamp's local space.
// - If that transformed local position lies inside a sphere centered at (0,0,0)
//   with radius 0.5, the pixel is clipped.
// - In Unity, this can be visualized with a default Sphere mesh on the source GameObject (MeshFilter).

#ifndef VIRTOSHA_STAMP_CLIP_SPEHERE_INCLUDED
#define VIRTOSHA_STAMP_CLIP_SPEHERE_INCLUDED

#ifndef MAX_SPHERE_STAMPS
#define MAX_SPHERE_STAMPS 64
#endif

float _StampClipEnabled;
float _SphereStampCount;
float4x4 _SphereStampWorldToLocal[MAX_SPHERE_STAMPS];
float _SphereStampSourceIndex[MAX_SPHERE_STAMPS];
int _StampClipSourceMask;

inline float IsInsideStampSphere(float3 stampLocalPosition)
{
    // stampLocalPosition is expected around the origin in stamp local space.
    // Radius 0.5 keeps the sphere inside the same unit bounds used before.
    float radiusSquared = 0.25;
    float distanceSquared = dot(stampLocalPosition, stampLocalPosition);
    return step(distanceSquared, radiusSquared);
}

inline float ComputeLocalUnitsPerWorldUnit(float4x4 worldToLocal)
{
    // Convert 1 world-unit vectors into local space and average magnitude.
    // This keeps edge width roughly constant in world space for scaled stamps.
    float3x3 worldToLocal3x3 = (float3x3)worldToLocal;
    float sx = length(mul(worldToLocal3x3, float3(1.0, 0.0, 0.0)));
    float sy = length(mul(worldToLocal3x3, float3(0.0, 1.0, 0.0)));
    float sz = length(mul(worldToLocal3x3, float3(0.0, 0.0, 1.0)));
    return max((sx + sy + sz) * (1.0 / 3.0), 1e-5);
}

void StampClipSpehereClip_float(float3 worldPosition, out float clipThreshold, out float3 tintedBaseColor)
{
    // Sphere radius in stamp-local space.
    static const float kSphereRadius = 0.5;

    // Edge controls in WORLD units (constant visual thickness across stamp sizes).
    static const float kEdgeWidthWS = 0.0007;
    static const float kEdgeSoftnessWS = 0.0005;

    static const float kEdgeIntensity = 1.0;
    static const float3 kEdgeColor = float3(0.85, 0.08, 0.06);

    // _BaseColor is authored in the Shader Graph and available as a uniform.
    tintedBaseColor = _BaseColor.rgb;
    clipThreshold = 0.0;

    if (_StampClipEnabled < 0.5 || _SphereStampCount <= 0.0)
    {
        return;
    }

    uint sourceMask = asuint(_StampClipSourceMask);
    if (sourceMask == 0u)
    {
        return;
    }

    float edgeMask = 0.0;

    [loop]
    for (int i = 0; i < MAX_SPHERE_STAMPS; i++)
    {
        if (i >= (int)_SphereStampCount)
        {
            break;
        }

        float4x4 worldToLocal = _SphereStampWorldToLocal[i];
        int sourceIndex = (int)_SphereStampSourceIndex[i];
        if (sourceIndex < 0 || sourceIndex >= 32)
        {
            continue;
        }

        uint sourceBit = 1u << sourceIndex;
        if ((sourceMask & sourceBit) == 0u)
        {
            continue;
        }

        float3 stampLocalPosition = mul(worldToLocal, float4(worldPosition, 1.0)).xyz;

        if (IsInsideStampSphere(stampLocalPosition) > 0.5)
        {
            // Alpha clip compares alpha - threshold. With alpha=1 this must exceed 1 to clip.
            clipThreshold = 2.0;
            return;
        }

        float distanceToCenter = length(stampLocalPosition);
        float signedDistanceToBoundary = distanceToCenter - kSphereRadius;

        // Convert constant world-space edge width to local-space per stamp.
        float localUnitsPerWorldUnit = ComputeLocalUnitsPerWorldUnit(worldToLocal);
        float edgeWidthLocal = kEdgeWidthWS * localUnitsPerWorldUnit;
        float edgeSoftnessLocal = max(kEdgeSoftnessWS * localUnitsPerWorldUnit, 1e-5);

        // Only tint the visible side (outside the clipped sphere), in a thin soft band.
        float outsideMask = step(0.0, signedDistanceToBoundary);
        float ring = 1.0 - smoothstep(edgeWidthLocal, edgeWidthLocal + edgeSoftnessLocal, signedDistanceToBoundary);
        edgeMask = max(edgeMask, outsideMask * ring);
    }

    tintedBaseColor = lerp(tintedBaseColor, kEdgeColor, saturate(edgeMask * kEdgeIntensity));
}

void StampClipSpehereClip_float(float3 worldPosition, out float clipThreshold)
{
    float3 unusedTintedBaseColor;
    StampClipSpehereClip_float(worldPosition, clipThreshold, unusedTintedBaseColor);
}

#endif
