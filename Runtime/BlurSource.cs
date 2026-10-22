using UnityEngine;
using UnityEngine.Rendering.RenderGraphModule;

namespace SteplessGaussianBlur
{
    /// <summary>
    /// The RenderGraph texture to blur, optionally limited to an active region.
    /// Converts implicitly from a <see cref="TextureHandle"/>.
    /// </summary>
    public readonly struct BlurSource
    {
        internal readonly TextureHandle handle;
        // Set for GaussianBlur.Blur sources: bound directly, not through the graph.
        internal readonly Texture texture;

        /// <summary>Active image size in current texels, or zero for the whole view.</summary>
        internal Vector2Int ViewportSize { get; }

        /// <summary>Sampleable texture-view size in current texels, or zero to derive it.</summary>
        internal Vector2Int TextureSize { get; }

        BlurSource(TextureHandle handle, Texture texture, Vector2Int viewportSize, Vector2Int textureSize)
        {
            this.handle = handle;
            this.texture = texture;
            ViewportSize = viewportSize;
            TextureSize = textureSize;
        }

        /// <summary>A texture in the current RenderGraph.</summary>
        public static implicit operator BlurSource(TextureHandle handle) => new BlurSource(handle, null, default, default);

        internal static BlurSource FromTexture(Texture texture) => new BlurSource(default, texture, default, default);

        /// <summary>Blurs only the top-left active region of the texture.</summary>
        /// <param name="viewportSize">Active image size in current source texels.</param>
        /// <param name="textureSize">
        /// Current sampleable texture-view size. Omit for ordinary or automatically
        /// scaled textures. Supply it for custom views, and always for explicit
        /// dynamic scaling: RenderGraph does not report it for imported textures.
        /// </param>
        public BlurSource WithViewport(Vector2Int viewportSize, Vector2Int textureSize = default)
            => new BlurSource(handle, texture, viewportSize, textureSize);

        internal bool HasViewport => ViewportSize != default || TextureSize != default;
    }
}
