using System.Collections.Generic;
using System.Runtime.CompilerServices;
using UnityEditor.Build;
using UnityEditor.Rendering;
using UnityEngine;
using UnityEngine.Rendering;

[assembly: InternalsVisibleTo("SteplessGaussianBlur.Tests")]

namespace SteplessGaussianBlur.Editor
{
    /// <summary>
    /// Removes eye-array variants from Player builds without XR: the recorder
    /// rejects array sources outside XR rendering.
    /// </summary>
    internal sealed class BlurShaderVariantStripper : IPreprocessShaders
    {
        internal const string ShaderName = "Hidden/SteplessGaussianBlur";
        internal const string Texture2DKeyword = "DISABLE_TEXTURE2D_X_ARRAY";

        // Runs after Unity's built-in stripping, which removes the stereo
        // variants from builds without XR.
        public int callbackOrder => 0;

        public void OnProcessShader(Shader shader, ShaderSnippetData snippet,
            IList<ShaderCompilerData> variants)
        {
            if (shader.name != ShaderName)
                return;

            var texture2D = new ShaderKeyword(shader, Texture2DKeyword);
            var instancing = new ShaderKeyword("STEREO_INSTANCING_ON");
            var multiview = new ShaderKeyword("STEREO_MULTIVIEW_ON");
            bool xr = false;
            foreach (ShaderCompilerData variant in variants)
            {
                xr = variant.shaderKeywordSet.IsEnabled(instancing) || variant.shaderKeywordSet.IsEnabled(multiview);
                if (xr)
                    break;
            }
            for (int i = variants.Count - 1; i >= 0; i--)
            {
                if (IsUnusedVariant(snippet.shaderType, xr, variants[i].shaderKeywordSet.IsEnabled(texture2D)))
                    variants.RemoveAt(i);
            }
        }

        /// <param name="stage">The program's shader stage.</param>
        /// <param name="xr">Whether the build keeps stereo variants.</param>
        /// <param name="texture2D">Whether the variant samples a 2D texture, not an array.</param>
        /// <remarks>The 2D keyword is fragment-only; vertex programs never carry it.</remarks>
        internal static bool IsUnusedVariant(ShaderType stage, bool xr, bool texture2D)
            => stage == ShaderType.Fragment && !xr && !texture2D;
    }
}
