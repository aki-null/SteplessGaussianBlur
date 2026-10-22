using System;
using UnityEngine;
using static SteplessGaussianBlur.BlurMath;

namespace SteplessGaussianBlur
{
    /// <summary>
    /// Coefficients of the wide layout, sigma 24 and above.
    /// </summary>
    /// <remarks>
    /// <code>
    /// pass          grid (source px per texel)        coefficients
    /// Quarter       1 -> 4          fixed 8-tap row   TransitionKernel.Quarter (in the shader)
    /// Halve x n     4 -> ... -> P   binomial [1,3,3,1]/8 (in the shader)
    /// Gaussian X/Y  P               tapered Gaussian  Gaussian (rebuilt per sigma)
    /// Cubic         P -> P/4        cubic B-spline    Cubic (constant, four phases)
    /// Final         P/4 -> 1        one bilinear fetch (in the shader)
    /// </code>
    /// <para>
    /// A reduced output of scale R stops at the 1/R grid. Cubic writes it
    /// directly when R is P/4; otherwise Final magnifies to it by P/4R. The
    /// viewer's bilinear filtering then does the rest, and the budget counts it.
    /// </para>
    /// <para>
    /// P is the pitch: 16 below sigma 70, doubling at 70, 140, 280 ... so the
    /// Gaussian stays short. Everything but the Gaussian is fixed for a given
    /// pitch, so blur width is one number: the Gaussian's variance, which is
    /// what the request leaves after the other passes.
    /// </para>
    /// <para>
    /// Over the last eighth of sigma before each doubling, the Gaussian row
    /// crossfades with a virtual level at the next pitch (one more halve, a
    /// Gaussian, a 2x up), written out on this grid, as the transition does
    /// toward this layout. Even and odd texels then need mirrored rows. At
    /// the switch the two layouts still differ in their reconstruction.
    /// </para>
    /// </remarks>
    internal sealed class WideKernel
    {
        // Gaussian: the even texels' row of up to PhaseCapacity fetches (first
        // texel offset, bilinear fraction, weight, 0); odd texels mirror it.
        // The rest of the array stays zero; _WideGaussian[64] keeps its timed
        // size.
        internal const int PhaseCapacity = 32;
        internal readonly Vector4[] Gaussian = new Vector4[2 * PhaseCapacity];
        // Cubic: four phases, each (left offset, right offset, left weight, right weight).
        internal readonly Vector4[] Cubic = new Vector4[4];
        internal int GaussianCount { get; private set; }
        internal int Pitch { get; private set; }
        /// <summary>Source pixels per output texel: 1, or a reduced output's scale.</summary>
        internal int OutputScale { get; private set; }
        /// <summary>Source pixels per texel of the cubic's output, which Final magnifies.</summary>
        internal int ReconstructionPitch => Pitch / 4;
        internal double GaussianVariance { get; private set; }

        const int BasePitch = 16;
        internal const double FirstDoublingSigma = 70;
        // The crossfade toward the next pitch starts at this share of its sigma.
        const double PitchFadeStart = 0.875;
        // Fixed fetch fractions (in 1/256) of the virtual level's 2x up toward
        // the switches at 70, 140, 280, then 560 and above. Fitted by
        // Tools~/FilterFit/fit_transition.py to match the next pitch at each switch.
        static readonly (int first, int second)[] PitchUpFractions = { (221, 36), (219, 38), (220, 45), (221, 48) };
        // The binomial [1, 3, 3, 1] / 8 halve on fine texels 2m - 2 .. 2m + 3.
        internal static readonly double[] BinomialHalve = { 0, 0.125, 0.375, 0.375, 0.125, 0 };
        // A five-sigma taper avoids the square-looking tails of a shorter
        // cutoff along each axis.
        const double GaussianCutoff = 5;
        const int GaussianOrigin = 40;
        readonly double[] row = new double[2 * GaussianOrigin + 1];
        readonly double[] virtualRow = new double[2 * GaussianOrigin + 1];
        readonly Vector4[] packed = new Vector4[PhaseCapacity];
        readonly double[] positions = new double[PhaseCapacity];
        readonly double[] masses = new double[PhaseCapacity];
        // Cubic's variance in its input texels², averaged over its four phases.
        readonly double cubicVariance;
        float cachedSigma = float.NaN;
        int cachedOutputScale;

        internal WideKernel()
        {
            cubicVariance = PrepareCubic(Cubic);
        }

        /// <param name="outputScale">1 for full resolution, or a reduced output's scale, at most P/4.</param>
        internal void Prepare(float sigma, int outputScale = 1)
        {
            if (!float.IsFinite(sigma) || sigma < TransitionKernel.EndSigma)
                throw new ArgumentOutOfRangeException(nameof(sigma));
            if (sigma == cachedSigma && outputScale == cachedOutputScale)
                return;

            // Validate before assigning, so a throw leaves the previous state whole.
            int pitch = PitchFor(sigma, out double nextDoubling, out int doublings);
            if (sigma >= nextDoubling)
                throw new ArgumentOutOfRangeException(nameof(sigma), "Working pitch exceeds integer capacity.");
            if (outputScale < 1 || outputScale > pitch / 4 || (outputScale & (outputScale - 1)) != 0)
                throw new ArgumentOutOfRangeException(nameof(outputScale));

            // Variance budget, in source pixels², converted to pitch texels².
            double pitchSquared = (double)pitch * pitch;
            double gaussianVariance = (RequestedVariance(sigma) - ReductionVariance(pitch) -
                ReconstructionVariance(pitch, cubicVariance, outputScale)) / pitchSquared;
            if (gaussianVariance <= 0)
                throw new InvalidOperationException("Wide Gaussian variance must be positive.");
            Pitch = pitch;
            OutputScale = outputScale;
            GaussianVariance = gaussianVariance;

            double fadeStart = PitchFadeStart * nextDoubling;
            double squared = sigma * (double)sigma;
            double virtualWeight = Smooth((squared - fadeStart * fadeStart) /
                (nextDoubling * nextDoubling - fadeStart * fadeStart));
            GaussianCount = PrepareGaussian(GaussianVariance, virtualWeight,
                PitchUpFractions[Math.Min(doublings, PitchUpFractions.Length - 1)]);
            cachedSigma = sigma;
            cachedOutputScale = outputScale;
        }

        /// <summary>The working pitch at this sigma, at most 2^29.</summary>
        internal static int PitchFor(float sigma) => PitchFor(sigma, out _, out _);

        // Stops below the next doubling, or at 2^29, where sigma can still
        // be at or past it.
        static int PitchFor(float sigma, out double nextDoubling, out int doublings)
        {
            int pitch = BasePitch;
            nextDoubling = FirstDoublingSigma;
            doublings = 0;
            for (; sigma >= nextDoubling && pitch < 1 << 29; nextDoubling *= 2, doublings++)
                pitch *= 2;
            return pitch;
        }

        /// <summary>Variance of Quarter and every Halve down to the pitch.</summary>
        static double ReductionVariance(int pitch)
        {
            // Each binomial halve adds 3/4 of its input texel size squared.
            double variance = TransitionKernel.QuarterVariance;
            for (int inputPitch = 4; inputPitch < pitch; inputPitch *= 2)
                variance += 0.75 * inputPitch * inputPitch;
            return variance;
        }

        /// <summary>
        /// Variance of Cubic and the bilinear magnifications to full
        /// resolution, averaged over full-resolution pixels. A reduced output
        /// leaves the last magnification to the viewer.
        /// </summary>
        static double ReconstructionVariance(int pitch, double cubicVariance, int outputScale)
        {
            // Cubic: its own variance, scaled from pitch texels. Then Final
            // magnifies by pitch / 4R onto the output grid (none when R is
            // pitch / 4), and bilinear sampling magnifies by R to full
            // resolution. Each is in its own output texels, R or 1 pixels.
            return (double)pitch * pitch * cubicVariance + MagnificationVariance(pitch / 4, outputScale);
        }

        /// <summary>
        /// Variance of magnifying a grid of <paramref name="pitch"/> source
        /// pixels per texel to full resolution: Final by pitch / R onto the
        /// output grid, then the viewer's bilinear sampling by R.
        /// </summary>
        internal static double MagnificationVariance(int pitch, int outputScale)
            => (double)outputScale * outputScale * Magnification(pitch / outputScale) + Magnification(outputScale);

        /// <summary>
        /// Variance a bilinear magnification by a power of two s adds, in its
        /// output texels², averaged over its phases: (s² - 1) / 6 + 1/4.
        /// </summary>
        static double Magnification(int scale) => scale == 1 ? 0 : ((double)scale * scale - 1) / 6 + 0.25;

        /// <summary>
        /// Fills <paramref name="cubic"/>: for each of four output phases of a 4x
        /// upsample, at (phase + 0.5) / 4 - 0.5 input texels from the input
        /// texel's center, the cubic B-spline's four weights as two fetches.
        /// The two fetch weights are then solved for unit total and the exact
        /// center, despite snapped offsets. Returns the variance it adds,
        /// averaged over the phases, in input texels².
        /// </summary>
        internal static double PrepareCubic(Vector4[] cubic)
        {
            double variance = 0;
            for (int phase = 0; phase < 4; phase++)
            {
                double position = (phase + 0.5) / 4 - 0.5;
                int first = (int)Math.Floor(position) - 1;
                double t = position - Math.Floor(position);
                double a = CubicBSpline(0, t);
                double b = CubicBSpline(1, t);
                double c = CubicBSpline(2, t);
                double d = CubicBSpline(3, t);
                variance += BilinearPairs.CenteredPair(first, a, b, c, d, position,
                    out double left, out double right, out double weight);
                if (weight < 0 || weight > 1)
                    throw new InvalidOperationException("Cubic pair weight must be nonnegative.");
                cubic[phase] = new Vector4((float)left, (float)right,
                    (float)(1 - weight), (float)weight);
            }
            return variance / 4;
        }

        int PrepareGaussian(double variance, double virtualWeight, (int first, int second) fractions)
        {
            BlurMath.Gaussian(variance, GaussianCutoff, row, GaussianOrigin);
            if (virtualWeight > 0)
            {
                for (int i = 0; i < row.Length; i++)
                    row[i] *= 1 - virtualWeight;
                TransitionKernel.AddVirtualLevel(row, GaussianOrigin, virtualWeight, variance, BinomialHalve,
                    fractions, GaussianCutoff, virtualRow);
            }
            // Keep the even texels' first moment, so a point on an odd texel,
            // which reads the row mirrored, moves exactly as far.
            int count = BilinearPairs.Pack(row, GaussianOrigin, packed, 0, PhaseCapacity, positions, masses,
                evenMoment: true);
            Array.Clear(Gaussian, 0, Gaussian.Length);
            for (int i = 0; i < count; i++)
                Gaussian[i] = Fetch(packed[i].x, packed[i].y);
            return count;
        }

        // A fetch as (first texel, fraction toward the next, weight).
        static Vector4 Fetch(float position, float weight)
        {
            float first = (float)Math.Floor(position);
            return new Vector4(first, position - first, weight, 0);
        }
    }
}
