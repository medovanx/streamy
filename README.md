<div align="center">

# Streamy

**Your Android phone in a window on your PC. Watch it, hear it, control it, over USB or Wi-Fi.**

Streamy mirrors an Android phone's screen to Windows and lets you use it with your mouse and keyboard.
Nothing is installed on the phone: Streamy talks to it through adb and the open-source scrcpy server.
Several phones can run side by side, each in its own window.

![Status: preview](https://img.shields.io/badge/status-0.2%20preview-c27c0e?style=flat-square)
[![License: MIT](https://img.shields.io/badge/license-MIT-0870a8?style=flat-square)](LICENSE)
![Windows 10 | 11](https://img.shields.io/badge/Windows-10%20%7C%2011-0870a8?style=flat-square&logo=windows)
![.NET 11](https://img.shields.io/badge/.NET-11-0870a8?style=flat-square&logo=dotnet)
![Android 5+](https://img.shields.io/badge/Android-5%2B-0870a8?style=flat-square&logo=android)

[Status](#status) | [Features](#features) | [Connect a phone](#connect-a-phone) | [Privacy](#privacy) | [How it works](#how-it-works) | [Build](#build-from-source) | [Releasing](#releasing)

</div>

---

## Status

Streamy is in early development. **0.2.0 shows the phone's screen live in a window, over USB or Wi-Fi.
You can't control the phone from the PC yet: mouse and keyboard input come in 0.3.0.**
The full plan is in the [requirements document](docs/SRS.md).

| Milestone | Content | State |
| --- | --- | --- |
| 0.1.0 | Device list, Wi-Fi pairing, session handshake, CI and releases | Done |
| 0.2.0 | Video decoding and display, rotation | Done |
| 0.3.0 | Mouse and keyboard control, toolbar | Next |
| 0.4.0 | Phone audio on the PC, MP4 recording, screenshots | Planned |
| 0.5.0 | Drag and drop files and APKs, clipboard sync, QR pairing, remembered devices | Planned |
| 0.6.0 | Settings, Arabic, auto-updates | Planned |
| 1.0.0 | Multi-device polish, tested on several phones | Planned |

## Features

Working now:

- **Device list.** Every phone adb can see, with its model, whether it is on USB or Wi-Fi, and whether it is ready or waiting for you to allow debugging on the phone. It refreshes by itself.
- **Wi-Fi without cables.** Pair with the six-digit code from Android's Wireless debugging, or connect to a known IP and port.
- **Live screen.** Double-click a phone and its screen opens in a window, decoded with FFmpeg and drawn with Direct3D 11. The window takes the phone's shape, keeps its proportions when you resize it and turns with the phone. Each session has its own id and port, so several phones can stream at once.
- **Show touches.** A switch in the main window shows a dot on the phone wherever it is touched while you mirror it, handy for recordings and demos. The phone's own setting comes back afterwards.
- **One exe.** adb, the scrcpy server and FFmpeg are built in. No Android SDK, no .NET install.

Coming for 1.0: mouse and keyboard control (Arabic text included), hardware decoding,
Back/Home/Recents buttons, phone audio on the PC, MP4 recording, screenshots, drag-and-drop files and APKs,
two-way clipboard, QR pairing, English and Arabic, and auto-updates.

## Connect a phone

First, on the phone: **Settings > About phone**, tap **Build number** seven times to unlock **Developer options**.

**Over USB**

1. In **Developer options**, turn on **USB debugging**.
2. Plug the phone in and accept the **Allow USB debugging** prompt on the phone.
3. The phone appears as *Ready* in Streamy. Double-click it.

**Over Wi-Fi (Android 11 and newer)**

1. Put the PC and phone on the same network.
2. In **Developer options**, open **Wireless debugging**, turn it on and tap **Pair device with pairing code**.
3. In Streamy, click **Pair with code...** and type the IP address and port and the six-digit code the phone shows.
4. The phone appears as a Wi-Fi device. If it doesn't, click **Connect by IP...** and type the address shown at the top of the Wireless debugging screen.

Some phones (Xiaomi, OPPO and others) also need **USB debugging (Security settings)** turned on before mouse and keyboard control can work.

## Privacy

Streamy has no account, analytics or tracking. The screen, sound and clipboard go only from your phone to your
PC, through adb on your own USB cable or local network. Nothing is sent anywhere else.

## How it works

- **The phone side** is [scrcpy-server](https://github.com/Genymobile/scrcpy) 5.0.1. Streamy pushes it to `/data/local/tmp`, starts it through `adb shell app_process` with a random session id, and opens `adb forward tcp:0` to the session's abstract socket. The server captures the screen with `MediaCodec` (H.264 by default) and the audio with `AudioRecord` (Opus), and injects touch and key events. It leaves nothing behind when the session ends.
- **Streams.** Three sockets are opened in order: video, audio, control. The first one carries the device name; each media stream starts with its codec id, then 12-byte packet headers carry the PTS, key frame and config flags, and the size. A session packet announces the frame size on start and after every rotation.
- **Video.** The codec configuration packet is put in front of the next frame, and FFmpeg decodes it in low-delay mode on the stream's reader thread. Frames go through a triple buffer to a render thread, which uploads the Y, U and V planes as three textures and converts them to RGB in a pixel shader with the stream's colour matrix (BT.601 or BT.709, limited or full range). The picture is letterboxed with the viewport and presented with a flip-model swap chain. A late frame is never queued: the newest one replaces it.
- **Control** messages (touch, scroll, keys, text, clipboard, screen power...) are serialized byte for byte like scrcpy's own client; the unit tests use scrcpy's test vectors.
- **Bundled files.** The release exe embeds adb, scrcpy-server and the FFmpeg DLLs from the official scrcpy Windows release (checked against its SHA-256) and unpacks them to `%LOCALAPPDATA%\Streamy\bin\5.0.1` on first run. See [third-party notices](THIRD-PARTY-NOTICES.md).

## Project structure

```text
streamy/
├── src/
│   ├── Streamy.Core/             Logic with no Windows or UI dependencies
│   │   ├── Adb/                  Parsers for adb devices, mdns, pair and connect output
│   │   ├── Geometry/             Fitting the frame in the window, window to phone coordinates
│   │   ├── Input/                Android key codes and actions, Windows key map
│   │   ├── Protocol/             scrcpy control and device messages, packet headers, codecs
│   │   └── Server/               scrcpy-server options and start command
│   └── Streamy/                  The Windows app
│       ├── Services/             adb runner, unpacking of the bundled files
│       ├── Session/              One mirroring session: tunnel, sockets, stream readers
│       ├── Video/                FFmpeg decoder, frame triple buffer, Direct3D 11 renderer
│       ├── ThirdParty/           adb, scrcpy-server, FFmpeg (downloaded, not committed)
│       └── UI/                   Device list, session window, dialogs
├── tests/
│   └── Streamy.Core.Tests/       xUnit tests
├── tools/
│   └── fetch-deps.ps1            Downloads and checks the pinned scrcpy release
├── docs/
│   └── SRS.md                    Requirements and milestones
├── deps.json                     Pinned scrcpy version, URL and SHA-256
├── Directory.Build.props         Version and settings shared by every project
├── global.json                   Pinned .NET SDK
└── Streamy.slnx
```

## Build from source

Requirements: Windows 10 or 11 and the [.NET 11 SDK](https://dotnet.microsoft.com/download).

```bash
git clone https://github.com/medovanx/streamy.git
cd streamy
pwsh tools/fetch-deps.ps1
dotnet build
dotnet test
dotnet run --project src/Streamy -c Release
```

`fetch-deps.ps1` downloads the scrcpy release pinned in `deps.json`, checks its SHA-256 and copies adb,
scrcpy-server and the FFmpeg DLLs into `src/Streamy/ThirdParty`, where the build embeds them.

Build the single-file release exe:

```bash
dotnet publish src/Streamy/Streamy.csproj -c Release -r win-x64 --self-contained -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:EnableCompressionInSingleFile=true -o publish
```

Developer switches:

| Switch | Purpose |
| --- | --- |
| `--mirror SERIAL` | Open the session window for that phone at start |
| `--probe SERIAL OUT [SECONDS]` | Start one session without a window, stream for a few seconds (5 by default), write the device name, codecs, frame size and packet and frame counts to `OUT`, and save the newest decoded frame as a PNG beside it |

### Updating scrcpy

The server refuses a client whose version differs from its own, so update both together: change the version,
URL and SHA-256 in `deps.json` (the digest is on the GitHub release page), set `ScrcpyProtocol.ServerVersion`
to the same version, check the [protocol notes](https://github.com/Genymobile/scrcpy/blob/master/doc/develop.md#protocol)
for changes, then run `fetch-deps.ps1`, the tests and a `--probe` against a real phone.

## Releasing

Versions follow [Semantic Versioning](https://semver.org/). Bump `<Version>` in `Directory.Build.props`, add the changes to [CHANGELOG.md](CHANGELOG.md), then tag and push:

```bash
git tag v0.2.0
git push origin v0.2.0
```

The [release workflow](.github/workflows/release.yml) checks that the tag matches the version, runs the tests, builds
`Streamy.exe` and publishes a GitHub Release with the changelog entry as its notes. `0.x` releases are marked as pre-releases.

## Credits

- [scrcpy](https://github.com/Genymobile/scrcpy) by Genymobile and Romain Vimont (Apache-2.0): the phone-side server and the protocol.
- [Android SDK Platform-Tools](https://developer.android.com/tools/releases/platform-tools) (Apache-2.0): adb.
- [FFmpeg](https://ffmpeg.org/) (LGPL-2.1+): video and audio decoding, through [FFmpeg.AutoGen](https://github.com/Ruslan-B/FFmpeg.AutoGen) (MIT).
- [Vortice.Windows](https://github.com/amerkoleci/Vortice.Windows) (MIT): Direct3D 11 bindings.

## License

[MIT](LICENSE). Bundled third-party files keep their own licenses; see [THIRD-PARTY-NOTICES.md](THIRD-PARTY-NOTICES.md).
