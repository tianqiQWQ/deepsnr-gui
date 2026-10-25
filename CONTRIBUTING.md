# Contributing

Thanks for your interest! This is a small, focused project. Please read the
legal notes below before opening a PR.

## Legal boundaries (important)

This project is an unofficial GUI wrapper for DeepSNR. Do **not** add or commit:

- DeepSNR executables, installer files, model weights or any StarNet Software
  binaries;
- mirrored DeepSNR download links, redistribution caches or bundled packages;
- DeepSNR output images as test fixtures, reference results, documentation
  assets or marketing images.

The GUI must keep invoking the user's separately installed official
`deepsnr.exe`. Keep the product name "DeepSNR" visible and keep a link to the
official license (see `THIRD_PARTY_NOTICES.md`).

## Development

Requirements: Windows 10/11 with .NET Framework 4.8 (included by default).

```powershell
# build
powershell -NoProfile -ExecutionPolicy Bypass -File .\build.ps1
# output: dist\DeepSNR_GUI.exe
```

There is no automated test suite yet. Before submitting a PR, please manually
verify inside a normal (non-sandboxed) Windows session:

1. App starts and finds `deepsnr.exe` automatically.
2. TIFF and PNG inputs process successfully, result preview appears.
3. A 4-channel PNG has its alpha channel stripped and processes successfully.
4. Output directory write-check shows an error for a non-writable directory.
5. Window renders correctly when moved between monitors with different DPI.

## Style

- Keep the UI in Chinese (the main audience), with clear error messages.
- No new third-party dependencies if avoidable; the app deliberately uses only
  the .NET Framework/WPF so it stays a single small exe.
- Keep code C# 5 compatible (it is compiled with the csc.exe shipped with the
  .NET Framework, not Roslyn).

## Pull requests

- One topic per PR, describe the change and how you tested it.
- Screenshots are welcome but must use synthetic test images and must not show
  DeepSNR outputs.
