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
    // Source formats, sizing, MSAA, XR and dynamic resolution.
    public sealed partial class BlurGpuTests
    {
        [UnityTest]
        public IEnumerator PackedHdrCameraColorCanBeFiltered()
        {
            // URP selects this camera format using Blend support. Some Unity
            // backends report Sample/Linear=false despite working sampling.
            if (!SystemInfo.IsFormatSupported(
                GraphicsFormat.B10G11R11_UFloatPack32,
                GraphicsFormatUsage.Blend))
                Assert.Ignore("Packed HDR camera color is unavailable.");
            blur.WarmUpShaders(GraphicsFormat.B10G11R11_UFloatPack32);
            var pixels = new Color[33 * 25];
            for (int y = 0; y < 25; y++)
                for (int x = 0; x < 33; x++)
                    pixels[y * 33 + x] = new Color(x < 13 ? 1 : 0, y < 9 ? 1 : 0, 0.25f, 1);
            // A single pass checks actual interpolation across sharp edges;
            // the target's 5/6-bit mantissas need a packed-format error budget.
            yield return RunCase(new BlurCase(33, 25, 0.8f)
            {
                InputPixels = pixels,
                Format = GraphicsFormat.B10G11R11_UFloatPack32,
                MaximumError = 0.016f,
                MeanError = 0.003f
            });
            // Every pass stores into the packed format. A store that truncates
            // instead of rounding darkens each one by about half a step
            // (0.56%, 1.1% in blue), so the mean error shows it.
            const int width = 384, height = 96;
            Color[] scene = PackedHdrScene(width, height);
            foreach (float sigma in new[] { 0.6f, 1f, 3f, 8f, 24f })
            {
                Color[] actual = null;
                yield return RunCase(new BlurCase(width, height, sigma)
                {
                    InputPixels = scene,
                    Format = GraphicsFormat.B10G11R11_UFloatPack32,
                    WriteToDestination = true,
                    CompareReference = false,
                    ReceiveResult = result => actual = result
                });
                Color[] expected = BlurGpuReference.Blur(scene, width, height, sigma);
                for (int channel = 0; channel < 3; channel++)
                {
                    double bias = 0, largest = 0;
                    for (int i = 0; i < actual.Length; i++)
                    {
                        double relative = (actual[i][channel] - expected[i][channel]) / expected[i][channel];
                        bias += relative;
                        largest = Math.Max(largest, Math.Abs(relative));
                    }
                    bias /= actual.Length;
                    // Measured on M4 with rounding: bias under 0.1%, about 3% at most per pixel.
                    string label = $"sigma {sigma}, channel {channel}";
                    Assert.That(Math.Abs(bias), Is.LessThan(0.0025), label + ", mean relative error");
                    Assert.That(largest, Is.LessThan(0.04), label + ", maximum relative error");
                }
            }
        }

        [UnityTest]
        public IEnumerator PackedOutputRoundsTiesToEven()
        {
            if (!SystemInfo.IsFormatSupported(GraphicsFormat.B10G11R11_UFloatPack32, GraphicsFormatUsage.Render))
                Assert.Ignore("Packed HDR targets are unavailable.");
            // A zero-strength blur into a packed destination copies each
            // texel through the packed-output rounding. Each value lies
            // exactly halfway between two packed values (6 mantissa bits in
            // red and green, 5 in blue). The first pixel's lower neighbor is
            // even, so rounding up fails it; the second's is odd, so
            // truncation fails it.
            var input = new[]
            {
                new Color(0.5f + 0.5f / 128, 0.5f + 0.5f / 128, 0.5f + 0.5f / 64, 1),
                new Color(0.5f + 1.5f / 128, 0.5f + 1.5f / 128, 0.5f + 1.5f / 64, 1)
            };
            var expected = new[]
            {
                new Color(0.5f, 0.5f, 0.5f, 1),
                new Color(0.5f + 2f / 128, 0.5f + 2f / 128, 0.5f + 2f / 64, 1)
            };
            Color[] actual = null;
            yield return RunCase(new BlurCase(2, 1, 0)
            {
                InputPixels = input,
                DestinationFormat = GraphicsFormat.B10G11R11_UFloatPack32,
                WriteToDestination = true,
                CompareReference = false,
                ReceiveResult = result => actual = result
            });
            for (int i = 0; i < expected.Length; i++)
                for (int channel = 0; channel < 3; channel++)
                    Assert.That(actual[i][channel], Is.EqualTo(expected[i][channel]), $"pixel {i}, channel {channel}");
        }

        // A dark gradient, a lit wall, sparse highlights and texture noise,
        // each value exact in B10G11R11.
        static Color[] PackedHdrScene(int width, int height)
        {
            static float Exact(float value, int mantissaBits)
            {
                double step = Math.Pow(2, Math.Floor(Math.Log(value, 2)) - mantissaBits);
                return (float)(Math.Round(value / step) * step);
            }
            var pixels = new Color[width * height];
            uint seed = 12345;
            for (int y = 0; y < height; y++)
                for (int x = 0; x < width; x++)
                {
                    seed = seed * 1664525 + 1013904223;
                    float noise = (seed >> 8) / 16777216f;
                    float u = x / (float)width, v = y / (float)height;
                    float r = 0.02f + 0.6f * u * u + 0.05f * noise;
                    float g = 0.03f + 0.4f * (0.5f + 0.5f * Mathf.Sin(6.28f * (1.5f * u + v))) + 0.03f * noise;
                    float b = 0.05f + 0.25f * v + 0.04f * noise;
                    float highlight = x % 37 == 11 && y % 23 == 7 ? 1 : 0;
                    pixels[y * width + x] = new Color(Exact(r + 24 * highlight, 6), Exact(g + 16 * highlight, 6),
                        Exact(b + 8 * highlight, 5), 1);
                }
            return pixels;
        }

        [UnityTest]
        public IEnumerator EightBitColorCanBeFiltered([Values(false, true)] bool srgb)
        {
            // The common non-HDR camera format. Intermediates keep the source
            // format, so every pass of a layout rounds to 8 bits.
            GraphicsFormat format = srgb ? GraphicsFormat.B8G8R8A8_SRGB : GraphicsFormat.B8G8R8A8_UNorm;
            if (!SystemInfo.IsFormatSupported(format, GraphicsFormatUsage.Render) ||
                !SystemInfo.IsFormatSupported(format, GraphicsFormatUsage.Linear))
                Assert.Ignore($"{format} is unavailable.");
            blur.WarmUpShaders(format);
            // Distinct channels catch swizzles. Values are exact in 8-bit UNorm.
            Color Pixel(int x, int y) => new Color(x < 13 ? 1 : 0, y < 9 ? 1 : 0, 64 / 255f, x < 20 ? 1 : 0);
            // Measured on M4: at most 1.2 UNorm steps, and 2 sRGB steps in
            // linear near white.
            float maximumError = srgb ? 0.01f : 0.006f;
            var pixels = new Color[33 * 25];
            for (int y = 0; y < 25; y++)
                for (int x = 0; x < 33; x++)
                    pixels[y * 33 + x] = Pixel(x, y);
            // One-pass, multi-pass and wide layouts.
            foreach (float sigma in new[] { 0.8f, 3f, 12f, 24f, 70f, 140f })
                yield return RunCase(new BlurCase(33, 25, sigma)
                {
                    InputPixels = pixels,
                    Format = format,
                    MaximumError = maximumError,
                    MeanError = 0.002f
                });
            // A reduced output.
            pixels = new Color[64 * 48];
            for (int y = 0; y < 48; y++)
                for (int x = 0; x < 64; x++)
                    pixels[y * 64 + x] = Pixel(x, y);
            yield return RunCase(new BlurCase(64, 48, 12)
            {
                InputPixels = pixels,
                Format = format,
                Reduced = true,
                MaximumError = maximumError,
                MeanError = 0.002f
            });
        }

        [UnityTest]
        public IEnumerator SeparableFloatSamplesPreserveHdrRange()
        {
            RequireFloat32();
            // Sigma 3 uses a binomial downsample, a separable gaussian, and
            // cubic upsample; none of these stages should narrow samples to half.
            // Float targets isolate sampling precision from attachment rounding.
            var pixels = new Color[32 * 24];
            for (int i = 0; i < pixels.Length; i++)
                pixels[i] = new Color(1, 0.75f, 0.625f, 0.375f);
            // Constant channels isolate sample narrowing from the hardware's
            // bilinear interpolation precision, already covered by pattern tests.
            yield return RunCase(new BlurCase(32, 24, 3)
            {
                SampleScale = 131072f,
                MaximumError = 0.00002f,
                MeanError = 0.000002f,
                InputPixels = pixels
            });
            // Float32 gaussian2D paths must retain the same range at small radii
            // and inside a reduced Gaussian, not only in H/V-only layouts.
            yield return RunCase(new BlurCase(32, 24, 0.8f)
            {
                SampleScale = 131072f,
                MaximumError = 0.00002f,
                MeanError = 0.000002f,
                InputPixels = pixels
            });
            // At sigma 1.8 the fitted pair's half-res Gaussian is one 2D pass.
            double halfResVariance = FittedDownUp.At(BlurMath.RequestedVariance(1.8)).halfResGaussianVariance;
            Assume.That(halfResVariance, Is.GreaterThan(1e-10));
            Assume.That(SmallKernel.GaussianRows(new SmallKernel.Workspace(), halfResVariance), Is.LessThanOrEqualTo(3));
            yield return RunCase(new BlurCase(32, 24, 1.8f)
            {
                SampleScale = 131072f,
                MaximumError = 0.00002f,
                MeanError = 0.000002f,
                InputPixels = pixels
            });
        }

        [UnityTest]
        public IEnumerator ZeroStrengthDestinationCopyIsExact()
        {
            // RecordTo at zero strength copies texels; no filtering or half
            // conversion may change a float32 value.
            yield return RunCase(new BlurCase(33, 25, 0)
            {
                WriteToDestination = true,
                MaximumError = 0,
                MeanError = 0
            });
        }

        [UnityTest]
        public IEnumerator SixteenBitNormalizedSourcesKeepFullPrecision()
        {
            RequireFloat32();
            if (!SystemInfo.SupportsRenderTextureFormat(RenderTextureFormat.ARGB64) ||
                !SystemInfo.IsFormatSupported(GraphicsFormat.R16G16B16A16_UNorm, GraphicsFormatUsage.Linear))
                Assert.Ignore("This check needs filterable 16-bit normalized textures.");
            // A one-pass 2D Gaussian. Half samples would round to an 11-bit
            // significand, about 3.7e-4 near 0.75; full-precision samples stay
            // within 16-bit storage of the float32 result.
            Color[] reference = null, normalized = null;
            yield return RunCase(new BlurCase(33, 25, 0.8f) { ReceiveResult = pixels => reference = pixels });
            yield return RunCase(new BlurCase(33, 25, 0.8f)
            {
                Format = GraphicsFormat.R16G16B16A16_UNorm,
                CompareReference = false,
                ReceiveResult = pixels => normalized = pixels
            });
            AssertPixels(normalized, reference, 4f / 65535, 1f / 65535, "16-bit normalized 2D Gaussian");
        }

        [UnityTest]
        public IEnumerator ActiveViewportExcludesUnusedAllocationPixels()
        {
            foreach (float sigma in new[] { 0.8f, 1.65f, 3f, 16.125f, 24f, 70f })
                yield return RunCase(new BlurCase(69, 51, sigma) { ViewportSize = new Vector2Int(33, 25) });
            yield return RunCase(new BlurCase(69, 51, 3)
            {
                ReferenceResolution = 25,
                ViewportSize = new Vector2Int(33, 25)
            });
            foreach (TextureSizeMode mode in new[] { TextureSizeMode.Scale, TextureSizeMode.Functor })
                yield return RunCase(new BlurCase(69, 51, 3)
                {
                    ViewportSize = new Vector2Int(33, 25),
                    SourceSizeMode = mode
                });
        }

        // Without WithViewport, a Scale or Functor source's active image
        // follows the RTHandle reference size, not its allocation.
        [UnityTest]
        public IEnumerator ScaledSourcesDeriveTheirViewport()
        {
            foreach (TextureSizeMode mode in new[] { TextureSizeMode.Scale, TextureSizeMode.Functor })
                foreach (float sigma in new[] { 3f, 24f })
                    yield return RunCase(new BlurCase(66, 50, sigma)
                    {
                        SourceSizeMode = mode,
                        HalfReferenceSize = true
                    });
        }

        [UnityTest]
        public IEnumerator ResolvedMsaaSourceDoesNotNeedABlurCopy()
        {
            if (!SystemInfo.SupportsRenderTextureFormat(RenderTextureFormat.ARGBHalf))
                Assert.Ignore("MSAA check needs RGBA16F.");
            var descriptor = new RenderTextureDescriptor(33, 25, RenderTextureFormat.ARGBHalf, 0)
            {
                msaaSamples = 4
            };
            if (SystemInfo.GetRenderTextureSupportedMSAASampleCount(descriptor) < 4)
                Assert.Ignore("RGBA16F 4x MSAA is unavailable.");
            foreach (float sigma in new[] { 0.8f, 3f, 24f })
                yield return RunCase(new BlurCase(33, 25, sigma)
                {
                    Format = GraphicsFormat.R16G16B16A16_SFloat,
                    MsaaSamples = 4
                });
        }

        [UnityTest]
        public IEnumerator StereoEyesRemainIndependent()
        {
            if (!SystemInfo.supports2DArrayTextures)
                Assert.Ignore("Stereo check needs texture arrays.");
            foreach (float sigma in new[] { 0.8f, 3f, 16.125f, 24f })
                yield return RunCase(new BlurCase(33, 25, sigma) { Slices = 2 });
            foreach (float sigma in new[] { 0.8f, 3f, 24f })
                yield return RunCase(new BlurCase(33, 25, sigma) { StereoContext = true });
        }

        [UnityTest]
        public IEnumerator HardwareDynamicResolutionUsesCurrentTextureView()
        {
            if (SystemInfo.graphicsDeviceType != GraphicsDeviceType.Metal)
                Assert.Ignore("Hardware scaling check currently targets Metal.");
            foreach (bool explicitScale in new[] { false, true })
                foreach (float scale in new[] { 0.5f, 0.75f })
                    foreach (float sigma in new[] { 3f, 24f })
                        yield return RunDynamicCase(scale, sigma, explicitScale);
        }

        IEnumerator RunDynamicCase(float scale, float sigma, bool explicitScale)
        {
            int width = Mathf.CeilToInt(69 * scale);
            int height = Mathf.CeilToInt(51 * scale);
            Color[] pixels = Pattern(width, height);
            Color[] expected = BlurGpuReference.Blur(pixels, width, height, sigma);
            var upload = new Texture2D(width, height, TextureFormat.RGBAFloat, false, true);
            textures.Add(upload);
            upload.SetPixels(pixels);
            upload.Apply(false, false);
            var source = new RenderTexture(69, 51, 0, RenderTextureFormat.ARGBFloat)
            {
                useDynamicScale = !explicitScale,
                useDynamicScaleExplicit = explicitScale
            };
            textures.Add(source);
            Assert.That(source.Create(), Is.True);
            RenderTexture destination = CreateTarget(width, height);
            RTHandle input = RTHandles.Alloc(source);
            RTHandle output = RTHandles.Alloc(destination);
            handles.Add(input);
            handles.Add(output);
            float previousX = ScalableBufferManager.widthScaleFactor;
            float previousY = ScalableBufferManager.heightScaleFactor;
            bool rendered = false;
            Exception error = null;
            try
            {
                pendingRender = (context, cameras) =>
                {
                    var commands = CommandBufferPool.Get("Dynamic blur test");
                    try
                    {
                        ScalableBufferManager.ResizeBuffers(scale, scale);
                        if (explicitScale)
                            source.ApplyDynamicScale();
                        // Scaling changes the native view; populate that view
                        // afterward, rather than relying on retained alias contents.
                        Graphics.Blit(upload, source);
                        graph.BeginRecording(new RenderGraphParameters
                        {
                            currentFrameIndex = Time.frameCount,
                            scriptableRenderContext = context,
                            commandBuffer = commands
                        });
                        TextureHandle imported = graph.ImportTexture(input);
                        Vector2Int size = new Vector2Int(width, height);
                        TextureHandle result = explicitScale
                            ? blur.Record(graph, ((BlurSource)imported).WithViewport(size, size), sigma)
                            : blur.Record(graph, imported, sigma);
                        TextureDesc descriptor = graph.GetTextureDesc(result);
                        Assert.That(descriptor.width, Is.EqualTo(explicitScale ? width : 69));
                        Assert.That(descriptor.height, Is.EqualTo(explicitScale ? height : 51));
                        Assert.That(descriptor.useDynamicScale, Is.EqualTo(!explicitScale),
                            "Automatic hardware scaling should retain the full-size allocation.");
                        Assert.That(descriptor.useDynamicScaleExplicit, Is.False);
                        // CopyTexture validates nominal dimensions. Sampling
                        // exports the current hardware-scaled view instead.
                        graph.AddBlitPass(result, graph.ImportTexture(output), Vector2.one, Vector2.zero,
                            passName: "Export dynamic blur");
                        graph.EndRecordingAndExecute();
                        context.ExecuteCommandBuffer(commands);
                        commands.Clear();
                        context.Submit();
                        graph.EndFrame();
                    }
                    catch (Exception exception)
                    {
                        error = exception;
                        graph.ResetGraphAndLogException(exception);
                    }
                    finally
                    {
                        CommandBufferPool.Release(commands);
                        rendered = true;
                    }
                };
                float deadline = Time.realtimeSinceStartup + TimeoutSeconds;
                while (!rendered && Time.realtimeSinceStartup < deadline)
                    yield return null;
                Assert.That(rendered, Is.True);
                Assert.That(error, Is.Null);
                var request = AsyncGPUReadback.Request(destination, 0, TextureFormat.RGBAFloat);
                deadline = Time.realtimeSinceStartup + TimeoutSeconds;
                while (!request.done && Time.realtimeSinceStartup < deadline)
                    yield return null;
                Assert.That(request.done && !request.hasError, Is.True);
                AssertPixels(ReadPixels(request), expected, MaximumError, MeanError,
                    $"Hardware scale {scale}, explicit={explicitScale}, sigma {sigma}");
            }
            finally
            {
                pendingRender = null;
                ScalableBufferManager.ResizeBuffers(previousX, previousY);
            }
        }
    }
}
