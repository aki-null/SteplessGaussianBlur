using System;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;

namespace SteplessGaussianBlur
{
    /// <summary>
    /// Controls the camera blur through the active Volume stack.
    /// Add this override to a Volume Profile to enable the renderer feature.
    /// </summary>
    [Serializable, VolumeComponentMenu("Post-processing/Stepless Gaussian Blur")]
    [SupportedOnRenderPipeline(typeof(UniversalRenderPipelineAsset))]
    public sealed class SteplessGaussianBlurVolume : VolumeComponent, IPostProcessComponent
    {
        /// <summary>
        /// Gaussian standard deviation in pixels when the camera's shorter side equals
        /// the renderer feature's Reference Resolution.
        /// </summary>
        [Tooltip("Gaussian standard deviation in pixels at the renderer feature's Reference Resolution.")]
        public MinFloatParameter blurStrength = new MinFloatParameter(0f, 0f);

        /// <summary>Returns whether this blended Volume setting needs a blur pass.</summary>
        public bool IsActive() => active && blurStrength.value > 0f;
    }
}
