using System;
using System.Collections;
using System.Collections.Generic;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.Experimental.Rendering;
using UnityEngine.Rendering;
using UnityEngine.Rendering.RenderGraphModule;
using UnityEngine.Rendering.RenderGraphModule.Util;
using UnityEngine.Rendering.Universal;
using UnityEngine.TestTools;
#if UNITY_EDITOR
using UnityEditor;
#endif

namespace SteplessGaussianBlur.Tests
{
    // CPU-reference checks of each layout and of recording state.
    public sealed partial class BlurGpuTests
    {
        [UnityTest]
        public IEnumerator MainLayoutsMatchCpuAndPreserveSource()
        {
            blur.WarmUpShaders(FloatFormat);
            // No-op, full-res 2D Gaussian, the row crossfading into down/up,
            // fitted down/up, half-res Gaussian, pyramid, a level crossfading
            // into one more, and a deeper pyramid.
            foreach (float sigma in new[] { 0f, 0.8f, 1.54f, 1.68f, 2.1f, 2.45f, 4f, 10f })
                yield return RunCase(new BlurCase(32, 24, sigma));
        }

        [UnityTest]
        public IEnumerator HalfStorageLayoutsMatchCpu()
        {
            if (!SystemInfo.SupportsRenderTextureFormat(RenderTextureFormat.ARGBHalf) ||
                !SystemInfo.SupportsTextureFormat(TextureFormat.RGBAHalf))
                Assert.Ignore("This check requires FP16 textures and targets.");
            blur.WarmUpShaders(GraphicsFormat.R16G16B16A16_SFloat);
            // Both sides of the outer Up's bilinear blend and its middle, and
            // deeper layouts. Binary input fractions are exact in FP16, so
            // source-preservation checks retain their strict tolerance.
            // Target stores accumulate quantization; permit two half spacings
            // in mean full-scale error without relaxing the FP32 tests.
            foreach (float sigma in new[] { 4.2499f, 4.375f, 4.5001f, 10f })
                yield return RunCase(new BlurCase(33, 25, sigma)
                {
                    MeanError = 2 * HalfSpacingBelowOne,
                    Format = GraphicsFormat.R16G16B16A16_SFloat
                });
            yield return RunCase(new BlurCase(129, 97, 24)
            {
                MeanError = 2 * HalfSpacingBelowOne,
                Format = GraphicsFormat.R16G16B16A16_SFloat
            });
            yield return RunCase(new BlurCase(257, 193, 96)
            {
                MeanError = 2 * HalfSpacingBelowOne,
                Format = GraphicsFormat.R16G16B16A16_SFloat
            });
        }

        [UnityTest]
        public IEnumerator RoundedSamplerPreservesLayoutsAndHandlesLargeSources()
        {
            // Hardware sampling at rounded positions beyond 4096 texels, where
            // float coordinates no longer guarantee the 1/256 bin. Powers of
            // two scale coordinates exactly; other sizes round them. 8192 at
            // sigma 3000 runs Final past a scale of 128, where its phases fall
            // between the sampler's steps.
            yield return RunCase(new BlurCase(4097, 1, 24f));
            yield return RunCase(new BlurCase(1, 4097, 4f));
            yield return RunCase(new BlurCase(5121, 1, 0.8f));
            yield return RunCase(new BlurCase(1, 7777, 24f));
            if (SystemInfo.maxTextureSize < 16384)
                yield break;
            foreach (float sigma in new[] { 0.8f, 1.8f, 12f, 24f })
                yield return RunCase(new BlurCase(16384, 1, sigma));
            yield return RunCase(new BlurCase(12289, 1, 70f));
            yield return RunCase(new BlurCase(8192, 1, 3000f));
        }

        [UnityTest]
        public IEnumerator OddAndTinyImagesMatchCpu()
        {
            yield return RunCase(new BlurCase(17, 13, 1.54f));
            yield return RunCase(new BlurCase(17, 13, 4f));
            yield return RunCase(new BlurCase(1, 7, 4f));
            yield return RunCase(new BlurCase(1, 1, 100_000_000f));
        }

        [UnityTest]
        public IEnumerator WideTransitionAndExtendedRadiiMatchCpu()
        {
            foreach (float sigma in new[] { 11.5f, 13f, 16.09f, 16.125f, 16.1875f, 16.6f, 21f, 23.99f, 24f, 70f, 139.999f, 140f, 1024f })
                yield return RunCase(new BlurCase(33, 25, sigma));
            // A 33x25 image is near its corner average from sigma 70. These
            // keep enough extent for shape and phase errors to show.
            foreach (float sigma in new[] { 69.99f, 70f, 140f })
                yield return RunCase(new BlurCase(513, 385, sigma)
                {
                    MeanError = 2 * HalfSpacingBelowOne,
                    Format = GraphicsFormat.R16G16B16A16_SFloat
                });
            // Extreme finite strength must remain bounded and finite under
            // the original-source clamp contract, including a one-pixel axis.
            yield return RunCase(new BlurCase(7, 5, float.MaxValue));
            yield return RunCase(new BlurCase(1, 7, float.MaxValue));
            yield return RunCase(new BlurCase(7, 5, float.MaxValue) { ReferenceResolution = 1 });
        }

        [UnityTest]
        public IEnumerator MultipleInvocationsKeepRecordedCoefficientsIndependent()
        {
            // Recording another strength must not mutate the first draw's
            // deferred property snapshot, even if the second result is culled.
            yield return RunCase(new BlurCase(33, 25, 1.55f) { AdditionalStrength = 8 });
            yield return RunCase(new BlurCase(33, 25, 16.125f) { AdditionalStrength = 23.5f });
            yield return RunCase(new BlurCase(33, 25, 24) { AdditionalStrength = 96 });
        }
    }
}
