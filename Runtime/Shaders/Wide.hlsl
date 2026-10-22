// Reduced allocations carry a three-texel halo on every side. Coordinates
// below remove the target halo and add the source halo only when sampling.
// _WideScale: the final magnification, a power of two.
int _WideSourceHalo, _WideTargetHalo, _WideScale, _WideGaussianCount;
// Gaussian pairs: (first offset, bilinear fraction, weight, 0); see WideKernel.
// The even nodes' row of up to 32; odd nodes mirror it. Array sizes set the
// uniform layout, which affects device timing
// (Documentation~/Design.md); keep 64.
// Cubic phases: (left position, right position, left weight, right weight).
float4 _WideGaussian[64], _WideCubic[4];

float4 FragWideQuarter(Varyings input) : SV_Target
{
    UNITY_SETUP_STEREO_EYE_INDEX_POST_VERTEX(input);
    int2 node = int2(input.positionCS.xy) - _WideTargetHalo;
    float2 center = float2(node) * 4 + 0.5;
    // Same eight-node row as TransitionKernel.Quarter. Every source
    // phase retains equal energy and nearly the same center after reduction.
    const float4 positions = float4(-1.25390625, 0.58203125, 2.41796875, 4.25390625);
    const float4 masses = float4(0.125, 0.375, 0.375, 0.125);
    // Neighbors in a 2x2 quad share two of their four pairs per axis.
    // Fetching those in the same instruction halves the distinct texel
    // blocks a quad reads; QuarterOrder mirrors the even pixel's order.
    float4 px = QuarterOrder(positions, input.positionCS.x);
    float4 py = QuarterOrder(positions, input.positionCS.y);
    float4 mx = QuarterOrder(masses, input.positionCS.x);
    float4 my = QuarterOrder(masses, input.positionCS.y);
    float4 anchor = SampleClamped(center + float2(px.x, py.x));
    float4 result = anchor;
    [unroll] for (int y = 0; y < 4; y++)
    {
        [unroll] for (int x = 0; x < 4; x++)
        {
            if (x != 0 || y != 0)
            {
                float4 value = SampleClamped(center + float2(px[x], py[y]));
                // Residual accumulation preserves constant images exactly.
                result += (value - anchor) * mx[x] * my[y];
            }
        }
    }
    return PassOutput(result);
}

float4 FragWideHalve(Varyings input) : SV_Target
{
    UNITY_SETUP_STEREO_EYE_INDEX_POST_VERTEX(input);
    // Two bilinear pairs implement the positive [1,3,3,1]/8 binomial row.
    float2 center = (input.positionCS.xy - _WideTargetHalo) * 2 + _WideSourceHalo;
    // Weight first so finite constant HDR values do not overflow the sum.
    float4 result = SampleClamped(center + float2(-0.75, -0.75)) * 0.25;
    result += SampleClamped(center + float2(0.75, -0.75)) * 0.25;
    result += SampleClamped(center + float2(-0.75, 0.75)) * 0.25;
    result += SampleClamped(center + float2(0.75, 0.75)) * 0.25;
    return PassOutput(result);
}

float4 WideGaussianAxis(Varyings input, bool vertical)
{
    int2 pixel = int2(input.positionCS.xy);
    int2 axis = vertical ? int2(0, 1) : int2(1, 0);
    // Odd nodes read the even row mirrored.
    int node = (vertical ? pixel.y : pixel.x) - _WideTargetHalo;
    int count = min(_WideGaussianCount, 32);
    // Source and destination halos match in the axis passes. Native clamp
    // handles taps outside the allocation; its outer halo texels are constant.
    // Every node reads the row at the same addresses; odd nodes negate its
    // positions.
    float2 direction = (node & 1) ? -float2(axis) : float2(axis);
    int anchorIndex = count / 2;
    float3 first = _WideGaussian[anchorIndex].xyz;
    float2 anchorUv = (float2(pixel) + 0.5 + direction *
        (first.x + first.y) + 1.0 / 1024) * _SourceSize.zw;
    float4 anchor = SAMPLE_TEXTURE2D_X_LOD(_SourceTex, sampler_LinearClamp, anchorUv, 0);
    // Residuals keep constant images exact; precise keeps the compiler
    // from reassociating the sum, which would lose that.
    precise float4 result = anchor;
    [loop] for (int tap = 0; tap < count; tap++)
    {
        if (tap == anchorIndex)
            continue;
        float3 pair = _WideGaussian[tap].xyz;
        float2 uv = (float2(pixel) + 0.5 + direction *
            (pair.x + pair.y) + 1.0 / 1024) * _SourceSize.zw;
        result += (SAMPLE_TEXTURE2D_X_LOD(_SourceTex, sampler_LinearClamp, uv, 0) - anchor) * pair.z;
    }
    return result;
}

float4 FragWideGaussianH(Varyings input) : SV_Target
{
    UNITY_SETUP_STEREO_EYE_INDEX_POST_VERTEX(input);
    return PassOutput(WideGaussianAxis(input, false));
}

float4 FragWideGaussianV(Varyings input) : SV_Target
{
    UNITY_SETUP_STEREO_EYE_INDEX_POST_VERTEX(input);
    return PassOutput(WideGaussianAxis(input, true));
}

float4 WideCubicSample(float2 center, float4 wx, float4 wy)
{
    // Share two X and two Y addresses across the four native queries.
    float2 u = (clamp(center.x + wx.xy + _WideSourceHalo,
        0.5, _SourceSize.x - 0.5) + 1.0 / 1024) * _SourceSize.z;
    float2 v = (clamp(center.y + wy.xy + _WideSourceHalo,
        0.5, _SourceSize.y - 0.5) + 1.0 / 1024) * _SourceSize.w;
    float4 row0 = lerp(
        SAMPLE_TEXTURE2D_X_LOD(_SourceTex, sampler_LinearClamp, float2(u.x, v.x), 0),
        SAMPLE_TEXTURE2D_X_LOD(_SourceTex, sampler_LinearClamp, float2(u.y, v.x), 0), wx.w);
    float4 row1 = lerp(
        SAMPLE_TEXTURE2D_X_LOD(_SourceTex, sampler_LinearClamp, float2(u.x, v.y), 0),
        SAMPLE_TEXTURE2D_X_LOD(_SourceTex, sampler_LinearClamp, float2(u.y, v.y), 0), wx.w);
    return lerp(row0, row1, wy.w);
}

// Selects among loaded phases instead of indexing the array per pixel.
float4 WideCubicPhase(int phase)
{
    return phase == 0 ? _WideCubic[0] : phase == 1 ? _WideCubic[1] : phase == 2 ? _WideCubic[2] : _WideCubic[3];
}

float4 FragWideCubic(Varyings input) : SV_Target
{
    UNITY_SETUP_STEREO_EYE_INDEX_POST_VERTEX(input);
    int2 node = int2(floor(input.positionCS.xy)) - _WideTargetHalo;
    float2 center = float2(node >> 2) + 0.5;
    return PassOutput(WideCubicSample(center, WideCubicPhase(node.x & 3), WideCubicPhase(node.y & 3)));
}

float4 FragWideFinal(Varyings input) : SV_Target
{
    UNITY_SETUP_STEREO_EYE_INDEX_POST_VERTEX(input);
    // Output coordinate x maps to x / scale on the reconstruction grid.
    return PassOutput(SampleClamped(input.positionCS.xy / _WideScale + _WideSourceHalo));
}

float4 FragWideLimit(Varyings input) : SV_Target
{
    UNITY_SETUP_STEREO_EYE_INDEX_POST_VERTEX(input);
    int2 last = int2(_SourceSize.xy) - 1;
    // Quarter each value before summing to avoid overflow for finite HDR
    // inputs near the full-precision format limit.
    float4 result = LOAD_TEXTURE2D_X(_SourceTex, int2(0, 0)) * 0.25;
    result += LOAD_TEXTURE2D_X(_SourceTex, int2(last.x, 0)) * 0.25;
    result += LOAD_TEXTURE2D_X(_SourceTex, int2(0, last.y)) * 0.25;
    result += LOAD_TEXTURE2D_X(_SourceTex, last) * 0.25;
    return PassOutput(result);
}
