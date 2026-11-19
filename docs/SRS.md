# Streamy: Software Requirements Specification

Status: 1.3.0. Last updated 2026-11-20.

## 1. Purpose

Streamy shows an Android phone's screen in a window on a Windows PC and lets the user control it with the
mouse and keyboard, over USB or Wi-Fi. It plays the phone's sound on the PC, records sessions, moves files and
the clipboard between the two, shows the phone's details, and handles several phones at once.

## 2. Decisions

| Topic | Decision |
| --- | --- |
| Name | Streamy |
| Phone side | scrcpy-server (Apache-2.0), pushed and started through adb. Nothing is installed on the phone. |
| Transport | adb over USB, or over Wi-Fi: Android 11+ wireless debugging (QR code or pairing code), or `adb tcpip` for any version. |
| PC stack | .NET 11, WinForms, Windows 10 (19041) and 11, x64. Same layout as Data Tide and Salaty Reminder. |
| Language | English only. |
| Packaging | One self-contained `Streamy.exe`. adb, scrcpy-server and the FFmpeg DLLs are embedded and unpacked to `%LOCALAPPDATA%\Streamy\bin\<scrcpy version>` on first run. |
| Video | FFmpeg software decoding (FFmpeg.AutoGen 9.0.1, matching the bundled avcodec-63) and a Direct3D 11 renderer (Vortice.Windows) that converts YUV to RGB in a shader. |
| Audio | FFmpeg decoding, NAudio WASAPI playback. |
| Versioning | Semantic Versioning in `Directory.Build.props`; a `vX.Y.Z` tag builds and publishes a GitHub Release (0.x as pre-releases). |
| Repository | `medovanx/streamy`, public, MIT. |
| Pinned scrcpy | 5.0.1 (`deps.json`, SHA-256 checked). `ScrcpyProtocol.ServerVersion` and `deps.json` always change together. |
| Code signing | None for now; SmartScreen's "Run anyway" as for Data Tide. |

## 3. Functional requirements

All are done in 1.0.0 unless marked otherwise.

### 3.1 Devices and connection

- FR-1 List the devices adb sees with model, USB or Wi-Fi, and state; refresh every 2 seconds without disturbing the selection.
- FR-2 Pair with a wireless debugging code.
- FR-3 Connect by IP:port.
- FR-4 Pair by QR code (`WIFI:T:ADB;S:<name>;P:<password>;;`, mDNS `_adb-tls-pairing`). *Not yet tried end to end with a phone scanning the code.*
- FR-5 Switch a USB phone to Wi-Fi in one click (`adb tcpip 5555` and connect to its Wi-Fi address).
- FR-6 Remember Wi-Fi devices, list them while away and reconnect them; disconnect and forget.
- FR-7 Show the selected phone's details: Android version and security patch, model, build, CPU, GPU, RAM, storage, battery, screen.

### 3.2 Mirroring

- FR-10 Start a session: push the server, forward tunnel, start, connect video, audio and control sockets, then remove the tunnel.
- FR-11 Decode H.264, H.265 and AV1 and draw them keeping the aspect ratio. *Software decoding: about 100 fps at a Galaxy A36's full 2340 × 1080 on the test PC. Hardware (D3D11VA) decoding is for later.*
- FR-12 Follow rotation; keep the window's size across rotations.
- FR-13 Quality settings: resolution, bit rate, frame rate, codec.
- FR-14 Window options: keep on top, fullscreen (F11), turn the phone's screen off, keep awake, lock on close.
- FR-15 Show touches, restoring the phone's own setting afterwards.

### 3.3 Input

- FR-20 Mouse as finger: click, drag, long press; wheel and tilt wheel scroll.
- FR-21 Right click = Back, middle click = Home, side buttons = Recents and notifications; toolbar for Power, Volume, Rotate, Back, Home, Recents, Notifications.
- FR-22 Keyboard: text as text (any PC layout), keys without text and Ctrl/Alt shortcuts as key events. Characters outside ASCII, which scrcpy-server cannot type, are pasted through the phone's clipboard. Alt+F4 and F11 stay with Windows.
- FR-23 Pinch to zoom with Ctrl + drag.

### 3.4 Audio and recording

- FR-30 Play the phone's audio on the PC (Android 11+), with mute and volume.
- FR-31 Record to MP4 without re-encoding.
- FR-32 Screenshot to PNG and the clipboard.

### 3.5 Files and clipboard

- FR-40 Drop an APK to install it; drop any other file to copy it to `/sdcard/Download` and scan it into the gallery.
- FR-41 Two-way clipboard: phone to PC automatically, PC to phone on Ctrl+V.
- FR-43 Gallery: photos and videos by album from Android's MediaStore (`content query`), phone-made thumbnails (`content read …/thumbnail`, so HEIC and video need no PC codecs) cached on disk, open, download, upload with media scan, delete through MediaStore. **Done in 1.3.0.**
- FR-42 File manager: browse the phone's storage, download, upload (button and drop from Explorer), new folder, rename without overwriting, delete with confirmation, open a file on the PC; unreadable folders say why. **Done in 1.1.0.**

### 3.6 Several devices

- FR-50 One window per phone, each with its own session id; a phone already open is brought forward.
- FR-51 Mirror all ready devices.

### 3.7 App

- FR-61 Auto-update from GitHub Releases with SHA-256 checking, a progress window (size, percent, speed) and Cancel.
- FR-62 Settings in `%APPDATA%\Streamy\settings.json`; errors logged to `%LOCALAPPDATA%\Streamy\streamy.log`.
- FR-63 App icon at every Windows size.

## 4. Non-functional requirements

- NFR-1 Low latency: no frame queueing anywhere (the newest frame wins) and the audio buffer is capped at 200 ms.
- NFR-2 No network traffic besides adb to the phone and update checks to GitHub. No account, no telemetry.
- NFR-3 Protocol, parsing, geometry and input mapping live in `Streamy.Core` without Windows dependencies and are unit tested; protocol tests use scrcpy's own byte vectors.
- NFR-4 One exe. *The goal was under 60 MB; it is about 70 MB (the .NET runtime, adb and FFmpeg's decoder make most of it).*

## 5. History

| Version | Content |
| --- | --- |
| 0.1.0 | Repo, device list, pair and connect, session handshake, CI and releases |
| 0.2.0 | Video decoding and display, rotation, show touches |
| 0.3.0 | Mouse and keyboard, toolbar, clipboard |
| 1.0.0 | Sound, recording, screenshots, files and APKs, QR pairing, Wi-Fi switch, saved phones, phone details, settings, updater, icon |
| 1.1.0 | File manager |
| 1.2.0 | Update progress window, icons on buttons |
| 1.3.0 | Gallery |

## 6. Later

1. Hardware decoding (D3D11VA) to save CPU at high resolutions.
2. Camera mirroring (`video_source=camera`).
3. Opening single apps in their own window on a virtual display (scrcpy 3+ `new_display`).
4. Code signing.
