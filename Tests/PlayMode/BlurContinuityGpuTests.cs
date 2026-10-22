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
    // Continuity across layout switches and stability of point positions.
    public sealed partial class BlurGpuTests
    {
        [UnityTest]
        public IEnumerator LayoutBoundariesRemainContinuous()
        {
            // Fixed source-space variances from the reference path, including
            // boundaries reached after one pyramid downsample. Literal values
            // keep a changed runtime threshold from moving the test with it.
            double[] boundaries =
            {
                0.5459533771635819,    // Rows pair texels by output parity (sigma 0.740902).
                0.5591010558458668,    // Four-fetch rows switch from one 2D pass to H/V passes.
                2.127346449506261,     // Full-res row starts crossfading into down/up.
                2.602865362752564,     // Full-res row hands over to real down/up passes.
                2.964963537657912,     // Half-res Gaussian starts fading in.
                5.742372185986587,     // Fitted stages -> fixed pyramid.
                6.564358997305093,     // A pyramid Gaussian row grows by a fetch.
                15.463531483015744,    // A pyramid level starts crossfading into one more level.
                17.743531483015744,    // That crossfade ends; the extra level becomes real passes.
                18.012380026063752,    // The outermost cubic Up starts fading to bilinear (sigma 4.25).
                20.193430251737784     // The outermost Up is bilinear: one fetch instead of four (sigma 4.5).
            };
            // A literal that drifts from its runtime switch would probe a
            // stretch with no switch at all.
            Assert.That(boundaries[2], Is.EqualTo(FittedDownUp.Start).Within(1e-12));
            Assert.That(boundaries[3], Is.EqualTo(SmallKernel.DownUpPassesStart).Within(1e-12));
            Assert.That(boundaries[5], Is.EqualTo(FittedDownUp.End).Within(1e-12));
            Assert.That(boundaries[7], Is.EqualTo(OuterLevelSwitch(SmallKernel.NextLevelFadeStart)).Within(1e-5));
            Assert.That(boundaries[8], Is.EqualTo(OuterLevelSwitch(SmallKernel.NextLevelFadeEnd)).Within(1e-5));
            Assert.That(boundaries[9], Is.EqualTo(BlurMath.RequestedVariance(SmallKernel.OuterBilinearStartSigma)).Within(1e-12));
            Assert.That(boundaries[10], Is.EqualTo(BlurMath.RequestedVariance(SmallKernel.OuterBilinearEndSigma)).Within(1e-12));
            foreach (double variance in boundaries)
            {
                float sigma = SigmaForVariance(variance);
                // A close pair isolates the scheduling switch from ordinary
                // change with sigma, and from the small steps of fetches
                // rounding to the sampler grid, a few hundredths of sigma apart.
                // LayoutSwitchesKeepTheResponse bounds the filters' own change;
                // this checks the GPU runs both sides alike.
                const float step = 0.00001f;
                Color[] below = null;
                Color[] above = null;
                // Both sides still undergo the usual CPU comparison and
                // source-preservation checks. Odd sizes exercise borders and
                // coarse-grid parity while retaining an interior region.
                yield return RunCase(new BlurCase(33, 25, sigma - step)
                {
                    ReceiveResult = pixels => below = pixels
                });
                yield return RunCase(new BlurCase(33, 25, sigma + step)
                {
                    ReceiveResult = pixels => above = pixels
                });
                // Require GPU continuity within sample precision: allow one
                // full-scale half spacing on average and three at the worst
                // pixel, accounting for intermediate sampling across stages.
                AssertPixels(above, below, 3 * HalfSpacingBelowOne, HalfSpacingBelowOne,
                    $"GPU boundary variance {variance}");
            }
        }

        static float SigmaForVariance(double variance)
        {
            double low = 0;
            double high = Math.Max(1, 1.5 * Math.Sqrt(variance));
            for (int i = 0; i < 48; i++)
            {
                double middle = (low + high) * 0.5;
                if (BlurMath.RequestedVariance(middle) < variance)
                    low = middle;
                else
                    high = middle;
            }
            return (float)((low + high) * 0.5);
        }

        // The requested variance at which the outermost level's half-res
        // budget reaches `coarse`, where the pyramid adds a level. The outer
        // Up's blend changes that budget, so the switch moves with it.
        static double OuterLevelSwitch(double coarse)
        {
            var workspace = new SmallKernel.Workspace();
            double target = SmallKernel.LevelVariance + 4 * coarse;
            double low = FittedDownUp.End;
            double high = BlurMath.RequestedVariance(TransitionKernel.StartSigma);
            for (int i = 0; i < 100; i++)
            {
                double middle = (low + high) * 0.5;
                double up = SmallKernel.OuterUpsample(workspace, SmallKernel.OuterUpsampleBilinear(SigmaForVariance(middle)));
                if (SmallKernel.OuterLevelVariance(middle, up) < target)
                    low = middle;
                else
                    high = middle;
            }
            return (low + high) * 0.5;
        }

        [UnityTest]
        public IEnumerator WideEntryEndpointsRemainContinuous()
        {
            const int width = 257, height = 193;
            const float amplitude = 10000;
            var input = new Color[width * height];
            for (int channel = 0; channel < 4; channel++)
            {
                Color point = Color.clear;
                point[channel] = amplitude;
                input[(height / 2 + channel) * width + width / 2 + channel] = point;
            }
            foreach (float boundary in new[] { 16.1f, 24f })
            {
                Color[] below = null, above = null;
                yield return RunCase(new BlurCase(width, height, boundary - 0.0001f)
                {
                    InputPixels = input,
                    ReceiveResult = pixels => below = pixels,
                    CompareReference = false
                });
                yield return RunCase(new BlurCase(width, height, boundary + 0.0001f)
                {
                    InputPixels = input,
                    ReceiveResult = pixels => above = pixels,
                    CompareReference = false
                });
                for (int channel = 0; channel < 4; channel++)
                {
                    double difference = 0;
                    for (int i = 0; i < below.Length; i++)
                        difference += Math.Abs(above[i][channel] - below[i][channel]);
                    // Compare total response change to input energy. A tolerance
                    // relative to the HDR input's peak would hide shape changes.
                    double change = difference / amplitude;
                    TestContext.WriteLine($"Endpoint sigma {boundary}, phase {channel}, L1 change {change:R}");
                    // Measured worst phases on M4: 0.40% at sigma 16.1 and 0.17%
                    // at sigma 24 (Tools~/FilterFit/fit_transition.py fractions).
                    // Keep separate regression budgets.
                    double budget = boundary < 20 ? 0.0045 : 0.0020;
                    Assert.That(change, Is.LessThan(budget),
                        $"Endpoint response change, sigma {boundary}, phase {channel}");
                }
            }
        }

        [UnityTest]
        public IEnumerator PointResponsesKeepEnergyPositionAndWidth()
        {
            RequireFloat32();

            const float amplitude = 10000;
            // One strength inside each layout and crossfade, plus both sides of
            // the transition's ends and the working pitch switches.
            foreach (float sigma in new[] { 0.5f, 0.9f, 1.3f, 1.55f, 1.7f, 2.1f, 3.2f, 6f, 10f, 15f,
                16.125f, 16.1875f, 20f, 23.99f, 24.01f, 70f, 140f })
            {
                // Keep the five-sigma support inside the row; a truncated tail
                // would bias the first moment toward the nearer border.
                int length = sigma >= 140 ? 2049 : 1025;
                // Four RGBA impulses per batch; batches cover every phase of the
                // working grid (16 pixels below sigma 70, 32 to 140, then 64).
                int batches = sigma >= 140 ? 16 : sigma >= 70 ? 8 : 4;
                double expected = BlurGpuReference.TaperedGaussianVariance(sigma);
                for (int axis = 0; axis < 2; axis++)
                {
                    double width = 0;
                    for (int batch = 0; batch < batches; batch++)
                    {
                        int first = length / 2 + 4 * batch;
                        var input = new Color[length];
                        for (int channel = 0; channel < 4; channel++)
                        {
                            Color point = Color.clear;
                            point[channel] = amplitude;
                            input[first + channel] = point;
                        }
                        Color[] result = null;
                        yield return RunCase(new BlurCase(axis == 0 ? length : 1, axis == 0 ? 1 : length, sigma)
                        {
                            InputPixels = input,
                            Format = GraphicsFormat.R32G32B32A32_SFloat,
                            ReceiveResult = pixels => result = pixels,
                            CompareReference = false
                        });
                        // These moments use source positions directly, independently
                        // of the runtime's coefficients and CPU pipeline reference.
                        for (int channel = 0; channel < 4; channel++)
                        {
                            double mass = 0, firstMoment = 0, secondMoment = 0;
                            for (int i = 0; i < length; i++)
                            {
                                float value = result[i][channel];
                                Assert.That(float.IsFinite(value), Is.True);
                                double offset = i - first - channel;
                                mass += value;
                                firstMoment += offset * value;
                                secondMoment += offset * offset * value;
                            }
                            string label = $"sigma {sigma}, axis {axis}, phase {4 * batch + channel}";
                            Assert.That(mass / amplitude, Is.EqualTo(1).Within(1e-4), "Point energy, " + label);
                            // The quarter-resolution pass centers each source phase
                            // within 1/128 pixel; below sigma 16.1 rounding to the
                            // sampler grid leaves a little more.
                            Assert.That(Math.Abs(firstMoment / mass),
                                Is.LessThan(sigma >= TransitionKernel.StartSigma ? 0.0081 : 0.011),
                                "Point displacement, " + label);
                            width += secondMoment / mass;
                        }
                    }
                    // Width: the second moment about the point, averaged over
                    // every phase. The 0.4% shortfall of a misbudgeted pass
                    // and the 0.1% of uncorrected fetch rounding both exceed this.
                    Assert.That(width / (4 * batches) / expected, Is.EqualTo(1).Within(2e-4),
                        $"Point width, sigma {sigma}, axis {axis}");
                }
            }
        }

        [UnityTest]
        public IEnumerator WorkingPitchSwitchesRemainContinuous()
        {
            if (!SystemInfo.SupportsRenderTextureFormat(RenderTextureFormat.ARGBHalf) ||
                !SystemInfo.SupportsTextureFormat(TextureFormat.RGBAHalf))
                Assert.Ignore("This check requires FP16 textures.");
            const int length = 1025;
            const float amplitude = 10000;
            // The working pitch doubles at 70 (to 32) and 140 (to 64). Each layout
            // approximates the same Gaussian, so the response may change only
            // slightly. The blur is separable, so rows along each axis suffice.
            // Four impulses per batch; batches cover every phase of the
            // coarser grid.
            foreach (float boundary in new[] { 70f, 140f })
            {
                for (int axis = 0; axis < 2; axis++)
                {
                    int width = axis == 0 ? length : 1, height = axis == 0 ? 1 : length;
                    int batches = boundary == 70 ? 8 : 16;
                    for (int batch = 0; batch < batches; batch++)
                    {
                        var input = new Color[length];
                        for (int channel = 0; channel < 4; channel++)
                        {
                            Color point = Color.clear;
                            point[channel] = amplitude;
                            input[length / 2 - 32 + 4 * batch + channel] = point;
                        }
                        Color[] below = null, above = null;
                        yield return RunCase(new BlurCase(width, height, boundary * 0.9999f)
                        {
                            InputPixels = input,
                            Format = GraphicsFormat.R16G16B16A16_SFloat,
                            ReceiveResult = pixels => below = pixels,
                            CompareReference = false
                        });
                        yield return RunCase(new BlurCase(width, height, boundary * 1.0001f)
                        {
                            InputPixels = input,
                            Format = GraphicsFormat.R16G16B16A16_SFloat,
                            ReceiveResult = pixels => above = pixels,
                            CompareReference = false
                        });
                        for (int channel = 0; channel < 4; channel++)
                        {
                            double difference = 0;
                            for (int i = 0; i < below.Length; i++)
                                difference += Math.Abs(above[i][channel] - below[i][channel]);
                            double change = difference / amplitude;
                            // Measured about 0.2% on M4; the budget leaves rounding headroom.
                            Assert.That(change, Is.LessThan(0.005),
                                $"Pitch switch response change, sigma {boundary}, axis {axis}, phase {4 * batch + channel}");
                        }
                    }
                }
            }
        }

        [UnityTest]
        public IEnumerator ExtremeStrengthLimitIsContinuous()
        {
            // At 1024 times the larger dimension, the layout switches to the
            // corner average. Both sides must already be close to it.
            const int width = 7, height = 5;
            float boundary = 1024f * Math.Max(width, height);
            Color[] below = null, above = null;
            yield return RunCase(new BlurCase(width, height, boundary * 0.999f)
            {
                ReceiveResult = pixels => below = pixels,
                CompareReference = false
            });
            yield return RunCase(new BlurCase(width, height, boundary * 1.001f)
            {
                ReceiveResult = pixels => above = pixels
            });
            AssertPixels(above, below, 0.001f, 0.001f, "Extreme strength limit");
        }
    }
}
