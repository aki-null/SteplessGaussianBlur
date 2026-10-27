// Sigma below 16.1: full-resolution Gaussian rows, the fitted down/up pair
// and the binomial/cubic pyramid. C# side: Layouts/SmallLayout.cs and
// Kernels/SmallKernel.cs.

// Small-layout taps, in source texels.
// Gaussian rows: tap k in [0..15] as (even offset, even weight, odd offset,
// odd weight), for even and odd output pixels (C#: RecordSmallPass).
// Down: three (offset, weight, 0, 0) pairs in [0..2], shared by both axes
// and phases.
// Up: (offset, weight, 0, 0), even phase in [0..1], odd phase in [2..3].
float4 _Taps[32];
int _Count;
// Halos around the active image of the source and target.
int _SmallSourceHalo, _SmallTargetHalo;

// Same sampling rules as SampleClamped in Common.hlsl, kept inline: routing
// the small passes through the shared helper measured up to 9% slower on
// Adreno 830.
float4 SampleSource(float2 pixel)
{
    pixel += _SmallSourceHalo;
    pixel = clamp(pixel, 0.5, _SourceSize.xy - 0.5) + 1.0 / 1024;
    return SAMPLE_TEXTURE2D_X_LOD(_SourceTex, sampler_LinearClamp,
        pixel * _SourceSize.zw, 0);
}

// Fragment position with the target halo removed: pixel j's center is j + 0.5.
float4 SmallPosition(Varyings input)
{
    float4 position = input.positionCS;
    position.xy -= _SmallTargetHalo;
    return position;
}

// (offset, weight) of tap k for this output pixel's parity. One
// wave-uniform load serves both parities; indexing by parity would give
// each lane its own uniform-buffer address.
float2 InteriorTap(int pixel, int k)
{
    float4 taps = _Taps[k];
    return (pixel & 1) == 0 ? taps.xy : taps.zw;
}

// The product of the horizontal and vertical 1D rows. This collapses
// a small Gaussian to one full-resolution draw, at the cost of up to
// 9 bilinear fetches per output pixel.
float4 Gaussian2D(float4 positionCS, int maximum)
{
    int2 pixel = int2(floor(positionCS.xy));
    // Each pass supplies a compile-time maximum. Zero-weight padded
    // taps cover shorter rows without per-pixel loop exits.
    float4 sum = 0;
    [unroll] for (int y = 0; y < maximum; y++)
    {
        float2 ty = InteriorTap(pixel.y, y);
        [unroll] for (int x = 0; x < maximum; x++)
        {
            float2 tx = InteriorTap(pixel.x, x);
            // Half samples relieve register pressure with up to nine
            // live fetches. Float32 HDR and 16-bit normalized sources
            // need more than half's 11-bit significand.
            #if defined(STEPLESS_FULL_PRECISION)
                float4 sampleColor = SampleSource(positionCS.xy + float2(tx.x, ty.x));
            #else
                half4 sampleColor = (half4)SampleSource(positionCS.xy + float2(tx.x, ty.x));
            #endif
            sum += float4(sampleColor) * (tx.y * ty.y);
        }
    }
    return sum;
}

float4 FragGaussian2D2(Varyings input) : SV_Target
{
    UNITY_SETUP_STEREO_EYE_INDEX_POST_VERTEX(input);
    return PassOutput(Gaussian2D(SmallPosition(input), 2));
}
float4 FragGaussian2D3(Varyings input) : SV_Target
{
    UNITY_SETUP_STEREO_EYE_INDEX_POST_VERTEX(input);
    return PassOutput(Gaussian2D(SmallPosition(input), 3));
}

float4 Separable(float4 positionCS, bool vertical)
{
    int pixel = vertical ? (int)floor(positionCS.y) : (int)floor(positionCS.x);
    int count = clamp(_Count, 0, 16);
    float4 sum = 0;
    [loop] for (int k = 0; k < 16; k++)
    {
        if (k >= count)
            break;
        float2 tap = InteriorTap(pixel, k);
        float2 offset = vertical ? float2(0, tap.x) : float2(tap.x, 0);
        sum += SampleSource(positionCS.xy + offset) * tap.y;
    }
    return sum;
}

float4 FragHorizontal(Varyings input) : SV_Target
{
    UNITY_SETUP_STEREO_EYE_INDEX_POST_VERTEX(input);
    return PassOutput(Separable(SmallPosition(input), false));
}

float4 FragVertical(Varyings input) : SV_Target
{
    UNITY_SETUP_STEREO_EYE_INDEX_POST_VERTEX(input);
    return PassOutput(Separable(SmallPosition(input), true));
}

// Unrolled H/V kernels for the common fetch counts; zero-weight taps pad
// shorter rows. `taps` is a literal at each call site.
float4 SeparableUnrolled(float4 positionCS, bool vertical, int taps)
{
    int pixel = vertical ? (int)floor(positionCS.y) : (int)floor(positionCS.x);
    float4 sum = 0;
    [unroll] for (int k = 0; k < taps; k++)
    {
        float2 tap = InteriorTap(pixel, k);
        float2 offset = vertical ? float2(0, tap.x) : float2(tap.x, 0);
        sum += SampleSource(positionCS.xy + offset) * tap.y;
    }
    return sum;
}

float4 FragHorizontal5(Varyings input) : SV_Target
{
    UNITY_SETUP_STEREO_EYE_INDEX_POST_VERTEX(input);
    return PassOutput(SeparableUnrolled(SmallPosition(input), false, 5));
}

float4 FragVertical5(Varyings input) : SV_Target
{
    UNITY_SETUP_STEREO_EYE_INDEX_POST_VERTEX(input);
    return PassOutput(SeparableUnrolled(SmallPosition(input), true, 5));
}

float4 FragHorizontal8(Varyings input) : SV_Target
{
    UNITY_SETUP_STEREO_EYE_INDEX_POST_VERTEX(input);
    return PassOutput(SeparableUnrolled(SmallPosition(input), false, 8));
}

float4 FragVertical8(Varyings input) : SV_Target
{
    UNITY_SETUP_STEREO_EYE_INDEX_POST_VERTEX(input);
    return PassOutput(SeparableUnrolled(SmallPosition(input), true, 8));
}

float4 FragHorizontal12(Varyings input) : SV_Target
{
    UNITY_SETUP_STEREO_EYE_INDEX_POST_VERTEX(input);
    return PassOutput(SeparableUnrolled(SmallPosition(input), false, 12));
}

float4 FragVertical12(Varyings input) : SV_Target
{
    UNITY_SETUP_STEREO_EYE_INDEX_POST_VERTEX(input);
    return PassOutput(SeparableUnrolled(SmallPosition(input), true, 12));
}

// Target pixel center j + 0.5 maps to source coordinate 2j + 1,
// between full-resolution source pixels 2j and 2j + 1.
float4 FragDown(Varyings input) : SV_Target
{
    UNITY_SETUP_STEREO_EYE_INDEX_POST_VERTEX(input);
    float2 center = (input.positionCS.xy - _SmallTargetHalo) * 2;
    float4 sum = 0;
    [unroll] for (int y = 0; y < 3; y++)
    [unroll] for (int x = 0; x < 3; x++)
        sum += SampleSource(center + float2(_Taps[x].x, _Taps[y].x))
            * (_Taps[x].y * _Taps[y].y);
    return PassOutput(sum);
}

// Each fine pixel maps to a different phase of the half-size grid.
// Even and odd output pixels therefore use mirrored coarse-grid pairs.
float4 FragUp(Varyings input) : SV_Target
{
    UNITY_SETUP_STEREO_EYE_INDEX_POST_VERTEX(input);
    int2 pixel = int2(input.positionCS.xy) - _SmallTargetHalo;
    float2 center = float2(pixel >> 1) + 0.5;
    // Each axis selects its parity's (offset, weight) pairs from
    // wave-uniform loads, as InteriorTap does.
    float4 even = float4(_Taps[0].xy, _Taps[1].xy);
    float4 odd = float4(_Taps[2].xy, _Taps[3].xy);
    float4 tx = (pixel.x & 1) == 0 ? even : odd;
    float4 ty = (pixel.y & 1) == 0 ? even : odd;
    float4 sum = SampleSource(center + float2(tx.x, ty.x)) * (tx.y * ty.y);
    sum += SampleSource(center + float2(tx.z, ty.x)) * (tx.w * ty.y);
    sum += SampleSource(center + float2(tx.x, ty.z)) * (tx.y * ty.w);
    sum += SampleSource(center + float2(tx.z, ty.z)) * (tx.w * ty.w);
    return PassOutput(sum);
}

// From sigma 4.5 the coarse image already carries nearly all blur.
// One bilinear reconstruction replaces the outer four-fetch cubic row.
// Alternating offsets preserve the same half-grid alignment as FragUp.
float4 FragLinearUp(Varyings input) : SV_Target
{
    UNITY_SETUP_STEREO_EYE_INDEX_POST_VERTEX(input);
    int2 pixel = int2(input.positionCS.xy) - _SmallTargetHalo;
    float2 center = float2(pixel >> 1) + 0.5;
    float2 offset = float2(pixel & 1) * 0.5 - 0.25;
    return PassOutput(SampleSource(center + offset));
}

// Texel copy for zero-strength RecordTo, with no interpolation.
float4 FragCopy(Varyings input) : SV_Target
{
    UNITY_SETUP_STEREO_EYE_INDEX_POST_VERTEX(input);
    return PassOutput(LOAD_TEXTURE2D_X(_SourceTex, int2(input.positionCS.xy)));
}
