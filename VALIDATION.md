# Validation record

Updated **2026-09-27 UTC**. The live game results below describe the original cabinet installation. They do not establish a new live game test of this renamed public candidate.

## Tested components

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

## Functional results

| Check | Result | Evidence or remaining work |
| --- | --- | --- |
| Second title boot camera check, original emulator | Passed | User confirmed successful camera check through the external helper. |
| Second title camera operation and improved smoothness | Passed by user report | Tested through the existing launcher after capture/helper optimization. |
| Second title linked cabinet session | Passed by user report | User reported the linked test worked with the camera active on cabinet A. This does not validate a webcam on cabinet B. |
| First title operation | Passed by user report | User reported the first title works well during the latest cabinet test. The bridge received camera requests without reported service errors. This records the installed cabinet version, not a new live test of the renamed public candidate. |
| Cabinet B standalone installation and live capture | Passed | Source archive verified; all 25 tests passed with zero skips. Valid 320x240 JPEGs, 30.12 output FPS over eight seconds, and the host EOF probe passed. Its camera destination was changed separately after backup; seat mappings, emulator executable, launcher profiles, and frontend files were preserved. |
| Both webcams active in a linked second title game | Pending | The paired game test after cabinet B installation is still required. |
| Frontend bridge startup and shutdown | Observed on both cabinets | After the user closed the previous game, cabinet A logged stop-request/shutdown at 00:02:00 UTC and cabinet B at 00:02:05 UTC on 2026-09-27. Their previous capture processes exited. New capture sessions began around 00:03:07 UTC for the next user test. Cabinet B's helper start command was confirmed exited while its new game/camera session continued. This verifies bridge lifecycle observations, not the pending in-game image and linked-race results. |
| Standalone setup helper | Passed configuration checks | Isolated checks covered dry run, unattended creation/defaults, invalid address and missing runtime rejection, existing-config protection, and verified backup/replacement. The live cabinet configuration remained unchanged. Setup does not change the emulator or frontend. |
| Local cabinet frontend hook configuration | Passed on cabinets A and B | Each cabinet has one Start and one Stop hook for each supported title entry under both configured platforms. The exact Start commands returned after fresh frames; the exact Stop commands released each bridge's server runtime, helper interpreter, and capture encoder processes. Cabinet A's camera listener also passed the host EOF check, and cabinet B's check confirmed both listeners closed. Installer reruns proposed no changes. Both bridges were initially left stopped for the user play-test; subsequent game-session shutdown and next-session startup were observed as recorded above. The local XML editing utility is excluded from the public package; public integration is documented in [FRONTEND.md](FRONTEND.md). |
| Original emulator and cabinet mappings | Verified | Running process used the original executable; only the camera destination changed among arcade IP mappings. |

## Timing observations

- Webcam capture measured **29.87 FPS** on cabinet A and **30.12 FPS** on cabinet B with a 30 FPS setting, over eight-second samples. Small sampling differences around the configured rate are expected.
- An isolated helper response benchmark improved from a **20.8 ms median to 0.46 ms** after removing unnecessary delay. This measures that benchmark, not game rendering.
- During live operation, observed response timing was approximately **34 ms**, with the game requesting roughly **10 images per second**. Capture rate, server response time, and game request/display rate are different measurements.
- The user reported improved camera behavior and a working linked session after the changes. No in-game 30 FPS claim is made.

## Protocol and automated evidence

The ordinary host close path was reproduced with native `WSAPoll`: data readiness was followed by `POLLHUP` alone, which the tested emulator did not expose as guest read readiness. The second title requires `recv() == 0` before finalizing its image.

The synthetic urgent-byte probe exercised **24 cases** across three payload sizes, two receive sizes, and two consumer delays. All 12 compatibility cases delivered the exact normal payload and EOF through the original read mask; all 12 ordinary-close controls reproduced the stall. Repeating the cases with an urgent byte value of zero gave the same result. An independent native probe against the actual helper interpreter helper also received the exact HTTP response, observed readable EOF, and verified graceful shutdown.

The renamed public candidate passed **25 automated tests with zero failures and zero skips**. The test runner also reports a support module as a separate passing item. Its setup dry run passed without writing configuration. These checks are automated evidence only; the live game results above belong to the prior cabinet installation. Coverage includes HTTP/JPEG handling, capture, helper forwarding, NUL-terminated requests, source filtering, runtime resolution, and lifecycle behavior. Rerun `node --test` after release changes with the required runtimes available. Passing automated tests does not replace the pending game and cabinet checks above.

Reproduction tools:

- `Test-CameraEof.ps1`: checks the running configured camera listener.
- `tools/Test-OobEof.ps1`: standalone synthetic host EOF comparison.

No emulator executable patch is needed by the installed compatibility route. Runtime binaries, game files, local logs, and diagnostic backups are excluded from the intended source package.
