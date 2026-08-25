// Axis lines: the origin triad and the corner orientation gizmo (P2-T11, P2-T08).
//
// One shader serves both. They differ only in the matrix that takes an axis vector to clip space:
// the triad uses the ordinary view-projection, and the gizmo uses a matrix built from the camera's
// rotation alone, scaled to a fixed pixel size and shifted into a corner. Giving them separate
// shaders would mean two pieces of quad-expansion arithmetic that are supposed to agree, and the
// gizmo would slowly stop matching the axes it is meant to be reporting on.
//
// The expansion is the same technique as the edge pass: a segment becomes a screen-space quad of
// constant pixel width, because a D3D12 line is one pixel and cannot be anti-aliased.

cbuffer AxisConstants : register(b0)
{
    row_major float4x4 Transform;

    float2 ViewportSize;      // physical pixels
    float  HalfWidthPixels;

    // How far to shorten each line at the far end, as a fraction, so an arrowhead or a label can
    // sit there later without the line running through it.
    float  _pad0;
};

struct VSOutput
{
    float4 Clip   : SV_Position;
    float4 Colour : COLOUR;
    float  Offset : AXISOFFSET;
};

VSOutput VSMain(
    float3 segmentStart : AXISSTART,
    float3 segmentEnd   : AXISEND,
    float4 colour       : AXISCOLOUR,
    uint   vertexId     : SV_VertexID)
{
    VSOutput output;
    output.Colour = colour;

    float4 clipA = mul(Transform, float4(segmentStart, 1.0));
    float4 clipB = mul(Transform, float4(segmentEnd, 1.0));

    // Same near-plane guard as the edge pass. The triad sits at the world origin, which a user
    // zoomed into a detail may well have behind them, and dividing through a negative w mirrors
    // the line across the screen.
    const float minimumW = 1e-4;

    if (clipA.w < minimumW && clipB.w < minimumW)
    {
        output.Clip = float4(0.0, 0.0, 0.0, 1.0);
        output.Offset = 1e6;
        return output;
    }

    if (clipA.w < minimumW)
    {
        clipA = lerp(clipA, clipB, (minimumW - clipA.w) / (clipB.w - clipA.w));
    }
    else if (clipB.w < minimumW)
    {
        clipB = lerp(clipB, clipA, (minimumW - clipB.w) / (clipA.w - clipB.w));
    }

    float2 screenA = clipA.xy / clipA.w * 0.5 * ViewportSize;
    float2 screenB = clipB.xy / clipB.w * 0.5 * ViewportSize;

    float2 along = screenB - screenA;
    float  length2 = dot(along, along);

    float2 direction = length2 > 1e-12 ? along * rsqrt(length2) : float2(1.0, 0.0);
    float2 perpendicular = float2(-direction.y, direction.x);

    bool atEnd = vertexId >= 2;
    float side = (vertexId & 1) ? 1.0 : -1.0;
    float expand = HalfWidthPixels + 1.0;

    float4 clip = atEnd ? clipB : clipA;
    clip.xy += perpendicular * (side * expand) * (2.0 / ViewportSize) * clip.w;

    output.Clip = clip;
    output.Offset = side * expand;

    return output;
}

float4 PSMain(VSOutput input) : SV_Target
{
    float coverage = saturate(HalfWidthPixels + 0.5 - abs(input.Offset));

    if (coverage <= 0.0)
    {
        discard;
    }

    // Premultiplied, matching the blend state. See the edge pass: straight alpha against a
    // SourceBlend of One adds the whole colour wherever coverage is low and haloes the line.
    float alpha = input.Colour.a * coverage;

    return float4(input.Colour.rgb * alpha, alpha);
}
