using System;
using UnityEngine;
using UnityEngine.Experimental.Rendering;
using UnityEngine.Rendering;
using UnityEngine.Rendering.RenderGraphModule;

namespace SteplessGaussianBlur
{
    // What the blur reads from caller textures: descriptors, and the checks
    // a destination must pass.
    public sealed partial class GaussianBlur
    {
        /// <summary>Validates a destination for <paramref name="layout"/>.</summary>
        /// <param name="source">The source: its dimension and slices.</param>
        internal static void ValidateDestination(TextureDesc destination, TextureDesc source, OutputSize layout)
        {
            ValidateDescriptor(destination, TextureRole.Destination);
            // Memoryless color exists only within its render pass; later reads
            // need stored color. A memoryless depth attachment is independent.
            if ((destination.memoryless & RenderTextureMemoryless.Color) != 0)
                throw new ArgumentException("Destination color storage must not be memoryless.", nameof(destination));
            if (destination.width != layout.Size.x || destination.height != layout.Size.y ||
                destination.sizeMode != TextureSizeMode.Explicit ||
                destination.dimension != source.dimension || destination.slices != source.slices ||
                destination.msaaSamples != MSAASamples.None ||
                destination.useDynamicScale || destination.useDynamicScaleExplicit)
                throw new ArgumentException(
                    $"Destination must be {layout.Size.x}x{layout.Size.y} with the source's slices, " +
                    "without MSAA or dynamic scaling.", nameof(destination));
        }

        static TextureDesc DescribeTexture(Texture texture)
        {
            if (texture is RenderTexture renderTexture)
            {
                var descriptor = new TextureDesc(renderTexture);
                // These operations sample/write color even when the RT also
                // owns a depth buffer. Unity's constructor instead selects depth.
                descriptor.format = renderTexture.graphicsFormat;
                descriptor.useDynamicScaleExplicit = renderTexture.useDynamicScaleExplicit;
                return descriptor;
            }
            return new TextureDesc(texture.width, texture.height)
            {
                colorFormat = texture.graphicsFormat,
                dimension = texture.dimension,
                slices = texture is Texture2DArray array ? array.depth : 1
            };
        }
    }
}
