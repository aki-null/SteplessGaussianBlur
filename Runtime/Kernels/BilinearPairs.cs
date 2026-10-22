using System;
using UnityEngine;
using static SteplessGaussianBlur.BlurMath;

namespace SteplessGaussianBlur
{
    /// <summary>
    /// Packs a row of per-texel weights into bilinear fetches: texels k and
    /// k + 1 with weights a and b become one fetch at k + b / (a + b) with
    /// weight a + b. Pack writes (offset, weight, 0, 0); each layout repacks
    /// fetches for its shader at upload.
    /// </summary>
    /// <remarks>
    /// The sampler rounds a fetch's position to 1/256 texel, which moves a
    /// little weight between its two texels. <see cref="Pack"/> rounds the
    /// positions itself, then reweights the fetches so the row's total, center
    /// and variance stay exact.
    /// Either the center texel is a fetch of its own and outer texels pair
    /// away from it, or pairs start on texels of one parity. Optionally the
    /// even texels' first moment stays exact too: a row whose odd-texel twin
    /// is its mirror image then moves a point equally on either parity.
    /// A row pairs texels the same way at every sigma, so a fetch never
    /// jumps to a new pairing when a tiny outer weight appears.
    /// </remarks>
    internal static class BilinearPairs
    {
        const double MinimumWeight = 1e-14;
        const int MaximumConstraints = 4;

        /// <summary>The snapped fraction of texels (a, b) that lies on b.</summary>
        internal static double SnappedFraction(double a, double b) => a + b > 0 ? Snap(b / (a + b)) : 0;

        /// <summary>
        /// Two fetches over texels first to first + 3, one per pair (a, b) and
        /// (c, d), each at its snapped fraction. Their weights are solved for
        /// unit total and the given center. Returns their variance about it.
        /// </summary>
        internal static double CenteredPair(int first, double a, double b, double c, double d, double center,
            out double left, out double right, out double rightWeight)
        {
            left = first + SnappedFraction(a, b);
            right = first + 2 + SnappedFraction(c, d);
            rightWeight = (center - left) / (right - left);
            return (1 - rightWeight) * SecondMoment(left) + rightWeight * SecondMoment(right) - center * center;
        }

        /// <summary>
        /// Second moment about zero of one bilinear fetch at <paramref name="x"/>:
        /// x² plus f(1 - f), the spread from splitting it between two texels.
        /// </summary>
        internal static double SecondMoment(double x)
        {
            double fraction = x - Math.Floor(x);
            return x * x + fraction * (1 - fraction);
        }

        /// <summary>
        /// Packs <paramref name="row"/> (offset k at row[origin + k]) into
        /// fetches at output[start ...]. Returns the fetch count.
        /// </summary>
        /// <remarks>
        /// With <paramref name="parity"/> below zero: texel 0 alone, then pairs
        /// (1, 2), (3, 4) ... and (-2, -1), (-4, -3) .... Otherwise pairs (k, k + 1)
        /// whose k has that parity, one fetch fewer for some short rows.
        /// </remarks>
        /// <param name="positions">Scratch, at least <paramref name="capacity"/> long.</param>
        /// <param name="weights">Scratch, at least <paramref name="capacity"/> long.</param>
        /// <param name="evenMoment">Also keep the even texels' first moment exact.</param>
        internal static int Pack(double[] row, int origin, Vector4[] output, int start, int capacity,
            double[] positions, double[] weights, int parity = -1, bool evenMoment = false)
        {
            int count = 0;
            int lo = -origin;
            int hi = row.Length - 1 - origin;
            if (parity >= 0)
            {
                if (((lo + parity) & 1) != 0)
                    lo--;
                for (int k = lo; k <= hi; k += 2)
                    Add(row, origin, k, k + 1, positions, weights, ref count, capacity);
            }
            else
            {
                Add(row, origin, 0, 0, positions, weights, ref count, capacity);
                for (int k = 1; k <= hi; k += 2)
                    Add(row, origin, k, k + 1, positions, weights, ref count, capacity);
                for (int k = -2; k + 1 >= lo; k -= 2)
                    Add(row, origin, k, k + 1, positions, weights, ref count, capacity);
            }
            if (count == 0)
            {
                output[start] = new Vector4(0, 1, 0, 0);
                return 1;
            }
            Restore(row, origin, positions, weights, count, evenMoment);
            Write(output, start, positions, weights, count);
            return count;
        }

        // Adds texels first..second as one fetch at the rounded position.
        static void Add(double[] row, int origin, int first, int second, double[] positions, double[] weights,
            ref int count, int capacity)
        {
            double a = Texel(row, origin, first);
            double b = first == second ? 0 : Texel(row, origin, second);
            double mass = a + b;
            if (mass <= MinimumWeight)
                return;
            if (count == capacity)
                throw new InvalidOperationException($"Blur kernel exceeds {capacity} fetches per axis.");
            // Round the position, not the fraction: Snap is odd, so mirrored
            // rows keep mirrored fetches.
            positions[count] = Snap(first + b / mass);
            weights[count] = mass;
            count++;
        }

        static double Texel(double[] row, int origin, int k)
        {
            int index = origin + k;
            return (uint)index < (uint)row.Length ? row[index] : 0;
        }

        // Writes the fetches in increasing position, so the middle one, which
        // anchored shader sums use, is the center texel or next to it.
        static void Write(Vector4[] output, int start, double[] positions, double[] weights, int count)
        {
            if (start + count > output.Length)
                throw new InvalidOperationException("Pair target is too small.");
            for (int i = 0; i < count; i++)
            {
                double position = positions[i];
                double weight = weights[i];
                int j = i;
                for (; j > 0 && output[start + j - 1].x > position; j--)
                    output[start + j] = output[start + j - 1];
                output[start + j] = new Vector4((float)position, (float)weight, 0, 0);
            }
        }

        // ---- Moment restoration ------------------------------------------------
        // Rounding moves weight within each fetch. Scaling each weight by
        // 1 + Σ c_j g_j(x), with the same functions g_j whose sums must match,
        // restores them: a small symmetric linear system in the c_j. If the
        // fetches cannot carry every moment with nonnegative weights, the last
        // moment is dropped and the rest are solved again.

        static void Restore(double[] row, int origin, double[] positions, double[] weights, int count,
            bool evenMoment)
        {
            Span<double> target = stackalloc double[MaximumConstraints];
            for (int i = 0; i < row.Length; i++)
            {
                double w = row[i];
                if (w == 0)
                    continue;
                int k = i - origin;
                target[0] += w;
                target[1] += k * w;
                target[2] += (double)k * k * w;
                if ((k & 1) == 0)
                    target[3] += k * w;
            }
            int constraints = Math.Min(evenMoment ? 4 : 3, count);
            Span<double> fitted = stackalloc double[64];
            if (count > fitted.Length)
                fitted = new double[count];
            for (int i = 0; i < count; i++)
                fitted[i] = weights[i];
            for (; constraints > 0; constraints--)
            {
                if (TrySolve(positions, fitted, weights, count, target, constraints))
                    return;
            }
            for (int i = 0; i < count; i++)
                weights[i] = fitted[i];
        }

        // Moment j of one unit fetch at x: total, first, second, even texels' first.
        static double Moment(int j, double x)
        {
            switch (j)
            {
                case 0: return 1;
                case 1: return x;
                case 2: return SecondMoment(x);
                default:
                {
                    int first = (int)Math.Floor(x);
                    double fraction = x - first;
                    return (first & 1) == 0 ? first * (1 - fraction) : (first + 1) * fraction;
                }
            }
        }

        static bool TrySolve(double[] positions, Span<double> fitted, double[] weights, int count,
            Span<double> target, int n)
        {
            Span<double> matrix = stackalloc double[MaximumConstraints * (MaximumConstraints + 1)];
            int columns = n + 1;
            for (int j = 0; j < n; j++)
            {
                double residual = target[j];
                for (int i = 0; i < count; i++)
                    residual -= fitted[i] * Moment(j, positions[i]);
                matrix[j * columns + n] = residual;
                for (int k = 0; k < n; k++)
                {
                    double sum = 0;
                    for (int i = 0; i < count; i++)
                        sum += fitted[i] * Moment(j, positions[i]) * Moment(k, positions[i]);
                    matrix[j * columns + k] = sum;
                }
            }
            // Gaussian elimination with partial pivoting.
            for (int c = 0; c < n; c++)
            {
                int pivot = c;
                for (int r = c + 1; r < n; r++)
                {
                    if (Math.Abs(matrix[r * columns + c]) > Math.Abs(matrix[pivot * columns + c]))
                        pivot = r;
                }
                if (Math.Abs(matrix[pivot * columns + c]) < 1e-12 * Math.Abs(matrix[0]))
                    return false;
                if (pivot != c)
                {
                    for (int k = 0; k < columns; k++)
                        (matrix[c * columns + k], matrix[pivot * columns + k]) =
                            (matrix[pivot * columns + k], matrix[c * columns + k]);
                }
                for (int r = 0; r < n; r++)
                {
                    if (r == c)
                        continue;
                    double factor = matrix[r * columns + c] / matrix[c * columns + c];
                    for (int k = c; k < columns; k++)
                        matrix[r * columns + k] -= factor * matrix[c * columns + k];
                }
            }
            for (int i = 0; i < count; i++)
            {
                double scale = 1;
                for (int j = 0; j < n; j++)
                    scale += matrix[j * columns + n] / matrix[j * columns + j] * Moment(j, positions[i]);
                double weight = fitted[i] * scale;
                if (weight < 0)
                    return false;
                weights[i] = weight;
            }
            return true;
        }
    }
}
