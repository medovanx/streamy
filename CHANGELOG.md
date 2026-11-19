# Changelog

All notable changes to Streamy are listed here.
The format follows [Keep a Changelog](https://keepachangelog.com/en/1.1.0/) and versions follow [Semantic Versioning](https://semver.org/).

## [1.3.0] - 2026-11-20

### Added
- Gallery: the phone's photos and videos by album in a thumbnail grid that scrolls smoothly through thousands of items. Thumbnails come from the phone, so HEIC photos and videos show without any codec on the PC; they load visible-first, eight at a time, and are cached on disk so an album opens at once the next time. Videos show their length. Select with click, Ctrl, Shift or Ctrl+A; open on the PC with a double-click; download a selection; upload into the open album (the phone indexes the files right away); delete after a confirmation. Open it with **Gallery** in the main window, on a phone's right-click menu, or from its toolbar.
- `--gallery SERIAL OUT` developer switch that tests every gallery operation with a generated picture.

## [1.2.0] - 2026-11-19

### Added
- Updates show their progress: a window with a progress bar, megabytes downloaded of the total, percent and speed, then the checksum check and the restart. **Cancel** stops the download and keeps the running version; a failed update says why.
- Icons on every button (main window, file manager, Settings, dialogs), centred together with the text.

## [1.1.0] - 2026-11-19

### Added
- File manager: browse the phone's storage with Windows' own icons, sizes and dates; download files and folders to the PC; upload with a button or by dropping files and folders from Explorer; create folders, rename (never overwriting) and delete with a confirmation. Double-click a file to open it on the PC. **Places** jumps to Download, Camera, Screenshots and other common folders, and any path can be typed. Open it with **Files** in the main window, **Browse files** on a phone's right-click menu, or the new folder button on a phone's toolbar.
- `--files SERIAL OUT` developer switch: runs every file operation in a throwaway folder on the phone.

## [1.0.0] - 2026-11-19

Everything planned for 1.0: sound, recording, files, Wi-Fi pairing, phone details and settings.

### Added
- The phone's sound plays on the PC (Android 11+): Opus decoded with FFmpeg and played through WASAPI with a 40 ms buffer that follows the default output device. Anything queued past 200 ms is dropped so sound stays with the picture. Mute from the toolbar; volume in Settings.
- Record to MP4 from the toolbar: the phone's video and sound are written without re-encoding, starting at once (Streamy asks the phone for a fresh key frame) and surviving rotations. The title bar shows the running time.
- Screenshot from the toolbar: the full-resolution frame is saved as PNG and copied to the clipboard.
- Drop files on a phone's window: APKs are installed or updated, other files go to the phone's Download folder and show up in Gallery and Files at once.
- Phone details beside the device list: Android version and security patch, model, build, CPU (chip, cores, top clock), GPU, RAM, storage, battery and screen, refreshed every 15 seconds. Right-click to copy them.
- Pair with QR: Streamy shows the code Android's Wireless debugging scans, finds the phone over mDNS and pairs.
- Switch to Wi-Fi: moves a USB phone to Wi-Fi (TCP mode) in one click, for any Android version.
- Wi-Fi phones are remembered, listed while away and reconnected by themselves. Right-click a phone to disconnect or forget it.
- Mirror all opens a window for every ready phone; a phone that is already open is brought forward instead of opened twice.
- Settings window: resolution, bit rate, frame rate and codec; sound and volume; show touches, keep awake, screen off at start, lock on close; keep windows on top; screenshot and recording folders; update checks.
- Toolbar: Fullscreen (also F11) and Keep on top. The toolbar shrinks its buttons to fit when the window is short.
- Updates from GitHub Releases, checked at start, verified with SHA-256 and installed in place.
- App icon.
- Typing Arabic, accented letters or emoji works: characters the phone cannot type are sent through its clipboard and pasted.
- Alt+F4 closes a phone's window instead of going to the phone.
- Errors are logged to `%LOCALAPPDATA%\Streamy\streamy.log`.

### Changed
- The Show touches checkbox moved into Settings.
- Rotating the phone keeps the window's size, even after the screen made one orientation smaller.

### Fixed
- Screenshots and recordings fall back to `%USERPROFILE%\Streamy` when Windows' Controlled Folder Access blocks Pictures and Videos.
- The device list no longer rebuilds itself every 2 seconds, which could swallow a click on a button that was briefly disabled.

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

[1.3.0]: https://github.com/medovanx/streamy/releases/tag/v1.3.0
[1.2.0]: https://github.com/medovanx/streamy/releases/tag/v1.2.0
[1.1.0]: https://github.com/medovanx/streamy/releases/tag/v1.1.0
[1.0.0]: https://github.com/medovanx/streamy/releases/tag/v1.0.0
[0.3.0]: https://github.com/medovanx/streamy/releases/tag/v0.3.0
[0.2.0]: https://github.com/medovanx/streamy/releases/tag/v0.2.0
[0.1.0]: https://github.com/medovanx/streamy/releases/tag/v0.1.0
