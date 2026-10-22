using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;
using UnityEngine.TestTools;

namespace SteplessGaussianBlur.Tests
{
    public sealed partial class BlurGpuTests
    {
        // A new camera's first frames depend on history it does not have yet.
        static void RenderSettled(Camera camera)
        {
            for (int i = 0; i < 3; i++)
                camera.Render();
        }

        // A span cannot be enumerated in an iterator method.
        static ScriptableRendererData[] RendererData(UniversalRenderPipelineAsset asset) =>
            asset.rendererDataList.ToArray();

        // The camera effect end to end: the feature reads the Volume, blurs
        // the camera color and hands it on. A lone camera publishes the blur
        // as its color; the base camera of a stack copies it back.
        [UnityTest]
        public IEnumerator FeatureBlursTheCameraColor([Values(false, true)] bool stacked) =>
            BlurCameraColor(stacked, multisampled: false);

        // Before post-processing, a multisampled camera color takes the blur
        // as a copy.
        [UnityTest]
        public IEnumerator FeatureCopiesTheBlurIntoMultisampledColor() =>
            BlurCameraColor(stacked: false, multisampled: true);

        IEnumerator BlurCameraColor(bool stacked, bool multisampled)
        {
            const int width = 64, height = 48;
            const float sigma = 3;
            var asset = (UniversalRenderPipelineAsset)GraphicsSettings.currentRenderPipeline;
            var feature = ScriptableObject.CreateInstance<SteplessGaussianBlurFeature>();
            if (multisampled)
            {
                // URP resolves the camera color for post-processing; draws
                // before it see the multisampled texture.
                typeof(SteplessGaussianBlurFeature)
                    .GetField("injectionPoint", BindingFlags.NonPublic | BindingFlags.Instance)
                    .SetValue(feature, RenderPassEvent.AfterRenderingOpaques);
            }
            var added = new List<ScriptableRendererData>();
            var disabled = new List<ScriptableRendererFeature>();
            var objects = new List<UnityEngine.Object> { feature };
            restoreSharedState = () =>
            {
                foreach (ScriptableRendererFeature other in disabled)
                    other.SetActive(true);
                foreach (ScriptableRendererData data in added)
                {
                    data.rendererFeatures.Remove(feature);
                    data.SetDirty();
                }
                feature.Dispose();
                foreach (UnityEngine.Object o in objects)
                    if (o != null)
                        UnityEngine.Object.DestroyImmediate(o);
            };
            foreach (ScriptableRendererData data in RendererData(asset))
            {
                if (data == null)
                    continue;
                // The project's renderer may already blur the camera.
                foreach (ScriptableRendererFeature other in data.rendererFeatures)
                {
                    if (other is SteplessGaussianBlurFeature && other.isActive)
                    {
                        other.SetActive(false);
                        disabled.Add(other);
                    }
                }
                data.rendererFeatures.Add(feature);
                data.SetDirty();
                added.Add(data);
            }

            var profile = ScriptableObject.CreateInstance<VolumeProfile>();
            objects.Add(profile);
            profile.Add<SteplessGaussianBlurVolume>();
            var volumeObject = new GameObject("Stepless Gaussian Feature Volume");
            objects.Add(volumeObject);
            var volume = volumeObject.AddComponent<Volume>();
            volume.isGlobal = true;
            volume.sharedProfile = profile;

            // A 2x2 white square on black, pixel-aligned at the center.
            var material = new Material(Shader.Find("Universal Render Pipeline/Unlit"));
            objects.Add(material);
            material.SetColor("_BaseColor", Color.white);
            GameObject quad = GameObject.CreatePrimitive(PrimitiveType.Quad);
            objects.Add(quad);
            quad.GetComponent<MeshRenderer>().sharedMaterial = material;
            quad.transform.SetPositionAndRotation(new Vector3(width / 2, height / 2, 0), Quaternion.identity);
            quad.transform.localScale = new Vector3(2, 2, 1);

            // A camera that targets a texture takes its sample count.
            RenderTexture color = CreateTarget(width, height, depthBits: 24, msaaSamples: multisampled ? 4 : 1);
            RenderTexture readable = multisampled ? CreateTarget(width, height) : color;
            var cameraObject = new GameObject("Stepless Gaussian Feature Camera");
            objects.Add(cameraObject);
            cameraObject.transform.position = new Vector3(width / 2, height / 2, -10);
            Camera view = cameraObject.AddComponent<Camera>();
            view.orthographic = true;
            view.orthographicSize = height / 2;
            view.clearFlags = CameraClearFlags.SolidColor;
            view.backgroundColor = Color.black;
            view.targetTexture = color;
            view.allowMSAA = multisampled;
            var viewData = cameraObject.AddComponent<UniversalAdditionalCameraData>();
            viewData.renderPostProcessing = true;
            if (stacked)
            {
                var overlayObject = new GameObject("Stepless Gaussian Feature Overlay");
                objects.Add(overlayObject);
                Camera overlay = overlayObject.AddComponent<Camera>();
                overlay.cullingMask = 0;
                overlayObject.AddComponent<UniversalAdditionalCameraData>().renderType = CameraRenderType.Overlay;
                viewData.cameraStack.Add(overlay);
            }

            // The renderers rebuild with the feature on their next frame.
            yield return null;
            // The reference blurs the camera's own unblurred frame, which
            // the project's post-processing has already graded. A strength
            // too small to blur keeps the pass, and with it URP's
            // intermediate color texture, so both frames take one path.
            SteplessGaussianBlurVolume settings = profile.components[0] as SteplessGaussianBlurVolume;
            settings.blurStrength.Override(1e-6f);
            RenderSettled(view);
            if (multisampled)
                Graphics.Blit(color, readable);
            Color[] input = null;
            yield return ReadBack(readable, pixels => input = pixels);
            // Strength is measured on the shorter side at the default
            // reference resolution of 1024.
            settings.blurStrength.Override(sigma * 1024 / height);
            RenderSettled(view);
            if (multisampled)
                Graphics.Blit(color, readable);
            Color[] actual = null;
            yield return ReadBack(readable, pixels => actual = pixels);

            Color[] expected = BlurGpuReference.Blur(input, width, height, sigma);
            float peak = 0;
            foreach (Color pixel in input)
                peak = Mathf.Max(peak, pixel.r);
            Assert.That(peak, Is.GreaterThan(0.1f), "The square must render.");
            // Camera color storage rounds the result.
            AssertPixels(actual, expected, 0.01f * peak, 0.002f * peak,
                stacked ? "stacked" : multisampled ? "multisampled" : "single camera");
        }
    }
}
