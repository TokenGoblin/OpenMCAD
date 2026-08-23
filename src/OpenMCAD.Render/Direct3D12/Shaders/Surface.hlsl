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
};

cbuffer BodyConstants : register(b1)
{
    float4 BaseColour;
};

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

float4 PSMain(VSOutput input) : SV_Target
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

    return float4(lit, BaseColour.a);
}
