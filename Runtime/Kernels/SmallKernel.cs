using System;
using UnityEngine;
using static SteplessGaussianBlur.BlurMath;

namespace SteplessGaussianBlur
{
    /// <summary>
    /// Filters of the small layouts, sigma below 16.1 (Layouts/SmallLayout.cs).
    /// Each filter is a 1D row; the shaders run it on X and then on Y.
    /// </summary>
    /// <remarks>
    /// <code>
    /// sigma 0 ......... 1.62 ................... 2.40 ......................... 16.1
    ///       Gaussian      Down -> [Gaussian] -> Up    Down -> ... Gaussian ... -> Up
    ///       (full res)    FittedDownUp (half res)     pyramid: binomial down, cubic up
    /// </code>
    /// <para>
    /// Crossfade before switching. Shortly before each switch, the Gaussian
    /// row crossfades into the next layout's chain of passes, written out as
    /// one row of source-pixel weights. Crossfading weights gives the same
    /// image as crossfading the two layouts' outputs, without running both.
    /// At the switch the row equals the next layout's chain, so only the
    /// passes change, not the image.
    /// </para>
    /// <para>
    /// After a 2x reduction, even and odd output pixels sit at different
    /// places on the coarse grid, so a row that contains one is built once
    /// per output parity.
    /// </para>
    /// Units are variance in source pixels² and offsets in source pixels.
    /// </remarks>
    internal static class SmallKernel
    {
        internal const int MaxFetches = 16;

        /// <summary>What a Gaussian stage's row crossfades into.</summary>
        internal enum Crossfade
        {
            /// <summary>Nothing: a plain tapered Gaussian.</summary>
            None,
            /// <summary>Full resolution, sigma 1.46 to 1.62: FittedDownUp's Down then Up.</summary>
            IntoDownUp,
            /// <summary>A pyramid level's Gaussian: one more pyramid level.</summary>
            IntoNextLevel
        }

        // Down/Up passes take over where the fitted Up gains its fourth tap.
        // Below that, the row crossfading into them fits five fetches.
        internal const double DownUpPassesStart = FittedDownUp.SplitVariance;
        // The full-res cutoff narrows just enough to keep offset 5 at zero
        // weight: a sixth fetch takes the eight-fetch pass, 47% slower at
        // sigma 1.3 on M4. Narrowing more would add error.
        const double DownUpExtent = 5;
        // From here rows pair texels by output parity, often one fetch fewer.
        // Below it the center keeps its own fetch, so the variance stays
        // reachable with nonnegative weights. Both pairings give the same row
        // at this sigma.
        internal const double ParityPairingSigma = 0.740902;
        internal static readonly double ParityPairingStart = RequestedVariance(ParityPairingSigma);

        // ---- Pyramid levels ----------------------------------------------------
        // A level is a binomial 2x down, then a Gaussian or more levels at
        // half resolution, then a cubic B-spline 2x up. A level crossfades
        // into one more level while its coarse Gaussian would grow from
        // sigma 0.4 to 0.55.
        //
        // The sampler holds fetch positions to 1/256 texel, so the down and up
        // below are the binomial and the B-spline as the sampler runs them,
        // and every budget uses their exact weights.

        // Down: [1, 5, 10, 10, 5, 1] / 32 as three fetches. The outer fetch,
        // (1, 5) / 32, would sit 5/6 of the way to its inner texel; the
        // sampler holds it at 213/256, leaving 43/256 of it on the far texel.
        // Its weight, near 3/16, keeps the inner minus the far texel at 1/8,
        // so a point stays centered on either source parity.
        internal const double DownOuterFarFraction = 43.0 / 256;
        internal const double DownOuterMass = 0.125 / (1 - 2 * DownOuterFarFraction);
        internal static readonly Vector4[] BinomialDown =
        {
            new Vector4(-2.5f + 213f / 256f, (float)DownOuterMass, 0, 0),
            new Vector4(0, (float)(1 - 2 * DownOuterMass), 0, 0),
            new Vector4(2.5f - 213f / 256f, (float)DownOuterMass, 0, 0)
        };
        // The same down's six weights at source pixels 2j-2 .. 2j+3.
        internal static readonly double[] Binomial =
        {
            DownOuterMass * DownOuterFarFraction, DownOuterMass * (1 - DownOuterFarFraction), 0.5 - DownOuterMass,
            0.5 - DownOuterMass, DownOuterMass * (1 - DownOuterFarFraction), DownOuterMass * DownOuterFarFraction
        };
        // Its variance in source pixels², close to the binomial's 5/4.
        internal const double DownVariance = 2 * DownOuterMass *
            (6.25 * DownOuterFarFraction + 2.25 * (1 - DownOuterFarFraction)) + (1 - 2 * DownOuterMass) * 0.25;

        // Up: an even output pixel sits a quarter texel left of a half-res
        // center. The B-spline's (1, 121, 235, 27) / 384 on texels -2 .. +1
        // become two fetches, at fractions 121/122 and 27/262, which the
        // sampler holds at 254/256 and 26/256. Their weights are solved so
        // the fetches keep unit total and the -1/4 center. Odd pixels mirror.
        const float CubicUpLeft = -2 + 254f / 256;
        const float CubicUpRight = 26f / 256;
        const float CubicUpRightWeight = (-0.25f - CubicUpLeft) / (CubicUpRight - CubicUpLeft);
        internal static readonly Vector4[] CubicUp =
        {
            new Vector4(CubicUpLeft, 1 - CubicUpRightWeight, 0, 0),
            new Vector4(CubicUpRight, CubicUpRightWeight, 0, 0),
            new Vector4(-CubicUpRight, CubicUpRightWeight, 0, 0),
            new Vector4(-CubicUpLeft, 1 - CubicUpRightWeight, 0, 0)
        };
        // Its variance in half-res pixels² (close to the B-spline's 1/3).
        internal const double CubicUpVariance =
            (1 - CubicUpRightWeight) * (CubicUpLeft * CubicUpLeft + 254.0 / 256 * (2.0 / 256)) +
            CubicUpRightWeight * (CubicUpRight * CubicUpRight + 26.0 / 256 * (230.0 / 256)) - 1.0 / 16;

        /// <summary>Variance the down and up of one level add, in source pixels².</summary>
        internal const double LevelVariance = DownVariance + 4 * CubicUpVariance;
        internal const double NextLevelFadeStart = LevelVariance + 4.0 * 0.4 * 0.4;
        internal const double NextLevelFadeEnd = LevelVariance + 4.0 * 0.55 * 0.55;

        /// <summary>
        /// The variance a level leaves for its half-resolution part, in half-res
        /// pixels²: a half-res pixel is twice as wide, so a quarter as much.
        /// </summary>
        internal static double CoarseVariance(double variance) => (variance - LevelVariance) / 4;

        // Rows map source-pixel offset k to row[RowOrigin + k].
        const int RowOrigin = 32;
        const int RowLength = RowOrigin * 2 + 1;
        const int GaussianOrigin = 16;
        const int GaussianLength = GaussianOrigin * 2 + 1;

        /// <summary>Scratch storage reused while recording.</summary>
        internal sealed class Workspace
        {
            internal readonly double[] row = new double[RowLength];
            internal readonly double[] gaussian = new double[GaussianLength];
            internal readonly double[] coarseRow = new double[RowLength];
            internal readonly Vector4[] taps = new Vector4[MaxFetches * 2];
            internal readonly Vector4[] coarseTaps = new Vector4[MaxFetches * 2];
            // The outermost Up, which inner levels' taps must not overwrite.
            internal readonly Vector4[] outerTaps = new Vector4[4];
            internal readonly double[] positions = new double[MaxFetches];
            internal readonly double[] weights = new double[MaxFetches];
        }

        // ---- Gaussian stages ------------------------------------------------------

        /// <summary>
        /// Builds one Gaussian stage into workspace.taps: fetches for even
        /// output pixels at [0, MaxFetches), odd at [MaxFetches, 2 * MaxFetches).
        /// Returns fetches per axis (the larger of the two).
        /// </summary>
        internal static int GaussianRows(Workspace workspace, double variance, Crossfade crossfade = Crossfade.None)
            => GaussianRows(workspace, variance, crossfade, workspace.taps);

        static int GaussianRows(Workspace workspace, double variance, Crossfade crossfade, Vector4[] taps)
        {
            double fade = 0;
            FittedDownUp downUp = default;
            int coarseCount = 0;
            if (crossfade == Crossfade.IntoDownUp)
            {
                fade = Smooth((variance - FittedDownUp.Start) / (DownUpPassesStart - FittedDownUp.Start));
                if (fade > 0)
                    downUp = FittedDownUp.At(variance);
            }
            else if (crossfade == Crossfade.IntoNextLevel)
            {
                fade = Smooth((variance - NextLevelFadeStart) / (NextLevelFadeEnd - NextLevelFadeStart));
                // The next level's own Gaussian, as its passes will run it.
                // Its variance is below NextLevelFadeStart, so it is plain.
                // Built first: it reuses workspace.gaussian.
                if (fade > 0)
                    coarseCount = GaussianRows(workspace, CoarseVariance(variance), Crossfade.None,
                        workspace.coarseTaps);
            }

            double extent = crossfade == Crossfade.IntoDownUp ? DownUpExtent : double.PositiveInfinity;
            int reach = BlurMath.Gaussian(variance, 4, workspace.gaussian, GaussianOrigin, extent);

            Array.Clear(taps, 0, taps.Length);
            int count = 0;
            for (int parity = 0; parity < 2; parity++)
            {
                double[] row = workspace.row;
                Array.Clear(row, 0, row.Length);
                for (int k = -reach; k <= reach; k++)
                    Add(row, k, (1 - fade) * workspace.gaussian[GaussianOrigin + k]);
                if (fade > 0)
                {
                    if (crossfade == Crossfade.IntoDownUp)
                        AddDownUp(row, downUp, parity, fade);
                    else
                        AddNextLevel(row, workspace.coarseTaps, coarseCount, parity, fade);
                }
                int pairing = variance >= ParityPairingStart ? parity : -1;
                count = Math.Max(count, BilinearPairs.Pack(row, RowOrigin, taps, parity * MaxFetches, MaxFetches,
                    workspace.positions, workspace.weights, pairing));
            }
            return count;
        }

        static void Add(double[] row, int offset, double weight)
        {
            if (weight <= 0)
                return;
            int index = RowOrigin + offset;
            if ((uint)index >= RowLength)
                throw new InvalidOperationException("Blur support exceeds row capacity.");
            row[index] += weight;
        }

        // Adds weight × one bilinear fetch at this offset, split between its two texels.
        static void AddFetch(double[] row, double position, double weight)
        {
            int first = (int)Math.Floor(position);
            double fraction = position - first;
            Add(row, first, weight * (1 - fraction));
            Add(row, first + 1, weight * fraction);
        }

        // ---- Expanded chains ---------------------------------------------------------
        // Each adds `weight` times a chain of passes, as seen by output pixel
        // x (0 or 1): it walks back from the output through each pass's taps,
        // multiplying their weights, down to the source pixels they read. The
        // taps are the ones the passes run, so at the end of a crossfade the
        // row equals the chain that replaces it.

        /// <summary>Adds FittedDownUp's Down then Up.</summary>
        static void AddDownUp(double[] row, in FittedDownUp downUp, int x, double weight)
        {
            for (int i = 0; i < 4; i++)
            {
                // Up texel weight i reads half-res texel i - 2 (mirrored for odd x).
                int coarse = (x & 1) == 0 ? i - 2 : 2 - i;
                // That texel's Down read source pixels 2 * coarse - 2 .. + 3.
                for (int h = -2; h <= 3; h++)
                {
                    int source = 2 * coarse + h;
                    Add(row, source - x, weight * downUp.UpWeight(i) * downUp.DownWeight(h));
                }
            }
        }

        /// <summary>Adds one pyramid level: binomial down, the coarse Gaussian, cubic up.</summary>
        /// <param name="coarseTaps">The coarse Gaussian's fetches, both parities.</param>
        static void AddNextLevel(double[] row, Vector4[] coarseTaps, int coarseCount, int x, double weight)
        {
            // Up: output pixel x reads around half-res texel 0 (x is 0 or 1).
            for (int u = 0; u < 2; u++)
            {
                Vector4 up = CubicUp[(x & 1) * 2 + u];
                int upFirst = (int)Math.Floor(up.x);
                double upFraction = up.x - upFirst;
                for (int side = 0; side < 2; side++)
                {
                    int coarse = upFirst + side;
                    double upWeight = up.y * (side == 0 ? 1 - upFraction : upFraction);
                    if (upWeight == 0)
                        continue;
                    // The coarse Gaussian at texel `coarse` reads its parity's row ...
                    int start = (coarse & 1) * MaxFetches;
                    for (int g = 0; g < coarseCount; g++)
                    {
                        Vector4 tap = coarseTaps[start + g];
                        if (tap.y == 0)
                            continue;
                        int gaussianFirst = (int)Math.Floor(coarse + (double)tap.x);
                        double gaussianFraction = coarse + (double)tap.x - gaussianFirst;
                        for (int gside = 0; gside < 2; gside++)
                        {
                            int blurred = gaussianFirst + gside;
                            double gaussianWeight = tap.y * (gside == 0 ? 1 - gaussianFraction : gaussianFraction);
                            if (gaussianWeight == 0)
                                continue;
                            // ... and each texel it reads, the down read from six source pixels.
                            for (int h = 0; h < 6; h++)
                            {
                                int source = 2 * blurred + h - 2;
                                Add(row, source - x, weight * upWeight * gaussianWeight * Binomial[h]);
                            }
                        }
                    }
                }
            }
        }

        // ---- Outermost upsample ------------------------------------------------------
        // From sigma 4.25 to 4.5 the outermost cubic up crossfades into
        // bilinear: one fetch instead of four at full resolution. The image is
        // smooth enough by then. The blend starts after the first added
        // level has crossfaded in (4.22), so it leaves that crossfade, the
        // small layout's largest error from sigma 2.4, unchanged. Inner
        // levels stay cubic.

        internal const double OuterBilinearStartSigma = 4.25;
        internal const double OuterBilinearEndSigma = 4.5;

        /// <summary>How far the outermost upsample has turned bilinear: 0 below sigma 4.25, 1 from 4.5.</summary>
        internal static double OuterUpsampleBilinear(double sigma) =>
            Smooth((sigma * sigma - OuterBilinearStartSigma * OuterBilinearStartSigma) /
                (OuterBilinearEndSigma * OuterBilinearEndSigma - OuterBilinearStartSigma * OuterBilinearStartSigma));

        /// <summary>
        /// The variance to give the outermost level when its Up adds
        /// <paramref name="upVariance"/> (half-res pixels²) instead of the
        /// cubic Up's, which <see cref="LevelVariance"/> budgets.
        /// </summary>
        internal static double OuterLevelVariance(double variance, double upVariance) =>
            variance + 4 * (CubicUpVariance - upVariance);

        /// <summary>
        /// Packs (1 - bilinear) × cubic + bilinear × bilinear into two fetches
        /// per parity, on the sampler's grid, with unit total and the ∓1/4
        /// center, into workspace.outerTaps. Returns the variance it adds, in
        /// half-res pixels².
        /// </summary>
        internal static double OuterUpsample(Workspace workspace, double bilinear)
        {
            double variance = 0;
            for (int phase = 0; phase < 2; phase++)
            {
                double[] row = workspace.row;
                Array.Clear(row, 0, row.Length);
                for (int k = 0; k < 2; k++)
                {
                    Vector4 tap = CubicUp[phase * 2 + k];
                    AddFetch(row, tap.x, (1 - bilinear) * tap.y);
                }
                double center = phase == 0 ? -0.25 : 0.25;
                AddFetch(row, center, bilinear);
                // Pair the same texels as cubic up, so the draw stays the
                // same throughout. Pure bilinear has its own one-fetch pass.
                int start = phase == 0 ? -2 : -1;
                int texel = RowOrigin + start;
                variance += BilinearPairs.CenteredPair(start, row[texel], row[texel + 1], row[texel + 2],
                    row[texel + 3], center, out double left, out double right, out double rightWeight) / 2;
                workspace.outerTaps[phase * 2] = new Vector4((float)left, (float)(1 - rightWeight), 0, 0);
                workspace.outerTaps[phase * 2 + 1] = new Vector4((float)right, (float)rightWeight, 0, 0);
            }
            return variance;
        }
    }
}
