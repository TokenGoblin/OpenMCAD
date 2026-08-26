// Screen-space ambient occlusion (P2-T12).
//
// What this buys in CAD specifically: concave features stop disappearing. A pocket, a counterbore,
// the inside corner where a rib meets a wall — all of them are lit almost identically to the
// surface around them by any directional light, because their normals barely differ. Darkening
// what is enclosed is the cue the eye actually uses to read depth in a machined part, and no
// amount of moving the key light supplies it.
//
// Everything here works from the depth buffer alone. Normals are reconstructed from it rather than
// taken from a G-buffer, because the renderer is forward-shaded and adding a normal target to
// carry them would cost more bandwidth every frame than this pass costs at all.

cbuffer OcclusionConstants : register(b0)
{
    row_major float4x4 InverseProjection;

    // Both directions are needed and neither can be derived cheaply from the other in a shader:
    // depth becomes a view-space position through the inverse, and a sampled point becomes a pixel
    // through the forward.
    row_major float4x4 Projection;

    float2 ViewportSize;

    // How far, in metres, a surface can be occluded from. The single most important knob: too
    // small and only the sharpest creases darken, too large and the whole model turns muddy and
    // the effect stops reading as contact and starts reading as dirt.
    float  Radius;

    // How hard the darkening is.
    float  Intensity;

    // Depth difference, in metres, beyond which a sample is treated as a different surface
    // altogether rather than as an occluder. Without it a near object casts occlusion onto the
    // distant background behind it -- a dark halo tracing its silhouette, which is the classic
    // way this technique announces itself.
    float  RangeCutoff;

    uint   SampleCount;
    float2 _pad0;
};

Texture2DMS<float> DepthBuffer : register(t0);
Texture2D<float>   Occlusion   : register(t1);

struct VSOutput
{
    float4 Clip : SV_Position;
    float2 Ndc  : NDC;
};

VSOutput VSMain(uint vertexId : SV_VertexID)
{
    VSOutput output;

    float2 corner = float2((vertexId == 1) ? 3.0 : -1.0, (vertexId == 2) ? 3.0 : -1.0);

    output.Ndc = corner;
    output.Clip = float4(corner, 0.0, 1.0);

    return output;
}

// View-space position of a pixel, from its depth.
float3 ViewPosition(int2 pixel, float2 ndc)
{
    float depth = DepthBuffer.Load(pixel, 0);
    float4 view = mul(InverseProjection, float4(ndc, depth, 1.0));

    return view.xyz / view.w;
}

float2 NdcOf(int2 pixel)
{
    float2 uv = (pixel + 0.5) / ViewportSize;

    return float2((uv.x * 2.0) - 1.0, 1.0 - (uv.y * 2.0));
}

// A hemisphere of offsets. Fixed rather than random: a rotating noise texture is the usual way to
// hide banding, and it trades banding for a shimmer that moves as the camera does -- which in a
// CAD viewport reads as the surface being dirty rather than as the sampling being coarse.
static const float3 Kernel[16] =
{
    float3( 0.5381,  0.1856, 0.4319), float3( 0.1379,  0.2486, 0.4430),
    float3( 0.3371,  0.5679, 0.0057), float3(-0.6999, -0.0451, 0.0019),
    float3( 0.0689, -0.1598, 0.8547), float3( 0.0560,  0.0069, 0.1843),
    float3(-0.0146,  0.1402, 0.0762), float3( 0.0100, -0.1924, 0.0344),
    float3(-0.3577, -0.5301, 0.4358), float3(-0.3169,  0.1063, 0.0158),
    float3( 0.0103, -0.5869, 0.0046), float3(-0.0897, -0.4940, 0.3287),
    float3( 0.7119, -0.0154, 0.0918), float3(-0.0533,  0.0596, 0.5411),
    float3( 0.0352, -0.0631, 0.5460), float3(-0.4776,  0.2847, 0.0271),
};

float PSOcclusion(VSOutput input) : SV_Target
{
    int2 pixel = int2(input.Clip.xy);
    float depth = DepthBuffer.Load(pixel, 0);

    // Nothing was drawn here. Returning one leaves the background untouched when this is
    // multiplied over the image, which is what keeps the sky and the far grid clean.
    if (depth >= 1.0)
    {
        return 1.0;
    }

    float3 origin = ViewPosition(pixel, input.Ndc);

    // Reconstructed from the depth of the neighbours rather than carried in a normal buffer. The
    // smaller of the two differences on each axis is used, so a silhouette -- where one neighbour
    // is on a completely different surface -- does not invent a normal facing sideways.
    float3 right = ViewPosition(pixel + int2(1, 0), NdcOf(pixel + int2(1, 0))) - origin;
    float3 left = origin - ViewPosition(pixel - int2(1, 0), NdcOf(pixel - int2(1, 0)));
    float3 down = ViewPosition(pixel + int2(0, 1), NdcOf(pixel + int2(0, 1))) - origin;
    float3 up = origin - ViewPosition(pixel - int2(0, 1), NdcOf(pixel - int2(0, 1)));

    float3 dx = abs(right.z) < abs(left.z) ? right : left;
    float3 dy = abs(down.z) < abs(up.z) ? down : up;

    float3 normal = cross(dx, dy);
    float lengthSquared = dot(normal, normal);

    if (lengthSquared < 1e-20)
    {
        return 1.0;
    }

    normal = normalize(normal);

    // Turned to face the camera, whichever way the cross product came out. The two screen-space
    // differences are right and *down*, and down is negative Y, so their cross product points away
    // from the viewer rather than towards it -- which flips the sampling hemisphere into the solid,
    // where every sample is naturally occluded, and darkens the entire model almost to black.
    // Rather than depend on getting that winding right, the sign is settled here from something
    // that cannot be got wrong: a fragment the camera can see faces the camera, and in view space
    // the camera is at the origin, so the direction to it is just the negated position.
    if (dot(normal, origin) > 0.0)
    {
        normal = -normal;
    }

    // A per-pixel rotation, from the pixel position rather than a texture. Enough to break the
    // pattern into noise the blur can remove, and it does not move when the camera does.
    float angle = frac(sin(dot(float2(pixel), float2(12.9898, 78.233))) * 43758.5453) * 6.2831853;
    float2 rotation = float2(cos(angle), sin(angle));

    float occluded = 0.0;
    uint samples = min(max(SampleCount, 1u), 16u);

    for (uint i = 0; i < samples; ++i)
    {
        float3 offset = Kernel[i];

        // Rotated about the view axis, then flipped into the hemisphere the surface faces. A full
        // sphere would count the surface's own back half as occluding it, darkening every flat
        // face uniformly -- which looks like a badly exposed photograph rather than occlusion.
        float3 rotated = float3(
            (offset.x * rotation.x) - (offset.y * rotation.y),
            (offset.x * rotation.y) + (offset.y * rotation.x),
            offset.z);

        if (dot(rotated, normal) < 0.0)
        {
            rotated = -rotated;
        }

        float3 samplePoint = origin + (rotated * Radius);

        // Back to the screen, to find what was actually drawn in that direction.
        float4 clip = mul(Projection, float4(samplePoint, 1.0));

        if (clip.w <= 1e-6)
        {
            continue;
        }

        float2 sampleNdc = clip.xy / clip.w;

        int2 samplePixel = int2(
            (sampleNdc.x + 1.0) * 0.5 * ViewportSize.x,
            (1.0 - sampleNdc.y) * 0.5 * ViewportSize.y);

        samplePixel = clamp(samplePixel, int2(0, 0), int2(ViewportSize) - 1);

        float sampleDepth = DepthBuffer.Load(samplePixel, 0);

        if (sampleDepth >= 1.0)
        {
            continue;
        }

        float3 occluderPosition = ViewPosition(samplePixel, NdcOf(samplePixel));

        // Nearer to the eye than the sample point means something is in the way. View space here
        // has depth increasing away from the camera along -z, so "nearer" is a larger z.
        if (occluderPosition.z <= samplePoint.z)
        {
            continue;
        }

        // Faded out past the cutoff, so a foreground object does not occlude the distant
        // background behind it and trace a dark halo around its own silhouette.
        float difference = abs(origin.z - occluderPosition.z);
        occluded += saturate(RangeCutoff / max(difference, 1e-6));
    }

    return saturate(1.0 - (Intensity * occluded / samples));
}

// A separable-in-spirit box blur, run once over both axes. Occlusion is low frequency, so a small
// box is enough to remove the per-pixel rotation's noise without softening the contact darkening
// that is the whole point.
float PSBlur(VSOutput input) : SV_Target
{
    int2 pixel = int2(input.Clip.xy);
    int2 limit = int2(ViewportSize) - 1;

    float total = 0.0;
    float count = 0.0;

    for (int y = -2; y <= 2; ++y)
    {
        for (int x = -2; x <= 2; ++x)
        {
            int2 at = clamp(pixel + int2(x, y), int2(0, 0), limit);

            total += Occlusion.Load(int3(at, 0));
            count += 1.0;
        }
    }

    return total / count;
}

// Multiplied over the image, so this returns the occlusion in every channel.
float4 PSApply(VSOutput input) : SV_Target
{
    float value = Occlusion.Load(int3(int2(input.Clip.xy), 0));

    return float4(value, value, value, 1.0);
}
