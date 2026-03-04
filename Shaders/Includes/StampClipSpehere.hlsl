#ifndef VIRTOSHA_STAMP_CLIP_SPEHERE_INCLUDED
#define VIRTOSHA_STAMP_CLIP_SPEHERE_INCLUDED

#ifndef MAX_SPHERE_STAMPS
#define MAX_SPHERE_STAMPS 64
#endif

float _StampClipEnabled;
float _SphereStampCount;
float4x4 _SphereStampWorldToLocal[MAX_SPHERE_STAMPS];

inline float IsInsideStampSphere(float3 stampLocalPosition)
{
    // stampLocalPosition is expected around the origin in stamp local space.
    // Radius 0.5 keeps the sphere inside the same unit bounds used before.
    float radiusSquared = 0.25;
    float distanceSquared = dot(stampLocalPosition, stampLocalPosition);
    return step(distanceSquared, radiusSquared);
}

void StampClipSpehereClip_float(float3 worldPosition, out float clipThreshold)
{
    if (_StampClipEnabled < 0.5 || _SphereStampCount <= 0.0)
    {
        clipThreshold = 0.0;
        return;
    }

    clipThreshold = 0.0;

    [loop]
    for (int i = 0; i < MAX_SPHERE_STAMPS; i++)
    {
        if (i >= (int)_SphereStampCount)
        {
            break;
        }

        float3 stampLocalPosition = mul(_SphereStampWorldToLocal[i], float4(worldPosition, 1.0)).xyz;
        if (IsInsideStampSphere(stampLocalPosition) > 0.5)
        {
            // Alpha clip compares alpha - threshold. With alpha=1 this must exceed 1 to clip.
            clipThreshold = 2.0;
            return;
        }
    }
}

#endif
