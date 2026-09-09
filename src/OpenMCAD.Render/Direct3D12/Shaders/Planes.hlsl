// Reference planes: the datum planes a part is built on (P2-T11).
//
// A plane is conceptually infinite and what gets drawn is a square standing for it, so the only
// geometry here is two triangles per plane, expanded in the vertex shader from the plane's own
// frame. The size arrives in the constants, computed from the scene rather than from the camera:
// a reference that changes size as you zoom is worse than one at the wrong scale, which is the
// same reasoning the grid's spacing follows.
//
// Drawn translucent and two-sided, depth-tested against the scene but writing no depth. Writing
// depth would let one plane hide another and hide the grid behind both, and three datum planes
// meeting at the origin overlap on nearly every pixel of the region they share -- which is exactly
// the case P2-T10 established no sorting of objects can fix. Not writing depth means the blend is
// order-dependent between planes, which for a handful of large flat quads reads as a slightly
// different tint where they cross rather than as anything popping: the failure weighted blending
// exists to prevent is geometry appearing in front of other geometry, and nothing here is in front
// of anything.

cbuffer PlaneConstants : register(b0)
{
    row_major float4x4 ViewProjection;

    float3 Colour;
    float  Alpha;

    // Half the side of the square, in metres, from the scene's own size.
    float  HalfSize;

    // How far in from the edge the fade starts, as a fraction of the half-size. A hard edge reads
    // as a wall; a plane that dissolves at its rim reads as something that carries on.
    float  FadeFraction;

    float2 _pad;
};

struct VSOutput
{
    float4 Clip   : SV_Position;
    float2 Local  : PLANELOCAL;   // -1 .. +1 across the square, for the edge fade
};

VSOutput VSMain(
    float3 planeOrigin : PLANEORIGIN,
    float3 planeRight  : PLANERIGHT,
    float3 planeUp     : PLANEUP,
    uint   vertexId    : SV_VertexID)
{
    VSOutput output;

    // Two triangles, as a strip order flattened into six indices: 0 1 2, 2 1 3.
    const uint corner[6] = { 0, 1, 2, 2, 1, 3 };
    uint which = corner[vertexId % 6];

    float2 local = float2(
        (which & 1) ? 1.0 : -1.0,
        (which & 2) ? 1.0 : -1.0);

    float3 world = planeOrigin
        + (planeRight * (local.x * HalfSize))
        + (planeUp * (local.y * HalfSize));

    output.Clip = mul(ViewProjection, float4(world, 1.0));
    output.Local = local;

    return output;
}

float4 PSMain(VSOutput input) : SV_Target
{
    // Square fade: distance from the centre measured along whichever axis is further out, so the
    // fade follows the square's own edges rather than a circle inscribed in it. A circular fade
    // would leave the corners transparent and make a square plane look round.
    float edge = max(abs(input.Local.x), abs(input.Local.y));

    // FadeFraction of 0 gives a hard edge; the smoothstep collapses to a step there rather than
    // dividing by zero, because the two bounds coincide.
    float start = 1.0 - FadeFraction;
    float fade = 1.0 - smoothstep(start, 1.0, edge);

    // Premultiplied, to match the blend state. BlendDescription.AlphaBlend has SourceBlend One
    // rather than SourceAlpha -- the straight-alpha state is NonPremultiplied, despite the names --
    // so a shader returning straight colour here adds the plane's full colour to the destination
    // and draws it solid. EdgePass carries the same note for the same reason.
    float alpha = Alpha * fade;

    return float4(Colour * alpha, alpha);
}
