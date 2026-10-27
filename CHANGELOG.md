# Changelog

## [Unreleased]

### Added

- `BlurQuality.High` (`GaussianBlur.Quality`, or the renderer feature's
  **Quality**): a full-resolution Gaussian below sigma 2.24, joining
  `Fast` by 2.4. It keeps the error under 0.5% up to sigma 2.3, where
  `Fast` reaches 6%, and costs up to 1.75 times as much there on M4.

### Fixed

- Passes into packed formats round ties to even. Rounding them up
  brightened wide blurs by 0.1–0.2%.

## [1.0.0]

- Initial release.
