using UnityEngine;
using UnityEngine.Experimental.Rendering;
using UnityEngine.Rendering;
using UnityEngine.Rendering.RenderGraphModule;
using UnityEngine.Rendering.RenderGraphModule.Util;
using UnityEngine.Rendering.Universal;

namespace SteplessGaussianBlur
{
    /// <summary>
    /// Blurs the camera color with the Volume's Stepless Gaussian Blur strength.
    /// Add this feature to a Universal Renderer asset.
    /// </summary>
    // The pass publishes its output as the camera color instead of copying it
    // back, except where a camera stack's persistent target or multisampled
    // color needs the copy.
    [SupportedOnRenderer(typeof(UniversalRendererData))]
    [DisallowMultipleRendererFeature("Stepless Gaussian Blur")]
    public sealed class SteplessGaussianBlurFeature : ScriptableRendererFeature
    {
        [SerializeField, Min(1), InspectorName("Reference Resolution")]
        [Tooltip("Shorter image side, in pixels, at which Blur Strength is measured.")]
        int referenceResolution = 1024;

        [SerializeField]
        [Tooltip("When in the frame the camera color is blurred.")]
        RenderPassEvent injectionPoint = RenderPassEvent.AfterRenderingPostProcessing;

        [SerializeField]
        [Tooltip("High runs a full-resolution Gaussian below sigma 2.24, joining Fast by 2.4: slower, more accurate.")]
        BlurQuality quality = BlurQuality.Fast;

        BlurPass blurPass;
        GaussianBlur blur;

        /// <summary>Creates the reusable blur recorder and RenderGraph pass.</summary>
        public override void Create()
        {
            blur?.Dispose();
            blur = null;
            try
            {
                blur = new GaussianBlur();
                // Throws for an undefined serialized value; the catch frees the materials.
                blur.Quality = quality;
            }
            catch (System.Exception exception) when (exception is System.ArgumentException ||
                exception is System.InvalidOperationException)
            {
                // URP constructs renderers without guarding Create(). Disable
                // only this feature, for a missing or unsupported shader, rather
                // than every camera that uses the renderer.
                blur?.Dispose();
                blur = null;
                blurPass = null;
                Debug.LogWarning("Stepless Gaussian Blur is disabled: " + exception.Message);
                return;
            }
            blurPass = new BlurPass(blur)
            {
                renderPassEvent = injectionPoint,
                // The blur samples camera color, so URP must allocate a
                // readable texture before this RenderGraph pass runs.
                requiresIntermediateTexture = true
            };
        }

        /// <summary>Prepares shader variants for a camera-color format during loading.</summary>
        /// <param name="colorFormat">The camera color format at the feature's injection point.</param>
        /// <param name="includeXR">Also prepare instanced and multiview stereo variants.</param>
        /// <remarks>Call on the main thread during loading.</remarks>
        public void WarmUpShaders(GraphicsFormat colorFormat, bool includeXR = false)
        {
            // URP calls Create() only once its pipeline exists, which can be
            // after a loading script runs.
            if (blur == null)
                Create();
            if (blur == null)
                throw new System.InvalidOperationException("Stepless Gaussian Blur is disabled; see the logged warning.");
            blur.WarmUpShaders(colorFormat, includeXR);
        }

        /// <summary>
        /// Enqueues the blur when the camera renders post-processing and its
        /// blended Volume strength is nonzero.
        /// </summary>
        public override void AddRenderPasses(ScriptableRenderer renderer, ref RenderingData renderingData)
        {
            // Follow URP's own post-processing rule: cameras with Post
            // Processing off, previews and a Scene view with effects off skip it.
            if (blurPass == null || !renderingData.cameraData.postProcessEnabled)
                return;

            // URP selects and updates this camera's Volume stack before it
            // asks renderer features to enqueue their passes. Copy the value
            // now: the same pass object is reused for later cameras.
            SteplessGaussianBlurVolume settings =
                VolumeManager.instance.stack.GetComponent<SteplessGaussianBlurVolume>();
            if (settings == null || !settings.IsActive())
                return;

            blurPass.referenceSigma = settings.blurStrength.value;
            blurPass.referenceResolution = referenceResolution;
            renderer.EnqueuePass(blurPass);
        }

        /// <summary>Releases persistent GPU resources owned by this feature.</summary>
        protected override void Dispose(bool disposing)
        {
            blur?.Dispose();
            blur = null;
            blurPass = null;
        }

        sealed class BlurPass : ScriptableRenderPass
        {
            readonly GaussianBlur blur;
            internal float referenceSigma;
            internal int referenceResolution;
            bool warnedBackbuffer;

            internal BlurPass(GaussianBlur blur)
            {
                this.blur = blur;
                profilingSampler = new ProfilingSampler("Stepless Gaussian Blur");
            }

            public override void RecordRenderGraph(RenderGraph graph, ContextContainer frameData)
            {
                UniversalResourceData resources = frameData.Get<UniversalResourceData>();
                if (resources.isActiveTargetBackBuffer)
                {
                    if (!warnedBackbuffer)
                    {
                        Debug.LogWarning(
                            "Stepless Gaussian Blur requires an intermediate camera color texture at this injection point.");
                        warnedBackbuffer = true;
                    }
                    return;
                }

                TextureHandle source = resources.activeColorTexture;
                if (!source.IsValid())
                    return;

                TextureHandle output = blur.Record(graph, source,
                    BlurStrength.AtReferenceResolution(referenceSigma, referenceResolution));
                if (output.Equals(source))
                    return;

                UniversalCameraData camera = frameData.Get<UniversalCameraData>();
                bool multisampled = graph.GetTextureDesc(source).msaaSamples != MSAASamples.None;
                if (camera.resolveFinalTarget && !multisampled)
                {
                    // Later URP passes, including the final blit, read this
                    // handle. Publishing the blur output avoids a copyback.
                    resources.cameraColor = output;
                }
                else
                {
                    // A stack's next camera reads its persistent A/B target,
                    // and passes after an early injection point draw into a
                    // multisampled color with multisampled depth. Neither can
                    // take the single-sample output, so copy it back.
                    graph.AddBlitPass(output, source, Vector2.one, Vector2.zero,
                        passName: "Stepless Color Copy");
                }
            }
        }
    }
}
