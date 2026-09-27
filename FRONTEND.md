# Manual frontend start and stop

These instructions add camera startup and cleanup to **the first supported title** and **the second supported title** only. Repeat them on each computer using that computer's bridge folder and configuration. Keep your existing launcher game entries, cabinet seat mappings, and unrelated Additional Apps. Leave unrelated titles unchanged.

The public Camera Bridge package contains instructions for this integration, not a frontend configuration installer. **Setup Camera.cmd** / **Setup-Camera.ps1** create the bridge's local configuration and never edit the frontend. No machine-specific platform XML or backups are bundled.

## Before adding the entries

Complete the [standalone setup](README.md#configure-this-computer), then manually start the bridge and confirm the preview and the game's Camera Test. Close the game and stop the bridge. This separates webcam/configuration problems from the frontend timing problems.

The examples below use `C:\Tools\Camera`. Replace that folder with the actual location on this computer. Open Windows PowerShell and obtain the executable path used to launch the `.ps1` scripts:

```text
(Get-Command powershell.exe).Source
```

Copy the returned path into the Application Path field below, separately from its command-line parameters.

## Add the before and after applications

1. In the frontend, right-click **the second supported title** and open **Edit > Edit Metadata/Media**. Select **Additional Apps**, then **Add Application**. Older versions may open the game editor directly under **Edit**.
2. Create the start entry with the settings below, then create the stop entry. Put the executable and its arguments in their separate fields; do not paste the whole command into Application Path.
3. Save both entries and the game. Repeat for **the first supported title**. Use the normal configured launch route for the tests below.

| Frontend setting | Start entry | Stop entry |
| --- | --- | --- |
| Application Name | `Start Camera Bridge` | `Stop Camera Bridge` |
| Application Path | Full `powershell.exe` path obtained above | Same executable path |
| Automatically Run Before Main Application | Checked | Unchecked |
| Automatically Run After Main Application | Unchecked | Checked |
| Wait for Exit | **Checked** | **Unchecked**; leave disabled if unavailable |

For the start entry's **Application Command-Line Parameters**, enter:

```text
-NoProfile -ExecutionPolicy Bypass -WindowStyle Hidden -File "C:\Tools\Camera\Start-Camera.ps1" -NoBrowser
```

For the stop entry's **Application Command-Line Parameters**, enter:

```text
-NoProfile -ExecutionPolicy Bypass -WindowStyle Hidden -File "C:\Tools\Camera\Stop-Camera.ps1"
```

Leave emulator and legacy DOS emulation handling disabled for these two helper applications. The `-WindowStyle Hidden` argument hides their PowerShell windows; it is a PowerShell option, not a separate frontend checkbox.

**Wait for Exit** makes the frontend wait for the before application to finish. Here it waits for the short start script, which launches the bridge in the background and checks for a fresh frame before exiting; it does not wait for the camera server to stop. The script's startup deadline is 15 seconds. `-NoBrowser` prevents the preview page from opening over the game.

## Verify a real launch and exit

Run this check for each supported title separately on each computer. For a linked test, both computers need their own bridge and webcam configured first.

1. With the game closed, use **Stop Camera.cmd** so the test starts with the bridge stopped. Do not start it manually afterward; the before application should do that.
2. Launch the game normally from the frontend. Confirm its camera check passes and that your camera image appears during the game's photo/camera sequence. The browser should stay closed.
3. While the game is running, optionally open `http://127.0.0.1/health` if your configured preview port is `80` (otherwise include `:<port>`). It should show `app: "camera-bridge"`, `ok: true`, the expected `cameraAddress`, and rising `gameImageRequests` when the game is using the camera. Check the in-game image as well; requests alone do not prove successful decoding.
4. Exit through your usual game/frontend controls. Once the game session has ended, the stop application should shut down the bridge and release the webcam. The health URL should stop responding; the camera activity light should turn off if your model has one. The stop script normally finishes within a few seconds and reports an error if shutdown does not complete.
5. Launch and exit a second time to check repeat startup. Repeat from the fullscreen frontend if that is the interface you normally use. Then run the linked test of the second title with both webcams active and confirm that exiting one cabinet releases its own camera without disrupting the other cabinet's bridge.

If a hidden helper fails, close the game and run the same start or stop script visibly in Windows PowerShell to see the error. Inspect `logs\bridge.log` in the bridge folder. Do not assume the game will be prevented from launching merely because its before application failed.

## Launcher session tracking

The after application is triggered when **the frontend considers its main application finished**. A launcher can hand the game to a child process or remain open after the game exits. Therefore, a correct before/after configuration alone does not guarantee that the frontend will stop the camera at the correct moment through every launcher setup.

If the camera stops while the game is still running, or stays on after exiting, verify which launcher/game process your existing frontend entry tracks. Keep the game's working launch configuration intact while diagnosing it. You can temporarily disable the stop Additional App and use **Stop Camera.cmd** after exiting until session tracking is resolved; do not leave that temporary arrangement marked as a passed automatic lifecycle test.

The same bridge instance can be reused when already healthy. The stop script stops the local bridge, so avoid overlapping game sessions on the same computer. Each linked cabinet's hooks control only that cabinet's local configuration and camera.

## Remove the integration

Delete only the two Camera Bridge entries from each game's Additional Apps list. This restores manual Start/Stop use without changing the game's launcher profile or other Additional Apps.

## Validation status

See [VALIDATION.md](VALIDATION.md) for recorded results. The user reports the first and second titles working on the installed cabinet version. Bridge shutdown after a game session and startup during the next session were observed on both cabinets. A paired race with both webcams active has not been separately confirmed, and the renamed public candidate has not had a new live game test. Complete the remaining play-tests and repeat-launch checks before treating this candidate as fully validated.
