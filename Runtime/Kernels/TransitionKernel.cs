using System;
using UnityEngine;
using static SteplessGaussianBlur.BlurMath;

namespace SteplessGaussianBlur
{
    /// <summary>
    /// Coefficients of the transition layout, sigma 11.5 to 24, which takes
    /// over from the small pyramid and carries it to the wide layout.
    /// </summary>
    /// <remarks>
    /// <code>
    /// pass          grid (source px per texel)   coefficients
    /// Quarter       1 -> 4    fixed 8-tap row     Quarter (constant)
    /// Halve         4 -> 8    3 fetches, then 2   Halve: the pyramid's 2x down to 16.1; from 16.6 the wide halve
    /// Gaussian X/Y  8         one row per parity  Gaussian (rebuilt per sigma)
    /// Up            8 -> 4    cubic B-spline      the pyramid's (SmallKernel.CubicUp)
    /// Final         4 -> 1    one bilinear fetch  (the wide layout's, at scale 4)
    /// </code>
    /// <para>
    /// A reduced output of scale R stops at the 1/R grid, as in the wide layout.
    /// </para>
    /// <para>
    /// The passes never change between 11.5 and 24. Blur width comes from the
    /// Gaussian alone: it gets the variance the request leaves after the
    /// other passes add theirs.
    /// </para>
    /// <para>
    /// From sigma 8 the pyramid has three levels, so its Gaussian runs on the
    /// same eighth grid as this one; this layout replaces its third level
    /// from 11.5 with fewer passes. The wide layout above 24 runs its
    /// Gaussian one level coarser. To match it, from 21 to 24 the Gaussian row
    /// crossfades with a virtual level: one more halve, Gaussian and 2x up,
    /// written out as one row on the eighth grid instead of drawn. The
    /// virtual halve is a 2x reduction, so even and odd eighth texels need
    /// mirrored rows.
    /// </para>
    /// Offsets and rows are in texels of the grid each pass reads, with
    /// texel centers at integers. Buffers are reused when sigma changes.
    /// </remarks>
    internal sealed class TransitionKernel
    {
        // Where the layout starts, where its halve starts and ends settling
        // into the binomial, where the crossfade toward the wide layout
        // starts, and where the layout ends. From 11.5 it is faster than the
        // pyramid's third level on every GPU measured and stays within 1% of
        // the Gaussian.
        internal const double StartSigma = 11.5;
        internal const double SettleStartSigma = 16.1;
        internal const double SettledSigma = 16.6;
        const double WideFadeSigma = 21;
        internal const double EndSigma = 24;

        // The pyramid's 2x down as the sampler runs it (SmallKernel.BinomialDown).
        const double PyramidOuterMass = SmallKernel.DownOuterMass;
        const double PyramidOuterFarFraction = SmallKernel.DownOuterFarFraction;
        // The binomial [1, 3, 3, 1] / 8 halve, used here from 16.6 and by the
        // wide layout: 1/8 on each outer texel.
        const double BinomialOuterMass = 0.125;
        // Fixed fetch fractions (in 1/256) of the virtual level's 2x up.
        // Fitted by Tools~/FilterFit/fit_transition.py to match the wide
        // layout at 24 without worsening the fade range.
        static readonly (int first, int second) WideUpFractions = (236, 49);

        // Quarter: [65, 191, 321, 447, 447, 321, 191, 65] / 2048 as four
        // fetches, offsets from the quarter texel's first source pixel. Each
        // source pixel phase (mod 4) gets a quarter of the weight, centered
        // within 1/128 pixel of the quarter texel's center, so a moving point
        // keeps its brightness and position. Fixed for every sigma.
        // Shaders/Wide.hlsl FragWideQuarter, which both layouts draw, inlines
        // these; change both.
        internal const double QuarterVariance = 2.7578125;
        internal static readonly Vector4[] Quarter =
        {
            new Vector4(-1.25390625f, 0.125f, 0, 0),
            new Vector4(0.58203125f, 0.375f, 0, 0),
            new Vector4(2.41796875f, 0.375f, 0, 0),
            new Vector4(4.25390625f, 0.125f, 0, 0)
        };
        // Halve: three fetches (offset, weight, 0, 0).
        internal readonly Vector4[] Halve = new Vector4[3];
        // Gaussian: the even nodes' row of up to PhaseCapacity fetches (offset,
        // weight, 0, 0); odd nodes negate its offsets. The rest of the array
        // stays zero; _TransitionGaussian[40] keeps its timed size.
        internal readonly Vector4[] Gaussian = new Vector4[2 * PhaseCapacity];
        internal int GaussianCount { get; private set; }
        internal const int PhaseCapacity = 20;
        /// <summary>Source pixels per texel of the Up's output, which Final magnifies.</summary>
        internal const int ReconstructionPitch = 4;

        /// <summary>0 to sigma 16.1, halving like the pyramid; 1 from 16.6, settled into this layout.</summary>
        internal double Settled { get; private set; }
        internal double GaussianVariance { get; private set; }

        const int Origin = 32;
        const int Length = 2 * Origin + 1;
        readonly double[] gaussianRow = new double[Length];
        readonly double[] virtualGaussianRow = new double[Length];
        readonly double[] eighthRow = new double[Length];
        // Packing scratch.
        readonly double[] positions = new double[Length];
        readonly double[] masses = new double[Length];
        float cachedSigma = float.NaN;
        int cachedOutputScale;

        /// <param name="outputScale">1 for full resolution, or a reduced output's scale: 2 or 4.</param>
        internal void Prepare(float sigma, int outputScale = 1)
        {
            if (!float.IsFinite(sigma) || sigma < StartSigma || sigma > EndSigma)
                throw new ArgumentOutOfRangeException(nameof(sigma));
            if (outputScale != 1 && outputScale != 2 && outputScale != ReconstructionPitch)
                throw new ArgumentOutOfRangeException(nameof(outputScale));
            if (sigma == cachedSigma && outputScale == cachedOutputScale)
                return;

            double squared = sigma * (double)sigma;
            Settled = Smooth((squared - SettleStartSigma * SettleStartSigma) /
                (SettledSigma * SettledSigma - SettleStartSigma * SettleStartSigma));
            double halveVariance = PrepareHalve();

            // Variance budget: the eighth-grid Gaussian supplies what the
            // other passes do not. Halve's variance is in quarter texels
            // (x 4²); the Gaussian's is in eighth texels (/ 8²).
            double reductionVariance = QuarterVariance + 16 * halveVariance;
            GaussianVariance = (RequestedVariance(sigma) -
                reductionVariance - ReconstructionVariance(outputScale)) / 64;
            // Its cutoff widens from four sigma to the wide layout's five.
            BuildGaussian(GaussianVariance, 4 + Settled, gaussianRow);

            // Crossfade with a virtual level shaped like the wide layout's.
            double virtualWeight = Smooth((squared - WideFadeSigma * WideFadeSigma) /
                (EndSigma * EndSigma - WideFadeSigma * WideFadeSigma));
            Array.Clear(eighthRow, 0, eighthRow.Length);
            if (virtualWeight > 0)
                AddVirtualLevel(eighthRow, Origin, virtualWeight, GaussianVariance, WideKernel.BinomialHalve,
                    WideUpFractions, 4 + Settled, virtualGaussianRow);
            for (int i = 0; i < Length; i++)
                eighthRow[i] += gaussianRow[i] * (1 - virtualWeight);
            PackGaussianRows();
            cachedSigma = sigma;
            cachedOutputScale = outputScale;
        }

        /// <summary>
        /// Fills <see cref="Halve"/>: an outer fetch, both center texels at
        /// 0.5, and the mirrored outer fetch. It moves from the pyramid's 2x
        /// down to the binomial as <see cref="Settled"/> goes from 0 to 1.
        /// </summary>
        /// <returns>The halve's variance in quarter texels².</returns>
        double PrepareHalve()
        {
            // Outer fetch: the blend's share on the far texel (the binomial
            // has none there), rounded. Its weight keeps the inner minus the
            // far texel at 1/8, which both ends obey, so a point stays
            // centered on either quarter-texel parity.
            double blended = PyramidOuterMass * (1 - Settled) + BinomialOuterMass * Settled;
            double farMass = PyramidOuterMass * (1 - Settled) * PyramidOuterFarFraction;
            double inner = Snap(1 - farMass / blended);
            double outer = 0.125 / (2 * inner - 1);
            double left = -2 + inner;
            Halve[0] = new Vector4((float)left, (float)outer, 0, 0);
            Halve[1] = new Vector4(0.5f, (float)(1 - 2 * outer), 0, 0);
            Halve[2] = new Vector4((float)(1 - left), (float)outer, 0, 0);
            // Second moment about texel 0, minus the center (0.5) squared.
            return outer * (BilinearPairs.SecondMoment(left) + BilinearPairs.SecondMoment(1 - left)) +
                (1 - 2 * outer) * 0.5 - 0.25;
        }

        /// <summary>
        /// Adds <paramref name="weight"/> × a virtual level (halve, Gaussian,
        /// 2x up) as seen by an even texel of the fine grid, written onto that
        /// grid: one more level than the layout draws, without drawing it.
        /// </summary>
        /// <param name="row">The fine grid's row, offset k at row[origin + k].</param>
        /// <param name="fineVariance">The fine grid Gaussian's variance, which the level replaces, in fine texels².</param>
        /// <param name="halve">The 2x down's weights on fine texels 2m - 2 .. 2m + 3 of coarse texel m.</param>
        /// <param name="fractions">The 2x up's two fetch fractions, in 1/256.</param>
        /// <param name="scratch">A row as long as <paramref name="row"/>.</param>
        internal static void AddVirtualLevel(double[] row, int origin, double weight, double fineVariance,
            double[] halve, (int first, int second) fractions, double cutoff, double[] scratch)
        {
            // Up: two fetches over coarse texels -2 .. +1. Their fractions are
            // fixed; their weights m and 1 - m put the center at -1/4, where
            // an even fine texel sits on the coarse grid:
            // m * f0 + (1 - m) * (2 + f1) = 2 - 1/4, measured from texel -2.
            double f0 = fractions.first / 256.0;
            double f1 = fractions.second / 256.0;
            double m = (f1 + 0.25) / (2 + f1 - f0);
            double up0 = m * (1 - f0), up1 = m * f0, up2 = (1 - m) * (1 - f1), up3 = (1 - m) * f1;
            double upVariance = up0 * 1.75 * 1.75 + up1 * 0.75 * 0.75 + up2 * 0.25 * 0.25 + up3 * 1.25 * 1.25;

            double halveVariance = 0;
            for (int i = 0; i < 6; i++)
                halveVariance += halve[i] * (i - 2.5) * (i - 2.5);

            // Gaussian: what the fine Gaussian's budget leaves, in coarse texels.
            BuildGaussian((fineVariance - halveVariance - 4 * upVariance) / 4, cutoff, scratch, origin);

            // Walk back: up tap u reads coarse texel u - 2; the Gaussian spread
            // coarse texel u - 2 + g into it; halve read that texel from six
            // fine texels.
            for (int u = 0; u < 4; u++)
            {
                double up = u == 0 ? up0 : u == 1 ? up1 : u == 2 ? up2 : up3;
                for (int g = -origin; g <= origin; g++)
                {
                    double upGaussian = up * scratch[g + origin] * weight;
                    if (upGaussian == 0)
                        continue;
                    for (int d = 0; d < 6; d++)
                    {
                        int node = 2 * (u - 2 + g) + d - 2;
                        double mass = upGaussian * halve[d];
                        if (mass == 0)
                            continue;
                        if (node < -origin || node > origin)
                            throw new InvalidOperationException("Virtual gaussian exceeds scratch capacity.");
                        row[node + origin] += mass;
                    }
                }
            }
        }

        /// <summary>Packs the even row into fetches; odd eighth texels read it mirrored.</summary>
        void PackGaussianRows()
        {
            Array.Clear(Gaussian, 0, Gaussian.Length);
            // Keep the even texels' first moment, so a point on an odd texel,
            // which reads the row mirrored, moves exactly as far.
            GaussianCount = BilinearPairs.Pack(eighthRow, Origin, Gaussian, 0, PhaseCapacity,
                positions, masses, evenMoment: true);
        }

        static void BuildGaussian(double variance, double cutoff, double[] row, int origin = Origin)
        {
            if (!(variance > 0))
                throw new InvalidOperationException("Working variance must be positive.");
            BlurMath.Gaussian(variance, cutoff, row, origin);
        }

        /// <summary>
        /// Variance Up and the bilinear magnifications to full resolution add,
        /// in source pixels², as the wide layout counts them.
        /// </summary>
        static double ReconstructionVariance(int outputScale)
            => 64 * SmallKernel.CubicUpVariance + WideKernel.MagnificationVariance(ReconstructionPitch, outputScale);
    }
}
