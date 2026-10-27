using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.RenderGraphModule;

namespace SteplessGaussianBlur
{
    // Passes of sigma 11.5 to 24; their chain and coefficients are in
    // Kernels/TransitionKernel.cs, shaders in Shaders/Transition.hlsl. The
    // pyramid's cubic Up and the wide layout's Final reconstruct, so a
    // reduced output ends at 1/4 as in the wide layout.
    public sealed partial class GaussianBlur
    {
        static readonly int TransitionSourceOriginId = Shader.PropertyToID("_TransitionSourceOrigin");
        static readonly int TransitionTargetOriginId = Shader.PropertyToID("_TransitionTargetOrigin");
        static readonly int TransitionHalveId = Shader.PropertyToID("_TransitionHalve");
        static readonly int TransitionGaussianId = Shader.PropertyToID("_TransitionGaussian");
        static readonly int TransitionGaussianCountId = Shader.PropertyToID("_TransitionGaussianCount");

        readonly TransitionKernel transitionKernel = new TransitionKernel();

        /// <summary>A transition texture and the texel where its active image starts.</summary>
        /// <remarks>The origin is the halo on each axis; the two may differ.</remarks>
        readonly struct Stage
        {
            internal readonly TextureHandle texture;
            internal readonly Vector2Int origin;

            internal Stage(TextureHandle texture, Vector2Int origin)
            {
                this.texture = texture;
                this.origin = origin;
            }
        }

        TextureHandle RecordTransition(RenderGraph graph, in RecordContext context, float sigma)
        {
            transitionKernel.Prepare(sigma, context.outputScale);
            TextureDesc input = context.input;
            int quarterWidth = DivideUp(input.width, 4);
            int quarterHeight = DivideUp(input.height, 4);
            int eighthWidth = DivideUp(input.width, 8);
            int eighthHeight = DivideUp(input.height, 8);
            const int h = ReducedHalo;

            // The wide layout's Quarter: the same row as TransitionKernel.Quarter,
            // with its taps as shader constants.
            TextureHandle reduced = CreateTarget(graph, context, quarterWidth + 2 * h, quarterHeight + 2 * h,
                "Stepless Transition Quarter");
            DrawWidePass(graph, context, context.source, reduced, ShaderPass.WideQuarter,
                sourceHalo: 0, targetHalo: h, "Stepless Transition Quarter");
            var quarter = new Stage(reduced, new Vector2Int(h, h));
            Stage eighth;
            if (sigma < TransitionKernel.SettledSigma)
            {
                eighth = TransitionStage(graph, context, quarter, ShaderPass.TransitionHalveBlend,
                    eighthWidth, eighthHeight, new Vector2Int(h, h), "Stepless Transition Halve");
            }
            else
            {
                // Settled into the binomial: the wide layout's fixed-weight halve.
                TextureHandle halved = CreateTarget(graph, context, eighthWidth + 2 * h, eighthHeight + 2 * h,
                    "Stepless Transition Halve");
                DrawWidePass(graph, context, quarter.texture, halved, ShaderPass.WideHalve,
                    sourceHalo: h, targetHalo: h, "Stepless Transition Halve");
                eighth = new Stage(halved, new Vector2Int(h, h));
            }
            // The Gaussian's two phase rows need one extra padded texel on each
            // side. Each axis pass widens only its own axis's halo.
            Stage gaussianX = TransitionStage(graph, context, eighth, ShaderPass.TransitionGaussianH,
                eighthWidth, eighthHeight, new Vector2Int(h + 1, h), "Stepless Transition Gaussian X");
            Stage gaussianY = TransitionStage(graph, context, gaussianX, ShaderPass.TransitionGaussianV,
                eighthWidth, eighthHeight, new Vector2Int(h + 1, h + 1), "Stepless Transition Gaussian Y");
            const int pitch = TransitionKernel.ReconstructionPitch;
            if (context.outputScale == pitch)
                return RecordSmallPass(graph, context, gaussianY.texture,
                    FinalTarget(graph, context, "Stepless Transition Up"), ShaderPass.Up, SmallKernel.CubicUp,
                    count: 2, "Stepless Transition Up", sourceHalo: h + 1, targetHalo: 0);
            TextureHandle up = CreateTarget(graph, context, DivideUp(input.width, pitch) + 2 * h,
                DivideUp(input.height, pitch) + 2 * h, "Stepless Transition Up");
            RecordSmallPass(graph, context, gaussianY.texture, up, ShaderPass.Up, SmallKernel.CubicUp, count: 2,
                "Stepless Transition Up", sourceHalo: h + 1, targetHalo: h);
            TextureHandle result = FinalTarget(graph, context, "Stepless Transition Final");
            return DrawFinal(graph, context, up, result, pitch / context.outputScale, "Stepless Transition Final");
        }

        /// <summary>Allocates an active image of the given size plus its halo, and draws into it.</summary>
        Stage TransitionStage(RenderGraph graph, in RecordContext context, Stage source, ShaderPass pass,
            int width, int height, Vector2Int origin, string name)
        {
            TextureHandle target = CreateTarget(graph, context, width + 2 * origin.x, height + 2 * origin.y, name);
            DrawTransitionPass(graph, context, source, target, origin, pass, name);
            return new Stage(target, origin);
        }

        /// <param name="targetOrigin">Texel of the target's active-image origin (its halo).</param>
        void DrawTransitionPass(RenderGraph graph, in RecordContext context, Stage source,
            TextureHandle target, Vector2Int targetOrigin, ShaderPass pass, string name)
        {
            MaterialPropertyBlock block = DrawReduced(graph, context, source.texture, target, pass, name);
            block.SetVector(TransitionSourceOriginId, new Vector4(source.origin.x, source.origin.y, 0, 0));
            block.SetVector(TransitionTargetOriginId, new Vector4(targetOrigin.x, targetOrigin.y, 0, 0));
            // Each block belongs to one deferred draw. Upload only the
            // coefficients that its shader pass reads.
            switch (pass)
            {
                case ShaderPass.TransitionHalveBlend:
                    block.SetVectorArray(TransitionHalveId, transitionKernel.Halve);
                    break;
                case ShaderPass.TransitionGaussianH:
                case ShaderPass.TransitionGaussianV:
                    block.SetVectorArray(TransitionGaussianId, transitionKernel.Gaussian);
                    block.SetInteger(TransitionGaussianCountId, transitionKernel.GaussianCount);
                    break;
            }
        }
    }
}
