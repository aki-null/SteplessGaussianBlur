# Changelog

## [Unreleased]

### Added

- `BlurQuality.High` (`GaussianBlur.Quality`, or the renderer feature's
  **Quality**): a full-resolution Gaussian below sigma 2.24, joining
  `Fast` by 2.4. It keeps the error under 0.5% up to sigma 2.3, where
  `Fast` reaches 6%, and costs up to 1.75 times as much there on M4.

### Changed

- `RecordReduced` and a default `BlurTarget` return a half-size texture
  from sigma 3 instead of 4.5. On M4 at 4K they take about half the GPU
  time over sigma 3–4.5 (0.8 ms against 1.6 ms; 30% less near 4.1).
  Their error is 1.1% at sigma 3, against 0.3% for `Record`, and at most
  1.3% near 4.1, against 1.0%. To read pixels or sample without
  filtering in this range, use `BlurTarget(fullSize: true)` or `Record`.
- Pyramid crossfades near sigma 4.1–4.2 (4.05–4.15 in reduced output)
  and 8.4–8.5 run unrolled 10-fetch passes: about 20% faster on M4,
  11–21% on iPhone 17 and 2–5% on Adreno 830, unchanged on Mali-G68. `BlurQuality.High` rows of 9 or 10
  fetches use them too.

### Fixed

- Passes into packed formats round ties to even. Rounding them up
  brightened wide blurs by 0.1–0.2%.

## [1.0.0]

- Initial release.
