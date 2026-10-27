# Changelog

## [Unreleased]

### Added

- `BlurQuality.High` (`GaussianBlur.Quality`, or the renderer feature's
  **Quality**): a full-resolution Gaussian below sigma 2.24, joining
  `Fast` by 2.4. It keeps the error under 0.5% up to sigma 2.3, where
  `Fast`'s error peaks at 6% over 1.4–2.3, and costs up to 1.6 times as
  much there on M4.

### Changed

- `RecordReduced` and a default `BlurTarget` return a half-size texture
  from sigma 3 instead of 4.5. On M4 at 4K they take about half the GPU
  time from sigma 4 to 4.5 (0.7–0.9 ms against 1.3–1.7 ms).
  Their error is 1.1% at sigma 3, against 0.3% for `Record`, and at most
  1.3% near 4.1, against 1.0%. To read pixels or sample without
  filtering in this range, use `BlurTarget(fullSize: true)` or `Record`.
- Blurs near sigma 4.1–4.2 (4.05–4.15 in reduced output) are faster:
  about 20% on M4, 11–21% on iPhone 17 and 2–5% on Adreno 830,
  unchanged on Mali-G68. So is `BlurQuality.High` near 2.0–2.24.
- Blurs from sigma 6.3 to 11.5 are faster: 15–17% on Immortalis-G925,
  5–8% on Adreno 830 and 11–15% on A12X. On M4 and iPhone 17 they are
  2–5% faster up to sigma 8 and unchanged above. Over 5.9–6.3 they are
  1–3% slower. Error stays under 1%: at most 0.69%, at 6.3.
- Blurs from sigma 11.5 to 16.1 are faster: 24–30% on M4, 26% on Adreno
  830 and 15–26% on Immortalis-G925. The layout used from 16.1 now starts
  at 11.5. Error stays under 1%: 0.9% at 11.5, against 0.4% before.
  Animating strength across 11.5 changes the output as much as a 0.46%
  change of sigma.
- `RecordReduced` and a default `BlurTarget` return a quarter-size texture
  from sigma 11.5 instead of 16.1, when both sides divide by 4.
- The shader has five new passes: `Horizontal10`, `Vertical10`,
  `Horizontal12`, `Vertical12` and `DownHalve`. A shader given to
  `GaussianBlur(Shader)` without them now throws `ArgumentException`.

### Fixed

- Passes into packed formats round ties to even. Rounding them up
  brightened wide blurs by 0.1–0.2%.

## [1.0.0]

- Initial release.
