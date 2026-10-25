# Third-party notices

This project is a GUI wrapper only. It does not include, modify or redistribute
any StarNet Software binaries, model weights or installers.

## DeepSNR / StarNet Software

- Product: **DeepSNR** — AI-powered noise reduction for astrophotography
- Author / copyright holder: **Mikita (Nikita) Misiura**
- Official site: <https://starnetastro.com/>
- DeepSNR CLI page: <https://starnetastro.com/cli-tools/deepsnr/>
- License: supplied with the official DeepSNR packages as `LICENSE.txt`
  (revision 2026-09-07 in DeepSNR CLI 1.3.2). The official license text is
  included in the download/installer; it is not reproduced here.

### How this project interacts with DeepSNR

This project launches the separately installed official `deepsnr.exe` as a
child process and passes image paths to it. DeepSNR performs all processing.
DeepSNR's license (Section 2) permits other applications and scripts to
automate permitted image processing by running the supplied CLI executable as
a separate process.

### Requirements placed on users

- You must obtain and install DeepSNR through the official download sources.
- You must accept DeepSNR's license before using it (the official installer
  presents it). The GUI exposes a "DeepSNR 许可" button that opens the
  installed `LICENSE.txt` or the official product page.
- Do not redistribute DeepSNR, its model files or its installer, and do not
  mirror or re-host its downloads.

### Restrictions from the DeepSNR license (summary, not legal advice)

- No bundling, mirroring, resale or redistribution of the software or model
  files without prior written permission.
- No use of the software, weights or outputs to train, fine-tune or distill
  any machine-learning model.
- No use of the software, components or outputs to develop, test, validate or
  improve other software or ML products (subject to the integration exception
  in the license).
- No independent extraction/loading of model weights or reverse engineering to
  bypass the supplied interfaces.

If in doubt, or for commercial packaging/distribution, contact the DeepSNR
author for written permission: <https://starnetastro.com/contact/>

## Microsoft .NET Framework

This application targets the Microsoft .NET Framework 4.8 (`csc.exe` from the
Windows .NET Framework installation). The .NET Framework is a Microsoft
component and is not distributed with this project; it is included with
Windows 10/11 and subject to Microsoft's license terms.

## NAFNet (reference only)

DeepSNR's neural-network architecture is based on NAFNet
(<https://github.com/megvii-research/NAFNet>). Neither NAFNet nor DeepSNR's
model files are included in this repository.

## App icon

`src/deepsnr_gui.ico` is an original work created for this project. It is not
the official DeepSNR/StarNet logo and does not use StarNet Software artwork.

## Third-party libraries

The GUI is written in C# and uses only the .NET Framework class library
(WPF). No third-party NuGet packages or other libraries are bundled.
