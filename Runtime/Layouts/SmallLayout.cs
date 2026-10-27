using System;
using UnityEngine;
using UnityEngine.Experimental.Rendering;
using UnityEngine.Rendering;
using UnityEngine.Rendering.RenderGraphModule;
using Crossfade = SteplessGaussianBlur.SmallKernel.Crossfade;

namespace SteplessGaussianBlur
{
    // Passes of sigma below 16.1; their chain and coefficients are in
    // Kernels/SmallKernel.cs and Kernels/FittedDownUp.cs, shaders in
    // Shaders/Small.hlsl. From sigma 4.5 the outermost Up is bilinear, and a
    // reduced output ends before it.
    public sealed partial class GaussianBlur
    {
        static readonly int TapsId = Shader.PropertyToID("_Taps");
        static readonly int CountId = Shader.PropertyToID("_Count");
        static readonly int SmallSourceHaloId = Shader.PropertyToID("_SmallSourceHalo");
        static readonly int SmallTargetHaloId = Shader.PropertyToID("_SmallTargetHalo");

        readonly SmallKernel.Workspace kernel = new SmallKernel.Workspace();

        TextureHandle RecordSmall(RenderGraph graph, in RecordContext context, float sigma, double variance)
        {
            TextureDesc input = context.input;
            // In high quality the full-res row runs to 2.40, crossfading from
            // 2.24 into the first pyramid level, which then replaces it.
            if (quality == BlurQuality.High && variance < FittedDownUp.End)
                return RecordGaussian(graph, context, context.source, input.width, input.height,
                    halo: 0, variance, Crossfade.IntoFirstLevel, FinalTarget(graph, context, "Stepless Gaussian"));
            // From sigma 1.46 this row crossfades into the fitted Down/Up,
            // which then replaces it at 1.62.
            if (variance < SmallKernel.DownUpPassesStart)
                return RecordGaussian(graph, context, context.source, input.width, input.height,
                    halo: 0, variance, Crossfade.IntoDownUp, FinalTarget(graph, context, "Stepless Gaussian"));
            if (variance < FittedDownUp.End)
                return RecordFittedDownUp(graph, context, variance);
            double bilinear = SmallKernel.OuterUpsampleBilinear(sigma);
            double upVariance = SmallKernel.OuterUpsample(kernel, bilinear);
            double levelVariance = SmallKernel.OuterLevelVariance(variance, upVariance);
            if (context.outputScale == 1)
                return RecordPyramidLevel(graph, context, context.source, input.width, input.height,
                    halo: 0, levelVariance, bilinear, FinalTarget(graph, context, "Stepless Pyramid Up"));

            // A reduced output is the outermost level's half-res result; the
            // viewer's bilinear sampling replaces its bilinear Up.
            TextureHandle half = RecordDown(graph, context, context.source, input.width, input.height,
                halo: 0, SmallKernel.BinomialDown, "Stepless Pyramid Down");
            return RecordPyramidLevel(graph, context, half, DivideUp(input.width, 2), DivideUp(input.height, 2),
                ReducedHalo, SmallKernel.CoarseVariance(levelVariance),
                output: FinalTarget(graph, context, "Stepless Pyramid Reduced"));
        }

        /// <summary>A reduced output's scale: 2 once the outermost Up is bilinear, which the viewer's sampling replaces.</summary>
        static int SmallReducedScale(float sigma) => SmallKernel.OuterUpsampleBilinear(sigma) == 1 ? 2 : 1;

        /// <summary>Sigma 1.62 to 2.40: Down, a half-res Gaussian from 1.72, Up.</summary>
        TextureHandle RecordFittedDownUp(RenderGraph graph, in RecordContext context, double variance)
        {
            TextureDesc input = context.input;
            FittedDownUp downUp = FittedDownUp.At(variance);
            downUp.PackDown(kernel.taps);
            TextureHandle half = RecordDown(graph, context, context.source, input.width, input.height,
                halo: 0, kernel.taps, "Stepless Down");
            if (downUp.halfResGaussianVariance > 1e-10)
                half = RecordGaussian(graph, context, half, DivideUp(input.width, 2), DivideUp(input.height, 2),
                    ReducedHalo, downUp.halfResGaussianVariance, Crossfade.None);

            downUp.PackUp(kernel.taps);
            TextureHandle result = FinalTarget(graph, context, "Stepless Up");
            return RecordSmallPass(graph, context, half, result, ShaderPass.Up, kernel.taps, count: 2,
                "Stepless Up", sourceHalo: ReducedHalo, targetHalo: 0);
        }

        /// <summary>
        /// One pyramid level: binomial Down, the rest of the blur at half
        /// resolution (a Gaussian or another level), cubic Up. Below
        /// NextLevelFadeEnd the level is only a Gaussian.
        /// </summary>
        /// <param name="halo">Halo around <paramref name="source"/>; zero at full resolution.</param>
        /// <param name="outerBilinear">
        /// How far this level's Up has turned bilinear; outermost level only, whose taps
        /// SmallKernel.OuterUpsample has written.
        /// </param>
        /// <param name="output">The final target, or default for a new texture.</param>
        TextureHandle RecordPyramidLevel(RenderGraph graph, in RecordContext context, TextureHandle source,
            int width, int height, int halo, double variance, double outerBilinear = 0,
            TextureHandle output = default)
        {
            if (variance < SmallKernel.NextLevelFadeStart)
                return RecordGaussian(graph, context, source, width, height, halo, variance, Crossfade.None, output);
            if (variance < SmallKernel.NextLevelFadeEnd)
                return RecordGaussian(graph, context, source, width, height, halo, variance,
                    Crossfade.IntoNextLevel, output);

            TextureHandle half = RecordDown(graph, context, source, width, height, halo,
                SmallKernel.BinomialDown, "Stepless Pyramid Down");
            half = RecordPyramidLevel(graph, context, half, DivideUp(width, 2), DivideUp(height, 2),
                ReducedHalo, SmallKernel.CoarseVariance(variance));

            bool bilinear = outerBilinear == 1;
            Vector4[] taps = SmallKernel.CubicUp;
            if (outerBilinear > 0 && !bilinear)
                taps = kernel.outerTaps;
            string name = bilinear ? "Stepless Linear Up" : "Stepless Pyramid Up";
            int targetHalo = output.IsValid() ? 0 : halo;
            TextureHandle result = output.IsValid() ? output :
                CreateTarget(graph, context, width + 2 * halo, height + 2 * halo, name);
            return RecordSmallPass(graph, context, half, result, bilinear ? ShaderPass.LinearUp : ShaderPass.Up,
                taps, count: bilinear ? 1 : 2, name, sourceHalo: ReducedHalo, targetHalo: targetHalo);
        }

        /// <summary>Records a 2x Down into a new half-res texture with a ReducedHalo halo.</summary>
        TextureHandle RecordDown(RenderGraph graph, in RecordContext context, TextureHandle source,
            int width, int height, int halo, Vector4[] taps, string name)
        {
            TextureHandle half = CreateTarget(graph, context, DivideUp(width, 2) + 2 * ReducedHalo,
                DivideUp(height, 2) + 2 * ReducedHalo, name);
            return RecordSmallPass(graph, context, source, half, ShaderPass.Down, taps, count: 3,
                name, sourceHalo: halo, targetHalo: ReducedHalo);
        }

        /// <summary>One Gaussian stage, as a single 2D pass or as horizontal and vertical passes.</summary>
        /// <param name="output">The final target, or default for a new texture.</param>
        TextureHandle RecordGaussian(RenderGraph graph, in RecordContext context, TextureHandle source,
            int width, int height, int halo, double variance, Crossfade crossfade, TextureHandle output = default)
        {
            int count = SmallKernel.GaussianRows(kernel, variance, crossfade);

            // Separable from four fetches per axis (23% faster than 2D on M4).
            bool separable = crossfade == Crossfade.IntoNextLevel || count >= 4;
            string name = separable ? "Stepless Gaussian X" : "Stepless Gaussian";
            bool fullResolution = halo == 0 && width == context.input.width && height == context.input.height;
            bool direct = output.IsValid() && !separable;
            TextureHandle result = direct ? output : CreateTarget(graph, context, width + 2 * halo, height + 2 * halo,
                name, fullResolution: fullResolution);
            RecordSmallPass(graph, context, source, result, separable ? AxisPass(count, false, crossfade) : Gaussian2DPass(count),
                kernel.taps, count, name, sourceHalo: halo, targetHalo: direct ? 0 : halo);
            if (!separable)
                return result;

            TextureHandle vertical = output.IsValid() ? output :
                CreateTarget(graph, context, width + 2 * halo, height + 2 * halo, "Stepless Gaussian Y");
            return RecordSmallPass(graph, context, result, vertical, AxisPass(count, true, crossfade), kernel.taps, count,
                "Stepless Gaussian Y", sourceHalo: halo, targetHalo: output.IsValid() ? 0 : halo);
        }

        // RecordGaussian goes separable from four fetches per axis.
        static ShaderPass Gaussian2DPass(int count) => count <= 2 ? ShaderPass.Gaussian2D2 : ShaderPass.Gaussian2D3;

        internal static ShaderPass AxisPass(int count, bool vertical, Crossfade crossfade)
        {
            // Unrolled passes pad shorter rows with zero-weight taps; unrolling
            // six to eight fetches made sigma 3-3.8 blurs 7-10% faster on M4.
            if (count >= 4 && count <= 5)
                return vertical ? ShaderPass.Vertical5 : ShaderPass.Horizontal5;
            if (count >= 6 && count <= 8)
                return vertical ? ShaderPass.Vertical8 : ShaderPass.Horizontal8;
            // High's full-res rows reach twelve fetches; looped, sigma 2.2-2.3
            // took up to 1.6x as long on M4.
            if (count >= 9 && count <= 12 && crossfade == Crossfade.IntoFirstLevel)
                return vertical ? ShaderPass.Vertical12 : ShaderPass.Horizontal12;
            return vertical ? ShaderPass.Vertical : ShaderPass.Horizontal;
        }

        static bool IsGaussian2DPass(ShaderPass pass)
            => pass == ShaderPass.Gaussian2D2 || pass == ShaderPass.Gaussian2D3;

        // Crossfades that change AxisPass's choice.
        static readonly Crossfade[] AxisPassCrossfades = { Crossfade.None, Crossfade.IntoFirstLevel };

        // Any pass Gaussian2DPass or AxisPass selects.
        internal static bool IsGaussianPass(ShaderPass pass)
        {
            for (int count = 1; count <= SmallKernel.MaxFetches; count++)
            {
                foreach (Crossfade crossfade in AxisPassCrossfades)
                    if (pass == Gaussian2DPass(count) || pass == AxisPass(count, false, crossfade) ||
                        pass == AxisPass(count, true, crossfade))
                        return true;
            }
            return false;
        }

        /// <summary>Records one pass of the small layouts.</summary>
        /// <param name="taps">(offset, weight, 0, 0) fetches as the kernels build them. Gaussian
        /// passes are repacked below into _Taps's (even, odd) layout; see Shaders/Small.hlsl.</param>
        /// <param name="count">Fetches per axis; only the separable passes read it.</param>
        /// <param name="sourceHalo">Halo around the source's active image.</param>
        /// <param name="targetHalo">Halo around the target's active image.</param>
        TextureHandle RecordSmallPass(RenderGraph graph, in RecordContext context, TextureHandle source,
            TextureHandle target, ShaderPass pass, Vector4[] taps, int count, string name,
            int sourceHalo = 0, int targetHalo = 0)
        {
            TextureDesc sourceDesc = context.Describe(graph, source);
            Array.Clear(passTaps, 0, passTaps.Length);
            Array.Copy(taps, passTaps, Math.Min(taps.Length, passTaps.Length));
            // Pixel centers are integers or half-integers, so rounding
            // offsets here rounds every fetch to the same 1/256 grid.
            // This avoids rounding coordinates in each shader sample.
            for (int i = 0; i < passTaps.Length; i++)
                passTaps[i].x = (float)BlurMath.Snap(passTaps[i].x);
            if (IsGaussianPass(pass))
            {
                MatchOddOrder(passTaps, count);
                // Pair each even-phase tap with its odd-phase tap, so the
                // shader loads both through one wave-uniform address.
                for (int k = 0; k < SmallKernel.MaxFetches; k++)
                {
                    Vector4 odd = passTaps[SmallKernel.MaxFetches + k];
                    passTaps[k] = new Vector4(passTaps[k].x, passTaps[k].y, odd.x, odd.y);
                    passTaps[SmallKernel.MaxFetches + k] = Vector4.zero;
                }
            }

            // 2D Gaussians sample in half precision unless the source needs more.
            bool fullPrecision = IsGaussian2DPass(pass) && StorageFormat.NeedsFullPrecision(sourceDesc.format);
            MaterialPropertyBlock properties = RecordDraw(graph, context, source, target, pass, name, fullPrecision);
            properties.SetInteger(SmallSourceHaloId, sourceHalo);
            properties.SetInteger(SmallTargetHaloId, targetHalo);
            properties.SetVectorArray(TapsId, passTaps);
            properties.SetInteger(CountId, count);
            properties.SetVector(SourceSizeId, context.SamplingSize(source, sourceDesc));
            return target;
        }

        // Where the odd row mirrors the even row, list it in mirrored order,
        // so both parities add the same weights in the same order. Packed
        // in ascending offset, the odd row would add them in reverse; float
        // addition is not associative, so at some sigmas equal inputs gave
        // the two parities different bits, and Adreno 830 ran those rows up
        // to 18% slower.
        static void MatchOddOrder(Vector4[] taps, int count)
        {
            int odd = SmallKernel.MaxFetches;
            bool sameOrder = true;
            for (int k = 0; k < count; k++)
            {
                Vector4 mirror = taps[odd + count - 1 - k];
                if (mirror.x != -taps[k].x || mirror.y != taps[k].y)
                    return;
                sameOrder &= taps[odd + k].y == taps[k].y;
            }
            // A symmetric row already adds its weights in the same order.
            if (!sameOrder)
                Array.Reverse(taps, odd, count);
        }
    }
}
