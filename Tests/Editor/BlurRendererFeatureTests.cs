using NUnit.Framework;
using UnityEngine;
using UnityEngine.Experimental.Rendering;

namespace SteplessGaussianBlur.Tests
{
    /// <summary>Checks the renderer feature's setup and the Volume's activation rule.</summary>
    public sealed class BlurRendererFeatureTests
    {
        [Test]
        public void VolumeIsActiveOnlyWithPositiveStrength()
        {
            var volume = ScriptableObject.CreateInstance<SteplessGaussianBlurVolume>();
            try
            {
                Assert.That(volume.IsActive(), Is.False);
                volume.blurStrength.Override(2);
                Assert.That(volume.IsActive(), Is.True);
                volume.active = false;
                Assert.That(volume.IsActive(), Is.False);
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(volume);
            }
        }

        [Test]
        public void NewFeatureWarmsUpWithoutAssignment()
        {
            var feature = ScriptableObject.CreateInstance<SteplessGaussianBlurFeature>();
            try
            {
                feature.Create();
                Assert.DoesNotThrow(() => feature.WarmUpShaders(GraphicsFormat.R16G16B16A16_SFloat));
            }
            finally
            {
                feature.Dispose();
                UnityEngine.Object.DestroyImmediate(feature);
            }
        }

        [Test]
        public void RecreatingTheFeatureKeepsOneSetOfMaterials()
        {
            int before = Resources.FindObjectsOfTypeAll<Material>().Length;
            var feature = ScriptableObject.CreateInstance<SteplessGaussianBlurFeature>();
            try
            {
                feature.Create();
                int created = Resources.FindObjectsOfTypeAll<Material>().Length;
                Assert.That(created, Is.GreaterThan(before));
                feature.Create();
                Assert.That(Resources.FindObjectsOfTypeAll<Material>().Length, Is.EqualTo(created));
            }
            finally
            {
                feature.Dispose();
                UnityEngine.Object.DestroyImmediate(feature);
            }
            Assert.That(Resources.FindObjectsOfTypeAll<Material>().Length, Is.EqualTo(before));
        }
    }
}
