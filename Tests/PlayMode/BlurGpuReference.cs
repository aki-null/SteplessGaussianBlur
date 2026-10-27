using System;
using System.Collections.Generic;
using UnityEngine;

namespace SteplessGaussianBlur.Tests
{
    internal static class BlurGpuReference
    {
        enum StageKind { Filter, Downsample, Upsample, BilinearUpsample }

        /// <summary>
        /// Evaluates filter rows on an unbounded integer grid. Only the original
        /// source clamps. Coefficient tests cover fitting; this oracle checks
        /// shader coordinates, phase selection and the complete pass sequence.
        /// It does not imitate texture allocations or GPU halo clamps.
        /// </summary>
        sealed class NodeStage
        {
            internal NodeStage parent;
            internal Vector4[] taps;
            internal int count, stride, phases = 1, factor = 1;
            internal StageKind kind;
            internal double shift;
            double[] source;
            readonly Dictionary<int, double> cache = new Dictionary<int, double>();

            internal void Reset(double[] row)
            {
                cache.Clear();
                if (parent == null)
                    source = row;
                else
                    parent.Reset(row);
            }

            internal double At(int node)
            {
                if (parent == null)
                    return source[Math.Clamp(node, 0, source.Length - 1)];
                if (cache.TryGetValue(node, out double value))
                    return value;
                int phase = (node % phases + phases) % phases;
                double center = kind == StageKind.Downsample ? factor * (double)node :
                    kind == StageKind.Upsample ? Math.Floor(node / (double)factor) :
                    kind == StageKind.BilinearUpsample ? node / (double)factor : node;
                center += shift;
                value = 0;
                if (kind == StageKind.BilinearUpsample)
                    value = Sample(center);
                else
                {
                    for (int i = 0; i < count; i++)
                    {
                        Vector4 tap = taps[phase * stride + i];
                        value += tap.y * Sample(center + tap.x);
                    }
                }
                cache[node] = value;
                return value;
            }

            double Sample(double position)
            {
                int first = (int)Math.Floor(position);
                double fraction = position - first;
                return parent.At(first) * (1 - fraction) + parent.At(first + 1) * fraction;
            }
        }

        static NodeStage Stage(NodeStage parent, Vector4[] taps, int count,
            StageKind kind = StageKind.Filter, int factor = 1, double shift = 0, int phases = 1,
            int stride = 0)
        {
            return new NodeStage
            {
                parent = parent,
                taps = taps == null ? null : (Vector4[])taps.Clone(),
                count = count,
                kind = kind,
                factor = factor,
                shift = shift,
                phases = phases,
                stride = stride
            };
        }

        static NodeStage SmallRow(NodeStage parent, SmallKernel.Workspace workspace,
            double variance, SmallKernel.Crossfade crossfade)
        {
            int count = SmallKernel.GaussianRows(workspace, variance, crossfade);
            return Stage(parent, workspace.taps, count, phases: 2, stride: 16);
        }

        static NodeStage SmallPyramid(NodeStage source, SmallKernel.Workspace workspace,
            double variance, double blend = 0, int outputScale = 1)
        {
            if (variance < SmallKernel.NextLevelFadeStart)
                return SmallRow(source, workspace, variance, SmallKernel.Crossfade.None);
            if (variance < SmallKernel.NextLevelFadeEnd)
                return SmallRow(source, workspace, variance, SmallKernel.Crossfade.IntoNextLevel);
            NodeStage down = Stage(source, SmallKernel.BinomialDown, 3, StageKind.Downsample, 2, 0.5);
            down = SmallPyramid(down, workspace, SmallKernel.CoarseVariance(variance));
            // A reduced output is the half-res level, before the bilinear Up.
            if (outputScale == 2)
                return down;
            if (blend == 1)
                return Stage(down, null, 0, StageKind.BilinearUpsample, 2, -0.25);
            // Every small-layout fetch sits on the sampler's 1/256 grid, so
            // these mathematical offsets are the positions the GPU reads.
            return Stage(down, blend > 0 ? workspace.outerTaps : SmallKernel.CubicUp,
                2, StageKind.Upsample, 2, phases: 2, stride: 2);
        }

        static NodeStage SmallPlan(float sigma, int outputScale, BlurQuality quality)
        {
            var source = new NodeStage();
            var workspace = new SmallKernel.Workspace();
            double variance = BlurMath.RequestedVariance(sigma);
            if (quality == BlurQuality.High && variance < FittedDownUp.End)
                return SmallRow(source, workspace, variance, SmallKernel.Crossfade.IntoFirstLevel);
            if (variance < SmallKernel.DownUpPassesStart)
                return SmallRow(source, workspace, variance, SmallKernel.Crossfade.IntoDownUp);
            if (variance >= FittedDownUp.End)
            {
                double bilinear = SmallKernel.OuterUpsampleBilinear(sigma);
                double upVariance = SmallKernel.OuterUpsample(workspace, bilinear);
                return SmallPyramid(source, workspace, SmallKernel.OuterLevelVariance(variance, upVariance), bilinear,
                    outputScale);
            }
            FittedDownUp downUp = FittedDownUp.At(variance);
            downUp.PackDown(workspace.taps);
            NodeStage down = Stage(source, workspace.taps, 3, StageKind.Downsample, 2, 0.5);
            if (downUp.halfResGaussianVariance > 1e-10)
                down = SmallRow(down, workspace, downUp.halfResGaussianVariance, SmallKernel.Crossfade.None);
            downUp.PackUp(workspace.taps);
            return Stage(down, workspace.taps, 2, StageKind.Upsample, 2, phases: 2, stride: 2);
        }

        static NodeStage WidePlan(float sigma, int outputScale)
        {
            var source = new NodeStage();
            if (sigma < TransitionKernel.EndSigma)
            {
                var kernel = new TransitionKernel();
                kernel.Prepare(sigma, outputScale);
                NodeStage quarter = Stage(source, TransitionKernel.Quarter, 4, StageKind.Downsample, 4);
                NodeStage down = Stage(quarter, kernel.Halve, 3, StageKind.Downsample, 2);
                // Even nodes' row, then the odd nodes' mirror.
                int rowCount = kernel.GaussianCount;
                var rows = new Vector4[2 * rowCount];
                for (int i = 0; i < rowCount; i++)
                {
                    rows[i] = kernel.Gaussian[i];
                    rows[rowCount + i] = new Vector4(-rows[i].x, rows[i].y, 0, 0);
                }
                NodeStage gaussian = Stage(down, rows, rowCount, phases: 2, stride: rowCount);
                // The pyramid's cubic Up onto the quarter grid, then Final.
                NodeStage up = Stage(gaussian, SmallKernel.CubicUp, 2, StageKind.Upsample, 2, phases: 2, stride: 2);
                int finalScale = TransitionKernel.ReconstructionPitch / outputScale;
                return finalScale == 1 ? up :
                    Stage(up, null, 0, StageKind.BilinearUpsample, finalScale, 0.5 / finalScale - 0.5);
            }
            var wide = new WideKernel();
            wide.Prepare(sigma, outputScale);
            // FragWideQuarter inlines the transition's quarter filter.
            NodeStage current = Stage(source, TransitionKernel.Quarter, 4, StageKind.Downsample, 4);
            Vector4[] binomial = { new Vector4(-0.25f, 0.5f, 0, 0), new Vector4(1.25f, 0.5f, 0, 0) };
            for (int pitch = 8; pitch <= wide.Pitch; pitch *= 2)
                current = Stage(current, binomial, 2, StageKind.Downsample, 2);
            // Even nodes' row, then the odd nodes' mirror.
            int count = wide.GaussianCount;
            var gaussianTaps = new Vector4[2 * count];
            for (int parity = 0; parity < 2; parity++)
                for (int i = 0; i < count; i++)
                {
                    Vector4 tap = wide.Gaussian[i];
                    gaussianTaps[parity * count + i] = new Vector4((1 - 2 * parity) * (tap.x + tap.y), tap.z, 0, 0);
                }
            current = Stage(current, gaussianTaps, count, phases: 2, stride: count);
            var cubicTaps = new Vector4[8];
            for (int phase = 0; phase < 4; phase++)
            {
                Vector4 pair = wide.Cubic[phase];
                cubicTaps[2 * phase] = new Vector4(pair.x, pair.z, 0, 0);
                cubicTaps[2 * phase + 1] = new Vector4(pair.y, pair.w, 0, 0);
            }
            current = Stage(current, cubicTaps, 2, StageKind.Upsample, 4, phases: 4, stride: 2);
            // Final magnifies onto the output grid; a reduced output at the
            // cubic's own grid skips it.
            int scale = wide.ReconstructionPitch / outputScale;
            return scale == 1 ? current :
                Stage(current, null, 0, StageKind.BilinearUpsample, scale, 0.5 / scale - 0.5);
        }

        static NodeStage Plan(float sigma, int outputScale = 1, BlurQuality quality = BlurQuality.Fast) =>
            sigma < TransitionKernel.StartSigma ? SmallPlan(sigma, outputScale, quality) : WidePlan(sigma, outputScale);

        // Variance of the discrete Gaussian, faded by smoothstep over its final
        // half sigma before four sigma (Documentation~/Design.md#conventions).
        internal static double TaperedGaussianVariance(double sigma)
        {
            int reach = (int)Math.Ceiling(4 * sigma);
            double total = 0, moment = 0;
            for (int k = -reach; k <= reach; k++)
            {
                double t = Math.Clamp((4 - Math.Abs(k) / sigma) / 0.5, 0, 1);
                double weight = Math.Exp(-0.5 * k * k / (sigma * sigma)) * t * t * (3 - 2 * t);
                total += weight;
                moment += (double)k * k * weight;
            }
            return moment / total;
        }

        /// <summary>Blurs one row with the reference pipeline; the row is clamp-extended.</summary>
        internal static double[] BlurRow(double[] row, float sigma)
        {
            NodeStage plan = Plan(sigma);
            plan.Reset(row);
            var result = new double[row.Length];
            for (int x = 0; x < row.Length; x++)
                result[x] = plan.At(x);
            return result;
        }

        /// <summary>Blurs an image with the reference pipeline.</summary>
        /// <param name="outputScale">1, or a reduced output's scale: the result is that much smaller.</param>
        internal static Color[] Blur(Color[] source, int width, int height, float sigma, int outputScale = 1,
            BlurQuality quality = BlurQuality.Fast)
        {
            if (sigma < 1e-5f || width == 1 && height == 1)
                return (Color[])source.Clone();
            int outputWidth = width / outputScale;
            int outputHeight = height / outputScale;
            var result = new Color[outputWidth * outputHeight];
            if (sigma >= 1024.0 * Math.Max(width, height))
            {
                Color mean = source[0] * 0.25f + source[width - 1] * 0.25f +
                    source[(height - 1) * width] * 0.25f + source[source.Length - 1] * 0.25f;
                Array.Fill(result, mean);
                return result;
            }
            NodeStage plan = Plan(sigma, outputScale, quality);
            var horizontal = new double[height, outputWidth, 4];
            var row = new double[width];
            for (int y = 0; y < height; y++)
            {
                for (int channel = 0; channel < 4; channel++)
                {
                    for (int x = 0; x < width; x++)
                        row[x] = source[y * width + x][channel];
                    plan.Reset(row);
                    for (int x = 0; x < outputWidth; x++)
                        horizontal[y, x, channel] = plan.At(x);
                }
            }
            var column = new double[height];
            for (int x = 0; x < outputWidth; x++)
            {
                for (int channel = 0; channel < 4; channel++)
                {
                    for (int y = 0; y < height; y++)
                        column[y] = horizontal[y, x, channel];
                    plan.Reset(column);
                    for (int y = 0; y < outputHeight; y++)
                        result[y * outputWidth + x][channel] = (float)plan.At(y);
                }
            }
            return result;
        }
    }
}
