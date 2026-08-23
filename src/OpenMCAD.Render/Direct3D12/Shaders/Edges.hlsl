// Edge polylines (P2-T06).
//
// Edges are drawn as screen-space quads rather than as line primitives. D3D12 lines are always
// exactly one pixel wide, which is too thin to read at any display scale above 100% and cannot be
// anti-aliased; a CAD drawing lives or dies on its edges. Each segment is expanded in the vertex
// shader to a quad of constant pixel width, so an edge looks the same whether it is a metre away
// or a kilometre.
//
// There is no vertex buffer. Four vertices per instance are generated from SV_VertexID and the
// segment endpoints arrive as per-instance data, which keeps the whole thing to one 24-byte
// stream and no index buffer.

cbuffer FrameConstants : register(b0)
{
    row_major float4x4 ViewProjection;
    float3 CameraPosition;
    float  _pad0;
    float3 LightDirection;
    float  _pad1;
    float2 ViewportSize;     // physical pixels
    float2 _pad2;
};

cbuffer EdgeConstants : register(b1)
{
    float4 EdgeColour;
    float  HalfWidthPixels;

    // Pushes the edge towards the viewer in normalised depth. Tessellated edges lie exactly on the
    // surface they bound -- that is the point of taking them from the face triangulation -- so
    // without a bias they z-fight with it, and the result is a stippled edge that changes pattern
    // as the camera moves.
    float  DepthBias;

    float2 _pad3;
};

struct VSOutput
{
    float4 Clip   : SV_Position;

    // Signed distance from the centre line, in pixels. Interpolating this and comparing against
    // the half width in the pixel shader is what anti-aliases the edge without multisampling.
    float  Offset : EDGEOFFSET;
};

VSOutput VSMain(
    float3 segmentStart : EDGESTART,
    float3 segmentEnd   : EDGEEND,
    uint   vertexId     : SV_VertexID)
{
    VSOutput output;

    float4 clipA = mul(ViewProjection, float4(segmentStart, 1.0));
    float4 clipB = mul(ViewProjection, float4(segmentEnd, 1.0));

    clipA.z -= DepthBias * clipA.w;
    clipB.z -= DepthBias * clipB.w;

    // A vertex behind the eye has w <= 0, and dividing by it mirrors the point to the far side of
    // the screen -- an edge that vanishes into the distance suddenly whips across the viewport.
    // Clipping the segment against a small positive w first is what keeps a zoomed-in camera,
    // which is exactly when the near plane cuts through the model, from painting garbage.
    const float minimumW = 1e-4;

    if (clipA.w < minimumW && clipB.w < minimumW)
    {
        // Wholly behind. Collapse to a degenerate quad rather than clamping, which would smear it
        // across the near plane.
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

    // A segment shorter than a pixel still has to produce a quad with a defined orientation.
    float2 direction = length2 > 1e-12 ? along * rsqrt(length2) : float2(1.0, 0.0);
    float2 perpendicular = float2(-direction.y, direction.x);

    // Triangle strip, four corners: 0 and 1 at the start, 2 and 3 at the end; odd on one side.
    bool atEnd = vertexId >= 2;
    float side = (vertexId & 1) ? 1.0 : -1.0;

    // One pixel wider than the line, so the coverage ramp in the pixel shader has somewhere to go.
    float expand = HalfWidthPixels + 1.0;

    float4 clip = atEnd ? clipB : clipA;

    // Pixels to clip space: a pixel is 2/ViewportSize in normalised device coordinates, and
    // multiplying by w undoes the perspective divide the rasteriser is about to apply.
    clip.xy += perpendicular * (side * expand) * (2.0 / ViewportSize) * clip.w;

    output.Clip = clip;
    output.Offset = side * expand;

    return output;
}

float4 PSMain(VSOutput input) : SV_Target
{
    // Coverage falls from 1 to 0 across the outermost pixel of the quad.
    float coverage = saturate(HalfWidthPixels + 0.5 - abs(input.Offset));

    if (coverage <= 0.0)
    {
        discard;
    }

    return float4(EdgeColour.rgb, EdgeColour.a * coverage);
}
