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
    /// <summary>
    /// Small GPU readback checks for the public RenderGraph utility. Run in
    /// PlayMode with URP and a graphics device; no renderer asset edits are needed.
    /// </summary>
    public sealed partial class BlurGpuTests
    {
        // Half-precision shader samples can round at every stage. These are
        // absolute errors for input channels in [0, 1], not Gaussian fit errors.
        const float MaximumError = 0.003f;
        const float MeanError = 0.0005f;
        // Largest spacing between adjacent half values in [0, 1]. A pass
        // layout change introduces additional sample rounding even when its
        // mathematical filter is continuous.
        const float HalfSpacingBelowOne = 1f / 2048f;
        const float TimeoutSeconds = 15f;
        // Most mobile GPUs cannot filter float32, so float cases run in FP16
        // there, with FP16 storage's error budget. STEPLESS_TEST_HALF_FALLBACK=1
        // selects that path on any GPU.
        static readonly bool HalfFloat =
            Environment.GetEnvironmentVariable("STEPLESS_TEST_HALF_FALLBACK") == "1" ||
            !SystemInfo.IsFormatSupported(GraphicsFormat.R32G32B32A32_SFloat, GraphicsFormatUsage.Linear);
        static GraphicsFormat FloatFormat =>
            HalfFloat ? GraphicsFormat.R16G16B16A16_SFloat : GraphicsFormat.R32G32B32A32_SFloat;
        static TextureFormat FloatTexture => HalfFloat ? TextureFormat.RGBAHalf : TextureFormat.RGBAFloat;
        static float FloatMeanError => HalfFloat ? 2 * HalfSpacingBelowOne : MeanError;

        // For tests of float32 range or precision themselves.
        static void RequireFloat32()
        {
            if (HalfFloat)
                Assert.Ignore("This test checks float32 range or precision, which this GPU cannot filter.");
        }

        GaussianBlur blur;
        Shader shader;
        RenderGraph graph;
        GameObject cameraObject;
        Camera camera;
        RenderTexture cameraTarget;
        Action<ScriptableRenderContext, List<Camera>> pendingRender;
        Action restoreSharedState;
        readonly List<UnityEngine.Object> textures = new List<UnityEngine.Object>();
        readonly List<RTHandle> handles = new List<RTHandle>();

        [SetUp]
        public void SetUp()
        {
            const CopyTextureSupport requiredCopies = CopyTextureSupport.Basic | CopyTextureSupport.DifferentTypes;
            if (SystemInfo.graphicsDeviceType == GraphicsDeviceType.Null ||
                !SystemInfo.supportsAsyncGPUReadback ||
                !SystemInfo.SupportsRenderTextureFormat(RenderTextureFormat.ARGBFloat) ||
                !SystemInfo.SupportsTextureFormat(TextureFormat.RGBAFloat) ||
                (SystemInfo.copyTextureSupport & requiredCopies) != requiredCopies)
                Assert.Ignore("GPU tests require float textures, texture copies, and asynchronous readback.");
            // The package requires URP: a missing asset is a broken setup, not an unsupported device.
            Assert.That(GraphicsSettings.currentRenderPipeline is UniversalRenderPipelineAsset, Is.True,
                "GPU tests require an active URP asset.");

#if UNITY_EDITOR
            shader = AssetDatabase.LoadAssetAtPath<Shader>(
                "Packages/net.aki-null.stepless-gaussian-blur/Runtime/Shaders/SteplessGaussianBlur.shader");
#else
            shader = Shader.Find("Hidden/SteplessGaussianBlur");
#endif
            Assert.That(shader, Is.Not.Null, "Include the blur shader when running tests in a Player.");
            Assert.That(shader.isSupported, Is.True);
            blur = new GaussianBlur(shader);
            graph = new RenderGraph("Stepless Gaussian GPU Tests");

            // A tiny camera ensures SRP supplies a real rendering context even
            // in an empty test scene. Its target is separate from test outputs.
            cameraTarget = CreateTarget(2, 2, 24);
            cameraObject = new GameObject("Stepless Gaussian GPU Test Camera");
            camera = cameraObject.AddComponent<Camera>();
            camera.targetTexture = cameraTarget;
            camera.cullingMask = 0;
            cameraObject.AddComponent<UniversalAdditionalCameraData>().renderPostProcessing = false;
            RenderPipelineManager.beginContextRendering += RenderPending;
        }

        [TearDown]
        public void TearDown()
        {
            // A failure inside a nested coroutine skips the test's finally;
            // undo shared state, such as an installed renderer feature, here.
            restoreSharedState?.Invoke();
            restoreSharedState = null;
            RenderPipelineManager.beginContextRendering -= RenderPending;
            pendingRender = null;
            if (cameraObject != null)
                UnityEngine.Object.DestroyImmediate(cameraObject);
            graph?.Cleanup();
            graph = null;
            blur?.Dispose();
            blur = null;
            foreach (RTHandle handle in handles)
                handle.Release();
            handles.Clear();
            foreach (UnityEngine.Object texture in textures)
                UnityEngine.Object.DestroyImmediate(texture);
            textures.Clear();
        }

        /// <summary>One GPU readback case; defaults compare an FP32 pattern against the CPU reference.</summary>
        sealed class BlurCase
        {
            internal readonly int Width, Height;
            internal readonly float Strength;
            internal int ReferenceResolution;
            internal Action<Color[]> ReceiveResult;
            internal float SampleScale = 1;
            internal float MaximumError = BlurGpuTests.MaximumError;
            internal float MeanError = BlurGpuTests.MeanError;
            internal Color[] InputPixels;
            internal GraphicsFormat Format = GraphicsFormat.R32G32B32A32_SFloat;
            // With WriteToDestination: the destination's format, if not Format.
            internal GraphicsFormat? DestinationFormat;
            // Recorded after the main blur; its result is unused and culled.
            internal float? AdditionalStrength;
            internal Vector2Int ViewportSize;
            internal TextureSizeMode SourceSizeMode = TextureSizeMode.Explicit;
            // With a Scale or Functor source and no ViewportSize: the RTHandle
            // reference size is half the maximum, so the active image is the
            // allocation's lower-left half on each axis.
            internal bool HalfReferenceSize;
            internal int MsaaSamples = 1;
            internal int Slices = 1;
            internal bool StereoContext;
            internal bool WriteToDestination;
            // Record the reduced output instead.
            internal bool Reduced;
            // Record into this target (RecordTo), then copy it out; set Reduced.
            internal BlurTarget Target;
            internal bool CompareReference = true;

            internal BlurCase(int width, int height, float strength)
            {
                Width = width;
                Height = height;
                Strength = strength;
            }
        }

        IEnumerator RunCase(BlurCase c)
        {
            int firstTexture = textures.Count, firstHandle = handles.Count;
            if (c.Format == GraphicsFormat.R32G32B32A32_SFloat && HalfFloat)
            {
                if (c.SampleScale != 1 || c.InputPixels != null && Array.Exists(c.InputPixels, p => p.maxColorComponent > 1))
                    Assert.Ignore("This HDR case needs filterable float32 textures.");
                c.Format = FloatFormat;
                c.MeanError = Math.Max(c.MeanError, FloatMeanError);
            }
            // Scaling a supplied fixture must not mutate it for the next case.
            Color[] sourcePixels = c.InputPixels == null ? Pattern(c.Width, c.Height) : (Color[])c.InputPixels.Clone();
            if (c.SampleScale != 1)
                for (int i = 0; i < sourcePixels.Length; i++)
                    sourcePixels[i] *= c.SampleScale;
            Vector2Int active = c.ViewportSize != default ? c.ViewportSize
                : c.HalfReferenceSize ? new Vector2Int(c.Width / 2, c.Height / 2)
                : new Vector2Int(c.Width, c.Height);
            if (active != new Vector2Int(c.Width, c.Height))
                for (int y = 0; y < c.Height; y++)
                    for (int x = 0; x < c.Width; x++)
                        if (x >= active.x || y >= active.y)
                            sourcePixels[y * c.Width + x] = new Color(1024, 256, 64, 1);
            if (c.Slices > 1)
            {
                var eyes = new Color[sourcePixels.Length * c.Slices];
                for (int eye = 0; eye < c.Slices; eye++)
                    for (int i = 0; i < sourcePixels.Length; i++)
                    {
                        Color pixel = sourcePixels[i];
                        eyes[eye * sourcePixels.Length + i] = eye == 0 ? pixel :
                            new Color(pixel.b, pixel.r, pixel.g, pixel.a);
                    }
                sourcePixels = eyes;
            }
            float sigma = c.ReferenceResolution == 0 ? c.Strength :
                c.Strength * Math.Min(active.x, active.y) / c.ReferenceResolution;
            OutputSize layout = c.Reduced
                ? GaussianBlur.ReducedOutputSize(sigma, active.x, active.y) : OutputSize.Full(active.x, active.y);
            int scale = layout.Scale;
            var outputSize = new Vector2Int(active.x / scale, active.y / scale);
            var expected = c.CompareReference ? new Color[outputSize.x * outputSize.y * c.Slices] : null;
            for (int eye = 0; c.CompareReference && eye < c.Slices; eye++)
            {
                var pixels = new Color[active.x * active.y];
                for (int y = 0; y < active.y; y++)
                    Array.Copy(sourcePixels, eye * c.Width * c.Height + y * c.Width, pixels, y * active.x, active.x);
                Array.Copy(BlurGpuReference.Blur(pixels, active.x, active.y, sigma, scale), 0,
                    expected, eye * outputSize.x * outputSize.y, outputSize.x * outputSize.y);
            }
            RenderTexture source = CreateTarget(c.Width, c.Height, format: c.Format,
                msaaSamples: c.MsaaSamples, slices: c.Slices);
            RenderTexture destination = CreateTarget(layout.Size.x, layout.Size.y,
                format: c.DestinationFormat ?? c.Format, slices: c.Slices);
            bool half = c.Format == GraphicsFormat.R16G16B16A16_SFloat;
            // Float uploads copy; other formats convert in a Blit.
            bool copy = c.MsaaSamples == 1 && (half || c.Format == GraphicsFormat.R32G32B32A32_SFloat);
            var upload = new Texture2D(c.Width, c.Height,
                half ? TextureFormat.RGBAHalf : TextureFormat.RGBAFloat, false, true);
            textures.Add(upload);
            for (int eye = 0; eye < c.Slices; eye++)
            {
                var pixels = new Color[c.Width * c.Height];
                Array.Copy(sourcePixels, eye * pixels.Length, pixels, 0, pixels.Length);
                upload.SetPixels(pixels);
                upload.Apply(false, false);
                if (copy)
                    Graphics.CopyTexture(upload, 0, 0, source, eye, 0);
                else
                    Graphics.Blit(upload, source); // Convert c.Format or populate all MSAA samples.
            }
            RTHandle input = RTHandles.Alloc(source);
            RTHandle output = RTHandles.Alloc(destination);
            handles.Add(input);
            handles.Add(output);

            Exception renderingError = null;
            bool rendered = false;
            bool sameHandle = false;
            pendingRender = (context, cameras) =>
            {
                var commands = CommandBufferPool.Get("Stepless Gaussian GPU Test");
                int previousViews = TextureXR.slices;
                RTHandleProperties previousHandles = RTHandles.rtHandleProperties;
                bool stereo = c.Slices > 1 || c.StereoContext;
                try
                {
                    if (stereo)
                        TextureXR.maxViews = 2;
                    graph.BeginRecording(new RenderGraphParameters
                    {
                        currentFrameIndex = Time.frameCount,
                        scriptableRenderContext = context,
                        commandBuffer = commands
                    });
                    TextureHandle importedSource = graph.ImportTexture(input);
                    if (c.HalfReferenceSize)
                    {
                        RTHandles.ResetReferenceSize(2 * c.Width, 2 * c.Height);
                        RTHandles.SetReferenceSize(c.Width, c.Height);
                    }
                    if (c.SourceSizeMode != TextureSizeMode.Explicit)
                    {
                        Vector2Int maximum = RTHandles.rtHandleProperties.currentRenderTargetSize;
                        // Both size the allocation W x H at the maximum, and
                        // the active image in proportion at the reference size.
                        TextureDesc descriptor = c.SourceSizeMode == TextureSizeMode.Scale
                            ? new TextureDesc(new Vector2((float)c.Width / maximum.x, (float)c.Height / maximum.y))
                            : new TextureDesc(size => new Vector2Int(size.x * c.Width / maximum.x,
                                size.y * c.Height / maximum.y));
                        descriptor.colorFormat = source.graphicsFormat;
                        importedSource = graph.CreateTexture(descriptor);
                        using (var builder = graph.AddUnsafePass<CopyData>("Populate scaled source", out var data))
                        {
                            data.source = graph.ImportTexture(input);
                            data.destination = importedSource;
                            builder.UseTexture(data.source, AccessFlags.Read);
                            builder.UseTexture(importedSource, AccessFlags.Write);
                            builder.SetRenderFunc((CopyData pass, UnsafeGraphContext passContext) =>
                                passContext.cmd.CopyTexture((Texture)pass.source, (Texture)pass.destination));
                        }
                    }
                    TextureHandle importedOutput = graph.ImportTexture(output);
                    BlurStrength blurStrength = c.ReferenceResolution == 0 ? c.Strength :
                        BlurStrength.AtReferenceResolution(c.Strength, c.ReferenceResolution);
                    BlurSource blurSource = c.ViewportSize == default ? importedSource :
                        ((BlurSource)importedSource).WithViewport(active);
                    TextureHandle result = c.Target != null
                        ? blur.RecordTo(graph, blurSource, c.Target, blurStrength)
                        : c.WriteToDestination
                        ? blur.RecordTo(graph, blurSource, importedOutput, blurStrength)
                        : c.Reduced ? blur.RecordReduced(graph, blurSource, blurStrength)
                        : blur.Record(graph, blurSource, blurStrength);
                    sameHandle = result.Equals(importedSource);
                    if (c.AdditionalStrength.HasValue)
                        blur.Record(graph, importedSource, c.AdditionalStrength.Value);
                    // Export the returned result so RenderGraph retains its
                    // producers and the GPU readback can outlive graph resources.
                    if (!c.WriteToDestination)
                    {
                        using (var builder = graph.AddUnsafePass<CopyData>("Export blur for readback", out var data))
                        {
                            data.source = result;
                            data.destination = importedOutput;
                            builder.UseTexture(result, AccessFlags.Read);
                            builder.UseTexture(importedOutput, AccessFlags.Write);
                            builder.SetRenderFunc((CopyData pass, UnsafeGraphContext passContext) =>
                                passContext.cmd.CopyTexture((Texture)pass.source, (Texture)pass.destination));
                        }
                    }
                    if (stereo)
                    {
                        commands.EnableShaderKeyword("STEREO_INSTANCING_ON");
                        commands.SetInstanceMultiplier(2);
                    }
                    graph.EndRecordingAndExecute();
                    if (stereo)
                    {
                        commands.SetInstanceMultiplier(1);
                        commands.DisableShaderKeyword("STEREO_INSTANCING_ON");
                    }
                    context.ExecuteCommandBuffer(commands);
                    commands.Clear();
                    context.Submit();
                    graph.EndFrame();
                }
                catch (Exception exception)
                {
                    renderingError = exception;
                    graph.ResetGraphAndLogException(exception);
                }
                finally
                {
                    TextureXR.maxViews = previousViews;
                    if (c.HalfReferenceSize)
                    {
                        RTHandles.ResetReferenceSize(previousHandles.currentRenderTargetSize.x,
                            previousHandles.currentRenderTargetSize.y);
                        RTHandles.SetReferenceSize(previousHandles.currentViewportSize.x,
                            previousHandles.currentViewportSize.y);
                    }
                    CommandBufferPool.Release(commands);
                    rendered = true;
                }
            };

            float deadline = Time.realtimeSinceStartup + TimeoutSeconds;
            while (!rendered && Time.realtimeSinceStartup < deadline)
                yield return null;
            pendingRender = null;
            Assert.That(rendered, Is.True, "Timed out waiting for the test camera's SRP context.");
            Assert.That(renderingError, Is.Null);
            Assert.That(sameHandle, Is.EqualTo(!c.WriteToDestination && !c.Reduced &&
                (sigma == 0 || active.x == 1 && active.y == 1)),
                "A nonzero blur should return its new target, except for a one-pixel image.");

            Color[] actual = null;
            Color[] unchanged = null;
            bool resultDone = false;
            bool sourceDone = false;
            bool resultError = false;
            bool sourceError = false;
            // Readback results live for one frame after completion. Requests
            // can finish in different frames, so copy each result in its own
            // callback instead of waiting for both requests before reading.
            // Some formats, such as 8-bit on Metal, lack readback support; read a float copy.
            bool convert = !SystemInfo.IsFormatSupported(c.Format, GraphicsFormatUsage.ReadPixels);
            RenderTexture resultReadback = destination;
            if (convert || !SystemInfo.IsFormatSupported(destination.graphicsFormat, GraphicsFormatUsage.ReadPixels))
            {
                resultReadback = CreateTarget(destination.width, destination.height);
                Graphics.Blit(destination, resultReadback);
            }
            AsyncGPUReadback.Request(resultReadback, 0, TextureFormat.RGBAFloat, request =>
            {
                resultError = request.hasError;
                if (!resultError)
                    actual = ReadPixels(request);
                resultDone = true;
            });
            RenderTexture sourceReadback = source;
            if (c.MsaaSamples > 1 || convert)
            {
                sourceReadback = CreateTarget(c.Width, c.Height);
                Graphics.Blit(source, sourceReadback);
            }
            AsyncGPUReadback.Request(sourceReadback, 0, TextureFormat.RGBAFloat, request =>
            {
                sourceError = request.hasError;
                if (!sourceError)
                    unchanged = ReadPixels(request);
                sourceDone = true;
            });
            deadline = Time.realtimeSinceStartup + TimeoutSeconds;
            while ((!resultDone || !sourceDone) && Time.realtimeSinceStartup < deadline)
                yield return null;
            string label = $"{c.Width}x{c.Height}, sigma {sigma}" + (scale > 1 ? $", scale {scale}" : "");
            Assert.That(resultDone && sourceDone, Is.True, label + ": GPU readback timed out.");
            Assert.That(resultError, Is.False, label + ": output GPU readback failed.");
            Assert.That(sourceError, Is.False, label + ": source GPU readback failed.");
            // Normalize the HDR comparison to the input range so tolerances
            // represent relative full-scale error rather than arbitrary HDR units.
            if (c.SampleScale != 1)
            {
                for (int i = 0; i < actual.Length; i++)
                {
                    for (int channel = 0; channel < 4; channel++)
                        Assert.That(float.IsFinite(actual[i][channel]), Is.True,
                            "Float sampling must preserve finite HDR values.");
                    actual[i] /= c.SampleScale;
                    if (expected != null)
                        expected[i] /= c.SampleScale;
                }
            }
            if (c.CompareReference)
                AssertPixels(actual, expected, c.MaximumError, c.MeanError, label);
            // Normalized formats hold the fixture to half a step. sRGB encoding
            // moves non-binary values by under 1/1000 in linear.
            float storage = GraphicsFormatUtility.IsSRGBFormat(c.Format) ? 0.001f :
                c.Format == GraphicsFormat.R16G16B16A16_UNorm ? 0.5f / 65535 :
                c.Format == GraphicsFormat.B8G8R8A8_UNorm ? 0.5f / 255 : 1e-6f;
            AssertPixels(unchanged, sourcePixels, storage, storage, "Source must remain unchanged");
            c.ReceiveResult?.Invoke(actual);
            ReleaseSince(firstTexture, firstHandle);
        }

        // A test can run many cases; free each case's textures rather than
        // holding them all until TearDown.
        void ReleaseSince(int firstTexture, int firstHandle)
        {
            for (int i = handles.Count - 1; i >= firstHandle; i--)
            {
                handles[i].Release();
                handles.RemoveAt(i);
            }
            for (int i = textures.Count - 1; i >= firstTexture; i--)
            {
                UnityEngine.Object.DestroyImmediate(textures[i]);
                textures.RemoveAt(i);
            }
        }

        /// <summary>Waits until a camera has recorded the pending blurs.</summary>
        IEnumerator WaitForBlurs()
        {
            float deadline = Time.realtimeSinceStartup + TimeoutSeconds;
            while (GaussianBlur.AnyPendingBlur && Time.realtimeSinceStartup < deadline)
                yield return null;
            Assert.That(GaussianBlur.AnyPendingBlur, Is.False, "No camera recorded the pending blurs.");
        }

        /// <summary>Reads a texture back as float colors.</summary>
        IEnumerator ReadBack(Texture texture, Action<Color[]> receive)
        {
            Color[] pixels = null;
            bool done = false;
            // Results live for one frame; copy them in the callback.
            AsyncGPUReadback.Request(texture, 0, TextureFormat.RGBAFloat, request =>
            {
                if (!request.hasError)
                    pixels = ReadPixels(request);
                done = true;
            });
            float deadline = Time.realtimeSinceStartup + TimeoutSeconds;
            while (!done && Time.realtimeSinceStartup < deadline)
                yield return null;
            Assert.That(pixels, Is.Not.Null, "Readback failed or timed out.");
            receive(pixels);
        }

        static Color[] ReadPixels(AsyncGPUReadbackRequest request)
        {
            int layerSize = request.GetData<Color>().Length;
            var pixels = new Color[layerSize * request.layerCount];
            for (int layer = 0; layer < request.layerCount; layer++)
            {
                var data = request.GetData<Color>(layer);
                for (int i = 0; i < data.Length; i++)
                    pixels[layer * layerSize + i] = data[i];
            }
            return pixels;
        }

        void RenderPending(ScriptableRenderContext context, List<Camera> cameras)
        {
            if (pendingRender == null || !cameras.Contains(camera))
                return;
            Action<ScriptableRenderContext, List<Camera>> render = pendingRender;
            pendingRender = null;
            render(context, cameras);
        }

        RenderTexture CreateTarget(int width, int height, int depthBits = 0,
            GraphicsFormat format = GraphicsFormat.R32G32B32A32_SFloat, int msaaSamples = 1, int slices = 1)
        {
            // URP camera targets require depth. Blur inputs and outputs are
            // color-only textures so they keep the default zero depth bits.
            var target = new RenderTexture(new RenderTextureDescriptor(width, height, format, depthBits)
            {
                msaaSamples = msaaSamples,
                dimension = slices > 1 ? TextureDimension.Tex2DArray : TextureDimension.Tex2D,
                volumeDepth = slices
            })
            {
                filterMode = FilterMode.Bilinear,
                wrapMode = TextureWrapMode.Clamp,
                name = "Stepless Gaussian GPU Test Texture"
            };
            textures.Add(target);
            Assert.That(target.Create(), Is.True);
            return target;
        }

        static Color[] Pattern(int width, int height)
        {
            var pixels = new Color[width * height];
            for (int y = 0; y < height; y++)
            {
                for (int x = 0; x < width; x++)
                {
                    // Distinct asymmetric channels reveal parity, axis swaps,
                    // orientation, border clamping, and constant-color drift.
                    float impulse = x == width / 2 && y == height / 3 ? 1f : 0f;
                    float border = x == 0 || y == height - 1 ? 0.75f : 0f;
                    float detail = ((3 * x + 5 * y) % 13) / 16f;
                    pixels[y * width + x] = new Color(impulse, border, detail, 0.375f);
                }
            }
            return pixels;
        }

        static void AssertPixels(Color[] actual, Color[] expected, float maximum, float mean, string label)
        {
            Assert.That(actual.Length, Is.EqualTo(expected.Length));
            double sum = 0;
            float largest = 0;
            for (int i = 0; i < actual.Length; i++)
            {
                for (int channel = 0; channel < 4; channel++)
                {
                    float value = actual[i][channel];
                    if (!float.IsFinite(value))
                        Assert.Fail($"{label}, pixel {i}, channel {channel}: {value}");
                    float error = Math.Abs(value - expected[i][channel]);
                    largest = Math.Max(largest, error);
                    sum += error;
                }
            }
            Assert.That(largest, Is.LessThanOrEqualTo(maximum), label + ", maximum absolute error");
            Assert.That(sum / (actual.Length * 4), Is.LessThanOrEqualTo(mean), label + ", mean absolute error");
        }

        sealed class CopyData
        {
            internal TextureHandle source;
            internal TextureHandle destination;
        }
    }
}
