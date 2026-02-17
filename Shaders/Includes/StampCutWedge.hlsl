#ifndef VIRTOSHA_STAMP_CUT_WEDGE_INCLUDED
#define VIRTOSHA_STAMP_CUT_WEDGE_INCLUDED

#ifndef MAX_WEDGE_STAMPS
#define MAX_WEDGE_STAMPS 64
#endif

float _StampCutEnabled;
float _WedgeStampCount;
float4x4 _WedgeStampInverse[MAX_WEDGE_STAMPS];

inline float IsInsideRightTrianglePrism(float3 localPosition)
{
    // localPosition is expected in object space around the origin, typically [-0.5, 0.5] for a unit cube.
    // Build a right triangle over the YZ face by remapping to [0, 1].
    float2 yz01 = localPosition.yz + 0.5;
    float withinTriangle = step(0.0, yz01.x) *
                           step(0.0, yz01.y) *
                           step(yz01.x + yz01.y, 1.0);

    // Prism thickness along local X.
    float withinThickness = step(abs(localPosition.x), 0.5);

    return withinTriangle * withinThickness;
}

void StampCutWedgeClip_float(float3 WorldPosition, out float ClipThreshold)
{
    if (_StampCutEnabled < 0.5 || _WedgeStampCount <= 0.0)
    {
        ClipThreshold = 0.0;
        return;
    }

    ClipThreshold = 0.0;

    [loop]
    for (int i = 0; i < MAX_WEDGE_STAMPS; i++)
    {
        if (i >= (int)_WedgeStampCount)
        {
            break;
        }

        float3 localPosition = mul(_WedgeStampInverse[i], float4(WorldPosition, 1.0)).xyz;
        if (IsInsideRightTrianglePrism(localPosition) > 0.5)
        {
            // Alpha clip compares alpha - threshold. With alpha=1 this must exceed 1 to clip.
            ClipThreshold = 2.0;
            return;
        }
    }
}

#endif
