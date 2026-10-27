using System;
using System.Collections.Generic;
using UnityEngine;
using Crossfade = SteplessGaussianBlur.SmallKernel.Crossfade;

namespace SteplessGaussianBlur.Tests
{
    /// <summary>
    /// A 1D CPU model of the passes each layout records, sampled the way the
    /// shaders sample them, in exact arithmetic and away from image edges.
    /// Coefficients come from the runtime kernels; the pass geometry, the
    /// sampler and the target width do not.
    /// </summary>
    /// <remarks>
    /// Signals are rows of texel values on one grid. Node n is texel n and
    /// sits at position n; the shaders add 0.5 when they turn a node into a
    /// sampling coordinate. Every fetch must sit on the sampler's 1/256 grid;
    /// fetches clamp to the row's ends. Every pass is separable,
    /// so the 2D response of a point is the product of two such rows.
    /// </remarks>
    internal static class ExecutedPipeline
    {
        /// <summary>Response to a unit point at <paramref name="point"/> of a row of <paramref name="length"/> texels.</summary>
        /// <param name="outputScale">1, or a reduced output's scale, which <see cref="View"/> then magnifies.</param>
        /// <param name="reduced">A reduced output, which <paramref name="outputScale"/> above 1 implies.</param>
        internal static double[] Respond(float sigma, int length, int point, int outputScale = 1,
            BlurQuality quality = BlurQuality.Fast, bool reduced = false)
        {
            var source = new double[length];
            source[point] = 1;
            return Run(sigma, source, outputScale, quality, reduced);
        }

        /// <summary>Runs the layout of <paramref name="sigma"/> on one row, at full resolution.</summary>
        /// <param name="outputScale">
        /// 1, or a reduced output's scale: the layout stops at that grid and
        /// <see cref="View"/> samples it back to full resolution.
        /// </param>
        internal static double[] Run(float sigma, double[] source, int outputScale = 1,
            BlurQuality quality = BlurQuality.Fast, bool reduced = false)
        {
            double[] output = RunOutput(sigma, source, outputScale, quality, reduced || outputScale > 1);
            return outputScale == 1 ? output : View(output, outputScale, source.Length);
        }

        static double[] RunOutput(float sigma, double[] source, int outputScale, BlurQuality quality, bool reduced)
        {
            if (sigma >= TransitionKernel.EndSigma)
                return Wide(sigma, source, outputScale);
            if (sigma >= TransitionKernel.StartSigma)
                return Transition(sigma, source, outputScale);
            double variance = BlurMath.RequestedVariance(sigma);
            if (variance < 1e-12)
                return (double[])source.Clone();
            return Small(sigma, variance, source, outputScale, quality, reduced);
        }

        /// <summary>
        /// A viewer's bilinear sampling of a reduced output at full
        /// resolution: pixel x reads (x + 0.5) / scale - 0.5, clamped.
        /// </summary>
        internal static double[] View(double[] reduced, int scale, int length)
        {
            var output = new double[length];
            for (int x = 0; x < length; x++)
                output[x] = Interpolate(reduced, (x + 0.5) / scale - 0.5);
            return output;
        }

        // ---- Sampling ------------------------------------------------------------

        static double Sample(double[] row, double position)
        {
            // Every fetch must sit on the sampler's 1/256 grid.
            double units = position * 256;
            if (Math.Abs(units - Math.Round(units)) > 1e-6)
                throw new InvalidOperationException($"Fetch at {position:R} is off the sampler grid.");
            double snapped = Math.Round(units) / 256;
            int first = (int)Math.Floor(snapped);
            double fraction = snapped - first;
            double a = row[Math.Clamp(first, 0, row.Length - 1)];
            double b = row[Math.Clamp(first + 1, 0, row.Length - 1)];
            return a + (b - a) * fraction;
        }

        // Exact linear interpolation, clamped to the row's ends.
        static double Interpolate(double[] row, double position)
        {
            int first = (int)Math.Floor(position);
            double fraction = position - first;
            double a = row[Math.Clamp(first, 0, row.Length - 1)];
            double b = row[Math.Clamp(first + 1, 0, row.Length - 1)];
            return a + (b - a) * fraction;
        }

        static int DivideUp(int value, int divisor) => (value + divisor - 1) / divisor;

        // RecordSmallPass rounds small-layout offsets to the 1/256 grid
        // before upload.
        static double Uploaded(float offset) => (float)BlurMath.Snap(offset);

        // ---- Small layouts (Layouts/SmallLayout.cs, Shaders/Small.hlsl) -----------

        static readonly SmallKernel.Workspace workspace = new SmallKernel.Workspace();

        static double[] Small(float sigma, double variance, double[] source, int outputScale, BlurQuality quality,
            bool reduced)
        {
            if (quality == BlurQuality.High && variance < FittedDownUp.End)
                return Rows(source, variance, Crossfade.IntoFirstLevel);
            if (variance < SmallKernel.DownUpPassesStart)
                return Rows(source, variance, Crossfade.IntoDownUp);
            if (variance < FittedDownUp.End)
            {
                FittedDownUp downUp = FittedDownUp.At(variance);
                var taps = new Vector4[4];
                downUp.PackDown(taps);
                double[] half = Down(source, taps);
                if (downUp.halfResGaussianVariance > 1e-10)
                    half = Rows(half, downUp.halfResGaussianVariance, Crossfade.None);
                downUp.PackUp(taps);
                return Up(half, taps, source.Length);
            }
            double bilinear = SmallKernel.OuterUpsampleBilinear(sigma, reduced);
            double upVariance = SmallKernel.OuterUpsample(workspace, bilinear);
            return PyramidLevel(source, SmallKernel.OuterLevelVariance(variance, upVariance), bilinear, outputScale);
        }

        static double[] PyramidLevel(double[] source, double variance, double outerBilinear, int outputScale = 1)
        {
            if (variance < SmallKernel.NextLevelFadeStart)
                return Rows(source, variance, Crossfade.None);
            if (variance < SmallKernel.NextLevelFadeEnd)
                return Rows(source, variance, Crossfade.IntoNextLevel);
            double[] half = Down(source, SmallKernel.BinomialDown);
            half = PyramidLevel(half, SmallKernel.CoarseVariance(variance), 0);
            // A reduced output stops before the bilinear Up.
            if (outputScale == 2)
                return half;
            if (outerBilinear == 1)
            {
                var linear = new double[source.Length];
                for (int x = 0; x < linear.Length; x++)
                    linear[x] = Sample(half, x * 0.5 - 0.25);
                return linear;
            }
            return Up(half, outerBilinear > 0 ? workspace.outerTaps : SmallKernel.CubicUp, source.Length);
        }

        // A Gaussian stage: even output nodes read taps [0, 16), odd [16, 32),
        // as the kernels build them. RecordSmallPass interleaves both phases
        // at upload; GaussianPassesUploadBothPhasesOfEachTap checks that.
        static double[] Rows(double[] source, double variance, Crossfade crossfade)
        {
            int count = SmallKernel.GaussianRows(workspace, variance, crossfade);
            var output = new double[source.Length];
            for (int x = 0; x < output.Length; x++)
            {
                int start = (x & 1) * SmallKernel.MaxFetches;
                double sum = 0;
                for (int k = 0; k < count; k++)
                {
                    Vector4 tap = workspace.taps[start + k];
                    sum += tap.y * Sample(source, x + Uploaded(tap.x));
                }
                output[x] = sum;
            }
            return output;
        }

        // Half-res node j is centered between source nodes 2j and 2j + 1.
        static double[] Down(double[] source, Vector4[] taps)
        {
            var output = new double[DivideUp(source.Length, 2)];
            for (int j = 0; j < output.Length; j++)
            {
                double sum = 0;
                for (int k = 0; k < 3; k++)
                    sum += taps[k].y * Sample(source, 2 * j + 0.5 + Uploaded(taps[k].x));
                output[j] = sum;
            }
            return output;
        }

        // Output node x reads around half-res node x >> 1: taps 0-1 even, 2-3 odd.
        static double[] Up(double[] half, Vector4[] taps, int length)
        {
            var output = new double[length];
            for (int x = 0; x < length; x++)
            {
                int start = (x & 1) * 2;
                double sum = 0;
                for (int k = 0; k < 2; k++)
                    sum += taps[start + k].y * Sample(half, (x >> 1) + Uploaded(taps[start + k].x));
                output[x] = sum;
            }
            return output;
        }

        // ---- Transition (Layouts/TransitionLayout.cs, Shaders/Transition.hlsl) ----

        static readonly TransitionKernel transition = new TransitionKernel();

        static double[] Transition(float sigma, double[] source, int outputScale)
        {
            transition.Prepare(sigma, outputScale);
            // Quarter: node m reads around source node 4m.
            var quarter = new double[DivideUp(source.Length, 4)];
            for (int m = 0; m < quarter.Length; m++)
                quarter[m] = Anchored(source, 4 * m, TransitionKernel.Quarter, 0, 4, 0);
            // Halve: node e reads around quarter node 2e; the fixed pass from
            // 16.6 runs [1, 3, 3, 1] / 8 as two pairs at -0.25 and 1.25.
            var eighth = new double[DivideUp(source.Length, 8)];
            bool settled = sigma >= TransitionKernel.SettledSigma;
            for (int e = 0; e < eighth.Length; e++)
            {
                if (settled)
                    eighth[e] = 0.5 * Sample(quarter, 2 * e - 0.25) + 0.5 * Sample(quarter, 2 * e + 1.25);
                else
                {
                    double sum = 0;
                    for (int k = 0; k < 3; k++)
                        sum += transition.Halve[k].y * Sample(quarter, 2 * e + transition.Halve[k].x);
                    eighth[e] = sum;
                }
            }
            // Gaussian: even eighth nodes read the row, odd ones its mirror.
            int count = transition.GaussianCount;
            var rows = new Vector4[2 * count];
            for (int i = 0; i < count; i++)
            {
                rows[i] = transition.Gaussian[i];
                rows[count + i] = new Vector4(-rows[i].x, rows[i].y, 0, 0);
            }
            var blurred = new double[eighth.Length];
            for (int e = 0; e < eighth.Length; e++)
                blurred[e] = Anchored(eighth, e, rows, (e & 1) * count, count, count / 2);
            // Up: the pyramid's cubic 2x onto the quarter grid.
            int pitch = TransitionKernel.ReconstructionPitch;
            double[] fine = Up(blurred, SmallKernel.CubicUp, DivideUp(source.Length, pitch));
            if (outputScale == pitch)
                return fine;
            // Final: output node x reads (x + 0.5) / scale - 0.5.
            int scale = pitch / outputScale;
            var output = new double[DivideUp(source.Length, outputScale)];
            for (int x = 0; x < output.Length; x++)
                output[x] = Sample(fine, (x + 0.5) / scale - 0.5);
            return output;
        }

        // The shaders' anchored sum: the anchor fetch takes whatever weight
        // the others leave, so the row's total is exactly one.
        static double Anchored(double[] row, double center, Vector4[] taps, int start, int count, int anchor)
        {
            double anchorValue = Sample(row, center + taps[start + anchor].x);
            double sum = anchorValue;
            for (int k = 0; k < count; k++)
            {
                if (k != anchor)
                    sum += (Sample(row, center + taps[start + k].x) - anchorValue) * taps[start + k].y;
            }
            return sum;
        }

        // ---- Wide (Layouts/WideLayout.cs, Shaders/Wide.hlsl) ----------------------

        static readonly WideKernel wide = new WideKernel();

        static double[] Wide(float sigma, double[] source, int outputScale)
        {
            wide.Prepare(sigma, outputScale);
            var quarter = new double[DivideUp(source.Length, 4)];
            for (int m = 0; m < quarter.Length; m++)
                quarter[m] = Anchored(source, 4 * m, TransitionKernel.Quarter, 0, 4, 0);
            // Halve [1, 3, 3, 1] / 8 down to the pitch: node j reads around 2j + 0.5.
            double[] current = quarter;
            for (int pitch = 4; pitch < wide.Pitch; pitch *= 2)
            {
                var next = new double[DivideUp(current.Length, 2)];
                for (int j = 0; j < next.Length; j++)
                    next[j] = 0.5 * Sample(current, 2 * j - 0.25) + 0.5 * Sample(current, 2 * j + 1.25);
                current = next;
            }
            // Gaussian: fetch (first, fraction, weight), even nodes' row, which
            // odd nodes mirror, anchored on the middle fetch.
            int count = wide.GaussianCount;
            var blurred = new double[current.Length];
            for (int j = 0; j < blurred.Length; j++)
            {
                int sign = (j & 1) == 0 ? 1 : -1;
                int anchor = count / 2;
                Vector4 a = wide.Gaussian[anchor];
                double anchorValue = Sample(current, j + sign * ((double)a.x + a.y));
                double sum = anchorValue;
                for (int k = 0; k < count; k++)
                {
                    Vector4 tap = wide.Gaussian[k];
                    if (k != anchor)
                        sum += (Sample(current, j + sign * ((double)tap.x + tap.y)) - anchorValue) * tap.z;
                }
                blurred[j] = sum;
            }
            // Cubic: 4x up, node i reads around node i >> 2.
            int scale = wide.ReconstructionPitch;
            var fine = new double[DivideUp(source.Length, scale)];
            for (int i = 0; i < fine.Length; i++)
            {
                Vector4 pair = wide.Cubic[i & 3];
                double a = Sample(blurred, (i >> 2) + pair.x);
                fine[i] = a + (Sample(blurred, (i >> 2) + pair.y) - a) * pair.w;
            }
            // Final: output node x reads (x + 0.5) / scale - 0.5. Past a scale
            // of 128 its phases fall between the sampler's 1/256 steps, where
            // the shader's 1/1024 guard and a truncating sampler land on the
            // step below. A reduced output's Final stops at its grid, or is
            // skipped there.
            if (outputScale == scale)
                return fine;
            scale /= outputScale;
            var output = new double[DivideUp(source.Length, outputScale)];
            for (int x = 0; x < output.Length; x++)
                output[x] = Sample(fine, Math.Floor(((x + 0.5) / scale - 0.5 + 1.0 / 1024) * 256) / 256);
            return output;
        }

        // ---- Measurements --------------------------------------------------------

        /// <summary>
        /// Variance of the discrete Gaussian of <paramref name="sigma"/> tapered
        /// to zero over its last half-sigma before four sigma, summed directly.
        /// </summary>
        internal static double TargetVariance(double sigma)
        {
            int reach = (int)Math.Ceiling(4 * sigma);
            double total = 0, second = 0;
            for (int k = -reach; k <= reach; k++)
            {
                double t = Math.Clamp((4 - Math.Abs(k) / sigma) * 2, 0, 1);
                double weight = Math.Exp(-0.5 * k * k / (sigma * sigma)) * t * t * (3 - 2 * t);
                total += weight;
                second += (double)k * k * weight;
            }
            return second / total;
        }

        internal struct Moments
        {
            internal double mass, mean, variance;
        }

        internal static Moments Measure(double[] response, int point)
        {
            double mass = 0, first = 0;
            for (int i = 0; i < response.Length; i++)
            {
                mass += response[i];
                first += (i - point) * response[i];
            }
            double mean = first / mass;
            double second = 0;
            for (int i = 0; i < response.Length; i++)
                second += (i - point - mean) * (i - point - mean) * response[i];
            return new Moments { mass = mass, mean = mean, variance = second / mass };
        }

        /// <summary>A row long enough that no response reaches its ends, and its first measured point.</summary>
        internal static (int length, int point) Frame(double sigma)
        {
            int margin = 256 * (int)Math.Ceiling((8 * sigma + 64) / 256);
            return (2 * margin + 256, margin);
        }

        /// <summary>Sum of absolute differences between two responses.</summary>
        internal static double L1(double[] a, double[] b)
        {
            double sum = 0;
            for (int i = 0; i < a.Length; i++)
                sum += Math.Abs(a[i] - b[i]);
            return sum;
        }
    }
}
