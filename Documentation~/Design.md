# Design

Start with `GaussianBlur.cs`. Each sigma range has a layout in
`Layouts/` that takes its coefficients from `Kernels/` and draws passes of
one shader. Kernel files open with a diagram of their passes.

## Variance budget

Downsampling and upsampling blur too, and variances add in sequence:

```
variance(target) = variance(down) + variance(Gaussian) + variance(up)
```

Each layout's Gaussian gets what its other passes leave. The target is a
discrete Gaussian tapered to zero at four sigma. Where the budget alone
cannot hold the shape, the filters are fitted.

## Fitted down/up pair (sigma 1.46–2.40)

A plain 2x down/up pair blurs by a fixed amount, about sigma 1.6. Here
the pair's weights follow Bézier curves in sigma until they become a
pyramid level at 2.40:

| Sigma | Passes |
|---|---|
| 1.46–1.62 | Full-resolution Gaussian, crossfading toward the pair |
| 1.62–1.72 | Fitted down to half resolution, fitted up |
| 1.72–2.40 | Fitted down, half-resolution Gaussian, fitted up |
| 2.40 | One pyramid level: binomial down, Gaussian, cubic up |

The curves were fitted offline on one axis to:

- match the target's weights and point response on even and odd pixels;
- keep the response steady as a point moves or sigma changes;
- keep weights nonnegative, summing to one and centered;
- end exactly at the pyramid's binomial down and cubic up.

The runtime rounds fetch positions. The half-res Gaussian (from 1.72), or
Down's outer weight before that, absorbs the variance error.
[`Tools~/FilterFit`](../Tools~/FilterFit/README.md) reruns the fit.

## Transition and wide layouts (sigma 16.1 and above)

Only the Gaussian follows sigma. Its fetches are bilinear pairs on either
parity of its grid.

- The transition starts where the pyramid would add a fourth level, so
  both run the Gaussian on the same eighth grid. Its halve settles into
  the binomial by 16.6.
- From 21 the Gaussian blends toward the wide layout's extra level,
  written out as one row. That level's two upsample fractions are fitted
  on the 1/256 lattice.

## Layout switches

Within a layout, passes change only where old and new compute the same
filter. Before a switch to different passes, the outgoing layout blends
toward the next, so both compute the same filter at the switch. The wide
layout's pitch doublings at 70, 140, … keep the shape, not every weight.

## Reduced output

A reduced output stops each layout before its last bilinear upscale: the
pyramid's half-res level, the transition's Up, the wide layout's Cubic.
Below that pass's grid, Final magnifies onto the 1/R grid. Budgets count
the viewer's bilinear sampling in place of the skipped pass.

## Measured error

In exact arithmetic every layout gives the requested variance within
2e-6, averaged over a point's position. The shape still differs: at worst
6.2% of input energy, near sigma 1.58. GPU measurements are in the
[benchmarks](Benchmarks.md#summary).

## Conventions

The C# kernel builders and the shaders share these definitions.

![Source texel i spans i to i + 1, centered at i + 0.5; half-res texel j spans 2j to 2j + 2, centered at 2j + 1](Images/texel-coordinates.svg)

- Texel `i` covers `[i, i + 1]`, centered at `i + 0.5`. Kernel offsets
  are measured from the output pixel's center, in texels of the texture
  being read.
- Output texel `j` of a factor-`f` reduction is centered at source
  coordinate `f·j + f/2`.
- After a reduction, even and odd output pixels sit at different offsets
  from the coarse grid. Upsampling rows are stored per phase (two for 2x,
  four for the 4x cubic); opposite phases mirror each other.
- Reduced textures carry three extra texels per side (four on the
  transition's Gaussian axes). Clamping each reduced texture separately
  would change the border rule.
- Weights `a` and `b` on texels `k` and `k + 1` become one fetch at
  `k + b / (a + b)` with weight `a + b`. Builders round that fraction to
  the sampler's 1/256, then correct weights so each row keeps its mass,
  center and variance. Fixed passes (binomial down, cubic up) sit at
  rounded positions, and budgets use their exact weights.
- Up to 4096 texels per axis, float coordinates hold these positions
  exactly. Larger views can move a fetch by one 1/256 step, which shifts
  under 0.4% of that fetch's weight.

## Vocabulary

| Term | Meaning |
|---|---|
| Small layouts | Everything below sigma 16.1 |
| Transition | Sigma 16.1 to 24 |
| Wide | Sigma 24 and above |
| Quarter / Halve | Passes that reduce resolution 4x / 2x |
| Halo | Extra texels around a reduced image, holding the filtered clamp-extended source |
| Origin | The texel where a texture's image starts, past its halo |
| Pitch | Source pixels per texel of the wide layout's Gaussian grid |
| Phase | An output pixel's position modulo the coarse grid it reads |
| Pair | Two adjacent texels read by one bilinear fetch |
| Crossfade | A Gaussian row blending toward the next layout's passes before a switch |
| Virtual level | One more halve, Gaussian and upsample, written out as one row instead of drawn |
| Keyword set | A material's keywords: half or float 2D samples, rounded packed-float output |

## Changing the shaders

Changes to compiled code need a timing A/B on M4, iPhone and Android.
Comments, function names and moving code between includes do not change
compiled code; uniform names, declaration order and array sizes do.

`Small.hlsl` keeps its own `SampleSource`; the shared form is slower on
some devices.
