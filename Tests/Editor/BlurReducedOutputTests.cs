using System;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.Experimental.Rendering;
using UnityEngine.Rendering;
using UnityEngine.Rendering.RenderGraphModule;

namespace SteplessGaussianBlur.Tests
{
    /// <summary>Sizes and destination rules of the reduced output.</summary>
    public sealed class BlurOutputSizeTests
    {
        // The largest scale grows with sigma; it then halves until it
        // divides both sides.
        [TestCase(2.99f, 1920, 1080, 1)]
        [TestCase(3f, 1920, 1080, 2)]
        [TestCase(3f, 1921, 1080, 1)]
        [TestCase(4.49f, 1920, 1080, 2)]
        [TestCase(11.49f, 1920, 1080, 2)]
        [TestCase(11.5f, 1920, 1080, 4)]
        [TestCase(11.5f, 1170, 2532, 2)]
        [TestCase(24f, 1920, 1080, 4)]
        [TestCase(69.99f, 3840, 2160, 4)]
        [TestCase(70f, 3840, 2160, 8)]
        [TestCase(1e9f, 6, 4, 2)]
        // Capped where a viewer's 1/256 sampler grid stops resolving the phases.
        [TestCase(1e5f, 4096, 4096, 128)]
        public void ScaleFollowsSigmaAndDividesBothSides(float sigma, int width, int height, int scale)
        {
            Assert.That(GaussianBlur.ReducedScale(sigma, width, height), Is.EqualTo(scale));
        }

        [Test]
        public void OutputSizeUsesTheStrengthsSourceSigma()
        {
            var size = new Vector2Int(1920, 1080);
            Assert.That(Layout(size, 30).Size, Is.EqualTo(new Vector2Int(480, 270)));
            // Sigma 3 at a 360-pixel reference is sigma 9 on a 1080-pixel short side.
            Assert.That(Layout(size, BlurStrength.AtReferenceResolution(3, 360)).Size,
                Is.EqualTo(new Vector2Int(960, 540)));
            Assert.That(Layout(size, 0).Size, Is.EqualTo(size));
        }

        [Test]
        public void OutputSizesDivideTheSource()
        {
            OutputSize reduced = Layout(new Vector2Int(1920, 1080), 30);
            Assert.That((reduced.Size, reduced.Scale), Is.EqualTo((new Vector2Int(480, 270), 4)));
            Assert.That(Layout(new Vector2Int(1920, 1080), 2.9f).Scale, Is.EqualTo(1));
            Assert.That(Layout(new Vector2Int(4, 4), 10).Scale, Is.EqualTo(2));
        }

        // Where a reduced output keeps full size, its last Up is the reduced
        // layout's, not Record's: blending to bilinear at sigma 2.9, bilinear
        // at 3.5 on sides no scale divides.
        [Test]
        public void FullSizeReducedOutputsUseTheReducedUp()
        {
            BlurSourceTests.WithGraph((blur, graph) =>
            {
                var passes = new RecordedPasses(blur);
                TextureHandle reduced = blur.RecordReduced(graph, graph.CreateTexture(Destination(64, 48)), 2.9f);
                TextureDesc output = graph.GetTextureDesc(reduced);
                Assert.That((output.width, output.height), Is.EqualTo((64, 48)));
                var workspace = new SmallKernel.Workspace();
                SmallKernel.OuterUpsample(workspace, SmallKernel.OuterUpsampleBilinear(2.9, reduced: true));
                Vector4[] last = passes.Taps(passes.Count - 1);
                for (int k = 0; k < 4; k++)
                {
                    Assert.That(last[k].x, Is.EqualTo((float)BlurMath.Snap(workspace.outerTaps[k].x)), $"tap {k}");
                    Assert.That(last[k].y, Is.EqualTo(workspace.outerTaps[k].y), $"tap {k} weight");
                }

                int recorded = passes.Count;
                blur.RecordReduced(graph, graph.CreateTexture(Destination(65, 49)), 3.5f);
                Assert.That(passes.FetchCount(passes.Count - 1), Is.EqualTo(1), "bilinear Up");
                Assert.That(passes.Count - recorded, Is.GreaterThan(1));
                blur.Record(graph, graph.CreateTexture(Destination(65, 49)), 3.5f);
                Assert.That(passes.FetchCount(passes.Count - 1), Is.EqualTo(2), "Record's cubic Up");
            });
        }

        static OutputSize Layout(Vector2Int size, BlurStrength strength)
            => GaussianBlur.ReducedOutputSize(strength.SourceSigma(size.x, size.y), size.x, size.y);

        static TextureDesc Destination(int width, int height) => new TextureDesc(width, height)
        {
            colorFormat = GraphicsFormat.R16G16B16A16_SFloat,
            filterMode = FilterMode.Bilinear,
            wrapMode = TextureWrapMode.Clamp
        };

        [Test]
        public void DestinationsMustHaveTheirLayoutsSize()
        {
            TextureDesc source = Destination(64, 32);
            OutputSize full = OutputSize.Create(64, 32, 1);
            OutputSize reduced = OutputSize.Create(64, 32, 4);
            Assert.DoesNotThrow(() => GaussianBlur.ValidateDestination(Destination(64, 32), source, full));
            Assert.DoesNotThrow(() => GaussianBlur.ValidateDestination(Destination(16, 8), source, reduced));
            Assert.Throws<ArgumentException>(() => GaussianBlur.ValidateDestination(Destination(16, 8), source, full));
            Assert.Throws<ArgumentException>(() => GaussianBlur.ValidateDestination(Destination(18, 10), source, reduced));
        }

        [Test]
        public void WideBudgetsLeaveTheGaussianPositiveAtEveryScale()
        {
            var kernel = new WideKernel();
            foreach (float sigma in new[] { 24f, 30f, 69.99f, 70f, 140f, 1000f })
            {
                for (int scale = 1; scale <= WideKernel.PitchFor(sigma) / 4; scale *= 2)
                {
                    kernel.Prepare(sigma, scale);
                    Assert.That(kernel.GaussianVariance, Is.GreaterThan(0), $"sigma {sigma}, scale {scale}");
                }
            }
            Assert.Throws<ArgumentOutOfRangeException>(() => kernel.Prepare(24, 8));
            Assert.Throws<ArgumentOutOfRangeException>(() => kernel.Prepare(24, 3));
        }
    }
}
