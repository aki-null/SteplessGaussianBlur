using System;
using System.Collections.Generic;
using NUnit.Framework;

namespace SteplessGaussianBlur.Tests
{
    public sealed class BlurKernelTests
    {
        // Level switches that BlurExecutedPipelineTests and
        // BlurReferenceMomentTests name as literals. The outer Up's blend
        // changes the outer level's budget, so a changed blend or budget
        // must move those literals too.
        [TestCase(1, 3.937810, 4.218166)]
        [TestCase(2, 8.002574, 8.554936)]
        public void PyramidLevelSwitchesMatchTheirTestLiterals(int levels, double fadeStart, double fadeEnd)
        {
            Assert.That(LevelSwitchSigma(SmallKernel.NextLevelFadeStart, levels), Is.EqualTo(fadeStart).Within(2e-6));
            Assert.That(LevelSwitchSigma(SmallKernel.NextLevelFadeEnd, levels), Is.EqualTo(fadeEnd).Within(2e-6));
        }

        // The transition's eighth-grid Gaussian takes over from the pyramid's
        // third level; the pyramid never starts a fourth.
        [Test]
        public void TransitionStartsBeforeAFourthPyramidLevel()
        {
            Assert.That(TransitionKernel.StartSigma, Is.LessThan(LevelSwitchSigma(SmallKernel.NextLevelFadeStart, 3)));
        }

        // The sigma at which the Gaussian `levels` downsamples below the
        // outermost level reaches the variance `coarse`.
        static double LevelSwitchSigma(double coarse, int levels)
        {
            double target = coarse;
            for (int i = 0; i < levels; i++)
                target = SmallKernel.LevelVariance + 4 * target;
            var workspace = new SmallKernel.Workspace();
            double low = 2.4, high = 24;
            for (int i = 0; i < 100; i++)
            {
                double middle = (low + high) * 0.5;
                double up = SmallKernel.OuterUpsample(workspace, SmallKernel.OuterUpsampleBilinear(middle));
                if (SmallKernel.OuterLevelVariance(BlurMath.RequestedVariance(middle), up) < target)
                    low = middle;
                else
                    high = middle;
            }
            return (low + high) * 0.5;
        }

        // RecordSmallPass uploads each Gaussian tap's even and odd phase in one
        // float4, with offsets snapped to the sampler grid.
        [TestCase(0.8f)] // One 2D pass.
        [TestCase(1.3f)] // H/V passes.
        public void GaussianPassesUploadBothPhasesOfEachTap(float sigma)
        {
            var workspace = new SmallKernel.Workspace();
            int count = SmallKernel.GaussianRows(workspace, BlurMath.RequestedVariance(sigma),
                SmallKernel.Crossfade.IntoDownUp);
            BlurSourceTests.WithGraph((blur, graph) =>
            {
                var passes = new RecordedPasses(blur);
                blur.Record(graph, graph.CreateTexture(Descriptor(33, 25)), sigma);
                UnityEngine.Vector4[] uploaded = passes.Taps(0);
                for (int k = 0; k < SmallKernel.MaxFetches; k++)
                {
                    UnityEngine.Vector4 even = workspace.taps[k];
                    // The odd row mirrors the even row; it is uploaded in
                    // mirrored order, so tap k carries the same weight.
                    UnityEngine.Vector4 odd = k < count ? workspace.taps[SmallKernel.MaxFetches + count - 1 - k] : default;
                    Assert.That(odd, Is.EqualTo(new UnityEngine.Vector4(-even.x, even.y, even.z, even.w)), $"mirror {k}");
                    Assert.That(uploaded[k], Is.EqualTo(new UnityEngine.Vector4((float)BlurMath.Snap(even.x), even.y,
                        (float)BlurMath.Snap(odd.x), odd.y)), $"tap {k}");
                    Assert.That(uploaded[SmallKernel.MaxFetches + k], Is.EqualTo(UnityEngine.Vector4.zero));
                }
            });
        }

        // Below the parity-pairing point both phases pack the same symmetric
        // list; its weights already come in the same order, so it keeps it.
        [Test]
        public void UnmirroredGaussianRowsKeepTheirOrder()
        {
            const float sigma = 0.5f;
            var workspace = new SmallKernel.Workspace();
            SmallKernel.GaussianRows(workspace, BlurMath.RequestedVariance(sigma), SmallKernel.Crossfade.IntoDownUp);
            BlurSourceTests.WithGraph((blur, graph) =>
            {
                var passes = new RecordedPasses(blur);
                blur.Record(graph, graph.CreateTexture(Descriptor(33, 25)), sigma);
                UnityEngine.Vector4[] uploaded = passes.Taps(0);
                for (int k = 0; k < SmallKernel.MaxFetches; k++)
                {
                    UnityEngine.Vector4 odd = workspace.taps[SmallKernel.MaxFetches + k];
                    Assert.That(new UnityEngine.Vector2(uploaded[k].z, uploaded[k].w),
                        Is.EqualTo(new UnityEngine.Vector2((float)BlurMath.Snap(odd.x), odd.y)), $"tap {k}");
                }
            });
        }

        // The outermost Up uploads the blended pairs of both phases unpacked,
        // after the inner levels have reused the kernel workspace.
        [Test]
        public void OutermostUpUploadsTheBlendedPairs()
        {
            var workspace = new SmallKernel.Workspace();
            SmallKernel.OuterUpsample(workspace, SmallKernel.OuterUpsampleBilinear(4.375));
            BlurSourceTests.WithGraph((blur, graph) =>
            {
                var passes = new RecordedPasses(blur);
                blur.Record(graph, graph.CreateTexture(Descriptor(64, 48)), 4.375f);
                UnityEngine.Vector4[] uploaded = passes.Taps(passes.Count - 1);
                for (int i = 0; i < 4; i++)
                {
                    UnityEngine.Vector4 tap = workspace.outerTaps[i];
                    Assert.That(uploaded[i], Is.EqualTo(new UnityEngine.Vector4((float)BlurMath.Snap(tap.x), tap.y, 0, 0)));
                }
            });
        }

        static UnityEngine.Rendering.RenderGraphModule.TextureDesc Descriptor(int width, int height) =>
            new UnityEngine.Rendering.RenderGraphModule.TextureDesc(width, height)
            {
                colorFormat = UnityEngine.Experimental.Rendering.GraphicsFormat.R16G16B16A16_SFloat
            };

        [TestCase(4.25)]
        [TestCase(4.375)]
        [TestCase(4.5)]
        [TestCase(16.09)]
        public void OuterReconstructionPreservesMassCenterAndBudget(double sigma)
        {
            var workspace = new SmallKernel.Workspace();
            double blend = SmallKernel.OuterUpsampleBilinear(sigma);
            double reported = SmallKernel.OuterUpsample(workspace, blend);
            double variance = 0;
            for (int phase = 0; phase < 2; phase++)
            {
                double mass = 0, first = 0, second = 0;
                for (int k = 0; k < 2; k++)
                {
                    UnityEngine.Vector4 tap = workspace.outerTaps[phase * 2 + k];
                    Assert.That(tap.y, Is.GreaterThanOrEqualTo(0));
                    double fraction = tap.x - Math.Floor(tap.x);
                    mass += tap.y;
                    first += tap.y * tap.x;
                    second += tap.y * (tap.x * tap.x + fraction * (1 - fraction));
                }
                Assert.That(mass, Is.EqualTo(1).Within(1e-7));
                Assert.That(first, Is.EqualTo(phase == 0 ? -0.25 : 0.25).Within(1e-7));
                variance += (second - first * first) / 2;
            }
            // The reported variance is what the packed fetches add.
            Assert.That(reported, Is.EqualTo(variance).Within(2e-7));
            // At the ends: the executed cubic up and plain bilinear (3/16).
            if (blend == 1)
                Assert.That(reported, Is.EqualTo(3.0 / 16).Within(1e-9));
            if (blend == 0)
                Assert.That(reported, Is.EqualTo(SmallKernel.CubicUpVariance).Within(2e-7));

            // Down's variance from its three fetches. Source pixel centers sit
            // at half-integers from the half-res texel's center.
            double down = 0;
            foreach (UnityEngine.Vector4 tap in SmallKernel.BinomialDown)
            {
                double near = Math.Floor(tap.x + 0.5) - 0.5;
                double fraction = tap.x - near;
                down += tap.y * ((1 - fraction) * near * near + fraction * (near + 1) * (near + 1));
            }
            // The level's down, coarse Gaussian and Up, in source pixels²,
            // add up to the request.
            double requested = BlurMath.RequestedVariance(sigma);
            double coarse = SmallKernel.CoarseVariance(SmallKernel.OuterLevelVariance(requested, reported));
            Assert.That(down + 4 * coarse + 4 * variance, Is.EqualTo(requested).Within(2e-6));
        }

        [Test]
        public void OuterReconstructionReusesWorkspaceWithoutAllocating()
        {
            var workspace = new SmallKernel.Workspace();
            SmallKernel.OuterUpsample(workspace, 0.5);
            long before = GC.GetAllocatedBytesForCurrentThread();
            for (int i = 0; i < 1000; i++)
                SmallKernel.OuterUpsample(workspace, SmallKernel.OuterUpsampleBilinear(4.25 + i * 0.00125));
            long allocated = GC.GetAllocatedBytesForCurrentThread() - before;
            Assert.That(allocated, Is.Zero);
        }

        [TestCaseSource(nameof(FittedReferenceCases))]
        public void FittedCurvesMatchTheFitScript(double variance, double expectedPosition,
            double[] expectedDown, double[] expectedUp)
        {
            FittedDownUp downUp = FittedDownUp.At(variance);
            Assert.That(downUp.curvePosition, Is.EqualTo(expectedPosition).Within(1e-9));
            Assert.That(downUp.down0, Is.EqualTo(expectedDown[0]).Within(1e-9));
            Assert.That(downUp.down1, Is.EqualTo(expectedDown[1]).Within(1e-9));
            Assert.That(downUp.down2, Is.EqualTo(expectedDown[2]).Within(1e-9));
            Assert.That(downUp.up0, Is.EqualTo(expectedUp[0]).Within(1e-9));
            Assert.That(downUp.up1, Is.EqualTo(expectedUp[1]).Within(1e-9));
            Assert.That(downUp.up2, Is.EqualTo(expectedUp[2]).Within(1e-9));
            Assert.That(downUp.up3, Is.EqualTo(expectedUp[3]).Within(1e-9));
        }

        // The curve position and fitted weights as Tools~/FilterFit/fit_down_up.py
        // evaluates them (its own implementation of the curves and the inverse
        // table lookup). Update them when the fit is regenerated.
        static IEnumerable<TestCaseData> FittedReferenceCases()
        {
            yield return new TestCaseData(2.4, 2.399356520184155,
                new[] { 0.30008893036161405, 0.16245553481919295, 0.037455534819192976 },
                new[] { 0.0, 0.28758884916071903, 0.674822301678562, 0.037588849160719036 });
            yield return new TestCaseData(2.8, 2.7831667812317678,
                new[] { 0.29780073692518383, 0.16359963153740809, 0.0385996315374081 },
                new[] { 0.005269463502056642, 0.31949235934753917, 0.5952068907987514, 0.0800312863516531 });
            yield return new TestCaseData(4.2, 4.200848251761479,
                new[] { 0.3024795601508221, 0.1612602199245889, 0.03626021992458892 },
                new[] { 0.020736369239930257, 0.3312598090090107, 0.5252712742621888, 0.12273254748887079 });
            yield return new TestCaseData(5.2, 5.2003919332091035,
                new[] { 0.31016552530051245, 0.15741723734974378, 0.03241723734974377 },
                new[] { 0.011863349141433801, 0.3311873757064234, 0.5520352011628518, 0.10491407398929098 });
        }

        [Test]
        public void FittedPairEndsAsOnePyramidLevel()
        {
            // At sigma 2.40 the pair must run exactly the pyramid's down and
            // up, with a level's Gaussian between them.
            FittedDownUp downUp = FittedDownUp.At(FittedDownUp.End);
            var down = new UnityEngine.Vector4[3];
            var up = new UnityEngine.Vector4[4];
            downUp.PackDown(down);
            downUp.PackUp(up);
            for (int i = 0; i < 3; i++)
            {
                Assert.That(down[i].x, Is.EqualTo(SmallKernel.BinomialDown[i].x).Within(1e-6));
                Assert.That(down[i].y, Is.EqualTo(SmallKernel.BinomialDown[i].y).Within(1e-6));
            }
            for (int i = 0; i < 4; i++)
            {
                Assert.That(up[i].x, Is.EqualTo(SmallKernel.CubicUp[i].x).Within(1e-6));
                Assert.That(up[i].y, Is.EqualTo(SmallKernel.CubicUp[i].y).Within(1e-6));
            }
            Assert.That(downUp.halfResGaussianVariance,
                Is.EqualTo(SmallKernel.CoarseVariance(FittedDownUp.End)).Within(1e-6));
        }

        [Test]
        public void VeryWideRadiusRemainsFinite()
        {
            // This must not iterate over eight times the requested radius.
            double sigma = 100_000_000;
            double variance = BlurMath.RequestedVariance(sigma);

            Assert.That(double.IsNaN(variance) || double.IsInfinity(variance), Is.False);
            // The four-sigma taper's continuous-limit variance ratio.
            Assert.That(variance / (sigma * sigma),
                Is.EqualTo(0.99716948283642).Within(1e-9));
        }

        [TestCase(0.0)]
        [TestCase(0.5)]
        [TestCase(3.0)]
        public void PlainKernelPreservesRequestedVarianceInBothPhases(double variance)
        {
            var workspace = new SmallKernel.Workspace();
            int count = SmallKernel.GaussianRows(workspace, variance);
            for (int phase = 0; phase < 2; phase++)
            {
                double mean = 0;
                double moment = 0;
                for (int i = 0; i < count; i++)
                {
                    UnityEngine.Vector4 tap = workspace.taps[phase * SmallKernel.MaxFetches + i];
                    int left = (int)Math.Floor(tap.x);
                    double fraction = tap.x - left;
                    mean += tap.y * tap.x;
                    // A bilinear fetch represents two integer texels. Squaring
                    // its fractional offset would underestimate the variance.
                    moment += tap.y * ((1 - fraction) * left * left +
                        fraction * (left + 1) * (left + 1));
                }
                Assert.That(mean, Is.EqualTo(0).Within(1e-6));
                Assert.That(moment, Is.EqualTo(variance).Within(2e-6));
            }
        }

        // Up to the real Down/Up passes, the full-res row, crossfade
        // included, runs as the unrolled five-fetch passes.
        [Test]
        public void FullResolutionRowsFitFiveFetches()
        {
            var workspace = new SmallKernel.Workspace();
            for (int i = 0; i <= 2000; i++)
            {
                double variance = SmallKernel.DownUpPassesStart * i / 2000 * (1 - 1e-12);
                int count = SmallKernel.GaussianRows(workspace, variance, SmallKernel.Crossfade.IntoDownUp);
                Assert.That(count, Is.LessThanOrEqualTo(5), $"variance {variance}");
            }
        }

        // In high quality the full-res row stays a plain tapered Gaussian
        // until its fade into the first pyramid level.
        [TestCase(0.5)]
        [TestCase(1.6)]
        [TestCase(2.2)]
        public void FirstLevelRowsMatchPlainGaussianBelowFade(double sigma)
        {
            var first = new SmallKernel.Workspace();
            var plain = new SmallKernel.Workspace();
            double variance = BlurMath.RequestedVariance(sigma);
            int count = SmallKernel.GaussianRows(first, variance, SmallKernel.Crossfade.IntoFirstLevel);
            Assert.That(count, Is.EqualTo(SmallKernel.GaussianRows(plain, variance)));
            Assert.That(first.taps, Is.EqualTo(plain.taps));
        }

        // High-quality rows above twelve fetches fall back to the looped passes.
        [Test]
        public void FirstLevelRowsFitMaxFetches()
        {
            var workspace = new SmallKernel.Workspace();
            for (int i = 0; i <= 2000; i++)
            {
                double variance = FittedDownUp.End * i / 2000 * (1 - 1e-12);
                int count = SmallKernel.GaussianRows(workspace, variance, SmallKernel.Crossfade.IntoFirstLevel);
                Assert.That(count, Is.LessThanOrEqualTo(SmallKernel.MaxFetches), $"variance {variance}");
            }
        }

        // At the end of its fade the row is the pyramid level that replaces
        // it from FittedDownUp.End: Down, half-res Gaussian, cubic Up. Packing
        // that chain into one full-res row of grid-snapped pairs leaves an L1
        // step of 0.0014, like the handover to real Down/Up passes in
        // LayoutSwitchesKeepTheResponse.
        [TestCase(48)]
        [TestCase(49)]
        public void FirstLevelRowEndsAtThePyramidLevel(int point)
        {
            var workspace = new SmallKernel.Workspace();
            int count = SmallKernel.GaussianRows(workspace, FittedDownUp.End * (1 - 1e-12),
                SmallKernel.Crossfade.IntoFirstLevel);
            double[] pyramid = ExecutedPipeline.Respond(SigmaAbove(FittedDownUp.End), 96, point);
            double change = 0;
            for (int x = point - 24; x <= point + 24; x++)
            {
                // Output x reads source x + offset; the point sits at offset point - x.
                int start = (x & 1) * SmallKernel.MaxFetches;
                double weight = 0;
                for (int k = 0; k < count; k++)
                {
                    UnityEngine.Vector4 tap = workspace.taps[start + k];
                    double position = x + BlurMath.Snap(tap.x);
                    int first = (int)Math.Floor(position);
                    double fraction = position - first;
                    if (first == point) weight += tap.y * (1 - fraction);
                    if (first + 1 == point) weight += tap.y * fraction;
                }
                change += Math.Abs(weight - pyramid[x]);
            }
            Assert.That(change, Is.LessThan(0.0015));
        }

        // The smallest float sigma whose requested variance is at least `variance`.
        static float SigmaAbove(double variance)
        {
            double low = 0, high = 64;
            for (int i = 0; i < 100; i++)
            {
                double middle = (low + high) * 0.5;
                if (BlurMath.RequestedVariance(middle) < variance)
                    low = middle;
                else
                    high = middle;
            }
            float sigma = (float)high;
            while (BlurMath.RequestedVariance(sigma) < variance)
                sigma = BitConverter.Int32BitsToSingle(BitConverter.SingleToInt32Bits(sigma) + 1);
            return sigma;
        }

        [TestCase(0.5, "None")]
        [TestCase(3.0, "None")]
        [TestCase(2.2, "IntoDownUp")]
        [TestCase(2.5, "IntoDownUp")]
        [TestCase(5.0, "IntoFirstLevel")]
        [TestCase(3.3, "IntoNextLevel")]
        [TestCase(3.7, "IntoNextLevel")]
        public void KernelWeightsAreNormalized(double variance, string crossfade)
        {
            var workspace = new SmallKernel.Workspace();
            SmallKernel.GaussianRows(workspace, variance, Enum.Parse<SmallKernel.Crossfade>(crossfade));

            AssertNormalized(workspace.taps, 0);
            AssertNormalized(workspace.taps, SmallKernel.MaxFetches);
        }

        [Test]
        public void TransitionQuarterRetainsSourcePhaseMassAndPosition()
        {
            var phases = new double[4];
            var firstMoments = new double[4];
            foreach (UnityEngine.Vector4 tap in TransitionKernel.Quarter)
            {
                int left = (int)Math.Floor(tap.x);
                double fraction = tap.x - left;
                Assert.That(tap.y, Is.GreaterThanOrEqualTo(0));
                phases[(left % 4 + 4) % 4] += tap.y * (1 - fraction);
                phases[((left + 1) % 4 + 4) % 4] += tap.y * fraction;
                firstMoments[(left % 4 + 4) % 4] += left * tap.y * (1 - fraction);
                firstMoments[((left + 1) % 4 + 4) % 4] += (left + 1) * tap.y * fraction;
            }
            foreach (double mass in phases)
                Assert.That(mass, Is.EqualTo(0.25).Within(1e-6));
            // A normalized filter can still displace points differently in
            // each decimation phase. Check the conditional centers separately.
            for (int phase = 0; phase < phases.Length; phase++)
                Assert.That(firstMoments[phase] / phases[phase],
                    Is.EqualTo(1.5).Within(1.0 / 128 + 1e-7));
        }

        [TestCase(24f)]
        [TestCase(69.999f)]
        [TestCase(70f)]
        [TestCase(140f)]
        [TestCase(1024f)]
        public void WideGaussianRetainsMassCenterAndVariance(float sigma)
        {
            var kernel = new WideKernel();
            kernel.Prepare(sigma);
            double mass = 0, mean = 0, second = 0;
            for (int i = 0; i < kernel.GaussianCount; i++)
            {
                UnityEngine.Vector4 tap = kernel.Gaussian[i];
                Assert.That(tap.z, Is.GreaterThanOrEqualTo(0));
                mass += tap.z;
                mean += tap.z * (tap.x + tap.y);
                second += tap.z * (tap.x * tap.x * (1 - tap.y) +
                    (tap.x + 1) * (tap.x + 1) * tap.y);
            }
            Assert.That(mass, Is.EqualTo(1).Within(1e-6));
            Assert.That(mean, Is.EqualTo(0).Within(1e-6));
            Assert.That(second / mass, Is.EqualTo(kernel.GaussianVariance).Within(5e-6));
        }

        [Test]
        public void AnimatedWideCoefficientsDoNotAllocateAfterWarmup()
        {
            var transition = new TransitionKernel();
            var wide = new WideKernel();
            for (int i = 0; i < 64; i++)
            {
                transition.Prepare(16.25f + i % 60 / 8f);
                wide.Prepare(24 + i % 60 * 2);
            }
            long before = GC.GetAllocatedBytesForCurrentThread();
            for (int i = 0; i < 64; i++)
            {
                transition.Prepare(16.25f + i % 60 / 8f);
                wide.Prepare(24 + i % 60 * 2);
            }
            long allocated = GC.GetAllocatedBytesForCurrentThread() - before;
            Assert.That(allocated, Is.Zero);
        }

        static void AssertNormalized(UnityEngine.Vector4[] taps, int start)
        {
            double sum = 0;
            for (int i = 0; i < SmallKernel.MaxFetches; i++)
            {
                float weight = taps[start + i].y;
                Assert.That(weight, Is.GreaterThanOrEqualTo(0));
                sum += weight;
            }
            Assert.That(sum, Is.EqualTo(1).Within(1e-5));
        }
    }
}
