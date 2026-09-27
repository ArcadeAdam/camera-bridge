# Validation record

Updated **2026-09-27 UTC**. Results for the new portable rewrite and the previous installed edition are recorded separately.

## Portable v1.0.0-rc.4

The portable download is `camera-bridge-1.0.0-rc.4-win-x64.zip`, containing only `CameraBridge.exe` and `CameraBridge.cfg`. The standalone executable is about **80 KB** and needs no third-party binaries. It uses the system's .NET Framework; both cabinets have **4.8.1**, registry Release `533509`.

Version rc.3 captured correctly on cabinet A, but cabinet B could not connect its selected video format. Version rc.4 requests a supported video header and tries another format after a connection failure. The fix passed physical capture and three game-style EOF checks on each cabinet. Cabinet B's candidate started in 1.924 seconds and stopped cleanly. These are standalone checks; new game sessions remain pending.

| Check | Current result | Scope |
| --- | --- | --- |
| Native build and test-pattern startup | Passed initial checks | The app built and served the generated test image. This does not test the physical camera or a game. |
| Native server automated suite | Passed: 93 assertions | Exact JPEG bytes, original `WSAPoll` mask `0x300` with readable EOF, fragmented NUL request, stale-frame rejection, peer filtering, both listeners' Host checks, preview origin checks, and port-busy cleanup. |
| Native process suite | Passed: 44 assertions | Repeated and concurrent starts, mode/config mismatch handling, changed-config stop, invalid settings, missing-camera cleanup, and cancellation during startup. |
| Physical webcam on cabinet A | Passed capture checks | Baseline 320x240 JPEG, orientation/stride/mirror checks, three capture/release cycles, lower-rate throttling, and 50 startup-cancellation races. Current output was about 15 FPS, matching a separate current FFmpeg comparison on the same camera. |
| Hidden `--start` and `--stop` commands | Passed with test pattern and physical webcam | Startup waited for a fresh frame; both commands returned exit code `0`. The live camera health check showed a 4 ms frame age. Actual frontend game sessions remain separate. |
| Capture/server code review | Completed and fix tested | Preview routes reject non-local Host names. Game requests remain compatible with the original hostless HTTP request. |
| Portable installation on cabinet A | Passed standalone checks | Installed EXE with local CFG; all eight existing frontend hooks migrated for four game entries, with XML backups and an idempotent verification. Exact start/stop commands succeeded with the physical webcam. Three LAN-bound probes passed JPEG dimensions, Content-Length, final EOI, urgent readiness and normal EOF, including a fragmented NUL request. |
| Game camera tests on cabinet A | Pending | Recheck both supported titles with the portable rewrite. |
| Cabinet B capture fallback candidate | Passed standalone checks | Candidate built from commit `6b725760f711b053b6c1d1607b1475026f88535a`: live 320x240 capture, three game-style checks, and successful start/stop with no leftover processes or listeners. Official release-file installation and fresh game tests are recorded separately. |
| Official rc.4 release installed on both cabinets | Passed standalone checks | Both installed EXEs match SHA-256 `1AAE296C6D0B8401D4041D8C915D1DEE612858D94F69FE2C74B9EC0E9ED91ECD`. Cabinet A passed its final installed start/EOF/stop check. Cabinet B passed two physical start/stop cycles and all three game-style protocol checks. Both use their own configured webcam and LAN address; each has eight frontend hooks across four game entries and a desktop shortcut. Both bridges were left stopped, with no remaining processes or listeners, ready for a user game test. |
| Linked play with both portable bridges | Pending | Recheck both images and linked play using this build on both cabinets. |
| LaunchBox repeated start/exit | Pending | Verify `--start` / `--stop` with real game sessions on each cabinet. |

The user's [linked-cabinet photo](docs/images/linked-cabinets.jpg) shows **two visible camera images from the previous installed edition**. It does not validate a fresh game session using the portable rewrite.

## Previous edition

The following records describe the earlier script-based cabinet installation and its renamed source candidate. Their tests and timing do not carry over automatically to the native rewrite.


### Tested components

| Component | Observed version/setup |
| --- | --- |
| Emulator | Original, unmodified installed executable. |
| Executable integrity | Original installed executable used for successful second title testing; original digest verified. |
| Webcam | One USB webcam per cabinet, with each local capture-device name configured separately. |
| Server runtime | 24.17.0 on cabinet A; 24.21.0 on cabinet B. |
| Helper interpreter | 3.14.6, standard library only. |
| Capture/encoding runtime | 8.1.2, `dshow` input and `mjpeg` output. |
| Output | 320 × 240 baseline JPEG; capture configured at 30 FPS. |
| Camera route | Same-computer LAN-address listener with host EOF compatibility; cabinet seat mappings preserved. |

### Functional results

| Check | Result | Evidence or remaining work |
| --- | --- | --- |
| Second title boot camera check, original emulator | Passed | User confirmed successful camera check through the external helper. |
| Second title camera operation and improved smoothness | Passed by user report | Tested through the existing launcher after capture/helper optimization. |
| Second title linked cabinet session | Passed by user report | User reported the linked test worked with the camera active on cabinet A. This does not validate a webcam on cabinet B. |
| First title operation | Passed by user report | User reported the first title works well during the latest cabinet test. The bridge received camera requests without reported service errors. This records the installed cabinet version, not a new live test of the renamed public candidate. |
| Cabinet B standalone installation and live capture | Passed | Source archive verified; all 25 tests passed with zero skips. Valid 320x240 JPEGs, 30.12 output FPS over eight seconds, and the host EOF probe passed. Its camera destination was changed separately after backup; seat mappings, emulator executable, launcher profiles, and frontend files were preserved. |
| Both webcams active in a linked second title game | Visible images confirmed by later photo | The later user photo shows both local camera images. A separate completed paired-race result was not recorded here. This is evidence for the earlier installed edition only. |
| Frontend bridge startup and shutdown | Observed on both cabinets | After the user closed the previous game, cabinet A logged stop-request/shutdown at 00:02:00 UTC and cabinet B at 00:02:05 UTC on 2026-09-27. Their previous capture processes exited. New capture sessions began around 00:03:07 UTC for the next user test. Cabinet B's helper start command was confirmed exited while its new game/camera session continued. This verifies bridge lifecycle observations, not the pending in-game image and linked-race results. |
| Standalone setup helper | Passed configuration checks | Isolated checks covered dry run, unattended creation/defaults, invalid address and missing runtime rejection, existing-config protection, and verified backup/replacement. The live cabinet configuration remained unchanged. Setup does not change the emulator or frontend. |
| Local cabinet frontend hook configuration | Passed on cabinets A and B | Each cabinet has one Start and one Stop hook for each supported title entry under both configured platforms. The exact Start commands returned after fresh frames; the exact Stop commands released each bridge's server runtime, helper interpreter, and capture encoder processes. Cabinet A's camera listener also passed the host EOF check, and cabinet B's check confirmed both listeners closed. Installer reruns proposed no changes. Both bridges were initially left stopped for the user play-test; subsequent game-session shutdown and next-session startup were observed as recorded above. The local XML editing utility is excluded from the public package; public integration is documented in [FRONTEND.md](FRONTEND.md). |
| Original emulator and cabinet mappings | Verified | Running process used the original executable; only the camera destination changed among arcade IP mappings. |

### Timing observations

- Webcam capture measured **29.87 FPS** on cabinet A and **30.12 FPS** on cabinet B with a 30 FPS setting, over eight-second samples. Small sampling differences around the configured rate are expected.
- An isolated helper response benchmark improved from a **20.8 ms median to 0.46 ms** after removing unnecessary delay. This measures that benchmark, not game rendering.
- During live operation, observed response timing was approximately **34 ms**, with the game requesting roughly **10 images per second**. Capture rate, server response time, and game request/display rate are different measurements.
- The user reported improved camera behavior and a working linked session after the changes. No in-game 30 FPS claim is made.

### Protocol and automated evidence

The ordinary host close path was reproduced with native `WSAPoll`: data readiness was followed by `POLLHUP` alone, which the tested emulator did not expose as guest read readiness. The second title requires `recv() == 0` before finalizing its image.

The synthetic urgent-byte probe exercised **24 cases** across three payload sizes, two receive sizes, and two consumer delays. All 12 compatibility cases delivered the exact normal payload and EOF through the original read mask; all 12 ordinary-close controls reproduced the stall. Repeating the cases with an urgent byte value of zero gave the same result. An independent native probe against the actual helper interpreter helper also received the exact HTTP response, observed readable EOF, and verified graceful shutdown.

The renamed public candidate passed **25 automated tests with zero failures and zero skips**. The test runner also reports a support module as a separate passing item. Its setup dry run passed without writing configuration. These checks are automated evidence only; the live game results above belong to the prior cabinet installation. Coverage includes HTTP/JPEG handling, capture, helper forwarding, NUL-terminated requests, source filtering, runtime resolution, and lifecycle behavior. Rerun `node --test` after release changes with the required runtimes available. Passing automated tests does not replace the pending game and cabinet checks above.

Reproduction tools:

- `Test-CameraEof.ps1`: checks the running configured camera listener.
- `tools/Test-OobEof.ps1`: standalone synthetic host EOF comparison.

No emulator executable patch is needed by the installed compatibility route. Runtime binaries, game files, local logs, and diagnostic backups are excluded from the intended source package.
