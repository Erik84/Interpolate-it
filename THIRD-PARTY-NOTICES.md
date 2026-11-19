# Third-party notices

Interpolate-it contains or uses the following third-party work.

## RIFE (model architecture and weights)

The network definitions in `tools/export/rife_arch/` are adapted from Practical-RIFE and vs-rife, and the
weights that `tools/export/fetch_weights.ps1` downloads (`flownet_v4.6.pkl`, `flownet_v4.25.pkl`) are the RIFE 4.6
and 4.25 checkpoints as published in the vs-rife release. The ONNX files under `models/` are generated from them
by `tools/export/export_rife_flow.py`; they are not part of the repository, but they are part of the installer.

- Practical-RIFE — https://github.com/hzwer/Practical-RIFE — MIT License, Copyright (c) 2021 hzwer
- vs-rife — https://github.com/HolyWu/vs-rife — MIT License, Copyright (c) 2021 HolyWu

```
MIT License

Permission is hereby granted, free of charge, to any person obtaining a copy
of this software and associated documentation files (the "Software"), to deal
in the Software without restriction, including without limitation the rights
to use, copy, modify, merge, publish, distribute, sublicense, and/or sell
copies of the Software, and to permit persons to whom the Software is
furnished to do so, subject to the following conditions:

The above copyright notice and this permission notice shall be included in all
copies or substantial portions of the Software.

THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR
IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY,
FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE
AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER
LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING FROM,
OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN THE
SOFTWARE.
```

## NuGet packages (referenced, not copied into this repository; their binaries are part of the installer)

| Package | Used for | Licence |
|---|---|---|
| Vortice.Direct3D11, Vortice.DXGI, Vortice.D3DCompiler (and their dependencies Vortice.DirectX, Vortice.Mathematics) | Direct3D 11 / DXGI bindings | MIT, Copyright (c) Amer Koleci and Contributors — https://github.com/amerkoleci/Vortice.Windows |
| SharpGen.Runtime, SharpGen.Runtime.COM | COM interop underneath Vortice | MIT, (c) 2010-2017 Alexandre Mutel, 2017-2023 Jeremy Koritzinsky, 2023-2024 Amer Koleci — https://github.com/SharpGenTools/SharpGenTools |
| Microsoft.Windows.AI.MachineLearning | Windows ML runtime: `Microsoft.Windows.AI.MachineLearning.dll`, `onnxruntime.dll`, `DirectML.dll` and their .NET projections, and the execution-provider catalog | **Microsoft Software License Terms: Microsoft Windows ML Runtime** (not MIT). The files are redistributed unmodified, as the package intends; the terms and the package's own third-party notices are installed as `licenses\Windows-ML-Runtime-license.txt` and `licenses\Windows-ML-Runtime-ThirdPartyNotices.txt`. ONNX Runtime itself is MIT — https://github.com/microsoft/onnxruntime |
| Microsoft.Windows.SDK.NET, WinRT.Runtime (C#/WinRT), System.Numerics.Tensors | Windows Runtime projections and tensor types for .NET | MIT, Copyright (c) Microsoft Corporation / .NET Foundation and Contributors |
| xunit, xunit.runner.visualstudio, Microsoft.NET.Test.Sdk | Unit tests only, not shipped | See each package |

The MIT licence text printed above applies to each MIT-licensed item in this table with the copyright line given
for it.

## .NET runtime (part of the installer)

The installer contains the .NET runtime and the Windows Desktop (WPF) runtime, so that nothing has to be installed
first. Both are MIT-licensed, Copyright (c) .NET Foundation and Contributors — https://github.com/dotnet/runtime,
https://github.com/dotnet/wpf. Their licence and third-party notices are installed as
`licenses\dotnet-LICENSE.txt` and `licenses\dotnet-ThirdPartyNotices.txt`.

## Installer

The installer is made with Inno Setup, Copyright (C) 1997-2026 Jordan Russell and Martijn Laan —
https://jrsoftware.org/isinfo.php.

## Downloaded at run time by Windows

On a PC with an NVIDIA RTX card, Windows ML downloads NVIDIA's "TensorRT for RTX" execution provider through the
Windows execution-provider catalog the first time it is needed. It is installed and licensed by Windows / NVIDIA
and is not redistributed with Interpolate-it.

## Design references (no code used)

Lossless Scaling (commercial) and Magpie (GPL-3.0) were studied as prior art for capturing a window and covering
it with an overlay. No code from either project is included.
