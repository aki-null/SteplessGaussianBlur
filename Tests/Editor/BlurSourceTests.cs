using System;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;
using UnityEngine.Experimental.Rendering;
using UnityEngine.Rendering;
using UnityEngine.Rendering.RenderGraphModule;

namespace SteplessGaussianBlur.Tests
{
    /// <summary>Checks the texture contract without allocating or executing GPU resources.</summary>
    public sealed class BlurSourceTests
    {
        internal static Shader LoadShader() => AssetDatabase.LoadAssetAtPath<Shader>(
            "Packages/net.aki-null.stepless-gaussian-blur/Runtime/Shaders/SteplessGaussianBlur.shader");

        [Test]
        public void ZeroStrengthReturnsUnsupportedSourceWithoutSampling()
        {
            WithGraph((blur, graph) =>
            {
                TextureHandle source = graph.CreateTexture(new TextureDesc(Vector2.one)
                {
                    colorFormat = GraphicsFormat.R32_UInt
                });
                Assert.That(blur.Record(graph, source, 0), Is.EqualTo(source));
                Assert.That(blur.Record(graph, source, BlurStrength.AtReferenceResolution(0, 1024)),
                    Is.EqualTo(source));
                Assert.Throws<ArgumentException>(() => blur.Record(graph, source, 1));
                Assert.Throws<ArgumentException>(() =>
                    blur.Record(graph, source, BlurStrength.AtReferenceResolution(1, 1024)));
            });
        }

        // Each entry point rejects a missing or disposed argument with the
        // matching exception, before recording anything.
        [Test]
        public void EntryPointsRejectMissingAndDisposedArguments()
        {
            Assert.Throws<ArgumentNullException>(() => new GaussianBlur(null));
            Assert.Throws<ArgumentException>(() => new BlurTarget(format: GraphicsFormat.R32_UInt));
            var disposedTarget = new BlurTarget();
            disposedTarget.Dispose();
            WithGraph((blur, graph) =>
            {
                TextureHandle source = graph.CreateTexture(new TextureDesc(17, 13)
                {
                    colorFormat = GraphicsFormat.R16G16B16A16_SFloat
                });
                Assert.Throws<ArgumentNullException>(() => blur.RecordTo(graph, source, (BlurTarget)null, 1));
                Assert.Throws<ObjectDisposedException>(() => blur.RecordTo(graph, source, disposedTarget, 1));
                var invalid = Assert.Throws<ArgumentException>(() =>
                    blur.RecordTo(graph, source, default(TextureHandle), 1));
                Assert.That(invalid.Message, Does.Contain("destination texture is invalid"));
                Assert.Throws<ArgumentNullException>(() => blur.Record(null, source, 1));
            });
            var disposed = new GaussianBlur(LoadShader());
            disposed.Dispose();
            WithGraph((_, graph) =>
            {
                TextureHandle source = graph.CreateTexture(new TextureDesc(17, 13)
                {
                    colorFormat = GraphicsFormat.R16G16B16A16_SFloat
                });
                Assert.Throws<ObjectDisposedException>(() => disposed.Record(graph, source, 1));
                Assert.Throws<ObjectDisposedException>(() => disposed.RecordReduced(graph, source, 1));
                Assert.Throws<ObjectDisposedException>(() => disposed.RecordTo(graph, source, disposedTarget, 1));
            });
        }

        [TestCase(GraphicsFormat.R8G8B8A8_UNorm)]
        [TestCase(GraphicsFormat.R8G8B8A8_SRGB)]
        [TestCase(GraphicsFormat.B8G8R8A8_UNorm)]
        [TestCase(GraphicsFormat.B8G8R8A8_SRGB)]
        [TestCase(GraphicsFormat.B10G11R11_UFloatPack32)]
        [TestCase(GraphicsFormat.R16G16B16A16_SFloat)]
        public void CameraColorFormatsAreSupported(GraphicsFormat format)
        {
            bool sample = SystemInfo.IsFormatSupported(format, GraphicsFormatUsage.Sample);
            bool linear = SystemInfo.IsFormatSupported(format, GraphicsFormatUsage.Linear);
            bool render = SystemInfo.IsFormatSupported(format, GraphicsFormatUsage.Render);
            bool packedCameraColor = format == GraphicsFormat.B10G11R11_UFloatPack32 &&
                SystemInfo.IsFormatSupported(format, GraphicsFormatUsage.Blend);
            if (!render || ((!sample || !linear) && !packedCameraColor))
                Assert.Ignore($"{format}: sample={sample}, linear={linear}, render={render}.");
            var source = new TextureDesc(17, 13) { colorFormat = format };
            Assert.DoesNotThrow(() => GaussianBlur.ValidateDescriptor(source, GaussianBlur.TextureRole.Source),
                $"{format}: integer={GraphicsFormatUtility.IsIntegerFormat(format)}, " +
                $"uint={GraphicsFormatUtility.IsUIntFormat(format)}, " +
                $"sint={GraphicsFormatUtility.IsSIntFormat(format)}");
        }

        [TestCase("Scale")]
        [TestCase("Functor")]
        [TestCase("Width")]
        [TestCase("Height")]
        [TestCase("Cube")]
        [TestCase("Slices")]
        [TestCase("BindMS")]
        [TestCase("NoColor")]
        [TestCase("Depth")]
        [TestCase("Integer")]
        public void UnsupportedSourcesAreRejected(string configuration)
        {
            var source = new TextureDesc(17, 13)
            {
                colorFormat = GraphicsFormat.R32G32B32A32_SFloat
            };
            switch (configuration)
            {
                case "Scale": source.sizeMode = TextureSizeMode.Scale; break;
                case "Functor": source.sizeMode = TextureSizeMode.Functor; break;
                case "Width": source.width = 0; break;
                case "Height": source.height = 0; break;
                case "Cube": source.dimension = TextureDimension.Cube; break;
                case "Slices": source.slices = 2; break;
                case "BindMS": source.bindTextureMS = true; break;
                case "NoColor": source.format = GraphicsFormat.None; break;
                case "Depth": source.format = GraphicsFormat.D32_SFloat; break;
                case "Integer": source.format = GraphicsFormat.R32_UInt; break;
            }
            Assert.Throws<ArgumentException>(() => GaussianBlur.ValidateDescriptor(source, GaussianBlur.TextureRole.Source));
        }

        [TestCase("Scale")]
        [TestCase("Functor")]
        [TestCase("Array")]
        [TestCase("MSAA")]
        [TestCase("Dynamic")]
        [TestCase("DynamicExplicit")]
        public void SampleableSourceLayoutsAreSupported(string configuration)
        {
            var source = new TextureDesc(17, 13) { colorFormat = GraphicsFormat.R32G32B32A32_SFloat };
            switch (configuration)
            {
                case "Scale": source.sizeMode = TextureSizeMode.Scale; source.scale = Vector2.one; break;
                case "Functor": source.sizeMode = TextureSizeMode.Functor; source.func = size => size; break;
                case "Array": source.dimension = TextureDimension.Tex2DArray; source.slices = 2; break;
                case "MSAA": source.msaaSamples = MSAASamples.MSAA4x; break;
                case "Dynamic": source.useDynamicScale = true; break;
                case "DynamicExplicit": source.useDynamicScaleExplicit = true; break;
            }
            Assert.DoesNotThrow(() => GaussianBlur.ValidateDescriptor(source, GaussianBlur.TextureRole.Source));
        }

        [TestCase(17, 13, 64, 48)]
        [TestCase(64, 48, 64, 48)]
        public void ActiveRegionFitsTextureView(int width, int height, int textureWidth, int textureHeight)
            => Assert.DoesNotThrow(() => GaussianBlur.ValidateSamplingSize(
                new Vector2Int(width, height), new Vector2Int(textureWidth, textureHeight)));

        [TestCase(65, 13, 64, 48)]
        [TestCase(17, 49, 64, 48)]
        [TestCase(0, 13, 64, 48)]
        [TestCase(17, 13, 0, 48)]
        public void InvalidActiveRegionsAreRejected(int width, int height, int textureWidth, int textureHeight)
            => Assert.Throws<ArgumentException>(() => GaussianBlur.ValidateSamplingSize(
                new Vector2Int(width, height), new Vector2Int(textureWidth, textureHeight)));

        [TestCase(0.8f)]
        [TestCase(3f)]
        [TestCase(24f)]
        public void Unused2DBlurIsCulledWhenXrViewsAreAvailable(float sigma)
        {
            // TextureXR.slices reads back the value maxViews sets.
            int previousViews = TextureXR.slices;
            int generated = 0;
            try
            {
                TextureXR.maxViews = 2;
                WithGraph((blur, graph) =>
                {
                    TextureHandle source = graph.CreateTexture(new TextureDesc(17, 13)
                    {
                        colorFormat = GraphicsFormat.R16G16B16A16_SFloat
                    });
                    using (var builder = graph.AddRasterRenderPass<object>("Generate unused source", out _))
                    {
                        builder.SetRenderAttachment(source, 0, AccessFlags.WriteAll);
                        builder.SetRenderFunc<object>((data, context) => generated++);
                    }
                    // The producer is needed only if the unused blur executes.
                    // Both must be culled, even when the XR system has two views.
                    var passes = new RecordedPasses(blur);
                    blur.Record(graph, source, sigma);
                    Assert.That(passes.Count, Is.GreaterThan(0));
                });
                Assert.That(generated, Is.Zero);
            }
            finally
            {
                TextureXR.maxViews = previousViews;
            }
        }

        [Test]
        public void GraphCreatedExplicitScalingRequiresTextureViewSize()
        {
            WithGraph((blur, graph) =>
            {
                TextureHandle source = graph.CreateTexture(new TextureDesc(17, 13)
                {
                    colorFormat = GraphicsFormat.R16G16B16A16_SFloat,
                    useDynamicScaleExplicit = true
                });
                Assert.Throws<ArgumentException>(() => blur.Record(graph, source, 1));
            });
        }

        [Test]
        public void EyeArraysRequireAnXrContext()
        {
            WithGraph((blur, graph) =>
            {
                // Outside XR, a draw would filter only the first eye, and builds
                // without XR strip the array variants even for one slice.
                Assert.That(TextureXR.slices, Is.EqualTo(1));
                foreach (int slices in new[] { 1, 2 })
                {
                    TextureHandle source = graph.CreateTexture(new TextureDesc(17, 13)
                    {
                        colorFormat = GraphicsFormat.R16G16B16A16_SFloat,
                        dimension = TextureDimension.Tex2DArray,
                        slices = slices
                    });
                    Assert.Throws<ArgumentException>(() => blur.Record(graph, source, 3), $"{slices} slices");
                }
            });
        }

        [Test]
        public void StrengthValidation()
        {
            Assert.Throws<ArgumentOutOfRangeException>(() => BlurStrength.AtReferenceResolution(1, 0));
            // The shorter side, in landscape and portrait.
            Assert.That(BlurStrength.AtReferenceResolution(2, 1024).SourceSigma(4096, 2048), Is.EqualTo(4));
            Assert.That(BlurStrength.AtReferenceResolution(2, 1024).SourceSigma(2048, 4096), Is.EqualTo(4));
            Assert.That(((BlurStrength)3f).SourceSigma(4096, 2048), Is.EqualTo(3));
            WithGraph((blur, graph) =>
            {
                TextureHandle source = graph.CreateTexture(new TextureDesc(17, 13)
                {
                    colorFormat = GraphicsFormat.R16G16B16A16_SFloat
                });
                Assert.Throws<ArgumentOutOfRangeException>(() => blur.Record(graph, source, float.NaN));
                Assert.Throws<ArgumentOutOfRangeException>(() => blur.Record(graph, source, -1));
                Assert.Throws<ArgumentException>(() => blur.Record(graph, default(TextureHandle), 1));
            });
        }

        /// <summary>Imports a caller's texture, as a RenderGraph user does.</summary>
        internal static TextureHandle Import(RenderGraph graph, Texture texture) => graph.ImportTexture(
            texture is RenderTexture renderTexture
                ? RTHandles.Alloc(renderTexture, transferOwnership: false)
                : RTHandles.Alloc(texture));

        internal static void WithGraph(Action<GaussianBlur, RenderGraph> record)
        {
            using (var blur = new GaussianBlur(LoadShader()))
            using (var commands = new CommandBuffer())
            {
                var graph = new RenderGraph("Blur source metadata and culling");
                try
                {
                    graph.BeginRecording(new RenderGraphParameters { commandBuffer = commands });
                    try
                    {
                        record(blur, graph);
                    }
                    finally
                    {
                        // Ends the recording even after a failure, so it
                        // reports instead of a cleanup error.
                        graph.EndRecordingAndExecute();
                        graph.EndFrame();
                    }
                }
                finally
                {
                    graph.Cleanup();
                }
            }
        }
    }
}
