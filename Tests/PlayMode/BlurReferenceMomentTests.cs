using System;
using NUnit.Framework;

namespace SteplessGaussianBlur.Tests
{
    /// <summary>
    /// Checks the complete CPU pipeline against Gaussian moments computed here,
    /// without the runtime's variance budgets or thresholds.
    /// </summary>
    /// <remarks>
    /// The GPU tests compare against BlurGpuReference, which reuses the runtime's
    /// coefficient builders. A budget or origin error would move both sides
    /// together; these moments would not.
    /// </remarks>
    public sealed class BlurReferenceMomentTests
    {
        // Each layout switch and blend endpoint from both sides, at sigma
        // 0.7409 (row pairing), 1.46 (fitted rows), 1.616 (down/up), 1.74
        // (half-res Gaussian), 2.40 (pyramid), 3.94, 4.22, 8.00 and 8.55
        // (pyramid levels), 4.25 and 4.5 (outer upsample blend), 16.1 (transition),
        // 16.6 and 21 (transition blends), 24 (wide), 64 (variance
        // approximation), 70 and 140 (working pitch). 0.8 is an interior control.
        [TestCase(0.8f), TestCase(0.735f), TestCase(0.745f)]
        [TestCase(1.455f), TestCase(1.465f), TestCase(1.611f), TestCase(1.621f)]
        [TestCase(1.735f), TestCase(1.745f), TestCase(2.395f), TestCase(2.405f)]
        [TestCase(4.24f), TestCase(4.26f), TestCase(4.49f), TestCase(4.51f)]
        [TestCase(3.935f), TestCase(3.945f), TestCase(4.215f), TestCase(4.225f)]
        [TestCase(7.995f), TestCase(8.01f), TestCase(8.55f), TestCase(8.56f)]
        [TestCase(16.09f), TestCase(16.11f), TestCase(16.59f), TestCase(16.61f)]
        [TestCase(20.99f), TestCase(21.01f), TestCase(23.99f), TestCase(24.01f)]
        [TestCase(63.99f), TestCase(64.01f), TestCase(69.99f), TestCase(70.01f)]
        [TestCase(139.99f), TestCase(140.01f)]
        public void ImpulseResponseIsCenteredWithTheRequestedVariance(float sigma)
        {
            // Keep the response (at most five sigma wide) far from both borders.
            int length = 2 * (int)Math.Ceiling(6 * sigma) + 129;
            double expectedVariance = BlurGpuReference.TaperedGaussianVariance(sigma);
            // Cover every position on the coarsest grid: at most 16 pixels
            // below sigma 70, and Pitch (at most 64 here) above.
            int phases = sigma < 70 ? 16 : sigma < 140 ? 32 : 64;
            double width = 0;
            for (int phase = 0; phase < phases; phase++)
            {
                int position = length / 2 + phase;
                var row = new double[length];
                row[position] = 1;
                double[] response = BlurGpuReference.BlurRow(row, sigma);
                double mass = 0, first = 0, second = 0;
                for (int i = 0; i < length; i++)
                {
                    double offset = i - position;
                    mass += response[i];
                    first += offset * response[i];
                    second += offset * offset * response[i];
                }
                double mean = first / mass;
                string label = $"sigma {sigma}, phase {phase}";
                Assert.That(mass, Is.EqualTo(1).Within(1e-6), label + ", energy");
                // The quarter-resolution pass centers each source phase within
                // 1/128 pixel; below sigma 16.1 rounding to the sampler grid
                // leaves a little more.
                Assert.That(Math.Abs(mean), Is.LessThan(sigma >= TransitionKernel.StartSigma ? 1.0 / 128 + 1e-6 : 0.011),
                    label + ", center");
                width += second / mass;
            }
            // Width as the second moment about the point, averaged over positions.
            Assert.That(width / phases / expectedVariance, Is.EqualTo(1).Within(2e-6), $"sigma {sigma}, width");
        }
    }
}
