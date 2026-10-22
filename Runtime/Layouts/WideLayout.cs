using System;
using UnityEngine;
using UnityEngine.Experimental.Rendering;
using UnityEngine.Rendering;
using UnityEngine.Rendering.RenderGraphModule;

namespace SteplessGaussianBlur
{
    // Passes of sigma 24 and above; their chain, pitch P and reduced output
    // are described in Kernels/WideKernel.cs, shaders in Shaders/Wide.hlsl.
    public sealed partial class GaussianBlur
    {
        static readonly int WideSourceHaloId = Shader.PropertyToID("_WideSourceHalo");
        static readonly int WideTargetHaloId = Shader.PropertyToID("_WideTargetHalo");
        static readonly int WideScaleId = Shader.PropertyToID("_WideScale");
        static readonly int WideGaussianId = Shader.PropertyToID("_WideGaussian");
        static readonly int WideGaussianCountId = Shader.PropertyToID("_WideGaussianCount");
        static readonly int WideCubicId = Shader.PropertyToID("_WideCubic");

        readonly WideKernel wideKernel = new WideKernel();

        TextureHandle RecordWide(RenderGraph graph, in RecordContext context, float sigma)
        {
            TextureDesc input = context.input;
            // Under original-image clamp extension, an increasingly wide blur
            // approaches the mean of the four corner colors. Past 1024 image
            // extents its remaining spatial variation is below 0.1% of input
            // range. Avoid unbounded pitch growth for extreme finite strengths.
            if (sigma >= 1024.0 * Math.Max(input.width, input.height))
            {
                TextureHandle limit = FinalTarget(graph, context, "Stepless Wide Limit");
                DrawReduced(graph, context, context.source, limit, ShaderPass.WideLimit, "Stepless Wide Limit");
                return limit;
            }

            wideKernel.Prepare(sigma, context.outputScale);
            const int h = ReducedHalo;
            int width = DivideUp(input.width, 4);
            int height = DivideUp(input.height, 4);
            TextureHandle current = CreateTarget(graph, context, width + 2 * h, height + 2 * h,
                "Stepless Wide Quarter");
            DrawWidePass(graph, context, context.source, current, ShaderPass.WideQuarter,
                sourceHalo: 0, targetHalo: h, "Stepless Wide Quarter");
            for (int pitch = 8; pitch <= wideKernel.Pitch; pitch *= 2)
            {
                width = DivideUp(width, 2);
                height = DivideUp(height, 2);
                TextureHandle next = CreateTarget(graph, context, width + 2 * h, height + 2 * h,
                    "Stepless Wide Halve");
                DrawWidePass(graph, context, current, next, ShaderPass.WideHalve,
                    sourceHalo: h, targetHalo: h, "Stepless Wide Halve");
                current = next;
            }

            TextureHandle horizontal = CreateTarget(graph, context, width + 2 * h,
                height + 2 * h, "Stepless Wide Gaussian X");
            DrawWidePass(graph, context, current, horizontal, ShaderPass.WideGaussianH,
                sourceHalo: h, targetHalo: h, "Stepless Wide Gaussian X");
            TextureHandle vertical = CreateTarget(graph, context, width + 2 * h,
                height + 2 * h, "Stepless Wide Gaussian Y");
            DrawWidePass(graph, context, horizontal, vertical, ShaderPass.WideGaussianV,
                sourceHalo: h, targetHalo: h, "Stepless Wide Gaussian Y");

            int finePitch = wideKernel.ReconstructionPitch;
            if (context.outputScale == finePitch)
                return DrawWidePass(graph, context, vertical, FinalTarget(graph, context, "Stepless Wide Cubic"),
                    ShaderPass.WideCubic, sourceHalo: h, targetHalo: 0, "Stepless Wide Cubic");
            TextureHandle cubic = CreateTarget(graph, context, DivideUp(input.width, finePitch) + 2 * h,
                DivideUp(input.height, finePitch) + 2 * h, "Stepless Wide Cubic");
            DrawWidePass(graph, context, vertical, cubic, ShaderPass.WideCubic,
                sourceHalo: h, targetHalo: h, "Stepless Wide Cubic");
            TextureHandle result = FinalTarget(graph, context, "Stepless Wide Final");
            return DrawFinal(graph, context, cubic, result, FinalScale, "Stepless Wide Final");
        }

        TextureHandle DrawWidePass(RenderGraph graph, in RecordContext context, TextureHandle source,
            TextureHandle target, ShaderPass pass, int sourceHalo, int targetHalo, string name)
        {
            MaterialPropertyBlock block = DrawReduced(graph, context, source, target, pass, name);
            block.SetInteger(WideSourceHaloId, sourceHalo);
            block.SetInteger(WideTargetHaloId, targetHalo);
            switch (pass)
            {
                case ShaderPass.WideGaussianH:
                case ShaderPass.WideGaussianV:
                    block.SetVectorArray(WideGaussianId, wideKernel.Gaussian);
                    block.SetInteger(WideGaussianCountId, wideKernel.GaussianCount);
                    break;
                case ShaderPass.WideCubic:
                    block.SetVectorArray(WideCubicId, wideKernel.Cubic);
                    break;
            }
            return target;
        }

        /// <summary>
        /// Magnifies a grid with the reduced halo onto the output by a power
        /// of two: output pixel x reads (x + 0.5) / scale. Ends the transition
        /// and wide layouts.
        /// </summary>
        TextureHandle DrawFinal(RenderGraph graph, in RecordContext context, TextureHandle source,
            TextureHandle target, int scale, string name)
        {
            MaterialPropertyBlock block = DrawReduced(graph, context, source, target, ShaderPass.WideFinal, name);
            block.SetInteger(WideSourceHaloId, ReducedHalo);
            block.SetInteger(WideScaleId, scale);
            return target;
        }

        /// <summary>
        /// A reduced output's scale: the cubic's grid, at most 128, past which
        /// the viewer's bilinear phases, multiples of 1/(2 * scale), fall
        /// between the sampler's 1/256 steps.
        /// </summary>
        static int WideReducedScale(float sigma)
            => Math.Min(WideKernel.PitchFor(sigma) / 4, BlurMath.SamplerSubtexels / 2);

        /// <summary>Final's magnification: from the cubic's grid to the output's.</summary>
        int FinalScale => wideKernel.ReconstructionPitch / wideKernel.OutputScale;

        /// <summary>Records a transition or wide pass.</summary>
        /// <returns>The draw's property block, holding the source size.</returns>
        MaterialPropertyBlock DrawReduced(RenderGraph graph, in RecordContext context, TextureHandle source,
            TextureHandle target, ShaderPass pass, string name)
        {
            TextureDesc input = context.Describe(graph, source);
            MaterialPropertyBlock block = RecordDraw(graph, context, source, target, pass, name);
            block.SetVector(SourceSizeId, context.SamplingSize(source, input));
            return block;
        }
    }
}
