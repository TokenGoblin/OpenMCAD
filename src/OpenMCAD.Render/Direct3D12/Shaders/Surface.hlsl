// Shaded faces (P2-T05).
//
// Positions arrive relative to the snapshot origin, not in world space. See DisplaySnapshot:
// a model placed a kilometre from the origin and detailed to a micron exceeds what float can
// represent, so the double->float conversion happens against a nearby origin and everything
// downstream -- including this shader and the camera position it is given -- lives in that
// same shifted frame.

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

cbuffer BodyConstants : register(b1)
{
    float4 BaseColour;

    // Ambient, diffuse, specular, gloss. Root constants rather than a buffer, because this is
    // eight floats pushed inline per body -- cheaper than allocating, writing and addressing a
    // constant buffer for each one.
    float4 Material;
};

// One display id per triangle, indexed by SV_PrimitiveID. Read only by the ID pass; the shaded
// pass leaves it unbound, which is legal because nothing it runs references it.
StructuredBuffer<uint> EntityIds : register(t0);

// Hemisphere fill: cooler from above, warmer from below, about the world Z up axis. It costs one
// lerp and does the job an ambient constant cannot -- faces turned away from the key light stay
// distinguishable from one another instead of flattening into a single dark tone.
//
// The two tints are written so that the brightest component of either is exactly one, which leaves
// the material's ambient term as the only thing setting how bright the fill is. That is what makes
// "ambient plus diffuse must not exceed one" a rule that can be checked in C# rather than a
// property of whatever numbers happen to be in this file.
float3 Fill(float3 n)
{
    static const float3 ground = float3(0.62, 0.58, 0.54);
    static const float3 sky = float3(0.85, 0.90, 1.00);

    return lerp(ground, sky, 0.5 + (0.5 * n.z)) * Material.x;
}

struct VSInput
{
    float3 Position : POSITION;
    float3 Normal   : NORMAL;
};

struct VSOutput
{
    float4 Clip   : SV_Position;
    float3 World  : WORLDPOS;
    float3 Normal : NORMAL;
};

VSOutput VSMain(VSInput input)
{
    VSOutput output;
    output.World  = input.Position;
    output.Clip   = mul(ViewProjection, float4(input.Position, 1.0));
    output.Normal = input.Normal;
    return output;
}

// Blends a surface towards its highlight colour.
//
// A tint rather than a replacement. Painting a selected face flat blue destroys the shading that
// tells the user what shape they have selected: the face becomes a silhouette, and a curved one
// stops reading as curved at all. Keeping the lighting and pushing the hue is what every CAD
// package does, and why selection there still looks like geometry.
float3 ApplyHighlight(float3 lit, uint state)
{
    if (state == 1)
    {
        return lerp(lit, PreSelectedColour.rgb, PreSelectedColour.a);
    }

    if (state == 2)
    {
        return lerp(lit, SelectedColour.rgb, SelectedColour.a);
    }

    if (state == 3)
    {
        return lerp(lit, ErrorColour.rgb, ErrorColour.a);
    }

    return lit;
}

float4 PSMain(VSOutput input, uint primitive : SV_PrimitiveID) : SV_Target
{
    float3 n = input.Normal;

    // A mesh may arrive without normals -- DisplayMesh models that explicitly, and the buffer is
    // zero-filled when it happens. Reconstructing the facet normal from the screen-space
    // derivatives of the world position gives correct flat shading rather than a black body.
    if (dot(n, n) < 1e-12)
    {
        n = cross(ddx(input.World), ddy(input.World));

        // Degenerate triangles have no derivative to speak of; anything is better than a NaN.
        if (dot(n, n) < 1e-30)
        {
            n = float3(0.0, 0.0, 1.0);
        }
    }

    n = normalize(n);

    float3 viewDirection = normalize(CameraPosition - input.World);

    // Two-sided. A CAD model is routinely viewed from inside -- a section, an open shell, a face
    // whose winding the kernel had no reason to agree with -- and a one-sided shader renders those
    // black, which reads as a modelling error rather than a display convention.
    if (dot(n, viewDirection) < 0.0)
    {
        n = -n;
    }

    float diffuse = saturate(dot(n, LightDirection));

    float3 halfway = normalize(LightDirection + viewDirection);
    float specular = pow(saturate(dot(n, halfway)), Material.w) * Material.z;

    float3 lit = (BaseColour.rgb * (Fill(n) + (diffuse * Material.y))) + specular;

    return float4(ApplyHighlight(lit, StateOf(EntityIds[primitive])), BaseColour.a);
}

// The ID pass (P2-T07). Deliberately paired with the same VSMain above rather than given a vertex
// shader of its own: picking is only correct if the ID buffer is rasterised from identical
// geometry, and two vertex shaders that are supposed to agree eventually will not.
uint PSMainId(VSOutput input, uint primitive : SV_PrimitiveID) : SV_Target
{
    return EntityIds[primitive];
}

// --- Weighted-blended transparency (P2-T10) ---------------------------------------------------
//
// Paired with the same VSMain as the opaque and ID passes, for the same reason: a transparent face
// must occupy exactly the pixels its opaque version would, or a body fading in and out would
// appear to change shape as it did so.

struct TransparentOutput
{
    float4 Accumulation : SV_Target0;
    float  Revealage    : SV_Target1;
};

// How much a fragment counts, given how far away it is.
//
// The weight falls off sharply with depth so that nearer surfaces dominate, which is what makes an
// unordered sum resemble an ordered blend. The constants are McGuire and Bavoil's, and the ranges
// matter: too flat and everything averages into fog, too steep and the far surfaces vanish
// entirely rather than showing faintly.
//
// Clamped at both ends because the expression spans several orders of magnitude and the target is
// a half float -- unclamped, near fragments saturate to infinity and far ones round to zero, which
// are the two ways this technique visibly fails.
float TransparencyWeight(float viewDepth, float alpha)
{
    float falloff = 10.0 / (1e-5 + pow(abs(viewDepth) / 5.0, 2.0) + pow(abs(viewDepth) / 200.0, 6.0));

    return alpha * clamp(falloff, 1e-2, 3e3);
}

TransparentOutput PSMainTransparent(VSOutput input, uint primitive : SV_PrimitiveID)
{
    float3 n = input.Normal;

    if (dot(n, n) < 1e-12)
    {
        n = cross(ddx(input.World), ddy(input.World));

        if (dot(n, n) < 1e-30)
        {
            n = float3(0.0, 0.0, 1.0);
        }
    }

    n = normalize(n);

    float3 viewDirection = normalize(CameraPosition - input.World);

    if (dot(n, viewDirection) < 0.0)
    {
        n = -n;
    }

    float diffuse = saturate(dot(n, LightDirection));

    // No highlight on the transparent path. A specular bloom on a surface being seen through
    // reads as a smear on the glass rather than as a property of the part behind it.
    float3 lit = BaseColour.rgb * (Fill(n) + (diffuse * Material.y));
    lit = ApplyHighlight(lit, StateOf(EntityIds[primitive]));

    float alpha = saturate(BaseColour.a);
    float weight = TransparencyWeight(length(CameraPosition - input.World), alpha);

    TransparentOutput output;

    // Both of these are commutative, which is the whole point: the accumulation is summed and the
    // revealage multiplied, so neither depends on the order fragments happen to arrive in.
    output.Accumulation = float4(lit * alpha, alpha) * weight;
    output.Revealage = alpha;

    return output;
}
