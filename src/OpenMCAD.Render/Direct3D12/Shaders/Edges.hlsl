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
    // Declared row_major so that the sixteen floats can be memcpy'd straight out of Mat4d, which
    // is row-major with translation in the fourth column. With that declaration mul(M, v) is the
    // ordinary M*v of a column vector, which is what the camera matrices are built for.
    row_major float4x4 ViewProjection;

    float3 CameraPosition;   // relative to the snapshot origin
    float  _pad0;
    float3 LightDirection;   // unit, pointing from the surface towards the light
    float  _pad1;
    float2 ViewportSize;     // physical pixels

    // How many entries EntityStates holds. It has to be passed in: EntityStates is bound as a
    // root descriptor, which is a bare GPU address with no size attached, so GetDimensions on it
    // returns nothing meaningful and reading past the end is an access violation rather than a
    // clamp. This is the bound.
    uint   HighlightCount;
    uint   _pad2;

    // Highlight colours, indexed by HighlightState: 1 pre-selected, 2 selected, 3 error. The
    // alpha is how strongly to tint rather than an opacity. Held in the frame block because they
    // are a property of the session, and one buffer both passes read cannot disagree with itself
    // about what "selected" looks like.
    float4 PreSelectedColour;
    float4 SelectedColour;
    float4 ErrorColour;
};

// THIS BLOCK MUST MATCH BYTE FOR BYTE IN EVERY SHADER THAT DECLARES IT, and must match
// FrameConstants in FacePass.cs. HLSL lets a shader declare a prefix of a constant buffer, which
// is why a mismatch does not fail to compile -- it silently reads the wrong offsets, and the
// symptom is geometry or colour going somewhere unexpected rather than an error. These two files
// had already drifted once.

// One highlight state per display id, indexed by the same id the ID pass writes.
StructuredBuffer<uint> EntityStates : register(t1);

// Reads a state, tolerating a buffer shorter than the id or nothing highlighted at all.
uint StateOf(uint id)
{
    return id < HighlightCount ? EntityStates[id] : 0;
}

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

// One display id per segment, indexed by the instance. Read only by the ID pass.
StructuredBuffer<uint> EntityIds : register(t0);

struct VSOutput
{
    float4 Clip   : SV_Position;

    // Signed distance from the centre line, in pixels. Interpolating this and comparing against
    // the half width in the pixel shader is what anti-aliases the edge without multisampling.
    float  Offset : EDGEOFFSET;

    // Which segment this came from. SV_InstanceID is not available in a pixel shader, so the ID
    // pass needs it carried across; nointerpolation because an index must not be averaged.
    nointerpolation uint Instance : EDGEINSTANCE;
};

VSOutput VSMain(
    float3 segmentStart : EDGESTART,
    float3 segmentEnd   : EDGEEND,
    uint   vertexId     : SV_VertexID,
    uint   instanceId   : SV_InstanceID)
{
    VSOutput output;
    output.Instance = instanceId;

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

    // Premultiplied, because the pipeline blends with SourceBlend = One. Returning straight alpha
    // against that blend adds the whole edge colour to the destination wherever coverage is low,
    // so the anti-aliasing ramp that exists to soften the line instead paints a band brighter than
    // the background either side of it -- a halo around every edge.
    float alpha = EdgeColour.a * coverage;

    // A highlighted edge takes the highlight colour outright rather than being tinted towards it.
    // An edge is a pixel or two wide with no shading to preserve, so the thing a tint protects on
    // a face does not exist here -- a subtle tint on a hairline is simply invisible.
    uint state = StateOf(EntityIds[input.Instance]);
    float3 colour = EdgeColour.rgb;

    if (state == 1)
    {
        colour = PreSelectedColour.rgb;
    }
    else if (state == 2)
    {
        colour = SelectedColour.rgb;
    }
    else if (state == 3)
    {
        colour = ErrorColour.rgb;
    }

    return float4(colour * alpha, alpha);
}

// The ID pass (P2-T07), sharing VSMain with the visible pass so that what is picked is exactly
// what is drawn.
//
// The coverage test is kept identical rather than widened. Making the hit area larger here would
// be the wrong place for it: it would claim pixels for an edge in the ID buffer that the eye
// cannot see it occupying, and the same widening is done properly at resolve time, where it can
// prefer the *nearest* edge rather than whichever happens to have been rasterised last.
uint PSMainId(VSOutput input) : SV_Target
{
    float coverage = saturate(HalfWidthPixels + 0.5 - abs(input.Offset));

    if (coverage <= 0.0)
    {
        discard;
    }

    return EntityIds[input.Instance];
}
