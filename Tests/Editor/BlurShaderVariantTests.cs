using NUnit.Framework;
using SteplessGaussianBlur.Editor;
using UnityEngine;
using UnityEngine.Experimental.Rendering;
using UnityEngine.Rendering;
using ShaderType = UnityEditor.Rendering.ShaderType;

namespace SteplessGaussianBlur.Tests
{
    /// <summary>Checks build stripping and the shader's keywords.</summary>
    public sealed class BlurShaderVariantTests
    {
        // 2D fragments survive every build; array fragments survive only with
        // XR, since the recorder rejects array sources outside XR rendering.
        [Test]
        public void ArrayFragmentsAreStrippedOnlyWithoutXr()
        {
            Assert.That(BlurShaderVariantStripper.IsUnusedVariant(ShaderType.Fragment, xr: false, texture2D: true), Is.False);
            Assert.That(BlurShaderVariantStripper.IsUnusedVariant(ShaderType.Fragment, xr: true, texture2D: true), Is.False);
            Assert.That(BlurShaderVariantStripper.IsUnusedVariant(ShaderType.Fragment, xr: true, texture2D: false), Is.False);
            Assert.That(BlurShaderVariantStripper.IsUnusedVariant(ShaderType.Fragment, xr: false, texture2D: false), Is.True);
        }

        // The 2D keyword is fragment-only, so no vertex variant carries it.
        // Stripping them by it would leave the shader without vertex programs,
        // and Unity would substitute its error shader in Players.
        [Test]
        public void VertexProgramsAreNeverStripped()
        {
            Assert.That(BlurShaderVariantStripper.IsUnusedVariant(ShaderType.Vertex, xr: false, texture2D: false), Is.False);
            Assert.That(BlurShaderVariantStripper.IsUnusedVariant(ShaderType.Vertex, xr: true, texture2D: false), Is.False);
        }

        [Test]
        public void StripperKeywordsExistInTheShader()
        {
            Shader shader = BlurSourceTests.LoadShader();
            Assert.That(shader.name, Is.EqualTo(BlurShaderVariantStripper.ShaderName));
            // An invalid keyword would read as disabled and silently strip used variants.
            Assert.That(new ShaderKeyword(shader, BlurShaderVariantStripper.Texture2DKeyword).IsValid(), Is.True);
            foreach (string[] set in GaussianBlur.Keywords)
                foreach (string keyword in set)
                    Assert.That(new ShaderKeyword(shader, keyword).IsValid(), Is.True, keyword);
        }
    }
}
