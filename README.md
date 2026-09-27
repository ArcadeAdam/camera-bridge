# Camera Bridge

Use a USB webcam as the network camera for two supported arcade titles in the tested emulator. The bridge keeps the webcam open and serves its latest complete **320 × 240 baseline JPEG** at `/img.jpg`.

The user reports both supported titles working on the installed cabinets with the original, unmodified emulator executable. A linked session of the second title was also reported working; a paired race with both webcams active has not been separately confirmed. Both cabinets have passed standalone bridge capture and EOF checks, and their previous game sessions logged bridge shutdown followed by fresh capture during subsequent sessions. These live results describe the installed cabinet version, not a new live test of this renamed public candidate. See [VALIDATION.md](VALIDATION.md) for confirmed checks and remaining play-tests.

## Requirements

- Windows with Windows PowerShell and a USB webcam accessible through DirectShow (`dshow`).
- Node.js **18 or newer** for the server; tested versions **24.17.0** and **24.21.0**.
- Python **3** for the compatibility helper; tested version **3.14.6**.
- FFmpeg with DirectShow input and MJPEG (`mjpeg`) encoding; tested version **8.1.2**.
- Your existing tested emulator installation and games.

The bridge uses the runtimes' standard libraries and requires no npm or pip package installation. Node.js, Python, FFmpeg, emulator/launcher binaries, and game files are not bundled. Use a complete FFmpeg distribution: shared builds also need their accompanying DLLs.

## Configure this computer

Extract the source package into a writable folder. For a fresh installation, double-click **Setup Camera.cmd** and follow the prompts for runtime paths, webcam name, and this computer's local IP address. Setup creates only the bridge configuration and prints the camera redirection to enter manually. It does not download runtimes, start applications, or change the emulator or frontend.

The setup script also supports unattended parameters with `-NoPrompt`, previewing changes with `-WhatIf`, and explicitly replacing an existing configuration with `-ReplaceExisting`; replacement creates a backup. Check `Get-Help .\Setup-Camera.ps1` for its parameter names.

For manual setup instead, change to the extracted folder in Windows PowerShell and create your local settings:

```text
Copy-Item .\bridge.config.example.json .\bridge.config.json
```

Edit `bridge.config.json` before starting. The example's paths and IP are placeholders.

| Setting | What to enter |
| --- | --- |
| `ffmpegPath` | Path to this computer's `ffmpeg.exe`. |
| `pythonPath` | Path to this computer's Python 3 executable. |
| `cameraName` | Exact DirectShow video-device name. |
| `selfAddress` | This computer's own LAN IPv4 address, matching its cabinet source mapping in the emulator. |
| `selfPort` | Game-facing camera port; the example uses `18080`. |
| `port` | Local preview/status port; the example uses `80`. |
| `eofCompat` | Keep `true` for the tested host emulator build. |
| `fps` | Requested capture rate, 1–30; the example uses 30. |
| `jpegQuality` | Encoder JPEG quality, 2–31; smaller values produce larger, higher-quality images. |
| `mirror` | Set `true` to flip the image horizontally. |

Explicit FFmpeg and Python paths can be absolute or relative to the configuration file. If you omit a JSON field, the bridge checks `CAMERA_FFMPEG_PATH` or `CAMERA_PYTHON_PATH`, then searches `PATH`. An invalid explicit setting causes an error instead of silently selecting another executable. Remove a placeholder field if you want environment or `PATH` discovery.

The start script finds `node.exe` on `PATH`. Alternatively, add `nodePath` to your configuration or set `CAMERA_NODE_PATH`. Relative runtime paths resolve from the bridge folder. JSON paths need escaped backslashes, for example `"C:\\Tools\\Runtime\\node.exe"`.

Find the webcam's exact name using the FFmpeg executable selected in your configuration. Run these commands in Windows PowerShell from the bridge folder. For a relative path, resolve it from that folder first. If you omitted the field for automatic discovery, set `$captureExecutable` to its actual full path instead:

```text
$cameraSettings = Get-Content .\bridge.config.json -Raw | ConvertFrom-Json
$captureExecutable = $cameraSettings.ffmpegPath
& $captureExecutable -hide_banner -list_devices true -f dshow -i dummy
```

Inspect its capture modes, substituting the reported name:

```text
& $captureExecutable -hide_banner -list_options true -f dshow -i "video=USB Video Device"
```

These commands can end with an input error after printing the device or mode list. Choose a device supporting 320 × 240 at your requested rate. One tested webcam appears as `USB Video Device`.

Each cabinet runs its own bridge and webcam, with its own local configuration. Do not copy another cabinet's `selfAddress` unchanged. The bridge rejects an address that is not assigned to the local computer.

## Connect the emulator

In the emulator's arcade IP redirections, change only the camera row:

| Emulated address | Real address | Description |
| --- | --- | --- |
| `192.168.29.104-107` | Your `selfAddress:selfPort`, for example `192.168.1.100:18080` | Arcade camera |

Preserve every cabinet seat mapping. Enter an IP and port, without `http://` or `/img.jpg`. Restart the emulator after editing the mapping.

The listener uses the computer's LAN address because the tested emulator configuration binds outgoing sockets to that address. The host rejected a connection from that bound LAN address directly to loopback with error 10049. The listener forwards same-computer requests to the preview server without changing cabinet mappings used for linked play.

Enable the camera in each game's service/test settings. Start the bridge before booting the game, then run Camera Test. If the boot camera check already failed, restart the game with the bridge running.

## Start and stop

Double-click **Start Camera.cmd**. It starts the bridge in the background, waits for a fresh frame, and opens the preview/status page. With the example preview port, that page is [http://127.0.0.1/](http://127.0.0.1/).

Closing the page leaves the camera running. Click **Stop camera** on the page or double-click **Stop Camera.cmd** to stop it. For launch scripts, use `Start-Camera.ps1 -NoBrowser`; it reuses an already healthy instance. Stop the bridge before changing settings.

The bridge installs no host startup entry. If a configured port is busy, choose a free port and update the corresponding setting. Changing `selfPort` also requires changing the camera destination in the emulator.

## Optional frontend integration

The public package provides [manual frontend setup instructions](FRONTEND.md). It includes no frontend configuration installer, machine XML, or configuration backups. Standalone Setup writes only the bridge's local configuration; Setup and normal Start/Stop never edit the frontend.

Add two Additional Apps separately to **the first supported title** and **the second supported title**: run `Start-Camera.ps1 -NoBrowser` before the game with **Wait for Exit** enabled, and run `Stop-Camera.ps1` afterward with no wait. The guide provides the exact executable path, hidden-window command lines, checkbox settings, and launch/exit checks. Preserve unrelated applications and leave unrelated titles unchanged.

Verify a real launch and exit on each computer. The frontend must track the actual game session through the launcher for the stop application to run at the correct time. A configured entry or successful standalone camera check does not establish that launcher lifecycle behavior.

## Capture rate and privacy

`fps: 30` requests 30 captures per second. It does not make the game request or display 30 images per second. During the tested second title session, capture measured about 29.87 FPS while the game requested approximately 10 images per second. The browser's alignment preview refreshes about twice per second; that timer does not control the game's camera requests. See [VALIDATION.md](VALIDATION.md) for timing details.

Frames stay in memory and are replaced continuously. The bridge does not save photographs or record video. Logs contain service/request metadata, not image data. The preview and management server binds to `127.0.0.1`; the game listener accepts only peers whose source is the configured local `selfAddress`, rejecting other LAN peers before forwarding a request.

## End-of-stream compatibility

The tested emulator build drops the final host socket hangup notification from its emulated read set. The second title requires a zero-byte receive to finish its image, so an otherwise valid response can leave it at CAMERA ERROR.

The compatibility listener sends one TCP urgent, out-of-band byte before closing. This lets the original emulator observe read readiness and receive EOF. The byte is outside the ordinary HTTP/JPEG stream; native tests verified unchanged image bytes. The helper also accepts and strips the game's bounded trailing NUL suffix after its request headers.

Keep this listener enabled for the tested build even when the preview works. The preview server alone uses ordinary HTTP and does not supply the EOF workaround. No emulator or launcher executable modification is required.

## Diagnostics and development

| Endpoint | Purpose |
| --- | --- |
| `/` | Preview and status page. |
| `/img.jpg` | Latest complete 320 × 240 JPEG; increases the game request counter. |
| `/preview.jpg` | Same image without increasing that counter. |
| `/health` | JSON status, frame age/count, configured capture FPS, request counts, and last error. |
| `POST /api/stop` | Stop through the local management server. |

A rising request counter proves requests reached the server; verify the image in the game too. If it stays at zero, check camera enablement and the running emulator configuration. If capture fails, close other webcam apps and check the capture-backend name and mode. Inspect `logs\bridge.log` for errors.

Run tests with Node.js on `PATH`:

```text
node --test
```

Integration tests that invoke external runtimes need their executables through the local configuration, documented environment variables, or `PATH`. Unavailable runtimes produce explicit skip reasons. For native EOF diagnostics:

```text
.\Test-CameraEof.ps1
.\tools\Test-OobEof.ps1
```

The first probe uses the running bridge and local configuration. The second creates synthetic loopback connections and needs neither webcam nor emulator.

For a foreground server or test pattern:

```text
node server.js
node server.js --config .\test.config.json --test-pattern
```

Create `test.config.json` from the example with separate preview and game-listener ports before running it beside the normal bridge. Test-pattern mode replaces webcam capture; it still needs FFmpeg and retains the configured compatibility listener. Keep local configurations, logs, diagnostic backups, and emulator/game binaries out of source archives.

For a source release, `tools/build_source_zip.py` builds an allowlisted archive and a SHA-256 manifest. Publish only the inspected archive contents, with confirmed and pending validation results recorded accurately; do not upload the live cabinet folder. Local frontend helpers and machine data are excluded from the public source package.

## Observed camera protocol

Both locally inspected game images request `GET /img.jpg HTTP/1.0`. The second title uses TCP port 80 before redirection, scans for the HTTP header separator, and requires JPEG end marker `FF D9`. Its receive buffer is 512 KiB; the bridge caps images at 500 KiB to leave room for headers. The first title's exact parser limits were not independently established.
