// Resolving weighted-blended transparency over the opaque image (P2-T10).
//
// The transparent pass wrote two buffers: a weighted sum of colour, and the product of what every
// fragment let through. This turns those back into a single blended result and lays it over what
// was already drawn.
//
// One full-screen triangle, no vertex buffer, as with the environment pass.

cbuffer CompositeConstants : register(b0)
{
    // Non-zero when the accumulation buffers carry more than one sample per pixel, which decides
    // whether they are read as Texture2D or Texture2DMS. HLSL has no way to pick between two
    // resource types at run time, so both are declared and the shader branches -- one of them is
    // bound and the other is not, which is legal precisely because the branch never reads it.
    uint  Multisampled;
    uint  SampleCount;
    float _pad0;
    float _pad1;
};

Texture2D<float4> Accumulation : register(t0);
Texture2D<float>  Revealage    : register(t1);

Texture2DMS<float4> AccumulationMS : register(t2);
Texture2DMS<float>  RevealageMS    : register(t3);

struct VSOutput
{
    float4 Clip : SV_Position;
};

VSOutput VSMain(uint vertexId : SV_VertexID)
{
    VSOutput output;

    float2 corner = float2((vertexId == 1) ? 3.0 : -1.0, (vertexId == 2) ? 3.0 : -1.0);
    output.Clip = float4(corner, 0.0, 1.0);

    return output;
}

float4 PSMain(VSOutput input) : SV_Target
{
    int2 pixel = int2(input.Clip.xy);

    float4 accumulated;
    float revealage;

    if (Multisampled != 0)
    {
        // Averaged across samples rather than resolved beforehand. Resolving revealage with a
        // hardware resolve would average a quantity that is a running product, which is close
        // enough not to look wrong and is not what the maths says; doing it here keeps the two
        // buffers consistent with one another.
        accumulated = float4(0.0, 0.0, 0.0, 0.0);
        revealage = 0.0;

        uint samples = max(SampleCount, 1u);

        for (uint i = 0; i < samples; ++i)
        {
            accumulated += AccumulationMS.Load(pixel, i);
            revealage += RevealageMS.Load(pixel, i);
        }

        accumulated /= samples;
        revealage /= samples;
    }
    else
    {
        accumulated = Accumulation.Load(int3(pixel, 0));
        revealage = Revealage.Load(int3(pixel, 0));
    }

    // Nothing transparent covered this pixel. Returning zero alpha leaves the opaque image exactly
    // as it was, rather than laying a black film over the whole viewport.
    if (revealage >= 1.0)
    {
        discard;
    }

    // The accumulated alpha is the sum of the weights, so dividing by it recovers a weighted
    // average colour. Guarded because a pixel covered only by fragments of vanishing weight has a
    // denominator arbitrarily close to zero, and the resulting infinity spreads.
    float3 colour = accumulated.rgb / max(accumulated.a, 1e-5);

    // Premultiplied out, to be blended over the opaque image with SourceBlend = One. The coverage
    // is one minus what got through -- the same quantity, read the other way round.
    float coverage = saturate(1.0 - revealage);

    return float4(colour * coverage, coverage);
}
