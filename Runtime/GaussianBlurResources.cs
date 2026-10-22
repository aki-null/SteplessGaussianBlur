using System;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;

namespace SteplessGaussianBlur
{
    /// <summary>
    /// The package shader, kept in URP's graphics settings so that Player
    /// builds include it without a scene or asset referencing it.
    /// </summary>
    [Serializable, HideInInspector]
    [SupportedOnRenderPipeline(typeof(UniversalRenderPipelineAsset))]
    sealed class GaussianBlurResources : IRenderPipelineResources
    {
        [SerializeField, ResourcePath("Runtime/Shaders/SteplessGaussianBlur.shader")]
        Shader shader;

        public int version => 0;

        bool IRenderPipelineGraphicsSettings.isAvailableInPlayerBuild => true;

        /// <summary>The package shader from the active pipeline's graphics settings.</summary>
        internal static Shader Load()
        {
            if (GraphicsSettings.TryGetRenderPipelineSettings(out GaussianBlurResources resources) &&
                resources.shader != null)
                return resources.shader;
            throw new InvalidOperationException(
                "The Stepless Gaussian Blur shader is missing from URP's graphics settings. Use URP as the " +
                "active render pipeline, or pass the shader to the constructor.");
        }
    }
}
