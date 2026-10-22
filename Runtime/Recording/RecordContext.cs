using UnityEngine;
using UnityEngine.Experimental.Rendering;
using UnityEngine.Rendering;
using UnityEngine.Rendering.RenderGraphModule;

namespace SteplessGaussianBlur
{
    public sealed partial class GaussianBlur
    {
        /// <summary>
        /// Everything one Record call needs after its source and destination are
        /// resolved. Passes read source metadata here instead of from RenderGraph,
        /// which loses the sizing and color-format details of imported textures.
        /// </summary>
        readonly struct RecordContext
        {
            internal readonly TextureHandle source;
            internal readonly Texture sourceTexture;
            internal readonly TextureDesc sourceDescriptor;
            // xy: active image size; zw: reciprocal of the sampleable view size.
            internal readonly Vector4 sourceSamplingSize;
            internal readonly Vector2Int sourceAllocationSize;
            // The caller's texture, or default for a new one.
            internal readonly TextureHandle destination;
            internal readonly TextureDesc destinationDescriptor;
            // Template for scratch targets: active size, intermediate format.
            internal readonly TextureDesc input;
            // Full-resolution outputs reuse the source allocation through automatic DRS.
            internal readonly bool retainHardwareScaling;
            // Source pixels per output texel: 1, or ReducedScale.
            internal readonly int outputScale;

            internal RecordContext(TextureHandle source, Texture sourceTexture, TextureDesc descriptor,
                Vector2Int allocation, Vector2Int viewport, Vector2Int textureView, TextureHandle destination,
                TextureDesc destinationDescriptor, GraphicsFormat format, int outputScale)
            {
                this.source = source;
                this.sourceTexture = sourceTexture;
                sourceDescriptor = descriptor;
                sourceSamplingSize = SamplingSize(viewport, textureView);
                sourceAllocationSize = allocation;
                this.destination = destination;
                this.destinationDescriptor = destinationDescriptor;
                retainHardwareScaling = !destination.IsValid() && descriptor.useDynamicScale &&
                    !descriptor.useDynamicScaleExplicit && viewport == textureView &&
                    textureView == ScaleHardwareSize(allocation);
                input = descriptor;
                input.sizeMode = TextureSizeMode.Explicit;
                input.width = viewport.x;
                input.height = viewport.y;
                input.useDynamicScale = false;
                input.useDynamicScaleExplicit = false;
                input.format = format;
                this.outputScale = outputScale;
            }

            internal TextureDesc Describe(RenderGraph graph, TextureHandle texture)
            {
                if (texture.Equals(source))
                    return sourceDescriptor;
                return texture.Equals(destination) ? destinationDescriptor : graph.GetTextureDesc(texture);
            }

            internal Vector4 SamplingSize(TextureHandle texture, TextureDesc descriptor)
            {
                if (texture.Equals(source))
                    return sourceSamplingSize;
                Vector2Int size = TextureViewSize(descriptor);
                return SamplingSize(size, size);
            }

            static Vector4 SamplingSize(Vector2Int viewport, Vector2Int texture) =>
                new Vector4(viewport.x, viewport.y, 1f / texture.x, 1f / texture.y);
        }
    }
}
