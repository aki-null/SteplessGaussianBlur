using System;
using UnityEngine;
using UnityEngine.Experimental.Rendering;

namespace SteplessGaussianBlur
{
    /// <summary>The formats a blur stores, chosen from what its source holds.</summary>
    static class StorageFormat
    {
        // Renderable formats with one, two and four channels, by precision.
        static readonly GraphicsFormat[] Float32 =
            { GraphicsFormat.R32_SFloat, GraphicsFormat.R32G32_SFloat, GraphicsFormat.R32G32B32A32_SFloat };
        static readonly GraphicsFormat[] Half =
            { GraphicsFormat.R16_SFloat, GraphicsFormat.R16G16_SFloat, GraphicsFormat.R16G16B16A16_SFloat };
        static readonly GraphicsFormat[] UNorm16 =
            { GraphicsFormat.R16_UNorm, GraphicsFormat.R16G16_UNorm, GraphicsFormat.R16G16B16A16_UNorm };
        static readonly GraphicsFormat[] UNorm8 =
            { GraphicsFormat.R8_UNorm, GraphicsFormat.R8G8_UNorm, GraphicsFormat.R8G8B8A8_UNorm };
        static readonly GraphicsFormat[] Srgb8 =
            { GraphicsFormat.R8_SRGB, GraphicsFormat.R8G8_SRGB, GraphicsFormat.R8G8B8A8_SRGB };

        /// <summary>
        /// The smallest format the blur can render that holds a source's
        /// channels, range and precision: the source's own when possible.
        /// </summary>
        /// <remarks>
        /// Packed normalized formats (R5G6B5, RGBA4444, A2B10G10R10) widen to
        /// 8 or 16 bits: each pass rounds, and their narrow channels would band.
        /// </remarks>
        internal static GraphicsFormat For(GraphicsFormat source)
        {
            bool packedNormalized = GraphicsFormatUtility.IsPackedFormat(source) &&
                GraphicsFormatUtility.IsUNormFormat(source);
            if (CanStore(source) && !packedNormalized)
                return source;
            if (source == GraphicsFormat.RGB_BC6H_UFloat && CanStore(GraphicsFormat.B10G11R11_UFloatPack32))
                return GraphicsFormat.B10G11R11_UFloatPack32;
            GraphicsFormat[] precision = PrecisionOf(source);
            // A single alpha channel (Alpha8) is sampled as alpha, so it needs RGBA.
            int channels = GraphicsFormatUtility.HasAlphaChannel(source)
                ? 4 : (int)GraphicsFormatUtility.GetComponentCount(source);
            GraphicsFormat matched = precision[Math.Min(channels, 3) - 1];
            if (CanStore(matched))
                return matched;
            if (CanStore(precision[2]))
                return precision[2];
            if (CanStore(GraphicsFormat.R16G16B16A16_SFloat))
                return GraphicsFormat.R16G16B16A16_SFloat;
            throw new ArgumentException(
                $"No format this device can render and filter holds {source}.", nameof(source));
        }

        /// <summary>
        /// The format of passes before the destination: the destination's when
        /// it is smaller and keeps the source's range unpacked, since clipping
        /// or rounding before the blur would change the result.
        /// </summary>
        internal static GraphicsFormat Intermediate(GraphicsFormat source, GraphicsFormat destination)
        {
            GraphicsFormat held = For(source);
            bool keepsSource = For(destination) == destination &&
                Range(destination) >= Range(held) &&
                (GraphicsFormatUtility.IsSignedFormat(destination) || !GraphicsFormatUtility.IsSignedFormat(held));
            return keepsSource && GraphicsFormatUtility.GetBlockSize(destination) < GraphicsFormatUtility.GetBlockSize(held)
                ? destination : held;
        }

        /// <summary>
        /// 0 for formats bounded by 1, 1 for FP16-range floats (up to 65504),
        /// 2 for float32. A float32 source's partial sums can exceed FP16
        /// even where its blurred result does not.
        /// </summary>
        static int Range(GraphicsFormat format)
        {
            if (IsFloat32(format))
                return 2;
            return GraphicsFormatUtility.IsIEEE754Format(format) || GraphicsFormatUtility.IsHDRFormat(format) ? 1 : 0;
        }

        /// <summary>
        /// Whether one-pass 2D samples must stay in float. Half keeps an 11-bit
        /// significand: enough for 8-bit and FP16 color, not for float32 HDR
        /// beyond 65504 or for 16-bit normalized formats.
        /// </summary>
        internal static bool NeedsFullPrecision(GraphicsFormat format) => IsFloat32(format) ||
            (BytesPerComponent(format) == 2 && !GraphicsFormatUtility.IsIEEE754Format(format) &&
            !GraphicsFormatUtility.IsIntegerFormat(format));

        static bool IsFloat32(GraphicsFormat format)
            => GraphicsFormatUtility.IsIEEE754Format(format) && BytesPerComponent(format) == 4;

        // 0 for compressed and packed formats.
        static int BytesPerComponent(GraphicsFormat format) =>
            GraphicsFormatUtility.IsCompressedFormat(format) || GraphicsFormatUtility.IsPackedFormat(format) ? 0 :
                (int)GraphicsFormatUtility.GetBlockSize(format) / (int)GraphicsFormatUtility.GetComponentCount(format);

        static GraphicsFormat[] PrecisionOf(GraphicsFormat source)
        {
            int bytesPerComponent = BytesPerComponent(source);
            if (IsFloat32(source))
                return Float32;
            if (GraphicsFormatUtility.IsSRGBFormat(source))
                return Srgb8;
            if (!GraphicsFormatUtility.IsUNormFormat(source) || GraphicsFormatUtility.IsHDRFormat(source))
                return Half;
            bool tenBit = source == GraphicsFormat.A2B10G10R10_UNormPack32 ||
                source == GraphicsFormat.A2R10G10B10_UNormPack32;
            return bytesPerComponent == 2 || tenBit ? UNorm16 : UNorm8;
        }

        /// <summary>Whether the blur can render into the format and filter it.</summary>
        internal static bool CanStore(GraphicsFormat format) =>
            format != GraphicsFormat.None && !GraphicsFormatUtility.IsIntegerFormat(format) &&
            !GraphicsFormatUtility.IsCompressedFormat(format) &&
            SystemInfo.IsFormatSupported(format, GraphicsFormatUsage.Render) &&
            (SystemInfo.IsFormatSupported(format, GraphicsFormatUsage.Linear) || IsPackedCameraColor(format));

        /// <summary>
        /// Packed HDR camera color, which URP samples although Unity can report
        /// Sample/Linear=false for it. Matches URP's own B10G11R11 check
        /// (UniversalRenderPipelineCore.MakeRenderTextureGraphicsFormat,
        /// UUM-41070); Blend support is not a general substitute for filtering.
        /// </summary>
        internal static bool IsPackedCameraColor(GraphicsFormat format) =>
            format == GraphicsFormat.B10G11R11_UFloatPack32 &&
            SystemInfo.IsFormatSupported(format, GraphicsFormatUsage.Blend);
    }
}
