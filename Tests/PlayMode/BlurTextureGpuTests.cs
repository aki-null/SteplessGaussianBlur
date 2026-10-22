using System;
using System.Collections;
using System.Collections.Generic;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.Experimental.Rendering;
using UnityEngine.Rendering;
using UnityEngine.Rendering.RenderGraphModule;
using UnityEngine.TestTools;
using System.Text.RegularExpressions;

namespace SteplessGaussianBlur.Tests
{
    public sealed partial class BlurGpuTests
    {
        [UnityTest]
        public IEnumerator PersistentDestinationMatchesCpuWithoutExportCopy()
        {
            foreach (float sigma in new[] { 0.8f, 1.6f, 1.65f, 3f, 16.125f, 24f })
                yield return RunCase(new BlurCase(33, 25, sigma) { WriteToDestination = true });
            yield return RunCase(new BlurCase(1, 1, 3) { WriteToDestination = true });
        }

        [UnityTest]
        public IEnumerator SameGraphConsumerReadsNewPersistentOutput()
        {
            const int width = 33;
            const int height = 25;
            const float sigma = 1.6f;
            Color[] pixels = Pattern(width, height);
            Color[] expected = BlurGpuReference.Blur(pixels, width, height, sigma);
            var upload = new Texture2D(width, height, FloatTexture, false, true);
            textures.Add(upload);
            upload.SetPixels(pixels);
            upload.Apply(false, true);
            RTHandle input = RTHandles.Alloc((Texture)upload);
            // Write a caller's texture, then consume the returned handle in the same graph.
            RTHandle persistent = RTHandles.Alloc(CreateTarget(width, height));
            RTHandle observed = RTHandles.Alloc(CreateTarget(width, height));
            handles.Add(input);
            handles.Add(persistent);
            handles.Add(observed);
            bool submitted = false;
            pendingRender = (context, cameras) =>
            {
                using (var commands = new CommandBuffer())
                {
                    graph.BeginRecording(new RenderGraphParameters
                    {
                        commandBuffer = commands,
                        scriptableRenderContext = context,
                        currentFrameIndex = Time.frameCount
                    });
                    TextureHandle cameraColor = graph.CreateTexture(new TextureDesc(width, height)
                    {
                        colorFormat = FloatFormat
                    });
                    using (var builder = graph.AddUnsafePass<CopyData>("Base UI producer", out var data))
                    {
                        data.source = graph.ImportTexture(input);
                        data.destination = cameraColor;
                        builder.UseTexture(data.source, AccessFlags.Read);
                        builder.UseTexture(cameraColor, AccessFlags.Write);
                        builder.SetRenderFunc((CopyData pass, UnsafeGraphContext render) =>
                            render.cmd.CopyTexture((Texture)pass.source, (Texture)pass.destination));
                    }
                    TextureHandle blurred = blur.RecordTo(graph, cameraColor, graph.ImportTexture(persistent), sigma);
                    // The zero-strength draw samples the returned handle in the
                    // same graph, explicitly declaring the producer dependency.
                    blur.RecordTo(graph, blurred, graph.ImportTexture(observed), 0);
                    graph.EndRecordingAndExecute();
                    context.ExecuteCommandBuffer(commands);
                    context.Submit();
                    graph.EndFrame();
                }
                submitted = true;
            };
            float deadline = Time.realtimeSinceStartup + TimeoutSeconds;
            while (!submitted && Time.realtimeSinceStartup < deadline)
                yield return null;
            Assert.That(submitted, Is.True);
            var readback = AsyncGPUReadback.Request(observed.rt, 0);
            while (!readback.done && Time.realtimeSinceStartup < deadline)
                yield return null;
            Assert.That(readback.done && !readback.hasError, Is.True);
            AssertPixels(ReadPixels(readback), expected, MaximumError, FloatMeanError, "Same-graph UI consumer");
        }

        [UnityTest]
        public IEnumerator BlurSupportsTextureSourcesAndKeepsResults()
        {
            // Sizes above 64: a draw clipped to 64x64 must fail.
            foreach ((string kind, int width, int height) in new[]
            {
                ("Texture2D", 150, 110), ("RenderTextureWithDepth", 150, 110), ("RTHandle", 150, 110)
            })
            {
                Color[] pixels = Pattern(width, height);
                var upload = new Texture2D(width, height, FloatTexture, false, true);
                textures.Add(upload);
                upload.SetPixels(pixels);
                upload.Apply(false, true);
                Assert.That(upload.isReadable, Is.False);
                Texture source = upload;
                if (kind == "RenderTextureWithDepth")
                {
                    RenderTexture captured = CreateTarget(width, height, 24, FloatFormat);
                    Graphics.CopyTexture(upload, captured);
                    source = captured;
                }
                yield return RunTextureBlur(source, pixels, kind == "RTHandle");
                blur = new GaussianBlur(shader);
            }
        }

        [UnityTest]
        public IEnumerator CompressedTextureCanBlurIntoRenderableStorage()
        {
            const int width = 16;
            const int height = 12;
            var upload = new Texture2D(width, height, TextureFormat.RGBA32, false, true);
            textures.Add(upload);
            upload.SetPixels(Pattern(width, height));
            upload.Apply(false, false);
            upload.Compress(false);
            upload.Apply(false, true);
            // Compress() picks the active build target's format.
            if (!GraphicsFormatUtility.IsCompressedFormat(upload.graphicsFormat))
                Assert.Ignore($"Compression produced {upload.format}.");
            RenderTexture decoded = CreateTarget(width, height);
            // Reference-only conversion: compare against the decoded compressed
            // pixels, rather than counting compression loss as blur error.
            Graphics.Blit(upload, decoded);
            var readback = AsyncGPUReadback.Request(decoded, 0);
            float deadline = Time.realtimeSinceStartup + TimeoutSeconds;
            while (!readback.done && Time.realtimeSinceStartup < deadline)
                yield return null;
            Assert.That(readback.done && !readback.hasError, Is.True);
            yield return RunTextureBlur(upload, ReadPixels(readback), false);
        }

        // Several blurs in one frame all run, in call order; a later blur into
        // the same target replaces an earlier one.
        [UnityTest]
        public IEnumerator SeveralBlursRunInOneFrame()
        {
            Color[] pixels = Pattern(33, 25);
            var source = new Texture2D(33, 25, FloatTexture, false, true);
            textures.Add(source);
            source.SetPixels(pixels);
            source.Apply(false, true);
            float[] sigmas = { 1, 3, 12, 30 };
            var targets = new BlurTarget[sigmas.Length];
            try
            {
                for (int i = 0; i < targets.Length; i++)
                {
                    targets[i] = new BlurTarget(fullSize: true, format: FloatFormat);
                    blur.Blur(source, targets[i], 50);
                }
                for (int i = 0; i < targets.Length; i++)
                    blur.Blur(source, targets[i], sigmas[i]);
                yield return WaitForBlurs();
                for (int i = 0; i < targets.Length; i++)
                {
                    Color[] expected = BlurGpuReference.Blur(pixels, 33, 25, sigmas[i]);
                    string label = $"sigma {sigmas[i]}";
                    yield return ReadBack(targets[i].Texture, actual =>
                        AssertPixels(actual, expected, MaximumError, FloatMeanError, label));
                }
            }
            finally
            {
                foreach (BlurTarget target in targets)
                    target?.Dispose();
            }
        }

        // Replacement and the read/write rule hold across instances, and each
        // instance's blur runs with its own materials.
        [UnityTest]
        public IEnumerator PendingRulesHoldAcrossInstances()
        {
            Color[] pixels = Pattern(33, 25);
            var source = new Texture2D(33, 25, FloatTexture, false, true);
            textures.Add(source);
            source.SetPixels(pixels);
            source.Apply(false, true);
            var other = new GaussianBlur(shader);
            var first = new BlurTarget(fullSize: true, format: FloatFormat);
            var second = new BlurTarget(fullSize: true, format: FloatFormat);
            try
            {
                blur.Blur(source, first, 30);
                other.Blur(source, first, 3);
                other.Blur(source, second, 12);
                Assert.Throws<InvalidOperationException>(() => blur.Blur(first.Texture, second, 3));
                Assert.That(first.HasPendingBlur && second.HasPendingBlur, Is.True);
                yield return WaitForBlurs();
                Assert.That(first.HasPendingBlur || second.HasPendingBlur, Is.False);
                Color[] firstExpected = BlurGpuReference.Blur(pixels, 33, 25, 3);
                Color[] secondExpected = BlurGpuReference.Blur(pixels, 33, 25, 12);
                yield return ReadBack(first.Texture, actual =>
                    AssertPixels(actual, firstExpected, MaximumError, FloatMeanError, "first"));
                yield return ReadBack(second.Texture, actual =>
                    AssertPixels(actual, secondExpected, MaximumError, FloatMeanError, "second"));
            }
            finally
            {
                other.Dispose();
                first.Dispose();
                second.Dispose();
            }
        }

        // A target's blur can be read in a later frame, but not in the frame
        // that writes it.
        [UnityTest]
        public IEnumerator TargetsAreReadInALaterFrame()
        {
            Color[] pixels = Pattern(64, 48);
            var source = new Texture2D(64, 48, FloatTexture, false, true);
            textures.Add(source);
            source.SetPixels(pixels);
            source.Apply(false, true);
            var first = new BlurTarget(fullSize: true, format: FloatFormat);
            var second = new BlurTarget(fullSize: true, format: FloatFormat);
            try
            {
                blur.Blur(source, first, 3);
                Assert.Throws<InvalidOperationException>(() => blur.Blur(first.Texture, second, 3));
                yield return WaitForBlurs();
                blur.Blur(first.Texture, second, 3);
                Assert.Throws<InvalidOperationException>(() => blur.Blur(source, first, 30));
                yield return WaitForBlurs();
                // Reading the source again instead would give one blur, not two.
                Color[] expected = BlurGpuReference.Blur(BlurGpuReference.Blur(pixels, 64, 48, 3), 64, 48, 3);
                yield return ReadBack(second.Texture, actual =>
                    AssertPixels(actual, expected, 2 * MaximumError, 2 * FloatMeanError, "The second blur reads the first's result."));
            }
            finally
            {
                first.Dispose();
                second.Dispose();
            }
        }

        // A source released, resized or destroyed before the camera records
        // drops its blur, which leaves the target unsized.
        [UnityTest]
        public IEnumerator ChangedSourcesDropTheirBlur()
        {
            foreach (string change in new[] { "release", "resize", "destroy" })
            {
                var source = new RenderTexture(8, 6, 0, GraphicsFormat.R16G16B16A16_SFloat);
                source.Create();
                var target = new BlurTarget();
                try
                {
                    blur.Blur(source, target, 3);
                    if (change == "release")
                        source.Release();
                    else if (change == "resize")
                    {
                        source.Release();
                        source.width = 10;
                        source.Create();
                    }
                    else
                        UnityEngine.Object.DestroyImmediate(source);
                    yield return WaitForBlurs();
                    Assert.That(target.HasPendingBlur, Is.False, change);
                    Assert.That((target.Texture.width, target.Texture.height), Is.EqualTo((1, 1)),
                        change + ": a dropped blur must not size the target.");
                }
                finally
                {
                    target.Dispose();
                    if (source != null)
                        UnityEngine.Object.DestroyImmediate(source);
                }
            }
        }

        // Only Game and Scene view cameras record pending blurs: a preview
        // camera rendering first leaves them to the next eligible camera.
        [UnityTest]
        public IEnumerator PreviewCamerasLeaveBlursPending()
        {
            var source = new Texture2D(8, 6, FloatTexture, false, true);
            textures.Add(source);
            source.Apply(false, true);
            var target = new BlurTarget(fullSize: true);
            var previewObject = new GameObject("Stepless Gaussian Preview Camera");
            Camera preview = previewObject.AddComponent<Camera>();
            bool previewRendered = false;
            Action<ScriptableRenderContext, Camera> seen = (_, rendered) => previewRendered |= rendered == preview;
            RenderPipelineManager.beginCameraRendering += seen;
            try
            {
                preview.cameraType = CameraType.Preview;
                preview.targetTexture = cameraTarget;
                preview.cullingMask = 0;
                blur.Blur(source, target, 3);
                preview.Render();
                Assert.That(previewRendered, Is.True, "URP must render the preview camera for this check.");
                Assert.That(target.HasPendingBlur, Is.True, "A preview camera must not record the blur.");
                yield return WaitForBlurs();
                Assert.That((target.Texture.width, target.Texture.height), Is.EqualTo((8, 6)));
            }
            finally
            {
                RenderPipelineManager.beginCameraRendering -= seen;
                UnityEngine.Object.DestroyImmediate(previewObject);
                target.Dispose();
            }
        }

        // Disposing the utility drops its pending blurs and keeps the target.
        [UnityTest]
        public IEnumerator DisposingDropsPendingBlurs()
        {
            var source = new Texture2D(8, 6, FloatTexture, false, true);
            textures.Add(source);
            source.Apply(false, true);
            var target = new BlurTarget();
            var disposed = new BlurTarget();
            try
            {
                blur.Blur(source, target, 3);
                blur.Blur(source, disposed, 3);
                disposed.Dispose();
                Assert.That(disposed.HasPendingBlur, Is.False);
                Assert.That(target.HasPendingBlur, Is.True);
                blur.Dispose();
                Assert.That(target.HasPendingBlur || GaussianBlur.AnyPendingBlur, Is.False);
                for (int frame = 0; frame < 3; frame++)
                    yield return null;
                Assert.That((target.Texture.width, target.Texture.height), Is.EqualTo((1, 1)),
                    "A dropped blur must not size the target.");
            }
            finally
            {
                target.Dispose();
            }
        }

        // A target switched to an sRGB format stores encoded values: a 1-pixel
        // checkerboard blurs to linear 0.5, which sRGB stores as 188.
        [UnityTest]
        public IEnumerator SrgbTargetsEncodeTheirResult([Values(false, true)] bool explicitFormat)
        {
            var pixels = new Color32[64 * 48];
            for (int i = 0; i < pixels.Length; i++)
                pixels[i] = (i % 64 + i / 64) % 2 == 0 ? new Color32(255, 255, 255, 255) : new Color32(0, 0, 0, 255);
            var source = new Texture2D(64, 48, TextureFormat.RGBA32, false, false);
            textures.Add(source);
            source.SetPixels32(pixels);
            source.Apply(false, true);
            Assume.That(QualitySettings.activeColorSpace, Is.EqualTo(ColorSpace.Linear));
            var target = explicitFormat
                ? new BlurTarget(fullSize: true, format: GraphicsFormat.R8G8B8A8_SRGB)
                : new BlurTarget(fullSize: true);
            try
            {
                blur.Blur(source, target, 8);
                yield return WaitForBlurs();
                Assert.That(target.Texture.graphicsFormat, Is.EqualTo(GraphicsFormat.R8G8B8A8_SRGB));
                byte[] raw = null;
                AsyncGPUReadback.Request(target.Texture, 0, request => raw = request.GetData<byte>().ToArray());
                float deadline = Time.realtimeSinceStartup + TimeoutSeconds;
                while (raw == null && Time.realtimeSinceStartup < deadline)
                    yield return null;
                Assert.That(raw, Is.Not.Null);
                Assert.That(raw[(24 * 64 + 32) * 4], Is.InRange(186, 190));
            }
            finally
            {
                target.Dispose();
            }
        }

        [Test]
        public void BlurRejectsInvalidRequests()
        {
            var source = new Texture2D(8, 6, FloatTexture, false, true);
            textures.Add(source);
            RenderTexture array = CreateTarget(8, 6, slices: 2);
            var target = new BlurTarget();
            try
            {
                Assert.Throws<ArgumentOutOfRangeException>(() => blur.Blur(source, target, float.NaN));
                Assert.Throws<ArgumentOutOfRangeException>(() => blur.Blur(source, target, -1));
                Assert.Throws<ArgumentException>(() => blur.Blur(target.Texture, target, 1));
                Assert.Throws<ArgumentException>(() => blur.Blur(array, target, 1));
                Assert.Throws<ArgumentNullException>(() => blur.Blur(source, null, 1));
            }
            finally
            {
                target.Dispose();
            }
            Assert.Throws<ObjectDisposedException>(() => blur.Blur(source, target, 1));
            blur.Dispose();
            Assert.Throws<ObjectDisposedException>(() => blur.Blur(source, new BlurTarget(), 1));
        }

        [UnityTest]
        public IEnumerator ExecutionRejectsSeparatelyImportedAliases()
        {
            RenderTexture texture = CreateTarget(8, 6);
            RTHandle first = RTHandles.Alloc(texture);
            RTHandle second = RTHandles.Alloc(texture);
            handles.Add(first);
            handles.Add(second);
            Exception error = null;
            bool rendered = false;
            pendingRender = (context, cameras) =>
            {
                using (var commands = new CommandBuffer())
                {
                    try
                    {
                        graph.BeginRecording(new RenderGraphParameters
                        {
                            commandBuffer = commands,
                            scriptableRenderContext = context,
                            currentFrameIndex = Time.frameCount
                        });
                        // Opaque imports of one texture pass recording; the draw
                        // detects the alias once RenderGraph resolves both handles.
                        blur.RecordTo(graph, graph.ImportTexture(first), graph.ImportTexture(second), 0);
                        graph.EndRecordingAndExecute();
                    }
                    catch (Exception exception)
                    {
                        error = exception;
                        graph.ResetGraphAndLogException(exception);
                    }
                }
                rendered = true;
            };
            LogAssert.ignoreFailingMessages = true;
            try
            {
                float deadline = Time.realtimeSinceStartup + TimeoutSeconds;
                while (!rendered && Time.realtimeSinceStartup < deadline)
                    yield return null;
            }
            finally
            {
                LogAssert.ignoreFailingMessages = false;
            }
            Assert.That(rendered, Is.True);
            Assert.That(error, Is.Not.Null);
            Exception inner = error;
            while (inner.InnerException != null && !(inner is ArgumentException))
                inner = inner.InnerException;
            Assert.That(inner, Is.InstanceOf<ArgumentException>());
            Assert.That(inner.Message, Does.Contain("distinct"));
        }

        // Blurs, reads back, blurs again, then disposes the utility: the
        // target keeps its result.
        IEnumerator RunTextureBlur(Texture source, Color[] sourcePixels, bool useHandle)
        {
            const float sigma = 3;
            var target = new BlurTarget(fullSize: true, format: FloatFormat);
            RTHandle sourceHandle = RTHandles.Alloc(source);
            handles.Add(sourceHandle);
            Color[] expected = BlurGpuReference.Blur(sourcePixels, source.width, source.height, sigma);
            try
            {
                for (int repeat = 0; repeat < 2; repeat++)
                {
                    if (useHandle)
                        blur.Blur(sourceHandle, target, sigma);
                    else
                        blur.Blur(source, target, sigma);
                    yield return WaitForBlurs();
                    yield return ReadBack(target.Texture, actual =>
                        AssertPixels(actual, expected, MaximumError, FloatMeanError, $"Blur {repeat}"));
                }
                blur.Dispose();
                Assert.That(target.Texture.IsCreated(), Is.True, "Utility disposal must preserve the target.");
                yield return ReadBack(target.Texture, actual =>
                    AssertPixels(actual, expected, MaximumError, FloatMeanError, "Retained after disposal"));
            }
            finally
            {
                target.Dispose();
            }
        }
    }
}
