using System;
using UnityEngine;
using UnityEngine.Experimental.Rendering;
using UnityEngine.Rendering;
using UnityEngine.Rendering.RenderGraphModule;

namespace SteplessGaussianBlur
{
    /// <summary>
    /// A blurred texture that <see cref="GaussianBlur.Blur(Texture, BlurTarget, BlurStrength)"/> and
    /// <see cref="GaussianBlur.RecordTo(RenderGraph, BlurSource, BlurTarget, BlurStrength)"/>
    /// size to the source. Dispose it to release the texture.
    /// </summary>
    public sealed class BlurTarget : IDisposable
    {
        const GraphicsFormat PlaceholderFormat = GraphicsFormat.R8G8B8A8_UNorm;

        readonly bool fullSize;
        // Null to follow each source (StorageFormat.For).
        readonly GraphicsFormat? format;
        // The requested format: Unity can report another, such as UNorm for
        // sRGB in Gamma.
        GraphicsFormat allocatedFormat = PlaceholderFormat;
        RenderTexture texture;
        bool disposed;
        // The texture's wrapper, and its handle in the latest graph recording.
        RTHandle handle;
        RenderGraph importGraph;
        TextureHandle imported;
        Vector2Int importedSize;
        GraphicsFormat importedFormat;

        /// <summary>
        /// The blurred texture. It stays the same object while blurs resize it,
        /// so it can be bound once. Until the first blur it is 1×1 with
        /// undefined contents; null after disposal.
        /// </summary>
        public RenderTexture Texture
        {
            get
            {
                // Created on first use, so a target can be a field initializer;
                // recreated if destroyed, and then sized again by the next blur.
                if (texture == null && !disposed)
                {
                    Output = default;
                    allocatedFormat = PlaceholderFormat;
                    texture = new RenderTexture(new RenderTextureDescriptor(1, 1, PlaceholderFormat, GraphicsFormat.None))
                    {
                        name = "Stepless Blur Target",
                        filterMode = FilterMode.Bilinear,
                        wrapMode = TextureWrapMode.Clamp
                    };
                    texture.Create();
                }
                return texture;
            }
        }

        /// <summary>The size <see cref="Texture"/> was last given.</summary>
        internal OutputSize Output { get; private set; }

        /// <summary>Whether a <see cref="GaussianBlur.Blur(Texture, BlurTarget, BlurStrength)"/> into this target still waits for a camera.</summary>
        public bool HasPendingBlur => Pending.owner != null;

        /// <summary>The latest <see cref="GaussianBlur.Blur(Texture, BlurTarget, BlurStrength)"/> request not yet recorded.</summary>
        internal GaussianBlur.PendingBlur Pending;

        /// <param name="fullSize">
        /// Keep the source size. By default, from sigma 3, the texture shrinks
        /// by the largest power of two up to the blur's reduction that divides
        /// both source sides, and the bilinear sampling that displays it
        /// finishes the blur. Use full size to read pixels or to sample
        /// without filtering.
        /// </param>
        /// <param name="format">
        /// A color format that supports rendering and bilinear filtering; by
        /// default the smallest that holds the source. With a format, warm up
        /// with <c>WarmUpShaders(sourceFormat, format)</c>.
        /// </param>
        public BlurTarget(bool fullSize = false, GraphicsFormat? format = null)
        {
            if (format.HasValue && !StorageFormat.CanStore(format.Value))
                throw new ArgumentException(
                    $"Format {format} must be a color format that supports rendering and linear filtering.",
                    nameof(format));
            this.fullSize = fullSize;
            this.format = format;
        }

        internal OutputSize OutputFor(float sigma, int width, int height) =>
            fullSize ? OutputSize.Full(width, height) : GaussianBlur.ReducedOutputSize(sigma, width, height);

        /// <summary>Releases <see cref="Texture"/>.</summary>
        public void Dispose()
        {
            disposed = true;
            GaussianBlur.CancelPending(this);
            handle?.Release();
            handle = null;
            importGraph = null;
            imported = default;
            CoreUtils.Destroy(texture);
            texture = null;
            Output = default;
        }

        /// <summary>
        /// The texture's handle in this recording of <paramref name="graph"/>,
        /// imported once per recording and size, so RenderGraph orders the
        /// passes that write and read it.
        /// </summary>
        internal TextureHandle Import(RenderGraph graph)
        {
            RenderTexture target = Texture;
            var size = new Vector2Int(target.width, target.height);
            if (IsCurrent(graph) && importedSize == size && importedFormat == target.graphicsFormat)
                return imported;
            if (handle == null || handle.rt != target)
            {
                handle?.Release();
                handle = RTHandles.Alloc(target, transferOwnership: false);
            }
            imported = graph.ImportTexture(handle);
            importGraph = graph;
            importedSize = size;
            importedFormat = target.graphicsFormat;
            return imported;
        }

        /// <summary>Whether <paramref name="source"/> is this target's texture in the recording.</summary>
        internal bool IsImportedAs(RenderGraph graph, TextureHandle source)
            => source.IsValid() && IsCurrent(graph) && imported.Equals(source);

        // Core keeps a handle valid only while its recording lasts. Its
        // validity bit can repeat across graphs, so the graph is compared too.
        bool IsCurrent(RenderGraph graph) => importGraph == graph && imported.IsValid();

        internal void ThrowIfDisposed()
        {
            if (disposed)
                throw new ObjectDisposedException(nameof(BlurTarget));
        }

        /// <summary>Sizes <see cref="Texture"/> in place for a blur about to write it.</summary>
        internal void Allocate(OutputSize output, GraphicsFormat sourceFormat)
        {
            GraphicsFormat textureFormat = format ?? StorageFormat.For(sourceFormat);
            RenderTexture target = Texture;
            if (Output.Size != output.Size || allocatedFormat != textureFormat)
            {
                target.Release();
                // A whole descriptor: changing only graphicsFormat from UNorm to
                // sRGB leaves stores unencoded on Metal.
                target.descriptor = new RenderTextureDescriptor(output.Size.x, output.Size.y, textureFormat,
                    GraphicsFormat.None);
                allocatedFormat = textureFormat;
            }
            // A reduced texture is finished by bilinear, clamped sampling.
            target.filterMode = FilterMode.Bilinear;
            target.wrapMode = TextureWrapMode.Clamp;
            Output = output;
            // Also restores storage lost, for example, to a device reset.
            if (!target.IsCreated())
                target.Create();
        }
    }
}
