using System;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.Experimental.Rendering;
using UnityEngine.Rendering;
using KeywordSet = SteplessGaussianBlur.GaussianBlur.KeywordSet;

namespace SteplessGaussianBlur.Tests
{
    /// <summary>Checks explicit warmup's format selection, reuse and resource lifetime.</summary>
    public sealed class BlurShaderWarmupTests
    {
        static Shader LoadShader() => BlurSourceTests.LoadShader();

        // Materials that RecordSmallPass and DrawReduced can select for these formats.
        static KeywordSet[] SelectableMaterials(GraphicsFormat source, GraphicsFormat destination)
        {
            GraphicsFormat intermediate = StorageFormat.Intermediate(source, destination);
            bool fullPrecision = StorageFormat.NeedsFullPrecision(source) || StorageFormat.NeedsFullPrecision(intermediate);
            bool packed = intermediate == GraphicsFormat.B10G11R11_UFloatPack32 ||
                destination == GraphicsFormat.B10G11R11_UFloatPack32;
            var sets = new System.Collections.Generic.List<KeywordSet> { KeywordSet.None };
            if (fullPrecision)
                sets.Add(KeywordSet.FullPrecision);
            if (packed)
                sets.Add(KeywordSet.PackedOutput);
            if (fullPrecision && packed)
                sets.Add(KeywordSet.FullPrecision | KeywordSet.PackedOutput);
            return sets.ToArray();
        }

        static void AssertCoversMaterials(Shader shader, ShaderVariantCollection variants,
            GraphicsFormat source, GraphicsFormat destination)
        {
            KeywordSet[] materials = SelectableMaterials(source, destination);
            Assert.That(variants.variantCount, Is.EqualTo(materials.Length),
                "Each selectable material needs exactly one 2D variant.");
            foreach (KeywordSet set in materials)
            {
                string[] keywords = GaussianBlur.Keywords[(int)set];
                var twoD = new string[keywords.Length + 1];
                keywords.CopyTo(twoD, 0);
                twoD[keywords.Length] = "DISABLE_TEXTURE2D_X_ARRAY";
                Assert.That(variants.Contains(new ShaderVariantCollection.ShaderVariant(
                    shader, PassType.Normal, twoD)), Is.True, string.Join(", ", twoD));
            }
        }

        [TestCase(GraphicsFormat.R8G8B8A8_UNorm)]
        [TestCase(GraphicsFormat.R32G32B32A32_SFloat)]
        [TestCase(GraphicsFormat.R16G16B16A16_UNorm)]
        [TestCase(GraphicsFormat.B10G11R11_UFloatPack32)]
        public void FormatCollectionsAreCachedAndReleased(GraphicsFormat format)
        {
            if (!SystemInfo.IsFormatSupported(format, GraphicsFormatUsage.Render))
                Assert.Ignore($"{format} is not renderable here.");
            Shader shader = LoadShader();
            var blur = new GaussianBlur(shader);
            ShaderVariantCollection variants;
            try
            {
                variants = blur.GetWarmupVariants(format, format);
                Assert.That(variants.shaderCount, Is.EqualTo(1));
                AssertCoversMaterials(shader, variants, format, format);
                Assert.That(blur.GetWarmupVariants(format, format), Is.SameAs(variants));
            }
            finally
            {
                blur.Dispose();
            }
            Assert.That(variants == null, Is.True, "Disposal must release the native collection.");
        }

        [Test]
        public void MixedFloatSourceAndHalfStorageCoversBothMaterialFamilies()
        {
            Shader shader = LoadShader();
            using (var blur = new GaussianBlur(shader))
            {
                ShaderVariantCollection variants = blur.GetWarmupVariants(
                    GraphicsFormat.R32G32B32A32_SFloat, GraphicsFormat.R16G16B16A16_SFloat);
                AssertCoversMaterials(shader, variants,
                    GraphicsFormat.R32G32B32A32_SFloat, GraphicsFormat.R16G16B16A16_SFloat);
                blur.WarmUpShaders(GraphicsFormat.R32G32B32A32_SFloat, GraphicsFormat.R16G16B16A16_SFloat);
            }
        }

        [Test]
        public void XrWarmupIncludesBothStereoModes()
        {
            Shader shader = LoadShader();
            using (var blur = new GaussianBlur(shader))
            {
                ShaderVariantCollection variants = blur.GetWarmupVariants(GraphicsFormat.R16G16B16A16_SFloat, GraphicsFormat.R16G16B16A16_SFloat, true);
                // Per material: 2D, array without stereo (a non-XR camera in an
                // XR app), instanced and multiview arrays, and 2D under each stereo mode.
                int materials = SelectableMaterials(GraphicsFormat.R16G16B16A16_SFloat,
                    GraphicsFormat.R16G16B16A16_SFloat).Length;
                Assert.That(variants.variantCount, Is.EqualTo(materials * 6));
                foreach (string keyword in new[] { "STEREO_INSTANCING_ON", "STEREO_MULTIVIEW_ON" })
                    Assert.That(variants.Contains(new ShaderVariantCollection.ShaderVariant(shader,
                        PassType.Normal, keyword)), Is.True);
                blur.WarmUpShaders(GraphicsFormat.R16G16B16A16_SFloat, true);
            }
        }

        [Test]
        public void WarmupRestoresActiveTargetAndRejectsInvalidRequests()
        {
            using (var blur = new GaussianBlur(LoadShader()))
            {
                RenderTexture previous = RenderTexture.active;
                RenderTexture target = RenderTexture.GetTemporary(4, 4, 0, RenderTextureFormat.ARGBHalf);
                try
                {
                    RenderTexture.active = target;
                    blur.WarmUpShaders(GraphicsFormat.R16G16B16A16_SFloat);
                    Assert.That(RenderTexture.active, Is.SameAs(target));
                    Assert.Throws<ArgumentException>(() => blur.WarmUpShaders(GraphicsFormat.None));
                    Assert.That(RenderTexture.active, Is.SameAs(target));
                    blur.Dispose();
                    Assert.Throws<ObjectDisposedException>(() =>
                        blur.WarmUpShaders(GraphicsFormat.R16G16B16A16_SFloat));
                }
                finally
                {
                    RenderTexture.active = previous;
                    RenderTexture.ReleaseTemporary(target);
                }
            }
        }
    }
}
