# Changelog

All notable changes to Streamy are listed here.
The format follows [Keep a Changelog](https://keepachangelog.com/en/1.1.0/) and versions follow [Semantic Versioning](https://semver.org/).

## [0.2.0] - 2026-11-19

The phone's screen now shows in the session window.

### Added
- Video decoding with FFmpeg (H.264, H.265, AV1) in low-delay mode. The codec configuration MediaCodec sends on its own is put in front of the next frame, as scrcpy does.
- Direct3D 11 display: the picture is converted from YUV to RGB on the graphics card, using the colours the stream declares (BT.601 or BT.709, limited or full range). Frames are never queued: when the window falls behind it skips straight to the newest one.
- The session window opens at the phone's shape (85% of the screen height for a portrait phone), keeps the picture's proportions with black bars when resized, and turns around its centre when the phone rotates.
- The window title shows the frames drawn per second.
- `--mirror SERIAL` opens a session window for that phone at start. `--probe` also decodes and saves the newest frame as a PNG beside its report.

## [0.1.0] - 2026-11-19

First preview. It connects to phones and streams from them, but does not show the picture yet.

### Added
- Device list: every phone adb sees, with its model, USB or Wi-Fi, and whether it is ready or waiting for approval on the phone. It refreshes every 2 seconds.
- Pair with code and Connect by IP for Android 11+ wireless debugging.
- Mirror sessions built on scrcpy-server 5.0.1: Streamy pushes the server, opens its own adb tunnel and connects the video, audio and control streams. Each session has its own id and port, so several phones can run at once. The session window shows the codecs, frame size, live video bit rate and the phone's clipboard.
- adb, scrcpy-server and FFmpeg are embedded in the exe and unpacked to `%LOCALAPPDATA%\Streamy\bin` on first run.
- `--probe SERIAL OUT [SECONDS]` developer switch: runs one session without a window and writes what came back.
- CI on every push and a release workflow that publishes `Streamy.exe` for each `vX.Y.Z` tag.

[0.2.0]: https://github.com/medovanx/streamy/releases/tag/v0.2.0
[0.1.0]: https://github.com/medovanx/streamy/releases/tag/v0.1.0
