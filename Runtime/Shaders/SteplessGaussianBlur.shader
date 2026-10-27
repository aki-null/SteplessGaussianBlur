Shader "Hidden/SteplessGaussianBlur"
{
    // Pass names are the C# GaussianBlur.ShaderPass names; the
    // recorder resolves passes by name, so their order here is free.
    // Coordinate conventions: Documentation~/Design.md#conventions.
    SubShader
    {
        Tags { "RenderPipeline" = "UniversalPipeline" }
        Cull Off ZWrite Off ZTest Always Blend Off

        HLSLINCLUDE
        #pragma target 3.5
        #pragma vertex Vert
        #pragma multi_compile _ STEREO_INSTANCING_ON STEREO_MULTIVIEW_ON
        // Selects 2D sampling of _SourceTex, including a monoscopic texture
        // read during XR rendering. Only fragments sample.
        #pragma multi_compile_local_fragment _ DISABLE_TEXTURE2D_X_ARRAY
        // Rounds output for B10G11R11 targets (PassOutput in Common.hlsl).
        #pragma multi_compile_local_fragment _ STEPLESS_PACKED_OUTPUT
        #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
        // Redefine the three 2D/array macros this shader uses. Core's TextureXR
        // also honors the keyword, but instanced stereo needs this block
        // (GPU test StereoEyesRemainIndependent).
        #if defined(DISABLE_TEXTURE2D_X_ARRAY)
            #undef TEXTURE2D_X_FLOAT
            #undef LOAD_TEXTURE2D_X
            #undef SAMPLE_TEXTURE2D_X_LOD
            #define TEXTURE2D_X_FLOAT(name) TEXTURE2D_FLOAT(name)
            #define LOAD_TEXTURE2D_X(name, pixel) LOAD_TEXTURE2D(name, pixel)
            #define SAMPLE_TEXTURE2D_X_LOD(name, samplerName, uv, lod) SAMPLE_TEXTURE2D_LOD(name, samplerName, uv, lod)
        #endif
        // Include order fixes the uniform declaration order; keep it.
        #include "Common.hlsl"
        #include "Small.hlsl"
        #include "Transition.hlsl"
        #include "Wide.hlsl"
        ENDHLSL

        Pass
        {
            Name "Gaussian2D2"
            HLSLPROGRAM
            #pragma multi_compile_local_fragment _ STEPLESS_FULL_PRECISION
            #pragma fragment FragGaussian2D2
            ENDHLSL
        }
        Pass
        {
            Name "Gaussian2D3"
            HLSLPROGRAM
            #pragma multi_compile_local_fragment _ STEPLESS_FULL_PRECISION
            #pragma fragment FragGaussian2D3
            ENDHLSL
        }
        Pass
        {
            Name "Horizontal"
            HLSLPROGRAM
            #pragma fragment FragHorizontal
            ENDHLSL
        }
        Pass
        {
            Name "Vertical"
            HLSLPROGRAM
            #pragma fragment FragVertical
            ENDHLSL
        }
        Pass
        {
            Name "Horizontal5"
            HLSLPROGRAM
            #pragma fragment FragHorizontal5
            ENDHLSL
        }
        Pass
        {
            Name "Vertical5"
            HLSLPROGRAM
            #pragma fragment FragVertical5
            ENDHLSL
        }
        Pass
        {
            Name "Horizontal8"
            HLSLPROGRAM
            #pragma fragment FragHorizontal8
            ENDHLSL
        }
        Pass
        {
            Name "Vertical8"
            HLSLPROGRAM
            #pragma fragment FragVertical8
            ENDHLSL
        }
        Pass
        {
            Name "Horizontal12"
            HLSLPROGRAM
            #pragma fragment FragHorizontal12
            ENDHLSL
        }
        Pass
        {
            Name "Vertical12"
            HLSLPROGRAM
            #pragma fragment FragVertical12
            ENDHLSL
        }
        Pass
        {
            Name "Down"
            HLSLPROGRAM
            #pragma fragment FragDown
            ENDHLSL
        }
        Pass
        {
            Name "Up"
            HLSLPROGRAM
            #pragma fragment FragUp
            ENDHLSL
        }
        Pass
        {
            Name "LinearUp"
            HLSLPROGRAM
            #pragma fragment FragLinearUp
            ENDHLSL
        }
        Pass
        {
            Name "Copy"
            HLSLPROGRAM
            #pragma fragment FragCopy
            ENDHLSL
        }
        Pass
        {
            Name "TransitionHalveBlend"
            HLSLPROGRAM
            #pragma fragment FragTransitionHalveBlend
            ENDHLSL
        }
        Pass
        {
            Name "TransitionGaussianH"
            HLSLPROGRAM
            #pragma fragment FragTransitionGaussianH
            ENDHLSL
        }
        Pass
        {
            Name "TransitionGaussianV"
            HLSLPROGRAM
            #pragma fragment FragTransitionGaussianV
            ENDHLSL
        }
        Pass
        {
            Name "WideQuarter"
            HLSLPROGRAM
            #pragma fragment FragWideQuarter
            ENDHLSL
        }
        Pass
        {
            Name "WideHalve"
            HLSLPROGRAM
            #pragma fragment FragWideHalve
            ENDHLSL
        }
        Pass
        {
            Name "WideGaussianH"
            HLSLPROGRAM
            #pragma fragment FragWideGaussianH
            ENDHLSL
        }
        Pass
        {
            Name "WideGaussianV"
            HLSLPROGRAM
            #pragma fragment FragWideGaussianV
            ENDHLSL
        }
        Pass
        {
            Name "WideCubic"
            HLSLPROGRAM
            #pragma fragment FragWideCubic
            ENDHLSL
        }
        Pass
        {
            Name "WideFinal"
            HLSLPROGRAM
            #pragma fragment FragWideFinal
            ENDHLSL
        }
        Pass
        {
            Name "WideLimit"
            HLSLPROGRAM
            #pragma fragment FragWideLimit
            ENDHLSL
        }
    }
}
