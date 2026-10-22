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

        OutputSize(Vector2Int size, int scale)
        {
            Size = size;
            Scale = scale;
        }

        internal static OutputSize Full(int width, int height) => Create(width, height, 1);

        /// <summary>The output of an active image at <paramref name="scale"/> (see ReducedScale).</summary>
        internal static OutputSize Create(int width, int height, int scale) =>
            new OutputSize(new Vector2Int(width / scale, height / scale), scale);
    }
}
