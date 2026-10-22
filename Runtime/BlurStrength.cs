using System;

namespace SteplessGaussianBlur
{
    /// <summary>
    /// Target Gaussian standard deviation (sigma), in source pixels or measured
    /// at a reference resolution.
    /// </summary>
    /// <remarks>
    /// A plain <c>float</c> converts to a strength in source pixels. With a
    /// reference resolution, sigma scales with the active image's shorter side:
    /// at reference 1024, strength 1 is sigma 1 on a 1024-pixel short side and
    /// sigma 2 on a 2048-pixel one.
    /// </remarks>
    public readonly struct BlurStrength
    {
        /// <summary>Gaussian standard deviation, in pixels at <see cref="ReferenceResolution"/>.</summary>
        internal float Sigma { get; }

        /// <summary>Shorter image side at which <see cref="Sigma"/> applies, or zero for source pixels.</summary>
        internal int ReferenceResolution { get; }

        BlurStrength(float sigma, int referenceResolution)
        {
            Sigma = sigma;
            ReferenceResolution = referenceResolution;
        }

        /// <summary>Sigma measured in active source pixels.</summary>
        public static BlurStrength Pixels(float sigma) => new BlurStrength(sigma, 0);

        /// <summary>Sigma measured on an image whose shorter side is <paramref name="referenceResolution"/>.</summary>
        public static BlurStrength AtReferenceResolution(float sigma, int referenceResolution)
        {
            if (referenceResolution <= 0)
                throw new ArgumentOutOfRangeException(nameof(referenceResolution), "Reference resolution must be positive.");
            return new BlurStrength(sigma, referenceResolution);
        }

        /// <summary>Sigma measured in active source pixels.</summary>
        public static implicit operator BlurStrength(float sigma) => Pixels(sigma);

        /// <summary>Sigma in source pixels for an active image of the given size.</summary>
        internal float SourceSigma(int width, int height)
        {
            if (ReferenceResolution == 0)
                return Sigma;
            return (float)Math.Min(float.MaxValue,
                (double)Sigma * Math.Min(width, height) / ReferenceResolution);
        }

        /// <summary>Zero strength is an identity even before the source is inspected.</summary>
        internal bool IsIdentity => Sigma == 0;

        internal void ThrowIfInvalid(string paramName)
        {
            if (!float.IsFinite(Sigma) || Sigma < 0)
                throw new ArgumentOutOfRangeException(paramName, "Blur sigma must be finite and nonnegative.");
        }
    }
}
