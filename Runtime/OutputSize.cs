using UnityEngine;

namespace SteplessGaussianBlur
{
    /// <summary>
    /// The size of the texture a blur writes: the source's, or reduced by
    /// <see cref="Scale"/> and finished by the viewer's bilinear sampling.
    /// </summary>
    readonly struct OutputSize
    {
        internal Vector2Int Size { get; }

        /// <summary>Source pixels per output texel; 1 when no reduction applies.</summary>
        internal int Scale { get; }

        /// <summary>Whether the viewer's bilinear sampling finishes the blur, at any <see cref="Scale"/>.</summary>
        internal bool Reduced { get; }

        OutputSize(Vector2Int size, int scale, bool reduced)
        {
            Size = size;
            Scale = scale;
            Reduced = reduced;
        }

        internal static OutputSize Full(int width, int height) => new OutputSize(new Vector2Int(width, height), 1, false);

        /// <summary>The reduced output of an active image at <paramref name="scale"/> (see ReducedScale).</summary>
        internal static OutputSize Create(int width, int height, int scale) =>
            new OutputSize(new Vector2Int(width / scale, height / scale), scale, true);
    }
}
