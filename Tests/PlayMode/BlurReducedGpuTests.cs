using System;
using System.Collections;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.Experimental.Rendering;
using UnityEngine.Rendering;
using UnityEngine.TestTools;

namespace SteplessGaussianBlur.Tests
{
    // Reduced outputs: the texture before each layout's final bilinear
    // magnification, which the viewer's sampler then performs.
    public sealed partial class BlurGpuTests
    {
        [UnityTest]
        public IEnumerator OutputSizesMatchCpu()
        {
            // Pyramid, transition and wide at their largest scale, and the
            // wide layout magnifying to a smaller scale where the sides allow
            // only that.
            var target = new BlurTarget(format: FloatFormat);
            try
            {
                // RecordReduced's own texture, then a BlurTarget.
                foreach (BlurTarget written in new[] { null, target })
                {
                    foreach (float sigma in new[] { 4.5f, 8.6f, 16f, 16.125f, 21f, 23.99f })
                        yield return RunCase(new BlurCase(66, 50, sigma) { Reduced = true, Target = written });
                    yield return RunCase(new BlurCase(64, 48, 24) { Reduced = true, Target = written });
                    yield return RunCase(new BlurCase(66, 50, 30) { Reduced = true, Target = written });
                }
            }
            finally
            {
                target.Dispose();
            }
            foreach (Vector2Int size in new[] { new Vector2Int(512, 384), new Vector2Int(516, 388) })
                foreach (float sigma in new[] { 70f, 140f })
                    yield return RunCase(new BlurCase(size.x, size.y, sigma)
                    {
                        Reduced = true,
                        MeanError = 2 * HalfSpacingBelowOne,
                        Format = GraphicsFormat.R16G16B16A16_SFloat
                    });
        }

        // Sampled bilinearly at the source resolution, a reduced output is the
        // full blur: the sampler runs the layout's last pass. It differs only
        // in the outermost half texel, where the sampler clamps to the edge
        // texel instead of reading the blur's own border extension.
        [UnityTest]
        public IEnumerator SampledOutputSizeMatchesTheFullBlur()
        {
            RequireFloat32();
            foreach ((int width, int height, float sigma) in new[]
                { (66, 50, 6f), (68, 52, 20f), (128, 96, 40f), (512, 384, 100f) })
            {
                Color[] full = null;
                yield return RunCase(new BlurCase(width, height, sigma) { ReceiveResult = pixels => full = pixels });
                Color[] reduced = null;
                yield return RunCase(new BlurCase(width, height, sigma)
                {
                    Reduced = true,
                    ReceiveResult = pixels => reduced = pixels
                });
                OutputSize layout = GaussianBlur.ReducedOutputSize(sigma, width, height);
                Assert.That(layout.Scale, Is.GreaterThan(1));
                (double interior, double border) = SamplingError(View(reduced, layout, width, height), full,
                    width, height, layout.Scale);
                TestContext.WriteLine($"sigma {sigma}, scale {layout.Scale}: " +
                    $"interior {interior:E2}, border {border:E2}");
                Assert.That(interior, Is.LessThan(1e-5), $"sigma {sigma}: interior");
                Assert.That(border, Is.LessThan(0.03), $"sigma {sigma}: outermost half texel");
            }
        }

        /// <summary>Largest error inside, and within the outermost half texel of, the image.</summary>
        static (double interior, double border) SamplingError(Color[] sampled, Color[] full, int width, int height,
            int scale)
        {
            double interior = 0, border = 0;
            for (int y = 0; y < height; y++)
            {
                for (int x = 0; x < width; x++)
                {
                    bool edge = Math.Min(Math.Min(x, width - 1 - x), Math.Min(y, height - 1 - y)) < scale / 2;
                    for (int channel = 0; channel < 4; channel++)
                    {
                        double error = Math.Abs(sampled[y * width + x][channel] - full[y * width + x][channel]);
                        if (edge)
                            border = Math.Max(border, error);
                        else
                            interior = Math.Max(interior, error);
                    }
                }
            }
            return (interior, border);
        }

        // A viewer's bilinear, clamped sampling at the source resolution.
        static Color[] View(Color[] reduced, OutputSize layout, int width, int height)
        {
            int textureWidth = layout.Size.x, textureHeight = layout.Size.y;
            var result = new Color[width * height];
            for (int y = 0; y < height; y++)
            {
                double v = (y + 0.5) / height * textureHeight - 0.5;
                int y0 = Math.Clamp((int)Math.Floor(v), 0, textureHeight - 1);
                int y1 = Math.Clamp((int)Math.Floor(v) + 1, 0, textureHeight - 1);
                float fy = (float)(v - Math.Floor(v));
                for (int x = 0; x < width; x++)
                {
                    double u = (x + 0.5) / width * textureWidth - 0.5;
                    int x0 = Math.Clamp((int)Math.Floor(u), 0, textureWidth - 1);
                    int x1 = Math.Clamp((int)Math.Floor(u) + 1, 0, textureWidth - 1);
                    float fx = (float)(u - Math.Floor(u));
                    Color top = Color.LerpUnclamped(reduced[y0 * textureWidth + x0], reduced[y0 * textureWidth + x1], fx);
                    Color bottom = Color.LerpUnclamped(reduced[y1 * textureWidth + x0], reduced[y1 * textureWidth + x1], fx);
                    result[y * width + x] = Color.LerpUnclamped(top, bottom, fy);
                }
            }
            return result;
        }

        [UnityTest]
        public IEnumerator BlurTargetFollowsTheStrengthsLayout()
        {
            const int width = 64, height = 48;
            Color[] pixels = Pattern(width, height);
            var upload = new Texture2D(width, height, FloatTexture, false, true);
            textures.Add(upload);
            upload.SetPixels(pixels);
            upload.Apply(false, true);
            var target = new BlurTarget(format: FloatFormat);
            // The same texture object is resized for each layout.
            RenderTexture texture = target.Texture;
            try
            {
                foreach ((float sigma, int scale, Vector2Int size) in new[]
                {
                    (30f, 4, new Vector2Int(16, 12)),
                    (40f, 4, new Vector2Int(16, 12)),
                    (3f, 1, new Vector2Int(64, 48)),
                    (8f, 2, new Vector2Int(32, 24))
                })
                {
                    blur.Blur(upload, target, sigma);
                    yield return WaitForBlurs();
                    Assert.That(target.Texture, Is.SameAs(texture), $"sigma {sigma}: texture");
                    Assert.That(new Vector2Int(texture.width, texture.height), Is.EqualTo(size), $"sigma {sigma}: size");
                    yield return ReadBack(texture, actual => AssertPixels(actual,
                        BlurGpuReference.Blur(pixels, width, height, sigma, scale), MaximumError, FloatMeanError,
                        $"Target at sigma {sigma}"));
                }
                // A target's own texture cannot be its source.
                Assert.Throws<ArgumentException>(() => blur.Blur(texture, target, 8f));
            }
            finally
            {
                target.Dispose();
            }
            Assert.That(target.Texture, Is.Null);
            Assert.Throws<ObjectDisposedException>(() => blur.Blur(upload, target, 30f));
        }
    }
}