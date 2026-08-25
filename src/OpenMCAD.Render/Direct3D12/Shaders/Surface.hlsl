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
};

// One display id per triangle, indexed by SV_PrimitiveID. Read only by the ID pass; the shaded
// pass leaves it unbound, which is legal because nothing it runs references it.
StructuredBuffer<uint> EntityIds : register(t0);

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
    float specular = pow(saturate(dot(n, halfway)), 48.0) * 0.20;

    // Hemisphere fill: cooler from above, warmer from below, about the world Z up axis. It costs
    // one lerp and does the job an ambient constant cannot -- faces turned away from the key light
    // stay distinguishable from one another instead of flattening into a single dark tone.
    float  sky     = 0.5 + (0.5 * n.z);
    float3 ambient = lerp(float3(0.16, 0.15, 0.14), float3(0.34, 0.36, 0.40), sky);

    float3 lit = (BaseColour.rgb * (ambient + (diffuse * 0.85))) + specular;

    return float4(ApplyHighlight(lit, StateOf(EntityIds[primitive])), BaseColour.a);
}

// The ID pass (P2-T07). Deliberately paired with the same VSMain above rather than given a vertex
// shader of its own: picking is only correct if the ID buffer is rasterised from identical
// geometry, and two vertex shaders that are supposed to agree eventually will not.
uint PSMainId(VSOutput input, uint primitive : SV_PrimitiveID) : SV_Target
{
    return EntityIds[primitive];
}
