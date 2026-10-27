using System;
using System.Collections.Generic;
using NUnit.Framework;
using UnityEngine;

namespace SteplessGaussianBlur.Tests
{
    /// <summary>
    /// End-to-end checks of every layout as the shaders run it: a point's
    /// response, built by <see cref="ExecutedPipeline"/>, against a Gaussian
    /// target computed here from first principles.
    /// </summary>
    public sealed class BlurExecutedPipelineTests
    {
        const int Phases = 16;

        // Log-spaced strengths across every layout, plus both sides of each switch.
        static IEnumerable<float> Strengths(int count = 900)
        {
            for (int i = 0; i < count; i++)
                yield return (float)(0.3 * Math.Pow(256 / 0.3, i / (count - 1.0)));
            foreach (float s in Switches)
            {
                yield return Below(s);
                yield return s;
            }
        }

        // Layout switches and the ends of crossfades, in sigma.
        static readonly float[] Switches =
        {
            0.740902f, 1.25f, 1.46f, 1.6160f, 1.7237f, 1.80f, 2.40f, 3.937810f, 4.218166f, 4.25f, 4.5f,
            8.002574f, 8.554936f, 11.5f,
            16.1f, 16.1875f, 16.6f, 21, 24, 64, 70, 140
        };

        // The float just below a positive float.
        static float Below(float x) => BitConverter.Int32BitsToSingle(BitConverter.SingleToInt32Bits(x) - 1);

        // Where the point sits for phase p. Below the wide layout, a stride of
        // 17 pixels reaches every phase of a 16-pixel coarse grid. The wide
        // layout's even and odd rows repeat every two working-grid texels, so
        // its phases spread over that.
        static int Position(float sigma, int point, int p)
        {
            if (sigma < TransitionKernel.EndSigma)
                return point + 17 * p;
            int pitch = 16;
            for (double doubling = WideKernel.FirstDoublingSigma; sigma >= doubling; doubling *= 2)
                pitch *= 2;
            return point + p * (2 * pitch / Phases);
        }

        // Worst summed absolute difference from the tapered Gaussian, per range
        // of sigma: the shape each layout reaches with exact width.
        static double ShapeBound(float sigma) =>
            sigma < 0.740902f ? 0.007 : sigma < 2.4f ? 0.052 : sigma < TransitionKernel.StartSigma ? 0.011 : 0.0075;

        [Test]
        public void EveryLayoutKeepsMassCenterAndRequestedWidth()
        {
            foreach (float sigma in Strengths())
            {
                var (length, point) = ExecutedPipeline.Frame(sigma);
                double target = ExecutedPipeline.TargetVariance(sigma);
                double second = 0;
                for (int p = 0; p < 2 * Phases; p++)
                {
                    // Consecutive positions give the average width; the wide
                    // layout's spread positions reach its mirrored odd rows.
                    bool consecutive = p < Phases;
                    int position = consecutive ? point + p : Position(sigma, point, p - Phases);
                    double[] response = ExecutedPipeline.Respond(sigma, length, position);
                    var m = ExecutedPipeline.Measure(response, position);
                    Assert.That(m.mass, Is.EqualTo(1).Within(1e-6), $"sigma {sigma:R}, phase {p}: energy");
                    // The fixed quarter-resolution pass centers each source
                    // phase within 1/128 pixel; below the transition rounding to the
                    // sampler grid leaves a little more.
                    double center = sigma >= TransitionKernel.StartSigma ? 1.0 / 128 + 1e-6 : 0.011;
                    Assert.That(Math.Abs(m.mean), Is.LessThan(center), $"sigma {sigma:R}, phase {p}: center");
                    double phaseWidth = m.variance + m.mean * m.mean;
                    Assert.That(phaseWidth / target, Is.EqualTo(1).Within(0.01), $"sigma {sigma:R}, phase {p}: width");
                    if (consecutive)
                        second += phaseWidth;
                    double minimum = double.MaxValue;
                    foreach (double weight in response)
                        minimum = Math.Min(minimum, weight);
                    Assert.That(minimum, Is.GreaterThanOrEqualTo(-1e-9), $"sigma {sigma:R}: negative weight");
                    Assert.That(ShapeError(response, position, sigma), Is.LessThan(ShapeBound(sigma)),
                        $"sigma {sigma:R}, phase {p}: shape");
                }
                // Width as the second moment about the point, averaged over
                // the point's position on the source grid.
                Assert.That(second / Phases / target, Is.EqualTo(1).Within(2e-6), $"sigma {sigma:R}: width");
            }
        }

        // A reduced output, sampled bilinearly at full resolution, keeps the
        // layout's energy, center and width at every scale up to its
        // largest. At the largest, it is the reduced layout at full size:
        // the viewer's sampling is that layout's own last pass. Reduced
        // budgets are analytic in sigma, so a coarser grid suffices.
        [Test]
        public void OutputSizesKeepMassCenterAndRequestedWidth()
        {
            foreach (float sigma in ReducedStrengths(100))
            {
                int largest = GaussianBlur.ReducedScale(sigma, 1 << 20, 1 << 20);
                var (length, point) = ExecutedPipeline.Frame(sigma);
                double target = ExecutedPipeline.TargetVariance(sigma);
                for (int scale = 1; scale <= largest; scale *= 2)
                {
                    double second = 0;
                    for (int p = 0; p < Phases; p++)
                    {
                        int position = point + p;
                        double[] response = ExecutedPipeline.Respond(sigma, length, position, scale, reduced: true);
                        string label = $"sigma {sigma:R}, scale {scale}, phase {p}";
                        if (scale == largest && largest > 1)
                        {
                            Assert.That(ExecutedPipeline.L1(response,
                                ExecutedPipeline.Respond(sigma, length, position, reduced: true)),
                                Is.LessThan(1e-12), label + ": full-size reduced output");
                            continue;
                        }
                        var m = ExecutedPipeline.Measure(response, position);
                        Assert.That(m.mass, Is.EqualTo(1).Within(1e-6), label + ": energy");
                        double center = sigma >= TransitionKernel.StartSigma ? 1.0 / 128 + 1e-6 : 0.011;
                        Assert.That(Math.Abs(m.mean), Is.LessThan(center), label + ": center");
                        Assert.That(ShapeError(response, position, sigma), Is.LessThan(ShapeBound(sigma)),
                            label + ": shape");
                        second += m.variance + m.mean * m.mean;
                    }
                    if (scale < largest || largest == 1)
                        Assert.That(second / Phases / target, Is.EqualTo(1).Within(2e-6),
                            $"sigma {sigma:R}, scale {scale}: width");
                }
            }
        }

        // Strengths, with both sides of the reduced output's own switches.
        static IEnumerable<float> ReducedStrengths(int count)
        {
            foreach (float s in Strengths(count))
                yield return s;
            foreach (float s in ReducedSwitches)
            {
                yield return Below(s);
                yield return s;
            }
        }

        static readonly float[] ReducedSwitches =
            { (float)SmallKernel.ReducedBilinearStartSigma, (float)SmallKernel.ReducedBilinearEndSigma };

        // A reduced output computes what Record does, except where its Up is
        // already bilinear and Record's is not.
        [TestCase(1.3f)]
        [TestCase(2.6f)]
        [TestCase(2.749f)]
        [TestCase(4.5f)]
        [TestCase(10f)]
        [TestCase(30f)]
        public void ReducedOutputMatchesRecordOutsideItsBilinearRange(float sigma)
        {
            var (length, point) = ExecutedPipeline.Frame(sigma);
            for (int p = 0; p < 4; p++)
                Assert.That(ExecutedPipeline.L1(ExecutedPipeline.Respond(sigma, length, point + p, reduced: true),
                    ExecutedPipeline.Respond(sigma, length, point + p)), Is.LessThan(1e-12), $"phase {p}");
        }

        [TestCase(2.9f)]
        [TestCase(3.5f)]
        [TestCase(4.4f)]
        public void ReducedOutputTurnsBilinearBeforeRecord(float sigma)
        {
            var (length, point) = ExecutedPipeline.Frame(sigma);
            Assert.That(ExecutedPipeline.L1(ExecutedPipeline.Respond(sigma, length, point, reduced: true),
                ExecutedPipeline.Respond(sigma, length, point)), Is.GreaterThan(1e-4));
        }

        // Across the reduced output's own switches, at its largest scale.
        [TestCase(2.75f, 0.0006)]
        [TestCase(3f, 0.0006)]
        public void ReducedSwitchesKeepTheResponse(float sigma, double budget)
        {
            float below = sigma - 1e-5f, above = sigma + 1e-5f;
            var (length, point) = ExecutedPipeline.Frame(above);
            for (int p = 0; p < Phases; p++)
            {
                double change = ExecutedPipeline.L1(ReducedResponse(below, length, point + p),
                    ReducedResponse(above, length, point + p));
                Assert.That(change, Is.LessThan(budget), $"phase {p}");
            }
        }

        // A reduced output's bilinear Up budgets less variance than the cubic
        // Up, so its second level crossfades in at lower sigma than Record's.
        // The third comes after both Ups are bilinear, at the same sigma.
        [TestCase(SmallKernel.NextLevelFadeStart, 1)]
        [TestCase(SmallKernel.NextLevelFadeEnd, 1)]
        [TestCase(SmallKernel.NextLevelFadeStart, 2)]
        [TestCase(SmallKernel.NextLevelFadeEnd, 2)]
        public void ReducedLevelCrossfadesKeepTheResponse(double coarse, int levels)
        {
            double sigma = ReducedLevelSwitchSigma(coarse, levels);
            TestContext.WriteLine($"reduced level switch at sigma {sigma:F6}");
            float below = (float)sigma - 1e-5f, above = (float)sigma + 1e-5f;
            var (length, point) = ExecutedPipeline.Frame(above);
            for (int p = 0; p < Phases; p++)
            {
                double change = ExecutedPipeline.L1(ReducedResponse(below, length, point + p),
                    ReducedResponse(above, length, point + p));
                Assert.That(change, Is.LessThan(0.0006), $"phase {p}");
            }
        }

        // The sigma at which a reduced output's Gaussian `levels` downsamples
        // below the outermost level reaches the variance `coarse`.
        static double ReducedLevelSwitchSigma(double coarse, int levels)
        {
            double target = coarse;
            for (int i = 0; i < levels; i++)
                target = SmallKernel.LevelVariance + 4 * target;
            var workspace = new SmallKernel.Workspace();
            double low = SmallKernel.ReducedBilinearEndSigma, high = 16;
            for (int i = 0; i < 100; i++)
            {
                double middle = (low + high) * 0.5;
                double up = SmallKernel.OuterUpsample(workspace, SmallKernel.OuterUpsampleBilinear(middle, reduced: true));
                if (SmallKernel.OuterLevelVariance(BlurMath.RequestedVariance(middle), up) < target)
                    low = middle;
                else
                    high = middle;
            }
            return (low + high) * 0.5;
        }

        // As ResponseChangesSmoothlyWithSigma, for a reduced output.
        [Test]
        public void ReducedResponseChangesSmoothlyWithSigma()
        {
            const double lo = 2.4, hi = 5, step = 2e-4;
            for (int i = 0; i < 300; i++)
            {
                float sigma = (float)(lo * Math.Pow(hi / lo, i / 300.0));
                float next = (float)(sigma * (1 + step));
                var (length, point) = ExecutedPipeline.Frame(next);
                double ideal = TargetChange(sigma, next);
                for (int p = 0; p < 4; p++)
                {
                    double change = ExecutedPipeline.L1(ReducedResponse(sigma, length, point + p),
                        ReducedResponse(next, length, point + p));
                    Assert.That(change / ideal, Is.LessThan(12), $"sigma {sigma:R}, phase {p}");
                }
            }
        }

        // A reduced output at its largest scale, sampled back to full resolution.
        static double[] ReducedResponse(float sigma, int length, int point) =>
            ExecutedPipeline.Respond(sigma, length, point, GaussianBlur.ReducedScale(sigma, 1 << 20, 1 << 20),
                reduced: true);

        [Test]
        public void WidthGrowsWithSigma()
        {
            double previous = 0;
            float previousSigma = 0;
            for (int i = 0; i < 3000; i++)
            {
                float sigma = (float)(0.3 * Math.Pow(256 / 0.3, i / 2999.0));
                var (length, point) = ExecutedPipeline.Frame(sigma);
                double second = 0;
                for (int p = 0; p < 4; p++)
                {
                    var m = ExecutedPipeline.Measure(ExecutedPipeline.Respond(sigma, length, point + p), point + p);
                    second += m.variance + m.mean * m.mean;
                }
                Assert.That(second, Is.GreaterThan(previous), $"sigma {previousSigma:R} to {sigma:R}");
                previous = second;
                previousSigma = sigma;
            }
        }

        // Largest change of a point's response between neighboring strengths,
        // as a multiple of the change of the target Gaussian itself. Rounding
        // fetch positions to the sampler's 1/256 grid moves a little weight
        // whenever a position steps to the next grid point; these bounds keep
        // those steps from growing.
        [TestCase(0.3, 0.740902, 55)]
        [TestCase(0.740902, 1.616, 45)]
        [TestCase(1.616, 2.4, 25)]
        // The pyramid's 15.1 is at sigma 9.73, where a fetch steps to the
        // next grid point; the sweep reached it once it ended at 11.5.
        [TestCase(2.4, 11.5, 16)]
        [TestCase(9.72, 9.74, 16)]
        [TestCase(11.5, 24, 18)]
        [TestCase(24, 256, 12)]
        public void ResponseChangesSmoothlyWithSigma(double lo, double hi, double bound)
        {
            const double step = 2e-4;
            for (int i = 0; i < 300; i++)
            {
                float sigma = (float)(lo * Math.Pow(hi / lo, i / 300.0));
                float next = (float)(sigma * (1 + step));
                var (length, point) = ExecutedPipeline.Frame(next);
                double ideal = TargetChange(sigma, next);
                for (int p = 0; p < 4; p++)
                {
                    double change = ExecutedPipeline.L1(ExecutedPipeline.Respond(sigma, length, point + p),
                        ExecutedPipeline.Respond(next, length, point + p));
                    Assert.That(change / ideal, Is.LessThan(bound), $"sigma {sigma:R}, phase {p}");
                }
            }
        }

        // At each switch the two layouts compute nearly the same response.
        // The literals are rounded, so the pair straddles the switch by
        // 1e-5 sigma; the ideal Gaussian changes by under 1e-5 across it.
        // Where the layouts are designed to meet, the budget is the size of
        // an ordinary step to the next sampler grid point (up to 5.5e-4).
        // The full-res row's handover to real down/up passes (sigma 1.616),
        // the transition (0.47% at 11.5) and the pitch switches get their
        // measured steps with a little room.
        [TestCase(0.740902f, 0.0006)]
        [TestCase(0.75f, 0.0006)]
        [TestCase(1.46f, 0.0006)]
        [TestCase(1.616003f, 0.0013)]
        [TestCase(1.723715f, 0.0006)]
        [TestCase(2.40f, 0.0006)]
        [TestCase(2.565532f, 0.0006)]
        [TestCase(3.937810f, 0.0006)]
        [TestCase(4.218166f, 0.0006)]
        [TestCase(4.25f, 0.0006)]
        [TestCase(4.5f, 0.0006)]
        [TestCase(8.554936f, 0.0006)]
        [TestCase(11.5f, 0.0055)]
        [TestCase(16.1f, 0.0006)]
        [TestCase(16.6f, 0.0006)]
        [TestCase(21f, 0.0006)]
        [TestCase(24f, 0.0015)]
        [TestCase(64f, 0.0006)]
        [TestCase(70f, 0.0013)]
        [TestCase(140f, 0.0013)]
        public void LayoutSwitchesKeepTheResponse(float sigma, double budget)
        {
            float below = sigma - 1e-5f, above = sigma + 1e-5f;
            var (length, point) = ExecutedPipeline.Frame(above);
            for (int p = 0; p < Phases; p++)
            {
                double change = ExecutedPipeline.L1(ExecutedPipeline.Respond(below, length, point + p),
                    ExecutedPipeline.Respond(above, length, point + p));
                Assert.That(change, Is.LessThan(budget), $"phase {p}");
            }
        }

        [Test]
        public void SmallLayoutFetchesSitOnTheSamplerGrid()
        {
            // The sampler then reads the positions the kernels budgeted.
            var workspace = new SmallKernel.Workspace();
            var taps = new Vector4[4];
            for (int i = 0; i < 2000; i++)
            {
                double sigma = 0.3 * Math.Pow(TransitionKernel.StartSigma / 0.3, i / 1999.0);
                double variance = BlurMath.RequestedVariance(sigma);
                // Each Gaussian stage where a layout uses it.
                SmallKernel.Crossfade crossfade = variance < SmallKernel.DownUpPassesStart ?
                    SmallKernel.Crossfade.IntoDownUp : SmallKernel.Crossfade.IntoNextLevel;
                if (crossfade == SmallKernel.Crossfade.IntoDownUp || variance < SmallKernel.NextLevelFadeEnd)
                {
                    int count = SmallKernel.GaussianRows(workspace, variance, crossfade);
                    for (int k = 0; k < count; k++)
                    {
                        AssertOnGrid(workspace.taps[k].x, sigma);
                        AssertOnGrid(workspace.taps[SmallKernel.MaxFetches + k].x, sigma);
                    }
                }
                if (variance >= FittedDownUp.Start && variance < FittedDownUp.End)
                {
                    FittedDownUp downUp = FittedDownUp.At(variance);
                    downUp.PackDown(taps);
                    for (int k = 0; k < 3; k++)
                        AssertOnGrid(taps[k].x, sigma);
                    downUp.PackUp(taps);
                    for (int k = 0; k < 4; k++)
                        AssertOnGrid(taps[k].x, sigma);
                }
                SmallKernel.OuterUpsample(workspace, SmallKernel.OuterUpsampleBilinear(sigma));
                for (int k = 0; k < 4; k++)
                    AssertOnGrid(workspace.outerTaps[k].x, sigma);
            }
            foreach (Vector4 tap in SmallKernel.BinomialDown)
                AssertOnGrid(tap.x, 0);
            foreach (Vector4 tap in SmallKernel.CubicUp)
                AssertOnGrid(tap.x, 0);
        }

        static void AssertOnGrid(float offset, double sigma)
        {
            double units = offset * 256.0;
            Assert.That(units, Is.EqualTo(Math.Round(units)).Within(1e-9), $"sigma {sigma:R}: offset {offset:R}");
        }

        static double ShapeError(double[] response, int position, double sigma)
        {
            int reach = (int)Math.Ceiling(4 * sigma) + 1;
            double[] target = Target(sigma, reach);
            double sum = 0;
            for (int i = 0; i < response.Length; i++)
            {
                int k = i - position;
                sum += Math.Abs(response[i] - (Math.Abs(k) <= reach ? target[k + reach] : 0));
            }
            return sum;
        }

        // L1 change of the normalized tapered Gaussian between two strengths.
        static double TargetChange(double a, double b)
        {
            int reach = (int)Math.Ceiling(4 * Math.Max(a, b)) + 1;
            double[] x = Target(a, reach), y = Target(b, reach);
            double sum = 0;
            for (int i = 0; i < x.Length; i++)
                sum += Math.Abs(x[i] - y[i]);
            return sum;
        }

        static double[] Target(double sigma, int reach)
        {
            var weights = new double[2 * reach + 1];
            double total = 0;
            for (int k = -reach; k <= reach; k++)
            {
                double t = Math.Clamp((4 - Math.Abs(k) / sigma) * 2, 0, 1);
                weights[k + reach] = Math.Exp(-0.5 * k * k / (sigma * sigma)) * t * t * (3 - 2 * t);
                total += weights[k + reach];
            }
            for (int i = 0; i < weights.Length; i++)
                weights[i] /= total;
            return weights;
        }
    }
}
