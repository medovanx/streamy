# Streamy: Software Requirements Specification

Status: draft for 1.0. Last updated 2026-11-19 (0.2.0).

## 1. Purpose

Streamy shows an Android phone's screen in a window on a Windows PC and lets the user control it with the
mouse and keyboard, over USB or Wi-Fi. It plays the phone's sound on the PC, records sessions, moves files and
the clipboard between the two, and handles several phones at once.

## 2. Decisions taken

| Topic | Decision |
| --- | --- |
| Name | Streamy |
| Phone side | scrcpy-server (Apache-2.0), pushed and started through adb. Nothing is installed on the phone. |
| Transport | adb over USB, or adb over Wi-Fi (Android 11+ wireless debugging with a pairing code, or `adb tcpip` on older phones). |
| PC stack | .NET 11, WinForms, Windows 10 (19041) and 11, x64. Same layout as Data Tide and Salaty Reminder. |
| Packaging | One self-contained `Streamy.exe`. adb, scrcpy-server and the FFmpeg DLLs are embedded and unpacked to `%LOCALAPPDATA%\Streamy\bin\<scrcpy version>` on first run. |
| Versioning | Semantic Versioning in `Directory.Build.props`; tag `vX.Y.Z` builds and publishes a GitHub Release. |
| Video | FFmpeg software decoding (FFmpeg.AutoGen 9.0.1, matching the bundled avcodec-63) and a Direct3D 11 renderer (Vortice.Windows) that converts YUV to RGB in a shader. |
| Pinned scrcpy | 5.0.1 (`deps.json`, SHA-256 checked). The server refuses a client with a different version string, so `ScrcpyProtocol.ServerVersion` and `deps.json` always change together. |

## 3. Functional requirements for 1.0

### 3.1 Devices and connection

- FR-1 List devices adb sees, with model, USB or Wi-Fi, and state (ready, needs approval on the phone, offline). Refresh every 2 seconds. **Done in 0.1.0.**
- FR-2 Pair with a wireless debugging code (address + six digits). **Done in 0.1.0.**
- FR-3 Connect by IP:port. **Done in 0.1.0.**
- FR-4 Pair by QR code: Streamy shows a QR in the `WIFI:T:ADB;S:<name>;P:<password>;;` format and pairs when the phone scans it (mDNS `_adb-tls-pairing`).
- FR-5 Switch a USB phone to Wi-Fi in one click (`adb tcpip 5555` then connect to its Wi-Fi address), for Android 10 and older.
- FR-6 Remember Wi-Fi devices by name and reconnect them when they show up.

### 3.2 Mirroring

- FR-10 Start a session: push the server, `adb forward tcp:0`, start the server, connect video, audio and control sockets, read device name and codecs. **Done in 0.1.0 (verified on a Galaxy A36, Android 15).**
- FR-11 Decode H.264/H.265/AV1 with FFmpeg and draw it in the session window, keeping the aspect ratio. **Done in 0.2.0 with software decoding (about 100 fps from a Galaxy A36 at 1280 px).** D3D11VA hardware decoding is still to do, to save CPU at high resolutions.
- FR-12 Follow rotation (new session packet) by resizing the window. **Done in 0.2.0.**
- FR-13 Quality settings: max size, bit rate, max fps, codec.
- FR-14 Window options: always on top, fullscreen, pixel-perfect 1:1, turn the phone screen off while mirroring, keep awake.

### 3.3 Input

- FR-20 Mouse as finger: click, drag, long press; wheel scrolls.
- FR-21 Right click = Back, middle click = Home; toolbar buttons for Back, Home, Recents, Power, Volume, Rotate, Notifications.
- FR-22 Keyboard: text is sent as text (so the phone's keyboard layout does not matter, Arabic works), special keys as key events.
- FR-23 Pinch to zoom with Ctrl + drag.

### 3.4 Audio and recording

- FR-30 Play the phone's audio on the PC (Opus, Android 11+), with mute and volume, and an option to keep sound on the phone (Android 13+).
- FR-31 Record the session to MP4 (video + audio), remuxed without re-encoding.
- FR-32 Screenshot to PNG and clipboard.

### 3.5 Files and clipboard

- FR-40 Drop an APK on the window to install it; drop any other file to copy it to `/sdcard/Download` and scan it into the gallery.
- FR-41 Two-way clipboard sync: phone to PC automatically, PC to phone on Ctrl+V. **Phone to PC is received in 0.1.0; PC side comes with input.**

### 3.6 Several devices

- FR-50 One window per phone, each with its own session id and local port. **Session design is done in 0.1.0.**
- FR-51 "Mirror all ready devices" action.

### 3.7 App

- FR-60 English and Arabic with a right-to-left layout; light and dark theme following Windows.
- FR-61 Auto-update from GitHub Releases with SHA-256 checking (same updater as Data Tide).
- FR-62 Settings in `%APPDATA%\Streamy\settings.json`.

## 4. Non-functional requirements

- NFR-1 Latency: under 100 ms glass to glass over USB at 1080p/60.
- NFR-2 Start a session in under 3 seconds over USB.
- NFR-3 No network traffic besides adb to the phone and update checks to GitHub. No account, no telemetry.
- NFR-4 Core logic (protocol, parsing, geometry, key mapping) lives in `Streamy.Core` without Windows dependencies and is unit tested; protocol tests use scrcpy's own byte vectors.
- NFR-5 Ships as one exe under 60 MB.

## 5. Milestones

| Version | Content |
| --- | --- |
| 0.1.0 | Repo, device list, pair/connect, session handshake and stream statistics, CI and releases |
| 0.2.0 | Video decoding and display (FR-11, FR-12). Done. |
| 0.3.0 | Mouse and keyboard input, toolbar (FR-20 to FR-23) |
| 0.4.0 | Audio playback, recording, screenshots (FR-30 to FR-32) |
| 0.5.0 | Files, clipboard, QR pairing, remembered devices (FR-4 to FR-6, FR-40, FR-41) |
| 0.6.0 | Settings, Arabic, updater (FR-13, FR-14, FR-60 to FR-62) |
| 1.0.0 | Multi-device polish (FR-51), a signed release checked on several phones |

## 6. Open questions

1. **Code signing.** Unsigned exes trigger SmartScreen. Is a certificate planned, or is "More info > Run anyway" acceptable as for Data Tide?
2. **Audio output.** NAudio (WASAPI) is proposed for playing decoded PCM.
3. **Camera mode.** scrcpy can stream the phone's camera as a source (`video_source=camera`). Wanted for 1.0, later, or never?
4. **Virtual display / app windows.** scrcpy 3+ can start an app on a separate virtual display (a "window" for one app). Wanted?
5. **Private repository and updates.** The repo `medovanx/streamy` is private. Running copies can't read a private
   repo's GitHub Releases without a token, so the updater (FR-61) only works once the repo is public, or with
   another download location. Decide before 0.6.0.
