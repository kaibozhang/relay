# Native UIAutomation integration harness

Package-free Windows-only executable targeting .NET 10 WPF. UIAutomationClient/UIAutomationTypes come from the Windows desktop framework; no mock UI, browser driver, test packages, or Core references.

## Run

Use an unlocked interactive Windows desktop with .NET 10 SDK. Do not type or change focus during the short test run. Build the app separately so the harness tests the intended binary:

```powershell
dotnet build native/Relay.Desktop/Relay.Desktop.csproj -c Debug
dotnet run --project native/Relay.Automation/Relay.Automation.csproj -c Debug -- --workspace D:\DPH
```

Omit `--workspace` when running from the repository root or a descendant. Also accepts `--stress-restarts N` (0–100, default 0) to force-stop/restart the owned app N additional times and verify exact vault bytes plus completed-card UI. No arbitrary vault or executable argument is accepted. The harness always starts `native/Relay.Desktop/bin/Debug/net10.0-windows/Relay.exe`.

## Safety and isolation

- Generates a fresh `artifacts/test-vault-<GUID>` for every run, passing its absolute path through `ProcessStartInfo.ArgumentList` as `--vault`. Never reads the default user vault. Retains synthetic data for diagnosis.
- Discovers top-level windows filtered by the exact launched PID; rechecks ownership before input and screenshots. Terminates only Process objects it started, never processes selected by name. `finally` kills owned processes, without process-tree termination. Forced termination after a confirmed disk save deliberately tests crash durability and avoids close-to-tray.
- Does not read/write clipboard, click clipboard controls, inspect unrelated windows, automatically open resource paths, install Explorer registrations, or change registry settings. The synthetic resource file lives inside the test vault.
- Probes Ctrl+Alt+Space with `RegisterHotKey` before launching. If already occupied, skips synthetic global input rather than triggering an unrelated app. A separate collision test temporarily reserves the binding, starts the owned app and checks its reported registration failure, then releases only its own registration.
- SendInput sends balanced key-down/up events, attempts releases after partial injection, and skips if shortcut keys are held or injection is denied. It does not bypass UIPI.
- Screenshots use `PrintWindow` on an owned WPF HWND only, bounded to that window rectangle (maximum 8192 pixels per dimension). No desktop screenshot or unrelated background capture fallback. A four-second capture wait records unavailability rather than blocking indefinitely. Client-interior pixel variation (excluding native window chrome) detects blank white/black frames and marks them unavailable. Each owned process also receives an explicit `--diagnostic-snapshots` directory inside this run: app-side RenderTargetBitmap snapshots of its own live WPF visual trees are collected with a 3.5-second freshness bound, validated, and distinctly labeled `-visual.png`. These are rendered client visuals, not desktop screenshots; no app/source behavior is mocked.

## Coverage and evidence

Tests use AutomationIds with ValuePattern for fields and InvokePattern for buttons; native SendInput is used for Ctrl+Enter and Ctrl+Alt+Space. Readiness is polled at 80 ms with ten-second deadlines and a five-minute suite readiness budget, extended by six seconds per requested stress restart.

1. Empty dedicated vault/main window; startup milliseconds and working set.
2. Native CaptureWindow required controls and save; actual `vault.json` fields verified.
3. Forced stop/restart and persisted card presence in UI.
4. Matching/nonmatching main search.
5. Same-PID resume strip, rectangle checks, and collapse.
6. Editing without changing identity; invalid next action retains the editor fields and unchanged vault bytes. Progress prompt updates both checkpoint and next action on disk and in the live resume strip.
7. Completion status/timestamp and Done/All navigation.
8. Ctrl+Enter capture save where synthetic input is available.
9. Secondary same-vault process exits and forwards capture plus resource; repeated forwarding while the draft is open attaches a second resource without losing draft fields. Both resources are verified on disk; the resource-open dialog requires explicit selection and is cancelled without opening files.
10. Real global hotkey and explicit RegisterHotKey collision status. A separately labeled `hotkey.registered-window-handler` posts WM_HOTKEY only to the owned HWND to verify the minimized-dashboard handler path; it is not a substitute for real keyboard delivery. Successful SendInput with no capture window is a failure, not a skip. Only after a failed real global-hotkey assertion, an independent delivery probe stops the app, temporarily registers the same chord on a harness-owned HwndSource, injects the same balanced keys, and pumps its dispatcher for two seconds. This distinguishes observed WM_HOTKEY delivery from mere SendInput acceptance without touching another app.
11. Focus start/pause/resume via UI, paused crash restart, on-disk remaining time, seeded expired pending completion and exactly-once checkpoint settlement across replay/restart. Fixture writes occur only in the owned vault while its app is stopped.
12. Cold `--capture` and warm single-instance `--capture` while dashboard is hidden: native capture appears and dashboard remains absent/offscreen.
13. Minimum main window size 900×620 layout, capture controls with minimum-size owner, and optional 0–100 additional real crash restarts. Does not change global DPI/system settings.
14. Explicit Help entry opens its native window with visible action buttons and closes; no unsolicited startup help.

Reports and window PNGs are stored in `artifacts/native-tests/<UTC-run-id>/`. The JSON report is also written to stdout; progress/report path go to stderr. It includes individual pass/fail/skip results, durations, startup/working-set metrics, control rectangles and screenshot outcomes. Exit code 0 means no failed assertions, 1 means product/functional failures, and 2 means only independently corroborated environment delivery blocks. Raw results and total failures are always preserved. `productFunctionalFailures`, `environmentFailures`, `environmentDiagnostics` and `classifiedResults` distinguish a missing global hotkey event on both app and independent registered helper from product behavior. This classification is applied only when the independent same-input probe also fails; no raw failure becomes a pass. Skips are explicit and never reported as passes. Screenshots are evidence, not automated pixel-diff assertions. Visible application buttons are checked for zero geometry/horizontal window overflow; key controls additionally must be onscreen and contained. Legitimately scrolled-off controls and unnamed native scrollbar template parts are not classified as defects. Geometry failures are recorded separately without aborting the functional action. Failed/skipped scenarios restart only the owned app to clear modal state before continuing; original failures remain in the report.

## Verified baseline

Final real UI run on 2026-09-30 completed **18/18 passed, 0 skipped, 0 failed** in 57.8 seconds. Both **actual SendInput Ctrl+Enter and Ctrl+Alt+Space passed**, alongside the separately labeled WM_HOTKEY handler and registration-conflict checks. First startup was 877 ms with 138,862,592 bytes working set. All 10 owned app processes exited. Evidence: `artifacts/native-tests/20260930-073211-7277c1f8/report.json`. All 30 images in that report (15 PrintWindow and 15 explicitly labeled app-owned visual renders) had nonblank client pixel variation. A separate earlier run completed **30/30 real forced-stop/restart durability trials** in 80.1 seconds.

Earlier restricted-runtime runs had blocked foreground/input delivery and blank PrintWindow client areas. Those historical raw results remain unchanged; they do not describe the final passing baseline. Conditional environment diagnostics remain available if the same restrictions recur. Pixel checks establish nonblank captures, not model-based visual review or pixel-perfect UI correctness.

## Limitations

An unlocked desktop and functioning Windows UIAutomation providers are required. Focus interference can make SendInput tests fail. UIAutomation calls themselves are OS/provider calls; readiness loops are bounded but a hung provider may require terminating the harness externally (which bypasses managed finally). PrintWindow returned blank white/black client areas in the restricted runtime used for development; those captures are explicitly unavailable, not valid visual evidence. The separately labeled app-owned visual-tree captures are validated for pixel variation but may contain transparent regions and are not a screenshot of the desktop/compositor. No model-based image review or pixel-perfect visual correctness is claimed. Run one harness at a time to avoid intentional hotkey contention. Explorer registration and clipboard OS contents are deliberately out of scope.
