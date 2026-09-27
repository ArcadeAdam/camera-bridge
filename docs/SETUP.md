# More settings and help

Start with the [short setup guide](../README.md). Use this page if you need more detail.

## Camera settings

Keep `CameraBridge.cfg` beside `CameraBridge.exe`. Click **Edit settings** in the app to stop capture and open the file in Notepad. Save it, close Notepad, then click **Start camera**.

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
| `camera` | `auto` picks the first webcam. Use **Camera names** in the app to find the name of another webcam. |
| `address` | `auto` picks a LAN address. If it picks the wrong one, enter this computer's own IPv4 address. |
| `camera_port` | Port used by the game. Leave it at `18080` unless you also change the game's camera row. |
| `preview_port` | Local preview and status port. Default: `80`. |
| `fps` | Requested camera speed, from 1 to 30. The webcam and game may run more slowly. |
| `jpeg_quality` | Picture quality, from 1 to 100. Higher numbers make larger images. Default: `85`. |
| `mirror` | Use `true` to flip the picture left to right. |

The app shows the camera destination. Check that it uses this cabinet's own LAN address and matches its seat address in the emulator. To find the computer's IPv4 address, open Command Prompt and type `ipconfig`; use the active network connection.

Keep the local preview on `127.0.0.1`. The game's camera row uses the cabinet's LAN address instead. Each bridge accepts game requests only from its own configured computer. Pictures stay in memory; the bridge does not save photos or record video.

## GP2 linked photo fix

If your own photo works but the other player's photo is black, apply these settings on **both cabinets**. This fixed the reported problem in a linked GP2 race with portable version rc.4. The same requirement has not been tested for GP1.

1. Close GP2 and the emulator.
2. Open the folder that contains the emulator's EXE. In the tested portable setup, its settings are in `User\GameSettings`.
3. Make a copy of `GNLE82.ini` as a backup. Open the original with Notepad.
4. Find `[Video_Hacks]`. Add or change the two settings below in that section. If the section is missing, add all three lines at the end. Keep the rest of the file.

```ini
[Video_Hacks]
EFBToTextureEnable = False
DeferEFBCopies = False
```

5. Save the file and repeat on the other cabinet.
6. Start a new GP2 session on both. Take new photos, enter a linked race, and check the other player's photo on each screen.

If `GNLE82.ini` is missing, create it inside `User\GameSettings` with those three lines. In Notepad's **Save As** window, choose **All Files** so it is not saved as `GNLE82.ini.txt`. This file is for GP2 with game ID `GNLE82`; another game ID needs its own matching file. If your emulator uses a separate user-data folder, use its `GameSettings` folder instead.

These lines turn off **Store EFB Copies to Texture Only** and **Defer EFB Copies to RAM** for GP2. They make copied pictures available in RAM immediately. That can let the game share a picture it could already display locally. The two settings together fixed the reported problem; they have not been tested one at a time. RAM copies can affect performance, so check game speed too. [Dolphin's settings guide](https://dolphin-emu.org/docs/guides/settings/) explains these options.

To undo the change, close the emulator and restore your backup. This setup changes only a game settings file. No bridge update or emulator executable patch is needed.

## Example network setup

If linked play already works, keep its seat rows. Change only the camera row in **Triforce → IP Address Redirections**.

These are example addresses. Replace them with the addresses of your own computers.

| Setting | Cabinet A | Cabinet B |
| --- | --- | --- |
| Camera Emulated address | `192.168.29.104-107` | `192.168.29.104-107` |
| Camera Real address | `192.168.1.2:18080` | `192.168.1.3:18080` |
| CFG `address`, if entered by hand | `192.168.1.2` | `192.168.1.3` |
| In-game `PCB ID` | `1` | `2` |

Enter only the address and port in **Real**, without `http://` or `/img.jpg`. Each cabinet points to its own camera. Restart the emulator after changing routes.

![Example IP table for cabinet A](images/triforce-ip-redirections.png)

The tested setup used these seat rows on both cabinets. Keep the rows with port ranges above the rows without ports.

| Emulated | Real in this example | Seat |
| --- | --- | --- |
| `192.168.29.150:5000-5008` | `192.168.1.2` | 1 |
| `192.168.29.151:5000-5008` | `192.168.1.3` | 2 |
| `192.168.29.152:5000-5008` | `192.168.1.4` | 3 |
| `192.168.29.153:5000-5008` | `192.168.1.5` | 4 |
| `192.168.29.150` | `192.168.1.2` | 1 |
| `192.168.29.151` | `192.168.1.3` | 2 |
| `192.168.29.152` | `192.168.1.4` | 3 |
| `192.168.29.153` | `192.168.1.5` | 4 |

Seats 3 and 4 are for extra cabinets. Keep unrelated rows already in your settings. In each game's test menu, enable the camera and give each linked cabinet its own **GAME OPTIONS → PCB ID**.

## Quick fixes

| Problem | Try this |
| --- | --- |
| No picture in the app | Close other webcam apps and older bridges. Check `camera` in the CFG. |
| Wrong camera address | Stop the bridge and set `address` to this cabinet's own LAN IPv4 address. |
| Port already in use | Stop the older bridge. If you change `camera_port`, change the game's camera row too. |
| Game reports a camera error | Check camera enablement and the camera row. Restart the game with the bridge already running. |
| Remote GP2 photo is black | Apply the [GP2 photo fix](#gp2-linked-photo-fix) on both cabinets and take new photos. |
| Camera stops too soon or stays on | Check the [LaunchBox guide](../FRONTEND.md#troubleshooting). |
| Settings did not change | Stop the bridge, save the CFG beside its EXE, then start it again. |

For manual use, closing the bridge stops capture. For automatic start and stop, follow the [LaunchBox guide](../FRONTEND.md).
