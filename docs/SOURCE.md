# Source and development notes

The main [setup guide](../README.md) is for the new portable package: **CameraBridge.exe + CameraBridge.cfg**. Users of that package do not need the development runtimes described below.

The portable rewrite uses the system's .NET Framework 4.8, native webcam capture through DirectShow, and JPEG encoding through System.Drawing. Native rc.4 passed physical capture checks on both cabinets. The user also confirmed GP2 local and remote portraits on both linked cabinets after the GP2-only settings change below. A fresh native GP1 test and repeated frontend start/exit tests remain pending; earlier installed-edition results do not cover them.

## Build the portable app

From the repository root, run:

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File .\tools\Build-Portable.ps1
```

The build uses the system's .NET Framework C# compiler. It does not download runtimes or bundle third-party binaries. Its inputs are the C# files under `portable/`, the application manifest, and `CameraBridge.cfg`.

The output is `dist\camera-bridge-1.0.0-rc.4-win-x64.zip`, containing only `CameraBridge.exe` and `CameraBridge.cfg`. Compile success is not a camera or game test. Check the built files before distribution and record fresh results separately.

Run the native socket tests from the repository root:

```powershell
& "$env:WINDIR\Microsoft.NET\Framework64\v4.0.30319\csc.exe" /nologo /target:exe /platform:x64 /reference:System.Drawing.dll /out:dist\ServerTests.exe portable\CameraServer.cs portable\tests\ServerTests.cs
.\dist\ServerTests.exe
.\portable\tests\Test-Portable.ps1 -ExePath .\dist\camera-bridge-1.0.0-rc.4-win-x64\CameraBridge.exe
```

The process tests use a generated picture and temporary ports. Run them in the same Windows user session as the app; a restricted sandbox identity can differ from the user that launches the child process. They do not start games or change cabinet settings. After changing or rebuilding the app, also check a live webcam and the game itself.

## GP2 linked photos

Native rc.4 displayed both cabinets' local photos, but each remote player's photo was black. The user confirmed the fix after adding these settings to each cabinet's GP2-only `User\GameSettings\GNLE82.ini`:

```ini
[Video_Hacks]
EFBToTextureEnable = False
DeferEFBCopies = False
```

Both settings were changed together; the minimum individual change has not been established. No equivalent GP1 requirement has been confirmed. The existing guarded 20-second LAN-check patch, emulator executable, and IP table were preserved. These graphics settings are separate from the camera bridge's HTTP/EOF compatibility.

See the [step-by-step fix](SETUP.md#gp2-linked-photo-fix) and [validation record](../VALIDATION.md#gp2-remote-portrait-fix), including the saved configuration hash. No new capture or game FPS measurement was made during this test.

## Protocol requirements

The camera client requests `GET /img.jpg HTTP/1.0\r\n\r\n`. It may include a trailing C-string NUL. Accept a bounded zero suffix without placing it in the HTTP response.

Serve one complete **320 x 240 baseline JPEG** per connection. Include an accurate `Content-Length`, send the whole body, and close the response. Do not append ordinary bytes after the JPEG end marker `FF D9`.

The inspected second title accumulates positive receives and waits for a zero-byte receive before it checks the JPEG and strips the HTTP headers. Its receive buffer is 512 KiB. The earlier bridge caps image data at 500 KiB to leave header space. The first title's exact parser limits were not independently established.

## EOF compatibility

The tested emulator's host-socket polling ignores a final hangup flag when checking normal read readiness. A complete, valid JPEG can therefore still leave the game waiting for EOF.

The proven response sequence is:

1. Send every normal HTTP header and JPEG byte, handling partial sends.
2. Send exactly one zero byte as TCP out-of-band data.
3. Shut down the socket's send side.
4. Close it without an abortive linger setting.

The urgent byte remains outside the normal JPEG stream. Do not append it to the body or enable inline urgent data in the test client. Avoid zero-time abortive closure. Consume the bounded request suffix so unread request bytes do not cause a connection reset.

A useful regression test must use the original `WSAPoll` read mask `0x300`, verify that all normal bytes are unchanged, and reach a zero-byte receive. Testing only with a broadened `0x302` mask hides the original problem.

The synthetic [OOB probe](../tools/Test-OobEof.ps1) creates loopback sockets without opening a webcam or starting a game.

## Address and lifecycle requirements

The game-facing listener uses this computer's LAN IPv4 address because the tested emulator binds outgoing traffic to that address. A bound LAN source could not connect directly to loopback on the tested host.

Accept camera traffic only from the configured same-computer address. Keep preview and management on loopback. Preserve cabinet-to-cabinet seat mappings; the only camera route change is its destination address and port.

Capture should keep one latest complete frame in memory. Stop must release the webcam, sockets, and workers. Repeated startup must not leave an older capture process or locked device behind.

The portable command contract is `CameraBridge.exe --start` for hidden startup with a fresh-frame readiness wait, and `CameraBridge.exe --stop` for shutdown. Manual app closure also releases the camera.

## Previous source implementation

The following instructions apply only to the earlier script-based source implementation retained in this repository. They are not installation steps for the portable EXE, and its JSON settings are not interchangeable with `CameraBridge.cfg`.

It uses:

- Node.js 18 or newer for the server. Tested versions: 24.17.0 and 24.21.0.
- Python 3 for the socket helper. Tested version: 3.14.6.
- FFmpeg with DirectShow input and MJPEG encoding. Tested version: 8.1.2.

Only standard libraries are used; there are no npm or pip packages to install. Runtime executables are not bundled. A shared FFmpeg build needs its accompanying DLLs.

### Source setup

Run [Setup Camera.cmd](../Setup%20Camera.cmd), or copy [bridge.config.example.json](../bridge.config.example.json) to `bridge.config.json` in the repository root and edit it.

Setup creates only that JSON configuration. It supports `-NoPrompt`, `-WhatIf`, and `-ReplaceExisting`; replacement saves a backup. See `Get-Help .\Setup-Camera.ps1`.

| JSON setting | Value |
| --- | --- |
| `ffmpegPath` | Full or configuration-relative path to `ffmpeg.exe`. |
| `pythonPath` | Full or configuration-relative path to Python 3. |
| `cameraName` | Exact DirectShow device name. |
| `selfAddress` | This computer's own LAN IPv4 address. |
| `selfPort` | Camera listener port, normally `18080`. |
| `port` | Local preview port, normally `80`. |
| `eofCompat` | Keep `true` for the tested emulator. |
| `fps` | Requested capture rate from 1 to 30. |
| `jpegQuality` | FFmpeg quality from 2 to 31; lower is higher quality. This differs from the portable CFG's 1-100 scale. |
| `mirror` | `true` flips the image left to right. |

If runtime path fields are omitted, discovery checks `CAMERA_FFMPEG_PATH` / `CAMERA_PYTHON_PATH`, then `PATH`. A bad explicit path fails rather than selecting another executable. Startup uses `nodePath`, then `CAMERA_NODE_PATH`, then `node.exe` on `PATH`. Relative paths resolve from the configuration folder.

Use a complete executable path in these PowerShell examples:

```powershell
& 'C:\Tools\ffmpeg.exe' -hide_banner -list_devices true -f dshow -i dummy
& 'C:\Tools\ffmpeg.exe' -hide_banner -list_options true -f dshow -i 'video=USB Video Device'
```

A device-listing command can report an input error after printing the list. Choose a webcam mode that supports the requested rate. Each cabinet needs its own JSON address and device settings.

### Source startup and tests

From the repository root:

```powershell
.\Start-Camera.ps1 -NoBrowser
.\Stop-Camera.ps1
node --test
.\tools\Test-OobEof.ps1
```

The source start script waits for a fresh frame and leaves capture running in the background. Closing its browser page does not stop capture; use its stop script or the page's stop control. This differs from closing the portable app.

[The running-bridge probe](../Test-CameraEof.ps1) reads the JSON configuration and tests the active source bridge. Integration tests report explicit skip reasons when required runtimes are missing.

For a foreground source server:

```powershell
node server.js
node server.js --config .\test.config.json --test-pattern
```

Use separate ports in `test.config.json` when another bridge is running. Test-pattern mode still needs FFmpeg.

| Source endpoint | Purpose |
| --- | --- |
| `/` | Preview and status page. |
| `/img.jpg` | Latest JPEG; counts a game request. |
| `/preview.jpg` | Same image without the game counter. |
| `/health` | Frame age/count, request counts, and last error. |
| `POST /api/stop` | Local shutdown. |

Inspect `logs\bridge.log` for source-runtime errors. A rising request counter does not prove that the game decoded the image; check the game itself.

## Packaging and validation

[tools/build_source_zip.py](../tools/build_source_zip.py) creates an allowlisted source archive and SHA-256 manifest. That archive is separate from the two-file portable download. Never package a live cabinet folder, local settings, logs, diagnostic backups, game images, or emulator binaries.

The published portable asset is `camera-bridge-1.0.0-rc.4-win-x64.zip` under tag [`v1.0.0-rc.4`](https://github.com/ArcadeAdam/camera-bridge/releases/tag/v1.0.0-rc.4). Its release page includes `SHA256SUMS.txt` for checking the download. GP2 local and remote linked portraits are user-confirmed with the settings above; a fresh native GP1 test and repeated frontend start/exit tests remain pending.

For the native rewrite, record the compiler target and binary hash, then check capture/JPEG format, exact-response EOF behavior, same-host access, busy ports, stop/restart, both games, linked play, and frontend lifecycle. Keep native results separate from the previous implementation in [VALIDATION.md](../VALIDATION.md).

Earlier measurements were about 29.87 capture FPS, roughly 10 image requests per second from the second title, and about 34 ms per production response. An isolated helper benchmark improved from a 20.8 ms median to 0.46 ms after reducing request-suffix waiting. These measurements describe different tests of the earlier implementation, not the portable rewrite's performance.
