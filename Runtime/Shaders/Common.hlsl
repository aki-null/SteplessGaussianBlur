// Declarations and helpers shared by every pass: the source binding, the
// full-screen triangle and clamp-to-edge sampling.
// Coordinate conventions: Documentation~/Design.md#conventions.

TEXTURE2D_X_FLOAT(_SourceTex);
// xy: active source size in texels; zw: reciprocal of the texture-view size.
float4 _SourceSize;

struct Attributes
{
    uint vertexID : SV_VertexID;
    UNITY_VERTEX_INPUT_INSTANCE_ID
};

struct Varyings
{
    float4 positionCS : SV_POSITION;
    UNITY_VERTEX_OUTPUT_STEREO
};

// One triangle covering the viewport; no vertex buffer.
Varyings Vert(Attributes input)
{
    Varyings output;
    UNITY_SETUP_INSTANCE_ID(input);
    UNITY_INITIALIZE_VERTEX_OUTPUT_STEREO(output);
    float2 uv = float2((input.vertexID << 1) & 2, input.vertexID & 2);
    output.positionCS = float4(uv * 2.0 - 1.0, 0, 1);
    return output;
}

// The order of a quarter reduction's four pairs on one axis. A 2x2 quad
// starts at an even pixel; its odd pixel's first two pairs are the even
// pixel's last two, so the even pixel starts with those. Takes the target
// pixel coordinate, not a halo-offset node: an odd halo flips the parity.
float4 QuarterOrder(float4 row, float pixel)
{
    return (uint(pixel) & 1) != 0 ? row : row.zwxy;
}

// Shared source sampling. `pixel` is a continuous coordinate in source texels:
// texel i covers [i, i + 1] and has its center at i + 0.5.
// Samples outside the source repeat its nearest edge texel (clamp-to-edge).
float4 SampleClamped(float2 pixel)
{
    // C# snaps offsets to the sampler's 1/256-texel grid. Clamping first
    // bounds coordinates and preserves clamp-to-edge sampling. A 1/1024-texel
    // guard keeps a truncating 8-bit sampler in the chosen bin and a nearest
    // sampler below its half-bin boundary. Up to 4096 texels, the three
    // float32 rounding errors (reciprocal, UV multiply, denormalization)
    // total less than the guard (gamma(3) * 4096 = 0.000733 texel). Larger
    // views can exceed it, which moves a fetch by one bin: under 0.4% of
    // that fetch's weight.
    float2 uv = (clamp(pixel, 0.5, _SourceSize.xy - 0.5) + 1.0 / 1024) * _SourceSize.zw;
    return SAMPLE_TEXTURE2D_X_LOD(_SourceTex, sampler_LinearClamp, uv, 0);
}

// B10G11R11 keeps 6 mantissa bits in red and green and 5 in blue. Some GPUs,
// M4 among them, truncate a store into it, and every pass of a layout
// stores, which would darken a blur by up to 4% (9% in blue). Rounding to nearest here
// (ties away from zero) leaves the store an exact value, whether the GPU
// truncates or rounds. Values clamp to the format's range first, so infinity
// and values that would round past its largest stay finite.
float4 PassOutput(float4 value)
{
#if defined(STEPLESS_PACKED_OUTPUT)
    value.rgb = clamp(value.rgb, 0, float3(65024, 65024, 64512));
    uint3 bits = asuint(value.rgb) + uint3(1u << 16, 1u << 16, 1u << 17);
    value.rgb = asfloat(bits & uint3(0xFFFE0000u, 0xFFFE0000u, 0xFFFC0000u));
#endif
    return value;
}
