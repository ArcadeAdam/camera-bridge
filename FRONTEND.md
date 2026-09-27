# Optional LaunchBox start and stop

Camera Bridge can run on its own. These steps are only for users who want LaunchBox to start the camera before a game and stop it afterward.

The portable package contains **CameraBridge.exe** and **CameraBridge.cfg**. It does not edit LaunchBox, emulator settings, game profiles, or cabinet routes.

## Before you begin

1. Finish the [standalone setup](README.md#download-and-start).
2. Open **CameraBridge.exe** and check its preview.
3. Start the game manually. Check its camera test and in-game image.
4. Close the game and close Camera Bridge.

Use each cabinet's own bridge folder and CFG. The examples below use `C:\Tools\Camera`.

## Add two Additional Apps

In LaunchBox, edit the game and open **Additional Apps**. Add the following two entries. Put the executable path and arguments in their separate fields.

| Field | Start entry | Stop entry |
| --- | --- | --- |
| Application Name | `Start Camera Bridge` | `Stop Camera Bridge` |
| Application Path | `C:\Tools\Camera\CameraBridge.exe` | `C:\Tools\Camera\CameraBridge.exe` |
| Application Command-Line Parameters | `--start` | `--stop` |
| Automatically Run Before Main Application | Checked | Unchecked |
| Automatically Run After Main Application | Unchecked | Checked |
| Wait for Exit | **Checked** | **Unchecked** |

Replace the example path with the actual folder. Leave emulator handling disabled for these helper entries. Keep the game's existing launch settings and unrelated Additional Apps.

Save the entries. Repeat for the other supported game, then repeat on the other cabinet.

## What the commands do

`--start` launches a hidden background app and waits for a fresh camera frame before returning. **Wait for Exit** makes LaunchBox wait for that short startup command, not for the whole camera session. The preview window stays out of the game's way.

`--stop` stops the local bridge and releases the webcam. It affects only the copy configured on that computer.

For manual use, double-click the EXE to show the preview/status window. Its **Stop** button releases the camera; closing the app also stops capture. Stop the app before changing its CFG.

## Test the full launch

Test each game on each cabinet.

1. Begin with Camera Bridge stopped.
2. Launch the game from LaunchBox. Do not start the bridge yourself.
3. Check that the camera test passes and that the image appears in the game.
4. Exit the game normally. Check that the webcam light turns off, if it has one.
5. Launch and exit again to check that the camera can reopen.
6. Test linked play with both cabinets. Exiting one cabinet should release only its own camera.

The new portable build still needs these fresh game and lifecycle checks. Successful tests of the earlier installed edition do not automatically cover this rewrite.

## Troubleshooting

**The camera never starts:** Open the EXE directly and read its status. Check the CFG, webcam availability, and ports. Confirm that the start entry points to the EXE and has only `--start` in its arguments. A failed before command does not necessarily prevent LaunchBox from launching the game.

**The camera stops while the game is running:** A launcher may start a child process and exit. LaunchBox can then think the game has ended and run the stop entry too soon. Check how your existing game entry tracks its session.

**The camera stays on after exiting:** A launcher may remain open after the game closes. Check the same session tracking. Until that is fixed, disable only the stop entry and stop the bridge manually after playing.

**Another copy or an older bridge is running:** Stop it before testing this version. Two apps cannot normally share the same webcam or listening ports.

Avoid overlapping game sessions on one computer: the stop entry stops that computer's bridge.

## Remove the integration

Delete only **Start Camera Bridge** and **Stop Camera Bridge** from the game's Additional Apps list. Repeat for the other game if needed.

The bridge then returns to manual use. No other game or frontend settings need to change.
