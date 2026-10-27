using System;
using UnityEngine;
using UnityEngine.Experimental.Rendering;
using UnityEngine.Rendering;
using UnityEngine.Rendering.RenderGraphModule;

namespace SteplessGaussianBlur
{
    // Validates a source and its output, then records the layout.
    public sealed partial class GaussianBlur
    {
        /// <summary>What a recording writes: a new texture, the caller's texture, or a target's.</summary>
        readonly struct Output
        {
            internal readonly TextureHandle destination;
            internal readonly BlurTarget target;
            // A new texture that stops at ReducedScale.
            internal readonly bool reduced;

            Output(TextureHandle destination, BlurTarget target, bool reduced)
            {
                this.destination = destination;
                this.target = target;
                this.reduced = reduced;
            }

            internal static Output New => default;
            internal static Output NewReduced => new Output(default, null, true);
            internal static Output Into(TextureHandle destination) => new Output(destination, null, false);
            internal static Output Into(BlurTarget target) => new Output(default, target, false);

            internal bool HasDestination => destination.IsValid() || target != null;

            internal OutputSize Size(float sigma, int width, int height) =>
                target?.OutputFor(sigma, width, height) ??
                (reduced ? ReducedOutputSize(sigma, width, height) : OutputSize.Full(width, height));
        }

        /// <summary>A new output reduced as far as the strength allows.</summary>
        internal static OutputSize ReducedOutputSize(float sigma, int width, int height)
            => OutputSize.Create(width, height, ReducedScale(sigma, width, height));

        TextureHandle RecordSource(RenderGraph graph, BlurSource source, BlurStrength strength, Output output)
        {
            Texture sourceTexture = source.texture;
            TextureHandle handle = source.handle;
            if (sourceTexture == null && !handle.IsValid())
                throw new ArgumentException("Blur source texture is invalid.", nameof(source));
            strength.ThrowIfInvalid(nameof(strength));
            BlurTarget target = output.target;
            // A returned handle stands for its whole texture view, so a source
            // read through a viewport is copied rather than returned. A
            // destination or a reduced call also needs a texture of its own.
            bool ownResult = output.HasDestination || output.reduced || source.HasViewport;
            // Zero strength is an identity even for sources the blur could not read.
            if (!ownResult && strength.IsIdentity)
                return handle;

            TextureDesc descriptor = sourceTexture != null ? DescribeTexture(sourceTexture) : graph.GetTextureDesc(handle);
            // Before GetRenderTargetInfo, which evaluates a graph texture's size.
            ValidateSource(descriptor, target);
            // Sizing the target would release the texture being read.
            if (target != null && (sourceTexture == target.Texture || target.IsImportedAs(graph, handle)))
                throw new ArgumentException("Blur a target's texture into another target.", nameof(source));
            Vector2Int allocation;
            if (sourceTexture != null)
                allocation = new Vector2Int(sourceTexture.width, sourceTexture.height);
            else
            {
                RenderTargetInfo info = graph.GetRenderTargetInfo(handle);
                allocation = new Vector2Int(info.width, info.height);
            }
            Vector2Int viewport = source.ViewportSize;
            Vector2Int textureView = source.TextureSize;
            ResolveSourceSizes(descriptor, allocation, ref viewport, ref textureView);
            float sigma = strength.SourceSigma(viewport.x, viewport.y);
            OutputSize size = output.Size(sigma, viewport.x, viewport.y);

            TextureHandle destination = output.destination;
            TextureDesc destinationDescriptor = default;
            if (target != null)
            {
                target.Allocate(size, descriptor.format);
                destination = target.Import(graph);
                destinationDescriptor = DescribeTexture(target.Texture);
            }
            else if (destination.IsValid())
            {
                if (destination.Equals(handle))
                    throw new ArgumentException("Blur source and destination must be distinct.", nameof(destination));
                destinationDescriptor = graph.GetTextureDesc(destination);
            }
            GraphicsFormat format = StorageFormat.For(descriptor.format);
            if (destination.IsValid())
            {
                ValidateDestination(destinationDescriptor, descriptor, size);
                format = StorageFormat.Intermediate(descriptor.format, destinationDescriptor.format);
            }

            var context = new RecordContext(handle, sourceTexture, descriptor, allocation, viewport, textureView,
                destination, destinationDescriptor, format, size);
            TextureHandle result = RecordLayout(graph, context, sigma);
            if (ownResult && result.Equals(handle))
                result = RecordIdentity(graph, context);
            // A target is sampled through materials, outside the graph.
            if (target != null)
                RecordPublish(graph, destination);
            return result;
        }

        /// <summary>Every check of a source, for <see cref="RecordSource"/> and <see cref="Blur(Texture, BlurTarget, BlurStrength)"/>.</summary>
        /// <param name="target">The target the source is blurred into, or null.</param>
        static void ValidateSource(TextureDesc descriptor, BlurTarget target)
        {
            ValidateDescriptor(descriptor, TextureRole.Source);
            if (target != null && descriptor.dimension != TextureDimension.Tex2D)
                throw new ArgumentException("A BlurTarget holds a 2D texture; blur eye arrays with RecordTo " +
                    "into an array texture.", "source");
            ValidateEyeArray(descriptor);
        }

        /// <summary>A source is only sampled, a destination only rendered to.</summary>
        internal enum TextureRole { Source, Destination }

        internal static void ValidateDescriptor(TextureDesc descriptor, TextureRole use, string paramName = null)
        {
            bool isSource = use == TextureRole.Source;
            string role = isSource ? "source" : "destination";
            paramName ??= role;
            if (descriptor.sizeMode == TextureSizeMode.Explicit && (descriptor.width <= 0 || descriptor.height <= 0))
                throw new ArgumentException($"Blur {role} dimensions must be positive.", paramName);
            if (descriptor.sizeMode == TextureSizeMode.Scale &&
                (!float.IsFinite(descriptor.scale.x) || !float.IsFinite(descriptor.scale.y) ||
                descriptor.scale.x <= 0 || descriptor.scale.y <= 0))
                throw new ArgumentException($"Blur {role} scale must be finite and positive.", paramName);
            if (descriptor.sizeMode == TextureSizeMode.Functor && descriptor.func == null)
                throw new ArgumentException($"Blur {role} scale function is missing.", paramName);
            bool texture2D = descriptor.dimension == TextureDimension.Tex2D && descriptor.slices == 1;
            bool eyeArray = descriptor.dimension == TextureDimension.Tex2DArray && descriptor.slices > 0;
            if (!texture2D && !eyeArray)
                throw new ArgumentException($"Blur {role} must be a 2D color texture or an XR eye array.", paramName);
            // bindTextureMS=false exposes Unity's resolved sampling view. Its
            // MSAA allocation does not require a separate blur-side resolve.
            if (descriptor.bindTextureMS)
                throw new ArgumentException("Resolve explicitly bound MSAA before blurring.", paramName);
            if (descriptor.format == GraphicsFormat.None || GraphicsFormatUtility.IsDepthFormat(descriptor.format))
                throw new ArgumentException($"Blur {role} must have a color format." +
                    (isSource ? " RenderGraph imports a RenderTexture with depth as its depth buffer; Blur reads its color." : ""), paramName);
            // Sample-only inputs can use a compressed format; passes store
            // StorageFormat's choice.
            bool sample = SystemInfo.IsFormatSupported(descriptor.format, GraphicsFormatUsage.Sample);
            bool linear = SystemInfo.IsFormatSupported(descriptor.format, GraphicsFormatUsage.Linear);
            bool render = SystemInfo.IsFormatSupported(descriptor.format, GraphicsFormatUsage.Render);
            bool packedCameraColor = StorageFormat.IsPackedCameraColor(descriptor.format);
            bool filtered = sample && linear || packedCameraColor;
            if (GraphicsFormatUtility.IsIntegerFormat(descriptor.format) || (isSource ? !filtered : !render))
            {
                string needs = isSource ? "float sampling and linear filtering" : "float color rendering";
                throw new ArgumentException(
                    $"Blur {role} format {descriptor.format} on {SystemInfo.graphicsDeviceType} must support " +
                    $"{needs} (sample={sample}, linear={linear}, render={render}).", paramName);
            }
        }

        /// <summary>
        /// Outside an XR context, array shaders read only slice 0 and draws write
        /// only slice 0, so the remaining eyes would be left unfiltered. Builds
        /// without XR also strip the array variants (BlurShaderVariantStripper).
        /// </summary>
        static void ValidateEyeArray(TextureDesc source)
        {
            if (source.dimension == TextureDimension.Tex2DArray && TextureXR.slices < 2)
                throw new ArgumentException("Texture arrays are blurred only as XR eye arrays, during XR rendering.",
                    nameof(source));
            if (source.dimension == TextureDimension.Tex2DArray && source.slices > TextureXR.slices)
                throw new ArgumentException(
                    $"A {source.slices}-slice eye array needs an XR context rendering {source.slices} views " +
                    $"(TextureXR.slices is {TextureXR.slices}).", nameof(source));
        }

        static void ResolveSourceSizes(TextureDesc input, Vector2Int allocation,
            ref Vector2Int viewportSize, ref Vector2Int textureSize)
        {
            bool hardwareScaled = input.useDynamicScale || input.useDynamicScaleExplicit;
            if (textureSize == default)
            {
                // Explicit scaling may not match ScalableBufferManager's
                // current factor. Only Blur sources carry the flag; RenderGraph
                // drops it on import.
                if (input.useDynamicScaleExplicit)
                    throw new ArgumentException("Supply textureSize for explicit dynamic scaling.", nameof(textureSize));
                textureSize = hardwareScaled ? ScaleHardwareSize(allocation) : allocation;
            }
            if (viewportSize == default)
            {
                Vector2Int reference = RTHandles.rtHandleProperties.currentViewportSize;
                viewportSize = input.sizeMode switch
                {
                    TextureSizeMode.Scale => new Vector2Int(
                        Mathf.Max(1, Mathf.RoundToInt(reference.x * input.scale.x)),
                        Mathf.Max(1, Mathf.RoundToInt(reference.y * input.scale.y))),
                    TextureSizeMode.Functor => input.func(reference),
                    _ => textureSize
                };
                if (hardwareScaled && input.sizeMode != TextureSizeMode.Explicit)
                    viewportSize = ScaleHardwareSize(viewportSize);
            }
            ValidateSamplingSize(viewportSize, textureSize);
        }

        internal static void ValidateSamplingSize(Vector2Int viewport, Vector2Int texture)
        {
            if (texture.x <= 0 || texture.y <= 0)
                throw new ArgumentException("Texture-view dimensions must be positive.", "source");
            if (viewport.x <= 0 || viewport.y <= 0 || viewport.x > texture.x || viewport.y > texture.y)
                throw new ArgumentException("Active image must fit inside the texture view.", "source");
        }

        static Vector2Int ScaleHardwareSize(Vector2Int size) => new Vector2Int(
            Math.Clamp(Mathf.CeilToInt(size.x * ScalableBufferManager.widthScaleFactor), 1, size.x),
            Math.Clamp(Mathf.CeilToInt(size.y * ScalableBufferManager.heightScaleFactor), 1, size.y));

        static Vector2Int TextureViewSize(TextureDesc descriptor)
        {
            var size = new Vector2Int(descriptor.width, descriptor.height);
            return descriptor.useDynamicScale ? ScaleHardwareSize(size) : size;
        }
    }
}
