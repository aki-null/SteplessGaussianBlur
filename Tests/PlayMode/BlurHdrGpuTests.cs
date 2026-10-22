using System;
using System.Collections;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.Experimental.Rendering;
using UnityEngine.Rendering;
using UnityEngine.TestTools;

namespace SteplessGaussianBlur.Tests
{
    public sealed partial class BlurGpuTests
    {
        [UnityTest]
        public IEnumerator Float32HighlightCanBlurIntoHalfStorage()
        {
            RequireFloat32();
            const int size = 64;
            var pixels = new Color[size * size];
            // A 3x3 cluster: its quarter-resolution sum exceeds FP16's range
            // although the blurred result does not.
            for (int y = 1; y <= 3; y++)
                for (int x = 1; x <= 3; x++)
                    pixels[y * size + x] = new Color(200000, 0, 0, 1);
            Color[] expected = BlurGpuReference.Blur(pixels, size, size, 24);
            yield return RunHdrCase(pixels, size, 24, GraphicsFormat.R16G16B16A16_SFloat, actual =>
            {
                double maximumError = 0;
                double maximumExpected = 0;
                double actualMass = 0;
                double expectedMass = 0;
                for (int i = 0; i < actual.Length; i++)
                {
                    AssertFinite(actual[i]);
                    maximumError = Math.Max(maximumError, Math.Abs(actual[i].r - expected[i].r));
                    maximumExpected = Math.Max(maximumExpected, expected[i].r);
                    actualMass += actual[i].r;
                    expectedMass += expected[i].r;
                }
                // The oracle omits the final FP16 store. Bound rounding
                // relative to this HDR response, not to unit intensity.
                Assert.That(maximumError / maximumExpected, Is.LessThanOrEqualTo(0.003));
                Assert.That(actualMass / expectedMass, Is.EqualTo(1).Within(0.005));
            });
        }

        [UnityTest]
        public IEnumerator WideReductionPreservesExtremeFiniteConstant()
        {
            RequireFloat32();
            const int size = 64;
            var pixels = new Color[size * size];
            for (int i = 0; i < pixels.Length; i++)
                pixels[i] = new Color(1e38f, 1e38f, 1e38f, 1);
            foreach (float sigma in new[] { 21f, 24f })
            {
                yield return RunHdrCase(pixels, size, sigma, GraphicsFormat.R32G32B32A32_SFloat, actual =>
                {
                    double maximumError = 0;
                    foreach (Color value in actual)
                    {
                        AssertFinite(value);
                        maximumError = Math.Max(maximumError, Math.Abs((double)value.r / 1e38f - 1));
                    }
                    Assert.That(maximumError, Is.LessThanOrEqualTo(1e-5), $"Constant HDR, sigma {sigma}");
                });
            }
        }

        IEnumerator RunHdrCase(Color[] pixels, int size, float sigma, GraphicsFormat format,
            Action<Color[]> verify)
        {
            var source = new Texture2D(size, size, TextureFormat.RGBAFloat, false, true);
            textures.Add(source);
            source.SetPixels(pixels);
            source.Apply(false, true);
            var target = new BlurTarget(fullSize: true, format: format);
            try
            {
                blur.Blur(source, target, sigma);
                yield return WaitForBlurs();
                yield return ReadBack(target.Texture, verify);
            }
            finally
            {
                target.Dispose();
            }
        }

        static void AssertFinite(Color value)
        {
            Assert.That(float.IsFinite(value.r) && float.IsFinite(value.g) &&
                float.IsFinite(value.b) && float.IsFinite(value.a), Is.True, "HDR output must remain finite.");
        }
    }
}
