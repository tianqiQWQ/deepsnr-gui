# Changelog

## v1.0.0 (2026-10-25)

Initial public version.

- Standalone Windows GUI for the DeepSNR CLI (`deepsnr.exe`), run as a separate
  official process.
- Drag & drop or browse TIFF / PNG / FITS / JPEG input.
- Automatic alpha-channel stripping for 4-channel PNG/TIFF (temporary 3-channel
  TIFF, removed after processing).
- Model 1/2 selection, stride, linear FITS options (`--linear`,
  `--shadows-clipping`, `--target-background`).
- Progress bar, full log view, cancel, batch queue.
- Side-by-side before/after preview with 50%/100%/200%/400% zoom and
  synchronized panning.
- Automatic discovery of the official DeepSNR installation paths, manual
  override, remembered settings.
- Per-Monitor V2 DPI awareness for mixed-DPI multi-monitor setups.
- Output-directory write pre-check and clearer permission-error diagnostics.
- DeepSNR license link in the UI; unofficial-project disclaimers.

Tested with DeepSNR CLI 1.3.2 (build 0147) on Windows 11, including 7680×4320
8-bit RGBA PNG and 16-bit TIFF inputs.
