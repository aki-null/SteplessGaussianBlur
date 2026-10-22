using System;

namespace SteplessGaussianBlur
{
    /// <summary>
    /// Math shared by every kernel: the blur width each layout aims for, the
    /// tapered discrete Gaussian, the cubic B-spline, smoothstep and the
    /// sampler's 1/256-texel grid.
    /// </summary>
    /// <remarks>
    /// Blur width is tracked as variance (sigma²) because blurs run in
    /// sequence add their variances: a blur of sigma a followed by one of
    /// sigma b is a blur of sqrt(a² + b²). Every layout budgets with this.
    /// </remarks>
    internal static class BlurMath
    {
        /// <summary>Fixed-point resolution of bilinear interpolation weights.</summary>
        internal const int SamplerSubtexels = 256;

        const double LargeSigmaThreshold = 64;
        // Above sigma 64, RequestedVariance uses the continuous limit:
        // sigma² times this ratio, which the four-sigma taper causes. The
        // discrete sum differs from it by less than 1e-7 relatively.
        const double LargeSigmaVarianceFactor = 0.99716948283642;
        static readonly double VarianceAtLargeSigmaThreshold = TaperedVariance(LargeSigmaThreshold, 4);

        /// <summary>
        /// The blur width every layout aims for: the variance of the discrete
        /// Gaussian of this sigma, tapered to zero at four sigma.
        /// </summary>
        internal static double RequestedVariance(double sigma)
        {
            // A direct sum costs O(sigma), so wide blurs use the continuous
            // limit, anchored at the switch so the result stays continuous.
            if (sigma < 1e-5)
                return 0;
            if (sigma < LargeSigmaThreshold)
                return TaperedVariance(sigma, 4);
            return VarianceAtLargeSigmaThreshold +
                LargeSigmaVarianceFactor *
                (sigma * sigma - LargeSigmaThreshold * LargeSigmaThreshold);
        }

        /// <summary>Cubic smoothstep on [0, 1]: zero value and slope at both ends.</summary>
        internal static double Smooth(double x)
        {
            x = Math.Clamp(x, 0, 1);
            return x * x * (3 - 2 * x);
        }

        /// <summary>
        /// Rounds to the sampler's 1/256-texel grid, ties away from zero, so
        /// mirrored offsets stay mirrored: Snap(-x) == -Snap(x).
        /// </summary>
        internal static double Snap(double x) =>
            Math.Sign(x) * Math.Floor(Math.Abs(x) * SamplerSubtexels + 0.5) / SamplerSubtexels;

        /// <summary>
        /// Weight of the cubic B-spline at node <paramref name="node"/> of four:
        /// nodes floor - 1 to floor + 2 around a sample at fraction
        /// <paramref name="f"/> past node floor.
        /// </summary>
        internal static double CubicBSpline(int node, double f)
        {
            switch (node)
            {
                case 0:
                    return Math.Pow(1 - f, 3) / 6;
                case 1:
                    return (3 * f * f * f - 6 * f * f + 4) / 6;
                case 2:
                    return (-3 * f * f * f + 3 * f * f + 3 * f + 1) / 6;
                default:
                    return f * f * f / 6;
            }
        }

        // ---- Tapered Gaussian ------------------------------------------------
        // A hard cutoff would add a whole tap at some sigma, which shows as a
        // step when sigma animates. Instead the tail fades to zero over the
        // last half-sigma before the cutoff, so each new tap enters at zero.
        // Sampling and the taper make the row's variance differ from sigma²,
        // so builders solve for the spread that gives the requested variance.

        /// <summary>
        /// Gaussian weight at integer offset <paramref name="k"/>, faded to zero
        /// over the last half-spread before <paramref name="cutoff"/> spreads.
        /// </summary>
        internal static double TaperedWeight(int k, double spread, double cutoff)
        {
            double taper = Smooth((cutoff - Math.Abs(k) / spread) / 0.5);
            return Math.Exp(-0.5 * k * k / (spread * spread)) * taper;
        }

        /// <summary>
        /// The cutoff in spreads, narrowed where needed so the taper ends
        /// within <paramref name="extent"/> pixels.
        /// </summary>
        static double Cutoff(double spread, double cutoff, double extent) => Math.Min(cutoff, extent / spread);

        /// <summary>Last integer offset that can carry tapered weight.</summary>
        internal static int Reach(double spread, double cutoff) => Math.Max(1, (int)Math.Ceiling(cutoff * spread));

        /// <summary>Discrete variance of the normalized tapered Gaussian.</summary>
        internal static double TaperedVariance(double spread, double cutoff, double extent = double.PositiveInfinity)
        {
            if (spread < 1e-10)
                return 0;
            cutoff = Cutoff(spread, cutoff, extent);
            int reach = Reach(spread, cutoff);
            // Symmetric: sum one side and double it.
            double total = 0.5 * TaperedWeight(0, spread, cutoff);
            double secondMoment = 0;
            for (int k = 1; k <= reach; k++)
            {
                double weight = TaperedWeight(k, spread, cutoff);
                total += weight;
                secondMoment += k * k * weight;
            }
            return secondMoment / total;
        }

        /// <summary>Solves for the spread whose tapered Gaussian has this discrete variance.</summary>
        /// <remarks>
        /// Variance grows continuously and monotonically with spread, so the
        /// bracket always holds the answer. False position with the Illinois
        /// rule (halving the stale end's value) converges in about ten
        /// evaluations where bisection takes 48. Near zero variance the curve
        /// is flat and false position stalls, so it falls back to bisection.
        /// </remarks>
        internal static double SolveSpread(double variance, double cutoff, double extent = double.PositiveInfinity)
        {
            double lo = 0, hi = Math.Max(1, 2 * Math.Sqrt(variance));
            double fLo = -variance, fHi = TaperedVariance(hi, cutoff, extent) - variance;
            int side = 0;
            for (int i = 0; i < 100; i++)
            {
                double middle = i < 20 ? (lo * fHi - hi * fLo) / (fHi - fLo) : (lo + hi) * 0.5;
                double f = TaperedVariance(middle, cutoff, extent) - variance;
                if (Math.Abs(f) <= 1e-15 * variance || hi - lo <= 1e-14 * hi)
                    return middle;
                if (f < 0)
                {
                    lo = middle;
                    fLo = f;
                    if (side < 0)
                        fHi *= 0.5;
                    side = -1;
                }
                else
                {
                    hi = middle;
                    fHi = f;
                    if (side > 0)
                        fLo *= 0.5;
                    side = 1;
                }
            }
            return (lo + hi) * 0.5;
        }

        /// <summary>
        /// Fills <paramref name="weights"/>[origin + k] with the normalized tapered
        /// Gaussian of exactly this discrete variance and returns its reach.
        /// The taper ends within <paramref name="extent"/> pixels: a narrower
        /// cutoff, solved with the spread, keeps that offset at zero weight.
        /// </summary>
        internal static int Gaussian(double variance, double cutoff, double[] weights, int origin,
            double extent = double.PositiveInfinity)
        {
            Array.Clear(weights, 0, weights.Length);
            if (variance < 1e-12)
            {
                weights[origin] = 1;
                return 0;
            }
            double spread = SolveSpread(variance, cutoff, extent);
            cutoff = Cutoff(spread, cutoff, extent);
            int reach = Reach(spread, cutoff);
            if (reach > origin || origin + reach >= weights.Length)
                throw new InvalidOperationException("Gaussian support exceeds workspace capacity.");
            double sum = 0;
            for (int k = -reach; k <= reach; k++)
            {
                double weight = TaperedWeight(k, spread, cutoff);
                weights[origin + k] = weight;
                sum += weight;
            }
            for (int k = -reach; k <= reach; k++)
                weights[origin + k] /= sum;
            return reach;
        }
    }
}
