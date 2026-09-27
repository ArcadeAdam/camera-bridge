# Camera Bridge

![Two linked cabinets showing their local camera images](docs/images/linked-cabinets.jpg)

*Both linked cabinets showing camera images with the earlier installed edition. The new portable build still needs a fresh game test.*

Camera Bridge lets a USB webcam act as a network camera. It serves a small, complete 320 x 240 JPEG for each camera request. Each cabinet uses its own webcam and its own copy of the app.

## Download and start

**[Download Camera Bridge for Windows](https://github.com/ArcadeAdam/camera-bridge/releases/download/v1.0.0-rc.3/camera-bridge-1.0.0-rc.3-win-x64.zip)** (release candidate).

The portable ZIP contains only:

- `CameraBridge.exe`
- `CameraBridge.cfg`

Use Windows 10 or 11, 64-bit, with the system's .NET Framework 4.8 or newer. No separate runtime download, installer, or administrator access is normally needed.

1. Right-click the downloaded ZIP and choose **Extract All**. Keep both files together in a folder, such as `C:\Tools\Camera`.
2. Stop any older camera bridge so it releases the webcam and ports.
3. Double-click **CameraBridge.exe**.
4. Check the preview and the camera destination shown in the app.
5. Set the game's camera route as explained below, then start the game.

The app starts capture when opened. **Stop** releases the webcam. **Closing the app also stops capture and releases the webcam.** The package does not edit your emulator or frontend.

## Settings

Click **Edit settings** to stop capture and open `CameraBridge.cfg` in Notepad. Save your changes, close Notepad, then click **Start camera**. Use **Camera names** in the app if you need the exact webcam name.

The default file is:

```ini
camera=auto
address=auto
camera_port=18080
preview_port=80
fps=30
jpeg_quality=85
mirror=false
```

| Setting | What it does |
| --- | --- |
| `camera` | `auto` selects the first webcam. Enter the exact device name to choose another. |
| `address` | `auto` finds a LAN address. Enter this computer's own IPv4 address if it chooses the wrong adapter. |
| `camera_port` | Port used by the game's camera route. Default: `18080`. |
| `preview_port` | Local preview/status port. Default: `80`. |
| `fps` | Requested capture rate, from 1 to 30. The webcam must support it. |
| `jpeg_quality` | Image quality from 1 to 100. Higher values make larger images. Default: `85`. |
| `mirror` | Use `true` to flip the image left to right. |

Always check the displayed camera destination. It must use **this cabinet's own LAN address**, matching its source address in the emulator's seat mappings. Do not copy another cabinet's address into this file.

Requesting 30 FPS does not make the game show 30 camera images per second. The webcam and game may run more slowly. The app shows the actual capture rate.

## Set the camera route

Open **IP Address Redirections** in the emulator's **Triforce** settings. If linked play already works, **keep all eight seat mappings** and change only the camera row labeled `namcam2`.

This screenshot shows cabinet A at `192.168.1.2`:

![Cabinet A IP Address Redirections with the camera destination at 192.168.1.2:18080](docs/images/triforce-ip-redirections.png)

Use your actual LAN addresses. The addresses below are examples.

| Setting | Cabinet A | Cabinet B |
| --- | --- | --- |
| Camera Emulated address | `192.168.29.104-107` | `192.168.29.104-107` |
| Camera Real address | `192.168.1.2:18080` | `192.168.1.3:18080` |
| CFG `address`, if set manually | `192.168.1.2` | `192.168.1.3` |
| CFG `camera_port` | `18080` | `18080` |
| In-game `PCB ID` | `1` | `2` |

Enter only the IP address and port in **Real**, without `http://` or `/img.jpg`. Cabinet B must point to its own camera, not cabinet A's. Keep the local preview on `127.0.0.1`; the game's camera route uses the LAN address.

For a new linked setup, this is the complete seat table from the screenshot. Use the **same table on each cabinet**, replacing the example Real addresses with your own. Keep the port-range rows above the rows without ports.

| Emulated | Real in this example | Seat |
| --- | --- | --- |
| `192.168.29.150:5000-5008` | `192.168.1.2` | 1, static ports |
| `192.168.29.151:5000-5008` | `192.168.1.3` | 2, static ports |
| `192.168.29.152:5000-5008` | `192.168.1.4` | 3, static ports |
| `192.168.29.153:5000-5008` | `192.168.1.5` | 4, static ports |
| `192.168.29.150` | `192.168.1.2` | 1, other ports |
| `192.168.29.151` | `192.168.1.3` | 2, other ports |
| `192.168.29.152` | `192.168.1.4` | 3, other ports |
| `192.168.29.153` | `192.168.1.5` | 4, other ports |

Seats 3 and 4 are for extra cabinets; two-player linking does not need four computers. Keep unrelated rows already present in your settings.

Restart the emulator after changing its routes. In each game's service/test menu, enable the camera and set **GAME OPTIONS > PCB ID** to match that cabinet's seat. Use ID `1` on A and `2` on B.

Start each bridge before its game. Check the camera test and the image in both games, then test linked play. If a game's boot camera test failed before the bridge started, restart that game.

## Optional LaunchBox setup

Manual use works without frontend integration. To start and stop the bridge with a game, add two **Additional Apps** entries in LaunchBox:

| Setting | Before the game | After the game |
| --- | --- | --- |
| Application Path | `C:\Tools\Camera\CameraBridge.exe` | Same path |
| Application Command-Line Parameters | `--start` | `--stop` |
| Automatically Run Before Main Application | Checked | Unchecked |
| Automatically Run After Main Application | Unchecked | Checked |
| Wait for Exit | **Checked** | **Unchecked** |

Replace the example path with your folder. `--start` starts the app in the background, waits for a fresh frame, then returns. `--stop` stops the local app and releases the camera.

The package never changes LaunchBox itself. Follow the [full manual guide](FRONTEND.md) to add, test, or remove these entries.

## If something goes wrong

| Problem | What to check |
| --- | --- |
| No camera image | Close other webcam apps. Check `camera` in the CFG and restart the bridge. |
| Wrong network address | Stop the app and set `address` to this cabinet's own LAN IPv4 address. |
| Port already in use | Stop the older bridge. If you change `camera_port`, change the game's camera route to match. |
| Preview works, but the game reports a camera error | Check camera enablement, the `namcam2` route, and the app's displayed destination. Restart the game with the bridge already running. |
| Camera stops too early after a frontend launch | Check how the frontend tracks the real game session. See [frontend troubleshooting](FRONTEND.md#troubleshooting). |
| Settings seem unchanged | Stop the app before editing, save the CFG beside the EXE, then reopen the app. |

Frames stay in memory. The bridge does not save photographs or record video. The preview is local, and the game listener accepts only requests from the configured address on the same computer.

## Test status and source

The earlier installed edition worked with both supported titles. The photo above shows both linked cabinets with their camera images. **That result does not establish that the new portable rewrite has passed those game tests.** Recheck both games, linked play, and repeated start/stop before relying on this release candidate.

See [validation records](VALIDATION.md) for recorded results and [source/development notes](docs/SOURCE.md) for technical details and the previous source-based setup.
