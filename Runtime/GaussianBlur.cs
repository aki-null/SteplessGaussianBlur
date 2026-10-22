using System;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.RenderGraphModule;

namespace SteplessGaussianBlur
{
    /// <summary>
    /// Records an approximate Gaussian blur into a URP RenderGraph.
    /// Each pass writes a new texture; <see cref="Record"/> returns the texture
    /// that the next pass should read.
    /// </summary>
    /// <remarks>
    /// Use on the main thread. Reuse across frames and dispose with the owner.
    /// Pixels outside the image repeat its edge.
    /// </remarks>
    // Design notes: Documentation~/Design.md.
    public sealed partial class GaussianBlur : IDisposable
    {
        bool disposed;

        /// <summary>Creates a reusable recorder with the package shader, from URP's graphics settings.</summary>
        /// <exception cref="InvalidOperationException">URP is not the active render pipeline.</exception>
        public GaussianBlur() : this(GaussianBlurResources.Load())
        {
        }

        /// <summary>Creates a reusable recorder with an explicitly supplied blur shader.</summary>
        /// <param name="shader">The package's SteplessGaussianBlur shader, or a compatible one.</param>
        public GaussianBlur(Shader shader)
        {
            if (shader == null)
                throw new ArgumentNullException(nameof(shader));

            // Run the kernel tables' static initializers here, not while recording.
            _ = FittedDownUp.At(FittedDownUp.Start);
            materials = CreateMaterials(shader);
            try
            {
                passIndices = ResolvePasses(Materials(KeywordSet.None).Select(TextureDimension.Tex2D));
            }
            catch
            {
                // An unsupported shader must not leak the materials made for it.
                foreach (KeywordMaterials set in materials)
                    set.Dispose();
                throw;
            }
        }

        /// <summary>Records the blur and returns the blurred texture.</summary>
        /// <param name="graph">The current URP RenderGraph.</param>
        /// <param name="source">
        /// A texture RenderGraph describes: one it created, or an imported
        /// RenderTexture. Optionally <see cref="BlurSource.WithViewport"/>.
        /// Blur other textures, such as Texture2D assets, with <see cref="Blur(Texture, BlurTarget, BlurStrength)"/>.
        /// </param>
        /// <param name="strength">Sigma in source pixels, or a <see cref="BlurStrength"/>.</param>
        /// <returns>The blurred texture, or the source itself when the blur would not change it.</returns>
        public TextureHandle Record(RenderGraph graph, BlurSource source, BlurStrength strength)
        {
            CheckRecorder(graph);
            return RecordSource(graph, source, strength, Output.New);
        }

        /// <summary>
        /// Records the blur into a new texture that is smaller from sigma 4.5:
        /// by the largest power of two up to the blur's reduction that divides
        /// both sides of the active image, so an odd side keeps full size.
        /// Sample it bilinearly with UVs 0–1.
        /// </summary>
        public TextureHandle RecordReduced(RenderGraph graph, BlurSource source, BlurStrength strength)
        {
            CheckRecorder(graph);
            return RecordSource(graph, source, strength, Output.NewReduced);
        }

        /// <summary>Records the blur into <paramref name="destination"/>.</summary>
        /// <param name="destination">
        /// A texture the size of the source's active image, without MSAA or
        /// dynamic scaling.
        /// </param>
        /// <returns>The destination; later passes must declare a read of it.</returns>
        public TextureHandle RecordTo(RenderGraph graph, BlurSource source, TextureHandle destination,
            BlurStrength strength)
        {
            CheckRecorder(graph);
            if (!destination.IsValid())
                throw new ArgumentException("Blur destination texture is invalid.", nameof(destination));
            return RecordSource(graph, source, strength, Output.Into(destination));
        }

        /// <summary>Records the blur into <paramref name="target"/>, sizing its texture first.</summary>
        /// <returns>The target texture's handle; later passes must declare a read of it.</returns>
        /// <remarks>
        /// Use one target per camera. Sizing can recreate the texture's storage,
        /// so passes recorded earlier in the same graph must not read it, and
        /// the source must not be the target's texture.
        /// </remarks>
        public TextureHandle RecordTo(RenderGraph graph, BlurSource source, BlurTarget target, BlurStrength strength)
        {
            CheckRecorder(graph);
            if (target == null)
                throw new ArgumentNullException(nameof(target));
            target.ThrowIfDisposed();
            return RecordSource(graph, source, strength, Output.Into(target));
        }

        /// <summary>Releases the recorder's materials and scratch resources.</summary>
        /// <remarks>Textures passed in, including RecordTo destinations, are left intact.</remarks>
        public void Dispose()
        {
            if (disposed)
                return;

            disposed = true;
            foreach (KeywordMaterials set in materials)
                set.Dispose();
            DisposeShaderWarmup();
            DisposeTextureBlur();
        }

        /// <summary>The pass layouts, by sigma; README "How it works" describes each.</summary>
        enum Layout { Small, Transition, Wide }

        static Layout LayoutFor(float sigma) => sigma >= TransitionKernel.EndSigma ? Layout.Wide
            : sigma >= TransitionKernel.StartSigma ? Layout.Transition : Layout.Small;

        /// <returns>The final texture, or the source itself when nothing would change.</returns>
        TextureHandle RecordLayout(RenderGraph graph, in RecordContext context, float sigma)
        {
            if (context.input.width == 1 && context.input.height == 1)
                return context.source;
            switch (LayoutFor(sigma))
            {
                case Layout.Wide:
                    return RecordWide(graph, context, sigma);
                case Layout.Transition:
                    return RecordTransition(graph, context, sigma);
            }
            double variance = BlurMath.RequestedVariance(sigma);
            // Below this, every non-center tap rounds to zero weight.
            if (variance < 1e-12)
                return context.source;
            return RecordSmall(graph, context, sigma, variance);
        }

        /// <summary>
        /// Source pixels per reduced-output texel: the grid the layout's last
        /// bilinear magnification reads, which the viewer's sampling replaces.
        /// Halved until it divides both sides, so plain UVs cover the image.
        /// </summary>
        internal static int ReducedScale(float sigma, int width, int height)
        {
            int scale = LayoutFor(sigma) switch
            {
                Layout.Wide => WideReducedScale(sigma),
                Layout.Transition => TransitionKernel.ReconstructionPitch,
                _ => SmallReducedScale(sigma)
            };
            while (scale > 1 && (width % scale != 0 || height % scale != 0))
                scale /= 2;
            return scale;
        }

        void CheckRecorder(RenderGraph graph)
        {
            ThrowIfDisposed();
            if (graph == null)
                throw new ArgumentNullException(nameof(graph));
        }

        void ThrowIfDisposed()
        {
            if (disposed)
                throw new ObjectDisposedException(nameof(GaussianBlur));
        }
    }
}
