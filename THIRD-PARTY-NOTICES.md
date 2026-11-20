# Third-party notices

Streamy's own code is under the [MIT License](LICENSE). The release exe embeds the following files,
taken unchanged from the [scrcpy 5.0.1 Windows release](https://github.com/Genymobile/scrcpy/releases/tag/v5.0.1)
(`scrcpy-win64-v5.0.1.zip`, SHA-256 `b12a2c4ee8be317422451fc7dcf8ee20a71b5ea7ef9ad73ddd825a25316227a5`), and unpacks them
to `%LOCALAPPDATA%\Streamy\bin\5.0.1` when it runs.

| File | Project | License |
| --- | --- | --- |
| `scrcpy-server` | [scrcpy](https://github.com/Genymobile/scrcpy) by Genymobile and Romain Vimont | [Apache License 2.0](https://github.com/Genymobile/scrcpy/blob/master/LICENSE) |
| `adb.exe`, `AdbWinApi.dll`, `AdbWinUsbApi.dll` | [Android SDK Platform-Tools](https://developer.android.com/tools/releases/platform-tools) (Android Open Source Project) | [Apache License 2.0](https://android.googlesource.com/platform/packages/modules/adb/+/refs/heads/main/NOTICE) |
| `avcodec-63.dll`, `avformat-63.dll`, `avutil-61.dll`, `swresample-7.dll` | [FFmpeg](https://ffmpeg.org/), as built by scrcpy | [GNU LGPL 2.1 or later](https://ffmpeg.org/legal.html) |

The exe also contains these .NET libraries, compiled in:

| Library | License |
| --- | --- |
| [FFmpeg.AutoGen](https://github.com/Ruslan-B/FFmpeg.AutoGen) 9.0.1.1 | MIT |
| [Vortice.Windows](https://github.com/amerkoleci/Vortice.Windows) 3.8.3 (Direct3D 11, DXGI, D3DCompiler) | MIT |
| [NAudio](https://github.com/naudio/NAudio) 3.3.0 (Core, Wasapi) | MIT |
| [QRCoder](https://github.com/codebude/QRCoder) 1.8.1 | MIT |

`Streamy.Core` embeds data from [GeoNames](https://www.geonames.org) (`cities15000` and `countryInfo`), reduced to each
place's name, country and position, under the [Creative Commons Attribution 4.0 License](https://creativecommons.org/licenses/by/4.0/).

The FFmpeg DLLs are loaded dynamically and can be replaced: put compatible DLLs of the same names in the folder above.
Their source code is available from [ffmpeg.org](https://ffmpeg.org/download.html); scrcpy's build scripts are in
[its repository](https://github.com/Genymobile/scrcpy/tree/v5.0.1/app/deps).

Streamy talks to scrcpy-server using the scrcpy protocol; the protocol code in `src/Streamy.Core/Protocol` is
written from scrcpy's documentation and tests, and its unit tests reuse scrcpy's byte vectors.

## Apache License 2.0 notice for scrcpy

```text
Copyright (C) 2018 Genymobile
Copyright (C) 2018-2026 Romain Vimont

Licensed under the Apache License, Version 2.0 (the "License");
you may not use this file except in compliance with the License.
You may obtain a copy of the License at

    http://www.apache.org/licenses/LICENSE-2.0

Unless required by applicable law or agreed to in writing, software
distributed under the License is distributed on an "AS IS" BASIS,
WITHOUT WARRANTIES OR CONDITIONS OF ANY KIND, either express or implied.
See the License for the specific language governing permissions and
limitations under the License.
```
