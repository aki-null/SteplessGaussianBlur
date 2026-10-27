using System;
using UnityEngine;
using UnityEngine.Experimental.Rendering;
using UnityEngine.Rendering;
using UnityEngine.Rendering.RenderGraphModule;

namespace SteplessGaussianBlur
{
    // Turns one layout step into one RenderGraph draw: picks the material
    // (keyword set) and shader pass, hands back the pass's property block to
    // fill, and binds the source when the graph executes.
    public sealed partial class GaussianBlur
    {
        // The halo: texels around every reduced texture. They hold real filtered
        // values of the clamp-extended original, not replicated reduced edges,
        // so later passes keep the original image's border rule.
        const int ReducedHalo = 3;

        static readonly int SourceId = Shader.PropertyToID("_SourceTex");
        static readonly int SourceSizeId = Shader.PropertyToID("_SourceSize");
        static readonly BaseRenderFunc<PassData, RasterGraphContext> Execute = ExecutePass;
        static readonly BaseRenderFunc<PublishData, RasterGraphContext> Publish = (data, context) => { };

        /// <summary>Shader passes, named exactly as in SteplessGaussianBlur.shader.</summary>
        /// <remarks>Indices are resolved by name at construction, so shader order is free.</remarks>
        internal enum ShaderPass
        {
            // Small layouts (Shaders/Small.hlsl).
            Gaussian2D2, Gaussian2D3,
            Horizontal, Vertical, Horizontal5, Vertical5, Horizontal8, Vertical8, Horizontal10, Vertical10, Horizontal12, Vertical12,
            Down, DownHalve, Up, LinearUp, Copy,
            // Transition layout (Shaders/Transition.hlsl).
            TransitionHalveBlend,
            TransitionGaussianH, TransitionGaussianV,
            // Wide layout (Shaders/Wide.hlsl).
            WideQuarter, WideHalve, WideGaussianH, WideGaussianV, WideCubic, WideFinal, WideLimit
        }

        /// <summary>The shader keyword set of each recorder material, as flags.</summary>
        [Flags]
        internal enum KeywordSet
        {
            None = 0,
            /// <summary>Float (not half) samples in 2D Gaussians.</summary>
            FullPrecision = 1,
            /// <summary>Output rounded to the nearest B10G11R11 value.</summary>
            PackedOutput = 2
        }

        // Selects 2D textures in URP's TEXTURE2D_X macros; arrays otherwise.
        internal const string Texture2DKeyword = "DISABLE_TEXTURE2D_X_ARRAY";

        /// <summary>Keywords of each <see cref="KeywordSet"/>, indexed by the enum.</summary>
        internal static readonly string[][] Keywords =
        {
            Array.Empty<string>(),
            new[] { "STEPLESS_FULL_PRECISION" },
            new[] { "STEPLESS_PACKED_OUTPUT" },
            new[] { "STEPLESS_FULL_PRECISION", "STEPLESS_PACKED_OUTPUT" }
        };

        // Immutable 2D/array materials select keywords without changing
        // global XR state or preventing RenderGraph from culling passes.
        // Indexed by KeywordSet.
        readonly KeywordMaterials[] materials;
        readonly int[] passIndices;
        // Taps of the pass being recorded, after sampler-grid rounding. The
        // property block copies them, so one scratch array serves every pass.
        readonly Vector4[] passTaps = new Vector4[SmallKernel.MaxFetches * 2];

        /// <summary>Tests: each recorded draw's property block (filled after the call) and bound caller texture.</summary>
        internal Action<MaterialPropertyBlock, Texture> PassRecorded;

        static KeywordMaterials[] CreateMaterials(Shader shader)
        {
            var created = new KeywordMaterials[Keywords.Length];
            for (int set = 0; set < created.Length; set++)
                created[set] = new KeywordMaterials(shader, Keywords[set]);
            return created;
        }

        KeywordMaterials Materials(KeywordSet set) => materials[(int)set];

        static int[] ResolvePasses(Material material)
        {
            string[] names = Enum.GetNames(typeof(ShaderPass));
            var indices = new int[names.Length];
            for (int i = 0; i < names.Length; i++)
            {
                indices[i] = material.FindPass(names[i]);
                if (indices[i] >= 0)
                    continue;
                // An unsupported shader is replaced by Unity's error shader,
                // so list what the material actually has.
                var found = new string[material.passCount];
                for (int pass = 0; pass < found.Length; pass++)
                    found[pass] = material.GetPassName(pass);
                throw new ArgumentException($"Blur shader {material.shader.name} has no pass named {names[i]}; " +
                    $"found [{string.Join(", ", found)}]. An unsupported or failed shader is replaced " +
                    "by Unity's error shader.", "shader");
            }
            return indices;
        }

        readonly struct KeywordMaterials
        {
            readonly Material array;
            readonly Material texture2D;
            internal Shader shader => array.shader;

            internal KeywordMaterials(Shader shader, string[] keywords)
            {
                array = CoreUtils.CreateEngineMaterial(shader);
                texture2D = CoreUtils.CreateEngineMaterial(shader);
                texture2D.EnableKeyword(Texture2DKeyword);
                foreach (string keyword in keywords)
                {
                    array.EnableKeyword(keyword);
                    texture2D.EnableKeyword(keyword);
                }
            }

            internal Material Select(TextureDimension dimension)
                => dimension == TextureDimension.Tex2D ? texture2D : array;

            internal void Dispose()
            {
                CoreUtils.Destroy(array);
                CoreUtils.Destroy(texture2D);
            }
        }

        // RenderGraph pools pass data and returns it only after the graph has
        // executed (or culled the pass), so each block lives as long as its
        // draw needs it. Pooled data of a culled pass keeps its references
        // until reused.
        sealed class PassData
        {
            internal TextureHandle source;
            internal TextureHandle target;
            internal Material material;
            internal int shaderPass;
            internal MaterialPropertyBlock properties;
            internal Texture sourceTexture;
        }

        /// <summary>The caller's destination, or a new texture at the output scale.</summary>
        TextureHandle FinalTarget(RenderGraph graph, in RecordContext context, string name)
        {
            if (context.destination.IsValid())
                return context.destination;
            int scale = context.outputScale;
            return CreateTarget(graph, context, context.input.width / scale,
                context.input.height / scale, name, fullResolution: scale == 1);
        }

        TextureHandle CreateTarget(RenderGraph graph, in RecordContext context,
            int width, int height, string name, bool fullResolution = false)
        {
            TextureDesc target = context.input;
            target.width = width;
            target.height = height;
            target.sizeMode = TextureSizeMode.Explicit;
            target.name = name;
            target.depthBufferBits = DepthBits.None;
            target.clearBuffer = false;
            target.msaaSamples = MSAASamples.None;
            target.bindTextureMS = false;
            target.useDynamicScale = false;
            target.useDynamicScaleExplicit = false;
            target.memoryless = RenderTextureMemoryless.None;
            // Raster attachments do not need UAV capability. Inheriting it
            // from a compute-produced source can prevent render-target
            // compression on tile-based mobile GPUs.
            target.enableRandomWrite = false;
            target.filterMode = FilterMode.Bilinear;
            target.wrapMode = TextureWrapMode.Clamp;
            target.useMipMap = false;
            target.autoGenerateMips = false;
            // Reuse the full-size allocation through automatic hardware DRS.
            // Reduced targets have fixed-width halos and cannot simply scale
            // them with the image. Their dimensions are explicit.
            if (fullResolution && context.retainHardwareScaling)
            {
                target.width = context.sourceAllocationSize.x;
                target.height = context.sourceAllocationSize.y;
                target.useDynamicScale = true;
            }
            return graph.CreateTexture(target);
        }

        sealed class PublishData
        {
        }

        /// <summary>
        /// Declares a read of a written destination, which ends the native
        /// render pass that wrote it (PassesData.CanMerge, NextPassReadsTexture).
        /// Its contents are then stored before draws that sample it through a
        /// material bound outside the graph, such as a BlurTarget's.
        /// </summary>
        static void RecordPublish(RenderGraph graph, TextureHandle destination)
        {
            using (var builder = graph.AddRasterRenderPass<PublishData>("Stepless Publish", out _))
            {
                builder.UseTexture(destination, AccessFlags.Read);
                builder.AllowPassCulling(false);
                builder.SetRenderFunc(Publish);
            }
        }

        static int DivideUp(int value, int divisor) => (value - 1) / divisor + 1;

        /// <summary>Copies the active source into the final target with one exact texel load.</summary>
        TextureHandle RecordIdentity(RenderGraph graph, in RecordContext context)
        {
            return RecordSmallPass(graph, context, context.source, FinalTarget(graph, context, "Stepless Texture Copy"),
                ShaderPass.Copy, Array.Empty<Vector4>(), count: 0, "Stepless Texture Copy");
        }

        /// <summary>Records one draw of <paramref name="pass"/>.</summary>
        /// <param name="fullPrecision">Sample 2D Gaussians in float.</param>
        /// <returns>The draw's cleared property block; fill it before the graph executes.</returns>
        MaterialPropertyBlock RecordDraw(RenderGraph graph, in RecordContext context, TextureHandle source,
            TextureHandle target, ShaderPass pass, string name, bool fullPrecision = false)
        {
            KeywordSet set = fullPrecision ? KeywordSet.FullPrecision : KeywordSet.None;
            // Rounds before a B10G11R11 store; see PassOutput in Common.hlsl.
            if (context.Describe(graph, target).format == GraphicsFormat.B10G11R11_UFloatPack32)
                set |= KeywordSet.PackedOutput;
            Material selectedMaterial = Materials(set).Select(context.Describe(graph, source).dimension);
            Texture sourceTexture = source.Equals(context.source) ? context.sourceTexture : null;
            int shaderPass = passIndices[(int)pass];
            PassData recorded;
            using (var builder = graph.AddRasterRenderPass<PassData>(name, out PassData data))
            {
                recorded = data;
                FillPass(data, source, target, selectedMaterial, shaderPass, sourceTexture);
                // Bound directly; not a graph resource.
                if (sourceTexture == null)
                    builder.UseTexture(source, AccessFlags.Read);
                builder.SetRenderAttachment(target, 0, AccessFlags.WriteAll);
                builder.SetRenderFunc(Execute);
            }
            PassRecorded?.Invoke(recorded.properties, sourceTexture);
            return recorded.properties;
        }

        static void FillPass(PassData data, TextureHandle source, TextureHandle target,
            Material selectedMaterial, int shaderPass, Texture sourceTexture)
        {
            data.source = source;
            data.target = target;
            data.material = selectedMaterial;
            data.shaderPass = shaderPass;
            data.properties ??= new MaterialPropertyBlock();
            data.properties.Clear();
            data.sourceTexture = sourceTexture;
        }

        static void ExecutePass(PassData pass, RasterGraphContext context)
        {
            BindSource(pass);
            context.cmd.DrawProcedural(Matrix4x4.identity, pass.material,
                pass.shaderPass, MeshTopology.Triangles, 3, 1, pass.properties);
            pass.sourceTexture = null;
        }

        // Resolve TextureHandles inside the RenderGraph callback, after the
        // graph has assigned the physical textures for this pass.
        static void BindSource(PassData pass)
        {
            RTHandle target = pass.target;
            Texture texture = pass.sourceTexture;
            if (texture == null)
            {
                RTHandle source = pass.source;
                // Separately imported handles of one texture cannot be told
                // apart while recording. This guards each draw.
                ValidatePhysicalAlias(source.nameID, target.nameID);
                texture = source;
            }
            else
                ValidatePhysicalAlias(new RenderTargetIdentifier(texture), target.nameID);
            pass.properties.SetTexture(SourceId, texture);
        }

        internal static void ValidatePhysicalAlias(RenderTargetIdentifier source, RenderTargetIdentifier target)
        {
            if (source.Equals(target))
                throw new ArgumentException("Blur source and destination must be distinct.", nameof(target));
        }
    }
}
