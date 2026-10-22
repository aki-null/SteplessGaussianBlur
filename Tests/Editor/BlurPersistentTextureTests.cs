using System;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.Experimental.Rendering;
using UnityEngine.Rendering;
using UnityEngine.Rendering.RenderGraphModule;

namespace SteplessGaussianBlur.Tests
{
    public sealed class BlurPersistentTextureTests
    {
        [TestCase(GraphicsFormat.R8_UNorm)]
        [TestCase(GraphicsFormat.B10G11R11_UFloatPack32)]
        public void StorableSourcesKeepTheirFormat(GraphicsFormat source)
        {
            Assume.That(StorageFormat.CanStore(source), $"{source} cannot be rendered and filtered here.");
            Assert.That(StorageFormat.For(source), Is.EqualTo(source));
        }

        [TestCase(GraphicsFormat.RGBA_BC7_UNorm, GraphicsFormat.R8G8B8A8_UNorm)]
        [TestCase(GraphicsFormat.RGBA_BC7_SRGB, GraphicsFormat.R8G8B8A8_SRGB)]
        [TestCase(GraphicsFormat.R_BC4_UNorm, GraphicsFormat.R8_UNorm)]
        [TestCase(GraphicsFormat.RG_BC5_UNorm, GraphicsFormat.R8G8_UNorm)]
        [TestCase(GraphicsFormat.RGB_BC6H_UFloat, GraphicsFormat.B10G11R11_UFloatPack32)]
        [TestCase(GraphicsFormat.RGB_BC6H_SFloat, GraphicsFormat.R16G16B16A16_SFloat)]
        public void CompressedSourcesMapToTheirPrecisionAndChannels(GraphicsFormat source, GraphicsFormat expected)
        {
            Assume.That(StorageFormat.CanStore(expected), $"{expected} cannot be rendered and filtered here.");
            Assert.That(StorageFormat.For(source), Is.EqualTo(expected));
        }

        // Low-bit packed formats widen: every pass rounds to the storage format.
        [TestCase(TextureFormat.RGB565, GraphicsFormat.R8G8B8A8_UNorm)]
        [TestCase(TextureFormat.RGBA4444, GraphicsFormat.R8G8B8A8_UNorm)]
        public void PackedSourcesWiden(TextureFormat textureFormat, GraphicsFormat expected)
        {
            GraphicsFormat source = GraphicsFormatUtility.GetGraphicsFormat(textureFormat, false);
            Assert.That(StorageFormat.For(source), Is.EqualTo(expected));
        }

        [Test]
        public void TenBitSourcesWidenToSixteenBits()
        {
            Assume.That(StorageFormat.CanStore(GraphicsFormat.R16G16B16A16_UNorm));
            Assert.That(StorageFormat.For(GraphicsFormat.A2B10G10R10_UNormPack32),
                Is.EqualTo(GraphicsFormat.R16G16B16A16_UNorm));
        }

        [Test]
        public void AlphaOnlySourcesKeepTheirAlpha()
        {
            // One channel sampled as alpha: kept where renderable, never R8.
            GraphicsFormat alpha8 = GraphicsFormatUtility.GetGraphicsFormat(TextureFormat.Alpha8, false);
            GraphicsFormat expected = StorageFormat.CanStore(alpha8) ? alpha8 : GraphicsFormat.R8G8B8A8_UNorm;
            Assert.That(StorageFormat.For(alpha8), Is.EqualTo(expected));
        }

        [TestCase(GraphicsFormat.R8_UNorm, GraphicsFormat.R16G16B16A16_SFloat, GraphicsFormat.R8_UNorm)]
        [TestCase(GraphicsFormat.R8G8B8A8_UNorm, GraphicsFormat.R32G32B32A32_SFloat, GraphicsFormat.R8G8B8A8_UNorm)]
        [TestCase(GraphicsFormat.R16G16B16A16_UNorm, GraphicsFormat.R8G8B8A8_UNorm, GraphicsFormat.R8G8B8A8_UNorm)]
        // A float32 source's partial sums can exceed FP16's range.
        [TestCase(GraphicsFormat.R32G32B32A32_SFloat, GraphicsFormat.R16G16B16A16_SFloat,
            GraphicsFormat.R32G32B32A32_SFloat)]
        // HDR is blurred before the destination clips it, and packed formats
        // would round every pass.
        [TestCase(GraphicsFormat.R16G16B16A16_SFloat, GraphicsFormat.R8_UNorm, GraphicsFormat.R16G16B16A16_SFloat)]
        [TestCase(GraphicsFormat.R8G8B8A8_UNorm, GraphicsFormat.R5G6B5_UNormPack16, GraphicsFormat.R8G8B8A8_UNorm)]
        // An unsigned destination would clip negative values.
        [TestCase(GraphicsFormat.R16G16B16A16_SFloat, GraphicsFormat.B10G11R11_UFloatPack32,
            GraphicsFormat.R16G16B16A16_SFloat)]
        public void IntermediatesHoldNoMoreThanSourceOrDestination(GraphicsFormat source, GraphicsFormat destination,
            GraphicsFormat expected)
        {
            Assert.That(StorageFormat.Intermediate(source, destination), Is.EqualTo(expected));
        }

        [Test]
        public void BlurAcceptsCompressedSources()
        {
            var compressed = new Texture2D(16, 12, TextureFormat.RGBA32, false);
            compressed.Compress(false);
            try
            {
                Assume.That(GraphicsFormatUtility.IsCompressedFormat(compressed.graphicsFormat));
                var target = new BlurTarget(fullSize: true);
                try
                {
                    // Blur's path: a sample-only texture RenderGraph cannot describe.
                    BlurSourceTests.WithGraph((blur, graph) =>
                        blur.RecordTo(graph, BlurSource.FromTexture(compressed), target, 3));
                    Assert.That(target.Texture.graphicsFormat, Is.EqualTo(StorageFormat.For(compressed.graphicsFormat)));
                }
                finally
                {
                    target.Dispose();
                }
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(compressed);
            }
        }

        [Test]
        public void BlurTargetsSurviveSelfBlurAndDestroyedTextures()
        {
            var source = new RenderTexture(64, 48, 0, GraphicsFormat.R16G16B16A16_SFloat);
            var target = new BlurTarget();
            try
            {
                BlurSourceTests.WithGraph((blur, graph) =>
                {
                    TextureHandle written = blur.RecordTo(graph, BlurSourceTests.Import(graph, source), target, 30);
                    RenderTexture texture = target.Texture;
                    Assert.That((texture.width, texture.height), Is.EqualTo((16, 12)));
                    // Rejected before sizing, which would release the source.
                    Assert.Throws<ArgumentException>(() => blur.RecordTo(graph, written, target, 3));
                    Assert.That((texture.width, texture.height), Is.EqualTo((16, 12)));
                });
                UnityEngine.Object.DestroyImmediate(target.Texture);
                BlurSourceTests.WithGraph((blur, graph) =>
                {
                    blur.RecordTo(graph, BlurSourceTests.Import(graph, source), target, 30);
                    Assert.That((target.Texture.width, target.Texture.height), Is.EqualTo((16, 12)),
                        "A recreated texture is sized again.");
                });
            }
            finally
            {
                target.Dispose();
                UnityEngine.Object.DestroyImmediate(source);
            }
        }

        [TestCase(0.8f)]
        [TestCase(1.6f)]
        [TestCase(1.65f)]
        [TestCase(3f)]
        [TestCase(16.125f)]
        [TestCase(24f)]
        [TestCase(70f)]
        public void DestinationRoutingUsesTheSameFilterPasses(float sigma)
        {
            BlurSourceTests.WithGraph((blur, graph) =>
            {
                var descriptor = new TextureDesc(33, 25) { colorFormat = GraphicsFormat.R32G32B32A32_SFloat };
                TextureHandle source = graph.CreateTexture(descriptor);
                TextureHandle destination = graph.CreateTexture(descriptor);
                var passes = new RecordedPasses(blur);
                blur.Record(graph, source, sigma);
                int normalPasses = passes.Count;
                Assert.That(blur.RecordTo(graph, source, destination, sigma), Is.EqualTo(destination));
                Assert.That(passes.Count - normalPasses, Is.EqualTo(normalPasses),
                    "Keeping the result must not add another filter pass.");
                for (int pass = 0; pass < normalPasses; pass++)
                    Assert.That(passes.Taps(normalPasses + pass), Is.EqualTo(passes.Taps(pass)),
                        $"Pass {pass} must upload the same taps.");
            });
        }

        [TestCase(33, 25, 0f)]
        [TestCase(1, 1, 3f)]
        public void IdentityOperationStillWritesTheDestination(int width, int height, float sigma)
        {
            BlurSourceTests.WithGraph((blur, graph) =>
            {
                var descriptor = new TextureDesc(width, height) { colorFormat = GraphicsFormat.R32G32B32A32_SFloat };
                TextureHandle source = graph.CreateTexture(descriptor);
                TextureHandle destination = graph.CreateTexture(descriptor);
                var passes = new RecordedPasses(blur);
                Assert.That(blur.RecordTo(graph, source, destination, sigma), Is.EqualTo(destination));
                Assert.That(passes.Count, Is.EqualTo(1));
                Assert.Throws<ArgumentException>(() => blur.RecordTo(graph, source, source, sigma));
            });
        }

        [TestCase("MSAA")]
        [TestCase("Dynamic")]
        [TestCase("Integer")]
        [TestCase("MemorylessColor")]
        public void InvalidDestinationsAreRejected(string kind)
        {
            var source = new TextureDesc(33, 25) { colorFormat = GraphicsFormat.R32G32B32A32_SFloat };
            TextureDesc destination = source;
            switch (kind)
            {
                case "MSAA": destination.msaaSamples = MSAASamples.MSAA4x; break;
                case "Dynamic": destination.useDynamicScale = true; break;
                case "Integer": destination.colorFormat = GraphicsFormat.R32_UInt; break;
                case "MemorylessColor": destination.memoryless = RenderTextureMemoryless.Color; break;
            }
            Assert.Throws<ArgumentException>(() => GaussianBlur.ValidateDestination(destination, source,
                OutputSize.Create(source.width, source.height, 1)));
        }

        [Test]
        public void MemorylessDepthDoesNotPreventStoringColor()
        {
            var source = new TextureDesc(33, 25) { colorFormat = GraphicsFormat.R32G32B32A32_SFloat };
            TextureDesc destination = source;
            destination.memoryless = RenderTextureMemoryless.Depth;
            Assert.DoesNotThrow(() => GaussianBlur.ValidateDestination(destination, source,
                OutputSize.Create(source.width, source.height, 1)));
        }
    }
}
