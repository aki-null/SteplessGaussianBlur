using System;
using System.Collections.Generic;
using System.Reflection;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;
using UnityEngine.Experimental.Rendering;
using UnityEngine.Rendering.RenderGraphModule;
using AllocatingGCMemory = UnityEngine.TestTools.Constraints.AllocatingGCMemoryConstraint;

namespace SteplessGaussianBlur.Tests
{
    /// <summary>What BlurQuality.High records, and that switching modes keeps no state.</summary>
    public sealed class BlurQualityTests
    {
        // Below sigma 2.40 High runs the full-res row as H/V passes, with no
        // Down or Up; both passes upload the high-quality row.
        [TestCase(1.62f)]
        [TestCase(1.8f)]
        [TestCase(2.3f)]
        public void HighRecordsFullResolutionGaussianBelow240(float sigma)
        {
            List<Vector4[]> draws = Draws(BlurQuality.High, sigma);
            Assert.That(draws.Count, Is.EqualTo(2));
            AssertUploadsHighQualityRow(draws[0], sigma);
            AssertUploadsHighQualityRow(draws[1], sigma);
        }

        // High is a Gaussian at every phase: mass, center and variance from
        // independently computed moments, and its shape within 1% L1 of the
        // tapered Gaussian, where Fast reaches 5.9%.
        [TestCase(1.5f)]
        [TestCase(1.62f)]
        [TestCase(1.8f)]
        [TestCase(2.0f)]
        [TestCase(2.2f)]
        [TestCase(2.3f)]
        [TestCase(2.39f)]
        public void HighMatchesTheGaussian(float sigma)
        {
            var (length, point) = ExecutedPipeline.Frame(sigma);
            double target = ExecutedPipeline.TargetVariance(sigma);
            double[] ideal = TaperedGaussian(sigma, length, point);
            for (int p = 0; p < 4; p++)
            {
                double[] response = ExecutedPipeline.Respond(sigma, length, point + p, quality: BlurQuality.High);
                var m = ExecutedPipeline.Measure(response, point + p);
                Assert.That(m.mass, Is.EqualTo(1).Within(1e-5), $"mass, phase {p}");
                Assert.That(Math.Abs(m.mean), Is.LessThanOrEqualTo(0.01), $"center, phase {p}");
                Assert.That(m.variance / target - 1, Is.InRange(-0.005, 0.005), $"variance, phase {p}");
                Assert.That(ExecutedPipeline.L1(response, Shift(ideal, p)), Is.LessThan(0.01), $"shape, phase {p}");
            }
        }

        // High's own switches: the start of its fade, and the handover to
        // the pyramid, whose step is that of packing the level into one row.
        [TestCase(2.24f, 0.0006)]
        [TestCase(2.40f, 0.0015)]
        public void HighSwitchesKeepTheResponse(float sigma, double budget)
        {
            float below = sigma - 1e-5f, above = sigma + 1e-5f;
            var (length, point) = ExecutedPipeline.Frame(above);
            for (int p = 0; p < 4; p++)
            {
                double change = ExecutedPipeline.L1(
                    ExecutedPipeline.Respond(below, length, point + p, quality: BlurQuality.High),
                    ExecutedPipeline.Respond(above, length, point + p, quality: BlurQuality.High));
                Assert.That(change, Is.LessThan(budget), $"phase {p}");
            }
        }

        static double[] TaperedGaussian(double sigma, int length, int point)
        {
            var row = new double[length];
            int reach = (int)Math.Ceiling(4 * sigma);
            double total = 0;
            for (int k = -reach; k <= reach; k++)
            {
                double t = Math.Clamp((4 - Math.Abs(k) / sigma) * 2, 0, 1);
                double weight = Math.Exp(-0.5 * k * k / (sigma * sigma)) * t * t * (3 - 2 * t);
                row[point + k] = weight;
                total += weight;
            }
            for (int i = 0; i < length; i++)
                row[i] /= total;
            return row;
        }

        static double[] Shift(double[] row, int by)
        {
            var shifted = new double[row.Length];
            Array.Copy(row, 0, shifted, by, row.Length - by);
            return shifted;
        }

        // Rows of nine or ten fetches (pyramid crossfades, High) run an
        // exact ten-fetch pass, eleven or twelve (quarter-res pyramid rows,
        // High) the twelve-fetch pass.
        [TestCase(9, "Horizontal10")]
        [TestCase(10, "Horizontal10")]
        [TestCase(11, "Horizontal12")]
        [TestCase(12, "Horizontal12")]
        public void RowsOf9To12RunExactUnrolledPasses(int count, string pass)
        {
            Assert.That(GaussianBlur.AxisPass(count, false), Is.EqualTo(Enum.Parse<GaussianBlur.ShaderPass>(pass)));
            Assert.That(GaussianBlur.AxisPass(count, true),
                Is.EqualTo(Enum.Parse<GaussianBlur.ShaderPass>(pass.Replace("Horizontal", "Vertical"))));
        }

        // RecordSmallPass asks this for every draw; recording must not allocate.
        [Test]
        public void PassClassificationDoesNotAllocate()
        {
            var passes = (GaussianBlur.ShaderPass[])Enum.GetValues(typeof(GaussianBlur.ShaderPass));
            // The constraint detects allocations here, unlike the thread counter.
            Assert.That(() => GC.KeepAlive(new object[4]), new AllocatingGCMemory());
            Assert.That(() =>
            {
                foreach (GaussianBlur.ShaderPass pass in passes)
                    GaussianBlur.IsGaussianPass(pass);
            }, !new AllocatingGCMemory());
            Assert.That(GaussianBlur.IsGaussianPass(GaussianBlur.ShaderPass.Horizontal12), Is.True);
            Assert.That(GaussianBlur.IsGaussianPass(GaussianBlur.ShaderPass.Vertical10), Is.True);
            Assert.That(GaussianBlur.IsGaussianPass(GaussianBlur.ShaderPass.Down), Is.False);
        }

        [TestCase(5, "Horizontal5")]
        [TestCase(8, "Horizontal8")]
        [TestCase(13, "Horizontal")]
        public void OtherRowsKeepTheirPasses(int count, string pass)
        {
            Assert.That(GaussianBlur.AxisPass(count, false), Is.EqualTo(Enum.Parse<GaussianBlur.ShaderPass>(pass)));
        }

        [TestCase(2.41f)]
        [TestCase(4f)]
        [TestCase(12f)]
        [TestCase(20f)]
        [TestCase(64f)]
        public void HighMatchesFastFrom240(float sigma)
        {
            Assert.That(Draws(BlurQuality.High, sigma), Is.EqualTo(Draws(BlurQuality.Fast, sigma)));
        }

        // One recorder reuses its kernel workspace across modes.
        [Test]
        public void SwitchingQualityDoesNotLeakState()
        {
            var recordings = new List<List<Vector4[]>>();
            BlurSourceTests.WithGraph((blur, graph) =>
            {
                foreach (BlurQuality quality in new[] { BlurQuality.Fast, BlurQuality.High, BlurQuality.Fast })
                {
                    var passes = new RecordedPasses(blur);
                    blur.Quality = quality;
                    blur.Record(graph, graph.CreateTexture(Descriptor(257, 193)), 1.8f);
                    recordings.Add(Taps(passes));
                }
            });
            Assert.That(recordings[2], Is.EqualTo(recordings[0]));
            Assert.That(recordings[1], Is.Not.EqualTo(recordings[0]));
        }

        // Below sigma 2.75 a reduced output is Record's, so High's reduced
        // output is its full-res result.
        [Test]
        public void ReducedHighKeepsFullSizeBelow275()
        {
            BlurSourceTests.WithGraph((blur, graph) =>
            {
                var passes = new RecordedPasses(blur);
                blur.Quality = BlurQuality.High;
                TextureHandle result = blur.RecordReduced(graph, graph.CreateTexture(Descriptor(257, 193)), 2f);
                List<Vector4[]> draws = Taps(passes);
                TextureDesc output = graph.GetTextureDesc(result);
                Assert.That((output.width, output.height), Is.EqualTo((257, 193)));
                Assert.That(draws.Count, Is.EqualTo(2));
                AssertUploadsHighQualityRow(draws[0], 2f);
            });
        }

        [Test]
        public void UndefinedQualityThrows()
        {
            BlurSourceTests.WithGraph((blur, graph) =>
            {
                Assert.That(blur.Quality, Is.EqualTo(BlurQuality.Fast));
                Assert.Throws<ArgumentOutOfRangeException>(() => blur.Quality = (BlurQuality)7);
                Assert.That(blur.Quality, Is.EqualTo(BlurQuality.Fast));
            });
        }

        [Test]
        public void FeatureAppliesQuality()
        {
            var feature = ScriptableObject.CreateInstance<SteplessGaussianBlurFeature>();
            try
            {
                var serialized = new SerializedObject(feature);
                serialized.FindProperty("quality").enumValueIndex = (int)BlurQuality.High;
                serialized.ApplyModifiedPropertiesWithoutUndo();
                feature.Create();
                var blur = (GaussianBlur)typeof(SteplessGaussianBlurFeature)
                    .GetField("blur", BindingFlags.Instance | BindingFlags.NonPublic).GetValue(feature);
                Assert.That(blur.Quality, Is.EqualTo(BlurQuality.High));
            }
            finally
            {
                feature.Dispose();
                UnityEngine.Object.DestroyImmediate(feature);
            }
        }

        [Test]
        public void FeatureWithUndefinedQualityKeepsNoMaterials()
        {
            int before = Resources.FindObjectsOfTypeAll<Material>().Length;
            var feature = ScriptableObject.CreateInstance<SteplessGaussianBlurFeature>();
            try
            {
                var serialized = new SerializedObject(feature);
                serialized.FindProperty("quality").intValue = 7;
                serialized.ApplyModifiedPropertiesWithoutUndo();
                UnityEngine.TestTools.LogAssert.Expect(LogType.Warning,
                    new System.Text.RegularExpressions.Regex("Stepless Gaussian Blur is disabled"));
                feature.Create();
                Assert.That(Resources.FindObjectsOfTypeAll<Material>().Length, Is.EqualTo(before));
            }
            finally
            {
                feature.Dispose();
                UnityEngine.Object.DestroyImmediate(feature);
            }
        }

        static List<Vector4[]> Draws(BlurQuality quality, float sigma)
        {
            var draws = new List<Vector4[]>();
            BlurSourceTests.WithGraph((blur, graph) =>
            {
                var passes = new RecordedPasses(blur);
                blur.Quality = quality;
                blur.Record(graph, graph.CreateTexture(Descriptor(257, 193)), sigma);
                draws = Taps(passes);
            });
            return draws;
        }

        // Read after recording: each block is filled after its draw is reported.
        static List<Vector4[]> Taps(RecordedPasses passes)
        {
            var taps = new List<Vector4[]>();
            for (int pass = 0; pass < passes.Count; pass++)
                taps.Add(passes.Taps(pass));
            return taps;
        }

        // RecordSmallPass uploads each even-phase tap with an odd-phase tap in
        // one float4, offsets snapped; it may reorder the odd phase.
        static void AssertUploadsHighQualityRow(Vector4[] uploaded, float sigma)
        {
            var workspace = new SmallKernel.Workspace();
            int count = SmallKernel.GaussianRows(workspace, BlurMath.RequestedVariance(sigma),
                SmallKernel.Crossfade.IntoFirstLevel);
            var expectedOdd = new List<Vector2>();
            var actualOdd = new List<Vector2>();
            for (int k = 0; k < SmallKernel.MaxFetches; k++)
            {
                Vector4 even = workspace.taps[k];
                Assert.That(new Vector2(uploaded[k].x, uploaded[k].y),
                    Is.EqualTo(new Vector2((float)BlurMath.Snap(even.x), even.y)), $"even {k}");
                Vector4 odd = workspace.taps[SmallKernel.MaxFetches + k];
                expectedOdd.Add(new Vector2((float)BlurMath.Snap(odd.x), odd.y));
                actualOdd.Add(new Vector2(uploaded[k].z, uploaded[k].w));
                Assert.That(uploaded[SmallKernel.MaxFetches + k], Is.EqualTo(Vector4.zero));
            }
            expectedOdd.Sort((a, b) => a.x.CompareTo(b.x));
            actualOdd.Sort((a, b) => a.x.CompareTo(b.x));
            Assert.That(actualOdd, Is.EqualTo(expectedOdd), "odd phase");
            Assert.That(count, Is.GreaterThanOrEqualTo(4), "H/V passes");
        }

        static TextureDesc Descriptor(int width, int height) => new TextureDesc(width, height)
        {
            colorFormat = GraphicsFormat.R16G16B16A16_SFloat,
            filterMode = FilterMode.Bilinear,
            wrapMode = TextureWrapMode.Clamp
        };
    }
}
