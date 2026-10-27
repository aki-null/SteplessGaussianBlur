# Usage details

## Source sizing

The blur reads only the active image and clamps to its edges. RTHandle
scaling can leave a larger allocation around the image, and hardware
dynamic resolution shrinks the view.

- A source covers its whole view unless
  `((BlurSource)source).WithViewport(viewportSize)` limits it. The region
  starts at texel (0, 0). Such a source is copied at zero strength, so
  returned handles always cover their whole view.
- With explicit dynamic scaling or a custom view, also pass the view
  size: `WithViewport(viewportSize, textureSize)`, both in current texels.
  RenderGraph drops the explicit-scaling flag on import, so the blur
  cannot detect it.
- `Blur` sources use their full view.
- Full-resolution outputs keep automatic hardware scaling when the input
  fills its view. Reduced textures have explicit sizes.

## Borders

Samples outside the image repeat the nearest edge pixel. From a strength
of 1024 times the larger image side, the output is the average of the
four corner colors.

## Reduced output

From sigma 3, `RecordReduced` and a default `BlurTarget` return a
texture up to 128 times smaller per side. Over 2.75–3 their last upsample
turns bilinear. Their error is 1.1% at sigma 3, against 0.3% for
`Record`, and at most 1.3% near 4.1, against 1.0%. The factor is a power of two
that divides both sides, so a 1170-pixel side allows only 2. A
`BlurTarget`'s size changes with sigma. Bilinear sampling with UVs 0–1
finishes the blur. In the outermost half texel the sampler's clamp
replaces the blur's edge rule, up to 3% off. To read pixels, use
`BlurTarget(fullSize: true)` or `Record`.

## Formats and precision

- Passes store the smallest renderable format that holds the source, with
  its channel count. Compressed sources map to their uncompressed
  precision. A smaller destination format is used instead.
- FP16 passes use the GPU's own conversion. Some GPUs truncate, M4 among
  them, which darkens an FP16 blur by up to about 0.3%. For a blur that
  feeds back into itself, use a float32 target.
- Passes into B10G11R11, URP's default HDR camera color, round to the
  nearest value in the shader. Truncation there would darken a blur by
  up to 4% (9% in blue).
- Fetch positions are rounded to the 1/256-texel grid on the CPU, so GPUs
  with 8-bit filter fractions, as on all tested, sample the same
  positions.

## XR

Record eye arrays inside URP's XR render context; outside it only the
first slice is filtered. Without XR views (`TextureXR.slices` is 1),
arrays are rejected. Once XR is initialized, `TextureXR.slices` stays 2,
so check the camera before passing an array. A `BlurTarget` holds a 2D
texture.

## Quality

`BlurQuality.High` (`blur.Quality`, or the renderer feature's
**Quality**) runs a full-resolution Gaussian below sigma 2.24 and joins
`Fast` by 2.4. Its error stays under 0.5% up to sigma 2.3. Over 1.4–2.3
`Fast`'s error peaks at 6%. There High costs up to 1.6 times as much on
M4. Switching while a blur animates through sigma 1.46–2.4 makes a
visible step.

## Warmup

`WarmUpShaders(colorFormat)` prepares a source format's variants for
both qualities, including a `BlurTarget`'s pick for it; `includeXR: true`
adds stereo. Pipeline creation can finish asynchronously, so the first
draw can still stall. Player builds strip keyword combinations the
recorder never selects.
