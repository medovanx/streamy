# Changelog

All notable changes to Streamy are listed here.
The format follows [Keep a Changelog](https://keepachangelog.com/en/1.1.0/) and versions follow [Semantic Versioning](https://semver.org/).

## [0.3.0] - 2026-11-19

The phone can now be used from the PC with the mouse and keyboard.

### Added
- Mouse as a finger: click to tap, drag to swipe or scroll, hold for a long press. Ctrl + drag pinches to zoom (a second finger mirrors the first around the screen's centre).
- Right click is Back (and wakes the screen), middle click is Home, the mouse's side buttons open Recent apps and the notifications. The wheel scrolls where the pointer is, and tilting it scrolls sideways.
- Keyboard: what you type is sent as text, so the PC's keyboard layout decides the letters and Arabic works. Enter, Backspace, Delete, Tab, Esc, the arrows, Home/End, Page Up/Down and F1-F12 go as keys, and so does anything typed with Ctrl or Alt (Ctrl+A, Ctrl+C...). Held keys repeat.
- Ctrl+V (or Shift+Insert) puts the PC's clipboard on the phone and pastes it. When something is copied on the phone it lands in the PC's clipboard.
- Toolbar beside the screen: Power (right-click turns only the phone's screen off and keeps mirroring), Volume up and down, Rotate, Back, Home, Recent apps, and Notifications (right-click opens quick settings). Its buttons never take the keyboard away from the phone.

### Fixed
- The adb tunnel is removed as soon as the session's sockets are connected, so a Streamy that is killed or crashes no longer leaves an adb forward behind.

## [0.2.0] - 2026-11-19

The phone's screen now shows in the session window.

### Added
- Video decoding with FFmpeg (H.264, H.265, AV1) in low-delay mode. The codec configuration MediaCodec sends on its own is put in front of the next frame, as scrcpy does.
- Direct3D 11 display: the picture is converted from YUV to RGB on the graphics card, using the colours the stream declares (BT.601 or BT.709, limited or full range). Frames are never queued: when the window falls behind it skips straight to the newest one.
- The session window opens at the phone's shape (85% of the screen height for a portrait phone), keeps the picture's proportions with black bars when resized, and turns around its centre when the phone rotates.
- The window title shows the frames drawn per second.
- **Show touches** option in the main window: a dot appears on the phone's screen wherever it is touched while mirroring. It applies to sessions started after changing it, and the phone's own setting comes back when mirroring ends, even if Streamy is closed abruptly. The choice is saved in `%APPDATA%\Streamy\settings.json`.
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

[0.3.0]: https://github.com/medovanx/streamy/releases/tag/v0.3.0
[0.2.0]: https://github.com/medovanx/streamy/releases/tag/v0.2.0
[0.1.0]: https://github.com/medovanx/streamy/releases/tag/v0.1.0
