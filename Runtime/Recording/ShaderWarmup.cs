using System;
using UnityEngine;
using UnityEngine.Experimental.Rendering;
using UnityEngine.Rendering;
using UnityEngine.Rendering.RenderGraphModule;

namespace SteplessGaussianBlur
{
    public sealed partial class GaussianBlur
    {
        readonly ShaderVariantCollection[] warmupVariants = new ShaderVariantCollection[8];

        /// <summary>
        /// Prepares this blur's shader variants for a source format, for
        /// <see cref="Record(UnityEngine.Rendering.RenderGraphModule.RenderGraph, BlurSource, BlurStrength)"/>
        /// and a <see cref="BlurTarget"/>.
        /// </summary>
        /// <param name="includeXR">Also prepare instanced and multiview stereo variants.</param>
        /// <remarks>
        /// Call on the main thread during loading, once for each format you use.
        /// The first draw can still stall while the driver finishes.
        /// </remarks>
        public void WarmUpShaders(GraphicsFormat colorFormat, bool includeXR = false)
        {
            ThrowIfDisposed();
            ValidateDescriptor(new TextureDesc(4, 4) { colorFormat = colorFormat }, TextureRole.Source,
                nameof(colorFormat));
            WarmUpShaders(colorFormat, StorageFormat.For(colorFormat), includeXR);
        }

        /// <summary>Prepares blurs from a source format into a destination format you chose.</summary>
        /// <param name="includeXR">Also prepare instanced and multiview stereo variants.</param>
        /// <remarks>Call on the main thread during loading, once for each format pair you use.</remarks>
        public void WarmUpShaders(GraphicsFormat sourceFormat, GraphicsFormat destinationFormat, bool includeXR = false)
        {
            ThrowIfDisposed();
            ValidateDescriptor(new TextureDesc(4, 4) { colorFormat = sourceFormat }, TextureRole.Source,
                nameof(sourceFormat));
            ValidateDescriptor(new TextureDesc(4, 4) { colorFormat = destinationFormat }, TextureRole.Destination,
                nameof(destinationFormat));
            ShaderVariantCollection variants = GetWarmupVariants(sourceFormat, destinationFormat, includeXR);
            // Pipelines differ by render target format: passes before the
            // last render the intermediate format.
            GraphicsFormat intermediate = StorageFormat.Intermediate(sourceFormat, destinationFormat);
            foreach (bool eyeArray in includeXR ? new[] { false, true } : new[] { false })
            {
                WarmUpTarget(variants, intermediate, eyeArray);
                if (intermediate != destinationFormat)
                    WarmUpTarget(variants, destinationFormat, eyeArray);
            }
        }

        void WarmUpTarget(ShaderVariantCollection variants, GraphicsFormat colorFormat, bool eyeArray)
        {
            var descriptor = new RenderTextureDescriptor(4, 4)
            {
                graphicsFormat = colorFormat,
                depthStencilFormat = GraphicsFormat.None,
                msaaSamples = 1,
                dimension = eyeArray ? TextureDimension.Tex2DArray : TextureDimension.Tex2D,
                volumeDepth = eyeArray ? 2 : 1,
                useMipMap = false,
                enableRandomWrite = false
            };
            RenderTexture previous = RenderTexture.active;
            RenderTexture target = RenderTexture.GetTemporary(descriptor);
            try
            {
                RenderTexture.active = target;
                ShaderWarmup.WarmupShaderFromCollection(variants, Materials(KeywordSet.None).shader,
                    new ShaderWarmupSetup { vdecl = Array.Empty<VertexAttributeDescriptor>() });
            }
            finally
            {
                RenderTexture.active = previous;
                RenderTexture.ReleaseTemporary(target);
            }
        }

        internal ShaderVariantCollection GetWarmupVariants(GraphicsFormat sourceFormat,
            GraphicsFormat destinationFormat, bool includeXR = false)
        {
            GraphicsFormat intermediate = StorageFormat.Intermediate(sourceFormat, destinationFormat);
            // Passes sample the source and intermediates, never the destination.
            bool fullPrecision = StorageFormat.NeedsFullPrecision(sourceFormat) ||
                StorageFormat.NeedsFullPrecision(intermediate);
            // Passes rounding into packed float: intermediates or the last pass.
            bool packed = intermediate == GraphicsFormat.B10G11R11_UFloatPack32 ||
                destinationFormat == GraphicsFormat.B10G11R11_UFloatPack32;
            int index = (fullPrecision ? 1 : 0) + (includeXR ? 2 : 0) + (packed ? 4 : 0);
            ShaderVariantCollection variants = warmupVariants[index];
            if (variants != null)
                return variants;

            variants = new ShaderVariantCollection { hideFlags = HideFlags.HideAndDontSave };
            Shader shader = Materials(KeywordSet.None).shader;
            // A collection identifies a shader/pass type and keyword set, not
            // an individual named pass. Unity applies each set to matching
            // passes. These sets cover the recorder's materials for this format;
            // build stripping still removes impossible per-pass combinations.
            for (var set = KeywordSet.None; (int)set < Keywords.Length; set++)
            {
                if (!fullPrecision && (set & KeywordSet.FullPrecision) != 0 ||
                    !packed && (set & KeywordSet.PackedOutput) != 0)
                    continue;
                AddWarmupVariant(variants, shader, includeXR, Keywords[(int)set]);
            }
            warmupVariants[index] = variants;
            return variants;
        }

        static void AddWarmupVariant(ShaderVariantCollection variants, Shader shader,
            bool includeXR, string[] keywords)
        {
            var stereo = new string[keywords.Length + 1];
            Array.Copy(keywords, stereo, keywords.Length);
            stereo[keywords.Length] = Texture2DKeyword;
            variants.Add(new ShaderVariantCollection.ShaderVariant(shader, PassType.Normal, stereo));
            if (!includeXR)
                return;
            // Arrays without a stereo keyword: non-XR cameras in an XR app.
            variants.Add(new ShaderVariantCollection.ShaderVariant(shader, PassType.Normal, keywords));
            stereo[keywords.Length] = "STEREO_INSTANCING_ON";
            variants.Add(new ShaderVariantCollection.ShaderVariant(shader, PassType.Normal, stereo));
            stereo[keywords.Length] = "STEREO_MULTIVIEW_ON";
            variants.Add(new ShaderVariantCollection.ShaderVariant(shader, PassType.Normal, stereo));
            var monoscopic = new string[keywords.Length + 2];
            Array.Copy(keywords, monoscopic, keywords.Length);
            monoscopic[keywords.Length] = Texture2DKeyword;
            monoscopic[keywords.Length + 1] = "STEREO_INSTANCING_ON";
            variants.Add(new ShaderVariantCollection.ShaderVariant(shader, PassType.Normal, monoscopic));
            monoscopic[keywords.Length + 1] = "STEREO_MULTIVIEW_ON";
            variants.Add(new ShaderVariantCollection.ShaderVariant(shader, PassType.Normal, monoscopic));
        }

        void DisposeShaderWarmup()
        {
            foreach (ShaderVariantCollection variants in warmupVariants)
                CoreUtils.Destroy(variants);
        }
    }
}
