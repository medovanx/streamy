# Changelog

All notable changes to Streamy are listed here.
The format follows [Keep a Changelog](https://keepachangelog.com/en/1.1.0/) and versions follow [Semantic Versioning](https://semver.org/).

## [0.1.0] - 2026-11-19

First preview. It connects to phones and streams from them, but does not show the picture yet.

### Added
- Device list: every phone adb sees, with its model, USB or Wi-Fi, and whether it is ready or waiting for approval on the phone. It refreshes every 2 seconds.
- Pair with code and Connect by IP for Android 11+ wireless debugging.
- Mirror sessions built on scrcpy-server 5.0.1: Streamy pushes the server, opens its own adb tunnel and connects the video, audio and control streams. Each session has its own id and port, so several phones can run at once. The session window shows the codecs, frame size, live video bit rate and the phone's clipboard.
- adb, scrcpy-server and FFmpeg are embedded in the exe and unpacked to `%LOCALAPPDATA%\Streamy\bin` on first run.
- `--probe SERIAL OUT [SECONDS]` developer switch: runs one session without a window and writes what came back.
- CI on every push and a release workflow that publishes `Streamy.exe` for each `vX.Y.Z` tag.

[0.1.0]: https://github.com/medovanx/streamy/releases/tag/v0.1.0
