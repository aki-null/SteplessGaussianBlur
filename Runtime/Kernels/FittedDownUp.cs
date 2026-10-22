using System;
using UnityEngine;
using static SteplessGaussianBlur.BlurMath;

namespace SteplessGaussianBlur
{
    /// <summary>
    /// The fitted 2x downsample/upsample pair of sigma 1.46 to 2.40,
    /// evaluated at one strength.
    /// </summary>
    /// <remarks>
    /// <code>
    /// source --Down (6 taps)--> half res --[Gaussian, from 1.72]--> --Up (4 taps)--> output
    /// </code>
    /// <para>
    /// A plain 2x down/up pair blurs by one fixed amount. To make the blur
    /// grow smoothly, these weights move along Bézier curves fitted offline
    /// (Documentation~/Design.md). At sigma 2.40 they arrive at the
    /// pyramid's binomial down and cubic B-spline up, so the pyramid takes
    /// over without a jump.
    /// </para>
    /// <para>
    /// The curves are driven by a curve position p, not by sigma. p is
    /// close to the variance the pair produces but not equal to it, so
    /// <see cref="At"/> looks p up in an inverse table first.
    /// </para>
    /// Tools~/FilterFit/fit_down_up.py fits the curves and writes their
    /// numbers to FittedDownUp.Generated.cs.
    /// </remarks>
    internal readonly partial struct FittedDownUp
    {
        // The generated knots are curve positions p:
        //   Start, End            the fitted range; at End the pair equals one pyramid level
        //   UpsampleSplit         Up's second curve begins
        //   HalfResGaussianStart  the half-res Gaussian fades in, holds from
        //   HalfResGaussianHold   at HalfResGaussianHoldShare of what a level would leave it,
        //   HalfResGaussianRise   then rises to all of it at End

        // Down has three free weights (it is symmetric). Two rules fix two:
        // they sum to one, and down1 - down2 = 1/8, which keeps a moving
        // point centered whether it lands on an even or an odd source pixel.
        // DownBase, (1/6, 11/48, 5/48), obeys both. Sliding along DownSlide,
        // (-2, 1, 1) / sqrt(6), moves weight from the center taps to the outer
        // taps without breaking either rule; DownSlideControls pick how far.

        // Up is two Bézier curves, UpControlsBeforeSplit and
        // UpControlsAfterSplit (one row per control point, columns up0..up3).
        // Before sigma 1.62, up0 stays zero: Down then Up spans five source
        // pixels per axis, so the crossfaded row below 1.62 fits in five
        // fetches (see SplitVariance). The second curve ends at the cubic B-spline.

        /// <summary>The curves' input p for this strength.</summary>
        internal readonly double curvePosition;

        /// <summary>
        /// Down's fitted weights at source distance 0.5, 1.5 and 2.5 from the
        /// half-res texel's center, which sits between source pixels 2j and 2j + 1.
        /// </summary>
        internal readonly double down0, down1, down2;

        /// <summary>
        /// Up's fitted weights for an even output pixel x, of the half-res texels
        /// at offsets -2, -1, 0 and +1 from floor(x / 2). Odd pixels use the mirror image.
        /// </summary>
        internal readonly double up0, up1, up2, up3;

        /// <summary>
        /// Variance of the half-res Gaussian between Down and Up, in half-res
        /// pixels². Zero below sigma 1.72 (curve position HalfResGaussianStart).
        /// </summary>
        internal readonly double halfResGaussianVariance;

        // The fetches the passes run. Down: an outer fetch on each side at
        // downOuterPosition (from the center) with weight downOuterMass, and
        // both center pixels in one fetch. Up, for even output pixels: fetches
        // at upLeft and upRight with weights 1 - upRightWeight and upRightWeight.
        readonly double downOuterPosition, downOuterMass;
        readonly double upLeft, upRight, upRightWeight;

        internal static FittedDownUp At(double variance) => new FittedDownUp(CurvePosition(variance), variance);

        FittedDownUp(double p, double variance)
        {
            curvePosition = p;

            // Down: slide along the fitted curve, then ease into the
            // pyramid's binomial by End.
            double slide = Bezier(DownSlideControls, Progress(p, Start, HalfResGaussianStart));
            double toBinomial = Smooth((p - HalfResGaussianStart) / (End - HalfResGaussianStart));
            down0 = Down(0, slide, toBinomial);
            down1 = Down(1, slide, toBinomial);
            down2 = Down(2, slide, toBinomial);

            // Up: two curve segments joined at UpsampleSplit.
            bool beforeSplit = p < UpsampleSplit;
            double[,] controls = beforeSplit ? UpControlsBeforeSplit : UpControlsAfterSplit;
            double x = beforeSplit ? Progress(p, Start, UpsampleSplit) : Progress(p, UpsampleSplit, End);
            up0 = Bezier(controls, 0, x);
            up1 = Bezier(controls, 1, x);
            up2 = Bezier(controls, 2, x);
            up3 = Bezier(controls, 3, x);

            // Half-res Gaussian: a pyramid level would give it the variance
            // left after its own down/up. Take a share of that: 0 below
            // HalfResGaussianStart, eased to HalfResGaussianHoldShare (the fit's
            // optimum) by HalfResGaussianHold, then eased to all of it from
            // HalfResGaussianRise to End, where this becomes a level.
            double coarseVariance = Math.Max(SmallKernel.CoarseVariance(p), 0);
            double fadeIn = Smooth((p - HalfResGaussianStart) / (HalfResGaussianHold - HalfResGaussianStart));
            double share = HalfResGaussianHoldShare + (1 - HalfResGaussianHoldShare) *
                Smooth((p - HalfResGaussianRise) / (End - HalfResGaussianRise));
            halfResGaussianVariance = coarseVariance * fadeIn * share;

            // Up as the sampler runs it: each pair's fraction rounded to
            // 1/256, the two weights solved for unit total and the -1/4 center.
            double upVariance = BilinearPairs.CenteredPair(-2, up0, up1, up2, up3, -0.25,
                out upLeft, out upRight, out upRightWeight);

            // Rounding Up's fractions and the inverse table's interpolation
            // leave the chain's variance slightly off. The half-res Gaussian
            // takes up the difference as it fades in (HalfResGaussianStart to Hold);
            // before that, Down does, by moving weight between its outer and
            // center fetches. That moves a point on either source parity by
            // under 0.015 pixel.
            double inner = BilinearPairs.SnappedFraction(down2, down1);
            downOuterPosition = -2.5 + inner;
            double outerSecond = 6.25 * (1 - inner) + 2.25 * inner;
            double fittedOuterMass = down1 + down2;
            double missing = variance - 4 * halfResGaussianVariance - 4 * upVariance -
                (2 * fittedOuterMass * outerSecond + (1 - 2 * fittedOuterMass) * 0.25);
            halfResGaussianVariance += fadeIn * missing / 4;
            downOuterMass = fittedOuterMass + (1 - fadeIn) * missing / (2 * (outerSecond - 0.25));
            if (!(downOuterMass >= 0 && downOuterMass <= 0.5 && halfResGaussianVariance >= 0))
                throw new InvalidOperationException("Fitted down/up weights must stay nonnegative.");
        }

        /// <summary>
        /// Down weight i (distance i + 0.5 from the center): the slid base,
        /// eased into the pyramid's binomial by <paramref name="toBinomial"/>.
        /// </summary>
        static double Down(int i, double slide, double toBinomial)
        {
            double fitted = DownBase[i] + DownSlide[i] * slide;
            double binomial = SmallKernel.Binomial[2 - i];
            // Max only guards negative round-off near a zero weight.
            return Math.Max(0, fitted * (1 - toBinomial) + binomial * toBinomial);
        }

        /// <summary>Down's executed weight for source pixel 2j + h, h in -2..3, of half-res texel j.</summary>
        internal double DownWeight(int h)
        {
            double inner = downOuterPosition + 2.5;
            if (h == 0 || h == 1)
                return 0.5 - downOuterMass;
            if (h == -1 || h == 2)
                return downOuterMass * inner;
            return downOuterMass * (1 - inner);
        }

        /// <summary>Up's executed weight i, 0..3, on half-res texels -2..+1, for an even output pixel.</summary>
        internal double UpWeight(int i)
        {
            double left = upLeft + 2;
            switch (i)
            {
                case 0: return (1 - upRightWeight) * (1 - left);
                case 1: return (1 - upRightWeight) * left;
                case 2: return upRightWeight * (1 - upRight);
                default: return upRightWeight * upRight;
            }
        }

        /// <summary>
        /// Down as three bilinear fetches per axis, offsets from the half-res
        /// texel's center: pixels (2j-2, 2j-1), (2j, 2j+1) and (2j+2, 2j+3).
        /// </summary>
        internal void PackDown(Vector4[] taps)
        {
            Array.Clear(taps, 0, taps.Length);
            taps[0] = new Vector4((float)downOuterPosition, (float)downOuterMass, 0, 0);
            taps[1] = new Vector4(0, (float)(1 - 2 * downOuterMass), 0, 0);
            taps[2] = new Vector4((float)-downOuterPosition, (float)downOuterMass, 0, 0);
        }

        /// <summary>
        /// Up as two bilinear fetches per parity, offsets in half-res texels
        /// from the center of texel floor(x / 2). Entries 0-1 serve even output
        /// pixels; 2-3 are their mirror image for odd pixels.
        /// </summary>
        internal void PackUp(Vector4[] taps)
        {
            Array.Clear(taps, 0, taps.Length);
            taps[0] = new Vector4((float)upLeft, (float)(1 - upRightWeight), 0, 0);
            taps[1] = new Vector4((float)upRight, (float)upRightWeight, 0, 0);
            taps[2] = new Vector4((float)-upRight, (float)upRightWeight, 0, 0);
            taps[3] = new Vector4((float)-upLeft, (float)(1 - upRightWeight), 0, 0);
        }

        /// <summary>
        /// The p whose pair produces <paramref name="variance"/>: Catmull-Rom
        /// interpolation of <see cref="InverseTable"/>. The variance a given
        /// p produces briefly runs backward, so a numeric search could land
        /// on the wrong branch; the table follows the intended one.
        /// </summary>
        static double CurvePosition(double variance)
        {
            double[] table = InverseTable;
            int last = table.Length - 1;
            double x = Math.Max(0, Math.Min(last, (variance - Start) / (End - Start) * last));
            int i = Math.Min((int)x, last - 1);
            double f = x - i;
            double f2 = f * f;
            double f3 = f2 * f;
            double m0 = i == 0 ? table[1] - table[0] :
                (table[i + 1] - table[i - 1]) * 0.5;
            double m1 = i + 1 == last ? table[last] - table[last - 1] :
                (table[i + 2] - table[i]) * 0.5;
            return (2 * f3 - 3 * f2 + 1) * table[i] +
                (f3 - 2 * f2 + f) * m0 +
                (-2 * f3 + 3 * f2) * table[i + 1] +
                (f3 - f2) * m1;
        }

        static double Progress(double p, double lo, double hi) => Math.Max(0, Math.Min(1, (p - lo) / (hi - lo)));

        /// <summary>Bézier curve with these control points, at x in [0, 1].</summary>
        static double Bezier(double[] controls, double x)
        {
            int degree = controls.Length - 1;
            double result = 0;
            for (int i = 0; i <= degree; i++)
                result += Bernstein(degree, i, x) * controls[i];
            return result;
        }

        /// <summary>Bézier curve through one column of the control-point rows, at x in [0, 1].</summary>
        static double Bezier(double[,] controls, int column, double x)
        {
            int degree = controls.GetLength(0) - 1;
            double result = 0;
            for (int i = 0; i <= degree; i++)
                result += Bernstein(degree, i, x) * controls[i, column];
            return result;
        }

        static double Bernstein(int degree, int i, double x)
        {
            double binomial = 1;
            for (int k = 0; k < i; k++)
                binomial *= (double)(degree - k) / (k + 1);
            return binomial * Math.Pow(x, i) * Math.Pow(1 - x, degree - i);
        }
    }
}
