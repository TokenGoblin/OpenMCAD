// The background gradient and the ground grid (P2-T11).
//
// Both are drawn by one full-screen triangle with no vertex buffer, the three corners generated
// from SV_VertexID. A triangle rather than two triangles making a quad: the quad's diagonal makes
// the rasteriser evaluate the pixels along it twice, in two separate quads of four, and a single
// oversized triangle covering the screen has no seam at all.
//
// The grid is analytic. Drawing one as line geometry means choosing an extent and a spacing in
// advance, and a CAD user zooms across six orders of magnitude in a session -- the lines either
// run out or turn into a solid mass. Intersecting a ray per pixel with the ground plane and
// deciding there and then whether the pixel is on a line gives a grid with no extent at all, and
// screen-space derivatives keep every line one pixel wide however far away it is.

cbuffer EnvironmentConstants : register(b0)
{
    // Turns a clip-space corner back into a world-space ray. The camera cannot supply this: the
    // whole point is to work backwards from the pixel.
    row_major float4x4 InverseViewProjection;

    float3 CameraPosition;    // world space, relative to the snapshot origin
    float  _pad0;

    float4 TopColour;
    float4 BottomColour;
    float4 GridColour;
    float4 AxisXColour;
    float4 AxisYColour;

    // Spacing of the fine lines, in metres. The coarse lines are ten times this.
    float  GridSpacing;
    // How far from the camera the grid has faded out entirely, in metres.
    float  GridFade;
    // 0 draws the gradient alone.
    float  ShowGrid;
    float  _pad1;
};

struct VSOutput
{
    float4 Clip : SV_Position;
    float2 Ndc  : NDC;
};

VSOutput VSMain(uint vertexId : SV_VertexID)
{
    VSOutput output;

    // One triangle covering the screen: (-1,-1), (3,-1), (-1,3) in normalised device coordinates.
    float2 corner = float2((vertexId == 1) ? 3.0 : -1.0, (vertexId == 2) ? 3.0 : -1.0);

    output.Ndc = corner;

    // Depth 1 is the far plane, so everything drawn afterwards passes the depth test over it.
    output.Clip = float4(corner, 1.0, 1.0);

    return output;
}

// Unprojects a normalised device coordinate at a given depth back to world space.
float3 Unproject(float2 ndc, float depth)
{
    float4 world = mul(InverseViewProjection, float4(ndc, depth, 1.0));

    return world.xyz / world.w;
}

// How much of this pixel is covered by the nearest line of a grid of the given spacing.
//
// The derivatives are what make this work at any zoom. fwidth gives how far the world position
// moves between neighbouring pixels, so dividing the distance-to-a-line by it converts metres
// into pixels -- and a line that is always the same number of pixels wide neither disappears in
// the distance nor swells into a band up close.
float LineCoverage(float2 position, float spacing, float width)
{
    float2 derivative = fwidth(position);
    float2 toLine = abs(frac((position / spacing) + 0.5) - 0.5) * spacing;
    float2 pixels = toLine / max(derivative, 1e-12);

    return 1.0 - saturate((min(pixels.x, pixels.y) / max(width, 0.5)) - 0.5);
}

float4 PSMain(VSOutput input) : SV_Target
{
    // The vertical gradient, in normalised device coordinates so it does not move with the camera.
    float3 colour = lerp(BottomColour.rgb, TopColour.rgb, (input.Ndc.y * 0.5) + 0.5);

    if (ShowGrid < 0.5)
    {
        return float4(colour, 1.0);
    }

    // A ray from the eye through this pixel. Taking two points at different depths and
    // subtracting is correct for perspective and orthographic alike -- an orthographic ray has a
    // direction that does not depend on the pixel, and this recovers that without a special case.
    float3 near = Unproject(input.Ndc, 0.0);
    float3 far = Unproject(input.Ndc, 1.0);
    float3 direction = far - near;

    // Where it crosses the ground plane, z = 0.
    float denominator = direction.z;

    if (abs(denominator) < 1e-9)
    {
        // Looking exactly along the plane. There is nothing to draw and the division below would
        // produce an infinity that spreads through the rest of the arithmetic.
        return float4(colour, 1.0);
    }

    float t = -near.z / denominator;

    if (t < 0.0 || t > 1.0)
    {
        // The plane is behind the eye, or beyond the far plane.
        return float4(colour, 1.0);
    }

    float3 hit = near + (direction * t);

    // Fades with distance from the camera rather than from the origin, so the grid stays dense
    // around wherever the user is looking instead of thinning out as they pan away from nothing.
    float distance = length(hit - CameraPosition);
    float fade = 1.0 - saturate(distance / max(GridFade, 1e-6));
    fade *= fade;

    if (fade <= 0.001)
    {
        return float4(colour, 1.0);
    }

    float fine = LineCoverage(hit.xy, GridSpacing, 1.0);
    float coarse = LineCoverage(hit.xy, GridSpacing * 10.0, 1.4);

    // The coarse lines are stronger, which is what gives a grid a readable rhythm rather than a
    // uniform mesh the eye cannot count against.
    float grid = max(fine * 0.45, coarse * 0.8);

    colour = lerp(colour, GridColour.rgb, grid * fade * GridColour.a);

    // The two ground axes, drawn over the grid in their own colours. The Z axis is not drawn here
    // because it leaves the plane; the origin triad handles it.
    float axisWidth = 1.6;
    float onX = 1.0 - saturate((abs(hit.y) / max(fwidth(hit.y), 1e-12) / axisWidth) - 0.5);
    float onY = 1.0 - saturate((abs(hit.x) / max(fwidth(hit.x), 1e-12) / axisWidth) - 0.5);

    colour = lerp(colour, AxisXColour.rgb, onX * fade * AxisXColour.a);
    colour = lerp(colour, AxisYColour.rgb, onY * fade * AxisYColour.a);

    return float4(colour, 1.0);
}
