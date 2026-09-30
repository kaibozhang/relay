# Native UIAutomation integration harness

Package-free Windows-only executable targeting .NET 10 WPF. UIAutomationClient/UIAutomationTypes come from the Windows desktop framework; no mock UI, browser driver, test packages, or Core references.

## Run

Use an unlocked interactive Windows desktop with .NET 10 SDK. Do not type or change focus during the short test run. Build the app separately so the harness tests the intended binary:

```powershell
dotnet build native/Relay.Desktop/Relay.Desktop.csproj -c Debug
dotnet run --project native/Relay.Automation/Relay.Automation.csproj -c Debug -- --workspace D:\DPH
```

Omit `--workspace` when running from the repository root or a descendant. The only accepted argument is an optional repository root, not a vault or arbitrary executable. The harness always starts `native/Relay.Desktop/bin/Debug/net10.0-windows/Relay.exe`.

## Safety and isolation

- Generates a fresh `artifacts/test-vault-<GUID>` for every run, passing its absolute path through `ProcessStartInfo.ArgumentList` as `--vault`. Never reads the default user vault. Retains synthetic data for diagnosis.
- Discovers top-level windows filtered by the exact launched PID; rechecks ownership before input and screenshots. Terminates only Process objects it started, never processes selected by name. `finally` kills owned processes, without process-tree termination. Forced termination after a confirmed disk save deliberately tests crash durability and avoids close-to-tray.
- Does not read/write clipboard, click clipboard controls, inspect unrelated windows, automatically open resource paths, install Explorer registrations, or change registry settings. The synthetic resource file lives inside the test vault.
- Probes Ctrl+Alt+Space with `RegisterHotKey` before launching. If already occupied, skips synthetic global input rather than triggering an unrelated app. A separate collision test temporarily reserves the binding, starts the owned app and checks its reported registration failure, then releases only its own registration.
- SendInput sends balanced key-down/up events, attempts releases after partial injection, and skips if shortcut keys are held or injection is denied. It does not bypass UIPI.
- Screenshots use `PrintWindow` on an owned WPF HWND only, bounded to that window rectangle (maximum 8192 pixels per dimension). No desktop screenshot or unrelated background capture fallback. A four-second capture wait records unavailability rather than blocking indefinitely.

## Coverage and evidence

Tests use AutomationIds with ValuePattern for fields and InvokePattern for buttons; native SendInput is used for Ctrl+Enter and Ctrl+Alt+Space. Readiness is polled at 80 ms with ten-second deadlines and a four-minute suite readiness budget.

1. Empty dedicated vault/main window; startup milliseconds and working set.
2. Native CaptureWindow required controls and save; actual `vault.json` fields verified.
3. Forced stop/restart and persisted card presence in UI.
4. Matching/nonmatching main search.
5. Same-PID resume strip, rectangle checks, and collapse.
6. Editing without changing identity, checkpoint prompt and on-disk contents.
7. Completion status/timestamp and Done/All navigation.
8. Ctrl+Enter capture save where synthetic input is available.
9. Secondary same-vault process exits and forwards capture plus resource; resource is verified on disk.
10. Real global hotkey and explicit RegisterHotKey collision status.

Reports and window PNGs are stored in `artifacts/native-tests/<UTC-run-id>/`. The JSON report is also written to stdout; progress/report path go to stderr. It includes individual pass/fail/skip results, durations, startup/working-set metrics, control rectangles and screenshot outcomes. Exit code 1 means one or more failures; skips are explicit and never reported as passes. Screenshots are evidence, not automated pixel-diff assertions. Visible application buttons are checked for zero geometry/horizontal window overflow; key controls additionally must be onscreen and contained. Legitimately scrolled-off controls and unnamed native scrollbar template parts are not classified as defects. Geometry failures are recorded separately without aborting the functional action. Failed/skipped scenarios restart only the owned app to clear modal state before continuing; original failures remain in the report.

## Limitations

An unlocked desktop and functioning Windows UIAutomation providers are required. Focus interference can make SendInput tests fail. UIAutomation calls themselves are OS/provider calls; readiness loops are bounded but a hung provider may require terminating the harness externally (which bypasses managed finally). PrintWindow can return blank pixels on some GPU/remote-desktop configurations; the report distinguishes unavailable captures but does not promise pixel fidelity. Run one harness at a time to avoid intentional hotkey contention. Explorer registration and clipboard OS contents are deliberately out of scope.
