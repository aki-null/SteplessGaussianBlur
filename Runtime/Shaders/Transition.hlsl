// Sigma 11.5–24 transition on quarter/eighth-resolution grids. Coordinates are
// texel nodes: node n is texel n of the active image, and its center is
// n + 0.5 in sampling coordinates. Allocation halos carry the original image
// extension between stages; _TransitionSourceOrigin and
// _TransitionTargetOrigin are the halos of each side. The wide layout's
// Quarter starts the layout, and from sigma 16.6 its halve also runs; the
// pyramid's 2x Up and the wide layout's 4x Final always end it.
float4 _TransitionSourceOrigin, _TransitionTargetOrigin;
float4 _TransitionHalve[3], _TransitionGaussian[40];
int _TransitionGaussianCount;

float4 TransitionSample(float2 node)
{
    return SampleClamped(node + _TransitionSourceOrigin.xy + 0.5);
}

int2 TransitionNode(Varyings input) { return int2(input.positionCS.xy) - int2(_TransitionTargetOrigin.xy); }

float4 FragTransitionHalveBlend(Varyings input) : SV_Target
{
    UNITY_SETUP_STEREO_EYE_INDEX_POST_VERTEX(input);
    float2 center = 2 * TransitionNode(input);
    float4 result = 0;
    // Three positive pairs per axis during the reduction transition.
    const int count = 3;
    [unroll] for (int y = 0; y < count; y++)
        [unroll] for (int x = 0; x < count; x++)
            result += TransitionSample(center + float2(_TransitionHalve[x].x, _TransitionHalve[y].x)) * _TransitionHalve[x].y * _TransitionHalve[y].y;
    return PassOutput(result);
}

float4 TransitionGaussianAxis(Varyings input, bool vertical)
{
    int2 node = TransitionNode(input);
    int index = vertical ? node.y : node.x;
    float2 axis = vertical ? float2(0, 1) : float2(1, 0);
    // Every node reads the even row [0..19] at the same addresses; odd nodes
    // negate its offsets. Array sizes set the uniform layout
    // (Documentation~/Design.md); keep 40.
    int count = min(_TransitionGaussianCount, 20), anchorIndex = count / 2;
    float2 direction = (index & 1) ? -axis : axis;
    float4 anchor = TransitionSample(float2(node) + direction * _TransitionGaussian[anchorIndex].x);
    precise float4 result = anchor;
    // A residual central mass absorbs FP32 normalization error.
    // Constant inputs subtract to zero, including HDR tiny images.
    // precise keeps the compiler from reassociating the sum, which would
    // lose that.
    [loop] for (int i = 0; i < count; i++)
    {
        if (i == anchorIndex) continue;
        float4 tap = _TransitionGaussian[i];
        result += (TransitionSample(float2(node) + direction * tap.x) - anchor) * tap.y;
    }
    return result;
}

float4 FragTransitionGaussianH(Varyings input) : SV_Target
{
    UNITY_SETUP_STEREO_EYE_INDEX_POST_VERTEX(input);
    return PassOutput(TransitionGaussianAxis(input, false));
}

float4 FragTransitionGaussianV(Varyings input) : SV_Target
{
    UNITY_SETUP_STEREO_EYE_INDEX_POST_VERTEX(input);
    return PassOutput(TransitionGaussianAxis(input, true));
}
